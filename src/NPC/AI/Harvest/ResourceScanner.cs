using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Physics-based resource scanning with scoring.
    /// Per-instance scan buffer for multi-companion safety.
    /// Scores by distance, height, player proximity, and pathfinding viability.
    /// </summary>
    internal class ResourceScanner
    {
        private readonly Transform _transform;
        private readonly HarvestBlacklist _blacklist;
        private readonly Collider[] _scanBuffer = new Collider[512];
        private readonly HashSet<int> _seenIds = new HashSet<int>();

        internal ResourceScanner(Transform transform, HarvestBlacklist blacklist)
        {
            _transform = transform;
            _blacklist = blacklist;
        }

        internal (GameObject, ResourceType) FindBestForMode(
            ResourceType filter, float closeRange, float farRange,
            ItemDrop.ItemData tool, System.Func<ResourceType, int, ItemDrop.ItemData> findToolFn,
            out bool toolTierBlocked)
        {
            toolTierBlocked = false;

            bool localBlocked;
            var (target, type) = FindBest(filter, closeRange, tool, findToolFn, out localBlocked);
            toolTierBlocked |= localBlocked;

            if (target == null)
            {
                (target, type) = FindBest(filter, farRange, tool, findToolFn, out localBlocked);
                toolTierBlocked |= localBlocked;
            }

            return (target, type);
        }

        private (GameObject, ResourceType) FindBest(
            ResourceType filter, float scanRange, ItemDrop.ItemData toolOverride,
            System.Func<ResourceType, int, ItemDrop.ItemData> findToolFn,
            out bool toolTierBlocked)
        {
            toolTierBlocked = false;
            int colliderCount = Physics.OverlapSphereNonAlloc(
                _transform.position, scanRange, _scanBuffer);
            if (colliderCount > _scanBuffer.Length) colliderCount = _scanBuffer.Length;

            GameObject   best     = null;
            ResourceType bestType = ResourceType.None;
            float        bestScore = float.MinValue;

            GameObject   fallback     = null;
            ResourceType fallbackType = ResourceType.None;
            float        fallbackScore = float.MinValue;

            _seenIds.Clear();

            for (int i = 0; i < colliderCount; i++)
            {
                var col = _scanBuffer[i];
                if (col == null) continue;

                var (resGo, resType) = ResourceClassifier.Classify(col.gameObject);
                if (resGo == null || resType == ResourceType.None || resType != filter) continue;
                if (!resGo.activeInHierarchy) continue;

                int id = resGo.GetInstanceID();
                if (!_seenIds.Add(id)) continue;
                if (_blacklist.IsBlacklisted(id)) continue;

                int minTier = ResourceClassifier.GetMinToolTier(resGo);
                var tool = toolOverride;
                if (tool == null || tool.m_shared == null ||
                    ResourceClassifier.GetRelevantToolDamage(tool, resType) <= 0f ||
                    tool.m_shared.m_toolTier < minTier)
                {
                    tool = findToolFn?.Invoke(resType, minTier);
                }
                if (tool == null)
                {
                    toolTierBlocked = true;
                    continue;
                }

                float dist = Vector3.Distance(_transform.position, resGo.transform.position);
                float yDiff = Mathf.Abs(_transform.position.y - resGo.transform.position.y);
                float playerDist = Player.m_localPlayer != null
                    ? Vector3.Distance(Player.m_localPlayer.transform.position,
                                       resGo.transform.position)
                    : dist;

                float score = 1f - Mathf.Clamp01(dist / scanRange);
                score -= yDiff * 0.08f;
                if (playerDist < 20f)
                    score += 0.15f * (1f - playerDist / 20f);

                // Track fallback (best by base score, no path requirement)
                if (score > fallbackScore)
                {
                    fallback      = resGo;
                    fallbackType  = resType;
                    fallbackScore = score;
                }

                // Primary: require pathfinding
                if (Pathfinding.instance != null &&
                    !Pathfinding.instance.HavePath(
                        _transform.position, resGo.transform.position,
                        Pathfinding.AgentType.Humanoid))
                    continue;

                float pathScore = score + 0.3f;
                if (pathScore > bestScore)
                {
                    best      = resGo;
                    bestType  = resType;
                    bestScore = pathScore;
                }
            }

            if (best == null && fallback != null)
                return (fallback, fallbackType);
            return (best, bestType);
        }
    }
}
