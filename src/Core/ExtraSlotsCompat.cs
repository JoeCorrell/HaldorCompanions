using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Soft dependency on the ExtraSlots mod (shudnal.ExtraSlots).
    /// Detects whether ExtraSlots is loaded, reads dynamic slot metadata, and
    /// exposes reflection helpers needed by companion equipment integration.
    /// </summary>
    internal static class ExtraSlotsCompat
    {
        internal sealed class SlotDefinition
        {
            internal string Id;
            internal string Name;
            internal bool IsActive;
            internal bool IsEquipmentSlot;
            internal Vector2 Position;
            internal Vector2i GridPosition;
            internal object SlotObject;
        }

        internal static bool IsLoaded { get; private set; }
        internal static int ExtraUtilitySlotCount { get; private set; }
        internal static Vector2 EquipmentPanelOffset { get; private set; } = Vector2.zero;

        private static MethodInfo _getExtraUtility;
        private static MethodInfo _setExtraUtility;

        private static object _extraUtilityConfigEntry;
        private static object _equipmentPanelOffsetConfigEntry;
        private static PropertyInfo _configValue;

        private static MethodInfo _apiGetEquipmentSlots;
        private static MethodInfo _apiGetExtraSlots;
        private static Type _equipmentPanelType;
        private static FieldInfo _quickSlotSpriteField;
        private static FieldInfo _ammoSlotSpriteField;
        private static FieldInfo _miscSlotSpriteField;

        private static PropertyInfo _slotIdProp;
        private static PropertyInfo _slotNameProp;
        private static PropertyInfo _slotIsActiveProp;
        private static PropertyInfo _slotIsEquipmentSlotProp;
        private static PropertyInfo _slotPositionProp;
        private static PropertyInfo _slotGridPositionProp;
        private static MethodInfo _slotItemFitsMethod;

        private static readonly List<SlotDefinition> _activeEquipmentSlots = new List<SlotDefinition>();
        private static readonly List<SlotDefinition> _activeSlots = new List<SlotDefinition>();

        internal static void Init()
        {
            IsLoaded = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("shudnal.ExtraSlots");
            if (!IsLoaded)
            {
                CompanionsPlugin.Log.LogInfo("[ExtraSlotsCompat] ExtraSlots not detected - companion extra utility slots disabled");
                return;
            }

            var pluginInfo = BepInEx.Bootstrap.Chainloader.PluginInfos["shudnal.ExtraSlots"];
            var asm = pluginInfo.Instance.GetType().Assembly;

            // Cache HumanoidExtension.GetExtraUtility(Humanoid, int) and SetExtraUtility(Humanoid, int, ItemData)
            var extType = asm.GetType("ExtraSlots.HumanoidExtension");
            if (extType != null)
            {
                _getExtraUtility = extType.GetMethod("GetExtraUtility", BindingFlags.Public | BindingFlags.Static);
                _setExtraUtility = extType.GetMethod("SetExtraUtility", BindingFlags.Public | BindingFlags.Static);
            }

            // Cache config entries used by companion UI/slot allocation.
            var pluginType = asm.GetType("ExtraSlots.ExtraSlots");
            if (pluginType != null)
            {
                var utilityField = pluginType.GetField("extraUtilitySlotsAmount", BindingFlags.Public | BindingFlags.Static);
                _extraUtilityConfigEntry = utilityField?.GetValue(null);
                _configValue = _extraUtilityConfigEntry?.GetType().GetProperty("Value");

                var panelOffsetField = pluginType.GetField("equipmentPanelOffset", BindingFlags.Public | BindingFlags.Static);
                _equipmentPanelOffsetConfigEntry = panelOffsetField?.GetValue(null);
            }

            // Cache API methods so we can mirror active slot layout/order.
            var apiType = asm.GetType("ExtraSlots.API");
            if (apiType != null)
            {
                _apiGetEquipmentSlots = apiType.GetMethod("GetEquipmentSlots", BindingFlags.Public | BindingFlags.Static);
                _apiGetExtraSlots = apiType.GetMethod("GetExtraSlots", BindingFlags.Public | BindingFlags.Static);
            }
            _equipmentPanelType = asm.GetType("ExtraSlots.EquipmentPanel");
            if (_equipmentPanelType != null)
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                _quickSlotSpriteField = _equipmentPanelType.GetField("quickSlot", flags);
                _ammoSlotSpriteField = _equipmentPanelType.GetField("ammoSlot", flags);
                _miscSlotSpriteField = _equipmentPanelType.GetField("miscSlot", flags);
            }

            RefreshSlotsMetadata();
            RefreshSlotCount();

            CompanionsPlugin.Log.LogInfo(
                $"[ExtraSlotsCompat] ExtraSlots detected - {ExtraUtilitySlotCount} extra utility slots enabled " +
                $"(getMethod={_getExtraUtility != null} setMethod={_setExtraUtility != null})");
        }

        internal static void RefreshSlotCount()
        {
            if (!IsLoaded)
            {
                ExtraUtilitySlotCount = 0;
                return;
            }

            // Prefer active slot metadata so progression/custom slot states match the player.
            int derived = GetRequiredExtraUtilitySlotsCount();
            if (derived > 0)
            {
                ExtraUtilitySlotCount = derived;
                return;
            }

            if (_extraUtilityConfigEntry == null)
            {
                ExtraUtilitySlotCount = 0;
                return;
            }

            ExtraUtilitySlotCount = (int)(_configValue?.GetValue(_extraUtilityConfigEntry) ?? 0);
        }

        internal static ItemDrop.ItemData GetExtraUtility(Humanoid h, int index)
        {
            if (_getExtraUtility == null) return null;
            return _getExtraUtility.Invoke(null, new object[] { h, index }) as ItemDrop.ItemData;
        }

        internal static void SetExtraUtility(Humanoid h, int index, ItemDrop.ItemData item)
        {
            _setExtraUtility?.Invoke(null, new object[] { h, index, item });
        }

        internal static IReadOnlyList<SlotDefinition> GetActiveEquipmentSlots()
        {
            return _activeEquipmentSlots;
        }

        internal static IReadOnlyList<SlotDefinition> GetActiveSlots()
        {
            return _activeSlots;
        }

        internal static void RefreshSlotsMetadata()
        {
            _activeSlots.Clear();
            _activeEquipmentSlots.Clear();
            EquipmentPanelOffset = GetConfigVector2(_equipmentPanelOffsetConfigEntry);

            if (!IsLoaded) return;

            IEnumerable rawSlots = null;
            try
            {
                if (_apiGetExtraSlots != null)
                    rawSlots = _apiGetExtraSlots.Invoke(null, null) as IEnumerable;
                else if (_apiGetEquipmentSlots != null)
                    rawSlots = _apiGetEquipmentSlots.Invoke(null, null) as IEnumerable;
            }
            catch (Exception ex)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[ExtraSlotsCompat] Failed reading slot metadata: {ex.Message}");
            }

            if (rawSlots == null) return;

            foreach (var slotObj in rawSlots)
            {
                if (slotObj == null) continue;

                var type = slotObj.GetType();
                if (_slotIdProp == null)
                {
                    _slotIdProp = type.GetProperty("ID", BindingFlags.Public | BindingFlags.Instance);
                    _slotNameProp = type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    _slotIsActiveProp = type.GetProperty("IsActive", BindingFlags.Public | BindingFlags.Instance);
                    _slotIsEquipmentSlotProp = type.GetProperty("IsEquipmentSlot", BindingFlags.Public | BindingFlags.Instance);
                    _slotPositionProp = type.GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
                    _slotGridPositionProp = type.GetProperty("GridPosition", BindingFlags.Public | BindingFlags.Instance);
                    _slotItemFitsMethod = type.GetMethod("ItemFits", BindingFlags.Public | BindingFlags.Instance);
                }

                bool isActive = (_slotIsActiveProp?.GetValue(slotObj) as bool?) ?? false;
                if (!isActive) continue;

                bool isEquipment = (_slotIsEquipmentSlotProp?.GetValue(slotObj) as bool?) ?? false;
                string id = _slotIdProp?.GetValue(slotObj) as string;
                if (string.IsNullOrEmpty(id)) continue;

                object posObj = _slotPositionProp?.GetValue(slotObj);
                Vector2 pos = posObj is Vector2 v ? v : Vector2.zero;
                object gridPosObj = _slotGridPositionProp?.GetValue(slotObj);
                Vector2i gridPos = gridPosObj is Vector2i gp ? gp : Vector2i.zero;

                var def = new SlotDefinition
                {
                    Id = id,
                    Name = _slotNameProp?.GetValue(slotObj) as string ?? id,
                    IsActive = true,
                    IsEquipmentSlot = isEquipment,
                    Position = pos,
                    GridPosition = gridPos,
                    SlotObject = slotObj
                };

                _activeSlots.Add(def);
                if (isEquipment) _activeEquipmentSlots.Add(def);
            }
        }

        internal static bool ItemFitsSlot(string slotId, ItemDrop.ItemData item)
        {
            if (!IsLoaded || string.IsNullOrEmpty(slotId) || item == null) return false;

            for (int i = 0; i < _activeSlots.Count; i++)
            {
                var slot = _activeSlots[i];
                if (!string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_slotItemFitsMethod == null || slot.SlotObject == null) return false;
                try
                {
                    return (bool)(_slotItemFitsMethod.Invoke(slot.SlotObject, new object[] { item }) ?? false);
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        internal static bool TryGetExtraUtilityIndex(string slotId, out int index)
        {
            index = -1;
            if (string.IsNullOrEmpty(slotId)) return false;
            if (!slotId.StartsWith("ExtraUtility", StringComparison.OrdinalIgnoreCase)) return false;

            string suffix = slotId.Substring("ExtraUtility".Length);
            if (!int.TryParse(suffix, out int oneBased)) return false;
            if (oneBased <= 0) return false;

            index = oneBased - 1;
            return true;
        }

        internal static int GetRequiredExtraUtilitySlotsCount()
        {
            int maxIndex = -1;
            for (int i = 0; i < _activeEquipmentSlots.Count; i++)
            {
                if (TryGetExtraUtilityIndex(_activeEquipmentSlots[i].Id, out int idx))
                    maxIndex = Math.Max(maxIndex, idx);
            }

            return maxIndex + 1;
        }

        internal static bool TryGetSlotGridPosition(string slotId, out Vector2i gridPos)
        {
            gridPos = Vector2i.zero;
            if (string.IsNullOrEmpty(slotId)) return false;

            for (int i = 0; i < _activeSlots.Count; i++)
            {
                var slot = _activeSlots[i];
                if (!string.Equals(slot.Id, slotId, StringComparison.OrdinalIgnoreCase))
                    continue;

                gridPos = slot.GridPosition;
                return true;
            }

            return false;
        }

        internal static bool IsAnySlotGridPosition(Vector2i pos)
        {
            for (int i = 0; i < _activeSlots.Count; i++)
            {
                if (_activeSlots[i].GridPosition == pos)
                    return true;
            }

            return false;
        }

        internal static Sprite GetSlotHintSprite(string slotId)
        {
            if (!IsLoaded || string.IsNullOrEmpty(slotId)) return null;

            if (slotId.StartsWith("Quick", StringComparison.OrdinalIgnoreCase))
                return _quickSlotSpriteField?.GetValue(null) as Sprite;
            if (slotId.StartsWith("Ammo", StringComparison.OrdinalIgnoreCase))
                return _ammoSlotSpriteField?.GetValue(null) as Sprite;
            if (slotId.StartsWith("Misc", StringComparison.OrdinalIgnoreCase))
                return _miscSlotSpriteField?.GetValue(null) as Sprite;

            return null;
        }

        private static Vector2 GetConfigVector2(object configEntry)
        {
            if (configEntry == null) return Vector2.zero;
            try
            {
                object value = configEntry.GetType().GetProperty("Value")?.GetValue(configEntry);
                return value is Vector2 v ? v : Vector2.zero;
            }
            catch
            {
                return Vector2.zero;
            }
        }
    }
}
