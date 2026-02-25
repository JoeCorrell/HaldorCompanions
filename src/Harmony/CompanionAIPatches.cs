using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Harmony patches for companion AI:
    /// - Tighter follow distance (1.5 stop / 5 run vs vanilla 3 / 10)
    /// - Bridge Character.UseStamina/HaveStamina to CompanionStamina
    /// </summary>
    public static class CompanionAIPatches
    {
        // ── Reflection for BaseAI.MoveTo (protected) ────────────────────────
        private static readonly MethodInfo _moveToMethod =
            AccessTools.Method(typeof(BaseAI), "MoveTo",
                new[] { typeof(float), typeof(Vector3), typeof(float), typeof(bool) });

        // ══════════════════════════════════════════════════════════════════════
        //  Follow patch — tighter distance for companions
        // ══════════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(BaseAI), "Follow")]
        private static class Follow_Patch
        {
            /// <summary>
            /// Override follow behavior for companions:
            /// - Stop at 1.5 units (vanilla = 3)
            /// - Run at 5+ units (vanilla = 10)
            /// </summary>
            static bool Prefix(BaseAI __instance, GameObject go, float dt)
            {
                if (__instance.GetComponent<CompanionSetup>() == null)
                    return true; // not a companion — use vanilla

                if (go == null) return true;

                float dist = Vector3.Distance(
                    go.transform.position, __instance.transform.position);

                bool run = dist > 5f;

                if (dist < 1.5f)
                {
                    __instance.StopMoving();
                    return false;
                }

                // Use pathfinding to move toward the follow target
                _moveToMethod?.Invoke(__instance,
                    new object[] { dt, go.transform.position, 0f, run });

                return false; // skip original Follow
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Stamina bridge — route Character.UseStamina to CompanionStamina
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Character.UseStamina is virtual and a no-op in the base class.
        /// Player overrides it, so this patch only fires for non-Player characters.
        /// For companions with CompanionStamina, drain their custom stamina pool.
        /// This makes vanilla attack/block stamina costs work for companions.
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.UseStamina))]
        private static class UseStamina_Patch
        {
            static bool Prefix(Character __instance, float stamina)
            {
                var cs = __instance.GetComponent<CompanionStamina>();
                if (cs == null) return true; // not a companion

                cs.Drain(stamina);
                return false; // skip original no-op
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Stamina bridge — route Character.HaveStamina to CompanionStamina
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Character.HaveStamina always returns true in the base class.
        /// For companions, check their actual stamina pool so attacks are gated.
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.HaveStamina))]
        private static class HaveStamina_Patch
        {
            static bool Prefix(Character __instance, float amount, ref bool __result)
            {
                var cs = __instance.GetComponent<CompanionStamina>();
                if (cs == null) return true; // not a companion

                __result = cs.Stamina >= amount;
                return false; // skip original always-true
            }
        }
    }
}
