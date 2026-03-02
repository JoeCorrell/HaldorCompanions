using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Teleports owned companions along with the player when using portals or
    /// dungeon entrances. Captures companion ZDOIDs when the player starts
    /// teleporting, then warps them to the player when the teleport completes
    /// (area loaded, floor found).
    /// </summary>
    [HarmonyPatch]
    internal static class PortalPatches
    {
        private static readonly List<ZDOID> _pendingCompanions = new List<ZDOID>();

        /// <summary>
        /// When the local player successfully starts a teleport, capture the
        /// ZDOIDs of all owned companions that aren't in Stay mode.
        /// </summary>
        [HarmonyPatch(typeof(Player), nameof(Player.TeleportTo))]
        [HarmonyPostfix]
        static void TeleportTo_Postfix(Player __instance, bool __result)
        {
            if (!__result) return;
            if (__instance != Player.m_localPlayer) return;

            _pendingCompanions.Clear();

            string localId = __instance.GetPlayerID().ToString();

            foreach (var setup in Object.FindObjectsByType<CompanionSetup>(FindObjectsSortMode.None))
            {
                var nview = setup.GetComponent<ZNetView>();
                if (nview?.GetZDO() == null) continue;

                var zdo = nview.GetZDO();
                if (zdo.GetString(CompanionSetup.OwnerHash, "") != localId) continue;

                // Skip companions in Stay mode or with Follow OFF — they're deliberately placed
                int mode = zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                if (mode == CompanionSetup.ModeStay) continue;
                if (zdo.GetBool(CompanionSetup.StayHomeHash, false)) continue;
                if (!zdo.GetBool(CompanionSetup.FollowHash, true)) continue;

                _pendingCompanions.Add(zdo.m_uid);
            }

            if (_pendingCompanions.Count > 0)
                CompanionsPlugin.Log.LogInfo(
                    $"[Portal] Player teleporting — {_pendingCompanions.Count} companion(s) queued");
        }

        /// <summary>
        /// Detect teleport completion (was teleporting → no longer teleporting)
        /// and warp pending companions to the player.
        /// </summary>
        [HarmonyPatch(typeof(Player), "UpdateTeleport")]
        [HarmonyPrefix]
        static void UpdateTeleport_Prefix(Player __instance, out bool __state)
        {
            __state = __instance == Player.m_localPlayer && __instance.IsTeleporting();
        }

        [HarmonyPatch(typeof(Player), "UpdateTeleport")]
        [HarmonyPostfix]
        static void UpdateTeleport_Postfix(Player __instance, bool __state)
        {
            if (!__state) return;                      // wasn't teleporting
            if (__instance.IsTeleporting()) return;    // still teleporting
            if (__instance != Player.m_localPlayer) return;
            if (_pendingCompanions.Count == 0) return;

            Vector3 playerPos = __instance.transform.position;
            CompanionsPlugin.Log.LogInfo(
                $"[Portal] Teleport complete at {playerPos:F1} — warping {_pendingCompanions.Count} companion(s)");

            foreach (var zdoid in _pendingCompanions)
                WarpCompanion(zdoid, playerPos);

            _pendingCompanions.Clear();
        }

        private static void WarpCompanion(ZDOID zdoid, Vector3 targetPos)
        {
            var zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo == null)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[Portal] Companion ZDO {zdoid} not found — skipping");
                return;
            }

            Vector3 spawnPos = FindNearbyPosition(targetPos, 3f);

            // Update ZDO position — authoritative for zone loading / next instantiation
            zdo.SetPosition(spawnPos);

            // If the companion's GameObject is still loaded, warp it directly
            var go = ZNetScene.instance?.FindInstance(zdoid);
            if (go != null)
            {
                // Cancel any active rest state (sitting/sleeping) before warping —
                // otherwise the companion arrives with isKinematic=true and stuck animations
                var rest = go.GetComponent<CompanionRest>();
                if (rest != null) rest.CancelDirected();

                go.transform.position = spawnPos;
                var body = go.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.position = spawnPos;
                    body.velocity = Vector3.zero;
                }

                var ai = go.GetComponent<CompanionAI>();
                if (ai != null)
                    ai.SetFollowTarget(Player.m_localPlayer?.gameObject);

                CompanionsPlugin.Log.LogInfo(
                    $"[Portal] Warped companion to {spawnPos:F1} (GameObject present)");
            }
            else
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[Portal] Updated companion ZDO position to {spawnPos:F1} (not loaded)");
            }
        }

        private static Vector3 FindNearbyPosition(Vector3 origin, float radius)
        {
            for (int i = 0; i < 20; i++)
            {
                Vector2 rnd = Random.insideUnitCircle * radius;
                Vector3 candidate = origin + new Vector3(rnd.x, 0f, rnd.y);
                if (ZoneSystem.instance != null &&
                    ZoneSystem.instance.FindFloor(candidate, out float height))
                {
                    candidate.y = height;
                    return candidate;
                }
            }
            return origin + Vector3.right * 2f;
        }
    }
}
