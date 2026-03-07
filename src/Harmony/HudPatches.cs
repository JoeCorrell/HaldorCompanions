using System.Collections.Generic;
using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Injects companion status effects (e.g., Rested buff, potions) into the HUD status bar.
    /// The vanilla Hud.UpdateStatusEffects receives a List&lt;StatusEffect&gt; from the
    /// player's SEMan. This prefix appends companion display SEs to that list so they
    /// appear alongside the player's own status effects.
    /// </summary>
    public static class HudPatches
    {
        [HarmonyPatch(typeof(Hud), "UpdateStatusEffects")]
        private static class UpdateStatusEffects_Patch
        {
            static void Prefix(List<StatusEffect> statusEffects)
            {
                // Companion Rested buff (display-only SE, not in SEMan)
                var buffs = CompanionRestedBuff.Instances;
                for (int i = 0; i < buffs.Count; i++)
                {
                    var buff = buffs[i];
                    if (buff == null || !buff.IsRested) continue;

                    var se = buff.DisplaySE;
                    if (se == null || se.m_icon == null) continue;

                    statusEffects.Add(se);
                }

                // Companion potion/mead status effects (from SEMan)
                var companions = CompanionSetup.AllCompanions;
                for (int i = 0; i < companions.Count; i++)
                {
                    var comp = companions[i];
                    if (comp == null) continue;

                    var humanoid = comp.GetComponent<Humanoid>();
                    if (humanoid == null) continue;

                    var seman = humanoid.GetSEMan();
                    if (seman == null) continue;

                    var effects = seman.GetStatusEffects();
                    for (int j = 0; j < effects.Count; j++)
                    {
                        var se = effects[j];
                        if (se == null || se.m_icon == null) continue;

                        // Skip Encumbered — shown via the companion panel already
                        if (se.NameHash() == SEMan.s_statusEffectEncumbered) continue;

                        statusEffects.Add(se);
                    }
                }
            }
        }
    }
}
