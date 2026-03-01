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
                    $"Not enough coins in bank! Need {price:N0}");
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
                $"Your new {def.DisplayName.ToLower()} has arrived!");

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
            public float Timer;
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

            // Get world spawn point (sacrificial stones / StartTemple)
            Vector3 spawnPos = GetWorldSpawnPoint();
            CompanionsPlugin.Log.LogDebug($"[CompanionManager] World spawn point: {spawnPos:F1}");

            var prefab = ZNetScene.instance?.GetPrefab(data.PrefabName);
            if (prefab == null)
            {
                CompanionsPlugin.Log.LogError(
                    $"[CompanionManager] Respawn failed — prefab not found: {data.PrefabName}");
                return;
            }

            var pos = FindSpawnPosition(spawnPos, 4f);
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
            zdo.Set(CompanionSetup.FollowHash, true);
            zdo.Set(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            zdo.Set(CompanionSetup.StayHomeHash, false);
            zdo.Persistent = true;

            // Apply appearance
            var appearance = CompanionAppearance.Deserialize(data.AppearanceSerialized);
            var setup = go.GetComponent<CompanionSetup>();
            if (setup != null) setup.ApplyAppearance(appearance);

            // Set companion name
            var character = go.GetComponent<Character>();
            if (character != null && !string.IsNullOrEmpty(data.Name))
                character.m_name = data.Name;

            // Set follow target to player
            var ai = go.GetComponent<CompanionAI>();
            if (Player.m_localPlayer != null)
                ai?.SetFollowTarget(Player.m_localPlayer.gameObject);

            // Grace period — prevent immediate tombstone recovery and movement
            // so the companion doesn't loot its own grave before the player can see it
            if (ai != null)
                ai.FreezeTimer = 5f;

            CompanionsPlugin.Log.LogInfo(
                $"[CompanionManager] Respawned \"{data.Name}\" at world spawn {pos:F1} — " +
                $"tombstoneId={data.TombstoneId}");

            MessageHud.instance?.ShowMessage(
                MessageHud.MessageType.Center,
                $"{data.Name} has returned at the world spawn!");
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
        /// Called on first spawn in a world. Spawns a free companion if this
        /// character hasn't received one in this world yet.
        /// </summary>
        public static void SpawnStarterCompanion()
        {
            var player = Player.m_localPlayer;
            if (player == null || ZNet.instance == null) return;

            string worldKey = $"HC_StarterCompanion_{ZNet.instance.GetWorldUID()}";
            if (player.m_customData.ContainsKey(worldKey)) return;

            var appearance = CompanionAppearance.Default();
            appearance.ModelIndex = Random.Range(0, 2);

            if (SpawnCompanion(appearance, CompanionTierData.Companion))
            {
                player.m_customData[worldKey] = "1";
                CompanionsPlugin.Log.LogInfo("[CompanionManager] Starter companion spawned!");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    "A companion has joined you on your journey!");
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
