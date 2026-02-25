using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Combat AI for companions. Runs alongside MonsterAI and handles:
    /// - Blocking and parrying incoming melee attacks
    /// - Facing attackers during block
    /// - Stamina-gated combat (via CompanionStamina + Harmony bridge)
    /// MonsterAI still handles target selection, weapon equipping, and attack initiation.
    /// </summary>
    public class CompanionAI : MonoBehaviour
    {
        // ── Reflection ──────────────────────────────────────────────────────
        private static readonly FieldInfo _blockingField =
            AccessTools.Field(typeof(Character), "m_blocking");

        // ── References ──────────────────────────────────────────────────────
        private Character        _character;
        private Humanoid         _humanoid;
        private MonsterAI        _ai;
        private ZNetView         _nview;
        private CompanionStamina _stamina;

        // ── Blocking state ──────────────────────────────────────────────────
        private bool      _isBlocking;
        private float     _blockHoldTimer;
        private float     _blockCooldown;
        private float     _blockDelayTimer;
        private Character _blockTarget;

        private const float BlockHoldDuration = 0.5f;   // hold block this long
        private const float BlockCooldownTime = 2.0f;    // wait between blocks
        private const float BlockReactDelay   = 0.15f;   // delay after detecting attack before blocking
        private const float BlockDetectRange  = 5f;      // detect enemy attacks within this range
        private const float BlockChance       = 0.5f;    // 50% chance to attempt a block

        // ── Idle facing ─────────────────────────────────────────────────────
        private float _idleTimer;
        private const float IdleFaceDelay = 1.5f; // face player direction after idle this long

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _character = GetComponent<Character>();
            _humanoid  = GetComponent<Humanoid>();
            _ai        = GetComponent<MonsterAI>();
            _nview     = GetComponent<ZNetView>();
            _stamina   = GetComponent<CompanionStamina>();
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

            float dt = Time.deltaTime;

            _blockCooldown -= dt;

            UpdateBlocking(dt);
            UpdateIdleFacing(dt);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Blocking / Parrying
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateBlocking(float dt)
        {
            // Don't block while we're attacking or staggering
            if (_character.InAttack() || _character.IsStaggering())
            {
                if (_isBlocking) ReleaseBlock();
                return;
            }

            // If currently holding a block, tick down the timer
            if (_isBlocking)
            {
                _blockHoldTimer -= dt;
                if (_blockHoldTimer <= 0f)
                    ReleaseBlock();
                else
                    FaceBlockTarget();
                return;
            }

            // Waiting for delayed block trigger
            if (_blockTarget != null)
            {
                _blockDelayTimer -= dt;
                if (_blockDelayTimer <= 0f)
                {
                    StartBlock();
                    _blockTarget = null;
                }
                else
                {
                    FaceBlockTarget();
                }
                return;
            }

            // Scan for incoming attacks
            if (_blockCooldown > 0f) return;
            if (!HasBlocker()) return;

            var attacker = FindAttackingEnemy();
            if (attacker == null) return;

            // Random chance — companions don't block every attack
            if (Random.value > BlockChance)
            {
                // Skip this attack, but still set a short cooldown
                _blockCooldown = BlockCooldownTime * 0.5f;
                return;
            }

            // Queue a delayed block for better parry timing
            _blockTarget     = attacker;
            _blockDelayTimer = BlockReactDelay;
        }

        private void StartBlock()
        {
            if (_blockingField == null) return;

            _isBlocking     = true;
            _blockHoldTimer = BlockHoldDuration;
            _blockCooldown  = BlockCooldownTime;

            _blockingField.SetValue(_character, true);
        }

        private void ReleaseBlock()
        {
            if (_blockingField == null) return;

            _isBlocking  = false;
            _blockTarget = null;

            _blockingField.SetValue(_character, false);
        }

        private void FaceBlockTarget()
        {
            if (_blockTarget == null || !_blockTarget) return;

            Vector3 dir = _blockTarget.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir.normalized);
        }

        private static readonly FieldInfo _leftItemField =
            AccessTools.Field(typeof(Humanoid), "m_leftItem");

        private bool HasBlocker()
        {
            if (_humanoid == null) return false;

            // Check left hand (shield) first, then current weapon
            var left = _leftItemField?.GetValue(_humanoid) as ItemDrop.ItemData;
            if (left != null && left.m_shared.m_blockPower > 0f) return true;

            var weapon = _humanoid.GetCurrentWeapon();
            if (weapon != null && weapon.m_shared.m_blockPower > 0f) return true;

            return false;
        }

        /// <summary>
        /// Find the nearest enemy within melee range that is mid-attack animation.
        /// </summary>
        private Character FindAttackingEnemy()
        {
            Character nearest = null;
            float nearestDist = float.MaxValue;

            List<Character> all = Character.GetAllCharacters();
            for (int i = 0; i < all.Count; i++)
            {
                Character c = all[i];
                if (c == _character) continue;
                if (c.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, c)) continue;
                if (!c.InAttack()) continue;

                float dist = Vector3.Distance(transform.position, c.transform.position);
                if (dist > BlockDetectRange) continue;

                if (dist < nearestDist)
                {
                    nearest     = c;
                    nearestDist = dist;
                }
            }

            return nearest;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Idle facing — match player direction when standing still
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateIdleFacing(float dt)
        {
            if (_ai == null) return;

            // Only face player when following and close enough
            var follow = _ai.GetFollowTarget();
            if (follow == null) return;

            float dist = Vector3.Distance(transform.position, follow.transform.position);

            // If far away or moving, reset idle timer
            var moveDir = _character.GetMoveDir();
            bool isMoving = moveDir.sqrMagnitude > 0.01f;

            if (isMoving || dist > 3f)
            {
                _idleTimer = 0f;
                return;
            }

            // No combat target — idle near player
            if (_character.InAttack() || _isBlocking) return;

            _idleTimer += dt;
            if (_idleTimer < IdleFaceDelay) return;

            // Slowly rotate to match player's forward direction
            Vector3 playerForward = follow.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.01f) return;

            Quaternion target = Quaternion.LookRotation(playerForward);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, target, dt * 2f);
        }
    }
}
