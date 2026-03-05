using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Companions
{
    /// <summary>
    /// Small floating panel for adjusting a companion's home zone radius.
    /// Opened from the radial menu's "Set Home" action.
    /// Independent of InventoryGui — works with mouse and gamepad.
    /// </summary>
    public class HomeZonePanel : MonoBehaviour
    {
        public static HomeZonePanel Instance { get; private set; }
        public bool IsVisible => _visible;
        public CompanionSetup CurrentCompanion => _companion;

        public static bool IsOpenFor(CompanionSetup setup)
            => Instance != null && Instance._visible
            && Instance._companion != null && Instance._companion == setup;

        // ── UI state ──────────────────────────────────────────────────────
        private bool _visible;
        private Canvas _canvas;
        private GameObject _root;
        private TMP_FontAsset _font;

        // ── Slider ────────────────────────────────────────────────────────
        private Slider _slider;
        private TextMeshProUGUI _valueLabel;
        private TextMeshProUGUI _titleLabel;
        private TextMeshProUGUI _hintLabel;

        // ── Companion refs ────────────────────────────────────────────────
        private CompanionSetup _companion;
        private CompanionAI _companionAI;
        private HomeZoneVisual _homeZoneVisual;

        // ── Controller input ──────────────────────────────────────────────
        private float _dpadRepeatTimer;
        private const float DpadInitialDelay = 0.35f;
        private const float DpadRepeatRate   = 0.08f;
        private const float StickDeadZone    = 0.3f;

        // ── Slider mapping: 1-40 → 5m-200m ───────────────────────────────
        private const int SliderMin  = 1;
        private const int SliderMax  = 40;
        private const int StepMeters = 5;

        // ── Style ─────────────────────────────────────────────────────────
        private const float PanelW = 350f;
        private const float PanelH = 140f;
        private static readonly Color BgColor     = new Color(0.08f, 0.07f, 0.06f, 0.92f);
        private static readonly Color GoldColor   = new Color(1f, 0.85f, 0.2f, 0.7f);
        private static readonly Color LabelColor  = new Color(1f, 0.9f, 0.5f, 1f);
        private static readonly Color HandleColor = new Color(1f, 0.95f, 0.6f, 1f);

        // ══════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════

        public static void EnsureInstance()
        {
            if (Instance != null) return;
            var go = new GameObject("HC_HomeZonePanel");
            DontDestroyOnLoad(go);
            go.AddComponent<HomeZonePanel>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Show / Hide
        // ══════════════════════════════════════════════════════════════════

        public void Show(CompanionSetup companion)
        {
            if (companion == null) return;
            if (_root == null) Build();

            _companion = companion;
            _companionAI = companion.GetComponent<CompanionAI>();

            // Get or create HomeZoneVisual
            _homeZoneVisual = companion.gameObject.GetComponent<HomeZoneVisual>()
                ?? companion.gameObject.AddComponent<HomeZoneVisual>();

            // Read current radius from ZDO
            float radius = _companion.GetHomeRadius();
            int sliderVal = Mathf.Clamp(Mathf.RoundToInt(radius / StepMeters), SliderMin, SliderMax);
            _slider.SetValueWithoutNotify(sliderVal);
            UpdateValueLabel(sliderVal * StepMeters);

            // Show the ring
            if (_companion.HasHomePosition())
                _homeZoneVisual.Show(_companion.GetHomePosition(), radius);

            // Title with companion name
            string name = companion.GetComponent<Humanoid>()?.m_name;
            var nview = companion.GetComponent<ZNetView>();
            string zdoName = nview?.GetZDO()?.GetString(CompanionSetup.NameHash, "");
            if (!string.IsNullOrEmpty(zdoName)) name = zdoName;
            _titleLabel.text = string.IsNullOrEmpty(name) ? "Home Zone" : $"{name} — Home Zone";

            // Update hint text for current input mode
            UpdateHintText();

            _root.SetActive(true);
            _visible = true;
            _dpadRepeatTimer = 0f;
        }

        public void Hide()
        {
            _visible = false;
            if (_root != null) _root.SetActive(false);
            _homeZoneVisual?.Hide();
            _companion = null;
            _companionAI = null;
            _homeZoneVisual = null;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Update
        // ══════════════════════════════════════════════════════════════════

        private void Update()
        {
            if (!_visible) return;

            // Companion destroyed or unloaded
            if (_companion == null || !_companion)
            {
                Hide();
                return;
            }

            // Close on Escape / B button
            if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyButtonB"))
            {
                Hide();
                return;
            }

            // Confirm on A button (value already saved on change)
            if (ZInput.GetButtonDown("JoyButtonA"))
            {
                Hide();
                return;
            }

            // Keep cursor free
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Controller slider adjustment
            if (ZInput.IsGamepadActive())
                UpdateControllerInput();

            // Update hint if input mode changed
            UpdateHintText();
        }

        // ══════════════════════════════════════════════════════════════════
        //  Controller input
        // ══════════════════════════════════════════════════════════════════

        private void UpdateControllerInput()
        {
            float dt = Time.unscaledDeltaTime;

            float hAxis = ZInput.GetJoyLeftStickX();
            bool dpadLeft  = ZInput.GetButtonDown("JoyDPadLeft");
            bool dpadRight = ZInput.GetButtonDown("JoyDPadRight");
            bool hLeft  = dpadLeft  || hAxis < -StickDeadZone;
            bool hRight = dpadRight || hAxis >  StickDeadZone;

            if (hLeft || hRight)
            {
                if (_dpadRepeatTimer <= 0f || dpadLeft || dpadRight)
                {
                    AdjustSlider(hRight ? 1 : -1);
                    _dpadRepeatTimer = (dpadLeft || dpadRight)
                        ? DpadInitialDelay
                        : DpadRepeatRate;
                }
                _dpadRepeatTimer -= dt;
            }
            else
            {
                _dpadRepeatTimer = 0f;
            }
        }

        private void AdjustSlider(int direction)
        {
            int current = Mathf.RoundToInt(_slider.value);
            int next = Mathf.Clamp(current + direction, SliderMin, SliderMax);
            _slider.value = next; // triggers OnSliderChanged
        }

        // ══════════════════════════════════════════════════════════════════
        //  Slider callback
        // ══════════════════════════════════════════════════════════════════

        private void OnSliderChanged(float rawValue)
        {
            if (_companion == null) return;
            int steps = Mathf.RoundToInt(rawValue);
            float radius = steps * StepMeters;

            _companion.SetHomeRadius(radius);
            UpdateValueLabel(radius);

            if (_homeZoneVisual != null && _companion.HasHomePosition())
                _homeZoneVisual.Show(_companion.GetHomePosition(), radius);
        }

        private void UpdateValueLabel(float radius)
        {
            if (_valueLabel != null)
                _valueLabel.text = $"{radius:F0}m";
        }

        private void UpdateHintText()
        {
            if (_hintLabel == null) return;
            _hintLabel.text = ZInput.IsGamepadActive()
                ? "[D-Pad] Adjust   [A] Confirm   [B] Close"
                : "Drag slider  \u2022  Esc to close";
        }

        // ══════════════════════════════════════════════════════════════════
        //  Build UI
        // ══════════════════════════════════════════════════════════════════

        private void Build()
        {
            // Canvas
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 29;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            gameObject.AddComponent<GraphicRaycaster>();

            _font = ResolveFont();

            // Root panel
            _root = new GameObject("HomeZoneRoot", typeof(RectTransform), typeof(Image));
            _root.transform.SetParent(transform, false);
            var rootRT = _root.GetComponent<RectTransform>();
            rootRT.anchorMin = new Vector2(0.5f, 0.5f);
            rootRT.anchorMax = new Vector2(0.5f, 0.5f);
            rootRT.pivot     = new Vector2(0.5f, 0.5f);
            rootRT.sizeDelta = new Vector2(PanelW, PanelH);

            var rootImg = _root.GetComponent<Image>();
            rootImg.color = BgColor;
            rootImg.raycastTarget = true;

            BuildTitle();
            BuildCloseButton();
            BuildSliderRow();
            BuildHintText();

            _root.SetActive(false);
        }

        private void BuildTitle()
        {
            var go = new GameObject("Title", typeof(RectTransform));
            go.transform.SetParent(_root.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, 32f);
            rt.anchoredPosition = new Vector2(0f, -2f);

            _titleLabel = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) _titleLabel.font = _font;
            _titleLabel.text      = "Home Zone";
            _titleLabel.fontSize  = 16f;
            _titleLabel.color     = LabelColor;
            _titleLabel.fontStyle = FontStyles.Bold;
            _titleLabel.alignment = TextAlignmentOptions.Center;
            _titleLabel.raycastTarget = false;
        }

        private void BuildCloseButton()
        {
            var go = new GameObject("CloseBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(_root.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(28f, 28f);
            rt.anchoredPosition = new Vector2(-4f, -4f);
            go.GetComponent<Image>().color = new Color(0.3f, 0.15f, 0.1f, 0.7f);
            go.GetComponent<Button>().onClick.AddListener(Hide);

            var txtGO = new GameObject("X", typeof(RectTransform));
            txtGO.transform.SetParent(go.transform, false);
            var tmp = txtGO.AddComponent<TextMeshProUGUI>();
            if (_font != null) tmp.font = _font;
            tmp.text      = "X";
            tmp.fontSize  = 14f;
            tmp.color     = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            var txtRT = txtGO.GetComponent<RectTransform>();
            txtRT.anchorMin = Vector2.zero;
            txtRT.anchorMax = Vector2.one;
            txtRT.offsetMin = Vector2.zero;
            txtRT.offsetMax = Vector2.zero;
        }

        private void BuildSliderRow()
        {
            // Container
            var row = new GameObject("SliderRow", typeof(RectTransform));
            row.transform.SetParent(_root.transform, false);
            var rowRT = row.GetComponent<RectTransform>();
            rowRT.anchorMin = new Vector2(0f, 0.3f);
            rowRT.anchorMax = new Vector2(1f, 0.7f);
            rowRT.offsetMin = new Vector2(20f, 0f);
            rowRT.offsetMax = new Vector2(-20f, 0f);

            // Label: "Radius:"
            var labelGO = new GameObject("RadiusLabel", typeof(RectTransform));
            labelGO.transform.SetParent(row.transform, false);
            var label = labelGO.AddComponent<TextMeshProUGUI>();
            if (_font != null) label.font = _font;
            label.text      = "Radius:";
            label.fontSize  = 14f;
            label.color     = new Color(0.85f, 0.80f, 0.65f, 1f);
            label.alignment = TextAlignmentOptions.Left;
            label.raycastTarget = false;
            var lblRT = labelGO.GetComponent<RectTransform>();
            lblRT.anchorMin = new Vector2(0f, 0f);
            lblRT.anchorMax = new Vector2(0.2f, 1f);
            lblRT.offsetMin = Vector2.zero;
            lblRT.offsetMax = Vector2.zero;

            // Slider (middle 62%)
            var sliderGO = BuildSlider(row.transform);
            var sRT = sliderGO.GetComponent<RectTransform>();
            sRT.anchorMin = new Vector2(0.2f, 0.15f);
            sRT.anchorMax = new Vector2(0.82f, 0.85f);
            sRT.offsetMin = new Vector2(4f, 0f);
            sRT.offsetMax = new Vector2(-4f, 0f);

            // Value label: "50m"
            var valGO = new GameObject("ValueLabel", typeof(RectTransform));
            valGO.transform.SetParent(row.transform, false);
            _valueLabel = valGO.AddComponent<TextMeshProUGUI>();
            if (_font != null) _valueLabel.font = _font;
            _valueLabel.text      = "50m";
            _valueLabel.fontSize  = 16f;
            _valueLabel.color     = LabelColor;
            _valueLabel.fontStyle = FontStyles.Bold;
            _valueLabel.alignment = TextAlignmentOptions.Center;
            _valueLabel.raycastTarget = false;
            var valRT = valGO.GetComponent<RectTransform>();
            valRT.anchorMin = new Vector2(0.82f, 0f);
            valRT.anchorMax = new Vector2(1f, 1f);
            valRT.offsetMin = Vector2.zero;
            valRT.offsetMax = Vector2.zero;
        }

        private GameObject BuildSlider(Transform parent)
        {
            var go = new GameObject("RadiusSlider", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            // Background track
            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(go.transform, false);
            bgGO.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.15f, 0.6f);
            var bgRT = bgGO.GetComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero; bgRT.anchorMax = Vector2.one;
            bgRT.offsetMin = new Vector2(0f, 8f); bgRT.offsetMax = new Vector2(0f, -8f);

            // Fill area + fill image
            var fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
            fillAreaGO.transform.SetParent(go.transform, false);
            var fillAreaRT = fillAreaGO.GetComponent<RectTransform>();
            fillAreaRT.anchorMin = Vector2.zero; fillAreaRT.anchorMax = Vector2.one;
            fillAreaRT.offsetMin = new Vector2(5f, 8f); fillAreaRT.offsetMax = new Vector2(-5f, -8f);

            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(fillAreaGO.transform, false);
            fillGO.GetComponent<Image>().color = GoldColor;
            var fillRT = fillGO.GetComponent<RectTransform>();
            fillRT.anchorMin = Vector2.zero; fillRT.anchorMax = new Vector2(0f, 1f);
            fillRT.offsetMin = Vector2.zero; fillRT.offsetMax = Vector2.zero;

            // Handle slide area + handle
            var handleAreaGO = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleAreaGO.transform.SetParent(go.transform, false);
            var handleAreaRT = handleAreaGO.GetComponent<RectTransform>();
            handleAreaRT.anchorMin = Vector2.zero; handleAreaRT.anchorMax = Vector2.one;
            handleAreaRT.offsetMin = new Vector2(5f, 6f); handleAreaRT.offsetMax = new Vector2(-5f, -6f);

            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(handleAreaGO.transform, false);
            var handleImg = handleGO.GetComponent<Image>();
            handleImg.color = HandleColor;
            var handleRT = handleGO.GetComponent<RectTransform>();
            handleRT.sizeDelta = new Vector2(10f, 0f);
            handleRT.anchorMin = new Vector2(0f, 0f); handleRT.anchorMax = new Vector2(0f, 1f);

            _slider = go.AddComponent<Slider>();
            _slider.fillRect      = fillRT;
            _slider.handleRect    = handleRT;
            _slider.targetGraphic = handleImg;
            _slider.direction     = Slider.Direction.LeftToRight;
            _slider.minValue      = SliderMin;
            _slider.maxValue      = SliderMax;
            _slider.wholeNumbers  = true;
            _slider.value         = 10; // default 50m

            _slider.onValueChanged.AddListener(OnSliderChanged);

            return go;
        }

        private void BuildHintText()
        {
            var go = new GameObject("Hint", typeof(RectTransform));
            go.transform.SetParent(_root.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0.25f);
            rt.offsetMin = new Vector2(10f, 4f);
            rt.offsetMax = new Vector2(-10f, 0f);

            _hintLabel = go.AddComponent<TextMeshProUGUI>();
            if (_font != null) _hintLabel.font = _font;
            _hintLabel.fontSize  = 11f;
            _hintLabel.color     = new Color(0.6f, 0.6f, 0.6f, 0.8f);
            _hintLabel.alignment = TextAlignmentOptions.Center;
            _hintLabel.raycastTarget = false;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Font resolution (matches ConfigPanel pattern)
        // ══════════════════════════════════════════════════════════════════

        private static TMP_FontAsset ResolveFont()
        {
            if (InventoryGui.instance != null)
            {
                var texts = InventoryGui.instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] != null && texts[i].font != null &&
                        texts[i].font.name.IndexOf("LiberationSans",
                            System.StringComparison.OrdinalIgnoreCase) < 0)
                        return texts[i].font;
                }
            }
            if (Hud.instance != null)
            {
                var texts = Hud.instance.GetComponentsInChildren<TextMeshProUGUI>(true);
                for (int i = 0; i < texts.Length; i++)
                {
                    if (texts[i] != null && texts[i].font != null &&
                        texts[i].font.name.IndexOf("LiberationSans",
                            System.StringComparison.OrdinalIgnoreCase) < 0)
                        return texts[i].font;
                }
            }
            return TMP_Settings.defaultFontAsset;
        }
    }
}
