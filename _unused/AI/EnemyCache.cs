using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Single centralized enemy scanner. Refreshed once per 0.25s per companion.
    /// Computes all enemy-related data in a single pass over Character.GetAllCharacters(),
    /// eliminating redundant scans across combat, blocking, harvest, rest, and talk systems.
    /// </summary>
    internal class EnemyCache
    {
        /// <summary>Nearest hostile character.</summary>
        internal Character NearestEnemy { get; private set; }

        /// <summary>Distance to nearest hostile.</summary>
        internal float NearestEnemyDist { get; private set; } = float.MaxValue;

        /// <summary>Nearest enemy currently in an attack animation within block range.</summary>
        internal Character NearestAttackingEnemy { get; private set; }

        /// <summary>Best enemy to assist the player against (targeting player, close to player).</summary>
        internal Character BestThreatAssistTarget { get; private set; }

        /// <summary>Best low-health enemy to focus down.</summary>
        internal Character BestLowHealthTarget { get; private set; }

        private float _scanTimer;

        private const float ScanInterval       = 0.25f;
        private const float BlockDetectRange   = 6f;
        private const float ThreatAssistRange  = 16f;
        private const float LowHealthThreshold = 0.4f;
        private const float LowHealthRange     = 20f;

        internal void Update(float dt, Character self, Vector3 position)
        {
            _scanTimer -= dt;
            if (_scanTimer > 0f) return;
            _scanTimer = ScanInterval;

            NearestEnemy = null;
            NearestAttackingEnemy = null;
            BestThreatAssistTarget = null;
            BestLowHealthTarget = null;

            float nearestSq = float.MaxValue;
            float nearestAttackSq = BlockDetectRange * BlockDetectRange;
            float bestThreatScore = float.MinValue;
            float bestLowHpScore = float.MinValue;

            var player = Player.m_localPlayer;
            Vector3 playerPos = player != null ? player.transform.position : position;

            var all = Character.GetAllCharacters();
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == null || c == self || c.IsDead()) continue;
                if (!BaseAI.IsEnemy(self, c)) continue;

                float distSq = (position - c.transform.position).sqrMagnitude;

                // Nearest enemy overall
                if (distSq < nearestSq)
                {
                    nearestSq = distSq;
                    NearestEnemy = c;
                }

                // Nearest attacking enemy (for blocking)
                if (c.InAttack() && distSq < nearestAttackSq)
                {
                    nearestAttackSq = distSq;
                    NearestAttackingEnemy = c;
                }

                // Threat assist (enemies targeting or near the player)
                if (player != null)
                {
                    float distToPlayerSq = (c.transform.position - playerPos).sqrMagnitude;
                    float threatRangeSq = ThreatAssistRange * ThreatAssistRange;
                    if (distToPlayerSq < threatRangeSq)
                    {
                        float score = (ThreatAssistRange - Mathf.Sqrt(distToPlayerSq)) * 0.2f;
                        if (c.InAttack()) score += 2f;

                        var enemyAI = c.GetComponent<BaseAI>();
                        if (enemyAI != null && enemyAI.GetTargetCreature() == player)
                            score += 3f;

                        if (c == NearestEnemy) score += 0.75f;

                        if (score > bestThreatScore)
                        {
                            bestThreatScore = score;
                            BestThreatAssistTarget = c;
                        }
                    }
                }

                // Low-health target focus
                float hpPct = c.GetHealthPercentage();
                if (hpPct <= LowHealthThreshold && distSq < LowHealthRange * LowHealthRange)
                {
                    float score = (1f - hpPct) * 4f +
                                  (LowHealthRange - Mathf.Sqrt(distSq)) * 0.1f;
                    if (score > bestLowHpScore)
                    {
                        bestLowHpScore = score;
                        BestLowHealthTarget = c;
                    }
                }
            }

            NearestEnemyDist = NearestEnemy != null
                ? Mathf.Sqrt(nearestSq)
                : float.MaxValue;
        }

        internal void Reset()
        {
            NearestEnemy = null;
            NearestEnemyDist = float.MaxValue;
            NearestAttackingEnemy = null;
            BestThreatAssistTarget = null;
            BestLowHealthTarget = null;
            _scanTimer = 0f;
        }
    }
}
