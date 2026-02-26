using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Terrain-aware interaction point calculation (8-direction sampling) and
    /// harvest-specific stuck escalation. Handles LOS checks and target bounds.
    /// </summary>
    internal class HarvestNavigation
    {
        private readonly Character _character;
        private readonly Transform _transform;
        private readonly PositionTracker _tracker;
        private readonly DoorSystem _doors;

        private float _stuckTimer;
        private int   _stuckTier;
        private bool  _offsetLeft;
        private int   _probeMask = -1;

        // Bounds cache — avoids GetComponentsInChildren allocations every call
        private int    _cachedBoundsId = int.MinValue;
        private Bounds _cachedBounds;
        private float  _boundsCacheExpiry;

        private const float DefaultAttackRange      = 2.6f;
        private const float PreferredSurfaceDist   = 1.0f;
        private const float MinSurfaceDist         = 0.65f;
        private const float MaxSurfaceDist         = 1.35f;
        private const float MinTargetRadius        = 0.45f;
        private const float MaxTargetRadius        = 1.2f;
        private const float MaxDownwardAttackOffset = 1.2f;
        private const float MaxUpwardAttackOffset  = 2.0f;

        internal HarvestNavigation(Character character, Transform transform,
                                   PositionTracker tracker, DoorSystem doors)
        {
            _character = character;
            _transform = transform;
            _tracker   = tracker;
            _doors     = doors;

            // Probe mask: exclude character layer to avoid self-hits
            int charLayer = LayerMask.NameToLayer("character");
            _probeMask = charLayer >= 0 ? ~(1 << charLayer) : ~0;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Interaction Point Calculation
        // ══════════════════════════════════════════════════════════════════════

        /// <param name="weaponRange">
        /// The equipped weapon's m_shared.m_attack.m_attackRange, or 0 to use default.
        /// VikingNPC lesson: using the weapon's actual attack range as stop distance
        /// matches what vanilla enemies do in HandleStaticTarget.
        /// </param>
        internal bool TryGetInteractionPoint(
            GameObject target,
            out Vector3 targetCenter, out Vector3 standPoint,
            out float minDist, out float maxDist,
            float weaponRange = 0f)
        {
            float effectiveRange = weaponRange > 0f ? weaponRange : DefaultAttackRange;
            targetCenter = _transform.position;
            standPoint   = _transform.position;
            minDist      = 1.2f;
            maxDist      = effectiveRange;

            if (target == null) return false;

            if (!TryGetTargetBoundsCached(target, out Bounds bounds))
                bounds = new Bounds(target.transform.position, new Vector3(1f, 2f, 1f));

            targetCenter = bounds.center;

            float radius = Mathf.Clamp(
                Mathf.Max(bounds.extents.x, bounds.extents.z),
                MinTargetRadius, MaxTargetRadius);
            minDist = Mathf.Clamp(radius + MinSurfaceDist, 1.0f, effectiveRange - 0.15f);
            maxDist = Mathf.Clamp(radius + MaxSurfaceDist, minDist + 0.2f, effectiveRange);
            float preferredDist = Mathf.Clamp(
                radius + PreferredSurfaceDist, minDist, maxDist);

            Vector3 currentApproach = _transform.position - targetCenter;
            currentApproach.y = 0f;
            if (currentApproach.sqrMagnitude < 0.001f)
            {
                Vector3 fallbackDir = targetCenter - (Player.m_localPlayer != null
                    ? Player.m_localPlayer.transform.position
                    : _transform.position);
                fallbackDir.y = 0f;
                currentApproach = fallbackDir.sqrMagnitude > 0.001f
                    ? fallbackDir : _transform.forward;
            }
            currentApproach.Normalize();

            // Sample 8 directions for stand point selection (halved from 16 for perf)
            Vector3 bestPoint = targetCenter + currentApproach * preferredDist;
            bestPoint.y = _transform.position.y;
            float bestScore = float.MinValue;
            bool anyValid = false;

            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f;
                Vector3 dir = Quaternion.Euler(0, angle, 0) * Vector3.forward;
                Vector3 candidatePos = targetCenter + dir * preferredDist;
                float score = 0f;

                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetSolidHeight(candidatePos, out groundHeight))
                    {
                        float heightDiff = Mathf.Abs(groundHeight - _transform.position.y);
                        score -= heightDiff * 0.5f;
                        candidatePos.y = groundHeight;
                        anyValid = true;
                        // Prefer flat terrain
                        if (heightDiff < 0.3f) score += 0.2f;
                    }
                    else
                    {
                        score -= 5f;
                    }
                }
                else
                {
                    candidatePos.y = _transform.position.y;
                    anyValid = true;
                }

                // Prefer continuing from current approach
                score += Vector3.Dot(dir, currentApproach) * 0.3f;

                // Prefer closer positions (less travel)
                float travelDist = Vector3.Distance(_transform.position, candidatePos);
                score -= travelDist * 0.05f;

                // LOS to target center
                Vector3 eyePos = candidatePos + Vector3.up * 1.5f;
                if (!IsLineOfSightBlocked(eyePos, targetCenter, target.transform))
                    score += 0.4f;

                // Predictive obstacle probe (every other candidate to limit cost)
                if ((i & 1) == 0)
                {
                    Vector3 probeDir = candidatePos - _transform.position;
                    probeDir.y = 0f;
                    float probeDist = probeDir.magnitude;
                    if (probeDist > 0.5f)
                    {
                        probeDir.Normalize();
                        Vector3 probeStart = _transform.position + Vector3.up * 0.5f;
                        if (Physics.Raycast(probeStart, probeDir, out RaycastHit probeHit,
                            probeDist, _probeMask, QueryTriggerInteraction.Ignore))
                        {
                            // Ignore hits on the target itself
                            if (target.transform == null ||
                                (!probeHit.transform.IsChildOf(target.transform) &&
                                 probeHit.transform != target.transform))
                                score -= 1.5f;
                        }
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPoint = candidatePos;
                }
            }

            if (!anyValid)
            {
                bestPoint = targetCenter + currentApproach * preferredDist;
                bestPoint.y = _transform.position.y;
            }

            standPoint = bestPoint;
            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Stuck Escalation
        // ══════════════════════════════════════════════════════════════════════

        /// <returns>true if target was blacklisted (caller should abandon)</returns>
        internal bool UpdateStuck(float dt, Vector3 targetCenter, Vector3 standPoint,
                                  MonsterAI ai, GameObject waypoint,
                                  System.Action<BlacklistReason> blacklistCallback)
        {
            if (_tracker == null) return false;

            if (_tracker.IsOscillating(0.5f, 4f))
            {
                blacklistCallback?.Invoke(BlacklistReason.Oscillation);
                return true;
            }

            float moved = _tracker.DistanceOverWindow(2f);
            if (moved > 0.5f)
            {
                _stuckTimer = 0f;
                _stuckTier  = 0;
                return false;
            }

            _stuckTimer += dt;

            // Tier 4: 7.5s → blacklist
            if (_stuckTimer >= 7.5f && _stuckTier < 5)
            {
                blacklistCallback?.Invoke(BlacklistReason.Unreachable);
                return true;
            }

            // Tier 3: 6s → jump
            if (_stuckTimer >= 6f && _stuckTier < 4)
            {
                _stuckTier = 4;
                if (_character != null) _character.Jump(false);
                return false;
            }

            // Tier 2: 5s → opposite perpendicular offset
            if (_stuckTimer >= 5f && _stuckTier < 3)
            {
                _stuckTier = 3;
                ApplyPerpendicularOffset(targetCenter, standPoint, !_offsetLeft, ai, waypoint);
                return false;
            }

            // Tier 1: 3.5s → perpendicular offset
            if (_stuckTimer >= 3.5f && _stuckTier < 2)
            {
                _stuckTier = 2;
                _offsetLeft = Random.value > 0.5f;
                ApplyPerpendicularOffset(targetCenter, standPoint, _offsetLeft, ai, waypoint);
                return false;
            }

            // Tier 0: 1.5s → try door
            if (_stuckTimer >= 1.5f && _stuckTier < 1)
            {
                _stuckTier = 1;
                _doors?.TryOpenNearbyDoor();
                return false;
            }

            return false;
        }

        internal void ResetStuck()
        {
            _stuckTimer = 0f;
            _stuckTier  = 0;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Validation
        // ══════════════════════════════════════════════════════════════════════

        internal bool ValidateAttackPosition(Vector3 targetCenter, Transform targetRoot)
        {
            float yDiff = _transform.position.y - targetCenter.y;
            if (yDiff < -MaxDownwardAttackOffset || yDiff > MaxUpwardAttackOffset)
                return false;

            Vector3 eye = _transform.position + Vector3.up * 1.5f;
            return !IsLineOfSightBlocked(eye, targetCenter, targetRoot);
        }

        /// <summary>True when the companion is too far below the target to attack it.</summary>
        internal bool IsTooFarBelow(Vector3 targetCenter)
        {
            return (targetCenter.y - _transform.position.y) > MaxUpwardAttackOffset;
        }

        /// <summary>True when the companion is too far above the target to attack it.</summary>
        internal bool IsTooFarAbove(Vector3 targetCenter)
        {
            return (_transform.position.y - targetCenter.y) > MaxDownwardAttackOffset;
        }

        /// <summary>True when the height difference is too large in either direction.</summary>
        internal bool IsHeightOutOfRange(Vector3 targetCenter)
        {
            float yDiff = _transform.position.y - targetCenter.y;
            return yDiff < -MaxUpwardAttackOffset || yDiff > MaxDownwardAttackOffset;
        }

        internal static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        // Cached LOS layer mask — excludes "character" layer to avoid self-hits.
        // Computed once on first use (layer IDs are stable after scene load).
        private static int _losMask = 0;
        private static bool _losMaskInit;

        private static int GetLOSMask()
        {
            if (!_losMaskInit)
            {
                _losMaskInit = true;
                int charLayer = LayerMask.NameToLayer("character");
                int charNetLayer = LayerMask.NameToLayer("character_net");
                int mask = ~0;
                if (charLayer >= 0) mask &= ~(1 << charLayer);
                if (charNetLayer >= 0) mask &= ~(1 << charNetLayer);
                _losMask = mask;
            }
            return _losMask;
        }

        internal static bool IsLineOfSightBlocked(Vector3 from, Vector3 to, Transform targetRoot)
        {
            if (!Physics.Linecast(from, to, out RaycastHit hit, GetLOSMask(), QueryTriggerInteraction.Ignore))
                return false;

            var hitCollider = hit.collider;
            if (hitCollider == null) return true;

            Transform hitTransform = hitCollider.transform;
            if (targetRoot != null &&
                (hitTransform == targetRoot || hitTransform.IsChildOf(targetRoot)))
                return false;

            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private void ApplyPerpendicularOffset(
            Vector3 targetCenter, Vector3 standPoint, bool leftSide,
            MonsterAI ai, GameObject waypoint)
        {
            Vector3 toTarget = targetCenter - _transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.001f) toTarget = _transform.forward;
            toTarget.Normalize();
            Vector3 perp = Vector3.Cross(toTarget, Vector3.up).normalized;
            if (perp.sqrMagnitude < 0.01f) perp = _transform.right;
            float side = leftSide ? 1f : -1f;

            if (waypoint != null)
                waypoint.transform.position = standPoint + perp * side * 3f;
            ai?.SetFollowTarget(waypoint);
        }

        private bool TryGetTargetBoundsCached(GameObject target, out Bounds bounds)
        {
            int id = target.GetInstanceID();
            if (id == _cachedBoundsId && Time.time < _boundsCacheExpiry)
            {
                bounds = _cachedBounds;
                return true;
            }

            if (!TryGetTargetBounds(target, out bounds))
                return false;

            _cachedBoundsId = id;
            _cachedBounds = bounds;
            _boundsCacheExpiry = Time.time + 2f;
            return true;
        }

        private static bool TryGetTargetBounds(GameObject target, out Bounds bounds)
        {
            bounds = default;
            if (target == null) return false;

            bool found = false;
            var colliders = target.GetComponentsInChildren<Collider>(true);
            foreach (var col in colliders)
            {
                if (col == null || !col.enabled) continue;
                if (!found) { bounds = col.bounds; found = true; }
                else bounds.Encapsulate(col.bounds);
            }

            if (found) return true;

            var renderers = target.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return found;
        }
    }
}
