using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Companions
{
    /// <summary>
    /// Draws flat rectangular outlines in world space to visualize farm zones.
    /// Attach to the companion GameObject. Dynamically scales to any number of zones.
    /// </summary>
    public class FarmZoneVisual : MonoBehaviour
    {
        private readonly List<LineRenderer> _borders = new List<LineRenderer>();
        private Material       _borderMaterial;
        private const float    Width = 0.06f;
        private static readonly Color ZoneColor = new Color(0.3f, 0.85f, 0.3f, 0.7f);

        private void Awake()
        {
            _borderMaterial = new Material(Shader.Find("Sprites/Default"));
        }

        private void OnDestroy()
        {
            if (_borderMaterial != null)
                Destroy(_borderMaterial);
        }

        private LineRenderer CreateBorder(int index)
        {
            var go = new GameObject($"FarmZoneBorder_{index}");
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = true;
            lr.positionCount = 4;
            lr.startWidth = Width;
            lr.endWidth = Width;
            lr.material = _borderMaterial;
            lr.startColor = ZoneColor;
            lr.endColor = ZoneColor;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;
            return lr;
        }

        private void EnsureCapacity(int count)
        {
            while (_borders.Count < count)
                _borders.Add(CreateBorder(_borders.Count));
        }

        internal void ShowZones(List<FarmZone> zones)
        {
            int needed = zones?.Count ?? 0;
            EnsureCapacity(needed);

            for (int i = 0; i < _borders.Count; i++)
            {
                if (zones != null && i < zones.Count && zones[i].IsValid)
                {
                    var z = zones[i];
                    float y = z.Center.y + 0.12f;
                    Vector3 c0, c1, c2, c3;
                    z.GetCorners(out c0, out c1, out c2, out c3);
                    c0.y = y; c1.y = y; c2.y = y; c3.y = y;
                    _borders[i].SetPosition(0, c0);
                    _borders[i].SetPosition(1, c1);
                    _borders[i].SetPosition(2, c2);
                    _borders[i].SetPosition(3, c3);
                    _borders[i].enabled = true;
                }
                else
                {
                    _borders[i].enabled = false;
                }
            }
        }

        internal void HideAll()
        {
            for (int i = 0; i < _borders.Count; i++)
                if (_borders[i] != null) _borders[i].enabled = false;
        }
    }
}
