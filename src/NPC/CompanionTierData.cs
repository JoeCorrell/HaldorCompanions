namespace Companions
{
    public class CompanionTierDef
    {
        public string PrefabName;
        public string DisplayName;
        public float  Health;
        public float  WalkSpeed;
        public float  RunSpeed;
    }

    public static class CompanionTierData
    {
        public const int   Price          = 2000;
        public const float MaxCarryWeight = 300f;

        public static readonly CompanionTierDef Companion = new CompanionTierDef
        {
            PrefabName  = "HC_Companion",
            DisplayName = "Companion",
            Health      = 25f,
            WalkSpeed   = 2f,
            RunSpeed    = 7f,
        };
    }
}
