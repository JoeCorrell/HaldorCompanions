using System.Collections.Generic;
using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Injects companion status effects (e.g., Rested buff) into the HUD status bar.
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
                var buffs = CompanionRestedBuff.Instances;
                for (int i = 0; i < buffs.Count; i++)
                {
                    var buff = buffs[i];
                    if (buff == null || !buff.IsRested) continue;

                    var se = buff.DisplaySE;
                    if (se == null || se.m_icon == null) continue;

                    statusEffects.Add(se);
                }
            }
        }
    }
}
