using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CommandItemQueue
{
    internal sealed class BuildEntry
    {
        public string InternalName;
        public int Count;
    }

    internal sealed class BuildData
    {
        public string Name;
        public DateTime Created;
        public bool AutoLoad;
        public readonly Dictionary<string, List<BuildEntry>> Queues = new Dictionary<string, List<BuildEntry>>();
        /// <summary>Categories whose queue repeats. All others run once.</summary>
        public readonly HashSet<string> RepeatKeys = new HashSet<string>();

        public int TotalCount
        {
            get
            {
                int total = 0;
                foreach (var list in Queues.Values)
                    foreach (var e in list) total += e.Count;
                return total;
            }
        }

        public void CopyContentFrom(BuildData other)
        {
            Queues.Clear();
            foreach (var pair in other.Queues) Queues[pair.Key] = pair.Value;
            RepeatKeys.Clear();
            foreach (var key in other.RepeatKeys) RepeatKeys.Add(key);
            Created = other.Created;
        }
    }

    /// <summary>
    /// Saves builds to BepInEx/config/CommandItemQueue.builds.txt.
    /// Items are stored by their internal name (e.g. "ItemIndex.Hoof"), which survives game updates.
    /// The file is plain text, so you can rename builds there.
    /// </summary>
    internal sealed class BuildStore
    {
        private const string DateFormat = "yyyy-MM-dd HH:mm";

        private readonly string path;
        public readonly List<BuildData> Builds = new List<BuildData>();

        public BuildStore(string path)
        {
            this.path = path;
        }

        public BuildData GetAutoLoadBuild()
        {
            foreach (var b in Builds)
                if (b.AutoLoad) return b;
            return null;
        }

        public void SetAutoLoad(BuildData build, bool value)
        {
            foreach (var b in Builds) b.AutoLoad = false;
            if (build != null) build.AutoLoad = value;
            Save();
        }

        public void Add(BuildData build)
        {
            Builds.Insert(0, build);
            Save();
        }

        public void Remove(BuildData build)
        {
            Builds.Remove(build);
            Save();
        }

        public void Load()
        {
            Builds.Clear();
            if (!File.Exists(path)) return;
            try
            {
                BuildData current = null;
                foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    if (line.Equals("[build]", StringComparison.OrdinalIgnoreCase))
                    {
                        current = new BuildData { Name = "Build", Created = DateTime.Now };
                        Builds.Add(current);
                        continue;
                    }
                    if (current == null) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var field = line.Substring(0, eq).Trim().ToLowerInvariant();
                    var value = line.Substring(eq + 1).Trim();

                    switch (field)
                    {
                        case "name":
                            current.Name = value;
                            break;
                        case "autoload":
                            current.AutoLoad = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "created":
                            if (DateTime.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                                current.Created = dt;
                            break;
                        case "repeat":
                            // List of categories that repeat, e.g. "item:Tier2Def;item:Tier3Def".
                            foreach (var key in value.Split(';'))
                                if (key.Trim().Contains(":")) current.RepeatKeys.Add(key.Trim());
                            break;
                        case "queue":
                            ParseQueue(current, value);
                            break;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"Could not read builds from {path}: {e}");
            }
        }

        private static void ParseQueue(BuildData build, string value)
        {
            // queue=item:Tier1Def=ItemIndex.Hoof*3,ItemIndex.Syringe*2
            int eq = value.IndexOf('=');
            if (eq <= 0) return;
            var key = value.Substring(0, eq).Trim();
            var list = new List<BuildEntry>();
            foreach (var part in value.Substring(eq + 1).Split(','))
            {
                var p = part.Trim();
                if (p.Length == 0) continue;
                int star = p.LastIndexOf('*');
                int count = 1;
                var name = p;
                if (star > 0 && int.TryParse(p.Substring(star + 1), out var c))
                {
                    count = c;
                    name = p.Substring(0, star);
                }
                if (count > 0) list.Add(new BuildEntry { InternalName = name, Count = count });
            }
            if (list.Count > 0) build.Queues[key] = list;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# CommandItemQueue builds. Builds can be renamed in game (Tab -> Item Queue -> Builds -> Rename).");
                sb.AppendLine("# Format: queue=<category>=<item internal name>*<count>,...");
                foreach (var b in Builds)
                {
                    sb.AppendLine();
                    sb.AppendLine("[build]");
                    sb.AppendLine("name=" + (b.Name ?? "Build").Replace("\n", " ").Replace("\r", " "));
                    sb.AppendLine("autoload=" + (b.AutoLoad ? "1" : "0"));
                    sb.AppendLine("created=" + b.Created.ToString(DateFormat, CultureInfo.InvariantCulture));
                    if (b.RepeatKeys.Count > 0)
                        sb.AppendLine("repeat=" + string.Join(";", new List<string>(b.RepeatKeys).ToArray()));
                    foreach (var pair in b.Queues)
                    {
                        var parts = new List<string>();
                        foreach (var e in pair.Value) parts.Add(e.InternalName + "*" + e.Count);
                        sb.AppendLine("queue=" + pair.Key + "=" + string.Join(",", parts.ToArray()));
                    }
                }

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Log.Error($"Could not save builds to {path}: {e}");
            }
        }
    }
}
