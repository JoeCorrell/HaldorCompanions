using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Post-harvest drop scanning and collection. Finds nearby item drops
    /// and navigates the companion to pick them up.
    /// Per-instance scan buffer for multi-companion safety.
    /// </summary>
    internal class DropCollector
    {
        private readonly Humanoid _humanoid;
        private readonly Transform _transform;

        private int _itemMask = -1;
        private readonly Collider[] _scanBuffer = new Collider[192];
        private readonly HashSet<int> _seenIds = new HashSet<int>();

        private const float SearchRadius    = 6f;
        private const float MoveRange       = 1.8f;
        private const float PickupInterval  = 0.1f;
        private const float MaxDuration     = 6f;
        private const float RangeCap        = 10f;
        private const int EmptyCheckGoal    = 3;

        // State for active collection
        private float _timer;
        private float _pickupTimer;
        private int   _emptyChecks;
        private Vector3 _center;

        internal DropCollector(Humanoid humanoid, Transform transform)
        {
            _humanoid  = humanoid;
            _transform = transform;
        }

        internal void Init()
        {
            _itemMask = LayerMask.GetMask("item");
            if (_itemMask == 0) _itemMask = -1;
        }

        internal void ResetState(Vector3 center)
        {
            _timer       = 0f;
            _pickupTimer = 0f;
            _emptyChecks = 0;
            _center      = center;
        }

        /// <summary>
        /// Check if there are drops worth collecting near the given center.
        /// Returns true if collection should be started.
        /// </summary>
        internal bool HasDropsNear(Vector3 center)
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return false;
            if (!HasInventoryCapacity(inv)) return false;

            bool blocked = false;
            int pending = 0;
            float nearestDist = float.MaxValue;
            FindNearest(center, inv, ref blocked, ref pending, ref nearestDist);
            return pending > 0;
        }

        /// <summary>
        /// Update active drop collection. Returns false when collection should stop.
        /// outNearest is the nearest pickupable drop (for waypoint targeting).
        /// </summary>
        internal bool Update(float dt, Inventory inv, MonsterAI ai, GameObject waypoint,
                             out bool inventoryFull, out ItemDrop nearest)
        {
            inventoryFull = false;
            nearest = null;

            _timer       += dt;
            _pickupTimer -= dt;

            if (_timer > MaxDuration) return false;

            bool blockedByCapacity = false;
            int pendingCount = 0;
            float nearestDist = float.MaxValue;
            nearest = FindNearest(_center, inv, ref blockedByCapacity, ref pendingCount, ref nearestDist);

            if (pendingCount <= 0)
            {
                _emptyChecks++;
                if (_emptyChecks >= EmptyCheckGoal) return false;
                return true;
            }
            _emptyChecks = 0;

            if (blockedByCapacity && nearest == null)
            {
                inventoryFull = true;
                return false;
            }

            if (nearest == null) return true;

            // LOS check
            Vector3 from = _transform.position + Vector3.up * 0.5f;
            if (HarvestNavigation.IsLineOfSightBlocked(from, nearest.transform.position, nearest.transform))
                return true;

            if (waypoint != null)
                waypoint.transform.position = nearest.transform.position;
            if (ai != null && ai.GetFollowTarget() != waypoint)
                ai.SetFollowTarget(waypoint);

            float dist = Vector3.Distance(from, nearest.transform.position);
            if (dist > MoveRange) return true;

            ai?.StopMoving();
            if (_pickupTimer > 0f) return true;
            _pickupTimer = PickupInterval;

            if (!nearest.CanPickup()) { nearest.RequestOwn(); return true; }
            _humanoid.Pickup(nearest.gameObject);
            return true;
        }

        internal ItemDrop FindNearest(
            Vector3 center, Inventory inv,
            ref bool blockedByCapacity, ref int pendingCount, ref float nearestDist)
        {
            ItemDrop nearest = null;
            Vector3 scanCenter = center + Vector3.up;
            int colliderCount = Physics.OverlapSphereNonAlloc(
                scanCenter, SearchRadius, _scanBuffer, _itemMask);
            if (colliderCount > _scanBuffer.Length) colliderCount = _scanBuffer.Length;

            _seenIds.Clear();

            for (int i = 0; i < colliderCount; i++)
            {
                var col = _scanBuffer[i];
                if (col == null || col.attachedRigidbody == null) continue;

                var itemDrop = col.attachedRigidbody.GetComponent<ItemDrop>();
                if (itemDrop == null) continue;
                if (!_seenIds.Add(itemDrop.GetInstanceID())) continue;

                var itemNview = itemDrop.GetComponent<ZNetView>();
                if (itemNview == null || !itemNview.IsValid()) continue;
                if (itemDrop.m_itemData == null) continue;

                float distFromCenter = Vector3.Distance(center, itemDrop.transform.position);
                if (distFromCenter > RangeCap) continue;

                pendingCount++;

                if (!itemDrop.CanPickup())
                {
                    itemDrop.RequestOwn();
                    continue;
                }

                bool canAdd = inv.CanAddItem(itemDrop.m_itemData);
                bool canCarry = itemDrop.m_itemData.GetWeight() +
                    inv.GetTotalWeight() <= CompanionTierData.MaxCarryWeight;
                if (!canAdd || !canCarry)
                {
                    blockedByCapacity = true;
                    continue;
                }

                float dist = Vector3.Distance(
                    _transform.position + Vector3.up, itemDrop.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = itemDrop;
                }
            }
            return nearest;
        }

        internal static bool HasInventoryCapacity(Inventory inv)
        {
            if (inv == null) return false;
            if (inv.HaveEmptySlot()) return true;
            foreach (var item in inv.GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                if (item.m_stack < item.m_shared.m_maxStackSize) return true;
            }
            return false;
        }
    }
}
