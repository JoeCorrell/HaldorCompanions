using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Companions
{
    /// <summary>
    /// Injects a "Mod Options" button into Valheim's pause menu (ESC menu)
    /// and integrates it with the controller navigation system.
    /// </summary>
    internal static class MenuPatches
    {
        private const string ButtonName = "HC_ModOptions";

        // ══════════════════════════════════════════════════════════════════
        //  Menu.Show — inject button once per Menu instance
        // ══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Menu), nameof(Menu.Show))]
        [HarmonyPostfix]
        private static void MenuShow_Postfix(Menu __instance)
        {
            var menuDialog = __instance.m_menuDialog;
            if (menuDialog == null) return;

            // Already injected?
            if (menuDialog.Find($"MenuEntries/{ButtonName}") != null) return;

            // Find the Settings button to clone its style
            var settingsGO = menuDialog.Find("MenuEntries/Settings")?.gameObject;
            if (settingsGO == null)
            {
                CompanionsPlugin.Log.LogWarning("[MenuPatches] Could not find MenuEntries/Settings — skipping Mod Options button");
                return;
            }

            // Clone the Settings button for identical visual style
            var modOptsGO = Object.Instantiate(settingsGO, settingsGO.transform.parent);
            modOptsGO.name = ButtonName;

            // Insert after Settings
            int settingsIdx = settingsGO.transform.GetSiblingIndex();
            modOptsGO.transform.SetSiblingIndex(settingsIdx + 1);

            // Update the button text
            var tmp = modOptsGO.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null)
            {
                string locText = ModLocalization.Loc("hc_menu_mod_options");
                tmp.text = (locText != null && !locText.StartsWith("["))
                    ? locText : "Mod Options";
            }

            // Rewire onClick to open ConfigPanel
            var btn = modOptsGO.GetComponent<Button>();
            if (btn != null)
            {
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(() =>
                {
                    __instance.Hide();
                    if (ConfigPanel.Instance != null)
                        ConfigPanel.Instance.Show();
                });
            }

            CompanionsPlugin.Log.LogDebug("[MenuPatches] Injected 'Mod Options' button into pause menu");
        }

        // ══════════════════════════════════════════════════════════════════
        //  Menu.UpdateNavigation — splice our button into the nav chain
        // ══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Menu), "UpdateNavigation")]
        [HarmonyPostfix]
        private static void UpdateNavigation_Postfix(Menu __instance)
        {
            var menuDialog = __instance.m_menuDialog;
            if (menuDialog == null) return;

            var modOptsTransform = menuDialog.Find($"MenuEntries/{ButtonName}");
            if (modOptsTransform == null) return;

            var modOptsBtn = modOptsTransform.GetComponent<Button>();
            if (modOptsBtn == null) return;

            // Find Settings and Logout to splice between them
            var settingsBtn = menuDialog.Find("MenuEntries/Settings")?.GetComponent<Button>();
            var logoutBtn = menuDialog.Find("MenuEntries/Logout")?.GetComponent<Button>();
            if (settingsBtn == null || logoutBtn == null) return;

            // Read current Settings nav — its selectOnDown should point to Logout
            // Splice: Settings → ModOptions → (whatever Settings was pointing to)
            var settingsNav = settingsBtn.navigation;
            var modOptsNav = modOptsBtn.navigation;
            var logoutNav = logoutBtn.navigation;

            // ModOptions: up = Settings, down = whatever Settings was pointing down to
            modOptsNav.mode = Navigation.Mode.Explicit;
            modOptsNav.selectOnUp = settingsBtn;
            modOptsNav.selectOnDown = settingsNav.selectOnDown;
            modOptsBtn.navigation = modOptsNav;

            // Settings: down = ModOptions (up stays the same)
            settingsNav.selectOnDown = modOptsBtn;
            settingsBtn.navigation = settingsNav;

            // Logout (or whatever was below Settings): up = ModOptions
            if (settingsNav.selectOnDown != null)
            {
                // The original target of Settings.down needs its up changed to ModOptions
                // But we already overwrote settingsNav.selectOnDown above, so use logoutBtn directly
            }
            logoutNav.selectOnUp = modOptsBtn;
            logoutBtn.navigation = logoutNav;
        }
    }
}
