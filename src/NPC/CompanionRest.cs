using System;
using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Handles companion sitting near campfires when the player sits.
    /// While resting: heals slowly and doubles stamina regen.
    /// </summary>
    public class CompanionRest : MonoBehaviour
    {
        private ZNetView         _nview;
        private Character        _character;
        private CompanionAI      _ai;
        private CompanionStamina _stamina;
        private ZSyncAnimation   _zanim;

        private bool  _isSitting;
        private float _checkTimer;
        private float _healTimer;
        private GameObject _fireTarget;

        private const float CheckInterval = 1f;
        private const float HealRate      = 2f;     // hp per second while resting
        private const float HealInterval  = 0.5f;
        private const float PlayerRange   = 5f;
        private const float FireRange     = 5f;
        private const int FireScanBufferSize = 48;

        private readonly Collider[] _fireScanBuffer = new Collider[FireScanBufferSize];
        private readonly HashSet<int> _seenFireIds = new HashSet<int>();

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
            _ai        = GetComponent<CompanionAI>();
            _stamina   = GetComponent<CompanionStamina>();
            _zanim     = GetComponent<ZSyncAnimation>();
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

            if (_isSleeping)
            {
                UpdateSleeping();
                return;
            }

            if (_isSitting)
            {
                UpdateSitting();
                return;
            }

            _checkTimer -= Time.deltaTime;
            if (_checkTimer > 0f) return;
            _checkTimer = CheckInterval;

            TryStartSitting();
        }

        private void OnDestroy()
        {
            if (_stamina != null) _stamina.IsResting = false;

            // Clear inBed ZDO if sleeping when destroyed
            if (_isSleeping && _nview != null && _nview.GetZDO() != null)
                _nview.GetZDO().Set(ZDOVars.s_inBed, false);
        }

        // ── Sit detection ──────────────────────────────────────────────────

        private void TryStartSitting()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            // Must be in follow mode (0)
            var zdo = _nview.GetZDO();
            if (zdo == null) return;
            int mode = zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            if (mode != CompanionSetup.ModeFollow) return;

            // Player must be sitting (check LastEmote — static members)
            if (Player.LastEmote == null ||
                !Player.LastEmote.Equals("sit", StringComparison.OrdinalIgnoreCase))
                return;

            // Check time since last emote (within 30s means still sitting)
            if ((DateTime.Now - Player.LastEmoteTime).TotalSeconds > 30.0)
                return;

            // Must be near player
            float playerDist = Vector3.Distance(transform.position, player.transform.position);
            if (playerDist > PlayerRange) return;

            // Must be near a burning fire
            var fire = FindNearbyFire();
            if (fire == null) return;

            // No enemies nearby
            if (HasEnemyNearby()) return;

            StartSitting(fire);
        }

        // ── Public API — directed from hotkey ─────────────────────────────

        private bool _isDirected;   // true when sitting/sleeping was triggered by hotkey
        private bool _isSleeping;   // true when lying down at a bed
        private Bed  _bedTarget;

        /// <summary>Direct the companion to sit near a fire (from hotkey).</summary>
        public void DirectSit(GameObject fire)
        {
            if (fire == null) return;
            if (_isSitting || _isSleeping) StopAll();

            _isDirected = true;
            StartSitting(fire);
        }

        /// <summary>Direct the companion to sleep at a bed (from hotkey).</summary>
        public void DirectSleep(Bed bed)
        {
            if (bed == null) return;
            if (_isSitting || _isSleeping) StopAll();

            _isDirected = true;
            _isSleeping = true;
            _bedTarget  = bed;

            CompanionsPlugin.Log.LogInfo(
                $"[Rest] DirectSleep — lying down at bed \"{bed.name}\"");

            // Stop movement and move to bed
            if (_ai != null) _ai.SetFollowTarget(null);

            // Position at the bed's spawn point
            var spawnPoint = bed.GetComponent<Bed>()?.m_spawnPoint;
            if (spawnPoint != null)
            {
                transform.position = spawnPoint.position;
                transform.rotation = spawnPoint.rotation;
            }
            else
            {
                transform.position = bed.transform.position;
                Vector3 dir = bed.transform.forward;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                    transform.rotation = Quaternion.LookRotation(dir);
            }

            // Play sleep/bed animation
            if (_zanim != null) _zanim.SetTrigger("attach_bed");

            // Set inBed on ZDO so companion doesn't block player sleep
            if (_nview != null && _nview.GetZDO() != null)
                _nview.GetZDO().Set(ZDOVars.s_inBed, true);

            // Enable resting regen
            if (_stamina != null) _stamina.IsResting = true;
        }

        /// <summary>Cancel any directed sit or sleep.</summary>
        public void CancelDirected()
        {
            if (!_isDirected) return;
            StopAll();
        }

        private void StopAll()
        {
            if (_isSitting) StopSitting();
            if (_isSleeping) StopSleeping();
            _isDirected = false;
        }

        private void StopSleeping()
        {
            _isSleeping = false;
            _bedTarget  = null;

            CompanionsPlugin.Log.LogInfo("[Rest] Stopped sleeping — resuming normal behavior");

            if (_zanim != null) _zanim.SetTrigger("emote_stop");
            if (_stamina != null) _stamina.IsResting = false;

            // Clear inBed ZDO
            if (_nview != null && _nview.GetZDO() != null)
                _nview.GetZDO().Set(ZDOVars.s_inBed, false);

            if (_ai == null) return;
            int mode = _nview?.GetZDO()?.GetInt(
                CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                ?? CompanionSetup.ModeFollow;
            if (mode == CompanionSetup.ModeStay)
            {
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPoint();
            }
            else if (Player.m_localPlayer != null)
            {
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
            }
        }

        // ── Core sitting logic ──────────────────────────────────────────────

        private void StartSitting(GameObject fire)
        {
            _isSitting   = true;
            _fireTarget  = fire;
            _healTimer   = 0f;

            CompanionsPlugin.Log.LogInfo(
                $"[Rest] Sitting near fire \"{fire.name}\" — starting heal + stamina regen");

            // Stop movement and face the fire
            if (_ai != null) _ai.SetFollowTarget(null);

            Vector3 dir = fire.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir.normalized);

            // Trigger sit animation
            if (_zanim != null) _zanim.SetTrigger("emote_sit");

            // Enable resting regen bonus
            if (_stamina != null) _stamina.IsResting = true;
        }

        private void UpdateSitting()
        {
            bool shouldStop = false;

            // Fire went out or destroyed — always check
            if (_fireTarget == null || !_fireTarget) shouldStop = true;
            else
            {
                var fp = _fireTarget.GetComponent<Fireplace>();
                if (fp != null && !fp.IsBurning()) shouldStop = true;
            }

            // Enemy appeared — always check
            if (HasEnemyNearby()) shouldStop = true;

            // Organic sits (not directed) also check player state
            if (!_isDirected && !shouldStop)
            {
                var player = Player.m_localPlayer;
                if (player == null) { shouldStop = true; }
                else
                {
                    int mode = _nview?.GetZDO()?.GetInt(
                        CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                        ?? CompanionSetup.ModeFollow;
                    if (mode != CompanionSetup.ModeFollow)
                        shouldStop = true;

                    // Player stopped sitting
                    if (Player.LastEmote == null ||
                        !Player.LastEmote.Equals("sit", StringComparison.OrdinalIgnoreCase) ||
                        (DateTime.Now - Player.LastEmoteTime).TotalSeconds > 30.0)
                        shouldStop = true;

                    // Player moved away
                    if (!shouldStop)
                    {
                        float dist = Vector3.Distance(transform.position, player.transform.position);
                        if (dist > PlayerRange * 1.5f) shouldStop = true;
                    }
                }
            }

            if (shouldStop)
            {
                StopSitting();
                return;
            }

            // Heal while resting
            _healTimer -= Time.deltaTime;
            if (_healTimer <= 0f)
            {
                _healTimer = HealInterval;
                float maxHp = _character.GetMaxHealth();
                float curHp = _character.GetHealth();
                if (curHp < maxHp)
                    _character.Heal(HealRate * HealInterval, true);
            }
        }

        private void UpdateSleeping()
        {
            // Enemy appeared — wake up
            if (HasEnemyNearby())
            {
                StopSleeping();
                _isDirected = false;
                return;
            }

            // Bed destroyed
            if (_bedTarget == null || !_bedTarget)
            {
                StopSleeping();
                _isDirected = false;
                return;
            }

            // Heal while sleeping (same rate as sitting)
            _healTimer -= Time.deltaTime;
            if (_healTimer <= 0f)
            {
                _healTimer = HealInterval;
                float maxHp = _character.GetMaxHealth();
                float curHp = _character.GetHealth();
                if (curHp < maxHp)
                    _character.Heal(HealRate * HealInterval, true);
            }
        }

        private void StopSitting()
        {
            _isSitting   = false;
            _fireTarget  = null;
            _isDirected  = false;

            CompanionsPlugin.Log.LogInfo("[Rest] Stopped sitting — resuming normal behavior");

            // Stop sit animation
            if (_zanim != null) _zanim.SetTrigger("emote_stop");

            // Disable resting bonus
            if (_stamina != null) _stamina.IsResting = false;

            if (_ai == null) return;

            int mode = _nview?.GetZDO()?.GetInt(
                CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                ?? CompanionSetup.ModeFollow;

            if (mode == CompanionSetup.ModeStay)
            {
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPoint();
            }
            else if (Player.m_localPlayer != null)
            {
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private GameObject FindNearbyFire()
        {
            Vector3 center = transform.position;
            float rangeSq = FireRange * FireRange;

            int hitCount = Physics.OverlapSphereNonAlloc(
                center, FireRange, _fireScanBuffer);
            Collider[] colliders = _fireScanBuffer;
            int colliderCount = hitCount;
            if (colliderCount > FireScanBufferSize) colliderCount = FireScanBufferSize;

            _seenFireIds.Clear();

            Fireplace nearest = null;
            float nearestSq = float.MaxValue;

            for (int i = 0; i < colliderCount; i++)
            {
                var col = colliders[i];
                if (col == null) continue;

                var fp = col.GetComponentInParent<Fireplace>();
                if (fp == null || !fp.IsBurning()) continue;
                if (!_seenFireIds.Add(fp.GetInstanceID())) continue;

                float distSq = (fp.transform.position - center).sqrMagnitude;
                if (distSq > rangeSq || distSq >= nearestSq) continue;

                nearest = fp;
                nearestSq = distSq;
            }

            return nearest != null ? nearest.gameObject : null;
        }

        private bool HasEnemyNearby()
        {
            if (_character == null) return false;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, c)) continue;
                float d = Vector3.Distance(transform.position, c.transform.position);
                if (d < 15f) return true;
            }
            return false;
        }
    }
}
