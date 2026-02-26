using UnityEngine;

namespace Companions
{
    internal enum ResourceType { None, Wood, Stone, Ore }

    /// <summary>
    /// Pure static classification of GameObjects into resource types.
    /// Identifies TreeBase, TreeLog, MineRock, MineRock5, and Destructible resources
    /// and maps them to Wood, Stone, or Ore based on damage modifiers and name hints.
    /// </summary>
    internal static class ResourceClassifier
    {
        private static readonly string[] OreNameHints = {
            "copper", "tin", "silver", "iron", "flametal", "blackmetal",
            "obsidian", "sulfur", "crystal", "vein", "deposit", "ore",
            "mudpile", "scrap"
        };

        private static readonly string[] StoneNameHints = {
            "stone", "rock", "boulder", "marble", "grausten", "slate", "basalt"
        };

        internal static (GameObject, ResourceType) Classify(GameObject go)
        {
            if (go == null) return (null, ResourceType.None);

            var tree = go.GetComponentInParent<TreeBase>();
            if (tree != null)
                return (tree.gameObject, ResourceType.Wood);

            var log = go.GetComponentInParent<TreeLog>();
            if (log != null)
                return (log.gameObject, ResourceType.Wood);

            var rock = go.GetComponentInParent<MineRock>();
            if (rock != null)
                return (rock.gameObject, ClassifyMineRock(rock.gameObject));

            var rock5 = go.GetComponentInParent<MineRock5>();
            if (rock5 != null)
                return (rock5.gameObject, ClassifyMineRock(rock5.gameObject));

            var destr = go.GetComponentInParent<Destructible>();
            if (destr != null)
            {
                // Never harvest player-built pieces or storage
                if (destr.GetComponent<Piece>() != null ||
                    destr.GetComponent<Container>() != null)
                    return (null, ResourceType.None);

                int chopScore = GetModifierEffectiveness(destr.m_damages.m_chop);
                int pickaxeScore = GetModifierEffectiveness(destr.m_damages.m_pickaxe);

                if (chopScore <= -100 && pickaxeScore <= -100)
                    return (null, ResourceType.None);

                if (destr.m_destructibleType == DestructibleType.Tree)
                    return (destr.gameObject, ResourceType.Wood);

                if (pickaxeScore > chopScore)
                    return (destr.gameObject, ResourceType.Stone);
                if (chopScore > pickaxeScore)
                    return (destr.gameObject, ResourceType.Wood);

                string name = destr.gameObject.name.ToLowerInvariant();
                if (name.Contains("rock") || name.Contains("stone") || name.Contains("ore"))
                    return (destr.gameObject, ResourceType.Stone);
                if (name.Contains("tree") || name.Contains("log") || name.Contains("stump") ||
                    name.Contains("trunk") || name.Contains("branch") || name.Contains("wood"))
                    return (destr.gameObject, ResourceType.Wood);

                if (pickaxeScore >= 0)
                    return (destr.gameObject, ResourceType.Stone);
                if (chopScore >= 0)
                    return (destr.gameObject, ResourceType.Wood);
            }

            return (null, ResourceType.None);
        }

        internal static int GetMinToolTier(GameObject go)
        {
            if (go == null) return 999;

            var tree = go.GetComponentInParent<TreeBase>();
            if (tree != null) return tree.m_minToolTier;
            var log = go.GetComponentInParent<TreeLog>();
            if (log != null) return log.m_minToolTier;
            var rock = go.GetComponentInParent<MineRock>();
            if (rock != null) return rock.m_minToolTier;
            var rock5 = go.GetComponentInParent<MineRock5>();
            if (rock5 != null) return rock5.m_minToolTier;
            var destr = go.GetComponentInParent<Destructible>();
            if (destr != null) return destr.m_minToolTier;
            return 999;
        }

        internal static float GetRelevantToolDamage(ItemDrop.ItemData item, ResourceType type)
        {
            if (item == null || item.m_shared == null) return 0f;
            return type == ResourceType.Wood
                ? item.m_shared.m_damages.m_chop
                : item.m_shared.m_damages.m_pickaxe;
        }

        internal static int GetModifierEffectiveness(HitData.DamageModifier modifier)
        {
            switch (modifier)
            {
                case HitData.DamageModifier.Immune:
                case HitData.DamageModifier.Ignore:     return -100;
                case HitData.DamageModifier.VeryResistant: return -4;
                case HitData.DamageModifier.Resistant:     return -3;
                case HitData.DamageModifier.SlightlyResistant: return -2;
                case HitData.DamageModifier.Normal:        return 0;
                case HitData.DamageModifier.SlightlyWeak:  return 1;
                case HitData.DamageModifier.Weak:          return 2;
                case HitData.DamageModifier.VeryWeak:      return 3;
                default: return 0;
            }
        }

        private static ResourceType ClassifyMineRock(GameObject go)
        {
            if (go == null) return ResourceType.None;
            string name = go.name.ToLowerInvariant();

            for (int i = 0; i < OreNameHints.Length; i++)
                if (name.Contains(OreNameHints[i])) return ResourceType.Ore;

            for (int i = 0; i < StoneNameHints.Length; i++)
                if (name.Contains(StoneNameHints[i])) return ResourceType.Stone;

            return ResourceType.Ore;
        }
    }
}
