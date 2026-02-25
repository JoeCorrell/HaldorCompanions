using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Automatic item pickup within 2m range when enabled.
    /// Persisted via ZDO. Per-instance scan buffer for multi-companion safety.
    /// </summary>
    internal class AutoPickup
    {
        private readonly Humanoid _humanoid;
        private readonly ZNetView _nview;
        private readonly Transform _transform;

        private bool  _enabled;
        private float _pickupTimer;
        private int   _itemMask = -1;
        private readonly Collider[] _scanBuffer = new Collider[64];

        private const float Range    = 2f;
        private const float Interval = 0.25f;

        internal bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (_nview == null) return;
                var zdo = _nview.GetZDO();
                if (zdo == null) return;
                if (!_nview.IsOwner())
                {
                    _nview.ClaimOwnership();
                    if (!_nview.IsOwner()) return;
                }
                zdo.Set(CompanionSetup.AutoPickupHash, value);
            }
        }

        internal AutoPickup(Humanoid humanoid, ZNetView nview, Transform transform)
        {
            _humanoid  = humanoid;
            _nview     = nview;
            _transform = transform;
        }

        internal void Init()
        {
            _itemMask = LayerMask.GetMask("item");
            if (_itemMask == 0) _itemMask = -1;

            if (_nview != null && _nview.GetZDO() != null)
                _enabled = _nview.GetZDO().GetBool(CompanionSetup.AutoPickupHash, false);
        }

        internal void Update(float dt)
        {
            if (!_enabled || _humanoid == null) return;

            _pickupTimer -= dt;
            if (_pickupTimer > 0f) return;
            _pickupTimer = Interval;

            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            Vector3 center = _transform.position + Vector3.up;
            float currentWeight = inv.GetTotalWeight();
            int hitCount = Physics.OverlapSphereNonAlloc(
                center, Range, _scanBuffer, _itemMask);
            if (hitCount > _scanBuffer.Length) hitCount = _scanBuffer.Length;

            for (int i = 0; i < hitCount; i++)
                TryPickup(_scanBuffer[i], inv, center, ref currentWeight);
        }

        private void TryPickup(Collider col, Inventory inv, Vector3 center, ref float weight)
        {
            if (col == null || col.attachedRigidbody == null) return;

            var itemDrop = col.attachedRigidbody.GetComponent<ItemDrop>();
            if (itemDrop == null || !itemDrop.m_autoPickup || itemDrop.m_itemData == null) return;

            var itemNview = itemDrop.GetComponent<ZNetView>();
            if (itemNview == null || !itemNview.IsValid()) return;

            if (!itemDrop.CanPickup())
            {
                itemDrop.RequestOwn();
                return;
            }

            if (!inv.CanAddItem(itemDrop.m_itemData)) return;

            float itemWeight = itemDrop.m_itemData.GetWeight();
            if (itemWeight + weight > CompanionTierData.MaxCarryWeight) return;

            float distSq = (itemDrop.transform.position - center).sqrMagnitude;
            if (distSq >= Range * Range) return;

            _humanoid.Pickup(itemDrop.gameObject);
            weight = inv.GetTotalWeight();
        }
    }
}
