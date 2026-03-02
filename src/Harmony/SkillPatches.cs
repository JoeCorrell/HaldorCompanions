using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Harmony patches on Character's virtual skill methods. Since Player overrides
    /// all three, these patches only fire for non-Player characters (companions).
    ///
    /// This covers all vanilla combat skill gains automatically:
    /// - Attack.cs: m_character.RaiseSkill(weaponSkill) on melee hit
    /// - Projectile.cs: m_owner.RaiseSkill(m_skill) on projectile hit
    /// - Humanoid.BlockAttack: RaiseSkill(Blocking, 2f/1f) on block/parry
    /// - Attack.cs: GetRandomSkillFactor(weaponSkill) for damage scaling
    /// - Attack.cs: GetSkillFactor(weaponSkill) for stamina cost reduction
    /// - Humanoid.BlockAttack: GetSkillFactor(Blocking) for block power scaling
    /// </summary>
    public static class SkillPatches
    {
        [HarmonyPatch(typeof(Character), nameof(Character.RaiseSkill))]
        private static class RaiseSkill_Patch
        {
            static bool Prefix(Character __instance, Skills.SkillType skill, float value)
            {
                var cs = __instance.GetComponent<CompanionSkills>();
                if (cs == null) return true;

                cs.RaiseSkill(skill, value);
                return false;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetSkillFactor))]
        private static class GetSkillFactor_Patch
        {
            static bool Prefix(Character __instance, Skills.SkillType skill, ref float __result)
            {
                var cs = __instance.GetComponent<CompanionSkills>();
                if (cs == null) return true;

                __result = cs.GetSkillFactor(skill);
                return false;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetRandomSkillFactor))]
        private static class GetRandomSkillFactor_Patch
        {
            static bool Prefix(Character __instance, Skills.SkillType skill, ref float __result)
            {
                var cs = __instance.GetComponent<CompanionSkills>();
                if (cs == null) return true;

                __result = cs.GetRandomSkillFactor(skill);
                return false;
            }
        }
    }
}
