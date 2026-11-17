using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using RoR2;
using UnityEngine;

namespace CommandItemQueue
{
    /// <summary>One command category, e.g. "Common", "Lunar", "Equipment" or any tier added by a future DLC.</summary>
    internal sealed class PickupGroup
    {
        public string Key;
        public string DisplayName;
        public Color Color;
        public int SortOrder;
        public readonly List<PickupIndex> Members = new List<PickupIndex>();
        public readonly HashSet<PickupIndex> Available = new HashSet<PickupIndex>();

        public bool IsAvailable(PickupIndex p) => Available.Contains(p);
    }

    /// <summary>
    /// Builds the categories dynamically from the game's catalogs every time they are needed,
    /// so new item tiers (DLCs, other mods) show up automatically without updating this mod.
    /// </summary>
    internal static class PickupGroups
    {
        private const string ItemPrefix = "item:";
        private const string EquipPrefix = "equip:";
        private const string OtherPrefix = "other:";

        // Friendly names for the tiers we already know. Unknown tiers get a name generated from their ItemTierDef.
        private static readonly Dictionary<string, string> KnownTierNames = new Dictionary<string, string>
        {
            { "Tier1Def", "Common" },
            { "Tier2Def", "Uncommon" },
            { "Tier3Def", "Legendary" },
            { "BossTierDef", "Boss" },
            { "LunarTierDef", "Lunar" },
            { "VoidTier1Def", "Void Common" },
            { "VoidTier2Def", "Void Uncommon" },
            { "VoidTier3Def", "Void Legendary" },
            { "VoidBossDef", "Void Boss" },
        };

        private static readonly string[] KnownTierOrder =
        {
            "Tier1Def", "Tier2Def", "Tier3Def", "BossTierDef", "LunarTierDef",
            "VoidTier1Def", "VoidTier2Def", "VoidTier3Def", "VoidBossDef"
        };

        private static readonly Dictionary<string, PickupGroup> groups = new Dictionary<string, PickupGroup>();
        private static readonly List<PickupGroup> ordered = new List<PickupGroup>();
        private static readonly HashSet<string> discoveredOtherKeys = new HashSet<string>();
        // Pickups we have actually seen offered by a command cube this run (always treated as available).
        private static readonly Dictionary<string, HashSet<PickupIndex>> seenInCubes = new Dictionary<string, HashSet<PickupIndex>>();

        private static bool dirty = true;
        private static float lastBuildTime = -999f;
        private static Run lastRun;

        public static void Invalidate()
        {
            dirty = true;
        }

        public static IReadOnlyList<PickupGroup> GetAll()
        {
            if (dirty || lastRun != Run.instance || Time.unscaledTime - lastBuildTime > 3f)
                Rebuild();
            return ordered;
        }

        public static PickupGroup Find(string key)
        {
            if (key == null) return null;
            GetAll();
            groups.TryGetValue(key, out var g);
            return g;
        }

        // ---------------------------------------------------------------- keys

        public static string KeyFor(PickupIndex pickupIndex)
        {
            if (!pickupIndex.isValid) return null;
            return KeyFor(PickupCatalog.GetPickupDef(pickupIndex));
        }

        public static string KeyFor(PickupDef def)
        {
            if (def == null) return null;

            if (def.itemIndex != ItemIndex.None)
            {
                var itemDef = ItemCatalog.GetItemDef(def.itemIndex);
                if (!itemDef || itemDef.hidden) return null;
                var tierDef = ItemTierCatalog.GetItemTierDef(itemDef.tier);
                if (!tierDef) return null; // NoTier / untiered helper items
                return ItemPrefix + tierDef.name;
            }

            if (def.equipmentIndex != EquipmentIndex.None)
            {
                var equipDef = EquipmentCatalog.GetEquipmentDef(def.equipmentIndex);
                if (!equipDef) return null;
                if (equipDef.isBoss) return EquipPrefix + "boss";
                if (equipDef.isLunar) return EquipPrefix + "lunar";
                return EquipPrefix + "normal";
            }

            // Any other kind of pickup a future update might put into command cubes.
            // Grouped by the "kind" part of the internal name, e.g. "MiscPickupIndex.LunarCoin" -> "MiscPickupIndex".
            var name = def.internalName ?? "";
            int dot = name.IndexOf('.');
            return OtherPrefix + (dot > 0 ? name.Substring(0, dot) : name);
        }

        // ---------------------------------------------------------------- discovery

        /// <summary>Called whenever a command cube is opened. Makes sure every offered pickup has a category.</summary>
        public static void LearnFromOptions(IList<PickupIndex> offered)
        {
            if (offered == null) return;
            bool newCategory = false;
            foreach (var pickup in offered)
            {
                var key = KeyFor(pickup);
                if (key == null) continue;
                if (!seenInCubes.TryGetValue(key, out var set))
                    seenInCubes[key] = set = new HashSet<PickupIndex>();
                if (set.Add(pickup)) dirty = true;

                if (key.StartsWith(OtherPrefix, StringComparison.Ordinal) && key != OtherPrefix + "ItemTier" && discoveredOtherKeys.Add(key))
                    newCategory = true;
            }

            if (newCategory)
            {
                dirty = true;
                CommandItemQueuePlugin.Instance?.SaveDiscoveredCategories();
            }
        }

        public static void LoadDiscovered(string serialized)
        {
            discoveredOtherKeys.Clear();
            if (string.IsNullOrEmpty(serialized)) return;
            foreach (var part in serialized.Split(';'))
                if (part.StartsWith(OtherPrefix, StringComparison.Ordinal) && part.Trim() != OtherPrefix + "ItemTier")
                    discoveredOtherKeys.Add(part.Trim());
        }

        public static string SerializeDiscovered() => string.Join(";", discoveredOtherKeys.ToArray());

        // ---------------------------------------------------------------- building

        private static void Rebuild()
        {
            dirty = false;
            lastBuildTime = Time.unscaledTime;
            if (lastRun != Run.instance)
            {
                seenInCubes.Clear();
                lastRun = Run.instance;
            }

            groups.Clear();
            ordered.Clear();

            foreach (var def in PickupCatalog.allPickups)
            {
                string key;
                try { key = KeyFor(def); }
                catch { continue; }
                if (key == null) continue;
                if (key.StartsWith(OtherPrefix, StringComparison.Ordinal) && !discoveredOtherKeys.Contains(key)) continue;

                if (!groups.TryGetValue(key, out var group))
                {
                    group = CreateGroup(key, def);
                    if (group == null) continue;
                    groups[key] = group;
                }
                group.Members.Add(def.pickupIndex);
            }

            var run = Run.instance;
            foreach (var group in groups.Values)
            {
                seenInCubes.TryGetValue(group.Key, out var seen);
                foreach (var p in group.Members)
                    if (seen != null && seen.Contains(p) || IsAvailableInRun(run, p))
                        group.Available.Add(p);

                group.Members.Sort((a, b) => string.Compare(DisplayName(a), DisplayName(b), StringComparison.CurrentCultureIgnoreCase));
                ordered.Add(group);
            }

            ordered.Sort((a, b) =>
            {
                int c = a.SortOrder.CompareTo(b.SortOrder);
                return c != 0 ? c : string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal);
            });
        }

        private static bool IsAvailableInRun(Run run, PickupIndex p)
        {
            if (!run) return true;
            try
            {
                // Exactly the list a command cube would offer for this pickup.
                var available = PickupTransmutationManager.GetAvailableGroupFromPickupIndex(p);
                if (available != null && available.Length > 0)
                    return Array.IndexOf(available, p) >= 0;
            }
            catch { /* fall through */ }

            try { return run.IsPickupAvailable(p); }
            catch { return true; }
        }

        private static PickupGroup CreateGroup(string key, PickupDef firstMember)
        {
            var group = new PickupGroup { Key = key, Color = firstMember.baseColor };

            if (key.StartsWith(ItemPrefix, StringComparison.Ordinal))
            {
                var itemDef = ItemCatalog.GetItemDef(firstMember.itemIndex);
                var tierDef = itemDef ? ItemTierCatalog.GetItemTierDef(itemDef.tier) : null;
                string tierName = key.Substring(ItemPrefix.Length);

                group.DisplayName = KnownTierNames.TryGetValue(tierName, out var known) ? known : Prettify(tierName);
                int knownIndex = Array.IndexOf(KnownTierOrder, tierName);
                group.SortOrder = knownIndex >= 0 ? knownIndex : 100 + (tierDef ? (int)tierDef.tier : 0);
                if (tierDef)
                {
                    try { group.Color = ColorCatalog.GetColor(tierDef.colorIndex); }
                    catch { /* keep pickup color */ }
                }
            }
            else if (key.StartsWith(EquipPrefix, StringComparison.Ordinal))
            {
                string kind = key.Substring(EquipPrefix.Length);
                switch (kind)
                {
                    case "lunar": group.DisplayName = "Lunar Equipment"; group.SortOrder = 201; break;
                    case "boss": group.DisplayName = "Boss Equipment"; group.SortOrder = 202; break;
                    default: group.DisplayName = "Equipment"; group.SortOrder = 200; break;
                }
            }
            else
            {
                group.DisplayName = Prettify(key.Substring(OtherPrefix.Length).Replace("Index", ""));
                group.SortOrder = 300;
            }

            if (string.IsNullOrEmpty(group.DisplayName)) group.DisplayName = key;
            group.Color.a = 1f;
            return group;
        }

        /// <summary>"HexTierDef" -> "Hex", "SuperRareTierDef" -> "Super Rare".</summary>
        internal static string Prettify(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            string s = raw;
            if (s.EndsWith("Def", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 3);
            if (s.EndsWith("Tier", StringComparison.Ordinal) && s.Length > 4) s = s.Substring(0, s.Length - 4);
            s = s.Replace("_", " ");
            s = Regex.Replace(s, "(?<=[a-z])(?=[A-Z0-9])", " ").Trim();
            return string.IsNullOrEmpty(s) ? raw : s;
        }

        // ---------------------------------------------------------------- per-pickup helpers

        private static readonly Dictionary<PickupIndex, string> nameCache = new Dictionary<PickupIndex, string>();
        private static string cachedLanguage;

        public static string DisplayName(PickupIndex p)
        {
            var lang = Language.currentLanguageName;
            if (lang != cachedLanguage) { nameCache.Clear(); cachedLanguage = lang; }
            if (nameCache.TryGetValue(p, out var n)) return n;

            var def = PickupCatalog.GetPickupDef(p);
            n = def != null ? Language.GetString(def.nameToken) : "?";
            if (string.IsNullOrEmpty(n) && def != null) n = def.internalName;
            nameCache[p] = n;
            return n;
        }

        public static string LongDescription(PickupIndex p)
        {
            var def = PickupCatalog.GetPickupDef(p);
            if (def == null) return "";
            string token = null;
            if (def.itemIndex != ItemIndex.None)
            {
                var item = ItemCatalog.GetItemDef(def.itemIndex);
                if (item) token = item.descriptionToken;
            }
            else if (def.equipmentIndex != EquipmentIndex.None)
            {
                var equip = EquipmentCatalog.GetEquipmentDef(def.equipmentIndex);
                if (equip) token = equip.descriptionToken;
            }
            return string.IsNullOrEmpty(token) ? "" : Language.GetString(token);
        }

        public static string Description(PickupIndex p)
        {
            var def = PickupCatalog.GetPickupDef(p);
            if (def == null) return "";
            string token = null;
            if (def.itemIndex != ItemIndex.None)
            {
                var item = ItemCatalog.GetItemDef(def.itemIndex);
                if (item) token = item.pickupToken;
            }
            else if (def.equipmentIndex != EquipmentIndex.None)
            {
                var equip = EquipmentCatalog.GetEquipmentDef(def.equipmentIndex);
                if (equip) token = equip.pickupToken;
            }
            return string.IsNullOrEmpty(token) ? "" : Language.GetString(token);
        }

        // ---------------------------------------------------------------- icons
        // Newer game versions load item icons on demand (Addressables), so PickupDef.iconTexture can be null.
        // We ask the game to load them through its own helper (found via reflection, so older versions still work).

        private static readonly Dictionary<PickupIndex, Sprite> loadedIcons = new Dictionary<PickupIndex, Sprite>();
        private static readonly List<object> iconHandles = new List<object>(); // keeps async loads alive
        private static readonly MethodInfo GetIconAodrMethod =
            typeof(PickupDef).GetMethod("GetIconAODR", BindingFlags.Public | BindingFlags.Instance);
        private static readonly MethodInfo GetIconMethod =
            typeof(PickupDef).GetMethod("GetIcon", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        private static bool loggedIconError;

        private static readonly Dictionary<PickupIndex, float> requestTimes = new Dictionary<PickupIndex, float>();

        /// <summary>Returns the icon texture, the UV rect inside it and the icon's aspect ratio (width / height), or false while it is loading.</summary>
        public static bool TryGetIcon(PickupIndex p, out Texture texture, out Rect uv, out float aspect)
        {
            texture = null;
            uv = new Rect(0f, 0f, 1f, 1f);
            aspect = 1f;
            var def = PickupCatalog.GetPickupDef(p);
            if (def == null) return false;

            if (loadedIcons.TryGetValue(p, out var loaded))
            {
                if (loaded) return FromSprite(loaded, out texture, out uv, out aspect);
                // The game unloaded the icon (e.g. after a run ended) - load it again.
                loadedIcons.Remove(p);
                requestTimes.Remove(p);
            }

            if (def.iconSprite) return FromSprite(def.iconSprite, out texture, out uv, out aspect);
            if (def.iconTexture)
            {
                texture = def.iconTexture;
                aspect = texture.height > 0 ? (float)texture.width / texture.height : 1f;
                return true;
            }

            // Request (again if an earlier request never finished, e.g. because it was cancelled when a run ended).
            if (!requestTimes.TryGetValue(p, out var requested) || Time.unscaledTime - requested > 3f)
            {
                requestTimes[p] = Time.unscaledTime;
                RequestIcon(p, def);
            }
            return false;
        }

        private static void RequestIcon(PickupIndex p, PickupDef def)
        {
            try
            {
                if (GetIconMethod != null && GetIconMethod.Invoke(def, null) is Sprite direct && direct)
                {
                    loadedIcons[p] = direct;
                    return;
                }
                if (GetIconAodrMethod != null)
                {
                    Action<Sprite> done = sprite => { if (sprite) loadedIcons[p] = sprite; };
                    var parameters = GetIconAodrMethod.GetParameters();
                    var args = new object[parameters.Length];
                    if (args.Length > 0) args[0] = true;   // load immediately
                    if (args.Length > 1) args[1] = done;   // completed callback
                    var handle = GetIconAodrMethod.Invoke(def, args);
                    if (handle != null) iconHandles.Add(handle);
                }
            }
            catch (Exception e)
            {
                if (!loggedIconError)
                {
                    loggedIconError = true;
                    Log.Warning("Could not load item icons: " + e);
                }
            }
        }

        private static bool FromSprite(Sprite sprite, out Texture texture, out Rect uv, out float aspect)
        {
            texture = sprite.texture;
            uv = new Rect(0f, 0f, 1f, 1f);
            aspect = 1f;
            if (!texture) return false;
            try
            {
                // sprite.uv works for atlas-packed and tightly packed sprites alike.
                var uvs = sprite.uv;
                if (uvs != null && uvs.Length > 0)
                {
                    float minX = 1f, minY = 1f, maxX = 0f, maxY = 0f;
                    foreach (var v in uvs)
                    {
                        if (v.x < minX) minX = v.x;
                        if (v.y < minY) minY = v.y;
                        if (v.x > maxX) maxX = v.x;
                        if (v.y > maxY) maxY = v.y;
                    }
                    if (maxX > minX && maxY > minY) uv = new Rect(minX, minY, maxX - minX, maxY - minY);
                }
                float w = uv.width * texture.width, h = uv.height * texture.height;
                if (w > 0f && h > 0f) aspect = w / h;
            }
            catch { /* use the whole texture */ }
            return true;
        }

        public static Color PickupColor(PickupIndex p)
        {
            var def = PickupCatalog.GetPickupDef(p);
            if (def == null) return UnityEngine.Color.white;
            var c = def.baseColor;
            c.a = 1f;
            return c;
        }
    }
}
