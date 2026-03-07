using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Companions
{
    /// <summary>
    /// Modal popup that appears after placing a farm zone.
    /// Shows a grid of crop buttons from the seed mapping.
    /// Player selects a crop (or "Any") and the zone is finalized.
    /// </summary>
    public class FarmZoneCropPicker : MonoBehaviour
    {
        private static FarmZoneCropPicker _instance;
        internal static bool IsOpen => _instance != null
            && _instance._root != null && _instance._root.activeSelf;

        // ── Callbacks ────────────────────────────────────────────────────────
        private Action<string> _onSelect;
        private Action _onCancel;

        // ── UI ───────────────────────────────────────────────────────────────
        private Canvas _canvas;
        private GameObject _root;
        private TMP_FontAsset _font;

        // ── Style ────────────────────────────────────────────────────────────
        private static readonly Color BgColor = new Color(0.08f, 0.07f, 0.06f, 0.94f);
        private static readonly Color ButtonColor = new Color(0.15f, 0.13f, 0.10f, 0.9f);
        private static readonly Color ButtonHover = new Color(0.25f, 0.22f, 0.16f, 0.9f);
        private static readonly Color LabelColor = new Color(1f, 0.9f, 0.5f, 1f);
        private static readonly Color TextColor = new Color(0.85f, 0.80f, 0.65f, 1f);
        private const float ButtonSize = 80f;
        private const float Spacing = 6f;
        private const int Columns = 5;

        // ══════════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════════

        internal static void Show(FarmZone zone, Action<string> onSelect, Action onCancel)
        {
            if (_instance == null)
            {
                var go = new GameObject("HC_FarmZoneCropPicker");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<FarmZoneCropPicker>();
            }
            _instance._onSelect = onSelect;
            _instance._onCancel = onCancel;
            _instance.Build();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            if (_root == null || !_root.activeSelf) return;

            // Keep cursor free every frame — game systems try to re-lock it
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
            {
                Cancel();
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Build UI
        // ══════════════════════════════════════════════════════════════════════

        private void Build()
        {
            // Destroy previous UI if it exists
            if (_root != null) Destroy(_root);
            if (_canvas != null) Destroy(_canvas.gameObject);

            _font = ResolveFont();

            // Canvas
            var canvasGO = new GameObject("CropPickerCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 31;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // Ensure seed mapping is built
            FarmController.BuildSeedMappingPublic();

            // Collect crops — filter out food items planted for seed multiplication
            // (e.g. Carrot, Turnip) while keeping actual seeds (CarrotSeeds, TurnipSeeds)
            // and self-seeding crops (Barley, JotunPuffs).
            var crops = new List<CropEntry>();
            if (FarmController.s_seedToPlant != null)
            {
                foreach (var kvp in FarmController.s_seedToPlant)
                {
                    if (IsSeedMultiplicationEntry(kvp.Key, kvp.Value)) continue;

                    var entry = new CropEntry
                    {
                        SeedPrefabName = kvp.Key,
                        DisplayName = GetCropDisplayName(kvp.Value),
                        Icon = GetCropIcon(kvp.Key)
                    };
                    crops.Add(entry);
                }
            }
            crops.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName,
                StringComparison.OrdinalIgnoreCase));

            // Calculate layout
            int totalItems = crops.Count + 1; // +1 for "Any Crop"
            int rows = Mathf.CeilToInt(totalItems / (float)Columns);
            float contentW = Columns * (ButtonSize + Spacing) + Spacing;
            float contentH = rows * (ButtonSize + Spacing) + Spacing;
            float panelW = contentW + 20f;
            float panelH = contentH + 60f; // title + padding

            // Root panel
            _root = new GameObject("CropPickerRoot", typeof(RectTransform), typeof(Image));
            _root.transform.SetParent(canvasGO.transform, false);
            var rootRT = _root.GetComponent<RectTransform>();
            rootRT.anchorMin = new Vector2(0.5f, 0.5f);
            rootRT.anchorMax = new Vector2(0.5f, 0.5f);
            rootRT.pivot = new Vector2(0.5f, 0.5f);
            rootRT.sizeDelta = new Vector2(panelW, panelH);
            _root.GetComponent<Image>().color = BgColor;

            // Title
            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(_root.transform, false);
            var titleTMP = titleGO.AddComponent<TextMeshProUGUI>();
            if (_font != null) titleTMP.font = _font;
            titleTMP.text = ModLocalization.Loc("hc_farmzone_title");
            titleTMP.fontSize = 18f;
            titleTMP.color = LabelColor;
            titleTMP.fontStyle = FontStyles.Bold;
            titleTMP.alignment = TextAlignmentOptions.Center;
            titleTMP.raycastTarget = false;
            var titleRT = titleGO.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0f, 1f);
            titleRT.anchorMax = new Vector2(1f, 1f);
            titleRT.pivot = new Vector2(0.5f, 1f);
            titleRT.sizeDelta = new Vector2(0f, 36f);
            titleRT.anchoredPosition = new Vector2(0f, -4f);

            // Close button
            BuildCloseButton(_root.transform);

            // Content area
            var contentGO = new GameObject("Content", typeof(RectTransform));
            contentGO.transform.SetParent(_root.transform, false);
            var contentRT = contentGO.GetComponent<RectTransform>();
            contentRT.anchorMin = new Vector2(0.5f, 1f);
            contentRT.anchorMax = new Vector2(0.5f, 1f);
            contentRT.pivot = new Vector2(0.5f, 1f);
            contentRT.sizeDelta = new Vector2(contentW, contentH);
            contentRT.anchoredPosition = new Vector2(0f, -44f);

            // "Any Crop" button first
            BuildCropButton(contentGO.transform, 0, "",
                ModLocalization.Loc("hc_farmzone_any"), null);

            // Crop buttons
            for (int i = 0; i < crops.Count; i++)
            {
                var crop = crops[i];
                BuildCropButton(contentGO.transform, i + 1,
                    crop.SeedPrefabName, crop.DisplayName, crop.Icon);
            }

            // Ensure cursor is free
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void BuildCropButton(Transform parent, int index, string seedName,
            string displayName, Sprite icon)
        {
            int col = index % Columns;
            int row = index / Columns;
            float x = Spacing + col * (ButtonSize + Spacing);
            float y = -(Spacing + row * (ButtonSize + Spacing));

            var btnGO = new GameObject($"Crop_{seedName}", typeof(RectTransform),
                typeof(Image), typeof(Button));
            btnGO.transform.SetParent(parent, false);
            var btnRT = btnGO.GetComponent<RectTransform>();
            btnRT.anchorMin = new Vector2(0f, 1f);
            btnRT.anchorMax = new Vector2(0f, 1f);
            btnRT.pivot = new Vector2(0f, 1f);
            btnRT.sizeDelta = new Vector2(ButtonSize, ButtonSize);
            btnRT.anchoredPosition = new Vector2(x, y);

            var btnImg = btnGO.GetComponent<Image>();
            btnImg.color = ButtonColor;

            var btn = btnGO.GetComponent<Button>();
            var colors = btn.colors;
            colors.normalColor = ButtonColor;
            colors.highlightedColor = ButtonHover;
            colors.pressedColor = ButtonHover;
            colors.selectedColor = ButtonColor;
            btn.colors = colors;

            // Capture for closure
            string capturedSeed = seedName;
            btn.onClick.AddListener(() => SelectCrop(capturedSeed));

            // Icon
            if (icon != null)
            {
                var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconGO.transform.SetParent(btnGO.transform, false);
                var iconRT = iconGO.GetComponent<RectTransform>();
                iconRT.anchorMin = new Vector2(0.5f, 0.5f);
                iconRT.anchorMax = new Vector2(0.5f, 0.5f);
                iconRT.pivot = new Vector2(0.5f, 0.5f);
                iconRT.sizeDelta = new Vector2(48f, 48f);
                iconRT.anchoredPosition = new Vector2(0f, 6f);
                var iconImg = iconGO.GetComponent<Image>();
                iconImg.sprite = icon;
                iconImg.raycastTarget = false;
            }

            // Label
            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(btnGO.transform, false);
            var labelTMP = labelGO.AddComponent<TextMeshProUGUI>();
            if (_font != null) labelTMP.font = _font;
            labelTMP.text = displayName;
            labelTMP.fontSize = 10f;
            labelTMP.color = TextColor;
            labelTMP.alignment = TextAlignmentOptions.Bottom;
            labelTMP.raycastTarget = false;
            labelTMP.textWrappingMode = TextWrappingModes.Normal;
            labelTMP.overflowMode = TextOverflowModes.Truncate;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = new Vector2(0f, 0f);
            labelRT.anchorMax = new Vector2(1f, 0.35f);
            labelRT.offsetMin = new Vector2(2f, 2f);
            labelRT.offsetMax = new Vector2(-2f, 0f);
        }

        private void BuildCloseButton(Transform parent)
        {
            var go = new GameObject("CloseBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(28f, 28f);
            rt.anchoredPosition = new Vector2(-4f, -4f);
            go.GetComponent<Image>().color = new Color(0.3f, 0.15f, 0.1f, 0.7f);
            go.GetComponent<Button>().onClick.AddListener(Cancel);

            var txtGO = new GameObject("X", typeof(RectTransform));
            txtGO.transform.SetParent(go.transform, false);
            var tmp = txtGO.AddComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.text = "X";
            tmp.fontSize = 14f;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            var txtRT = txtGO.GetComponent<RectTransform>();
            txtRT.anchorMin = Vector2.zero;
            txtRT.anchorMax = Vector2.one;
            txtRT.offsetMin = Vector2.zero;
            txtRT.offsetMax = Vector2.zero;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Selection
        // ══════════════════════════════════════════════════════════════════════

        private void SelectCrop(string seedPrefabName)
        {
            var cb = _onSelect;
            CleanupUI();
            cb?.Invoke(seedPrefabName);
        }

        private void Cancel()
        {
            var cb = _onCancel;
            CleanupUI();
            cb?.Invoke();
        }

        private void CleanupUI()
        {
            _onSelect = null;
            _onCancel = null;
            if (_root != null) { Destroy(_root); _root = null; }
            if (_canvas != null) { Destroy(_canvas.gameObject); _canvas = null; }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private struct CropEntry
        {
            public string SeedPrefabName;
            public string DisplayName;
            public Sprite Icon;
        }

        /// <summary>
        /// Returns true if this seed entry is a food crop planted only for seed
        /// multiplication (e.g. planting Carrot to get CarrotSeeds). We filter
        /// these out so the picker only shows actual seeds and self-seeding crops.
        /// </summary>
        private static bool IsSeedMultiplicationEntry(string seedName,
            FarmController.SeedPlantInfo info)
        {
            // If not a known crop output, it's a pure seed — keep it
            if (FarmController.s_cropOutputNames == null ||
                !FarmController.s_cropOutputNames.Contains(seedName))
                return false;

            // Self-seeding crops (Barley, JotunPuffs, Magecap) output themselves — keep them
            var plant = info.PlantPrefab?.GetComponent<Plant>();
            if (plant?.m_grownPrefabs != null)
            {
                foreach (var grown in plant.m_grownPrefabs)
                {
                    if (grown == null) continue;
                    var pickable = grown.GetComponent<Pickable>();
                    if (pickable?.m_itemPrefab != null &&
                        pickable.m_itemPrefab.name == seedName)
                        return false; // self-seeding — keep it
                }
            }

            // Check if the item has food stats — food crops like Carrot, Turnip
            // are planted only for seed multiplication
            if (ZNetScene.instance != null)
            {
                var prefab = ZNetScene.instance.GetPrefab(seedName);
                if (prefab != null)
                {
                    var itemDrop = prefab.GetComponent<ItemDrop>();
                    if (itemDrop?.m_itemData?.m_shared != null)
                    {
                        var s = itemDrop.m_itemData.m_shared;
                        if (s.m_food > 0f || s.m_foodStamina > 0f)
                            return true; // food crop for seed multiplication
                    }
                }
            }

            return false;
        }

        private static string GetCropDisplayName(FarmController.SeedPlantInfo info)
        {
            if (info.PlantPrefab == null) return info.SeedPrefabName;

            // Try to get localized name from the plant's Piece component
            var piece = info.PlantPrefab.GetComponent<Piece>();
            if (piece != null && !string.IsNullOrEmpty(piece.m_name))
                return Localization.instance.Localize(piece.m_name);

            return info.SeedPrefabName;
        }

        private static Sprite GetCropIcon(string seedPrefabName)
        {
            if (ZNetScene.instance == null) return null;
            var prefab = ZNetScene.instance.GetPrefab(seedPrefabName);
            if (prefab == null) return null;
            var itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop?.m_itemData == null) return null;
            return itemDrop.m_itemData.GetIcon();
        }

        private static TMP_FontAsset ResolveFont()
        {
            if (InventoryGui.instance != null)
            {
                var texts = InventoryGui.instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] != null && texts[i].font != null &&
                        texts[i].font.name.IndexOf("LiberationSans",
                            StringComparison.OrdinalIgnoreCase) < 0)
                        return texts[i].font;
                }
            }
            return TMP_Settings.defaultFontAsset;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Harmony — block player input while crop picker is open.
        //  Hud.InRadial integrates with GameCamera (cursor unlock),
        //  PlayerController (blocks camera look + movement), and
        //  Player (blocks Use/attack).
        // ══════════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Hud), nameof(Hud.InRadial))]
        private static class HudInRadial_Patch
        {
            static void Postfix(ref bool __result)
            {
                if (!__result && IsOpen)
                    __result = true;
            }
        }
    }
}
