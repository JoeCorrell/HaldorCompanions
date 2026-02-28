using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Auto-repair controller for companions.
    /// When items drop below a durability threshold, the companion walks to
    /// the nearest valid CraftingStation, plays the crafting animation,
    /// and repairs all repairable items one by one.
    /// </summary>
    public class RepairController : MonoBehaviour
    {
        internal enum RepairPhase { Idle, MovingToStation, Repairing }

        internal RepairPhase Phase => _phase;
        public bool IsActive => _phase != RepairPhase.Idle;

        // ── Components ──────────────────────────────────────────────────────
        private CompanionAI      _ai;
        private Humanoid         _humanoid;
        private Character        _character;
        private CompanionSetup   _setup;
        private ZNetView         _nview;
        private ZSyncAnimation   _zanim;
        private CompanionTalk    _talk;
        private CombatController _combat;
        private HarvestController _harvest;
        private DoorHandler      _doorHandler;

        // ── State ───────────────────────────────────────────────────────────
        private RepairPhase     _phase;
        private CraftingStation _targetStation;
        private float           _scanTimer;
        private float           _repairTickTimer;
        private float           _stuckTimer;
        private float           _stuckCheckTimer;
        private Vector3         _stuckCheckPos;
        private bool            _lastScanFailed;  // suppresses repeated scan detail logging
        private readonly List<ItemDrop.ItemData> _repairQueue = new List<ItemDrop.ItemData>();
        private readonly List<ItemDrop.ItemData> _tempWorn    = new List<ItemDrop.ItemData>();

        // ── Config ──────────────────────────────────────────────────────────
        private const float DurabilityThreshold = 0.70f;
        private const float ScanInterval        = 5f;
        private const float ScanBackoffInterval = 60f;   // long backoff when no station can help
        private const float ScanRadius          = 20f;
        private const float RepairTickInterval  = 0.8f;
        private const float MoveTimeout         = 12f;
        private const float StuckCheckPeriod    = 1f;     // check movement every 1s
        private const float StuckMinDistance     = 0.5f;   // must move at least 0.5m per check period
        private const float UseDistance         = 2.5f;

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
            _combat      = GetComponent<CombatController>();
            _harvest     = GetComponent<HarvestController>();
            _doorHandler = GetComponent<DoorHandler>();
        }

        private void Update()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

            float dt = Time.deltaTime;

            if (ShouldAbort())
            {
                if (_phase != RepairPhase.Idle)
                    Abort("interrupted (combat/harvest/UI)");
                return;
            }

            switch (_phase)
            {
                case RepairPhase.Idle:
                    UpdateIdle(dt);
                    break;
                case RepairPhase.MovingToStation:
                    UpdateMoving(dt);
                    break;
                case RepairPhase.Repairing:
                    UpdateRepairing(dt);
                    break;
            }
        }

        // ── Idle — periodic scan ────────────────────────────────────────────

        private void UpdateIdle(float dt)
        {
            _scanTimer -= dt;
            if (_scanTimer > 0f) return;
            _scanTimer = ScanInterval;

            var inv = _humanoid?.GetInventory();
            if (inv == null) return;

            // Get ALL worn items (any durability below max)
            _tempWorn.Clear();
            inv.GetWornItems(_tempWorn);
            if (_tempWorn.Count == 0) return;

            // Log worn item details (suppress on repeated failed scans to avoid spam)
            if (!_lastScanFailed)
            {
                for (int i = 0; i < _tempWorn.Count; i++)
                {
                    var item = _tempWorn[i];
                    float maxDur = item.GetMaxDurability();
                    float pct = maxDur > 0f ? item.m_durability / maxDur * 100f : 100f;
                    var recipe = ObjectDB.instance?.GetRecipe(item);
                    string stationName = recipe?.m_craftingStation?.m_name ?? recipe?.m_repairStation?.m_name ?? "none";
                    int minLevel = recipe?.m_minStationLevel ?? 0;
                    Log($"Scan: \"{item.m_shared.m_name}\" dur={item.m_durability:F0}/{maxDur:F0} ({pct:F0}%) " +
                        $"station=\"{stationName}\" minLevel={minLevel} canRepair={item.m_shared.m_canBeReparied}");
                }
            }

            // Only trigger a repair trip if at least one item is below threshold
            bool anyBelowThreshold = false;
            for (int i = 0; i < _tempWorn.Count; i++)
            {
                float maxDur = _tempWorn[i].GetMaxDurability();
                if (maxDur > 0f && _tempWorn[i].m_durability / maxDur < DurabilityThreshold)
                {
                    anyBelowThreshold = true;
                    break;
                }
            }

            if (!anyBelowThreshold)
            {
                Log($"Scan: {_tempWorn.Count} worn items but none below {DurabilityThreshold * 100f:F0}% — skipping");
                return;
            }

            // Find nearest station that can repair at least one worn item
            var stations = ReflectionHelper.GetAllCraftingStations();
            if (stations == null) return;

            CraftingStation bestStation = null;
            float bestDist = float.MaxValue;

            for (int s = 0; s < stations.Count; s++)
            {
                var station = stations[s];
                if (station == null) continue;

                float dist = Vector3.Distance(transform.position, station.transform.position);
                if (dist > ScanRadius) continue;

                bool canRepairAny = false;
                for (int i = 0; i < _tempWorn.Count; i++)
                {
                    if (CanRepairAt(station, _tempWorn[i]))
                    {
                        canRepairAny = true;
                        break;
                    }
                }

                Log($"Scan: station \"{station.m_name}\" level={station.GetLevel()} " +
                    $"dist={dist:F1}m canRepairAny={canRepairAny}");

                if (canRepairAny && dist < bestDist)
                {
                    bestStation = station;
                    bestDist = dist;
                }
            }

            if (bestStation == null)
            {
                if (!_lastScanFailed)
                    Log($"Scan: no reachable station can repair any worn items within {ScanRadius}m — backing off {ScanBackoffInterval}s");
                _lastScanFailed = true;
                _scanTimer = ScanBackoffInterval;
                return;
            }

            _lastScanFailed = false;

            // Build repair queue: ALL worn items this station can repair
            // (not just items below 50% — once we're making the trip,
            // repair everything to full)
            _repairQueue.Clear();
            for (int i = 0; i < _tempWorn.Count; i++)
            {
                bool canRepair = CanRepairAt(bestStation, _tempWorn[i]);
                if (canRepair)
                    _repairQueue.Add(_tempWorn[i]);
                else
                    Log($"Scan: \"{_tempWorn[i].m_shared.m_name}\" cannot be repaired at " +
                        $"\"{bestStation.m_name}\" — {GetRepairRejectReason(bestStation, _tempWorn[i])}");
            }

            _targetStation = bestStation;
            _stuckTimer = 0f;
            _stuckCheckTimer = 0f;
            _stuckCheckPos = transform.position;

            _phase = RepairPhase.MovingToStation;
            Log($"Found {_repairQueue.Count} items to repair at " +
                $"\"{_targetStation.m_name}\" level={_targetStation.GetLevel()} ({bestDist:F1}m away)");

            if (_talk != null) _talk.Say("Time for repairs.");
        }

        // ── Moving to station ───────────────────────────────────────────────

        private void UpdateMoving(float dt)
        {
            // DoorHandler is actively opening a door — pause movement
            if (_doorHandler != null && _doorHandler.IsActive)
                return;

            if (_targetStation == null)
            {
                Abort("station destroyed while moving");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetStation.transform.position);

            // Arrived?
            if (dist < UseDistance)
            {
                _ai.StopMoving();
                _ai.LookAtPoint( _targetStation.transform.position);

                // Start crafting animation (Player-model only — DvergerMage lacks this param)
                if (_zanim != null && _setup != null && _setup.CanWearArmor())
                    _zanim.SetInt("crafting", _targetStation.m_useAnimation);
                _targetStation.PokeInUse();

                _repairTickTimer = RepairTickInterval;
                _phase = RepairPhase.Repairing;
                Log($"Arrived at \"{_targetStation.m_name}\" — starting repair of {_repairQueue.Count} items");
                return;
            }

            // Move toward station
            _ai.MoveToPoint( dt, _targetStation.transform.position, UseDistance, true);

            // Stuck detection — check movement over 1s windows
            _stuckCheckTimer += dt;
            if (_stuckCheckTimer >= StuckCheckPeriod)
            {
                float moved = Vector3.Distance(transform.position, _stuckCheckPos);
                if (moved < StuckMinDistance)
                    _stuckTimer += _stuckCheckTimer; // accumulate actual stuck time
                else
                    _stuckTimer = 0f;
                _stuckCheckPos = transform.position;
                _stuckCheckTimer = 0f;
            }

            if (_stuckTimer > MoveTimeout)
            {
                Abort($"stuck moving to \"{_targetStation.m_name}\" ({dist:F1}m away, stuck {_stuckTimer:F1}s)");
                return;
            }
        }

        // ── Repairing items one by one ──────────────────────────────────────

        private void UpdateRepairing(float dt)
        {
            if (_targetStation == null)
            {
                Abort("station destroyed while repairing");
                return;
            }

            // Check we haven't drifted too far (pushed by enemy, etc.)
            float dist = Vector3.Distance(transform.position, _targetStation.transform.position);
            if (dist > UseDistance * 2f)
            {
                Abort($"drifted too far from station ({dist:F1}m)");
                return;
            }

            // Face station and keep animation active
            _ai.LookAtPoint( _targetStation.transform.position);
            _targetStation.PokeInUse();

            _repairTickTimer -= dt;
            if (_repairTickTimer > 0f) return;
            _repairTickTimer = RepairTickInterval;

            // Pop next repairable item
            while (_repairQueue.Count > 0)
            {
                var item = _repairQueue[0];
                _repairQueue.RemoveAt(0);

                // Verify item is still valid and needs repair
                var inv = _humanoid?.GetInventory();
                if (inv == null || !inv.ContainsItem(item)) continue;
                if (item.m_durability >= item.GetMaxDurability()) continue;

                float oldDur = item.m_durability;
                float maxDur = item.GetMaxDurability();
                item.m_durability = maxDur;

                // Play repair VFX
                _targetStation.m_repairItemDoneEffects.Create(
                    _targetStation.transform.position, Quaternion.identity);

                Log($"Repaired \"{item.m_shared.m_name}\" — {oldDur:F0} → {maxDur:F0}");
                return; // one item per tick
            }

            // All items repaired
            FinishRepair();
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Directed repair: immediately start walking to the given station.
        /// Builds repair queue from all worn items that station can fix.
        /// </summary>
        public bool DirectRepairAt(CraftingStation station)
        {
            if (station == null) return false;
            if (_phase != RepairPhase.Idle) Abort("new directed repair");

            var inv = _humanoid?.GetInventory();
            if (inv == null) return false;

            _tempWorn.Clear();
            inv.GetWornItems(_tempWorn);
            if (_tempWorn.Count == 0)
            {
                Log("DirectRepairAt — no worn items");
                return false;
            }

            _repairQueue.Clear();
            for (int i = 0; i < _tempWorn.Count; i++)
            {
                if (CanRepairAt(station, _tempWorn[i]))
                    _repairQueue.Add(_tempWorn[i]);
                else
                    Log($"DirectRepairAt: \"{_tempWorn[i].m_shared.m_name}\" — {GetRepairRejectReason(station, _tempWorn[i])}");
            }

            if (_repairQueue.Count == 0)
            {
                Log($"DirectRepairAt — no items repairable at \"{station.m_name}\"");
                return false;
            }

            _targetStation = station;
            _stuckTimer = 0f;
            _stuckCheckTimer = 0f;
            _stuckCheckPos = transform.position;
            _phase = RepairPhase.MovingToStation;
            _lastScanFailed = false;

            Log($"DirectRepairAt — {_repairQueue.Count} items to repair at \"{station.m_name}\"");
            return true;
        }

        private void FinishRepair()
        {
            if (_zanim != null && _setup != null && _setup.CanWearArmor()) _zanim.SetInt("crafting", 0);
            Log($"All items repaired at \"{_targetStation?.m_name ?? "?"}\"");
            if (_talk != null) _talk.Say("All fixed up!");
            _targetStation = null;
            _repairQueue.Clear();
            _phase = RepairPhase.Idle;
            _scanTimer = 1f; // quick rescan for remaining items at other stations
        }

        /// <summary>
        /// Public cancel — called by CancelExistingActions / CancelAll
        /// when a new directed command preempts an active repair.
        /// </summary>
        public void CancelDirected()
        {
            if (_phase == RepairPhase.Idle) return;
            Abort("cancelled by new command");
        }

        private void Abort(string reason)
        {
            if (_zanim != null && _setup != null && _setup.CanWearArmor()) _zanim.SetInt("crafting", 0);
            Log($"Aborted — {reason} (phase was {_phase})");
            _targetStation = null;
            _repairQueue.Clear();
            _phase = RepairPhase.Idle;
            _scanTimer = ScanInterval;
        }

        private bool ShouldAbort()
        {
            // Skip during active combat
            if (_combat != null && _combat.Phase != CombatController.CombatPhase.Idle)
                return true;

            // Skip during active harvest (moving/attacking/collecting, not idle scanning)
            if (_harvest != null && _harvest.IsActive)
                return true;

            // Skip while companion UI is open for this companion
            if (CompanionInteractPanel.IsOpenFor(_setup) || CompanionRadialMenu.IsOpenFor(_setup))
                return true;

            return false;
        }

        /// <summary>
        /// Check if a specific item can be repaired at a specific station.
        /// Mirrors vanilla InventoryGui.CanRepair logic without Player dependency.
        /// </summary>
        private static bool CanRepairAt(CraftingStation station, ItemDrop.ItemData item)
        {
            if (station == null || item == null) return false;
            if (!item.m_shared.m_canBeReparied) return false;

            var recipe = ObjectDB.instance?.GetRecipe(item);
            if (recipe == null) return false;
            if (recipe.m_craftingStation == null && recipe.m_repairStation == null) return false;

            // Station name must match recipe's crafting or repair station
            bool stationMatch =
                (recipe.m_repairStation != null &&
                 recipe.m_repairStation.m_name == station.m_name) ||
                (recipe.m_craftingStation != null &&
                 recipe.m_craftingStation.m_name == station.m_name);

            if (!stationMatch) return false;

            // Station level check (capped at 4 like vanilla)
            if (Mathf.Min(station.GetLevel(), 4) < recipe.m_minStationLevel)
                return false;

            return true;
        }

        /// <summary>Diagnostic: explains why an item can't be repaired at a station.</summary>
        private static string GetRepairRejectReason(CraftingStation station, ItemDrop.ItemData item)
        {
            if (!item.m_shared.m_canBeReparied) return "canBeRepaired=false";
            var recipe = ObjectDB.instance?.GetRecipe(item);
            if (recipe == null) return "no recipe found";
            if (recipe.m_craftingStation == null && recipe.m_repairStation == null) return "recipe has no station";

            string recipeStation = recipe.m_repairStation?.m_name ?? recipe.m_craftingStation?.m_name ?? "?";
            bool nameMatch = (recipe.m_repairStation != null && recipe.m_repairStation.m_name == station.m_name) ||
                             (recipe.m_craftingStation != null && recipe.m_craftingStation.m_name == station.m_name);
            if (!nameMatch) return $"wrong station (needs \"{recipeStation}\")";

            int stationLevel = Mathf.Min(station.GetLevel(), 4);
            if (stationLevel < recipe.m_minStationLevel)
                return $"station level too low ({stationLevel} < {recipe.m_minStationLevel})";

            return "unknown";
        }

        private void Log(string msg)
        {
            string name = _character?.m_name ?? "?";
            CompanionsPlugin.Log.LogDebug($"[Repair|{name}] {msg}");
        }
    }
}
