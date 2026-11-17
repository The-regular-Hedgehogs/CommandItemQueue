using RoR2;
using RoR2.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CommandItemQueue
{
    /// <summary>
    /// Knows whether the Tab scoreboard is open and can hide its content while the queue view is shown.
    /// While hidden, an invisible full-screen UI element sits on top of it: the game treats "mouse over UI"
    /// as UI focus and stops shooting/moving, exactly like when you hover the normal scoreboard.
    /// </summary>
    internal sealed class ScoreboardBridge
    {
        private HUD hud;
        private GameObject panel;
        private CanvasGroup canvasGroup;
        private bool addedCanvasGroup;
        private float originalAlpha = 1f;
        private bool hidden;
        private GameObject blocker;

        public bool IsOpen { get; private set; }

        /// <summary>Scoreboard stays open without holding Tab (uses the game's own "toggle scoreboard" mode). Tab closes it.</summary>
        public bool Pinned { get; private set; }
        private bool originalForceToggle;

        public void Update()
        {
            var current = FindHud();
            var currentPanel = current ? current.scoreboardPanel : null;
            if (currentPanel != panel)
            {
                SetPinned(false);
                SetHidden(false);
                if (blocker) UnityEngine.Object.Destroy(blocker);
                blocker = null;
                hud = current;
                panel = currentPanel;
                canvasGroup = null;
            }
            IsOpen = panel && panel.activeInHierarchy;
            if (Pinned && !IsOpen) SetPinned(false); // closed with Tab
        }

        public void SetPinned(bool pin)
        {
            if (!hud) { Pinned = false; return; }
            if (pin && !Pinned)
            {
                originalForceToggle = hud.shouldForceScoreboardToggle;
                hud.shouldForceScoreboardToggle = true;
                Pinned = true;
            }
            else if (!pin && Pinned)
            {
                hud.shouldForceScoreboardToggle = originalForceToggle;
                Pinned = false;
            }
        }

        private static HUD FindHud()
        {
            var list = HUD.readOnlyInstanceList;
            if (list == null || list.Count == 0) return null;
            var localUser = LocalUserManager.GetFirstLocalUser();
            foreach (var h in list)
                if (h && h.localUserViewer == localUser) return h;
            return list[0];
        }

        /// <summary>Makes the scoreboard invisible and click-through without disabling it (so the cursor stays available).</summary>
        public void SetHidden(bool hide)
        {
            if (!panel)
            {
                hidden = false;
                canvasGroup = null;
                return;
            }

            if (hide)
            {
                if (!canvasGroup)
                {
                    canvasGroup = panel.GetComponent<CanvasGroup>();
                    addedCanvasGroup = !canvasGroup;
                    if (addedCanvasGroup) canvasGroup = panel.AddComponent<CanvasGroup>();
                    if (!hidden) originalAlpha = canvasGroup.alpha;
                }
                // Enforced every frame in case the game animates the panel.
                canvasGroup.alpha = 0f;
                canvasGroup.blocksRaycasts = true; // needed so the blocker below receives the mouse
                canvasGroup.interactable = false;
                EnsureBlocker();
                if (!blocker.activeSelf) blocker.SetActive(true);
                blocker.transform.SetAsLastSibling();
                hidden = true;
            }
            else if (hidden)
            {
                if (canvasGroup)
                {
                    canvasGroup.alpha = addedCanvasGroup ? 1f : originalAlpha;
                    canvasGroup.blocksRaycasts = true;
                    canvasGroup.interactable = true;
                }
                if (blocker) blocker.SetActive(false);
                hidden = false;
            }
        }
    
        private void EnsureBlocker()
        {
            if (blocker) return;
            blocker = new GameObject("CommandItemQueue_InputBlocker", typeof(RectTransform));
            blocker.layer = panel.layer;
            var rt = (RectTransform)blocker.transform;
            rt.SetParent(panel.transform, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(-20000f, -20000f);
            rt.offsetMax = new Vector2(20000f, 20000f);
            var image = blocker.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0f);
            image.raycastTarget = true;
        }
    }
}
