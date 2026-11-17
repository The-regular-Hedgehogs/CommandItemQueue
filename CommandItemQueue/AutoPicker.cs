using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RoR2;
using UnityEngine;

namespace CommandItemQueue
{
    /// <summary>
    /// When a command cube opens its selection panel for the local player, pick the next queued item automatically.
    /// The choice is submitted exactly like a mouse click on the panel would, so it is multiplayer safe
    /// and only needs to be installed on your own client.
    /// Everything that touches the picker goes through reflection, because the game changes these members between versions
    /// (e.g. Option.pickupIndex was a field and is now a property).
    /// </summary>
    internal static class AutoPicker
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly List<PickupPickerController> pending = new List<PickupPickerController>();
        private static bool disabled;

        private static readonly FieldInfo OptionsField = typeof(PickupPickerController).GetField("options", AnyInstance);
        private static readonly FieldInfo PromptField = typeof(PickupPickerController).GetField("networkUIPromptController", AnyInstance);
        private static readonly FieldInfo PanelField = typeof(PickupPickerController).GetField("panelInstance", AnyInstance);
        private static readonly MethodInfo SubmitChoiceMethod = typeof(PickupPickerController).GetMethod("SubmitChoice", AnyInstance, null, new[] { typeof(int) }, null);

        // Option struct accessors, resolved from the real runtime type.
        private static Type optionType;
        private static FieldInfo optionPickupField;       // Option.pickup (UniquePickup)
        private static FieldInfo uniquePickupIndexField;  // UniquePickup.pickupIndex
        private static FieldInfo optionDirectIndexField;  // Option.pickupIndex (older game versions)
        private static FieldInfo optionAvailableField;
        private static bool loggedSample;

        public static void Install(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(PickupPickerController), "OnDisplayBegin");
                if (target == null || OptionsField == null || PromptField == null || SubmitChoiceMethod == null)
                {
                    disabled = true;
                    Log.Error($"Command cube API not found (OnDisplayBegin={target != null}, options={OptionsField != null}, prompt={PromptField != null}, SubmitChoice={SubmitChoiceMethod != null}) - auto picking is disabled.");
                    return;
                }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(AutoPicker), nameof(OnDisplayBeginPostfix)));
                Log.Info("Command cube hook installed.");
            }
            catch (Exception e)
            {
                disabled = true;
                Log.Error("Could not patch command cubes, auto picking is disabled: " + e);
            }
        }

        // Only __instance is injected, so this keeps working even if the game renames the method parameters.
        private static void OnDisplayBeginPostfix(PickupPickerController __instance)
        {
            try
            {
                if (__instance && !pending.Contains(__instance))
                    pending.Add(__instance);
            }
            catch (Exception e) { Log.Error(e); }
        }

        /// <summary>Processed one step later (from Update) so we never re-enter the game's prompt code.</summary>
        public static void Update()
        {
            if (pending.Count == 0) return;
            var copy = pending.ToArray();
            pending.Clear();
            foreach (var picker in copy)
            {
                try { Process(picker); }
                catch (Exception e) { Log.Error("Auto pick failed: " + e); }
            }
        }

        private static void Process(PickupPickerController picker)
        {
            if (!picker || disabled) return;

            string objectName = picker.gameObject.name;
            if (!IsQueueablePicker(objectName)) return;

            var prompt = PromptField.GetValue(picker) as NetworkUIPromptController;
            if (!prompt)
            {
                Log.Info($"[{objectName}] no prompt controller - skipped.");
                return;
            }

            if (!ReadOptions(picker, out var pickups, out var available))
            {
                Log.Info($"[{objectName}] could not read options - skipped.");
                return;
            }

            PickupGroups.LearnFromOptions(pickups);

            if (CommandItemQueuePlugin.Cfg.ManualPickKey.Value.IsPressed())
            {
                Log.Info($"[{objectName}] manual pick key held - choose yourself.");
                return;
            }
            if (pickups.Count < 2 && !IsVoidPotential(objectName)) return;

            var queues = CommandItemQueuePlugin.Instance.Queues;

            // A command cube offers one category; a void potential can mix categories, so try each one.
            var triedKeys = new HashSet<string>();
            for (int o = 0; o < pickups.Count; o++)
            {
                var key = PickupGroups.KeyFor(pickups[o]);
                if (key == null || !triedKeys.Add(key)) continue;

                var queue = queues.TryGet(key);
                if (queue == null || queue.Entries.Count == 0) continue;

                if (!queue.TryTake(p => IndexOfOption(pickups, available, p), out int optionIndex, out var taken))
                {
                    Log.Info($"[{objectName}] queue '{key}' has entries, but none of them is offered by this cube.");
                    continue;
                }

                var panel = PanelField?.GetValue(picker) as GameObject;
                if (panel) panel.SetActive(false);
                SubmitChoiceMethod.Invoke(picker, new object[] { optionIndex });
                Log.Info($"[{objectName}] picked {PickupGroups.DisplayName(taken)} from queue '{key}' ({queue.TotalCount} left).");
                return;
            }

            Log.Info($"[{objectName}] {pickups.Count} options ({string.Join(", ", new List<string>(triedKeys).ToArray())}) - nothing queued, choose yourself.");
        }

        private static bool ReadOptions(PickupPickerController picker, out List<PickupIndex> pickups, out List<bool> available)
        {
            pickups = new List<PickupIndex>();
            available = new List<bool>();
            if (!(OptionsField.GetValue(picker) is Array array) || array.Length == 0) return false;

            var type = array.GetType().GetElementType();
            if (type != optionType)
            {
                optionType = type;
                // Current game: Option.pickup (UniquePickup) -> .pickupIndex. Older game: Option.pickupIndex directly.
                optionPickupField = type.GetField("pickup", AnyInstance);
                uniquePickupIndexField = optionPickupField?.FieldType.GetField("pickupIndex", AnyInstance);
                if (uniquePickupIndexField == null) optionPickupField = null;
                optionDirectIndexField = optionPickupField == null ? type.GetField("pickupIndex", AnyInstance) : null;
                optionAvailableField = type.GetField("available", AnyInstance);
                if (optionPickupField == null && optionDirectIndexField == null)
                {
                    Log.Error($"Option type {type} has no pickup index - auto picking cannot work with this game version.");
                    return false;
                }
                Log.Info($"Reading command options via {(optionPickupField != null ? "Option.pickup.pickupIndex" : "Option.pickupIndex")}.");
            }

            for (int i = 0; i < array.Length; i++)
            {
                object option = array.GetValue(i);
                object index;
                if (optionPickupField != null)
                {
                    object unique = optionPickupField.GetValue(option);
                    index = uniquePickupIndexField.GetValue(unique);
                }
                else index = optionDirectIndexField.GetValue(option);

                object a = optionAvailableField != null ? optionAvailableField.GetValue(option) : (object)true;
                pickups.Add(index is PickupIndex pi ? pi : PickupIndex.none);
                available.Add(!(a is bool b) || b);
            }

            if (!loggedSample)
            {
                loggedSample = true;
                var sample = new List<string>();
                for (int i = 0; i < Math.Min(3, pickups.Count); i++)
                {
                    var def = PickupCatalog.GetPickupDef(pickups[i]);
                    sample.Add($"{pickups[i].value}={(def != null ? def.internalName : "null")}");
                }
                Log.Info("First command options: " + string.Join(", ", sample.ToArray()));
            }
            return true;
        }

        private static int IndexOfOption(List<PickupIndex> pickups, List<bool> available, PickupIndex pickup)
        {
            for (int i = 0; i < pickups.Count; i++)
                if (pickups[i] == pickup && available[i])
                    return i;
            return -1;
        }

        private static bool IsQueueablePicker(string objectName)
        {
            // Strict check on purpose: scrappers, crafting and other pickers use the same component and must never be auto-picked.
            if (objectName.StartsWith("CommandCube", StringComparison.OrdinalIgnoreCase)) return true;
            return IsVoidPotential(objectName) && CommandItemQueuePlugin.Cfg.ApplyToVoidPotentials.Value;
        }

        private static bool IsVoidPotential(string objectName)
        {
            return objectName.StartsWith("OptionPickup", StringComparison.OrdinalIgnoreCase);
        }
    }
}
