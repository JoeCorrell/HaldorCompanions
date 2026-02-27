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
        private MonsterAI        _ai;
        private Humanoid         _humanoid;
        private Character        _character;
        private CompanionSetup   _setup;
        private ZNetView         _nview;
        private ZSyncAnimation   _zanim;
        private CompanionTalk    _talk;
        private CombatController _combat;
        private HarvestController _harvest;

        // ── State ───────────────────────────────────────────────────────────
        private RepairPhase     _phase;
        private CraftingStation _targetStation;
        private float           _scanTimer;
        private float           _repairTickTimer;
        private float           _stuckTimer;
        private float           _stuckCheckTimer;
        private Vector3         _stuckCheckPos;
        private readonly List<ItemDrop.ItemData> _repairQueue = new List<ItemDrop.ItemData>();
        private readonly List<ItemDrop.ItemData> _tempWorn    = new List<ItemDrop.ItemData>();

        // ── Config ──────────────────────────────────────────────────────────
        private const float DurabilityThreshold = 0.50f;
        private const float ScanInterval        = 5f;
        private const float ScanRadius          = 20f;
        private const float RepairTickInterval  = 0.8f;
        private const float MoveTimeout         = 12f;
        private const float StuckCheckPeriod    = 1f;     // check movement every 1s
        private const float StuckMinDistance     = 0.5f;   // must move at least 0.5m per check period
        private const float UseDistance         = 2.5f;

        // ── Lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            _ai        = GetComponent<MonsterAI>();
            _humanoid  = GetComponent<Humanoid>();
            _character = GetComponent<Character>();
            _setup     = GetComponent<CompanionSetup>();
            _nview     = GetComponent<ZNetView>();
            _zanim     = GetComponent<ZSyncAnimation>();
            _talk      = GetComponent<CompanionTalk>();
            _combat    = GetComponent<CombatController>();
            _harvest   = GetComponent<HarvestController>();
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

            // Find items below durability threshold
            _tempWorn.Clear();
            inv.GetWornItems(_tempWorn);

            bool anyBelowThreshold = false;
            for (int i = _tempWorn.Count - 1; i >= 0; i--)
            {
                var item = _tempWorn[i];
                float maxDur = item.GetMaxDurability();
                if (maxDur <= 0f || item.m_durability / maxDur >= DurabilityThreshold)
                    _tempWorn.RemoveAt(i);
                else
                    anyBelowThreshold = true;
            }

            if (!anyBelowThreshold) return;

            // Find nearest station that can repair at least one item
            var stations = ReflectionHelper.GetAllCraftingStations();
            if (stations == null) return;

            CraftingStation bestStation = null;
            float bestDist = float.MaxValue;

            for (int s = 0; s < stations.Count; s++)
            {
                var station = stations[s];
                if (station == null) continue;

                float dist = Vector3.Distance(transform.position, station.transform.position);
                if (dist > ScanRadius || dist >= bestDist) continue;

                // Check if this station can repair any of our worn items
                bool canRepairAny = false;
                for (int i = 0; i < _tempWorn.Count; i++)
                {
                    if (CanRepairAt(station, _tempWorn[i]))
                    {
                        canRepairAny = true;
                        break;
                    }
                }

                if (canRepairAny)
                {
                    bestStation = station;
                    bestDist = dist;
                }
            }

            if (bestStation == null) return;

            // Build repair queue for this station
            _repairQueue.Clear();
            for (int i = 0; i < _tempWorn.Count; i++)
            {
                if (CanRepairAt(bestStation, _tempWorn[i]))
                    _repairQueue.Add(_tempWorn[i]);
            }

            _targetStation = bestStation;
            _stuckTimer = 0f;
            _stuckCheckTimer = 0f;
            _stuckCheckPos = transform.position;

            _phase = RepairPhase.MovingToStation;
            Log($"Found {_repairQueue.Count} items to repair at " +
                $"\"{_targetStation.m_name}\" ({bestDist:F1}m away)");

            if (_talk != null) _talk.Say("Time for repairs.");
        }

        // ── Moving to station ───────────────────────────────────────────────

        private void UpdateMoving(float dt)
        {
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
                ReflectionHelper.LookAt(_ai, _targetStation.transform.position);

                // Start crafting animation
                if (_zanim != null)
                    _zanim.SetInt("crafting", _targetStation.m_useAnimation);
                _targetStation.PokeInUse();

                _repairTickTimer = RepairTickInterval;
                _phase = RepairPhase.Repairing;
                Log($"Arrived at \"{_targetStation.m_name}\" — starting repair of {_repairQueue.Count} items");
                return;
            }

            // Move toward station
            ReflectionHelper.TryMoveTo(_ai, dt, _targetStation.transform.position, UseDistance, true);

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
            ReflectionHelper.LookAt(_ai, _targetStation.transform.position);
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

        private void FinishRepair()
        {
            if (_zanim != null) _zanim.SetInt("crafting", 0);
            Log($"All items repaired at \"{_targetStation?.m_name ?? "?"}\"");
            if (_talk != null) _talk.Say("All fixed up!");
            _targetStation = null;
            _repairQueue.Clear();
            _phase = RepairPhase.Idle;
            _scanTimer = ScanInterval; // don't re-scan immediately
        }

        private void Abort(string reason)
        {
            if (_zanim != null) _zanim.SetInt("crafting", 0);
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

            // Skip during active harvest
            if (_harvest != null && _harvest.IsInGatherMode)
                return true;

            // Skip while companion UI is open for this companion
            var panel = CompanionInteractPanel.Instance;
            if (panel != null && panel.IsVisible && panel.CurrentCompanion == _setup)
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

        private void Log(string msg)
        {
            string name = _character?.m_name ?? "?";
            CompanionsPlugin.Log.LogInfo($"[Repair|{name}] {msg}");
        }
    }
}
