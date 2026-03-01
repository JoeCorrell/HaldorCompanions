using System.Collections.Generic;
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
        private const int   MaxCarryAmount     = 10;   // take up to 10 items per trip

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
                Log("No smelters found nearby");
                _phase = SmeltPhase.Idle;
                _scanTimer = 10f; // back off
                return;
            }

            // First check if any smelter has output to collect
            foreach (var smelter in _nearbySmelters)
            {
                if (smelter == null) continue;
                var snview = smelter.GetComponent<ZNetView>();
                if (snview == null || snview.GetZDO() == null) continue;

                int processed = snview.GetZDO().GetInt(ZDOVars.s_spawnAmount);
                if (processed > 0)
                {
                    _targetSmelter = smelter;
                    _phase = SmeltPhase.CollectingOutput;
                    Log($"Smelter \"{smelter.m_name}\" has {processed} output ready — collecting");
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

            // Priority: kilns first (no fuel needed, just input), then furnaces
            // Kilns: m_maxFuel == 0 (they don't use fuel)
            // Furnaces: m_maxFuel > 0 (they need fuel + ore)

            // Pass 1: Kilns that need input
            foreach (var smelter in _nearbySmelters)
            {
                if (smelter == null) continue;
                if (smelter.m_maxFuel > 0) continue; // skip furnaces in kiln pass

                if (TryPlanRefill(smelter)) return;
            }

            // Pass 2: Furnaces that need fuel
            foreach (var smelter in _nearbySmelters)
            {
                if (smelter == null) continue;
                if (smelter.m_maxFuel == 0) continue; // skip kilns

                if (TryPlanFuelRefill(smelter)) return;
            }

            // Pass 3: Furnaces that need ore/input
            foreach (var smelter in _nearbySmelters)
            {
                if (smelter == null) continue;
                if (smelter.m_maxFuel == 0) continue; // skip kilns

                if (TryPlanRefill(smelter)) return;
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
                _phase = SmeltPhase.TakingFromChest;
                Log($"Arrived at chest — taking {_carryingAmount}x {_carryingItemPrefab}");
                return;
            }

            _ai.MoveToPoint(dt, _targetChest.transform.position, UseDistance, true);
            UpdateStuckDetection(dt, "chest");
        }

        // ── Taking from chest ──────────────────────────────────────────────

        private void UpdateTakingFromChest()
        {
            if (_targetChest == null)
            {
                Abort("chest destroyed while taking");
                return;
            }

            var chestInv = _targetChest.GetInventory();
            var companionInv = _humanoid?.GetInventory();
            if (chestInv == null || companionInv == null)
            {
                Abort("inventory null");
                return;
            }

            // Find the item in chest by prefab name
            int taken = 0;
            var allItems = chestInv.GetAllItems();

            for (int i = allItems.Count - 1; i >= 0 && taken < _carryingAmount; i--)
            {
                var item = allItems[i];
                if (item == null || item.m_dropPrefab == null) continue;
                if (item.m_dropPrefab.name != _carryingItemPrefab) continue;

                int toTake = Mathf.Min(item.m_stack, _carryingAmount - taken);
                // Try to add to companion inventory first
                if (!companionInv.HaveEmptySlot() && companionInv.GetItem(item.m_shared.m_name) == null)
                    break; // no space

                chestInv.RemoveItem(item, toTake);
                // We don't actually need to add to companion inventory — we track via _carryingAmount
                // and insert directly into smelter via RPC
                taken += toTake;
            }

            if (taken == 0)
            {
                Log("Failed to take items from chest — retrying scan");
                _phase = SmeltPhase.Scanning;
                return;
            }

            _carryingAmount = taken;
            Log($"Took {taken}x {_carryingItemPrefab} from chest — heading to smelter");

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

            float dist = Vector3.Distance(transform.position, _targetSmelter.transform.position);

            if (dist < UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_targetSmelter.transform.position);
                _insertTimer = InsertDelay;
                _phase = SmeltPhase.InsertingItem;
                Log($"Arrived at smelter — inserting {_carryingAmount}x {_carryingItemPrefab}");
                return;
            }

            _ai.MoveToPoint(dt, _targetSmelter.transform.position, UseDistance, true);
            UpdateStuckDetection(dt, "smelter");
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

            float dist = Vector3.Distance(transform.position, _targetSmelter.transform.position);

            // Walk to smelter if not close enough
            if (dist > UseDistance)
            {
                _ai.MoveToPoint(Time.deltaTime, _targetSmelter.transform.position, UseDistance, true);
                UpdateStuckDetection(Time.deltaTime, "smelter (collect)");
                return;
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
                _ai.LookAtPoint(_targetSmelter.transform.position);
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
                _phase = SmeltPhase.StoringOutput;
                return;
            }

            _ai.MoveToPoint(dt, _outputChest.transform.position, UseDistance, true);
            UpdateStuckDetection(dt, "output chest");
        }

        // ── Storing output ─────────────────────────────────────────────────

        private void UpdateStoringOutput()
        {
            if (_outputChest == null)
            {
                _phase = SmeltPhase.Scanning;
                return;
            }

            var companionInv = _humanoid?.GetInventory();
            var chestInv = _outputChest.GetInventory();
            if (companionInv == null || chestInv == null)
            {
                _phase = SmeltPhase.Scanning;
                return;
            }

            // Move smelted materials from companion inventory to chest
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
                    var clone = item.Clone();
                    clone.m_stack = item.m_stack;
                    chestInv.AddItem(clone);
                    companionInv.RemoveItem(item);
                    stored++;
                }
            }

            if (stored > 0)
                Log($"Stored {stored} item stacks in chest");

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

            Log($"Scan: found {_nearbyChests.Count} chests");
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

            int toTake = Mathf.Min(needed, bestCount, MaxCarryAmount);
            _targetSmelter = smelter;
            _targetChest = bestChest;
            _carryingItemPrefab = fuelPrefab;
            _carryingIsFuel = true;
            _carryingAmount = toTake;
            ResetStuck();
            _phase = SmeltPhase.MovingToChest;

            Log($"Plan: take {toTake}x fuel \"{fuelName}\" from chest → " +
                $"\"{smelter.m_name}\" (fuel={fuel:F0}/{smelter.m_maxFuel})");
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

            int toTake = Mathf.Min(needed, bestCount, MaxCarryAmount);
            _targetSmelter = smelter;
            _targetChest = bestChest;
            _carryingItemPrefab = bestPrefab;
            _carryingIsFuel = false;
            _carryingAmount = toTake;
            ResetStuck();
            _phase = SmeltPhase.MovingToChest;

            Log($"Plan: take {toTake}x ore \"{bestPrefab}\" from chest → " +
                $"\"{smelter.m_name}\" (queued={queued}/{smelter.m_maxOre})");
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
            _targetSmelter = null;
            _targetChest = null;
            _outputChest = null;
            _carryingItemPrefab = null;
            _carryingAmount = 0;
            _collectTriggered = false;
            _phase = SmeltPhase.Idle;
            _scanTimer = ScanInterval;
        }

        private void Abort(string reason)
        {
            Log($"Aborted — {reason} (phase was {_phase})");
            _targetSmelter = null;
            _targetChest = null;
            _outputChest = null;
            _carryingItemPrefab = null;
            _carryingAmount = 0;
            _collectTriggered = false;
            _phase = SmeltPhase.Idle;
            _scanTimer = ScanInterval;
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

        private void Log(string msg)
        {
            string name = _character?.m_name ?? "?";
            CompanionsPlugin.Log.LogDebug($"[Smelt|{name}] {msg}");
        }
    }
}
