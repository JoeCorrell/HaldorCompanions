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

        // Keyed by instance ID to avoid destroyed-Unity-object dictionary key issues
        private static readonly Dictionary<int, CompanionBars> _bars =
            new Dictionary<int, CompanionBars>();
        // Track Character refs for update iteration (separate from key)
        private static readonly Dictionary<int, Character> _barCharacters =
            new Dictionary<int, Character>();

        // Cached m_huds field to avoid per-frame Traverse reflection
        private static System.Collections.IDictionary _cachedHuds;
        private static EnemyHud _cachedHudInstance;

        private static System.Collections.IDictionary GetHuds(EnemyHud instance)
        {
            if (_cachedHudInstance != instance || _cachedHuds == null)
            {
                _cachedHuds = Traverse.Create(instance).Field("m_huds")
                    .GetValue() as System.Collections.IDictionary;
                _cachedHudInstance = instance;
            }
            return _cachedHuds;
        }

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
                int id = c.GetInstanceID();
                if (_bars.ContainsKey(id)) return;

                var huds = GetHuds(__instance);
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

                _bars[id] = new CompanionBars
                {
                    StaminaBar = staminaBar,
                    WeightBar  = weightBar
                };
                _barCharacters[id] = c;

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
                _barCharacters.Clear();
                _cachedHuds = null;
                _cachedHudInstance = null;
            }
        }

        [HarmonyPatch(typeof(EnemyHud), "UpdateHuds")]
        private static class UpdateHuds_Patch
        {
            static void Postfix(EnemyHud __instance)
            {
                // Hide the overhead HUD for the companion targeted by the radial menu
                HideHudForRadialTarget(__instance);

                List<int> toRemove = null;
                foreach (var kvp in _bars)
                {
                    _barCharacters.TryGetValue(kvp.Key, out var character);
                    if (character == null ||
                        (kvp.Value.StaminaBar == null && kvp.Value.WeightBar == null))
                    {
                        if (toRemove == null) toRemove = new List<int>();
                        toRemove.Add(kvp.Key);
                        continue;
                    }

                    var bars = kvp.Value;

                    // Update stamina bar
                    if (bars.StaminaBar != null)
                    {
                        var stamina = character.GetComponent<CompanionStamina>();
                        if (stamina != null)
                            bars.StaminaBar.SetValue(stamina.GetStaminaPercentage());
                    }

                    // Update weight bar
                    if (bars.WeightBar != null)
                    {
                        var humanoid = character as Humanoid;
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
                        _barCharacters.Remove(toRemove[i]);
                    }
                }
            }
        }

        /// <summary>
        /// Hides the overhead HUD (name, health, stamina, weight) for the companion
        /// that the radial command menu is currently targeting.
        /// </summary>
        private static void HideHudForRadialTarget(EnemyHud instance)
        {
            if (CompanionRadialMenu.Instance == null || !CompanionRadialMenu.Instance.IsVisible)
                return;

            var companion = CompanionRadialMenu.Instance.CurrentCompanion;
            if (companion == null) return;

            var character = companion.GetComponent<Character>();
            if (character == null) return;

            var huds = GetHuds(instance);
            if (huds == null || !huds.Contains(character)) return;

            var hudData = huds[character];
            var gui = Traverse.Create(hudData).Field("m_gui")
                .GetValue<GameObject>();
            if (gui != null)
                gui.SetActive(false);
        }
    }
}
