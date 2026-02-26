using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Adds a yellow stamina bar below the health bar on companion overhead HUDs.
    /// Clones the vanilla Health bar section, recolors it, and updates it each frame
    /// from CompanionStamina.
    /// </summary>
    public static class EnemyHudPatches
    {
        private static readonly Color StaminaYellow = new Color(1f, 0.8f, 0.1f, 1f);
        private static readonly Dictionary<Character, GuiBar> _staminaBars =
            new Dictionary<Character, GuiBar>();

        [HarmonyPatch(typeof(EnemyHud), "ShowHud")]
        private static class ShowHud_Patch
        {
            static void Postfix(EnemyHud __instance, Character c)
            {
                if (c == null || c.GetComponent<CompanionStamina>() == null) return;
                if (_staminaBars.ContainsKey(c)) return;

                // Access private m_huds to get the HudData just created for this character
                var huds = Traverse.Create(__instance).Field("m_huds")
                    .GetValue() as System.Collections.IDictionary;
                if (huds == null || !huds.Contains(c)) return;

                var hudData = huds[c];
                var gui = Traverse.Create(hudData).Field("m_gui")
                    .GetValue<GameObject>();
                if (gui == null) return;

                var healthTransform = gui.transform.Find("Health");
                if (healthTransform == null) return;

                // Clone the Health section as our stamina bar
                var staminaGO = Object.Instantiate(healthTransform.gameObject, gui.transform);
                staminaGO.name = "CompanionStamina";

                // Position below the health bar
                var healthRT = healthTransform.GetComponent<RectTransform>();
                var staminaRT = staminaGO.GetComponent<RectTransform>();
                float healthH = healthRT.rect.height;
                if (healthH <= 0f) healthH = 8f;
                staminaRT.localPosition = healthRT.localPosition - new Vector3(0f, healthH + 2f, 0f);

                // Disable slow-drain and friendly bars — only keep the fast fill
                var slowBar = staminaGO.transform.Find("health_slow");
                if (slowBar != null) slowBar.gameObject.SetActive(false);
                var friendlyBar = staminaGO.transform.Find("health_fast_friendly");
                if (friendlyBar != null) friendlyBar.gameObject.SetActive(false);

                // Get the fast bar, recolor to yellow
                var fastBarT = staminaGO.transform.Find("health_fast");
                if (fastBarT == null) return;

                var guiBar = fastBarT.GetComponent<GuiBar>();
                if (guiBar == null) return;

                guiBar.SetColor(StaminaYellow);

                var companionStamina = c.GetComponent<CompanionStamina>();
                guiBar.SetValue(companionStamina.GetStaminaPercentage());

                _staminaBars[c] = guiBar;
            }
        }

        [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
        private static class UpdateHuds_Patch
        {
            static void Postfix()
            {
                List<Character> toRemove = null;
                foreach (var kvp in _staminaBars)
                {
                    if (kvp.Key == null || kvp.Value == null)
                    {
                        if (toRemove == null) toRemove = new List<Character>();
                        toRemove.Add(kvp.Key);
                        continue;
                    }

                    var stamina = kvp.Key.GetComponent<CompanionStamina>();
                    if (stamina == null)
                    {
                        if (toRemove == null) toRemove = new List<Character>();
                        toRemove.Add(kvp.Key);
                        continue;
                    }

                    kvp.Value.SetValue(stamina.GetStaminaPercentage());
                }

                if (toRemove != null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                        _staminaBars.Remove(toRemove[i]);
                }
            }
        }
    }
}
