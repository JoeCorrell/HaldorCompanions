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
    /// Companion interaction overlay — appears alongside InventoryGui when
    /// a companion's Container is opened.  Single panel with two tabs:
    /// Actions = stats + controls, Inventory = grid + food slots.
    /// Vanilla container panel is hidden; our grid handles item transfer.
    /// </summary>
    public class CompanionInteractPanel : MonoBehaviour
    {
        public static CompanionInteractPanel Instance { get; private set; }

        // ── Public state ─────────────────────────────────────────────────────
        public bool IsVisible => _visible;
        public bool IsNameInputFocused => _nameInput != null && _nameInput.isFocused;
        public CompanionSetup CurrentCompanion => _companion;

        // ── Layout constants ─────────────────────────────────────────────────
        private const float PanelW       = 400f;
        private const float PanelH       = 400f;
        private const float DvergerPanelH = 260f;
        private const float UiScale      = 1.25f;
        private const float OuterPad     = 6f;
        private const float TabBarH      = 28f;

        // ── Grid constants ───────────────────────────────────────────────────
        private const int GridCols          = 5;
        private const int GridSlots         = 30;
        private const int FoodSlotCount     = 3;
        private const int MainGridSlots     = 30;
        private const float InventorySlotGap = 3f;

        // ── Style constants ──────────────────────────────────────────────────
        private static readonly Color PanelBg        = new Color(0.18f, 0.14f, 0.09f, 0.92f);
        private static readonly Color ColBg          = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color GoldColor      = new Color(0.83f, 0.64f, 0.31f, 1f);
        private static readonly Color GoldTextColor  = new Color(0.83f, 0.52f, 0.18f, 1f);
        private static readonly Color LabelText      = new Color(1f, 0.9f, 0.5f, 1f);
        private static readonly Color EquipBlue      = new Color(0.29f, 0.55f, 0.94f, 1f);
        private static readonly Color BarBg          = new Color(0.15f, 0.15f, 0.15f, 0.85f);
        private static readonly Color SlotTint       = new Color(0f, 0f, 0f, 0.5625f);
        private static readonly Color EquippedSlotTint = new Color(0.10f, 0.20f, 0.38f, 0.80f);

        // ── Custom sprite caches ─────────────────────────────────────────────
        private static Sprite _panelBgSprite;
        private static Sprite _sliderBgSprite;

        // ── Button template ──────────────────────────────────────────────────
        private static GameObject _buttonTemplate;
        private static float      _btnHeight = 30f;

        // ── Preview camera ───────────────────────────────────────────────────
        private static readonly Vector3 PreviewPos = new Vector3(10040f, 5000f, 10000f);
        private RenderTexture  _rt;
        private Camera         _cam;
        private GameObject     _camGO;
        private GameObject     _clone;
        private GameObject     _lightRig;
        private static readonly Vector3 PreviewCameraOffsetDir =
            new Vector3(0f, 0.18f, 1f).normalized;

        private Color                             _savedAmbient;
        private float                             _savedAmbientIntensity;
        private UnityEngine.Rendering.AmbientMode _savedAmbientMode;

        // ── VisEquipment reflection ──────────────────────────────────────────
        private static readonly MethodInfo _updateVisuals =
            AccessTools.Method(typeof(VisEquipment), "UpdateVisuals");
        private static readonly FieldInfo _visRightItemHash    = AccessTools.Field(typeof(VisEquipment), "m_currentRightItemHash");
        private static readonly FieldInfo _visLeftItemHash     = AccessTools.Field(typeof(VisEquipment), "m_currentLeftItemHash");
        private static readonly FieldInfo _visChestItemHash    = AccessTools.Field(typeof(VisEquipment), "m_currentChestItemHash");
        private static readonly FieldInfo _visLegItemHash      = AccessTools.Field(typeof(VisEquipment), "m_currentLegItemHash");
        private static readonly FieldInfo _visHelmetItemHash   = AccessTools.Field(typeof(VisEquipment), "m_currentHelmetItemHash");
        private static readonly FieldInfo _visShoulderItemHash = AccessTools.Field(typeof(VisEquipment), "m_currentShoulderItemHash");
        private static readonly FieldInfo _visUtilityItemHash  = AccessTools.Field(typeof(VisEquipment), "m_currentUtilityItemHash");

        // ── InventoryGui drag system reflection ──────────────────────────────
        private static readonly FieldInfo _dragItemField      = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo _dragInventoryField  = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
        private static readonly FieldInfo _dragAmountField     = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
        private static readonly MethodInfo _setupDragItem      = AccessTools.Method(typeof(InventoryGui), "SetupDragItem",
            new[] { typeof(ItemDrop.ItemData), typeof(Inventory), typeof(int) });

        // ── Companion references ─────────────────────────────────────────────
        private CompanionSetup   _companion;
        private Character        _companionChar;
        private CompanionStamina _companionStamina;
        private CompanionFood    _companionFood;
        private Humanoid         _companionHumanoid;
        private Container        _companionContainer;
        private ZNetView         _companionNview;
        private CompanionAI      _companionAI;
        private HarvestController   _companionHarvest;

        // ── UI elements ──────────────────────────────────────────────────────
        private GameObject      _root;
        private TMP_InputField  _nameInput;
        private TextMeshProUGUI _modeText;
        private Button          _followBtn;
        private Button          _gatherWoodBtn;
        private Button          _gatherStoneBtn;
        private Button          _gatherOreBtn;
        private Button          _stayBtn;
        private Button          _setHomeBtn;
        private Button          _autoPickupBtn;
        private Button          _commandableBtn;
        private int             _activeMode;

        // ── Stance buttons ──────────────────────────────────────────────────
        private Button _balancedBtn;
        private Button _aggressiveBtn;
        private Button _defensiveBtn;
        private Button _passiveBtn;
        private int    _activeStance;

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

        // ── Tab system ───────────────────────────────────────────────────────
        private RectTransform   _actionsContent;
        private RectTransform   _inventoryContent;
        private Button          _actionsTabBtn;
        private Button          _inventoryTabBtn;
        private int             _activeTab;

        // ── Controller selection highlight ──────────────────────────────────
        private static readonly Color SelectionGlow = new Color(1f, 0.82f, 0.24f, 0.25f);
        private readonly System.Collections.Generic.Dictionary<Button, GameObject> _selectionOverlays =
            new System.Collections.Generic.Dictionary<Button, GameObject>();
        private GameObject _lastSelectedOverlay;

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()  { Instance = this; }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            ClearPreviewClone();
            // Release RenderTexture and camera
            if (_rt != null)  { _rt.Release(); UnityEngine.Object.Destroy(_rt); _rt = null; }
            if (_camGO != null) { UnityEngine.Object.Destroy(_camGO); _camGO = null; }
            _cam = null;
            // Clear static sprite caches to avoid holding destroyed Texture2D refs
            _panelBgSprite = null;
            _sliderBgSprite = null;
            _buttonTemplate = null;
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

            // Sync action mode from ZDO (may change externally, e.g. overweight auto-revert)
            if (_companionNview != null && _companionNview.GetZDO() != null)
            {
                int zdoMode = _companionNview.GetZDO().GetInt(
                    CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                if (zdoMode != _activeMode)
                {
                    _activeMode = zdoMode;
                    RefreshActionButtons();
                    RefreshModeText();
                }

                int zdoStance = _companionNview.GetZDO().GetInt(
                    CompanionSetup.CombatStanceHash, CompanionSetup.StanceBalanced);
                if (zdoStance != _activeStance)
                {
                    _activeStance = zdoStance;
                    RefreshStanceButtons();
                }

                // Sync StayHome toggle from ZDO
                RefreshStayHomeButton();
            }

            // Gamepad: LB/RB cycle tabs, same keys vanilla uses for InventoryGui tab cycling
            if (ZInput.GetButtonDown("JoyTabLeft"))
            {
                SwitchTab(0); // Actions tab
                ZInput.ResetButtonStatus("JoyTabLeft");
            }
            else if (ZInput.GetButtonDown("JoyTabRight"))
            {
                SwitchTab(1); // Inventory tab
                ZInput.ResetButtonStatus("JoyTabRight");
            }

            UpdateControllerHighlight();
        }

        private void UpdateControllerHighlight()
        {
            // Hide previous overlay
            if (_lastSelectedOverlay != null)
            {
                _lastSelectedOverlay.SetActive(false);
                _lastSelectedOverlay = null;
            }

            if (!ZInput.IsGamepadActive()) return;
            if (EventSystem.current == null) return;

            var selected = EventSystem.current.currentSelectedGameObject;
            if (selected == null) return;

            // Find the "selected" child overlay on the focused button
            var selTr = selected.transform.Find("selected");
            if (selTr == null) return;

            selTr.gameObject.SetActive(true);
            _lastSelectedOverlay = selTr.gameObject;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════════

        public void Show(CompanionSetup companion)
        {
            _companion          = companion;
            _companionChar      = companion.GetComponent<Character>();
            _companionStamina   = companion.GetComponent<CompanionStamina>();
            _companionFood      = companion.GetComponent<CompanionFood>();
            _companionHumanoid  = companion.GetComponent<Humanoid>();
            _companionContainer = companion.GetComponent<Container>();
            _companionNview     = companion.GetComponent<ZNetView>();
            _companionAI        = companion.GetComponent<CompanionAI>();
            _companionHarvest   = companion.GetComponent<HarvestController>();

            CompanionsPlugin.Log.LogDebug(
                $"[UI] Show — companion=\"{companion.name}\" " +
                $"char={_companionChar != null} stamina={_companionStamina != null} " +
                $"food={_companionFood != null} humanoid={_companionHumanoid != null} " +
                $"nview={_companionNview != null} ai={_companionAI != null} " +
                $"harvest={_companionHarvest != null} " +
                $"dragField={_dragItemField != null} dragInvField={_dragInventoryField != null} " +
                $"dragAmtField={_dragAmountField != null} setupDrag={_setupDragItem != null}");

            EnsureCompanionOwnership();

            // Detect Dverger — simplified UI (no gather/stance/commandable)
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

            string savedName = "";
            if (_companionNview != null && _companionNview.GetZDO() != null)
                savedName = _companionNview.GetZDO().GetString(CompanionSetup.NameHash, "");
            if (_nameInput != null)
            {
                _nameInput.onValueChanged.RemoveAllListeners();
                _nameInput.text = savedName;
                _nameInput.onValueChanged.AddListener(OnNameChanged);
            }

            _activeMode = CompanionSetup.ModeFollow;
            if (_companionNview != null && _companionNview.GetZDO() != null)
                _activeMode = _companionNview.GetZDO().GetInt(
                    CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            if (_activeMode < CompanionSetup.ModeFollow || _activeMode > CompanionSetup.ModeStay)
                _activeMode = CompanionSetup.ModeFollow;

            _activeStance = _companion.GetCombatStance();
            RefreshActionButtons();
            RefreshStanceButtons();
            RefreshModeText();
            RefreshAutoPickupButton();
            RefreshCommandableButton();
            _invRefreshTimer = 0f;

            SwitchTab(0); // Always open on Actions tab

            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            _visible = true;

            // Give gamepad focus to the first action button when opening
            if (_followBtn != null && ZInput.IsGamepadActive())
                EventSystem.current?.SetSelectedGameObject(_followBtn.gameObject);
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

            ClearPreviewClone();
            _companion          = null;
            _companionChar      = null;
            _companionStamina   = null;
            _companionFood      = null;
            _companionHumanoid  = null;
            _companionContainer = null;
            _companionNview     = null;
            _companionAI        = null;
            _companionHarvest   = null;
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
        //  Button template
        // ══════════════════════════════════════════════════════════════════════

        private static void EnsureButtonTemplate()
        {
            if (_buttonTemplate != null) return;
            if (InventoryGui.instance == null || InventoryGui.instance.m_craftButton == null) return;

            var origRT = InventoryGui.instance.m_craftButton.GetComponent<RectTransform>();
            if (origRT != null)
                _btnHeight = Mathf.Max(origRT.rect.height, 30f);

            _buttonTemplate = UnityEngine.Object.Instantiate(InventoryGui.instance.m_craftButton.gameObject);
            _buttonTemplate.name = "CIP_ButtonTemplate";
            _buttonTemplate.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(_buttonTemplate);
        }

        private static GameObject CreateTintedButton(Transform parent, string name, string label)
        {
            EnsureButtonTemplate();
            if (_buttonTemplate == null) return null;

            var go = UnityEngine.Object.Instantiate(_buttonTemplate, parent);
            go.name = name;
            go.SetActive(true);

            var btn = go.GetComponent<Button>();
            var img = go.GetComponent<Image>();
            if (img == null) img = go.AddComponent<Image>();
            img.raycastTarget = true;
            var txt = go.GetComponentInChildren<TMP_Text>(true);
            if (txt != null)
            {
                txt.gameObject.SetActive(true);
                txt.text = label;
            }

            StripButtonHints(go, txt);

            // Fix button after stripping: Animator is gone, targetGraphic may be null
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.navigation = new Navigation { mode = Navigation.Mode.None };
                btn.interactable = true;
                btn.transition = Selectable.Transition.None;
                btn.targetGraphic = img;
            }

            // Controller selection highlight — semi-transparent gold glow behind the button
            var selGO = new GameObject("selected", typeof(RectTransform), typeof(Image));
            selGO.transform.SetParent(go.transform, false);
            selGO.transform.SetAsFirstSibling();
            var selRT = selGO.GetComponent<RectTransform>();
            selRT.anchorMin = Vector2.zero;
            selRT.anchorMax = Vector2.one;
            selRT.offsetMin = new Vector2(-4f, -4f);
            selRT.offsetMax = new Vector2(4f, 4f);
            selGO.GetComponent<Image>().color = SelectionGlow;
            selGO.GetComponent<Image>().raycastTarget = false;
            selGO.SetActive(false);

            return go;
        }

        private static void StripButtonHints(GameObject btnGO, TMP_Text label)
        {
            var anim = btnGO.GetComponent<Animator>();
            if (anim != null) UnityEngine.Object.Destroy(anim);
            var csf = btnGO.GetComponent<ContentSizeFitter>();
            if (csf != null) UnityEngine.Object.Destroy(csf);
            var le = btnGO.GetComponent<LayoutElement>();
            if (le != null) UnityEngine.Object.Destroy(le);

            for (int i = btnGO.transform.childCount - 1; i >= 0; i--)
            {
                var child = btnGO.transform.GetChild(i);
                if (label != null && (child.gameObject == label.gameObject || label.transform.IsChildOf(child)))
                    continue;
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Build UI — 2-panel layout with tabs
        // ══════════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            TMP_FontAsset font = GetFont();
            EnsureButtonTemplate();

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

            float panelH = _builtForDverger ? DvergerPanelH : PanelH;

            var rootRT = _root.GetComponent<RectTransform>();
            rootRT.anchorMin        = new Vector2(0.5f, 0.5f);
            rootRT.anchorMax        = new Vector2(0.5f, 0.5f);
            rootRT.pivot            = new Vector2(0.5f, 0.5f);
            rootRT.sizeDelta        = new Vector2(PanelW, panelH);
            rootRT.anchoredPosition = Vector2.zero;
            rootRT.localScale       = Vector3.one * UiScale;

            var rootImg = _root.AddComponent<Image>();
            ApplyPanelBg(rootImg, PanelBg);
            rootImg.raycastTarget = true;

            // Tab bar — two buttons at the top
            BuildTabBar(_root.transform, font);

            // Content containers — fill area below tab bar, above mode text
            _actionsContent = CreateContentContainer(_root.transform, "ActionsContent");
            _inventoryContent = CreateContentContainer(_root.transform, "InventoryContent");

            // Actions tab: stat bars + action controls + food display
            BuildPreview(_actionsContent, font);

            // Inventory tab: inventory grid + food slots
            var invPad = MakePadded(_inventoryContent, 2f);
            BuildInventoryContent(invPad, font);

            // Mode text — anchored to bottom of root panel (always visible)
            _modeText = MakeText(_root.transform, "ModeText", "Follow into Battle",
                font, 11f, GoldColor, TextAlignmentOptions.Center);
            var modeRT = _modeText.GetComponent<RectTransform>();
            modeRT.anchorMin        = new Vector2(0f, 0f);
            modeRT.anchorMax        = new Vector2(1f, 0f);
            modeRT.pivot            = new Vector2(0.5f, 0f);
            modeRT.sizeDelta        = new Vector2(0f, 16f);
            modeRT.anchoredPosition = new Vector2(0f, -14f);

            // Default to Actions tab
            SwitchTab(0);

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

        private void BuildTabBar(Transform parent, TMP_FontAsset font)
        {
            var barGO = new GameObject("TabBar", typeof(RectTransform));
            barGO.transform.SetParent(parent, false);
            var barRT = barGO.GetComponent<RectTransform>();
            barRT.anchorMin = new Vector2(0f, 1f);
            barRT.anchorMax = new Vector2(1f, 1f);
            barRT.pivot = new Vector2(0.5f, 1f);
            barRT.sizeDelta = new Vector2(0f, TabBarH);
            barRT.anchoredPosition = new Vector2(0f, -OuterPad);

            float halfW = (PanelW - OuterPad * 2f - 4f) / 2f;

            _actionsTabBtn = BuildTabButton(barGO.transform, "ActionsTab", "Actions",
                font, OuterPad, halfW);
            if (_actionsTabBtn != null)
                _actionsTabBtn.onClick.AddListener(() => SwitchTab(0));

            _inventoryTabBtn = BuildTabButton(barGO.transform, "InventoryTab", "Inventory",
                font, OuterPad + halfW + 4f, halfW);
            if (_inventoryTabBtn != null)
                _inventoryTabBtn.onClick.AddListener(() => SwitchTab(1));
        }

        private Button BuildTabButton(Transform parent, string name, string label,
            TMP_FontAsset font, float xOffset, float width)
        {
            var go = CreateTintedButton(parent, name, label);
            if (go == null) return null;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(width, 0f);
            rt.anchoredPosition = new Vector2(xOffset, 0f);

            return go.GetComponent<Button>();
        }

        private RectTransform CreateContentContainer(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            // Fill area below tab bar, above mode text area
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.offsetMin = new Vector2(OuterPad, OuterPad);
            rt.offsetMax = new Vector2(-OuterPad, -(OuterPad + TabBarH + 4f));
            var img = go.GetComponent<Image>();
            img.sprite = null;
            img.color = ColBg;
            img.raycastTarget = false;
            return rt;
        }

        private void SwitchTab(int tab)
        {
            CompanionsPlugin.Log.LogDebug($"[UI] SwitchTab — {_activeTab} → {tab}");
            _activeTab = tab;
            if (_actionsContent != null)
                _actionsContent.gameObject.SetActive(tab == 0);
            if (_inventoryContent != null)
                _inventoryContent.gameObject.SetActive(tab == 1);

            SetBtnHighlight(_actionsTabBtn, tab == 0);
            SetBtnHighlight(_inventoryTabBtn, tab == 1);
        }

        private static float ComputeInventorySlotSize()
        {
            int gridRows = Mathf.CeilToInt(MainGridSlots / (float)GridCols);

            float availW = PanelW - OuterPad * 2f - 4f;
            float slotW = (availW - (GridCols - 1) * InventorySlotGap) / GridCols;

            // Food section overhead: bottomY(4) + gap(2) + label(14) + gap(4) + sep(1) + gridGap(8) = 33
            const float foodOverhead = 33f;
            float totalH = PanelH - OuterPad * 2f - TabBarH - 4f;
            // Height budget: gridRows grid slots + 1 food slot row + overhead + row gaps
            float slotH = (totalH - foodOverhead - (gridRows - 1) * InventorySlotGap) / (gridRows + 1);

            return Mathf.Floor(Mathf.Min(slotW, slotH));
        }

        private void BuildInventoryContent(RectTransform parent, TMP_FontAsset font)
        {
            float slotSize = ComputeInventorySlotSize();
            float y = 0f;
            BuildInventoryGrid(parent, font, ref y, slotSize);
            BuildFoodSlots(parent, font, slotSize);
        }

        private void BuildActionControls(RectTransform parent, TMP_FontAsset font, ref float y)
        {
            float btnH = 30f;
            float btnGap = 2f;
            _followBtn = BuildActionButton(parent, ref y, btnH, btnGap, "Follow into Battle", CompanionSetup.ModeFollow);

            // Gather buttons — Player-clone companions only (Dvergers don't gather)
            if (!_builtForDverger)
            {
                _gatherWoodBtn = BuildActionButton(parent, ref y, btnH, btnGap, "Gather Wood", CompanionSetup.ModeGatherWood);
                _gatherStoneBtn = BuildActionButton(parent, ref y, btnH, btnGap, "Gather Stone", CompanionSetup.ModeGatherStone);
                _gatherOreBtn = BuildActionButton(parent, ref y, btnH, btnGap, "Gather Ore", CompanionSetup.ModeGatherOre);
            }
            else
            {
                _gatherWoodBtn = null;
                _gatherStoneBtn = null;
                _gatherOreBtn = null;
            }

            _stayBtn = BuildUnwiredActionButton(parent, ref y, btnH, btnGap, "Stay Home: OFF");
            if (_stayBtn != null)
            {
                _stayBtn.onClick.AddListener(ToggleStayHome);
            }
            _setHomeBtn = BuildUnwiredActionButton(parent, ref y, btnH, btnGap, "Set Home");
            if (_setHomeBtn != null)
                _setHomeBtn.onClick.AddListener(OnSetHomeClicked);
            _autoPickupBtn = BuildActionButton(parent, ref y, btnH, btnGap, "Auto Pickup: OFF", -1);
            if (_autoPickupBtn != null)
            {
                _autoPickupBtn.onClick.RemoveAllListeners();
                _autoPickupBtn.onClick.AddListener(ToggleAutoPickup);
            }

            // Commandable + Combat Stance — Player-clone companions only
            if (!_builtForDverger)
            {
                _commandableBtn = BuildActionButton(parent, ref y, btnH, btnGap, "Command: ON", -1);
                if (_commandableBtn != null)
                {
                    _commandableBtn.onClick.RemoveAllListeners();
                    _commandableBtn.onClick.AddListener(ToggleCommandable);
                }

                // ── Combat Stance section ──
                y -= 6f;
                var stanceHeader = MakeText(parent, "StanceHeader", "Combat Stance",
                    font, 10f, GoldColor, TextAlignmentOptions.Center);
                stanceHeader.fontStyle = FontStyles.Bold;
                PlaceTopStretch(stanceHeader.GetComponent<RectTransform>(), ref y, 14f);
                y -= 2f;

                // 4 horizontal stance buttons
                float stanceBtnH = 30f;
                float stanceGap = 2f;
                float totalW = parent.rect.width > 0 ? parent.rect.width : (PanelW - OuterPad * 2f - 4f);
                float stanceBtnW = (totalW - stanceGap * 3f) / 4f;

                var stanceRow = new GameObject("StanceRow", typeof(RectTransform));
                stanceRow.transform.SetParent(parent, false);
                var stanceRowRT = stanceRow.GetComponent<RectTransform>();
                stanceRowRT.anchorMin = new Vector2(0f, 1f);
                stanceRowRT.anchorMax = new Vector2(1f, 1f);
                stanceRowRT.pivot = new Vector2(0.5f, 1f);
                stanceRowRT.sizeDelta = new Vector2(0f, stanceBtnH);
                stanceRowRT.anchoredPosition = new Vector2(0f, y);
                y -= stanceBtnH + 2f;

                _balancedBtn   = BuildStanceButton(stanceRow.transform, "BAL", 0, stanceBtnW, stanceBtnH, stanceGap, CompanionSetup.StanceBalanced);
                _aggressiveBtn = BuildStanceButton(stanceRow.transform, "AGG", 1, stanceBtnW, stanceBtnH, stanceGap, CompanionSetup.StanceAggressive);
                _defensiveBtn  = BuildStanceButton(stanceRow.transform, "DEF", 2, stanceBtnW, stanceBtnH, stanceGap, CompanionSetup.StanceDefensive);
                _passiveBtn    = BuildStanceButton(stanceRow.transform, "PAS", 3, stanceBtnW, stanceBtnH, stanceGap, CompanionSetup.StancePassive);
            }
            else
            {
                _commandableBtn = null;
                _balancedBtn = null;
                _aggressiveBtn = null;
                _defensiveBtn = null;
                _passiveBtn = null;
            }

            // Set up explicit gamepad navigation — d-pad / left stick cycles through buttons vertically.
            // Tab buttons intentionally excluded so LB/RB handle tab switching, not up/down.
            // Build a filtered list so navigation never links to null buttons.
            var allBtns = new Button[] {
                _followBtn, _gatherWoodBtn, _gatherStoneBtn, _gatherOreBtn,
                _stayBtn, _setHomeBtn, _autoPickupBtn, _commandableBtn,
                _balancedBtn, _aggressiveBtn, _defensiveBtn, _passiveBtn
            };
            var validBtns = new System.Collections.Generic.List<Button>();
            for (int i = 0; i < allBtns.Length; i++)
                if (allBtns[i] != null) validBtns.Add(allBtns[i]);
            for (int i = 0; i < validBtns.Count; i++)
            {
                var nav = new Navigation { mode = Navigation.Mode.Explicit };
                nav.selectOnUp   = validBtns[(i - 1 + validBtns.Count) % validBtns.Count];
                nav.selectOnDown = validBtns[(i + 1) % validBtns.Count];
                validBtns[i].navigation = nav;
            }
        }

        private float BuildNameInput(RectTransform parent, TMP_FontAsset font, float y)
        {
            float h = 26f;

            var inputGO = new GameObject("NameInput", typeof(RectTransform), typeof(Image));
            inputGO.transform.SetParent(parent, false);
            var inputRT = inputGO.GetComponent<RectTransform>();
            inputRT.anchorMin = new Vector2(0f, 1f);
            inputRT.anchorMax = new Vector2(1f, 1f);
            inputRT.pivot = new Vector2(0f, 1f);
            inputRT.sizeDelta = new Vector2(0f, h);
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

        private void BuildInventoryGrid(RectTransform parent, TMP_FontAsset font, ref float y, float slotSize)
        {
            _slotBgs = new Image[MainGridSlots];
            _slotIcons = new Image[MainGridSlots];
            _slotCounts = new TextMeshProUGUI[MainGridSlots];
            _slotBorders = new Image[MainGridSlots];
            _slotDurabilityBars = new GameObject[MainGridSlots];
            _slotDurabilityFills = new Image[MainGridSlots];
            CompanionsPlugin.Log.LogDebug(
                $"[UI] BuildInventoryGrid — {GridCols}x{Mathf.CeilToInt(MainGridSlots / (float)GridCols)} grid, " +
                $"slotSize={slotSize}px, durability bars enabled");

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
                btn.navigation = new Navigation { mode = Navigation.Mode.None };
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

                // Durability bar — thin bar at slot bottom
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
            foodLabelRT.anchoredPosition = new Vector2(0f, bottomY + foodSlotSz + 2f);
            foodLabel.fontStyle = FontStyles.Bold;

            // Gold separator line between inventory grid and food section
            var sepGO = new GameObject("FoodSeparator", typeof(RectTransform), typeof(Image));
            sepGO.transform.SetParent(parent, false);
            var sepRT = sepGO.GetComponent<RectTransform>();
            sepRT.anchorMin = new Vector2(0.05f, 0f);
            sepRT.anchorMax = new Vector2(0.95f, 0f);
            sepRT.pivot = new Vector2(0.5f, 0f);
            sepRT.sizeDelta = new Vector2(0f, 1f);
            sepRT.anchoredPosition = new Vector2(0f, bottomY + foodSlotSz + 2f + 14f + 4f);
            sepGO.GetComponent<Image>().color = GoldColor;
            sepGO.GetComponent<Image>().raycastTarget = false;

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

        private Button BuildActionButton(RectTransform parent,
            ref float y, float h, float gap, string label, int mode)
        {
            var go = CreateTintedButton(parent, label, label);
            if (go == null) return null;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, h);
            rt.anchoredPosition = new Vector2(0f, y);
            y -= h + gap;

            var btn = go.GetComponent<Button>();
            if (btn != null)
                btn.onClick.AddListener(() => SetActionMode(mode));

            return btn;
        }

        private Button BuildUnwiredActionButton(RectTransform parent,
            ref float y, float h, float gap, string label)
        {
            var go = CreateTintedButton(parent, label, label);
            if (go == null) return null;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, h);
            rt.anchoredPosition = new Vector2(0f, y);
            y -= h + gap;

            return go.GetComponent<Button>();
        }

        private void BuildPreview(RectTransform col, TMP_FontAsset font)
        {
            float topY = -2f;

            // ── Name section ──
            var nameHeader = MakeText(col, "NameHeader", "Name",
                font, 11f, GoldColor, TextAlignmentOptions.Center);
            nameHeader.fontStyle = FontStyles.Bold;
            PlaceTopStretch(nameHeader.GetComponent<RectTransform>(), ref topY, 14f);
            topY -= 2f;
            topY = BuildNameInput(col, font, topY);
            topY -= 4f;

            // ── Actions section ──
            var actionsHeader = MakeText(col, "ActionsHeader", "Actions",
                font, 11f, GoldColor, TextAlignmentOptions.Center);
            actionsHeader.fontStyle = FontStyles.Bold;
            PlaceTopStretch(actionsHeader.GetComponent<RectTransform>(), ref topY, 14f);
            topY -= 2f;
            BuildActionControls(col, font, ref topY);
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
        // Food slots are display-only — no click handlers.
        // Active food effects + duration are shown via RefreshFoodSlots().
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
            int flatIndex = slotIndex;
            gx = flatIndex % GridCols;
            gy = flatIndex / GridCols;
            return true;
        }
        private void HandleSlotLeftClick(int gx, int gy, bool consumableOnly)
        {
            if (InventoryGui.instance == null)
            {
                CompanionsPlugin.Log.LogWarning("[UI] HandleSlotLeftClick ABORT — invGui is null");
                return;
            }
            var inv = GetStorageInventory();
            if (inv == null)
            {
                CompanionsPlugin.Log.LogWarning("[UI] HandleSlotLeftClick ABORT — companion inventory is null");
                return;
            }

            var dragItem = _dragItemField?.GetValue(InventoryGui.instance) as ItemDrop.ItemData;
            var dragInv  = _dragInventoryField?.GetValue(InventoryGui.instance) as Inventory;
            int dragAmt  = (_dragAmountField != null) ? (int)_dragAmountField.GetValue(InventoryGui.instance) : 1;
            bool changed = false;

            CompanionsPlugin.Log.LogDebug(
                $"[UI] HandleSlotLeftClick — slot=({gx},{gy}) consumableOnly={consumableOnly} " +
                $"dragItem=\"{dragItem?.m_shared?.m_name}\" dragInv={dragInv != null} dragAmt={dragAmt} " +
                $"invItems={inv.GetAllItems().Count}");

            if (dragItem != null && dragInv != null)
            {
                if (consumableOnly && !IsConsumableItem(dragItem))
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[UI] Food slot REJECTED drag — \"{dragItem.m_shared?.m_name}\" " +
                        $"type={dragItem.m_shared?.m_itemType} " +
                        $"food={dragItem.m_shared?.m_food} stam={dragItem.m_shared?.m_foodStamina} " +
                        $"eitr={dragItem.m_shared?.m_foodEitr} " +
                        $"slot=({gx},{gy}) consumableOnly={consumableOnly}");
                    MessageHud.instance?.ShowMessage(
                        MessageHud.MessageType.Center,
                        "Food slots only accept food items.");
                    return;
                }

                // Place dragged item into companion inventory
                try
                {
                    if (!dragInv.ContainsItem(dragItem))
                    {
                        ClearDrag();
                        return;
                    }
                }
                catch (System.NullReferenceException)
                {
                    ClearDrag();
                    return;
                }

                var targetItem = inv.GetItemAt(gx, gy);
                if (consumableOnly && targetItem != null && !IsConsumableItem(targetItem))
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[UI] Food slot REJECTED swap — target \"{targetItem.m_shared?.m_name}\" " +
                        $"is not food, slot=({gx},{gy})");
                    MessageHud.instance?.ShowMessage(
                        MessageHud.MessageType.Center,
                        "Food slots only accept food items.");
                    return;
                }

                if (targetItem != null && _companionHumanoid.IsItemEquiped(targetItem))
                    _companionHumanoid.UnequipItem(targetItem, false);

                // Keep player gear state in sync when dragging equipped gear.
                var player = Player.m_localPlayer;
                if (player != null && dragInv == player.GetInventory() && player.IsItemEquiped(dragItem))
                {
                    player.RemoveEquipAction(dragItem);
                    player.UnequipItem(dragItem, false);
                }

                bool moved = DropItemToCompanion(inv, dragInv, dragItem, dragAmt, gx, gy);
                CompanionsPlugin.Log.LogDebug(
                    $"[UI] DropItemToCompanion result={moved} item=\"{dragItem.m_shared?.m_name}\" → slot=({gx},{gy})");
                if (moved)
                {
                    ClearDrag();
                    changed = true;
                }
            }
            else
            {
                // No drag active — pick up item from companion inventory to start drag
                var item = inv.GetItemAt(gx, gy);
                if (item == null)
                {
                    CompanionsPlugin.Log.LogDebug($"[UI] No item at companion slot ({gx},{gy}) — nothing to pick up");
                    return;
                }

                CompanionsPlugin.Log.LogDebug(
                    $"[UI] Picking up \"{item.m_shared?.m_name}\" from companion({gx},{gy}) " +
                    $"stack={item.m_stack} — starting drag");

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
            if (targetInv == null || fromInv == null || dragItem == null)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[UI] DropItemToCompanion ABORT — targetInv={targetInv != null} " +
                    $"fromInv={fromInv != null} dragItem={dragItem != null}");
                return false;
            }
            amount = Mathf.Clamp(amount, 1, dragItem.m_stack);

            var targetItem = targetInv.GetItemAt(x, y);
            CompanionsPlugin.Log.LogDebug(
                $"[UI] DropItemToCompanion — \"{dragItem.m_shared?.m_name}\" amt={amount} " +
                $"→ slot=({x},{y}) targetSlotItem=\"{targetItem?.m_shared?.m_name}\" " +
                $"fromInv={fromInv.GetName()} targetInv={targetInv.GetName()}");

            // Mirror vanilla InventoryGrid.DropItem semantics so swap interactions
            // behave like base inventory drag/drop.
            if (ShouldSwapForDrop(dragItem, targetItem, amount))
            {
                CompanionsPlugin.Log.LogDebug("[UI] DropItemToCompanion — performing swap");
                Vector2i oldPos = dragItem.m_gridPos;
                fromInv.RemoveItem(dragItem);
                fromInv.MoveItemToThis(targetInv, targetItem, targetItem.m_stack, oldPos.x, oldPos.y);
                targetInv.MoveItemToThis(fromInv, dragItem, amount, x, y);
                return true;
            }

            bool result = targetInv.MoveItemToThis(fromInv, dragItem, amount, x, y);
            CompanionsPlugin.Log.LogDebug($"[UI] DropItemToCompanion — MoveItemToThis result={result}");
            return result;
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
            if (item == null)
            {
                CompanionsPlugin.Log.LogDebug($"[UI] HandleSlotRightClick — no item at ({gx},{gy})");
                return;
            }
            CompanionsPlugin.Log.LogDebug(
                $"[UI] HandleSlotRightClick — \"{item.m_shared?.m_name}\" at ({gx},{gy}) " +
                $"equipped={_companionHumanoid.IsItemEquiped(item)}");

            bool hadItem = inv.ContainsItem(item);
            int stackBefore = hadItem ? item.m_stack : 0;
            bool equippedBefore = _companionHumanoid.IsItemEquiped(item);
            bool changed = false;

            if (IsConsumableItem(item) && _companionFood != null)
            {
                changed = _companionFood.TryConsumeItem(item);
                CompanionsPlugin.Log.LogDebug(
                    $"[UI] Right-click food consume — \"{item.m_shared?.m_name}\" " +
                    $"slot=({gx},{gy}) success={changed}");
            }

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
            CompanionsPlugin.Log.LogDebug("[UI] ClearDrag called");
            if (InventoryGui.instance == null) return;
            _setupDragItem?.Invoke(InventoryGui.instance,
                new object[] { null, null, 1 });
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Action mode
        // ══════════════════════════════════════════════════════════════════════

        private void SetActionMode(int mode)
        {
            if (mode < CompanionSetup.ModeFollow || mode > CompanionSetup.ModeStay)
                mode = CompanionSetup.ModeFollow;

            // Block gathering modes when companion is overweight
            bool isGatherMode = mode >= CompanionSetup.ModeGatherWood
                             && mode <= CompanionSetup.ModeGatherOre;
            if (isGatherMode && _companionHarvest != null && _companionHarvest.IsOverweight())
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[UI] SetActionMode BLOCKED — companion is overweight, cannot gather");
                MessageHud.instance?.ShowMessage(
                    MessageHud.MessageType.Center,
                    "Companion is too heavy to gather. Unload their inventory first.");
                return;
            }

            EnsureCompanionOwnership();
            int oldMode = _activeMode;
            _activeMode = mode;

            string oldName = ModeName(oldMode);
            string newName = ModeName(mode);
            CompanionsPlugin.Log.LogDebug(
                $"[UI] SetActionMode: {oldName}({oldMode}) → {newName}({mode}) " +
                $"harvest={(_companionHarvest != null ? "attached" : "NULL")} " +
                $"ai={(_companionAI != null ? "attached" : "NULL")}");

            if (_companionNview != null && _companionNview.GetZDO() != null)
            {
                _companionNview.GetZDO().Set(CompanionSetup.ActionModeHash, mode);
                CompanionsPlugin.Log.LogDebug($"[UI]   ZDO ActionModeHash set to {mode}");
            }
            else
                CompanionsPlugin.Log.LogWarning("[UI]   ZDO is null — mode NOT persisted!");

            _companionHarvest?.NotifyActionModeChanged();
            ApplyActionMode();
            RefreshActionButtons();
            RefreshModeText();
        }

        private static string ModeName(int mode)
        {
            switch (mode)
            {
                case CompanionSetup.ModeFollow:      return "Follow";
                case CompanionSetup.ModeGatherWood:  return "GatherWood";
                case CompanionSetup.ModeGatherStone: return "GatherStone";
                case CompanionSetup.ModeGatherOre:   return "GatherOre";
                case CompanionSetup.ModeStay:        return "Stay";
                default:                             return "Unknown";
            }
        }

        private void ApplyActionMode()
        {
            if (_companionAI == null)
            {
                CompanionsPlugin.Log.LogWarning("[UI] ApplyActionMode — _companionAI is null!");
                return;
            }

            bool stayHome = _companion != null && _companion.GetStayHome() && _companion.HasHomePosition();

            switch (_activeMode)
            {
                case CompanionSetup.ModeFollow:
                case CompanionSetup.ModeGatherWood:
                case CompanionSetup.ModeGatherStone:
                case CompanionSetup.ModeGatherOre:
                    if (stayHome)
                    {
                        _companionAI.SetFollowTarget(null);
                        _companionAI.SetPatrolPointAt(_companion.GetHomePosition());
                        CompanionsPlugin.Log.LogDebug(
                            $"[UI] ApplyActionMode: StayHome patrol (mode={_activeMode})");
                    }
                    else if (Player.m_localPlayer != null)
                    {
                        _companionAI.SetFollowTarget(Player.m_localPlayer.gameObject);
                        CompanionsPlugin.Log.LogDebug(
                            $"[UI] ApplyActionMode: SetFollowTarget → player (mode={_activeMode})");
                    }
                    else
                        CompanionsPlugin.Log.LogWarning("[UI] ApplyActionMode — no local player!");
                    break;
                case CompanionSetup.ModeStay:
                    _companionAI.SetFollowTarget(null);
                    if (stayHome)
                        _companionAI.SetPatrolPointAt(_companion.GetHomePosition());
                    else
                        _companionAI.SetPatrolPoint();
                    CompanionsPlugin.Log.LogDebug("[UI] ApplyActionMode: Stay — cleared follow, set patrol");
                    break;
                default:
                    if (Player.m_localPlayer != null)
                        _companionAI.SetFollowTarget(Player.m_localPlayer.gameObject);
                    CompanionsPlugin.Log.LogDebug(
                        $"[UI] ApplyActionMode: default follow (mode={_activeMode})");
                    break;
            }
        }

        private void ToggleStayHome()
        {
            if (_companion == null || _companionAI == null) return;
            EnsureCompanionOwnership();

            bool current = _companion.GetStayHome();
            bool next = !current;
            _companion.SetStayHome(next);

            if (next)
            {
                // Auto-set home position if not already set
                if (!_companion.HasHomePosition())
                    _companion.SetHomePosition(_companion.transform.position);

                _companionAI.SetFollowTarget(null);
                _companionAI.SetPatrolPointAt(_companion.GetHomePosition());
                MessageHud.instance?.ShowMessage(
                    MessageHud.MessageType.Center,
                    "Companion will stay home.");
            }
            else
            {
                // Respect current action mode — gather modes should continue with player follow
                if (Player.m_localPlayer != null)
                    _companionAI.SetFollowTarget(Player.m_localPlayer.gameObject);
                // Re-apply current mode so gather modes restore follow correctly
                ApplyActionMode();
                MessageHud.instance?.ShowMessage(
                    MessageHud.MessageType.Center,
                    "Companion will follow you.");
            }

            RefreshStayHomeButton();
            CompanionsPlugin.Log.LogDebug(
                $"[UI] ToggleStayHome → {next} home={_companion.GetHomePosition():F1}");
        }

        private void OnSetHomeClicked()
        {
            if (_companionAI == null || _companion == null) return;
            EnsureCompanionOwnership();

            Vector3 pos = _companion.transform.position;
            _companion.SetHomePosition(pos);

            // If StayHome is active, update patrol to new home
            if (_companion.GetStayHome())
                _companionAI.SetPatrolPointAt(pos);

            MessageHud.instance?.ShowMessage(
                MessageHud.MessageType.Center,
                "Home point set.");
            CompanionsPlugin.Log.LogDebug(
                $"[UI] Set home point at {pos}");
        }

        private void RefreshActionButtons()
        {
            SetBtnHighlight(_followBtn,      _activeMode == CompanionSetup.ModeFollow);
            SetBtnHighlight(_gatherWoodBtn,  _activeMode == CompanionSetup.ModeGatherWood);
            SetBtnHighlight(_gatherStoneBtn, _activeMode == CompanionSetup.ModeGatherStone);
            SetBtnHighlight(_gatherOreBtn,   _activeMode == CompanionSetup.ModeGatherOre);
            RefreshStayHomeButton();
        }

        private void RefreshStayHomeButton()
        {
            if (_stayBtn == null || _companion == null) return;
            bool on = _companion.GetStayHome();
            var txt = _stayBtn.GetComponentInChildren<TMP_Text>(true);
            if (txt != null) txt.text = on ? "Stay Home: ON" : "Stay Home: OFF";
            SetBtnHighlight(_stayBtn, on);
        }

        // ── Combat Stance UI ─────────────────────────────────────────────────

        private Button BuildStanceButton(Transform parent, string label, int index,
            float btnW, float btnH, float gap, int stanceValue)
        {
            var go = CreateTintedButton(parent, $"Stance_{label}", label);
            if (go == null) return null;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.sizeDelta = new Vector2(btnW, 0f);
            rt.anchoredPosition = new Vector2(index * (btnW + gap), 0f);

            var btn = go.GetComponent<Button>();
            if (btn != null)
                btn.onClick.AddListener(() => SetCombatStance(stanceValue));
            return btn;
        }

        private void SetCombatStance(int stance)
        {
            if (stance < CompanionSetup.StanceBalanced || stance > CompanionSetup.StancePassive)
                stance = CompanionSetup.StanceBalanced;

            _activeStance = stance;
            _companion?.SetCombatStance(stance);
            RefreshStanceButtons();

            string[] names = { "Balanced", "Aggressive", "Defensive", "Passive" };
            CompanionsPlugin.Log.LogDebug($"[UI] SetCombatStance → {names[stance]}");
        }

        private void RefreshStanceButtons()
        {
            SetBtnHighlight(_balancedBtn,   _activeStance == CompanionSetup.StanceBalanced);
            SetBtnHighlight(_aggressiveBtn, _activeStance == CompanionSetup.StanceAggressive);
            SetBtnHighlight(_defensiveBtn,  _activeStance == CompanionSetup.StanceDefensive);
            SetBtnHighlight(_passiveBtn,    _activeStance == CompanionSetup.StancePassive);
        }

        private void SetBtnHighlight(Button btn, bool active)
        {
            if (btn == null) return;
            var img = btn.GetComponent<Image>();
            if (img != null)
                img.color = active ? GoldColor : Color.white;

            var txt = btn.GetComponentInChildren<TMP_Text>(true);
            if (txt != null)
                txt.color = active ? LabelText : Color.white;
        }

        private void RefreshModeText()
        {
            if (_modeText == null) return;
            string text;
            switch (_activeMode)
            {
                case CompanionSetup.ModeFollow:      text = "Follow into Battle"; break;
                case CompanionSetup.ModeGatherWood:  text = "Gather Wood"; break;
                case CompanionSetup.ModeGatherStone: text = "Gather Stone"; break;
                case CompanionSetup.ModeGatherOre:   text = "Gather Ore"; break;
                case CompanionSetup.ModeStay:        text = "Stay Home"; break;
                default:                             text = "Follow into Battle"; break;
            }
            if (_companion != null && _companion.GetStayHome() && _activeMode != CompanionSetup.ModeStay)
                text += " (Home)";
            _modeText.text = text;
        }

        // ── Auto pickup toggle ─────────────────────────────────────────────

        private void ToggleAutoPickup()
        {
            if (_companionNview == null || _companionNview.GetZDO() == null) return;
            EnsureCompanionOwnership();
            var zdo = _companionNview.GetZDO();
            bool current = zdo.GetBool(CompanionSetup.AutoPickupHash, true);
            zdo.Set(CompanionSetup.AutoPickupHash, !current);
            RefreshAutoPickupButton();
        }

        private void RefreshAutoPickupButton()
        {
            if (_autoPickupBtn == null) return;
            bool on = _companionNview != null && _companionNview.GetZDO() != null
                && _companionNview.GetZDO().GetBool(CompanionSetup.AutoPickupHash, true);
            var txt = _autoPickupBtn.GetComponentInChildren<TMP_Text>(true);
            if (txt != null) txt.text = on ? "Auto Pickup: ON" : "Auto Pickup: OFF";
            SetBtnHighlight(_autoPickupBtn, on);
        }

        // ── Commandable toggle ────────────────────────────────────────────

        private void ToggleCommandable()
        {
            if (_companion == null) return;
            EnsureCompanionOwnership();
            bool current = _companion.GetIsCommandable();
            _companion.SetIsCommandable(!current);
            RefreshCommandableButton();
        }

        private void RefreshCommandableButton()
        {
            if (_commandableBtn == null) return;
            bool on = _companion != null && _companion.GetIsCommandable();
            var txt = _commandableBtn.GetComponentInChildren<TMP_Text>(true);
            if (txt != null) txt.text = on ? "Command: ON" : "Command: OFF";
            SetBtnHighlight(_commandableBtn, on);
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

        /// <summary>
        /// Returns the inventory used for player-facing storage.
        /// For Player-clone companions: Humanoid inventory (shared with Container).
        /// For Dverger variants: Container's own inventory (Humanoid inventory holds
        /// fixed default gear and must not be exposed to the player).
        /// </summary>
        private Inventory GetStorageInventory()
        {
            if (_builtForDverger && _companionContainer != null)
                return _companionContainer.GetInventory();
            return _companionHumanoid?.GetInventory();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Stat bars
        // ══════════════════════════════════════════════════════════════════════

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

                // Durability bar
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
        //  Preview camera
        // ══════════════════════════════════════════════════════════════════════

        private Animator _cloneAnimator;
        private Animator _sourceAnimator;
        private AnimatorCullingMode _savedSourceCullingMode;
        private bool _sourceCullingOverridden;
        private Vector3 _previewCenter = PreviewPos + Vector3.up * 0.95f;
        private float _previewDistance = 2.8f;
        private float _previewBoundsTimer;
        private const float PreviewBoundsRefreshInterval = 0.5f;

        private void SetupPreviewClone()
        {
            ClearPreviewClone();
            if (_companion == null) return;

            ZNetView.m_forceDisableInit = true;
            try   { _clone = UnityEngine.Object.Instantiate(_companion.gameObject, PreviewPos, Quaternion.identity); }
            finally { ZNetView.m_forceDisableInit = false; }

            _clone.name = "HC_InteractPreviewClone";
            _clone.transform.position = PreviewPos;

            var rb = _clone.GetComponent<Rigidbody>();
            if (rb != null) UnityEngine.Object.Destroy(rb);

            // Disable all MonoBehaviours except VisEquipment
            foreach (var mb in _clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb is VisEquipment) continue;
                mb.enabled = false;
            }
            foreach (var renderer in _clone.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = true;

            // Re-enable and configure the Animator for live animation sync
            _cloneAnimator  = _clone.GetComponentInChildren<Animator>();
            _sourceAnimator = _companion.GetComponentInChildren<Animator>();
            if (_cloneAnimator != null)
            {
                _cloneAnimator.enabled    = true;
                _cloneAnimator.updateMode = AnimatorUpdateMode.Normal;
                _cloneAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _cloneAnimator.applyRootMotion = false;
                _cloneAnimator.Rebind();
            }
            OverrideSourceAnimatorCulling();

            int charLayer = LayerMask.NameToLayer("character");
            if (charLayer < 0) charLayer = 9;
            foreach (var t in _clone.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = charLayer;

            SetupLightRig();

            SyncPreviewEquipment();
            SyncPreviewAnimation();
            _previewBoundsTimer = 0f;
            RecalculatePreviewBounds();
            UpdatePreviewCamera();
            LockPreviewCloneFacing();
        }

        private void SyncPreviewAnimation()
        {
            EnsurePreviewAnimators();
            if (_cloneAnimator == null || _sourceAnimator == null) return;
            if (!_sourceAnimator.isInitialized) return;
            if (!_cloneAnimator.isInitialized) _cloneAnimator.Rebind();

            // Copy all animator parameters from live companion to clone
            for (int i = 0; i < _sourceAnimator.parameterCount; i++)
            {
                var param = _sourceAnimator.GetParameter(i);
                switch (param.type)
                {
                    case AnimatorControllerParameterType.Float:
                        _cloneAnimator.SetFloat(param.nameHash, _sourceAnimator.GetFloat(param.nameHash));
                        break;
                    case AnimatorControllerParameterType.Int:
                        _cloneAnimator.SetInteger(param.nameHash, _sourceAnimator.GetInteger(param.nameHash));
                        break;
                    case AnimatorControllerParameterType.Bool:
                        _cloneAnimator.SetBool(param.nameHash, _sourceAnimator.GetBool(param.nameHash));
                        break;
                }
            }

            _cloneAnimator.speed = _sourceAnimator.speed;

            // Sync all layers so upper-body actions (eat/equip/attack) stay live.
            int layers = Mathf.Min(_sourceAnimator.layerCount, _cloneAnimator.layerCount);
            for (int layer = 0; layer < layers; layer++)
            {
                _cloneAnimator.SetLayerWeight(layer, _sourceAnimator.GetLayerWeight(layer));
                var state = _sourceAnimator.GetCurrentAnimatorStateInfo(layer);
                _cloneAnimator.Play(state.fullPathHash, layer, state.normalizedTime);
            }
            _cloneAnimator.Update(0f);
        }

        private void EnsurePreviewAnimators()
        {
            if (_cloneAnimator == null && _clone != null)
            {
                _cloneAnimator = _clone.GetComponentInChildren<Animator>(true);
                if (_cloneAnimator != null)
                {
                    _cloneAnimator.enabled = true;
                    _cloneAnimator.updateMode = AnimatorUpdateMode.Normal;
                    _cloneAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    _cloneAnimator.applyRootMotion = false;
                }
            }

            if (_sourceAnimator == null && _companion != null)
            {
                _sourceAnimator = _companion.GetComponentInChildren<Animator>(true);
                OverrideSourceAnimatorCulling();
            }
        }

        private void OverrideSourceAnimatorCulling()
        {
            if (_sourceCullingOverridden || _sourceAnimator == null) return;
            _savedSourceCullingMode = _sourceAnimator.cullingMode;
            _sourceAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _sourceCullingOverridden = true;
        }

        private void RestoreSourceAnimatorCulling()
        {
            if (!_sourceCullingOverridden) return;
            if (_sourceAnimator != null)
                _sourceAnimator.cullingMode = _savedSourceCullingMode;
            _sourceCullingOverridden = false;
        }

        private void ClearPreviewClone()
        {
            RestoreSourceAnimatorCulling();
            if (_lightRig != null) { UnityEngine.Object.Destroy(_lightRig); _lightRig = null; }
            if (_clone    != null) { UnityEngine.Object.Destroy(_clone);    _clone    = null; }
            _cloneAnimator  = null;
            _sourceAnimator = null;
            _previewCenter = PreviewPos + Vector3.up * 0.95f;
            _previewDistance = 2.8f;
            _previewBoundsTimer = 0f;
        }

        private void SetupLightRig()
        {
            if (_lightRig != null) UnityEngine.Object.Destroy(_lightRig);
            _lightRig = new GameObject("HC_InteractPreviewLightRig");
            UnityEngine.Object.DontDestroyOnLoad(_lightRig);
            _lightRig.transform.position = PreviewPos;

            int charLayer = LayerMask.NameToLayer("character");
            if (charLayer < 0) charLayer = 9;
            int charNet   = LayerMask.NameToLayer("character_net");
            int lightMask = 1 << charLayer;
            if (charNet >= 0) lightMask |= 1 << charNet;

            AddLight(_lightRig.transform, "Key",    new Vector3( 1.5f,  2.5f,  3.5f), 2.0f, new Color(1f, 0.92f, 0.82f), 15f, lightMask);
            AddLight(_lightRig.transform, "Fill",   new Vector3(-2.5f,  1.5f,  3.0f), 1.2f, new Color(0.9f, 0.92f, 1f),  15f, lightMask);
            AddLight(_lightRig.transform, "Rim",    new Vector3( 0.0f,  3.0f, -2.5f), 1.2f, new Color(0.95f, 0.88f, 0.78f), 15f, lightMask);
            AddLight(_lightRig.transform, "Bottom", new Vector3( 0.0f, -0.5f,  3.0f), 0.5f, new Color(0.85f, 0.82f, 0.78f), 10f, lightMask);
        }

        private static void AddLight(Transform parent, string name,
            Vector3 pos, float intensity, Color color, float range, int mask)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var lt        = go.AddComponent<Light>();
            lt.type       = LightType.Point;
            lt.intensity  = intensity;
            lt.color      = color;
            lt.range      = range;
            lt.shadows    = LightShadows.None;
            lt.cullingMask = mask;
        }

        private void SyncPreviewEquipment()
        {
            if (_clone == null || _companionHumanoid == null) return;

            var cloneVE = _clone.GetComponent<VisEquipment>();
            var realVE  = _companionHumanoid.GetComponent<VisEquipment>();
            if (cloneVE == null || realVE == null) return;

            CopyField(_visRightItemHash,    realVE, cloneVE);
            CopyField(_visLeftItemHash,     realVE, cloneVE);
            CopyField(_visChestItemHash,    realVE, cloneVE);
            CopyField(_visLegItemHash,      realVE, cloneVE);
            CopyField(_visHelmetItemHash,   realVE, cloneVE);
            CopyField(_visShoulderItemHash, realVE, cloneVE);
            CopyField(_visUtilityItemHash,  realVE, cloneVE);

            _updateVisuals?.Invoke(cloneVE, null);
        }

        private static void CopyField(FieldInfo fi, VisEquipment src, VisEquipment dst)
        {
            if (fi != null) fi.SetValue(dst, fi.GetValue(src));
        }

        private void LockPreviewCloneFacing()
        {
            if (_clone == null) return;

            _clone.transform.position = PreviewPos;
            _clone.transform.rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
        }

        private void UpdatePreviewCamera()
        {
            if (_camGO == null) return;
            Vector3 offset = PreviewCameraOffsetDir * _previewDistance;
            _camGO.transform.position = _previewCenter + offset;
            _camGO.transform.LookAt(_previewCenter);
        }

        private void UpdatePreviewBounds()
        {
            if (_clone == null || _cam == null) return;
            _previewBoundsTimer -= Time.deltaTime;
            if (_previewBoundsTimer > 0f) return;
            _previewBoundsTimer = PreviewBoundsRefreshInterval;
            RecalculatePreviewBounds();
        }

        private void RecalculatePreviewBounds()
        {
            if (_clone == null || _cam == null) return;

            bool hasBounds = false;
            Bounds bounds = default;
            foreach (var renderer in _clone.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds)
            {
                _previewCenter = PreviewPos + Vector3.up * 0.95f;
                _previewDistance = 2.8f;
                return;
            }

            _previewCenter = bounds.center + Vector3.up * (bounds.extents.y * 0.05f);
            float radius = Mathf.Max(0.5f, bounds.extents.magnitude * 0.95f);
            float aspect = (_rt != null && _rt.height > 0) ? (float)_rt.width / _rt.height : 1f;
            float halfFovV = _cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
            float halfFovH = Mathf.Atan(Mathf.Tan(halfFovV) * aspect);
            float limitingHalfFov = Mathf.Min(halfFovV, halfFovH);
            float safeHalfFov = Mathf.Max(0.2f, limitingHalfFov);

            _previewDistance = Mathf.Clamp(radius / Mathf.Sin(safeHalfFov), 1.6f, 4.8f);
            _cam.farClipPlane = Mathf.Max(10f, _previewDistance + 8f);
        }

        private void RenderPreview()
        {
            if (_cam == null || _clone == null) return;
            _savedAmbient          = RenderSettings.ambientLight;
            _savedAmbientIntensity = RenderSettings.ambientIntensity;
            _savedAmbientMode      = RenderSettings.ambientMode;
            try
            {
                RenderSettings.ambientMode      = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight     = new Color(0.45f, 0.4f, 0.35f);
                RenderSettings.ambientIntensity = 1.2f;
                _cam.Render();
            }
            finally
            {
                RenderSettings.ambientMode      = _savedAmbientMode;
                RenderSettings.ambientLight     = _savedAmbient;
                RenderSettings.ambientIntensity = _savedAmbientIntensity;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Teardown
        // ══════════════════════════════════════════════════════════════════════

        private void Teardown()
        {
            ClearPreviewClone();
            if (_camGO != null) { UnityEngine.Object.Destroy(_camGO); _camGO = null; _cam = null; }
            if (_rt    != null) { _rt.Release(); UnityEngine.Object.Destroy(_rt); _rt = null; }
            if (_root  != null) { UnityEngine.Object.Destroy(_root); _root = null; }
            if (_buttonTemplate != null) { UnityEngine.Object.Destroy(_buttonTemplate); _buttonTemplate = null; }
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
                var font = fonts[i];
                if (IsBrokenTmpFont(font)) continue;
                string name = font.name.ToLowerInvariant();
                if (name.Contains("averia") || name.Contains("norse") || name.Contains("valheim"))
                    return font;
                if (best == null) best = font;
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

        private static void PlaceTopStretch(RectTransform rt, ref float y, float h)
        {
            rt.anchorMin        = new Vector2(0f, 1f);
            rt.anchorMax        = new Vector2(1f, 1f);
            rt.pivot            = new Vector2(0.5f, 1f);
            rt.sizeDelta        = new Vector2(0f, h);
            rt.anchoredPosition = new Vector2(0f, y);
            y -= h;
        }

        private static void MakeSeparator(RectTransform parent, ref float y)
        {
            var sep = new GameObject("Sep", typeof(RectTransform), typeof(Image));
            sep.transform.SetParent(parent, false);
            var sepRT = sep.GetComponent<RectTransform>();
            sepRT.anchorMin        = new Vector2(0.05f, 1f);
            sepRT.anchorMax        = new Vector2(0.95f, 1f);
            sepRT.pivot            = new Vector2(0.5f, 1f);
            sepRT.sizeDelta        = new Vector2(0f, 2f);
            sepRT.anchoredPosition = new Vector2(0f, y);
            sep.GetComponent<Image>().color = GoldColor;
            y -= 2f;
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




