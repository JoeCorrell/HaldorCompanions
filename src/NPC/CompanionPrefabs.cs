using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    public static class CompanionPrefabs
    {
        private static GameObject _container;
        private static readonly FieldInfo _namedPrefabsField =
            AccessTools.Field(typeof(ZNetScene), "m_namedPrefabs");

        public static void Init(ZNetScene zNetScene)
        {
            if (_container == null)
            {
                _container = new GameObject("HC_CompanionPrefabs");
                Object.DontDestroyOnLoad(_container);
                _container.SetActive(false);
            }

            CreatePrefab(zNetScene);
            CompanionsPlugin.Log.LogInfo("[CompanionPrefabs] Registered companion prefab.");
        }

        private static void CreatePrefab(ZNetScene zNetScene)
        {
            var def = CompanionTierData.Companion;

            if (zNetScene.GetPrefab(def.PrefabName) != null)
                return;

            var playerPrefab = zNetScene.GetPrefab("Player");
            if (playerPrefab == null)
            {
                CompanionsPlugin.Log.LogError("[CompanionPrefabs] Player prefab not found!");
                return;
            }

            var go = Object.Instantiate(playerPrefab, _container.transform, false);
            go.name = def.PrefabName;

            // Strip player-specific components
            DestroyComponent<PlayerController>(go);
            DestroyComponent<Player>(go);
            DestroyComponent<Talker>(go);
            DestroyComponent<Skills>(go);

            // Humanoid (Character base — includes VisEquipment already on the prefab)
            var humanoid = go.AddComponent<Humanoid>();
            SetupHumanoid(humanoid, zNetScene, def);

            // CompanionAI — custom BaseAI subclass (follows player, attacks enemies)
            var ai = go.AddComponent<CompanionAI>();
            SetupCompanionAI(ai);

            // ZNetView
            var nview = go.GetComponent<ZNetView>();
            nview.m_persistent       = true;
            nview.m_distant          = false;
            nview.m_type             = (ZDO.ObjectType)0;
            nview.m_syncInitialScale = false;

            var syncXform = go.GetComponent<ZSyncTransform>();
            if (syncXform != null)
            {
                syncXform.m_syncPosition       = true;
                syncXform.m_syncRotation       = true;
                syncXform.m_syncScale          = false;
                syncXform.m_syncBodyVelocity   = false;
                syncXform.m_characterParentSync = false;
            }

            var syncAnim = go.GetComponent<ZSyncAnimation>();
            if (syncAnim != null) syncAnim.m_smoothCharacterSpeeds = true;

            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) rb.mass = 50f;

            // CompanionSetup — reads appearance from ZDO and applies it
            go.AddComponent<CompanionSetup>();
            CompanionsPlugin.Log.LogInfo("[CompanionPrefabs]   + CompanionSetup");

            // Container — for vanilla chest-style inventory interaction
            var container           = go.AddComponent<Container>();
            container.m_name        = "Companion";
            container.m_width       = 5;
            container.m_height      = 6;
            container.m_privacy     = Container.PrivacySetting.Public;
            container.m_checkGuardStone = false;
            container.m_openEffects  = new EffectList();
            container.m_closeEffects = new EffectList();

            // Interaction handler (hover text)
            go.AddComponent<CompanionInteract>();

            // Food system (must be before CompanionStamina — stamina reads food bonuses)
            go.AddComponent<CompanionFood>();

            // Custom stamina system (base 25 + food bonuses)
            go.AddComponent<CompanionStamina>();

            // Resource harvesting AI (active in Gather modes 1-3)
            go.AddComponent<HarvestController>();
            CompanionsPlugin.Log.LogInfo("[CompanionPrefabs]   + HarvestController");

            // Advanced combat AI (active in Follow/Stay modes)
            go.AddComponent<CombatController>();
            CompanionsPlugin.Log.LogInfo("[CompanionPrefabs]   + CombatController");

            // Overhead speech text (context-aware lines like Haldor)
            go.AddComponent<CompanionTalk>();

            // Campfire sitting + rested regen
            go.AddComponent<CompanionRest>();

            // Door handling (open, pass through, close behind)
            go.AddComponent<DoorHandler>();

            // Auto-repair at nearby workbenches
            go.AddComponent<RepairController>();

            // Register with ZNetScene
            int hash = StringExtensionMethods.GetStableHashCode(go.name);
            zNetScene.m_prefabs.Add(go);
            var namedPrefabs = _namedPrefabsField?.GetValue(zNetScene) as Dictionary<int, GameObject>;
            if (namedPrefabs != null)
                namedPrefabs[hash] = go;

            CompanionsPlugin.Log.LogInfo($"[CompanionPrefabs] Registered {def.PrefabName}");
        }

        private static void SetupHumanoid(Humanoid h, ZNetScene scene, CompanionTierDef def)
        {
            var c = (Character)h;

            c.m_name    = def.DisplayName;
            c.m_group   = "HC_Companion";
            c.m_faction = Character.Faction.Players;
            c.m_boss    = false;
            c.m_health  = def.Health;

            c.m_walkSpeed            = def.WalkSpeed;
            c.m_runSpeed             = def.RunSpeed;
            c.m_speed                = 5f;  // jog speed — matches player default
            c.m_crouchSpeed          = 2f;
            c.m_turnSpeed            = 300f;
            c.m_runTurnSpeed         = 300f;
            c.m_acceleration         = 1f;  // faster acceleration (was 0.6)
            c.m_jumpForce            = 8f;
            c.m_jumpForceForward     = 2f;
            c.m_jumpForceTiredFactor = 0.6f;

            c.m_canSwim          = true;
            c.m_swimDepth        = 1f;
            c.m_swimSpeed        = 2f;
            c.m_swimTurnSpeed    = 100f;
            c.m_swimAcceleration = 0.05f;

            c.m_groundTilt           = (Character.GroundTiltType)0;
            c.m_groundTiltSpeed      = 50f;
            c.m_eye                  = Utils.FindChild(h.gameObject.transform, "EyePos");
            c.m_tolerateWater        = true;
            c.m_staggerWhenBlocked   = true;
            c.m_staggerDamageFactor  = 0f;

            c.m_hitEffects.m_effectPrefabs       = BuildEffects(scene, "vfx_player_hit", "sfx_hit");
            c.m_critHitEffects.m_effectPrefabs   = BuildEffects(scene, "fx_crit");
            c.m_backstabHitEffects.m_effectPrefabs = BuildEffects(scene, "fx_backstab");
            c.m_deathEffects.m_effectPrefabs     = BuildEffects(scene, "vfx_ghost_death", "sfx_ghost_death");

            c.m_damageModifiers = new HitData.DamageModifiers
            {
                m_chop    = HitData.DamageModifier.Immune,
                m_pickaxe = HitData.DamageModifier.Immune,
                m_spirit  = HitData.DamageModifier.Immune
            };
        }

        private static void SetupCompanionAI(CompanionAI ai)
        {
            CompanionsPlugin.Log.LogInfo("[CompanionPrefabs] Setting up CompanionAI...");

            // BaseAI perception
            ai.m_viewRange            = 30f;
            ai.m_viewAngle            = 90f;
            ai.m_hearRange            = 9999f;
            ai.m_mistVision           = true;
            ai.m_alertedEffects       = new EffectList();
            ai.m_idleSound            = new EffectList();
            ai.m_idleSoundInterval    = 10f;
            ai.m_idleSoundChance      = 0f;

            // Pathfinding — m_moveMinAngle=10 lets companion start moving while
            // still turning (was 90f which froze movement until fully rotated).
            // m_jumpInterval=0 matches vanilla — no creature auto-jumps.
            ai.m_pathAgentType        = Pathfinding.AgentType.Humanoid;
            ai.m_moveMinAngle         = 10f;
            ai.m_smoothMovement       = true;
            ai.m_serpentMovement      = false;
            ai.m_jumpInterval         = 0f;
            ai.m_randomCircleInterval = 2f;
            ai.m_randomMoveInterval   = 9999f;
            ai.m_randomMoveRange      = 0f;
            ai.m_avoidFire            = false;
            ai.m_afraidOfFire         = false;
            ai.m_avoidWater           = true;   // vanilla default — humanoids avoid water
            ai.m_aggravatable         = false;

            CompanionsPlugin.Log.LogInfo(
                $"[CompanionPrefabs] CompanionAI configured: pathAgent=Humanoid " +
                $"moveMinAngle={ai.m_moveMinAngle} smoothMove={ai.m_smoothMovement} " +
                $"viewRange={ai.m_viewRange} hearRange={ai.m_hearRange} " +
                $"randomMoveInterval={ai.m_randomMoveInterval} " +
                $"randomMoveRange={ai.m_randomMoveRange}");
        }

        private static EffectList.EffectData[] BuildEffects(ZNetScene scene, params string[] names)
        {
            var list = new List<EffectList.EffectData>();
            foreach (var name in names)
            {
                var prefab = scene.GetPrefab(name);
                if (prefab != null)
                    list.Add(new EffectList.EffectData { m_prefab = prefab, m_enabled = true, m_variant = -1 });
                else
                    CompanionsPlugin.Log.LogWarning($"[CompanionPrefabs] Effect prefab not found: {name}");
            }
            return list.ToArray();
        }

        private static void DestroyComponent<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c != null) Object.DestroyImmediate(c);
        }
    }
}
