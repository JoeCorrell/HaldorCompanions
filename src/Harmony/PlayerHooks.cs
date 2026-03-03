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
            var starter = StarterCompanionPanel.Instance;
            if (starter != null && starter.IsVisible)
            { __result = false; return; }
            var cfg = ConfigPanel.Instance;
            if (cfg != null && cfg.IsVisible)
            { __result = false; return; }
            var panel = CompanionInteractPanel.Instance;
            if (panel != null && panel.IsVisible)
            { __result = false; return; }
            var radial = CompanionRadialMenu.Instance;
            if (radial != null && radial.IsVisible)
                __result = false;
        }
    }

    /// <summary>
    /// Block PlayerController input (movement + look) while the starter panel,
    /// config panel, or radial menu is visible.
    /// </summary>
    [HarmonyPatch(typeof(PlayerController), "TakeInput")]
    internal static class PlayerControllerInput_BlockForCompanionUI
    {
        static void Postfix(ref bool __result)
        {
            if (!__result) return;
            var starter = StarterCompanionPanel.Instance;
            if (starter != null && starter.IsVisible)
            { __result = false; return; }
            var cfg = ConfigPanel.Instance;
            if (cfg != null && cfg.IsVisible)
            { __result = false; return; }
            var panel = CompanionInteractPanel.Instance;
            if (panel != null && panel.IsNameInputFocused)
            { __result = false; return; }
            var radial = CompanionRadialMenu.Instance;
            if (radial != null && radial.IsVisible)
                __result = false;
        }
    }

    /// <summary>
    /// Make Hud.InRadial() return true when the starter panel or config panel
    /// is visible. GameCamera checks this to block camera rotation and unlock cursor.
    /// </summary>
    [HarmonyPatch(typeof(Hud), nameof(Hud.InRadial))]
    internal static class InRadial_BlockForStarterPanel
    {
        static void Postfix(ref bool __result)
        {
            if (__result) return;
            var starter = StarterCompanionPanel.Instance;
            if (starter != null && starter.IsVisible)
            { __result = true; return; }
            var cfg = ConfigPanel.Instance;
            if (cfg != null && cfg.IsVisible)
                __result = true;
        }
    }
}
