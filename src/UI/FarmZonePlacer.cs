using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace Companions
{
    /// <summary>
    /// Placement mode for creating/deleting farm zones on the ground.
    /// Entered from radial menu "Farm Zones" action.
    /// Raycast from camera to ground, preview rectangle follows crosshair,
    /// scroll wheel resizes, LMB places, RMB deletes, Escape exits.
    /// </summary>
    public class FarmZonePlacer : MonoBehaviour
    {
        public static FarmZonePlacer Instance { get; private set; }
        public bool IsVisible => _active;

        // ── State ────────────────────────────────────────────────────────────
        private bool _active;
        private CompanionSetup _companion;
        private ZNetView _companionNview;
        private FarmZoneVisual _zoneVisual;
        private List<FarmZone> _zones;

        // ── Preview ──────────────────────────────────────────────────────────
        private float _previewHalfSize = 4f;
        private float _previewRotation;
        private const float MinHalfSize = 2f;
        private const float MaxHalfSize = 15f;
        private const float SizeStep = 1f;
        private const float RotationStep = 15f;
        private LineRenderer _previewBorder;
        private GameObject _previewQuad;
        private Material _previewBorderMat;
        private Material _previewQuadMat;
        private bool _previewValid;
        private Vector3 _previewCenter;

        // ── RMB tracking (distinguish rotate from delete) ────────────────────
        private bool _rmbScrolled;

        // ── Camera zoom suppression ──────────────────────────────────────────
        private float _savedZoomSens;

        // ── Raycast ──────────────────────────────────────────────────────────
        private int _groundMask;
        private const float MaxRayDist = 50f;

        // ── HUD ──────────────────────────────────────────────────────────────
        private GameObject _hudRoot;
        private TextMeshProUGUI _hintLabel;
        private TextMeshProUGUI _sizeLabel;
        private Canvas _hudCanvas;

        // ── Pending zone (awaiting crop pick) ────────────────────────────────
        private FarmZone _pendingZone;
        private bool _pickingCrop;

        // ── Style ────────────────────────────────────────────────────────────
        private static readonly Color PreviewColor = new Color(0.3f, 0.85f, 0.3f, 0.7f);
        private static readonly Color PreviewQuadColor = new Color(0.3f, 0.85f, 0.3f, 0.12f);
        private static readonly Color InvalidColor = new Color(0.85f, 0.3f, 0.3f, 0.7f);

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        public static void EnsureInstance()
        {
            if (Instance != null) return;
            var go = new GameObject("HC_FarmZonePlacer");
            DontDestroyOnLoad(go);
            go.AddComponent<FarmZonePlacer>();
        }

        public static void Enter(CompanionSetup companion)
        {
            if (companion == null) return;
            EnsureInstance();
            Instance.Show(companion);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            _groundMask = LayerMask.GetMask("Default", "static_solid", "terrain",
                                             "piece", "piece_nonsolid");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            CleanupPreview();
            CleanupHud();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Show / Hide
        // ══════════════════════════════════════════════════════════════════════

        private void Show(CompanionSetup companion)
        {
            _companion = companion;
            _companionNview = companion.GetComponent<ZNetView>();
            _zoneVisual = companion.GetComponent<FarmZoneVisual>();

            // Load existing zones
            var zdo = _companionNview?.GetZDO();
            _zones = zdo != null ? FarmZoneSerializer.Load(zdo) : new List<FarmZone>();

            // Show existing zones
            _zoneVisual?.ShowZones(_zones);

            // Create preview if needed
            if (_previewBorder == null) BuildPreview();
            if (_hudRoot == null) BuildHud();

            _previewHalfSize = 4f;
            _previewRotation = 0f;
            _rmbScrolled = false;
            _pickingCrop = false;
            _active = true;

            // Block camera zoom — scroll is used for resize/rotate
            if (GameCamera.instance != null)
            {
                _savedZoomSens = GameCamera.instance.m_zoomSens;
                GameCamera.instance.m_zoomSens = 0f;
            }
            _hudRoot.SetActive(true);
            UpdateHintText();
        }

        public void Hide()
        {
            _active = false;
            _pickingCrop = false;
            if (_previewBorder != null) _previewBorder.enabled = false;
            if (_previewQuad != null) _previewQuad.SetActive(false);
            if (_hudRoot != null) _hudRoot.SetActive(false);
            _zoneVisual?.HideAll();

            // Restore camera zoom
            if (GameCamera.instance != null && _savedZoomSens > 0f)
                GameCamera.instance.m_zoomSens = _savedZoomSens;

            // Restore cursor
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Update
        // ══════════════════════════════════════════════════════════════════════

        private void Update()
        {
            // ── Global hotkey: Modifier + Key to toggle farm zone editor ──
            if (!_active)
            {
                if (Player.m_localPlayer != null && !InventoryGui.IsVisible()
                    && !Menu.IsVisible() && !Minimap.IsOpen()
                    && !FarmZoneCropPicker.IsOpen
                    && Input.GetKeyDown(ModConfig.FarmZoneKey.Value)
                    && Input.GetKey(ModConfig.FarmZoneModifier.Value))
                {
                    var companion = FindNearestCompanion();
                    if (companion != null)
                        Enter(companion);
                }
                return;
            }

            // Companion destroyed or unloaded
            if (_companion == null || !_companion)
            {
                Hide();
                return;
            }

            // If crop picker is open, let it handle input
            if (_pickingCrop) return;

            // Close on Escape, Start button, or same hotkey
            if (Input.GetKeyDown(KeyCode.Escape) || ZInput.GetButtonDown("JoyMenu")
                || (Input.GetKeyDown(ModConfig.FarmZoneKey.Value)
                    && Input.GetKey(ModConfig.FarmZoneModifier.Value)))
            {
                Hide();
                return;
            }

            UpdateRaycast();
            UpdatePreview();
            UpdateInput();
            UpdateHintText();
        }

        private static CompanionSetup FindNearestCompanion()
        {
            var player = Player.m_localPlayer;
            if (player == null) return null;
            var pos = player.transform.position;
            CompanionSetup best = null;
            float bestDist = float.MaxValue;
            foreach (var c in CompanionSetup.AllCompanions)
            {
                if (c == null) continue;
                float d = Vector3.Distance(pos, c.transform.position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best;
        }

        private void UpdateRaycast()
        {
            _previewValid = false;
            var cam = GameCamera.instance;
            if (cam == null) return;

            var camTransform = cam.transform;
            Ray ray = new Ray(camTransform.position, camTransform.forward);
            RaycastHit hit;
            if (Physics.Raycast(ray, out hit, MaxRayDist, _groundMask))
            {
                // Reject rigidbodies (movable objects)
                if (hit.collider.attachedRigidbody != null) return;

                _previewCenter = hit.point;
                _previewValid = true;
            }
        }

        private void UpdatePreview()
        {
            if (!_previewValid)
            {
                _previewBorder.enabled = false;
                _previewQuad.SetActive(false);
                return;
            }

            Color borderColor = PreviewColor;

            float y = _previewCenter.y + 0.12f;
            float hs = _previewHalfSize;

            // Build a temporary FarmZone to get rotated corners
            var preview = new FarmZone
            {
                Center = _previewCenter,
                HalfSize = hs,
                Rotation = _previewRotation
            };
            Vector3 c0, c1, c2, c3;
            preview.GetCorners(out c0, out c1, out c2, out c3);
            c0.y = y; c1.y = y; c2.y = y; c3.y = y;

            _previewBorder.SetPosition(0, c0);
            _previewBorder.SetPosition(1, c1);
            _previewBorder.SetPosition(2, c2);
            _previewBorder.SetPosition(3, c3);
            _previewBorder.startColor = borderColor;
            _previewBorder.endColor = borderColor;
            _previewBorder.enabled = true;

            // Position + rotate quad
            _previewQuad.transform.position = new Vector3(_previewCenter.x, y + 0.01f, _previewCenter.z);
            _previewQuad.transform.rotation = Quaternion.Euler(90f, _previewRotation, 0f);
            _previewQuad.transform.localScale = new Vector3(hs * 2f, 1f, hs * 2f);
            Color quadColor = PreviewQuadColor;
            _previewQuadMat.color = quadColor;
            _previewQuad.SetActive(true);

            // Update size label
            if (_sizeLabel != null)
            {
                string rotText = Mathf.Abs(_previewRotation) > 0.1f
                    ? $"  ({_previewRotation:F0}\u00B0)" : "";
                _sizeLabel.text = $"{hs * 2f:F0}m x {hs * 2f:F0}m{rotText}";
            }
        }

        private void UpdateInput()
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            bool rmbHeld = Input.GetMouseButton(1);

            if (rmbHeld && !ZInput.IsGamepadActive())
            {
                // RMB held + scroll = rotate
                if (scroll > 0.01f)
                {
                    _previewRotation = Mathf.Repeat(_previewRotation + RotationStep, 360f);
                    _rmbScrolled = true;
                }
                else if (scroll < -0.01f)
                {
                    _previewRotation = Mathf.Repeat(_previewRotation - RotationStep, 360f);
                    _rmbScrolled = true;
                }
            }
            else
            {
                // Scroll wheel (no RMB) = resize
                if (scroll > 0.01f)
                    _previewHalfSize = Mathf.Min(_previewHalfSize + SizeStep, MaxHalfSize);
                else if (scroll < -0.01f)
                    _previewHalfSize = Mathf.Max(_previewHalfSize - SizeStep, MinHalfSize);
            }

            // RMB released without scrolling = delete zone under crosshair
            if (Input.GetMouseButtonDown(1) && !ZInput.IsGamepadActive())
                _rmbScrolled = false;
            if (Input.GetMouseButtonUp(1) && !ZInput.IsGamepadActive() && !_rmbScrolled)
                TryDelete();

            // Gamepad bumpers to resize
            if (ZInput.GetButtonDown("JoyLTrigger"))
                _previewHalfSize = Mathf.Max(_previewHalfSize - SizeStep, MinHalfSize);
            if (ZInput.GetButtonDown("JoyRTrigger"))
                _previewHalfSize = Mathf.Min(_previewHalfSize + SizeStep, MaxHalfSize);

            // Gamepad D-pad up/down to rotate
            if (ZInput.GetButtonDown("JoyDPadUp"))
                _previewRotation = Mathf.Repeat(_previewRotation + RotationStep, 360f);
            if (ZInput.GetButtonDown("JoyDPadDown"))
                _previewRotation = Mathf.Repeat(_previewRotation - RotationStep, 360f);

            // LMB / A button to place
            if ((Input.GetMouseButtonDown(0) && !ZInput.IsGamepadActive()) ||
                ZInput.GetButtonDown("JoyButtonA"))
            {
                TryPlace();
            }

            // Gamepad X button to delete
            if (ZInput.GetButtonDown("JoyButtonX"))
                TryDelete();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Place / Delete
        // ══════════════════════════════════════════════════════════════════════

        private void TryPlace()
        {
            if (!_previewValid) return;

            _pendingZone = new FarmZone
            {
                Center = _previewCenter,
                HalfSize = _previewHalfSize,
                Rotation = _previewRotation,
                CropSeed = ""
            };

            // Hide preview while crop picker is open
            _previewBorder.enabled = false;
            _previewQuad.SetActive(false);
            _pickingCrop = true;

            // Open crop picker
            FarmZoneCropPicker.Show(_pendingZone, OnCropSelected, OnCropCancelled);
        }

        private void OnCropSelected(string seedPrefabName)
        {
            _pendingZone.CropSeed = seedPrefabName ?? "";
            _zones.Add(_pendingZone);
            SaveZones();
            _zoneVisual?.ShowZones(_zones);
            _pickingCrop = false;
        }

        private void OnCropCancelled()
        {
            _pickingCrop = false;
        }

        private void TryDelete()
        {
            if (!_previewValid) return;

            // Find zone under crosshair
            for (int i = _zones.Count - 1; i >= 0; i--)
            {
                if (_zones[i].Contains(_previewCenter))
                {
                    _zones.RemoveAt(i);
                    SaveZones();
                    _zoneVisual?.ShowZones(_zones);
                    return;
                }
            }
        }

        private void SaveZones()
        {
            var zdo = _companionNview?.GetZDO();
            if (zdo != null)
                FarmZoneSerializer.Save(zdo, _zones);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Preview construction
        // ══════════════════════════════════════════════════════════════════════

        private void BuildPreview()
        {
            // Border LineRenderer
            var borderGO = new GameObject("FarmZonePreviewBorder");
            borderGO.transform.SetParent(transform, false);
            _previewBorderMat = new Material(Shader.Find("Sprites/Default"));
            _previewBorder = borderGO.AddComponent<LineRenderer>();
            _previewBorder.useWorldSpace = true;
            _previewBorder.loop = true;
            _previewBorder.positionCount = 4;
            _previewBorder.startWidth = 0.08f;
            _previewBorder.endWidth = 0.08f;
            _previewBorder.material = _previewBorderMat;
            _previewBorder.startColor = PreviewColor;
            _previewBorder.endColor = PreviewColor;
            _previewBorder.shadowCastingMode = ShadowCastingMode.Off;
            _previewBorder.receiveShadows = false;
            _previewBorder.enabled = false;

            // Semi-transparent quad fill
            _previewQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _previewQuad.name = "FarmZonePreviewFill";
            _previewQuad.transform.SetParent(transform, false);
            _previewQuad.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            Object.Destroy(_previewQuad.GetComponent<Collider>());
            _previewQuadMat = new Material(Shader.Find("Sprites/Default"));
            _previewQuadMat.color = PreviewQuadColor;
            _previewQuad.GetComponent<MeshRenderer>().material = _previewQuadMat;
            _previewQuad.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
            _previewQuad.GetComponent<MeshRenderer>().receiveShadows = false;
            _previewQuad.SetActive(false);
        }

        private void CleanupPreview()
        {
            if (_previewBorderMat != null) Destroy(_previewBorderMat);
            if (_previewQuadMat != null) Destroy(_previewQuadMat);
            if (_previewBorder != null) Destroy(_previewBorder.gameObject);
            if (_previewQuad != null) Destroy(_previewQuad);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  HUD hint bar
        // ══════════════════════════════════════════════════════════════════════

        private void BuildHud()
        {
            var canvasGO = new GameObject("HC_FarmZoneHud",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _hudCanvas = canvasGO.GetComponent<Canvas>();
            _hudCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _hudCanvas.sortingOrder = 28;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            _hudRoot = new GameObject("HudRoot", typeof(RectTransform), typeof(Image));
            _hudRoot.transform.SetParent(canvasGO.transform, false);
            var rootRT = _hudRoot.GetComponent<RectTransform>();
            rootRT.anchorMin = new Vector2(0.5f, 0f);
            rootRT.anchorMax = new Vector2(0.5f, 0f);
            rootRT.pivot = new Vector2(0.5f, 0f);
            rootRT.sizeDelta = new Vector2(500f, 60f);
            rootRT.anchoredPosition = new Vector2(0f, 20f);
            _hudRoot.GetComponent<Image>().color = new Color(0.08f, 0.07f, 0.06f, 0.85f);

            var font = ResolveFont();

            // Hint text
            var hintGO = new GameObject("Hint", typeof(RectTransform));
            hintGO.transform.SetParent(_hudRoot.transform, false);
            _hintLabel = hintGO.AddComponent<TextMeshProUGUI>();
            if (font != null) _hintLabel.font = font;
            _hintLabel.fontSize = 14f;
            _hintLabel.color = new Color(0.85f, 0.80f, 0.65f, 1f);
            _hintLabel.alignment = TextAlignmentOptions.Center;
            _hintLabel.raycastTarget = false;
            var hintRT = hintGO.GetComponent<RectTransform>();
            hintRT.anchorMin = new Vector2(0f, 0.5f);
            hintRT.anchorMax = new Vector2(1f, 1f);
            hintRT.offsetMin = new Vector2(10f, 0f);
            hintRT.offsetMax = new Vector2(-10f, 0f);

            // Size label
            var sizeGO = new GameObject("Size", typeof(RectTransform));
            sizeGO.transform.SetParent(_hudRoot.transform, false);
            _sizeLabel = sizeGO.AddComponent<TextMeshProUGUI>();
            if (font != null) _sizeLabel.font = font;
            _sizeLabel.fontSize = 12f;
            _sizeLabel.color = new Color(0.6f, 0.6f, 0.6f, 0.8f);
            _sizeLabel.alignment = TextAlignmentOptions.Center;
            _sizeLabel.raycastTarget = false;
            var sizeRT = sizeGO.GetComponent<RectTransform>();
            sizeRT.anchorMin = new Vector2(0f, 0f);
            sizeRT.anchorMax = new Vector2(1f, 0.5f);
            sizeRT.offsetMin = new Vector2(10f, 0f);
            sizeRT.offsetMax = new Vector2(-10f, 0f);

            _hudRoot.SetActive(false);
        }

        private void UpdateHintText()
        {
            if (_hintLabel == null) return;
            _hintLabel.text = ZInput.IsGamepadActive()
                ? ModLocalization.Loc("hc_farmzone_hint_gamepad")
                : ModLocalization.Loc("hc_farmzone_hint");
        }

        private void CleanupHud()
        {
            if (_hudCanvas != null) Destroy(_hudCanvas.gameObject);
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
                            System.StringComparison.OrdinalIgnoreCase) < 0)
                        return texts[i].font;
                }
            }
            return TMP_Settings.defaultFontAsset;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Harmony: block player placement while active
        // ══════════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Player), "UpdatePlacement")]
        private static class BlockPlacement
        {
            static bool Prefix() => Instance == null || !Instance._active;
        }
    }
}
