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

        // ── MonsterAI ────────────────────────────────────────────────────────
        private static readonly FieldInfo _targetCreatureField =
            AccessTools.Field(typeof(MonsterAI), "m_targetCreature");
        private static readonly FieldInfo _targetStaticField =
            AccessTools.Field(typeof(MonsterAI), "m_targetStatic");
        private static readonly FieldInfo _lastKnownTargetPosField =
            AccessTools.Field(typeof(MonsterAI), "m_lastKnownTargetPos");
        private static readonly FieldInfo _beenAtLastPosField =
            AccessTools.Field(typeof(MonsterAI), "m_beenAtLastPos");

        // ── BaseAI ───────────────────────────────────────────────────────────
        private static readonly MethodInfo _moveToMethod =
            AccessTools.Method(typeof(BaseAI), "MoveTo",
                new[] { typeof(float), typeof(Vector3), typeof(float), typeof(bool) });
        private static readonly MethodInfo _lookAtMethod =
            AccessTools.Method(typeof(BaseAI), "LookAt", new[] { typeof(Vector3) });
        private static readonly MethodInfo _isLookingAtMethod =
            AccessTools.Method(typeof(BaseAI), "IsLookingAt",
                new[] { typeof(Vector3), typeof(float), typeof(bool) });
        private static readonly MethodInfo _setAlertedMethod =
            AccessTools.Method(typeof(BaseAI), "SetAlerted", new[] { typeof(bool) });

        private static bool _loggedMoveToInit;

        // ── VisEquipment ─────────────────────────────────────────────────────
        internal static readonly MethodInfo UpdateVisualsMethod =
            AccessTools.Method(typeof(VisEquipment), "UpdateVisuals");

        // One-shot warning flags to avoid log spam
        private static bool _warnedBlocking;
        private static bool _warnedTarget;
        private static bool _warnedMoveTo;
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
        //  MonsterAI targets
        // ══════════════════════════════════════════════════════════════════════

        internal static bool TrySetTargetCreature(MonsterAI ai, Character target)
        {
            if (ai == null || _targetCreatureField == null)
            {
                WarnOnce(ref _warnedTarget, "MonsterAI.m_targetCreature");
                return false;
            }
            try
            {
                _targetCreatureField.SetValue(ai, target);
                return true;
            }
            catch (Exception)
            {
                WarnOnce(ref _warnedTarget, "MonsterAI.m_targetCreature");
                return false;
            }
        }

        internal static Character GetTargetCreature(MonsterAI ai)
        {
            if (ai == null || _targetCreatureField == null) return null;
            try { return _targetCreatureField.GetValue(ai) as Character; }
            catch { return null; }
        }

        internal static bool TrySetTargetStatic(MonsterAI ai, StaticTarget target)
        {
            if (ai == null || _targetStaticField == null) return false;
            try { _targetStaticField.SetValue(ai, target); return true; }
            catch { return false; }
        }

        internal static StaticTarget GetTargetStatic(MonsterAI ai)
        {
            if (ai == null || _targetStaticField == null) return null;
            try { return _targetStaticField.GetValue(ai) as StaticTarget; }
            catch { return null; }
        }

        internal static bool TrySetLastKnownTargetPos(MonsterAI ai, Vector3 pos)
        {
            if (ai == null || _lastKnownTargetPosField == null) return false;
            try { _lastKnownTargetPosField.SetValue(ai, pos); return true; }
            catch { return false; }
        }

        internal static bool TrySetBeenAtLastPos(MonsterAI ai, bool value)
        {
            if (ai == null || _beenAtLastPosField == null) return false;
            try { _beenAtLastPosField.SetValue(ai, value); return true; }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Clear all MonsterAI targets at once
        // ══════════════════════════════════════════════════════════════════════

        internal static void ClearAllTargets(MonsterAI ai)
        {
            var hadCreature = GetTargetCreature(ai);
            var hadStatic = GetTargetStatic(ai);
            TrySetTargetCreature(ai, null);
            TrySetTargetStatic(ai, null);
            if (hadCreature != null || hadStatic != null)
                CompanionsPlugin.Log.LogInfo(
                    $"[Reflection] ClearAllTargets — cleared creature=\"{hadCreature?.m_name ?? ""}\" " +
                    $"static=\"{hadStatic?.name ?? ""}\"");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  BaseAI.MoveTo (protected)
        // ══════════════════════════════════════════════════════════════════════

        internal static bool TryMoveTo(BaseAI ai, float dt, Vector3 point, float dist, bool run)
        {
            if (!_loggedMoveToInit)
            {
                _loggedMoveToInit = true;
                CompanionsPlugin.Log.LogInfo(
                    $"[Reflection] MoveTo method resolved: {(_moveToMethod != null ? "OK" : "FAILED")}");
            }

            if (ai == null || _moveToMethod == null)
            {
                WarnOnce(ref _warnedMoveTo, "BaseAI.MoveTo");
                return false;
            }
            try
            {
                _moveToMethod.Invoke(ai, new object[] { dt, point, dist, run });
                return true;
            }
            catch (Exception ex)
            {
                WarnOnce(ref _warnedMoveTo, "BaseAI.MoveTo");
                CompanionsPlugin.Log.LogError($"[Reflection] MoveTo exception: {ex.Message}");
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  BaseAI.LookAt / IsLookingAt (protected)
        // ══════════════════════════════════════════════════════════════════════

        internal static void LookAt(BaseAI ai, Vector3 point)
        {
            if (ai == null || _lookAtMethod == null) return;
            try { _lookAtMethod.Invoke(ai, new object[] { point }); }
            catch { }
        }

        internal static bool IsLookingAt(BaseAI ai, Vector3 point, float minAngle = 20f)
        {
            if (ai == null || _isLookingAtMethod == null) return false;
            try { return (bool)_isLookingAtMethod.Invoke(ai, new object[] { point, minAngle, false }); }
            catch { return false; }
        }

        /// <summary>
        /// Clears the alert state on a BaseAI. SetAlerted is protected virtual,
        /// so we invoke via reflection. This resets m_alerted, the animator bool,
        /// and the ZDO flag.
        /// </summary>
        internal static void SetAlerted(BaseAI ai, bool alert)
        {
            if (ai == null || _setAlertedMethod == null) return;
            try { _setAlertedMethod.Invoke(ai, new object[] { alert }); }
            catch { }
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
