using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Extends vanilla durability mechanics to companions.
    /// Vanilla gates all durability drain behind IsPlayer() checks,
    /// so companions need explicit patches.
    /// </summary>
    public static class DurabilityPatches
    {
        // Cached FieldInfos for equipped armor slots (same as Humanoid internals)
        private static readonly FieldInfo _chestItem    = AccessTools.Field(typeof(Humanoid), "m_chestItem");
        private static readonly FieldInfo _legItem      = AccessTools.Field(typeof(Humanoid), "m_legItem");
        private static readonly FieldInfo _helmetItem   = AccessTools.Field(typeof(Humanoid), "m_helmetItem");
        private static readonly FieldInfo _shoulderItem = AccessTools.Field(typeof(Humanoid), "m_shoulderItem");

        /// <summary>
        /// Drain weapon durability when a companion swings.
        /// Vanilla drains in Attack.DoAreaAttack/ProjectileAttackTriggered
        /// but those paths are gated by IsPlayer().
        /// </summary>
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class WeaponDurability_Patch
        {
            static void Postfix(Humanoid __instance, bool __result)
            {
                if (!__result) return;
                if (__instance.GetComponent<CompanionSetup>() == null) return;

                var weapon = ReflectionHelper.GetRightItem(__instance);
                if (weapon == null || !weapon.m_shared.m_useDurability) return;

                float before = weapon.m_durability;
                float drain = weapon.m_shared.m_useDurabilityDrain;
                weapon.m_durability -= drain;

                float maxDur = weapon.GetMaxDurability();
                float pct = maxDur > 0f ? (weapon.m_durability / maxDur * 100f) : 0f;

                CompanionsPlugin.Log.LogInfo(
                    $"[Durability] Weapon drain — \"{weapon.m_shared.m_name}\" " +
                    $"durability {before:F1} → {weapon.m_durability:F1} / {maxDur:F0} " +
                    $"({pct:F0}%) drain={drain:F1} " +
                    $"companion=\"{__instance.m_name}\"");

                if (weapon.m_durability <= 0f)
                {
                    weapon.m_durability = 0f;
                    __instance.UnequipItem(weapon, false);
                    CompanionsPlugin.Log.LogWarning(
                        $"[Durability] Weapon BROKEN — \"{weapon.m_shared.m_name}\" " +
                        $"unequipped, companion=\"{__instance.m_name}\"");
                }
            }
        }

        /// <summary>
        /// Drain armor durability when a companion takes damage.
        /// Vanilla calls Player.DamageArmorDurability() only for players.
        /// Also applies armor damage reduction (vanilla skips this for non-players).
        /// </summary>
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class ArmorDurability_Patch
        {
            static void Prefix(Character __instance, HitData hit)
            {
                // Only apply to companions, not players or monsters
                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return;

                // Apply armor damage reduction (vanilla only does this for IsPlayer)
                float armor = setup.GetTotalArmor();
                if (armor > 0f)
                {
                    float dmgBefore = hit.GetTotalDamage();
                    hit.ApplyArmor(armor);
                    float dmgAfter = hit.GetTotalDamage();
                    CompanionsPlugin.Log.LogInfo(
                        $"[Durability] Armor reduction — totalArmor={armor:F1} " +
                        $"dmg {dmgBefore:F1} → {dmgAfter:F1} " +
                        $"(blocked {dmgBefore - dmgAfter:F1}) " +
                        $"companion=\"{__instance.m_name}\"");
                }
            }

            static void Postfix(Character __instance, HitData hit)
            {
                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return;

                var humanoid = __instance as Humanoid;
                if (humanoid == null) return;

                // Collect equipped armor pieces
                var pieces = new List<ItemDrop.ItemData>(4);
                AddIfValid(pieces, _chestItem?.GetValue(humanoid) as ItemDrop.ItemData);
                AddIfValid(pieces, _legItem?.GetValue(humanoid) as ItemDrop.ItemData);
                AddIfValid(pieces, _helmetItem?.GetValue(humanoid) as ItemDrop.ItemData);
                AddIfValid(pieces, _shoulderItem?.GetValue(humanoid) as ItemDrop.ItemData);

                if (pieces.Count == 0)
                {
                    CompanionsPlugin.Log.LogInfo(
                        $"[Durability] No armor with durability equipped — skipping drain " +
                        $"companion=\"{__instance.m_name}\"");
                    return;
                }

                float totalDmg = hit.GetTotalPhysicalDamage() + hit.GetTotalElementalDamage();
                if (totalDmg <= 0f) return;

                // Drain a random armor piece (vanilla behavior)
                var piece = pieces[Random.Range(0, pieces.Count)];
                float before = piece.m_durability;
                piece.m_durability = Mathf.Max(0f, piece.m_durability - totalDmg);

                float maxDur = piece.GetMaxDurability();
                float pct = maxDur > 0f ? (piece.m_durability / maxDur * 100f) : 0f;

                CompanionsPlugin.Log.LogInfo(
                    $"[Durability] Armor drain — \"{piece.m_shared.m_name}\" " +
                    $"durability {before:F1} → {piece.m_durability:F1} / {maxDur:F0} " +
                    $"({pct:F0}%) dmgTaken={totalDmg:F1} " +
                    $"armorPieces={pieces.Count} companion=\"{__instance.m_name}\"");

                // Auto-unequip if broken
                if (piece.m_durability <= 0f)
                {
                    humanoid.UnequipItem(piece, false);
                    CompanionsPlugin.Log.LogWarning(
                        $"[Durability] Armor BROKEN — \"{piece.m_shared.m_name}\" " +
                        $"unequipped, companion=\"{__instance.m_name}\"");
                }
            }
        }

        private static void AddIfValid(List<ItemDrop.ItemData> list, ItemDrop.ItemData item)
        {
            if (item != null && item.m_shared != null && item.m_shared.m_useDurability)
                list.Add(item);
        }
    }
}
