using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Companion farming controller. Autonomously harvests ripe crops, collects
    /// drops, takes seeds from chests, and plants them on cultivated soil in an
    /// organized grid. The companion must have a cultivator in inventory to plant.
    ///
    /// State machine: Idle → Scanning → MovingToPickable → Harvesting →
    ///     CollectingDrops → MovingToSeedChest → TakingSeeds →
    ///     MovingToPlantSpot → Planting → MovingToOutputChest → StoringOutput
    /// </summary>
    public class FarmController : MonoBehaviour
    {
        internal enum FarmPhase
        {
            Idle,
            Scanning,
            MovingToPickable,
            Harvesting,
            CollectingDrops,
            MovingToSeedChest,
            TakingSeeds,
            MovingToPlantSpot,
            Planting,
            MovingToOutputChest,
            StoringOutput
        }

        internal FarmPhase Phase => _phase;
        public bool IsActive => _phase != FarmPhase.Idle;

        // ── Seed → Plant mapping ─────────────────────────────────────────────

        internal struct SeedPlantInfo
        {
            public string SeedPrefabName;    // e.g. "CarrotSeeds"
            public GameObject PlantPrefab;    // sapling prefab to instantiate
            public float GrowRadius;          // spacing between plants
            public bool NeedsCultivated;      // must be on cultivated ground
            public Heightmap.Biome Biome;     // required biome mask
        }

        private static Dictionary<string, SeedPlantInfo> s_seedToPlant;
        private static HashSet<string> s_cropOutputNames;  // e.g. "Carrot", "Turnip" — items dropped by grown crops
        private static bool s_mappingBuilt;

        // ── Components ──────────────────────────────────────────────────────
        private CompanionAI       _ai;
        private Humanoid          _humanoid;
        private Character         _character;
        private CompanionSetup    _setup;
        private ZNetView          _nview;
        private ZSyncAnimation    _zanim;
        private CompanionTalk     _talk;
        private DoorHandler       _doorHandler;
        private HarvestController _harvest;
        private SmeltController   _smelt;
        private RepairController  _repair;

        // ── State ───────────────────────────────────────────────────────────
        private FarmPhase   _phase;
        private float       _scanTimer;
        private float       _actionTimer;
        private float       _stuckTimer;
        private float       _stuckCheckTimer;
        private Vector3     _stuckCheckPos;
        private bool        _chestOpened;
        private bool        _allDoneNotified;
        private float       _moveLogTimer;
        private bool        _shouldRun;        // run decision from movement — LateUpdate respects this

        // ── Harvest/Plant rotation ─────────────────────────────────────────
        // Forces alternation between harvesting and planting on a 30s cycle
        // so the companion doesn't spend all its time on one task.
        private bool        _preferHarvest = true;  // current rotation phase
        private float       _rotationTimer;          // time spent in current phase
        private const float RotationInterval = 30f;  // switch every 30 seconds

        // Shared across all FarmController instances — prevents multiple companions targeting same crop
        private static readonly HashSet<int> s_claimedPickables = new HashSet<int>();
        private int _claimedPickableId;

        // Current task tracking
        private Pickable    _targetPickable;
        private Container   _targetChest;
        private Container   _outputChest;
        private Vector3     _plantPosition;
        private SeedPlantInfo _plantInfo;
        private string      _seedPrefabToTake;  // prefab name of seed to take from chest
        private int         _seedAmountToTake;

        // ── Scan buffers ─────────────────────────────────────────────────────
        private readonly Collider[] _spacingBuffer    = new Collider[64];
        private readonly Collider[] _dropBuffer       = new Collider[64];
        private readonly Collider[] _pickableScanBuffer = new Collider[512];
        private readonly List<Piece> _tempPieces      = new List<Piece>();
        private readonly List<Container> _nearbyChests = new List<Container>();

        // ── Layer masks ──────────────────────────────────────────────────────
        private int _spaceMask;      // for plant spacing checks
        private int _itemMask;       // for drop collection
        private int _pickableMask;   // for ripe pickable scanning

        // ── Config ──────────────────────────────────────────────────────────
        private static float ScanRadius    => ModConfig.FarmScanRadius.Value;
        private static float ScanInterval  => ModConfig.FarmScanInterval.Value;
        private static float PlantSpacing  => ModConfig.FarmPlantSpacing.Value;
        private static float UseDistance   => ModConfig.FarmUseDistance.Value;
        private const float MoveTimeout   = 10f;
        private const float StuckCheckPeriod = 1f;
        private const float StuckMinDistance = 0.5f;

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
            _doorHandler = GetComponent<DoorHandler>();
            _harvest   = GetComponent<HarvestController>();
            _smelt     = GetComponent<SmeltController>();
            _repair    = GetComponent<RepairController>();

            _spaceMask    = LayerMask.GetMask("Default", "static_solid", "Default_small",
                                              "piece", "piece_nonsolid");
            _itemMask     = LayerMask.GetMask("item");
            _pickableMask = LayerMask.GetMask("Default", "static_solid", "Default_small",
                                              "piece", "piece_nonsolid");
        }

        private void Update()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

            if (ShouldAbort())
            {
                if (_phase != FarmPhase.Idle)
                    Abort("interrupted");
                return;
            }

            if (!IsFarmMode())
            {
                if (_phase != FarmPhase.Idle)
                    Abort("left farm mode");
                return;
            }

            // Don't run while other controllers are active
            if ((_harvest != null && _harvest.IsActive) ||
                (_smelt != null && _smelt.IsActive) ||
                (_repair != null && _repair.IsActive))
                return;

            float dt = Time.deltaTime;

            switch (_phase)
            {
                case FarmPhase.Idle:              UpdateIdle(dt);                break;
                case FarmPhase.Scanning:          UpdateScanning();              break;
                case FarmPhase.MovingToPickable:   UpdateMovingToPickable(dt);   break;
                case FarmPhase.Harvesting:         UpdateHarvesting();           break;
                case FarmPhase.CollectingDrops:    UpdateCollectingDrops();      break;
                case FarmPhase.MovingToSeedChest:  UpdateMovingToSeedChest(dt);  break;
                case FarmPhase.TakingSeeds:        UpdateTakingSeeds();          break;
                case FarmPhase.MovingToPlantSpot:  UpdateMovingToPlantSpot(dt);  break;
                case FarmPhase.Planting:           UpdatePlanting();             break;
                case FarmPhase.MovingToOutputChest: UpdateMovingToOutputChest(dt); break;
                case FarmPhase.StoringOutput:      UpdateStoringOutput();        break;
            }
        }

        private void LateUpdate()
        {
            if (_character == null) return;
            // Walk when close, run when far — prevents overshooting targets
            if (_phase == FarmPhase.MovingToPickable ||
                _phase == FarmPhase.MovingToSeedChest ||
                _phase == FarmPhase.MovingToPlantSpot ||
                _phase == FarmPhase.MovingToOutputChest ||
                _phase == FarmPhase.CollectingDrops)
            {
                _character.SetRun(_shouldRun);
            }
        }

        // ── Mode check ──────────────────────────────────────────────────────

        /// <summary>
        /// Called by radial menu when action mode changes. Resets scan timer
        /// so farming starts immediately when farm mode is activated.
        /// </summary>
        public void NotifyActionModeChanged()
        {
            if (IsFarmMode())
            {
                _scanTimer = 0f;
                _allDoneNotified = false;
                _preferHarvest = true;
                _rotationTimer = 0f;
                Log("Mode changed to Farm — scan timer reset, rotation=HARVEST");
            }
        }

        internal bool IsFarmMode()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return false;

            // In homestead mode, farming is managed by the rotation timer
            if (_setup != null && _setup.GetStayHome()
                && _setup.HasHomePosition() && !_setup.GetFollow())
            {
                var homestead = GetComponent<HomesteadController>();
                return homestead != null && homestead.IsFarmTurn;
            }

            return zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                == CompanionSetup.ModeFarm;
        }

        private bool ShouldAbort()
        {
            if (_ai != null && _ai.IsInCombat)
                return true;
            if (CompanionInteractPanel.IsOpenFor(_setup) || CompanionRadialMenu.IsOpenFor(_setup))
                return true;
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  State Machine
        // ══════════════════════════════════════════════════════════════════════

        // ── Idle ────────────────────────────────────────────────────────────

        private void UpdateIdle(float dt)
        {
            _scanTimer -= dt;
            if (_scanTimer > 0f) return;
            _scanTimer = ScanInterval;

            // Rotation timer — switch between harvest-priority and plant-priority
            _rotationTimer += ScanInterval;
            if (_rotationTimer >= RotationInterval)
            {
                _rotationTimer = 0f;
                _preferHarvest = !_preferHarvest;
                Log($"Rotation switch → {(_preferHarvest ? "HARVEST" : "PLANT")} priority");
            }

            _phase = FarmPhase.Scanning;
        }

        // ── Scanning ────────────────────────────────────────────────────────

        private void UpdateScanning()
        {
            // Ensure seed mapping is built
            BuildSeedMapping();

            Log($"Scanning — mode={(_preferHarvest ? "HARVEST" : "PLANT")} " +
                $"rotTimer={_rotationTimer:F0}s cropNames={s_cropOutputNames?.Count ?? 0} " +
                $"seedMap={s_seedToPlant?.Count ?? 0} pos={transform.position:F1}");

            // Always store crop output first (clears inventory space)
            if (TryScanStore()) return;

            // Rotation: alternate between harvest-first and plant-first every 30s
            if (_preferHarvest)
            {
                if (TryScanHarvest()) return;
                if (TryScanPlant())   return;
            }
            else
            {
                if (TryScanPlant())   return;
                if (TryScanHarvest()) return;
            }

            // Nothing to do
            if (!_allDoneNotified)
            {
                _allDoneNotified = true;
                Log("Nothing to farm — idle (30s backoff)");
                if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_farm_done"), "Farm");
                RestoreFollow();
            }
            _phase = FarmPhase.Idle;
            _scanTimer = 30f; // long backoff
        }

        /// <summary>Try to find and harvest a ripe crop. Returns true if task started.</summary>
        private bool TryScanHarvest()
        {
            var pickable = FindRipePickable();
            if (pickable != null)
            {
                _targetPickable = pickable;
                ClaimPickable();
                ClearFollowForMovement();
                ResetStuck();
                _phase = FarmPhase.MovingToPickable;
                if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_farm_harvesting"), "Farm");
                Log($"[Harvest] Found ripe crop \"{pickable.name}\" at " +
                    $"{pickable.transform.position:F1} dist={Vector3.Distance(transform.position, pickable.transform.position):F1}m");
                return true;
            }
            Log("[Harvest] No ripe crops found");
            return false;
        }

        /// <summary>Try to store crop output in a nearby chest. Returns true if task started.</summary>
        private bool TryScanStore()
        {
            bool hasCropOut = HasCropOutput();
            if (hasCropOut)
            {
                ScanNearbyChests();
                var chest = FindChestWithSpace();
                if (chest != null)
                {
                    _outputChest = chest;
                    ClearFollowForMovement();
                    ResetStuck();
                    _phase = FarmPhase.MovingToOutputChest;
                    Log($"[Store] Have crop output — storing in chest at {chest.transform.position:F1}");
                    return true;
                }
                Log("[Store] Have crop output but no chest with space found");
            }
            else
            {
                Log("[Store] No crop output in inventory");
            }
            return false;
        }

        /// <summary>Try to plant seeds or fetch seeds from chest. Returns true if task started.</summary>
        private bool TryScanPlant()
        {
            var cultivator = FindCultivator();
            if (cultivator == null)
            {
                if (!_allDoneNotified)
                {
                    Log("No cultivator in inventory — harvest-only mode");
                    if (_talk != null) _talk.Say(
                        ModLocalization.Loc("hc_speech_farm_no_cultivator"), "Farm");
                }
                return false;
            }

            // Seeds in inventory + empty cultivated spots → plant
            var seedInfo = FindPlantableSeedInInventory(cultivator);
            if (seedInfo.HasValue)
            {
                var info = seedInfo.Value;
                Vector3 plantPos;
                if (FindPlantPosition(info, out plantPos))
                {
                    _plantInfo = info;
                    _plantPosition = plantPos;
                    ClearFollowForMovement();
                    ResetStuck();
                    _phase = FarmPhase.MovingToPlantSpot;
                    if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_farm_planting"), "Farm");
                    Log($"[Plant] Planting \"{info.SeedPrefabName}\" at {plantPos:F1}");
                    return true;
                }
                Log($"[Plant] Have seed \"{info.SeedPrefabName}\" but no valid plant position");
            }
            else
            {
                Log("[Plant] No plantable seeds in inventory");
            }

            // Seeds in nearby chest + empty spots → fetch seeds
            ScanNearbyChests();
            var seedChest = FindChestWithSeeds(cultivator, out SeedPlantInfo chestSeedInfo,
                out string seedPrefab, out int seedCount);
            if (seedChest != null)
            {
                Vector3 plantPos;
                if (FindPlantPosition(chestSeedInfo, out plantPos))
                {
                    _targetChest = seedChest;
                    _seedPrefabToTake = seedPrefab;
                    _seedAmountToTake = Mathf.Min(seedCount, 20);
                    ClearFollowForMovement();
                    ResetStuck();
                    _phase = FarmPhase.MovingToSeedChest;
                    Log($"[Fetch] Fetching {_seedAmountToTake}x \"{seedPrefab}\" from chest");
                    return true;
                }
                Log($"[Fetch] Found seeds \"{seedPrefab}\" in chest but no plant position");
            }
            else
            {
                Log("[Fetch] No seeds in nearby chests");
            }

            return false;
        }

        // ── Moving to pickable ──────────────────────────────────────────────

        private void UpdateMovingToPickable(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetPickable == null || IsPicked(_targetPickable))
            {
                Abort("pickable gone or already picked");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetPickable.transform.position);
            if (dist < UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_targetPickable.transform.position);
                _phase = FarmPhase.Harvesting;
                _actionTimer = 0f;
                Log($"At pickable (dist={dist:F1}m) — harvesting");
                return;
            }

            _shouldRun = dist > 8f;
            bool moveOk = _ai.MoveToPoint(dt, _targetPickable.transform.position, UseDistance, _shouldRun);
            LogMovement(dt, "pickable", dist, moveOk);
            UpdateStuckDetection(dt, "pickable");
        }

        // ── Harvesting ──────────────────────────────────────────────────────

        private void UpdateHarvesting()
        {
            if (_targetPickable == null)
            {
                _phase = FarmPhase.Scanning;
                return;
            }

            // Step 1: Interact with the pickable
            if (_actionTimer == 0f)
            {
                _targetPickable.Interact(_humanoid, false, false);
                _actionTimer = 1.5f; // wait for drops to spawn
                Log($"Interacted with \"{_targetPickable.name}\"");
                return;
            }

            // Step 2: Wait for drops
            _actionTimer -= Time.deltaTime;
            if (_actionTimer > 0f) return;

            // Step 3: Collect drops near the pickable position
            _phase = FarmPhase.CollectingDrops;
            _actionTimer = 0.5f; // brief delay then collect
        }

        // ── Collecting drops ────────────────────────────────────────────────

        private const float DropPickupDelay = 0.4f;     // seconds between picking up each drop
        private const float PostHarvestCooldown = 2.0f;  // seconds of idle after collecting all drops

        private void UpdateCollectingDrops()
        {
            _actionTimer -= Time.deltaTime;
            if (_actionTimer > 0f) return;

            Vector3 pickPos = _targetPickable != null
                ? _targetPickable.transform.position
                : transform.position;

            // Pick up ONE drop at a time for a natural look
            if (PickUpOneDrop(pickPos))
            {
                _actionTimer = DropPickupDelay; // wait before picking up the next one
                return;
            }

            // No more drops — finish harvest with a cooldown before next scan
            _targetPickable = null;
            _phase = FarmPhase.Idle;
            _scanTimer = PostHarvestCooldown;
            Log($"Drops collected — idle for {PostHarvestCooldown}s before next scan");
        }

        // ── Moving to seed chest ────────────────────────────────────────────

        private void UpdateMovingToSeedChest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetChest == null)
            {
                Abort("seed chest destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetChest.transform.position);
            if (dist < UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_targetChest.transform.position);
                _chestOpened = false;
                _phase = FarmPhase.TakingSeeds;
                Log($"At seed chest (dist={dist:F1}m) — taking seeds");
                return;
            }

            _shouldRun = dist > 8f;
            bool moveOk = _ai.MoveToPoint(dt, _targetChest.transform.position, UseDistance, _shouldRun);
            LogMovement(dt, "seed chest", dist, moveOk);
            UpdateStuckDetection(dt, "seed chest");
        }

        // ── Taking seeds from chest ─────────────────────────────────────────

        private void UpdateTakingSeeds()
        {
            if (_targetChest == null)
            {
                if (_chestOpened) _chestOpened = false;
                Abort("chest destroyed while taking seeds");
                return;
            }

            // Step 1: Open chest
            if (!_chestOpened)
            {
                _targetChest.SetInUse(true);
                _chestOpened = true;
                _actionTimer = 0.8f;
                Log("Opened seed chest — waiting for animation");
                return;
            }

            // Step 2: Wait for animation
            _actionTimer -= Time.deltaTime;
            if (_actionTimer > 0f) return;

            // Step 3: Take seeds
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
            for (int i = allItems.Count - 1; i >= 0 && taken < _seedAmountToTake; i--)
            {
                var item = allItems[i];
                if (item?.m_dropPrefab == null) continue;
                if (item.m_dropPrefab.name != _seedPrefabToTake) continue;

                int toTake = Mathf.Min(item.m_stack, _seedAmountToTake - taken);
                if (!companionInv.HaveEmptySlot() &&
                    companionInv.GetItem(item.m_shared.m_name) == null)
                {
                    Log($"  No inventory space — stopping at {taken} seeds");
                    break;
                }

                chestInv.RemoveItem(item, toTake);
                companionInv.AddItem(
                    item.m_dropPrefab.name, toTake, item.m_quality,
                    item.m_variant, 0L, "");
                taken += toTake;
                Log($"  Took {toTake}x \"{_seedPrefabToTake}\" (total={taken})");
            }

            _targetChest.SetInUse(false);
            _chestOpened = false;

            if (taken == 0)
            {
                Log("Failed to take seeds — back to scanning");
                _phase = FarmPhase.Scanning;
                return;
            }

            // Go back to scanning which will pick up "seeds in inventory" path
            _phase = FarmPhase.Scanning;
            _scanTimer = 0f;
        }

        // ── Moving to plant spot ────────────────────────────────────────────

        private void UpdateMovingToPlantSpot(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            float dist = Vector3.Distance(transform.position, _plantPosition);
            if (dist < UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_plantPosition);
                _phase = FarmPhase.Planting;
                _actionTimer = 0f;
                Log($"At plant spot (dist={dist:F1}m) — planting");
                return;
            }

            _shouldRun = dist > 8f;
            bool moveOk = _ai.MoveToPoint(dt, _plantPosition, UseDistance, _shouldRun);
            LogMovement(dt, "plant spot", dist, moveOk);
            UpdateStuckDetection(dt, "plant spot");
        }

        // ── Planting ────────────────────────────────────────────────────────

        private void UpdatePlanting()
        {
            // Step 1: Equip cultivator and play attack animation
            if (_actionTimer == 0f)
            {
                var cultivator = FindCultivator();
                if (cultivator == null)
                {
                    Log("Lost cultivator during plant — aborting");
                    _phase = FarmPhase.Scanning;
                    return;
                }

                EquipCultivator(cultivator);

                // Play attack animation
                if (_zanim != null)
                    _zanim.SetTrigger("attack");

                _actionTimer = 0.8f; // wait for animation
                return;
            }

            // Step 2: Wait for animation
            _actionTimer -= Time.deltaTime;
            if (_actionTimer > 0f) return;

            // Step 3: Instantiate the plant
            PlantSeed(_plantInfo, _plantPosition);

            // Check for more seeds of the same type
            var inv = _humanoid?.GetInventory();
            if (inv != null && CountItemByPrefab(inv, _plantInfo.SeedPrefabName) > 0)
            {
                // Try to find next plant position
                Vector3 nextPos;
                if (FindPlantPosition(_plantInfo, out nextPos))
                {
                    _plantPosition = nextPos;
                    ResetStuck();
                    _phase = FarmPhase.MovingToPlantSpot;
                    Log($"More seeds — next plant spot at {nextPos:F1}");
                    return;
                }
            }

            // No more seeds or no more valid positions
            _phase = FarmPhase.Scanning;
            _scanTimer = 0f;
        }

        // ── Moving to output chest ──────────────────────────────────────────

        private void UpdateMovingToOutputChest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_outputChest == null)
            {
                _phase = FarmPhase.Scanning;
                return;
            }

            float dist = Vector3.Distance(transform.position, _outputChest.transform.position);
            if (dist < UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint(_outputChest.transform.position);
                _chestOpened = false;
                _phase = FarmPhase.StoringOutput;
                Log($"At output chest (dist={dist:F1}m) — storing crops");
                return;
            }

            _shouldRun = dist > 8f;
            bool moveOk = _ai.MoveToPoint(dt, _outputChest.transform.position, UseDistance, _shouldRun);
            LogMovement(dt, "output chest", dist, moveOk);
            UpdateStuckDetection(dt, "output chest");
        }

        // ── Storing output ──────────────────────────────────────────────────

        private void UpdateStoringOutput()
        {
            if (_outputChest == null)
            {
                if (_chestOpened) _chestOpened = false;
                _phase = FarmPhase.Scanning;
                return;
            }

            // Step 1: Open chest
            if (!_chestOpened)
            {
                _outputChest.SetInUse(true);
                _chestOpened = true;
                _actionTimer = 0.8f;
                Log("Opened output chest");
                return;
            }

            // Step 2: Wait for animation
            _actionTimer -= Time.deltaTime;
            if (_actionTimer > 0f) return;

            // Step 3: Transfer crop items
            var companionInv = _humanoid?.GetInventory();
            var chestInv = _outputChest.GetInventory();
            if (companionInv == null || chestInv == null)
            {
                _outputChest.SetInUse(false);
                _chestOpened = false;
                _phase = FarmPhase.Scanning;
                return;
            }

            int stored = 0;
            var allItems = companionInv.GetAllItems();
            for (int i = allItems.Count - 1; i >= 0; i--)
            {
                var item = allItems[i];
                if (item == null) continue;

                // Only store material items (crops, seeds), not equipped gear
                if (!IsCropItem(item)) continue;

                if (chestInv.CanAddItem(item, item.m_stack))
                {
                    var clone = item.Clone();
                    clone.m_stack = item.m_stack;
                    if (chestInv.AddItem(clone))
                    {
                        companionInv.RemoveItem(item);
                        stored++;
                        Log($"  Stored \"{item.m_shared?.m_name ?? "?"}\" x{clone.m_stack}");
                    }
                }
            }

            _outputChest.SetInUse(false);
            _chestOpened = false;
            Log($"Stored {stored} items in output chest");

            _phase = FarmPhase.Scanning;
            _scanTimer = 0f;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Seed → Plant Mapping
        // ══════════════════════════════════════════════════════════════════════

        private static void BuildSeedMapping()
        {
            if (s_mappingBuilt) return;
            s_mappingBuilt = true;
            s_seedToPlant = new Dictionary<string, SeedPlantInfo>();
            s_cropOutputNames = new HashSet<string>();

            if (ZNetScene.instance == null) return;

            foreach (var go in ZNetScene.instance.m_prefabs)
            {
                if (go == null) continue;

                var plant = go.GetComponent<Plant>();
                var piece = go.GetComponent<Piece>();
                if (plant == null || piece == null) continue;
                if (piece.m_resources == null || piece.m_resources.Length == 0) continue;
                if (piece.m_resources[0].m_resItem == null) continue;

                string seedName = piece.m_resources[0].m_resItem.gameObject.name;
                if (s_seedToPlant.ContainsKey(seedName)) continue;

                s_seedToPlant[seedName] = new SeedPlantInfo
                {
                    SeedPrefabName = seedName,
                    PlantPrefab = go,
                    GrowRadius = plant.m_growRadius,
                    NeedsCultivated = plant.m_needCultivatedGround,
                    Biome = plant.m_biome
                };

                // Build reverse lookup: grown prefab Pickable drops → crop output names
                if (plant.m_grownPrefabs != null)
                {
                    foreach (var grownGO in plant.m_grownPrefabs)
                    {
                        if (grownGO == null) continue;
                        var pickable = grownGO.GetComponent<Pickable>();
                        if (pickable?.m_itemPrefab != null)
                            s_cropOutputNames.Add(pickable.m_itemPrefab.name);
                    }
                }

                CompanionsPlugin.Log.LogDebug(
                    $"[Farm] Seed mapping: \"{seedName}\" → \"{go.name}\" " +
                    $"growRadius={plant.m_growRadius:F1} cultivated={plant.m_needCultivatedGround}");
            }

            CompanionsPlugin.Log.LogInfo(
                $"[Farm] Built seed→plant mapping: {s_seedToPlant.Count} entries, " +
                $"{s_cropOutputNames.Count} crop output names");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Finding Targets
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Find a cultivator in the companion's inventory.</summary>
        private ItemDrop.ItemData FindCultivator()
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return null;

            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_shared == null) continue;
                if (item.m_shared.m_buildPieces != null)
                {
                    // Check if this is a cultivator by looking for plant-type pieces
                    var table = item.m_shared.m_buildPieces;
                    if (table.m_pieces != null)
                    {
                        foreach (var p in table.m_pieces)
                        {
                            if (p != null && p.GetComponent<Plant>() != null)
                                return item;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>Get valid seed→plant entries based on the cultivator's PieceTable.</summary>
        private HashSet<string> GetCultivatorSeeds(ItemDrop.ItemData cultivator)
        {
            var validSeeds = new HashSet<string>();
            if (cultivator?.m_shared?.m_buildPieces == null) return validSeeds;

            var table = cultivator.m_shared.m_buildPieces;
            if (table.m_pieces == null) return validSeeds;

            foreach (var piecePrefab in table.m_pieces)
            {
                if (piecePrefab == null) continue;
                var piece = piecePrefab.GetComponent<Piece>();
                var plant = piecePrefab.GetComponent<Plant>();
                if (piece == null || plant == null) continue;
                if (piece.m_resources == null || piece.m_resources.Length == 0) continue;
                if (piece.m_resources[0].m_resItem == null) continue;

                string seedName = piece.m_resources[0].m_resItem.gameObject.name;
                validSeeds.Add(seedName);
            }

            return validSeeds;
        }

        /// <summary>Find a ripe (unharvested) crop Pickable within scan radius.
        /// Only picks crops that grow from Plant components on cultivated ground —
        /// ignores wild pickables (berries, mushrooms, thistle, etc.).
        /// Uses Physics.OverlapSphere because grown crop pickables may not have a
        /// Piece component (Piece.GetAllPiecesInRadius would miss them).</summary>
        private Pickable FindRipePickable()
        {
            int hits = Physics.OverlapSphereNonAlloc(
                transform.position, ScanRadius, _pickableScanBuffer, _pickableMask);

            Log($"FindRipePickable: {hits} colliders in {ScanRadius:F0}m radius");

            Pickable best = null;
            float bestDist = float.MaxValue;
            int pickableCount = 0;
            int cropsFound = 0;

            for (int i = 0; i < hits; i++)
            {
                var pickable = _pickableScanBuffer[i].GetComponent<Pickable>();
                if (pickable == null)
                    pickable = _pickableScanBuffer[i].GetComponentInParent<Pickable>();
                if (pickable == null) continue;
                pickableCount++;

                if (IsPicked(pickable)) continue;
                if (s_claimedPickables.Contains(pickable.GetInstanceID())) continue;

                // Only harvest crops on cultivated ground (not wild pickables).
                // Check if this pickable drops a known crop output (e.g. "Carrot"),
                // or if it's sitting on cultivated soil (player-planted crops).
                bool isCrop = false;
                string dropName = pickable.m_itemPrefab != null ? pickable.m_itemPrefab.name : "null";
                if (s_cropOutputNames != null && pickable.m_itemPrefab != null)
                {
                    if (s_cropOutputNames.Contains(dropName))
                        isCrop = true;
                }

                // Also check if it's on cultivated ground (player-planted)
                bool onCultivated = false;
                if (!isCrop)
                {
                    var hm = Heightmap.FindHeightmap(pickable.transform.position);
                    if (hm != null && hm.IsCultivated(pickable.transform.position))
                    {
                        onCultivated = true;
                        isCrop = true;
                    }
                }

                if (!isCrop)
                {
                    Log($"  Skip \"{pickable.name}\" drop=\"{dropName}\" — not a crop " +
                        $"(knownCrop={s_cropOutputNames?.Contains(dropName) == true} cultivated={onCultivated})");
                    continue;
                }

                cropsFound++;

                // Skip positions the AI couldn't reach before
                if (_ai != null && _ai.IsPositionBlacklisted(pickable.transform.position))
                {
                    Log($"  Skip \"{pickable.name}\" — position blacklisted (unreachable)");
                    continue;
                }

                float dist = Vector3.Distance(transform.position, pickable.transform.position);
                Log($"  Crop \"{pickable.name}\" drop=\"{dropName}\" dist={dist:F1}m " +
                    $"cultivated={onCultivated}");
                if (dist < bestDist)
                {
                    best = pickable;
                    bestDist = dist;
                }
            }

            Log($"FindRipePickable result: {pickableCount} pickables, {cropsFound} crops, " +
                $"best=\"{best?.name ?? "none"}\"");
            return best;
        }

        /// <summary>Check if a Pickable has already been harvested.</summary>
        private static bool IsPicked(Pickable pickable)
        {
            var nview = pickable.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return true;
            return nview.GetZDO().GetBool(ZDOVars.s_picked);
        }

        /// <summary>Find a seed in companion inventory that has a valid plant spot.</summary>
        private SeedPlantInfo? FindPlantableSeedInInventory(ItemDrop.ItemData cultivator)
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null || s_seedToPlant == null) return null;

            var validSeeds = GetCultivatorSeeds(cultivator);

            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_dropPrefab == null) continue;
                string prefabName = item.m_dropPrefab.name;
                if (!validSeeds.Contains(prefabName)) continue;

                SeedPlantInfo info;
                if (s_seedToPlant.TryGetValue(prefabName, out info))
                    return info;
            }

            return null;
        }

        /// <summary>
        /// Find a valid position to plant on cultivated ground using grid sampling.
        /// </summary>
        private bool FindPlantPosition(SeedPlantInfo info, out Vector3 result)
        {
            result = Vector3.zero;

            // Grid step = max of configured spacing and vanilla minimum (2 * growRadius).
            // Previous bug: max(PlantSpacing, GrowRadius) * 2  doubled the CONFIGURED spacing too.
            float spacing = Mathf.Max(PlantSpacing, info.GrowRadius * 2f);
            float radius = ScanRadius;
            Vector3 center = transform.position;

            // Snap grid origin to world-aligned coordinates so the grid is stable
            // regardless of companion position. This ensures previously planted crops
            // align with future grid points.
            float gridOriginX = Mathf.Floor(center.x / spacing) * spacing;
            float gridOriginZ = Mathf.Floor(center.z / spacing) * spacing;

            float bestDist = float.MaxValue;
            bool found = false;

            // Calculate grid bounds: enough steps to cover the scan radius from center
            int steps = Mathf.CeilToInt(radius / spacing);

            // World-aligned grid sample within scan radius
            for (int ix = -steps; ix <= steps; ix++)
            {
                for (int iz = -steps; iz <= steps; iz++)
                {
                    float worldX = gridOriginX + ix * spacing;
                    float worldZ = gridOriginZ + iz * spacing;
                    Vector3 candidate = new Vector3(worldX, 0f, worldZ);

                    // Check if within circular radius of companion
                    float dx = worldX - center.x;
                    float dz = worldZ - center.z;
                    float flatDist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (flatDist > radius) continue;

                    // Get terrain height (excludes building pieces — prevents planting on wood floors)
                    float groundHeight;
                    if (!ZoneSystem.instance.GetGroundHeight(candidate, out groundHeight))
                        continue;
                    candidate.y = groundHeight;

                    // Biome check
                    if (info.Biome != 0)
                    {
                        Heightmap.Biome biome = Heightmap.FindBiome(candidate);
                        if ((biome & info.Biome) == 0) continue;
                    }

                    // Cultivated ground check
                    if (info.NeedsCultivated)
                    {
                        var hm = Heightmap.FindHeightmap(candidate);
                        if (hm == null || !hm.IsCultivated(candidate))
                            continue;
                    }

                    // Spacing check — no existing Plant, Pickable, or building Piece within grow radius
                    int hits = Physics.OverlapSphereNonAlloc(
                        candidate, info.GrowRadius, _spacingBuffer, _spaceMask);
                    bool blocked = false;
                    for (int i = 0; i < hits; i++)
                    {
                        var col = _spacingBuffer[i];
                        if (col.GetComponent<Plant>() != null ||
                            col.GetComponent<Pickable>() != null ||
                            col.GetComponent<Piece>() != null)
                        {
                            blocked = true;
                            break;
                        }
                    }
                    if (blocked) continue;

                    // Pick closest valid position to companion
                    float dist = Vector3.Distance(transform.position, candidate);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        result = candidate;
                        found = true;
                    }
                }
            }

            return found;
        }

        /// <summary>Find a chest containing seeds that can be planted.</summary>
        private Container FindChestWithSeeds(ItemDrop.ItemData cultivator,
            out SeedPlantInfo seedInfo, out string seedPrefab, out int seedCount)
        {
            seedInfo = default;
            seedPrefab = null;
            seedCount = 0;

            if (s_seedToPlant == null) return null;

            var validSeeds = GetCultivatorSeeds(cultivator);
            Container bestChest = null;
            float bestDist = float.MaxValue;

            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                if (_ai != null && _ai.IsPositionBlacklisted(chest.transform.position)) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;

                foreach (var item in inv.GetAllItems())
                {
                    if (item?.m_dropPrefab == null) continue;
                    string prefab = item.m_dropPrefab.name;
                    if (!validSeeds.Contains(prefab)) continue;

                    SeedPlantInfo info;
                    if (!s_seedToPlant.TryGetValue(prefab, out info)) continue;

                    float dist = Vector3.Distance(transform.position, chest.transform.position);
                    if (dist < bestDist)
                    {
                        bestChest = chest;
                        bestDist = dist;
                        seedInfo = info;
                        seedPrefab = prefab;
                        seedCount = item.m_stack;
                    }
                }
            }

            return bestChest;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Planting
        // ══════════════════════════════════════════════════════════════════════

        private void PlantSeed(SeedPlantInfo info, Vector3 pos)
        {
            if (info.PlantPrefab == null)
            {
                Log("PlantPrefab is null — cannot plant");
                return;
            }

            var inv = _humanoid?.GetInventory();
            if (inv == null) return;

            // Consume one seed
            int seedCount = CountItemByPrefab(inv, info.SeedPrefabName);
            if (seedCount <= 0)
            {
                Log("No seeds left — cannot plant");
                return;
            }

            RemoveItemByPrefab(inv, info.SeedPrefabName, 1);

            // Instantiate the plant
            Quaternion rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);

            TerrainModifier.SetTriggerOnPlaced(true);
            var planted = Object.Instantiate(info.PlantPrefab, pos, rot);
            TerrainModifier.SetTriggerOnPlaced(false);

            // Set up the piece for ownership
            var piece = planted.GetComponent<Piece>();
            if (piece != null && Player.m_localPlayer != null)
            {
                piece.SetCreator(Player.m_localPlayer.GetPlayerID());
                piece.m_placeEffect.Create(pos, rot, planted.transform);
            }

            Log($"Planted \"{info.PlantPrefab.name}\" at {pos:F1} " +
                $"(seeds remaining: {seedCount - 1})");
        }

        private void EquipCultivator(ItemDrop.ItemData cultivator)
        {
            if (cultivator == null || _humanoid == null) return;

            // Suppress auto-equip so CompanionSetup doesn't re-equip combat gear
            if (_setup != null) _setup.SuppressAutoEquip = true;

            if (_humanoid.IsItemEquiped(cultivator)) return;

            var curRight = ReflectionHelper.GetRightItem(_humanoid);
            var curLeft  = ReflectionHelper.GetLeftItem(_humanoid);

            if (curRight != null) _humanoid.UnequipItem(curRight, false);
            if (curLeft  != null) _humanoid.UnequipItem(curLeft, false);

            _humanoid.EquipItem(cultivator, false);
            Log($"Equipped cultivator \"{cultivator.m_shared.m_name}\"");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Chest & Inventory Helpers
        // ══════════════════════════════════════════════════════════════════════

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
                if (container.gameObject == gameObject) continue;
                if (container.IsInUse()) continue;

                _nearbyChests.Add(container);
            }

            LogDebug($"Scan: found {_nearbyChests.Count} chests within {ScanRadius}m");
        }

        private Container FindChestWithSpace()
        {
            Container best = null;
            float bestDist = float.MaxValue;

            foreach (var chest in _nearbyChests)
            {
                if (chest == null) continue;
                if (_ai != null && _ai.IsPositionBlacklisted(chest.transform.position)) continue;
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

        /// <summary>Check if companion has harvestable crop output in inventory.</summary>
        private bool HasCropOutput()
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return false;

            foreach (var item in inv.GetAllItems())
            {
                if (IsCropItem(item)) return true;
            }
            return false;
        }

        /// <summary>
        /// Check if an item is a crop/farm output (material type, not equipped gear).
        /// Excludes seeds that can still be planted — those stay in inventory for planting.
        /// </summary>
        private bool IsCropItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            if (_humanoid != null && _humanoid.IsItemEquiped(item)) return false;
            var type = item.m_shared.m_itemType;
            if (type != ItemDrop.ItemData.ItemType.Material) return false;

            // Don't store seeds that the companion can plant — keep them for planting.
            if (item.m_dropPrefab != null && s_seedToPlant != null &&
                s_seedToPlant.ContainsKey(item.m_dropPrefab.name))
                return false;

            return true;
        }

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

        private static void RemoveItemByPrefab(Inventory inv, string prefabName, int amount)
        {
            var items = inv.GetAllItems();
            int remaining = amount;
            for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
            {
                var item = items[i];
                if (item?.m_dropPrefab == null || item.m_dropPrefab.name != prefabName)
                    continue;
                int toRemove = Mathf.Min(item.m_stack, remaining);
                inv.RemoveItem(item, toRemove);
                remaining -= toRemove;
            }
        }

        /// <summary>
        /// Picks up a single nearby drop and returns true, or returns false
        /// if no more drops remain. Called repeatedly from CollectingDrops
        /// with a delay between each pickup for natural pacing.
        /// </summary>
        private bool PickUpOneDrop(Vector3 pos)
        {
            var companionInv = _humanoid?.GetInventory();
            if (companionInv == null) return false;

            int hits = Physics.OverlapSphereNonAlloc(pos, 5f, _dropBuffer, _itemMask);
            for (int i = 0; i < hits; i++)
            {
                var itemDrop = _dropBuffer[i].GetComponentInParent<ItemDrop>();
                if (itemDrop == null || itemDrop.m_itemData == null) continue;

                var nview = itemDrop.GetComponent<ZNetView>();
                if (nview == null || nview.GetZDO() == null) continue;

                if (companionInv.CanAddItem(itemDrop.m_itemData, itemDrop.m_itemData.m_stack))
                {
                    companionInv.AddItem(itemDrop.m_itemData);
                    nview.ClaimOwnership();
                    nview.Destroy();
                    Log($"Picked up {itemDrop.m_itemData.m_stack}x " +
                        $"\"{itemDrop.m_itemData.m_shared.m_name}\"");
                    return true; // picked one — caller waits before next pickup
                }
            }
            return false; // no more drops
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Movement & Follow Management
        // ══════════════════════════════════════════════════════════════════════

        private void ClearFollowForMovement()
        {
            _allDoneNotified = false;
            if (_ai != null)
            {
                _ai.SetFollowTarget(null);
                _ai.StopMoving();
                Log("Cleared follow target + stopped AI — FarmController driving movement");
            }
        }

        private void RestoreFollow()
        {
            if (_ai == null) return;
            bool follow = _setup != null && _setup.GetFollow();
            bool stayHome = _setup != null && _setup.GetStayHome() && _setup.HasHomePosition();
            if (follow && Player.m_localPlayer != null)
            {
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                Log("Restored follow target to player");
            }
            else if (stayHome)
            {
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPointAt(_setup.GetHomePosition());
                Log("Restored patrol to home");
            }
        }

        private void ClaimPickable()
        {
            UnclaimPickable();
            _claimedPickableId = _targetPickable?.GetInstanceID() ?? 0;
            if (_claimedPickableId != 0) s_claimedPickables.Add(_claimedPickableId);
        }

        private void UnclaimPickable()
        {
            if (_claimedPickableId != 0)
            {
                s_claimedPickables.Remove(_claimedPickableId);
                _claimedPickableId = 0;
            }
        }

        private void Finish()
        {
            Log("Finish — immediate re-scan for next task");
            UnclaimPickable();
            CloseAnyOpenChest();
            RestoreCombatLoadout();
            _targetPickable = null;
            _targetChest = null;
            _outputChest = null;
            _allDoneNotified = false;
            _phase = FarmPhase.Idle;
            // Immediate re-scan instead of waiting ScanInterval — prevents
            // one-frame follow jitter between tasks (Finish→RestoreFollow→
            // next frame scan→ClearFollow caused companion to briefly walk
            // toward player then snap back to work).
            _scanTimer = 0f;
        }

        private void Abort(string reason)
        {
            Log($"Aborted — {reason} (phase was {_phase})");
            UnclaimPickable();
            CloseAnyOpenChest();
            RestoreCombatLoadout();
            _targetPickable = null;
            _targetChest = null;
            _outputChest = null;
            _allDoneNotified = false;
            _phase = FarmPhase.Idle;
            _scanTimer = ScanInterval;
            RestoreFollow();
        }

        /// <summary>
        /// Unequip cultivator and re-enable auto-equip so the companion
        /// returns to combat gear after farming ends.
        /// </summary>
        private void RestoreCombatLoadout()
        {
            if (_setup != null)
            {
                _setup.SuppressAutoEquip = false;
                _setup.SyncEquipmentToInventory();
                Log("SuppressAutoEquip=false, triggered auto-equip");
            }
        }

        private void CloseAnyOpenChest()
        {
            if (!_chestOpened) return;
            if (_phase == FarmPhase.StoringOutput && _outputChest != null)
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
                // Blacklist the target position so we don't keep trying to reach it
                Vector3 targetPos = GetCurrentTargetPosition();
                if (targetPos != Vector3.zero && _ai != null)
                {
                    _ai.BlacklistPosition(targetPos);
                    Log($"Blacklisted stuck target \"{targetName}\" at {targetPos:F1}");
                }
                Abort($"stuck moving to {targetName}");
            }
        }

        // ── Logging ─────────────────────────────────────────────────────────

        private void LogMovement(float dt, string target, float dist, bool moveOk)
        {
            _moveLogTimer -= dt;
            if (_moveLogTimer > 0f) return;
            _moveLogTimer = 1f;
            Log($"Moving → {target} dist={dist:F1}m moveOk={moveOk} pos={transform.position:F1}");
        }

        private void Log(string msg)
        {
            string name = _character?.m_name ?? "?";
            CompanionsPlugin.Log.LogInfo($"[Farm|{name}] {msg}");
        }

        private void LogDebug(string msg)
        {
            string name = _character?.m_name ?? "?";
            CompanionsPlugin.Log.LogDebug($"[Farm|{name}] {msg}");
        }

        private Vector3 GetCurrentTargetPosition()
        {
            switch (_phase)
            {
                case FarmPhase.MovingToPickable:
                    return _targetPickable != null ? _targetPickable.transform.position : Vector3.zero;
                case FarmPhase.MovingToSeedChest:
                    return _targetChest != null ? _targetChest.transform.position : Vector3.zero;
                case FarmPhase.MovingToPlantSpot:
                    return _plantPosition;
                case FarmPhase.MovingToOutputChest:
                    return _outputChest != null ? _outputChest.transform.position : Vector3.zero;
                default:
                    return Vector3.zero;
            }
        }
    }
}
