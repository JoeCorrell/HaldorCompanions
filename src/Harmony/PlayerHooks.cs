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

    /// <summary>
    /// Block all player input while the companion interaction panel or radial is visible.
    /// Prevents jumping/moving/attacking while editing the companion name, etc.
    /// </summary>
    [HarmonyPatch(typeof(Player), "TakeInput")]
    internal static class TakeInput_BlockForCompanionUI
    {
        static void Postfix(ref bool __result)
        {
            if (!__result) return;
            var panel = CompanionInteractPanel.Instance;
            if (panel != null && panel.IsVisible)
            { __result = false; return; }
            var radial = CompanionRadialMenu.Instance;
            if (radial != null && radial.IsVisible)
                __result = false;
        }
    }
}
