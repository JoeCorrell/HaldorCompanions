using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Companions
{
    /// <summary>
    /// Full-screen overlay that shows the companion customisation panel on first
    /// world entry. Wraps CompanionPanel in StarterMode so the player can choose
    /// their starter companion's appearance before spawning.
    /// </summary>
    public class StarterCompanionPanel : MonoBehaviour
    {
        public static StarterCompanionPanel Instance { get; private set; }
        public bool IsVisible => _panel?.Root != null && _panel.Root.activeSelf;

        private CompanionPanel _panel;
        private GameObject _backdrop;
        private Canvas _canvas;
        private GameObject _container;

        // Background sprite cache
        private static Sprite _panelBgSprite;

        // ── Show / Hide API ─────────────────────────────────────────────────

        public static void ShowPanel()
        {
            if (Instance != null) return;

            var go = new GameObject("HC_StarterCompanionPanel");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<StarterCompanionPanel>();
        }

        // ── Background sprite ──────────────────────────────────────────────

        private static Sprite GetPanelBgSprite()
        {
            if (_panelBgSprite != null) return _panelBgSprite;
            var tex = TextureLoader.LoadUITexture("PanelBackground");
            if (tex == null) return null;
            _panelBgSprite = Sprite.Create(tex,
                new Rect(0f, 0f, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);
            _panelBgSprite.name = "StarterPanelBackground";
            return _panelBgSprite;
        }

        // ── Lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            // Canvas — screen-space overlay above HUD
            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 30;
            gameObject.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            gameObject.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1920f, 1080f);
            gameObject.AddComponent<GraphicRaycaster>();

            // Dark backdrop
            _backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
            _backdrop.transform.SetParent(transform, false);
            var bdRT = _backdrop.GetComponent<RectTransform>();
            bdRT.anchorMin = Vector2.zero;
            bdRT.anchorMax = Vector2.one;
            bdRT.offsetMin = Vector2.zero;
            bdRT.offsetMax = Vector2.zero;
            _backdrop.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.85f);

            // Centered panel container with background texture
            _container = new GameObject("PanelContainer", typeof(RectTransform), typeof(Image));
            _container.transform.SetParent(transform, false);
            var cRT = _container.GetComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0.5f, 0.5f);
            cRT.anchorMax = new Vector2(0.5f, 0.5f);
            cRT.pivot = new Vector2(0.5f, 0.5f);
            cRT.sizeDelta = new Vector2(920f, 560f);

            // Apply PanelBackground sprite (matches CompanionInteractPanel style)
            var containerImg = _container.GetComponent<Image>();
            var bgSprite = GetPanelBgSprite();
            if (bgSprite != null)
            {
                containerImg.sprite = bgSprite;
                containerImg.type = Image.Type.Simple;
                containerImg.color = Color.white;
            }
            else
            {
                containerImg.color = new Color(0.05f, 0.05f, 0.08f, 0.95f);
            }
            containerImg.raycastTarget = true;

            // Resolve font and button template from existing UI
            TMP_FontAsset font = ResolveFont();
            GameObject buttonTemplate = ResolveButtonTemplate();

            CompanionsPlugin.Log.LogDebug(
                $"[StarterPanel] Font={font?.name ?? "null"}, " +
                $"ButtonTemplate={buttonTemplate?.name ?? "null"}");

            // Build the companion panel in starter mode
            _panel = new CompanionPanel { StarterMode = true };
            _panel.OnSpawnConfirmed = OnConfirm;

            // Column layout: left (customisation), middle (description + button), right (preview)
            float panelW = 920f;
            float panelH = 560f;
            float pad = 6f;
            float colGap = 6f;
            float colW = (panelW - pad * 2 - colGap * 2) / 3f;
            float leftXL = pad;
            float leftXR = leftXL + colW;
            float midXL = leftXR + colGap;
            float midXR = midXL + colW;
            float rightXL = midXR + colGap;
            float rightXR = rightXL + colW;

            _panel.Build(_container.transform,
                colTopInset: 6f,
                bottomPad: 6f,
                font: font,
                buttonTemplate: buttonTemplate,
                buttonHeight: 48f,
                leftXL: leftXL, leftXR: leftXR,
                midXL: midXL, midXR: midXR,
                rightXL: rightXL, rightXR: rightXR,
                panelHeight: panelH);

            _panel.Root.SetActive(true);

            // Close button (top-right X)
            BuildCloseButton(_container.transform, font);

            // Enable gamepad navigation on all interactive elements
            EnableGamepadNavigation();

            // Delay Refresh() by one frame so ObjectDB/ZNetScene are ready
            _refreshPending = true;

            CompanionsPlugin.Log.LogInfo("[StarterPanel] Opened customisation panel");
        }

        /// <summary>
        /// Switches all Selectables to Automatic navigation so D-pad / stick
        /// can move between buttons and sliders. Adds color-tint feedback for
        /// the currently focused element.
        /// </summary>
        private void EnableGamepadNavigation()
        {
            var highlightColor = new Color(1f, 0.85f, 0.5f, 1f);
            var pressedColor   = new Color(0.9f, 0.7f, 0.3f, 1f);

            foreach (var selectable in _container.GetComponentsInChildren<Selectable>(true))
            {
                selectable.navigation = new Navigation { mode = Navigation.Mode.Automatic };

                // Add visual feedback for gamepad selection
                if (selectable.transition == Selectable.Transition.None)
                {
                    selectable.transition = Selectable.Transition.ColorTint;
                    var colors = selectable.colors;
                    colors.highlightedColor = highlightColor;
                    colors.selectedColor    = highlightColor;
                    colors.pressedColor     = pressedColor;
                    selectable.colors = colors;
                }
            }
        }

        private bool _refreshPending;

        private void Update()
        {
            if (_refreshPending)
            {
                _refreshPending = false;
                _panel?.Refresh();
                CompanionsPlugin.Log.LogDebug("[StarterPanel] Initial Refresh() complete");
            }

            _panel?.UpdatePerFrame();

            // Keep cursor visible while panel is active (game doesn't know about our overlay)
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Gamepad: ensure something is selected for D-pad navigation
            if (ZInput.IsGamepadActive())
            {
                var es = EventSystem.current;
                if (es != null && es.currentSelectedGameObject == null)
                {
                    var first = _container.GetComponentInChildren<Selectable>(false);
                    if (first != null)
                        es.SetSelectedGameObject(first.gameObject);
                }
            }

            // Escape / controller B to close/skip (will re-appear next session)
            if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyBack"))
            {
                CompanionsPlugin.Log.LogInfo("[StarterPanel] Skipped — will re-appear next session");
                Close();
            }
        }

        private void OnDestroy()
        {
            _panel?.Teardown();
            _panel = null;
            if (Instance == this) Instance = null;
            CompanionsPlugin.Log.LogDebug("[StarterPanel] Destroyed");
        }

        // ── Callbacks ───────────────────────────────────────────────────────

        private void OnConfirm(CompanionAppearance appearance)
        {
            CompanionManager.SpawnStarterWithAppearance(appearance);
            CompanionsPlugin.Log.LogInfo("[StarterPanel] Companion spawned — closing panel");
            Close();
        }

        private void Close()
        {
            Destroy(gameObject);  // OnDestroy handles Teardown + Instance cleanup
        }

        // ── Close button ────────────────────────────────────────────────────

        private void BuildCloseButton(Transform parent, TMP_FontAsset font)
        {
            var go = new GameObject("CloseButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(36f, 36f);
            rt.anchoredPosition = new Vector2(-2f, -2f);

            go.GetComponent<Image>().color = new Color(0.3f, 0.1f, 0.1f, 0.9f);
            go.GetComponent<Button>().onClick.AddListener(() =>
            {
                CompanionsPlugin.Log.LogInfo("[StarterPanel] Skipped via close button — will re-appear next session");
                Close();
            });

            var txtGO = new GameObject("X", typeof(RectTransform));
            txtGO.transform.SetParent(go.transform, false);
            var tmp = txtGO.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.text = "X";
            tmp.fontSize = 20f;
            tmp.color = Color.white;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            var tRT = txtGO.GetComponent<RectTransform>();
            tRT.anchorMin = Vector2.zero;
            tRT.anchorMax = Vector2.one;
            tRT.offsetMin = Vector2.zero;
            tRT.offsetMax = Vector2.zero;
        }

        // ── Resolve font / button template from existing UI ─────────────────

        private static TMP_FontAsset ResolveFont()
        {
            // Try InventoryGui first, then Hud, then any TMP_Text in the scene
            if (InventoryGui.instance != null)
            {
                var txt = InventoryGui.instance.GetComponentInChildren<TMP_Text>(true);
                if (txt?.font != null) return txt.font;
            }
            if (Hud.instance != null)
            {
                var txt = Hud.instance.GetComponentInChildren<TMP_Text>(true);
                if (txt?.font != null) return txt.font;
            }
            return null;
        }

        private static GameObject ResolveButtonTemplate()
        {
            if (InventoryGui.instance != null && InventoryGui.instance.m_craftButton != null)
                return InventoryGui.instance.m_craftButton.gameObject;
            return null;
        }
    }
}
