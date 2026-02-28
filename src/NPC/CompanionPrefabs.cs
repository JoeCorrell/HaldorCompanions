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

            CreateCompanionPrefab(zNetScene);
            foreach (var variant in CompanionTierData.DvergerVariants)
                CreateDvergerPrefab(zNetScene, variant);
            CompanionsPlugin.Log.LogInfo("[CompanionPrefabs] Registered companion prefabs.");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Companion — Player clone with full appearance customization
        // ═══════════════════════════════════════════════════════════════════

        private static void CreateCompanionPrefab(ZNetScene zNetScene)
        {
            var def = CompanionTierData.Companion;
            if (zNetScene.GetPrefab(def.PrefabName) != null) return;

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

            // Add fresh Humanoid (Player component was destroyed, taking Humanoid base with it)
            var playerHumanoid = playerPrefab.GetComponent<Humanoid>();
            var humanoid = go.AddComponent<Humanoid>();
            if (playerHumanoid != null)
            {
                humanoid.m_unarmedWeapon = playerHumanoid.m_unarmedWeapon;
                humanoid.m_consumeItemEffects = playerHumanoid.m_consumeItemEffects;
                int effectCount = humanoid.m_consumeItemEffects?.m_effectPrefabs?.Length ?? 0;
                CompanionsPlugin.Log.LogInfo(
                    $"[CompanionPrefabs]   Copied m_unarmedWeapon + m_consumeItemEffects ({effectCount} effects)");
            }
            SetupHumanoid(humanoid, zNetScene, def);

            // Fix CharacterAnimEvent — destroying Player broke its m_character reference.
            // It re-resolves in its own Awake via GetComponentInParent<Character>(), but
            // we also set it here so the prefab reference is correct.
            var animEvent = go.GetComponentInChildren<CharacterAnimEvent>();
            if (animEvent != null)
            {
                var characterField = AccessTools.Field(typeof(CharacterAnimEvent), "m_character");
                if (characterField != null)
                    characterField.SetValue(animEvent, humanoid);
            }

            // CompanionAI — custom BaseAI subclass
            var ai = go.AddComponent<CompanionAI>();
            SetupCompanionAI(ai);

            SetupSharedComponents(go, def, zNetScene);
            RegisterPrefab(go, zNetScene);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Dverger — vanilla Dvergr clone (variant determines source prefab)
        // ═══════════════════════════════════════════════════════════════════

        private static void CreateDvergerPrefab(ZNetScene zNetScene, CompanionTierDef def)
        {
            if (zNetScene.GetPrefab(def.PrefabName) != null) return;

            var dvergerPrefab = zNetScene.GetPrefab(def.SourcePrefab);
            if (dvergerPrefab == null)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[CompanionPrefabs] Vanilla prefab \"{def.SourcePrefab}\" not found — " +
                    $"{def.DisplayName} dverger variant will not be available.");
                return;
            }

            var go = Object.Instantiate(dvergerPrefab, _container.transform, false);
            go.name = def.PrefabName;

            // Strip vanilla AI — we replace with CompanionAI
            DestroyComponent<MonsterAI>(go);
            // Strip NPC-specific components
            DestroyComponent<CharacterDrop>(go);
            DestroyComponent<NpcTalk>(go);

            // Reconfigure existing Humanoid (DvergerMage already has one with the right model)
            var humanoid = go.GetComponent<Humanoid>();
            if (humanoid != null)
            {
                // Merge all gear sources into m_defaultItems before clearing random arrays.
                // Some vanilla prefabs (e.g. DvergerMage) store gear in m_randomSets/m_randomWeapon
                // instead of m_defaultItems. Without merging, those variants spawn with no gear.
                var merged = new System.Collections.Generic.List<GameObject>();
                var seen = new System.Collections.Generic.HashSet<string>();

                // Start with existing m_defaultItems
                if (humanoid.m_defaultItems != null)
                {
                    foreach (var item in humanoid.m_defaultItems)
                    {
                        if (item != null && seen.Add(item.name))
                            merged.Add(item);
                    }
                }

                // Absorb first items from random arrays (consistent gear, no randomization)
                MergeFirstItem(merged, seen, humanoid.m_randomWeapon);
                MergeFirstItem(merged, seen, humanoid.m_randomShield);
                MergeFirstItem(merged, seen, humanoid.m_randomArmor);
                if (humanoid.m_randomSets != null)
                {
                    foreach (var set in humanoid.m_randomSets)
                    {
                        if (set.m_items == null || set.m_items.Length == 0) continue;
                        foreach (var item in set.m_items)
                        {
                            if (item != null && seen.Add(item.name))
                                merged.Add(item);
                        }
                        break; // only take first set
                    }
                }

                humanoid.m_defaultItems = merged.ToArray();

                string defaultNames = "";
                foreach (var di in humanoid.m_defaultItems)
                    defaultNames += (di != null ? di.name : "null") + ", ";
                CompanionsPlugin.Log.LogInfo(
                    $"[CompanionPrefabs]   {def.PrefabName}: merged {humanoid.m_defaultItems.Length} m_defaultItems: [{defaultNames.TrimEnd(',', ' ')}]");

                humanoid.m_randomWeapon  = System.Array.Empty<GameObject>();
                humanoid.m_randomArmor   = System.Array.Empty<GameObject>();
                humanoid.m_randomShield  = System.Array.Empty<GameObject>();
                humanoid.m_randomSets    = System.Array.Empty<Humanoid.ItemSet>();

                // Copy consume effects from Player for eating sounds
                var playerPrefab = zNetScene.GetPrefab("Player");
                var playerHumanoid = playerPrefab?.GetComponent<Humanoid>();
                if (playerHumanoid != null)
                {
                    humanoid.m_consumeItemEffects = playerHumanoid.m_consumeItemEffects;
                    CompanionsPlugin.Log.LogInfo(
                        "[CompanionPrefabs]   Dverger: copied m_consumeItemEffects from Player");
                }

                SetupHumanoid(humanoid, zNetScene, def);
            }
            else
            {
                CompanionsPlugin.Log.LogError(
                    $"[CompanionPrefabs] {def.SourcePrefab} prefab missing Humanoid component!");
                Object.DestroyImmediate(go);
                return;
            }

            // Log Animator info for debugging animation issues on Dverger variants
            var dvergerAnimator = go.GetComponentInChildren<Animator>();
            if (dvergerAnimator != null)
            {
                string ctrlName = dvergerAnimator.runtimeAnimatorController != null
                    ? dvergerAnimator.runtimeAnimatorController.name : "NULL";
                int paramCount = dvergerAnimator.parameterCount;
                CompanionsPlugin.Log.LogInfo(
                    $"[CompanionPrefabs]   {def.PrefabName}: Animator controller=\"{ctrlName}\" " +
                    $"params={paramCount}");
            }
            else
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[CompanionPrefabs]   {def.PrefabName}: NO Animator found on prefab!");
            }

            // CompanionAI — custom BaseAI subclass
            var ai = go.AddComponent<CompanionAI>();
            SetupCompanionAI(ai);

            SetupSharedComponents(go, def, zNetScene);
            RegisterPrefab(go, zNetScene);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Shared setup — components common to both Companion and Dverger
        // ═══════════════════════════════════════════════════════════════════

        private static void SetupSharedComponents(GameObject go, CompanionTierDef def,
                                                   ZNetScene zNetScene)
        {
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
                syncXform.m_syncRotation        = true;
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
            CompanionsPlugin.Log.LogInfo($"[CompanionPrefabs]   + CompanionSetup ({def.PrefabName})");

            // Container — for vanilla chest-style inventory interaction
            var container           = go.AddComponent<Container>();
            container.m_name        = def.DisplayName;
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
            CompanionsPlugin.Log.LogInfo($"[CompanionPrefabs]   + HarvestController ({def.PrefabName})");

            // Advanced combat AI (active in Follow/Stay modes)
            go.AddComponent<CombatController>();
            CompanionsPlugin.Log.LogInfo($"[CompanionPrefabs]   + CombatController ({def.PrefabName})");

            // Overhead speech text (context-aware lines like Haldor)
            go.AddComponent<CompanionTalk>();

            // Campfire sitting + rested regen
            go.AddComponent<CompanionRest>();

            // Door handling (open, pass through, close behind)
            go.AddComponent<DoorHandler>();

            // Auto-repair at nearby workbenches
            go.AddComponent<RepairController>();
        }

        private static void RegisterPrefab(GameObject go, ZNetScene zNetScene)
        {
            int hash = StringExtensionMethods.GetStableHashCode(go.name);
            zNetScene.m_prefabs.Add(go);
            var namedPrefabs = _namedPrefabsField?.GetValue(zNetScene) as Dictionary<int, GameObject>;
            if (namedPrefabs != null)
                namedPrefabs[hash] = go;

            CompanionsPlugin.Log.LogInfo($"[CompanionPrefabs] Registered {go.name}");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Configuration helpers
        // ═══════════════════════════════════════════════════════════════════

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

        private static void MergeFirstItem(
            System.Collections.Generic.List<GameObject> merged,
            System.Collections.Generic.HashSet<string> seen,
            GameObject[] items)
        {
            if (items == null || items.Length == 0) return;
            if (items[0] != null && seen.Add(items[0].name))
                merged.Add(items[0]);
        }

        private static void DestroyComponent<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            if (c != null) Object.DestroyImmediate(c);
        }
    }
}
