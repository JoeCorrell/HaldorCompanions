using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Draws a flat circle ring in world space to visualize the companion's home zone radius.
    /// Attach to the companion GameObject. Call Show() to display, Hide() to remove.
    /// </summary>
    public class HomeZoneVisual : MonoBehaviour
    {
        private LineRenderer _lr;
        private const int    Points    = 64;
        private const float  Width     = 0.08f;
        private static readonly Color RingColor = new Color(1f, 0.9f, 0.2f, 0.8f);

        private void Awake()
        {
            _lr = gameObject.AddComponent<LineRenderer>();
            _lr.useWorldSpace  = true;
            _lr.loop           = true;
            _lr.positionCount  = Points;
            _lr.startWidth     = Width;
            _lr.endWidth       = Width;
            _lr.startColor     = RingColor;
            _lr.endColor       = RingColor;
            _lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _lr.receiveShadows = false;
            _lr.enabled        = false;

            // Use a simple unlit material so the ring is always visible
            _lr.material = new Material(Shader.Find("Sprites/Default"));
        }

        public void Show(Vector3 center, float radius)
        {
            if (_lr == null) return;
            float y = center.y + 0.12f;
            for (int i = 0; i < Points; i++)
            {
                float angle = i * (360f / Points) * Mathf.Deg2Rad;
                _lr.SetPosition(i, new Vector3(
                    center.x + Mathf.Cos(angle) * radius,
                    y,
                    center.z + Mathf.Sin(angle) * radius));
            }
            _lr.enabled = true;
        }

        public void Hide()
        {
            if (_lr != null) _lr.enabled = false;
        }
    }
}
