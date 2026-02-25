using System;
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
    /// a companion's Container is opened.  Two-panel layout:
    /// Left = tabbed (Inventory / Actions), Right = camera preview.
    /// Vanilla container panel is hidden; our grid handles item transfer.
    /// </summary>
    public class CompanionInteractPanel : MonoBehaviour
    {
        public static CompanionInteractPanel Instance { get; private set; }

        // ── Public state ─────────────────────────────────────────────────────
        public bool IsVisible => _visible;

        // ── Layout constants ─────────────────────────────────────────────────
        private const float PanelW       = 620f;
        private const float PanelH       = 480f;
        private const float UiScale      = 1.25f;
        private const float OuterPad     = 6f;
        private const float ColGap       = 4f;
        private const float LeftColW     = 300f;
        private const float RightColW    = 300f;
        private const float TabH         = 34f;
        private const float TabTopGap    = 6f;

        // ── Grid constants ───────────────────────────────────────────────────
        private const int GridCols          = 5;
        private const int GridSlots         = 25;
        private const int FoodSlotCount     = 3;
        private const int MainGridSlots     = 20;
        private const float SlotSize        = 52f;
        private const float InventorySlotGap = 3f;

        // ── Style constants ──────────────────────────────────────────────────
        private static readonly Color PanelBg        = new Color(0.18f, 0.14f, 0.09f, 0.92f);
        private static readonly Color ColBg          = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color GoldColor      = new Color(0.83f, 0.64f, 0.31f, 1f);
        private static readonly Color GoldTextColor  = new Color(0.83f, 0.52f, 0.18f, 1f);
        private static readonly Color LabelText      = new Color(1f, 0.9f, 0.5f, 1f);
        private static readonly Color HealthRed      = new Color(0.48f, 0.08f, 0.08f, 1f);
        private static readonly Color StaminaYellow  = new Color(0.48f, 0.40f, 0.08f, 1f);
        private static readonly Color BarBg          = new Color(0.15f, 0.15f, 0.15f, 0.85f);
        private static readonly Color BtnTint        = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color SlotTint       = new Color(0f, 0f, 0f, 0.5625f);

        // ── Custom sprite caches ─────────────────────────────────────────────
        private static Sprite _panelBgSprite;
        private static Sprite _sliderBgSprite;
        private static Sprite _healthBarGradientSprite;
        private static Sprite _staminaBarGradientSprite;
        private static Texture2D _healthBarGradientTex;
        private static Texture2D _staminaBarGradientTex;

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
        private RawImage       _previewImg;

        private float _rotation;
        private bool  _dragging;
        private float _lastMouseX;
        private const float AutoRotSpeed    = 15f;
        private const float DragSensitivity = 0.4f;

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
        private Humanoid         _companionHumanoid;
        private ZNetView         _companionNview;
        private MonsterAI        _companionAI;

        // ── UI elements ──────────────────────────────────────────────────────
        private GameObject      _root;
        private TMP_InputField  _nameInput;
        private Image           _healthFill;
        private TextMeshProUGUI _healthText;
        private Image           _staminaFill;
        private TextMeshProUGUI _staminaText;
        private TextMeshProUGUI _modeText;
        private Button          _followBtn;
        private Button          _collectBtn;
        private Button          _stayBtn;
        private int             _activeMode;

        private bool _built;
        private bool _visible;

        // ── Inventory grid ───────────────────────────────────────────────────
        private Image[]           _slotBgs;
        private Image[]           _slotIcons;
        private TextMeshProUGUI[] _slotCounts;
        private Image[]           _slotBorders;
        private Image[]           _foodSlotIcons;
        private TextMeshProUGUI[] _foodSlotCounts;
        private float             _invRefreshTimer;

        // ── Tab system ───────────────────────────────────────────────────────
        private GameObject _invContent;
        private GameObject _actionContent;
        private Image      _invTabTint;
        private Image      _actionTabTint;
        private TextMeshProUGUI _invTabText;
        private TextMeshProUGUI _actionTabText;
        private int        _activeTab;

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()  { Instance = this; }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
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

            UpdateBars();
            RefreshInventoryGrid();
            UpdatePreviewRotation();
            UpdatePreviewCamera();
            RenderPreview();
            SyncPreviewEquipment();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════════

        public void Show(CompanionSetup companion)
        {
            _companion         = companion;
            _companionChar     = companion.GetComponent<Character>();
            _companionStamina  = companion.GetComponent<CompanionStamina>();
            _companionHumanoid = companion.GetComponent<Humanoid>();
            _companionNview    = companion.GetComponent<ZNetView>();
            _companionAI       = companion.GetComponent<MonsterAI>();

            // Rebuild if destroyed (scene change) or never built
            if (_root == null) _built = false;
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

            _activeMode = 0;
            if (_companionNview != null && _companionNview.GetZDO() != null)
                _activeMode = _companionNview.GetZDO().GetInt(CompanionSetup.ActionModeHash, 0);

            SetupPreviewClone();
            RefreshActionButtons();
            RefreshModeText();
            _invRefreshTimer = 0f;
            SwitchTab(0);

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

            ClearPreviewClone();
            _companion         = null;
            _companionChar     = null;
            _companionStamina  = null;
            _companionHumanoid = null;
            _companionNview    = null;
            _companionAI       = null;
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

        private static Sprite BuildBarGradientSprite(
            ref Sprite cacheSprite, ref Texture2D cacheTexture,
            Color leftColor, Color rightColor, string name)
        {
            if (cacheSprite != null) return cacheSprite;

            const int w = 64;
            const int h = 8;
            cacheTexture = new Texture2D(w, h, TextureFormat.RGBA32, false);
            cacheTexture.wrapMode   = TextureWrapMode.Clamp;
            cacheTexture.filterMode = FilterMode.Bilinear;

            for (int y = 0; y < h; y++)
            {
                float v = h <= 1 ? 0f : y / (float)(h - 1);
                float vertical = Mathf.Lerp(1.06f, 0.84f, v);
                for (int x = 0; x < w; x++)
                {
                    float t = w <= 1 ? 0f : x / (float)(w - 1);
                    var c = Color.Lerp(leftColor, rightColor, t) * vertical;
                    c.a = 1f;
                    cacheTexture.SetPixel(x, y, c);
                }
            }
            cacheTexture.Apply();

            cacheSprite = Sprite.Create(
                cacheTexture,
                new Rect(0f, 0f, w, h),
                new Vector2(0.5f, 0.5f),
                100f);
            cacheSprite.name = name;
            return cacheSprite;
        }

        private static Sprite GetHealthBarFillSprite()
        {
            return BuildBarGradientSprite(
                ref _healthBarGradientSprite, ref _healthBarGradientTex,
                new Color(0.52f, 0.11f, 0.11f, 1f),
                new Color(0.27f, 0.05f, 0.05f, 1f),
                "CIP_HealthBarGradient");
        }

        private static Sprite GetStaminaBarFillSprite()
        {
            return BuildBarGradientSprite(
                ref _staminaBarGradientSprite, ref _staminaBarGradientTex,
                new Color(0.52f, 0.43f, 0.10f, 1f),
                new Color(0.30f, 0.24f, 0.05f, 1f),
                "CIP_StaminaBarGradient");
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
                btn.targetGraphic = go.GetComponent<Image>();
            }

            // Tint overlay
            var tintGO = new GameObject("Tint", typeof(RectTransform), typeof(Image));
            tintGO.transform.SetParent(go.transform, false);
            tintGO.transform.SetAsFirstSibling();
            var tintRT = tintGO.GetComponent<RectTransform>();
            tintRT.anchorMin = Vector2.zero;
            tintRT.anchorMax = Vector2.one;
            tintRT.offsetMin = Vector2.zero;
            tintRT.offsetMax = Vector2.zero;
            tintGO.GetComponent<Image>().color         = BtnTint;
            tintGO.GetComponent<Image>().raycastTarget = false;

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

            var rootRT = _root.GetComponent<RectTransform>();
            rootRT.anchorMin        = new Vector2(0.5f, 0.5f);
            rootRT.anchorMax        = new Vector2(0.5f, 0.5f);
            rootRT.pivot            = new Vector2(0.5f, 0.5f);
            rootRT.sizeDelta        = new Vector2(PanelW, PanelH);
            rootRT.anchoredPosition = new Vector2(-30f, 0f);
            rootRT.localScale       = Vector3.one * UiScale;

            var rootImg = _root.AddComponent<Image>();
            ApplyPanelBg(rootImg, PanelBg);
            rootImg.raycastTarget = true;

            // Tab bar — full panel width, above both columns
            BuildTabBar(rootRT, font);

            // Both columns pushed down for tab bar
            float tabInset = TabTopGap + TabH + 4f;
            float rightX   = OuterPad + LeftColW + ColGap;
            var leftCol  = CreateColumn(_root.transform, "LeftCol", OuterPad, OuterPad + LeftColW);
            leftCol.offsetMax = new Vector2(leftCol.offsetMax.x, -tabInset);
            var rightCol = CreateColumn(_root.transform, "RightCol", rightX, rightX + RightColW);
            rightCol.offsetMax = new Vector2(rightCol.offsetMax.x, -tabInset);

            // Left column: tabbed content (tabs are at root level above)
            BuildLeftPanel(leftCol, font);

            // Right column: camera preview
            BuildPreview(rightCol);

            _root.SetActive(false);
            _built = true;
        }

        private RectTransform CreateColumn(Transform parent, string name, float xLeft, float xRight)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot     = new Vector2(0f, 0.5f);
            rt.offsetMin = new Vector2(xLeft, OuterPad);
            rt.offsetMax = new Vector2(xRight, -OuterPad);
            var colImg = go.GetComponent<Image>();
            colImg.sprite = null;
            colImg.color  = ColBg;
            colImg.raycastTarget = false;
            return rt;
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

        // ── Left panel: content only (tabs are at root level) ────────────

        private void BuildLeftPanel(RectTransform col, TMP_FontAsset font)
        {
            var pad = MakePadded(col, 10f);

            // Inventory content — fills entire column
            _invContent = new GameObject("InvContent", typeof(RectTransform));
            _invContent.transform.SetParent(pad, false);
            StretchFill(_invContent.GetComponent<RectTransform>());
            BuildInventoryContent(_invContent.GetComponent<RectTransform>(), font);

            // Actions content — same position, toggled by tabs
            _actionContent = new GameObject("ActionContent", typeof(RectTransform));
            _actionContent.transform.SetParent(pad, false);
            StretchFill(_actionContent.GetComponent<RectTransform>());
            BuildActionsContent(_actionContent.GetComponent<RectTransform>(), font);

            _actionContent.SetActive(false);
        }

        // ── Tab bar: positioned above left column, children of root ─────

        private void BuildTabBar(RectTransform root, TMP_FontAsset font)
        {
            const float tabWidthTrim = 8f;
            float leftTabW    = Mathf.Max(100f, LeftColW - tabWidthTrim);
            float rightTabW   = Mathf.Max(100f, RightColW - tabWidthTrim);
            float leftCenterX = OuterPad + LeftColW * 0.5f;
            float rightX      = OuterPad + LeftColW + ColGap;
            float rightCenterX = rightX + RightColW * 0.5f;

            // Inventory tab — tinted craft-button style, above left column
            var invTabGO = CreateTintedButton(root, "TabInventory", "Inventory");
            if (invTabGO != null)
            {
                var rt = invTabGO.GetComponent<RectTransform>();
                rt.anchorMin        = new Vector2(0f, 1f);
                rt.anchorMax        = new Vector2(0f, 1f);
                rt.pivot            = new Vector2(0.5f, 1f);
                rt.sizeDelta        = new Vector2(leftTabW, TabH);
                rt.anchoredPosition = new Vector2(leftCenterX, -TabTopGap);

                var btn = invTabGO.GetComponent<Button>();
                if (btn != null) btn.onClick.AddListener(() => SwitchTab(0));

                var tint = invTabGO.transform.Find("Tint");
                _invTabTint = tint != null ? tint.GetComponent<Image>() : null;

                _invTabText = invTabGO.GetComponentInChildren<TMP_Text>(true) as TextMeshProUGUI;
                if (_invTabText != null)
                {
                    _invTabText.fontSize  = 14f;
                    _invTabText.fontStyle = FontStyles.Bold;
                    _invTabText.alignment = TextAlignmentOptions.Center;
                    _invTabText.color     = LabelText;
                }
            }

            // Actions tab
            var actTabGO = CreateTintedButton(root, "TabActions", "Actions");
            if (actTabGO != null)
            {
                var rt = actTabGO.GetComponent<RectTransform>();
                rt.anchorMin        = new Vector2(0f, 1f);
                rt.anchorMax        = new Vector2(0f, 1f);
                rt.pivot            = new Vector2(0.5f, 1f);
                rt.sizeDelta        = new Vector2(rightTabW, TabH);
                rt.anchoredPosition = new Vector2(rightCenterX, -TabTopGap);

                var btn = actTabGO.GetComponent<Button>();
                if (btn != null) btn.onClick.AddListener(() => SwitchTab(1));

                var tint = actTabGO.transform.Find("Tint");
                _actionTabTint = tint != null ? tint.GetComponent<Image>() : null;

                _actionTabText = actTabGO.GetComponentInChildren<TMP_Text>(true) as TextMeshProUGUI;
                if (_actionTabText != null)
                {
                    _actionTabText.fontSize  = 14f;
                    _actionTabText.fontStyle = FontStyles.Bold;
                    _actionTabText.alignment = TextAlignmentOptions.Center;
                    _actionTabText.color     = new Color(0.7f, 0.7f, 0.7f, 1f);
                }
            }
        }

        private void SwitchTab(int tab)
        {
            _activeTab = tab;
            if (_invContent != null)     _invContent.SetActive(tab == 0);
            if (_actionContent != null)  _actionContent.SetActive(tab == 1);

            // Active tab = lighter tint overlay, inactive = heavier tint
            if (_invTabTint != null)
                _invTabTint.color = tab == 0 ? new Color(0f, 0f, 0f, 0.4f) : BtnTint;
            if (_actionTabTint != null)
                _actionTabTint.color = tab == 1 ? new Color(0f, 0f, 0f, 0.4f) : BtnTint;
            if (_invTabText != null)
                _invTabText.color = tab == 0 ? LabelText : new Color(0.7f, 0.7f, 0.7f, 1f);
            if (_actionTabText != null)
                _actionTabText.color = tab == 1 ? LabelText : new Color(0.7f, 0.7f, 0.7f, 1f);
        }

        // ── Inventory tab content ──────────────────────────────────────────

        private void BuildInventoryContent(RectTransform parent, TMP_FontAsset font)
        {
            float y = 0f;

            // Health
            var hpLabel = MakeText(parent, "HealthLabel", "Health",
                font, 12f, LabelText, TextAlignmentOptions.MidlineLeft);
            hpLabel.fontStyle = FontStyles.Bold;
            PlaceTopStretch(hpLabel.GetComponent<RectTransform>(), ref y, 14f);
            y -= 2f;
            BuildStatBar(parent, font, ref y, GetHealthBarFillSprite(), HealthRed, out _healthFill, out _healthText);
            y -= 6f;

            // Stamina
            var stLabel = MakeText(parent, "StaminaLabel", "Stamina",
                font, 12f, LabelText, TextAlignmentOptions.MidlineLeft);
            stLabel.fontStyle = FontStyles.Bold;
            PlaceTopStretch(stLabel.GetComponent<RectTransform>(), ref y, 14f);
            y -= 2f;
            BuildStatBar(parent, font, ref y, GetStaminaBarFillSprite(), StaminaYellow, out _staminaFill, out _staminaText);
            y -= 6f;

            // Food quick slots
            BuildFoodSlots(parent, font, ref y);
            y -= 8f;

            // Separator
            MakeSeparator(parent, ref y);
            y -= 6f;

            // Inventory grid
            BuildInventoryGrid(parent, font, ref y);
        }

        private void BuildFoodSlots(RectTransform parent, TMP_FontAsset font, ref float y)
        {
            var foodLabel = MakeText(parent, "FoodLabel", "Food",
                font, 12f, LabelText, TextAlignmentOptions.MidlineLeft);
            foodLabel.fontStyle = FontStyles.Bold;
            PlaceTopStretch(foodLabel.GetComponent<RectTransform>(), ref y, 14f);
            y -= 2f;

            _foodSlotIcons  = new Image[FoodSlotCount];
            _foodSlotCounts = new TextMeshProUGUI[FoodSlotCount];

            float rowW = FoodSlotCount * SlotSize + (FoodSlotCount - 1) * InventorySlotGap;

            var rowContainer = new GameObject("FoodSlots", typeof(RectTransform));
            rowContainer.transform.SetParent(parent, false);
            var rowRT = rowContainer.GetComponent<RectTransform>();
            rowRT.anchorMin        = new Vector2(0.5f, 1f);
            rowRT.anchorMax        = new Vector2(0.5f, 1f);
            rowRT.pivot            = new Vector2(0.5f, 1f);
            rowRT.sizeDelta        = new Vector2(rowW, SlotSize);
            rowRT.anchoredPosition = new Vector2(0f, y);

            for (int i = 0; i < FoodSlotCount; i++)
            {
                float x = i * (SlotSize + InventorySlotGap);

                var slotGO = new GameObject($"FoodSlot{i}", typeof(RectTransform), typeof(Image));
                slotGO.transform.SetParent(rowContainer.transform, false);
                var slotRT = slotGO.GetComponent<RectTransform>();
                slotRT.anchorMin        = new Vector2(0f, 1f);
                slotRT.anchorMax        = new Vector2(0f, 1f);
                slotRT.pivot            = new Vector2(0f, 1f);
                slotRT.sizeDelta        = new Vector2(SlotSize, SlotSize);
                slotRT.anchoredPosition = new Vector2(x, 0f);

                var bgImg = slotGO.GetComponent<Image>();
                bgImg.sprite = null;
                bgImg.color  = SlotTint;

                var btn = slotGO.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.navigation = new Navigation { mode = Navigation.Mode.None };
                int slotIndex = i;
                btn.onClick.AddListener(() => OnFoodSlotLeftClick(slotIndex));

                var trigger = slotGO.AddComponent<EventTrigger>();
                var rightEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                rightEntry.callback.AddListener((data) =>
                {
                    var pData = (PointerEventData)data;
                    if (pData.button == PointerEventData.InputButton.Right)
                        OnFoodSlotRightClick(slotIndex);
                });
                trigger.triggers.Add(rightEntry);

                var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconGO.transform.SetParent(slotGO.transform, false);
                var iconRT = iconGO.GetComponent<RectTransform>();
                iconRT.anchorMin = Vector2.zero;
                iconRT.anchorMax = Vector2.one;
                iconRT.offsetMin = new Vector2(4f, 4f);
                iconRT.offsetMax = new Vector2(-4f, -4f);
                _foodSlotIcons[i] = iconGO.GetComponent<Image>();
                _foodSlotIcons[i].preserveAspect = true;
                _foodSlotIcons[i].raycastTarget  = false;
                _foodSlotIcons[i].enabled = false;

                var countGO = new GameObject("Count", typeof(RectTransform));
                countGO.transform.SetParent(slotGO.transform, false);
                var countTmp = countGO.AddComponent<TextMeshProUGUI>();
                if (font != null) countTmp.font = font;
                countTmp.fontSize      = 11f;
                countTmp.color         = Color.white;
                countTmp.alignment     = TextAlignmentOptions.BottomRight;
                countTmp.raycastTarget = false;
                var countRT = countGO.GetComponent<RectTransform>();
                countRT.anchorMin = Vector2.zero;
                countRT.anchorMax = Vector2.one;
                countRT.offsetMin = new Vector2(2f, 1f);
                countRT.offsetMax = new Vector2(-2f, -1f);
                _foodSlotCounts[i] = countTmp;
                _foodSlotCounts[i].enabled = false;
            }

            y -= SlotSize;
        }

        // ── Actions tab content ────────────────────────────────────────────

        private void BuildActionsContent(RectTransform parent, TMP_FontAsset font)
        {
            float y = 0f;

            // Name input
            var nameLabel = MakeText(parent, "NameLabel", "Name",
                font, 13f, LabelText, TextAlignmentOptions.MidlineLeft);
            nameLabel.fontStyle = FontStyles.Bold;
            PlaceTopStretch(nameLabel.GetComponent<RectTransform>(), ref y, 18f);
            y -= 4f;
            y = BuildNameInput(parent, font, y);
            y -= 12f;

            // Separator
            MakeSeparator(parent, ref y);
            y -= 8f;

            // Action buttons
            float btnH = 32f;
            float btnGap = 4f;
            _followBtn  = BuildActionButton(parent, ref y, btnH, btnGap, "Follow into Battle", 0);
            _collectBtn = BuildActionButton(parent, ref y, btnH, btnGap, "Collect Resources", 1);
            _stayBtn    = BuildActionButton(parent, ref y, btnH, btnGap, "Stay Home", 2);

            // Mode text
            y -= 4f;
            _modeText = MakeText(parent, "ModeText", "Follow into Battle",
                font, 12f, GoldColor, TextAlignmentOptions.Center);
            PlaceTopStretch(_modeText.GetComponent<RectTransform>(), ref y, 16f);

            // Resource gather buttons (visual only for now)
            y -= 8f;
            BuildUnwiredActionButton(parent, ref y, btnH, btnGap, "Gather Wood");
            BuildUnwiredActionButton(parent, ref y, btnH, btnGap, "Gather Stone");
            BuildUnwiredActionButton(parent, ref y, btnH, btnGap, "Gather Ore");
        }

        // ── Name input ──────────────────────────────────────────────────────

        private float BuildNameInput(RectTransform parent, TMP_FontAsset font, float y)
        {
            float h = 30f;

            var inputGO = new GameObject("NameInput", typeof(RectTransform), typeof(Image));
            inputGO.transform.SetParent(parent, false);
            var inputRT = inputGO.GetComponent<RectTransform>();
            inputRT.anchorMin        = new Vector2(0f, 1f);
            inputRT.anchorMax        = new Vector2(1f, 1f);
            inputRT.pivot            = new Vector2(0f, 1f);
            inputRT.sizeDelta        = new Vector2(0f, h);
            inputRT.anchoredPosition = new Vector2(0f, y);

            var bgSprite = GetSliderBgSprite();
            var bgImg = inputGO.GetComponent<Image>();
            if (bgSprite != null)
            {
                bgImg.sprite = bgSprite;
                bgImg.type   = Image.Type.Simple;
                bgImg.color  = Color.white;
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
            childTmp.fontSize      = 16f;
            childTmp.color         = Color.white;
            childTmp.alignment     = TextAlignmentOptions.MidlineLeft;
            childTmp.raycastTarget = false;
            StretchFill(childTextGO.GetComponent<RectTransform>());

            _nameInput = inputGO.AddComponent<TMP_InputField>();
            _nameInput.textComponent  = childTmp;
            _nameInput.textViewport   = textAreaRT;
            _nameInput.characterLimit = 24;
            _nameInput.contentType    = TMP_InputField.ContentType.Standard;
            _nameInput.onFocusSelectAll = false;

            var phGO = new GameObject("Placeholder", typeof(RectTransform));
            phGO.transform.SetParent(textAreaGO.transform, false);
            var phTmp = phGO.AddComponent<TextMeshProUGUI>();
            if (font != null) phTmp.font = font;
            phTmp.fontSize      = 16f;
            phTmp.color         = new Color(0.6f, 0.6f, 0.6f, 0.6f);
            phTmp.alignment     = TextAlignmentOptions.MidlineLeft;
            phTmp.text          = "Enter name...";
            phTmp.fontStyle     = FontStyles.Italic;
            phTmp.raycastTarget = false;
            StretchFill(phGO.GetComponent<RectTransform>());
            _nameInput.placeholder = phTmp;

            return y - h;
        }

        // ── Stat bars ───────────────────────────────────────────────────────

        private void BuildStatBar(RectTransform parent, TMP_FontAsset font, ref float y,
            Sprite fillSprite, Color fillColor, out Image fill, out TextMeshProUGUI text)
        {
            float barH = 20f;

            var barGO = new GameObject("Bar", typeof(RectTransform), typeof(Image));
            barGO.transform.SetParent(parent, false);
            var barRT = barGO.GetComponent<RectTransform>();
            barRT.anchorMin        = new Vector2(0.03f, 1f);
            barRT.anchorMax        = new Vector2(0.97f, 1f);
            barRT.pivot            = new Vector2(0.5f, 1f);
            barRT.sizeDelta        = new Vector2(0f, barH);
            barRT.anchoredPosition = new Vector2(0f, y);

            var bgSprite = GetSliderBgSprite();
            var bgImg = barGO.GetComponent<Image>();
            if (bgSprite != null)
            {
                bgImg.sprite = bgSprite;
                bgImg.type   = Image.Type.Simple;
                bgImg.color  = Color.white;
            }
            else
            {
                bgImg.color = BarBg;
            }

            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(barGO.transform, false);
            var fillRT = fillGO.GetComponent<RectTransform>();
            fillRT.anchorMin = Vector2.zero;
            fillRT.anchorMax = new Vector2(1f, 1f);
            fillRT.offsetMin = new Vector2(2f, 2f);
            fillRT.offsetMax = new Vector2(-2f, -2f);
            fill = fillGO.GetComponent<Image>();
            if (fillSprite != null)
            {
                fill.sprite = fillSprite;
                fill.type = Image.Type.Simple;
                fill.color = Color.white;
            }
            else
            {
                fill.color = fillColor;
            }

            var valTmp = MakeText(barGO.transform, "Value", "0 / 0",
                font, 11f, Color.white, TextAlignmentOptions.Center);
            StretchFill(valTmp.GetComponent<RectTransform>());
            text = valTmp;

            y -= barH;
        }

        // ── Inventory grid ──────────────────────────────────────────────────

        private void BuildInventoryGrid(RectTransform parent, TMP_FontAsset font, ref float y)
        {
            _slotBgs     = new Image[MainGridSlots];
            _slotIcons   = new Image[MainGridSlots];
            _slotCounts  = new TextMeshProUGUI[MainGridSlots];
            _slotBorders = new Image[MainGridSlots];

            int   gridRows = Mathf.CeilToInt(MainGridSlots / (float)GridCols);
            float gridW = GridCols * SlotSize + (GridCols - 1) * InventorySlotGap;
            float gridH = gridRows * SlotSize + (gridRows - 1) * InventorySlotGap;

            // Center the grid horizontally
            var gridContainer = new GameObject("Grid", typeof(RectTransform));
            gridContainer.transform.SetParent(parent, false);
            var gridRT = gridContainer.GetComponent<RectTransform>();
            gridRT.anchorMin        = new Vector2(0.5f, 1f);
            gridRT.anchorMax        = new Vector2(0.5f, 1f);
            gridRT.pivot            = new Vector2(0.5f, 1f);
            gridRT.sizeDelta        = new Vector2(gridW, gridH);
            gridRT.anchoredPosition = new Vector2(0f, y);

            for (int i = 0; i < MainGridSlots; i++)
            {
                int visualCol = i % GridCols;
                int visualRow = i / GridCols;
                float x = visualCol * (SlotSize + InventorySlotGap);
                float sy = -(visualRow * (SlotSize + InventorySlotGap));

                var slotGO = new GameObject($"Slot{i}", typeof(RectTransform), typeof(Image));
                slotGO.transform.SetParent(gridContainer.transform, false);
                var slotRT = slotGO.GetComponent<RectTransform>();
                slotRT.anchorMin        = new Vector2(0f, 1f);
                slotRT.anchorMax        = new Vector2(0f, 1f);
                slotRT.pivot            = new Vector2(0f, 1f);
                slotRT.sizeDelta        = new Vector2(SlotSize, SlotSize);
                slotRT.anchoredPosition = new Vector2(x, sy);

                var bgImg = slotGO.GetComponent<Image>();
                bgImg.sprite = null;
                bgImg.color  = SlotTint;
                _slotBgs[i]  = bgImg;

                // Click handler via Button component
                var btn = slotGO.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.navigation = new Navigation { mode = Navigation.Mode.None };
                int slotIndex = i;
                btn.onClick.AddListener(() => OnSlotLeftClick(slotIndex));

                // Right-click via EventTrigger
                var trigger = slotGO.AddComponent<EventTrigger>();
                var rightEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
                rightEntry.callback.AddListener((data) =>
                {
                    var pData = (PointerEventData)data;
                    if (pData.button == PointerEventData.InputButton.Right)
                        OnSlotRightClick(slotIndex);
                });
                trigger.triggers.Add(rightEntry);

                // Equipped border (gold)
                var borderGO = new GameObject("Border", typeof(RectTransform), typeof(Image));
                borderGO.transform.SetParent(slotGO.transform, false);
                var borderRT = borderGO.GetComponent<RectTransform>();
                borderRT.anchorMin = Vector2.zero;
                borderRT.anchorMax = Vector2.one;
                borderRT.offsetMin = new Vector2(1f, 1f);
                borderRT.offsetMax = new Vector2(-1f, -1f);
                _slotBorders[i] = borderGO.GetComponent<Image>();
                _slotBorders[i].color = GoldColor;
                _slotBorders[i].raycastTarget = false;
                _slotBorders[i].enabled = false;

                // Item icon
                var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconGO.transform.SetParent(slotGO.transform, false);
                var iconRT = iconGO.GetComponent<RectTransform>();
                iconRT.anchorMin = Vector2.zero;
                iconRT.anchorMax = Vector2.one;
                iconRT.offsetMin = new Vector2(4f, 4f);
                iconRT.offsetMax = new Vector2(-4f, -4f);
                _slotIcons[i] = iconGO.GetComponent<Image>();
                _slotIcons[i].preserveAspect = true;
                _slotIcons[i].raycastTarget  = false;
                _slotIcons[i].enabled = false;

                // Stack count
                var countGO = new GameObject("Count", typeof(RectTransform));
                countGO.transform.SetParent(slotGO.transform, false);
                var countTmp = countGO.AddComponent<TextMeshProUGUI>();
                if (font != null) countTmp.font = font;
                countTmp.fontSize      = 11f;
                countTmp.color         = Color.white;
                countTmp.alignment     = TextAlignmentOptions.BottomRight;
                countTmp.raycastTarget = false;
                var countRT = countGO.GetComponent<RectTransform>();
                countRT.anchorMin = Vector2.zero;
                countRT.anchorMax = Vector2.one;
                countRT.offsetMin = new Vector2(2f, 1f);
                countRT.offsetMax = new Vector2(-2f, -1f);
                _slotCounts[i] = countTmp;
                _slotCounts[i].enabled = false;
            }

            y -= gridH;
        }

        // ── Action buttons ──────────────────────────────────────────────────

        private Button BuildActionButton(RectTransform parent,
            ref float y, float h, float gap, string label, int mode)
        {
            var go = CreateTintedButton(parent, label, label);
            if (go == null) return null;

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin        = new Vector2(0f, 1f);
            rt.anchorMax        = new Vector2(1f, 1f);
            rt.pivot            = new Vector2(0.5f, 1f);
            rt.sizeDelta        = new Vector2(0f, h);
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
            rt.anchorMin        = new Vector2(0f, 1f);
            rt.anchorMax        = new Vector2(1f, 1f);
            rt.pivot            = new Vector2(0.5f, 1f);
            rt.sizeDelta        = new Vector2(0f, h);
            rt.anchoredPosition = new Vector2(0f, y);
            y -= h + gap;

            return go.GetComponent<Button>();
        }

        // ── Preview ─────────────────────────────────────────────────────────

        private void BuildPreview(RectTransform col)
        {
            var imgGO = new GameObject("PreviewImg", typeof(RectTransform));
            imgGO.transform.SetParent(col, false);
            StretchFill(imgGO.GetComponent<RectTransform>());

            _previewImg               = imgGO.AddComponent<RawImage>();
            _previewImg.color         = Color.white;
            _previewImg.raycastTarget = true;

            float colW = RightColW;
            float colH = PanelH - OuterPad * 2f;
            int rtScale = 4;
            int rtW = Mathf.Max(64, Mathf.RoundToInt(colW) * rtScale);
            int rtH = Mathf.Max(64, Mathf.RoundToInt(colH) * rtScale);
            _rt              = new RenderTexture(rtW, rtH, 24, RenderTextureFormat.ARGB32);
            _rt.antiAliasing = 4;
            _rt.filterMode   = FilterMode.Trilinear;
            _previewImg.texture = _rt;

            _camGO = new GameObject("HC_InteractPreviewCam");
            UnityEngine.Object.DontDestroyOnLoad(_camGO);
            _cam               = _camGO.AddComponent<Camera>();
            _cam.targetTexture = _rt;
            _cam.clearFlags    = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            _cam.fieldOfView   = 30f;
            _cam.nearClipPlane = 0.1f;
            _cam.farClipPlane  = 10f;
            _cam.depth         = -2;
            _cam.enabled       = false;

            int charLayer = LayerMask.NameToLayer("character");
            if (charLayer < 0) charLayer = 9;
            int charNet = LayerMask.NameToLayer("character_net");
            int mask    = 1 << charLayer;
            if (charNet >= 0) mask |= 1 << charNet;
            _cam.cullingMask = mask;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Inventory grid — click handlers
        // ══════════════════════════════════════════════════════════════════════

        private void OnSlotLeftClick(int slotIndex)
        {
            if (!TryGetMainGridCoord(slotIndex, out int gx, out int gy)) return;
            HandleSlotLeftClick(gx, gy, consumableOnly: false);
        }
        private void OnSlotRightClick(int slotIndex)
        {
            if (!TryGetMainGridCoord(slotIndex, out int gx, out int gy)) return;
            HandleSlotRightClick(gx, gy);
        }
        private void OnFoodSlotLeftClick(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= FoodSlotCount) return;
            HandleSlotLeftClick(slotIndex, 0, consumableOnly: true);
        }
        private void OnFoodSlotRightClick(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= FoodSlotCount) return;
            HandleSlotRightClick(slotIndex, 0);
        }
        private static bool IsConsumableItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable;
        }
        private bool TryGetMainGridCoord(int slotIndex, out int gx, out int gy)
        {
            gx = 0;
            gy = 0;
            if (slotIndex < 0 || slotIndex >= MainGridSlots) return false;
            int flatIndex = slotIndex + FoodSlotCount;
            gx = flatIndex % GridCols;
            gy = flatIndex / GridCols;
            return true;
        }
        private void HandleSlotLeftClick(int gx, int gy, bool consumableOnly)
        {
            if (_companionHumanoid == null || InventoryGui.instance == null) return;
            var inv = _companionHumanoid.GetInventory();
            if (inv == null) return;
            var dragItem = _dragItemField?.GetValue(InventoryGui.instance) as ItemDrop.ItemData;
            var dragInv  = _dragInventoryField?.GetValue(InventoryGui.instance) as Inventory;
            int dragAmt  = (_dragAmountField != null) ? (int)_dragAmountField.GetValue(InventoryGui.instance) : 1;
            if (dragItem != null && dragInv != null)
            {
                if (consumableOnly && !IsConsumableItem(dragItem))
                {
                    MessageHud.instance?.ShowMessage(
                        MessageHud.MessageType.Center,
                        "Food slots only accept consumables.");
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
                bool moved = inv.MoveItemToThis(dragInv, dragItem, dragAmt, gx, gy);
                if (moved) ClearDrag();
            }
            else
            {
                // Click item in companion inventory -> move to player inventory
                var item = inv.GetItemAt(gx, gy);
                if (item == null) return;
                if (Player.m_localPlayer != null)
                {
                    var playerInv = Player.m_localPlayer.GetInventory();
                    if (playerInv != null)
                        playerInv.MoveItemToThis(inv, item);
                }
            }
            _invRefreshTimer = 0f;
        }
        private void HandleSlotRightClick(int gx, int gy)
        {
            if (_companionHumanoid == null || Player.m_localPlayer == null) return;
            var inv = _companionHumanoid.GetInventory();
            if (inv == null) return;
            var item = inv.GetItemAt(gx, gy);
            if (item == null) return;
            Player.m_localPlayer.UseItem(inv, item, true);
            _invRefreshTimer = 0f;
        }
        private void ClearDrag()
        {
            if (InventoryGui.instance == null) return;
            _setupDragItem?.Invoke(InventoryGui.instance,
                new object[] { null, null, 1 });
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Action mode
        // ══════════════════════════════════════════════════════════════════════

        private void SetActionMode(int mode)
        {
            _activeMode = mode;
            if (_companionNview != null && _companionNview.GetZDO() != null)
                _companionNview.GetZDO().Set(CompanionSetup.ActionModeHash, mode);

            ApplyActionMode();
            RefreshActionButtons();
            RefreshModeText();
        }

        private void ApplyActionMode()
        {
            if (_companionAI == null || Player.m_localPlayer == null) return;

            switch (_activeMode)
            {
                case 0:
                    _companionAI.SetFollowTarget(Player.m_localPlayer.gameObject);
                    break;
                case 1:
                    _companionAI.SetFollowTarget(Player.m_localPlayer.gameObject);
                    break;
                case 2:
                    _companionAI.SetFollowTarget(null);
                    _companionAI.SetPatrolPoint();
                    break;
            }
        }

        private void RefreshActionButtons()
        {
            SetBtnHighlight(_followBtn,  _activeMode == 0);
            SetBtnHighlight(_collectBtn, _activeMode == 1);
            SetBtnHighlight(_stayBtn,    _activeMode == 2);
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
            switch (_activeMode)
            {
                case 0: _modeText.text = "Follow into Battle"; break;
                case 1: _modeText.text = "Collect Resources";  break;
                case 2: _modeText.text = "Stay Home";          break;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Name change
        // ══════════════════════════════════════════════════════════════════════

        private void OnNameChanged(string newName)
        {
            if (_companionNview == null || _companionNview.GetZDO() == null) return;
            _companionNview.GetZDO().Set(CompanionSetup.NameHash, newName);
            if (_companionChar != null)
                _companionChar.m_name = string.IsNullOrEmpty(newName) ? "Companion" : newName;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Stat bars
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateBars()
        {
            if (_companionChar != null && _healthFill != null)
            {
                float hp    = _companionChar.GetHealth();
                float maxHp = _companionChar.GetMaxHealth();
                float pct   = maxHp > 0f ? hp / maxHp : 0f;
                _healthFill.GetComponent<RectTransform>().anchorMax = new Vector2(pct, 1f);
                if (_healthText != null)
                    _healthText.text = $"{Mathf.CeilToInt(hp)} / {Mathf.CeilToInt(maxHp)}";
            }

            if (_companionStamina != null && _staminaFill != null)
            {
                float pct = _companionStamina.GetStaminaPercentage();
                float cur = _companionStamina.Stamina;
                float max = _companionStamina.MaxStamina;
                _staminaFill.GetComponent<RectTransform>().anchorMax = new Vector2(pct, 1f);
                if (_staminaText != null)
                    _staminaText.text = $"{Mathf.CeilToInt(cur)} / {Mathf.CeilToInt(max)}";
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Inventory grid refresh
        // ══════════════════════════════════════════════════════════════════════

        private void RefreshInventoryGrid()
        {
            if (_slotIcons == null || _companionHumanoid == null) return;

            _invRefreshTimer -= Time.deltaTime;
            if (_invRefreshTimer > 0f) return;
            _invRefreshTimer = 0.25f;

            var inv = _companionHumanoid.GetInventory();
            if (inv == null) return;

            for (int i = 0; i < MainGridSlots; i++)
            {
                _slotIcons[i].enabled   = false;
                _slotCounts[i].enabled  = false;
                _slotBorders[i].enabled = false;
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

                if (flatIndex < FoodSlotCount)
                {
                    if (_foodSlotIcons == null || _foodSlotCounts == null) continue;

                    _foodSlotIcons[flatIndex].sprite  = item.GetIcon();
                    _foodSlotIcons[flatIndex].enabled = true;
                    if (item.m_stack > 1)
                    {
                        _foodSlotCounts[flatIndex].text    = item.m_stack.ToString();
                        _foodSlotCounts[flatIndex].enabled = true;
                    }
                    continue;
                }

                int idx = flatIndex - FoodSlotCount;
                if (idx < 0 || idx >= MainGridSlots) continue;

                _slotIcons[idx].sprite  = item.GetIcon();
                _slotIcons[idx].enabled = true;

                if (item.m_stack > 1)
                {
                    _slotCounts[idx].text    = item.m_stack.ToString();
                    _slotCounts[idx].enabled = true;
                }

                if (_companionHumanoid.IsItemEquiped(item))
                    _slotBorders[idx].enabled = true;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Preview camera
        // ══════════════════════════════════════════════════════════════════════

        private void SetupPreviewClone()
        {
            ClearPreviewClone();
            if (_companion == null) return;

            ZNetView.m_forceDisableInit = true;
            try   { _clone = UnityEngine.Object.Instantiate(_companion.gameObject, PreviewPos, Quaternion.identity); }
            finally { ZNetView.m_forceDisableInit = false; }

            _clone.name = "HC_InteractPreviewClone";

            var rb = _clone.GetComponent<Rigidbody>();
            if (rb != null) UnityEngine.Object.Destroy(rb);
            foreach (var mb in _clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb is VisEquipment) continue;
                mb.enabled = false;
            }
            foreach (var anim in _clone.GetComponentsInChildren<Animator>())
                anim.updateMode = AnimatorUpdateMode.Normal;

            int charLayer = LayerMask.NameToLayer("character");
            if (charLayer < 0) charLayer = 9;
            foreach (var t in _clone.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = charLayer;

            SetupLightRig();

            Vector3 center = PreviewPos + Vector3.up * 0.9f;
            _camGO.transform.position = center + new Vector3(0f, 0.3f, 5.0f);
            _camGO.transform.LookAt(center);
        }

        private void ClearPreviewClone()
        {
            if (_lightRig != null) { UnityEngine.Object.Destroy(_lightRig); _lightRig = null; }
            if (_clone    != null) { UnityEngine.Object.Destroy(_clone);    _clone    = null; }
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

        private void UpdatePreviewRotation()
        {
            if (_previewImg == null || !_previewImg.gameObject.activeInHierarchy) return;

            if (!_dragging && Input.GetMouseButtonDown(0))
            {
                if (RectTransformUtility.RectangleContainsScreenPoint(
                        _previewImg.rectTransform, Input.mousePosition, null))
                {
                    _dragging   = true;
                    _lastMouseX = Input.mousePosition.x;
                }
            }

            if (_dragging)
            {
                if (Input.GetMouseButton(0))
                {
                    float delta = Input.mousePosition.x - _lastMouseX;
                    _rotation   = (_rotation + delta * DragSensitivity) % 360f;
                    _lastMouseX = Input.mousePosition.x;
                }
                else _dragging = false;
            }
            else
            {
                _rotation = (_rotation + AutoRotSpeed * Time.deltaTime) % 360f;
            }
        }

        private void UpdatePreviewCamera()
        {
            if (_camGO == null) return;
            Vector3 center = PreviewPos + Vector3.up * 0.9f;
            float   rad    = _rotation * Mathf.Deg2Rad;
            Vector3 offset = new Vector3(Mathf.Sin(rad), 0.3f, Mathf.Cos(rad)) * 5.0f;
            _camGO.transform.position = center + offset;
            _camGO.transform.LookAt(center);
        }

        private void RenderPreview()
        {
            if (_cam == null) return;
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
            if (_healthBarGradientSprite != null) { UnityEngine.Object.Destroy(_healthBarGradientSprite); _healthBarGradientSprite = null; }
            if (_staminaBarGradientSprite != null) { UnityEngine.Object.Destroy(_staminaBarGradientSprite); _staminaBarGradientSprite = null; }
            if (_healthBarGradientTex != null) { UnityEngine.Object.Destroy(_healthBarGradientTex); _healthBarGradientTex = null; }
            if (_staminaBarGradientTex != null) { UnityEngine.Object.Destroy(_staminaBarGradientTex); _staminaBarGradientTex = null; }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private static TMP_FontAsset GetFont()
        {
            if (InventoryGui.instance == null) return null;
            var tmp = InventoryGui.instance.GetComponentInChildren<TextMeshProUGUI>(true);
            return tmp != null ? tmp.font : null;
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

