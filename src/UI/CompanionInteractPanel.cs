using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Companions
{
    /// <summary>
    /// Companion inventory panel â€” clones the vanilla container UI from InventoryGui
    /// for authentic Valheim inventory visuals.  Positioned below the player inventory
    /// like a normal container.  Shows name input, weight display, inventory grid,
    /// and food slots.
    /// </summary>
    public class CompanionInteractPanel : MonoBehaviour
    {
        public static CompanionInteractPanel Instance { get; private set; }

        // â”€â”€ Public state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        public bool IsVisible => _visible;
        public bool IsNameInputFocused => _nameInput != null && _nameInput.isFocused;
        public CompanionSetup CurrentCompanion => _companion;

        public static bool IsOpenFor(CompanionSetup setup)
            => Instance != null && Instance._visible
            && Instance._companion != null && Instance._companion == setup;

        // â”€â”€ InventoryGui drag system reflection â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static readonly FieldInfo _dragItemField      = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo _dragInventoryField  = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
        private static readonly FieldInfo _dragAmountField     = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
        private static readonly MethodInfo _setupDragItem      = AccessTools.Method(typeof(InventoryGui), "SetupDragItem",
            new[] { typeof(ItemDrop.ItemData), typeof(Inventory), typeof(int) });

        // â”€â”€ InventoryGui m_currentContainer (detect external close) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static readonly FieldInfo _currentContainerField =
            AccessTools.Field(typeof(InventoryGui), "m_currentContainer");

        // â”€â”€ InventoryGrid force-rebuild reflection â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static readonly FieldInfo _gridWidthField =
            AccessTools.Field(typeof(InventoryGrid), "m_width");
        private static readonly FieldInfo _gridHeightField =
            AccessTools.Field(typeof(InventoryGrid), "m_height");
        private static readonly FieldInfo _gridElementsField =
            AccessTools.Field(typeof(InventoryGrid), "m_elements");

        // â”€â”€ Split dialog reflection â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static readonly MethodInfo _showSplitDialog = AccessTools.Method(
            typeof(InventoryGui), "ShowSplitDialog",
            new[] { typeof(ItemDrop.ItemData), typeof(Inventory) });

        // â”€â”€ Gamepad UIGroupHandler integration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static readonly FieldInfo _uiGroupsField =
            AccessTools.Field(typeof(InventoryGui), "m_uiGroups");
        private static readonly FieldInfo _containerGridField =
            AccessTools.Field(typeof(InventoryGui), "m_containerGrid");
        private static readonly FieldInfo _activeGroupField =
            AccessTools.Field(typeof(InventoryGui), "m_activeGroup");
        private static readonly MethodInfo _setActiveGroupMethod =
            AccessTools.Method(typeof(InventoryGui), "SetActiveGroup",
                new[] { typeof(int), typeof(bool) });
        private static readonly FieldInfo _gridSelectedField =
            AccessTools.Field(typeof(InventoryGrid), "m_selected");

        // â”€â”€ InventoryGrid Element reflection (for fixing equipped indicators) â”€â”€
        private static readonly Type _elementType =
            AccessTools.Inner(typeof(InventoryGrid), "Element");
        private static readonly FieldInfo _elemPosField =
            _elementType != null ? AccessTools.Field(_elementType, "m_pos") : null;
        private static readonly FieldInfo _elemEquipedField =
            _elementType != null ? AccessTools.Field(_elementType, "m_equiped") : null;
        private static readonly FieldInfo _elemQueuedField =
            _elementType != null ? AccessTools.Field(_elementType, "m_queued") : null;
        private static readonly FieldInfo _elemIconField =
            _elementType != null ? AccessTools.Field(_elementType, "m_icon") : null;
        private static readonly FieldInfo _elemAmountField =
            _elementType != null ? AccessTools.Field(_elementType, "m_amount") : null;
        private static readonly FieldInfo _elemGoField =
            _elementType != null ? AccessTools.Field(_elementType, "m_go") : null;

        // â”€â”€ Constants â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private const int FoodSlotCount = 3;
        private const string PrefKeyOffsetX = "HC_PanelOffsetX";
        private const string PrefKeyOffsetY = "HC_PanelOffsetY";

        // â”€â”€ Companion references â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private CompanionSetup   _companion;
        private Character        _companionChar;
        private CompanionFood    _companionFood;
        private Humanoid         _companionHumanoid;
        private Container        _companionContainer;
        private ZNetView         _companionNview;

        // â”€â”€ UI elements â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private GameObject      _root;
        private InventoryGrid   _grid;
        private TMP_InputField  _nameInput;
        private GameObject      _foodSlotsContainer;
        private Image[]           _foodSlotIcons;
        private TextMeshProUGUI[] _foodSlotCounts;

        // Equipment panel (visible only when ExtraSlots mod is installed)
        private GameObject _equipPanel;
        private Image[]    _equipSlotIcons;
        private string[]   _equipSlotIds;
        private int        _equipSlotCount;
        private float      _equipTileSize = 64f;

        // Weight & armor display (on the cloned container panel)
        private TMP_Text _weightText;
        private TMP_Text _armorText;

        private bool _built;
        private bool _visible;
        private bool _builtForDverger;
        private bool _gridCreated;  // true after first UpdateInventory (grid elements exist)
        private readonly HashSet<ItemDrop.ItemData> _slotItemsInPanel = new HashSet<ItemDrop.ItemData>();

        // â”€â”€ Gamepad UIGroupHandler state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private UIGroupHandler _uiGroup;             // our companion grid's UIGroupHandler
        private UIGroupHandler _savedVanillaGroup;   // original m_uiGroups[0]
        private InventoryGrid  _savedContainerGrid;  // original m_containerGrid
        private bool           _gamepadInjected;     // true while our group is in m_uiGroups

        // â”€â”€ Drag-to-reposition (F7) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private bool _dragMode;
        private bool _dragging;
        private Vector2 _dragMouseStart;
        private Vector2 _dragPanelStart;
        private Vector2 _defaultPosition;   // panel's original anchoredPosition
        private Vector2 _userOffset;        // saved offset from default position
        private CanvasGroup _canvasGroup;

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Lifecycle
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private void Awake()  { Instance = this; }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Teardown();
        }

        private void Update()
        {
            if (!_visible || _companion == null) return;

            var invGui = InventoryGui.instance;
            if (invGui == null || !invGui.IsContainerOpen())
            { Hide(); return; }

            // Detect external close â€” another container opened or vanilla closed us
            if (_currentContainerField != null)
            {
                var currentContainer = _currentContainerField.GetValue(invGui) as Container;
                if (currentContainer == null || currentContainer != _companionContainer)
                { Hide(); return; }
            }

            // Unity null check â€” companion may have been destroyed
            if (_companion == null || !_companion)
            { Hide(); return; }

            if (Player.m_localPlayer != null)
            {
                float dist = Vector3.Distance(
                    Player.m_localPlayer.transform.position,
                    _companion.transform.position);
                if (dist > 5f) { Hide(); return; }
            }

            // F7: toggle drag-to-reposition mode
            if (Input.GetKeyDown(KeyCode.F7) && _root != null)
            {
                _dragMode = !_dragMode;
                SetDragModeUI(_dragMode);
            }

            if (_dragMode)
                HandleDragReposition();
        }

        /// <summary>
        /// LateUpdate runs after ALL Update() calls, guaranteeing we run after
        /// vanilla InventoryGui.UpdateContainer(). This is critical because:
        /// 1. UpdateContainer re-enables m_container.gameObject â€” we disable it here
        /// 2. UpdateContainer calls m_containerGrid.UpdateInventory with null player,
        ///    stripping equip indicators â€” we re-update with the real player here
        /// </summary>
        private void LateUpdate()
        {
            if (!_visible || _companion == null || _dragMode) return;

            // Suppress vanilla container panel â€” must run AFTER vanilla's UpdateContainer
            // re-enables it each frame when m_currentContainer is set
            var invGui = InventoryGui.instance;
            if (invGui != null && invGui.m_container != null)
                invGui.m_container.gameObject.SetActive(false);

            // Do NOT call _grid.UpdateInventory() here â€” vanilla's UpdateContainer already
            // calls m_containerGrid.UpdateInventory() (which IS our grid) every frame.
            // Calling it again would run UpdateGamepad() twice per frame, causing D-pad
            // presses to skip rows (ZInput.GetButtonDown returns true for the whole frame).
            //
            // Instead, fix the equipped indicators that vanilla renders incorrectly
            // (it passes null player, disabling all equipped icons), then update our
            // custom weight/armor and food slot displays.
            FixEquippedIndicators();
            UpdateWeightAndArmor();
            RefreshFoodSlots(GetStorageInventory());
            RefreshEquipmentPanel();
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Public API
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        public void Show(CompanionSetup companion)
        {
            _companion          = companion;
            _companionChar      = companion.GetComponent<Character>();
            _companionFood      = companion.GetComponent<CompanionFood>();
            _companionHumanoid  = companion.GetComponent<Humanoid>();
            _companionContainer = companion.GetComponent<Container>();
            _companionNview     = companion.GetComponent<ZNetView>();

            var inv = _companionHumanoid?.GetInventory();
            int itemCount = inv?.NrOfItems() ?? -1;
            float totalWeight = inv?.GetTotalWeight() ?? -1f;
            CompanionsPlugin.Log.LogDebug(
                $"[UI] Show â€” companion=\"{companion.name}\" id={companion.GetInstanceID()} " +
                $"char={_companionChar != null} humanoid={_companionHumanoid != null} " +
                $"food={_companionFood != null} nview={_companionNview != null} " +
                $"items={itemCount} weight={totalWeight:F1}");

            EnsureCompanionOwnership();

            bool isDverger = !companion.CanWearArmor();

            // Rebuild if destroyed, never built, or companion type changed
            if (_root == null) _built = false;
            if (_built && isDverger != _builtForDverger)
            {
                Destroy(_root);
                _root = null;
                _built = false;
            }
            _builtForDverger = isDverger;
            CompanionsPlugin.Log.LogDebug(
                $"[UI] Show â€” built={_built} gridCreated={_gridCreated} " +
                $"grid={_grid != null} root={_root != null}");
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

            // Show/hide food slots based on companion type
            if (_foodSlotsContainer != null)
                _foodSlotsContainer.SetActive(!_builtForDverger);

            // Show/hide equipment panel (ExtraSlots only)
            if (_equipPanel != null)
                _equipPanel.SetActive(ExtraSlotsCompat.IsLoaded);

            // Force grid to fully rebuild its elements whenever we show a (possibly
            // different) companion.  Without this, a respawned companion reuses stale
            // InventoryGrid elements from the previous session, causing ghost items.
            if (_grid != null)
            {
                int oldWidth = _gridWidthField != null ? (int)_gridWidthField.GetValue(_grid) : -1;
                int oldHeight = _gridHeightField != null ? (int)_gridHeightField.GetValue(_grid) : -1;

                // Destroy existing grid elements explicitly to prevent any visual artifacts
                if (_gridElementsField != null)
                {
                    var elements = _gridElementsField.GetValue(_grid) as System.Collections.IList;
                    if (elements != null && elements.Count > 0)
                    {
                        CompanionsPlugin.Log.LogDebug(
                            $"[UI] Show â€” destroying {elements.Count} old grid elements");
                        foreach (var elem in elements)
                        {
                            var goField = elem.GetType().GetField("m_go");
                            var go = goField?.GetValue(elem) as GameObject;
                            if (go != null) UnityEngine.Object.Destroy(go);
                        }
                        elements.Clear();
                    }
                }

                // Reset both width and height to 0 â€” guarantees UpdateGui will recreate elements
                _gridWidthField?.SetValue(_grid, 0);
                _gridHeightField?.SetValue(_grid, 0);
                _gridCreated = false;
                CompanionsPlugin.Log.LogDebug(
                    $"[UI] Show â€” forced grid rebuild (old={oldWidth}x{oldHeight} â†’ 0x0, gridCreated=false)");
            }

            // Hide vanilla container panel immediately to prevent single-frame flicker
            var invGui = InventoryGui.instance;
            if (invGui != null && invGui.m_container != null)
                invGui.m_container.gameObject.SetActive(false);

            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            _visible = true;

            // Immediately refresh grid so the very first visible frame is correct
            var showInv = GetStorageInventory();
            CompanionsPlugin.Log.LogDebug(
                $"[UI] Show â€” pre-UpdateGrid inv={showInv != null} " +
                $"items={showInv?.NrOfItems() ?? -1} weight={showInv?.GetTotalWeight() ?? -1f:F1} " +
                $"invDim={showInv?.GetWidth() ?? -1}x{showInv?.GetHeight() ?? -1}");

            // Validate inventory dimensions match expected values
            if (showInv != null && (showInv.GetWidth() <= 0 || showInv.GetHeight() <= 0))
            {
                CompanionsPlugin.Log.LogError(
                    $"[UI] Show â€” INVALID inventory dimensions: {showInv.GetWidth()}x{showInv.GetHeight()}! " +
                    "This will cause grid rendering issues.");
            }

            UpdateGrid();

            // Inject our UIGroupHandler into vanilla's group system for gamepad support
            InjectGamepadGroups();
        }

        public void Hide()
        {
            if (!_visible) return;
            _visible = false;

            // Restore vanilla gamepad groups before hiding
            RestoreGamepadGroups();

            // Exit drag mode cleanly, saving position
            if (_dragMode)
            {
                _dragMode = false;
                _dragging = false;
                SavePanelOffset();
                if (_canvasGroup != null) _canvasGroup.interactable = true;
            }

            if (_nameInput != null)
                _nameInput.onValueChanged.RemoveAllListeners();
            if (_root != null) _root.SetActive(false);

            // Check if another container took over before we clear our reference â€”
            // if so, don't close InventoryGui (the new container should stay open)
            bool anotherContainerOpen = false;
            if (_companionContainer != null && InventoryGui.instance != null && _currentContainerField != null)
            {
                var current = _currentContainerField.GetValue(InventoryGui.instance) as Container;
                anotherContainerOpen = current != null && current != _companionContainer;
            }

            _companion          = null;
            _companionChar      = null;
            _companionFood      = null;
            _companionHumanoid  = null;
            _companionContainer = null;
            _companionNview     = null;

            // Close vanilla InventoryGui only if no other container took over
            if (!anotherContainerOpen && InventoryGui.instance != null && InventoryGui.IsVisible())
                InventoryGui.instance.Hide();
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Drag-to-reposition (F7)
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private void SetDragModeUI(bool enabled)
        {
            if (_canvasGroup == null && _root != null)
                _canvasGroup = _root.GetComponent<CanvasGroup>() ?? _root.AddComponent<CanvasGroup>();

            if (_canvasGroup != null)
                _canvasGroup.interactable = !enabled;

            _dragging = false;

            if (enabled)
            {
                CompanionsPlugin.Log.LogInfo("[UI] F7 â€” drag-to-reposition mode ENABLED");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    ModLocalization.Loc("hc_msg_reposition_on"));
            }
            else
            {
                SavePanelOffset();
                CompanionsPlugin.Log.LogInfo(
                    $"[UI] F7 â€” drag-to-reposition mode DISABLED, offset=({_userOffset.x:F0}, {_userOffset.y:F0})");
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    ModLocalization.Loc("hc_msg_reposition_off"));
            }
        }

        private void HandleDragReposition()
        {
            var rt = _root != null ? _root.GetComponent<RectTransform>() : null;
            if (rt == null) return;

            if (Input.GetMouseButtonDown(0))
            {
                if (RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition))
                {
                    _dragging = true;
                    _dragMouseStart = Input.mousePosition;
                    _dragPanelStart = rt.anchoredPosition;
                    CompanionsPlugin.Log.LogDebug(
                        $"[UI] Drag started â€” mouse={_dragMouseStart} panel={_dragPanelStart}");
                }
            }

            if (_dragging && Input.GetMouseButton(0))
            {
                Vector2 delta = (Vector2)Input.mousePosition - _dragMouseStart;
                var canvas = rt.GetComponentInParent<Canvas>();
                if (canvas != null)
                    delta /= canvas.scaleFactor;
                rt.anchoredPosition = _dragPanelStart + delta;
            }

            if (Input.GetMouseButtonUp(0) && _dragging)
            {
                _dragging = false;
                CompanionsPlugin.Log.LogDebug(
                    $"[UI] Drag ended â€” final position={rt.anchoredPosition}");
            }
        }

        private void ApplyPanelOffset()
        {
            _userOffset = new Vector2(
                PlayerPrefs.GetFloat(PrefKeyOffsetX, 0f),
                PlayerPrefs.GetFloat(PrefKeyOffsetY, 0f));

            var rt = _root != null ? _root.GetComponent<RectTransform>() : null;
            if (rt != null)
            {
                rt.anchoredPosition = _defaultPosition + _userOffset;
                if (_userOffset.sqrMagnitude > 0.01f)
                    CompanionsPlugin.Log.LogInfo(
                        $"[UI] Applied saved panel offset=({_userOffset.x:F0}, {_userOffset.y:F0}) " +
                        $"â†’ position={rt.anchoredPosition}");
            }
        }

        private void SavePanelOffset()
        {
            var rt = _root != null ? _root.GetComponent<RectTransform>() : null;
            if (rt == null) return;

            _userOffset = rt.anchoredPosition - _defaultPosition;
            PlayerPrefs.SetFloat(PrefKeyOffsetX, _userOffset.x);
            PlayerPrefs.SetFloat(PrefKeyOffsetY, _userOffset.y);
            PlayerPrefs.Save();
            CompanionsPlugin.Log.LogDebug(
                $"[UI] Panel offset saved=({_userOffset.x:F0}, {_userOffset.y:F0})");
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Build UI â€” clone vanilla container panel
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private void BuildUI()
        {
            var gui = InventoryGui.instance;
            if (gui == null || gui.m_container == null)
            {
                CompanionsPlugin.Log.LogError("[UI] BuildUI â€” InventoryGui or m_container is null!");
                return;
            }

            // Deep-clone the vanilla container panel (preserves all serialized refs)
            _root = Instantiate(gui.m_container.gameObject, gui.m_container.parent);
            _root.name = "HC_CompanionInventory";
            _root.SetActive(false);

            // Lower the panel so it sits below the player inventory properly
            var cloneRT = _root.GetComponent<RectTransform>();
            if (cloneRT != null)
            {
                var pos = cloneRT.anchoredPosition;
                pos.y -= 110f;
                cloneRT.anchoredPosition = pos;
                _defaultPosition = pos;
            }

            // Apply saved user offset from F7 repositioning
            ApplyPanelOffset();

            // Find the cloned InventoryGrid
            _grid = _root.GetComponentInChildren<InventoryGrid>();
            if (_grid == null)
            {
                CompanionsPlugin.Log.LogError("[UI] BuildUI â€” No InventoryGrid found in clone!");
                Destroy(_root);
                return;
            }

            // Force grid to rebuild all elements on next UpdateInventory
            // (m_width=0 triggers the rebuild path in InventoryGrid.UpdateGui)
            _gridWidthField?.SetValue(_grid, 0);

            // Wire our callbacks (Action delegates are NOT serialized â€” they're null after clone)
            _grid.m_onSelected  = OnCompanionSelected;
            _grid.m_onRightClick = OnCompanionRightClick;

            // Null out the scrollbar (it references a sibling in the original container)
            _grid.m_scrollbar = null;

            // Keep cloned UIGroupHandler â€” it's needed for gamepad input gating.
            // We inject it into InventoryGui.m_uiGroups[0] when Show() is called
            // so that RB/LB tab switching and D-pad navigation work correctly.
            _uiGroup = _root.GetComponentInChildren<UIGroupHandler>(true);

            // Wire D-pad edge navigation: pressing Up at top of companion grid â†’ player grid
            _grid.OnMoveToUpperInventoryGrid = OnMoveToUpperGrid;

            // Find cloned container name text (child of m_container)
            TMP_Text clonedNameText = FindClonedComponent<TMP_Text>(gui.m_containerName, gui.m_container);

            CompanionsPlugin.Log.LogDebug(
                $"[UI] Clone â€” grid={_grid != null}, name={clonedNameText != null}");

            // Hide vanilla buttons (TakeAll / StackAll / Drop)
            HideClonedComponent(gui.m_takeAllButton, gui.m_container);
            HideClonedComponent(gui.m_stackAllButton, gui.m_container);
            HideClonedComponent(gui.m_dropButton, gui.m_container);

            // Destroy any mod-injected buttons that were cloned along with the
            // container panel (e.g. Quick Stack Store Sort Trash Restock buttons).
            // These are clones of TakeAll parented to its parent â€” they end up as
            // direct children of _root with Button components.
            DestroyClonedModButtons();

            // Hide the cloned scrollbar (not needed for companion inventory)
            var clonedScrollbar = _root.GetComponentInChildren<Scrollbar>(true);
            if (clonedScrollbar != null) clonedScrollbar.gameObject.SetActive(false);

            // Replace container name text with an editable name input field
            if (clonedNameText != null)
                BuildNameInput(clonedNameText);

            // Find the cloned weight text and set up armor display
            BuildWeightAndArmor(gui);

            // Build food slots at the bottom (expand panel height to fit)
            BuildFoodSlots();

            // Build equipment panel on the right side (ExtraSlots mod only)
            BuildEquipmentPanel();

            // Fix any broken TMP fonts in the cloned hierarchy
            TMP_FontAsset font = GetFont();
            if (font != null) ApplyFallbackFont(_root.transform, font);

            // Also fix fonts in the equipment panel
            if (_equipPanel != null && font != null)
                ApplyFallbackFont(_equipPanel.transform, font);

            _built = true;
            _gridCreated = false;  // first UpdateInventory will create elements with null player
            CompanionsPlugin.Log.LogInfo("[UI] BuildUI â€” Cloned container panel successfully");
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Clone navigation helpers
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        /// <summary>
        /// Find a component's equivalent in the cloned hierarchy using its
        /// relative transform path from the original root.
        /// </summary>
        private T FindClonedComponent<T>(Component original, RectTransform originalRoot) where T : Component
        {
            if (original == null || originalRoot == null) return null;
            string path = GetRelativePath(original.transform, originalRoot.transform);
            if (string.IsNullOrEmpty(path)) return _root.GetComponent<T>();
            var found = _root.transform.Find(path);
            return found != null ? found.GetComponent<T>() : null;
        }

        /// <summary>
        /// Hide a component's cloned equivalent by deactivating its GameObject.
        /// </summary>
        private void HideClonedComponent(Component original, RectTransform originalRoot)
        {
            if (original == null || originalRoot == null) return;
            string path = GetRelativePath(original.transform, originalRoot.transform);
            var found = string.IsNullOrEmpty(path)
                ? _root.transform
                : _root.transform.Find(path);
            if (found != null) found.gameObject.SetActive(false);
        }

        /// <summary>
        /// Destroy any mod-injected Button GameObjects that were cloned along with
        /// the vanilla container panel.  We already hid vanilla's TakeAll/StackAll/Drop
        /// (SetActive=false), so any remaining *active* Button that is a direct child
        /// of _root is from an external mod (Quick Stack Store Sort Trash Restock, etc.).
        /// </summary>
        private void DestroyClonedModButtons()
        {
            if (_root == null) return;
            // Collect first, then destroy (can't modify children while iterating)
            var toDestroy = new List<GameObject>();
            foreach (Transform child in _root.transform)
            {
                if (child.GetComponent<Button>() != null && child.gameObject.activeSelf)
                    toDestroy.Add(child.gameObject);
            }
            foreach (var go in toDestroy)
            {
                CompanionsPlugin.Log.LogDebug($"[UI] DestroyClonedModButtons â€” removing \"{go.name}\"");
                Destroy(go);
            }
        }

        /// <summary>
        /// Compute the transform path from a child to a root, suitable for Transform.Find.
        /// </summary>
        private static string GetRelativePath(Transform child, Transform root)
        {
            if (child == root || child == null) return "";
            var parts = new List<string>();
            Transform current = child;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            if (current != root) return "";
            parts.Reverse();
            return string.Join("/", parts);
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Name input â€” replaces the cloned container name text
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private void BuildNameInput(TMP_Text originalNameText)
        {
            originalNameText.gameObject.SetActive(false);

            var origRT = originalNameText.GetComponent<RectTransform>();
            var parent = origRT.parent;
            TMP_FontAsset font = originalNameText.font;
            if (IsBrokenTmpFont(font)) font = GetFont();

            var inputGO = new GameObject("NameInput", typeof(RectTransform), typeof(Image));
            inputGO.transform.SetParent(parent, false);
            var inputRT = inputGO.GetComponent<RectTransform>();
            // Stretch across the top of the panel, centered
            inputRT.anchorMin        = new Vector2(0f, 1f);
            inputRT.anchorMax        = new Vector2(1f, 1f);
            inputRT.pivot            = new Vector2(0.5f, 1f);
            inputRT.sizeDelta        = new Vector2(-20f, 36f);
            inputRT.anchoredPosition = new Vector2(0f, -3f);

            var inputImg = inputGO.GetComponent<Image>();
            var bgTex = TextureLoader.LoadUITexture("SliderBackground");
            if (bgTex != null)
            {
                inputImg.sprite = Sprite.Create(bgTex,
                    new Rect(0f, 0f, bgTex.width, bgTex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                inputImg.color = Color.white;
            }
            else
            {
                inputImg.color = new Color(0.15f, 0.15f, 0.15f, 0.85f);
            }

            var textAreaGO = new GameObject("TextArea", typeof(RectTransform));
            textAreaGO.transform.SetParent(inputGO.transform, false);
            var textAreaRT = textAreaGO.GetComponent<RectTransform>();
            textAreaRT.anchorMin = Vector2.zero;
            textAreaRT.anchorMax = Vector2.one;
            textAreaRT.offsetMin = new Vector2(10f, 2f);
            textAreaRT.offsetMax = new Vector2(-10f, -2f);
            textAreaGO.AddComponent<RectMask2D>();

            var childTextGO = new GameObject("Text", typeof(RectTransform));
            childTextGO.transform.SetParent(textAreaGO.transform, false);
            var childTmp = childTextGO.AddComponent<TextMeshProUGUI>();
            if (font != null) childTmp.font = font;
            childTmp.fontSize     = 18f;
            childTmp.color        = Color.white;
            childTmp.alignment    = TextAlignmentOptions.MidlineLeft;
            childTmp.enableAutoSizing = true;
            childTmp.fontSizeMin  = 12f;
            childTmp.fontSizeMax  = 18f;
            childTmp.raycastTarget = false;
            StretchFill(childTextGO.GetComponent<RectTransform>());

            _nameInput = inputGO.AddComponent<TMP_InputField>();
            _nameInput.textComponent    = childTmp;
            _nameInput.textViewport     = textAreaRT;
            _nameInput.characterLimit   = 24;
            _nameInput.contentType      = TMP_InputField.ContentType.Standard;
            _nameInput.onFocusSelectAll = false;

            var phGO = new GameObject("Placeholder", typeof(RectTransform));
            phGO.transform.SetParent(textAreaGO.transform, false);
            var phTmp = phGO.AddComponent<TextMeshProUGUI>();
            if (font != null) phTmp.font = font;
            phTmp.fontSize     = 18f;
            phTmp.color        = new Color(0.6f, 0.6f, 0.6f, 0.6f);
            phTmp.alignment    = TextAlignmentOptions.MidlineLeft;
            phTmp.text         = ModLocalization.Loc("hc_ui_placeholder_name");
            phTmp.fontStyle    = FontStyles.Italic;
            phTmp.enableAutoSizing = true;
            phTmp.fontSizeMin  = 12f;
            phTmp.fontSizeMax  = 18f;
            phTmp.raycastTarget = false;
            StretchFill(phGO.GetComponent<RectTransform>());

            _nameInput.placeholder = phTmp;
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Food slots â€” appended at the bottom of the panel
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private void BuildFoodSlots()
        {
            _foodSlotIcons  = new Image[FoodSlotCount];
            _foodSlotCounts = new TextMeshProUGUI[FoodSlotCount];

            TMP_FontAsset font = GetFont();
            float slotSize = _grid != null ? _grid.m_elementSpace : 70f;
            float foodGap  = 3f;
            float foodRowW = FoodSlotCount * slotSize + (FoodSlotCount - 1) * foodGap;
            float sectionH = slotSize + 16f;

            // Expand panel to make room
            float foodExpansion = sectionH + 4f;
            var rootRT = _root.GetComponent<RectTransform>();
            if (rootRT != null)
            {
                var sd = rootRT.sizeDelta;
                sd.y += foodExpansion;
                rootRT.sizeDelta = sd;
            }

            // Prevent the InventoryGrid from stretching into the food slot area.
            // Vanilla UpdateGui centers the grid root within its parent RectTransform;
            // without this, the expanded panel height shifts the grid content downward.
            if (_grid != null)
            {
                var gridRT = _grid.transform as RectTransform;
                if (gridRT != null)
                {
                    gridRT.offsetMin = new Vector2(
                        gridRT.offsetMin.x,
                        gridRT.offsetMin.y + foodExpansion);
                }
            }

            _foodSlotsContainer = new GameObject("FoodSlots", typeof(RectTransform));
            _foodSlotsContainer.transform.SetParent(_root.transform, false);
            var containerRT = _foodSlotsContainer.GetComponent<RectTransform>();
            containerRT.anchorMin = new Vector2(0f, 0f);
            containerRT.anchorMax = new Vector2(1f, 0f);
            containerRT.pivot     = new Vector2(0.5f, 0f);
            containerRT.sizeDelta = new Vector2(0f, sectionH);
            containerRT.anchoredPosition = new Vector2(0f, 4f);

            // "Food" label
            var labelGO = new GameObject("FoodLabel", typeof(RectTransform));
            labelGO.transform.SetParent(_foodSlotsContainer.transform, false);
            var labelTmp = labelGO.AddComponent<TextMeshProUGUI>();
            if (font != null) labelTmp.font = font;
            labelTmp.text         = ModLocalization.Loc("hc_ui_label_food");
            labelTmp.fontSize     = 11f;
            labelTmp.color        = new Color(1f, 0.9f, 0.5f, 1f);
            labelTmp.alignment    = TextAlignmentOptions.Center;
            labelTmp.fontStyle    = FontStyles.Bold;
            labelTmp.raycastTarget = false;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = new Vector2(0.5f, 1f);
            labelRT.anchorMax = new Vector2(0.5f, 1f);
            labelRT.pivot     = new Vector2(0.5f, 1f);
            labelRT.sizeDelta = new Vector2(foodRowW, 14f);
            labelRT.anchoredPosition = Vector2.zero;

            // Slot row
            var foodRow = new GameObject("FoodRow", typeof(RectTransform));
            foodRow.transform.SetParent(_foodSlotsContainer.transform, false);
            var foodRowRT = foodRow.GetComponent<RectTransform>();
            foodRowRT.anchorMin = new Vector2(0.5f, 0f);
            foodRowRT.anchorMax = new Vector2(0.5f, 0f);
            foodRowRT.pivot     = new Vector2(0.5f, 0f);
            foodRowRT.sizeDelta = new Vector2(foodRowW, slotSize);
            foodRowRT.anchoredPosition = Vector2.zero;

            for (int i = 0; i < FoodSlotCount; i++)
            {
                float x = i * (slotSize + foodGap);
                var slotGO = new GameObject($"FoodSlot{i}", typeof(RectTransform), typeof(Image));
                slotGO.transform.SetParent(foodRow.transform, false);
                var slotRT = slotGO.GetComponent<RectTransform>();
                slotRT.anchorMin = new Vector2(0f, 0f);
                slotRT.anchorMax = new Vector2(0f, 0f);
                slotRT.pivot     = new Vector2(0f, 0f);
                slotRT.sizeDelta = new Vector2(slotSize, slotSize);
                slotRT.anchoredPosition = new Vector2(x, 0f);
                slotGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.5625f);
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
                _foodSlotIcons[i].raycastTarget  = false;
                _foodSlotIcons[i].enabled        = false;

                var countGO = new GameObject("Count", typeof(RectTransform));
                countGO.transform.SetParent(slotGO.transform, false);
                var countTmp = countGO.AddComponent<TextMeshProUGUI>();
                if (font != null) countTmp.font = font;
                countTmp.fontSize         = 9f;
                countTmp.color            = Color.white;
                countTmp.alignment        = TextAlignmentOptions.BottomRight;
                countTmp.textWrappingMode = TextWrappingModes.NoWrap;
                countTmp.overflowMode     = TextOverflowModes.Overflow;
                countTmp.raycastTarget    = false;
                var countRT = countGO.GetComponent<RectTransform>();
                countRT.anchorMin = Vector2.zero;
                countRT.anchorMax = Vector2.one;
                countRT.offsetMin = new Vector2(2f, 1f);
                countRT.offsetMax = new Vector2(-2f, -1f);
                _foodSlotCounts[i] = countTmp;
                _foodSlotCounts[i].enabled = false;
            }
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Equipment panel â€” ExtraSlots mod only, side panel replicating
        //  the ExtraSlots equipment layout (3-row columns on the right)
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private const float EquipTileSize = 70f;
        private const float EquipTileGap  = 6f;
        private const float EquipPadding  = 10f;
        private const float EquipPanelGap = 100f;

        private static readonly string[] LegacyEquipSlotIds =
        {
            "Helmet", "Chest", "Legs", "Shoulder", "Utility", "Trinket"
        };

        private void BuildEquipmentPanel()
        {
            if (!ExtraSlotsCompat.IsLoaded) return;

            ExtraSlotsCompat.RefreshSlotsMetadata();
            var slotDefs = ExtraSlotsCompat.GetActiveSlots();
            if (slotDefs == null || slotDefs.Count == 0)
            {
                BuildLegacyEquipmentPanel();
                return;
            }

            _equipSlotCount = slotDefs.Count;
            _equipSlotIcons = new Image[_equipSlotCount];
            _equipSlotIds = new string[_equipSlotCount];
            _equipTileSize = GetEffectiveEquipTileSize();

            TMP_FontAsset font = GetFont();

            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minY = float.MaxValue;
            float maxY = float.MinValue;
            for (int i = 0; i < slotDefs.Count; i++)
            {
                var p = slotDefs[i].Position;
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }

            float panelW = (maxX - minX) + _equipTileSize + EquipPadding * 2f;
            float panelH = (maxY - minY) + _equipTileSize + EquipPadding * 2f;

            _equipPanel = new GameObject("HC_EquipPanel", typeof(RectTransform));
            _equipPanel.transform.SetParent(_root.transform, false);

            var panelRT = _equipPanel.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(1f, 1f);
            panelRT.anchorMax = new Vector2(1f, 1f);
            panelRT.pivot = new Vector2(0f, 1f);
            panelRT.sizeDelta = new Vector2(panelW, panelH);
            panelRT.anchoredPosition = new Vector2(
                EquipPanelGap + ExtraSlotsCompat.EquipmentPanelOffset.x,
                -ExtraSlotsCompat.EquipmentPanelOffset.y);

            BuildEquipmentPanelBackground();

            for (int i = 0; i < _equipSlotCount; i++)
            {
                var slot = slotDefs[i];
                float x = EquipPadding + (slot.Position.x - minX);
                float y = -EquipPadding - (maxY - slot.Position.y);
                string label = string.IsNullOrEmpty(slot.Name) ? slot.Id : slot.Name;
                CreateEquipSlot(i, x, y, label, slot.Id, font);
            }
        }

        private void BuildLegacyEquipmentPanel()
        {
            ExtraSlotsCompat.RefreshSlotCount();
            int extraCount = ExtraSlotsCompat.ExtraUtilitySlotCount;
            _equipSlotCount = LegacyEquipSlotIds.Length + extraCount;
            _equipSlotIcons = new Image[_equipSlotCount];
            _equipSlotIds = new string[_equipSlotCount];
            _equipTileSize = GetEffectiveEquipTileSize();

            TMP_FontAsset font = GetFont();

            int cols = 2 + (extraCount > 0 ? (int)Math.Ceiling(extraCount / 3.0) : 0);
            int rows = 3;
            float panelW = cols * _equipTileSize + (cols - 1) * EquipTileGap + EquipPadding * 2f;
            float panelH = rows * _equipTileSize + (rows - 1) * EquipTileGap + EquipPadding * 2f;

            _equipPanel = new GameObject("HC_EquipPanel", typeof(RectTransform));
            _equipPanel.transform.SetParent(_root.transform, false);

            var panelRT = _equipPanel.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(1f, 1f);
            panelRT.anchorMax = new Vector2(1f, 1f);
            panelRT.pivot = new Vector2(0f, 1f);
            panelRT.sizeDelta = new Vector2(panelW, panelH);
            panelRT.anchoredPosition = new Vector2(
                EquipPanelGap + ExtraSlotsCompat.EquipmentPanelOffset.x,
                -ExtraSlotsCompat.EquipmentPanelOffset.y);

            BuildEquipmentPanelBackground();

            for (int i = 0; i < _equipSlotCount; i++)
            {
                int col;
                int row;
                if (i < LegacyEquipSlotIds.Length)
                {
                    col = i / 3;
                    row = i % 3;
                }
                else
                {
                    int extraIdx = i - LegacyEquipSlotIds.Length;
                    col = 2 + extraIdx / 3;
                    row = extraIdx % 3;
                }

                float x = EquipPadding + col * (_equipTileSize + EquipTileGap);
                float y = -EquipPadding - row * (_equipTileSize + EquipTileGap);
                string slotId = i < LegacyEquipSlotIds.Length
                    ? LegacyEquipSlotIds[i]
                    : $"ExtraUtility{i - LegacyEquipSlotIds.Length + 1}";
                string label = i < LegacyEquipSlotIds.Length
                    ? LegacyEquipSlotIds[i]
                    : $"Utility {i - LegacyEquipSlotIds.Length + 2}";

                CreateEquipSlot(i, x, y, label, slotId, font);
            }
        }

        private void BuildEquipmentPanelBackground()
        {
            var gui = InventoryGui.instance;
            if (gui != null && gui.m_player != null)
            {
                var darken = gui.m_player.Find("Darken");
                var bkg = gui.m_player.Find("Bkg");

                if (darken != null)
                {
                    var darkenClone = Instantiate(darken.gameObject, _equipPanel.transform);
                    darkenClone.name = "Darken";
                    var dkRT = darkenClone.GetComponent<RectTransform>();
                    dkRT.anchorMin = Vector2.zero;
                    dkRT.anchorMax = Vector2.one;
                    dkRT.offsetMin = Vector2.zero;
                    dkRT.offsetMax = Vector2.zero;
                    darkenClone.transform.SetAsFirstSibling();
                }

                if (bkg != null)
                {
                    var bkgClone = Instantiate(bkg.gameObject, _equipPanel.transform);
                    bkgClone.name = "Bkg";
                    var bgRT = bkgClone.GetComponent<RectTransform>();
                    bgRT.anchorMin = Vector2.zero;
                    bgRT.anchorMax = Vector2.one;
                    bgRT.offsetMin = Vector2.zero;
                    bgRT.offsetMax = Vector2.zero;
                    bkgClone.transform.SetSiblingIndex(1);
                }
            }

            if (_equipPanel.transform.childCount == 0)
            {
                var fallbackBg = _equipPanel.AddComponent<Image>();
                fallbackBg.color = new Color(0f, 0f, 0f, 0.85f);
                fallbackBg.raycastTarget = false;
            }
        }

        private float GetEffectiveEquipTileSize()
        {
            var gui = InventoryGui.instance;
            if (gui != null && gui.m_playerGrid != null &&
                gui.m_playerGrid.m_gridRoot != null &&
                gui.m_playerGrid.m_gridRoot.childCount > 0)
            {
                var firstChild = gui.m_playerGrid.m_gridRoot.GetChild(0);
                var rt = firstChild != null ? firstChild.GetComponent<RectTransform>() : null;
                if (rt != null)
                {
                    float size = rt.rect.width;
                    if (size <= 0f) size = rt.sizeDelta.x;
                    if (size > 0f) return size;
                }
            }

            // ExtraSlots uses 70 spacing; vanilla slot visuals are typically ~64px.
            return 64f;
        }

        private static void ApplyEquipSlotBackgroundStyle(Image slotImg)
        {
            var gui = InventoryGui.instance;
            if (gui != null && gui.m_playerGrid != null &&
                gui.m_playerGrid.m_gridRoot != null &&
                gui.m_playerGrid.m_gridRoot.childCount > 0)
            {
                var firstChild = gui.m_playerGrid.m_gridRoot.GetChild(0);
                var src = firstChild != null ? firstChild.GetComponent<Image>() : null;
                if (src != null)
                {
                    slotImg.sprite = src.sprite;
                    slotImg.type = src.type;
                    slotImg.preserveAspect = src.preserveAspect;
                    slotImg.color = src.color;
                    return;
                }
            }

            slotImg.color = new Color(0f, 0f, 0f, 0.5625f);
        }

        private void CreateEquipSlot(int index, float x, float y,
            string label, string slotId, TMP_FontAsset font)
        {
            // Slot container
            var slotGO = new GameObject($"EquipSlot_{label}", typeof(RectTransform), typeof(Image));
            slotGO.transform.SetParent(_equipPanel.transform, false);

            var slotRT = slotGO.GetComponent<RectTransform>();
            slotRT.anchorMin = new Vector2(0f, 1f);
            slotRT.anchorMax = new Vector2(0f, 1f);
            slotRT.pivot     = new Vector2(0f, 1f);
            slotRT.sizeDelta = new Vector2(_equipTileSize, _equipTileSize);
            slotRT.anchoredPosition = new Vector2(x, y);

            var slotImg = slotGO.GetComponent<Image>();
            ApplyEquipSlotBackgroundStyle(slotImg);
            slotImg.raycastTarget = false;

            // Item icon â€” centered with padding
            var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGO.transform.SetParent(slotGO.transform, false);
            var iconRT = iconGO.GetComponent<RectTransform>();
            float labelH = Mathf.Clamp(_equipTileSize * 0.18f, 9f, 14f);
            iconRT.anchorMin = Vector2.zero;
            iconRT.anchorMax = Vector2.one;
            iconRT.offsetMin = new Vector2(4f, labelH + 1f);
            iconRT.offsetMax = new Vector2(-4f, -4f);
            _equipSlotIcons[index] = iconGO.GetComponent<Image>();
            if (_equipSlotIds != null && index >= 0 && index < _equipSlotIds.Length)
                _equipSlotIds[index] = slotId;
            _equipSlotIcons[index].preserveAspect = true;
            _equipSlotIcons[index].raycastTarget  = false;
            _equipSlotIcons[index].enabled        = false;

            // Slot label at bottom
            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(slotGO.transform, false);
            var labelTmp = labelGO.AddComponent<TextMeshProUGUI>();
            if (font != null) labelTmp.font = font;
            labelTmp.text          = label;
            labelTmp.fontSize      = 9f;
            labelTmp.color         = new Color(0.8f, 0.8f, 0.6f, 0.8f);
            labelTmp.alignment     = TextAlignmentOptions.Bottom;
            labelTmp.fontStyle     = FontStyles.Normal;
            labelTmp.raycastTarget = false;
            labelTmp.enableAutoSizing = true;
            labelTmp.fontSizeMin   = 6f;
            labelTmp.fontSizeMax   = 9f;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = new Vector2(0f, 0f);
            labelRT.anchorMax = new Vector2(1f, 0f);
            labelRT.pivot     = new Vector2(0.5f, 0f);
            labelRT.sizeDelta = new Vector2(0f, labelH);
            labelRT.anchoredPosition = new Vector2(0f, 1f);
        }

        private void RefreshEquipmentPanel()
        {
            if (_equipSlotIcons == null || _equipSlotIds == null || _companion == null) return;

            int count = Mathf.Min(_equipSlotIcons.Length, _equipSlotIds.Length);
            for (int i = 0; i < count; i++)
                SetEquipIcon(i, _equipSlotIds[i], _companion.GetItemForExtraSlotsSlot(_equipSlotIds[i]));
        }

        private void SetEquipIcon(int index, string slotId, ItemDrop.ItemData item)
        {
            if (index < 0 || index >= _equipSlotIcons.Length) return;
            var icon = _equipSlotIcons[index];
            if (item != null)
            {
                icon.sprite  = item.GetIcon();
                icon.color   = Color.white;
                icon.enabled = true;
            }
            else
            {
                icon.sprite = ExtraSlotsCompat.GetSlotHintSprite(slotId);
                icon.color = new Color(0.6f, 0.6f, 0.6f, 0.9f);
                icon.enabled = icon.sprite != null;
            }
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Weight & armor display
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private void BuildWeightAndArmor(InventoryGui gui)
        {
            // â”€â”€ Weight text â”€â”€
            // m_containerWeight is inside m_container and was cloned â€” find it
            _weightText = FindClonedComponent<TMP_Text>(gui.m_containerWeight, gui.m_container);

            if (_weightText != null)
            {
                // Match player weight text size â€” the container weight is smaller by default
                if (gui.m_weight != null)
                {
                    _weightText.fontSize = gui.m_weight.fontSize;
                    _weightText.fontSizeMin = gui.m_weight.fontSizeMin;
                    _weightText.fontSizeMax = gui.m_weight.fontSizeMax;
                    _weightText.enableAutoSizing = gui.m_weight.enableAutoSizing;
                }

                CompanionsPlugin.Log.LogDebug(
                    $"[UI] Found cloned weight text: \"{_weightText.name}\" fontSize={_weightText.fontSize}");
            }
            else
            {
                CompanionsPlugin.Log.LogWarning(
                    "[UI] Could not find cloned m_containerWeight â€” weight display unavailable");
            }

            // â”€â”€ Armor display â”€â”€
            // m_armor is on m_infoPanel (NOT inside m_container), so it wasn't cloned.
            // Create a new armor text+icon next to the weight display.
            BuildArmorDisplay(gui);
        }

        private void BuildArmorDisplay(InventoryGui gui)
        {
            if (gui.m_armor == null)
            {
                CompanionsPlugin.Log.LogWarning("[UI] gui.m_armor is null â€” cannot clone armor display");
                return;
            }

            if (_weightText == null)
            {
                CompanionsPlugin.Log.LogWarning("[UI] Weight text not found â€” cannot position armor display");
                return;
            }

            // Clone the vanilla armor group (parent contains shield icon + text)
            Transform armorSourceParent = gui.m_armor.transform.parent;
            if (armorSourceParent == null)
            {
                CompanionsPlugin.Log.LogWarning("[UI] m_armor has no parent â€” cannot clone armor display");
                return;
            }

            // Parent to the same container as the weight text so coordinates align.
            // Previously this was parented to _root which is a different coordinate
            // space, causing the armor display to appear in the middle of the grid.
            var armorClone = Instantiate(armorSourceParent.gameObject, _weightText.transform.parent);
            armorClone.name = "HC_ArmorDisplay";
            armorClone.SetActive(true);

            _armorText = armorClone.GetComponentInChildren<TMP_Text>(true);

            // Position directly above the weight text
            var armorRT = armorClone.GetComponent<RectTransform>();
            var weightRT = _weightText.GetComponent<RectTransform>();
            armorRT.anchorMin = weightRT.anchorMin;
            armorRT.anchorMax = weightRT.anchorMax;
            armorRT.pivot = weightRT.pivot;
            armorRT.anchoredPosition = weightRT.anchoredPosition + new Vector2(0f, 150f);

            CompanionsPlugin.Log.LogDebug(
                $"[UI] Cloned armor display â€” text={_armorText != null} " +
                $"parent=\"{_weightText.transform.parent.name}\" pos={armorRT.anchoredPosition}");
        }

        private void UpdateWeightAndArmor()
        {
            if (_companion == null) return;

            // â”€â”€ Weight â”€â”€
            if (_weightText != null && _companionHumanoid != null)
            {
                var inv = _companionHumanoid.GetInventory();
                if (inv != null)
                {
                    int current = Mathf.CeilToInt(inv.GetTotalWeight());
                    int max = Mathf.CeilToInt(CompanionTierData.MaxCarryWeight);

                    if (current > max)
                    {
                        _weightText.text = (Mathf.Sin(Time.time * 10f) > 0f)
                            ? $"<color=red>{current}</color>/{max}"
                            : $"{current}/{max}";
                    }
                    else
                    {
                        _weightText.text = $"{current}/{max}";
                    }
                }
            }

            // â”€â”€ Armor â”€â”€
            if (_armorText != null)
            {
                float armor = _companion.GetTotalArmor();
                _armorText.text = Mathf.FloorToInt(armor).ToString();
            }
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Grid callbacks
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private void OnCompanionSelected(InventoryGrid grid, ItemDrop.ItemData item,
            Vector2i pos, InventoryGrid.Modifier mod)
        {
            if (InventoryGui.instance == null) return;
            var inv = GetStorageInventory();
            if (inv == null) return;

            var dragItem = _dragItemField?.GetValue(InventoryGui.instance) as ItemDrop.ItemData;
            var dragInv  = _dragInventoryField?.GetValue(InventoryGui.instance) as Inventory;
            int dragAmt  = (_dragAmountField != null)
                ? (int)_dragAmountField.GetValue(InventoryGui.instance) : 1;

            // â”€â”€ Currently dragging an item â”€â”€
            if (dragItem != null && dragInv != null)
            {
                try
                {
                    if (!dragInv.ContainsItem(dragItem))
                    { ClearDrag(); return; }
                }
                catch (NullReferenceException) { ClearDrag(); return; }

                // Unequip the target slot's item on the companion
                var target = inv.GetItemAt(pos.x, pos.y);
                if (target != null && _companionHumanoid != null
                    && _companionHumanoid.IsItemEquiped(target))
                    _companionHumanoid.UnequipItem(target, false);

                // Unequip the drag item from the player if applicable
                var player = Player.m_localPlayer;
                if (player != null && dragInv == player.GetInventory()
                    && player.IsItemEquiped(dragItem))
                {
                    player.RemoveEquipAction(dragItem);
                    player.UnequipItem(dragItem, false);
                }

                // Use InventoryGrid.DropItem â€” handles swap & stack merge natively
                if (_grid.DropItem(dragInv, dragItem, dragAmt, pos))
                {
                    ClearDrag();
                    OnCompanionInventoryMutated();
                }
                return;
            }

            // â”€â”€ Not dragging â€” modifier-based actions â”€â”€
            if (item == null) return;

            switch (mod)
            {
                case InventoryGrid.Modifier.Move:
                {
                    // Ctrl+Click: move item from companion â†’ player
                    var playerInv = Player.m_localPlayer?.GetInventory();
                    if (playerInv != null)
                    {
                        if (_companionHumanoid != null
                            && _companionHumanoid.IsItemEquiped(item))
                            _companionHumanoid.UnequipItem(item, false);

                        playerInv.MoveItemToThis(inv, item, item.m_stack,
                            item.m_gridPos.x, item.m_gridPos.y);
                        OnCompanionInventoryMutated();
                    }
                    break;
                }

                case InventoryGrid.Modifier.Drop:
                    // Ctrl+Shift or gamepad: drop item on ground from companion
                    Player.m_localPlayer?.DropItem(inv, item, item.m_stack);
                    OnCompanionInventoryMutated();
                    break;

                case InventoryGrid.Modifier.Split:
                    // Shift+Click: show split dialog to pick up partial stack
                    if (item.m_stack > 1)
                    {
                        _showSplitDialog?.Invoke(InventoryGui.instance,
                            new object[] { item, inv });
                        return; // Don't pick up full stack
                    }
                    goto default; // Single item: pick up normally

                default:
                    // Select: pick up item from companion grid
                    _setupDragItem?.Invoke(InventoryGui.instance,
                        new object[] { item, inv, item.m_stack });
                    break;
            }
        }

        private void OnCompanionRightClick(InventoryGrid grid, ItemDrop.ItemData item, Vector2i pos)
        {
            if (item == null) return;
            var inv = GetStorageInventory();
            if (inv == null) return;

            bool changed = false;

            // Food consumption first
            if (IsConsumableItem(item) && _companionFood != null)
                changed = _companionFood.TryConsumeItem(item);

            // Fall back to use (equip/unequip toggle)
            if (!changed && _companionHumanoid != null)
            {
                bool hadItem = inv.ContainsItem(item);
                int stackBefore = hadItem ? item.m_stack : 0;
                bool equippedBefore = _companionHumanoid.IsItemEquiped(item);

                _companionHumanoid.UseItem(inv, item, true);

                bool hasAfter = inv.ContainsItem(item);
                int stackAfter = hasAfter ? item.m_stack : 0;
                bool equippedAfter = hasAfter && _companionHumanoid.IsItemEquiped(item);
                changed = hadItem != hasAfter ||
                          stackBefore != stackAfter ||
                          equippedBefore != equippedAfter;
            }

            if (changed)
                OnCompanionInventoryMutated();
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Gamepad UIGroupHandler injection
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        /// <summary>
        /// Inject our companion grid's UIGroupHandler into InventoryGui.m_uiGroups[0]
        /// and swap m_containerGrid so that vanilla's tab switching (RB/LB) and
        /// D-pad edge navigation work correctly with our companion grid.
        /// </summary>
        private void InjectGamepadGroups()
        {
            var gui = InventoryGui.instance;
            if (gui == null || _uiGroup == null) return;

            var groups = _uiGroupsField?.GetValue(gui) as UIGroupHandler[];
            if (groups == null || groups.Length == 0) return;

            // Save originals
            _savedVanillaGroup = groups[0];
            _savedContainerGrid = _containerGridField?.GetValue(gui) as InventoryGrid;

            // Inject our companion UIGroupHandler and grid
            groups[0] = _uiGroup;
            _containerGridField?.SetValue(gui, _grid);

            // Reset grid selection to top-left so switching from player grid
            // starts at row 0 instead of a stale position from a previous session
            _grid.SetSelection(new Vector2i(0, 0));

            // Activate our companion group (index 0) by default
            _setActiveGroupMethod?.Invoke(gui, new object[] { 0, false });

            _gamepadInjected = true;
            CompanionsPlugin.Log.LogDebug(
                $"[UI] Gamepad groups injected â€” companion UIGroup into m_uiGroups[0]");
        }

        /// <summary>
        /// Restore the original vanilla UIGroupHandler and container grid.
        /// </summary>
        private void RestoreGamepadGroups()
        {
            if (!_gamepadInjected) return;
            _gamepadInjected = false;

            var gui = InventoryGui.instance;
            if (gui == null) return;

            var groups = _uiGroupsField?.GetValue(gui) as UIGroupHandler[];
            if (groups != null && groups.Length > 0 && _savedVanillaGroup != null)
                groups[0] = _savedVanillaGroup;

            if (_savedContainerGrid != null)
                _containerGridField?.SetValue(gui, _savedContainerGrid);

            // Reactivate the player group (index 1)
            _setActiveGroupMethod?.Invoke(gui, new object[] { 1, false });

            _savedVanillaGroup = null;
            _savedContainerGrid = null;
            CompanionsPlugin.Log.LogDebug("[UI] Gamepad groups restored to vanilla");
        }

        /// <summary>
        /// D-pad Up at top of companion grid â†’ switch focus to player grid.
        /// Mirrors vanilla InventoryGui.MoveToUpperInventoryGrid behavior.
        /// </summary>
        private void OnMoveToUpperGrid(Vector2i previousGridPosition)
        {
            var gui = InventoryGui.instance;
            if (gui == null) return;

            // Map position from companion grid width to player grid width
            int playerWidth = _gridWidthField != null && gui.m_playerGrid != null
                ? (int)_gridWidthField.GetValue(gui.m_playerGrid) : 8;
            int companionWidth = _gridWidthField != null && _grid != null
                ? (int)_gridWidthField.GetValue(_grid) : 4;
            int offset = (int)Math.Ceiling((playerWidth - companionWidth) / 2f);

            // Read current player grid selection via reflection
            var sel = _gridSelectedField != null && gui.m_playerGrid != null
                ? (Vector2i)_gridSelectedField.GetValue(gui.m_playerGrid)
                : new Vector2i(0, 0);
            int x = Mathf.Max(0, previousGridPosition.x + offset);
            sel.x = Mathf.Max(x, Mathf.Min(playerWidth - 1, previousGridPosition.x));
            gui.m_playerGrid.SetSelection(sel);

            // Switch active group to player (index 1)
            _setActiveGroupMethod?.Invoke(gui, new object[] { 1, true });
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Grid update â€” called every frame while visible
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private void UpdateGrid()
        {
            if (_grid == null) return;
            var inv = GetStorageInventory();
            if (inv == null) return;

            var dragItem = _dragItemField?.GetValue(InventoryGui.instance) as ItemDrop.ItemData;

            // First call after build: pass null player so grid element creation
            // doesn't add hotbar binding numbers (row 0 only).
            // Subsequent calls: pass Player.m_localPlayer so vanilla natively
            // handles equipped indicators via item.m_equipped (set by Humanoid.EquipItem).
            Player gridPlayer = _gridCreated ? Player.m_localPlayer : null;
            _grid.UpdateInventory(inv, gridPlayer, dragItem);
            _gridCreated = true;

            UpdateWeightAndArmor();
            RefreshFoodSlots(inv);
        }

        /// <summary>
        /// Vanilla's UpdateContainer calls m_containerGrid.UpdateInventory(inv, null, dragItem)
        /// every frame. The null player parameter disables all equipped/queued indicators.
        /// This method runs in LateUpdate (after vanilla) to re-enable them based on
        /// the actual item.m_equipped state set by the companion's Humanoid.
        /// </summary>
        private void FixEquippedIndicators()
        {
            if (_grid == null || _gridElementsField == null) return;

            var elements = _gridElementsField.GetValue(_grid) as System.Collections.IList;
            if (elements == null || elements.Count == 0) return;

            var inv = GetStorageInventory();
            if (inv == null) return;

            if (_elemPosField == null || _elemEquipedField == null) return;

            foreach (var elem in elements)
            {
                var pos = (Vector2i)_elemPosField.GetValue(elem);
                var equipImg = _elemEquipedField.GetValue(elem) as Image;
                if (equipImg == null) continue;

                var item = inv.GetItemAt(pos.x, pos.y);
                equipImg.enabled = item != null && item.m_equipped;

                // Also fix queued indicator
                if (_elemQueuedField != null)
                {
                    var queuedImg = _elemQueuedField.GetValue(elem) as Image;
                    if (queuedImg != null)
                        queuedImg.enabled = false; // companions don't queue equips through player
                }
            }
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Food slot refresh
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private void RefreshFoodSlots(Inventory inv)
        {
            if (_foodSlotIcons == null || _foodSlotCounts == null) return;
            if (_companionFood == null) return;

            for (int i = 0; i < FoodSlotCount; i++)
            {
                _foodSlotIcons[i].enabled  = false;
                _foodSlotCounts[i].enabled = false;

                var food = _companionFood.GetFood(i);
                if (!food.IsActive) continue;

                Sprite icon = ResolveFoodIcon(food, inv);
                if (icon != null)
                {
                    _foodSlotIcons[i].sprite  = icon;
                    _foodSlotIcons[i].enabled = true;
                }

                _foodSlotCounts[i].text = FormatFoodDuration(food.RemainingTime);
                _foodSlotCounts[i].color = food.RemainingTime >= 60f
                    ? Color.white
                    : new Color(1f, 1f, 1f,
                        Mathf.Clamp01(0.4f + Mathf.Sin(Time.time * 10f) * 0.6f));
                _foodSlotCounts[i].enabled = true;
            }
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

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Name / ownership / inventory
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

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

        private void OnCompanionInventoryMutated()
        {
            _companion?.SyncEquipmentToInventory();
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Item interaction helpers
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private static bool IsConsumableItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) return false;
            return item.m_shared.m_food > 0f ||
                   item.m_shared.m_foodStamina > 0f ||
                   item.m_shared.m_foodEitr > 0f;
        }

        private void ClearDrag()
        {
            if (InventoryGui.instance == null) return;
            _setupDragItem?.Invoke(InventoryGui.instance,
                new object[] { null, null, 1 });
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Teardown
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private void Teardown()
        {
            RestoreGamepadGroups();
            if (_root != null) { Destroy(_root); _root = null; }
            _grid               = null;
            _uiGroup            = null;
            _nameInput          = null;
            _foodSlotIcons      = null;
            _foodSlotCounts     = null;
            _foodSlotsContainer = null;
            _equipSlotIcons     = null;
            _equipSlotIds       = null;
            _equipPanel         = null;
            _weightText         = null;
            _armorText          = null;
            _canvasGroup        = null;
            _dragMode           = false;
            _dragging           = false;
            _built              = false;
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Font helpers
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

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

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Helpers
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        private static void StretchFill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•
        //  Bootstrap
        // â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•â•

        internal static void EnsureInstance()
        {
            if (Instance != null) return;
            var go = new GameObject("HC_CompanionInteractPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<CompanionInteractPanel>();
        }
    }
}

