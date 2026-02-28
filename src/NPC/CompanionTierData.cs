using System.Collections.Generic;

namespace Companions
{
    public class CompanionTierDef
    {
        public string PrefabName;
        public string DisplayName;
        public string SourcePrefab;   // vanilla prefab to clone ("Player", "DvergerMage", etc.)
        public float  Health;
        public float  WalkSpeed;
        public float  RunSpeed;
        public bool   CanWearArmor;
        public bool   GiveStarterGear;
    }

    public static class CompanionTierData
    {
        public const int   Price          = 2000;
        public const float MaxCarryWeight = 300f;

        public static readonly CompanionTierDef Companion = new CompanionTierDef
        {
            PrefabName      = "HC_Companion",
            DisplayName     = "Companion",
            SourcePrefab    = "Player",
            Health          = 25f,
            WalkSpeed       = 2f,
            RunSpeed        = 7f,
            CanWearArmor    = true,
            GiveStarterGear = true,
        };

        // ── Dverger variants ────────────────────────────────────────────────
        // Each clones a different vanilla Dvergr prefab for its visual model.
        // All share the same companion behavior (CompanionAI, HarvestController, etc.).

        public static readonly CompanionTierDef DvergerWarrior = new CompanionTierDef
        {
            PrefabName      = "HC_DvergerWarrior",
            DisplayName     = "Warrior",
            SourcePrefab    = "Dverger",
            Health          = 25f,
            WalkSpeed       = 2f,
            RunSpeed        = 7f,
            CanWearArmor    = false,
            GiveStarterGear = false,
        };

        /// <summary>Backward-compatible: "HC_Dverger" matches existing saves.</summary>
        public static readonly CompanionTierDef Dverger = new CompanionTierDef
        {
            PrefabName      = "HC_Dverger",
            DisplayName     = "Mage",
            SourcePrefab    = "DvergerMage",
            Health          = 25f,
            WalkSpeed       = 2f,
            RunSpeed        = 7f,
            CanWearArmor    = false,
            GiveStarterGear = false,
        };

        public static readonly CompanionTierDef DvergerFire = new CompanionTierDef
        {
            PrefabName      = "HC_DvergerFire",
            DisplayName     = "Fire Mage",
            SourcePrefab    = "DvergerMageFire",
            Health          = 25f,
            WalkSpeed       = 2f,
            RunSpeed        = 7f,
            CanWearArmor    = false,
            GiveStarterGear = false,
        };

        public static readonly CompanionTierDef DvergerIce = new CompanionTierDef
        {
            PrefabName      = "HC_DvergerIce",
            DisplayName     = "Ice Mage",
            SourcePrefab    = "DvergerMageIce",
            Health          = 25f,
            WalkSpeed       = 2f,
            RunSpeed        = 7f,
            CanWearArmor    = false,
            GiveStarterGear = false,
        };

        public static readonly CompanionTierDef DvergerSupport = new CompanionTierDef
        {
            PrefabName      = "HC_DvergerSupport",
            DisplayName     = "Support",
            SourcePrefab    = "DvergerMageSupport",
            Health          = 25f,
            WalkSpeed       = 2f,
            RunSpeed        = 7f,
            CanWearArmor    = false,
            GiveStarterGear = false,
        };

        /// <summary>All Dverger variants in UI display order.
        /// Note: Dverger (Mage) is excluded — it duplicates DvergerFire.
        /// The def is kept for backward compatibility with existing saves.</summary>
        public static readonly CompanionTierDef[] DvergerVariants =
        {
            DvergerWarrior, DvergerFire, DvergerIce, DvergerSupport
        };

        // ── Variant detection ───────────────────────────────────────────────

        private static readonly HashSet<int> _dvergerHashes;

        static CompanionTierData()
        {
            _dvergerHashes = new HashSet<int>();
            foreach (var v in DvergerVariants)
                _dvergerHashes.Add(StringExtensionMethods.GetStableHashCode(v.PrefabName));
            // Legacy Mage variant (HC_Dverger) — not in UI but still exists in saves
            _dvergerHashes.Add(StringExtensionMethods.GetStableHashCode(Dverger.PrefabName));
        }

        /// <summary>
        /// Returns true if the given prefab hash belongs to any Dverger variant.
        /// Used by CompanionSetup.CanWearArmor() and starter gear checks.
        /// </summary>
        public static bool IsDvergerVariant(int prefabHash) => _dvergerHashes.Contains(prefabHash);
    }
}
