using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Handles "Collect Resources" mode (action mode 1).
    /// Scans for nearby trees, rocks, and ore deposits, equips the correct tool,
    /// navigates to the resource, and attacks it to harvest.
    /// </summary>
    public class CompanionHarvest : MonoBehaviour
    {
        private enum HarvestState { Idle, Moving, Attacking }
        internal enum ResourceType { None, Tree, Rock }

        private ZNetView         _nview;
        private Humanoid         _humanoid;
        private MonsterAI        _ai;
        private CompanionSetup   _setup;
        private CompanionStamina _stamina;
        private Character        _character;

        private HarvestState _state = HarvestState.Idle;
        private GameObject   _currentTarget;
        private ResourceType _currentResourceType;

        private float   _scanTimer;
        private float   _attackCooldown;
        private float   _stuckTimer;
        private Vector3 _lastPosition;

        private GameObject _waypoint;

        // Targets we recently failed to reach — skip for a while
        private readonly Dictionary<int, float> _blacklist = new Dictionary<int, float>();

        private const float ScanInterval      = 2f;
        private const float ScanRange         = 20f;
        private const float AttackRange       = 2.5f;
        private const float AttackCooldown    = 2.5f;
        private const float MaxPlayerDistance  = 40f;
        private const float StuckTimeout      = 8f;
        private const float BlacklistDuration = 30f;
        private const float StaminaCost       = 10f;

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _humanoid  = GetComponent<Humanoid>();
            _ai        = GetComponent<MonsterAI>();
            _setup     = GetComponent<CompanionSetup>();
            _stamina   = GetComponent<CompanionStamina>();
            _character = GetComponent<Character>();

            _waypoint = new GameObject("HC_HarvestWaypoint");
            Object.DontDestroyOnLoad(_waypoint);
        }

        private void OnDestroy()
        {
            if (_waypoint != null) Object.Destroy(_waypoint);
            if (_setup != null) _setup.SuppressAutoEquip = false;
        }

        private void FixedUpdate()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            int mode = zdo.GetInt(CompanionSetup.ActionModeHash, 0);
            if (mode != 1)
            {
                if (_state != HarvestState.Idle) StopHarvesting();
                return;
            }

            // Don't harvest while dead or mid-attack animation
            if (_character != null && (_character.IsDead() || _character.InAttack()))
                return;

            // Pause harvesting if enemies nearby
            if (HasEnemyNearby())
            {
                if (_state != HarvestState.Idle) PauseHarvesting();
                return;
            }

            // Don't wander too far from player
            if (Player.m_localPlayer != null)
            {
                float playerDist = Vector3.Distance(
                    transform.position, Player.m_localPlayer.transform.position);
                if (playerDist > MaxPlayerDistance)
                {
                    if (_state != HarvestState.Idle) StopHarvesting();
                    return;
                }
            }

            switch (_state)
            {
                case HarvestState.Idle:     UpdateIdle();      break;
                case HarvestState.Moving:   UpdateMoving();    break;
                case HarvestState.Attacking: UpdateAttacking(); break;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  State: Idle — follow player, scan for resources periodically
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateIdle()
        {
            _scanTimer += Time.deltaTime;
            if (_scanTimer < ScanInterval) return;
            _scanTimer = 0f;

            CleanBlacklist();

            var (target, type) = FindNearestResource();
            if (target == null) return;

            var tool = FindBestTool(type);
            if (tool == null) return;

            _currentTarget       = target;
            _currentResourceType = type;

            EquipToolForHarvest(tool);

            // Navigate to resource via waypoint
            _waypoint.transform.position = target.transform.position;
            _ai.SetFollowTarget(_waypoint);

            _stuckTimer   = 0f;
            _lastPosition = transform.position;
            _state        = HarvestState.Moving;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  State: Moving — pathfind to resource, switch to attack when close
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateMoving()
        {
            if (!IsTargetValid())
            {
                StopHarvesting();
                return;
            }

            float dist = Vector3.Distance(
                transform.position, _currentTarget.transform.position);

            if (dist <= AttackRange)
            {
                // Close enough — stop following, start attacking
                _ai.SetFollowTarget(null);
                _attackCooldown = 0f;
                _state = HarvestState.Attacking;
                return;
            }

            // Stuck detection
            _stuckTimer += Time.deltaTime;
            if (_stuckTimer >= StuckTimeout)
            {
                float moved = Vector3.Distance(transform.position, _lastPosition);
                if (moved < 1f)
                {
                    // Can't reach this target — blacklist it
                    _blacklist[_currentTarget.GetInstanceID()] = Time.time;
                    StopHarvesting();
                    return;
                }
                _stuckTimer   = 0f;
                _lastPosition = transform.position;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  State: Attacking — face resource and swing tool
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateAttacking()
        {
            if (!IsTargetValid())
            {
                StopHarvesting();
                return;
            }

            float dist = Vector3.Distance(
                transform.position, _currentTarget.transform.position);

            // Drifted out of range — resume moving
            if (dist > AttackRange + 1f)
            {
                _waypoint.transform.position = _currentTarget.transform.position;
                _ai.SetFollowTarget(_waypoint);
                _state = HarvestState.Moving;
                return;
            }

            _attackCooldown -= Time.deltaTime;
            if (_attackCooldown > 0f) return;

            // Check stamina
            if (_stamina != null && !_stamina.UseStamina(StaminaCost))
                return;

            // Face the target (Y-axis only)
            Vector3 dir = _currentTarget.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(dir.normalized);

            // Swing!
            _humanoid.StartAttack(null, false);
            _attackCooldown = AttackCooldown;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  State transitions
        // ═════════════════════════════════════════════════════════════════════

        private void StopHarvesting()
        {
            _currentTarget       = null;
            _currentResourceType = ResourceType.None;
            _state               = HarvestState.Idle;
            _scanTimer           = 0f;

            // Restore auto-equip and follow player
            if (_setup != null) _setup.SuppressAutoEquip = false;
            if (_ai != null && Player.m_localPlayer != null)
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
        }

        /// <summary>Pause without clearing follow — enemy appeared, will resume after.</summary>
        private void PauseHarvesting()
        {
            if (_setup != null) _setup.SuppressAutoEquip = false;
            _state = HarvestState.Idle;
            // Don't reset _currentTarget so we can resume after enemy is gone
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Validation
        // ═════════════════════════════════════════════════════════════════════

        private bool IsTargetValid()
        {
            if (_currentTarget == null) return false;
            // Unity null check for destroyed GameObjects
            if (!_currentTarget) return false;
            if (!_currentTarget.activeInHierarchy) return false;

            var nv = _currentTarget.GetComponent<ZNetView>();
            if (nv != null && nv.GetZDO() == null) return false;

            return true;
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

        // ═════════════════════════════════════════════════════════════════════
        //  Resource scanning
        // ═════════════════════════════════════════════════════════════════════

        private (GameObject target, ResourceType type) FindNearestResource()
        {
            var colliders = Physics.OverlapSphere(transform.position, ScanRange);

            GameObject   best     = null;
            ResourceType bestType = ResourceType.None;
            float        bestDist = float.MaxValue;
            var          seen     = new HashSet<int>();

            foreach (var col in colliders)
            {
                if (col == null) continue;

                var (resGo, resType) = ClassifyResource(col.gameObject);
                if (resGo == null || resType == ResourceType.None) continue;

                int id = resGo.GetInstanceID();
                if (!seen.Add(id)) continue;
                if (_blacklist.ContainsKey(id)) continue;

                // Check we have a tool that meets the tier requirement
                var tool = FindBestTool(resType);
                if (tool == null) continue;
                if (tool.m_shared.m_toolTier < GetMinToolTier(resGo)) continue;

                float dist = Vector3.Distance(transform.position, resGo.transform.position);
                if (dist < bestDist)
                {
                    best     = resGo;
                    bestType = resType;
                    bestDist = dist;
                }
            }

            return (best, bestType);
        }

        internal static (GameObject, ResourceType) ClassifyResource(GameObject go)
        {
            var tree = go.GetComponentInParent<TreeBase>();
            if (tree != null)
            {
                var nv = tree.GetComponent<ZNetView>();
                if (nv != null && nv.GetZDO() != null)
                    return (tree.gameObject, ResourceType.Tree);
            }

            var log = go.GetComponentInParent<TreeLog>();
            if (log != null)
            {
                var nv = log.GetComponent<ZNetView>();
                if (nv != null && nv.GetZDO() != null)
                    return (log.gameObject, ResourceType.Tree);
            }

            var rock = go.GetComponentInParent<MineRock>();
            if (rock != null)
            {
                var nv = rock.GetComponent<ZNetView>();
                if (nv != null && nv.GetZDO() != null)
                    return (rock.gameObject, ResourceType.Rock);
            }

            var rock5 = go.GetComponentInParent<MineRock5>();
            if (rock5 != null)
            {
                var nv = rock5.GetComponent<ZNetView>();
                if (nv != null && nv.GetZDO() != null)
                    return (rock5.gameObject, ResourceType.Rock);
            }

            var destr = go.GetComponentInParent<Destructible>();
            if (destr != null)
            {
                var nv = destr.GetComponent<ZNetView>();
                if (nv == null || nv.GetZDO() == null)
                    return (null, ResourceType.None);

                if (destr.m_damages.m_chop != HitData.DamageModifier.Immune &&
                    destr.m_damages.m_chop != HitData.DamageModifier.Ignore)
                    return (destr.gameObject, ResourceType.Tree);

                if (destr.m_damages.m_pickaxe != HitData.DamageModifier.Immune &&
                    destr.m_damages.m_pickaxe != HitData.DamageModifier.Ignore)
                    return (destr.gameObject, ResourceType.Rock);
            }

            return (null, ResourceType.None);
        }

        private static int GetMinToolTier(GameObject go)
        {
            var tree = go.GetComponent<TreeBase>();
            if (tree != null) return tree.m_minToolTier;

            var log = go.GetComponent<TreeLog>();
            if (log != null) return log.m_minToolTier;

            var rock = go.GetComponent<MineRock>();
            if (rock != null) return rock.m_minToolTier;

            var rock5 = go.GetComponent<MineRock5>();
            if (rock5 != null) return rock5.m_minToolTier;

            var destr = go.GetComponent<Destructible>();
            if (destr != null) return destr.m_minToolTier;

            return 999;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Tool management
        // ═════════════════════════════════════════════════════════════════════

        private ItemDrop.ItemData FindBestTool(ResourceType type)
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return null;

            ItemDrop.ItemData best = null;
            float bestDmg = 0f;

            foreach (var item in inv.GetAllItems())
            {
                float relevant = type == ResourceType.Tree
                    ? item.m_shared.m_damages.m_chop
                    : item.m_shared.m_damages.m_pickaxe;

                if (relevant > bestDmg)
                {
                    best    = item;
                    bestDmg = relevant;
                }
            }

            return best;
        }

        private void EquipToolForHarvest(ItemDrop.ItemData tool)
        {
            if (_setup == null || _humanoid == null) return;

            _setup.SuppressAutoEquip = true;

            // Unequip current right hand if different from tool
            var curRight = _setup.GetEquipSlot(CompanionSetup._rightItemField);
            if (curRight != null && curRight != tool)
                _humanoid.UnequipItem(curRight, false);

            // Unequip left hand if tool is two-handed
            var toolType = tool.m_shared.m_itemType;
            bool is2H = toolType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                        toolType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
            if (is2H)
            {
                var curLeft = _setup.GetEquipSlot(CompanionSetup._leftItemField);
                if (curLeft != null)
                    _humanoid.UnequipItem(curLeft, false);
            }

            if (!_humanoid.IsItemEquiped(tool))
                _humanoid.EquipItem(tool, false);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Blacklist cleanup
        // ═════════════════════════════════════════════════════════════════════

        private void CleanBlacklist()
        {
            var expired = new List<int>();
            float now = Time.time;
            foreach (var kv in _blacklist)
            {
                if (now - kv.Value > BlacklistDuration)
                    expired.Add(kv.Key);
            }
            foreach (var k in expired)
                _blacklist.Remove(k);
        }
    }
}
