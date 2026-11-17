using System;
using System.Collections.Generic;
using RoR2;

namespace CommandItemQueue
{
    internal sealed class QueueEntry
    {
        public PickupIndex Pickup;
        public int Count;

        public QueueEntry(PickupIndex pickup, int count)
        {
            Pickup = pickup;
            Count = count;
        }
    }

    /// <summary>The queue of one category (e.g. all white items).</summary>
    internal sealed class GroupQueue
    {
        public readonly string Key;
        public readonly List<QueueEntry> Entries = new List<QueueEntry>();

        /// <summary>false = entries are consumed in order. true = the queue loops forever.</summary>
        public bool Repeat;

        // Repeat mode position: which entry is next and how often it was already taken in this cycle.
        public int Cursor;
        public int UsedAtCursor;

        public GroupQueue(string key)
        {
            Key = key;
        }

        public int TotalCount
        {
            get
            {
                int total = 0;
                foreach (var e in Entries) total += e.Count;
                return total;
            }
        }

        public int CountOf(PickupIndex pickup)
        {
            int total = 0;
            foreach (var e in Entries)
                if (e.Pickup == pickup) total += e.Count;
            return total;
        }

        /// <summary>Adds to the end of the queue. Clicking the same item again just raises the count of the last entry.</summary>
        public void Add(PickupIndex pickup, int amount = 1)
        {
            if (amount <= 0) return;
            if (Entries.Count > 0 && Entries[Entries.Count - 1].Pickup == pickup)
                Entries[Entries.Count - 1].Count += amount;
            else
                Entries.Add(new QueueEntry(pickup, amount));
            Clamp();
        }

        /// <summary>Removes one of this item, starting with the last entry that contains it.</summary>
        public void RemoveOne(PickupIndex pickup)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                if (Entries[i].Pickup != pickup) continue;
                ChangeEntry(i, -1);
                return;
            }
        }

        public void RemoveAllOf(PickupIndex pickup)
        {
            for (int i = Entries.Count - 1; i >= 0; i--)
                if (Entries[i].Pickup == pickup) RemoveEntry(i);
        }

        public void ChangeEntry(int index, int delta)
        {
            if (index < 0 || index >= Entries.Count) return;
            Entries[index].Count += delta;
            if (Entries[index].Count <= 0) RemoveEntry(index);
            else Clamp();
        }

        public void RemoveEntry(int index)
        {
            if (index < 0 || index >= Entries.Count) return;
            Entries.RemoveAt(index);
            if (index < Cursor) Cursor--;
            else if (index == Cursor) UsedAtCursor = 0;
            Clamp();
        }

        public void Move(int index, int direction)
        {
            int target = index + direction;
            if (index < 0 || index >= Entries.Count || target < 0 || target >= Entries.Count) return;
            var tmp = Entries[index];
            Entries[index] = Entries[target];
            Entries[target] = tmp;
            if (Cursor == index) Cursor = target;
            else if (Cursor == target) Cursor = index;
            MergeNeighbours();
            Clamp();
        }

        /// <summary>Drag &amp; drop: moves an entry so it ends up in front of the entry that is currently at <paramref name="insertIndex"/>.</summary>
        public void MoveTo(int from, int insertIndex)
        {
            if (from < 0 || from >= Entries.Count) return;
            insertIndex = Math.Max(0, Math.Min(insertIndex, Entries.Count));
            if (insertIndex == from || insertIndex == from + 1) return;

            var entry = Entries[from];
            Entries.RemoveAt(from);
            if (insertIndex > from) insertIndex--;
            Entries.Insert(insertIndex, entry);
            MergeNeighbours();
            // New order = start from the front again (otherwise repeat mode would continue where it was).
            Cursor = 0;
            UsedAtCursor = 0;
        }

        /// <summary>Drag &amp; drop from the item list: inserts at a position (merging with a neighbour of the same item).</summary>
        public void Insert(int insertIndex, PickupIndex pickup, int amount = 1)
        {
            if (amount <= 0) return;
            insertIndex = Math.Max(0, Math.Min(insertIndex, Entries.Count));
            var current = Entries.Count > 0 ? Entries[Cursor] : null;

            if (insertIndex > 0 && Entries[insertIndex - 1].Pickup == pickup) Entries[insertIndex - 1].Count += amount;
            else if (insertIndex < Entries.Count && Entries[insertIndex].Pickup == pickup) Entries[insertIndex].Count += amount;
            else Entries.Insert(insertIndex, new QueueEntry(pickup, amount));

            RestoreCursor(current);
        }

        private void RestoreCursor(QueueEntry current)
        {
            int index = current != null ? Entries.IndexOf(current) : -1;
            if (index < 0) { Cursor = 0; UsedAtCursor = 0; }
            else Cursor = index;
            Clamp();
        }

        public void Clear()
        {
            Entries.Clear();
            Cursor = 0;
            UsedAtCursor = 0;
        }

        public void ResetCursor()
        {
            Cursor = 0;
            UsedAtCursor = 0;
        }

        private void MergeNeighbours()
        {
            for (int i = Entries.Count - 1; i > 0; i--)
            {
                if (Entries[i].Pickup != Entries[i - 1].Pickup) continue;
                Entries[i - 1].Count += Entries[i].Count;
                Entries.RemoveAt(i);
                if (Cursor >= i) Cursor = Math.Max(0, Cursor - 1);
            }
        }

        private void Clamp()
        {
            if (Entries.Count == 0) { Cursor = 0; UsedAtCursor = 0; return; }
            if (Cursor < 0 || Cursor >= Entries.Count) { Cursor = 0; UsedAtCursor = 0; }
            if (UsedAtCursor >= Entries[Cursor].Count) UsedAtCursor = 0;
        }

        /// <summary>Index of the entry that would be picked next (ignoring what a cube offers), or -1.</summary>
        public int PeekNextIndex(Func<PickupIndex, bool> isAvailable)
        {
            if (Entries.Count == 0) return -1;
            int start = Repeat ? Cursor : 0;
            for (int k = 0; k < Entries.Count; k++)
            {
                int i = (start + k) % Entries.Count;
                if (isAvailable == null || isAvailable(Entries[i].Pickup)) return i;
            }
            return -1;
        }

        /// <summary>
        /// Takes the next queued pickup that the cube actually offers.
        /// Entries the cube doesn't offer are skipped (and stay in the queue).
        /// </summary>
        public bool TryTake(Func<PickupIndex, int> optionIndexOf, out int optionIndex, out PickupIndex taken)
        {
            optionIndex = -1;
            taken = PickupIndex.none;
            if (Entries.Count == 0) return false;

            if (!Repeat)
            {
                for (int i = 0; i < Entries.Count; i++)
                {
                    int idx = optionIndexOf(Entries[i].Pickup);
                    if (idx < 0) continue;
                    optionIndex = idx;
                    taken = Entries[i].Pickup;
                    ChangeEntry(i, -1);
                    return true;
                }
                return false;
            }

            Clamp();
            for (int k = 0; k < Entries.Count; k++)
            {
                int i = (Cursor + k) % Entries.Count;
                int idx = optionIndexOf(Entries[i].Pickup);
                if (idx < 0) continue;

                if (i != Cursor) { Cursor = i; UsedAtCursor = 0; }
                optionIndex = idx;
                taken = Entries[i].Pickup;
                UsedAtCursor++;
                if (UsedAtCursor >= Entries[i].Count)
                {
                    Cursor = (Cursor + 1) % Entries.Count;
                    UsedAtCursor = 0;
                }
                return true;
            }
            return false;
        }
    }

    internal sealed class QueueManager
    {
        private readonly Dictionary<string, GroupQueue> queues = new Dictionary<string, GroupQueue>();
        /// <summary>Sets every category back to "run once" (the default at the start of each run).</summary>
        public void ResetRepeat()
        {
            foreach (var q in queues.Values)
            {
                q.Repeat = false;
                q.ResetCursor();
            }
        }

        public GroupQueue Get(string key)
        {
            if (!queues.TryGetValue(key, out var q))
                queues[key] = q = new GroupQueue(key);
            return q;
        }

        public GroupQueue TryGet(string key)
        {
            if (key == null) return null;
            queues.TryGetValue(key, out var q);
            return q;
        }

        public int TotalQueued
        {
            get
            {
                int total = 0;
                foreach (var q in queues.Values) total += q.TotalCount;
                return total;
            }
        }

        public void ClearAll()
        {
            foreach (var q in queues.Values) q.Clear();
        }

        public void ResetCursors()
        {
            foreach (var q in queues.Values) q.ResetCursor();
        }

        public BuildData Snapshot(string name)
        {
            var build = new BuildData { Name = name, Created = DateTime.Now };
            foreach (var q in queues.Values)
            {
                if (q.Repeat) build.RepeatKeys.Add(q.Key);
                if (q.Entries.Count == 0) continue;
                var list = new List<BuildEntry>();
                foreach (var e in q.Entries)
                {
                    var def = PickupCatalog.GetPickupDef(e.Pickup);
                    if (def == null) continue;
                    list.Add(new BuildEntry { InternalName = def.internalName, Count = e.Count });
                }
                build.Queues[q.Key] = list;
            }
            return build;
        }

        /// <summary>Replaces the current queues with a saved build. Unknown items (removed by an update / missing mod) are skipped.</summary>
        public int Apply(BuildData build)
        {
            ClearAll();

            int missing = 0;
            foreach (var pair in build.Queues)
            {
                foreach (var entry in pair.Value)
                {
                    var pickup = PickupCatalog.FindPickupIndex(entry.InternalName);
                    // Re-derive the category from the pickup itself, so a build stays valid even if tiers get reshuffled.
                    var key = pickup.isValid ? PickupGroups.KeyFor(pickup) : null;
                    if (key == null) { missing++; continue; }
                    Get(key).Add(pickup, entry.Count);
                }
            }

            // Each category gets the mode it had when the build was saved; everything else runs once.
            ResetRepeat();
            foreach (var key in build.RepeatKeys)
                Get(key).Repeat = true;
            ResetCursors();
            if (missing > 0) Log.Warning($"Build '{build.Name}': {missing} entries could not be found and were skipped.");
            return missing;
        }
    }
}
