using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RoR2;
using UnityEngine;

namespace CommandItemQueue
{
    /// <summary>
    /// The "Item Queue" view inside the Tab scoreboard, styled like the vanilla scoreboard
    /// (translucent slate rows, dark corner brackets, golden highlight lines, game font, game-style tooltips).
    /// Drawn with Unity's immediate-mode GUI on purpose: it does not depend on any RoR2 UI prefab,
    /// so game updates that rework the HUD can't break it.
    /// </summary>
    internal sealed class QueueWindow
    {
        // ------------------------------------------------------------ palette taken from the vanilla scoreboard
        private static readonly Color PanelColor = new Color(0.13f, 0.16f, 0.20f, 0.55f);
        private static readonly Color StripColor = new Color(0.27f, 0.31f, 0.38f, 0.62f);
        private static readonly Color StripHover = new Color(0.36f, 0.41f, 0.49f, 0.75f);
        private static readonly Color StripSelected = new Color(0.40f, 0.44f, 0.51f, 0.82f);
        private static readonly Color SlotColor = new Color(0.17f, 0.20f, 0.25f, 0.55f);
        private static readonly Color BracketColor = new Color(0.04f, 0.045f, 0.06f, 0.95f);
        private static readonly Color Gold = new Color(0.93f, 0.80f, 0.33f, 1f);
        private static readonly Color GoldFill = new Color(0.80f, 0.64f, 0.18f, 0.95f);
        private static readonly Color GoldHover = new Color(0.90f, 0.74f, 0.25f, 1f);
        private static readonly Color ButtonColor = new Color(0.12f, 0.15f, 0.19f, 0.85f);
        private static readonly Color ButtonHover = new Color(0.22f, 0.26f, 0.32f, 0.9f);
        private static readonly Color DangerFill = new Color(0.62f, 0.18f, 0.16f, 0.9f);
        private static readonly Color DangerHover = new Color(0.78f, 0.24f, 0.20f, 1f);
        private static readonly Color White = new Color(1f, 1f, 1f, 1f);
        private static readonly Color Muted = new Color(0.78f, 0.81f, 0.86f, 1f);
        private static readonly Color Dim = new Color(0.60f, 0.64f, 0.70f, 1f);
        private static readonly Color Red = new Color(0.90f, 0.36f, 0.30f, 1f);
        private static readonly Color Shadow = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color TooltipBody = new Color(0.27f, 0.30f, 0.36f, 0.94f);

        private const float ConfirmSeconds = 3f;

        private readonly CommandItemQueuePlugin plugin;

        private bool offered;
        private bool wasOpen;
        private bool showQueue;
        private string selectedKey;

        private Vector2 categoryScroll, queueScroll, itemScroll, buildScroll;

        private PickupIndex hoverPickup = PickupIndex.none;
        private string hoverHint;
        private Vector2 outerMouse;

        private string status;
        private bool statusIsError;
        private float statusUntil;

        private object pendingConfirm;
        private string pendingConfirmAction;
        private float pendingConfirmUntil;

        private bool loggedGuiError;
        private bool loggedCursorError;
        private bool stylesReady;
        private Font gameFont;
        private GUIStyle titleStyle, headerStyle, nameStyle, textStyle, smallStyle, smallWrapStyle, centerStyle,
            countStyle, slotNumberStyle, buttonStyle, buttonDarkStyle, tabStyle, tabActiveStyle,
            tooltipTitleStyle, tooltipBodyStyle, tooltipHintStyle, iconFallbackStyle;

        // Drag & drop
        private enum DragSource { None, Queue, Grid }
        private const float QueueSlot = 76f, QueueGap = 10f;
        private DragSource dragSource;
        private int dragIndex;
        private PickupIndex dragPickup = PickupIndex.none;
        private Vector2 dragStart;
        private bool dragging;
        private float lastMouseHeldTime;
        private Rect queueViewRect;      // outer (unscrolled) coordinates of the queue strip

        private readonly List<PickupGroup> visibleGroups = new List<PickupGroup>();
        private bool editCategories;
        private int hiddenCategoryCount;
        private readonly List<PickupIndex> visibleItems = new List<PickupIndex>();

        private static ArtifactDef commandArtifact;
        private static bool commandLookupDone;

        public QueueWindow(CommandItemQueuePlugin plugin)
        {
            this.plugin = plugin;
            showQueue = CommandItemQueuePlugin.Cfg.LastViewWasQueue.Value;
        }

        // ================================================================ per-frame logic

        public void Update()
        {
            var cfg = CommandItemQueuePlugin.Cfg;
            var scoreboard = plugin.Scoreboard;
            scoreboard.Update();

            bool allowed = Run.instance && (!cfg.OnlyWithArtifactOfCommand.Value || IsCommandEnabled());
            offered = scoreboard.IsOpen && allowed;

            if (offered && !wasOpen)
            {
                switch (cfg.DefaultView.Value)
                {
                    case StartView.Scoreboard: showQueue = false; break;
                    case StartView.ItemQueue: showQueue = true; break;
                    default: showQueue = cfg.LastViewWasQueue.Value; break;
                }
            }
            wasOpen = offered;

            if (offered && cfg.SwitchViewKey.Value.IsDown())
                SetView(!showQueue);

            scoreboard.SetHidden(offered && showQueue);
        }

        private void SetView(bool queue)
        {
            showQueue = queue;
            if (CommandItemQueuePlugin.Cfg.LastViewWasQueue.Value != queue)
                CommandItemQueuePlugin.Cfg.LastViewWasQueue.Value = queue;
        }

        private static bool IsCommandEnabled()
        {
            var manager = RunArtifactManager.instance;
            if (!manager) return false;
            if (!commandLookupDone)
            {
                commandLookupDone = true;
                commandArtifact = ArtifactCatalog.FindArtifactDef("Command");
                if (!commandArtifact) Log.Warning("Artifact of Command not found by name - the Item Queue tab will always be shown.");
            }
            return !commandArtifact || manager.IsArtifactEnabled(commandArtifact);
        }

        // ================================================================ drawing

        public void OnGUI()
        {
            if (!offered) return;
            var oldMatrix = GUI.matrix;
            var oldColor = GUI.color;
            try
            {
                Draw();
            }
            catch (Exception e)
            {
                if (!loggedGuiError)
                {
                    loggedGuiError = true;
                    Log.Error("Item Queue window error: " + e);
                }
            }
            finally
            {
                GUI.matrix = oldMatrix;
                GUI.color = oldColor;
            }

            // The game's cursor is part of its UI and ends up behind this window, so draw it again on top.
            try { CursorOverlay.Draw(); }
            catch (Exception e)
            {
                if (!loggedCursorError)
                {
                    loggedCursorError = true;
                    Log.Warning("Cursor overlay error: " + e);
                }
            }
        }

        private void Draw()
        {
            EnsureStyles();
            var cfg = CommandItemQueuePlugin.Cfg;

            // The layout is designed for a fixed virtual canvas. Scale 1 = as large as fits the screen,
            // smaller values shrink it. It can never grow past the screen, so nothing gets squeezed.
            const float designW = 1760f, designH = 1000f;
            float fit = Mathf.Min(Screen.width / designW, Screen.height / designH);
            float scale = Mathf.Max(0.2f, fit * Mathf.Clamp(cfg.UiScale.Value, 0.6f, 1f));
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));
            float vw = Screen.width / scale;
            float vh = Screen.height / scale;

            outerMouse = Event.current.mousePosition;
            hoverPickup = PickupIndex.none;
            hoverHint = null;

            float top = Mathf.Max(0f, (vh - designH) / 2f);
            DrawTabs(vw, top);
            if (!showQueue) { CancelDrag(); return; }

            HandleDragEvents();

            // Same footprint as the vanilla scoreboard: centred, leaving the HUD edges free.
            float w = Mathf.Min(1640f, vw - 40f);
            float h = Mathf.Min(860f, vh - top - 110f);
            var panel = new Rect((vw - w) / 2f, top + 92f, w, h);

            const float gap = 18f;
            var categories = new Rect(panel.x, panel.y, 260f, h);
            var builds = new Rect(panel.xMax - 330f, panel.y, 330f, h);
            var main = new Rect(categories.xMax + gap, panel.y, builds.x - gap - (categories.xMax + gap), h);

            CollectVisibleGroups();
            DrawCategories(categories);
            DrawMain(main);
            DrawBuilds(builds);

            var e = Event.current;
            if (e.type == EventType.MouseDown && panel.Contains(e.mousePosition)) e.Use();

            if (dragging) DrawDragGhost();
            else if (cfg.ShowTooltips.Value) DrawTooltip(vw, vh);
        }

        // ------------------------------------------------------------ drag & drop

        private void StartPress(DragSource source, int index, PickupIndex pickup)
        {
            dragSource = source;
            dragIndex = index;
            dragPickup = pickup;
            dragStart = outerMouse;
            dragging = false;
            lastMouseHeldTime = Time.unscaledTime;
        }

        private void CancelDrag()
        {
            dragSource = DragSource.None;
            dragging = false;
            dragPickup = PickupIndex.none;
        }

        private void HandleDragEvents()
        {
            if (dragSource == DragSource.None) return;
            var e = Event.current;

            if (e.type == EventType.MouseDrag && !dragging && (outerMouse - dragStart).sqrMagnitude > 64f)
                dragging = true;

            if (e.type == EventType.MouseUp && e.button == 0)
            {
                FinishPress();
                e.Use();
                return;
            }

            // Safety net: if the button was released outside of the window we never get MouseUp.
            if (e.type == EventType.Repaint)
            {
                bool held = true;
                try { held = Input.GetMouseButton(0); } catch { /* legacy input unavailable */ }
                if (held) lastMouseHeldTime = Time.unscaledTime;
                else if (Time.unscaledTime - lastMouseHeldTime > 0.3f) CancelDrag();
            }
        }

        private void FinishPress()
        {
            var group = PickupGroups.Find(selectedKey);
            var queue = group != null ? plugin.Queues.Get(group.Key) : null;
            var source = dragSource;
            bool wasDrag = dragging;
            int index = dragIndex;
            var pickup = dragPickup;
            CancelDrag();
            if (queue == null) return;

            bool overQueue = queueViewRect.Contains(outerMouse);
            if (source == DragSource.Queue)
            {
                if (!wasDrag) queue.ChangeEntry(index, +1);
                else if (overQueue) queue.MoveTo(index, DropIndex(queue));
                else
                {
                    queue.RemoveEntry(index);
                    SetStatus($"Removed {PickupGroups.DisplayName(pickup)} from the queue.");
                }
            }
            else if (source == DragSource.Grid)
            {
                if (!wasDrag) queue.Add(pickup, 1);
                else if (overQueue) queue.Insert(DropIndex(queue), pickup, 1);
            }
        }

        /// <summary>Insert position in the queue strip under the mouse.</summary>
        private int DropIndex(GroupQueue queue)
        {
            float x = outerMouse.x - queueViewRect.x + queueScroll.x;
            int index = Mathf.FloorToInt((x + QueueSlot * 0.5f) / (QueueSlot + QueueGap));
            return Mathf.Clamp(index, 0, queue.Entries.Count);
        }

        private void DrawDragGhost()
        {
            if (!dragPickup.isValid) return;
            const float size = 64f;
            var r = new Rect(outerMouse.x - size / 2f, outerMouse.y - size / 2f, size, size);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            DrawIcon(r, dragPickup, true);
            GUI.color = old;

            bool overQueue = queueViewRect.Contains(outerMouse);
            string hint = overQueue ? "Drop to place here"
                : dragSource == DragSource.Queue ? "Drop outside to remove" : "Drop onto the queue";
            ShadowLabel(new Rect(outerMouse.x + 36f, outerMouse.y - 10f, 260f, 22f), hint,
                dragSource == DragSource.Queue && !overQueue ? centeredSmallRedLeft : goldSmallLeft);
        }

        private void DrawTabs(float vw, float top)
        {
            const float tabW = 250f, tabH = 40f;
            float y = top + 14f;
            int total = plugin.Queues.TotalQueued;
            var left = new Rect(vw / 2f - tabW - 4f, y, tabW, tabH);
            var right = new Rect(vw / 2f + 4f, y, tabW, tabH);

            if (Tab(left, "Scoreboard", !showQueue)) SetView(false);
            if (Tab(right, total > 0 ? $"Item Queue  ·  {total}" : "Item Queue", showQueue)) SetView(true);

            // Keep the window open without holding Tab (Tab closes it again).
            var scoreboard = plugin.Scoreboard;
            var pin = new Rect(right.xMax + 14f, y + 4f, 190f, tabH - 8f);
            if (Button(pin, scoreboard.Pinned ? "Pinned · Tab closes" : "Keep open", scoreboard.Pinned ? ButtonKind.Active : ButtonKind.Normal))
                scoreboard.SetPinned(!scoreboard.Pinned);
        }

        private bool Tab(Rect r, string text, bool active)
        {
            var e = Event.current;
            bool hover = r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                // Like the game's difficulty buttons: gold when selected, dark slate otherwise.
                Fill(r, active ? GoldFill : hover ? ButtonHover : ButtonColor);
                Brackets(r, BracketColor, 10f, 2f);
                ShadowLabel(r, text, active ? tabActiveStyle : tabStyle, !active);
            }
            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                e.Use();
                return !active;
            }
            return false;
        }

        // ------------------------------------------------------------ categories (left)

        private void CollectVisibleGroups()
        {
            var cfg = CommandItemQueuePlugin.Cfg;
            bool showUnavailable = cfg.ShowUnavailableItems.Value;
            visibleGroups.Clear();
            hiddenCategoryCount = 0;
            foreach (var g in PickupGroups.GetAll())
            {
                var q = plugin.Queues.TryGet(g.Key);
                bool hasQueue = q != null && q.Entries.Count > 0;
                if (!(g.Available.Count > 0 || hasQueue || (showUnavailable && g.Members.Count > 0))) continue;

                if (!cfg.IsCategoryVisible(g))
                {
                    hiddenCategoryCount++;
                    if (!editCategories) continue; // hidden categories only show up while editing
                }
                visibleGroups.Add(g);
            }

            bool selectedVisible = false;
            foreach (var g in visibleGroups)
                if (g.Key == selectedKey) { selectedVisible = true; break; }
            if (!selectedVisible)
                selectedKey = visibleGroups.Count > 0 ? visibleGroups[0].Key : null;
        }

        private void DrawCategories(Rect area)
        {
            Section(area);
            ShadowLabel(new Rect(area.x + 14f, area.y + 8f, area.width - 28f, 30f), "Categories", headerStyle);

            const float rowH = 52f, gap = 8f;
            var view = new Rect(area.x + 10f, area.y + 46f, area.width - 20f, area.height - 46f - 108f);
            bool needsBar = visibleGroups.Count * (rowH + gap) > view.height;
            var content = new Rect(0f, 0f, view.width - (needsBar ? 16f : 0f), visibleGroups.Count * (rowH + gap));
            bool inView = view.Contains(Event.current.mousePosition);

            categoryScroll = GUI.BeginScrollView(view, categoryScroll, content, false, false);
            for (int i = 0; i < visibleGroups.Count; i++)
            {
                var g = visibleGroups[i];
                var r = new Rect(0f, i * (rowH + gap) + 2f, content.width, rowH);
                var q = plugin.Queues.TryGet(g.Key);
                bool selected = g.Key == selectedKey;
                bool hover = inView && r.Contains(Event.current.mousePosition);

                bool shown = !editCategories || CommandItemQueuePlugin.Cfg.IsCategoryVisible(g);

                if (Event.current.type == EventType.Repaint)
                {
                    var oldColor = GUI.color;
                    if (!shown) GUI.color = new Color(1f, 1f, 1f, 0.4f);
                    Fill(r, selected ? StripSelected : hover ? StripHover : StripColor);
                    if (selected) GoldLines(r);
                    Fill(new Rect(r.x + 8f, r.y + 10f, 4f, r.height - 20f), g.Color);
                    Brackets(r, BracketColor, 9f, 2f);

                    ShadowLabel(new Rect(r.x + 22f, r.y + 6f, r.width - 90f, 24f), g.DisplayName, nameStyle);
                    ShadowLabel(new Rect(r.x + 22f, r.y + 29f, r.width - 90f, 18f),
                        !shown ? "hidden" : $"{g.Available.Count} items" + (q != null && q.Repeat ? "  ·  repeat" : ""), smallStyle);

                    int count = q != null ? q.TotalCount : 0;
                    if (count > 0 && !editCategories)
                        ShadowLabel(new Rect(r.xMax - 70f, r.y, 58f, rowH), "x" + count, countStyle);
                    GUI.color = oldColor;
                }

                if (editCategories)
                {
                    var toggle = new Rect(r.xMax - 74f, r.y + 10f, 64f, rowH - 20f);
                    if (Button(toggle, shown ? "Hide" : "Show", shown ? ButtonKind.Normal : ButtonKind.Active, inView))
                    {
                        CommandItemQueuePlugin.Cfg.CategoryToggle(g).Value = !shown;
                        continue;
                    }
                }

                if (inView && Clicked(r) == 0)
                {
                    selectedKey = g.Key;
                    itemScroll = Vector2.zero;
                    queueScroll = Vector2.zero;
                }
            }
            GUI.EndScrollView();

            if (visibleGroups.Count == 0)
                ShadowLabel(new Rect(area.x + 14f, area.y + 50f, area.width - 28f, 60f), "No categories yet.\nThey appear once the run is loaded.", smallWrapStyle);

            var edit = new Rect(area.x + 10f, area.yMax - 98f, area.width - 20f, 40f);
            string editText = editCategories ? "Done"
                : hiddenCategoryCount > 0 ? $"Show / hide categories ({hiddenCategoryCount} hidden)" : "Show / hide categories";
            if (Button(edit, editText, editCategories ? ButtonKind.Active : ButtonKind.Normal))
            {
                editCategories = !editCategories;
                if (editCategories) SetStatus("Click Hide / Show next to a category. Hidden categories stay hidden in every run (also in Mod Options).");
            }

            var clearAll = new Rect(area.x + 10f, area.yMax - 52f, area.width - 20f, 40f);
            bool confirming = IsConfirming("clearall", this);
            if (Button(clearAll, confirming ? "Click again to clear all" : "Clear all queues", confirming ? ButtonKind.Danger : ButtonKind.Normal))
            {
                if (confirming)
                {
                    plugin.Queues.ClearAll();
                    ClearConfirm();
                    SetStatus("All queues cleared.");
                }
                else StartConfirm("clearall", this);
            }
        }

        // ------------------------------------------------------------ main (queue + items)

        private void DrawMain(Rect area)
        {
            var group = PickupGroups.Find(selectedKey);
            if (group == null)
            {
                Section(area);
                ShadowLabel(area, "Nothing to show yet.", centerStyle);
                return;
            }
            var queue = plugin.Queues.Get(group.Key);

            // Header row, like the "Players / Items / Equipment" header of the scoreboard
            var header = new Rect(area.x, area.y, area.width, 60f);
            Fill(header, StripSelected);
            GoldLines(header);
            Brackets(header, BracketColor, 12f, 2f);
            Fill(new Rect(header.x + 10f, header.y + 12f, 5f, header.height - 24f), group.Color);

            var oldTitle = titleStyle.normal.textColor;
            titleStyle.normal.textColor = group.Color;
            ShadowLabel(new Rect(header.x + 26f, header.y + 4f, header.width - 440f, 34f), group.DisplayName, titleStyle);
            titleStyle.normal.textColor = oldTitle;
            ShadowLabel(new Rect(header.x + 26f, header.y + 36f, header.width - 440f, 18f),
                $"{group.Available.Count} items available this run  ·  {queue.TotalCount} queued", smallStyle);

            float bx = header.xMax - 14f;
            var clear = new Rect(bx - 96f, header.y + 12f, 96f, 36f);
            var repeat = new Rect(clear.x - 10f - 130f, header.y + 12f, 130f, 36f);
            var once = new Rect(repeat.x - 4f - 130f, header.y + 12f, 130f, 36f);

            // Per category; saved with the build.
            if (Button(once, "Run once", queue.Repeat ? ButtonKind.Normal : ButtonKind.Active))
            {
                queue.Repeat = false;
                queue.ResetCursor();
                SetStatus($"{group.DisplayName}: the queue is used up entry by entry.");
            }
            if (Button(repeat, "Repeat", queue.Repeat ? ButtonKind.Active : ButtonKind.Normal))
            {
                queue.Repeat = true;
                queue.ResetCursor();
                SetStatus($"{group.DisplayName}: the queue starts over when it reaches the end.");
            }
            if (Button(clear, "Clear", ButtonKind.Normal))
            {
                queue.Clear();
                SetStatus($"{group.DisplayName} queue cleared.");
            }

            var queueArea = new Rect(area.x, header.yMax + 12f, area.width, 150f);
            DrawQueueStrip(queueArea, group, queue);

            const float footerH = 28f;
            var itemsArea = new Rect(area.x, queueArea.yMax + 12f, area.width, area.yMax - footerH - 6f - (queueArea.yMax + 12f));
            DrawItemGrid(itemsArea, group, queue);

            var footer = new Rect(area.x + 6f, area.yMax - footerH, area.width - 12f, footerH);
            if (!string.IsNullOrEmpty(status) && Time.unscaledTime < statusUntil)
            {
                var old = textStyle.normal.textColor;
                textStyle.normal.textColor = statusIsError ? Red : Gold;
                ShadowLabel(footer, status, textStyle);
                textStyle.normal.textColor = old;
            }
            else
            {
                var key = CommandItemQueuePlugin.Cfg.ManualPickKey.Value;
                string manual = key.MainKey != KeyCode.None ? $"  ·  Hold {key} while opening a cube to choose yourself" : "";
                ShadowLabel(footer, "Items a cube can't offer are skipped automatically." + manual, smallStyle);
            }
        }

        private void DrawQueueStrip(Rect area, PickupGroup group, GroupQueue queue)
        {
            Section(area);
            ShadowLabel(new Rect(area.x + 14f, area.y + 6f, 120f, 28f), "Queue", headerStyle);
            ShadowLabel(new Rect(area.x + 110f, area.y + 8f, area.width - 124f, 24f),
                "Left +1   ·   Right −1   ·   Drag to reorder   ·   Drag out to remove", smallStyle);

            var view = new Rect(area.x + 14f, area.y + 40f, area.width - 28f, area.height - 44f);
            queueViewRect = view;
            bool dropping = dragging && view.Contains(outerMouse);

            if (queue.Entries.Count == 0)
            {
                if (dropping) Fill(view, new Color(Gold.r, Gold.g, Gold.b, 0.12f));
                ShadowLabel(view, dropping ? "Drop here" : "Empty – click or drag items below to queue them. The first entry is picked first.", centerStyle);
                return;
            }

            int next = queue.PeekNextIndex(group.IsAvailable);
            var content = new Rect(0f, 0f, Mathf.Max(view.width, (queue.Entries.Count + 1) * (QueueSlot + QueueGap)), QueueSlot + 22f);
            bool inView = view.Contains(Event.current.mousePosition);
            int dropIndex = dropping ? DropIndex(queue) : -1;

            queueScroll = GUI.BeginScrollView(view, queueScroll, content, false, false);
            for (int i = 0; i < queue.Entries.Count; i++)
            {
                var entry = queue.Entries[i];
                var r = new Rect(i * (QueueSlot + QueueGap), 2f, QueueSlot, QueueSlot);
                if (r.xMax < queueScroll.x - 4f || r.x > queueScroll.x + view.width + 4f) continue;

                bool hover = !dragging && inView && r.Contains(Event.current.mousePosition);
                bool available = group.IsAvailable(entry.Pickup);
                bool beingDragged = dragging && dragSource == DragSource.Queue && dragIndex == i;

                if (Event.current.type == EventType.Repaint)
                {
                    var oldColor = GUI.color;
                    if (beingDragged) GUI.color = new Color(1f, 1f, 1f, 0.3f);
                    Fill(r, hover ? StripHover : SlotColor);
                    DrawIcon(r, entry.Pickup, available);
                    Brackets(r, i == next ? Gold : BracketColor, 12f, i == next ? 3f : 2f);
                    ShadowLabel(new Rect(r.x + 5f, r.y + 2f, 30f, 20f), (i + 1).ToString(), slotNumberStyle);

                    string countText = queue.Repeat && i == queue.Cursor && queue.UsedAtCursor > 0
                        ? $"{queue.UsedAtCursor}/{entry.Count}"
                        : "x" + entry.Count;
                    ShadowLabel(new Rect(r.x, r.y + 2f, r.width - 5f, 22f), countText, countStyle);

                    if (i == next) ShadowLabel(new Rect(r.x, r.yMax + 2f, r.width, 18f), "NEXT", centeredSmall);
                    else if (!available) ShadowLabel(new Rect(r.x - 10f, r.yMax + 2f, r.width + 20f, 18f), "not in run", centeredSmallRed);
                    GUI.color = oldColor;
                }

                if (hover)
                {
                    hoverPickup = entry.Pickup;
                    hoverHint = available ? $"Queue slot {i + 1}: picked {entry.Count}x" : "Not available in this run – skipped.";
                }

                if (inView && !dragging)
                {
                    int button = Clicked(r);
                    if (button == 0) StartPress(DragSource.Queue, i, entry.Pickup);
                    else if (button == 1) { queue.ChangeEntry(i, -1); break; }
                    else if (button == 2) { queue.RemoveEntry(i); break; }
                }
            }

            if (dropIndex >= 0 && Event.current.type == EventType.Repaint)
            {
                float x = dropIndex * (QueueSlot + QueueGap) - QueueGap / 2f - 2f;
                Fill(new Rect(Mathf.Max(0f, x), 0f, 4f, QueueSlot + 4f), Gold);
            }
            GUI.EndScrollView();
        }

        private void DrawItemGrid(Rect area, PickupGroup group, GroupQueue queue)
        {
            Section(area);
            ShadowLabel(new Rect(area.x + 14f, area.y + 6f, 120f, 28f), "Items", headerStyle);
            ShadowLabel(new Rect(area.x + 110f, area.y + 8f, area.width - 124f, 24f),
                "Left add   ·   Right remove   ·   Drag onto the queue to insert at a position", smallStyle);

            bool showUnavailable = CommandItemQueuePlugin.Cfg.ShowUnavailableItems.Value;
            visibleItems.Clear();
            foreach (var p in group.Members)
                if (showUnavailable || group.IsAvailable(p))
                    visibleItems.Add(p);

            const float cell = 76f, gap = 10f;
            var view = new Rect(area.x + 14f, area.y + 40f, area.width - 20f, area.height - 48f);
            int cols = Mathf.Max(1, Mathf.FloorToInt((view.width - 16f + gap) / (cell + gap)));
            int rows = Mathf.CeilToInt(visibleItems.Count / (float)cols);
            var content = new Rect(0f, 0f, view.width - 16f, Mathf.Max(view.height, rows * (cell + gap) + 4f));
            bool inView = view.Contains(Event.current.mousePosition);

            itemScroll = GUI.BeginScrollView(view, itemScroll, content, false, false);
            for (int i = 0; i < visibleItems.Count; i++)
            {
                var pickup = visibleItems[i];
                var r = new Rect((i % cols) * (cell + gap) + 2f, (i / cols) * (cell + gap) + 2f, cell, cell);
                if (r.yMax < itemScroll.y - 4f || r.y > itemScroll.y + view.height + 4f) continue;

                bool hover = !dragging && inView && r.Contains(Event.current.mousePosition);
                bool available = group.IsAvailable(pickup);
                int queued = queue.CountOf(pickup);

                if (Event.current.type == EventType.Repaint)
                {
                    Fill(r, hover ? StripHover : SlotColor);
                    DrawIcon(r, pickup, available);
                    Brackets(r, queued > 0 ? Gold : BracketColor, 12f, queued > 0 ? 3f : 2f);
                    if (queued > 0)
                        ShadowLabel(new Rect(r.x, r.y + 2f, r.width - 5f, 22f), "x" + queued, countStyle);
                }

                if (hover)
                {
                    hoverPickup = pickup;
                    hoverHint = !available ? "Not available in this run – will be skipped."
                        : queued > 0 ? $"In queue: {queued}x" : "Click to add to the queue.";
                }

                if (inView && !dragging)
                {
                    int button = Clicked(r);
                    if (button == 0) StartPress(DragSource.Grid, -1, pickup);
                    else if (button == 1) queue.RemoveOne(pickup);
                }
            }
            GUI.EndScrollView();

            if (visibleItems.Count == 0)
                ShadowLabel(view, "No items of this category can drop in this run.", centerStyle);
        }

        // ------------------------------------------------------------ builds (right)

        private void DrawBuilds(Rect area)
        {
            Section(area);
            ShadowLabel(new Rect(area.x + 14f, area.y + 8f, area.width - 28f, 30f), "Builds", headerStyle);

            var save = new Rect(area.x + 10f, area.y + 46f, area.width - 20f, 44f);
            if (Button(save, "Save queue as new build", ButtonKind.Active))
            {
                if (plugin.Queues.TotalQueued == 0) SetStatus("The queue is empty – nothing to save.", true);
                else
                {
                    var build = plugin.Queues.Snapshot(MakeBuildName());
                    plugin.Builds.Add(build);
                    buildScroll = Vector2.zero;
                    BeginRename(build);
                    SetStatus("Saved. Type a name and press Enter (or click Save).");
                }
            }

            var builds = plugin.Builds.Builds;
            const float cardH = 150f, gap = 10f;
            var view = new Rect(area.x + 10f, save.yMax + 12f, area.width - 20f, area.yMax - (save.yMax + 12f) - 50f);
            bool needsBar = builds.Count * (cardH + gap) > view.height;
            var content = new Rect(0f, 0f, view.width - (needsBar ? 16f : 0f), builds.Count * (cardH + gap));
            bool inView = view.Contains(Event.current.mousePosition);

            if (builds.Count == 0)
                ShadowLabel(view, "No builds yet.\nSet up your queues and save them\nhere to reuse them in later runs.", centerStyle);

            BuildData toRemove = null;
            buildScroll = GUI.BeginScrollView(view, buildScroll, content, false, false);
            for (int i = 0; i < builds.Count; i++)
            {
                var b = builds[i];
                var r = new Rect(0f, i * (cardH + gap) + 2f, content.width, cardH);
                if (r.yMax < buildScroll.y - 4f || r.y > buildScroll.y + view.height + 4f) continue;
                bool editing = renaming == b;

                if (Event.current.type == EventType.Repaint)
                {
                    Fill(r, b.AutoLoad || editing ? StripSelected : StripColor);
                    if (b.AutoLoad || editing) GoldLines(r);
                    Brackets(r, BracketColor, 10f, 2f);
                    if (!editing) ShadowLabel(new Rect(r.x + 12f, r.y + 6f, r.width - 24f, 26f), b.Name, nameStyle);
                    ShadowLabel(new Rect(r.x + 12f, r.y + 34f, r.width - 24f, 18f), DescribeBuild(b), smallStyle);
                    if (b.AutoLoad)
                    {
                        var old = smallStyle.normal.textColor;
                        smallStyle.normal.textColor = Gold;
                        ShadowLabel(new Rect(r.x + 12f, r.y + 52f, r.width - 24f, 18f), "Loads automatically at run start", smallStyle);
                        smallStyle.normal.textColor = old;
                    }
                    else
                        ShadowLabel(new Rect(r.x + 12f, r.y + 52f, r.width - 24f, 18f), b.Created.ToString("dd.MM.yyyy HH:mm"), smallStyle);
                }

                float bw3 = (r.width - 24f - 2f * 6f) / 3f;
                float row1 = r.yMax - 76f, row2 = r.yMax - 40f;

                if (editing)
                {
                    DrawRenameField(new Rect(r.x + 10f, r.y + 4f, r.width - 20f, 30f));
                    var ok = new Rect(r.x + 12f, row2, (r.width - 30f) / 2f, 32f);
                    var cancel = new Rect(ok.xMax + 6f, row2, ok.width, 32f);
                    if (Button(ok, "Save name", ButtonKind.Active, inView)) CommitRename();
                    if (Button(cancel, "Cancel", ButtonKind.Normal, inView)) CancelRename();
                    continue;
                }

                // Row 1: Load · Update · Delete
                var load = new Rect(r.x + 12f, row1, bw3, 32f);
                var over = new Rect(load.xMax + 6f, row1, bw3, 32f);
                var del = new Rect(over.xMax + 6f, row1, bw3, 32f);
                // Row 2: Rename · Auto
                float bw2 = (r.width - 24f - 6f) / 2f;
                var rename = new Rect(r.x + 12f, row2, bw2, 32f);
                var auto = new Rect(rename.xMax + 6f, row2, bw2, 32f);

                bool confirmOverwrite = IsConfirming("overwrite", b);
                bool confirmDelete = IsConfirming("delete", b);

                if (Button(load, "Load", ButtonKind.Normal, inView))
                {
                    int missing = plugin.Queues.Apply(b);
                    SetStatus(missing > 0 ? $"Loaded '{b.Name}' ({missing} unknown items skipped)." : $"Loaded '{b.Name}'.");
                }
                if (Button(over, confirmOverwrite ? "Sure?" : "Update", confirmOverwrite ? ButtonKind.Danger : ButtonKind.Normal, inView))
                {
                    if (confirmOverwrite)
                    {
                        b.CopyContentFrom(plugin.Queues.Snapshot(b.Name));
                        b.Created = DateTime.Now;
                        plugin.Builds.Save();
                        ClearConfirm();
                        SetStatus($"Updated '{b.Name}' with the current queue.");
                    }
                    else StartConfirm("overwrite", b);
                }
                if (Button(del, confirmDelete ? "Sure?" : "Delete", confirmDelete ? ButtonKind.Danger : ButtonKind.Normal, inView))
                {
                    if (confirmDelete)
                    {
                        toRemove = b;
                        ClearConfirm();
                    }
                    else StartConfirm("delete", b);
                }
                if (Button(rename, "Rename", ButtonKind.Normal, inView)) BeginRename(b);
                if (Button(auto, "Auto", b.AutoLoad ? ButtonKind.Active : ButtonKind.Normal, inView))
                {
                    plugin.Builds.SetAutoLoad(b, !b.AutoLoad);
                    SetStatus(b.AutoLoad ? $"'{b.Name}' will be loaded automatically at run start." : "Auto-load disabled.");
                }
            }
            GUI.EndScrollView();

            if (toRemove != null)
            {
                if (renaming == toRemove) CancelRename();
                plugin.Builds.Remove(toRemove);
                SetStatus($"Deleted '{toRemove.Name}'.");
            }

            ShadowLabel(new Rect(area.x + 14f, area.yMax - 46f, area.width - 28f, 40f),
                "Each category's Run once / Repeat setting is saved with the build.", smallWrapStyle);
        }

        // ------------------------------------------------------------ renaming

        private const string RenameControl = "CommandItemQueue_Rename";
        private BuildData renaming;
        private string renameText = "";
        private bool focusRename;
        private bool pinnedForRename;

        private void BeginRename(BuildData build)
        {
            renaming = build;
            renameText = build.Name ?? "";
            focusRename = true;
            // Typing while holding Tab is awkward - keep the window open until the name is saved.
            if (!plugin.Scoreboard.Pinned)
            {
                plugin.Scoreboard.SetPinned(true);
                pinnedForRename = plugin.Scoreboard.Pinned;
            }
        }

        private void CommitRename()
        {
            if (renaming == null) return;
            var name = (renameText ?? "").Trim();
            if (name.Length > 0)
            {
                renaming.Name = name;
                plugin.Builds.Save();
                SetStatus($"Renamed to '{name}'.");
            }
            EndRename();
        }

        private void CancelRename() => EndRename();

        private void EndRename()
        {
            renaming = null;
            GUI.FocusControl(null);
            if (pinnedForRename) plugin.Scoreboard.SetPinned(false);
            pinnedForRename = false;
        }

        private void DrawRenameField(Rect r)
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == RenameControl)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { e.Use(); CommitRename(); return; }
                if (e.keyCode == KeyCode.Escape) { e.Use(); CancelRename(); return; }
            }

            GUI.SetNextControlName(RenameControl);
            renameText = GUI.TextField(r, renameText ?? "", 48, textFieldStyle);
            if (focusRename && e.type == EventType.Repaint)
            {
                GUI.FocusControl(RenameControl);
                focusRename = false;
            }
        }

        private string DescribeBuild(BuildData b)
        {
            var names = new List<string>();
            foreach (var key in b.Queues.Keys)
            {
                var g = PickupGroups.Find(key);
                names.Add(g != null ? g.DisplayName : key);
            }
            string cats = names.Count <= 2 ? string.Join(", ", names.ToArray()) : $"{names[0]}, {names[1]} +{names.Count - 2}";
            string text = $"{b.TotalCount} picks  ·  {cats}";
            if (b.RepeatKeys.Count > 0)
            {
                var rep = new List<string>();
                foreach (var key in b.RepeatKeys)
                {
                    var g = PickupGroups.Find(key);
                    rep.Add(g != null ? g.DisplayName : key);
                }
                text += "  ·  repeat: " + string.Join(", ", rep.ToArray());
            }
            return text;
        }

        private static string MakeBuildName()
        {
            string survivor = null;
            try
            {
                var user = LocalUserManager.GetFirstLocalUser();
                var body = user != null ? user.cachedBody : null;
                if (body) survivor = body.GetDisplayName();
            }
            catch { /* ignore */ }
            if (string.IsNullOrEmpty(survivor)) survivor = "Build";
            return $"{StripAllTags(survivor)} · {DateTime.Now:dd.MM. HH:mm}";
        }

        // ------------------------------------------------------------ tooltip (vanilla look: tier-coloured title bar, slate body)

        private void DrawTooltip(float vw, float vh)
        {
            if (!hoverPickup.isValid) return;
            const float width = 540f, pad = 16f;
            float inner = width - pad * 2f;

            string name = PickupGroups.DisplayName(hoverPickup);
            string shortText = GameRichText(PickupGroups.Description(hoverPickup));
            string longText = GameRichText(PickupGroups.LongDescription(hoverPickup));
            string body = string.IsNullOrEmpty(longText) ? shortText
                : string.IsNullOrEmpty(shortText) ? longText
                : shortText + "\n\n" + longText;

            float titleH = Mathf.Max(52f, tooltipTitleStyle.CalcHeight(new GUIContent(name), inner) + 18f);
            float bodyH = string.IsNullOrEmpty(body) ? 0f : tooltipBodyStyle.CalcHeight(new GUIContent(body), inner);
            float hintH = string.IsNullOrEmpty(hoverHint) ? 0f : 24f;
            float height = titleH + pad + bodyH + (hintH > 0f ? 10f + hintH : 0f) + pad;

            float x = outerMouse.x + 22f;
            float y = outerMouse.y + 22f;
            if (x + width > vw - 8f) x = outerMouse.x - width - 14f;
            if (y + height > vh - 8f) y = vh - height - 8f;
            var r = new Rect(x, y, width, height);

            var tier = PickupGroups.PickupColor(hoverPickup);
            var titleBar = new Rect(r.x, r.y, r.width, titleH);
            Fill(new Rect(r.x, r.y + titleH, r.width, r.height - titleH), TooltipBody);
            Fill(titleBar, new Color(tier.r * 0.85f, tier.g * 0.85f, tier.b * 0.85f, 0.96f));
            ShadowLabel(new Rect(r.x + pad, r.y, inner, titleH), name, tooltipTitleStyle);

            float cy = r.y + titleH + pad;
            if (bodyH > 0f)
            {
                ShadowLabel(new Rect(r.x + pad, cy, inner, bodyH), body, tooltipBodyStyle);
                cy += bodyH + 10f;
            }
            if (hintH > 0f)
                ShadowLabel(new Rect(r.x + pad, cy, inner, hintH), hoverHint, tooltipHintStyle);
        }

        // ------------------------------------------------------------ drawing helpers

        private enum ButtonKind { Normal, Active, Danger }

        private bool Button(Rect r, string text, ButtonKind kind, bool enabled = true)
        {
            var e = Event.current;
            bool hover = enabled && r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                Color bg;
                switch (kind)
                {
                    case ButtonKind.Active: bg = hover ? GoldHover : GoldFill; break;
                    case ButtonKind.Danger: bg = hover ? DangerHover : DangerFill; break;
                    default: bg = hover ? ButtonHover : ButtonColor; break;
                }
                Fill(r, bg);
                Brackets(r, BracketColor, 8f, 2f);
                ShadowLabel(r, text, kind == ButtonKind.Active ? buttonDarkStyle : buttonStyle, kind != ButtonKind.Active);
            }
            if (enabled && e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                e.Use();
                return true;
            }
            return false;
        }

        /// <summary>Returns the mouse button that clicked inside the rect this event (and consumes it), or -1.</summary>
        private static int Clicked(Rect r)
        {
            var e = Event.current;
            if (e.type != EventType.MouseDown || !r.Contains(e.mousePosition)) return -1;
            int button = e.button;
            e.Use();
            return button;
        }

        private void DrawIcon(Rect r, PickupIndex pickup, bool available)
        {
            var old = GUI.color;
            if (!available) GUI.color = new Color(old.r, old.g, old.b, old.a * 0.3f);
            var inner = new Rect(r.x + 6f, r.y + 6f, r.width - 12f, r.height - 12f);
            if (PickupGroups.TryGetIcon(pickup, out var tex, out var uv, out var aspect))
            {
                // Keep the icon's own proportions (fit inside the slot).
                if (aspect > 1f) { float h = inner.width / aspect; inner = new Rect(inner.x, inner.y + (inner.height - h) / 2f, inner.width, h); }
                else if (aspect < 1f) { float w = inner.height * aspect; inner = new Rect(inner.x + (inner.width - w) / 2f, inner.y, w, inner.height); }
                GUI.DrawTextureWithTexCoords(inner, tex, uv, true);
            }
            else
                GUI.Label(inner, PickupGroups.DisplayName(pickup), iconFallbackStyle);
            GUI.color = old;
        }

        /// <summary>Translucent slate panel with dark corner brackets, like the scoreboard.</summary>
        private static void Section(Rect r)
        {
            Fill(r, PanelColor);
            Brackets(r, BracketColor, 16f, 2f);
        }

        /// <summary>The thin golden lines the scoreboard draws above and below the local player's row.</summary>
        private static void GoldLines(Rect r)
        {
            Fill(new Rect(r.x, r.y, r.width, 2f), Gold);
            Fill(new Rect(r.x, r.yMax - 2f, r.width, 2f), Gold);
        }

        /// <summary>L-shaped corner brackets – the signature frame of RoR2's HUD.</summary>
        private static void Brackets(Rect r, Color c, float length, float thickness)
        {
            float lx = Mathf.Min(length, r.width / 2f), ly = Mathf.Min(length, r.height / 2f);
            // top-left
            Fill(new Rect(r.x, r.y, lx, thickness), c);
            Fill(new Rect(r.x, r.y, thickness, ly), c);
            // top-right
            Fill(new Rect(r.xMax - lx, r.y, lx, thickness), c);
            Fill(new Rect(r.xMax - thickness, r.y, thickness, ly), c);
            // bottom-left
            Fill(new Rect(r.x, r.yMax - thickness, lx, thickness), c);
            Fill(new Rect(r.x, r.yMax - ly, thickness, ly), c);
            // bottom-right
            Fill(new Rect(r.xMax - lx, r.yMax - thickness, lx, thickness), c);
            Fill(new Rect(r.xMax - thickness, r.yMax - ly, thickness, ly), c);
        }

        private static void Fill(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        /// <summary>Text with the soft drop shadow the game uses everywhere.</summary>
        private static void ShadowLabel(Rect r, string text, GUIStyle style, bool shadow = true)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (shadow && Event.current.type == EventType.Repaint)
            {
                var col = style.normal.textColor;
                style.normal.textColor = Shadow;
                GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), StripAllTags(text), style);
                style.normal.textColor = col;
            }
            GUI.Label(r, text, style);
        }

        // ------------------------------------------------------------ text helpers

        private static readonly Regex StyleTag = new Regex(@"<style=([^>]+)>(.*?)</style>", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex UnsupportedTags = new Regex(@"<(?!/?(b|i|color)\b)[^>]*>", RegexOptions.Compiled);
        private static readonly Regex AnyTag = new Regex(@"<[^>]*>", RegexOptions.Compiled);

        // Colours of the game's text styles (Language "style" tags).
        private static readonly Dictionary<string, string> StyleColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "cIsDamage", "#E5C962" }, { "cIsHealing", "#C9F24D" }, { "cIsUtility", "#95CDE5" },
            { "cIsHealth", "#E25F5F" }, { "cDeath", "#E25F5F" }, { "cStack", "#A6A6A6" }, { "cSub", "#A6A6A6" },
            { "cMono", "#A6A6A6" }, { "cShrine", "#DFA0FF" }, { "cArtifact", "#A07FDF" }, { "cIsVoid", "#ED7FCD" },
            { "cIsLunar", "#307FFF" }, { "cKeywordName", "#FFFFFF" }, { "cHumanObjective", "#E5C962" },
            { "cLunarObjective", "#307FFF" }, { "cWorldEvent", "#FFA34C" }, { "cUserSetting", "#A6A6A6" },
        };

        /// <summary>Converts the game's &lt;style=cIsDamage&gt; tags into colours IMGUI understands.</summary>
        private static string GameRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            for (int pass = 0; pass < 3 && s.Contains("<style="); pass++)
            {
                s = StyleTag.Replace(s, m =>
                    StyleColors.TryGetValue(m.Groups[1].Value.Trim(), out var hex)
                        ? $"<color={hex}>{m.Groups[2].Value}</color>"
                        : m.Groups[2].Value);
            }
            return UnsupportedTags.Replace(s, "");
        }

        private static string StripAllTags(string s) => string.IsNullOrEmpty(s) ? s : AnyTag.Replace(s, "");

        private void SetStatus(string message, bool error = false)
        {
            status = message;
            statusIsError = error;
            statusUntil = Time.unscaledTime + 4f;
        }

        private bool IsConfirming(string action, object target)
        {
            return pendingConfirmAction == action && ReferenceEquals(pendingConfirm, target) && Time.unscaledTime < pendingConfirmUntil;
        }

        private void StartConfirm(string action, object target)
        {
            pendingConfirmAction = action;
            pendingConfirm = target;
            pendingConfirmUntil = Time.unscaledTime + ConfirmSeconds;
        }

        private void ClearConfirm()
        {
            pendingConfirm = null;
            pendingConfirmAction = null;
        }

        // ------------------------------------------------------------ styles

        private GUIStyle centeredSmall, centeredSmallRed, goldSmallLeft, centeredSmallRedLeft, textFieldStyle;

        private void EnsureStyles()
        {
            if (stylesReady) return;
            stylesReady = true;

            gameFont = FindGameFont();

            titleStyle = Make(28, FontStyle.Normal, TextAnchor.MiddleLeft, White);
            headerStyle = Make(22, FontStyle.Normal, TextAnchor.MiddleLeft, White);
            nameStyle = Make(19, FontStyle.Normal, TextAnchor.MiddleLeft, White);
            textStyle = Make(16, FontStyle.Normal, TextAnchor.MiddleLeft, White);
            smallStyle = Make(14, FontStyle.Normal, TextAnchor.MiddleLeft, Muted);
            smallWrapStyle = Make(13, FontStyle.Normal, TextAnchor.UpperLeft, Dim, true);
            centerStyle = Make(16, FontStyle.Normal, TextAnchor.MiddleCenter, Muted, true);
            centeredSmall = Make(13, FontStyle.Normal, TextAnchor.MiddleCenter, Gold);
            centeredSmallRed = Make(12, FontStyle.Normal, TextAnchor.MiddleCenter, Red);
            goldSmallLeft = Make(15, FontStyle.Normal, TextAnchor.MiddleLeft, Gold);
            centeredSmallRedLeft = Make(15, FontStyle.Normal, TextAnchor.MiddleLeft, Red);
            countStyle = Make(17, FontStyle.Normal, TextAnchor.UpperRight, White);
            slotNumberStyle = Make(14, FontStyle.Normal, TextAnchor.UpperLeft, Muted);
            buttonStyle = Make(16, FontStyle.Normal, TextAnchor.MiddleCenter, White);
            buttonDarkStyle = Make(16, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(0.10f, 0.09f, 0.05f, 1f));
            tabStyle = Make(17, FontStyle.Normal, TextAnchor.MiddleCenter, Muted);
            tabActiveStyle = Make(17, FontStyle.Normal, TextAnchor.MiddleCenter, new Color(0.10f, 0.09f, 0.05f, 1f));
            tooltipTitleStyle = Make(26, FontStyle.Normal, TextAnchor.MiddleLeft, White, true);
            tooltipBodyStyle = Make(17, FontStyle.Normal, TextAnchor.UpperLeft, new Color(0.80f, 0.82f, 0.86f, 1f), true);
            tooltipHintStyle = Make(15, FontStyle.Italic, TextAnchor.MiddleLeft, Gold);
            iconFallbackStyle = Make(11, FontStyle.Normal, TextAnchor.MiddleCenter, Muted, true);

            textFieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(8, 8, 2, 2),
                margin = new RectOffset(0, 0, 0, 0)
            };
            if (gameFont) textFieldStyle.font = gameFont;
            var fieldBg = SolidTexture(new Color(0.06f, 0.07f, 0.09f, 0.95f));
            var fieldBgFocus = SolidTexture(new Color(0.10f, 0.11f, 0.14f, 1f));
            textFieldStyle.normal.background = fieldBg;
            textFieldStyle.hover.background = fieldBgFocus;
            textFieldStyle.focused.background = fieldBgFocus;
            textFieldStyle.active.background = fieldBgFocus;
            textFieldStyle.normal.textColor = White;
            textFieldStyle.hover.textColor = White;
            textFieldStyle.focused.textColor = White;
            textFieldStyle.active.textColor = White;
        }

        /// <summary>Uses the game's own font (Bombardier) if Unity has it loaded as a regular font; otherwise the default font.</summary>
        private static Font FindGameFont()
        {
            try
            {
                Font best = null;
                foreach (var font in Resources.FindObjectsOfTypeAll<Font>())
                {
                    if (!font) continue;
                    var n = font.name.ToLowerInvariant();
                    if (n.Contains("bombard")) return LogFont(font);
                    if (best == null && (n.Contains("ror") || n.Contains("risk"))) best = font;
                }
                if (best) return LogFont(best);
            }
            catch (Exception e) { Log.Warning("Font lookup failed: " + e.Message); }
            Log.Info("Game font not available as a Unity font - using the default font.");
            return null;
        }

        private static Texture2D SolidTexture(Color c)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            tex.SetPixel(0, 0, c);
            tex.Apply();
            return tex;
        }

        private static Font LogFont(Font f)
        {
            Log.Info("Using game font: " + f.name);
            return f;
        }

        private GUIStyle Make(int size, FontStyle fontStyle, TextAnchor anchor, Color color, bool wrap = false)
        {
            var s = new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = fontStyle,
                alignment = anchor,
                wordWrap = wrap,
                richText = true,
                clipping = TextClipping.Clip,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };
            if (gameFont) s.font = gameFont;
            s.normal.textColor = color;
            s.hover.textColor = color;
            return s;
        }
    }
}
