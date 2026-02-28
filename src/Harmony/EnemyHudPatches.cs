using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Adds stamina (yellow) and weight (brown) bars below the health bar on
    /// companion overhead HUDs. Clones the vanilla Health bar section, recolors
    /// it, and updates each frame from CompanionStamina / Inventory weight.
    /// </summary>
    public static class EnemyHudPatches
    {
        private static readonly Color StaminaYellow = new Color(1f, 0.8f, 0.1f, 1f);
        private static readonly Color WeightBrown   = new Color(0.72f, 0.53f, 0.26f, 1f);

        private struct CompanionBars
        {
            public GuiBar StaminaBar;
            public GuiBar WeightBar;
        }

        private static readonly Dictionary<Character, CompanionBars> _bars =
            new Dictionary<Character, CompanionBars>();

        private static GuiBar CreateBar(Transform healthTransform, Transform guiParent,
            string name, Color color, float yOffset)
        {
            var barGO = Object.Instantiate(healthTransform.gameObject, guiParent);
            barGO.name = name;

            var healthRT = healthTransform.GetComponent<RectTransform>();
            var barRT = barGO.GetComponent<RectTransform>();
            barRT.localPosition = healthRT.localPosition - new Vector3(0f, yOffset, 0f);

            var slowBar = barGO.transform.Find("health_slow");
            if (slowBar != null) slowBar.gameObject.SetActive(false);
            var friendlyBar = barGO.transform.Find("health_fast_friendly");
            if (friendlyBar != null) friendlyBar.gameObject.SetActive(false);

            var fastBarT = barGO.transform.Find("health_fast");
            if (fastBarT == null) return null;

            var guiBar = fastBarT.GetComponent<GuiBar>();
            if (guiBar == null) return null;

            guiBar.SetColor(color);
            return guiBar;
        }

        [HarmonyPatch(typeof(EnemyHud), "ShowHud")]
        private static class ShowHud_Patch
        {
            static void Postfix(EnemyHud __instance, Character c)
            {
                if (c == null || c.GetComponent<CompanionStamina>() == null) return;
                if (_bars.ContainsKey(c)) return;

                var huds = Traverse.Create(__instance).Field("m_huds")
                    .GetValue() as System.Collections.IDictionary;
                if (huds == null || !huds.Contains(c)) return;

                var hudData = huds[c];
                var gui = Traverse.Create(hudData).Field("m_gui")
                    .GetValue<GameObject>();
                if (gui == null) return;

                var healthTransform = gui.transform.Find("Health");
                if (healthTransform == null) return;

                float healthH = healthTransform.GetComponent<RectTransform>().rect.height;
                if (healthH <= 0f) healthH = 8f;

                // Stamina bar — directly below health
                float staminaY = healthH + 2f;
                var staminaBar = CreateBar(healthTransform, gui.transform,
                    "CompanionStamina", StaminaYellow, staminaY);

                // Weight bar — below stamina
                float weightY = staminaY + healthH + 2f;
                var weightBar = CreateBar(healthTransform, gui.transform,
                    "CompanionWeight", WeightBrown, weightY);

                if (staminaBar != null)
                {
                    var stamina = c.GetComponent<CompanionStamina>();
                    staminaBar.SetValue(stamina.GetStaminaPercentage());
                }

                if (weightBar != null)
                {
                    var humanoid = c.GetComponent<Humanoid>();
                    if (humanoid != null)
                    {
                        var inv = humanoid.GetInventory();
                        float pct = inv != null
                            ? Mathf.Clamp01(inv.GetTotalWeight() / CompanionTierData.MaxCarryWeight)
                            : 0f;
                        weightBar.SetValue(pct);
                    }
                }

                _bars[c] = new CompanionBars
                {
                    StaminaBar = staminaBar,
                    WeightBar  = weightBar
                };

                CompanionsPlugin.Log.LogDebug(
                    $"[HUD] Created companion bars — stamina={staminaBar != null} " +
                    $"weight={weightBar != null} companion=\"{c.m_name}\"");
            }
        }

        [HarmonyPatch(typeof(EnemyHud), "OnDestroy")]
        private static class OnDestroy_Patch
        {
            static void Postfix()
            {
                _bars.Clear();
            }
        }

        [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
        private static class UpdateHuds_Patch
        {
            static void Postfix()
            {
                List<Character> toRemove = null;
                foreach (var kvp in _bars)
                {
                    if (kvp.Key == null ||
                        (kvp.Value.StaminaBar == null && kvp.Value.WeightBar == null))
                    {
                        if (toRemove == null) toRemove = new List<Character>();
                        toRemove.Add(kvp.Key);
                        continue;
                    }

                    var bars = kvp.Value;

                    // Update stamina bar
                    if (bars.StaminaBar != null)
                    {
                        var stamina = kvp.Key.GetComponent<CompanionStamina>();
                        if (stamina != null)
                            bars.StaminaBar.SetValue(stamina.GetStaminaPercentage());
                    }

                    // Update weight bar
                    if (bars.WeightBar != null)
                    {
                        var humanoid = kvp.Key as Humanoid;
                        if (humanoid != null)
                        {
                            var inv = humanoid.GetInventory();
                            float pct = inv != null
                                ? Mathf.Clamp01(inv.GetTotalWeight() / CompanionTierData.MaxCarryWeight)
                                : 0f;
                            bars.WeightBar.SetValue(pct);
                        }
                    }
                }

                if (toRemove != null)
                {
                    for (int i = 0; i < toRemove.Count; i++)
                    {
                        CompanionsPlugin.Log.LogDebug(
                            $"[HUD] Removing stale companion bars — character destroyed or null");
                        _bars.Remove(toRemove[i]);
                    }
                }
            }
        }
    }
}
