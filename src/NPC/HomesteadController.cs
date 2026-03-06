using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Organic home-life controller for companions in StayHome mode.
    /// When Follow is OFF and StayHome is ON, picks weighted-random behaviors:
    ///   - Wander around home area
    ///   - Sit on chairs/stools
    ///   - Stand at workbench/forge (cosmetic)
    ///   - Repair damaged buildings
    ///   - Refuel low fires/torches
    ///   - Sort items within chests
    ///   - Cook meals at a cauldron when hungry
    /// </summary>
    public class HomesteadController : MonoBehaviour
    {
        internal enum HomePhase
        {
            Idle,
            // Wander
            Wandering,
            // Sit
            MovingToChair, Sitting,
            // Cosmetic station use
            MovingToStation, UsingStation,
            // Repair
            MovingToRepairTarget, Repairing,
            // Refuel
            MovingToSupplyChest, TakingFuel, MovingToFireplace, Refueling,
            // Sort
            MovingToSortChest, SortingChest,
            // Cook
            MovingToIngredientChest, TakingIngredients, MovingToCauldron, Cooking
        }

        internal HomePhase Phase => _phase;
        public bool IsActive => _phase != HomePhase.Idle;

        // ── Components ──────────────────────────────────────────────────────
        private CompanionAI       _ai;
        private Humanoid          _humanoid;
        private Character         _character;
        private CompanionSetup    _setup;
        private ZNetView          _nview;
        private ZSyncAnimation    _zanim;
        private CompanionTalk     _talk;
        private CompanionFood     _food;
        private HarvestController _harvest;
        private RepairController  _repair;
        private CompanionRest     _rest;
        private DoorHandler       _doorHandler;

        // ── State ───────────────────────────────────────────────────────────
        private HomePhase _phase;
        private float _idleTimer;       // delay before picking next behavior
        private float _actionTimer;     // generic timer for current action
        private float _stuckTimer;
        private float _stuckCheckTimer;
        private Vector3 _stuckCheckPos;
        private bool _chestOpened;

        // ── Behavior cooldowns ──────────────────────────────────────────────
        private float _wanderCooldown;
        private float _sitCooldown;
        private float _stationCooldown;
        private float _repairCooldown;
        private float _refuelCooldown;
        private float _sortCooldown;
        private float _cookCooldown;

        // ── Wander state ────────────────────────────────────────────────────
        private Vector3 _wanderTarget;
        private float _wanderTimeout;      // per-waypoint timeout
        private float _wanderDuration;     // total walk duration remaining

        // ── Chair state ─────────────────────────────────────────────────────
        private Transform _chairAttachPoint;
        private float _sitDuration;
        private float _sitTimer;
        private bool _isSitting;
        private Rigidbody _body;

        // ── Station state ───────────────────────────────────────────────────
        private CraftingStation _targetStation;
        private float _stationDuration;

        // ── Repair state ────────────────────────────────────────────────────
        private WearNTear _targetPiece;
        private ItemDrop.ItemData _prevRightItem;
        private bool _hammerEquipped;
        private bool _lastHammerMissing;

        // ── Refuel state ────────────────────────────────────────────────────
        private Fireplace _targetFireplace;
        private Container _supplyChest;
        private string _fuelItemPrefab;
        private int _fuelToAdd;

        // ── Sort state ──────────────────────────────────────────────────────
        private Container _sortChest;

        // ── Cook state ──────────────────────────────────────────────────────
        private CraftingStation _targetCauldron;
        private Recipe _selectedRecipe;
        private Container _ingredientChest;
        private readonly List<(string prefab, int needed)> _ingredientList = new List<(string, int)>();
        private int _ingredientIndex;

        // ── Shared claims ───────────────────────────────────────────────────
        private static readonly HashSet<int> s_claimedPieces = new HashSet<int>();
        private int _claimedPieceId;

        // ── Blacklist ───────────────────────────────────────────────────────
        private readonly Dictionary<int, float> _blacklist = new Dictionary<int, float>();
        private readonly List<int> _expiredKeys = new List<int>();
        private const float BlacklistDuration = 120f;

        // ── Scan buffers ────────────────────────────────────────────────────
        private readonly List<Piece> _tempPieces = new List<Piece>();
        private readonly List<Container> _nearbyChests = new List<Container>();
        private readonly Collider[] _scanBuffer = new Collider[256];

        // ── Constants ───────────────────────────────────────────────────────
        private static float ScanRadius       => ModConfig.HomesteadScanRadius.Value;
        private static float UseDistance      => ModConfig.HomesteadUseDistance.Value;
        private static float FuelThreshold    => ModConfig.HomesteadFuelThreshold.Value;
        private const float FuelAddDelay     = 0.8f;
        private const float RepairDelay      = 1.2f;
        private const float ChestOpenDelay   = 0.8f;
        private const float MoveTimeout      = 10f;
        private const float StuckCheckPeriod = 1f;
        private const float StuckMinDistance = 0.5f;
        private const float IdleDelayMin     = 3f;
        private const float IdleDelayMax     = 8f;

        // ── Behavior weights ────────────────────────────────────────────────
        private const float WeightCook    = 100f;
        private const float WeightRepair  = 20f;
        private const float WeightRefuel  = 20f;
        private const float WeightSort    = 10f;
        private const float WeightSit     = 15f;
        private const float WeightStation = 10f;
        private const float WeightWander  = 25f;

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
            _food      = GetComponent<CompanionFood>();
            _harvest   = GetComponent<HarvestController>();
            _repair    = GetComponent<RepairController>();
            _rest      = GetComponent<CompanionRest>();
            _doorHandler = GetComponent<DoorHandler>();
            _body      = GetComponent<Rigidbody>();
            Log("Awake OK");
        }

        private void OnDestroy()
        {
            UnclaimPiece();
            CloseAnyChest();
            if (_isSitting) StandUp();
        }

        private void Update()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;
            if (_character == null || _character.GetHealth() <= 0f) return;

            if (!IsHomesteadMode())
            {
                if (_phase != HomePhase.Idle)
                    Abort("left homestead mode");
                return;
            }

            if (ShouldAbort())
            {
                if (_phase != HomePhase.Idle)
                    Abort("interrupted");
                return;
            }

            // Tick cooldowns
            float dt = Time.deltaTime;
            _wanderCooldown  = Mathf.Max(0f, _wanderCooldown  - dt);
            _sitCooldown     = Mathf.Max(0f, _sitCooldown     - dt);
            _stationCooldown = Mathf.Max(0f, _stationCooldown - dt);
            _repairCooldown  = Mathf.Max(0f, _repairCooldown  - dt);
            _refuelCooldown  = Mathf.Max(0f, _refuelCooldown  - dt);
            _sortCooldown    = Mathf.Max(0f, _sortCooldown    - dt);
            _cookCooldown    = Mathf.Max(0f, _cookCooldown    - dt);

            switch (_phase)
            {
                case HomePhase.Idle:                  UpdateIdle(dt);               break;
                // Wander
                case HomePhase.Wandering:             UpdateWandering(dt);          break;
                // Sit
                case HomePhase.MovingToChair:         UpdateMovingToChair(dt);      break;
                case HomePhase.Sitting:               UpdateSitting(dt);            break;
                // Station
                case HomePhase.MovingToStation:        UpdateMovingToStation(dt);   break;
                case HomePhase.UsingStation:            UpdateUsingStation(dt);     break;
                // Repair
                case HomePhase.MovingToRepairTarget:  UpdateMovingToRepair(dt);     break;
                case HomePhase.Repairing:             UpdateRepairing(dt);          break;
                // Refuel
                case HomePhase.MovingToSupplyChest:   UpdateMovingToChest(dt);      break;
                case HomePhase.TakingFuel:            UpdateTakingFuel(dt);         break;
                case HomePhase.MovingToFireplace:     UpdateMovingToFire(dt);       break;
                case HomePhase.Refueling:             UpdateRefueling(dt);          break;
                // Sort
                case HomePhase.MovingToSortChest:     UpdateMovingToSortChest(dt);  break;
                case HomePhase.SortingChest:          UpdateSortingChest(dt);       break;
                // Cook
                case HomePhase.MovingToIngredientChest: UpdateMovingToIngredientChest(dt); break;
                case HomePhase.TakingIngredients:     UpdateTakingIngredients(dt);  break;
                case HomePhase.MovingToCauldron:      UpdateMovingToCauldron(dt);   break;
                case HomePhase.Cooking:               UpdateCooking(dt);            break;
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
            if (_ai != null && _ai.IsInCombat) return true;
            if (_harvest != null && _harvest.IsActive) return true;
            if (_repair != null && _repair.IsActive) return true;
            if (_rest != null && (_rest.IsResting || _rest.IsNavigating)) return true;
            if (CompanionInteractPanel.IsOpenFor(_setup) || CompanionRadialMenu.IsOpenFor(_setup))
                return true;
            return false;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Idle — weighted random behavior selection
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateIdle(float dt)
        {
            _idleTimer -= dt;
            if (_idleTimer > 0f) return;

            PruneBlacklist();
            PickNextBehavior();
        }

        private void PickNextBehavior()
        {
            Vector3 homePos = _setup.GetHomePosition();
            float homeRadius = _setup.GetHomeRadius();

            // Build weighted candidate list
            float totalWeight = 0f;
            bool isHungry = IsCompanionHungry();

            // Cook (only when hungry)
            float wCook = 0f;
            if (isHungry && _cookCooldown <= 0f)
            {
                wCook = WeightCook;
                totalWeight += wCook;
            }

            // Repair
            float wRepair = _repairCooldown <= 0f ? WeightRepair : 0f;
            totalWeight += wRepair;

            // Refuel
            float wRefuel = _refuelCooldown <= 0f ? WeightRefuel : 0f;
            totalWeight += wRefuel;

            // Sort
            float wSort = _sortCooldown <= 0f ? WeightSort : 0f;
            totalWeight += wSort;

            // Sit
            float wSit = _sitCooldown <= 0f ? WeightSit : 0f;
            totalWeight += wSit;

            // Station
            float wStation = _stationCooldown <= 0f ? WeightStation : 0f;
            totalWeight += wStation;

            // Wander (always available)
            float wWander = _wanderCooldown <= 0f ? WeightWander : 0f;
            totalWeight += wWander;

            if (totalWeight <= 0f)
            {
                _idleTimer = 5f;
                return;
            }

            float roll = Random.Range(0f, totalWeight);
            float cumulative = 0f;

            // Select behavior by weighted roll. Each block returns on success
            // or falls through to wander on failure (never cascades into other behaviors).
            cumulative += wCook;
            if (roll < cumulative && wCook > 0f)
            {
                if (TryStartCook(homePos)) return;
                _cookCooldown = 30f;
            }
            else
            {
                cumulative += wRepair;
                if (roll < cumulative && wRepair > 0f)
                {
                    if (TryStartRepair(homePos)) return;
                    _repairCooldown = 30f;
                }
                else
                {
                    cumulative += wRefuel;
                    if (roll < cumulative && wRefuel > 0f)
                    {
                        if (TryStartRefuel(homePos)) return;
                        _refuelCooldown = 30f;
                    }
                    else
                    {
                        cumulative += wSort;
                        if (roll < cumulative && wSort > 0f)
                        {
                            if (TryStartSort(homePos)) return;
                            _sortCooldown = 60f;
                        }
                        else
                        {
                            cumulative += wSit;
                            if (roll < cumulative && wSit > 0f)
                            {
                                if (TryStartSit(homePos, homeRadius)) return;
                                _sitCooldown = 30f;
                            }
                            else
                            {
                                cumulative += wStation;
                                if (roll < cumulative && wStation > 0f)
                                {
                                    if (TryStartStation(homePos)) return;
                                    _stationCooldown = 30f;
                                }
                                else if (wWander > 0f)
                                {
                                    StartWander(homePos, homeRadius);
                                    return;
                                }
                            }
                        }
                    }
                }
            }

            // Selected behavior failed or no wander — idle briefly then re-pick
            _idleTimer = Random.Range(IdleDelayMin, IdleDelayMax);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Wander
        // ═════════════════════════════════════════════════════════════════════

        private void StartWander(Vector3 homePos, float radius)
        {
            PickWanderPoint(homePos, radius);
            _wanderDuration = Random.Range(30f, 60f);
            _phase = HomePhase.Wandering;
            Log($"Wandering around home for {_wanderDuration:F0}s");
        }

        private void PickWanderPoint(Vector3 homePos, float radius)
        {
            Vector2 rnd = Random.insideUnitCircle * radius * 0.8f;
            _wanderTarget = homePos + new Vector3(rnd.x, 0f, rnd.y);
            // Snap Y to terrain
            if (ZoneSystem.instance != null)
            {
                float h;
                if (ZoneSystem.instance.GetSolidHeight(_wanderTarget, out h))
                    _wanderTarget.y = h;
            }
            _wanderTimeout = 15f;
        }

        private void UpdateWandering(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            _wanderTimeout -= dt;
            _wanderDuration -= dt;
            float dist = Vector3.Distance(transform.position, _wanderTarget);

            // Total wander time expired — stop and go idle
            if (_wanderDuration <= 0f)
            {
                _ai?.StopMoving();
                _wanderCooldown = Random.Range(5f, 15f);
                _phase = HomePhase.Idle;
                _idleTimer = Random.Range(IdleDelayMin, IdleDelayMax);
                return;
            }

            // Reached waypoint or timed out — pick a new one and keep walking
            if (dist < 2f || _wanderTimeout <= 0f)
            {
                var setup = GetComponent<CompanionSetup>();
                if (setup != null && setup.HasHomePosition())
                    PickWanderPoint(setup.GetHomePosition(), setup.GetHomeRadius());
                else
                    PickWanderPoint(transform.position, 10f);
                return;
            }

            _ai?.MoveToPoint(dt, _wanderTarget, 1.5f, false);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Sit on Chair
        // ═════════════════════════════════════════════════════════════════════

        private bool TryStartSit(Vector3 homePos, float radius)
        {
            int count = Physics.OverlapSphereNonAlloc(homePos, radius, _scanBuffer);
            Chair bestChair = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                var chair = _scanBuffer[i].GetComponentInParent<Chair>();
                if (chair == null) continue;
                if (chair.m_attachPoint == null) continue;
                if (IsBlacklisted(chair.gameObject)) continue;

                // Check if chair is occupied by another character
                bool occupied = false;
                var nearbyPlayers = Player.GetAllPlayers();
                for (int p = 0; p < nearbyPlayers.Count; p++)
                {
                    if (nearbyPlayers[p].IsAttached() &&
                        Vector3.Distance(nearbyPlayers[p].transform.position, chair.m_attachPoint.position) < 0.5f)
                    {
                        occupied = true;
                        break;
                    }
                }
                // Check other companions
                if (!occupied)
                {
                    foreach (var comp in CompanionSetup.AllCompanions)
                    {
                        if (comp == null || comp == _setup) continue;
                        var otherHome = comp.GetComponent<HomesteadController>();
                        if (otherHome != null && otherHome._isSitting && otherHome._chairAttachPoint == chair.m_attachPoint)
                        {
                            occupied = true;
                            break;
                        }
                    }
                }
                if (occupied) continue;

                float dist = Vector3.Distance(transform.position, chair.transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestChair = chair;
                }
            }

            if (bestChair == null) return false;

            _chairAttachPoint = bestChair.m_attachPoint;
            _sitDuration = Random.Range(30f, 90f);
            ResetStuck();
            _phase = HomePhase.MovingToChair;
            Log($"Moving to chair at {bestChair.transform.position:F1}");
            return true;
        }

        private void UpdateMovingToChair(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_chairAttachPoint == null)
            {
                Abort("chair destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _chairAttachPoint.position);

            if (dist <= 2f)
            {
                _ai?.StopMoving();
                SitDown();
                return;
            }

            _ai?.MoveToPoint(dt, _chairAttachPoint.position, 1.5f, dist > 8f);
            UpdateStuck(dt, "chair", dist);
        }

        private void SitDown()
        {
            if (_chairAttachPoint == null) { Abort("no attach point"); return; }

            transform.position = _chairAttachPoint.position;
            transform.rotation = _chairAttachPoint.rotation;

            if (_zanim != null) _zanim.SetBool("attach_chair", true);
            if (_body != null) _body.useGravity = false;
            _isSitting = true;
            _sitTimer = _sitDuration;
            _phase = HomePhase.Sitting;
            Log($"Sat down for {_sitDuration:F0}s");
        }

        private void UpdateSitting(float dt)
        {
            if (_chairAttachPoint == null)
            {
                StandUp();
                Abort("chair destroyed while sitting");
                return;
            }

            // Keep snapped to chair
            transform.position = _chairAttachPoint.position;
            transform.rotation = _chairAttachPoint.rotation;
            if (_body != null)
            {
                _body.velocity = Vector3.zero;
                _body.angularVelocity = Vector3.zero;
            }

            // Freeze AI
            if (_ai != null) _ai.FreezeTimer = 0.5f;

            _sitTimer -= dt;
            if (_sitTimer <= 0f)
            {
                StandUp();
                _sitCooldown = Random.Range(60f, 180f);
                _phase = HomePhase.Idle;
                _idleTimer = Random.Range(IdleDelayMin, IdleDelayMax);
                Log("Stood up from chair");
            }
        }

        private void StandUp()
        {
            if (!_isSitting) return;
            _isSitting = false;
            if (_zanim != null)
                _zanim.SetBool("attach_chair", false);
            if (_body != null) _body.useGravity = true;
            // Offset up slightly to avoid clipping
            transform.position += Vector3.up * 0.5f;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Use Station (cosmetic)
        // ═════════════════════════════════════════════════════════════════════

        private bool TryStartStation(Vector3 homePos)
        {
            var allStations = ReflectionHelper.GetAllCraftingStations();
            if (allStations == null) return false;

            CraftingStation best = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < allStations.Count; i++)
            {
                var station = allStations[i];
                if (station == null) continue;
                // Skip cauldrons — they're for cooking
                if (station.m_name != null && station.m_name.ToLower().Contains("cauldron")) continue;
                float dist = Vector3.Distance(homePos, station.transform.position);
                if (dist > ScanRadius) continue;
                if (IsBlacklisted(station.gameObject)) continue;

                float dFromMe = Vector3.Distance(transform.position, station.transform.position);
                if (dFromMe < bestDist)
                {
                    best = station;
                    bestDist = dFromMe;
                }
            }

            if (best == null) return false;

            _targetStation = best;
            _stationDuration = Random.Range(10f, 20f);
            ResetStuck();
            _phase = HomePhase.MovingToStation;
            Log($"Moving to station \"{best.m_name}\" at {best.transform.position:F1}");
            return true;
        }

        private void UpdateMovingToStation(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetStation == null)
            {
                Abort("station destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetStation.transform.position);

            if (dist <= UseDistance)
            {
                _ai?.StopMoving();
                _ai?.LookAtPoint(_targetStation.transform.position);
                _actionTimer = _stationDuration;
                _phase = HomePhase.UsingStation;
                Log($"Using station \"{_targetStation.m_name}\"");
                return;
            }

            _ai?.MoveToPoint(dt, _targetStation.transform.position, UseDistance, dist > 8f);
            UpdateStuck(dt, "station", dist);
        }

        private void UpdateUsingStation(float dt)
        {
            if (_targetStation == null)
            {
                Abort("station destroyed while using");
                return;
            }

            _ai?.StopMoving();
            _ai?.LookAtPoint(_targetStation.transform.position);

            _actionTimer -= dt;
            if (_actionTimer <= 0f)
            {
                _stationCooldown = Random.Range(60f, 120f);
                _targetStation = null;
                _phase = HomePhase.Idle;
                _idleTimer = Random.Range(IdleDelayMin, IdleDelayMax);
                Log("Finished using station");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Repair Buildings
        // ═════════════════════════════════════════════════════════════════════

        private bool TryStartRepair(Vector3 homePos)
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return false;
            if (FindHammerInInventory(inv) == null)
            {
                if (!_lastHammerMissing)
                {
                    Log("No hammer — skipping repair");
                    _lastHammerMissing = true;
                }
                return false;
            }
            _lastHammerMissing = false;

            var allWNT = WearNTear.GetAllInstances();
            if (allWNT == null) return false;

            WearNTear bestPiece = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < allWNT.Count; i++)
            {
                var wnt = allWNT[i];
                if (wnt == null) continue;
                var piece = wnt.GetComponent<Piece>();
                if (piece == null || !piece.IsPlacedByPlayer()) continue;
                float dist = Vector3.Distance(homePos, wnt.transform.position);
                if (dist > ScanRadius) continue;
                float hp = wnt.GetHealthPercentage();
                if (hp >= 1f) continue;
                if (IsBlacklisted(wnt.gameObject)) continue;
                if (s_claimedPieces.Contains(wnt.GetInstanceID())) continue;

                if (dist < bestDist)
                {
                    bestPiece = wnt;
                    bestDist = dist;
                }
            }

            if (bestPiece == null) return false;

            _targetPiece = bestPiece;
            ClaimPiece(bestPiece.GetInstanceID());
            ResetStuck();
            _phase = HomePhase.MovingToRepairTarget;
            Log($"Repairing \"{bestPiece.GetComponent<Piece>()?.m_name ?? bestPiece.name}\" ({bestDist:F1}m)");
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_repair"), "Repair");
            return true;
        }

        private void UpdateMovingToRepair(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetPiece == null) { Abort("repair target destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _targetPiece.transform.position);

            if (dist <= UseDistance)
            {
                _ai?.StopMoving();
                _ai?.LookAtPoint(_targetPiece.transform.position);

                _hammerEquipped = false;
                var cInv = _humanoid?.GetInventory();
                if (cInv != null)
                {
                    var hammer = FindHammerInInventory(cInv);
                    if (hammer != null)
                    {
                        _prevRightItem = ReflectionHelper.GetRightItem(_humanoid);
                        if (_prevRightItem != null && _prevRightItem != hammer)
                            _humanoid.UnequipItem(_prevRightItem, false);
                        _humanoid.EquipItem(hammer, true);
                        _hammerEquipped = true;
                    }
                }

                _actionTimer = RepairDelay;
                _phase = HomePhase.Repairing;
                return;
            }

            _ai?.MoveToPoint(dt, _targetPiece.transform.position, UseDistance, dist > 8f);
            UpdateStuck(dt, "repair", dist);
        }

        private void UpdateRepairing(float dt)
        {
            if (_targetPiece == null) { Abort("repair target destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _targetPiece.transform.position);
            if (dist > UseDistance * 2f) { Abort("drifted from repair"); return; }

            _ai?.LookAtPoint(_targetPiece.transform.position);

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;
            _actionTimer = RepairDelay;

            // Play hammer animation
            if (_zanim != null) _zanim.SetTrigger("swing_pickaxe");

            _targetPiece.Repair();
            var piece = _targetPiece.GetComponent<Piece>();
            if (piece?.m_placeEffect != null)
                piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation);

            if (_targetPiece.GetHealthPercentage() >= 1f)
            {
                if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_repaired"), "Repair");
                RestoreWeaponAfterRepair();
                UnclaimPiece();
                _targetPiece = null;
                _repairCooldown = 5f;
                _phase = HomePhase.Idle;
                _idleTimer = 2f;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Refuel Fires
        // ═════════════════════════════════════════════════════════════════════

        private bool TryStartRefuel(Vector3 homePos)
        {
            _tempPieces.Clear();
            Piece.GetAllPiecesInRadius(homePos, ScanRadius, _tempPieces);

            Fireplace bestFire = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < _tempPieces.Count; i++)
            {
                var fp = _tempPieces[i]?.GetComponent<Fireplace>();
                if (fp == null || !fp.m_canRefill || fp.m_infiniteFuel || fp.m_fuelItem == null) continue;

                var fpNview = fp.GetComponent<ZNetView>();
                if (fpNview?.GetZDO() == null) continue;

                float fuel = fpNview.GetZDO().GetFloat(ZDOVars.s_fuel);
                if (fp.m_maxFuel <= 0f) continue;
                if (fuel / fp.m_maxFuel >= FuelThreshold) continue;
                if (IsBlacklisted(fp.gameObject)) continue;
                if (s_claimedPieces.Contains(fp.GetInstanceID())) continue;

                float dist = Vector3.Distance(transform.position, fp.transform.position);
                if (dist < bestDist)
                {
                    bestFire = fp;
                    bestDist = dist;
                }
            }

            if (bestFire == null) return false;

            _targetFireplace = bestFire;
            ClaimPiece(bestFire.GetInstanceID());
            _fuelItemPrefab = bestFire.m_fuelItem.gameObject.name;

            var fireNview = bestFire.GetComponent<ZNetView>();
            float curFuel = fireNview.GetZDO().GetFloat(ZDOVars.s_fuel);
            _fuelToAdd = Mathf.Max(1, (int)(bestFire.m_maxFuel - curFuel));

            // Check if already carrying fuel
            var inv = _humanoid?.GetInventory();
            int carried = inv != null ? CountItemInInventory(inv, _fuelItemPrefab) : 0;

            if (carried > 0)
            {
                _fuelToAdd = Mathf.Min(_fuelToAdd, carried);
                ResetStuck();
                _phase = HomePhase.MovingToFireplace;
            }
            else
            {
                ScanNearbyChests(homePos);
                _supplyChest = FindChestWithItem(_fuelItemPrefab);
                if (_supplyChest == null)
                {
                    _targetFireplace = null;
                    UnclaimPiece();
                    return false;
                }
                ResetStuck();
                _phase = HomePhase.MovingToSupplyChest;
            }

            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_refuel"), "Action");
            return true;
        }

        private void UpdateMovingToChest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;
            if (_supplyChest == null) { Abort("chest destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _supplyChest.transform.position);

            if (dist <= UseDistance)
            {
                _ai?.StopMoving();
                _ai?.LookAtPoint(_supplyChest.transform.position);
                _chestOpened = false;
                _phase = HomePhase.TakingFuel;
                return;
            }

            _ai?.MoveToPoint(dt, _supplyChest.transform.position, UseDistance, dist > 8f);
            UpdateStuck(dt, "supply chest", dist);
        }

        private void UpdateTakingFuel(float dt)
        {
            if (_supplyChest == null) { CloseAnyChest(); Abort("chest destroyed"); return; }

            if (!_chestOpened)
            {
                _supplyChest.SetInUse(true);
                _chestOpened = true;
                _actionTimer = ChestOpenDelay;
                if (_zanim != null) _zanim.SetTrigger("interact");
                return;
            }

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;
            _actionTimer = 0.6f;

            var chestInv = _supplyChest.GetInventory();
            var compInv = _humanoid?.GetInventory();
            if (chestInv == null || compInv == null) { CloseChest(_supplyChest); Abort("inv null"); return; }

            var item = FindItemByPrefab(chestInv, _fuelItemPrefab);
            if (item == null || _fuelToAdd <= 0)
            {
                CloseChest(_supplyChest);
                int carried = CountItemInInventory(compInv, _fuelItemPrefab);
                if (carried == 0) { Abort("no fuel taken"); return; }
                _fuelToAdd = Mathf.Min(_fuelToAdd, carried);
                ResetStuck();
                _phase = HomePhase.MovingToFireplace;
                return;
            }

            if (TransferOne(chestInv, compInv, item))
                _fuelToAdd--;
            else
            {
                CloseChest(_supplyChest);
                int carried = CountItemInInventory(compInv, _fuelItemPrefab);
                if (carried > 0) { _fuelToAdd = carried; ResetStuck(); _phase = HomePhase.MovingToFireplace; }
                else Abort("inv full");
            }
        }

        private void UpdateMovingToFire(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;
            if (_targetFireplace == null) { Abort("fire destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _targetFireplace.transform.position);

            if (dist <= UseDistance)
            {
                _ai?.StopMoving();
                _ai?.LookAtPoint(_targetFireplace.transform.position);
                _actionTimer = FuelAddDelay;
                _phase = HomePhase.Refueling;
                return;
            }

            _ai?.MoveToPoint(dt, _targetFireplace.transform.position, UseDistance, dist > 8f);
            UpdateStuck(dt, "fire", dist);
        }

        private void UpdateRefueling(float dt)
        {
            if (_targetFireplace == null) { Abort("fire destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _targetFireplace.transform.position);
            if (dist > UseDistance * 2f) { Abort("drifted from fire"); return; }

            _ai?.LookAtPoint(_targetFireplace.transform.position);
            _actionTimer -= dt;
            if (_actionTimer > 0f) return;
            _actionTimer = FuelAddDelay;

            var fpNview = _targetFireplace.GetComponent<ZNetView>();
            if (fpNview?.GetZDO() == null) { Abort("fire nview lost"); return; }

            float curFuel = fpNview.GetZDO().GetFloat(ZDOVars.s_fuel);
            if (curFuel >= _targetFireplace.m_maxFuel) { FinishRefuel(); return; }

            var inv = _humanoid?.GetInventory();
            if (inv == null || !ConsumeOneFromInventory(inv, _fuelItemPrefab)) { FinishRefuel(); return; }

            if (_zanim != null) _zanim.SetTrigger("interact");
            fpNview.InvokeRPC("RPC_AddFuel");
            _fuelToAdd--;

            if (_fuelToAdd <= 0) FinishRefuel();
        }

        private void FinishRefuel()
        {
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_stoked"), "Action");
            UnclaimPiece();
            _targetFireplace = null;
            _supplyChest = null;
            _fuelItemPrefab = null;
            _refuelCooldown = 30f;
            _phase = HomePhase.Idle;
            _idleTimer = 2f;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Sort Chest (in-place)
        // ═════════════════════════════════════════════════════════════════════

        private bool TryStartSort(Vector3 homePos)
        {
            ScanNearbyChests(homePos);
            if (_nearbyChests.Count == 0) return false;

            // Pick a random chest with 2+ items to sort
            var candidates = new List<Container>();
            for (int i = 0; i < _nearbyChests.Count; i++)
            {
                var inv = _nearbyChests[i].GetInventory();
                if (inv != null && inv.GetAllItems().Count >= 2)
                    candidates.Add(_nearbyChests[i]);
            }
            if (candidates.Count == 0) return false;

            _sortChest = candidates[Random.Range(0, candidates.Count)];
            ResetStuck();
            _phase = HomePhase.MovingToSortChest;
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_tidy"), "Action");
            Log($"Moving to sort chest at {_sortChest.transform.position:F1}");
            return true;
        }

        private void UpdateMovingToSortChest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;
            if (_sortChest == null) { Abort("sort chest destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _sortChest.transform.position);

            if (dist <= UseDistance)
            {
                _ai?.StopMoving();
                _ai?.LookAtPoint(_sortChest.transform.position);
                _chestOpened = false;
                _phase = HomePhase.SortingChest;
                return;
            }

            _ai?.MoveToPoint(dt, _sortChest.transform.position, UseDistance, dist > 8f);
            UpdateStuck(dt, "sort chest", dist);
        }

        private void UpdateSortingChest(float dt)
        {
            if (_sortChest == null) { CloseAnyChest(); Abort("sort chest destroyed"); return; }

            if (!_chestOpened)
            {
                _sortChest.SetInUse(true);
                _chestOpened = true;
                _actionTimer = ChestOpenDelay;
                if (_zanim != null) _zanim.SetTrigger("interact");
                return;
            }

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;

            // Sort items within the chest
            SortInventoryInPlace(_sortChest.GetInventory());

            CloseChest(_sortChest);
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_tidied"), "Action");
            _sortChest = null;
            _sortCooldown = 120f;
            _phase = HomePhase.Idle;
            _idleTimer = Random.Range(IdleDelayMin, IdleDelayMax);
            Log("Chest sorted");
        }

        /// <summary>Sort items within a chest by type then name.</summary>
        private void SortInventoryInPlace(Inventory inv)
        {
            if (inv == null) return;
            var items = inv.GetAllItems();
            if (items.Count < 2) return;

            int width = inv.GetWidth();

            // Sort by type priority, then alphabetically
            items.Sort((a, b) =>
            {
                int ta = GetSortPriority(a);
                int tb = GetSortPriority(b);
                if (ta != tb) return ta.CompareTo(tb);
                return string.Compare(a.m_shared.m_name, b.m_shared.m_name, System.StringComparison.Ordinal);
            });

            // Reassign grid positions
            for (int i = 0; i < items.Count; i++)
            {
                items[i].m_gridPos = new Vector2i(i % width, i / width);
            }

            inv.m_onChanged?.Invoke();
        }

        private static int GetSortPriority(ItemDrop.ItemData item)
        {
            switch (item.m_shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                    return 0; // Weapons
                case ItemDrop.ItemData.ItemType.Shield:
                    return 1;
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Shoulder:
                    return 2; // Armor
                case ItemDrop.ItemData.ItemType.Consumable:
                    return 3;
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
                    return 4;
                case ItemDrop.ItemData.ItemType.Trophy:
                    return 6;
                case ItemDrop.ItemData.ItemType.Tool:
                    return 5;
                default:
                    return 5; // Materials/misc
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Cook Meals
        // ═════════════════════════════════════════════════════════════════════

        private bool IsCompanionHungry()
        {
            if (_food == null) return false;
            // Check if any food slot is empty or below 50%
            for (int i = 0; i < CompanionFood.MaxFoodSlots; i++)
            {
                var slot = _food.GetFood(i);
                if (!slot.IsActive) return true;
                if (slot.RemainingTime < slot.TotalTime * 0.5f) return true;
            }
            return false;
        }

        private bool TryStartCook(Vector3 homePos)
        {
            // Find a Cauldron
            var allStations = ReflectionHelper.GetAllCraftingStations();
            if (allStations == null) return false;

            CraftingStation cauldron = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < allStations.Count; i++)
            {
                var station = allStations[i];
                if (station == null || station.m_name == null) continue;
                if (!station.m_name.ToLower().Contains("cauldron")) continue;
                float dist = Vector3.Distance(homePos, station.transform.position);
                if (dist > ScanRadius) continue;

                float dFromMe = Vector3.Distance(transform.position, station.transform.position);
                if (dFromMe < bestDist)
                {
                    cauldron = station;
                    bestDist = dFromMe;
                }
            }

            if (cauldron == null) return false;

            // Scan chests for ingredients
            ScanNearbyChests(homePos);
            if (_nearbyChests.Count == 0) return false;

            // Find a viable food recipe
            var recipes = ObjectDB.instance?.m_recipes;
            if (recipes == null) return false;

            var viableRecipes = new List<Recipe>();
            for (int i = 0; i < recipes.Count; i++)
            {
                var recipe = recipes[i];
                if (recipe == null || !recipe.m_enabled || recipe.m_item == null) continue;
                if (recipe.m_craftingStation == null) continue;
                if (recipe.m_craftingStation.m_name != cauldron.m_name) continue;

                // Must produce food
                var shared = recipe.m_item.m_itemData?.m_shared;
                if (shared == null) continue;
                if (shared.m_food <= 0f && shared.m_foodStamina <= 0f) continue;

                // Check station level
                if (cauldron.GetLevel() < recipe.GetRequiredStationLevel(1)) continue;

                // Check if all ingredients are available in nearby chests
                if (CanCraftFromChests(recipe)) viableRecipes.Add(recipe);
            }

            if (viableRecipes.Count == 0) return false;

            // Pick random recipe
            _selectedRecipe = viableRecipes[Random.Range(0, viableRecipes.Count)];
            _targetCauldron = cauldron;

            // Build ingredient list
            _ingredientList.Clear();
            foreach (var req in _selectedRecipe.m_resources)
            {
                if (req?.m_resItem == null) continue;
                string prefab = req.m_resItem.gameObject.name;
                int needed = req.GetAmount(1);
                if (needed > 0)
                    _ingredientList.Add((prefab, needed));
            }
            _ingredientIndex = 0;

            Log($"Cooking \"{_selectedRecipe.m_item.m_itemData.m_shared.m_name}\" — {_ingredientList.Count} ingredient types");

            // Start gathering ingredients
            if (!FindNextIngredientChest())
            {
                _selectedRecipe = null;
                _targetCauldron = null;
                return false;
            }

            return true;
        }

        private bool CanCraftFromChests(Recipe recipe)
        {
            foreach (var req in recipe.m_resources)
            {
                if (req?.m_resItem == null) continue;
                string prefab = req.m_resItem.gameObject.name;
                int needed = req.GetAmount(1);
                if (needed <= 0) continue;

                int available = 0;
                for (int c = 0; c < _nearbyChests.Count; c++)
                {
                    var inv = _nearbyChests[c]?.GetInventory();
                    if (inv == null) continue;
                    available += CountItemInInventory(inv, prefab);
                }

                if (available < needed) return false;
            }
            return true;
        }

        private bool FindNextIngredientChest()
        {
            while (_ingredientIndex < _ingredientList.Count)
            {
                var (prefab, needed) = _ingredientList[_ingredientIndex];
                var compInv = _humanoid?.GetInventory();
                int alreadyHave = compInv != null ? CountItemInInventory(compInv, prefab) : 0;

                if (alreadyHave >= needed)
                {
                    _ingredientIndex++;
                    continue;
                }

                // Find chest with this ingredient
                _ingredientChest = FindChestWithItem(prefab);
                if (_ingredientChest == null) { Abort("ingredient chest not found"); return false; }

                ResetStuck();
                _phase = HomePhase.MovingToIngredientChest;
                return true;
            }

            // All ingredients gathered — move to cauldron
            ResetStuck();
            _phase = HomePhase.MovingToCauldron;
            return true;
        }

        private void UpdateMovingToIngredientChest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;
            if (_ingredientChest == null) { Abort("ingredient chest destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _ingredientChest.transform.position);

            if (dist <= UseDistance)
            {
                _ai?.StopMoving();
                _ai?.LookAtPoint(_ingredientChest.transform.position);
                _chestOpened = false;
                _phase = HomePhase.TakingIngredients;
                return;
            }

            _ai?.MoveToPoint(dt, _ingredientChest.transform.position, UseDistance, dist > 8f);
            UpdateStuck(dt, "ingredient chest", dist);
        }

        private void UpdateTakingIngredients(float dt)
        {
            if (_ingredientChest == null) { CloseAnyChest(); Abort("chest destroyed"); return; }

            if (!_chestOpened)
            {
                _ingredientChest.SetInUse(true);
                _chestOpened = true;
                _actionTimer = ChestOpenDelay;
                if (_zanim != null) _zanim.SetTrigger("interact");
                return;
            }

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;
            _actionTimer = 0.6f;

            if (_ingredientIndex >= _ingredientList.Count)
            {
                CloseChest(_ingredientChest);
                ResetStuck();
                _phase = HomePhase.MovingToCauldron;
                return;
            }

            var (prefab, needed) = _ingredientList[_ingredientIndex];
            var compInv = _humanoid?.GetInventory();
            var chestInv = _ingredientChest.GetInventory();
            if (compInv == null || chestInv == null) { CloseChest(_ingredientChest); Abort("inv null"); return; }

            int have = CountItemInInventory(compInv, prefab);
            if (have >= needed)
            {
                // This ingredient is done, move to next
                _ingredientIndex++;
                CloseChest(_ingredientChest);
                FindNextIngredientChest();
                return;
            }

            var item = FindItemByPrefab(chestInv, prefab);
            if (item == null)
            {
                // This chest ran out — try another
                CloseChest(_ingredientChest);
                _ingredientChest = FindChestWithItem(prefab);
                if (_ingredientChest == null)
                {
                    Abort("ran out of ingredient");
                    return;
                }
                ResetStuck();
                _phase = HomePhase.MovingToIngredientChest;
                return;
            }

            TransferOne(chestInv, compInv, item);
        }

        private void UpdateMovingToCauldron(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;
            if (_targetCauldron == null) { Abort("cauldron destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _targetCauldron.transform.position);

            if (dist <= UseDistance)
            {
                _ai?.StopMoving();
                _ai?.LookAtPoint(_targetCauldron.transform.position);
                _actionTimer = 3f; // brief cooking animation time
                _phase = HomePhase.Cooking;
                return;
            }

            _ai?.MoveToPoint(dt, _targetCauldron.transform.position, UseDistance, dist > 8f);
            UpdateStuck(dt, "cauldron", dist);
        }

        private void UpdateCooking(float dt)
        {
            if (_targetCauldron == null) { Abort("cauldron destroyed"); return; }

            _ai?.StopMoving();
            _ai?.LookAtPoint(_targetCauldron.transform.position);

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;

            if (_selectedRecipe == null) { Abort("no recipe"); return; }

            // Consume ingredients from companion inventory
            var inv = _humanoid?.GetInventory();
            if (inv == null) { Abort("inv null"); return; }

            foreach (var req in _selectedRecipe.m_resources)
            {
                if (req?.m_resItem == null) continue;
                string prefab = req.m_resItem.gameObject.name;
                int needed = req.GetAmount(1);
                for (int n = 0; n < needed; n++)
                    ConsumeOneFromInventory(inv, prefab);
            }

            // Add crafted item — if inventory is full, drop on ground
            string mealPrefab = _selectedRecipe.m_item.gameObject.name;
            int amount = _selectedRecipe.m_amount;
            var added = inv.AddItem(mealPrefab, amount, 1, 0, 0L, "");

            string mealName = _selectedRecipe.m_item.m_itemData.m_shared.m_name;
            if (added == null)
            {
                // Inventory full — spawn as world item drop
                var prefabObj = ObjectDB.instance?.GetItemPrefab(mealPrefab);
                if (prefabObj != null)
                {
                    var drop = UnityEngine.Object.Instantiate(prefabObj, transform.position + Vector3.up, Quaternion.identity);
                    var itemDrop = drop.GetComponent<ItemDrop>();
                    if (itemDrop != null) itemDrop.m_itemData.m_stack = amount;
                }
                Log($"Cooked {amount}x \"{mealName}\" (dropped — inv full)");
            }
            else
            {
                Log($"Cooked {amount}x \"{mealName}\"");
            }
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_cooked"), "Action");

            // Play effect
            var player = Player.m_localPlayer;
            if (player != null && _character != null)
                player.m_skillLevelupEffects?.Create(_character.GetHeadPoint(), _character.transform.rotation, _character.transform);

            _selectedRecipe = null;
            _targetCauldron = null;
            _ingredientChest = null;
            _ingredientList.Clear();
            _cookCooldown = 60f;
            _phase = HomePhase.Idle;
            _idleTimer = 5f;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Public API
        // ═════════════════════════════════════════════════════════════════════

        public void CancelDirected()
        {
            if (_phase == HomePhase.Idle) return;
            Abort("cancelled by command");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Helpers
        // ═════════════════════════════════════════════════════════════════════

        private void Abort(string reason)
        {
            Log($"Aborted — {reason} (phase was {_phase})");
            if (_isSitting) StandUp();
            UnclaimPiece();
            CloseAnyChest();
            RestoreWeaponAfterRepair();
            _targetFireplace = null;
            _supplyChest = null;
            _fuelItemPrefab = null;
            _targetPiece = null;
            _sortChest = null;
            _targetStation = null;
            _targetCauldron = null;
            _selectedRecipe = null;
            _ingredientChest = null;
            _ingredientList.Clear();
            _phase = HomePhase.Idle;
            _idleTimer = Random.Range(IdleDelayMin, IdleDelayMax);
        }

        private void ClaimPiece(int id)
        {
            UnclaimPiece();
            _claimedPieceId = id;
            if (_claimedPieceId != 0) s_claimedPieces.Add(_claimedPieceId);
        }

        private void UnclaimPiece()
        {
            if (_claimedPieceId != 0)
            {
                s_claimedPieces.Remove(_claimedPieceId);
                _claimedPieceId = 0;
            }
        }

        private void RestoreWeaponAfterRepair()
        {
            if (!_hammerEquipped) return;
            _hammerEquipped = false;

            var rightItem = ReflectionHelper.GetRightItem(_humanoid);
            if (rightItem?.m_shared?.m_buildPieces != null)
                _humanoid.UnequipItem(rightItem, false);

            if (_prevRightItem != null)
            {
                var inv = _humanoid?.GetInventory();
                if (inv != null && inv.ContainsItem(_prevRightItem))
                    _humanoid.EquipItem(_prevRightItem, true);
                else
                    _setup?.SyncEquipmentToInventory();
            }
            else
            {
                _setup?.SyncEquipmentToInventory();
            }
            _prevRightItem = null;
        }

        private void ScanNearbyChests(Vector3 center)
        {
            _nearbyChests.Clear();
            _tempPieces.Clear();
            Piece.GetAllPiecesInRadius(center, ScanRadius, _tempPieces);

            for (int i = 0; i < _tempPieces.Count; i++)
            {
                var container = _tempPieces[i]?.GetComponentInChildren<Container>();
                if (container == null) continue;
                if (container.GetInventory() == null) continue;
                if (container.gameObject == gameObject) continue;
                if (container.IsInUse()) continue;
                if (IsBlacklisted(container.gameObject)) continue;
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
                if (FindItemByPrefab(inv, prefabName) == null) continue;

                float dist = Vector3.Distance(transform.position, chest.transform.position);
                if (dist < bestDist)
                {
                    best = chest;
                    bestDist = dist;
                }
            }
            return best;
        }

        private void CloseChest(Container chest)
        {
            if (chest != null) chest.SetInUse(false);
            _chestOpened = false;
        }

        private void CloseAnyChest()
        {
            if (!_chestOpened) return;

            switch (_phase)
            {
                case HomePhase.TakingFuel:
                    if (_supplyChest != null) _supplyChest.SetInUse(false);
                    break;
                case HomePhase.SortingChest:
                    if (_sortChest != null) _sortChest.SetInUse(false);
                    break;
                case HomePhase.TakingIngredients:
                    if (_ingredientChest != null) _ingredientChest.SetInUse(false);
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
                Abort($"stuck moving to {target} ({dist:F1}m)");
        }

        private bool IsBlacklisted(GameObject target)
        {
            if (target == null) return false;
            int id = target.GetInstanceID();
            if (!_blacklist.TryGetValue(id, out float expiry)) return false;
            if (Time.time >= expiry) { _blacklist.Remove(id); return false; }
            return true;
        }

        private void PruneBlacklist()
        {
            if (_blacklist.Count == 0) return;
            float now = Time.time;
            _expiredKeys.Clear();
            foreach (var kv in _blacklist)
            {
                if (now >= kv.Value) _expiredKeys.Add(kv.Key);
            }
            for (int i = 0; i < _expiredKeys.Count; i++)
                _blacklist.Remove(_expiredKeys[i]);
        }

        private void Log(string msg)
        {
            string name = _character?.m_name ?? "?";
            CompanionsPlugin.Log.LogInfo($"[HomeLife|{name}] {msg}");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Static utility methods (used by CompanionAI Restock mode)
        // ═════════════════════════════════════════════════════════════════════

        internal static ItemDrop.ItemData FindItemByPrefab(Inventory inv, string prefabName)
        {
            var items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].m_dropPrefab != null && items[i].m_dropPrefab.name == prefabName)
                    return items[i];
            }
            return null;
        }

        internal static bool TransferOne(Inventory source, Inventory dest, ItemDrop.ItemData item)
        {
            var clone = item.Clone();
            clone.m_stack = 1;
            if (!dest.CanAddItem(clone, 1)) return false;
            if (!dest.AddItem(clone)) return false;
            source.RemoveItem(item, 1);
            return true;
        }

        internal static int CountItemInInventory(Inventory inv, string prefabName)
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

        internal static bool ConsumeOneFromInventory(Inventory inv, string prefabName)
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

        internal static ItemDrop.ItemData FindHammerInInventory(Inventory inv)
        {
            var items = inv.GetAllItems();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i]?.m_shared?.m_buildPieces != null)
                    return items[i];
            }
            return null;
        }
    }
}
