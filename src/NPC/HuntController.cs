using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Companion hunting controller. Active in ModeHunt.
    ///
    /// State machine:
    ///   Scanning       — periodic prey scan, engage CombatAI on find
    ///   Hunting        — CombatAI owns movement/attack; we monitor distance + death
    ///   CollectingDrops — prey killed; walk to kill site, pick up loot, then back to Scanning
    ///
    /// Respects the same home / follow rules as other action modes.
    /// </summary>
    public class HuntController : MonoBehaviour
    {
        // ══════════════════════════════════════════════════════════════════════
        //  Configuration
        // ══════════════════════════════════════════════════════════════════════

        private const float ScanInterval    = 3f;    // seconds between prey scans
        private const float ScanRadius      = 20f;   // matches AI view/hear range

        private const float DropScanRadius  = 12f;   // radius to search for drops around kill pos
        private const float DropPickupRange = 3.0f;  // close enough to Pickup()
        private const float DropScanDelay   = 0.6f;  // wait for drops to spawn after kill
        private const float DropTimeout     = 12f;   // give up collecting after this many seconds

        private static readonly string[] s_preyPrefixes =
            { "Boar", "Deer", "Chicken", "Hare" };

        // ══════════════════════════════════════════════════════════════════════
        //  Components
        // ══════════════════════════════════════════════════════════════════════

        private ZNetView          _nview;
        private CompanionAI       _ai;
        private CompanionCombatAI _combatAI;
        private CompanionSetup    _setup;
        private Character         _character;
        private Humanoid          _humanoid;

        // ══════════════════════════════════════════════════════════════════════
        //  State
        // ══════════════════════════════════════════════════════════════════════

        private enum HuntState { Scanning, Hunting, CollectingDrops }
        private HuntState _state = HuntState.Scanning;

        // Scanning / Hunting
        private float     _scanTimer;
        private Character _prey;           // tracked so we can detect death
        private Vector3   _lastPreyPos;   // continuously updated; used when prey destroyed before check

        // CollectingDrops
        private Vector3    _killPos;
        private GameObject _currentDrop;
        private float      _dropTimer;
        private float      _dropScanDelayTimer;
        private int        _dropsPickedUp;
        private readonly Collider[] _dropBuffer = new Collider[64];
        private int        _itemLayerMask;

        // ── Public state ────────────────────────────────────────────────────

        /// <summary>
        /// True while HuntController is driving movement (hunting or collecting loot).
        /// CompanionSetup checks this to suppress follow-target overrides.
        /// </summary>
        public bool IsActive => GetMode() == CompanionSetup.ModeHunt
                             && (_state == HuntState.Hunting || _state == HuntState.CollectingDrops);

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _nview         = GetComponent<ZNetView>();
            _ai            = GetComponent<CompanionAI>();
            _combatAI      = GetComponent<CompanionCombatAI>();
            _setup         = GetComponent<CompanionSetup>();
            _character     = GetComponent<Character>();
            _humanoid      = GetComponent<Humanoid>();
            _itemLayerMask = LayerMask.GetMask("item");
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;

            int mode = GetMode();
            if (mode != CompanionSetup.ModeHunt)
            {
                // Leaving hunt mode — abort everything
                if (_combatAI != null && _combatAI.HuntMode)
                    _combatAI.HuntMode = false;
                _state = HuntState.Scanning;
                _prey  = null;
                return;
            }

            switch (_state)
            {
                case HuntState.Scanning:        UpdateScanning();        break;
                case HuntState.Hunting:         UpdateHunting();         break;
                case HuntState.CollectingDrops: UpdateCollectingDrops(); break;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Scanning — idle between prey finds
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateScanning()
        {
            // A regular (non-hunt) threat engaged CombatAI — self-defence; don't scan.
            if (_combatAI != null && _combatAI.IsEngaged && !_combatAI.HuntMode)
                return;

            // UI guard
            if (CompanionInteractPanel.IsOpenFor(_setup) || CompanionRadialMenu.IsOpenFor(_setup))
                return;

            _scanTimer -= Time.deltaTime;
            if (_scanTimer > 0f) return;
            _scanTimer = ScanInterval;

            Character prey = FindPrey();
            if (prey == null) return;

            CompanionsPlugin.Log.LogInfo(
                $"[Hunt] Found prey \"{prey.m_name}\" " +
                $"dist={Vector3.Distance(transform.position, prey.transform.position):F1}m — engaging");

            _prey = prey;
            _combatAI.HuntMode = true;
            _combatAI.Engage(prey);
            _state = HuntState.Hunting;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Hunting — CombatAI controls movement; we monitor prey
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateHunting()
        {
            // Self-defence took over (non-hunt engagement)
            if (_combatAI != null && _combatAI.IsEngaged && !_combatAI.HuntMode)
                return;

            // CombatAI disengaged — either prey fled/died or it was cleared externally
            if (_combatAI == null || !_combatAI.IsEngaged)
            {
                // Character.IsDead() always returns false for non-player characters.
                // OnDeath() calls ZNetScene.Destroy immediately, so by the time this
                // runs, _prey may already be Unity-null (destroyed). Use !_prey as the
                // death signal, or fall back to GetHealth() if still accessible.
                bool preyDestroyed = !_prey;   // Unity fake-null = GameObject destroyed
                bool preyDead = !preyDestroyed && _prey.GetHealth() <= 0f;

                if (preyDestroyed || preyDead)
                {
                    // Use the last cached position — the reference may be null already.
                    _killPos            = preyDestroyed ? _lastPreyPos : _prey.transform.position;
                    _currentDrop        = null;
                    _dropTimer          = 0f;
                    _dropScanDelayTimer = DropScanDelay;
                    _dropsPickedUp      = 0;
                    _state              = HuntState.CollectingDrops;
                    if (_ai != null) _ai.StopMoving();

                    CompanionsPlugin.Log.LogInfo(
                        $"[Hunt] Prey killed (destroyed={preyDestroyed}) — collecting drops at {_killPos:F1}");
                }
                else
                {
                    // Prey fled; back to scanning
                    _state = HuntState.Scanning;
                    _scanTimer = ScanInterval * 0.5f; // shorter wait before rescanning
                }
                _prey = null;
                return;
            }

            // CombatAI is engaged in hunt mode — check prey status
            Character target = _combatAI.TargetCreature;

            // Continuously cache the last known target position so the disengaged
            // path can use it even after the GameObject is destroyed.
            if (target != null)
                _lastPreyPos = target.transform.position;

            // Character.IsDead() returns false for all non-player characters — check
            // health instead. Also handle Unity-null (destroyed between frames).
            bool targetDestroyed = !target;
            bool targetDead = !targetDestroyed && target.GetHealth() <= 0f;

            if (targetDestroyed || targetDead)
            {
                // Prey just died while CombatAI is still in its last frame — disengage and collect
                _killPos = target != null ? target.transform.position : _lastPreyPos;
                _combatAI.Disengage();
                _currentDrop        = null;
                _dropTimer          = 0f;
                _dropScanDelayTimer = DropScanDelay;
                _dropsPickedUp      = 0;
                _prey               = null;
                _state              = HuntState.CollectingDrops;
                if (_ai != null) _ai.StopMoving();

                CompanionsPlugin.Log.LogInfo(
                    $"[Hunt] Prey killed — moving to collect drops at {_killPos:F1}");
                return;
            }

            // Prey fled beyond scan radius — disengage, back to scanning
            if (target != null)
            {
                float dist = Vector3.Distance(transform.position, target.transform.position);
                if (dist > ScanRadius)
                {
                    CompanionsPlugin.Log.LogInfo(
                        $"[Hunt] Prey \"{target.m_name}\" fled beyond {ScanRadius}m — disengaging");
                    _combatAI.Disengage();
                    _prey  = null;
                    _state = HuntState.Scanning;
                    _scanTimer = ScanInterval;
                }
            }
            else
            {
                // Target became null without death confirmed — disengage
                _combatAI.Disengage();
                _prey  = null;
                _state = HuntState.Scanning;
                _scanTimer = ScanInterval;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  CollectingDrops — walk to kill site, pick up loot
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateCollectingDrops()
        {
            float dt = Time.deltaTime;
            _dropTimer += dt;

            if (_dropTimer > DropTimeout)
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[Hunt] Drop collection timed out — picked up {_dropsPickedUp} items, resuming scan");
                FinishDropCollection();
                return;
            }

            // Wait for drops to spawn
            if (_dropScanDelayTimer > 0f)
            {
                _dropScanDelayTimer -= dt;
                // Walk toward kill position during the delay
                _ai?.MoveToPoint(dt, _killPos, DropPickupRange, true);
                return;
            }

            // Move toward and pick up current drop target
            if (_currentDrop != null)
            {
                var nview = _currentDrop.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid())
                {
                    _currentDrop = null;
                    // fall through to scan
                }
                else
                {
                    float distToDrop = Vector3.Distance(transform.position, _currentDrop.transform.position);
                    if (distToDrop <= DropPickupRange)
                    {
                        TryPickupDrop(_currentDrop);
                        _currentDrop = null;
                        // fall through to scan for more
                    }
                    else
                    {
                        _ai?.MoveToPoint(dt, _currentDrop.transform.position, DropPickupRange * 0.5f, true);
                        return;
                    }
                }
            }

            // Scan for next drop
            _currentDrop = ScanForDrops();
            if (_currentDrop == null)
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[Hunt] No more drops — picked up {_dropsPickedUp} items, resuming scan");
                FinishDropCollection();
                return;
            }

            // Start moving to newly found drop
            _ai?.MoveToPoint(dt, _currentDrop.transform.position, DropPickupRange * 0.5f, true);

            var itemDrop = _currentDrop.GetComponent<ItemDrop>();
            string itemName = itemDrop?.m_itemData?.m_shared?.m_name ?? "?";
            float dist2 = Vector3.Distance(transform.position, _currentDrop.transform.position);
            CompanionsPlugin.Log.LogInfo($"[Hunt] Moving to drop \"{itemName}\" dist={dist2:F1}m");
        }

        private GameObject ScanForDrops()
        {
            int count = Physics.OverlapSphereNonAlloc(_killPos, DropScanRadius, _dropBuffer, _itemLayerMask);
            if (count > _dropBuffer.Length) count = _dropBuffer.Length;

            GameObject best     = null;
            float      bestDist = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                var col = _dropBuffer[i];
                if (col == null || col.attachedRigidbody == null) continue;

                var itemDrop = col.attachedRigidbody.GetComponent<ItemDrop>();
                if (itemDrop == null || !itemDrop.m_autoPickup) continue;

                var nview = itemDrop.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                float dist = Vector3.Distance(transform.position, itemDrop.transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best     = itemDrop.gameObject;
                }
            }

            return best;
        }

        private void TryPickupDrop(GameObject dropGO)
        {
            if (dropGO == null || _humanoid == null) return;

            var itemDrop = dropGO.GetComponent<ItemDrop>();
            if (itemDrop == null) return;

            string itemName = itemDrop.m_itemData?.m_shared?.m_name ?? "?";
            int    stack    = itemDrop.m_itemData?.m_stack ?? 0;

            bool picked = _humanoid.Pickup(dropGO, false, false);
            if (picked)
            {
                _dropsPickedUp++;
                CompanionsPlugin.Log.LogInfo(
                    $"[Hunt] Picked up \"{itemName}\" x{stack} (total {_dropsPickedUp})");
            }
        }

        private void FinishDropCollection()
        {
            _currentDrop = null;
            _state       = HuntState.Scanning;
            _scanTimer   = 0f;  // scan immediately for next prey

            // Restore follow if enabled
            bool follow = _setup != null && _setup.GetFollow();
            if (_ai != null && follow && Player.m_localPlayer != null)
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Prey Scanning
        // ══════════════════════════════════════════════════════════════════════

        private Character FindPrey()
        {
            bool stayHome = _setup != null && _setup.GetStayHome() && _setup.HasHomePosition();
            Vector3 origin = stayHome ? _setup.GetHomePosition() : transform.position;
            float radius   = stayHome
                ? Mathf.Min(ScanRadius, CompanionSetup.MaxLeashDistance)
                : ScanRadius;

            Character best   = null;
            float bestDistSq = radius * radius;

            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead() || c == _character) continue;
                if (!IsPrey(c)) continue;

                float distSq = (c.transform.position - origin).sqrMagnitude;
                if (distSq > bestDistSq) continue;

                bestDistSq = distSq;
                best = c;
            }

            return best;
        }

        private static bool IsPrey(Character c)
        {
            string name = c.gameObject.name;
            foreach (string prefix in s_preyPrefixes)
                if (name.StartsWith(prefix, System.StringComparison.Ordinal))
                    return true;
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called when action mode changes away from Hunt.
        /// Aborts all hunt activity immediately.
        /// </summary>
        public void NotifyActionModeChanged()
        {
            if (_combatAI != null && _combatAI.HuntMode)
                _combatAI.HuntMode = false;
            _state     = HuntState.Scanning;
            _prey      = null;
            _scanTimer = 0f;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private int GetMode()
        {
            var zdo = _nview?.GetZDO();
            return zdo?.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                ?? CompanionSetup.ModeFollow;
        }
    }
}
