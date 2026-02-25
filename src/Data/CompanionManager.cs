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
        public static bool Purchase(CompanionAppearance appearance)
        {
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

            if (!SpawnCompanion(appearance)) return false;

            // Deduct from bank and save
            balance -= price;
            player.m_customData[BankDataKey] = balance.ToString();

            MessageHud.instance?.ShowMessage(
                MessageHud.MessageType.Center,
                "Your new companion has arrived!");

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

        // ── Internal helpers ──────────────────────────────────────────────────

        private static bool SpawnCompanion(CompanionAppearance appearance)
        {
            var def    = CompanionTierData.Companion;
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
            var ai = go.GetComponent<MonsterAI>();
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
