using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Attached to every spawned companion.
    /// Reads appearance from ZDO and applies it; re-establishes follow target on zone reload.
    /// Handles auto-equip when inventory changes and restores action mode / name.
    /// </summary>
    public class CompanionSetup : MonoBehaviour
    {
        // ── ZDO hash keys ──────────────────────────────────────────────────────
        internal static readonly int AppearanceHash = StringExtensionMethods.GetStableHashCode("HC_Appearance");
        internal static readonly int OwnerHash      = StringExtensionMethods.GetStableHashCode("HC_Owner");
        internal static readonly int NameHash       = StringExtensionMethods.GetStableHashCode("HC_Name");
        internal static readonly int ActionModeHash = StringExtensionMethods.GetStableHashCode("HC_ActionMode");
        internal static readonly int ActionModeSchemaHash = StringExtensionMethods.GetStableHashCode("HC_ActionModeSchema");
        internal static readonly int StaminaHash    = StringExtensionMethods.GetStableHashCode("HC_Stamina");
        internal static readonly int AutoPickupHash = StringExtensionMethods.GetStableHashCode("HC_AutoPickup");
        internal static readonly int WanderHash = StringExtensionMethods.GetStableHashCode("HC_Wander");
        internal static readonly int FormationSlotHash = StringExtensionMethods.GetStableHashCode("HC_FormationSlot");
        internal static readonly int IsCommandableHash = StringExtensionMethods.GetStableHashCode("HC_IsCommandable");
        internal static readonly int StayHomeHash  = StringExtensionMethods.GetStableHashCode("HC_StayHome");
        internal static readonly int HomePosHash    = StringExtensionMethods.GetStableHashCode("HC_HomePos");
        internal static readonly int HomePosSetHash = StringExtensionMethods.GetStableHashCode("HC_HomePosSet");
        private  static readonly int StarterGearHash = StringExtensionMethods.GetStableHashCode("HC_StarterGear");
        internal static readonly int FollowHash     = StringExtensionMethods.GetStableHashCode("HC_Follow");
        internal static readonly int CombatStanceHash = StringExtensionMethods.GetStableHashCode("HC_CombatStance");
        internal static readonly int TombstoneIdHash  = StringExtensionMethods.GetStableHashCode("HC_TombstoneId");
        internal static readonly int TombInvWidthHash  = StringExtensionMethods.GetStableHashCode("HC_TombInvW");
        internal static readonly int TombInvHeightHash = StringExtensionMethods.GetStableHashCode("HC_TombInvH");

        // Fast lookup for VisEquipmentPatches — avoids GetComponent per frame
        private static readonly HashSet<VisEquipment> _companionVisEquips = new HashSet<VisEquipment>();
        internal static bool IsCompanionVisEquip(VisEquipment ve) => _companionVisEquips.Contains(ve);
        // DvergerPrefabHash removed — use CompanionTierData.IsDvergerVariant() instead
        internal const int ModeFollow           = 0;
        internal const int ModeGatherWood       = 1;
        internal const int ModeGatherStone      = 2;
        internal const int ModeGatherOre        = 3;
        internal const int ModeStay             = 4;
        internal const int ModeForage           = 5;
        internal const int ModeSmelt            = 6;
        internal const int ModeHunt             = 7;
        internal const int ModeFarm             = 8;
        internal const int ModeFish             = 9;
        internal const int ModeRepairBuildings  = 15;
        internal const int ModeRestock          = 16;

        // ── Combat stances ────────────────────────────────────────────────────
        internal const int StanceBalanced   = 0;
        internal const int StanceAggressive = 1;
        internal const int StanceDefensive  = 2;
        internal const int StancePassive    = 3;
        internal const int StanceMelee      = 4;
        internal const int StanceRanged     = 5;

        internal static float MaxLeashDistance => ModConfig.MaxLeashDistance.Value;
        private const int ActionModeSchemaVersion = 2;

        // ── Reflection ───────────────────────────────────────────────────────
        private static readonly MethodInfo _updateVisuals =
            AccessTools.Method(typeof(VisEquipment), "UpdateVisuals");
        internal static readonly FieldInfo _rightItemField    = AccessTools.Field(typeof(Humanoid), "m_rightItem");
        internal static readonly FieldInfo _leftItemField     = AccessTools.Field(typeof(Humanoid), "m_leftItem");
        private static readonly FieldInfo _chestItemField    = AccessTools.Field(typeof(Humanoid), "m_chestItem");
        private static readonly FieldInfo _legItemField      = AccessTools.Field(typeof(Humanoid), "m_legItem");
        private static readonly FieldInfo _helmetItemField   = AccessTools.Field(typeof(Humanoid), "m_helmetItem");
        private static readonly FieldInfo _shoulderItemField = AccessTools.Field(typeof(Humanoid), "m_shoulderItem");
        private static readonly FieldInfo _utilityItemField  = AccessTools.Field(typeof(Humanoid), "m_utilityItem");
        private static readonly FieldInfo _trinketItemField  = AccessTools.Field(typeof(Humanoid), "m_trinketItem");

        private ZNetView         _nview;
        private VisEquipment     _visEquip;
        private CompanionAI      _ai;
        private Humanoid         _humanoid;
        private ZSyncAnimation   _zanim;
        private HarvestController _harvestCached;
        private CompanionRest     _restCached;
        private SmeltController   _smeltCached;
        private bool           _initialized;
        private bool           _ownerMismatchLogged;
        private bool           _uiFrozen;
        private bool           _dead;

        // ── Extra utility slots (ExtraSlots compat) ─────────────────────────
        private ItemDrop.ItemData[] _extraUtilities;
        internal const string ExtraUtilitySlotKey = "HC_ExtraUtilitySlot";
        private const string ExtraSlotPrevPosXKey = "HC_ExtraSlotPrevPosX";
        private const string ExtraSlotPrevPosYKey = "HC_ExtraSlotPrevPosY";

        // Minimap pin — live-updating, owner-only
        private Minimap.PinData _minimapPin;
        private Minimap _lastMinimapInstance;

        /// <summary>
        /// When true, auto-equip is suppressed (used by CompanionHarvest to keep tools equipped).
        /// </summary>
        internal bool SuppressAutoEquip { get; set; }

        /// <summary>
        /// True while the equip queue is processing items (weapon + armor).
        /// CombatController checks this to delay engagement until gear is equipped.
        /// </summary>
        internal bool IsEquipping => _equipQueue.Count > 0 || _equipAnimActive;

        // ── Extra utility accessors (for Harmony patches) ───────────────────
        internal int GetExtraUtilityCount() => _extraUtilities?.Length ?? 0;

        internal ItemDrop.ItemData GetExtraUtilityItem(int index)
        {
            if (_extraUtilities == null || index < 0 || index >= _extraUtilities.Length)
                return null;
            return _extraUtilities[index];
        }

        internal ItemDrop.ItemData GetHelmetItem()   => GetEquipSlot(_helmetItemField);
        internal ItemDrop.ItemData GetChestItem()    => GetEquipSlot(_chestItemField);
        internal ItemDrop.ItemData GetLegsItem()     => GetEquipSlot(_legItemField);
        internal ItemDrop.ItemData GetShoulderItem() => GetEquipSlot(_shoulderItemField);
        internal ItemDrop.ItemData GetUtilityItem()  => GetEquipSlot(_utilityItemField);
        internal ItemDrop.ItemData GetTrinketItem()  => GetEquipSlot(_trinketItemField);

        internal ItemDrop.ItemData GetItemForExtraSlotsSlot(string slotId)
        {
            if (string.IsNullOrEmpty(slotId)) return null;

            if (string.Equals(slotId, "Helmet", StringComparison.OrdinalIgnoreCase))   return GetHelmetItem();
            if (string.Equals(slotId, "Chest", StringComparison.OrdinalIgnoreCase))    return GetChestItem();
            if (string.Equals(slotId, "Legs", StringComparison.OrdinalIgnoreCase))     return GetLegsItem();
            if (string.Equals(slotId, "Shoulder", StringComparison.OrdinalIgnoreCase)) return GetShoulderItem();
            if (string.Equals(slotId, "Utility", StringComparison.OrdinalIgnoreCase))  return GetUtilityItem();
            if (string.Equals(slotId, "Trinket", StringComparison.OrdinalIgnoreCase))  return GetTrinketItem();

            if (ExtraSlotsCompat.TryGetExtraUtilityIndex(slotId, out int extraIdx))
                return GetExtraUtilityItem(extraIdx);

            return null;
        }

        internal bool CanItemFitExtraSlotsSlot(string slotId, ItemDrop.ItemData item)
        {
            if (string.IsNullOrEmpty(slotId) || item?.m_shared == null) return false;

            if (ExtraSlotsCompat.IsLoaded)
                return ExtraSlotsCompat.ItemFitsSlot(slotId, item);

            switch (slotId)
            {
                case "Helmet":
                    return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Helmet;
                case "Chest":
                    return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Chest;
                case "Legs":
                    return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Legs;
                case "Shoulder":
                    return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shoulder;
                case "Utility":
                case "Trinket":
                    return item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Utility;
            }

            return false;
        }

        internal bool TrySetItemForExtraSlotsSlot(string slotId, ItemDrop.ItemData item)
        {
            if (_humanoid == null || string.IsNullOrEmpty(slotId)) return false;
            var inv = _humanoid.GetInventory();
            if (inv == null) return false;
            if (item != null && !inv.ContainsItem(item)) return false;

            if (ExtraSlotsCompat.TryGetExtraUtilityIndex(slotId, out int extraIdx))
                return TrySetExtraUtilitySlot(extraIdx, slotId, item);

            var slotField = GetVanillaSlotField(slotId);
            if (slotField == null) return false;

            var current = slotField.GetValue(_humanoid) as ItemDrop.ItemData;
            if (item == null)
            {
                if (current == null) return true;
                _humanoid.UnequipItem(current, false);
                RestoreFromExtraSlotsGridPosition(current, inv);
                return true;
            }

            if (!CanItemFitExtraSlotsSlot(slotId, item))
                return false;

            RemoveItemFromExtraUtilitySlots(item);

            if (current != null && current != item)
            {
                _humanoid.UnequipItem(current, false);
                RestoreFromExtraSlotsGridPosition(current, inv);
            }

            if (!_humanoid.IsItemEquiped(item))
                _humanoid.EquipItem(item, true);

            bool equipped = slotField.GetValue(_humanoid) == item || _humanoid.IsItemEquiped(item);
            if (equipped)
                TryMoveIntoExtraSlotsGridPosition(slotId, item, inv);
            return equipped;
        }

        private bool TrySetExtraUtilitySlot(int slot, string slotId, ItemDrop.ItemData item)
        {
            if (_extraUtilities == null) return false;
            if (slot < 0 || slot >= _extraUtilities.Length) return false;
            if (item != null && !CanItemFitExtraSlotsSlot(slotId, item)) return false;

            var current = _extraUtilities[slot];
            if (current == item) return true;

            if (item != null)
                RemoveItemFromExtraUtilitySlots(item);

            if (current != null)
            {
                current.m_equipped = false;
                current.m_customData?.Remove(ExtraUtilitySlotKey);
                _extraUtilities[slot] = null;
                ExtraSlotsCompat.SetExtraUtility(_humanoid, slot, null);
                RestoreFromExtraSlotsGridPosition(current, _humanoid?.GetInventory());
            }

            if (item != null)
            {
                item.m_equipped = true;
                if (item.m_customData == null)
                    item.m_customData = new Dictionary<string, string>();
                item.m_customData[ExtraUtilitySlotKey] = slot.ToString();
                _extraUtilities[slot] = item;
                ExtraSlotsCompat.SetExtraUtility(_humanoid, slot, item);
                TryMoveIntoExtraSlotsGridPosition(slotId, item, _humanoid?.GetInventory());
            }

            ExtraSlotsPatches.CallSetupEquipment(_humanoid);
            return true;
        }

        private void RemoveItemFromExtraUtilitySlots(ItemDrop.ItemData item)
        {
            if (_extraUtilities == null || item == null) return;
            for (int i = 0; i < _extraUtilities.Length; i++)
            {
                if (_extraUtilities[i] != item) continue;
                _extraUtilities[i] = null;
                item.m_customData?.Remove(ExtraUtilitySlotKey);
                ExtraSlotsCompat.SetExtraUtility(_humanoid, i, null);
                RestoreFromExtraSlotsGridPosition(item, _humanoid?.GetInventory());
            }
        }

        private void TryMoveIntoExtraSlotsGridPosition(string slotId, ItemDrop.ItemData item, Inventory inv)
        {
            if (!ExtraSlotsCompat.IsLoaded || item == null || inv == null || string.IsNullOrEmpty(slotId))
                return;
            if (!ExtraSlotsCompat.TryGetSlotGridPosition(slotId, out Vector2i slotPos))
                return;
            if (!IsPositionInsideInventory(inv, slotPos))
                return;

            if (!ExtraSlotsCompat.IsAnySlotGridPosition(item.m_gridPos))
                RememberItemRegularGridPosition(item);

            var existing = inv.GetItemAt(slotPos.x, slotPos.y);
            if (existing != null && existing != item)
            {
                if (TryFindOpenRegularInventoryCell(inv, out Vector2i free))
                    existing.m_gridPos = free;
                else if (IsPositionInsideInventory(inv, item.m_gridPos) &&
                         !ExtraSlotsCompat.IsAnySlotGridPosition(item.m_gridPos))
                    existing.m_gridPos = item.m_gridPos;
                else
                    return;
            }

            item.m_gridPos = slotPos;
        }

        private void RestoreFromExtraSlotsGridPosition(ItemDrop.ItemData item, Inventory inv)
        {
            if (!ExtraSlotsCompat.IsLoaded || item == null || inv == null)
                return;

            if (item.m_customData == null)
                return;

            bool restored = false;
            if (!item.m_customData.TryGetValue(ExtraSlotPrevPosXKey, out string xStr))
                return;
            if (!item.m_customData.TryGetValue(ExtraSlotPrevPosYKey, out string yStr))
                return;

            if (int.TryParse(xStr, out int x) &&
                int.TryParse(yStr, out int y))
            {
                var prev = new Vector2i(x, y);
                if (IsPositionInsideInventory(inv, prev) &&
                    !ExtraSlotsCompat.IsAnySlotGridPosition(prev))
                {
                    var blocker = inv.GetItemAt(prev.x, prev.y);
                    if (blocker == null || blocker == item)
                    {
                        item.m_gridPos = prev;
                        restored = true;
                    }
                }
            }

            if (!restored && TryFindOpenRegularInventoryCell(inv, out Vector2i free))
                item.m_gridPos = free;

            item.m_customData.Remove(ExtraSlotPrevPosXKey);
            item.m_customData.Remove(ExtraSlotPrevPosYKey);
        }

        private static bool TryFindOpenRegularInventoryCell(Inventory inv, out Vector2i pos)
        {
            pos = Vector2i.zero;
            if (inv == null) return false;

            int w = inv.GetWidth();
            int h = inv.GetHeight();

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var p = new Vector2i(x, y);
                    if (ExtraSlotsCompat.IsLoaded && ExtraSlotsCompat.IsAnySlotGridPosition(p))
                        continue;
                    if (inv.GetItemAt(x, y) != null)
                        continue;

                    pos = p;
                    return true;
                }
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (inv.GetItemAt(x, y) != null)
                        continue;

                    pos = new Vector2i(x, y);
                    return true;
                }
            }

            return false;
        }

        private static bool IsPositionInsideInventory(Inventory inv, Vector2i pos)
        {
            if (inv == null) return false;
            return pos.x >= 0 && pos.y >= 0 &&
                   pos.x < inv.GetWidth() && pos.y < inv.GetHeight();
        }

        private static void RememberItemRegularGridPosition(ItemDrop.ItemData item)
        {
            if (item == null) return;
            if (item.m_customData == null)
                item.m_customData = new Dictionary<string, string>();

            item.m_customData[ExtraSlotPrevPosXKey] = item.m_gridPos.x.ToString();
            item.m_customData[ExtraSlotPrevPosYKey] = item.m_gridPos.y.ToString();
        }

        private static FieldInfo GetVanillaSlotField(string slotId)
        {
            switch (slotId)
            {
                case "Helmet": return _helmetItemField;
                case "Chest": return _chestItemField;
                case "Legs": return _legItemField;
                case "Shoulder": return _shoulderItemField;
                case "Utility": return _utilityItemField;
                case "Trinket": return _trinketItemField;
                default: return null;
            }
        }

        private static string GetSlotIdForVanillaField(FieldInfo slotField)
        {
            if (slotField == _helmetItemField) return "Helmet";
            if (slotField == _chestItemField) return "Chest";
            if (slotField == _legItemField) return "Legs";
            if (slotField == _shoulderItemField) return "Shoulder";
            if (slotField == _utilityItemField) return "Utility";
            if (slotField == _trinketItemField) return "Trinket";
            return null;
        }

        private void Awake()
        {
            _nview         = GetComponent<ZNetView>();
            _visEquip      = GetComponent<VisEquipment>();
            _ai            = GetComponent<CompanionAI>();
            _humanoid      = GetComponent<Humanoid>();
            _zanim         = GetComponent<ZSyncAnimation>();
            _harvestCached = GetComponent<HarvestController>();
            _restCached    = GetComponent<CompanionRest>();
            _smeltCached   = GetComponent<SmeltController>();

            CompanionsPlugin.Log.LogInfo(
                $"[Setup] Awake — nview={_nview != null} visEquip={_visEquip != null} " +
                $"ai={_ai != null} humanoid={_humanoid != null} name=\"{gameObject.name}\"");

            if (_visEquip != null)
                _companionVisEquips.Add(_visEquip);

            // Subscribe to death event — fires before Character.OnDeath destroys the object
            if (_humanoid != null)
                _humanoid.m_onDeath = (Action)Delegate.Combine(_humanoid.m_onDeath, new Action(OnCompanionDeath));

            TryInit();
        }

        private void Start()  { if (!_initialized) TryInit(); }

        private void Update()
        {
            if (!_initialized) { TryInit(); return; }

            // Freeze companion in place while our UI is open —
            // prevents it from running around or following the player during interaction.
            if (CompanionInteractPanel.IsOpenFor(this) || CompanionRadialMenu.IsOpenFor(this))
            {
                _ai?.SetPatrolPoint();
                _ai?.SetFollowTarget(null);
                _uiFrozen = true;
                return;
            }

            // UI just closed — restore follow target based on current mode.
            // Use force=true to bypass the harvest-active guard, since the
            // harvest controller was paused during UI and needs a valid
            // fallback follow/patrol to resume from.
            // Skip if tombstone recovery is active — it manages its own follow state.
            if (_uiFrozen)
            {
                _uiFrozen = false;
                if (_ai != null && _ai.IsRecoveringTombstone)
                {
                    // Tombstone recovery was interrupted by UI — let it resume
                    // by keeping follow=null so it can navigate to the tombstone.
                    CompanionsPlugin.Log.LogDebug(
                        "[Setup] UI closed during tombstone recovery — skipping follow restore");
                }
                else
                {
                    var zdo = _nview?.GetZDO();
                    if (zdo != null && _ai != null)
                    {
                        int mode = zdo.GetInt(ActionModeHash, ModeFollow);
                        ApplyFollowMode(mode, force: true);
                    }
                }
            }

            // Process deferred auto-equip (throttled during rapid pickup)
            if (_autoEquipCooldown > 0f)
            {
                _autoEquipCooldown -= Time.deltaTime;
                if (_autoEquipCooldown <= 0f && _autoEquipPending)
                {
                    _autoEquipPending = false;
                    if (_humanoid != null && _nview != null && _nview.IsOwner() && !SuppressAutoEquip)
                    {
                        CompanionsPlugin.Log.LogDebug("[Setup] Deferred auto-equip firing now");
                        AutoEquipBest();
                    }
                    else if (SuppressAutoEquip)
                    {
                        CompanionsPlugin.Log.LogDebug("[Setup] Deferred auto-equip skipped — SuppressAutoEquip=true");
                    }
                }
            }

            // Process equip queue (delayed equipping with animation)
            UpdateEquipQueue(Time.deltaTime);

            // Minimap pin — must run every frame regardless of mode/state
            // so the pin stays visible during harvest, smelt, rest, etc.
            UpdateMinimapPin();

            // Ensure follow target stays set for follow/gather modes.
            // It can be lost on zone reload, player respawn, or after combat.
            if (_ai != null && _ai.GetFollowTarget() == null && Player.m_localPlayer != null)
            {
                var zdo = _nview?.GetZDO();
                if (zdo == null) return;

                string owner   = zdo.GetString(OwnerHash, "");
                long   localPid = Player.m_localPlayer.GetPlayerID();
                string localId = localPid.ToString();

                // Skip ownership check while player ID is 0 (ZDO not loaded yet)
                if (localPid == 0L) return;

                // Claim orphaned companions (empty owner = just spawned before ZDO synced)
                if (string.IsNullOrEmpty(owner))
                {
                    zdo.Set(OwnerHash, localId);
                    CompanionsPlugin.Log.LogDebug(
                        $"[Setup] Claimed orphan companion — set owner to {localId}");
                    owner = localId;
                }

                if (owner != localId)
                {
                    // Log once to aid debugging — don't spam every frame
                    if (!_ownerMismatchLogged)
                    {
                        _ownerMismatchLogged = true;
                        CompanionsPlugin.Log.LogWarning(
                            $"[Setup] Owner mismatch — zdo owner=\"{owner}\" localId=\"{localId}\" " +
                            $"— follow target NOT restored");
                    }
                    return;
                }

                int mode = zdo.GetInt(ActionModeHash, ModeFollow);

                // ** CONFLICT CHECKS ** — Various systems intentionally set follow=null.
                // Do NOT restore it or we'll create a tug-of-war.
                if (_harvestCached != null && _harvestCached.IsActive)
                    return; // HarvestController is driving movement

                if (_smeltCached != null && _smeltCached.IsActive)
                    return; // SmeltController is driving movement

                if (_restCached != null && (_restCached.IsNavigating || _restCached.IsResting))
                    return; // CompanionRest is navigating to a bed/fire or resting

                if (_ai.PendingCartAttach != null || _ai.PendingMoveTarget != null ||
                    _ai.PendingDepositContainer != null)
                    return; // Navigating to a directed position

                if (_ai.IsRecoveringTombstone)
                    return; // Tombstone recovery is driving movement

                // Follow toggle OFF: don't restore follow target
                if (!GetFollow())
                    return;

                // All active modes (everything except Stay): companion should follow
                // player when no directed navigation is active and Follow toggle is ON.
                if (mode != ModeStay)
                {
                    _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                    CompanionsPlugin.Log.LogDebug(
                        $"[Setup] Follow target was null — restored to player " +
                        $"(mode={mode} harvestActive={_harvestCached?.IsActive ?? false})");
                }
            }


        }

        private void UpdateMinimapPin()
        {
            if (Minimap.instance == null) return;

            // Detect stale pin from Minimap instance change (scene reload, logout/login).
            // When the Minimap is recreated, it calls ClearPins() which removes all pins
            // from its internal list. Our _minimapPin reference becomes orphaned — still
            // non-null but no longer tracked by the Minimap, so it's invisible.
            if (_lastMinimapInstance != Minimap.instance)
            {
                _minimapPin = null;
                _lastMinimapInstance = Minimap.instance;
            }

            // Only show pin for the local player's companions
            var zdo = _nview?.GetZDO();
            if (zdo == null || Player.m_localPlayer == null)
            {
                RemoveMinimapPin();
                return;
            }

            string owner = zdo.GetString(OwnerHash, "");
            long localPid = Player.m_localPlayer.GetPlayerID();
            if (localPid == 0L) return;

            if (owner != localPid.ToString())
            {
                RemoveMinimapPin();
                return;
            }

            // Create pin if it doesn't exist
            if (_minimapPin == null)
            {
                string compName = zdo.GetString(NameHash, "Companion");
                _minimapPin = Minimap.instance.AddPin(
                    transform.position, Minimap.PinType.Player,
                    compName, false, false, 0L);
                CompanionsPlugin.Log.LogDebug(
                    $"[Minimap] Created pin for \"{compName}\" at {transform.position:F1}");
            }

            // Update position every frame
            if (_minimapPin.m_pos != transform.position)
                _minimapPin.m_pos = transform.position;
        }

        private void RemoveMinimapPin()
        {
            if (_minimapPin != null && Minimap.instance != null)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Minimap] Removed pin for \"{_minimapPin.m_name}\"");
                Minimap.instance.RemovePin(_minimapPin);
                _minimapPin = null;
            }
        }

        private void TryInit()
        {
            if (_initialized) return;
            if (_nview == null || _nview.GetZDO() == null) return;

            var zdo        = _nview.GetZDO();
            string serial  = zdo.GetString(AppearanceHash, "");
            var appearance = string.IsNullOrEmpty(serial)
                ? CompanionAppearance.Default()
                : CompanionAppearance.Deserialize(serial);

            EnsureActionModeMigration();
            ApplyAppearance(appearance);
            RestoreFollowTarget();
            RestoreActionMode();
            RestoreName();
            RegisterInventoryCallback();
            AddStarterGear(zdo);
            InitExtraUtilities();
            RestoreExtraUtilities();

            int mode = zdo.GetInt(ActionModeHash, ModeFollow);
            CompanionsPlugin.Log.LogInfo(
                $"[Setup] Initialized — mode={mode} appearance={(string.IsNullOrEmpty(serial) ? "default" : "saved")} " +
                $"extraSlots={_extraUtilities?.Length ?? 0} pos={transform.position:F1}");

            _initialized = true;
        }

        private void EnsureActionModeMigration()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            int schema = zdo.GetInt(ActionModeSchemaHash, 0);
            if (schema >= ActionModeSchemaVersion) return;

            int mode = zdo.GetInt(ActionModeHash, ModeFollow);

            // Legacy mapping: 2 used to be Stay before gather modes were fully wired.
            if (mode == 2) mode = ModeStay;
            if (mode < ModeFollow || mode > ModeFish) mode = ModeFollow;

            zdo.Set(ActionModeHash, mode);
            zdo.Set(ActionModeSchemaHash, ActionModeSchemaVersion);
        }

        // ──────────────────────────────────────────────────────────────────────
        internal void ApplyAppearance(CompanionAppearance a)
        {
            // Companion (Player clone): apply full appearance customization
            if (CanWearArmor() && _visEquip != null)
            {
                _visEquip.SetModel(a.ModelIndex);
                _visEquip.SetHairItem(string.IsNullOrEmpty(a.HairItem) ? "Hair1" : a.HairItem);
                _visEquip.SetBeardItem(a.ModelIndex == 0 ? (a.BeardItem ?? "") : "");
                _visEquip.SetSkinColor(a.SkinColor);
                _visEquip.SetHairColor(a.HairColor);
            }

            // Refresh visual equipment state (needed for both types)
            if (_visEquip != null)
                _updateVisuals?.Invoke(_visEquip, null);

            // NOTE: Do NOT call animator.Update(0f) here — it triggers
            // CharacterAnimEvent.OnAnimatorMove before CharacterAnimEvent.Awake,
            // causing NullRef. The animator updates naturally on the first frame.

            // Ensure all children are on the character layer (rendering + camera culling)
            int charLayer = LayerMask.NameToLayer("character");
            if (charLayer < 0) charLayer = 9;
            foreach (var t in GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = charLayer;
        }

        // ──────────────────────────────────────────────────────────────────────
        internal void RestoreFollowTarget()
        {
            if (_ai == null || Player.m_localPlayer == null) return;
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            string owner   = zdo.GetString(OwnerHash, "");
            long   localPid = Player.m_localPlayer.GetPlayerID();
            string localId = localPid.ToString();

            if (localPid == 0L) return; // Player ZDO not ready yet

            if (string.IsNullOrEmpty(owner))
            {
                zdo.Set(OwnerHash, localId);
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] RestoreFollow — claimed orphan, set owner to {localId}");
                owner = localId;
            }

            if (owner != localId)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] RestoreFollow — owner mismatch: zdo=\"{owner}\" local=\"{localId}\"");
                return;
            }

            int mode = zdo.GetInt(ActionModeHash, ModeFollow);
            ApplyFollowMode(mode);

            // Ensure formation slot is assigned
            AssignFormationSlot();
        }

        // ──────────────────────────────────────────────────────────────────────
        private void RestoreActionMode()
        {
            if (_ai == null || Player.m_localPlayer == null) return;
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            string owner   = zdo.GetString(OwnerHash, "");
            long   localPid = Player.m_localPlayer.GetPlayerID();
            string localId = localPid.ToString();

            if (localPid == 0L) return;

            if (string.IsNullOrEmpty(owner))
            {
                zdo.Set(OwnerHash, localId);
                owner = localId;
            }

            if (owner != localId) return;

            int mode = zdo.GetInt(ActionModeHash, ModeFollow);
            ApplyFollowMode(mode);
        }

        internal void ApplyFollowMode(int mode, bool force = false)
        {
            if (_ai == null) return;

            bool stayHome = GetStayHome() && HasHomePosition();
            bool follow = GetFollow();
            CompanionsPlugin.Log.LogDebug($"[Setup] ApplyFollowMode — mode={mode} stayHome={stayHome} follow={follow} force={force}");

            switch (mode)
            {
                case ModeFollow:
                    if (follow && Player.m_localPlayer != null)
                    {
                        _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → Follow player (mode={mode})");
                    }
                    else if (stayHome)
                    {
                        _ai.SetFollowTarget(null);
                        _ai.SetPatrolPointAt(GetHomePosition());
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → StayHome patrol at {GetHomePosition():F1}");
                    }
                    else
                    {
                        _ai.SetFollowTarget(null);
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → Follow OFF, no StayHome — idle");
                    }
                    break;
                case ModeGatherWood:
                case ModeGatherStone:
                case ModeGatherOre:
                case ModeForage:
                case ModeSmelt:
                case ModeFarm:
                    // Don't override follow target if HarvestController, SmeltController,
                    // or FarmController is actively driving movement.
                    // Skip this guard when force=true (UI close restoration).
                    if (!force)
                    {
                        var harvestCheck = GetComponent<HarvestController>();
                        if (harvestCheck != null && harvestCheck.IsActive)
                        {
                            CompanionsPlugin.Log.LogDebug(
                                $"[Setup]   → Gather mode={mode}, harvest active — skipping follow override");
                            break;
                        }
                        var smeltCheck = GetComponent<SmeltController>();
                        if (smeltCheck != null && smeltCheck.IsActive)
                        {
                            CompanionsPlugin.Log.LogDebug(
                                $"[Setup]   → Smelt mode={mode}, smelt active — skipping follow override");
                            break;
                        }
                        var farmCheck = GetComponent<FarmController>();
                        if (farmCheck != null && farmCheck.IsActive)
                        {
                            CompanionsPlugin.Log.LogDebug(
                                $"[Setup]   → Farm mode={mode}, farm active — skipping follow override");
                            break;
                        }
                    }
                    if (follow && Player.m_localPlayer != null)
                    {
                        _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → Follow player (mode={mode})");
                    }
                    else if (stayHome)
                    {
                        _ai.SetFollowTarget(null);
                        _ai.SetPatrolPointAt(GetHomePosition());
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → Gather+StayHome patrol at {GetHomePosition():F1}");
                    }
                    else
                    {
                        _ai.SetFollowTarget(null);
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → Follow OFF, gather mode={mode} — idle");
                    }
                    break;
                case ModeStay:
                    _ai.SetFollowTarget(null);
                    if (stayHome)
                        _ai.SetPatrolPointAt(GetHomePosition());
                    else
                        _ai.SetPatrolPoint();
                    CompanionsPlugin.Log.LogDebug($"[Setup]   → Stay/patrol at {(stayHome ? GetHomePosition() : transform.position):F1}");
                    break;
                default:
                    if (follow && Player.m_localPlayer != null)
                    {
                        _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → Follow player (default fallback, mode={mode})");
                    }
                    else if (stayHome)
                    {
                        _ai.SetFollowTarget(null);
                        _ai.SetPatrolPointAt(GetHomePosition());
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → StayHome patrol (default fallback, mode={mode})");
                    }
                    else
                    {
                        _ai.SetFollowTarget(null);
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → Follow OFF (default fallback, mode={mode})");
                    }
                    break;
            }
        }

        private void RestoreName()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            string name = zdo.GetString(NameHash, "");
            if (!string.IsNullOrEmpty(name))
            {
                var character = GetComponent<Character>();
                if (character != null) character.m_name = name;
            }
        }

        // ── Starter gear ─────────────────────────────────────────────────────
        // Companions spawn with no gear — the player equips them manually.

        private void AddStarterGear(ZDO zdo)
        {
            // No-op: companions spawn empty. Kept as hook for future use.
            if (!_nview.IsOwner()) return;
            zdo.Set(StarterGearHash, true);
        }

        // ── Auto-equip ──────────────────────────────────────────────────────

        private bool _equipping;
        private float _autoEquipCooldown;
        private bool  _autoEquipPending;
        private const float AutoEquipMinInterval = 0.5f;

        // ── Equip queue (delayed equipping with animation) ──────────────────
        private readonly List<ItemDrop.ItemData> _equipQueue = new List<ItemDrop.ItemData>();
        private float _equipTimer;
        private bool  _equipAnimActive;

        private Action _inventoryCallback;

        private void RegisterInventoryCallback()
        {
            if (_humanoid == null) return;
            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            _inventoryCallback = new Action(OnInventoryChanged);
            inv.m_onChanged = (Action)Delegate.Combine(inv.m_onChanged, _inventoryCallback);
        }

        private void UnregisterInventoryCallback()
        {
            if (_humanoid == null || _inventoryCallback == null) return;
            var inv = _humanoid.GetInventory();
            if (inv == null) return;
            inv.m_onChanged = (Action)Delegate.Remove(inv.m_onChanged, _inventoryCallback);
            _inventoryCallback = null;
        }

        // ── Death System ──────────────────────────────────────────────────

        private void OnCompanionDeath()
        {
            if (_dead) return;
            _dead = true;

            if (_nview == null || !_nview.IsOwner()) return;
            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            // Close companion UI immediately — prevents glitched empty-grid rendering
            // and vanilla container panel flash when inventory is emptied below.
            // Must happen before any inventory manipulation.
            if (CompanionInteractPanel.IsOpenFor(this))
            {
                CompanionInteractPanel.Instance?.Hide();
                InventoryGui.instance?.Hide();
            }

            // Capture identity before destruction
            string companionName = _humanoid != null ? _humanoid.m_name : "Companion";
            string zdoName = zdo.GetString(NameHash, "");
            if (!string.IsNullOrEmpty(zdoName)) companionName = zdoName;

            string appearance = zdo.GetString(AppearanceHash, "");
            string owner = zdo.GetString(OwnerHash, "");
            int stance = zdo.GetInt(CombatStanceHash, StanceBalanced);
            int actionMode = zdo.GetInt(ActionModeHash, ModeFollow);
            int actionModeSchema = zdo.GetInt(ActionModeSchemaHash, 0);
            bool follow = zdo.GetBool(FollowHash, true);
            bool stayHome = zdo.GetBool(StayHomeHash, false);
            bool autoPickup = zdo.GetBool(AutoPickupHash, true);
            bool wander = zdo.GetBool(WanderHash, false);
            int commandable = zdo.GetInt(IsCommandableHash, 1);
            int formationSlot = zdo.GetInt(FormationSlotHash, -1);
            Vector3 deathPos = _humanoid != null ? _humanoid.GetCenterPoint() : transform.position;

            // Resolve prefab name from ZDO hash
            int prefabHash = zdo.GetPrefab();
            string prefabName = CompanionTierData.Companion.PrefabName;
            var resolvedPrefab = ZNetScene.instance?.GetPrefab(prefabHash);
            if (resolvedPrefab != null) prefabName = resolvedPrefab.name;

            CompanionsPlugin.Log.LogInfo(
                $"[Setup] Companion \"{companionName}\" died at {deathPos:F1} — prefab={prefabName}");

            // Apply skill death penalty and serialize for respawn
            string skillsSerialized = "";
            var skills = GetComponent<CompanionSkills>();
            if (skills != null)
            {
                skills.OnDeath();
                skillsSerialized = skills.SerializeSkills();
            }

            // Tombstone ID: only generate a new one if we actually have items to drop.
            // If inventory is empty (died before recovering previous tombstone), preserve
            // the existing tombstoneId so the respawned companion can still find the old grave.
            long existingTombstoneId = zdo.GetLong(TombstoneIdHash, 0L);
            bool hasItems = _humanoid != null && _humanoid.GetInventory() != null
                            && _humanoid.GetInventory().NrOfItems() > 0;
            long tombstoneId = hasItems ? System.DateTime.UtcNow.Ticks : existingTombstoneId;

            if (!hasItems && existingTombstoneId != 0L)
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[Setup] No items — preserving existing tombstoneId={existingTombstoneId} " +
                    "so respawned companion can find previous tombstone");
            }

            // Spawn tombstone with all inventory items
            CreateCompanionTombstone(companionName, deathPos, tombstoneId);

            // Queue respawn — at home position if set, otherwise world spawn
            bool hasHome = HasHomePosition();
            Vector3 homePos = hasHome ? GetHomePosition() : Vector3.zero;
            CompanionManager.QueueRespawn(new CompanionManager.RespawnData
            {
                PrefabName = prefabName,
                Name = companionName,
                AppearanceSerialized = appearance,
                OwnerId = owner,
                CombatStance = stance,
                ActionMode = actionMode,
                ActionModeSchema = actionModeSchema,
                Follow = follow,
                StayHome = stayHome,
                AutoPickup = autoPickup,
                Wander = wander,
                IsCommandable = commandable != 0,
                FormationSlot = formationSlot,
                TombstoneId = tombstoneId,
                SkillsSerialized = skillsSerialized,
                Timer = 5f,
                HasHomePos = hasHome,
                HomePos = homePos
            });
        }

        private void CreateCompanionTombstone(string companionName, Vector3 position, long tombstoneId)
        {
            if (_humanoid == null) return;
            var inv = _humanoid.GetInventory();
            if (inv == null || inv.NrOfItems() == 0)
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[Setup] No items to drop — skipping tombstone (inv={inv != null} items={inv?.NrOfItems() ?? -1})");
                return;
            }

            // Get tombstone prefab from local player
            GameObject tombPrefab = Player.m_localPlayer?.m_tombstone;
            if (tombPrefab == null)
            {
                CompanionsPlugin.Log.LogError("[Setup] Could not find tombstone prefab!");
                return;
            }

            // Suppress auto-equip during item transfer
            SuppressAutoEquip = true;

            int itemCount = inv.NrOfItems();
            CompanionsPlugin.Log.LogInfo(
                $"[Setup] Creating tombstone for \"{companionName}\" — {itemCount} items, " +
                $"tombstoneId={tombstoneId}, invDim={inv.GetWidth()}x{inv.GetHeight()}");

            // Log each item before transfer for diagnostics
            foreach (var item in inv.GetAllItems())
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup]   pre-transfer: \"{item.m_shared?.m_name ?? "?"}\" x{item.m_stack} " +
                    $"pos=({item.m_gridPos.x},{item.m_gridPos.y}) equipped={item.m_equipped}");
            }

            // Clear extra utility slots before death — prevents stale m_customData on tombstone items
            ClearAllExtraUtilities();

            // Unequip all items so MoveInventoryToGrave transfers everything
            _humanoid.UnequipAllItems();

            // Force-clear m_equipped on ALL items in inventory.
            // After Container.Load() from ZDO (zone transitions, reloads), new ItemData
            // instances are created with saved m_equipped=true flags, but the humanoid
            // slot variables still reference old (stale) objects. AutoEquipBest re-equips
            // the best items to slots, but previously-equipped items that are no longer
            // "best" retain their stale m_equipped=true flag. MoveInventoryToGrave skips
            // items with m_equipped=true, causing them to be lost on companion destruction.
            int equippedCleared = 0;
            foreach (var item in inv.GetAllItems())
            {
                if (item.m_equipped) equippedCleared++;
                item.m_equipped = false;
            }

            CompanionsPlugin.Log.LogDebug(
                $"[Setup] Unequipped all items for grave transfer (cleared {equippedCleared} stale equipped flags)");

            // Spawn tombstone at companion's position
            var tombGo = UnityEngine.Object.Instantiate(tombPrefab, position, transform.rotation);
            CompanionsPlugin.Log.LogDebug($"[Setup] Tombstone spawned at {position:F1}");

            // Transfer inventory to tombstone container
            var container = tombGo.GetComponent<Container>();
            if (container == null)
            {
                CompanionsPlugin.Log.LogError("[Setup] Tombstone has no Container component!");
                return;
            }

            var tombInv = container.GetInventory();
            if (tombInv == null)
            {
                CompanionsPlugin.Log.LogError(
                    "[Setup] Tombstone Container.GetInventory() returned null! " +
                    "ZNetView ZDO may not be ready — Container.Awake() skipped inventory creation.");
                return;
            }

            CompanionsPlugin.Log.LogDebug(
                $"[Setup] Tombstone inv before transfer: dim={tombInv.GetWidth()}x{tombInv.GetHeight()} " +
                $"items={tombInv.NrOfItems()}");

            tombInv.MoveInventoryToGrave(inv);

            int transferred = tombInv.NrOfItems();
            int remaining = inv.NrOfItems();
            CompanionsPlugin.Log.LogInfo(
                $"[Setup] Grave transfer complete — {transferred} items in tombstone, " +
                $"{remaining} remaining on companion, tombInv dim={tombInv.GetWidth()}x{tombInv.GetHeight()}");

            if (transferred == 0 && itemCount > 0)
            {
                CompanionsPlugin.Log.LogError(
                    $"[Setup] MoveInventoryToGrave transferred 0/{itemCount} items! " +
                    "Items may have had m_equipped=true despite force-clear.");
            }

            // Set tombstone ownership (companion name as label, tombstoneId for matching)
            var tombstone = tombGo.GetComponent<TombStone>();
            if (tombstone != null)
            {
                tombstone.Setup(companionName, tombstoneId);
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Tombstone ownership set — name=\"{companionName}\" id={tombstoneId}");
            }

            // Repack item grid positions to be sequential (0,0), (1,0), (2,0), ...
            // MoveInventoryToGrave preserves original positions from the companion's 8x4 grid.
            // Inventory.Save does NOT persist width/height, so on zone reload Container.Awake
            // recreates the inventory with prefab defaults. Inventory.Load then calls
            // AddItem(item, amount, x, y) which silently drops items where x >= m_width.
            // Repacking ensures items survive even if dimensions shrink.
            int tw = tombInv.GetWidth();
            int idx = 0;
            foreach (var item in tombInv.GetAllItems())
            {
                item.m_gridPos = new Vector2i(idx % tw, idx / tw);
                idx++;
            }

            // Tag the tombstone with our unique ID and inventory dimensions.
            // Dimensions must be persisted separately because Inventory.Save/Load
            // does not serialize m_width/m_height — they're lost on zone reload.
            var tombNview = tombGo.GetComponent<ZNetView>();
            if (tombNview?.GetZDO() != null)
            {
                var tombZdo = tombNview.GetZDO();
                tombZdo.Set(TombstoneIdHash, tombstoneId);
                tombZdo.Set(TombInvWidthHash, tombInv.GetWidth());
                tombZdo.Set(TombInvHeightHash, tombInv.GetHeight());
            }

            // Add death marker to minimap so the player can find the tombstone
            if (Minimap.instance != null)
            {
                string pinName = companionName + " " + ModLocalization.Loc("hc_minimap_grave");
                Minimap.instance.AddPin(position, Minimap.PinType.Death,
                    pinName, save: true, isChecked: false, 0L);
                CompanionsPlugin.Log.LogInfo(
                    $"[Setup] Added tombstone minimap pin at {position:F1}");
            }

            SuppressAutoEquip = false;
        }

        private void OnDestroy()
        {
            RemoveMinimapPin();
            UnregisterInventoryCallback();
            if (_visEquip != null)
                _companionVisEquips.Remove(_visEquip);
        }

        private void QueueEquip(ItemDrop.ItemData item)
        {
            if (item == null) return;
            _equipQueue.Add(item);
        }

        private void UpdateEquipQueue(float dt)
        {
            if (_equipQueue.Count == 0)
            {
                if (_equipAnimActive)
                {
                    if (_zanim != null && CanWearArmor()) _zanim.SetBool("equipping", false);
                    _equipAnimActive = false;
                }
                return;
            }

            // Validate front of queue — item may have been removed from inventory
            var inv = _humanoid?.GetInventory();
            while (_equipQueue.Count > 0 && (inv == null || !inv.ContainsItem(_equipQueue[0])))
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Equip queue: skipping \"{_equipQueue[0]?.m_shared?.m_name ?? "?"}\" — no longer in inventory");
                _equipQueue.RemoveAt(0);
            }
            if (_equipQueue.Count == 0) return;

            // Start animation if not playing
            if (!_equipAnimActive)
            {
                _equipAnimActive = true;
                if (_zanim != null && CanWearArmor()) _zanim.SetBool("equipping", true);
                float dur = _equipQueue[0].m_shared.m_equipDuration;
                _equipTimer = dur > 0f ? dur : 0.1f;
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Equip queue: started \"{_equipQueue[0].m_shared.m_name}\" " +
                    $"duration={_equipTimer:F1}s ({_equipQueue.Count} items queued)");
                return;
            }

            _equipTimer -= dt;
            if (_equipTimer > 0f) return;

            // Timer expired — actually equip the item
            var item = _equipQueue[0];
            _equipQueue.RemoveAt(0);

            _equipping = true;
            try
            {
                _humanoid.EquipItem(item, true);
                item.m_shared.m_equipEffect.Create(transform.position, Quaternion.identity);
            }
            finally { _equipping = false; }

            CompanionsPlugin.Log.LogDebug(
                $"[Setup] Equip queue: equipped \"{item.m_shared.m_name}\" ({_equipQueue.Count} remaining)");

            // Set timer for next item or stop
            if (_equipQueue.Count > 0)
            {
                float dur = _equipQueue[0].m_shared.m_equipDuration;
                _equipTimer = dur > 0f ? dur : 0.1f;
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Equip queue: next \"{_equipQueue[0].m_shared.m_name}\" duration={_equipTimer:F1}s");
            }
            else
            {
                if (_zanim != null && CanWearArmor()) _zanim.SetBool("equipping", false);
                _equipAnimActive = false;
            }
        }

        /// <summary>Cancel any pending equip queue (used when combat or harvest takes over).</summary>
        internal void ClearEquipQueue()
        {
            if (_equipQueue.Count > 0)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Equip queue cleared ({_equipQueue.Count} items dropped)");
                _equipQueue.Clear();
            }
            if (_equipAnimActive)
            {
                if (_zanim != null && CanWearArmor()) _zanim.SetBool("equipping", false);
                _equipAnimActive = false;
            }
            _equipTimer = 0f;
        }

        private void OnInventoryChanged()
        {
            if (_equipping) return;
            if (_humanoid == null) return;
            bool owner = _nview != null && _nview.IsOwner();
            _equipping = true;
            try
            {
                UnequipMissingEquippedItems();
                if (owner && !SuppressAutoEquip)
                {
                    // Throttle: during rapid pickup the callback fires many times.
                    // Defer the expensive full scan if on cooldown.
                    if (_autoEquipCooldown > 0f)
                    {
                        _autoEquipPending = true;
                        CompanionsPlugin.Log.LogDebug(
                            $"[Setup] OnInventoryChanged — deferred (cooldown={_autoEquipCooldown:F2}s)");
                    }
                    else
                    {
                        CompanionsPlugin.Log.LogDebug("[Setup] OnInventoryChanged — auto-equip triggered");
                        AutoEquipBest();
                        _autoEquipCooldown = AutoEquipMinInterval;
                    }
                }
            }
            finally { _equipping = false; }
        }

        internal void SyncEquipmentToInventory()
        {
            if (_humanoid == null || _nview == null || !_nview.IsOwner()) return;
            CompanionsPlugin.Log.LogDebug(
                $"[Setup] SyncEquipmentToInventory — SuppressAutoEquip={SuppressAutoEquip}");
            UnequipMissingEquippedItems();
            if (!SuppressAutoEquip) AutoEquipBest();
        }

        private void AutoEquipBest()
        {
            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            // Clear any pending equip queue from a previous run
            ClearEquipQueue();

            var items = inv.GetAllItems();

            // Find best item per slot by damage/armor value
            ItemDrop.ItemData bestRight    = null;  float bestRightDmg    = 0f;
            ItemDrop.ItemData best2H       = null;  float best2HDmg       = 0f;
            ItemDrop.ItemData bestTool     = null;  float bestToolDmg     = 0f;
            ItemDrop.ItemData bestShield   = null;  float bestShieldBlock  = 0f;
            ItemDrop.ItemData bestChest    = null;  float bestChestArmor   = 0f;
            ItemDrop.ItemData bestLegs     = null;  float bestLegsArmor    = 0f;
            ItemDrop.ItemData bestHelmet   = null;  float bestHelmetArmor  = 0f;
            ItemDrop.ItemData bestShoulder = null;  float bestShoulderArmor = 0f;
            ItemDrop.ItemData bestUtility  = null;  float bestUtilityArmor = 0f;
            ItemDrop.ItemData bestTrinket  = null;  float bestTrinketArmor = 0f;

            foreach (var item in items)
            {
                if (item == null || item.m_shared == null) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;

                var type = item.m_shared.m_itemType;
                float dmg = item.GetDamage().GetTotalDamage();
                float armor = item.GetArmor();

                switch (type)
                {
                    case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                    case ItemDrop.ItemData.ItemType.Torch:
                        if (dmg > bestRightDmg)
                        {
                            bestRight = item;
                            bestRightDmg = dmg;
                        }
                        break;
                    case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                    case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                        // Pickaxes are TwoHandedWeapon — exclude from combat weapons
                        if (item.GetDamage().m_pickaxe > 0f)
                        {
                            if (dmg > bestToolDmg)
                            {
                                bestTool = item;
                                bestToolDmg = dmg;
                            }
                            break;
                        }
                        if (dmg > best2HDmg)
                        {
                            best2H = item;
                            best2HDmg = dmg;
                        }
                        break;
                    case ItemDrop.ItemData.ItemType.Bow:
                        // Bows are managed by CombatController — skip for auto-equip
                        break;
                    case ItemDrop.ItemData.ItemType.Tool:
                        if (dmg > bestToolDmg)
                        {
                            bestTool = item;
                            bestToolDmg = dmg;
                        }
                        break;
                    case ItemDrop.ItemData.ItemType.Shield:
                        float block = item.m_shared.m_blockPower;
                        if (block > bestShieldBlock)
                        {
                            bestShield = item;
                            bestShieldBlock = block;
                        }
                        break;
                    case ItemDrop.ItemData.ItemType.Chest:
                        if (armor > bestChestArmor)   { bestChest    = item; bestChestArmor   = armor; }
                        break;
                    case ItemDrop.ItemData.ItemType.Legs:
                        if (armor > bestLegsArmor)    { bestLegs     = item; bestLegsArmor    = armor; }
                        break;
                    case ItemDrop.ItemData.ItemType.Helmet:
                        if (armor > bestHelmetArmor)  { bestHelmet   = item; bestHelmetArmor  = armor; }
                        break;
                    case ItemDrop.ItemData.ItemType.Shoulder:
                        if (armor > bestShoulderArmor) { bestShoulder = item; bestShoulderArmor = armor; }
                        break;
                    case ItemDrop.ItemData.ItemType.Utility:
                        if (armor > bestUtilityArmor) { bestUtility  = item; bestUtilityArmor = armor; }
                        break;
                    case ItemDrop.ItemData.ItemType.Trinket:
                        if (armor > bestTrinketArmor) { bestTrinket = item; bestTrinketArmor = armor; }
                        break;
                }

                // Match ExtraSlots slot validators so companion auto-equip follows the
                // same slot rules as the player (including modded/customized slot rules).
                if (ExtraSlotsCompat.IsLoaded)
                {
                    if (ExtraSlotsCompat.ItemFitsSlot("Chest", item) && armor > bestChestArmor)
                    {
                        bestChest = item;
                        bestChestArmor = armor;
                    }

                    if (ExtraSlotsCompat.ItemFitsSlot("Legs", item) && armor > bestLegsArmor)
                    {
                        bestLegs = item;
                        bestLegsArmor = armor;
                    }

                    if (ExtraSlotsCompat.ItemFitsSlot("Helmet", item) && armor > bestHelmetArmor)
                    {
                        bestHelmet = item;
                        bestHelmetArmor = armor;
                    }

                    if (ExtraSlotsCompat.ItemFitsSlot("Shoulder", item) && armor > bestShoulderArmor)
                    {
                        bestShoulder = item;
                        bestShoulderArmor = armor;
                    }

                    if (ExtraSlotsCompat.ItemFitsSlot("Utility", item) && armor > bestUtilityArmor)
                    {
                        bestUtility = item;
                        bestUtilityArmor = armor;
                    }

                    if (ExtraSlotsCompat.ItemFitsSlot("Trinket", item) && armor > bestTrinketArmor)
                    {
                        bestTrinket = item;
                        bestTrinketArmor = armor;
                    }
                }
            }

            // Log what AutoEquipBest selected
            CompanionsPlugin.Log.LogDebug(
                $"[Setup] AutoEquipBest — 1H=\"{bestRight?.m_shared?.m_name ?? "none"}\"({bestRightDmg:F0}) " +
                $"2H=\"{best2H?.m_shared?.m_name ?? "none"}\"({best2HDmg:F0}) " +
                $"shield=\"{bestShield?.m_shared?.m_name ?? "none"}\"(block={bestShieldBlock:F0}) " +
                $"helm=\"{bestHelmet?.m_shared?.m_name ?? "none"}\" " +
                $"chest=\"{bestChest?.m_shared?.m_name ?? "none"}\" " +
                $"legs=\"{bestLegs?.m_shared?.m_name ?? "none"}\" " +
                $"trinket=\"{bestTrinket?.m_shared?.m_name ?? "none"}\""
            );

            // Decide weapons: prefer 2H if it's stronger than 1H+shield combined
            var curRight = GetEquipSlot(_rightItemField);
            var curLeft  = GetEquipSlot(_leftItemField);

            bool use2H = best2H != null && (bestRight == null || best2HDmg >= bestRightDmg);
            var desiredRight = use2H ? best2H : bestRight;
            // Tools (pickaxes) are never auto-equipped as weapons — HarvestController manages them

            CompanionsPlugin.Log.LogDebug(
                $"[Setup] Weapon decision: use2H={use2H} " +
                $"desired=\"{desiredRight?.m_shared?.m_name ?? "none"}\" " +
                $"current=\"{curRight?.m_shared?.m_name ?? "none"}\" " +
                $"needsSwap={desiredRight != null && curRight != desiredRight}");

            if (desiredRight != null && curRight != desiredRight)
            {
                if (curRight != null)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Setup] Unequip right: \"{curRight.m_shared?.m_name ?? "?"}\"");
                    _humanoid.UnequipItem(curRight, false);
                }

                bool desiredIs2H = IsTwoHandedWeapon(desiredRight);
                if (desiredIs2H && curLeft != null)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Setup] Unequip left for 2H: \"{curLeft.m_shared?.m_name ?? "?"}\"");
                    _humanoid.UnequipItem(curLeft, false);
                    curLeft = null;
                }

                QueueEquip(desiredRight);
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Queued right: \"{desiredRight.m_shared?.m_name ?? "?"}\"");
                curRight = desiredRight;
            }

            bool rightIs2H = curRight != null && IsTwoHandedWeapon(curRight);

            // Shield: only if left hand is free and no 2H in right
            if (!rightIs2H && bestShield != null)
            {
                if (curLeft != bestShield)
                {
                    if (curLeft != null)
                    {
                        CompanionsPlugin.Log.LogDebug(
                            $"[Setup] Unequip left for shield swap: \"{curLeft.m_shared?.m_name ?? "?"}\"");
                        _humanoid.UnequipItem(curLeft, false);
                    }
                    QueueEquip(bestShield);
                    CompanionsPlugin.Log.LogDebug(
                        $"[Setup] Queued shield: \"{bestShield.m_shared?.m_name ?? "?"}\"");
                }
            }
            else if (rightIs2H && curLeft != null)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Unequip left (2H in right): \"{curLeft.m_shared?.m_name ?? "?"}\"");
                _humanoid.UnequipItem(curLeft, false);
            }

            // Armor slots: Dverger companions cannot wear armor — skip armor slots entirely.
            if (CanWearArmor())
            {
                EquipBestArmorSlot(_chestItemField, bestChest);
                EquipBestArmorSlot(_legItemField, bestLegs);
                EquipBestArmorSlot(_helmetItemField, bestHelmet);
                EquipBestArmorSlot(_shoulderItemField, bestShoulder);
            }

            // Utility & trinket — all companions can use these regardless of CanWearArmor
            EquipBestArmorSlot(_utilityItemField, bestUtility);
            EquipBestArmorSlot(_trinketItemField, bestTrinket);

            // Extra utility slots (ExtraSlots mod compatibility)
            AutoEquipExtraUtilities(items);
        }

        private void AutoEquipExtraUtilities(List<ItemDrop.ItemData> items)
        {
            if (_extraUtilities == null) return;

            ExtraSlotsCompat.RefreshSlotsMetadata();
            var vanillaUtility = GetEquipSlot(_utilityItemField);

            var slotIds = new string[_extraUtilities.Length];
            if (ExtraSlotsCompat.IsLoaded)
            {
                var defs = ExtraSlotsCompat.GetActiveEquipmentSlots();
                for (int i = 0; i < defs.Count; i++)
                {
                    var def = defs[i];
                    if (!ExtraSlotsCompat.TryGetExtraUtilityIndex(def.Id, out int slotIdx)) continue;
                    if (slotIdx < 0 || slotIdx >= slotIds.Length) continue;
                    slotIds[slotIdx] = def.Id;
                }
            }

            // Collect all items that fit at least one extra utility slot.
            var candidates = new List<ItemDrop.ItemData>();
            foreach (var item in items)
            {
                if (item?.m_shared == null) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;
                if (item == vanillaUtility) continue;

                bool fitsAnySlot = false;
                for (int slotIdx = 0; slotIdx < slotIds.Length; slotIdx++)
                {
                    string slotId = slotIds[slotIdx];
                    if (!string.IsNullOrEmpty(slotId))
                    {
                        if (ExtraSlotsCompat.ItemFitsSlot(slotId, item))
                        {
                            fitsAnySlot = true;
                            break;
                        }
                        continue;
                    }

                    // Fallback when dynamic slot metadata is unavailable.
                    if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Utility)
                    {
                        fitsAnySlot = true;
                        break;
                    }
                }

                if (fitsAnySlot) candidates.Add(item);
            }

            // Sort by armor descending (best protection first).
            candidates.Sort((a, b) => b.GetArmor().CompareTo(a.GetArmor()));

            bool changed = false;
            for (int i = 0; i < _extraUtilities.Length; i++)
            {
                var old = _extraUtilities[i];
                var desired = default(ItemDrop.ItemData);

                for (int c = 0; c < candidates.Count; c++)
                {
                    var item = candidates[c];
                    if (item == null) continue;

                    string slotId = slotIds[i];
                    bool fits = !string.IsNullOrEmpty(slotId)
                        ? ExtraSlotsCompat.ItemFitsSlot(slotId, item)
                        : item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Utility;

                    if (!fits) continue;

                    desired = item;
                    candidates.RemoveAt(c);
                    break;
                }

                if (old == desired) continue;

                // Unequip old
                if (old != null)
                {
                    UnequipExtraUtility(i);
                    changed = true;
                }

                // Equip new
                if (desired != null)
                {
                    EquipExtraUtility(desired, i);
                    changed = true;
                }
            }

            if (changed)
            {
                ExtraSlotsPatches.CallSetupEquipment(_humanoid);
                int equipped = 0;
                for (int i = 0; i < _extraUtilities.Length; i++)
                    if (_extraUtilities[i] != null) equipped++;
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Extra utility slots: {equipped}/{_extraUtilities.Length} filled");
            }
        }

        internal ItemDrop.ItemData GetEquipSlot(FieldInfo field)
        {
            return field?.GetValue(_humanoid) as ItemDrop.ItemData;
        }

        private void EquipBestArmorSlot(FieldInfo slotField, ItemDrop.ItemData bestItem)
        {
            if (_humanoid == null || slotField == null || bestItem == null) return;

            var equipped = GetEquipSlot(slotField);
            if (equipped == bestItem) return;

            if (equipped != null)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Armor swap {slotField.Name}: " +
                    $"\"{equipped.m_shared?.m_name ?? "?"}\" → \"{bestItem.m_shared?.m_name ?? "?"}\"");
                _humanoid.UnequipItem(equipped, false);
                RestoreFromExtraSlotsGridPosition(equipped, _humanoid.GetInventory());
            }
            else
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Armor queued {slotField.Name}: \"{bestItem.m_shared?.m_name ?? "?"}\"");
            }

            QueueEquip(bestItem);
            string slotId = GetSlotIdForVanillaField(slotField);
            if (!string.IsNullOrEmpty(slotId))
                TryMoveIntoExtraSlotsGridPosition(slotId, bestItem, _humanoid.GetInventory());
        }

        private static bool IsTwoHandedWeapon(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            var type = item.m_shared.m_itemType;
            return type == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                   type == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                   type == ItemDrop.ItemData.ItemType.Bow;
        }

        /// <summary>Sum of armor from all equipped armor pieces.</summary>
        // ── Follow toggle accessor ──────────────────────────────────────
        internal bool GetFollow() => _nview?.GetZDO()?.GetBool(FollowHash, true) ?? true;
        internal void SetFollow(bool v) => _nview?.GetZDO()?.Set(FollowHash, v);

        // ── Wander accessor ──────────────────────────────────────────────

        internal bool GetWander() => _nview?.GetZDO()?.GetBool(WanderHash, false) ?? false;

        internal void SetWander(bool v) => _nview?.GetZDO()?.Set(WanderHash, v);

        // ── Combat stance accessor ──────────────────────────────────────────

        internal int GetCombatStance()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return StanceBalanced;
            int v = zdo.GetInt(CombatStanceHash, StanceBalanced);
            return (v < StanceBalanced || v > StanceRanged) ? StanceBalanced : v;
        }

        internal void SetCombatStance(int stance)
        {
            if (stance < StanceBalanced || stance > StanceRanged) stance = StanceBalanced;
            _nview?.GetZDO()?.Set(CombatStanceHash, stance);
        }

        // ── Commandable accessor ─────────────────────────────────────────

        internal bool GetIsCommandable()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return true;
            return zdo.GetInt(IsCommandableHash, 1) != 0;
        }

        internal void SetIsCommandable(bool value)
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;
            zdo.Set(IsCommandableHash, value ? 1 : 0);
        }

        // ── StayHome accessors ────────────────────────────────────────────
        internal bool GetStayHome()
        {
            return _nview?.GetZDO()?.GetBool(StayHomeHash, false) ?? false;
        }

        internal void SetStayHome(bool v)
        {
            _nview?.GetZDO()?.Set(StayHomeHash, v);
        }

        internal Vector3 GetHomePosition()
        {
            return _nview?.GetZDO()?.GetVec3(HomePosHash, Vector3.zero) ?? Vector3.zero;
        }

        internal void SetHomePosition(Vector3 p)
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;
            zdo.Set(HomePosHash, p);
            zdo.Set(HomePosSetHash, true);
        }

        internal bool HasHomePosition()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return false;
            // New format: explicit bool key
            if (zdo.GetBool(HomePosSetHash, false)) return true;
            // Backward compat: old saves only have the vector, no bool key.
            // Treat non-zero HC_HomePos as "set" and migrate.
            Vector3 pos = zdo.GetVec3(HomePosHash, Vector3.zero);
            if (pos != Vector3.zero)
            {
                zdo.Set(HomePosSetHash, true);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns false for Dverger companions — they cannot equip armor.
        /// Detected via the ZDO prefab hash.
        /// </summary>
        internal bool CanWearArmor()
        {
            if (_nview == null || _nview.GetZDO() == null) return true;
            return !CompanionTierData.IsDvergerVariant(_nview.GetZDO().GetPrefab());
        }

        /// <summary>
        /// Assign a formation slot to this companion by scanning all owned
        /// companions and returning the first unused slot 0..7.
        /// </summary>
        internal int AssignFormationSlot()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return 0;

            int existing = zdo.GetInt(FormationSlotHash, -1);
            if (existing >= 0) return existing;

            // Collect used slots from other companions owned by the same player
            var used = new HashSet<int>();
            var player = Player.m_localPlayer;
            if (player == null) return 0;
            string localId = player.GetPlayerID().ToString();

            foreach (var setup in FindObjectsByType<CompanionSetup>(FindObjectsSortMode.None))
            {
                if (setup == this) continue;
                var otherZdo = setup._nview?.GetZDO();
                if (otherZdo == null) continue;
                if (otherZdo.GetString(OwnerHash, "") != localId) continue;
                int slot = otherZdo.GetInt(FormationSlotHash, -1);
                if (slot >= 0) used.Add(slot);
            }

            for (int i = 0; i < 8; i++)
            {
                if (!used.Contains(i))
                {
                    zdo.Set(FormationSlotHash, i);
                    return i;
                }
            }
            zdo.Set(FormationSlotHash, 0);
            return 0;
        }

        internal float GetTotalArmor()
        {
            float armor = 0f;
            var chest    = GetEquipSlot(_chestItemField);
            var legs     = GetEquipSlot(_legItemField);
            var helmet   = GetEquipSlot(_helmetItemField);
            var shoulder = GetEquipSlot(_shoulderItemField);
            var trinket  = GetEquipSlot(_trinketItemField);
            if (chest    != null) armor += chest.GetArmor();
            if (legs     != null) armor += legs.GetArmor();
            if (helmet   != null) armor += helmet.GetArmor();
            if (shoulder != null) armor += shoulder.GetArmor();
            if (trinket  != null) armor += trinket.GetArmor();

            // Include extra utility slot armor (ExtraSlots compat)
            if (_extraUtilities != null)
            {
                for (int i = 0; i < _extraUtilities.Length; i++)
                    if (_extraUtilities[i] != null)
                        armor += _extraUtilities[i].GetArmor();
            }

            return armor;
        }

        private void UnequipIfMissing(List<ItemDrop.ItemData> items, ItemDrop.ItemData equipped)
        {
            if (equipped == null) return;
            if (!items.Contains(equipped))
                _humanoid.UnequipItem(equipped, false);
        }

        private void UnequipMissingEquippedItems()
        {
            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            var items = inv.GetAllItems();
            UnequipIfMissing(items, GetEquipSlot(_rightItemField));
            UnequipIfMissing(items, GetEquipSlot(_leftItemField));
            UnequipIfMissing(items, GetEquipSlot(_chestItemField));
            UnequipIfMissing(items, GetEquipSlot(_legItemField));
            UnequipIfMissing(items, GetEquipSlot(_helmetItemField));
            UnequipIfMissing(items, GetEquipSlot(_shoulderItemField));
            UnequipIfMissing(items, GetEquipSlot(_utilityItemField));
            UnequipIfMissing(items, GetEquipSlot(_trinketItemField));
            UnequipMissingExtraUtilities();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Extra Utility Slots (ExtraSlots mod compatibility)
        // ══════════════════════════════════════════════════════════════════════

        private void InitExtraUtilities()
        {
            if (!ExtraSlotsCompat.IsLoaded) return;
            ExtraSlotsCompat.RefreshSlotsMetadata();
            ExtraSlotsCompat.RefreshSlotCount();
            int count = ExtraSlotsCompat.ExtraUtilitySlotCount;
            if (count <= 0) return;
            _extraUtilities = new ItemDrop.ItemData[count];
        }

        private void RestoreExtraUtilities()
        {
            if (_extraUtilities == null || _humanoid == null) return;

            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            bool restored = false;
            foreach (var item in inv.GetAllItems())
            {
                if (item == null || item.m_customData == null) continue;
                if (!item.m_customData.TryGetValue(ExtraUtilitySlotKey, out string slotStr)) continue;
                if (!int.TryParse(slotStr, out int slot)) continue;
                if (slot < 0 || slot >= _extraUtilities.Length) continue;

                // Prevent slot collision — skip if slot already occupied
                if (_extraUtilities[slot] != null)
                {
                    CompanionsPlugin.Log.LogWarning(
                        $"[Setup] Extra utility slot {slot} collision — " +
                        $"\"{item.m_shared?.m_name ?? "?"}\" conflicts with " +
                        $"\"{_extraUtilities[slot].m_shared?.m_name ?? "?"}\" — clearing duplicate");
                    item.m_equipped = false;
                    item.m_customData.Remove(ExtraUtilitySlotKey);
                    continue;
                }

                _extraUtilities[slot] = item;
                item.m_equipped = true;
                ExtraSlotsCompat.SetExtraUtility(_humanoid, slot, item);
                string slotId = $"ExtraUtility{slot + 1}";
                TryMoveIntoExtraSlotsGridPosition(slotId, item, inv);
                restored = true;

                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Restored extra utility slot {slot}: \"{item.m_shared?.m_name ?? "?"}\"");
            }

            if (restored)
                ExtraSlotsPatches.CallSetupEquipment(_humanoid);
        }

        private void EquipExtraUtility(ItemDrop.ItemData item, int slot)
        {
            if (_extraUtilities == null || item == null) return;
            if (slot < 0 || slot >= _extraUtilities.Length) return;

            item.m_equipped = true;
            if (item.m_customData == null)
                item.m_customData = new System.Collections.Generic.Dictionary<string, string>();
            item.m_customData[ExtraUtilitySlotKey] = slot.ToString();
            _extraUtilities[slot] = item;
            ExtraSlotsCompat.SetExtraUtility(_humanoid, slot, item);
            string slotId = $"ExtraUtility{slot + 1}";
            TryMoveIntoExtraSlotsGridPosition(slotId, item, _humanoid?.GetInventory());

            CompanionsPlugin.Log.LogDebug(
                $"[Setup] Equipped extra utility slot {slot}: \"{item.m_shared?.m_name ?? "?"}\"");
        }

        private void UnequipExtraUtility(int slot)
        {
            if (_extraUtilities == null) return;
            if (slot < 0 || slot >= _extraUtilities.Length) return;

            var item = _extraUtilities[slot];
            if (item == null) return;

            item.m_equipped = false;
            item.m_customData?.Remove(ExtraUtilitySlotKey);
            _extraUtilities[slot] = null;
            ExtraSlotsCompat.SetExtraUtility(_humanoid, slot, null);
            RestoreFromExtraSlotsGridPosition(item, _humanoid?.GetInventory());

            CompanionsPlugin.Log.LogDebug(
                $"[Setup] Unequipped extra utility slot {slot}: \"{item.m_shared?.m_name ?? "?"}\"");
        }

        private void ClearAllExtraUtilities()
        {
            if (_extraUtilities == null) return;
            for (int i = 0; i < _extraUtilities.Length; i++)
            {
                if (_extraUtilities[i] != null)
                {
                    _extraUtilities[i].m_equipped = false;
                    _extraUtilities[i].m_customData?.Remove(ExtraUtilitySlotKey);
                    _extraUtilities[i].m_customData?.Remove(ExtraSlotPrevPosXKey);
                    _extraUtilities[i].m_customData?.Remove(ExtraSlotPrevPosYKey);
                    _extraUtilities[i] = null;
                    ExtraSlotsCompat.SetExtraUtility(_humanoid, i, null);
                }
            }
        }

        private void UnequipMissingExtraUtilities()
        {
            if (_extraUtilities == null) return;
            var inv = _humanoid?.GetInventory();
            if (inv == null) return;
            var items = inv.GetAllItems();
            bool changed = false;
            for (int i = 0; i < _extraUtilities.Length; i++)
            {
                var item = _extraUtilities[i];
                if (item != null && !items.Contains(item))
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Setup] Extra utility slot {i} missing from inventory — clearing " +
                        $"\"{item.m_shared?.m_name ?? "?"}\"");
                    item.m_equipped = false;
                    item.m_customData?.Remove(ExtraUtilitySlotKey);
                    item.m_customData?.Remove(ExtraSlotPrevPosXKey);
                    item.m_customData?.Remove(ExtraSlotPrevPosYKey);
                    _extraUtilities[i] = null;
                    ExtraSlotsCompat.SetExtraUtility(_humanoid, i, null);
                    changed = true;
                }
            }
            if (changed) ExtraSlotsPatches.CallSetupEquipment(_humanoid);
        }
    }
}
