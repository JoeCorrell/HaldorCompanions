using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Centralized configuration for all companion systems.
    /// Every gameplay-relevant constant is exposed as a BepInEx ConfigEntry
    /// so players can tune behaviour via the in-game panel (F8) or the .cfg file.
    /// </summary>
    internal static class ModConfig
    {
        // Category → ordered list of entries (used by ConfigPanel to build UI)
        internal static readonly Dictionary<string, List<ConfigEntryBase>> Categories
            = new Dictionary<string, List<ConfigEntryBase>>();

        // Ordered category names for consistent tab display
        internal static readonly List<string> CategoryOrder = new List<string>();

        // ══════════════════════════════════════════════════════════════════════
        //  General
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<int>   CompanionPrice;
        internal static ConfigEntry<bool>  SpawnStarterCompanion;
        internal static ConfigEntry<float> MaxCarryWeight;
        internal static ConfigEntry<float> BaseHealth;
        internal static ConfigEntry<float> BaseStamina;
        internal static ConfigEntry<int>   MaxFoodSlots;
        internal static ConfigEntry<float> MaxLeashDistance;
        internal static ConfigEntry<float> FollowTeleportDistance;

        // ══════════════════════════════════════════════════════════════════════
        //  Combat
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> HealthRetreatPct;
        internal static ConfigEntry<float> StaminaRetreatPct;
        internal static ConfigEntry<float> HealthRecoverPct;
        internal static ConfigEntry<float> StaminaRecoverPct;
        internal static ConfigEntry<float> AttackCooldown;
        internal static ConfigEntry<float> PowerAttackCooldown;
        internal static ConfigEntry<float> BowMaxRange;
        internal static ConfigEntry<float> BowMinRange;
        internal static ConfigEntry<float> BowSwitchDistance;
        internal static ConfigEntry<float> MeleeSwitchDistance;
        internal static ConfigEntry<float> RetreatDistance;
        internal static ConfigEntry<float> BowDrawTime;
        internal static ConfigEntry<float> BowFireInterval;
        internal static ConfigEntry<float> DodgeCooldown;
        internal static ConfigEntry<float> DodgeStaminaCost;
        internal static ConfigEntry<float> ThreatDetectRange;
        internal static ConfigEntry<float> ProjectileDetectRange;
        internal static ConfigEntry<float> BlockSafetyCap;
        internal static ConfigEntry<float> CounterWindowDuration;
        internal static ConfigEntry<float> ConsumeCooldown;

        // ══════════════════════════════════════════════════════════════════════
        //  AI & Following
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> FormationOffset;
        internal static ConfigEntry<float> FormationCatchupDist;
        internal static ConfigEntry<float> GiveUpTime;
        internal static ConfigEntry<float> UpdateTargetIntervalNear;
        internal static ConfigEntry<float> UpdateTargetIntervalFar;
        internal static ConfigEntry<float> SelfDefenseRange;
        internal static ConfigEntry<float> AutoPickupRange;
        internal static ConfigEntry<float> TombstoneScanInterval;
        internal static ConfigEntry<float> TombstoneNavTimeout;
        internal static ConfigEntry<float> WanderMoveInterval;

        // ══════════════════════════════════════════════════════════════════════
        //  Movement
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> WalkSpeed;
        internal static ConfigEntry<float> RunSpeed;
        internal static ConfigEntry<float> SwimSpeed;
        internal static ConfigEntry<float> TurnSpeed;
        internal static ConfigEntry<float> ViewRange;
        internal static ConfigEntry<float> ViewAngle;

        // ══════════════════════════════════════════════════════════════════════
        //  Food
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> FoodRegenInterval;
        internal static ConfigEntry<float> MeadCooldown;
        internal static ConfigEntry<float> HealthMeadThreshold;
        internal static ConfigEntry<float> StaminaMeadThreshold;
        internal static ConfigEntry<float> ConsumeCheckInterval;

        // ══════════════════════════════════════════════════════════════════════
        //  Stamina
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> StaminaRegenRate;
        internal static ConfigEntry<float> StaminaRunDrain;
        internal static ConfigEntry<float> StaminaSneakDrain;
        internal static ConfigEntry<float> StaminaSwimDrain;
        internal static ConfigEntry<float> StaminaRegenDelay;

        // ══════════════════════════════════════════════════════════════════════
        //  Harvest
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> HarvestScanRadius;
        internal static ConfigEntry<float> HarvestScanInterval;
        internal static ConfigEntry<float> HarvestAttackInterval;
        internal static ConfigEntry<float> HarvestOverweightThreshold;
        internal static ConfigEntry<float> HarvestDropScanRadius;
        internal static ConfigEntry<float> HarvestBlacklistDuration;
        internal static ConfigEntry<string> ForageItems;
        internal static ConfigEntry<float> HomeZoneRadius;

        // ══════════════════════════════════════════════════════════════════════
        //  Smelting
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> SmeltScanRadius;
        internal static ConfigEntry<float> SmeltScanInterval;
        internal static ConfigEntry<int>   SmeltMaxCarryOre;
        internal static ConfigEntry<int>   SmeltMaxCarryFuel;
        internal static ConfigEntry<float> SmeltUseDistance;

        // ══════════════════════════════════════════════════════════════════════
        //  Farming
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> FarmScanRadius;
        internal static ConfigEntry<float> FarmScanInterval;
        internal static ConfigEntry<float> FarmPlantSpacing;
        internal static ConfigEntry<float> FarmUseDistance;

        // ══════════════════════════════════════════════════════════════════════
        //  Fishing
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> FishScanRadius;
        internal static ConfigEntry<float> FishWaitTimeMin;
        internal static ConfigEntry<float> FishWaitTimeMax;
        internal static ConfigEntry<float> FishReelTimeMin;
        internal static ConfigEntry<float> FishReelTimeMax;
        internal static ConfigEntry<float> FishHookChance;
        internal static ConfigEntry<float> FishMissChance;
        internal static ConfigEntry<float> FishReelStaminaDrain;

        // ══════════════════════════════════════════════════════════════════════
        //  Repair
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> RepairDurabilityThreshold;
        internal static ConfigEntry<float> RepairScanRadius;
        internal static ConfigEntry<float> RepairScanInterval;
        internal static ConfigEntry<float> RepairTickInterval;
        internal static ConfigEntry<float> RepairUseDistance;

        // ══════════════════════════════════════════════════════════════════════
        //  Homestead
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> HomesteadScanRadius;
        internal static ConfigEntry<float> HomesteadScanInterval;
        internal static ConfigEntry<float> HomesteadFuelThreshold;
        internal static ConfigEntry<float> HomesteadTaskSlotTime;
        internal static ConfigEntry<float> HomesteadSmeltSlotTime;
        internal static ConfigEntry<float> HomesteadScanBackoff;
        internal static ConfigEntry<float> HomesteadUseDistance;

        // ══════════════════════════════════════════════════════════════════════
        //  Rest
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> RestHealRate;
        internal static ConfigEntry<float> RestPlayerRange;
        internal static ConfigEntry<float> RestFireRange;
        internal static ConfigEntry<float> RestWarmupTime;
        internal static ConfigEntry<float> RestDirectedTimeout;

        // ══════════════════════════════════════════════════════════════════════
        //  Speech
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> SpeechMinInterval;
        internal static ConfigEntry<float> SpeechMaxInterval;
        internal static ConfigEntry<float> SpeechCullDistance;
        internal static ConfigEntry<float> SpeechDuration;
        internal static ConfigEntry<float> SpeechSayCooldown;

        // ══════════════════════════════════════════════════════════════════════
        //  Skills
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> SkillDeathLossFactor;

        // ══════════════════════════════════════════════════════════════════════
        //  Controls
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<KeyCode> DirectTargetKey;
        internal static ConfigEntry<KeyCode> RadialMenuKey;
        internal static ConfigEntry<KeyCode> ConfigPanelKey;
        internal static ConfigEntry<KeyCode> FarmZoneKey;
        internal static ConfigEntry<KeyCode> FarmZoneModifier;

        // ══════════════════════════════════════════════════════════════════════
        //  Speech Display
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<bool> MaleShowSpeechText;
        internal static ConfigEntry<bool> MaleEnableVoiceAudio;
        internal static ConfigEntry<bool> FemaleShowSpeechText;
        internal static ConfigEntry<bool> FemaleEnableVoiceAudio;

        // ══════════════════════════════════════════════════════════════════════
        //  Door
        // ══════════════════════════════════════════════════════════════════════
        internal static ConfigEntry<float> DoorScanRadius;
        internal static ConfigEntry<float> DoorInteractDist;
        internal static ConfigEntry<float> DoorProactiveScanRadius;
        internal static ConfigEntry<float> DoorFollowDistMin;
        internal static ConfigEntry<float> DoorCloseDistance;

        // ══════════════════════════════════════════════════════════════════════
        //  Init
        // ══════════════════════════════════════════════════════════════════════

        internal static void Init(ConfigFile cfg)
        {
            // ── General ──────────────────────────────────────────────────────
            CompanionPrice          = Bind(cfg, "General", "CompanionPrice", 2000, "Gold cost to purchase a companion", new AcceptableValueRange<int>(0, 50000));
            SpawnStarterCompanion   = Bind(cfg, "General", "SpawnStarterCompanion", true, "Spawn a free companion when entering a new world for the first time");
            MaxCarryWeight          = Bind(cfg, "General", "MaxCarryWeight", 300f, "Maximum carry weight for companions", new AcceptableValueRange<float>(50f, 2000f));
            BaseHealth              = Bind(cfg, "General", "BaseHealth", 25f, "Base HP before food bonuses", new AcceptableValueRange<float>(1f, 500f));
            BaseStamina             = Bind(cfg, "General", "BaseStamina", 50f, "Base stamina before food bonuses", new AcceptableValueRange<float>(1f, 500f));
            MaxFoodSlots            = Bind(cfg, "General", "MaxFoodSlots", 3, "Number of food slots", new AcceptableValueRange<int>(1, 5));
            MaxLeashDistance        = Bind(cfg, "General", "MaxLeashDistance", 50f, "StayHome teleport-home leash distance", new AcceptableValueRange<float>(10f, 200f));
            FollowTeleportDistance  = Bind(cfg, "General", "FollowTeleportDistance", 50f, "Distance from player before companion warps", new AcceptableValueRange<float>(20f, 200f));

            // ── Combat ───────────────────────────────────────────────────────
            HealthRetreatPct        = Bind(cfg, "Combat", "HealthRetreatPct", 0.30f, "Retreat when HP drops below this %", new AcceptableValueRange<float>(0.05f, 0.90f));
            StaminaRetreatPct       = Bind(cfg, "Combat", "StaminaRetreatPct", 0.15f, "Retreat when stamina drops below this %", new AcceptableValueRange<float>(0.05f, 0.90f));
            HealthRecoverPct        = Bind(cfg, "Combat", "HealthRecoverPct", 0.50f, "Re-engage when HP recovers to this %", new AcceptableValueRange<float>(0.10f, 1.0f));
            StaminaRecoverPct       = Bind(cfg, "Combat", "StaminaRecoverPct", 0.30f, "Re-engage when stamina recovers to this %", new AcceptableValueRange<float>(0.10f, 1.0f));
            AttackCooldown          = Bind(cfg, "Combat", "AttackCooldown", 0.225f, "Seconds between melee attacks", new AcceptableValueRange<float>(0.1f, 2.0f));
            PowerAttackCooldown     = Bind(cfg, "Combat", "PowerAttackCooldown", 3.0f, "Seconds between power attacks", new AcceptableValueRange<float>(0.5f, 10f));
            BowMaxRange             = Bind(cfg, "Combat", "BowMaxRange", 30f, "Maximum bow engagement range", new AcceptableValueRange<float>(10f, 80f));
            BowMinRange             = Bind(cfg, "Combat", "BowMinRange", 10f, "Always use melee below this distance", new AcceptableValueRange<float>(1f, 30f));
            BowSwitchDistance       = Bind(cfg, "Combat", "BowSwitchDistance", 20f, "Switch TO bow when target exceeds this distance", new AcceptableValueRange<float>(5f, 60f));
            MeleeSwitchDistance     = Bind(cfg, "Combat", "MeleeSwitchDistance", 12f, "Switch FROM bow when target is closer than this", new AcceptableValueRange<float>(3f, 40f));
            RetreatDistance         = Bind(cfg, "Combat", "RetreatDistance", 12f, "How far to retreat from enemy", new AcceptableValueRange<float>(3f, 30f));
            BowDrawTime             = Bind(cfg, "Combat", "BowDrawTime", 1.2f, "Seconds to fully draw bow", new AcceptableValueRange<float>(0.3f, 3.0f));
            BowFireInterval         = Bind(cfg, "Combat", "BowFireInterval", 2.5f, "Seconds between bow shots", new AcceptableValueRange<float>(0.5f, 5.0f));
            DodgeCooldown           = Bind(cfg, "Combat", "DodgeCooldown", 2.5f, "Seconds between dodges", new AcceptableValueRange<float>(0.5f, 10f));
            DodgeStaminaCost        = Bind(cfg, "Combat", "DodgeStaminaCost", 15f, "Stamina consumed per dodge", new AcceptableValueRange<float>(0f, 50f));
            ThreatDetectRange       = Bind(cfg, "Combat", "ThreatDetectRange", 8f, "Range to detect enemy attacks for blocking", new AcceptableValueRange<float>(2f, 25f));
            ProjectileDetectRange   = Bind(cfg, "Combat", "ProjectileDetectRange", 20f, "Range to detect incoming projectiles", new AcceptableValueRange<float>(5f, 50f));
            BlockSafetyCap          = Bind(cfg, "Combat", "BlockSafetyCap", 3.0f, "Max continuous block time before forced counter", new AcceptableValueRange<float>(1f, 10f));
            CounterWindowDuration   = Bind(cfg, "Combat", "CounterWindowDuration", 0.8f, "Post-parry attack window duration", new AcceptableValueRange<float>(0.2f, 3.0f));
            ConsumeCooldown         = Bind(cfg, "Combat", "ConsumeCooldown", 10f, "Seconds between food consumption in combat", new AcceptableValueRange<float>(1f, 60f));

            // ── AI & Following ───────────────────────────────────────────────
            FormationOffset         = Bind(cfg, "AI", "FormationOffset", 3f, "Lateral spacing between companions in formation", new AcceptableValueRange<float>(1f, 10f));
            FormationCatchupDist    = Bind(cfg, "AI", "FormationCatchupDist", 15f, "Distance to sprint to catch up with formation", new AcceptableValueRange<float>(5f, 50f));
            GiveUpTime              = Bind(cfg, "AI", "GiveUpTime", 30f, "Seconds before abandoning a target", new AcceptableValueRange<float>(5f, 120f));
            UpdateTargetIntervalNear = Bind(cfg, "AI", "UpdateTargetIntervalNear", 2f, "Target scan interval when player is near", new AcceptableValueRange<float>(0.5f, 10f));
            UpdateTargetIntervalFar = Bind(cfg, "AI", "UpdateTargetIntervalFar", 6f, "Target scan interval when player is far", new AcceptableValueRange<float>(1f, 30f));
            SelfDefenseRange        = Bind(cfg, "AI", "SelfDefenseRange", 10f, "Self-defense targeting range in gather mode", new AcceptableValueRange<float>(3f, 30f));
            AutoPickupRange         = Bind(cfg, "AI", "AutoPickupRange", 2f, "Item auto-pickup radius", new AcceptableValueRange<float>(0.5f, 10f));
            TombstoneScanInterval   = Bind(cfg, "AI", "TombstoneScanInterval", 5f, "Seconds between tombstone scans", new AcceptableValueRange<float>(1f, 30f));
            TombstoneNavTimeout     = Bind(cfg, "AI", "TombstoneNavTimeout", 120f, "Max tombstone recovery navigation time", new AcceptableValueRange<float>(10f, 600f));
            WanderMoveInterval      = Bind(cfg, "AI", "WanderMoveInterval", 5f, "Seconds between random wander movements at home", new AcceptableValueRange<float>(1f, 30f));

            // ── Movement ─────────────────────────────────────────────────────
            WalkSpeed               = Bind(cfg, "Movement", "WalkSpeed", 2f, "Walking speed (requires world reload)", new AcceptableValueRange<float>(0.5f, 5f));
            RunSpeed                = Bind(cfg, "Movement", "RunSpeed", 7f, "Running speed (requires world reload)", new AcceptableValueRange<float>(2f, 15f));
            SwimSpeed               = Bind(cfg, "Movement", "SwimSpeed", 2f, "Swimming speed (requires world reload)", new AcceptableValueRange<float>(0.5f, 5f));
            TurnSpeed               = Bind(cfg, "Movement", "TurnSpeed", 300f, "Turn speed (requires world reload)", new AcceptableValueRange<float>(50f, 600f));
            ViewRange               = Bind(cfg, "Movement", "ViewRange", 40f, "Visual detection range (requires world reload)", new AcceptableValueRange<float>(5f, 100f));
            ViewAngle               = Bind(cfg, "Movement", "ViewAngle", 90f, "Field of view angle (requires world reload)", new AcceptableValueRange<float>(30f, 360f));

            // ── Food ─────────────────────────────────────────────────────────
            FoodRegenInterval       = Bind(cfg, "Food", "FoodRegenInterval", 10f, "Seconds between food regen ticks", new AcceptableValueRange<float>(1f, 60f));
            MeadCooldown            = Bind(cfg, "Food", "MeadCooldown", 10f, "Mead consumption cooldown", new AcceptableValueRange<float>(1f, 60f));
            HealthMeadThreshold     = Bind(cfg, "Food", "HealthMeadThreshold", 0.50f, "Use health mead below this HP %", new AcceptableValueRange<float>(0.1f, 0.9f));
            StaminaMeadThreshold    = Bind(cfg, "Food", "StaminaMeadThreshold", 0.25f, "Use stamina mead below this stamina %", new AcceptableValueRange<float>(0.05f, 0.9f));
            ConsumeCheckInterval    = Bind(cfg, "Food", "ConsumeCheckInterval", 1f, "Auto-consume check interval", new AcceptableValueRange<float>(0.5f, 10f));

            // ── Stamina ──────────────────────────────────────────────────────
            StaminaRegenRate        = Bind(cfg, "Stamina", "RegenRate", 6f, "Stamina per second while idle", new AcceptableValueRange<float>(1f, 30f));
            StaminaRunDrain         = Bind(cfg, "Stamina", "RunDrainRate", 10f, "Stamina drain per second while running", new AcceptableValueRange<float>(1f, 30f));
            StaminaSneakDrain       = Bind(cfg, "Stamina", "SneakDrainRate", 5f, "Stamina drain per second while sneaking", new AcceptableValueRange<float>(1f, 30f));
            StaminaSwimDrain        = Bind(cfg, "Stamina", "SwimDrainRate", 10f, "Stamina drain per second while swimming", new AcceptableValueRange<float>(1f, 30f));
            StaminaRegenDelay       = Bind(cfg, "Stamina", "RegenDelay", 1f, "Seconds after stamina use before regen starts", new AcceptableValueRange<float>(0.1f, 5f));

            // ── Harvest ──────────────────────────────────────────────────────
            HarvestScanRadius       = Bind(cfg, "Harvest", "ScanRadius", 30f, "Resource scan radius", new AcceptableValueRange<float>(5f, 100f));
            HarvestScanInterval     = Bind(cfg, "Harvest", "ScanInterval", 4f, "Seconds between resource scans", new AcceptableValueRange<float>(1f, 30f));
            HarvestAttackInterval   = Bind(cfg, "Harvest", "AttackInterval", 2.5f, "Seconds between harvest swings", new AcceptableValueRange<float>(0.5f, 5f));
            HarvestOverweightThreshold = Bind(cfg, "Harvest", "OverweightThreshold", 298f, "Stop gathering at this carry weight", new AcceptableValueRange<float>(50f, 500f));
            HarvestDropScanRadius   = Bind(cfg, "Harvest", "DropScanRadius", 8f, "Drop collection scan radius", new AcceptableValueRange<float>(2f, 20f));
            HarvestBlacklistDuration = Bind(cfg, "Harvest", "BlacklistDuration", 60f, "Seconds a failed target stays blacklisted", new AcceptableValueRange<float>(5f, 300f));
            ForageItems              = Bind(cfg, "Harvest", "ForageItems", "*", "Comma-separated item prefab names to forage (e.g. Mushroom,Raspberry,Thistle). Use * for all pickables");
            HomeZoneRadius           = Bind(cfg, "StayHome", "HomeZoneRadius", 50f, "Default gather/homestead zone radius in meters around home position", new AcceptableValueRange<float>(5f, 200f));

            // ── Smelting ─────────────────────────────────────────────────────
            SmeltScanRadius         = Bind(cfg, "Smelting", "ScanRadius", 25f, "Smelter scan radius", new AcceptableValueRange<float>(5f, 100f));
            SmeltScanInterval       = Bind(cfg, "Smelting", "ScanInterval", 3f, "Seconds between smelter scans", new AcceptableValueRange<float>(1f, 30f));
            SmeltMaxCarryOre        = Bind(cfg, "Smelting", "MaxCarryOre", 20, "Maximum ore items per trip", new AcceptableValueRange<int>(1, 100));
            SmeltMaxCarryFuel       = Bind(cfg, "Smelting", "MaxCarryFuel", 40, "Maximum fuel items per trip", new AcceptableValueRange<int>(1, 100));
            SmeltUseDistance         = Bind(cfg, "Smelting", "UseDistance", 2.5f, "Smelter interaction range", new AcceptableValueRange<float>(1f, 5f));

            // ── Farming ──────────────────────────────────────────────────────
            FarmScanRadius          = Bind(cfg, "Farming", "ScanRadius", 30f, "Crop/soil scan radius", new AcceptableValueRange<float>(5f, 100f));
            FarmScanInterval        = Bind(cfg, "Farming", "ScanInterval", 4f, "Seconds between farm scans", new AcceptableValueRange<float>(1f, 30f));
            FarmPlantSpacing        = Bind(cfg, "Farming", "PlantSpacing", 0.75f, "Min spacing between crops (uses max of this and plant grow radius)", new AcceptableValueRange<float>(0.25f, 3f));
            FarmUseDistance         = Bind(cfg, "Farming", "UseDistance", 2f, "Interaction range for crops and chests", new AcceptableValueRange<float>(1f, 5f));

            // ── Fishing ─────────────────────────────────────────────────────
            FishScanRadius          = Bind(cfg, "Fishing", "ScanRadius", 30f, "Water scan radius", new AcceptableValueRange<float>(5f, 100f));
            FishWaitTimeMin         = Bind(cfg, "Fishing", "WaitTimeMin", 15f, "Min seconds waiting for nibble", new AcceptableValueRange<float>(1f, 120f));
            FishWaitTimeMax         = Bind(cfg, "Fishing", "WaitTimeMax", 20f, "Max seconds waiting for nibble", new AcceptableValueRange<float>(1f, 180f));
            FishReelTimeMin         = Bind(cfg, "Fishing", "ReelTimeMin", 4f, "Min seconds to reel in", new AcceptableValueRange<float>(1f, 30f));
            FishReelTimeMax         = Bind(cfg, "Fishing", "ReelTimeMax", 6f, "Max seconds to reel in", new AcceptableValueRange<float>(1f, 60f));
            FishHookChance          = Bind(cfg, "Fishing", "HookChance", 0.85f, "Chance to hook after nibble", new AcceptableValueRange<float>(0.1f, 1f));
            FishMissChance          = Bind(cfg, "Fishing", "MissChance", 0.10f, "Chance of no nibble per wait", new AcceptableValueRange<float>(0f, 0.9f));
            FishReelStaminaDrain    = Bind(cfg, "Fishing", "ReelStaminaDrain", 3f, "Stamina drain per second while reeling", new AcceptableValueRange<float>(0f, 20f));

            // ── Repair ───────────────────────────────────────────────────────
            RepairDurabilityThreshold = Bind(cfg, "Repair", "DurabilityThreshold", 0.50f, "Repair items below this durability %", new AcceptableValueRange<float>(0.1f, 0.9f));
            RepairScanRadius        = Bind(cfg, "Repair", "ScanRadius", 20f, "Crafting station scan radius", new AcceptableValueRange<float>(5f, 60f));
            RepairScanInterval      = Bind(cfg, "Repair", "ScanInterval", 5f, "Seconds between repair scans", new AcceptableValueRange<float>(1f, 30f));
            RepairTickInterval      = Bind(cfg, "Repair", "RepairTickInterval", 0.8f, "Seconds between individual item repairs", new AcceptableValueRange<float>(0.1f, 5f));
            RepairUseDistance        = Bind(cfg, "Repair", "UseDistance", 2.5f, "Crafting station interaction range", new AcceptableValueRange<float>(1f, 5f));

            // ── Homestead ────────────────────────────────────────────────────
            HomesteadScanRadius     = Bind(cfg, "Homestead", "ScanRadius", 40f, "Maintenance scan radius from home position", new AcceptableValueRange<float>(10f, 100f));
            HomesteadScanInterval   = Bind(cfg, "Homestead", "ScanInterval", 5f, "Seconds between maintenance scans", new AcceptableValueRange<float>(1f, 30f));
            HomesteadFuelThreshold  = Bind(cfg, "Homestead", "FuelThreshold", 0.50f, "Refuel fires below this fuel %", new AcceptableValueRange<float>(0.1f, 0.9f));
            HomesteadTaskSlotTime   = Bind(cfg, "Homestead", "TaskSlotTime", 60f, "Seconds per task rotation (repair/refuel/sort)", new AcceptableValueRange<float>(10f, 300f));
            HomesteadSmeltSlotTime  = Bind(cfg, "Homestead", "SmeltSlotTime", 60f, "Seconds for smelting turn in rotation", new AcceptableValueRange<float>(10f, 300f));
            HomesteadScanBackoff    = Bind(cfg, "Homestead", "ScanBackoff", 30f, "Extended scan interval when nothing to do", new AcceptableValueRange<float>(5f, 120f));
            HomesteadUseDistance    = Bind(cfg, "Homestead", "UseDistance", 2.0f, "Interaction range for fires/chests", new AcceptableValueRange<float>(1f, 5f));

            // ── Rest ─────────────────────────────────────────────────────────
            RestHealRate            = Bind(cfg, "Rest", "HealRate", 2f, "HP per second while resting", new AcceptableValueRange<float>(0.5f, 20f));
            RestPlayerRange         = Bind(cfg, "Rest", "PlayerRange", 5f, "Max player distance for auto-sit", new AcceptableValueRange<float>(2f, 20f));
            RestFireRange           = Bind(cfg, "Rest", "FireRange", 5f, "Campfire detection radius", new AcceptableValueRange<float>(2f, 20f));
            RestWarmupTime          = Bind(cfg, "Rest", "WarmupTime", 20f, "Seconds of sitting before Rested buff applies", new AcceptableValueRange<float>(5f, 120f));
            RestDirectedTimeout     = Bind(cfg, "Rest", "DirectedTimeout", 300f, "Directed sit timeout (seconds)", new AcceptableValueRange<float>(30f, 1800f));

            // ── Speech ───────────────────────────────────────────────────────
            SpeechMinInterval       = Bind(cfg, "Speech", "MinInterval", 20f, "Minimum seconds between ambient chatter", new AcceptableValueRange<float>(5f, 120f));
            SpeechMaxInterval       = Bind(cfg, "Speech", "MaxInterval", 40f, "Maximum seconds between ambient chatter", new AcceptableValueRange<float>(10f, 300f));
            SpeechCullDistance      = Bind(cfg, "Speech", "CullDistance", 20f, "Max distance at which speech is visible", new AcceptableValueRange<float>(5f, 60f));
            SpeechDuration          = Bind(cfg, "Speech", "Duration", 5f, "How long speech text stays visible", new AcceptableValueRange<float>(1f, 15f));
            SpeechSayCooldown       = Bind(cfg, "Speech", "SayCooldown", 20f, "Directed speech cooldown", new AcceptableValueRange<float>(5f, 120f));

            // ── Skills ───────────────────────────────────────────────────────
            SkillDeathLossFactor    = Bind(cfg, "Skills", "DeathLossFactor", 0.25f, "Fraction of skills lost on death (0 = none)", new AcceptableValueRange<float>(0f, 1.0f));

            // ── Controls ─────────────────────────────────────────────────────
            DirectTargetKey         = Bind(cfg, "Controls", "DirectTargetKey", KeyCode.Z, "Directed command / focus-fire key");
            RadialMenuKey           = Bind(cfg, "Controls", "RadialMenuKey", KeyCode.E, "Radial command menu key (hold while hovering companion)");
            ConfigPanelKey          = Bind(cfg, "Controls", "ConfigPanelKey", KeyCode.F8, "Open the in-game config panel");
            FarmZoneKey             = Bind(cfg, "Controls", "FarmZoneKey", KeyCode.Z, "Farm zone placement key (press with modifier to open zone editor)");
            FarmZoneModifier        = Bind(cfg, "Controls", "FarmZoneModifier", KeyCode.LeftAlt, "Modifier key for farm zone placement (hold this + FarmZoneKey)");

            // ── Speech Display ───────────────────────────────────────────────
            MaleShowSpeechText      = Bind(cfg, "Speech Display", "MaleShowOverheadText", false, "Show overhead speech text for male companions");
            MaleEnableVoiceAudio    = Bind(cfg, "Speech Display", "MaleEnableVoiceAudio", true, "Play voice audio for male companions");
            FemaleShowSpeechText    = Bind(cfg, "Speech Display", "FemaleShowOverheadText", true, "Show overhead speech text for female companions");
            FemaleEnableVoiceAudio  = Bind(cfg, "Speech Display", "FemaleEnableVoiceAudio", false, "Play voice audio for female companions");

            // ── Door ─────────────────────────────────────────────────────────
            DoorScanRadius          = Bind(cfg, "Door", "ScanRadius", 5f, "Stuck-mode door scan radius", new AcceptableValueRange<float>(2f, 15f));
            DoorInteractDist        = Bind(cfg, "Door", "InteractDist", 2f, "Door interaction range", new AcceptableValueRange<float>(1f, 5f));
            DoorProactiveScanRadius = Bind(cfg, "Door", "ProactiveScanRadius", 15f, "Proactive door scan radius", new AcceptableValueRange<float>(5f, 30f));
            DoorFollowDistMin       = Bind(cfg, "Door", "FollowDistMin", 4f, "Min distance from target to engage door handler", new AcceptableValueRange<float>(2f, 15f));
            DoorCloseDistance       = Bind(cfg, "Door", "CloseDistance", 3.5f, "Distance past door before closing it", new AcceptableValueRange<float>(1f, 10f));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private static ConfigEntry<T> Bind<T>(ConfigFile cfg, string category, string key,
            T defaultValue, string desc, AcceptableValueBase range = null)
        {
            var entry = cfg.Bind(category, key, defaultValue,
                new ConfigDescription(desc, range));

            if (!Categories.TryGetValue(category, out var list))
            {
                list = new List<ConfigEntryBase>();
                Categories[category] = list;
                CategoryOrder.Add(category);
            }
            list.Add(entry);

            return entry;
        }
    }
}
