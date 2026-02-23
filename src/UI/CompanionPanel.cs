using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HaldorCompanions
{
    public class CompanionPanel
    {
        public GameObject Root { get; private set; }

        private static readonly Color GoldColor = new Color(0.83f, 0.64f, 0.31f, 1f);
        private static readonly Color GoldTextColor = new Color(0.83f, 0.52f, 0.18f, 1f);

        public void Build(Transform parent, float colTopInset, float bottomPad,
                          TMP_FontAsset font, GameObject buttonTemplate, float buttonHeight)
        {
            // Root container — full panel area
            Root = new GameObject("CompanionContent", typeof(RectTransform), typeof(Image));
            Root.transform.SetParent(parent, false);

            var rootRT = Root.GetComponent<RectTransform>();
            rootRT.anchorMin = Vector2.zero;
            rootRT.anchorMax = Vector2.one;
            rootRT.offsetMin = new Vector2(6f, bottomPad);
            rootRT.offsetMax = new Vector2(-6f, -colTopInset);

            var bgImage = Root.GetComponent<Image>();
            bgImage.color = new Color(0f, 0f, 0f, 0.75f);
            bgImage.raycastTarget = true;

            // Inner container with padding
            var inner = new GameObject("Inner", typeof(RectTransform));
            inner.transform.SetParent(Root.transform, false);
            var innerRT = inner.GetComponent<RectTransform>();
            innerRT.anchorMin = Vector2.zero;
            innerRT.anchorMax = Vector2.one;
            innerRT.offsetMin = new Vector2(40f, 40f);
            innerRT.offsetMax = new Vector2(-40f, -40f);

            // Title
            CreateText(inner.transform, "Title", "Companions",
                font, 26, GoldColor, TextAlignmentOptions.Top,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -10f), new Vector2(0f, -50f));

            // Decorative separator line
            var separator = new GameObject("Separator", typeof(RectTransform), typeof(Image));
            separator.transform.SetParent(inner.transform, false);
            var sepRT = separator.GetComponent<RectTransform>();
            sepRT.anchorMin = new Vector2(0.15f, 1f);
            sepRT.anchorMax = new Vector2(0.85f, 1f);
            sepRT.pivot = new Vector2(0.5f, 1f);
            sepRT.sizeDelta = new Vector2(0f, 2f);
            sepRT.anchoredPosition = new Vector2(0f, -60f);
            separator.GetComponent<Image>().color = new Color(GoldColor.r, GoldColor.g, GoldColor.b, 0.3f);

            // Coming Soon subtitle
            CreateText(inner.transform, "Subtitle", "Coming Soon",
                font, 20, new Color(0.7f, 0.7f, 0.7f, 1f), TextAlignmentOptions.Top,
                new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -80f), new Vector2(0f, -115f));

            // Description
            CreateText(inner.transform, "Description",
                "Hire warriors, tame beasts, and summon spirits to fight at your side.\n\nVisit Haldor to recruit companions for your journey through the Tenth World.",
                font, 15, new Color(0.6f, 0.6f, 0.6f, 1f), TextAlignmentOptions.Top,
                new Vector2(0.1f, 1f), new Vector2(0.9f, 1f),
                new Vector2(0f, -130f), new Vector2(0f, -250f));

            // Decorative icon area — shield emblem placeholder
            var iconArea = new GameObject("IconArea", typeof(RectTransform), typeof(Image));
            iconArea.transform.SetParent(inner.transform, false);
            var iconRT = iconArea.GetComponent<RectTransform>();
            iconRT.anchorMin = new Vector2(0.5f, 0.5f);
            iconRT.anchorMax = new Vector2(0.5f, 0.5f);
            iconRT.pivot = new Vector2(0.5f, 0.5f);
            iconRT.sizeDelta = new Vector2(80f, 80f);
            iconRT.anchoredPosition = new Vector2(0f, -20f);
            var iconImg = iconArea.GetComponent<Image>();
            iconImg.color = new Color(GoldColor.r, GoldColor.g, GoldColor.b, 0.15f);

            // Inner icon symbol (smaller square)
            var iconInner = new GameObject("IconInner", typeof(RectTransform), typeof(Image));
            iconInner.transform.SetParent(iconArea.transform, false);
            var iconInnerRT = iconInner.GetComponent<RectTransform>();
            iconInnerRT.anchorMin = new Vector2(0.2f, 0.2f);
            iconInnerRT.anchorMax = new Vector2(0.8f, 0.8f);
            iconInnerRT.offsetMin = Vector2.zero;
            iconInnerRT.offsetMax = Vector2.zero;
            iconInner.GetComponent<Image>().color = new Color(GoldColor.r, GoldColor.g, GoldColor.b, 0.25f);

            Root.SetActive(false);
        }

        public void Refresh()
        {
            // Placeholder — no dynamic content yet
        }

        public void UpdatePerFrame()
        {
            // Placeholder — no per-frame logic yet
        }

        private static void CreateText(Transform parent, string name, string text,
            TMP_FontAsset font, float fontSize, Color color, TextAlignmentOptions alignment,
            Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;

            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = alignment;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            tmp.raycastTarget = false;
            if (font != null) tmp.font = font;
        }
    }
}
