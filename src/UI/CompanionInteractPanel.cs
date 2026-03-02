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
    /// Companion inventory panel — clones the vanilla container UI from InventoryGui
    /// for authentic Valheim inventory visuals.  Positioned below the player inventory
    /// like a normal container.  Shows name input, weight display, inventory grid,
    /// and food slots.
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

        // ── InventoryGui drag system reflection ──────────────────────────────
        private static readonly FieldInfo _dragItemField      = AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo _dragInventoryField  = AccessTools.Field(typeof(InventoryGui), "m_dragInventory");
        private static readonly FieldInfo _dragAmountField     = AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
        private static readonly MethodInfo _setupDragItem      = AccessTools.Method(typeof(InventoryGui), "SetupDragItem",
            new[] { typeof(ItemDrop.ItemData), typeof(Inventory), typeof(int) });

        // ── InventoryGrid force-rebuild reflection ───────────────────────────
        private static readonly FieldInfo _gridWidthField =
            AccessTools.Field(typeof(InventoryGrid), "m_width");

        // ── Split dialog reflection ────────────────────────────────────────
        private static readonly MethodInfo _showSplitDialog = AccessTools.Method(
            typeof(InventoryGui), "ShowSplitDialog",
            new[] { typeof(ItemDrop.ItemData), typeof(Inventory) });

        // ── Constants ────────────────────────────────────────────────────────
        private const int FoodSlotCount = 3;

        // ── Companion references ─────────────────────────────────────────────
        private CompanionSetup   _companion;
        private Character        _companionChar;
        private CompanionFood    _companionFood;
        private Humanoid         _companionHumanoid;
        private Container        _companionContainer;
        private ZNetView         _companionNview;

        // ── UI elements ──────────────────────────────────────────────────────
        private GameObject      _root;
        private InventoryGrid   _grid;
        private TMP_InputField  _nameInput;
        private GameObject      _foodSlotsContainer;
        private Image[]           _foodSlotIcons;
        private TextMeshProUGUI[] _foodSlotCounts;

        // Weight & armor display (on the cloned container panel)
        private TMP_Text _weightText;
        private TMP_Text _armorText;

        private bool _built;
        private bool _visible;
        private bool _builtForDverger;
        private bool _gridCreated;  // true after first UpdateInventory (grid elements exist)

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

            UpdateGrid();
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

            // Rebuild if destroyed, never built, or companion type changed
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

            // Show/hide food slots based on companion type
            if (_foodSlotsContainer != null)
                _foodSlotsContainer.SetActive(!_builtForDverger);

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

            _companion          = null;
            _companionChar      = null;
            _companionFood      = null;
            _companionHumanoid  = null;
            _companionContainer = null;
            _companionNview     = null;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Build UI — clone vanilla container panel
        // ══════════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            var gui = InventoryGui.instance;
            if (gui == null || gui.m_container == null)
            {
                CompanionsPlugin.Log.LogError("[UI] BuildUI — InventoryGui or m_container is null!");
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
            }

            // Find the cloned InventoryGrid
            _grid = _root.GetComponentInChildren<InventoryGrid>();
            if (_grid == null)
            {
                CompanionsPlugin.Log.LogError("[UI] BuildUI — No InventoryGrid found in clone!");
                Destroy(_root);
                return;
            }

            // Force grid to rebuild all elements on next UpdateInventory
            // (m_width=0 triggers the rebuild path in InventoryGrid.UpdateGui)
            _gridWidthField?.SetValue(_grid, 0);

            // Wire our callbacks (Action delegates are NOT serialized — they're null after clone)
            _grid.m_onSelected  = OnCompanionSelected;
            _grid.m_onRightClick = OnCompanionRightClick;

            // Null out the scrollbar (it references a sibling in the original container)
            _grid.m_scrollbar = null;

            // Disable cloned UIGroupHandler to avoid gamepad navigation conflicts
            var uiGroup = _root.GetComponentInChildren<UIGroupHandler>(true);
            if (uiGroup != null) uiGroup.enabled = false;

            // Find cloned container name text (child of m_container)
            TMP_Text clonedNameText = FindClonedComponent<TMP_Text>(gui.m_containerName, gui.m_container);

            CompanionsPlugin.Log.LogDebug(
                $"[UI] Clone — grid={_grid != null}, name={clonedNameText != null}");

            // Hide vanilla buttons (TakeAll / StackAll / Drop)
            HideClonedComponent(gui.m_takeAllButton, gui.m_container);
            HideClonedComponent(gui.m_stackAllButton, gui.m_container);
            HideClonedComponent(gui.m_dropButton, gui.m_container);

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

            // Fix any broken TMP fonts in the cloned hierarchy
            TMP_FontAsset font = GetFont();
            if (font != null) ApplyFallbackFont(_root.transform, font);

            _built = true;
            _gridCreated = false;  // first UpdateInventory will create elements with null player
            CompanionsPlugin.Log.LogInfo("[UI] BuildUI — Cloned container panel successfully");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Clone navigation helpers
        // ══════════════════════════════════════════════════════════════════════

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

        // ══════════════════════════════════════════════════════════════════════
        //  Name input — replaces the cloned container name text
        // ══════════════════════════════════════════════════════════════════════

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

        // ══════════════════════════════════════════════════════════════════════
        //  Food slots — appended at the bottom of the panel
        // ══════════════════════════════════════════════════════════════════════

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
            var rootRT = _root.GetComponent<RectTransform>();
            if (rootRT != null)
            {
                var sd = rootRT.sizeDelta;
                sd.y += sectionH + 4f;
                rootRT.sizeDelta = sd;
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

        // ══════════════════════════════════════════════════════════════════════
        //  Weight & armor display
        // ══════════════════════════════════════════════════════════════════════

        private void BuildWeightAndArmor(InventoryGui gui)
        {
            // ── Weight text ──
            // m_containerWeight is inside m_container and was cloned — find it
            _weightText = FindClonedComponent<TMP_Text>(gui.m_containerWeight, gui.m_container);

            if (_weightText != null)
            {
                // Match player weight text size — the container weight is smaller by default
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
                    "[UI] Could not find cloned m_containerWeight — weight display unavailable");
            }

            // ── Armor display ──
            // m_armor is on m_infoPanel (NOT inside m_container), so it wasn't cloned.
            // Create a new armor text+icon next to the weight display.
            BuildArmorDisplay(gui);
        }

        private void BuildArmorDisplay(InventoryGui gui)
        {
            if (gui.m_armor == null)
            {
                CompanionsPlugin.Log.LogWarning("[UI] gui.m_armor is null — cannot clone armor display");
                return;
            }

            if (_weightText == null)
            {
                CompanionsPlugin.Log.LogWarning("[UI] Weight text not found — cannot position armor display");
                return;
            }

            // Clone the vanilla armor group (parent contains shield icon + text)
            Transform armorSourceParent = gui.m_armor.transform.parent;
            if (armorSourceParent == null)
            {
                CompanionsPlugin.Log.LogWarning("[UI] m_armor has no parent — cannot clone armor display");
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
                $"[UI] Cloned armor display — text={_armorText != null} " +
                $"parent=\"{_weightText.transform.parent.name}\" pos={armorRT.anchoredPosition}");
        }

        private void UpdateWeightAndArmor()
        {
            if (_companion == null) return;

            // ── Weight ──
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

            // ── Armor ──
            if (_armorText != null)
            {
                float armor = _companion.GetTotalArmor();
                _armorText.text = Mathf.FloorToInt(armor).ToString();
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Grid callbacks
        // ══════════════════════════════════════════════════════════════════════

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

            // ── Currently dragging an item ──
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

                // Use InventoryGrid.DropItem — handles swap & stack merge natively
                if (_grid.DropItem(dragInv, dragItem, dragAmt, pos))
                {
                    ClearDrag();
                    OnCompanionInventoryMutated();
                }
                return;
            }

            // ── Not dragging — modifier-based actions ──
            if (item == null) return;

            switch (mod)
            {
                case InventoryGrid.Modifier.Move:
                {
                    // Ctrl+Click: move item from companion → player
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

        // ══════════════════════════════════════════════════════════════════════
        //  Grid update — called every frame while visible
        // ══════════════════════════════════════════════════════════════════════

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

        // ══════════════════════════════════════════════════════════════════════
        //  Food slot refresh
        // ══════════════════════════════════════════════════════════════════════

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

        // ══════════════════════════════════════════════════════════════════════
        //  Name / ownership / inventory
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

        private void OnCompanionInventoryMutated()
        {
            _companion?.SyncEquipmentToInventory();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Item interaction helpers
        // ══════════════════════════════════════════════════════════════════════

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

        // ══════════════════════════════════════════════════════════════════════
        //  Teardown
        // ══════════════════════════════════════════════════════════════════════

        private void Teardown()
        {
            if (_root != null) { Destroy(_root); _root = null; }
            _grid               = null;
            _nameInput          = null;
            _foodSlotIcons      = null;
            _foodSlotCounts     = null;
            _foodSlotsContainer = null;
            _weightText         = null;
            _armorText          = null;
            _built              = false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Font helpers
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

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private static void StretchFill(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Bootstrap
        // ══════════════════════════════════════════════════════════════════════

        internal static void EnsureInstance()
        {
            if (Instance != null) return;
            var go = new GameObject("HC_CompanionInteractPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<CompanionInteractPanel>();
        }
    }
}
