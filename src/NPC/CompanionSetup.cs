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
        private  static readonly int HomePosSetHash = StringExtensionMethods.GetStableHashCode("HC_HomePosSet");
        private  static readonly int StarterGearHash = StringExtensionMethods.GetStableHashCode("HC_StarterGear");
        // DvergerPrefabHash removed — use CompanionTierData.IsDvergerVariant() instead
        internal const int ModeFollow      = 0;
        internal const int ModeGatherWood  = 1;
        internal const int ModeGatherStone = 2;
        internal const int ModeGatherOre   = 3;
        internal const int ModeStay        = 4;

        internal const float MaxLeashDistance = 50f;
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

        private ZNetView         _nview;
        private VisEquipment     _visEquip;
        private CompanionAI      _ai;
        private Humanoid         _humanoid;
        private ZSyncAnimation   _zanim;
        private HarvestController _harvestCached;
        private CompanionRest     _restCached;
        private bool           _initialized;
        private bool           _ownerMismatchLogged;
        private bool           _uiFrozen;

        /// <summary>
        /// When true, auto-equip is suppressed (used by CompanionHarvest to keep tools equipped).
        /// </summary>
        internal bool SuppressAutoEquip { get; set; }

        private void Awake()
        {
            _nview         = GetComponent<ZNetView>();
            _visEquip      = GetComponent<VisEquipment>();
            _ai            = GetComponent<CompanionAI>();
            _humanoid      = GetComponent<Humanoid>();
            _zanim         = GetComponent<ZSyncAnimation>();
            _harvestCached = GetComponent<HarvestController>();
            _restCached    = GetComponent<CompanionRest>();

            CompanionsPlugin.Log.LogInfo(
                $"[Setup] Awake — nview={_nview != null} visEquip={_visEquip != null} " +
                $"ai={_ai != null} humanoid={_humanoid != null} name=\"{gameObject.name}\"");

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
            if (_uiFrozen)
            {
                _uiFrozen = false;
                var zdo = _nview?.GetZDO();
                if (zdo != null && _ai != null)
                {
                    int mode = zdo.GetInt(ActionModeHash, ModeFollow);
                    ApplyFollowMode(mode, force: true);
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

                if (_restCached != null && (_restCached.IsNavigating || _restCached.IsResting))
                    return; // CompanionRest is navigating to a bed/fire or resting

                if (_ai.PendingCartAttach != null || _ai.PendingMoveTarget != null ||
                    _ai.PendingDepositContainer != null)
                    return; // Navigating to a directed position

                // StayHome: don't restore follow — companion stays at home position
                if (GetStayHome() && HasHomePosition())
                    return;

                // Follow and gather modes: companion should follow player when no
                // directed navigation is active.
                if (mode == ModeFollow ||
                    (mode >= ModeGatherWood && mode <= ModeGatherOre))
                {
                    _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                    CompanionsPlugin.Log.LogDebug(
                        $"[Setup] Follow target was null — restored to player " +
                        $"(mode={mode} harvestActive={_harvestCached?.IsActive ?? false})");
                }
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

            int mode = zdo.GetInt(ActionModeHash, ModeFollow);
            CompanionsPlugin.Log.LogInfo(
                $"[Setup] Initialized — mode={mode} appearance={(string.IsNullOrEmpty(serial) ? "default" : "saved")} " +
                $"pos={transform.position:F1}");

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
            if (mode < ModeFollow || mode > ModeStay) mode = ModeFollow;

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
            CompanionsPlugin.Log.LogDebug($"[Setup] ApplyFollowMode — mode={mode} stayHome={stayHome} force={force}");

            switch (mode)
            {
                case ModeFollow:
                    if (stayHome)
                    {
                        _ai.SetFollowTarget(null);
                        _ai.SetPatrolPointAt(GetHomePosition());
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → StayHome patrol at {GetHomePosition():F1}");
                    }
                    else
                    {
                        _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → Follow player (mode={mode})");
                    }
                    break;
                case ModeGatherWood:
                case ModeGatherStone:
                case ModeGatherOre:
                    // Don't override follow target if HarvestController is actively
                    // driving movement to a resource — it sets its own follow target.
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
                    }
                    if (stayHome)
                    {
                        _ai.SetFollowTarget(null);
                        _ai.SetPatrolPointAt(GetHomePosition());
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → Gather+StayHome patrol at {GetHomePosition():F1}");
                    }
                    else
                    {
                        _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                        CompanionsPlugin.Log.LogDebug($"[Setup]   → Follow player (mode={mode})");
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
                    _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                    CompanionsPlugin.Log.LogDebug($"[Setup]   → Follow player (default fallback, mode={mode})");
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

        private void OnDestroy()
        {
            UnregisterInventoryCallback();
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
                }
            }

            // Log what AutoEquipBest selected
            CompanionsPlugin.Log.LogDebug(
                $"[Setup] AutoEquipBest — 1H=\"{bestRight?.m_shared?.m_name ?? "none"}\"({bestRightDmg:F0}) " +
                $"2H=\"{best2H?.m_shared?.m_name ?? "none"}\"({best2HDmg:F0}) " +
                $"shield=\"{bestShield?.m_shared?.m_name ?? "none"}\"(block={bestShieldBlock:F0}) " +
                $"helm=\"{bestHelmet?.m_shared?.m_name ?? "none"}\" " +
                $"chest=\"{bestChest?.m_shared?.m_name ?? "none"}\" " +
                $"legs=\"{bestLegs?.m_shared?.m_name ?? "none"}\""
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

            // Armor/accessory slots: always converge to best available piece.
            // Dverger companions cannot wear armor — skip armor slots entirely.
            if (CanWearArmor())
            {
                EquipBestArmorSlot(_chestItemField, bestChest);
                EquipBestArmorSlot(_legItemField, bestLegs);
                EquipBestArmorSlot(_helmetItemField, bestHelmet);
                EquipBestArmorSlot(_shoulderItemField, bestShoulder);
                EquipBestArmorSlot(_utilityItemField, bestUtility);
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
            }
            else
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Setup] Armor queued {slotField.Name}: \"{bestItem.m_shared?.m_name ?? "?"}\"");
            }

            QueueEquip(bestItem);
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
        // ── Wander accessor ──────────────────────────────────────────────

        internal bool GetWander() => _nview?.GetZDO()?.GetBool(WanderHash, false) ?? false;

        internal void SetWander(bool v) => _nview?.GetZDO()?.Set(WanderHash, v);

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
            if (chest    != null) armor += chest.GetArmor();
            if (legs     != null) armor += legs.GetArmor();
            if (helmet   != null) armor += helmet.GetArmor();
            if (shoulder != null) armor += shoulder.GetArmor();
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
        }
    }
}
