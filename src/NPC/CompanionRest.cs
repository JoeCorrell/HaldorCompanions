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
        // ── Static instance tracking (used by SleepPatches) ──
        private static readonly List<CompanionRest> _instances = new List<CompanionRest>();
        internal static IReadOnlyList<CompanionRest> Instances => _instances;

        private ZNetView         _nview;
        private Character        _character;
        private CompanionAI      _ai;
        private CompanionStamina _stamina;
        private ZSyncAnimation   _zanim;
        private CompanionSetup   _setup;
        private Rigidbody        _body;

        private bool  _isSitting;
        private float _checkTimer;
        private float _healTimer;
        private GameObject _fireTarget;

        // Navigation state — companion walks to target before snapping into position
        private bool    _navigating;
        private Vector3 _navTarget;
        private float   _navTimeout;

        private const float CheckInterval = 1f;
        private const float HealRate      = 2f;     // hp per second while resting
        private const float HealInterval  = 0.5f;
        private const float PlayerRange   = 5f;
        private const float FireRange     = 5f;
        private const float DirectedMaxDist    = 50f;  // stop directed rest if player is this far
        private const float DirectedSitTimeout = 300f;  // 5 min timeout for directed sit
        private const int FireScanBufferSize = 48;

        private readonly Collider[] _fireScanBuffer = new Collider[FireScanBufferSize];
        private readonly HashSet<int> _seenFireIds = new HashSet<int>();

        /// <summary>True for Player-clone companions that have Player animation params.</summary>
        private bool HasPlayerAnims => _setup != null && _setup.CanWearArmor();

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
            _ai        = GetComponent<CompanionAI>();
            _stamina   = GetComponent<CompanionStamina>();
            _zanim     = GetComponent<ZSyncAnimation>();
            _setup     = GetComponent<CompanionSetup>();
            _body      = GetComponent<Rigidbody>();
        }

        private void OnEnable()
        {
            if (!_instances.Contains(this))
                _instances.Add(this);
        }

        private void OnDisable()
        {
            _instances.Remove(this);
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

            if (_navigating)
            {
                UpdateNavigation();
                return;
            }

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
            _instances.Remove(this);
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
        private float _directedRestTimer; // countdown for directed sit/sleep timeout

        /// <summary>True when the companion is sitting or sleeping (organic or directed).</summary>
        public bool IsResting => _isSitting || _isSleeping;

        /// <summary>True when the companion is sleeping in a bed.</summary>
        public bool IsSleeping => _isSleeping;

        /// <summary>True when navigating to a bed or fire before resting.</summary>
        public bool IsNavigating => _navigating;

        /// <summary>The position the companion is walking toward.</summary>
        public Vector3 NavTarget => _navTarget;

        /// <summary>True when the companion is in a directed sit or sleep.</summary>
        public bool IsDirectedResting => _isDirected && (_isSitting || _isSleeping);

        /// <summary>Direct the companion to sit near a fire (from hotkey). Toggles off if already sitting.</summary>
        public void DirectSit(GameObject fire)
        {
            if (fire == null) return;

            CompanionsPlugin.Log.LogDebug(
                $"[Rest] DirectSit called — sitting={_isSitting}, sleeping={_isSleeping}, " +
                $"directed={_isDirected}, navigating={_navigating}, fire=\"{fire.name}\"");

            // Toggle off if already sitting (directed) or navigating
            if ((_isSitting || _navigating) && _isDirected)
            {
                CompanionsPlugin.Log.LogDebug("[Rest] DirectSit TOGGLE OFF — calling StopAll");
                StopAll();
                return;
            }

            if (_isSitting || _isSleeping || _navigating) StopAll();

            _isDirected = true;
            _fireTarget = fire;
            _directedRestTimer = DirectedSitTimeout;

            float dist = Vector3.Distance(transform.position, fire.transform.position);
            if (dist < 3f)
            {
                // Already close — sit immediately
                StartSitting(fire);
            }
            else
            {
                // Navigate to fire first
                _navigating = true;
                _navTarget = fire.transform.position;
                _navTimeout = 15f;
                if (_ai != null) _ai.SetFollowTarget(null);
                CompanionsPlugin.Log.LogDebug(
                    $"[Rest] Navigating to fire — dist={dist:F1}m, timeout=15s");
            }
        }

        /// <summary>Direct the companion to sleep at a bed (from hotkey). Toggles off if already sleeping.</summary>
        public void DirectSleep(Bed bed)
        {
            if (bed == null) return;

            CompanionsPlugin.Log.LogDebug(
                $"[Rest] DirectSleep called — sleeping={_isSleeping}, sitting={_isSitting}, " +
                $"directed={_isDirected}, navigating={_navigating}, bed=\"{bed.name}\" myPos={transform.position:F2}");

            // Toggle off if already sleeping or navigating
            if ((_isSleeping || _navigating) && _isDirected)
            {
                CompanionsPlugin.Log.LogDebug("[Rest] DirectSleep TOGGLE OFF — calling StopAll");
                StopAll();
                return;
            }

            if (_isSitting || _isSleeping || _navigating) StopAll();

            _isDirected = true;
            _bedTarget  = bed;
            _healTimer  = 0f;

            // Compute bed position
            Vector3 bedPos = bed.transform.position;
            if (bed.m_spawnPoint != null)
                bedPos = bed.m_spawnPoint.position;

            float dist = Vector3.Distance(transform.position, bedPos);
            if (dist < 3f)
            {
                // Already close — snap into bed immediately
                FinalizeSleep();
            }
            else
            {
                // Navigate to bed first
                _navigating = true;
                _navTarget = bedPos;
                _navTimeout = 15f;
                if (_ai != null) _ai.SetFollowTarget(null);
                CompanionsPlugin.Log.LogDebug(
                    $"[Rest] Navigating to bed — dist={dist:F1}m, timeout=15s");
            }
        }

        /// <summary>Cancel any directed sit or sleep.</summary>
        public void CancelDirected()
        {
            if (!_isDirected && !_isSitting && !_isSleeping && !_navigating) return;
            StopAll();
        }

        /// <summary>Called by CompanionAI when the companion arrives at the navigation target.</summary>
        public void ArriveAtNavTarget()
        {
            if (!_navigating) return;
            _navigating = false;

            CompanionsPlugin.Log.LogDebug(
                $"[Rest] Arrived at nav target — bed={(_bedTarget != null)}, fire={(_fireTarget != null)}");

            if (_bedTarget != null)
                FinalizeSleep();
            else if (_fireTarget != null)
                StartSitting(_fireTarget);
        }

        private void UpdateNavigation()
        {
            _navTimeout -= Time.deltaTime;

            // Timeout — give up navigating
            if (_navTimeout <= 0f)
            {
                CompanionsPlugin.Log.LogDebug("[Rest] Navigation timed out — cancelling");
                CancelNavigation();
                return;
            }

            // Target destroyed while navigating
            if (_bedTarget == null && _fireTarget == null)
            {
                CompanionsPlugin.Log.LogDebug("[Rest] Navigation target destroyed — cancelling");
                CancelNavigation();
                return;
            }

            // Actual movement is handled by CompanionAI.UpdateAI
        }

        private void CancelNavigation()
        {
            _navigating = false;
            _isDirected = false;
            _bedTarget = null;
            _fireTarget = null;

            // Restore follow target
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

        private void FinalizeSleep()
        {
            _isSleeping = true;

            // Character.AttachStart is a no-op on non-Player characters (empty virtual).
            // Implement the bed attach manually: position, animation, rigidbody, ZDO.
            Transform attachPoint = _bedTarget.m_spawnPoint != null
                ? _bedTarget.m_spawnPoint : _bedTarget.transform;

            // Position at bed
            transform.position = attachPoint.position;
            transform.rotation = attachPoint.rotation;

            // Freeze rigidbody — prevent physics from pushing companion off bed
            if (_body != null)
            {
                _body.position = attachPoint.position;
                _body.velocity = Vector3.zero;
                _body.useGravity = false;
                _body.isKinematic = true;
            }

            // Bed animation (Player-model only — DvergerMage lacks this param)
            CompanionsPlugin.Log.LogDebug(
                $"[Rest] FinalizeSleep anim — HasPlayerAnims={HasPlayerAnims} " +
                $"canWearArmor={_setup?.CanWearArmor()} zanim={_zanim != null}");
            if (_zanim != null && HasPlayerAnims) _zanim.SetBool("attach_bed", true);

            // ZDO inBed flag
            if (_nview?.GetZDO() != null) _nview.GetZDO().Set(ZDOVars.s_inBed, true);

            // Enable resting regen (Rested buff applies on wakeup, same as vanilla)
            if (_stamina != null) _stamina.IsResting = true;

            // Set bed as companion's spawn point (like vanilla player bed claim).
            // Save a bedside position (not on-bed) so respawns don't clip through roof.
            if (_setup != null)
            {
                Vector3 bedside = _bedTarget.transform.position + _bedTarget.transform.right * 1.5f;
                if (ZoneSystem.instance != null &&
                    ZoneSystem.instance.FindFloor(bedside, out float floorY))
                    bedside.y = floorY;
                _setup.SetHomePosition(bedside);
                CompanionsPlugin.Log.LogInfo(
                    $"[Rest] Bed spawn point set at {bedside:F2} (bedside offset)");
            }

            CompanionsPlugin.Log.LogDebug(
                $"[Rest] FinalizeSleep — positioned at {attachPoint.position:F2}");
        }

        private void StopAll()
        {
            CompanionsPlugin.Log.LogDebug(
                $"[Rest] StopAll — sitting={_isSitting}, sleeping={_isSleeping}, " +
                $"directed={_isDirected}, navigating={_navigating}, pos={transform.position:F2}");

            bool wasNavigating = _navigating;
            _navigating = false;
            if (_isSitting) StopSitting();
            if (_isSleeping) StopSleeping();
            _isDirected = false;

            // If we were navigating (not yet sitting/sleeping), clean up nav state
            // and restore follow. StopSitting/StopSleeping handle their own restore.
            if (wasNavigating && !_isSitting && !_isSleeping)
            {
                _fireTarget = null;
                _bedTarget = null;
                if (_ai != null)
                {
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
            }
        }

        private void StopSleeping()
        {
            // Save bed reference before clearing — needed for positioning
            Bed bed = _bedTarget;
            _isSleeping = false;
            _bedTarget  = null;

            CompanionsPlugin.Log.LogDebug("[Rest] Stopped sleeping — resuming normal behavior");

            // Character.AttachStop is a no-op on non-Player characters (empty virtual).
            // Manually reverse the attach: animation, rigidbody, ZDO, position offset.
            if (_zanim != null && HasPlayerAnims) _zanim.SetBool("attach_bed", false);
            if (_nview?.GetZDO() != null) _nview.GetZDO().Set(ZDOVars.s_inBed, false);

            // Position beside the bed (not on it) to prevent clipping through roof.
            // Offset along the bed's right axis so the companion stands at the bedside.
            if (bed != null)
            {
                Vector3 bedside = bed.transform.position + bed.transform.right * 1.5f;
                // Use floor height if available, otherwise bed Y
                if (ZoneSystem.instance != null &&
                    ZoneSystem.instance.FindFloor(bedside, out float floorY))
                    bedside.y = floorY;
                transform.position = bedside;
            }

            // Re-enable physics so the companion can walk
            if (_body != null)
            {
                _body.isKinematic = false;
                _body.useGravity = true;
                _body.velocity = Vector3.zero;
                if (bed != null) _body.position = transform.position;
            }

            if (_stamina != null) _stamina.IsResting = false;

            // Apply Rested buff on wakeup (same as vanilla Player.SetSleeping(false))
            var restedBuff = GetComponent<CompanionRestedBuff>();
            if (restedBuff != null) restedBuff.ApplyRestedBuff();

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

            CompanionsPlugin.Log.LogDebug(
                $"[Rest] Sitting near fire \"{fire.name}\" — starting heal + stamina regen " +
                $"pos={transform.position:F2}");

            // Stop movement and face the fire
            if (_ai != null)
            {
                _ai.SetFollowTarget(null);
                _ai.FreezeTimer = 0.5f;
            }

            Vector3 dir = fire.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir.normalized);

            // Trigger sit animation (Player-model only)
            CompanionsPlugin.Log.LogDebug(
                $"[Rest] StartSitting anim — HasPlayerAnims={HasPlayerAnims} " +
                $"canWearArmor={_setup?.CanWearArmor()} zanim={_zanim != null}");
            if (_zanim != null && HasPlayerAnims) _zanim.SetTrigger("emote_sit");

            // Enable resting regen bonus
            if (_stamina != null) _stamina.IsResting = true;

            // Start resting warmup — Rested buff applies after ~20s (like vanilla)
            var restedBuff = GetComponent<CompanionRestedBuff>();
            if (restedBuff != null) restedBuff.StartResting();
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
                // EffectArea heat sources have no fuel — just check object exists (already handled above)
            }

            // Enemy appeared — always check
            if (HasEnemyNearby()) shouldStop = true;

            // Directed sits: timeout and max-distance safety (prevents permanent stuck state)
            if (_isDirected && !shouldStop)
            {
                _directedRestTimer -= Time.deltaTime;
                if (_directedRestTimer <= 0f)
                {
                    CompanionsPlugin.Log.LogDebug("[Rest] Directed sit timed out — stopping");
                    shouldStop = true;
                }
                else
                {
                    var player = Player.m_localPlayer;
                    if (player != null)
                    {
                        float dist = Vector3.Distance(transform.position, player.transform.position);
                        if (dist > DirectedMaxDist)
                        {
                            CompanionsPlugin.Log.LogDebug(
                                $"[Rest] Directed sit — player too far ({dist:F1}m > {DirectedMaxDist}m), stopping");
                            shouldStop = true;
                        }
                    }
                }
            }

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
                CompanionsPlugin.Log.LogDebug("[Rest] UpdateSleeping — enemy nearby, waking up!");
                StopSleeping();
                _isDirected = false;
                return;
            }

            // Bed destroyed
            if (_bedTarget == null || !_bedTarget)
            {
                CompanionsPlugin.Log.LogDebug("[Rest] UpdateSleeping — bed destroyed/null, waking up!");
                StopSleeping();
                _isDirected = false;
                return;
            }

            // Continuously enforce position and rotation at the bed attach point.
            // Vanilla Player.UpdateAttach() does this every frame to prevent
            // animation root motion and physics from drifting the character.
            // Without this, the companion can end up sleeping sideways.
            Transform attachPoint = _bedTarget.m_spawnPoint != null
                ? _bedTarget.m_spawnPoint : _bedTarget.transform;
            transform.position = attachPoint.position;
            transform.rotation = attachPoint.rotation;

            if (_body != null)
            {
                _body.position = attachPoint.position;
                _body.velocity = Vector3.zero;
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

            CompanionsPlugin.Log.LogDebug("[Rest] Stopped sitting — resuming normal behavior");

            // Stop sit animation — use both trigger and bool clear to handle
            // all animator state paths. Trigger alone may not fire if the
            // animator didn't consume the previous sit trigger.
            if (_zanim != null && HasPlayerAnims)
            {
                _zanim.SetTrigger("emote_stop");
                _zanim.SetBool("emote_sit", false);
            }

            // Disable resting bonus and cancel warmup if incomplete
            if (_stamina != null) _stamina.IsResting = false;
            var restedBuff = GetComponent<CompanionRestedBuff>();
            if (restedBuff != null) restedBuff.StopResting();

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

            GameObject nearest = null;
            float nearestSq = float.MaxValue;

            for (int i = 0; i < colliderCount; i++)
            {
                var col = colliders[i];
                if (col == null) continue;

                // Check for Fireplace (campfire, hearth, bonfire, torches, etc.)
                var fp = col.GetComponentInParent<Fireplace>();
                if (fp != null && fp.IsBurning())
                {
                    if (!_seenFireIds.Add(fp.GetInstanceID())) continue;
                    float distSq = (fp.transform.position - center).sqrMagnitude;
                    if (distSq < rangeSq && distSq < nearestSq)
                    {
                        nearest = fp.gameObject;
                        nearestSq = distSq;
                    }
                    continue;
                }

                // Check for EffectArea with Heat type (forge, smelter, modded fires, etc.)
                var ea = col.GetComponentInParent<EffectArea>();
                if (ea != null && (ea.m_type & EffectArea.Type.Heat) != 0)
                {
                    if (!_seenFireIds.Add(ea.GetInstanceID())) continue;
                    float distSq = (ea.transform.position - center).sqrMagnitude;
                    if (distSq < rangeSq && distSq < nearestSq)
                    {
                        nearest = ea.gameObject;
                        nearestSq = distSq;
                    }
                }
            }

            return nearest;
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
