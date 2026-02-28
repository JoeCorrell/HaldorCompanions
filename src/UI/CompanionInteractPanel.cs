using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Companions
{
    /// <summary>
    /// Companion inventory overlay — appears alongside InventoryGui when
    /// a companion's Container is opened (tap E).  Single panel showing
    /// name input, 5×6 inventory grid, and 3 food slots.
    /// Action controls have moved to CompanionRadialMenu (hold E).
    /// </summary>
    public class CompanionInteractPanel : MonoBehaviour
    {
        public static CompanionInteractPanel Instance { get; private set; }

        // ── Public state ─────────────────────────────────────────────────────
        public bool IsVisible => _visible;
        public bool IsNameInputFocused => _nameInput != null && _nameInput.isFocused;
        public CompanionSetup CurrentCompanion => _companion;

        public static bool IsOpenFor(CompanionSetup setup)
            => Instance != null && Instance._visible
            && Instance._companion != null && Instance._companion == setup;

        // ── Layout constants ─────────────────────────────────────────────────
        private const float PanelH        = 400f;
        private const float DvergerPanelH = 290f;
        private const float UiScale       = 1.25f;
        private const float OuterPad      = 4f;

        // ── Grid constants ───────────────────────────────────────────────────
        private const int GridCols          = 5;
        private const int GridSlots         = 30;
        private const int FoodSlotCount     = 3;
        private const int MainGridSlots     = 30;
        private const float InventorySlotGap = 3f;

        // ── Style constants ──────────────────────────────────────────────────
        private static readonly Color PanelBg          = new Color(0.18f, 0.14f, 0.09f, 0.92f);
        private static readonly Color ColBg            = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color GoldColor        = new Color(0.83f, 0.64f, 0.31f, 1f);
        private static readonly Color LabelText        = new Color(1f, 0.9f, 0.5f, 1f);
        private static readonly Color EquipBlue        = new Color(0.29f, 0.55f, 0.94f, 1f);
        private static readonly Color BarBg            = new Color(0.15f, 0.15f, 0.15f, 0.85f);
        private static readonly Color SlotTint         = new Color(0f, 0f, 0f, 0.5625f);
        private static readonly Color SlotHoverTint   = new Color(0.25f, 0.22f, 0.15f, 0.85f);
        private static readonly Color EquippedSlotTint = new Color(0.10f, 0.20f, 0.38f, 0.80f);
        private static readonly Color EquippedHoverTint = new Color(0.16f, 0.30f, 0.52f, 0.90f);

        // ── Custom sprite caches ─────────────────────────────────────────────
        private static Sprite _panelBgSprite;
        private static Sprite _sliderBgSprite;

        // ── InventoryGui drag system reflection ──────────────────────────────
        private static readonly FieldInfo _dragItemField      = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo _dragInventoryField  = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
        private static readonly FieldInfo _dragAmountField     = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
        private static readonly MethodInfo _setupDragItem      = AccessTools.Method(typeof(InventoryGui), "SetupDragItem",
            new[] { typeof(ItemDrop.ItemData), typeof(Inventory), typeof(int) });

        // ── Companion references ─────────────────────────────────────────────
        private CompanionSetup   _companion;
        private Character        _companionChar;
        private CompanionFood    _companionFood;
        private Humanoid         _companionHumanoid;
        private Container        _companionContainer;
        private ZNetView         _companionNview;

        // ── UI elements ──────────────────────────────────────────────────────
        private GameObject      _root;
        private TMP_InputField  _nameInput;

        private bool _built;
        private bool _visible;
        private bool _builtForDverger;

        // ── Inventory grid ───────────────────────────────────────────────────
        private Image[]           _slotBgs;
        private Image[]           _slotIcons;
        private TextMeshProUGUI[] _slotCounts;
        private Image[]           _slotBorders;
        private GameObject[]      _slotDurabilityBars;
        private Image[]           _slotDurabilityFills;
        private Image[]           _foodSlotIcons;
        private TextMeshProUGUI[] _foodSlotCounts;
        private float             _invRefreshTimer;
        private int               _hoveredSlot = -1;

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()  { Instance = this; }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            _panelBgSprite = null;
            _sliderBgSprite = null;
            Teardown();
        }

        private void Update()
        {
            if (!_visible || _companion == null) return;

            if (InventoryGui.instance == null || !InventoryGui.instance.IsContainerOpen())
            { Hide(); return; }

            // Unity null check — companion may have been destroyed
            if (_companion == null || !_companion)
            { Hide(); return; }

            if (Player.m_localPlayer != null)
            {
                float dist = Vector3.Distance(
                    Player.m_localPlayer.transform.position,
                    _companion.transform.position);
                if (dist > 5f) { Hide(); return; }
            }

            RefreshInventoryGrid();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════════

        public void Show(CompanionSetup companion)
        {
            _companion          = companion;
            _companionChar      = companion.GetComponent<Character>();
            _companionFood      = companion.GetComponent<CompanionFood>();
            _companionHumanoid  = companion.GetComponent<Humanoid>();
            _companionContainer = companion.GetComponent<Container>();
            _companionNview     = companion.GetComponent<ZNetView>();

            CompanionsPlugin.Log.LogDebug(
                $"[UI] Show — companion=\"{companion.name}\" " +
                $"char={_companionChar != null} humanoid={_companionHumanoid != null} " +
                $"food={_companionFood != null} nview={_companionNview != null}");

            EnsureCompanionOwnership();

            bool isDverger = !companion.CanWearArmor();

            // Rebuild if destroyed (scene change), never built, or companion type changed
            if (_root == null) _built = false;
            if (_built && isDverger != _builtForDverger)
            {
                Destroy(_root);
                _root = null;
                _built = false;
            }
            _builtForDverger = isDverger;
            if (!_built) BuildUI();

            // Load name
            string savedName = "";
            if (_companionNview != null && _companionNview.GetZDO() != null)
                savedName = _companionNview.GetZDO().GetString(CompanionSetup.NameHash, "");
            if (_nameInput != null)
            {
                _nameInput.onValueChanged.RemoveAllListeners();
                _nameInput.text = savedName;
                _nameInput.onValueChanged.AddListener(OnNameChanged);
            }

            _invRefreshTimer = 0f;

            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            _visible = true;
        }

        public void Hide()
        {
            if (!_visible) return;
            _visible = false;
            if (_nameInput != null)
                _nameInput.onValueChanged.RemoveAllListeners();
            if (_root != null) _root.SetActive(false);

            // Re-show vanilla crafting panel if InventoryGui is still open
            if (InventoryGui.instance != null && InventoryGui.instance.m_crafting != null)
                InventoryGui.instance.m_crafting.gameObject.SetActive(true);

            _companion          = null;
            _companionChar      = null;
            _companionFood      = null;
            _companionHumanoid  = null;
            _companionContainer = null;
            _companionNview     = null;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Sprite loaders
        // ══════════════════════════════════════════════════════════════════════

        private static Sprite GetPanelBgSprite()
        {
            if (_panelBgSprite != null) return _panelBgSprite;
            var tex = TextureLoader.LoadUITexture("PanelBackground");
            if (tex == null) return null;
            _panelBgSprite = Sprite.Create(tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            _panelBgSprite.name = "CIP_PanelBackground";
            return _panelBgSprite;
        }

        private static Sprite GetSliderBgSprite()
        {
            if (_sliderBgSprite != null) return _sliderBgSprite;
            var tex = TextureLoader.LoadUITexture("SliderBackground");
            if (tex == null) return null;
            _sliderBgSprite = Sprite.Create(tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            _sliderBgSprite.name = "CIP_SliderBackground";
            return _sliderBgSprite;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Build UI — single panel: name + grid + food
        // ══════════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            TMP_FontAsset font = GetFont();

            // Compute slot size from height, then derive panel width from grid
            float slotSize = ComputeInventorySlotSize();
            float gridW = GridCols * slotSize + (GridCols - 1) * InventorySlotGap;
            float panelW = gridW + OuterPad * 2f;
            float panelH = _builtForDverger ? DvergerPanelH : PanelH;

            // Root panel — parented under InventoryGui's canvas for shared raycasting
            _root = new GameObject("HC_InteractRoot", typeof(RectTransform));
            Transform canvasParent = transform;
            if (InventoryGui.instance != null)
            {
                var canvas = InventoryGui.instance.GetComponentInParent<Canvas>();
                if (canvas != null) canvasParent = canvas.transform;
            }
            _root.transform.SetParent(canvasParent, false);
            _root.transform.SetAsLastSibling();

            var rootRT = _root.GetComponent<RectTransform>();
            rootRT.anchorMin        = new Vector2(0.5f, 0.5f);
            rootRT.anchorMax        = new Vector2(0.5f, 0.5f);
            rootRT.pivot            = new Vector2(0.5f, 0.5f);
            rootRT.sizeDelta        = new Vector2(panelW, panelH);
            rootRT.anchoredPosition = Vector2.zero;
            rootRT.localScale       = Vector3.one * UiScale;

            var rootImg = _root.AddComponent<Image>();
            ApplyPanelBg(rootImg, PanelBg);
            rootImg.raycastTarget = true;

            // Content area with minimal padding
            var content = new GameObject("Content", typeof(RectTransform), typeof(Image));
            content.transform.SetParent(_root.transform, false);
            var contentRT = content.GetComponent<RectTransform>();
            contentRT.anchorMin = Vector2.zero;
            contentRT.anchorMax = Vector2.one;
            contentRT.offsetMin = new Vector2(OuterPad, OuterPad);
            contentRT.offsetMax = new Vector2(-OuterPad, -OuterPad);
            var contentImg = content.GetComponent<Image>();
            contentImg.sprite = null;
            contentImg.color = ColBg;
            contentImg.raycastTarget = false;

            float topY = 0f;

            // Name input — centered, same width as inventory grid
            topY = BuildNameInput(contentRT, font, topY, gridW);
            topY -= 4f;

            // Inventory grid + food
            BuildInventoryGrid(contentRT, font, ref topY, slotSize);
            if (!_builtForDverger)
                BuildFoodSlots(contentRT, font, slotSize);

            ApplyFallbackFont(_root.transform, font);
            _root.SetActive(false);
            _built = true;
        }

        private static void ApplyPanelBg(Image img, Color fallback)
        {
            var sprite = GetPanelBgSprite();
            if (sprite != null)
            {
                img.sprite = sprite;
                img.type   = Image.Type.Simple;
                img.preserveAspect = false;
                img.color  = Color.white;
            }
            else
            {
                img.color = fallback;
            }
        }

        private float ComputeInventorySlotSize()
        {
            int gridRows = Mathf.CeilToInt(MainGridSlots / (float)GridCols);
            float curH = _builtForDverger ? DvergerPanelH : PanelH;

            // Available height: panel minus top/bottom pad, name input, gap
            float totalH = curH - OuterPad * 2f - 26f - 4f;

            if (_builtForDverger)
            {
                float slotH = (totalH - (gridRows - 1) * InventorySlotGap) / gridRows;
                return Mathf.Floor(slotH);
            }
            else
            {
                const float foodOverhead = 22f;
                float slotH = (totalH - foodOverhead - (gridRows - 1) * InventorySlotGap) / (gridRows + 1);
                return Mathf.Floor(slotH);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Name input
        // ══════════════════════════════════════════════════════════════════════

        private float BuildNameInput(RectTransform parent, TMP_FontAsset font, float y, float width)
        {
            float h = 26f;

            var inputGO = new GameObject("NameInput", typeof(RectTransform), typeof(Image));
            inputGO.transform.SetParent(parent, false);
            var inputRT = inputGO.GetComponent<RectTransform>();
            inputRT.anchorMin = new Vector2(0.5f, 1f);
            inputRT.anchorMax = new Vector2(0.5f, 1f);
            inputRT.pivot = new Vector2(0.5f, 1f);
            inputRT.sizeDelta = new Vector2(width, h);
            inputRT.anchoredPosition = new Vector2(0f, y);

            var bgSprite = GetSliderBgSprite();
            var bgImg = inputGO.GetComponent<Image>();
            if (bgSprite != null)
            {
                bgImg.sprite = bgSprite;
                bgImg.type = Image.Type.Simple;
                bgImg.color = Color.white;
            }
            else
            {
                bgImg.color = BarBg;
            }

            var textAreaGO = new GameObject("TextArea", typeof(RectTransform));
            textAreaGO.transform.SetParent(inputGO.transform, false);
            var textAreaRT = textAreaGO.GetComponent<RectTransform>();
            textAreaRT.anchorMin = Vector2.zero;
            textAreaRT.anchorMax = Vector2.one;
            textAreaRT.offsetMin = new Vector2(8f, 2f);
            textAreaRT.offsetMax = new Vector2(-8f, -2f);
            textAreaGO.AddComponent<RectMask2D>();

            var childTextGO = new GameObject("Text", typeof(RectTransform));
            childTextGO.transform.SetParent(textAreaGO.transform, false);
            var childTmp = childTextGO.AddComponent<TextMeshProUGUI>();
            if (font != null) childTmp.font = font;
            childTmp.fontSize = 16f;
            childTmp.color = Color.white;
            childTmp.alignment = TextAlignmentOptions.MidlineLeft;
            childTmp.raycastTarget = false;
            StretchFill(childTextGO.GetComponent<RectTransform>());

            _nameInput = inputGO.AddComponent<TMP_InputField>();
            _nameInput.textComponent = childTmp;
            _nameInput.textViewport = textAreaRT;
            _nameInput.characterLimit = 24;
            _nameInput.contentType = TMP_InputField.ContentType.Standard;
            _nameInput.onFocusSelectAll = false;

            var phGO = new GameObject("Placeholder", typeof(RectTransform));
            phGO.transform.SetParent(textAreaGO.transform, false);
            var phTmp = phGO.AddComponent<TextMeshProUGUI>();
            if (font != null) phTmp.font = font;
            phTmp.fontSize = 16f;
            phTmp.color = new Color(0.6f, 0.6f, 0.6f, 0.6f);
            phTmp.alignment = TextAlignmentOptions.MidlineLeft;
            phTmp.text = "Enter name...";
            phTmp.fontStyle = FontStyles.Italic;
            phTmp.raycastTarget = false;
            StretchFill(phGO.GetComponent<RectTransform>());
            _nameInput.placeholder = phTmp;

            return y - h;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Inventory grid
        // ══════════════════════════════════════════════════════════════════════

        private void BuildInventoryGrid(RectTransform parent, TMP_FontAsset font, ref float y, float slotSize)
        {
            _slotBgs = new Image[MainGridSlots];
            _slotIcons = new Image[MainGridSlots];
            _slotCounts = new TextMeshProUGUI[MainGridSlots];
            _slotBorders = new Image[MainGridSlots];
            _slotDurabilityBars = new GameObject[MainGridSlots];
            _slotDurabilityFills = new Image[MainGridSlots];

            int gridRows = Mathf.CeilToInt(MainGridSlots / (float)GridCols);
            float gridW = GridCols * slotSize + (GridCols - 1) * InventorySlotGap;
            float gridH = gridRows * slotSize + (gridRows - 1) * InventorySlotGap;

            var gridContainer = new GameObject("Grid", typeof(RectTransform));
            gridContainer.transform.SetParent(parent, false);
            var gridRT = gridContainer.GetComponent<RectTransform>();
            gridRT.anchorMin = new Vector2(0.5f, 1f);
            gridRT.anchorMax = new Vector2(0.5f, 1f);
            gridRT.pivot = new Vector2(0.5f, 1f);
            gridRT.sizeDelta = new Vector2(gridW, gridH);
            gridRT.anchoredPosition = new Vector2(0f, y);

            for (int i = 0; i < MainGridSlots; i++)
            {
                int visualCol = i % GridCols;
                int visualRow = i / GridCols;
                float x = visualCol * (slotSize + InventorySlotGap);
                float sy = -(visualRow * (slotSize + InventorySlotGap));

                var slotGO = new GameObject($"Slot{i}", typeof(RectTransform), typeof(Image));
                slotGO.transform.SetParent(gridContainer.transform, false);
                var slotRT = slotGO.GetComponent<RectTransform>();
                slotRT.anchorMin = new Vector2(0f, 1f);
                slotRT.anchorMax = new Vector2(0f, 1f);
                slotRT.pivot = new Vector2(0f, 1f);
                slotRT.sizeDelta = new Vector2(slotSize, slotSize);
                slotRT.anchoredPosition = new Vector2(x, sy);

                var bgImg = slotGO.GetComponent<Image>();
                bgImg.sprite = null;
                bgImg.color = SlotTint;
                _slotBgs[i] = bgImg;

                var btn = slotGO.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.navigation = new Navigation { mode = Navigation.Mode.Automatic };
                int slotIndex = i;
                btn.onClick.AddListener(() => OnSlotLeftClick(slotIndex));

                var trigger = slotGO.AddComponent<EventTrigger>();
                var rightEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                rightEntry.callback.AddListener((data) =>
                {
                    var pData = (PointerEventData)data;
                    if (pData.button == PointerEventData.InputButton.Right)
                        OnSlotRightClick(slotIndex);
                });
                trigger.triggers.Add(rightEntry);

                var hoverEnter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
                hoverEnter.callback.AddListener((_) => _hoveredSlot = slotIndex);
                trigger.triggers.Add(hoverEnter);
                var hoverExit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
                hoverExit.callback.AddListener((_) => { if (_hoveredSlot == slotIndex) _hoveredSlot = -1; });
                trigger.triggers.Add(hoverExit);

                var selectEntry = new EventTrigger.Entry { eventID = EventTriggerType.Select };
                selectEntry.callback.AddListener((_) => _hoveredSlot = slotIndex);
                trigger.triggers.Add(selectEntry);
                var deselectEntry = new EventTrigger.Entry { eventID = EventTriggerType.Deselect };
                deselectEntry.callback.AddListener((_) => { if (_hoveredSlot == slotIndex) _hoveredSlot = -1; });
                trigger.triggers.Add(deselectEntry);

                var borderGO = new GameObject("Border", typeof(RectTransform), typeof(Image));
                borderGO.transform.SetParent(slotGO.transform, false);
                var borderRT = borderGO.GetComponent<RectTransform>();
                borderRT.anchorMin = Vector2.zero;
                borderRT.anchorMax = Vector2.one;
                borderRT.offsetMin = new Vector2(1f, 1f);
                borderRT.offsetMax = new Vector2(-1f, -1f);
                _slotBorders[i] = borderGO.GetComponent<Image>();
                _slotBorders[i].color = EquipBlue;
                _slotBorders[i].raycastTarget = false;
                _slotBorders[i].enabled = false;

                var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconGO.transform.SetParent(slotGO.transform, false);
                var iconRT = iconGO.GetComponent<RectTransform>();
                iconRT.anchorMin = Vector2.zero;
                iconRT.anchorMax = Vector2.one;
                iconRT.offsetMin = new Vector2(3f, 3f);
                iconRT.offsetMax = new Vector2(-3f, -3f);
                _slotIcons[i] = iconGO.GetComponent<Image>();
                _slotIcons[i].preserveAspect = true;
                _slotIcons[i].raycastTarget = false;
                _slotIcons[i].enabled = false;

                var countGO = new GameObject("Count", typeof(RectTransform));
                countGO.transform.SetParent(slotGO.transform, false);
                var countTmp = countGO.AddComponent<TextMeshProUGUI>();
                if (font != null) countTmp.font = font;
                countTmp.fontSize = 9f;
                countTmp.color = Color.white;
                countTmp.alignment = TextAlignmentOptions.BottomRight;
                countTmp.textWrappingMode = TextWrappingModes.NoWrap;
                countTmp.overflowMode = TextOverflowModes.Overflow;
                countTmp.raycastTarget = false;
                var countRT = countGO.GetComponent<RectTransform>();
                countRT.anchorMin = Vector2.zero;
                countRT.anchorMax = Vector2.one;
                countRT.offsetMin = new Vector2(2f, 1f);
                countRT.offsetMax = new Vector2(-2f, -1f);
                _slotCounts[i] = countTmp;
                _slotCounts[i].enabled = false;

                // Durability bar
                var durBarGO = new GameObject("DurabilityBg", typeof(RectTransform), typeof(Image));
                durBarGO.transform.SetParent(slotGO.transform, false);
                var durBgRT = durBarGO.GetComponent<RectTransform>();
                durBgRT.anchorMin = new Vector2(0f, 0f);
                durBgRT.anchorMax = new Vector2(1f, 0f);
                durBgRT.pivot = new Vector2(0.5f, 0f);
                durBgRT.sizeDelta = new Vector2(0f, 3f);
                durBgRT.anchoredPosition = new Vector2(0f, 1f);
                durBarGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);
                durBarGO.GetComponent<Image>().raycastTarget = false;
                durBarGO.SetActive(false);

                var durFillGO = new GameObject("DurabilityFill", typeof(RectTransform), typeof(Image));
                durFillGO.transform.SetParent(durBarGO.transform, false);
                var durFillRT = durFillGO.GetComponent<RectTransform>();
                durFillRT.anchorMin = Vector2.zero;
                durFillRT.anchorMax = Vector2.one;
                durFillRT.offsetMin = Vector2.zero;
                durFillRT.offsetMax = Vector2.zero;
                durFillGO.GetComponent<Image>().raycastTarget = false;

                _slotDurabilityBars[i] = durBarGO;
                _slotDurabilityFills[i] = durFillGO.GetComponent<Image>();
            }

            y -= gridH;
        }

        private void BuildFoodSlots(RectTransform parent, TMP_FontAsset font, float slotSize)
        {
            float bottomY = 4f;
            _foodSlotIcons = new Image[FoodSlotCount];
            _foodSlotCounts = new TextMeshProUGUI[FoodSlotCount];
            float foodSlotSz = slotSize;
            float foodGap = 3f;
            float foodRowW = FoodSlotCount * foodSlotSz + (FoodSlotCount - 1) * foodGap;

            var foodLabel = MakeText(parent.transform, "FoodLabel", "Food",
                font, 11f, LabelText, TextAlignmentOptions.Center);
            var foodLabelRT = foodLabel.GetComponent<RectTransform>();
            foodLabelRT.anchorMin = new Vector2(0f, 0f);
            foodLabelRT.anchorMax = new Vector2(1f, 0f);
            foodLabelRT.pivot = new Vector2(0.5f, 0f);
            foodLabelRT.sizeDelta = new Vector2(0f, 14f);
            foodLabelRT.anchoredPosition = new Vector2(0f, bottomY + foodSlotSz + 1f);
            foodLabel.fontStyle = FontStyles.Bold;

            var foodRow = new GameObject("FoodSlots", typeof(RectTransform));
            foodRow.transform.SetParent(parent, false);
            var foodRowRT = foodRow.GetComponent<RectTransform>();
            foodRowRT.anchorMin = new Vector2(0.5f, 0f);
            foodRowRT.anchorMax = new Vector2(0.5f, 0f);
            foodRowRT.pivot = new Vector2(0.5f, 0f);
            foodRowRT.sizeDelta = new Vector2(foodRowW, foodSlotSz);
            foodRowRT.anchoredPosition = new Vector2(0f, bottomY);

            for (int i = 0; i < FoodSlotCount; i++)
            {
                float x = i * (foodSlotSz + foodGap);
                var slotGO = new GameObject($"FoodSlot{i}", typeof(RectTransform), typeof(Image));
                slotGO.transform.SetParent(foodRow.transform, false);
                var slotRT = slotGO.GetComponent<RectTransform>();
                slotRT.anchorMin = new Vector2(0f, 0f);
                slotRT.anchorMax = new Vector2(0f, 0f);
                slotRT.pivot = new Vector2(0f, 0f);
                slotRT.sizeDelta = new Vector2(foodSlotSz, foodSlotSz);
                slotRT.anchoredPosition = new Vector2(x, 0f);
                slotGO.GetComponent<Image>().color = SlotTint;
                slotGO.GetComponent<Image>().raycastTarget = false;

                var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconGO.transform.SetParent(slotGO.transform, false);
                var iconRT = iconGO.GetComponent<RectTransform>();
                iconRT.anchorMin = Vector2.zero;
                iconRT.anchorMax = Vector2.one;
                iconRT.offsetMin = new Vector2(4f, 4f);
                iconRT.offsetMax = new Vector2(-4f, -4f);
                _foodSlotIcons[i] = iconGO.GetComponent<Image>();
                _foodSlotIcons[i].preserveAspect = true;
                _foodSlotIcons[i].raycastTarget = false;
                _foodSlotIcons[i].enabled = false;

                var countGO = new GameObject("Count", typeof(RectTransform));
                countGO.transform.SetParent(slotGO.transform, false);
                var countTmp = countGO.AddComponent<TextMeshProUGUI>();
                if (font != null) countTmp.font = font;
                countTmp.fontSize = 9f;
                countTmp.color = Color.white;
                countTmp.alignment = TextAlignmentOptions.BottomRight;
                countTmp.textWrappingMode = TextWrappingModes.NoWrap;
                countTmp.overflowMode = TextOverflowModes.Overflow;
                countTmp.raycastTarget = false;
                var countRT = countGO.GetComponent<RectTransform>();
                countRT.anchorMin = Vector2.zero;
                countRT.anchorMax = Vector2.one;
                countRT.offsetMin = new Vector2(2f, 1f);
                countRT.offsetMax = new Vector2(-2f, -1f);
                _foodSlotCounts[i] = countTmp;
                _foodSlotCounts[i].enabled = false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Inventory grid — click handlers
        // ══════════════════════════════════════════════════════════════════════

        private void OnSlotLeftClick(int slotIndex)
        {
            CompanionsPlugin.Log.LogDebug($"[UI] OnSlotLeftClick — slotIndex={slotIndex}");
            if (!TryGetMainGridCoord(slotIndex, out int gx, out int gy)) return;
            HandleSlotLeftClick(gx, gy, consumableOnly: false);
        }

        private void OnSlotRightClick(int slotIndex)
        {
            CompanionsPlugin.Log.LogDebug($"[UI] OnSlotRightClick — slotIndex={slotIndex}");
            if (!TryGetMainGridCoord(slotIndex, out int gx, out int gy)) return;
            HandleSlotRightClick(gx, gy);
        }

        private static bool IsConsumableItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) return false;
            return item.m_shared.m_food > 0f ||
                   item.m_shared.m_foodStamina > 0f ||
                   item.m_shared.m_foodEitr > 0f;
        }

        private bool TryGetMainGridCoord(int slotIndex, out int gx, out int gy)
        {
            gx = 0;
            gy = 0;
            if (slotIndex < 0 || slotIndex >= MainGridSlots) return false;
            gx = slotIndex % GridCols;
            gy = slotIndex / GridCols;
            return true;
        }

        private void HandleSlotLeftClick(int gx, int gy, bool consumableOnly)
        {
            if (InventoryGui.instance == null) return;
            var inv = GetStorageInventory();
            if (inv == null) return;

            var dragItem = _dragItemField?.GetValue(InventoryGui.instance) as ItemDrop.ItemData;
            var dragInv  = _dragInventoryField?.GetValue(InventoryGui.instance) as Inventory;
            int dragAmt  = (_dragAmountField != null) ? (int)_dragAmountField.GetValue(InventoryGui.instance) : 1;
            bool changed = false;

            if (dragItem != null && dragInv != null)
            {
                if (consumableOnly && !IsConsumableItem(dragItem))
                {
                    MessageHud.instance?.ShowMessage(
                        MessageHud.MessageType.Center,
                        "Food slots only accept food items.");
                    return;
                }

                try
                {
                    if (!dragInv.ContainsItem(dragItem))
                    {
                        ClearDrag();
                        return;
                    }
                }
                catch (NullReferenceException)
                {
                    ClearDrag();
                    return;
                }

                var targetItem = inv.GetItemAt(gx, gy);
                if (consumableOnly && targetItem != null && !IsConsumableItem(targetItem))
                {
                    MessageHud.instance?.ShowMessage(
                        MessageHud.MessageType.Center,
                        "Food slots only accept food items.");
                    return;
                }

                if (targetItem != null && _companionHumanoid.IsItemEquiped(targetItem))
                    _companionHumanoid.UnequipItem(targetItem, false);

                var player = Player.m_localPlayer;
                if (player != null && dragInv == player.GetInventory() && player.IsItemEquiped(dragItem))
                {
                    player.RemoveEquipAction(dragItem);
                    player.UnequipItem(dragItem, false);
                }

                bool moved = DropItemToCompanion(inv, dragInv, dragItem, dragAmt, gx, gy);
                if (moved)
                {
                    ClearDrag();
                    changed = true;
                }
            }
            else
            {
                var item = inv.GetItemAt(gx, gy);
                if (item == null) return;

                if (_setupDragItem != null && InventoryGui.instance != null)
                {
                    _setupDragItem.Invoke(InventoryGui.instance,
                        new object[] { item, inv, item.m_stack });
                }
                return;
            }

            if (changed)
                OnCompanionInventoryMutated();
            _invRefreshTimer = 0f;
        }

        private static bool ShouldSwapForDrop(ItemDrop.ItemData dragItem, ItemDrop.ItemData targetItem, int amount)
        {
            if (dragItem == null || targetItem == null) return false;
            if (targetItem == dragItem) return false;
            if (dragItem.m_stack != amount) return false;
            return targetItem.m_shared.m_name != dragItem.m_shared.m_name ||
                   (dragItem.m_shared.m_maxQuality > 1 && targetItem.m_quality != dragItem.m_quality) ||
                   targetItem.m_shared.m_maxStackSize == 1;
        }

        private static bool DropItemToCompanion(Inventory targetInv, Inventory fromInv,
            ItemDrop.ItemData dragItem, int amount, int x, int y)
        {
            if (targetInv == null || fromInv == null || dragItem == null) return false;
            amount = Mathf.Clamp(amount, 1, dragItem.m_stack);

            var targetItem = targetInv.GetItemAt(x, y);
            if (ShouldSwapForDrop(dragItem, targetItem, amount))
            {
                Vector2i oldPos = dragItem.m_gridPos;
                fromInv.RemoveItem(dragItem);
                fromInv.MoveItemToThis(targetInv, targetItem, targetItem.m_stack, oldPos.x, oldPos.y);
                targetInv.MoveItemToThis(fromInv, dragItem, amount, x, y);
                return true;
            }
            return targetInv.MoveItemToThis(fromInv, dragItem, amount, x, y);
        }

        private void OnCompanionInventoryMutated()
        {
            _companion?.SyncEquipmentToInventory();
            _invRefreshTimer = 0f;
        }

        private void HandleSlotRightClick(int gx, int gy)
        {
            var inv = GetStorageInventory();
            if (inv == null) return;
            var item = inv.GetItemAt(gx, gy);
            if (item == null) return;

            bool hadItem = inv.ContainsItem(item);
            int stackBefore = hadItem ? item.m_stack : 0;
            bool equippedBefore = _companionHumanoid.IsItemEquiped(item);
            bool changed = false;

            if (IsConsumableItem(item) && _companionFood != null)
                changed = _companionFood.TryConsumeItem(item);

            if (!changed)
            {
                _companionHumanoid.UseItem(inv, item, true);
                bool hasItemAfter = inv.ContainsItem(item);
                int stackAfter = hasItemAfter ? item.m_stack : 0;
                bool equippedAfter = hasItemAfter && _companionHumanoid.IsItemEquiped(item);
                changed = hadItem != hasItemAfter ||
                          stackBefore != stackAfter ||
                          equippedBefore != equippedAfter;
            }

            if (changed)
                OnCompanionInventoryMutated();
            else
                _invRefreshTimer = 0f;
        }

        private void ClearDrag()
        {
            if (InventoryGui.instance == null) return;
            _setupDragItem?.Invoke(InventoryGui.instance,
                new object[] { null, null, 1 });
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Name change
        // ══════════════════════════════════════════════════════════════════════

        private void OnNameChanged(string newName)
        {
            EnsureCompanionOwnership();
            if (_companionNview == null || _companionNview.GetZDO() == null) return;
            _companionNview.GetZDO().Set(CompanionSetup.NameHash, newName);
            if (_companionChar != null)
                _companionChar.m_name = string.IsNullOrEmpty(newName) ? "Companion" : newName;
        }

        private void EnsureCompanionOwnership()
        {
            if (_companionNview == null || _companionNview.GetZDO() == null) return;
            if (!_companionNview.IsOwner())
                _companionNview.ClaimOwnership();
        }

        private Inventory GetStorageInventory()
        {
            if (_builtForDverger && _companionContainer != null)
                return _companionContainer.GetInventory();
            return _companionHumanoid?.GetInventory();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Inventory grid refresh
        // ══════════════════════════════════════════════════════════════════════

        private void RefreshInventoryGrid()
        {
            if (_slotIcons == null) return;

            _invRefreshTimer -= Time.deltaTime;
            if (_invRefreshTimer > 0f) return;
            _invRefreshTimer = 0.25f;

            var inv = GetStorageInventory();
            if (inv == null) return;

            for (int i = 0; i < MainGridSlots; i++)
            {
                _slotIcons[i].enabled   = false;
                _slotCounts[i].enabled  = false;
                _slotBorders[i].enabled = false;
                _slotBgs[i].color       = SlotTint;
                if (_slotDurabilityBars != null && _slotDurabilityBars[i] != null)
                    _slotDurabilityBars[i].SetActive(false);
            }
            if (_foodSlotIcons != null && _foodSlotCounts != null)
            {
                for (int i = 0; i < FoodSlotCount; i++)
                {
                    _foodSlotIcons[i].enabled  = false;
                    _foodSlotCounts[i].enabled = false;
                }
            }

            foreach (var item in inv.GetAllItems())
            {
                int gx = item.m_gridPos.x;
                int gy = item.m_gridPos.y;
                int flatIndex = gy * GridCols + gx;
                if (flatIndex < 0 || flatIndex >= GridSlots) continue;

                _slotIcons[flatIndex].sprite  = item.GetIcon();
                _slotIcons[flatIndex].enabled = true;

                if (item?.m_shared != null && item.m_shared.m_maxStackSize > 1)
                {
                    _slotCounts[flatIndex].text    = FormatStackSize(item);
                    _slotCounts[flatIndex].enabled = true;
                }

                if (_companionHumanoid.IsItemEquiped(item))
                {
                    _slotBorders[flatIndex].enabled = true;
                    _slotBgs[flatIndex].color = EquippedSlotTint;
                }

                if (_slotDurabilityBars != null && _slotDurabilityFills != null
                    && item.m_shared != null && item.m_shared.m_useDurability)
                {
                    bool damaged = item.m_durability < item.GetMaxDurability();
                    _slotDurabilityBars[flatIndex].SetActive(damaged);
                    if (damaged)
                    {
                        var fillRT = _slotDurabilityFills[flatIndex].GetComponent<RectTransform>();
                        if (item.m_durability <= 0f)
                        {
                            fillRT.anchorMax = Vector2.one;
                            bool blink = Mathf.Sin(Time.time * 10f) > 0f;
                            _slotDurabilityFills[flatIndex].color = blink
                                ? Color.red : new Color(0f, 0f, 0f, 0f);
                        }
                        else
                        {
                            float pct = item.GetDurabilityPercentage();
                            fillRT.anchorMax = new Vector2(pct, 1f);
                            Color durColor = pct > 0.5f
                                ? Color.Lerp(Color.yellow, Color.green, (pct - 0.5f) * 2f)
                                : Color.Lerp(Color.red, Color.yellow, pct * 2f);
                            _slotDurabilityFills[flatIndex].color = durColor;
                        }
                    }
                }
            }

            if (_hoveredSlot >= 0 && _hoveredSlot < MainGridSlots)
            {
                _slotBgs[_hoveredSlot].color = _slotBgs[_hoveredSlot].color == EquippedSlotTint
                    ? EquippedHoverTint : SlotHoverTint;
            }

            RefreshFoodSlots(inv);
        }

        private void RefreshFoodSlots(Inventory inv)
        {
            if (_foodSlotIcons == null || _foodSlotCounts == null) return;
            if (_companionFood == null) return;

            for (int i = 0; i < FoodSlotCount; i++)
            {
                var activeFood = _companionFood.GetFood(i);
                if (!activeFood.IsActive) continue;

                Sprite icon = ResolveFoodIcon(activeFood, inv);
                if (icon != null)
                {
                    _foodSlotIcons[i].sprite = icon;
                    _foodSlotIcons[i].enabled = true;
                }

                _foodSlotCounts[i].text = FormatFoodDuration(activeFood.RemainingTime);
                _foodSlotCounts[i].color = activeFood.RemainingTime >= 60f
                    ? Color.white
                    : new Color(1f, 1f, 1f, Mathf.Clamp01(0.4f + Mathf.Sin(Time.time * 10f) * 0.6f));
                _foodSlotCounts[i].enabled = true;
            }
        }

        private static string FormatStackSize(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return "";
            int current = Mathf.Max(0, item.m_stack);
            int max = Mathf.Max(1, item.m_shared.m_maxStackSize);
            return current.ToString(CultureInfo.InvariantCulture) + "/" +
                   max.ToString(CultureInfo.InvariantCulture);
        }

        private static Sprite ResolveFoodIcon(CompanionFood.FoodEffect food, Inventory inv)
        {
            if (inv != null)
            {
                foreach (var item in inv.GetAllItems())
                {
                    if (item?.m_shared == null) continue;
                    if (item.m_shared.m_name == food.ItemName)
                        return item.GetIcon();
                }
            }

            if (!string.IsNullOrEmpty(food.ItemPrefabName))
            {
                var prefab = ObjectDB.instance?.GetItemPrefab(food.ItemPrefabName);
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop != null) return drop.m_itemData.GetIcon();
            }

            var all = ObjectDB.instance?.m_items;
            if (all == null) return null;
            foreach (var prefab in all)
            {
                var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
                if (drop?.m_itemData?.m_shared == null) continue;
                if (drop.m_itemData.m_shared.m_name == food.ItemName)
                    return drop.m_itemData.GetIcon();
            }

            return null;
        }

        private static string FormatFoodDuration(float remainingSeconds)
        {
            if (remainingSeconds >= 60f)
                return Mathf.CeilToInt(remainingSeconds / 60f) + "m";
            return Mathf.Max(0, Mathf.FloorToInt(remainingSeconds)) + "s";
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Teardown
        // ══════════════════════════════════════════════════════════════════════

        private void Teardown()
        {
            if (_root != null) { UnityEngine.Object.Destroy(_root); _root = null; }
            _built = false;
            _panelBgSprite  = null;
            _sliderBgSprite = null;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private static bool IsBrokenTmpFont(TMP_FontAsset font)
        {
            return font == null ||
                   font.name.IndexOf("LiberationSans", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static TMP_FontAsset GetFont()
        {
            if (InventoryGui.instance != null)
            {
                var texts = InventoryGui.instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    var text = texts[i];
                    if (text == null || IsBrokenTmpFont(text.font)) continue;
                    return text.font;
                }
            }

            if (!IsBrokenTmpFont(TMP_Settings.defaultFontAsset))
                return TMP_Settings.defaultFontAsset;

            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            TMP_FontAsset best = null;
            for (int i = 0; i < fonts.Length; i++)
            {
                var fnt = fonts[i];
                if (IsBrokenTmpFont(fnt)) continue;
                string name = fnt.name.ToLowerInvariant();
                if (name.Contains("averia") || name.Contains("norse") || name.Contains("valheim"))
                    return fnt;
                if (best == null) best = fnt;
            }

            return best;
        }

        private static void ApplyFallbackFont(Transform root, TMP_FontAsset font)
        {
            if (root == null || IsBrokenTmpFont(font)) return;
            var texts = root.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text == null || !IsBrokenTmpFont(text.font)) continue;
                text.font = font;
            }
        }

        private static TextMeshProUGUI MakeText(Transform parent, string name, string text,
            TMP_FontAsset font, float size, Color color, TextAlignmentOptions align)
        {
            var go  = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var tmp = go.GetComponent<TextMeshProUGUI>();
            tmp.text             = text;
            tmp.fontSize         = size;
            tmp.color            = color;
            tmp.alignment        = align;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode     = TextOverflowModes.Ellipsis;
            tmp.raycastTarget    = false;
            if (font != null) tmp.font = font;
            return tmp;
        }

        private static void StretchFill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static RectTransform MakePadded(Transform parent, float pad)
        {
            var go = new GameObject("Pad", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(pad, pad);
            rt.offsetMax = new Vector2(-pad, -pad);
            return rt;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Bootstrap
        // ══════════════════════════════════════════════════════════════════════

        internal static void EnsureInstance()
        {
            if (Instance != null) return;
            var go = new GameObject("HC_CompanionInteractPanel");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<CompanionInteractPanel>();
        }
    }
}
