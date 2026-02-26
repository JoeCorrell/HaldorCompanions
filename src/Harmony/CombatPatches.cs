using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Perfect parry timing, food-based max health, power attacks, and weapon control
    /// for companions. Uses ReflectionHelper for safe block timer access.
    /// </summary>
    public static class CombatPatches
    {
        /// <summary>
        /// Prevents vanilla MonsterAI.SelectBestAttack from overriding the
        /// CombatController's weapon selections. Without this, MonsterAI calls
        /// EquipBestWeapon every 1s, causing pickaxe/bow cycling mid-combat.
        /// </summary>
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipBestWeapon))]
        private static class EquipBestWeapon_SuppressForCompanion
        {
            static bool Prefix(Humanoid __instance)
            {
                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return true; // Not a companion — run vanilla

                // Companions manage their own weapons via CombatController + AutoEquipBest
                return false;
            }
        }

        private static float _lastHealthLogTime;
        private const float HealthLogInterval = 5f;

        /// <summary>
        /// When a companion's DoAttack fires and the target is staggered,
        /// upgrade the normal attack to a power attack (secondary).
        /// </summary>
        [HarmonyPatch(typeof(MonsterAI), "DoAttack")]
        private static class DoAttack_PowerAttack
        {
            static bool Prefix(MonsterAI __instance, Character target, ref bool __result)
            {
                if (target == null) return true;
                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return true;

                if (!target.IsStaggering()) return true;

                var humanoid = __instance.GetComponent<Humanoid>();
                if (humanoid == null) return true;

                var weapon = humanoid.GetCurrentWeapon();
                if (weapon == null || !weapon.HaveSecondaryAttack()) return true;

                __result = humanoid.StartAttack(target, true);
                CompanionsPlugin.Log.LogInfo(
                    $"[Combat] DoAttack — power attack on staggered \"{target.m_name}\"");
                return false;
            }
        }

        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        private static class BlockAttack_Patch
        {
            static void Prefix(Humanoid __instance)
            {
                if (__instance.GetComponent<CompanionSetup>() == null) return;

                float timer = ReflectionHelper.GetBlockTimer(__instance);
                if (timer >= 0f)
                {
                    CompanionsPlugin.Log.LogInfo(
                        $"[Combat] BlockAttack — setting block timer to 0 for parry " +
                        $"(was {timer:F3}) companion=\"{__instance.m_name}\"");
                    ReflectionHelper.TrySetBlockTimer(__instance, 0f);
                }
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetMaxHealth))]
        private static class GetMaxHealth_Patch
        {
            static void Postfix(Character __instance, ref float __result)
            {
                var food = __instance.GetComponent<CompanionFood>();
                if (food == null) return;

                float baseHp = CompanionFood.BaseHealth;
                float foodBonus = food.TotalHealthBonus;
                float newMax = baseHp + foodBonus;

                // Throttled logging — GetMaxHealth is called frequently
                if (UnityEngine.Time.time - _lastHealthLogTime > HealthLogInterval)
                {
                    _lastHealthLogTime = UnityEngine.Time.time;
                    CompanionsPlugin.Log.LogInfo(
                        $"[Combat] GetMaxHealth — base={baseHp:F0} + food={foodBonus:F1} " +
                        $"= {newMax:F1} (vanilla was {__result:F1}) " +
                        $"companion=\"{__instance.m_name}\"");
                }

                __result = newMax;
            }
        }
    }
}
