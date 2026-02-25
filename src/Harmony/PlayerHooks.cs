using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// After the local player spawns (new session, death respawn, logout-login),
    /// scan the scene for owned companions and re-establish their follow targets.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
    public static class PlayerHooks
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            CompanionManager.RestoreFollowTargets();
        }
    }
}
