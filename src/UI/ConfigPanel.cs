using System.Collections.Generic;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Companions
{
    /// <summary>
    /// In-game configuration panel toggled by F8 (configurable).
    /// Reads categories and entries from <see cref="ModConfig"/> and builds
    /// tabbed UI with sliders (float/int) and toggle buttons (bool).
    /// Supports full gamepad/controller navigation.
    /// </summary>
    public class ConfigPanel : MonoBehaviour
    {
        public static ConfigPanel Instance { get; private set; }
        public bool IsVisible => _visible;

        // ── UI state ──────────────────────────────────────────────────────
        private bool _visible;
        private Canvas _canvas;
        private GameObject _root;
        private GameObject _backdrop;

        // ── Tabs & content ────────────────────────────────────────────────
        private string _activeCategory;
        private int _activeCategoryIndex;
        private readonly Dictionary<string, Button> _tabButtons = new Dictionary<string, Button>();
        private ScrollRect _scrollRect;
        private RectTransform _contentRT;
        private ScrollRect _tabScrollRect;
        private RectTransform _tabContentRT;
        private TMP_FontAsset _font;

        // ── Live entry rows ───────────────────────────────────────────────
        private readonly List<GameObject> _entryRows = new List<GameObject>();
        private readonly List<ConfigEntryBase> _entryData = new List<ConfigEntryBase>();
        private readonly List<Slider> _entrySliders = new List<Slider>();
        private readonly List<System.Action> _entryResetActions = new List<System.Action>();

        // ── Controller navigation ────────────────────────────────────────
        private int _selectedRowIndex = -1;
        private float _dpadRepeatTimer;
        private float _dpadHRepeatTimer;
        private const float DpadInitialDelay = 0.35f;
        private const float DpadRepeatRate = 0.08f;
        private const float SliderStep = 0.02f;  // fraction of range per d-pad press

        // ── Style ─────────────────────────────────────────────────────────
        private static readonly Color GoldColor     = new Color(0.83f, 0.64f, 0.31f, 1f);
        private static readonly Color GoldTextColor = new Color(0.83f, 0.52f, 0.18f, 1f);
        private static readonly Color LabelText     = new Color(1f, 0.9f, 0.5f, 1f);
        private static readonly Color TabInactive   = new Color(0.45f, 0.45f, 0.45f, 1f);
        private static readonly Color ColBg         = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color RowBg         = new Color(0.08f, 0.08f, 0.10f, 0.6f);
        private static readonly Color RowBgSelected = new Color(0.18f, 0.15f, 0.08f, 0.8f);
        private static readonly Color DefaultBtnColor = new Color(0.35f, 0.35f, 0.38f, 1f);

        // ── Layout constants ──────────────────────────────────────────────
        private const float PanelW = 700f;
        private const float PanelH = 500f;
        private const float TabW   = 150f;
        private const float Pad    = 8f;
        private const float RowH   = 30f;
        private const float RowGap = 2f;
        private const float TitleH = 32f;

        // ── Cached sprites from embedded textures ────────────────────────────
        private static Sprite _panelBgSprite;
        private static Sprite _catBgSprite;
        private static Sprite _sliderBgSprite;
        private static Sprite _btnBgSprite;

        private static Sprite GetPanelBgSprite()
        {
            if (_panelBgSprite != null) return _panelBgSprite;
            var tex = TextureLoader.LoadUITexture("PanelBackground");
            if (tex == null) return null;
            _panelBgSprite = Sprite.Create(tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            return _panelBgSprite;
        }

        private static Sprite GetCatBgSprite()
        {
            if (_catBgSprite != null) return _catBgSprite;
            var tex = TextureLoader.LoadUITexture("CategoryBackground");
            if (tex == null) return null;
            _catBgSprite = Sprite.Create(tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(12f, 12f, 12f, 12f));
            return _catBgSprite;
        }

        private static Sprite GetSliderBgSprite()
        {
            if (_sliderBgSprite != null) return _sliderBgSprite;
            var tex = TextureLoader.LoadUITexture("SliderBackground");
            if (tex == null) return null;
            _sliderBgSprite = Sprite.Create(tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            return _sliderBgSprite;
        }

        private static Sprite GetBtnBgSprite()
        {
            if (_btnBgSprite != null) return _btnBgSprite;
            var tex = TextureLoader.LoadUITexture("ButtonBackground");
            if (tex == null) return null;
            _btnBgSprite = Sprite.Create(tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            return _btnBgSprite;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════

        public static void Create()
        {
            if (Instance != null) return;
            var go = new GameObject("HC_ConfigPanel");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<ConfigPanel>();
        }

        public void Show()
        {
            if (_root == null) Build();

            // Re-resolve font if it was null or broken at build time
            if (IsBrokenTmpFont(_font))
            {
                var newFont = ResolveFont();
                if (newFont != null && !IsBrokenTmpFont(newFont))
                {
                    _font = newFont;
                    // Apply to all existing TMP_Text components
                    var texts = _root.GetComponentsInChildren<TextMeshProUGUI>(true);
                    for (int i = 0; i < texts.Length; i++)
                    {
                        if (texts[i] != null) texts[i].font = _font;
                    }
                }
            }

            _backdrop.SetActive(true);
            _root.SetActive(true);
            _visible = true;
            _selectedRowIndex = -1;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void Hide()
        {
            _visible = false;
            _selectedRowIndex = -1;
            if (_root != null) _root.SetActive(false);
            if (_backdrop != null) _backdrop.SetActive(false);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Update()
        {
            // Toggle with config key
            if (Input.GetKeyDown(ModConfig.ConfigPanelKey.Value))
            {
                if (_visible) Hide();
                else Show();
            }

            if (!_visible) return;

            // Close on Escape or gamepad B
            if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyBack") ||
                ZInput.GetButtonDown("JoyButtonB"))
            {
                Hide();
                return;
            }

            // Keep cursor free
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // ── Controller navigation ────────────────────────────────────
            UpdateControllerInput();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            DestroySprite(ref _panelBgSprite);
            DestroySprite(ref _catBgSprite);
            DestroySprite(ref _sliderBgSprite);
            DestroySprite(ref _btnBgSprite);
        }

        private static void DestroySprite(ref Sprite sprite)
        {
            if (sprite == null) return;
            var tex = sprite.texture;
            Object.Destroy(sprite);
            if (tex != null) Object.Destroy(tex);
            sprite = null;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Controller Input
        // ══════════════════════════════════════════════════════════════════

        private void UpdateControllerInput()
        {
            if (!ZInput.IsGamepadActive()) return;

            float dt = Time.unscaledDeltaTime;

            // ── Tab switching with LB/RB ──
            if (ZInput.GetButtonDown("JoyTabLeft"))
            {
                int newIdx = _activeCategoryIndex - 1;
                if (newIdx < 0) newIdx = ModConfig.CategoryOrder.Count - 1;
                SelectCategory(ModConfig.CategoryOrder[newIdx]);
            }
            if (ZInput.GetButtonDown("JoyTabRight"))
            {
                int newIdx = _activeCategoryIndex + 1;
                if (newIdx >= ModConfig.CategoryOrder.Count) newIdx = 0;
                SelectCategory(ModConfig.CategoryOrder[newIdx]);
            }

            // ── Row navigation with D-pad / left stick vertical ──
            float vAxis = ZInput.GetJoyLeftStickY();
            bool dpadUp = ZInput.GetButtonDown("JoyDPadUp");
            bool dpadDown = ZInput.GetButtonDown("JoyDPadDown");
            bool vUp = dpadUp || vAxis > 0.5f;
            bool vDown = dpadDown || vAxis < -0.5f;

            if (vUp || vDown)
            {
                if (_dpadRepeatTimer <= 0f || dpadUp || dpadDown)
                {
                    if (_dpadRepeatTimer <= 0f)
                    {
                        int dir = vUp ? -1 : 1;
                        int newRow = _selectedRowIndex + dir;
                        if (newRow < 0) newRow = _entryRows.Count - 1;
                        if (newRow >= _entryRows.Count) newRow = 0;
                        SetSelectedRow(newRow);
                        _dpadRepeatTimer = (_selectedRowIndex < 0) ? DpadInitialDelay : DpadInitialDelay;
                    }
                    else if (dpadUp || dpadDown)
                    {
                        // D-pad button press — immediate
                        int dir = vUp ? -1 : 1;
                        int newRow = _selectedRowIndex + dir;
                        if (newRow < 0) newRow = _entryRows.Count - 1;
                        if (newRow >= _entryRows.Count) newRow = 0;
                        SetSelectedRow(newRow);
                        _dpadRepeatTimer = DpadInitialDelay;
                    }
                }
                _dpadRepeatTimer -= dt;
            }
            else
            {
                _dpadRepeatTimer = 0f;
            }

            // ── Value adjustment with D-pad Left/Right ──
            if (_selectedRowIndex >= 0 && _selectedRowIndex < _entryData.Count)
            {
                float hAxis = ZInput.GetJoyLeftStickX();
                bool dpadLeft = ZInput.GetButtonDown("JoyDPadLeft");
                bool dpadRight = ZInput.GetButtonDown("JoyDPadRight");
                bool hLeft = dpadLeft || hAxis < -0.5f;
                bool hRight = dpadRight || hAxis > 0.5f;

                if (hLeft || hRight)
                {
                    if (_dpadHRepeatTimer <= 0f || dpadLeft || dpadRight)
                    {
                        if (_dpadHRepeatTimer <= 0f || dpadLeft || dpadRight)
                        {
                            AdjustSelectedValue(hRight ? 1 : -1);
                            _dpadHRepeatTimer = dpadLeft || dpadRight ? DpadInitialDelay : DpadRepeatRate;
                        }
                    }
                    _dpadHRepeatTimer -= dt;
                }
                else
                {
                    _dpadHRepeatTimer = 0f;
                }

                // A button: toggle bool / start keybind listening
                if (ZInput.GetButtonDown("JoyButtonA"))
                {
                    var entry = _entryData[_selectedRowIndex];
                    if (entry.SettingType == typeof(bool))
                        AdjustSelectedValue(1); // toggles
                }

                // Y button: reset to default
                if (ZInput.GetButtonDown("JoyButtonY"))
                {
                    if (_selectedRowIndex < _entryResetActions.Count)
                        _entryResetActions[_selectedRowIndex]?.Invoke();
                }
            }

            // ── Scroll with right stick ──
            float scrollInput = ZInput.GetJoyRightStickY();
            if (Mathf.Abs(scrollInput) > 0.1f && _scrollRect != null)
                _scrollRect.verticalNormalizedPosition =
                    Mathf.Clamp01(_scrollRect.verticalNormalizedPosition + scrollInput * dt * 2f);
        }

        private void AdjustSelectedValue(int direction)
        {
            if (_selectedRowIndex < 0 || _selectedRowIndex >= _entryData.Count) return;
            var entry = _entryData[_selectedRowIndex];

            if (entry.SettingType == typeof(float))
            {
                var typed = (ConfigEntry<float>)entry;
                float min = 0f, max = 100f;
                if (entry.Description?.AcceptableValues is AcceptableValueRange<float> range)
                { min = range.MinValue; max = range.MaxValue; }
                float step = (max - min) * SliderStep;
                typed.Value = Mathf.Clamp(Mathf.Round((typed.Value + step * direction) * 100f) / 100f, min, max);
                // Update slider UI
                if (_selectedRowIndex < _entrySliders.Count && _entrySliders[_selectedRowIndex] != null)
                    _entrySliders[_selectedRowIndex].value = typed.Value;
            }
            else if (entry.SettingType == typeof(int))
            {
                var typed = (ConfigEntry<int>)entry;
                int min = 0, max = 100;
                if (entry.Description?.AcceptableValues is AcceptableValueRange<int> range)
                { min = range.MinValue; max = range.MaxValue; }
                int step = Mathf.Max(1, (max - min) / 50);
                typed.Value = Mathf.Clamp(typed.Value + step * direction, min, max);
                if (_selectedRowIndex < _entrySliders.Count && _entrySliders[_selectedRowIndex] != null)
                    _entrySliders[_selectedRowIndex].value = typed.Value;
            }
            else if (entry.SettingType == typeof(bool))
            {
                var typed = (ConfigEntry<bool>)entry;
                typed.Value = !typed.Value;
                // UI updates via the SettingChanged event in the button listener
                RefreshCategory();
            }
        }

        private void SetSelectedRow(int index)
        {
            // Unhighlight old row
            if (_selectedRowIndex >= 0 && _selectedRowIndex < _entryRows.Count)
            {
                var oldImg = _entryRows[_selectedRowIndex].GetComponent<Image>();
                if (oldImg != null) oldImg.color = RowBg;
            }

            _selectedRowIndex = index;

            // Highlight new row
            if (_selectedRowIndex >= 0 && _selectedRowIndex < _entryRows.Count)
            {
                var newImg = _entryRows[_selectedRowIndex].GetComponent<Image>();
                if (newImg != null) newImg.color = RowBgSelected;

                // Auto-scroll to keep selected row visible
                ScrollToRow(_selectedRowIndex);
            }
        }

        private void ScrollToRow(int index)
        {
            if (_scrollRect == null || _contentRT == null || index < 0 || index >= _entryRows.Count)
                return;

            var rowRT = _entryRows[index].GetComponent<RectTransform>();
            float contentHeight = _contentRT.sizeDelta.y;
            float viewportHeight = _scrollRect.GetComponent<RectTransform>().rect.height;
            if (contentHeight <= viewportHeight) return;

            float rowTop = -rowRT.anchoredPosition.y;
            float rowBottom = rowTop + RowH;
            float scrollableHeight = contentHeight - viewportHeight;
            float currentScroll = (1f - _scrollRect.verticalNormalizedPosition) * scrollableHeight;

            if (rowTop < currentScroll)
                _scrollRect.verticalNormalizedPosition = 1f - (rowTop / scrollableHeight);
            else if (rowBottom > currentScroll + viewportHeight)
                _scrollRect.verticalNormalizedPosition = 1f - ((rowBottom - viewportHeight) / scrollableHeight);
        }

        private void ScrollToTab(int index)
        {
            if (_tabScrollRect == null || _tabContentRT == null || index < 0) return;

            var viewportRT = _tabScrollRect.GetComponent<RectTransform>();
            float contentHeight = _tabContentRT.sizeDelta.y;
            float viewportHeight = viewportRT.rect.height;
            if (contentHeight <= viewportHeight) return;

            float tabH = 32f;
            float tabGap = 2f;
            float tabTop = 4f + index * (tabH + tabGap);
            float tabBottom = tabTop + tabH;
            float scrollableHeight = contentHeight - viewportHeight;
            float currentScroll = (1f - _tabScrollRect.verticalNormalizedPosition) * scrollableHeight;

            if (tabTop < currentScroll)
                _tabScrollRect.verticalNormalizedPosition = 1f - (tabTop / scrollableHeight);
            else if (tabBottom > currentScroll + viewportHeight)
                _tabScrollRect.verticalNormalizedPosition = 1f - ((tabBottom - viewportHeight) / scrollableHeight);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Build UI
        // ══════════════════════════════════════════════════════════════════

        private void Build()
        {
            // Canvas
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 31;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            gameObject.AddComponent<GraphicRaycaster>();

            // Font
            _font = ResolveFont();

            // Dark backdrop
            _backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
            _backdrop.transform.SetParent(transform, false);
            var bdRT = _backdrop.GetComponent<RectTransform>();
            bdRT.anchorMin = Vector2.zero;
            bdRT.anchorMax = Vector2.one;
            bdRT.offsetMin = Vector2.zero;
            bdRT.offsetMax = Vector2.zero;
            _backdrop.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.85f);

            // Root panel
            _root = new GameObject("ConfigRoot", typeof(RectTransform), typeof(Image));
            _root.transform.SetParent(transform, false);
            var rootRT = _root.GetComponent<RectTransform>();
            rootRT.anchorMin = new Vector2(0.5f, 0.5f);
            rootRT.anchorMax = new Vector2(0.5f, 0.5f);
            rootRT.pivot = new Vector2(0.5f, 0.5f);
            rootRT.sizeDelta = new Vector2(PanelW, PanelH);

            var rootImg = _root.GetComponent<Image>();
            var bgSprite = GetPanelBgSprite();
            if (bgSprite != null)
            {
                rootImg.sprite = bgSprite;
                rootImg.type = Image.Type.Simple;
                rootImg.color = Color.white;
            }
            else
            {
                rootImg.color = new Color(0.05f, 0.05f, 0.08f, 0.95f);
            }
            rootImg.raycastTarget = true;

            // Title
            BuildTitle(_root.transform);

            // Close button
            BuildCloseButton(_root.transform);

            // Tab column (left)
            BuildTabColumn(_root.transform);

            // Content area (right) with scroll
            BuildContentArea(_root.transform);

            // Select first category
            if (ModConfig.CategoryOrder.Count > 0)
                SelectCategory(ModConfig.CategoryOrder[0]);

            _backdrop.SetActive(false);
            _root.SetActive(false);
        }

        // ── Title ─────────────────────────────────────────────────────────

        private void BuildTitle(Transform parent)
        {
            var go = new GameObject("Title", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, TitleH);
            rt.anchoredPosition = Vector2.zero;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.text = "Companion Settings";
            tmp.fontSize = 18f;
            tmp.color = LabelText;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
        }

        // ── Close button ──────────────────────────────────────────────────

        private void BuildCloseButton(Transform parent)
        {
            var go = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(36f, 36f);
            rt.anchoredPosition = new Vector2(-4f, -2f);

            var closeImg = go.GetComponent<Image>();
            var btnSprite = GetBtnBgSprite();
            if (btnSprite != null)
            {
                closeImg.sprite = btnSprite;
                closeImg.type = Image.Type.Simple;
                closeImg.color = new Color(0.6f, 0.2f, 0.2f, 1f);
            }
            else
            {
                closeImg.color = new Color(0.3f, 0.1f, 0.1f, 0.9f);
            }
            go.GetComponent<Button>().onClick.AddListener(Hide);

            var txtGO = new GameObject("X", typeof(RectTransform));
            txtGO.transform.SetParent(go.transform, false);
            var tmp = txtGO.AddComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.text = "X";
            tmp.fontSize = 16f;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            var tRT = txtGO.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero;
            tRT.anchorMax = Vector2.one;
            tRT.offsetMin = Vector2.zero;
            tRT.offsetMax = Vector2.zero;
        }

        // ── Tab column ────────────────────────────────────────────────────

        private void BuildTabColumn(Transform parent)
        {
            // Tab container with background — acts as scroll viewport
            var tabCol = new GameObject("TabColumn", typeof(RectTransform), typeof(Image), typeof(RectMask2D));
            tabCol.transform.SetParent(parent, false);
            var tcRT = tabCol.GetComponent<RectTransform>();
            tcRT.anchorMin = new Vector2(0f, 0f);
            tcRT.anchorMax = new Vector2(0f, 1f);
            tcRT.pivot = new Vector2(0f, 1f);
            tcRT.offsetMin = new Vector2(Pad, Pad);
            tcRT.offsetMax = new Vector2(Pad + TabW, -TitleH - 4f);
            tabCol.GetComponent<Image>().color = ColBg;

            // Scrollable content inside the tab column
            var tabContent = new GameObject("TabContent", typeof(RectTransform));
            tabContent.transform.SetParent(tabCol.transform, false);
            _tabContentRT = tabContent.GetComponent<RectTransform>();
            _tabContentRT.anchorMin = new Vector2(0f, 1f);
            _tabContentRT.anchorMax = new Vector2(1f, 1f);
            _tabContentRT.pivot = new Vector2(0.5f, 1f);
            _tabContentRT.anchoredPosition = Vector2.zero;

            float y = -4f;
            float tabH = 32f;
            float tabGap = 2f;

            foreach (string cat in ModConfig.CategoryOrder)
            {
                var btnGO = new GameObject("Tab_" + cat, typeof(RectTransform), typeof(Image), typeof(Button));
                btnGO.transform.SetParent(_tabContentRT, false);
                var bRT = btnGO.GetComponent<RectTransform>();
                bRT.anchorMin = new Vector2(0f, 1f);
                bRT.anchorMax = new Vector2(1f, 1f);
                bRT.pivot = new Vector2(0.5f, 1f);
                bRT.anchoredPosition = new Vector2(0f, y);
                bRT.sizeDelta = new Vector2(-8f, tabH);
                y -= tabH + tabGap;

                var btnImg = btnGO.GetComponent<Image>();
                var catSprite = GetCatBgSprite();
                if (catSprite != null)
                {
                    btnImg.sprite = catSprite;
                    btnImg.type = Image.Type.Simple;
                    btnImg.color = TabInactive;
                }
                else
                {
                    btnImg.color = TabInactive;
                }

                var txtGO = new GameObject("Label", typeof(RectTransform));
                txtGO.transform.SetParent(btnGO.transform, false);
                var tmp = txtGO.AddComponent<TextMeshProUGUI>();
                if (_font != null) tmp.font = _font;
                tmp.text = cat;
                tmp.fontSize = 11f;
                tmp.color = Color.white;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.raycastTarget = false;
                var txtRT = txtGO.GetComponent<RectTransform>();
                txtRT.anchorMin = Vector2.zero;
                txtRT.anchorMax = Vector2.one;
                txtRT.offsetMin = Vector2.zero;
                txtRT.offsetMax = Vector2.zero;

                string catCapture = cat;
                var btn = btnGO.GetComponent<Button>();
                btn.onClick.AddListener(() => SelectCategory(catCapture));
                _tabButtons[cat] = btn;
            }

            // Set content height for scrolling
            _tabContentRT.sizeDelta = new Vector2(0f, -y + 4f);

            // Hidden scrollbar for the tab column
            var tabSB = CreateHiddenScrollbar(tabCol.transform);

            // ScrollRect on the tab column
            _tabScrollRect = tabCol.AddComponent<ScrollRect>();
            _tabScrollRect.content = _tabContentRT;
            _tabScrollRect.viewport = tcRT;
            _tabScrollRect.vertical = true;
            _tabScrollRect.horizontal = false;
            _tabScrollRect.movementType = ScrollRect.MovementType.Clamped;
            _tabScrollRect.scrollSensitivity = 30f;
            _tabScrollRect.verticalScrollbar = tabSB;
            _tabScrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }

        // ── Content area with scroll ──────────────────────────────────────

        private void BuildContentArea(Transform parent)
        {
            // Scroll viewport
            float contentX = Pad + TabW + Pad;
            var scrollGO = new GameObject("ContentScroll", typeof(RectTransform), typeof(RectMask2D));
            scrollGO.transform.SetParent(parent, false);
            var sRT = scrollGO.GetComponent<RectTransform>();
            sRT.anchorMin = new Vector2(0f, 0f);
            sRT.anchorMax = new Vector2(1f, 1f);
            sRT.offsetMin = new Vector2(contentX, Pad);
            sRT.offsetMax = new Vector2(-Pad, -TitleH - 4f);

            // Content container — top-anchored, grows downward
            var contentGO = new GameObject("Content", typeof(RectTransform));
            contentGO.transform.SetParent(scrollGO.transform, false);
            _contentRT = contentGO.GetComponent<RectTransform>();
            _contentRT.anchorMin = new Vector2(0f, 1f);
            _contentRT.anchorMax = new Vector2(1f, 1f);
            _contentRT.pivot = new Vector2(0.5f, 1f);
            _contentRT.anchoredPosition = Vector2.zero;
            _contentRT.sizeDelta = new Vector2(0f, 0f);

            // Hidden scrollbar
            var sb = CreateHiddenScrollbar(scrollGO.transform);

            // ScrollRect
            _scrollRect = scrollGO.AddComponent<ScrollRect>();
            _scrollRect.content = _contentRT;
            _scrollRect.viewport = sRT;
            _scrollRect.vertical = true;
            _scrollRect.horizontal = false;
            _scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _scrollRect.scrollSensitivity = 40f;
            _scrollRect.verticalScrollbar = sb;
            _scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        private static Scrollbar CreateHiddenScrollbar(Transform parent)
        {
            var sbGO = new GameObject("Scrollbar", typeof(RectTransform));
            sbGO.transform.SetParent(parent, false);
            var sbRT = sbGO.GetComponent<RectTransform>();
            sbRT.anchorMin = new Vector2(1f, 0f);
            sbRT.anchorMax = new Vector2(1f, 1f);
            sbRT.pivot = new Vector2(1f, 0.5f);
            sbRT.sizeDelta = new Vector2(10f, 0f);
            sbGO.AddComponent<Image>().color = Color.clear;

            var slidingGO = new GameObject("Sliding Area", typeof(RectTransform));
            slidingGO.transform.SetParent(sbGO.transform, false);
            var slRT = slidingGO.GetComponent<RectTransform>();
            slRT.anchorMin = Vector2.zero;
            slRT.anchorMax = Vector2.one;
            slRT.offsetMin = Vector2.zero;
            slRT.offsetMax = Vector2.zero;

            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(slidingGO.transform, false);
            handleGO.GetComponent<Image>().color = Color.clear;
            var hRT = handleGO.GetComponent<RectTransform>();
            hRT.anchorMin = Vector2.zero;
            hRT.anchorMax = new Vector2(1f, 0.5f);
            hRT.offsetMin = Vector2.zero;
            hRT.offsetMax = Vector2.zero;

            var sb = sbGO.AddComponent<Scrollbar>();
            sb.handleRect = hRT;
            sb.direction = Scrollbar.Direction.BottomToTop;
            return sb;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Category selection
        // ══════════════════════════════════════════════════════════════════

        private void SelectCategory(string category)
        {
            _activeCategory = category;
            _activeCategoryIndex = ModConfig.CategoryOrder.IndexOf(category);
            _selectedRowIndex = -1;

            // Update tab highlights
            foreach (var kv in _tabButtons)
            {
                var img = kv.Value.GetComponent<Image>();
                bool active = kv.Key == category;
                img.color = active ? GoldColor : TabInactive;
                var txt = kv.Value.GetComponentInChildren<TMP_Text>();
                if (txt != null) txt.color = active ? Color.white : new Color(0.8f, 0.8f, 0.8f);
            }

            // Auto-scroll tab list to keep active tab visible
            ScrollToTab(_activeCategoryIndex);

            RefreshCategory();
        }

        private void RefreshCategory()
        {
            // Clear existing rows
            foreach (var row in _entryRows)
                Destroy(row);
            _entryRows.Clear();
            _entryData.Clear();
            _entrySliders.Clear();
            _entryResetActions.Clear();

            // Build rows for this category
            if (!ModConfig.Categories.TryGetValue(_activeCategory, out var entries)) return;

            float y = -4f;
            foreach (var entry in entries)
            {
                var row = BuildEntryRow(entry, y);
                if (row != null)
                {
                    _entryRows.Add(row);
                    y -= RowH + RowGap;
                }
            }

            // Set content height
            _contentRT.sizeDelta = new Vector2(0f, -y + Pad);

            // Reset scroll to top
            if (_scrollRect != null)
                _scrollRect.verticalNormalizedPosition = 1f;

            // Re-apply selection highlight if in range
            if (_selectedRowIndex >= _entryRows.Count)
                _selectedRowIndex = _entryRows.Count - 1;
            if (_selectedRowIndex >= 0)
                SetSelectedRow(_selectedRowIndex);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Entry row builders
        // ══════════════════════════════════════════════════════════════════

        private GameObject BuildEntryRow(ConfigEntryBase entry, float yPos)
        {
            if (entry.SettingType == typeof(float))
                return BuildFloatRow(entry, yPos);
            if (entry.SettingType == typeof(int))
                return BuildIntRow(entry, yPos);
            if (entry.SettingType == typeof(bool))
                return BuildBoolRow(entry, yPos);
            if (entry.SettingType == typeof(KeyCode))
                return BuildKeyCodeRow(entry, yPos);
            return null;
        }

        // ── Float slider row ──────────────────────────────────────────────

        private GameObject BuildFloatRow(ConfigEntryBase entry, float yPos)
        {
            var typed = (ConfigEntry<float>)entry;
            float min = 0f, max = 100f;
            if (entry.Description?.AcceptableValues is AcceptableValueRange<float> range)
            {
                min = range.MinValue;
                max = range.MaxValue;
            }

            var row = CreateRowContainer("Row_" + entry.Definition.Key, yPos);

            // Label (left)
            var label = CreateLabel(row.transform, entry.Definition.Key, 12f, Color.white);
            var lRT = label.GetComponent<RectTransform>();
            lRT.anchorMin = new Vector2(0f, 0f);
            lRT.anchorMax = new Vector2(0.38f, 1f);
            lRT.offsetMin = new Vector2(8f, 0f);
            lRT.offsetMax = Vector2.zero;
            label.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.MidlineLeft;

            // Value text
            var valTxt = CreateLabel(row.transform, FormatFloat(typed.Value), 11f, GoldTextColor);
            var vRT = valTxt.GetComponent<RectTransform>();
            vRT.anchorMin = new Vector2(0.74f, 0f);
            vRT.anchorMax = new Vector2(0.84f, 1f);
            vRT.offsetMin = Vector2.zero;
            vRT.offsetMax = Vector2.zero;
            valTxt.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.MidlineRight;
            var valTMP = valTxt.GetComponent<TMP_Text>();

            // Slider (middle)
            var sliderGO = CreateSlider(row.transform, min, max, typed.Value, false);
            var sRT = sliderGO.GetComponent<RectTransform>();
            sRT.anchorMin = new Vector2(0.40f, 0.15f);
            sRT.anchorMax = new Vector2(0.73f, 0.85f);
            sRT.offsetMin = Vector2.zero;
            sRT.offsetMax = Vector2.zero;

            var slider = sliderGO.GetComponent<Slider>();
            slider.onValueChanged.AddListener(v =>
            {
                typed.Value = Mathf.Round(v * 100f) / 100f;
                valTMP.text = FormatFloat(typed.Value);
            });

            // Default button
            CreateDefaultButton(row.transform, entry, () =>
            {
                slider.value = (float)entry.DefaultValue;
                valTMP.text = FormatFloat(typed.Value);
            });

            _entryData.Add(entry);
            _entrySliders.Add(slider);
            _entryResetActions.Add(() =>
            {
                entry.BoxedValue = entry.DefaultValue;
                slider.value = (float)entry.DefaultValue;
                valTMP.text = FormatFloat(typed.Value);
            });

            return row;
        }

        // ── Int slider row ────────────────────────────────────────────────

        private GameObject BuildIntRow(ConfigEntryBase entry, float yPos)
        {
            var typed = (ConfigEntry<int>)entry;
            int min = 0, max = 100;
            if (entry.Description?.AcceptableValues is AcceptableValueRange<int> range)
            {
                min = range.MinValue;
                max = range.MaxValue;
            }

            var row = CreateRowContainer("Row_" + entry.Definition.Key, yPos);

            var label = CreateLabel(row.transform, entry.Definition.Key, 12f, Color.white);
            var lRT = label.GetComponent<RectTransform>();
            lRT.anchorMin = new Vector2(0f, 0f);
            lRT.anchorMax = new Vector2(0.38f, 1f);
            lRT.offsetMin = new Vector2(8f, 0f);
            lRT.offsetMax = Vector2.zero;
            label.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.MidlineLeft;

            var valTxt = CreateLabel(row.transform, typed.Value.ToString(), 11f, GoldTextColor);
            var vRT = valTxt.GetComponent<RectTransform>();
            vRT.anchorMin = new Vector2(0.74f, 0f);
            vRT.anchorMax = new Vector2(0.84f, 1f);
            vRT.offsetMin = Vector2.zero;
            vRT.offsetMax = Vector2.zero;
            valTxt.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.MidlineRight;
            var valTMP = valTxt.GetComponent<TMP_Text>();

            var sliderGO = CreateSlider(row.transform, min, max, typed.Value, true);
            var sRT = sliderGO.GetComponent<RectTransform>();
            sRT.anchorMin = new Vector2(0.40f, 0.15f);
            sRT.anchorMax = new Vector2(0.73f, 0.85f);
            sRT.offsetMin = Vector2.zero;
            sRT.offsetMax = Vector2.zero;

            var slider = sliderGO.GetComponent<Slider>();
            slider.onValueChanged.AddListener(v =>
            {
                typed.Value = Mathf.RoundToInt(v);
                valTMP.text = typed.Value.ToString();
            });

            // Default button
            CreateDefaultButton(row.transform, entry, () =>
            {
                slider.value = (int)entry.DefaultValue;
                valTMP.text = typed.Value.ToString();
            });

            _entryData.Add(entry);
            _entrySliders.Add(slider);
            _entryResetActions.Add(() =>
            {
                entry.BoxedValue = entry.DefaultValue;
                slider.value = (int)entry.DefaultValue;
                valTMP.text = typed.Value.ToString();
            });

            return row;
        }

        // ── Bool toggle row ───────────────────────────────────────────────

        private GameObject BuildBoolRow(ConfigEntryBase entry, float yPos)
        {
            var typed = (ConfigEntry<bool>)entry;

            var row = CreateRowContainer("Row_" + entry.Definition.Key, yPos);

            var label = CreateLabel(row.transform, entry.Definition.Key, 12f, Color.white);
            var lRT = label.GetComponent<RectTransform>();
            lRT.anchorMin = new Vector2(0f, 0f);
            lRT.anchorMax = new Vector2(0.65f, 1f);
            lRT.offsetMin = new Vector2(8f, 0f);
            lRT.offsetMax = Vector2.zero;
            label.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.MidlineLeft;

            // Toggle button
            var btnGO = new GameObject("Toggle", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(row.transform, false);
            var bRT = btnGO.GetComponent<RectTransform>();
            bRT.anchorMin = new Vector2(0.62f, 0.1f);
            bRT.anchorMax = new Vector2(0.83f, 0.9f);
            bRT.offsetMin = Vector2.zero;
            bRT.offsetMax = Vector2.zero;

            var btnImg = btnGO.GetComponent<Image>();
            var toggleSprite = GetBtnBgSprite();
            if (toggleSprite != null)
            {
                btnImg.sprite = toggleSprite;
                btnImg.type = Image.Type.Simple;
                btnImg.color = typed.Value ? GoldColor : Color.white;
            }
            else
            {
                btnImg.color = typed.Value ? GoldColor : TabInactive;
            }

            var btnLabel = CreateLabel(btnGO.transform, typed.Value ? "ON" : "OFF", 11f, Color.white);
            var blRT = btnLabel.GetComponent<RectTransform>();
            blRT.anchorMin = Vector2.zero;
            blRT.anchorMax = Vector2.one;
            blRT.offsetMin = Vector2.zero;
            blRT.offsetMax = Vector2.zero;
            btnLabel.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.Center;
            var btnTMP = btnLabel.GetComponent<TMP_Text>();

            bool hasToggleSprite = toggleSprite != null;
            btnGO.GetComponent<Button>().onClick.AddListener(() =>
            {
                typed.Value = !typed.Value;
                btnImg.color = typed.Value ? GoldColor : (hasToggleSprite ? Color.white : TabInactive);
                btnTMP.text = typed.Value ? "ON" : "OFF";
            });

            // Default button
            CreateDefaultButton(row.transform, entry, () =>
            {
                typed.Value = (bool)entry.DefaultValue;
                btnImg.color = typed.Value ? GoldColor : (hasToggleSprite ? Color.white : TabInactive);
                btnTMP.text = typed.Value ? "ON" : "OFF";
            });

            _entryData.Add(entry);
            _entrySliders.Add(null); // no slider for bool
            _entryResetActions.Add(() =>
            {
                typed.Value = (bool)entry.DefaultValue;
                btnImg.color = typed.Value ? GoldColor : (hasToggleSprite ? Color.white : TabInactive);
                btnTMP.text = typed.Value ? "ON" : "OFF";
            });

            return row;
        }

        // ── KeyCode row ───────────────────────────────────────────────────

        private GameObject BuildKeyCodeRow(ConfigEntryBase entry, float yPos)
        {
            var typed = (ConfigEntry<KeyCode>)entry;

            var row = CreateRowContainer("Row_" + entry.Definition.Key, yPos);

            var label = CreateLabel(row.transform, entry.Definition.Key, 12f, Color.white);
            var lRT = label.GetComponent<RectTransform>();
            lRT.anchorMin = new Vector2(0f, 0f);
            lRT.anchorMax = new Vector2(0.55f, 1f);
            lRT.offsetMin = new Vector2(8f, 0f);
            lRT.offsetMax = Vector2.zero;
            label.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.MidlineLeft;

            // Key binding button
            var btnGO = new GameObject("KeyBind", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(row.transform, false);
            var bRT = btnGO.GetComponent<RectTransform>();
            bRT.anchorMin = new Vector2(0.50f, 0.1f);
            bRT.anchorMax = new Vector2(0.83f, 0.9f);
            bRT.offsetMin = Vector2.zero;
            bRT.offsetMax = Vector2.zero;

            var kbImg = btnGO.GetComponent<Image>();
            var kbSprite = GetBtnBgSprite();
            if (kbSprite != null)
            {
                kbImg.sprite = kbSprite;
                kbImg.type = Image.Type.Simple;
                kbImg.color = Color.white;
            }
            else
            {
                kbImg.color = new Color(0.15f, 0.15f, 0.18f, 0.9f);
            }

            var btnLabel = CreateLabel(btnGO.transform, typed.Value.ToString(), 11f, GoldTextColor);
            var blRT = btnLabel.GetComponent<RectTransform>();
            blRT.anchorMin = Vector2.zero;
            blRT.anchorMax = Vector2.one;
            blRT.offsetMin = Vector2.zero;
            blRT.offsetMax = Vector2.zero;
            btnLabel.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.Center;
            var btnTMP = btnLabel.GetComponent<TMP_Text>();

            bool listening = false;
            btnGO.GetComponent<Button>().onClick.AddListener(() =>
            {
                listening = true;
                btnTMP.text = "Press a key...";
                btnTMP.color = LabelText;
            });

            // Coroutine-free key listener via Update callback
            var listener = btnGO.AddComponent<KeyBindListener>();
            listener.Init(typed, btnTMP, () => listening, v => listening = v);

            // Default button
            CreateDefaultButton(row.transform, entry, () =>
            {
                typed.Value = (KeyCode)entry.DefaultValue;
                btnTMP.text = typed.Value.ToString();
                btnTMP.color = GoldTextColor;
            });

            _entryData.Add(entry);
            _entrySliders.Add(null); // no slider for keycode
            _entryResetActions.Add(() =>
            {
                typed.Value = (KeyCode)entry.DefaultValue;
                btnTMP.text = typed.Value.ToString();
                btnTMP.color = GoldTextColor;
            });

            return row;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Factory helpers
        // ══════════════════════════════════════════════════════════════════

        private GameObject CreateRowContainer(string name, float yPos)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_contentRT, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, yPos);
            rt.sizeDelta = new Vector2(-8f, RowH);
            go.GetComponent<Image>().color = RowBg;
            return go;
        }

        private GameObject CreateLabel(Transform parent, string text, float size, Color color)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.text = text;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.raycastTarget = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return go;
        }

        private void CreateDefaultButton(Transform parent, ConfigEntryBase entry, System.Action onReset)
        {
            var btnGO = new GameObject("DefaultBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(parent, false);
            var bRT = btnGO.GetComponent<RectTransform>();
            bRT.anchorMin = new Vector2(0.85f, 0.1f);
            bRT.anchorMax = new Vector2(1.0f, 0.9f);
            bRT.offsetMin = Vector2.zero;
            bRT.offsetMax = new Vector2(-2f, 0f);

            var btnImg = btnGO.GetComponent<Image>();
            var catSprite = GetCatBgSprite();
            if (catSprite != null)
            {
                btnImg.sprite = catSprite;
                btnImg.type = Image.Type.Simple;
                btnImg.color = TabInactive;
            }
            else
            {
                btnImg.color = DefaultBtnColor;
            }

            var txtGO = new GameObject("Label", typeof(RectTransform));
            txtGO.transform.SetParent(btnGO.transform, false);
            var tmp = txtGO.AddComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.text = "Default";
            tmp.fontSize = 9f;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            var txtRT = txtGO.GetComponent<RectTransform>();
            txtRT.anchorMin = Vector2.zero;
            txtRT.anchorMax = Vector2.one;
            txtRT.offsetMin = Vector2.zero;
            txtRT.offsetMax = Vector2.zero;

            btnGO.GetComponent<Button>().onClick.AddListener(() =>
            {
                entry.BoxedValue = entry.DefaultValue;
                onReset?.Invoke();
            });
        }

        // ─── Slider: cloned from InventoryGui.instance.m_splitSlider ───────
        // Mirrors CompanionPanel.MakeSlider — clones the vanilla Valheim slider
        // to get proper game textures, sprites, and styling. Falls back to a
        // programmatic slider when InventoryGui isn't available.
        // ─────────────────────────────────────────────────────────────────────

        private static GameObject CreateSlider(Transform parent, float min, float max, float value, bool wholeNumbers)
        {
            GameObject go;
            Slider slider;

            var srcSlider = InventoryGui.instance?.m_splitSlider;
            if (srcSlider != null)
            {
                go = Object.Instantiate(srcSlider.gameObject, parent);
                go.name = "Slider";
                go.SetActive(true);

                // Strip layout constraints from the clone
                var le = go.GetComponent<LayoutElement>();
                if (le != null) Object.Destroy(le);

                slider = go.GetComponent<Slider>();
                if (slider == null)
                {
                    Object.Destroy(go);
                    return CreateFallbackSlider(parent, min, max, value, wholeNumbers);
                }
            }
            else
            {
                return CreateFallbackSlider(parent, min, max, value, wholeNumbers);
            }

            // Replace slider background with our custom SliderBackground texture.
            var sliderBgSprite = GetSliderBgSprite();
            if (sliderBgSprite != null)
            {
                Image bgImg = null;
                var bg = go.transform.Find("Background");
                if (bg != null) bgImg = bg.GetComponent<Image>();

                if (bgImg == null)
                {
                    foreach (var img in go.GetComponentsInChildren<Image>(true))
                    {
                        if (img != null &&
                            img.gameObject.name.IndexOf("background", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            bgImg = img;
                            break;
                        }
                    }
                }

                if (bgImg == null) bgImg = go.GetComponent<Image>();

                if (bgImg != null)
                {
                    bgImg.sprite = sliderBgSprite;
                    bgImg.type = Image.Type.Simple;
                    bgImg.preserveAspect = false;
                    bgImg.color = Color.white;
                }
            }

            // Rewire the slider for our range
            slider.onValueChanged.RemoveAllListeners();
            slider.transition = Selectable.Transition.None;
            slider.wholeNumbers = wholeNumbers;
            slider.minValue = min;
            slider.maxValue = max;

            // Remove inherited dark tinting from cloned slider graphics.
            foreach (var img in go.GetComponentsInChildren<Image>(true))
            {
                if (img == null) continue;
                var c = img.color;
                img.color = new Color(1f, 1f, 1f, c.a);
            }

            // Gold fill for the inner bar while keeping vanilla frame/handle textures.
            if (slider.fillRect != null)
            {
                var fillImg = slider.fillRect.GetComponent<Image>();
                if (fillImg != null) fillImg.color = GoldColor;
            }

            slider.value = value;
            return go;
        }

        private static GameObject CreateFallbackSlider(Transform parent, float min, float max, float value, bool wholeNumbers)
        {
            var go = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
            go.transform.SetParent(parent, false);

            // Background — use SliderBackground texture if available
            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(go.transform, false);
            var bgRT = bgGO.GetComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero;
            bgRT.anchorMax = Vector2.one;
            bgRT.offsetMin = Vector2.zero;
            bgRT.offsetMax = Vector2.zero;
            var bgImage = bgGO.GetComponent<Image>();
            var slBgSprite = GetSliderBgSprite();
            if (slBgSprite != null)
            {
                bgImage.sprite = slBgSprite;
                bgImage.type = Image.Type.Simple;
                bgImage.color = Color.white;
            }
            else
            {
                bgImage.color = new Color(0.1f, 0.1f, 0.12f, 0.9f);
            }

            // Fill Area
            var fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
            fillAreaGO.transform.SetParent(go.transform, false);
            var faRT = fillAreaGO.GetComponent<RectTransform>();
            faRT.anchorMin = Vector2.zero;
            faRT.anchorMax = Vector2.one;
            faRT.offsetMin = new Vector2(5f, 0f);
            faRT.offsetMax = new Vector2(-5f, 0f);

            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(fillAreaGO.transform, false);
            var fillRT = fillGO.GetComponent<RectTransform>();
            fillRT.anchorMin = Vector2.zero;
            fillRT.anchorMax = new Vector2(0f, 1f);
            fillRT.offsetMin = Vector2.zero;
            fillRT.offsetMax = Vector2.zero;
            fillGO.GetComponent<Image>().color = GoldColor;

            // Handle Slide Area
            var hsaGO = new GameObject("Handle Slide Area", typeof(RectTransform));
            hsaGO.transform.SetParent(go.transform, false);
            var hsaRT = hsaGO.GetComponent<RectTransform>();
            hsaRT.anchorMin = Vector2.zero;
            hsaRT.anchorMax = Vector2.one;
            hsaRT.offsetMin = new Vector2(5f, 0f);
            hsaRT.offsetMax = new Vector2(-5f, 0f);

            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(hsaGO.transform, false);
            var handleRT = handleGO.GetComponent<RectTransform>();
            handleRT.sizeDelta = new Vector2(12f, 0f);
            handleRT.anchorMin = new Vector2(0f, 0f);
            handleRT.anchorMax = new Vector2(0f, 1f);
            handleGO.GetComponent<Image>().color = new Color(0.95f, 0.85f, 0.65f, 1f);

            var slider = go.GetComponent<Slider>();
            slider.fillRect = fillRT;
            slider.handleRect = handleRT;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.wholeNumbers = wholeNumbers;
            slider.value = value;

            return go;
        }

        // ── Utility ───────────────────────────────────────────────────────

        private static string FormatFloat(float v)
        {
            if (v >= 100f) return v.ToString("F0");
            if (v >= 10f) return v.ToString("F1");
            return v.ToString("F2");
        }

        private static bool IsBrokenTmpFont(TMP_FontAsset font)
        {
            return font == null ||
                   font.name.IndexOf("LiberationSans", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static TMP_FontAsset ResolveFont()
        {
            // Search InventoryGui for a valid (non-LiberationSans) font
            if (InventoryGui.instance != null)
            {
                var texts = InventoryGui.instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] != null && !IsBrokenTmpFont(texts[i].font))
                        return texts[i].font;
                }
            }

            // Try Hud
            if (Hud.instance != null)
            {
                var texts = Hud.instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] != null && !IsBrokenTmpFont(texts[i].font))
                        return texts[i].font;
                }
            }

            // Try TMP default
            if (!IsBrokenTmpFont(TMP_Settings.defaultFontAsset))
                return TMP_Settings.defaultFontAsset;

            // Search all loaded TMP fonts, prefer Valheim's AveriaSerifLibre
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
    }

    /// <summary>
    /// Tiny helper MonoBehaviour for key binding capture.
    /// Attached to a key-bind button; polls for key press each frame.
    /// </summary>
    internal class KeyBindListener : MonoBehaviour
    {
        private ConfigEntry<KeyCode> _entry;
        private TMP_Text _label;
        private System.Func<bool> _isListening;
        private System.Action<bool> _setListening;

        internal void Init(ConfigEntry<KeyCode> entry, TMP_Text label,
            System.Func<bool> isListening, System.Action<bool> setListening)
        {
            _entry = entry;
            _label = label;
            _isListening = isListening;
            _setListening = setListening;
        }

        private void Update()
        {
            if (_isListening == null || !_isListening()) return;

            // Check for any key press
            if (!Input.anyKeyDown) return;

            // Escape cancels binding
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                _label.text = _entry.Value.ToString();
                _label.color = new Color(0.83f, 0.52f, 0.18f, 1f);
                _setListening(false);
                return;
            }

            // Find which key was pressed
            foreach (KeyCode kc in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (kc == KeyCode.None) continue;
                if (Input.GetKeyDown(kc))
                {
                    _entry.Value = kc;
                    _label.text = kc.ToString();
                    _label.color = new Color(0.83f, 0.52f, 0.18f, 1f);
                    _setListening(false);
                    return;
                }
            }
        }
    }
}
