using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Autonomous base maintenance controller for companions in StayHome mode.
    /// When Follow is OFF and StayHome is ON, scans for maintenance tasks:
    ///   1. Refuel fires/torches (fetch fuel from chests, add via RPC)
    ///   2. Repair damaged building pieces (WearNTear.Repair)
    ///   3. Consolidate split item stacks across chests
    ///
    /// All chest interactions are slow with open/close animations and sounds.
    /// State machine follows SmeltController/RepairController patterns.
    /// </summary>
    public class HomesteadController : MonoBehaviour
    {
        internal enum HomesteadPhase
        {
            Idle,
            MovingToSupplyChest,
            TakingFromChest,
            MovingToFireplace,
            Refueling,
            MovingToRepairTarget,
            Repairing,
            MovingToSortSource,
            TakingSortItems,
            MovingToSortDest,
            DepositingSortItems
        }

        internal HomesteadPhase Phase => _phase;
        public bool IsActive => _phase != HomesteadPhase.Idle;

        // ── Components ──────────────────────────────────────────────────────
        private CompanionAI       _ai;
        private Humanoid          _humanoid;
        private Character         _character;
        private CompanionSetup    _setup;
        private ZNetView          _nview;
        private ZSyncAnimation    _zanim;
        private CompanionTalk     _talk;
        private CombatController  _combat;
        private HarvestController _harvest;
        private RepairController  _repair;
        private SmeltController   _smelt;
        private CompanionRest     _rest;
        private DoorHandler       _doorHandler;

        // ── State ───────────────────────────────────────────────────────────
        private HomesteadPhase _phase;
        private float _scanTimer;
        private float _actionTimer;
        private float _stuckTimer;
        private float _stuckCheckTimer;
        private Vector3 _stuckCheckPos;
        private bool _chestOpened;
        private bool _lastScanEmpty;
        private bool _lastHammerMissing;  // throttle "no hammer" log
        private float _moveLogTimer;      // throttle movement logs
        private float _diagTimer;         // throttle diagnostic logs

        // ── Rotation cycling ──────────────────────────────────────────────────
        private enum TaskSlot { Repair, Refuel, Sort, Smelt }
        private TaskSlot _currentSlot;
        private float _slotTimer;              // counts down within current slot
        internal bool IsSmeltTurn => _currentSlot == TaskSlot.Smelt;  // read by SmeltController

        // ── Refuel task ─────────────────────────────────────────────────────
        private Fireplace _targetFireplace;
        private Container _supplyChest;
        private string _fuelItemPrefab;  // gameObject.name for inventory lookup
        private string _fuelItemName;    // m_shared.m_name for display
        private int _fuelToAdd;

        // ── Repair task ─────────────────────────────────────────────────────
        private WearNTear _targetPiece;
        private ItemDrop.ItemData _prevRightItem;  // weapon to re-equip after hammer
        private bool _hammerEquipped;

        // ── Sort task ───────────────────────────────────────────────────────
        private Container _sourceChest;
        private Container _destChest;
        private string _sortItemPrefab;
        private readonly List<ItemDrop.ItemData> _transferQueue = new List<ItemDrop.ItemData>();

        // ── Scan buffers (reused, no alloc) ─────────────────────────────────
        private readonly List<Piece> _tempPieces = new List<Piece>();
        private readonly List<Container> _nearbyChests = new List<Container>();

        // ── Constants ───────────────────────────────────────────────────────
        private const float ScanInterval     = 5f;
        private const float ScanBackoff      = 30f;
        private const float ScanRadius       = 40f;
        private const float UseDistance      = 2.0f;
        private const float FuelAddDelay     = 0.8f;
        private const float RepairDelay      = 1.2f;
        private const float ItemTransferDelay = 0.6f;
        private const float ChestOpenDelay   = 0.8f;
        private const float FuelThreshold    = 0.5f;  // refuel below 50% capacity
        private const float MoveTimeout      = 15f;
        private const float StuckCheckPeriod = 1f;
        private const float StuckMinDistance = 0.5f;
        private const float TaskSlotTime    = 15f;  // seconds per homestead task (repair/refuel/sort)
        private const float SmeltSlotTime   = 15f;  // seconds for smelting turn

        // ── Lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            _ai        = GetComponent<CompanionAI>();
            _humanoid  = GetComponent<Humanoid>();
            _character = GetComponent<Character>();
            _setup     = GetComponent<CompanionSetup>();
            _nview     = GetComponent<ZNetView>();
            _zanim     = GetComponent<ZSyncAnimation>();
            _talk      = GetComponent<CompanionTalk>();
            _combat    = GetComponent<CombatController>();
            _harvest   = GetComponent<HarvestController>();
            _repair    = GetComponent<RepairController>();
            _smelt     = GetComponent<SmeltController>();
            _rest      = GetComponent<CompanionRest>();
            _doorHandler = GetComponent<DoorHandler>();
            Log("Awake OK");
        }

        private void Update()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

            // Only active in homestead mode (StayHome ON, Follow OFF)
            if (!IsHomesteadMode())
            {
                if (_phase != HomesteadPhase.Idle)
                    Abort("left homestead mode");
                _currentSlot = TaskSlot.Repair;
                _slotTimer = TaskSlotTime;

                // Diagnostic: log why homestead mode is inactive
                _diagTimer -= Time.deltaTime;
                if (_diagTimer <= 0f)
                {
                    _diagTimer = 10f;
                    bool stayHome = _setup != null && _setup.GetStayHome();
                    bool hasHome = _setup != null && _setup.HasHomePosition();
                    bool follow = _setup != null && _setup.GetFollow();
                    Log($"INACTIVE — stayHome={stayHome} hasHome={hasHome} follow={follow}");
                }
                return;
            }

            if (ShouldAbort())
            {
                if (_phase != HomesteadPhase.Idle)
                {
                    string reason = "unknown";
                    if (_combat != null && _combat.Phase != CombatController.CombatPhase.Idle) reason = $"combat(phase={_combat.Phase})";
                    else if (_harvest != null && _harvest.IsActive) reason = "harvest";
                    else if (_repair != null && _repair.IsActive) reason = "itemRepair";
                    else if (_rest != null && (_rest.IsResting || _rest.IsNavigating)) reason = "rest";
                    else if (CompanionInteractPanel.IsOpenFor(_setup)) reason = "UI panel";
                    else if (CompanionRadialMenu.IsOpenFor(_setup)) reason = "radial menu";
                    Abort($"interrupted by {reason}");
                }
                return;
            }

            // ── Slot rotation: Repair(10s) → Refuel(10s) → Sort(10s) → Smelt(45s) ──
            _slotTimer -= Time.deltaTime;
            if (_slotTimer <= 0f)
            {
                // Abort current task before switching
                if (_phase != HomesteadPhase.Idle)
                    Abort($"rotation → next slot");
                if (_currentSlot == TaskSlot.Smelt && _smelt != null && _smelt.IsActive)
                    _smelt.CancelDirected();

                // Advance to next slot
                switch (_currentSlot)
                {
                    case TaskSlot.Repair: _currentSlot = TaskSlot.Refuel; _slotTimer = TaskSlotTime; break;
                    case TaskSlot.Refuel: _currentSlot = TaskSlot.Sort;   _slotTimer = TaskSlotTime; break;
                    case TaskSlot.Sort:   _currentSlot = TaskSlot.Smelt;  _slotTimer = SmeltSlotTime; break;
                    case TaskSlot.Smelt:  _currentSlot = TaskSlot.Repair; _slotTimer = TaskSlotTime; break;
                }
                // Reset scan state so the new slot scans immediately
                _scanTimer = 0f;
                _lastScanEmpty = false;
                Log($"Rotation: switching to {_currentSlot} slot ({_slotTimer}s)");
            }

            // During smelt slot, stay idle and let SmeltController run
            if (_currentSlot == TaskSlot.Smelt) return;

            float dt = Time.deltaTime;

            switch (_phase)
            {
                case HomesteadPhase.Idle:              UpdateIdle(dt);            break;
                case HomesteadPhase.MovingToSupplyChest: UpdateMovingToChest(dt); break;
                case HomesteadPhase.TakingFromChest:    UpdateTakingFromChest(dt); break;
                case HomesteadPhase.MovingToFireplace:   UpdateMovingToFire(dt);  break;
                case HomesteadPhase.Refueling:          UpdateRefueling(dt);      break;
                case HomesteadPhase.MovingToRepairTarget: UpdateMovingToRepair(dt); break;
                case HomesteadPhase.Repairing:          UpdateRepairing(dt);      break;
                case HomesteadPhase.MovingToSortSource:  UpdateMovingToSortSource(dt); break;
                case HomesteadPhase.TakingSortItems:     UpdateTakingSortItems(dt); break;
                case HomesteadPhase.MovingToSortDest:    UpdateMovingToSortDest(dt); break;
                case HomesteadPhase.DepositingSortItems:  UpdateDepositingSortItems(dt); break;
            }
        }

        // ── Activation guard ────────────────────────────────────────────────

        private bool IsHomesteadMode()
        {
            return _setup != null
                && _setup.GetStayHome()
                && _setup.HasHomePosition()
                && !_setup.GetFollow();
        }

        private bool ShouldAbort()
        {
            if (_combat != null && _combat.Phase != CombatController.CombatPhase.Idle)
                return true;
            if (_harvest != null && _harvest.IsActive)
                return true;
            // SmeltController is managed by rotation — no need to abort for it
            if (_repair != null && _repair.IsActive)
                return true;
            if (_rest != null && (_rest.IsResting || _rest.IsNavigating))
                return true;
            if (CompanionInteractPanel.IsOpenFor(_setup) || CompanionRadialMenu.IsOpenFor(_setup))
                return true;
            return false;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Idle — periodic scan for maintenance tasks
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateIdle(float dt)
        {
            _scanTimer -= dt;
            if (_scanTimer > 0f) return;
            _scanTimer = ScanInterval;

            Vector3 homePos = _setup.GetHomePosition();
            Log($"Scanning [{_currentSlot}] from home=({homePos.x:F0},{homePos.y:F0},{homePos.z:F0}) radius={ScanRadius}");

            bool found = false;
            switch (_currentSlot)
            {
                case TaskSlot.Repair: found = TryScanForDamagedPieces(homePos); break;
                case TaskSlot.Refuel: found = TryScanForLowFires(homePos); break;
                case TaskSlot.Sort:   found = TryScanForSortableChests(homePos); break;
            }

            if (found) return;

            // Nothing found for this slot — backoff until slot expires
            if (!_lastScanEmpty)
                Log($"Scan [{_currentSlot}]: nothing to do — backing off");
            _lastScanEmpty = true;
            _scanTimer = ScanBackoff;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Priority 1: Fire / Torch Refueling
        // ═════════════════════════════════════════════════════════════════════

        private bool TryScanForLowFires(Vector3 homePos)
        {
            _tempPieces.Clear();
            Piece.GetAllPiecesInRadius(homePos, ScanRadius, _tempPieces);

            Fireplace bestFire = null;
            float bestDist = float.MaxValue;
            int fireCount = 0;
            int lowFuelCount = 0;

            for (int i = 0; i < _tempPieces.Count; i++)
            {
                var piece = _tempPieces[i];
                if (piece == null) continue;

                var fp = piece.GetComponent<Fireplace>();
                if (fp == null) continue;
                fireCount++;

                if (!fp.m_canRefill || fp.m_infiniteFuel)
                {
                    Log($"  Fire \"{fp.m_name}\" skipped: canRefill={fp.m_canRefill} infinite={fp.m_infiniteFuel}");
                    continue;
                }
                if (fp.m_fuelItem == null)
                {
                    Log($"  Fire \"{fp.m_name}\" skipped: no fuelItem");
                    continue;
                }

                var fpNview = fp.GetComponent<ZNetView>();
                if (fpNview == null || fpNview.GetZDO() == null) continue;

                float currentFuel = fpNview.GetZDO().GetFloat(ZDOVars.s_fuel);
                float maxFuel = fp.m_maxFuel;
                if (maxFuel <= 0f) continue;

                float ratio = currentFuel / maxFuel;
                if (ratio >= FuelThreshold)
                {
                    Log($"  Fire \"{fp.m_name}\" fuel={currentFuel:F1}/{maxFuel:F0} ({ratio * 100f:F0}%) — above threshold");
                    continue;
                }

                lowFuelCount++;
                float dist = Vector3.Distance(transform.position, fp.transform.position);
                Log($"  Fire \"{fp.m_name}\" fuel={currentFuel:F1}/{maxFuel:F0} ({ratio * 100f:F0}%) LOW — dist={dist:F1}m fuelItem={fp.m_fuelItem.gameObject.name}");

                if (dist < bestDist)
                {
                    bestFire = fp;
                    bestDist = dist;
                }
            }

            Log($"Fire scan: {_tempPieces.Count} pieces, {fireCount} fireplaces, {lowFuelCount} need fuel");

            if (bestFire == null) return false;

            _targetFireplace = bestFire;
            _fuelItemPrefab = bestFire.m_fuelItem.gameObject.name;
            _fuelItemName = bestFire.m_fuelItem.m_itemData.m_shared.m_name;

            var fireNview = bestFire.GetComponent<ZNetView>();
            if (fireNview == null || fireNview.GetZDO() == null) return false;
            float fuel = fireNview.GetZDO().GetFloat(ZDOVars.s_fuel);
            _fuelToAdd = Mathf.Max(1, (int)(bestFire.m_maxFuel - fuel));

            _lastScanEmpty = false;
            Log($"Found low fire \"{bestFire.m_name}\" fuel={fuel:F0}/{bestFire.m_maxFuel} " +
                $"({bestDist:F1}m) — need {_fuelToAdd}x \"{_fuelItemPrefab}\"");

            // Check if we already have fuel in inventory
            var inv = _humanoid?.GetInventory();
            int carried = inv != null ? CountItemInInventory(inv, _fuelItemPrefab) : 0;

            if (carried > 0)
            {
                // Skip chest, go straight to fire
                Log($"Already carrying {carried}x fuel — heading to fire");
                _fuelToAdd = Mathf.Min(_fuelToAdd, carried);
                ResetStuck();
                _phase = HomesteadPhase.MovingToFireplace;
                return true;
            }

            // Need to fetch fuel from a chest
            ScanNearbyChests(homePos);
            _supplyChest = FindChestWithItem(_fuelItemPrefab);

            if (_supplyChest == null)
            {
                Log($"No chest with \"{_fuelItemPrefab}\" found — skipping refuel");
                _targetFireplace = null;
                return false;
            }

            ResetStuck();
            _phase = HomesteadPhase.MovingToSupplyChest;
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_refuel"), "Action");
            Log($"Fetching fuel from chest at {_supplyChest.transform.position:F1}");
            return true;
        }

        // ── Moving to supply chest ──────────────────────────────────────────

        private void UpdateMovingToChest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_supplyChest == null)
            {
                Abort("supply chest destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _supplyChest.transform.position);

            if (dist <= UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_supplyChest.transform.position);
                _chestOpened = false;
                _phase = HomesteadPhase.TakingFromChest;
                Log($"Arrived at supply chest ({dist:F1}m) — taking fuel");
                return;
            }

            _ai.MoveToPoint(dt, _supplyChest.transform.position, UseDistance, dist > 8f);
            LogMovement(dt, "supply chest", dist);
            UpdateStuck(dt, "supply chest", dist);
        }

        // ── Taking fuel from chest ──────────────────────────────────────────

        private void UpdateTakingFromChest(float dt)
        {
            if (_supplyChest == null)
            {
                if (_chestOpened) _chestOpened = false;
                Abort("chest destroyed while taking");
                return;
            }

            // Step 1: Open chest
            if (!_chestOpened)
            {
                _supplyChest.SetInUse(true);
                _chestOpened = true;
                _actionTimer = ChestOpenDelay;
                if (_zanim != null) _zanim.SetTrigger("interact");
                Log("Opened supply chest");
                return;
            }

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;
            _actionTimer = ItemTransferDelay;

            var chestInv = _supplyChest.GetInventory();
            var companionInv = _humanoid?.GetInventory();
            if (chestInv == null || companionInv == null)
            {
                CloseChest(_supplyChest);
                Abort("inventory null during take");
                return;
            }

            // Take one fuel item per tick
            var item = FindItemByPrefab(chestInv, _fuelItemPrefab);
            if (item == null || _fuelToAdd <= 0)
            {
                // Done taking
                CloseChest(_supplyChest);
                int carried = CountItemInInventory(companionInv, _fuelItemPrefab);
                Log($"Finished taking fuel — carrying {carried}x \"{_fuelItemPrefab}\"");

                if (carried == 0)
                {
                    Abort("failed to take any fuel");
                    return;
                }

                _fuelToAdd = Mathf.Min(_fuelToAdd, carried);
                ResetStuck();
                _phase = HomesteadPhase.MovingToFireplace;
                return;
            }

            // Transfer one unit: clone with stack=1, add to companion, remove from chest
            if (TransferOne(chestInv, companionInv, item))
            {
                _fuelToAdd--;
                Log($"Took 1x \"{_fuelItemPrefab}\" from chest — {_fuelToAdd} more needed");
            }
            else
            {
                // Inventory full
                CloseChest(_supplyChest);
                int carried = CountItemInInventory(companionInv, _fuelItemPrefab);
                if (carried > 0)
                {
                    _fuelToAdd = carried;
                    ResetStuck();
                    _phase = HomesteadPhase.MovingToFireplace;
                }
                else
                {
                    Abort("inventory full, no fuel taken");
                }
            }
        }

        // ── Moving to fireplace ─────────────────────────────────────────────

        private void UpdateMovingToFire(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetFireplace == null)
            {
                Abort("fireplace destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetFireplace.transform.position);

            if (dist <= UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_targetFireplace.transform.position);
                _actionTimer = FuelAddDelay;
                _phase = HomesteadPhase.Refueling;
                Log("Arrived at fireplace — starting to refuel");
                return;
            }

            _ai.MoveToPoint(dt, _targetFireplace.transform.position, UseDistance, dist > 8f);
            LogMovement(dt, "fireplace", dist);
            UpdateStuck(dt, "fireplace", dist);
        }

        // ── Refueling ───────────────────────────────────────────────────────

        private void UpdateRefueling(float dt)
        {
            if (_targetFireplace == null)
            {
                Abort("fireplace destroyed while refueling");
                return;
            }

            // Check drift
            float dist = Vector3.Distance(transform.position, _targetFireplace.transform.position);
            if (dist > UseDistance * 2f)
            {
                Abort($"drifted from fireplace ({dist:F1}m)");
                return;
            }

            _ai.LookAtPoint(_targetFireplace.transform.position);

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;
            _actionTimer = FuelAddDelay;

            // Check fire is not already full
            var fpNview = _targetFireplace.GetComponent<ZNetView>();
            if (fpNview == null || fpNview.GetZDO() == null)
            {
                Abort("fireplace nview lost");
                return;
            }

            float currentFuel = fpNview.GetZDO().GetFloat(ZDOVars.s_fuel);
            if (currentFuel >= _targetFireplace.m_maxFuel)
            {
                Log("Fire is full");
                FinishRefuel();
                return;
            }

            // Consume one fuel from inventory and add to fire
            var inv = _humanoid?.GetInventory();
            if (inv == null || !ConsumeOneFromInventory(inv, _fuelItemPrefab))
            {
                Log("Out of fuel in inventory");
                FinishRefuel();
                return;
            }

            if (_zanim != null) _zanim.SetTrigger("interact");
            fpNview.InvokeRPC("RPC_AddFuel");
            _fuelToAdd--;
            Log($"Added 1 fuel to \"{_targetFireplace.m_name}\" — {_fuelToAdd} remaining");

            if (_fuelToAdd <= 0)
                FinishRefuel();
        }

        private void FinishRefuel()
        {
            Log($"Finished refueling \"{_targetFireplace?.m_name ?? "?"}\"");
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_stoked"), "Action");
            _targetFireplace = null;
            _supplyChest = null;
            _fuelItemPrefab = null;
            _fuelItemName = null;
            _phase = HomesteadPhase.Idle;
            _scanTimer = 2f; // quick rescan
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Priority 2: Building Repair
        // ═════════════════════════════════════════════════════════════════════

        private bool TryScanForDamagedPieces(Vector3 homePos)
        {
            // Require a hammer in inventory
            var inv = _humanoid?.GetInventory();
            if (inv == null) return false;
            if (FindHammerInInventory(inv) == null)
            {
                if (!_lastHammerMissing)
                {
                    Log("Scan: no hammer in inventory — skipping building repair");
                    _lastHammerMissing = true;
                }
                return false;
            }
            _lastHammerMissing = false;

            var allWNT = WearNTear.GetAllInstances();
            if (allWNT == null) return false;

            WearNTear bestPiece = null;
            float bestDist = float.MaxValue;
            float bestHealth = 1f;

            for (int i = 0; i < allWNT.Count; i++)
            {
                var wnt = allWNT[i];
                if (wnt == null) continue;

                // Only repair player-built pieces (skip world ruins/dungeons)
                var wntPiece = wnt.GetComponent<Piece>();
                if (wntPiece == null || !wntPiece.IsPlacedByPlayer()) continue;

                float dist = Vector3.Distance(homePos, wnt.transform.position);
                if (dist > ScanRadius) continue;

                float hp = wnt.GetHealthPercentage();
                if (hp >= 1f) continue;

                // Prefer closest first — repair nearby before running far
                if (dist < bestDist)
                {
                    bestPiece = wnt;
                    bestDist = dist;
                    bestHealth = hp;
                }
            }

            if (bestPiece == null) return false;

            _targetPiece = bestPiece;
            _lastScanEmpty = false;
            ResetStuck();
            _phase = HomesteadPhase.MovingToRepairTarget;

            var piece = bestPiece.GetComponent<Piece>();
            string pieceName = piece != null ? piece.m_name : bestPiece.name;
            Log($"Found damaged piece \"{pieceName}\" hp={bestHealth * 100f:F0}% ({bestDist:F1}m)");
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_repair"), "Repair");
            return true;
        }

        // ── Moving to repair target ─────────────────────────────────────────

        private void UpdateMovingToRepair(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetPiece == null)
            {
                Abort("repair target destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetPiece.transform.position);

            if (dist <= UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_targetPiece.transform.position);

                // Equip hammer for repair
                _hammerEquipped = false;
                var cInv = _humanoid?.GetInventory();
                if (cInv != null)
                {
                    var hammer = FindHammerInInventory(cInv);
                    if (hammer != null)
                    {
                        _prevRightItem = (ItemDrop.ItemData)CompanionSetup._rightItemField?.GetValue(_humanoid);
                        if (_prevRightItem != null && _prevRightItem != hammer)
                            _humanoid.UnequipItem(_prevRightItem, false);
                        _humanoid.EquipItem(hammer, true);
                        _hammerEquipped = true;
                        Log($"Equipped hammer for repair (prev=\"{_prevRightItem?.m_shared?.m_name ?? "none"}\")");
                    }
                }

                _actionTimer = RepairDelay;
                _phase = HomesteadPhase.Repairing;
                Log("Arrived at repair target");
                return;
            }

            _ai.MoveToPoint(dt, _targetPiece.transform.position, UseDistance, dist > 8f);
            LogMovement(dt, "repair target", dist);
            UpdateStuck(dt, "repair target", dist);
        }

        // ── Repairing ───────────────────────────────────────────────────────

        private void UpdateRepairing(float dt)
        {
            if (_targetPiece == null)
            {
                Abort("repair target destroyed while repairing");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetPiece.transform.position);
            if (dist > UseDistance * 2f)
            {
                Abort($"drifted from repair target ({dist:F1}m)");
                return;
            }

            _ai.LookAtPoint(_targetPiece.transform.position);

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;
            _actionTimer = RepairDelay;

            // Play hammer swing animation
            var rightItem = (ItemDrop.ItemData)CompanionSetup._rightItemField?.GetValue(_humanoid);
            if (_zanim != null && rightItem?.m_shared?.m_attack != null)
            {
                string anim = rightItem.m_shared.m_attack.m_attackAnimation;
                if (!string.IsNullOrEmpty(anim))
                    _zanim.SetTrigger(anim);
                else
                    _zanim.SetTrigger("swing_pickaxe");
            }
            else if (_zanim != null)
            {
                _zanim.SetTrigger("swing_pickaxe");
            }

            // Repair the piece
            bool repaired = _targetPiece.Repair();

            // Play place effect for VFX/sound
            var piece = _targetPiece.GetComponent<Piece>();
            if (piece != null && piece.m_placeEffect != null)
                piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation);

            if (repaired)
                Log($"Repaired \"{piece?.m_name ?? _targetPiece.name}\"");
            else
                Log($"Repair call returned false for \"{piece?.m_name ?? _targetPiece.name}\"");

            // Check if fully repaired
            float hp = _targetPiece.GetHealthPercentage();
            if (hp >= 1f)
            {
                Log("Piece fully repaired — scanning for next");
                if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_repaired"), "Repair");
                RestoreWeaponAfterRepair();
                _targetPiece = null;
                _phase = HomesteadPhase.Idle;
                _scanTimer = 1f; // quick rescan for next damaged piece
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Priority 3: Chest Sorting / Consolidation
        // ═════════════════════════════════════════════════════════════════════

        private bool TryScanForSortableChests(Vector3 homePos)
        {
            ScanNearbyChests(homePos);
            if (_nearbyChests.Count < 2) return false;

            // Build map: itemPrefab → list of (container, count)
            var itemMap = new Dictionary<string, List<(Container chest, int count)>>();

            for (int c = 0; c < _nearbyChests.Count; c++)
            {
                var chest = _nearbyChests[c];
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;

                var items = inv.GetAllItems();
                // Count per-prefab in this chest
                var localCounts = new Dictionary<string, int>();
                for (int i = 0; i < items.Count; i++)
                {
                    string prefab = items[i].m_dropPrefab?.name ?? items[i].m_shared?.m_name;
                    if (string.IsNullOrEmpty(prefab)) continue;
                    if (!localCounts.ContainsKey(prefab))
                        localCounts[prefab] = 0;
                    localCounts[prefab] += items[i].m_stack;
                }

                foreach (var kv in localCounts)
                {
                    if (!itemMap.ContainsKey(kv.Key))
                        itemMap[kv.Key] = new List<(Container, int)>();
                    itemMap[kv.Key].Add((chest, kv.Value));
                }
            }

            // Find items split across 2+ chests
            Container bestSource = null;
            Container bestDest = null;
            string bestPrefab = null;
            int smallestStack = int.MaxValue;

            foreach (var kv in itemMap)
            {
                if (kv.Value.Count < 2) continue;

                // Find smallest and largest stacks
                Container smallest = null, largest = null;
                int minCount = int.MaxValue, maxCount = 0;

                for (int i = 0; i < kv.Value.Count; i++)
                {
                    if (kv.Value[i].count < minCount)
                    {
                        minCount = kv.Value[i].count;
                        smallest = kv.Value[i].chest;
                    }
                    if (kv.Value[i].count > maxCount)
                    {
                        maxCount = kv.Value[i].count;
                        largest = kv.Value[i].chest;
                    }
                }

                // Don't sort if source and dest are the same chest
                if (smallest == largest) continue;

                // Check dest chest has space
                if (largest != null)
                {
                    var destInv = largest.GetInventory();
                    if (destInv != null && destInv.GetEmptySlots() == 0)
                    {
                        // Check if existing stack can accept more
                        var existingItem = FindItemByPrefab(destInv, kv.Key);
                        if (existingItem == null || existingItem.m_stack >= existingItem.m_shared.m_maxStackSize)
                            continue; // no room
                    }
                }

                if (minCount < smallestStack)
                {
                    smallestStack = minCount;
                    bestSource = smallest;
                    bestDest = largest;
                    bestPrefab = kv.Key;
                }
            }

            if (bestSource == null || bestDest == null || bestPrefab == null)
                return false;

            _sourceChest = bestSource;
            _destChest = bestDest;
            _sortItemPrefab = bestPrefab;
            _transferQueue.Clear();
            _lastScanEmpty = false;
            ResetStuck();
            _phase = HomesteadPhase.MovingToSortSource;

            Log($"Sort: consolidating \"{bestPrefab}\" ({smallestStack} items) " +
                $"from chest at {bestSource.transform.position:F1} → {bestDest.transform.position:F1}");
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_tidy"), "Action");
            return true;
        }

        // ── Moving to sort source ───────────────────────────────────────────

        private void UpdateMovingToSortSource(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_sourceChest == null)
            {
                Abort("source chest destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _sourceChest.transform.position);

            if (dist <= UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_sourceChest.transform.position);
                _chestOpened = false;
                _transferQueue.Clear();
                _phase = HomesteadPhase.TakingSortItems;
                Log($"Arrived at source chest ({dist:F1}m)");
                return;
            }

            _ai.MoveToPoint(dt, _sourceChest.transform.position, UseDistance, dist > 8f);
            LogMovement(dt, "sort source chest", dist);
            UpdateStuck(dt, "sort source chest", dist);
        }

        // ── Taking sort items ───────────────────────────────────────────────

        private void UpdateTakingSortItems(float dt)
        {
            if (_sourceChest == null)
            {
                if (_chestOpened) _chestOpened = false;
                Abort("source chest destroyed while taking");
                return;
            }

            // Step 1: Open chest
            if (!_chestOpened)
            {
                _sourceChest.SetInUse(true);
                _chestOpened = true;
                _actionTimer = ChestOpenDelay;
                if (_zanim != null) _zanim.SetTrigger("interact");
                Log("Opened source chest for sorting");
                return;
            }

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;
            _actionTimer = ItemTransferDelay;

            var chestInv = _sourceChest.GetInventory();
            var companionInv = _humanoid?.GetInventory();
            if (chestInv == null || companionInv == null)
            {
                CloseChest(_sourceChest);
                Abort("inventory null during sort take");
                return;
            }

            // Take matching items one by one
            var item = FindItemByPrefab(chestInv, _sortItemPrefab);
            if (item == null)
            {
                // Done taking
                CloseChest(_sourceChest);
                int carried = CountItemInInventory(companionInv, _sortItemPrefab);
                Log($"Finished taking sort items — carrying {carried}");

                if (carried == 0)
                {
                    Abort("no sort items taken");
                    return;
                }

                ResetStuck();
                _phase = HomesteadPhase.MovingToSortDest;
                return;
            }

            // Transfer one unit
            if (TransferOne(chestInv, companionInv, item))
            {
                Log($"Took 1x \"{_sortItemPrefab}\" for sorting");
            }
            else
            {
                // Inventory full — go deposit what we have
                CloseChest(_sourceChest);
                int carried = CountItemInInventory(companionInv, _sortItemPrefab);
                if (carried > 0)
                {
                    ResetStuck();
                    _phase = HomesteadPhase.MovingToSortDest;
                }
                else
                {
                    Abort("inventory full, no sort items taken");
                }
            }
        }

        // ── Moving to sort destination ──────────────────────────────────────

        private void UpdateMovingToSortDest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_destChest == null)
            {
                Abort("dest chest destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _destChest.transform.position);

            if (dist <= UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_destChest.transform.position);
                _chestOpened = false;
                _phase = HomesteadPhase.DepositingSortItems;
                Log($"Arrived at dest chest ({dist:F1}m)");
                return;
            }

            _ai.MoveToPoint(dt, _destChest.transform.position, UseDistance, dist > 8f);
            LogMovement(dt, "sort dest chest", dist);
            UpdateStuck(dt, "sort dest chest", dist);
        }

        // ── Depositing sort items ───────────────────────────────────────────

        private void UpdateDepositingSortItems(float dt)
        {
            if (_destChest == null)
            {
                if (_chestOpened) _chestOpened = false;
                Abort("dest chest destroyed while depositing");
                return;
            }

            // Step 1: Open chest
            if (!_chestOpened)
            {
                _destChest.SetInUse(true);
                _chestOpened = true;
                _actionTimer = ChestOpenDelay;
                if (_zanim != null) _zanim.SetTrigger("interact");
                Log("Opened dest chest for depositing");
                return;
            }

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;
            _actionTimer = ItemTransferDelay;

            var chestInv = _destChest.GetInventory();
            var companionInv = _humanoid?.GetInventory();
            if (chestInv == null || companionInv == null)
            {
                CloseChest(_destChest);
                Abort("inventory null during sort deposit");
                return;
            }

            // Deposit one matching item per tick
            var item = FindItemByPrefab(companionInv, _sortItemPrefab);
            if (item == null)
            {
                // Done
                CloseChest(_destChest);
                FinishSort();
                return;
            }

            if (TransferOne(companionInv, chestInv, item))
            {
                Log($"Deposited 1x \"{_sortItemPrefab}\" in dest chest");
            }
            else
            {
                // Chest full
                CloseChest(_destChest);
                Log("Dest chest full — finishing sort");
                FinishSort();
            }
        }

        private void FinishSort()
        {
            Log("Sort task complete");
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_tidied"), "Action");
            _sourceChest = null;
            _destChest = null;
            _sortItemPrefab = null;
            _transferQueue.Clear();
            _phase = HomesteadPhase.Idle;
            _scanTimer = 2f; // quick rescan
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Public API
        // ═════════════════════════════════════════════════════════════════════

        public void CancelDirected()
        {
            if (_phase == HomesteadPhase.Idle) return;
            Abort("cancelled by command");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Helpers
        // ═════════════════════════════════════════════════════════════════════

        private void Abort(string reason)
        {
            Log($"Aborted — {reason} (phase was {_phase})");
            CloseAnyOpenChest();
            RestoreWeaponAfterRepair();
            _targetFireplace = null;
            _supplyChest = null;
            _fuelItemPrefab = null;
            _fuelItemName = null;
            _targetPiece = null;
            _sourceChest = null;
            _destChest = null;
            _sortItemPrefab = null;
            _transferQueue.Clear();
            _phase = HomesteadPhase.Idle;
            _scanTimer = ScanInterval;
        }

        private void ScanNearbyChests(Vector3 center)
        {
            _nearbyChests.Clear();
            _tempPieces.Clear();
            Piece.GetAllPiecesInRadius(center, ScanRadius, _tempPieces);

            for (int i = 0; i < _tempPieces.Count; i++)
            {
                var piece = _tempPieces[i];
                if (piece == null) continue;
                var container = piece.GetComponentInChildren<Container>();
                if (container == null) continue;
                if (container.GetInventory() == null) continue;
                // Skip companion's own container
                if (container.gameObject == gameObject) continue;
                // Skip containers in use
                if (container.IsInUse()) continue;
                _nearbyChests.Add(container);
            }
        }

        private Container FindChestWithItem(string prefabName)
        {
            Container best = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < _nearbyChests.Count; i++)
            {
                var chest = _nearbyChests[i];
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;

                var item = FindItemByPrefab(inv, prefabName);
                if (item == null) continue;

                float dist = Vector3.Distance(transform.position, chest.transform.position);
                if (dist < bestDist)
                {
                    best = chest;
                    bestDist = dist;
                }
            }

            return best;
        }

        /// <summary>
        /// Find an item by prefab name (m_dropPrefab.name).
        /// Inventory.GetItem(string) searches m_shared.m_name (localized) by default,
        /// which does NOT match prefab names like "Wood" or "Sap".
        /// </summary>
        private static ItemDrop.ItemData FindItemByPrefab(Inventory inv, string prefabName)
        {
            var items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].m_dropPrefab != null && items[i].m_dropPrefab.name == prefabName)
                    return items[i];
            }
            return null;
        }

        /// <summary>Transfer 1 unit of an item from source to dest inventory.</summary>
        private static bool TransferOne(Inventory source, Inventory dest, ItemDrop.ItemData item)
        {
            var clone = item.Clone();
            clone.m_stack = 1;
            if (!dest.CanAddItem(clone, 1)) return false;
            dest.AddItem(clone);
            source.RemoveItem(item, 1);
            return true;
        }

        private static int CountItemInInventory(Inventory inv, string prefabName)
        {
            int count = 0;
            var items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                string name = items[i].m_dropPrefab?.name ?? items[i].m_shared?.m_name;
                if (name == prefabName)
                    count += items[i].m_stack;
            }
            return count;
        }

        private static bool ConsumeOneFromInventory(Inventory inv, string prefabName)
        {
            var items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                string name = items[i].m_dropPrefab?.name ?? items[i].m_shared?.m_name;
                if (name == prefabName)
                {
                    inv.RemoveItem(items[i], 1);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Find a hammer (build tool) in companion inventory.</summary>
        private static ItemDrop.ItemData FindHammerInInventory(Inventory inv)
        {
            var items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i]?.m_shared?.m_buildPieces != null)
                    return items[i];
            }
            return null;
        }

        /// <summary>Re-equip previous weapon after hammer repair.</summary>
        private void RestoreWeaponAfterRepair()
        {
            if (!_hammerEquipped) return;
            _hammerEquipped = false;

            // Unequip hammer
            var rightItem = (ItemDrop.ItemData)CompanionSetup._rightItemField?.GetValue(_humanoid);
            if (rightItem?.m_shared?.m_buildPieces != null)
                _humanoid.UnequipItem(rightItem, false);

            // Re-equip previous weapon if still in inventory
            if (_prevRightItem != null)
            {
                var inv = _humanoid?.GetInventory();
                if (inv != null && inv.ContainsItem(_prevRightItem))
                {
                    _humanoid.EquipItem(_prevRightItem, true);
                    Log($"Restored weapon \"{_prevRightItem.m_shared?.m_name ?? "?"}\"");
                }
                else
                {
                    // Previous weapon gone — trigger auto-equip
                    _setup?.SyncEquipmentToInventory();
                }
            }
            else
            {
                _setup?.SyncEquipmentToInventory();
            }
            _prevRightItem = null;
        }

        private void CloseChest(Container chest)
        {
            if (chest != null)
                chest.SetInUse(false);
            _chestOpened = false;
        }

        private void CloseAnyOpenChest()
        {
            if (!_chestOpened) return;

            switch (_phase)
            {
                case HomesteadPhase.TakingFromChest:
                    if (_supplyChest != null) _supplyChest.SetInUse(false);
                    break;
                case HomesteadPhase.TakingSortItems:
                    if (_sourceChest != null) _sourceChest.SetInUse(false);
                    break;
                case HomesteadPhase.DepositingSortItems:
                    if (_destChest != null) _destChest.SetInUse(false);
                    break;
            }
            _chestOpened = false;
        }

        private void ResetStuck()
        {
            _stuckTimer = 0f;
            _stuckCheckTimer = 0f;
            _stuckCheckPos = transform.position;
        }

        private void UpdateStuck(float dt, string target, float dist)
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
                Abort($"stuck moving to {target} ({dist:F1}m away, stuck {_stuckTimer:F1}s)");
        }

        private void LogMovement(float dt, string target, float dist)
        {
            _moveLogTimer -= dt;
            if (_moveLogTimer > 0f) return;
            _moveLogTimer = 2f;
            var vel = _character?.GetVelocity() ?? Vector3.zero;
            Log($"Moving → {target} dist={dist:F1}m speed={vel.magnitude:F1} " +
                $"stuck={_stuckTimer:F1}s pos={transform.position:F1}");
        }

        private void Log(string msg)
        {
            string name = _character?.m_name ?? "?";
            CompanionsPlugin.Log.LogInfo($"[Homestead|{name}] {msg}");
        }
    }
}
