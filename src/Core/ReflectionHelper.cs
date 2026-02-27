using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Centralized, fail-safe reflection wrapper for all protected/private field
    /// and method access. Every call is wrapped in try-catch so a game update
    /// that renames a field cannot crash the entire AI loop.
    ///
    /// Note: MonsterAI and BaseAI reflection was removed — CompanionAI provides
    /// direct access to target fields and exposes protected BaseAI methods.
    /// </summary>
    internal static class ReflectionHelper
    {
        // ── Character ────────────────────────────────────────────────────────
        private static readonly FieldInfo _blockingField =
            AccessTools.Field(typeof(Character), "m_blocking");

        // ── Humanoid ─────────────────────────────────────────────────────────
        internal static readonly FieldInfo LeftItemField =
            AccessTools.Field(typeof(Humanoid), "m_leftItem");
        internal static readonly FieldInfo RightItemField =
            AccessTools.Field(typeof(Humanoid), "m_rightItem");
        private static readonly FieldInfo _blockTimerField =
            AccessTools.Field(typeof(Humanoid), "m_blockTimer");

        // ── VisEquipment ─────────────────────────────────────────────────────
        internal static readonly MethodInfo UpdateVisualsMethod =
            AccessTools.Method(typeof(VisEquipment), "UpdateVisuals");

        // One-shot warning flags to avoid log spam
        private static bool _warnedBlocking;
        private static bool _warnedBlockTimer;

        // ══════════════════════════════════════════════════════════════════════
        //  Character.m_blocking
        // ══════════════════════════════════════════════════════════════════════

        internal static bool TrySetBlocking(Character c, bool value)
        {
            if (c == null || _blockingField == null)
            {
                WarnOnce(ref _warnedBlocking, "Character.m_blocking");
                return false;
            }
            try
            {
                _blockingField.SetValue(c, value);
                return true;
            }
            catch (Exception)
            {
                WarnOnce(ref _warnedBlocking, "Character.m_blocking");
                return false;
            }
        }

        internal static bool GetBlocking(Character c)
        {
            if (c == null || _blockingField == null) return false;
            try { return (bool)_blockingField.GetValue(c); }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Humanoid equipment
        // ══════════════════════════════════════════════════════════════════════

        internal static ItemDrop.ItemData GetLeftItem(Humanoid h)
        {
            if (h == null || LeftItemField == null) return null;
            try { return LeftItemField.GetValue(h) as ItemDrop.ItemData; }
            catch { return null; }
        }

        internal static ItemDrop.ItemData GetRightItem(Humanoid h)
        {
            if (h == null || RightItemField == null) return null;
            try { return RightItemField.GetValue(h) as ItemDrop.ItemData; }
            catch { return null; }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Humanoid.m_blockTimer
        // ══════════════════════════════════════════════════════════════════════

        internal static bool TrySetBlockTimer(Humanoid h, float value)
        {
            if (h == null || _blockTimerField == null)
            {
                WarnOnce(ref _warnedBlockTimer, "Humanoid.m_blockTimer");
                return false;
            }
            try
            {
                _blockTimerField.SetValue(h, value);
                return true;
            }
            catch (Exception)
            {
                WarnOnce(ref _warnedBlockTimer, "Humanoid.m_blockTimer");
                return false;
            }
        }

        internal static float GetBlockTimer(Humanoid h)
        {
            if (h == null || _blockTimerField == null) return -1f;
            try { return (float)_blockTimerField.GetValue(h); }
            catch { return -1f; }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  CraftingStation.m_allStations (private static)
        // ══════════════════════════════════════════════════════════════════════

        private static readonly FieldInfo _allStationsField =
            AccessTools.Field(typeof(CraftingStation), "m_allStations");

        internal static List<CraftingStation> GetAllCraftingStations()
        {
            if (_allStationsField == null) return null;
            try { return _allStationsField.GetValue(null) as List<CraftingStation>; }
            catch { return null; }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Projectile.m_owner (private)
        // ══════════════════════════════════════════════════════════════════════

        private static readonly FieldInfo _projOwnerField =
            AccessTools.Field(typeof(Projectile), "m_owner");

        internal static Character GetProjectileOwner(Projectile proj)
        {
            if (proj == null || _projOwnerField == null) return null;
            try { return _projOwnerField.GetValue(proj) as Character; }
            catch { return null; }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private static void WarnOnce(ref bool flag, string fieldName)
        {
            if (flag) return;
            flag = true;
            CompanionsPlugin.Log.LogWarning(
                $"[ReflectionHelper] Failed to access {fieldName} — " +
                "this field may have been renamed in a game update.");
        }
    }
}
