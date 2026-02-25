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

        private ZNetView     _nview;
        private VisEquipment _visEquip;
        private MonsterAI    _ai;
        private Humanoid     _humanoid;
        private bool         _initialized;

        /// <summary>
        /// When true, auto-equip is suppressed (used by CompanionHarvest to keep tools equipped).
        /// </summary>
        internal bool SuppressAutoEquip { get; set; }

        private void Awake()
        {
            _nview    = GetComponent<ZNetView>();
            _visEquip = GetComponent<VisEquipment>();
            _ai       = GetComponent<MonsterAI>();
            _humanoid = GetComponent<Humanoid>();
            TryInit();
        }

        private void Start()  { if (!_initialized) TryInit(); }

        private void Update()
        {
            if (!_initialized) { TryInit(); return; }

            // Ensure follow target stays set for follow/gather modes.
            // It can be lost on zone reload or player respawn.
            if (_ai != null && _ai.GetFollowTarget() == null && Player.m_localPlayer != null)
            {
                var zdo = _nview?.GetZDO();
                if (zdo == null) return;

                string owner   = zdo.GetString(OwnerHash, "");
                string localId = Player.m_localPlayer.GetPlayerID().ToString();
                if (owner != localId) return;

                int mode = zdo.GetInt(ActionModeHash, ModeFollow);
                if (mode == ModeFollow)
                    _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
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
            if (_visEquip == null) return;

            _visEquip.SetModel(a.ModelIndex);
            _visEquip.SetHairItem(string.IsNullOrEmpty(a.HairItem) ? "Hair1" : a.HairItem);
            _visEquip.SetBeardItem(a.ModelIndex == 0 ? (a.BeardItem ?? "") : "");
            _visEquip.SetSkinColor(a.SkinColor);
            _visEquip.SetHairColor(a.HairColor);
            _updateVisuals?.Invoke(_visEquip, null);

            var animator = GetComponentInChildren<Animator>(true);
            if (animator != null) animator.Update(0f);

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
            string localId = Player.m_localPlayer.GetPlayerID().ToString();
            if (owner == localId)
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
        }

        // ──────────────────────────────────────────────────────────────────────
        private void RestoreActionMode()
        {
            if (_ai == null || Player.m_localPlayer == null) return;
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            string owner   = zdo.GetString(OwnerHash, "");
            string localId = Player.m_localPlayer.GetPlayerID().ToString();
            if (owner != localId) return;

            int mode = zdo.GetInt(ActionModeHash, ModeFollow);
            switch (mode)
            {
                case ModeFollow:
                case ModeGatherWood:
                case ModeGatherStone:
                case ModeGatherOre:
                    _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                    break;
                case ModeStay:
                    _ai.SetFollowTarget(null);
                    _ai.SetPatrolPoint();
                    break;
                default:
                    _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
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

        // ── Auto-equip ──────────────────────────────────────────────────────

        private bool _equipping;

        private void RegisterInventoryCallback()
        {
            if (_humanoid == null) return;
            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            inv.m_onChanged = (Action)Delegate.Combine(inv.m_onChanged, new Action(OnInventoryChanged));
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
                if (owner && !SuppressAutoEquip) AutoEquipBest();
            }
            finally { _equipping = false; }
        }

        internal void SyncEquipmentToInventory()
        {
            if (_humanoid == null || _nview == null || !_nview.IsOwner()) return;
            UnequipMissingEquippedItems();
            if (!SuppressAutoEquip) AutoEquipBest();
        }

        private void AutoEquipBest()
        {
            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            var items = inv.GetAllItems();

            // Find best item per slot by damage/armor value
            ItemDrop.ItemData bestRight    = null;  float bestRightDmg    = 0f;
            ItemDrop.ItemData best2H       = null;  float best2HDmg       = 0f;
            ItemDrop.ItemData bestShield   = null;  float bestShieldBlock  = 0f;
            ItemDrop.ItemData bestChest    = null;  float bestChestArmor   = 0f;
            ItemDrop.ItemData bestLegs     = null;  float bestLegsArmor    = 0f;
            ItemDrop.ItemData bestHelmet   = null;  float bestHelmetArmor  = 0f;
            ItemDrop.ItemData bestShoulder = null;  float bestShoulderArmor = 0f;
            ItemDrop.ItemData bestUtility  = null;  float bestUtilityArmor = 0f;

            foreach (var item in items)
            {
                if (_humanoid.IsItemEquiped(item)) continue;

                var type = item.m_shared.m_itemType;
                float dmg = item.GetDamage().GetTotalDamage();
                float armor = item.GetArmor();

                switch (type)
                {
                    case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                    case ItemDrop.ItemData.ItemType.Torch:
                    case ItemDrop.ItemData.ItemType.Tool:
                        if (dmg > bestRightDmg)
                        {
                            bestRight = item;
                            bestRightDmg = dmg;
                        }
                        break;
                    case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                    case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                    case ItemDrop.ItemData.ItemType.Bow:
                        if (dmg > best2HDmg)
                        {
                            best2H = item;
                            best2HDmg = dmg;
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

            // Decide weapons: prefer 2H if it's stronger than 1H+shield combined
            var curRight = GetEquipSlot(_rightItemField);
            var curLeft  = GetEquipSlot(_leftItemField);

            if (curRight == null && curLeft == null)
            {
                if (best2H != null && (bestRight == null || best2HDmg >= bestRightDmg))
                    _humanoid.EquipItem(best2H, true);
                else if (bestRight != null)
                    _humanoid.EquipItem(bestRight, true);
            }
            else if (curRight == null && bestRight != null)
            {
                _humanoid.EquipItem(bestRight, true);
            }

            // Shield: only if left hand is free and no 2H in right
            if (bestShield != null && GetEquipSlot(_leftItemField) == null)
            {
                var right = GetEquipSlot(_rightItemField);
                bool rightIs2H = right != null && (
                    right.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                    right.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                    right.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow);
                if (!rightIs2H)
                    _humanoid.EquipItem(bestShield, true);
            }

            // Armor slots
            if (bestChest    != null && GetEquipSlot(_chestItemField)    == null) _humanoid.EquipItem(bestChest,    true);
            if (bestLegs     != null && GetEquipSlot(_legItemField)      == null) _humanoid.EquipItem(bestLegs,     true);
            if (bestHelmet   != null && GetEquipSlot(_helmetItemField)   == null) _humanoid.EquipItem(bestHelmet,   true);
            if (bestShoulder != null && GetEquipSlot(_shoulderItemField) == null) _humanoid.EquipItem(bestShoulder, true);
            if (bestUtility  != null && GetEquipSlot(_utilityItemField)  == null) _humanoid.EquipItem(bestUtility,  true);
        }

        internal ItemDrop.ItemData GetEquipSlot(FieldInfo field)
        {
            return field?.GetValue(_humanoid) as ItemDrop.ItemData;
        }

        /// <summary>Sum of armor from all equipped armor pieces.</summary>
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
