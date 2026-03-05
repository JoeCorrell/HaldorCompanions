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
        private const int ActionForage      = 5;
        private const int ActionSmelt       = 6;
        private const int ActionStayHome    = 10;
        private const int ActionSetHome     = 11;
        private const int ActionWander      = 12;
        private const int ActionAutoPickup  = 13;
        private const int ActionCommand     = 14;
        private const int ActionRepair      = 15;   // toggle mode: periodic building repair in 50m
        private const int ActionRestock     = 16;   // toggle mode: periodic campfire/light restock in 50m
        // Must match CompanionSetup.ModeHunt/ModeFarm/ModeFish — SetActionMode()
        // passes ActionId directly as the ZDO mode value.
        private const int ActionHunt        = 7;
        private const int ActionFarm        = 8;
        private const int ActionFish        = 9;

        // Combat stances — value minus ActionStanceBalanced = CompanionSetup.Stance*
        private const int ActionStanceBalanced = 20;
        private const int ActionStancePassive  = 21;
        private const int ActionStanceRanged   = 22;
        private const int ActionStanceMelee    = 23;

        private const int ActionDespawn        = 30;

        // ── Style ──────────────────────────────────────────────────────────
        private static readonly Color BgColor          = new Color(0.14f, 0.11f, 0.08f, 0.82f);
        private static readonly Color HighlightHover   = new Color(0.83f, 0.64f, 0.31f, 0.30f);
        private static readonly Color HighlightActive  = new Color(0.45f, 0.35f, 0.18f, 0.15f);
        private static readonly Color TextNormal       = new Color(0.85f, 0.80f, 0.65f, 1f);
        private static readonly Color TextHover        = new Color(1f, 0.95f, 0.80f, 1f);
        private static readonly Color ActiveDot        = new Color(0.40f, 0.85f, 0.40f, 1f);
        private static readonly Color InactiveDot      = new Color(0.5f, 0.5f, 0.5f, 0.5f);

        private const float RingRadius     = 270f;
        private const float SegSize        = 115f;
        private const float HighlightSize  = 90f;
        private const float IconSize       = 87f;
        private const float DeadZonePx     = 30f;
        private const float DeadZoneStick  = 0.3f;

        // Inner ring (combat stances + despawn)
        private const float InnerRingRadius = 120f;
        private const float InnerSegSize    = 80f;
        private const float InnerIconSize   = 58f;
        private const float InnerThresholdPx    = 190f;  // mouse dist < this → inner ring
        private const float InnerThresholdStick = 0.55f; // stick magnitude < this → inner ring

        // ── Animation ──────────────────────────────────────────────────────
        private enum AnimState { Closed, Opening, Open, Closing }
        private const float AnimDuration     = 0.45f;
        private const float SegStagger       = 0.04f;
        private const float BgAnimFraction   = 0.4f;

        // ── Speech pools (loaded from speech.json) ─────────────────────
        private static string[] ActionSpeech => SpeechConfig.Instance.Action;

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
        private GameObject _bgCircle;       // outer ring donut
        private GameObject _centerContainer;
        private TextMeshProUGUI _centerName;
        private TextMeshProUGUI _centerAction;
        private TextMeshProUGUI _centerState;

        // Outer ring segments
        private readonly List<Segment>    _segments   = new List<Segment>();
        private readonly List<GameObject> _segmentGOs = new List<GameObject>();
        private readonly List<Image>      _segHighlights = new List<Image>();
        private readonly List<Image>      _segIcons   = new List<Image>();
        private readonly List<TextMeshProUGUI> _segLabels = new List<TextMeshProUGUI>();
        private readonly List<Image>      _segDots    = new List<Image>();
        private int _hoveredIndex = -1;

        // Inner ring segments (stances + despawn)
        private readonly List<Segment>    _innerSegments   = new List<Segment>();
        private readonly List<GameObject> _innerSegmentGOs = new List<GameObject>();
        private readonly List<Image>      _innerSegHighlights = new List<Image>();
        private readonly List<Image>      _innerSegIcons   = new List<Image>();
        private readonly List<TextMeshProUGUI> _innerSegLabels = new List<TextMeshProUGUI>();
        private readonly List<Image>      _innerSegDots    = new List<Image>();
        private int _hoveredInnerIndex = -1;
        private bool _hoveringInner; // true = inner ring active, false = outer

        /// <summary>
        /// Tracks whether the Use key has been released at least once since
        /// the radial opened. Prevents the initial hold-to-open from
        /// immediately triggering an action execution.
        /// </summary>
        private bool _useReleasedSinceOpen;

        // ── Animation state ─────────────────────────────────────────────
        private AnimState _animState = AnimState.Closed;
        private float     _animTimer;
        private CanvasGroup _bgCanvasGroup;
        private CanvasGroup _centerCanvasGroup;
        private readonly List<CanvasGroup>    _segCanvasGroups  = new List<CanvasGroup>();
        private readonly List<RectTransform>  _segRTs           = new List<RectTransform>();
        private readonly List<Vector2>        _segFinalPositions = new List<Vector2>();

        // Inner ring animation
        private readonly List<CanvasGroup>    _innerSegCanvasGroups  = new List<CanvasGroup>();
        private readonly List<RectTransform>  _innerSegRTs           = new List<RectTransform>();
        private readonly List<Vector2>        _innerSegFinalPositions = new List<Vector2>();

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

        /// <summary>
        /// Pre-generates procedural sprites and loads all icon textures so the
        /// first radial menu open doesn't cause a frame hitch.
        /// </summary>
        internal static void WarmCache()
        {
            // Procedural background sprites
            GetCircleSprite();
            GetDonutSprite();

            // All action icons (outer ring)
            int[] allActions = {
                ActionFollow, ActionGatherWood, ActionGatherStone, ActionGatherOre,
                ActionForage, ActionSmelt, ActionHunt, ActionFarm, ActionFish,
                ActionStayHome, ActionSetHome,
                ActionWander, ActionAutoPickup, ActionCommand,
                ActionRepair, ActionRestock,
                ActionStanceBalanced, ActionStancePassive, ActionStanceRanged, ActionStanceMelee,
                ActionDespawn
            };
            foreach (int id in allActions)
                GetActionIcon(id);
        }

        private void Awake()  { Instance = this; }
        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Teardown();

            // Destroy native sprites/textures to prevent GPU memory leaks across scene reloads
            foreach (var kvp in _iconCache)
            {
                if (kvp.Value != null)
                {
                    if (kvp.Value.texture != null) Destroy(kvp.Value.texture);
                    Destroy(kvp.Value);
                }
            }
            _iconCache.Clear();

            if (_circleSprite != null)
            {
                if (_circleSprite.texture != null) Destroy(_circleSprite.texture);
                Destroy(_circleSprite);
                _circleSprite = null;
            }
            if (_donutSprite != null)
            {
                if (_donutSprite.texture != null) Destroy(_donutSprite.texture);
                Destroy(_donutSprite);
                _donutSprite = null;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════

        public void Show(CompanionSetup companion)
        {
            if (companion == null) return;

            // Cancel any in-progress close
            _animState = AnimState.Closed;

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
            _hoveredInnerIndex = -1;
            _hoveringInner = false;
            _useReleasedSinceOpen = false;

            // Clear any overhead speech text from this companion
            if (Chat.instance != null)
                Chat.instance.ClearNpcText(companion.gameObject);

            // Start opening animation
            _animState = AnimState.Opening;
            _animTimer = 0f;
            InitAnimationState();

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
            if (_animState == AnimState.Closing) return;

            _animState = AnimState.Closing;
            _animTimer = 0f;
            _hoveredIndex = -1;
            _hoveredInnerIndex = -1;
            _hoveringInner = false;

            CompanionsPlugin.Log.LogDebug("[Radial] Hide (closing)");
        }

        private void CompleteHide()
        {
            _visible = false;
            _animState = AnimState.Closed;
            if (_root != null) _root.SetActive(false);

            // Prevent camera jerk when closing while right stick is held.
            // Matches vanilla RadialBase.Close() behavior.
            if (ZInput.IsGamepadActive())
            {
                Vector2 rs = new Vector2(ZInput.GetJoyRightStickX(),
                                         -ZInput.GetJoyRightStickY());
                if (rs.sqrMagnitude > 0.01f)
                    PlayerController.cameraDirectionLock = rs.normalized;
            }

            _companion        = null;
            _companionNview   = null;
            _companionAI      = null;
            _companionHarvest = null;
            _companionTalk    = null;

            // Let GameCamera.UpdateMouseCapture restore cursor state
            // on the next frame via its normal checks.
        }

        // ══════════════════════════════════════════════════════════════════
        //  Update — selection tracking, execute on click, close on Esc/B
        // ══════════════════════════════════════════════════════════════════

        private void Update()
        {
            if (!_visible) return;

            // Drive animation
            if (_animState == AnimState.Opening || _animState == AnimState.Closing)
                UpdateAnimation();

            // Block all input during close animation
            if (_animState == AnimState.Closing) return;

            // Companion destroyed?
            if (_companion == null || !_companion)
            { Hide(); return; }

            // Close on Esc or B (keyboard), or gamepad B button
            if (ZInput.GetKeyDown(KeyCode.Escape) || ZInput.GetKeyDown(KeyCode.B)
                || ZInput.GetButtonDown("JoyButtonB"))
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
            // Only allow execution when fully open
            if (_animState == AnimState.Open && _useReleasedSinceOpen &&
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
            bool isGamepad = ZInput.IsGamepadActive();
            float magnitude;
            float angle;

            if (isGamepad)
            {
                Vector2 left = new Vector2(ZInput.GetJoyLeftStickX(), -ZInput.GetJoyLeftStickY());
                magnitude = left.magnitude;
                if (magnitude < DeadZoneStick)
                {
                    _hoveredIndex = -1;
                    _hoveredInnerIndex = -1;
                    _hoveringInner = false;
                    return;
                }
                angle = Mathf.Atan2(left.y, left.x) * Mathf.Rad2Deg;
                bool inner = magnitude < InnerThresholdStick && _innerSegments.Count > 0;
                _hoveringInner = inner;
            }
            else
            {
                Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                Vector2 input = (Vector2)Input.mousePosition - center;
                magnitude = input.magnitude;

                if (magnitude < DeadZonePx)
                {
                    _hoveredIndex = -1;
                    _hoveredInnerIndex = -1;
                    _hoveringInner = false;
                    return;
                }
                angle = Mathf.Atan2(input.y, input.x) * Mathf.Rad2Deg;
                bool inner = magnitude < InnerThresholdPx && _innerSegments.Count > 0;
                _hoveringInner = inner;
            }

            if (angle < 0f) angle += 360f;

            if (_hoveringInner)
            {
                _hoveredInnerIndex = ComputeSegmentIndex(angle, _innerSegments.Count);
                _hoveredIndex = -1;
            }
            else
            {
                _hoveredIndex = _segments.Count > 0
                    ? ComputeSegmentIndex(angle, _segments.Count) : -1;
                _hoveredInnerIndex = -1;
            }
        }

        private static int ComputeSegmentIndex(float angle, int count)
        {
            if (count <= 0) return -1;
            float segArc = 360f / count;
            float adjusted = (90f - angle + 360f + segArc * 0.5f) % 360f;
            int index = Mathf.FloorToInt(adjusted / segArc);
            return Mathf.Clamp(index, 0, count - 1);
        }

        private void RefreshVisuals()
        {
            // Outer ring
            for (int i = 0; i < _segmentGOs.Count; i++)
            {
                bool hovered = !_hoveringInner && i == _hoveredIndex;
                var seg = _segments[i];

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

                if (_segLabels[i] != null)
                    _segLabels[i].color = hovered ? TextHover : TextNormal;

                if (_segIcons[i] != null)
                    _segIcons[i].color = hovered ? Color.white : new Color(0.9f, 0.9f, 0.9f, 0.85f);
            }

            // Inner ring
            for (int i = 0; i < _innerSegmentGOs.Count; i++)
            {
                bool hovered = _hoveringInner && i == _hoveredInnerIndex;
                var seg = _innerSegments[i];

                if (_innerSegHighlights[i] != null)
                {
                    Color hlCol;
                    if (hovered)
                        hlCol = HighlightHover;
                    else if (seg.IsActive)
                        hlCol = HighlightActive;
                    else
                        hlCol = Color.clear;
                    _innerSegHighlights[i].color = hlCol;
                }

                if (_innerSegLabels[i] != null)
                    _innerSegLabels[i].color = hovered ? TextHover : TextNormal;

                if (_innerSegIcons[i] != null)
                    _innerSegIcons[i].color = hovered ? Color.white : new Color(0.9f, 0.9f, 0.9f, 0.85f);
            }

            // Center text — show hovered segment from either ring
            if (_centerAction != null)
            {
                Segment? hovSeg = null;
                if (_hoveringInner && _hoveredInnerIndex >= 0 && _hoveredInnerIndex < _innerSegments.Count)
                    hovSeg = _innerSegments[_hoveredInnerIndex];
                else if (!_hoveringInner && _hoveredIndex >= 0 && _hoveredIndex < _segments.Count)
                    hovSeg = _segments[_hoveredIndex];

                if (hovSeg.HasValue)
                {
                    var seg = hovSeg.Value;
                    _centerAction.text = seg.Label;
                    if (seg.IsToggle || seg.IsMode)
                        _centerState.text = seg.IsActive ? ModLocalization.Loc("hc_radial_on") : ModLocalization.Loc("hc_radial_off");
                    else if (seg.IsActive)
                        _centerState.text = ModLocalization.Loc("hc_radial_active");
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
        //  Animation
        // ══════════════════════════════════════════════════════════════════

        private void InitAnimationState()
        {
            // Backgrounds — start invisible and collapsed
            if (_bgCanvasGroup != null) _bgCanvasGroup.alpha = 0f;
            var bgRT = _bgCircle.GetComponent<RectTransform>();
            bgRT.localScale = Vector3.zero;

            // Center text — start invisible
            if (_centerCanvasGroup != null) _centerCanvasGroup.alpha = 0f;

            // Outer segments — start at center, collapsed, rotated
            for (int i = 0; i < _segmentGOs.Count; i++)
            {
                if (i < _segRTs.Count && _segRTs[i] != null)
                {
                    _segRTs[i].anchoredPosition = Vector2.zero;
                    _segRTs[i].localScale = Vector3.zero;
                    _segRTs[i].localEulerAngles = new Vector3(0f, 0f, -180f + i * 20f);
                }
                if (i < _segCanvasGroups.Count && _segCanvasGroups[i] != null)
                    _segCanvasGroups[i].alpha = 0f;
            }

            // Inner segments
            for (int i = 0; i < _innerSegmentGOs.Count; i++)
            {
                if (i < _innerSegRTs.Count && _innerSegRTs[i] != null)
                {
                    _innerSegRTs[i].anchoredPosition = Vector2.zero;
                    _innerSegRTs[i].localScale = Vector3.zero;
                    _innerSegRTs[i].localEulerAngles = new Vector3(0f, 0f, -120f + i * 30f);
                }
                if (i < _innerSegCanvasGroups.Count && _innerSegCanvasGroups[i] != null)
                    _innerSegCanvasGroups[i].alpha = 0f;
            }
        }

        private void UpdateAnimation()
        {
            _animTimer += Time.unscaledDeltaTime;
            float rawProgress = Mathf.Clamp01(_animTimer / AnimDuration);
            bool closing = _animState == AnimState.Closing;

            // ── Backgrounds ──
            float bgP = closing ? 1f - rawProgress : rawProgress;
            float bgT = Mathf.Clamp01(bgP / BgAnimFraction);
            float bgEased = EaseOutBack(bgT);
            var bgRT = _bgCircle.GetComponent<RectTransform>();
            bgRT.localScale = Vector3.one * Mathf.Max(0f, bgEased);
            if (_bgCanvasGroup != null)
                _bgCanvasGroup.alpha = Mathf.Clamp01(EaseOutQuad(bgT));

            // ── Center text ──
            if (_centerCanvasGroup != null)
            {
                float centerT = Mathf.Clamp01((bgP - 0.3f) / 0.3f);
                _centerCanvasGroup.alpha = Mathf.Clamp01(centerT);
            }

            // ── Segments ──
            int count = _segmentGOs.Count;
            float totalStagger = count * SegStagger;
            float segAnimDur = Mathf.Max(0.01f, AnimDuration - totalStagger);

            for (int i = 0; i < count; i++)
            {
                // Reverse stagger order when closing
                int staggerIdx = closing ? (count - 1 - i) : i;
                float segDelay = staggerIdx * SegStagger;

                float segVis;
                if (closing)
                    segVis = 1f - Mathf.Clamp01((_animTimer - segDelay) / segAnimDur);
                else
                    segVis = Mathf.Clamp01((_animTimer - segDelay) / segAnimDur);

                // Different easing for open vs close
                float eased = closing ? (segVis * segVis) : EaseOutBack(segVis);

                if (i < _segRTs.Count && _segRTs[i] != null)
                {
                    // Position: center → final ring position
                    if (i < _segFinalPositions.Count)
                        _segRTs[i].anchoredPosition = _segFinalPositions[i] * eased;

                    // Scale
                    _segRTs[i].localScale = Vector3.one * Mathf.Max(0f, eased);

                    // Rotation: full spin from starting angle to 0
                    float startRot = -360f + i * 30f;
                    float rotT = Mathf.Clamp01(eased);
                    _segRTs[i].localEulerAngles = new Vector3(0f, 0f,
                        Mathf.Lerp(startRot, 0f, rotT));
                }

                // Alpha
                if (i < _segCanvasGroups.Count && _segCanvasGroups[i] != null)
                    _segCanvasGroups[i].alpha = Mathf.Clamp01(EaseOutQuad(segVis));
            }

            // ── Inner segments ──
            int innerCount = _innerSegmentGOs.Count;
            for (int i = 0; i < innerCount; i++)
            {
                int staggerIdx = closing ? (innerCount - 1 - i) : i;
                float segDelay = staggerIdx * SegStagger;

                float segVis;
                if (closing)
                    segVis = 1f - Mathf.Clamp01((_animTimer - segDelay) / segAnimDur);
                else
                    segVis = Mathf.Clamp01((_animTimer - segDelay) / segAnimDur);

                float eased = closing ? (segVis * segVis) : EaseOutBack(segVis);

                if (i < _innerSegRTs.Count && _innerSegRTs[i] != null)
                {
                    if (i < _innerSegFinalPositions.Count)
                        _innerSegRTs[i].anchoredPosition = _innerSegFinalPositions[i] * eased;
                    _innerSegRTs[i].localScale = Vector3.one * Mathf.Max(0f, eased);
                    float startRot = -240f + i * 40f;
                    _innerSegRTs[i].localEulerAngles = new Vector3(0f, 0f,
                        Mathf.Lerp(startRot, 0f, Mathf.Clamp01(eased)));
                }
                if (i < _innerSegCanvasGroups.Count && _innerSegCanvasGroups[i] != null)
                    _innerSegCanvasGroups[i].alpha = Mathf.Clamp01(EaseOutQuad(segVis));
            }

            // ── Complete ──
            if (rawProgress >= 1f)
            {
                if (_animState == AnimState.Opening)
                {
                    _animState = AnimState.Open;
                    // Snap to final state
                    bgRT.localScale = Vector3.one;
                    if (_bgCanvasGroup != null) _bgCanvasGroup.alpha = 1f;
                    if (_centerCanvasGroup != null) _centerCanvasGroup.alpha = 1f;
                    for (int i = 0; i < count; i++)
                    {
                        if (i < _segRTs.Count && _segRTs[i] != null)
                        {
                            if (i < _segFinalPositions.Count)
                                _segRTs[i].anchoredPosition = _segFinalPositions[i];
                            _segRTs[i].localScale = Vector3.one;
                            _segRTs[i].localEulerAngles = Vector3.zero;
                        }
                        if (i < _segCanvasGroups.Count && _segCanvasGroups[i] != null)
                            _segCanvasGroups[i].alpha = 1f;
                    }
                    for (int i = 0; i < innerCount; i++)
                    {
                        if (i < _innerSegRTs.Count && _innerSegRTs[i] != null)
                        {
                            if (i < _innerSegFinalPositions.Count)
                                _innerSegRTs[i].anchoredPosition = _innerSegFinalPositions[i];
                            _innerSegRTs[i].localScale = Vector3.one;
                            _innerSegRTs[i].localEulerAngles = Vector3.zero;
                        }
                        if (i < _innerSegCanvasGroups.Count && _innerSegCanvasGroups[i] != null)
                            _innerSegCanvasGroups[i].alpha = 1f;
                    }
                }
                else if (_animState == AnimState.Closing)
                {
                    CompleteHide();
                }
            }
        }

        private static float EaseOutBack(float t)
        {
            const float c1 = 1.4f;
            const float c3 = c1 + 1f;
            float tm1 = t - 1f;
            return 1f + c3 * (tm1 * tm1 * tm1) + c1 * (tm1 * tm1);
        }

        private static float EaseOutQuad(float t) => 1f - (1f - t) * (1f - t);

        // ══════════════════════════════════════════════════════════════════
        //  Action execution
        // ══════════════════════════════════════════════════════════════════

        private void ExecuteSelectedAction()
        {
            if (_companion == null || _companionNview == null) return;

            // Inner ring — stances + despawn
            if (_hoveringInner)
            {
                if (_hoveredInnerIndex < 0 || _hoveredInnerIndex >= _innerSegments.Count) return;
                var innerSeg = _innerSegments[_hoveredInnerIndex];
                EnsureOwnership();
                switch (innerSeg.ActionId)
                {
                    case ActionStanceBalanced:
                    case ActionStancePassive:
                    case ActionStanceRanged:
                    case ActionStanceMelee:
                        _companion.SetCombatStance(innerSeg.ActionId - ActionStanceBalanced);
                        RefreshSegmentStates();
                        break;
                    case ActionDespawn:
                        DoDespawn();
                        return;
                }
                if (_companionTalk != null && ActionSpeech.Length > 0)
                    _companionTalk.Say(ActionSpeech[UnityEngine.Random.Range(0, ActionSpeech.Length)], "Action");
                return;
            }

            // Outer ring — action modes / toggles
            if (_hoveredIndex < 0 || _hoveredIndex >= _segments.Count) return;

            var outerSeg = _segments[_hoveredIndex];
            EnsureOwnership();

            CompanionsPlugin.Log.LogDebug(
                $"[Radial] Execute action={outerSeg.ActionId} label=\"{outerSeg.Label}\"");

            switch (outerSeg.ActionId)
            {
                case ActionFollow:
                    ToggleFollow();
                    break;
                case ActionGatherWood:
                case ActionGatherStone:
                case ActionGatherOre:
                case ActionForage:
                case ActionSmelt:
                case ActionHunt:
                case ActionFarm:
                case ActionFish:
                case ActionRepair:
                case ActionRestock:
                    SetActionMode(outerSeg.ActionId);
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
                _companionTalk.Say(ActionSpeech[UnityEngine.Random.Range(0, ActionSpeech.Length)], "Action");
        }

        private void SetActionMode(int mode)
        {
            var zdo = _companionNview.GetZDO();
            if (zdo == null) return;

            // Toggle: tapping an already-active gather mode deselects it
            int currentMode = zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            int newMode = (currentMode == mode) ? CompanionSetup.ModeFollow : mode;

            zdo.Set(CompanionSetup.ActionModeHash, newMode);
            _companionHarvest?.NotifyActionModeChanged();
            _companion.ApplyFollowMode(newMode);

            // Notify SmeltController of mode change
            var smelt = _companion.GetComponent<SmeltController>();
            smelt?.NotifyActionModeChanged();

            // Notify FarmController of mode change
            var farm = _companion.GetComponent<FarmController>();
            farm?.NotifyActionModeChanged();

            // Reset repair/restock state machines on mode change
            var ai = _companion.GetComponent<CompanionAI>();
            if (ai != null)
            {
                ai.ResetRepairBuildState();
                ai.ResetRestockState();
            }

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
                // Only set patrol if Follow toggle is OFF — Follow overrides StayHome
                if (_companionAI != null && !_companion.GetFollow())
                {
                    _companionAI.SetFollowTarget(null);
                    _companionAI.SetPatrolPointAt(_companion.GetHomePosition());
                }
            }
            else
            {
                var zdo = _companionNview?.GetZDO();
                int mode = zdo?.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                           ?? CompanionSetup.ModeFollow;
                _companion.ApplyFollowMode(mode);
            }
        }

        private void DoSetHome()
        {
            Vector3 pos = _companion.transform.position;
            _companion.SetHomePosition(pos);

            // Auto-enable StayHome if not already active
            if (!_companion.GetStayHome())
            {
                _companion.SetStayHome(true);
                if (_companionAI != null && !_companion.GetFollow())
                {
                    _companionAI.SetFollowTarget(null);
                    _companionAI.SetPatrolPointAt(pos);
                }
            }
            else if (_companionAI != null)
            {
                _companionAI.SetPatrolPointAt(pos);
            }

            // Close radial, then open the home zone radius panel
            var companionRef = _companion; // capture before Hide clears refs
            Hide();
            HomeZonePanel.EnsureInstance();
            HomeZonePanel.Instance.Show(companionRef);
        }

        private void ToggleWander()
        {
            bool next = !_companion.GetWander();
            _companion.SetWander(next);
            if (next && !_companion.HasHomePosition())
                _companion.SetHomePosition(_companion.transform.position);
        }

        private void ToggleAutoPickup()
        {
            var zdo = _companionNview?.GetZDO();
            if (zdo == null) return;
            bool current = zdo.GetBool(CompanionSetup.AutoPickupHash, true);
            zdo.Set(CompanionSetup.AutoPickupHash, !current);
        }

        private void ToggleCommandable()
        {
            bool current = _companion.GetIsCommandable();
            _companion.SetIsCommandable(!current);
        }

        private void DoDespawn()
        {
            if (_companion == null || _companionNview == null) return;

            string name = _companionNview.GetZDO()?.GetString(CompanionSetup.NameHash, "Companion") ?? "Companion";
            CompanionsPlugin.Log.LogInfo($"[Radial] Despawn — permanently removing \"{name}\"");

            // Unsubscribe death handler so OnCompanionDeath doesn't queue a respawn
            var humanoid = _companion.GetComponent<Humanoid>();
            if (humanoid != null)
                humanoid.m_onDeath = null;

            // Close radial first
            Hide();

            // Destroy the ZDO-backed object — removes from world permanently
            _companionNview.Destroy();
        }

        // ── One-shot: Repair all building pieces within 50m ──────────────

        private const float RepairRadius = 50f;

        private void DoRepairBuildings()
        {
            if (_companion == null) return;

            // Require hammer in inventory
            var inv = _companion.GetComponent<Humanoid>()?.GetInventory();
            if (inv == null) return;

            bool hasHammer = false;
            var items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i]?.m_shared?.m_buildPieces != null)
                {
                    hasHammer = true;
                    break;
                }
            }

            if (!hasHammer)
            {
                CompanionsPlugin.Log.LogInfo("[Radial] Repair: no hammer in inventory");
                if (_companionTalk != null)
                    _companionTalk.Say(ModLocalization.Loc("hc_speech_repair_no_hammer"), "Action");
                return;
            }

            // Find and repair all damaged player-placed pieces
            Vector3 center = _companion.transform.position;
            var allWNT = WearNTear.GetAllInstances();
            int repaired = 0;

            for (int i = 0; i < allWNT.Count; i++)
            {
                var wnt = allWNT[i];
                if (wnt == null) continue;

                var piece = wnt.GetComponent<Piece>();
                if (piece == null || !piece.IsPlacedByPlayer()) continue;

                float dist = Vector3.Distance(center, wnt.transform.position);
                if (dist > RepairRadius) continue;

                float hp = wnt.GetHealthPercentage();
                if (hp >= 1f) continue;

                if (wnt.Repair())
                {
                    repaired++;
                    // Play repair VFX on each piece
                    if (piece.m_placeEffect != null)
                        piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation);
                }
            }

            CompanionsPlugin.Log.LogInfo($"[Radial] Repair: fixed {repaired} building pieces within {RepairRadius}m");

            if (repaired > 0)
            {
                // Play hammer swing animation
                var zanim = _companion.GetComponent<ZSyncAnimation>();
                if (zanim != null)
                    zanim.SetTrigger("swing_pickaxe");

                if (_companionTalk != null)
                    _companionTalk.Say(ModLocalization.Loc("hc_speech_repair_done"), "Repair");
            }
            else
            {
                if (_companionTalk != null)
                    _companionTalk.Say(ModLocalization.Loc("hc_speech_repair_nothing"), "Action");
            }
        }

        // ── One-shot: Restock campfires / light sources within 50m ───────

        private const float RestockRadius = 50f;
        private const float FuelThreshold = 0.5f;

        private void DoRestock()
        {
            if (_companion == null) return;

            var inv = _companion.GetComponent<Humanoid>()?.GetInventory();
            if (inv == null) return;

            Vector3 center = _companion.transform.position;
            var tempPieces = new System.Collections.Generic.List<Piece>();
            Piece.GetAllPiecesInRadius(center, RestockRadius, tempPieces);

            int restocked = 0;
            int totalFuelAdded = 0;

            for (int i = 0; i < tempPieces.Count; i++)
            {
                var fp = tempPieces[i].GetComponent<Fireplace>();
                if (fp == null) continue;
                if (!fp.m_canRefill || fp.m_infiniteFuel) continue;
                if (fp.m_fuelItem == null) continue;

                var fpNview = fp.GetComponent<ZNetView>();
                if (fpNview == null || fpNview.GetZDO() == null) continue;

                float currentFuel = fpNview.GetZDO().GetFloat(ZDOVars.s_fuel);
                float maxFuel = fp.m_maxFuel;
                if (maxFuel <= 0f) continue;

                float ratio = currentFuel / maxFuel;
                if (ratio >= FuelThreshold) continue;

                // Find fuel item name
                string fuelPrefab = fp.m_fuelItem.gameObject.name;
                int fuelNeeded = Mathf.Max(1, (int)(maxFuel - currentFuel));

                // Check inventory and nearby chests for fuel
                int fuelAvailable = CountItemInInventory(inv, fuelPrefab);

                // Also check nearby chests
                var containers = new System.Collections.Generic.List<Container>();
                FindNearbyContainers(center, 20f, containers);
                for (int c = 0; c < containers.Count && fuelAvailable < fuelNeeded; c++)
                {
                    var chestInv = containers[c].GetInventory();
                    if (chestInv == null) continue;

                    int inChest = CountItemInInventory(chestInv, fuelPrefab);
                    int toTake = Mathf.Min(inChest, fuelNeeded - fuelAvailable);
                    if (toTake > 0)
                    {
                        // Transfer fuel from chest to companion inventory
                        for (int t = 0; t < toTake; t++)
                            ConsumeOneFromInventory(chestInv, fuelPrefab);
                        var prefab = ObjectDB.instance?.GetItemPrefab(fuelPrefab);
                        if (prefab != null)
                        {
                            var itemDrop = prefab.GetComponent<ItemDrop>();
                            if (itemDrop != null)
                                inv.AddItem(prefab, toTake);
                        }
                        fuelAvailable += toTake;
                    }
                }

                int toAdd = Mathf.Min(fuelNeeded, fuelAvailable);
                if (toAdd <= 0) continue;

                // Add fuel to fireplace, consuming from inventory
                bool addedAny = false;
                for (int f = 0; f < toAdd; f++)
                {
                    if (!ConsumeOneFromInventory(inv, fuelPrefab))
                        break;
                    fpNview.InvokeRPC("RPC_AddFuel");
                    totalFuelAdded++;
                    addedAny = true;
                }

                if (addedAny)
                {
                    restocked++;
                    CompanionsPlugin.Log.LogDebug(
                        $"[Radial] Restock: added fuel to \"{fp.m_name}\" " +
                        $"({currentFuel:F0}→{currentFuel + toAdd:F0}/{maxFuel:F0})");
                }
            }

            CompanionsPlugin.Log.LogInfo(
                $"[Radial] Restock: refueled {restocked} fires, {totalFuelAdded} total fuel added");

            if (restocked > 0)
            {
                var zanim = _companion.GetComponent<ZSyncAnimation>();
                if (zanim != null)
                    zanim.SetTrigger("interact");

                if (_companionTalk != null)
                    _companionTalk.Say(ModLocalization.Loc("hc_speech_restock_done"), "Action");
            }
            else
            {
                if (_companionTalk != null)
                    _companionTalk.Say(ModLocalization.Loc("hc_speech_restock_nothing"), "Action");
            }
        }

        private static int CountItemInInventory(Inventory inv, string prefabName)
        {
            int count = 0;
            var items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                string name = items[i].m_dropPrefab?.name ?? items[i].m_shared?.m_name;
                if (name == prefabName)
                    count += items[i].m_stack;
            }
            return count;
        }

        private static bool ConsumeOneFromInventory(Inventory inv, string prefabName)
        {
            var items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                string name = items[i].m_dropPrefab?.name ?? items[i].m_shared?.m_name;
                if (name == prefabName)
                {
                    inv.RemoveItem(items[i], 1);
                    return true;
                }
            }
            return false;
        }

        private static void FindNearbyContainers(Vector3 center, float radius, System.Collections.Generic.List<Container> results)
        {
            results.Clear();
            var tempPieces = new System.Collections.Generic.List<Piece>();
            Piece.GetAllPiecesInRadius(center, radius, tempPieces);
            for (int i = 0; i < tempPieces.Count; i++)
            {
                var piece = tempPieces[i];
                if (piece == null) continue;
                var c = piece.GetComponentInChildren<Container>();
                if (c == null) continue;
                if (c.GetInventory() == null) continue;
                // Skip companion inventories (they have Character component)
                if (c.GetComponent<Character>() != null) continue;
                if (c.IsInUse()) continue;
                results.Add(c);
            }
        }

        private void ToggleFollow()
        {
            bool next = !_companion.GetFollow();
            _companion.SetFollow(next);
            var zdo = _companionNview?.GetZDO();
            int mode = zdo?.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                       ?? CompanionSetup.ModeFollow;
            _companion.ApplyFollowMode(mode);
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
            for (int i = 0; i < _innerSegments.Count && i < _innerSegDots.Count; i++)
            {
                if (_innerSegDots[i] != null)
                    _innerSegDots[i].color = _innerSegments[i].IsActive ? ActiveDot : InactiveDot;
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
            bool follow      = _companion.GetFollow();
            bool stayHome    = _companion.GetStayHome();
            bool wander      = _companion.GetWander();
            bool autoPickup  = zdo?.GetBool(CompanionSetup.AutoPickupHash, true) ?? true;
            bool commandable = _companion.GetIsCommandable();

            _segments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_follow"), ActionId = ActionFollow,
                IsToggle = true, IsActive = follow,
                IconColor = new Color(0.40f, 0.75f, 0.40f)
            });

            if (!_isDverger)
            {
                _segments.Add(new Segment {
                    Label = ModLocalization.Loc("hc_radial_wood"), ActionId = ActionGatherWood,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeGatherWood,
                    IconColor = new Color(0.65f, 0.45f, 0.25f)
                });
                _segments.Add(new Segment {
                    Label = ModLocalization.Loc("hc_radial_stone"), ActionId = ActionGatherStone,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeGatherStone,
                    IconColor = new Color(0.60f, 0.60f, 0.60f)
                });
                _segments.Add(new Segment {
                    Label = ModLocalization.Loc("hc_radial_ore"), ActionId = ActionGatherOre,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeGatherOre,
                    IconColor = new Color(0.75f, 0.55f, 0.15f)
                });
                _segments.Add(new Segment {
                    Label = ModLocalization.Loc("hc_radial_forage"), ActionId = ActionForage,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeForage,
                    IconColor = new Color(0.45f, 0.75f, 0.30f)
                });
                _segments.Add(new Segment {
                    Label = ModLocalization.Loc("hc_radial_smelt"), ActionId = ActionSmelt,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeSmelt,
                    IconColor = new Color(0.85f, 0.45f, 0.15f)
                });
                _segments.Add(new Segment {
                    Label = ModLocalization.Loc("hc_radial_hunt"), ActionId = ActionHunt,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeHunt,
                    IconColor = new Color(0.80f, 0.35f, 0.30f)
                });
                _segments.Add(new Segment {
                    Label = ModLocalization.Loc("hc_radial_farm"), ActionId = ActionFarm,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeFarm,
                    IconColor = new Color(0.45f, 0.70f, 0.25f)
                });
                _segments.Add(new Segment {
                    Label = ModLocalization.Loc("hc_radial_fish"), ActionId = ActionFish,
                    IsMode = true, IsActive = currentMode == CompanionSetup.ModeFish,
                    IconColor = new Color(0.30f, 0.55f, 0.85f)
                });
            }

            _segments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_stayhome"), ActionId = ActionStayHome,
                IsToggle = true, IsActive = stayHome,
                IconColor = new Color(0.35f, 0.60f, 0.90f)
            });
            _segments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_sethome"), ActionId = ActionSetHome,
                IsToggle = false, IsMode = false, IsActive = false,
                IconColor = new Color(0.25f, 0.70f, 0.70f)
            });
            _segments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_wander"), ActionId = ActionWander,
                IsToggle = true, IsActive = wander,
                IconColor = new Color(0.50f, 0.80f, 0.35f)
            });
            _segments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_pickup"), ActionId = ActionAutoPickup,
                IsToggle = true, IsActive = autoPickup,
                IconColor = new Color(0.90f, 0.70f, 0.15f)
            });

            _segments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_command"), ActionId = ActionCommand,
                IsToggle = true, IsActive = commandable,
                IconColor = new Color(0.80f, 0.35f, 0.35f)
            });

            // Utility modes — persistent periodic actions
            _segments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_repair"), ActionId = ActionRepair,
                IsMode = true, IsActive = currentMode == CompanionSetup.ModeRepairBuildings,
                IconColor = new Color(0.70f, 0.55f, 0.30f)
            });
            _segments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_restock"), ActionId = ActionRestock,
                IsMode = true, IsActive = currentMode == CompanionSetup.ModeRestock,
                IconColor = new Color(0.90f, 0.55f, 0.20f)
            });


            // Inner ring: combat stances + despawn
            _innerSegments.Clear();
            int currentStance = _companion.GetCombatStance();
            _innerSegments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_balanced"), ActionId = ActionStanceBalanced,
                IsActive = currentStance == CompanionSetup.StanceBalanced,
                IconColor = new Color(0.70f, 0.70f, 0.70f)
            });
            _innerSegments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_passive"), ActionId = ActionStancePassive,
                IsActive = currentStance == CompanionSetup.StancePassive,
                IconColor = new Color(0.40f, 0.75f, 0.90f)
            });
            _innerSegments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_ranged"), ActionId = ActionStanceRanged,
                IsActive = currentStance == CompanionSetup.StanceRanged,
                IconColor = new Color(0.85f, 0.65f, 0.25f)
            });
            _innerSegments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_melee"), ActionId = ActionStanceMelee,
                IsActive = currentStance == CompanionSetup.StanceMelee,
                IconColor = new Color(0.85f, 0.35f, 0.30f)
            });
            _innerSegments.Add(new Segment {
                Label = ModLocalization.Loc("hc_radial_despawn"), ActionId = ActionDespawn,
                IsActive = false,
                IconColor = new Color(0.90f, 0.20f, 0.20f)
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
            rootRT.sizeDelta = new Vector2(700f, 700f);

            // Outer ring background — donut with transparent centre hole
            _bgCircle = new GameObject("BgDonut", typeof(RectTransform), typeof(Image),
                typeof(CanvasGroup));
            _bgCircle.transform.SetParent(_root.transform, false);
            var bgRT = _bgCircle.GetComponent<RectTransform>();
            bgRT.anchorMin = new Vector2(0.5f, 0.5f);
            bgRT.anchorMax = new Vector2(0.5f, 0.5f);
            bgRT.pivot = new Vector2(0.5f, 0.5f);
            bgRT.sizeDelta = new Vector2(680f, 680f);
            var bgImg = _bgCircle.GetComponent<Image>();
            bgImg.sprite = GetDonutSprite();
            bgImg.color = BgColor;
            bgImg.raycastTarget = false;
            _bgCanvasGroup = _bgCircle.GetComponent<CanvasGroup>();
            _bgCanvasGroup.blocksRaycasts = false;

            // Center container — groups name, action, state text
            _centerContainer = new GameObject("CenterContainer",
                typeof(RectTransform), typeof(CanvasGroup));
            _centerContainer.transform.SetParent(_root.transform, false);
            var ccRT = _centerContainer.GetComponent<RectTransform>();
            ccRT.anchorMin = new Vector2(0.5f, 0.5f);
            ccRT.anchorMax = new Vector2(0.5f, 0.5f);
            ccRT.pivot = new Vector2(0.5f, 0.5f);
            ccRT.sizeDelta = new Vector2(160f, 60f);
            _centerCanvasGroup = _centerContainer.GetComponent<CanvasGroup>();
            _centerCanvasGroup.blocksRaycasts = false;

            // Center text — companion name
            _centerName = MakeText(_centerContainer.transform, "CenterName", "", font, 14f,
                new Color(0.83f, 0.64f, 0.31f, 1f), TextAlignmentOptions.Center);
            var nameRT = _centerName.GetComponent<RectTransform>();
            nameRT.anchorMin = new Vector2(0.5f, 0.5f);
            nameRT.anchorMax = new Vector2(0.5f, 0.5f);
            nameRT.pivot = new Vector2(0.5f, 0.5f);
            nameRT.sizeDelta = new Vector2(160f, 20f);
            nameRT.anchoredPosition = new Vector2(0f, 14f);

            // Center text — hovered action name
            _centerAction = MakeText(_centerContainer.transform, "CenterAction", "", font, 12f,
                TextNormal, TextAlignmentOptions.Center);
            var actRT = _centerAction.GetComponent<RectTransform>();
            actRT.anchorMin = new Vector2(0.5f, 0.5f);
            actRT.anchorMax = new Vector2(0.5f, 0.5f);
            actRT.pivot = new Vector2(0.5f, 0.5f);
            actRT.sizeDelta = new Vector2(160f, 18f);
            actRT.anchoredPosition = new Vector2(0f, -4f);

            // Center text — state (ON/OFF)
            _centerState = MakeText(_centerContainer.transform, "CenterState", "", font, 11f,
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
            _segCanvasGroups.Clear();
            _segRTs.Clear();
            _segFinalPositions.Clear();

            float segArc = 360f / _segments.Count;

            for (int i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];

                // Position: clockwise from top
                float angle = 90f - segArc * i;
                float rad = angle * Mathf.Deg2Rad;
                float x = Mathf.Cos(rad) * RingRadius;
                float y = Mathf.Sin(rad) * RingRadius;

                // Store final position for animation
                _segFinalPositions.Add(new Vector2(x, y));

                // Segment container with CanvasGroup for alpha animation
                var segGO = new GameObject($"Seg_{i}",
                    typeof(RectTransform), typeof(CanvasGroup));
                segGO.transform.SetParent(_root.transform, false);
                var segRT = segGO.GetComponent<RectTransform>();
                segRT.anchorMin = new Vector2(0.5f, 0.5f);
                segRT.anchorMax = new Vector2(0.5f, 0.5f);
                segRT.pivot = new Vector2(0.5f, 0.5f);
                segRT.sizeDelta = new Vector2(SegSize, SegSize);
                segRT.anchoredPosition = new Vector2(x, y);
                var segCG = segGO.GetComponent<CanvasGroup>();
                segCG.blocksRaycasts = false;

                // Highlight circle (behind icon, invisible by default)
                var hlGO = new GameObject("Highlight", typeof(RectTransform), typeof(Image));
                hlGO.transform.SetParent(segGO.transform, false);
                var hlRT = hlGO.GetComponent<RectTransform>();
                hlRT.anchorMin = new Vector2(0.5f, 0.5f);
                hlRT.anchorMax = new Vector2(0.5f, 0.5f);
                hlRT.pivot = new Vector2(0.5f, 0.5f);
                hlRT.sizeDelta = new Vector2(HighlightSize, HighlightSize);
                hlRT.anchoredPosition = Vector2.zero;
                var hlImg = hlGO.GetComponent<Image>();
                hlImg.sprite = GetCircleSprite();
                hlImg.color = Color.clear;
                hlImg.raycastTarget = false;

                // Icon background (colored circle)
                var iconBgGO = new GameObject("IconBg", typeof(RectTransform), typeof(Image));
                iconBgGO.transform.SetParent(segGO.transform, false);
                var iconBgRT = iconBgGO.GetComponent<RectTransform>();
                iconBgRT.anchorMin = new Vector2(0.5f, 0.5f);
                iconBgRT.anchorMax = new Vector2(0.5f, 0.5f);
                iconBgRT.pivot = new Vector2(0.5f, 0.5f);
                iconBgRT.sizeDelta = new Vector2(IconSize, IconSize);
                iconBgRT.anchoredPosition = Vector2.zero;
                var iconBgImg = iconBgGO.GetComponent<Image>();
                iconBgImg.sprite = GetCircleSprite();
                var bgTint = seg.IconColor * 0.35f;
                iconBgImg.color = new Color(bgTint.r, bgTint.g, bgTint.b, 0.5f);
                iconBgImg.raycastTarget = false;

                // Icon (texture-based)
                var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                iconGO.transform.SetParent(segGO.transform, false);
                var iconRT = iconGO.GetComponent<RectTransform>();
                iconRT.anchorMin = new Vector2(0.5f, 0.5f);
                iconRT.anchorMax = new Vector2(0.5f, 0.5f);
                iconRT.pivot = new Vector2(0.5f, 0.5f);
                iconRT.sizeDelta = new Vector2(IconSize * 0.975f, IconSize * 0.975f);
                iconRT.anchoredPosition = Vector2.zero;
                var iconImg = iconGO.GetComponent<Image>();
                iconImg.sprite = GetActionIcon(seg.ActionId);
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
                labelRT.anchoredPosition = new Vector2(0f, 14f);

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
                _segCanvasGroups.Add(segCG);
                _segRTs.Add(segRT);
            }

            // ── Inner ring segments (stances + despawn) ──
            for (int i = 0; i < _innerSegmentGOs.Count; i++)
                if (_innerSegmentGOs[i] != null) Destroy(_innerSegmentGOs[i]);
            _innerSegmentGOs.Clear();
            _innerSegHighlights.Clear();
            _innerSegIcons.Clear();
            _innerSegLabels.Clear();
            _innerSegDots.Clear();
            _innerSegCanvasGroups.Clear();
            _innerSegRTs.Clear();
            _innerSegFinalPositions.Clear();

            if (_innerSegments.Count > 0)
            {
                float innerArc = 360f / _innerSegments.Count;
                for (int i = 0; i < _innerSegments.Count; i++)
                {
                    var seg = _innerSegments[i];
                    float iAngle = 90f - innerArc * i;
                    float iRad = iAngle * Mathf.Deg2Rad;
                    float ix = Mathf.Cos(iRad) * InnerRingRadius;
                    float iy = Mathf.Sin(iRad) * InnerRingRadius;
                    _innerSegFinalPositions.Add(new Vector2(ix, iy));

                    var iSegGO = new GameObject($"InnerSeg_{i}",
                        typeof(RectTransform), typeof(CanvasGroup));
                    iSegGO.transform.SetParent(_root.transform, false);
                    var iSegRT = iSegGO.GetComponent<RectTransform>();
                    iSegRT.anchorMin = new Vector2(0.5f, 0.5f);
                    iSegRT.anchorMax = new Vector2(0.5f, 0.5f);
                    iSegRT.pivot = new Vector2(0.5f, 0.5f);
                    iSegRT.sizeDelta = new Vector2(InnerSegSize, InnerSegSize);
                    iSegRT.anchoredPosition = new Vector2(ix, iy);
                    var iSegCG = iSegGO.GetComponent<CanvasGroup>();
                    iSegCG.blocksRaycasts = false;

                    // Highlight
                    var iHlGO = new GameObject("Highlight", typeof(RectTransform), typeof(Image));
                    iHlGO.transform.SetParent(iSegGO.transform, false);
                    var iHlRT = iHlGO.GetComponent<RectTransform>();
                    iHlRT.anchorMin = new Vector2(0.5f, 0.5f);
                    iHlRT.anchorMax = new Vector2(0.5f, 0.5f);
                    iHlRT.pivot = new Vector2(0.5f, 0.5f);
                    iHlRT.sizeDelta = new Vector2(InnerIconSize + 8f, InnerIconSize + 8f);
                    iHlRT.anchoredPosition = Vector2.zero;
                    var iHlImg = iHlGO.GetComponent<Image>();
                    iHlImg.sprite = GetCircleSprite();
                    iHlImg.color = Color.clear;
                    iHlImg.raycastTarget = false;

                    // Icon bg
                    var iIconBgGO = new GameObject("IconBg", typeof(RectTransform), typeof(Image));
                    iIconBgGO.transform.SetParent(iSegGO.transform, false);
                    var iIconBgRT = iIconBgGO.GetComponent<RectTransform>();
                    iIconBgRT.anchorMin = new Vector2(0.5f, 0.5f);
                    iIconBgRT.anchorMax = new Vector2(0.5f, 0.5f);
                    iIconBgRT.pivot = new Vector2(0.5f, 0.5f);
                    iIconBgRT.sizeDelta = new Vector2(InnerIconSize, InnerIconSize);
                    iIconBgRT.anchoredPosition = Vector2.zero;
                    var iIconBgImg = iIconBgGO.GetComponent<Image>();
                    iIconBgImg.sprite = GetCircleSprite();
                    var iBgTint = seg.IconColor * 0.35f;
                    iIconBgImg.color = new Color(iBgTint.r, iBgTint.g, iBgTint.b, 0.5f);
                    iIconBgImg.raycastTarget = false;

                    // Icon
                    var iIconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
                    iIconGO.transform.SetParent(iSegGO.transform, false);
                    var iIconRT = iIconGO.GetComponent<RectTransform>();
                    iIconRT.anchorMin = new Vector2(0.5f, 0.5f);
                    iIconRT.anchorMax = new Vector2(0.5f, 0.5f);
                    iIconRT.pivot = new Vector2(0.5f, 0.5f);
                    iIconRT.sizeDelta = new Vector2(InnerIconSize * 0.95f, InnerIconSize * 0.95f);
                    iIconRT.anchoredPosition = Vector2.zero;
                    var iIconImg = iIconGO.GetComponent<Image>();
                    iIconImg.sprite = GetActionIcon(seg.ActionId);
                    iIconImg.color = new Color(0.9f, 0.9f, 0.9f, 0.85f);
                    iIconImg.raycastTarget = false;

                    // Label
                    var iLabel = MakeText(iSegGO.transform, "Label", seg.Label, font, 7.5f,
                        TextNormal, TextAlignmentOptions.Center);
                    var iLabelRT = iLabel.GetComponent<RectTransform>();
                    iLabelRT.anchorMin = new Vector2(0f, 0f);
                    iLabelRT.anchorMax = new Vector2(1f, 0f);
                    iLabelRT.pivot = new Vector2(0.5f, 1f);
                    iLabelRT.sizeDelta = new Vector2(0f, 12f);
                    iLabelRT.anchoredPosition = new Vector2(0f, 10f);

                    // Active dot
                    Image iDotImg = null;
                    if (seg.ActionId != ActionDespawn)
                    {
                        var iDotGO = new GameObject("Dot", typeof(RectTransform), typeof(Image));
                        iDotGO.transform.SetParent(iSegGO.transform, false);
                        var iDotRT = iDotGO.GetComponent<RectTransform>();
                        iDotRT.anchorMin = new Vector2(0.5f, 0f);
                        iDotRT.anchorMax = new Vector2(0.5f, 0f);
                        iDotRT.pivot = new Vector2(0.5f, 0.5f);
                        iDotRT.sizeDelta = new Vector2(6f, 6f);
                        iDotRT.anchoredPosition = new Vector2(0f, 6f);
                        iDotImg = iDotGO.GetComponent<Image>();
                        iDotImg.sprite = GetCircleSprite();
                        iDotImg.color = seg.IsActive ? ActiveDot : InactiveDot;
                        iDotImg.raycastTarget = false;
                    }

                    _innerSegmentGOs.Add(iSegGO);
                    _innerSegHighlights.Add(iHlImg);
                    _innerSegIcons.Add(iIconImg);
                    _innerSegLabels.Add(iLabel);
                    _innerSegDots.Add(iDotImg);
                    _innerSegCanvasGroups.Add(iSegCG);
                    _innerSegRTs.Add(iSegRT);
                }
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
                _centerName.text = string.IsNullOrEmpty(name) ? ModLocalization.Loc("hc_msg_name_default") : name;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Icon loading — PNG textures from embedded resources
        // ══════════════════════════════════════════════════════════════════

        private static string GetRadialTextureName(int actionId)
        {
            switch (actionId)
            {
                case ActionFollow:      return "Follow";
                case ActionGatherWood:  return "GatherWood";
                case ActionGatherStone: return "GatherStone";
                case ActionGatherOre:   return "GatherOre";
                case ActionForage:      return "GatherForage";
                case ActionSmelt:       return "Smelt";
                case ActionStayHome:    return "StayHome";
                case ActionSetHome:     return "SetHome";
                case ActionWander:      return "Wander";
                case ActionAutoPickup:  return "AutoPickup";
                case ActionCommand:          return "Command";
                case ActionRepair:           return "Repair";
                case ActionRestock:          return "Restock";
                case ActionHunt:             return "Hunt";
                case ActionFarm:             return "Farm";
                case ActionFish:             return "Fish";
                case ActionStanceBalanced:   return "Balanced";
                case ActionStancePassive:    return "Passive";
                case ActionStanceRanged:     return "Ranged";
                case ActionStanceMelee:      return "Melee";
                default:                     return null;
            }
        }

        private static Sprite GetActionIcon(int actionId)
        {
            if (_iconCache.TryGetValue(actionId, out var cached)) return cached;

            string texName = GetRadialTextureName(actionId);
            Texture2D tex = texName != null ? TextureLoader.LoadRadialTexture(texName) : null;

            if (tex != null)
            {
                tex.filterMode = FilterMode.Bilinear;
                var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                _iconCache[actionId] = sprite;
                return sprite;
            }

            // Fallback: procedural icon for missing textures
            return GetProceduralIcon(actionId);
        }

        private static Sprite GetProceduralIcon(int actionId)
        {
            int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = Color.clear;

            Color w = new Color(1f, 1f, 1f, 0.95f);
            float c = (size - 1) * 0.5f;

            switch (actionId)
            {
                case ActionAutoPickup:
                    // Magnet — U-shape with field arcs
                    DrawLine(pixels, size, c - 14, c + 22, c - 14, c - 4, w, 6f);
                    DrawLine(pixels, size, c + 14, c + 22, c + 14, c - 4, w, 6f);
                    DrawArc(pixels, size, c, c - 4, 14f, 180f, 360f, w, 6f);
                    DrawLine(pixels, size, c - 20, c + 22, c - 8, c + 22, w, 4f);
                    DrawLine(pixels, size, c + 8, c + 22, c + 20, c + 22, w, 4f);
                    DrawArc(pixels, size, c, c + 4, 24f, 210f, 240f, w, 2.5f);
                    DrawArc(pixels, size, c, c + 4, 24f, 300f, 330f, w, 2.5f);
                    break;

                case ActionHunt:
                    // Crosshair — circle with four tick marks
                    DrawArc(pixels, size, c, c, 18f, 0f, 360f, w, 4f);
                    DrawLine(pixels, size, c, c - 28, c, c - 20, w, 4f);
                    DrawLine(pixels, size, c, c + 20, c, c + 28, w, 4f);
                    DrawLine(pixels, size, c - 28, c, c - 20, c, w, 4f);
                    DrawLine(pixels, size, c + 20, c, c + 28, c, w, 4f);
                    break;

                case ActionFarm:
                    // Wheat stalk — vertical stem with angled grain lines
                    DrawLine(pixels, size, c, c - 28, c, c + 24, w, 4f);
                    DrawLine(pixels, size, c, c - 18, c - 14, c - 28, w, 3f);
                    DrawLine(pixels, size, c, c - 18, c + 14, c - 28, w, 3f);
                    DrawLine(pixels, size, c, c - 8, c - 12, c - 18, w, 3f);
                    DrawLine(pixels, size, c, c - 8, c + 12, c - 18, w, 3f);
                    DrawLine(pixels, size, c, c + 2, c - 10, c - 8, w, 3f);
                    DrawLine(pixels, size, c, c + 2, c + 10, c - 8, w, 3f);
                    break;

                case ActionFish:
                    // Fish hook — curved hook with line
                    DrawLine(pixels, size, c, c - 28, c, c - 4, w, 4f);
                    DrawArc(pixels, size, c, c - 4, 14f, 180f, 360f, w, 4f);
                    DrawLine(pixels, size, c + 14, c - 4, c + 14, c + 10, w, 4f);
                    // Barb
                    DrawLine(pixels, size, c + 14, c + 10, c + 8, c + 4, w, 3f);
                    break;

                case ActionRepair:
                    // Hammer — handle + head
                    DrawLine(pixels, size, c - 10, c + 24, c + 8, c - 4, w, 5f); // handle
                    DrawLine(pixels, size, c - 2, c - 10, c + 18, c - 10, w, 6f);  // head horizontal
                    DrawLine(pixels, size, c - 2, c - 18, c - 2, c - 2, w, 6f);   // head left face
                    DrawLine(pixels, size, c + 18, c - 18, c + 18, c - 2, w, 6f); // head right face
                    DrawLine(pixels, size, c - 2, c - 18, c + 18, c - 18, w, 6f); // head top
                    break;

                case ActionRestock:
                    // Flame — campfire flame shape
                    DrawLine(pixels, size, c, c - 28, c - 4, c - 14, w, 4f);     // left flame
                    DrawLine(pixels, size, c, c - 28, c + 4, c - 14, w, 4f);     // right flame
                    DrawArc(pixels, size, c, c - 10, 8f, 180f, 360f, w, 4f);     // flame base
                    // Logs underneath
                    DrawLine(pixels, size, c - 16, c + 8, c + 16, c + 8, w, 5f);
                    DrawLine(pixels, size, c - 12, c + 16, c + 12, c + 16, w, 5f);
                    // Inner flame
                    DrawLine(pixels, size, c, c - 22, c, c - 12, w, 3f);
                    break;
            }

            tex.SetPixels(pixels);
            tex.Apply();
            var sprite = Sprite.Create(tex, new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100f);
            _iconCache[actionId] = sprite;
            return sprite;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Drawing primitives — used by procedural fallback icons
        // ══════════════════════════════════════════════════════════════════

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

                int minX = Mathf.Max(0, Mathf.FloorToInt(cx - halfT - 1));
                int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(cx + halfT + 1));
                int minY = Mathf.Max(0, Mathf.FloorToInt(cy - halfT - 1));
                int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(cy + halfT + 1));

                for (int py = minY; py <= maxY; py++)
                    for (int px = minX; px <= maxX; px++)
                    {
                        float dx = px - cx;
                        float dy = py - cy;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (d <= halfT - 0.5f)
                        {
                            pixels[py * size + px] = color;
                        }
                        else if (d <= halfT + 0.5f)
                        {
                            float aa = Mathf.Clamp01(halfT + 0.5f - d);
                            Color existing = pixels[py * size + px];
                            pixels[py * size + px] = Color.Lerp(existing, color, aa);
                        }
                    }
            }
        }

        private static void DrawArc(Color[] pixels, int size, float cx, float cy,
            float radius, float startDeg, float endDeg, Color color, float thickness)
        {
            float halfT = thickness * 0.5f;
            float range = endDeg - startDeg;
            int steps = Mathf.Max(16, Mathf.CeilToInt(Mathf.Abs(range) * radius * 0.08f));

            for (int s = 0; s <= steps; s++)
            {
                float t = s / (float)steps;
                float angle = Mathf.Lerp(startDeg, endDeg, t) * Mathf.Deg2Rad;
                float px = cx + Mathf.Cos(angle) * radius;
                float py = cy + Mathf.Sin(angle) * radius;

                int minX = Mathf.Max(0, Mathf.FloorToInt(px - halfT - 1));
                int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(px + halfT + 1));
                int minY = Mathf.Max(0, Mathf.FloorToInt(py - halfT - 1));
                int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(py + halfT + 1));

                for (int iy = minY; iy <= maxY; iy++)
                    for (int ix = minX; ix <= maxX; ix++)
                    {
                        float dx = ix - px;
                        float dy = iy - py;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (d <= halfT - 0.5f)
                        {
                            pixels[iy * size + ix] = color;
                        }
                        else if (d <= halfT + 0.5f)
                        {
                            float aa = Mathf.Clamp01(halfT + 0.5f - d);
                            Color existing = pixels[iy * size + ix];
                            pixels[iy * size + ix] = Color.Lerp(existing, color, aa);
                        }
                    }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Utility sprite — filled circle
        // ══════════════════════════════════════════════════════════════════

        private static Sprite _circleSprite;
        private static Sprite GetCircleSprite()
        {
            if (_circleSprite != null) return _circleSprite;
            int s = 256;
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
                    else if (dist <= c + 1.5f)
                        px[y * s + x] = new Color(1f, 1f, 1f, Mathf.Clamp01((c + 1.5f - dist) / 1.5f));
                    else
                        px[y * s + x] = Color.clear;
                }
            tex.SetPixels(px);
            tex.Apply();
            _circleSprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
            return _circleSprite;
        }

        /// <summary>
        /// Procedural donut (ring) sprite — filled circle with a transparent
        /// hole in the centre, used for the outer ring background so the gap
        /// between inner and outer rings is transparent.
        /// </summary>
        private static Sprite _donutSprite;
        private static Sprite GetDonutSprite()
        {
            if (_donutSprite != null) return _donutSprite;
            int s = 256;
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            var px = new Color[s * s];
            float c = (s - 1) * 0.5f;
            float outerR = c;
            // Inner hole at ~56% of outer radius
            // Visual: outer BG 540px (radius 270), hole at visual radius ~152 → 152/270 ≈ 0.563
            float innerR = c * 0.563f;
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float dx = x - c, dy = y - c;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist <= innerR - 1.5f)
                        px[y * s + x] = Color.clear;
                    else if (dist <= innerR)
                        px[y * s + x] = new Color(1f, 1f, 1f,
                            Mathf.Clamp01((dist - innerR + 1.5f) / 1.5f));
                    else if (dist <= outerR)
                        px[y * s + x] = Color.white;
                    else if (dist <= outerR + 1.5f)
                        px[y * s + x] = new Color(1f, 1f, 1f,
                            Mathf.Clamp01((outerR + 1.5f - dist) / 1.5f));
                    else
                        px[y * s + x] = Color.clear;
                }
            tex.SetPixels(px);
            tex.Apply();
            _donutSprite = Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100f);
            return _donutSprite;
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
            _segCanvasGroups.Clear();
            _segRTs.Clear();
            _segFinalPositions.Clear();

            for (int i = 0; i < _innerSegmentGOs.Count; i++)
                if (_innerSegmentGOs[i] != null) Destroy(_innerSegmentGOs[i]);
            _innerSegmentGOs.Clear();
            _innerSegHighlights.Clear();
            _innerSegIcons.Clear();
            _innerSegLabels.Clear();
            _innerSegDots.Clear();
            _innerSegCanvasGroups.Clear();
            _innerSegRTs.Clear();
            _innerSegFinalPositions.Clear();
            _innerSegments.Clear();

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
