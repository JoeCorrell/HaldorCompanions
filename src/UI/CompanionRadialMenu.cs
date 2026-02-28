using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Companions
{
    /// <summary>
    /// Radial command wheel for companion actions. Opens on hold-E,
    /// stays open for multiple selections, closes on Esc/B.
    /// Completely independent of InventoryGui.
    /// </summary>
    public class CompanionRadialMenu : MonoBehaviour
    {
        public static CompanionRadialMenu Instance { get; private set; }

        // ── Public state ───────────────────────────────────────────────────
        public bool IsVisible => _visible;
        public CompanionSetup CurrentCompanion => _companion;

        public static bool IsOpenFor(CompanionSetup setup)
            => Instance != null && Instance._visible
            && Instance._companion != null && Instance._companion == setup;

        // ── Action IDs ─────────────────────────────────────────────────────
        private const int ActionFollow      = 0;
        private const int ActionGatherWood  = 1;
        private const int ActionGatherStone = 2;
        private const int ActionGatherOre   = 3;
        private const int ActionStayHome    = 10;
        private const int ActionSetHome     = 11;
        private const int ActionWander      = 12;
        private const int ActionAutoPickup  = 13;
        private const int ActionCommand     = 14;

        // ── Style ──────────────────────────────────────────────────────────
        private static readonly Color BgColor          = new Color(0.08f, 0.06f, 0.04f, 0.85f);
        private static readonly Color HighlightHover   = new Color(0.83f, 0.64f, 0.31f, 0.30f);
        private static readonly Color HighlightActive  = new Color(0.45f, 0.35f, 0.18f, 0.15f);
        private static readonly Color TextNormal       = new Color(0.85f, 0.80f, 0.65f, 1f);
        private static readonly Color TextHover        = new Color(1f, 0.95f, 0.80f, 1f);
        private static readonly Color ActiveDot        = new Color(0.40f, 0.85f, 0.40f, 1f);
        private static readonly Color InactiveDot      = new Color(0.5f, 0.5f, 0.5f, 0.5f);

        private const float RingRadius     = 140f;
        private const float SegSize        = 80f;
        private const float HighlightSize  = 62f;
        private const float IconSize       = 36f;
        private const float DeadZonePx     = 30f;
        private const float DeadZoneStick  = 0.3f;

        // ── Speech pools ────────────────────────────────────────────────
        private static readonly string[] ActionSpeech = {
            "On it!", "As you wish.", "Consider it done.", "Right away!"
        };

        // ── Segment data ─────────────────────────────────────────────────
        private struct Segment
        {
            public string Label;
            public int    ActionId;
            public bool   IsToggle;
            public bool   IsMode;
            public bool   IsActive;
            public Color  IconColor;
        }

        // ── Companion refs ───────────────────────────────────────────────
        private CompanionSetup    _companion;
        private ZNetView          _companionNview;
        private CompanionAI       _companionAI;
        private HarvestController _companionHarvest;
        private CompanionTalk     _companionTalk;
        private bool              _isDverger;

        // ── UI elements ──────────────────────────────────────────────────
        private bool      _visible;
        private bool      _built;
        private Canvas    _canvas;
        private GameObject _root;
        private GameObject _bgCircle;
        private TextMeshProUGUI _centerName;
        private TextMeshProUGUI _centerAction;
        private TextMeshProUGUI _centerState;

        private readonly List<Segment>    _segments   = new List<Segment>();
        private readonly List<GameObject> _segmentGOs = new List<GameObject>();
        private readonly List<Image>      _segHighlights = new List<Image>();
        private readonly List<Image>      _segIcons   = new List<Image>();
        private readonly List<TextMeshProUGUI> _segLabels = new List<TextMeshProUGUI>();
        private readonly List<Image>      _segDots    = new List<Image>();
        private int _hoveredIndex = -1;

        /// <summary>
        /// Tracks whether the Use key has been released at least once since
        /// the radial opened. Prevents the initial hold-to-open from
        /// immediately triggering an action execution.
        /// </summary>
        private bool _useReleasedSinceOpen;

        // ── Icon cache ───────────────────────────────────────────────────
        private static readonly Dictionary<int, Sprite> _iconCache = new Dictionary<int, Sprite>();

        // ══════════════════════════════════════════════════════════════════
        //  Singleton
        // ══════════════════════════════════════════════════════════════════

        internal static void EnsureInstance()
        {
            if (Instance != null) return;
            var go = new GameObject("HC_CompanionRadialMenu");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<CompanionRadialMenu>();
        }

        private void Awake()  { Instance = this; }
        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Teardown();
        }

        // ══════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════

        public void Show(CompanionSetup companion)
        {
            if (companion == null) return;

            _companion        = companion;
            _companionNview   = companion.GetComponent<ZNetView>();
            _companionAI      = companion.GetComponent<CompanionAI>();
            _companionHarvest = companion.GetComponent<HarvestController>();
            _companionTalk    = companion.GetComponent<CompanionTalk>();
            _isDverger        = !companion.CanWearArmor();

            BuildSegments();

            if (!_built) BuildUI();
            PopulateUI();

            _root.SetActive(true);
            _visible = true;
            _hoveredIndex = -1;
            _useReleasedSinceOpen = false;

            // Unlock cursor for mouse selection (Hud.InRadial patch handles
            // subsequent frames via GameCamera.UpdateMouseCapture)
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            CompanionsPlugin.Log.LogDebug(
                $"[Radial] Show — companion=\"{companion.name}\" segments={_segments.Count}");
        }

        public void Hide()
        {
            if (!_visible) return;
            _visible = false;
            if (_root != null) _root.SetActive(false);

            _companion        = null;
            _companionNview   = null;
            _companionAI      = null;
            _companionHarvest = null;
            _hoveredIndex     = -1;

            // Let GameCamera.UpdateMouseCapture restore cursor state
            // on the next frame via its normal checks.

            CompanionsPlugin.Log.LogDebug("[Radial] Hide");
        }

        // ══════════════════════════════════════════════════════════════════
        //  Update — selection tracking, execute on click, close on Esc/B
        // ══════════════════════════════════════════════════════════════════

        private void Update()
        {
            if (!_visible) return;

            // Companion destroyed?
            if (_companion == null || !_companion)
            { Hide(); return; }

            // Close on Esc or B (keyboard), or gamepad B/Back
            if (ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetKeyDown(KeyCode.B)
                || ZInput.GetButtonDown("JoyBack"))
            {
                Hide();
                return;
            }

            // Track whether the initial hold-E has been released
            if (!_useReleasedSinceOpen)
            {
                if (!ZInput.GetButton("Use") && !ZInput.GetButton("JoyUse"))
                    _useReleasedSinceOpen = true;
            }

            UpdateSelection();

            // Execute on left click, or E/Use tap (after initial release)
            if (_useReleasedSinceOpen &&
                (Input.GetMouseButtonDown(0)
                || ZInput.GetButtonDown("Use") || ZInput.GetButtonDown("JoyUse")))
            {
                ExecuteSelectedAction();
                RefreshSegmentStates();
            }

            RefreshVisuals();
        }

        private void UpdateSelection()
        {
            if (_segments.Count == 0) return;

            Vector2 input;
            float deadZone;

            if (ZInput.IsGamepadActive())
            {
                float lx = ZInput.GetJoyLeftStickX();
                float ly = ZInput.GetJoyLeftStickY();
                input = new Vector2(lx, ly);
                deadZone = DeadZoneStick;
            }
            else
            {
                Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                input = (Vector2)Input.mousePosition - center;
                deadZone = DeadZonePx;
            }

            if (input.magnitude < deadZone) return;

            float angle = Mathf.Atan2(input.y, input.x) * Mathf.Rad2Deg;
            if (angle < 0f) angle += 360f;

            // Segments arranged clockwise from top (90 degrees)
            float segArc = 360f / _segments.Count;
            float adjusted = (90f - angle + 360f + segArc * 0.5f) % 360f;
            int index = Mathf.FloorToInt(adjusted / segArc);
            index = Mathf.Clamp(index, 0, _segments.Count - 1);

            _hoveredIndex = index;
        }

        private void RefreshVisuals()
        {
            for (int i = 0; i < _segmentGOs.Count; i++)
            {
                bool hovered = i == _hoveredIndex;
                var seg = _segments[i];

                // Highlight circle
                if (_segHighlights[i] != null)
                {
                    Color hlCol;
                    if (hovered)
                        hlCol = HighlightHover;
                    else if (seg.IsActive && (seg.IsToggle || seg.IsMode))
                        hlCol = HighlightActive;
                    else
                        hlCol = Color.clear;
                    _segHighlights[i].color = hlCol;
                }

                // Label
                if (_segLabels[i] != null)
                    _segLabels[i].color = hovered ? TextHover : TextNormal;

                // Icon brightness
                if (_segIcons[i] != null)
                    _segIcons[i].color = hovered ? Color.white : new Color(0.9f, 0.9f, 0.9f, 0.85f);
            }

            // Center text
            if (_centerAction != null)
            {
                if (_hoveredIndex >= 0 && _hoveredIndex < _segments.Count)
                {
                    var seg = _segments[_hoveredIndex];
                    _centerAction.text = seg.Label;
                    if (seg.IsToggle || seg.IsMode)
                        _centerState.text = seg.IsActive ? "ON" : "OFF";
                    else
                        _centerState.text = "";
                }
                else
                {
                    _centerAction.text = "";
                    _centerState.text = "";
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Action execution
        // ══════════════════════════════════════════════════════════════════

        private void ExecuteSelectedAction()
        {
            if (_hoveredIndex < 0 || _hoveredIndex >= _segments.Count) return;
            if (_companion == null || _companionNview == null) return;

            var seg = _segments[_hoveredIndex];
            EnsureOwnership();

            CompanionsPlugin.Log.LogDebug($"[Radial] Execute action={seg.ActionId} label=\"{seg.Label}\"");

            switch (seg.ActionId)
            {
                case ActionFollow:
                case ActionGatherWood:
                case ActionGatherStone:
                case ActionGatherOre:
                    SetActionMode(seg.ActionId);
                    break;
                case ActionStayHome:
                    ToggleStayHome();
                    break;
                case ActionSetHome:
                    DoSetHome();
                    break;
                case ActionWander:
                    ToggleWander();
                    break;
                case ActionAutoPickup:
                    ToggleAutoPickup();
                    break;
                case ActionCommand:
                    ToggleCommandable();
                    break;
            }

            // Overhead speech on any action
            if (_companionTalk != null && ActionSpeech.Length > 0)
                _companionTalk.Say(ActionSpeech[UnityEngine.Random.Range(0, ActionSpeech.Length)]);
        }

        private void SetActionMode(int mode)
        {
            var zdo = _companionNview.GetZDO();
            if (zdo == null) return;
            zdo.Set(CompanionSetup.ActionModeHash, mode);
            _companionHarvest?.NotifyActionModeChanged();
            _companion.ApplyFollowMode(mode);

            string name;
            switch (mode)
            {
                case CompanionSetup.ModeFollow:      name = "Follow into Battle"; break;
                case CompanionSetup.ModeGatherWood:  name = "Gather Wood"; break;
                case CompanionSetup.ModeGatherStone: name = "Gather Stone"; break;
                case CompanionSetup.ModeGatherOre:   name = "Gather Ore"; break;
                default:                             name = "Follow"; break;
            }
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, name);
        }

        private void ToggleStayHome()
        {
            bool current = _companion.GetStayHome();
            bool next = !current;
            _companion.SetStayHome(next);

            if (next)
            {
                if (!_companion.HasHomePosition())
                    _companion.SetHomePosition(_companion.transform.position);
                if (_companionAI != null)
                {
                    _companionAI.SetFollowTarget(null);
                    _companionAI.SetPatrolPointAt(_companion.GetHomePosition());
                }
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Stay Home: ON");
            }
            else
            {
                var zdo = _companionNview?.GetZDO();
                int mode = zdo?.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                           ?? CompanionSetup.ModeFollow;
                _companion.ApplyFollowMode(mode);
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Stay Home: OFF");
            }
        }

        private void DoSetHome()
        {
            Vector3 pos = _companion.transform.position;
            _companion.SetHomePosition(pos);
            if (_companion.GetStayHome() && _companionAI != null)
                _companionAI.SetPatrolPointAt(pos);
            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Home point set.");
        }

        private void ToggleWander()
        {
            bool next = !_companion.GetWander();
            _companion.SetWander(next);
            if (next && !_companion.HasHomePosition())
                _companion.SetHomePosition(_companion.transform.position);
            MessageHud.instance?.ShowMessage(
                MessageHud.MessageType.Center, next ? "Wander: ON" : "Wander: OFF");
        }

        private void ToggleAutoPickup()
        {
            var zdo = _companionNview?.GetZDO();
            if (zdo == null) return;
            bool current = zdo.GetBool(CompanionSetup.AutoPickupHash, true);
            zdo.Set(CompanionSetup.AutoPickupHash, !current);
            MessageHud.instance?.ShowMessage(
                MessageHud.MessageType.Center, !current ? "Auto Pickup: ON" : "Auto Pickup: OFF");
        }

        private void ToggleCommandable()
        {
            bool current = _companion.GetIsCommandable();
            _companion.SetIsCommandable(!current);
            MessageHud.instance?.ShowMessage(
                MessageHud.MessageType.Center, !current ? "Command: ON" : "Command: OFF");
        }

        private void EnsureOwnership()
        {
            if (_companionNview == null || _companionNview.GetZDO() == null) return;
            if (!_companionNview.IsOwner())
                _companionNview.ClaimOwnership();
        }

        /// <summary>
        /// Lightweight refresh after executing an action — re-reads ZDO state
        /// and updates the existing UI elements without recreating GameObjects.
        /// </summary>
        private void RefreshSegmentStates()
        {
            BuildSegments();
            for (int i = 0; i < _segments.Count && i < _segDots.Count; i++)
            {
                if (_segDots[i] != null)
                    _segDots[i].color = _segments[i].IsActive ? ActiveDot : InactiveDot;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Segment building — reads current state from ZDO
        // ══════════════════════════════════════════════════════════════════

        private void BuildSegments()
        {
            _segments.Clear();

            var zdo = _companionNview?.GetZDO();
            int currentMode = zdo?.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                              ?? CompanionSetup.ModeFollow;
            bool stayHome    = _companion.GetStayHome();
            bool wander      = _companion.GetWander();
            bool autoPickup  = zdo?.GetBool(CompanionSetup.AutoPickupHash, true) ?? true;
            bool commandable = _companion.GetIsCommandable();

            _segments.Add(new Segment {
                Label = "Follow", ActionId = ActionFollow,
                IsMode = true, IsActive = currentMode == CompanionSetup.ModeFollow,
                IconColor = new Color(0.40f, 0.75f, 0.40f)
            });

            if (!_isDverger)
            {
                _segments.Add(new Segment {
                    Label = "Gather Wood", ActionId = ActionGatherWood,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeGatherWood,
                    IconColor = new Color(0.65f, 0.45f, 0.25f)
                });
                _segments.Add(new Segment {
                    Label = "Gather Stone", ActionId = ActionGatherStone,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeGatherStone,
                    IconColor = new Color(0.60f, 0.60f, 0.60f)
                });
                _segments.Add(new Segment {
                    Label = "Gather Ore", ActionId = ActionGatherOre,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeGatherOre,
                    IconColor = new Color(0.75f, 0.55f, 0.15f)
                });
            }

            _segments.Add(new Segment {
                Label = "Stay Home", ActionId = ActionStayHome,
                IsToggle = true, IsActive = stayHome,
                IconColor = new Color(0.35f, 0.60f, 0.90f)
            });
            _segments.Add(new Segment {
                Label = "Set Home", ActionId = ActionSetHome,
                IsToggle = false, IsMode = false, IsActive = false,
                IconColor = new Color(0.25f, 0.70f, 0.70f)
            });
            _segments.Add(new Segment {
                Label = "Wander", ActionId = ActionWander,
                IsToggle = true, IsActive = wander,
                IconColor = new Color(0.50f, 0.80f, 0.35f)
            });
            _segments.Add(new Segment {
                Label = "Auto Pickup", ActionId = ActionAutoPickup,
                IsToggle = true, IsActive = autoPickup,
                IconColor = new Color(0.90f, 0.70f, 0.15f)
            });

            _segments.Add(new Segment {
                Label = "Command", ActionId = ActionCommand,
                IsToggle = true, IsActive = commandable,
                IconColor = new Color(0.80f, 0.35f, 0.35f)
            });
        }

        // ══════════════════════════════════════════════════════════════════
        //  UI construction
        // ══════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            TMP_FontAsset font = GetFont();

            // Own canvas — not parented under InventoryGui
            var canvasGO = new GameObject("HC_RadialCanvas",
                typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 30;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            // Root container — centered on screen
            _root = new GameObject("RadialRoot", typeof(RectTransform));
            _root.transform.SetParent(canvasGO.transform, false);
            var rootRT = _root.GetComponent<RectTransform>();
            rootRT.anchorMin = new Vector2(0.5f, 0.5f);
            rootRT.anchorMax = new Vector2(0.5f, 0.5f);
            rootRT.pivot = new Vector2(0.5f, 0.5f);
            rootRT.sizeDelta = new Vector2(400f, 400f);

            // Background circle — dark semi-transparent sphere
            _bgCircle = new GameObject("BgCircle", typeof(RectTransform), typeof(Image));
            _bgCircle.transform.SetParent(_root.transform, false);
            var bgRT = _bgCircle.GetComponent<RectTransform>();
            bgRT.anchorMin = new Vector2(0.5f, 0.5f);
            bgRT.anchorMax = new Vector2(0.5f, 0.5f);
            bgRT.pivot = new Vector2(0.5f, 0.5f);
            bgRT.sizeDelta = new Vector2(360f, 360f);
            var bgImg = _bgCircle.GetComponent<Image>();
            bgImg.sprite = GetCircleSprite();
            bgImg.color = BgColor;
            bgImg.raycastTarget = false;

            // Center text — companion name
            _centerName = MakeText(_root.transform, "CenterName", "", font, 14f,
                new Color(0.83f, 0.64f, 0.31f, 1f), TextAlignmentOptions.Center);
            var nameRT = _centerName.GetComponent<RectTransform>();
            nameRT.anchorMin = new Vector2(0.5f, 0.5f);
            nameRT.anchorMax = new Vector2(0.5f, 0.5f);
            nameRT.pivot = new Vector2(0.5f, 0.5f);
            nameRT.sizeDelta = new Vector2(160f, 20f);
            nameRT.anchoredPosition = new Vector2(0f, 14f);

            // Center text — hovered action name
            _centerAction = MakeText(_root.transform, "CenterAction", "", font, 12f,
                TextNormal, TextAlignmentOptions.Center);
            var actRT = _centerAction.GetComponent<RectTransform>();
            actRT.anchorMin = new Vector2(0.5f, 0.5f);
            actRT.anchorMax = new Vector2(0.5f, 0.5f);
            actRT.pivot = new Vector2(0.5f, 0.5f);
            actRT.sizeDelta = new Vector2(160f, 18f);
            actRT.anchoredPosition = new Vector2(0f, -4f);

            // Center text — state (ON/OFF)
            _centerState = MakeText(_root.transform, "CenterState", "", font, 11f,
                ActiveDot, TextAlignmentOptions.Center);
            var stateRT = _centerState.GetComponent<RectTransform>();
            stateRT.anchorMin = new Vector2(0.5f, 0.5f);
            stateRT.anchorMax = new Vector2(0.5f, 0.5f);
            stateRT.pivot = new Vector2(0.5f, 0.5f);
            stateRT.sizeDelta = new Vector2(80f, 16f);
            stateRT.anchoredPosition = new Vector2(0f, -20f);

            _root.SetActive(false);
            _built = true;
        }

        private void PopulateUI()
        {
            TMP_FontAsset font = GetFont();

            // Clear old segment GOs
            for (int i = 0; i < _segmentGOs.Count; i++)
                if (_segmentGOs[i] != null)
                    Destroy(_segmentGOs[i]);
            _segmentGOs.Clear();
            _segHighlights.Clear();
            _segIcons.Clear();
            _segLabels.Clear();
            _segDots.Clear();

            float segArc = 360f / _segments.Count;

            for (int i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];

                // Position: clockwise from top
                float angle = 90f - segArc * i;
                float rad = angle * Mathf.Deg2Rad;
                float x = Mathf.Cos(rad) * RingRadius;
                float y = Mathf.Sin(rad) * RingRadius;

                // Segment container (invisible — just layout)
                var segGO = new GameObject($"Seg_{i}", typeof(RectTransform));
                segGO.transform.SetParent(_root.transform, false);
                var segRT = segGO.GetComponent<RectTransform>();
                segRT.anchorMin = new Vector2(0.5f, 0.5f);
                segRT.anchorMax = new Vector2(0.5f, 0.5f);
                segRT.pivot = new Vector2(0.5f, 0.5f);
                segRT.sizeDelta = new Vector2(SegSize, SegSize);
                segRT.anchoredPosition = new Vector2(x, y);

                // Highlight circle (behind icon, invisible by default)
                var hlGO = new GameObject("Highlight", typeof(RectTransform), typeof(Image));
                hlGO.transform.SetParent(segGO.transform, false);
                var hlRT = hlGO.GetComponent<RectTransform>();
                hlRT.anchorMin = new Vector2(0.5f, 0.5f);
                hlRT.anchorMax = new Vector2(0.5f, 0.5f);
                hlRT.pivot = new Vector2(0.5f, 0.5f);
                hlRT.sizeDelta = new Vector2(HighlightSize, HighlightSize);
                hlRT.anchoredPosition = new Vector2(0f, 4f);
                var hlImg = hlGO.GetComponent<Image>();
                hlImg.sprite = GetCircleSprite();
                hlImg.color = Color.clear;
                hlImg.raycastTarget = false;

                // Icon
                var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconGO.transform.SetParent(segGO.transform, false);
                var iconRT = iconGO.GetComponent<RectTransform>();
                iconRT.anchorMin = new Vector2(0.5f, 0.5f);
                iconRT.anchorMax = new Vector2(0.5f, 0.5f);
                iconRT.pivot = new Vector2(0.5f, 0.5f);
                iconRT.sizeDelta = new Vector2(IconSize, IconSize);
                iconRT.anchoredPosition = new Vector2(0f, 8f);
                var iconImg = iconGO.GetComponent<Image>();
                iconImg.sprite = GetActionIcon(seg.ActionId, seg.IconColor);
                iconImg.color = new Color(0.9f, 0.9f, 0.9f, 0.85f);
                iconImg.raycastTarget = false;

                // Label
                var label = MakeText(segGO.transform, "Label", seg.Label, font, 9f,
                    TextNormal, TextAlignmentOptions.Center);
                var labelRT = label.GetComponent<RectTransform>();
                labelRT.anchorMin = new Vector2(0f, 0f);
                labelRT.anchorMax = new Vector2(1f, 0f);
                labelRT.pivot = new Vector2(0.5f, 1f);
                labelRT.sizeDelta = new Vector2(0f, 14f);
                labelRT.anchoredPosition = new Vector2(0f, 16f);

                // Active dot (for toggles/modes)
                Image dotImg = null;
                if (seg.IsToggle || seg.IsMode)
                {
                    var dotGO = new GameObject("Dot", typeof(RectTransform), typeof(Image));
                    dotGO.transform.SetParent(segGO.transform, false);
                    var dotRT = dotGO.GetComponent<RectTransform>();
                    dotRT.anchorMin = new Vector2(0.5f, 0f);
                    dotRT.anchorMax = new Vector2(0.5f, 0f);
                    dotRT.pivot = new Vector2(0.5f, 0.5f);
                    dotRT.sizeDelta = new Vector2(8f, 8f);
                    dotRT.anchoredPosition = new Vector2(0f, 8f);
                    dotImg = dotGO.GetComponent<Image>();
                    dotImg.sprite = GetCircleSprite();
                    dotImg.color = seg.IsActive ? ActiveDot : InactiveDot;
                    dotImg.raycastTarget = false;
                }

                _segmentGOs.Add(segGO);
                _segHighlights.Add(hlImg);
                _segIcons.Add(iconImg);
                _segLabels.Add(label);
                _segDots.Add(dotImg);
            }

            // Update center name
            if (_centerName != null)
            {
                string name = "";
                if (_companionNview != null && _companionNview.GetZDO() != null)
                    name = _companionNview.GetZDO().GetString(CompanionSetup.NameHash, "");
                if (string.IsNullOrEmpty(name) && _companion != null)
                {
                    var ch = _companion.GetComponent<Character>();
                    if (ch != null) name = ch.m_name;
                }
                _centerName.text = string.IsNullOrEmpty(name) ? "Companion" : name;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Procedural icon generation
        //  NOTE: In texture space y=0 is bottom, y=size-1 is top.
        //  +y offset from center = top of icon on screen.
        // ══════════════════════════════════════════════════════════════════

        private static Sprite GetActionIcon(int actionId, Color color)
        {
            if (_iconCache.TryGetValue(actionId, out var cached)) return cached;

            int size = 48;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color[size * size];

            float center = (size - 1) * 0.5f;
            float outerR = center;
            float innerR = center - 2f;

            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    float dx = px - center;
                    float dy = py - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    if (dist <= innerR)
                    {
                        float t = dist / innerR;
                        Color c = Color.Lerp(color, color * 0.6f, t * t);
                        c.a = 0.9f;
                        pixels[py * size + px] = c;
                    }
                    else if (dist <= outerR)
                    {
                        Color c = color * 1.2f;
                        c.a = Mathf.Clamp01(1f - (dist - innerR) / (outerR - innerR));
                        pixels[py * size + px] = c;
                    }
                    else
                    {
                        pixels[py * size + px] = Color.clear;
                    }
                }
            }

            DrawIconSymbol(pixels, size, actionId);

            tex.SetPixels(pixels);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            _iconCache[actionId] = sprite;
            return sprite;
        }

        private static void DrawIconSymbol(Color[] pixels, int size, int actionId)
        {
            Color white = new Color(1f, 1f, 1f, 0.95f);
            float c = (size - 1) * 0.5f;

            switch (actionId)
            {
                case ActionFollow:
                    // Sword — blade pointing up, crossguard near handle
                    DrawLine(pixels, size, c, c - 10, c, c + 12, white, 2f);
                    DrawLine(pixels, size, c - 6, c + 2, c + 6, c + 2, white, 2f);
                    break;

                case ActionGatherWood:
                    // Axe — handle vertical, blade at top-right
                    DrawLine(pixels, size, c - 2, c - 10, c - 2, c + 10, white, 2f);
                    DrawLine(pixels, size, c - 2, c + 8, c + 8, c + 4, white, 2.5f);
                    DrawLine(pixels, size, c - 2, c + 4, c + 8, c + 4, white, 2.5f);
                    break;

                case ActionGatherStone:
                    // Pickaxe — handle vertical, head at top
                    DrawLine(pixels, size, c - 2, c - 10, c - 2, c + 10, white, 2f);
                    DrawLine(pixels, size, c - 10, c + 8, c + 6, c + 8, white, 2.5f);
                    DrawLine(pixels, size, c + 6, c + 8, c + 4, c + 4, white, 2f);
                    break;

                case ActionGatherOre:
                    // Diamond shape (symmetric)
                    DrawLine(pixels, size, c, c + 10, c + 8, c, white, 2f);
                    DrawLine(pixels, size, c + 8, c, c, c - 10, white, 2f);
                    DrawLine(pixels, size, c, c - 10, c - 8, c, white, 2f);
                    DrawLine(pixels, size, c - 8, c, c, c + 10, white, 2f);
                    break;

                case ActionStayHome:
                    // House — roof at top, floor at bottom
                    DrawLine(pixels, size, c, c + 10, c - 9, c + 1, white, 2f);
                    DrawLine(pixels, size, c, c + 10, c + 9, c + 1, white, 2f);
                    DrawLine(pixels, size, c - 7, c, c - 7, c - 9, white, 2f);
                    DrawLine(pixels, size, c + 7, c, c + 7, c - 9, white, 2f);
                    DrawLine(pixels, size, c - 7, c - 9, c + 7, c - 9, white, 2f);
                    break;

                case ActionSetHome:
                    // Map pin — circle head at top, point at bottom
                    DrawCircle(pixels, size, c, c + 6, 5f, white);
                    DrawLine(pixels, size, c, c + 1, c, c - 10, white, 2f);
                    break;

                case ActionWander:
                    // Wavy path (symmetric, no orientation issue)
                    DrawLine(pixels, size, c - 10, c + 4, c - 4, c - 4, white, 2f);
                    DrawLine(pixels, size, c - 4, c - 4, c + 4, c + 4, white, 2f);
                    DrawLine(pixels, size, c + 4, c + 4, c + 10, c - 4, white, 2f);
                    break;

                case ActionAutoPickup:
                    // Hand — palm at bottom, fingers reaching up
                    DrawLine(pixels, size, c - 6, c - 6, c + 6, c - 6, white, 2f);
                    DrawLine(pixels, size, c, c - 6, c, c + 6, white, 2f);
                    DrawLine(pixels, size, c - 6, c - 6, c - 6, c + 4, white, 2f);
                    DrawLine(pixels, size, c + 6, c - 6, c + 6, c + 4, white, 2f);
                    break;

                case ActionCommand:
                    // Speech bubble — bubble at top, tail pointing down-left
                    DrawCircle(pixels, size, c, c + 3, 8f, white);
                    DrawLine(pixels, size, c - 2, c - 5, c - 6, c - 10, white, 2f);
                    break;
            }
        }

        private static void DrawLine(Color[] pixels, int size, float x0, float y0,
            float x1, float y1, Color color, float thickness)
        {
            float dist = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist * 2f));
            float halfT = thickness * 0.5f;

            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                float cx = Mathf.Lerp(x0, x1, t);
                float cy = Mathf.Lerp(y0, y1, t);

                int minX = Mathf.Max(0, Mathf.FloorToInt(cx - halfT));
                int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + halfT));
                int minY = Mathf.Max(0, Mathf.FloorToInt(cy - halfT));
                int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + halfT));

                for (int py = minY; py <= maxY; py++)
                    for (int px = minX; px <= maxX; px++)
                    {
                        float dx = px - cx;
                        float dy = py - cy;
                        if (dx * dx + dy * dy <= halfT * halfT)
                            pixels[py * size + px] = color;
                    }
            }
        }

        private static void DrawCircle(Color[] pixels, int size, float cx, float cy,
            float radius, Color color)
        {
            int minX = Mathf.Max(0, Mathf.FloorToInt(cx - radius));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + radius));
            int minY = Mathf.Max(0, Mathf.FloorToInt(cy - radius));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + radius));

            float innerR = radius - 1.5f;
            for (int py = minY; py <= maxY; py++)
                for (int px = minX; px <= maxX; px++)
                {
                    float dx = px - cx;
                    float dy = py - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    if (d <= radius && d >= innerR)
                        pixels[py * size + px] = color;
                }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Utility sprite — filled circle
        // ══════════════════════════════════════════════════════════════════

        private static Sprite _circleSprite;
        private static Sprite GetCircleSprite()
        {
            if (_circleSprite != null) return _circleSprite;
            int s = 64;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color[s * s];
            float c = (s - 1) * 0.5f;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = x - c, dy = y - c;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist <= c)
                        px[y * s + x] = Color.white;
                    else if (dist <= c + 1f)
                        px[y * s + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(c + 1f - dist));
                    else
                        px[y * s + x] = Color.clear;
                }
            tex.SetPixels(px);
            tex.Apply();
            _circleSprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
            return _circleSprite;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Text helper
        // ══════════════════════════════════════════════════════════════════

        private static TMP_FontAsset GetFont()
        {
            var labels = UnityEngine.Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None);
            foreach (var l in labels)
                if (l.font != null) return l.font;
            return null;
        }

        private static TextMeshProUGUI MakeText(Transform parent, string name, string text,
            TMP_FontAsset font, float size, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            if (font != null) tmp.font = font;
            tmp.fontSize = size;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            return tmp;
        }

        private void Teardown()
        {
            for (int i = 0; i < _segmentGOs.Count; i++)
                if (_segmentGOs[i] != null) Destroy(_segmentGOs[i]);
            _segmentGOs.Clear();
            _segHighlights.Clear();
            _segIcons.Clear();
            _segLabels.Clear();
            _segDots.Clear();
            _built = false;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Harmony — make Valheim's Hud.InRadial() recognize our menu.
        //  This integrates with GameCamera (cursor lock), PlayerController
        //  (blocks camera look + movement), and Player (blocks Use/attack).
        // ══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Hud), nameof(Hud.InRadial))]
        private static class HudInRadial_Patch
        {
            static void Postfix(ref bool __result)
            {
                if (!__result && Instance != null && Instance._visible)
                    __result = true;
            }
        }

        /// <summary>
        /// Hide crosshair, hover text, and piece health bar while the
        /// companion radial is visible.
        /// </summary>
        [HarmonyPatch(typeof(Hud), "UpdateCrosshair")]
        private static class HudCrosshair_Patch
        {
            static void Postfix(Hud __instance)
            {
                if (Instance == null || !Instance._visible) return;

                __instance.m_crosshair.gameObject.SetActive(false);
                __instance.m_hoverName.text = "";
                if (__instance.m_pieceHealthRoot != null)
                    __instance.m_pieceHealthRoot.gameObject.SetActive(false);
                if (__instance.m_crosshairBow != null)
                    __instance.m_crosshairBow.gameObject.SetActive(false);
            }
        }
    }
}
