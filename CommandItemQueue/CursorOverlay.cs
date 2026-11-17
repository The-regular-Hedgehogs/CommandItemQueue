using System.Reflection;
using RoR2;
using RoR2.UI;
using UnityEngine;
using UnityEngine.UI;

namespace CommandItemQueue
{
    /// <summary>
    /// Redraws the game's own mouse cursor on top of the Item Queue window.
    /// RoR2 draws its cursor as a UI image, which would otherwise be hidden behind the window.
    /// It copies the exact sprite, size and position, so it looks identical to the normal cursor.
    /// </summary>
    internal static class CursorOverlay
    {
        private static readonly FieldInfo IndicatorField =
            typeof(MPEventSystem).GetField("cursorIndicatorController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly Vector3[] corners = new Vector3[4];
        private static Image cachedImage;
        private static float nextSearchTime;
        private static Texture2D fallbackArrow;

        public static void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;

            var oldMatrix = GUI.matrix;
            var oldColor = GUI.color;
            GUI.matrix = Matrix4x4.identity;
            try
            {
                var image = FindCursorImage();
                if (image && image.sprite) DrawImage(image);
                else DrawFallback();
            }
            finally
            {
                GUI.matrix = oldMatrix;
                GUI.color = oldColor;
            }
        }

        private static Image FindCursorImage()
        {
            if (cachedImage && cachedImage.isActiveAndEnabled && cachedImage.sprite) return cachedImage;
            cachedImage = null;
            if (Time.unscaledTime < nextSearchTime) return null;
            nextSearchTime = Time.unscaledTime + 0.25f;

            var user = LocalUserManager.GetFirstLocalUser();
            var eventSystem = user != null ? user.eventSystem : null;
            if (!eventSystem || IndicatorField == null) return null;

            var indicator = IndicatorField.GetValue(eventSystem) as Component;
            if (!indicator) return null;

            foreach (var image in indicator.GetComponentsInChildren<Image>(false))
            {
                if (image.isActiveAndEnabled && image.sprite)
                {
                    cachedImage = image;
                    return image;
                }
            }
            return null;
        }

        private static void DrawImage(Image image)
        {
            image.rectTransform.GetWorldCorners(corners);
            var canvas = image.canvas;
            Camera cam = canvas && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
            Vector2 topRight = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
            var rect = new Rect(bottomLeft.x, Screen.height - topRight.y, topRight.x - bottomLeft.x, topRight.y - bottomLeft.y);
            if (rect.width < 1f || rect.height < 1f) { DrawFallback(); return; }

            var sprite = image.sprite;
            var tex = sprite.texture;
            var uv = new Rect(0f, 0f, 1f, 1f);
            try
            {
                var r = sprite.textureRect;
                uv = new Rect(r.x / tex.width, r.y / tex.height, r.width / tex.width, r.height / tex.height);
            }
            catch { /* tightly packed sprite: use the whole texture */ }

            GUI.color = image.color;
            GUI.DrawTextureWithTexCoords(rect, tex, uv, true);
        }

        /// <summary>Simple arrow, only used if the game's cursor image can't be found.</summary>
        private static void DrawFallback()
        {
            if (!fallbackArrow) fallbackArrow = CreateArrow();
            var mouse = Event.current.mousePosition;
            float size = Mathf.Round(32f * Screen.height / 1080f);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(mouse.x, mouse.y, size, size), fallbackArrow);
        }

        private static Texture2D CreateArrow()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var clear = new Color(0f, 0f, 0f, 0f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Arrow pointing to the top-left corner (texture y goes up, so flip).
                    int py = size - 1 - y;
                    bool inside = x <= py * 0.62f && py < 26 && !(py > 18 && x > 10);
                    bool edge = inside && (x == 0 || x >= py * 0.62f - 1.5f || py >= 25 || (py > 17 && x > 9));
                    tex.SetPixel(x, y, inside ? (edge ? Color.black : Color.white) : clear);
                }
            }
            tex.Apply();
            return tex;
        }
    }
}
