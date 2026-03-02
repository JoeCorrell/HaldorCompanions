using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Handles purchasing, spawning, and persistent tracking of companion NPCs.
    /// Purchases deduct from the TraderOverhaul bank balance, not inventory coins.
    /// </summary>
    public static class CompanionManager
    {
        // Must match TraderOverhaul's key exactly
        private const string BankDataKey = "TraderSharedBank_Balance";

        // ── Public API ────────────────────────────────────────────────────────

        public static int GetBankBalance()
        {
            var player = Player.m_localPlayer;
            if (player == null) return 0;
            if (player.m_customData.TryGetValue(BankDataKey, out string val) &&
                int.TryParse(val, out int balance))
                return balance;
            return 0;
        }

        public static bool CanAfford()
        {
            return GetBankBalance() >= CompanionTierData.Price;
        }

        /// <summary>
        /// Deducts from bank balance and spawns a companion near the player.
        /// Returns true on success.
        /// </summary>
        public static bool Purchase(CompanionAppearance appearance,
                                     CompanionTierDef def = null)
        {
            if (def == null) def = CompanionTierData.Companion;

            var player = Player.m_localPlayer;
            if (player == null) return false;

            int price   = CompanionTierData.Price;
            int balance = GetBankBalance();

            if (balance < price)
            {
                MessageHud.instance?.ShowMessage(
                    MessageHud.MessageType.Center,
                    ModLocalization.LocFmt("hc_msg_need_coins", price.ToString("N0")));
                return false;
            }

            // Deduct first to prevent double-purchase race
            balance -= price;
            player.m_customData[BankDataKey] = balance.ToString();

            if (!SpawnCompanion(appearance, def))
            {
                // Spawn failed — refund
                balance += price;
                player.m_customData[BankDataKey] = balance.ToString();
                return false;
            }

            MessageHud.instance?.ShowMessage(
                MessageHud.MessageType.Center,
                ModLocalization.LocFmt("hc_msg_arrived", def.DisplayName.ToLower()));

            return true;
        }

        /// <summary>
        /// Restore follow targets for all owned companions after a player respawn
        /// or zone reload.
        /// </summary>
        public static void RestoreFollowTargets()
        {
            foreach (var setup in Object.FindObjectsByType<CompanionSetup>(FindObjectsSortMode.None))
                setup.RestoreFollowTarget();
        }

        // ── Respawn Queue ───────────────────────────────────────────────────

        public struct RespawnData
        {
            public string PrefabName;
            public string Name;
            public string AppearanceSerialized;
            public string OwnerId;
            public int CombatStance;
            public long TombstoneId;
            public string SkillsSerialized;
            public float Timer;
            public bool HasHomePos;
            public Vector3 HomePos;
        }

        private static readonly List<RespawnData> _respawnQueue = new List<RespawnData>();

        public static void QueueRespawn(RespawnData data)
        {
            _respawnQueue.Add(data);
            CompanionsPlugin.Log.LogInfo(
                $"[CompanionManager] Queued respawn for \"{data.Name}\" in {data.Timer}s");
        }

        public static void ProcessRespawns(float dt)
        {
            if (_respawnQueue.Count == 0) return;

            for (int i = _respawnQueue.Count - 1; i >= 0; i--)
            {
                var data = _respawnQueue[i];
                data.Timer -= dt;
                _respawnQueue[i] = data;

                if (data.Timer <= 0f)
                {
                    _respawnQueue.RemoveAt(i);
                    DoRespawn(data);
                }
            }
        }

        private static void DoRespawn(RespawnData data)
        {
            CompanionsPlugin.Log.LogInfo(
                $"[CompanionManager] Processing respawn for \"{data.Name}\" — prefab={data.PrefabName}");

            if (Player.m_localPlayer == null)
            {
                // Player not loaded yet — re-queue with short delay
                data.Timer = 1f;
                _respawnQueue.Add(data);
                CompanionsPlugin.Log.LogDebug("[CompanionManager] Player not loaded — re-queued respawn");
                return;
            }

            // Respawn at home position if set, otherwise world spawn
            Vector3 spawnPos;
            if (data.HasHomePos)
            {
                spawnPos = data.HomePos;
                CompanionsPlugin.Log.LogInfo($"[CompanionManager] Respawning at home position: {spawnPos:F1}");
            }
            else
            {
                spawnPos = GetWorldSpawnPoint();
                CompanionsPlugin.Log.LogInfo($"[CompanionManager] Respawning at world spawn: {spawnPos:F1}");
            }

            var prefab = ZNetScene.instance?.GetPrefab(data.PrefabName);
            if (prefab == null)
            {
                CompanionsPlugin.Log.LogError(
                    $"[CompanionManager] Respawn failed — prefab not found: {data.PrefabName}");
                return;
            }

            // Bed respawn: spawn directly at bed position (FindSpawnPosition raycasts
            // from above and can land on roofs). World spawn: offset to avoid overlap.
            var pos = data.HasHomePos ? spawnPos : FindSpawnPosition(spawnPos, 4f);
            var rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            var go = Object.Instantiate(prefab, pos, rot);
            var nview = go.GetComponent<ZNetView>();

            if (nview?.GetZDO() == null)
            {
                CompanionsPlugin.Log.LogError(
                    "[CompanionManager] Respawn failed — ZDO not available.");
                Object.Destroy(go);
                return;
            }

            var zdo = nview.GetZDO();
            zdo.Set(CompanionSetup.AppearanceHash, data.AppearanceSerialized);
            zdo.Set(CompanionSetup.OwnerHash, data.OwnerId);
            zdo.Set(CompanionSetup.NameHash, data.Name);
            zdo.Set(CompanionSetup.CombatStanceHash, data.CombatStance);
            zdo.Set(CompanionSetup.TombstoneIdHash, data.TombstoneId);
            if (data.HasHomePos)
            {
                // Respawning at home — restore StayHome state
                zdo.Set(CompanionSetup.FollowHash, false);
                zdo.Set(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                zdo.Set(CompanionSetup.StayHomeHash, true);
                zdo.Set(CompanionSetup.HomePosHash, data.HomePos);
                zdo.Set(CompanionSetup.HomePosSetHash, true);
            }
            else
            {
                zdo.Set(CompanionSetup.FollowHash, true);
                zdo.Set(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                zdo.Set(CompanionSetup.StayHomeHash, false);
            }
            if (!string.IsNullOrEmpty(data.SkillsSerialized))
                zdo.Set(CompanionSkills.SkillsHash, data.SkillsSerialized);
            zdo.Persistent = true;

            // Apply appearance
            var appearance = CompanionAppearance.Deserialize(data.AppearanceSerialized);
            var setup = go.GetComponent<CompanionSetup>();
            if (setup != null) setup.ApplyAppearance(appearance);

            // Set companion name
            var character = go.GetComponent<Character>();
            if (character != null && !string.IsNullOrEmpty(data.Name))
                character.m_name = data.Name;

            // Set follow target — only follow player if not staying home
            var ai = go.GetComponent<CompanionAI>();
            if (data.HasHomePos)
            {
                if (ai != null)
                    ai.SetPatrolPointAt(data.HomePos);
            }
            else if (Player.m_localPlayer != null)
            {
                ai?.SetFollowTarget(Player.m_localPlayer.gameObject);
            }

            // Grace period — prevent immediate tombstone recovery and movement
            // so the companion doesn't loot its own grave before the player can see it
            if (ai != null)
                ai.FreezeTimer = 5f;

            string spawnType = data.HasHomePos ? "home" : "world spawn";
            CompanionsPlugin.Log.LogInfo(
                $"[CompanionManager] Respawned \"{data.Name}\" at {spawnType} {pos:F1} — " +
                $"tombstoneId={data.TombstoneId}");

            string locationMsg = data.HasHomePos
                ? ModLocalization.Loc("hc_msg_location_home")
                : ModLocalization.Loc("hc_msg_location_spawn");
            MessageHud.instance?.ShowMessage(
                MessageHud.MessageType.Center,
                ModLocalization.LocFmt("hc_msg_returned", data.Name, locationMsg));
        }

        private static Vector3 GetWorldSpawnPoint()
        {
            if (ZoneSystem.instance != null &&
                ZoneSystem.instance.GetLocationIcon(Game.instance.m_StartLocation, out Vector3 pos))
            {
                return pos + Vector3.up * 2f;
            }
            return Vector3.up * 2f;
        }

        // ── Starter companion ────────────────────────────────────────────────

        /// <summary>
        /// Called on first spawn in a world. Shows the customisation panel so the
        /// player can choose their starter companion's appearance before spawning.
        /// </summary>
        public static void SpawnStarterCompanion()
        {
            if (!CompanionsPlugin.SpawnStarterCompanion.Value)
            {
                CompanionsPlugin.Log.LogDebug("[CompanionManager] Starter companion disabled in config");
                return;
            }

            var player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null) return;

            string worldKey = $"HC_StarterCompanion_{ZNet.instance.GetWorldUID()}";
            if (player.m_customData.ContainsKey(worldKey))
            {
                CompanionsPlugin.Log.LogDebug("[CompanionManager] Starter companion already spawned for this world");
                return;
            }

            // Fallback: if any companion owned by this player already exists in the scene,
            // skip the panel (handles cases where m_customData didn't persist)
            string localId = player.GetPlayerID().ToString();
            foreach (var setup in Object.FindObjectsByType<CompanionSetup>(FindObjectsSortMode.None))
            {
                var zdo = setup.GetComponent<ZNetView>()?.GetZDO();
                if (zdo != null && zdo.GetString(CompanionSetup.OwnerHash, "") == localId)
                {
                    CompanionsPlugin.Log.LogInfo(
                        "[CompanionManager] Found existing companion in scene — marking world key and skipping panel");
                    player.m_customData[worldKey] = "1";
                    return;
                }
            }

            // Mark this world so the panel never re-appears (even if skipped)
            player.m_customData[worldKey] = "1";

            // Force immediate save so the key persists even if the player quits soon
            if (Game.instance != null)
                Game.instance.SavePlayerProfile(setLogoutPoint: false);

            CompanionsPlugin.Log.LogInfo("[CompanionManager] First spawn in world — showing starter companion panel");
            StarterCompanionPanel.ShowPanel();
        }

        /// <summary>
        /// Called by StarterCompanionPanel when the player confirms appearance.
        /// Spawns the companion. World key is already set in SpawnStarterCompanion().
        /// </summary>
        public static void SpawnStarterWithAppearance(CompanionAppearance appearance)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            if (SpawnCompanion(appearance, CompanionTierData.Companion))
            {
                CompanionsPlugin.Log.LogInfo("[CompanionManager] Starter companion spawned with custom appearance!");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    ModLocalization.Loc("hc_msg_starter_joined"));
            }
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private static bool SpawnCompanion(CompanionAppearance appearance,
                                             CompanionTierDef def)
        {
            var prefab = ZNetScene.instance?.GetPrefab(def.PrefabName);
            if (prefab == null)
            {
                CompanionsPlugin.Log.LogError(
                    $"[CompanionManager] Prefab not found: {def.PrefabName}");
                return false;
            }

            var player   = Player.m_localPlayer;
            var spawnPos = FindSpawnPosition(player.transform.position, 4f);
            var spawnRot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            var go    = Object.Instantiate(prefab, spawnPos, spawnRot);
            var nview = go.GetComponent<ZNetView>();

            if (nview?.GetZDO() == null)
            {
                CompanionsPlugin.Log.LogError(
                    "[CompanionManager] ZDO not available after spawn — aborting.");
                Object.Destroy(go);
                return false;
            }

            var zdo = nview.GetZDO();
            zdo.Set(CompanionSetup.AppearanceHash, appearance.Serialize());
            zdo.Set(CompanionSetup.OwnerHash, player.GetPlayerID().ToString());
            zdo.Persistent = true;

            // Apply appearance directly — CompanionSetup.TryInit fires during
            // Instantiate before ZDO data is set, so it only sees defaults.
            var setup = go.GetComponent<CompanionSetup>();
            if (setup != null) setup.ApplyAppearance(appearance);

            // Set follow target immediately so the companion moves to the player
            var ai = go.GetComponent<CompanionAI>();
            ai?.SetFollowTarget(player.gameObject);

            CompanionsPlugin.Log.LogInfo(
                $"[CompanionManager] Spawned companion for player {player.GetPlayerID()}");
            return true;
        }

        private static Vector3 FindSpawnPosition(Vector3 origin, float radius)
        {
            for (int i = 0; i < 20; i++)
            {
                Vector2 rnd       = Random.insideUnitCircle * radius;
                Vector3 candidate = origin + new Vector3(rnd.x, 0f, rnd.y);
                if (ZoneSystem.instance != null &&
                    ZoneSystem.instance.FindFloor(candidate, out float height))
                {
                    candidate.y = height;
                    return candidate;
                }
            }
            return origin + Vector3.right * 2f;
        }
    }
}
