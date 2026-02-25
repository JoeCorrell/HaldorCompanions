using System;
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
        private MonsterAI        _ai;
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

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
            _ai        = GetComponent<MonsterAI>();
            _stamina   = GetComponent<CompanionStamina>();
            _zanim     = GetComponent<ZSyncAnimation>();
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

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

        private void StartSitting(GameObject fire)
        {
            _isSitting   = true;
            _fireTarget  = fire;
            _healTimer   = 0f;

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
            // Check if we should stop
            bool shouldStop = false;

            var player = Player.m_localPlayer;
            if (player == null) { shouldStop = true; }
            else
            {
                // Player stopped sitting (static members)
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

            // Fire went out or destroyed
            if (_fireTarget == null || !_fireTarget) shouldStop = true;
            else
            {
                var fp = _fireTarget.GetComponent<Fireplace>();
                if (fp != null && !fp.IsBurning()) shouldStop = true;
            }

            // Enemy appeared
            if (HasEnemyNearby()) shouldStop = true;

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

        private void StopSitting()
        {
            _isSitting  = false;
            _fireTarget = null;

            // Stop sit animation
            if (_zanim != null) _zanim.SetTrigger("emote_stop");

            // Disable resting bonus
            if (_stamina != null) _stamina.IsResting = false;

            // Resume following player
            if (_ai != null && Player.m_localPlayer != null)
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private GameObject FindNearbyFire()
        {
            // Search for burning fireplaces within range
            foreach (var fp in FindObjectsByType<Fireplace>(FindObjectsSortMode.None))
            {
                if (fp == null || !fp.IsBurning()) continue;
                float dist = Vector3.Distance(transform.position, fp.transform.position);
                if (dist <= FireRange)
                    return fp.gameObject;
            }
            return null;
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
