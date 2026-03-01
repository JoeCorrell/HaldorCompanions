using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Companion smelting controller. Manages a continuous refill + smelt loop:
    ///   1. Scan for nearby kilns and furnaces/blast furnaces
    ///   2. Find nearby chests with valid fuel/ore materials
    ///   3. Walk to chest → take materials → walk to smelter → insert
    ///   4. Prioritize kilns first (they produce charcoal for furnaces)
    ///   5. Monitor levels and refill as needed while materials remain
    ///   6. Collect smelted output and store in chests
    ///
    /// State machine: Idle → Scanning → MovingToChest → TakingFromChest →
    ///                MovingToSmelter → InsertingItem → Monitoring → CollectingOutput →
    ///                StoringOutput → Idle
    /// </summary>
    public class SmeltController : MonoBehaviour
    {
        internal enum SmeltPhase
        {
            Idle,
            Scanning,
            MovingToChest,
            TakingFromChest,
            MovingToSmelter,
            InsertingItem,
            Monitoring,
            CollectingOutput,
            MovingToOutputChest,
            StoringOutput
        }

        internal SmeltPhase Phase => _phase;
        public bool IsActive => _phase != SmeltPhase.Idle;

        // ── Components ──────────────────────────────────────────────────────
        private CompanionAI       _ai;
        private Humanoid          _humanoid;
        private Character         _character;
        private CompanionSetup    _setup;
        private ZNetView          _nview;
        private CompanionTalk     _talk;
        private DoorHandler       _doorHandler;
        private HarvestController _harvest;
        private RepairController  _repair;

        // ── State ───────────────────────────────────────────────────────────
        private SmeltPhase  _phase;
        private float       _scanTimer;
        private float       _actionTimer;
        private float       _stuckTimer;
        private float       _stuckCheckTimer;
        private Vector3     _stuckCheckPos;
        private float       _monitorTimer;
        private float       _insertTimer;

        // Current task tracking
        private Smelter     _targetSmelter;
        private Container   _targetChest;
        private Container   _outputChest;       // chest to deposit output into
        private string      _carryingItemPrefab; // prefab name of item being carried
        private bool        _carryingIsFuel;     // true if carrying fuel, false if ore/input
        private int         _carryingAmount;     // how many of the item we're carrying
        private bool        _chestOpened;        // true while a chest is held open for interaction

        // Scan results cache
        private readonly List<Smelter>   _nearbySmelters  = new List<Smelter>();
        private readonly List<Container> _nearbyChests    = new List<Container>();
        private readonly List<Piece>     _tempPieces      = new List<Piece>();

        // ── Config ──────────────────────────────────────────────────────────
        private const float ScanInterval       = 3f;
        private const float ScanRadius         = 25f;
        private const float UseDistance        = 2.5f;
        private const float MoveTimeout        = 15f;
        private const float StuckCheckPeriod   = 1f;
        private const float StuckMinDistance    = 0.5f;
        private const float MonitorInterval    = 3f;
        private const float InsertDelay        = 0.3f;
        private const int   MaxCarryOre        = 20;   // take up to 20 ore per trip
        private const int   MaxCarryFuel       = 40;   // take up to 40 fuel (coal) per trip

        // ── Reflection ─────────────────────────────────────────────────────
        private static readonly FieldInfo s_addedOreTime = typeof(Smelter)
            .GetField("m_addedOreTime", BindingFlags.NonPublic | BindingFlags.Instance);

        // ── Movement logging ─────────────────────────────────────────────
        private float _moveLogTimer;

        // ── Lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            _ai        = GetComponent<CompanionAI>();
            _humanoid  = GetComponent<Humanoid>();
            _character = GetComponent<Character>();
            _setup     = GetComponent<CompanionSetup>();
            _nview     = GetComponent<ZNetView>();
            _talk      = GetComponent<CompanionTalk>();
            _doorHandler = GetComponent<DoorHandler>();
            _harvest   = GetComponent<HarvestController>();
            _repair    = GetComponent<RepairController>();
        }

        private void Update()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

            if (ShouldAbort())
            {
                if (_phase != SmeltPhase.Idle)
                    Abort("interrupted");
                return;
            }

            // Only run when in smelt mode
            if (!IsSmeltMode() && _phase == SmeltPhase.Idle) return;
            // If mode changed away while active, stop
            if (!IsSmeltMode() && _phase != SmeltPhase.Idle)
            {
                Abort("mode changed");
                return;
            }

            float dt = Time.deltaTime;

            switch (_phase)
            {
                case SmeltPhase.Idle:        UpdateIdle(dt); break;
                case SmeltPhase.Scanning:    UpdateScanning(); break;
                case SmeltPhase.MovingToChest:    UpdateMovingToChest(dt); break;
                case SmeltPhase.TakingFromChest:  UpdateTakingFromChest(); break;
                case SmeltPhase.MovingToSmelter:  UpdateMovingToSmelter(dt); break;
                case SmeltPhase.InsertingItem:    UpdateInsertingItem(dt); break;
                case SmeltPhase.Monitoring:       UpdateMonitoring(dt); break;
                case SmeltPhase.CollectingOutput: UpdateCollectingOutput(); break;
                case SmeltPhase.MovingToOutputChest: UpdateMovingToOutputChest(dt); break;
                case SmeltPhase.StoringOutput:    UpdateStoringOutput(); break;
            }
        }

        /// <summary>
        /// Force run speed while moving to targets. Runs after CompanionAI.UpdateAI
        /// so it overrides any Follow-based walk decisions.
        /// </summary>
        private void LateUpdate()
        {
            if (_character == null) return;
            if (_phase == SmeltPhase.MovingToChest ||
                _phase == SmeltPhase.MovingToSmelter ||
                _phase == SmeltPhase.MovingToOutputChest ||
                (_phase == SmeltPhase.CollectingOutput && !_collectTriggered))
            {
                _character.SetRun(true);
            }
        }

        // ── Idle — wait then scan ───────────────────────────────────────────

        private void UpdateIdle(float dt)
        {
            _scanTimer -= dt;
            if (_scanTimer > 0f) return;
            _scanTimer = ScanInterval;
            _phase = SmeltPhase.Scanning;
        }

        // ── Scanning — find smelters and chests ────────────────────────────

        private void UpdateScanning()
        {
            ScanNearbySmelters();

            if (_nearbySmelters.Count == 0)
            {
                Log("Scan: no smelters found within " + ScanRadius + "m");
                _phase = SmeltPhase.Idle;
                _scanTimer = 10f; // back off
                return;
            }

            // First check if any smelter has output to collect (queued or on ground)
            foreach (var smelter in _nearbySmelters)
            {
                if (smelter == null) continue;
                var snview = smelter.GetComponent<ZNetView>();
                if (snview == null || snview.GetZDO() == null) continue;

                float fuel = snview.GetZDO().GetFloat(ZDOVars.s_fuel);
                int queued = snview.GetZDO().GetInt(ZDOVars.s_queued);
                int processed = snview.GetZDO().GetInt(ZDOVars.s_spawnAmount);
                bool isKiln = smelter.m_maxFuel == 0;
                int groundDrops = CountGroundDropsNearSmelter(smelter);
                Log($"  Smelter \"{smelter.m_name}\" type={( isKiln ? "kiln" : "furnace")} " +
                    $"fuel={fuel:F0}/{smelter.m_maxFuel} ore={queued}/{smelter.m_maxOre} " +
                    $"output={processed} groundDrops={groundDrops} active={smelter.IsActive()} " +
                    $"pos={smelter.transform.position:F1} " +
                    $"dist={Vector3.Distance(transform.position, smelter.transform.position):F1}m");

                // Check for queued output (m_spawnStack = true smelters)
                if (processed > 0)
                {
                    _targetSmelter = smelter;
                    ClearFollowForMovement();
                    ResetStuck();
                    _phase = SmeltPhase.CollectingOutput;
                    Log($"→ Collecting queued output from \"{smelter.m_name}\" ({processed} items)");
                    return;
                }

                // Check for ground drops near output (m_spawnStack = false smelters)
                if (groundDrops > 0)
                {
                    _targetSmelter = smelter;
                    ClearFollowForMovement();
                    ResetStuck();
                    _collectTriggered = true;    // skip RPC_EmptyProcessed, go straight to pickup
                    _actionTimer = 0f;           // no wait needed, items already on ground
                    _phase = SmeltPhase.CollectingOutput;
                    Log($"→ Collecting {groundDrops} ground drops near \"{smelter.m_name}\"");
                    return;
                }
            }

            // Check if any smelter needs fuel or input
            ScanNearbyChests();

            if (_nearbyChests.Count == 0)
            {
                Log("No chests found nearby");
                _phase = SmeltPhase.Idle;
                _scanTimer = 10f;
                return;
            }

            // Priority per smelter (kilns first, then furnaces):
            //   1. Kilns: refill input
            //   2. Furnaces: prioritize ore when fuel is adequate, fuel when it's low
            //
            // For furnaces we use per-smelter logic:
            //   - No ore queued → ore first (fuel without ore is useless)
            //   - Ore queued but fuel critically low → fuel
            //   - Ore queued and fuel okay but room for more ore → ore
            //   - Both near full → skip

            // Pass 1: Kilns that need input (m_maxFuel == 0)
            foreach (var smelter in _nearbySmelters)
            {
                if (smelter == null) continue;
                if (smelter.m_maxFuel > 0) continue; // skip furnaces in kiln pass

                if (TryPlanRefill(smelter)) return;
            }

            // Pass 2: Furnaces — smart per-smelter priority
            foreach (var smelter in _nearbySmelters)
            {
                if (smelter == null) continue;
                if (smelter.m_maxFuel == 0) continue; // skip kilns

                var snview = smelter.GetComponent<ZNetView>();
                if (snview == null || snview.GetZDO() == null) continue;

                float fuel = snview.GetZDO().GetFloat(ZDOVars.s_fuel);
                int queued = snview.GetZDO().GetInt(ZDOVars.s_queued);

                bool fuelLow = fuel < (float)smelter.m_fuelPerProduct;
                bool fuelFull = fuel > (float)(smelter.m_maxFuel - 1);
                bool oreEmpty = queued == 0;
                bool oreFull = queued >= smelter.m_maxOre;

                if (oreEmpty && !oreFull)
                {
                    // No ore — prioritize ore (fuel without ore is useless)
                    if (TryPlanRefill(smelter)) return;
                    // If no ore available in chests, try fuel as fallback
                    if (!fuelFull && TryPlanFuelRefill(smelter)) return;
                }
                else if (fuelLow && !fuelFull)
                {
                    // Has ore but fuel is critically low — refuel so it doesn't stall
                    if (TryPlanFuelRefill(smelter)) return;
                }
                else if (!oreFull)
                {
                    // Has ore, fuel is fine — top up ore to keep queue going
                    if (TryPlanRefill(smelter)) return;
                }
                else if (!fuelFull && fuel < (float)(smelter.m_maxFuel / 2))
                {
                    // Ore is full, fuel below half — top up fuel
                    if (TryPlanFuelRefill(smelter)) return;
                }
                // else: both full or fuel adequate — skip
            }

            // Nothing needs refilling — enter monitoring mode
            bool anySmelterActive = false;
            foreach (var smelter in _nearbySmelters)
            {
                if (smelter != null && smelter.IsActive())
                {
                    anySmelterActive = true;
                    break;
                }
            }

            if (anySmelterActive)
            {
                _phase = SmeltPhase.Monitoring;
                _monitorTimer = MonitorInterval;
                Log("All smelters stocked — monitoring");
                if (_talk != null) _talk.Say("Everything's running. I'll keep watch.");
            }
            else
            {
                Log("All smelters idle and fully stocked — done");
                if (_talk != null) _talk.Say("All done smelting.");
                Finish();
            }
        }

        // ── Moving to chest ────────────────────────────────────────────────

        private void UpdateMovingToChest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetChest == null)
            {
                Abort("chest destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetChest.transform.position);

            if (dist < UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_targetChest.transform.position);
                _chestOpened = false;
                _phase = SmeltPhase.TakingFromChest;
                Log($"Arrived at chest (dist={dist:F1}m) — taking {_carryingAmount}x {_carryingItemPrefab}");
                return;
            }

            bool moveOk = _ai.MoveToPoint(dt, _targetChest.transform.position, UseDistance, true);
            LogMovement(dt, "chest", dist, moveOk);
            UpdateStuckDetection(dt, "chest");
        }

        // ── Taking from chest ──────────────────────────────────────────────

        private void UpdateTakingFromChest()
        {
            if (_targetChest == null)
            {
                if (_chestOpened) _chestOpened = false;
                Abort("chest destroyed while taking");
                return;
            }

            // Step 1: Open the chest and wait for the animation to play
            if (!_chestOpened)
            {
                _targetChest.SetInUse(true);
                _chestOpened = true;
                _actionTimer = 0.8f;
                Log("Opened chest — waiting for animation");
                return;
            }

            // Step 2: Wait for animation
            _actionTimer -= Time.deltaTime;
            if (_actionTimer > 0f) return;

            // Step 3: Take items and close
            var chestInv = _targetChest.GetInventory();
            var companionInv = _humanoid?.GetInventory();
            if (chestInv == null || companionInv == null)
            {
                _targetChest.SetInUse(false);
                _chestOpened = false;
                Abort("inventory null");
                return;
            }

            int taken = 0;
            var allItems = chestInv.GetAllItems();

            Log($"Taking from chest: looking for \"{_carryingItemPrefab}\" in {allItems.Count} items");
            for (int i = allItems.Count - 1; i >= 0 && taken < _carryingAmount; i--)
            {
                var item = allItems[i];
                if (item == null) continue;
                if (item.m_dropPrefab == null)
                {
                    Log($"  Skip item \"{item.m_shared?.m_name ?? "?"}\" — m_dropPrefab is null");
                    continue;
                }
                if (item.m_dropPrefab.name != _carryingItemPrefab)
                    continue;

                int toTake = Mathf.Min(item.m_stack, _carryingAmount - taken);
                if (!companionInv.HaveEmptySlot() && companionInv.GetItem(item.m_shared.m_name) == null)
                {
                    Log($"  No inventory space — stopping at {taken} items");
                    break;
                }

                chestInv.RemoveItem(item, toTake);
                taken += toTake;
                Log($"  Took {toTake}x \"{_carryingItemPrefab}\" (total taken={taken})");
            }

            // Close chest (animation + sound)
            _targetChest.SetInUse(false);
            _chestOpened = false;

            if (taken == 0)
            {
                Log("Failed to take any items from chest — retrying scan");
                _phase = SmeltPhase.Scanning;
                return;
            }

            _carryingAmount = taken;
            float smelterDist = _targetSmelter != null
                ? Vector3.Distance(transform.position, _targetSmelter.transform.position)
                : -1f;
            Log($"Took {taken}x {_carryingItemPrefab} — heading to smelter " +
                $"\"{_targetSmelter?.m_name ?? "null"}\" (dist={smelterDist:F1}m)");

            // Now move to smelter
            ResetStuck();
            _phase = SmeltPhase.MovingToSmelter;
        }

        // ── Moving to smelter ──────────────────────────────────────────────

        private void UpdateMovingToSmelter(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetSmelter == null)
            {
                Abort("smelter destroyed");
                return;
            }

            Vector3 interactPos = GetSmelterInteractPoint(_targetSmelter, _carryingIsFuel);
            float dist = Vector3.Distance(transform.position, interactPos);

            bool closeEnough = dist < UseDistance;
            if (!closeEnough)
            {
                bool moveOk = _ai.MoveToPoint(dt, interactPos, UseDistance, true);
                // MoveTo returns true when pathfinding reached closest point or
                // XZ distance satisfied — accept if within a relaxed threshold
                // (handles Y-offset and NavMesh imprecision near smelter colliders)
                if (moveOk && dist < UseDistance + 1f)
                    closeEnough = true;
                else
                {
                    LogMovement(dt, "smelter", dist, moveOk);
                    UpdateStuckDetection(dt, "smelter");
                    return;
                }
            }

            _ai.StopMoving();
            _ai.LookAtPoint(interactPos);
            _insertTimer = InsertDelay;
            _phase = SmeltPhase.InsertingItem;
            Log($"Arrived at smelter interact point (dist={dist:F1}m) — inserting {_carryingAmount}x {_carryingItemPrefab}");
        }

        // ── Inserting item into smelter ────────────────────────────────────

        private void UpdateInsertingItem(float dt)
        {
            if (_targetSmelter == null)
            {
                Abort("smelter destroyed while inserting");
                return;
            }

            _insertTimer -= dt;
            if (_insertTimer > 0f) return;
            _insertTimer = InsertDelay;

            if (_carryingAmount <= 0)
            {
                Log("All items inserted — returning to scan");
                _phase = SmeltPhase.Scanning;
                return;
            }

            var snview = _targetSmelter.GetComponent<ZNetView>();
            if (snview == null || snview.GetZDO() == null)
            {
                Abort("smelter ZDO lost");
                return;
            }

            if (_carryingIsFuel)
            {
                float fuel = snview.GetZDO().GetFloat(ZDOVars.s_fuel);
                if (fuel > (float)(_targetSmelter.m_maxFuel - 1))
                {
                    Log("Smelter fuel is full");
                    _carryingAmount = 0; // discard remainder tracking
                    _phase = SmeltPhase.Scanning;
                    return;
                }
                snview.InvokeRPC("RPC_AddFuel");
                _targetSmelter.m_fuelAddedEffects.Create(
                    _targetSmelter.transform.position,
                    _targetSmelter.transform.rotation,
                    _targetSmelter.transform);
                ConsumeOneFromInventory(_carryingItemPrefab);
                _carryingAmount--;
                Log($"Inserted fuel — {_carryingAmount} remaining");
            }
            else
            {
                int queued = snview.GetZDO().GetInt(ZDOVars.s_queued);
                if (queued >= _targetSmelter.m_maxOre)
                {
                    Log("Smelter ore queue is full");
                    _carryingAmount = 0;
                    _phase = SmeltPhase.Scanning;
                    return;
                }
                snview.InvokeRPC("RPC_AddOre", _carryingItemPrefab);
                _targetSmelter.m_oreAddedEffects.Create(
                    _targetSmelter.transform.position,
                    _targetSmelter.transform.rotation);
                // Trigger the feeding animation (m_addedOreTime is private)
                s_addedOreTime?.SetValue(_targetSmelter, Time.time);
                ConsumeOneFromInventory(_carryingItemPrefab);
                _carryingAmount--;
                Log($"Inserted ore {_carryingItemPrefab} — {_carryingAmount} remaining");
            }
        }

        // ── Monitoring — periodic check ────────────────────────────────────

        private void UpdateMonitoring(float dt)
        {
            _monitorTimer -= dt;
            if (_monitorTimer > 0f) return;
            _monitorTimer = MonitorInterval;

            // Re-scan to check for output or empty smelters
            _phase = SmeltPhase.Scanning;
        }

        // ── Collecting output ──────────────────────────────────────────────

        private bool _collectTriggered;

        private void UpdateCollectingOutput()
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetSmelter == null)
            {
                _collectTriggered = false;
                _phase = SmeltPhase.Scanning;
                return;
            }

            Vector3 outputPos = GetSmelterOutputPoint(_targetSmelter);
            float dist = Vector3.Distance(transform.position, outputPos);

            // Walk to smelter output side if not close enough
            if (dist > UseDistance)
            {
                bool moveOk = _ai.MoveToPoint(Time.deltaTime, outputPos, UseDistance, true);
                // Accept pathfinding "arrived" if within relaxed threshold
                if (moveOk && dist < UseDistance + 1f)
                {
                    _ai.StopMoving();
                }
                else
                {
                    LogMovement(Time.deltaTime, "smelter (collect)", dist, moveOk);
                    UpdateStuckDetection(Time.deltaTime, "smelter (collect)");
                    return;
                }
            }

            // At the smelter — trigger output spawn if not yet done
            if (!_collectTriggered)
            {
                var snview = _targetSmelter.GetComponent<ZNetView>();
                if (snview == null || snview.GetZDO() == null)
                {
                    _collectTriggered = false;
                    _phase = SmeltPhase.Scanning;
                    return;
                }

                int processed = snview.GetZDO().GetInt(ZDOVars.s_spawnAmount);
                if (processed <= 0)
                {
                    _collectTriggered = false;
                    _phase = SmeltPhase.Scanning;
                    return;
                }

                _ai.StopMoving();
                _ai.LookAtPoint(outputPos);
                snview.InvokeRPC("RPC_EmptyProcessed");
                Log($"Triggered output spawn ({processed} items) from \"{_targetSmelter.m_name}\"");
                _collectTriggered = true;
                _actionTimer = 1.5f; // wait for items to spawn
                return;
            }

            // Wait for items to spawn
            _actionTimer -= Time.deltaTime;
            if (_actionTimer > 0f) return;

            // Pick up dropped items near smelter output
            PickUpNearbyDrops();
            _collectTriggered = false;

            // Try to store output in a chest
            ScanNearbyChests();
            _outputChest = FindChestWithSpace();

            if (_outputChest != null)
            {
                ResetStuck();
                _phase = SmeltPhase.MovingToOutputChest;
                Log($"Will store output in chest at {_outputChest.transform.position:F1}");
            }
            else
            {
                Log("No chest with space for output — items left in inventory");
                _phase = SmeltPhase.Scanning;
            }
        }

        // ── Moving to output chest ─────────────────────────────────────────

        private void UpdateMovingToOutputChest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_outputChest == null)
            {
                _phase = SmeltPhase.Scanning;
                return;
            }

            float dist = Vector3.Distance(transform.position, _outputChest.transform.position);
            if (dist < UseDistance)
            {
                _ai.StopMoving();
                _chestOpened = false;
                _phase = SmeltPhase.StoringOutput;
                Log($"Arrived at output chest (dist={dist:F1}m) — storing output");
                return;
            }

            bool moveOk = _ai.MoveToPoint(dt, _outputChest.transform.position, UseDistance, true);
            LogMovement(dt, "output chest", dist, moveOk);
            UpdateStuckDetection(dt, "output chest");
        }

        // ── Storing output ─────────────────────────────────────────────────

        private void UpdateStoringOutput()
        {
            if (_outputChest == null)
            {
                if (_chestOpened) _chestOpened = false;
                Log("Output chest gone — back to scanning");
                _phase = SmeltPhase.Scanning;
                return;
            }

            // Step 1: Open chest and wait for animation
            if (!_chestOpened)
            {
                _outputChest.SetInUse(true);
                _chestOpened = true;
                _actionTimer = 0.8f;
                Log("Opened output chest — waiting for animation");
                return;
            }

            // Step 2: Wait for animation
            _actionTimer -= Time.deltaTime;
            if (_actionTimer > 0f) return;

            // Step 3: Transfer items and close
            var companionInv = _humanoid?.GetInventory();
            var chestInv = _outputChest.GetInventory();
            if (companionInv == null || chestInv == null)
            {
                _outputChest.SetInUse(false);
                _chestOpened = false;
                _phase = SmeltPhase.Scanning;
                return;
            }

            int stored = 0;
            var allItems = companionInv.GetAllItems();
            for (int i = allItems.Count - 1; i >= 0; i--)
            {
                var item = allItems[i];
                if (item == null) continue;

                // Only store material/misc items, not equipped gear
                if (!IsSmeltOutput(item)) continue;

                if (chestInv.CanAddItem(item, item.m_stack))
                {
                    Log($"  Storing \"{item.m_shared?.m_name ?? "?"}\" x{item.m_stack}");
                    var clone = item.Clone();
                    clone.m_stack = item.m_stack;
                    chestInv.AddItem(clone);
                    companionInv.RemoveItem(item);
                    stored++;
                }
            }

            // Close chest (animation + sound)
            _outputChest.SetInUse(false);
            _chestOpened = false;

            Log($"Stored {stored} item stacks in chest — back to scanning");
            _phase = SmeltPhase.Scanning;
        }

        // ── Public API ─────────────────────────────────────────────────────

        /// <summary>Called when action mode changes away from smelt.</summary>
        public void NotifyActionModeChanged()
        {
            if (_phase != SmeltPhase.Idle)
            {
                Log("NotifyActionModeChanged — aborting smelt");
                Abort("mode changed");
            }
        }

        /// <summary>Cancel active smelting (called by CancelExistingActions).</summary>
        public void CancelDirected()
        {
            if (_phase == SmeltPhase.Idle) return;
            Abort("cancelled by new command");
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private bool IsSmeltMode()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return false;
            return zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                   == CompanionSetup.ModeSmelt;
        }

        private void ScanNearbySmelters()
        {
            _nearbySmelters.Clear();
            _tempPieces.Clear();
            Piece.GetAllPiecesInRadius(transform.position, ScanRadius, _tempPieces);

            foreach (var piece in _tempPieces)
            {
                if (piece == null) continue;
                var smelter = piece.GetComponent<Smelter>();
                if (smelter == null) continue;

                var snview = smelter.GetComponent<ZNetView>();
                if (snview == null || snview.GetZDO() == null) continue;

                _nearbySmelters.Add(smelter);
            }

            Log($"Scan: found {_nearbySmelters.Count} smelters");
        }

        private void ScanNearbyChests()
        {
            _nearbyChests.Clear();
            _tempPieces.Clear();
            Piece.GetAllPiecesInRadius(transform.position, ScanRadius, _tempPieces);

            foreach (var piece in _tempPieces)
            {
                if (piece == null) continue;
                var container = piece.GetComponentInChildren<Container>();
                if (container == null) continue;
                if (container.GetInventory() == null) continue;

                // Skip companion's own container (if any)
                if (container.gameObject == gameObject) continue;
                // Skip containers that are in use by players
                if (container.IsInUse()) continue;

                _nearbyChests.Add(container);
            }

            Log($"Scan: found {_nearbyChests.Count} chests within {ScanRadius}m");
            foreach (var chest in _nearbyChests)
            {
                var inv = chest.GetInventory();
                float d = Vector3.Distance(transform.position, chest.transform.position);
                Log($"  Chest \"{chest.m_name}\" items={inv?.GetAllItems().Count ?? 0} " +
                    $"empty={inv?.GetEmptySlots() ?? 0} dist={d:F1}m " +
                    $"pos={chest.transform.position:F1}");
            }
        }

        /// <summary>
        /// Try to plan a fuel refill for a furnace-type smelter (m_maxFuel > 0).
        /// Returns true if a refill task was set up.
        /// </summary>
        private bool TryPlanFuelRefill(Smelter smelter)
        {
            if (smelter.m_fuelItem == null || smelter.m_maxFuel == 0) return false;

            var snview = smelter.GetComponent<ZNetView>();
            if (snview == null || snview.GetZDO() == null) return false;

            float fuel = snview.GetZDO().GetFloat(ZDOVars.s_fuel);
            if (fuel > (float)(smelter.m_maxFuel - 1)) return false;

            int needed = smelter.m_maxFuel - (int)fuel;
            string fuelPrefab = smelter.m_fuelItem.gameObject.name;
            string fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;

            // Check if companion already has fuel in inventory — skip chest trip
            var companionInv = _humanoid?.GetInventory();
            if (companionInv != null)
            {
                int have = CountItemByPrefab(companionInv, fuelPrefab);
                if (have > 0)
                {
                    int toUse = Mathf.Min(needed, have, MaxCarryFuel);
                    _targetSmelter = smelter;
                    _targetChest = null;
                    _carryingItemPrefab = fuelPrefab;
                    _carryingIsFuel = true;
                    _carryingAmount = toUse;
                    ClearFollowForMovement();
                    ResetStuck();
                    _phase = SmeltPhase.MovingToSmelter;
                    Log($"Plan: use {toUse}x fuel \"{fuelName}\" from inventory → " +
                        $"\"{smelter.m_name}\" (fuel={fuel:F0}/{smelter.m_maxFuel})");
                    return true;
                }
            }

            // Find a chest with this fuel
            Container bestChest = null;
            float bestDist = float.MaxValue;
            int bestCount = 0;

            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;

                int count = CountItemByPrefab(inv, fuelPrefab);
                if (count <= 0) continue;

                float dist = Vector3.Distance(transform.position, chest.transform.position);
                if (dist < bestDist)
                {
                    bestChest = chest;
                    bestDist = dist;
                    bestCount = count;
                }
            }

            if (bestChest == null) return false;

            int toTake = Mathf.Min(needed, bestCount, MaxCarryFuel);
            _targetSmelter = smelter;
            _targetChest = bestChest;
            _carryingItemPrefab = fuelPrefab;
            _carryingIsFuel = true;
            _carryingAmount = toTake;
            ClearFollowForMovement();
            ResetStuck();
            _phase = SmeltPhase.MovingToChest;

            Log($"Plan: take {toTake}x fuel \"{fuelName}\" from chest " +
                $"(dist={bestDist:F1}m) → \"{smelter.m_name}\" (fuel={fuel:F0}/{smelter.m_maxFuel})");
            if (_talk != null) _talk.Say("Fetching fuel.");
            return true;
        }

        /// <summary>
        /// Try to plan an ore/input refill for a smelter.
        /// Returns true if a refill task was set up.
        /// </summary>
        private bool TryPlanRefill(Smelter smelter)
        {
            var snview = smelter.GetComponent<ZNetView>();
            if (snview == null || snview.GetZDO() == null) return false;

            int queued = snview.GetZDO().GetInt(ZDOVars.s_queued);
            if (queued >= smelter.m_maxOre) return false;

            int needed = smelter.m_maxOre - queued;

            // Check if companion already has a valid ore/input in inventory — skip chest trip
            var companionInv = _humanoid?.GetInventory();
            if (companionInv != null)
            {
                foreach (var conversion in smelter.m_conversion)
                {
                    if (conversion?.m_from == null) continue;
                    string inputPrefab = conversion.m_from.gameObject.name;
                    int have = CountItemByPrefab(companionInv, inputPrefab);
                    if (have > 0)
                    {
                        int toUse = Mathf.Min(needed, have, MaxCarryOre);
                        _targetSmelter = smelter;
                        _targetChest = null;
                        _carryingItemPrefab = inputPrefab;
                        _carryingIsFuel = false;
                        _carryingAmount = toUse;
                        ClearFollowForMovement();
                        ResetStuck();
                        _phase = SmeltPhase.MovingToSmelter;
                        Log($"Plan: use {toUse}x ore \"{inputPrefab}\" from inventory → " +
                            $"\"{smelter.m_name}\" (queued={queued}/{smelter.m_maxOre})");
                        return true;
                    }
                }
            }

            // Find a chest with any valid conversion input
            Container bestChest = null;
            float bestDist = float.MaxValue;
            string bestPrefab = null;
            int bestCount = 0;

            foreach (var conversion in smelter.m_conversion)
            {
                if (conversion?.m_from == null) continue;
                string inputPrefab = conversion.m_from.gameObject.name;

                foreach (var chest in _nearbyChests)
                {
                    if (chest == null) continue;
                    var inv = chest.GetInventory();
                    if (inv == null) continue;

                    int count = CountItemByPrefab(inv, inputPrefab);
                    if (count <= 0) continue;

                    float dist = Vector3.Distance(transform.position, chest.transform.position);
                    if (dist < bestDist)
                    {
                        bestChest = chest;
                        bestDist = dist;
                        bestPrefab = inputPrefab;
                        bestCount = count;
                    }
                }
            }

            if (bestChest == null || bestPrefab == null) return false;

            int toTake = Mathf.Min(needed, bestCount, MaxCarryOre);
            _targetSmelter = smelter;
            _targetChest = bestChest;
            _carryingItemPrefab = bestPrefab;
            _carryingIsFuel = false;
            _carryingAmount = toTake;
            ClearFollowForMovement();
            ResetStuck();
            _phase = SmeltPhase.MovingToChest;

            Log($"Plan: take {toTake}x ore \"{bestPrefab}\" from chest " +
                $"(dist={bestDist:F1}m) → \"{smelter.m_name}\" (queued={queued}/{smelter.m_maxOre})");
            if (_talk != null) _talk.Say("Fetching materials.");
            return true;
        }

        private Container FindChestWithSpace()
        {
            Container best = null;
            float bestDist = float.MaxValue;

            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;
                if (inv.GetEmptySlots() <= 0) continue;

                float dist = Vector3.Distance(transform.position, chest.transform.position);
                if (dist < bestDist)
                {
                    best = chest;
                    bestDist = dist;
                }
            }

            return best;
        }

        private void PickUpNearbyDrops()
        {
            if (_targetSmelter == null) return;

            // Scan for ItemDrop components near the smelter's output point
            Vector3 outputPos = _targetSmelter.m_outputPoint != null
                ? _targetSmelter.m_outputPoint.position
                : _targetSmelter.transform.position;

            var colliders = Physics.OverlapSphere(outputPos, 3f, LayerMask.GetMask("item"));
            var companionInv = _humanoid?.GetInventory();
            if (companionInv == null) return;

            foreach (var col in colliders)
            {
                var itemDrop = col.GetComponentInParent<ItemDrop>();
                if (itemDrop == null || itemDrop.m_itemData == null) continue;

                var nview = itemDrop.GetComponent<ZNetView>();
                if (nview == null || nview.GetZDO() == null) continue;

                if (companionInv.CanAddItem(itemDrop.m_itemData, itemDrop.m_itemData.m_stack))
                {
                    companionInv.AddItem(itemDrop.m_itemData);
                    nview.ClaimOwnership();
                    nview.Destroy();
                    Log($"Picked up {itemDrop.m_itemData.m_stack}x \"{itemDrop.m_itemData.m_shared.m_name}\"");
                }
            }
        }

        /// <summary>
        /// Get the position the AI should walk to when inserting fuel or ore.
        /// Uses the switch position but offsets outward from the smelter center so the
        /// AI navigates to reachable ground on the correct side (switches are often
        /// embedded in the smelter mesh and can't be pathed to directly).
        /// </summary>
        private static Vector3 GetSmelterInteractPoint(Smelter smelter, bool isFuel)
        {
            Transform switchT = isFuel ? smelter.m_addWoodSwitch?.transform
                                       : smelter.m_addOreSwitch?.transform;
            if (switchT == null) return smelter.transform.position;
            return OffsetFromCenter(smelter.transform.position, switchT.position, 1.3f);
        }

        /// <summary>
        /// Get the position the AI should walk to when collecting output.
        /// Prefers m_emptyOreSwitch (the tap/output interaction), then m_outputPoint, then center.
        /// Offsets outward so the AI stands on reachable ground.
        /// </summary>
        private static Vector3 GetSmelterOutputPoint(Smelter smelter)
        {
            Vector3 center = smelter.transform.position;
            if (smelter.m_emptyOreSwitch != null)
                return OffsetFromCenter(center, smelter.m_emptyOreSwitch.transform.position, 1.3f);
            if (smelter.m_outputPoint != null)
                return OffsetFromCenter(center, smelter.m_outputPoint.position, 1.3f);
            return center;
        }

        /// <summary>
        /// Push a target position outward from a center point so the AI doesn't try to
        /// walk into the structure. Returns a point <paramref name="offset"/> meters
        /// beyond <paramref name="target"/> along the center→target direction (Y ignored).
        /// </summary>
        private static Vector3 OffsetFromCenter(Vector3 center, Vector3 target, float offset)
        {
            Vector3 dir = target - center;
            dir.y = 0f; // keep horizontal only
            if (dir.sqrMagnitude < 0.01f) return target; // switch is at center, can't offset
            return target + dir.normalized * offset;
        }

        /// <summary>Count physical ItemDrop objects on the ground near a smelter's output point.</summary>
        private static int CountGroundDropsNearSmelter(Smelter smelter)
        {
            Vector3 outputPos = smelter.m_outputPoint != null
                ? smelter.m_outputPoint.position
                : smelter.transform.position;

            var colliders = Physics.OverlapSphere(outputPos, 3f, LayerMask.GetMask("item"));
            int count = 0;
            foreach (var col in colliders)
            {
                var itemDrop = col.GetComponentInParent<ItemDrop>();
                if (itemDrop == null || itemDrop.m_itemData == null) continue;
                if (itemDrop.m_itemData.m_shared == null) continue;
                // Only count material items (bars, coal, etc.)
                if (itemDrop.m_itemData.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Material)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// Remove one item matching the prefab name from the companion's inventory.
        /// Used when inserting materials that came from the companion's own inventory
        /// (no-op when items were sourced from a chest, since they were already removed there).
        /// </summary>
        private void ConsumeOneFromInventory(string prefabName)
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return;
            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_dropPrefab != null && item.m_dropPrefab.name == prefabName)
                {
                    inv.RemoveItem(item, 1);
                    return;
                }
            }
        }

        /// <summary>Count items in an inventory by prefab name (gameObject.name).</summary>
        private static int CountItemByPrefab(Inventory inv, string prefabName)
        {
            int count = 0;
            var items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item?.m_dropPrefab != null && item.m_dropPrefab.name == prefabName)
                    count += item.m_stack;
            }
            return count;
        }

        /// <summary>Check if an item is smelted output (bar/ingot/material, not gear).</summary>
        private static bool IsSmeltOutput(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            var type = item.m_shared.m_itemType;
            return type == ItemDrop.ItemData.ItemType.Material;
        }

        private bool ShouldAbort()
        {
            // UI open — pause
            if (CompanionInteractPanel.IsOpenFor(_setup) || CompanionRadialMenu.IsOpenFor(_setup))
                return true;

            // Active combat
            var combat = GetComponent<CombatController>();
            if (combat != null && combat.Phase != CombatController.CombatPhase.Idle)
                return true;

            return false;
        }

        private void Finish()
        {
            Log("Finish — restoring follow target");
            CloseAnyOpenChest();
            _targetSmelter = null;
            _targetChest = null;
            _outputChest = null;
            _carryingItemPrefab = null;
            _carryingAmount = 0;
            _collectTriggered = false;
            _phase = SmeltPhase.Idle;
            _scanTimer = ScanInterval;
            RestoreFollow();
        }

        private void Abort(string reason)
        {
            Log($"Aborted — {reason} (phase was {_phase})");
            CloseAnyOpenChest();
            _targetSmelter = null;
            _targetChest = null;
            _outputChest = null;
            _carryingItemPrefab = null;
            _carryingAmount = 0;
            _collectTriggered = false;
            _phase = SmeltPhase.Idle;
            _scanTimer = ScanInterval;
            RestoreFollow();
        }

        private void CloseAnyOpenChest()
        {
            if (!_chestOpened) return;
            // Close whichever chest is currently open
            if (_phase == SmeltPhase.StoringOutput && _outputChest != null)
                _outputChest.SetInUse(false);
            else if (_targetChest != null)
                _targetChest.SetInUse(false);
            _chestOpened = false;
        }

        private void ResetStuck()
        {
            _stuckTimer = 0f;
            _stuckCheckTimer = 0f;
            _stuckCheckPos = transform.position;
        }

        private void UpdateStuckDetection(float dt, string targetName)
        {
            _stuckCheckTimer += dt;
            if (_stuckCheckTimer >= StuckCheckPeriod)
            {
                float moved = Vector3.Distance(transform.position, _stuckCheckPos);
                if (moved < StuckMinDistance)
                    _stuckTimer += _stuckCheckTimer;
                else
                    _stuckTimer = 0f;
                _stuckCheckPos = transform.position;
                _stuckCheckTimer = 0f;
            }

            if (_stuckTimer > MoveTimeout)
            {
                Abort($"stuck moving to {targetName}");
            }
        }

        /// <summary>
        /// Clear follow target so SmeltController owns movement exclusively.
        /// CompanionAI.UpdateAI skips Follow()/IdleMovement() when smelt IsActive.
        /// </summary>
        private void ClearFollowForMovement()
        {
            if (_ai != null)
            {
                _ai.SetFollowTarget(null);
                Log("Cleared follow target — SmeltController driving movement");
            }
        }

        /// <summary>
        /// Restore follow target based on Follow toggle and StayHome state.
        /// Called on Finish/Abort when SmeltController releases movement control.
        /// </summary>
        private void RestoreFollow()
        {
            if (_ai == null) return;
            bool follow = _setup != null && _setup.GetFollow();
            bool stayHome = _setup != null && _setup.GetStayHome() && _setup.HasHomePosition();
            if (follow && Player.m_localPlayer != null)
            {
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                Log("Restored follow target to player (Follow=ON)");
            }
            else if (stayHome)
            {
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPointAt(_setup.GetHomePosition());
                Log("Restored patrol to home (StayHome ON, Follow OFF)");
            }
            else
            {
                Log("Follow OFF, no StayHome — idle");
            }
        }

        /// <summary>Throttled movement log — fires every 1s to avoid spam.</summary>
        private void LogMovement(float dt, string target, float dist, bool moveOk)
        {
            _moveLogTimer -= dt;
            if (_moveLogTimer > 0f) return;
            _moveLogTimer = 1f;

            var vel = _character?.GetVelocity() ?? Vector3.zero;
            var followTarget = _ai?.GetFollowTarget();
            Log($"Moving → {target} dist={dist:F1}m moveOk={moveOk} " +
                $"vel={vel.magnitude:F1} follow=\"{followTarget?.name ?? "null"}\" " +
                $"pos={transform.position:F1}");
        }

        private void Log(string msg)
        {
            string name = _character?.m_name ?? "?";
            CompanionsPlugin.Log.LogInfo($"[Smelt|{name}] {msg}");
        }
    }
}
