using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Square farm zone with optional Y-axis rotation.
    /// Center + half-extent + rotation define bounds.
    /// CropSeed identifies which seed prefab to plant exclusively in this zone.
    /// </summary>
    internal struct FarmZone
    {
        public Vector3 Center;
        public float   HalfSize;    // half the side length (zone = HalfSize*2 square)
        public string  CropSeed;    // seed prefab name e.g. "CarrotSeeds", "" = any
        public float   Rotation;    // degrees around Y axis (0 = axis-aligned)

        public bool IsValid => HalfSize > 0f;

        /// <summary>
        /// Returns true if pos is inside the (possibly rotated) square zone.
        /// Transforms pos into the zone's local space then does axis-aligned check.
        /// </summary>
        public bool Contains(Vector3 pos)
        {
            float lx, lz;
            WorldToLocal(pos, out lx, out lz);
            return lx >= -HalfSize && lx <= HalfSize && lz >= -HalfSize && lz <= HalfSize;
        }

        /// <summary>Get the 4 world-space corners of the zone.</summary>
        public void GetCorners(out Vector3 c0, out Vector3 c1, out Vector3 c2, out Vector3 c3)
        {
            float hs = HalfSize;
            c0 = LocalToWorld(-hs, -hs);
            c1 = LocalToWorld( hs, -hs);
            c2 = LocalToWorld( hs,  hs);
            c3 = LocalToWorld(-hs,  hs);
        }

        /// <summary>Convert local-space offset (relative to center) to world position.</summary>
        public Vector3 LocalToWorld(float lx, float lz)
        {
            float rad = Rotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);
            return new Vector3(
                Center.x + lx * cos - lz * sin,
                Center.y,
                Center.z + lx * sin + lz * cos);
        }

        /// <summary>Convert world position to local-space offset (relative to center).</summary>
        public void WorldToLocal(Vector3 pos, out float lx, out float lz)
        {
            float dx = pos.x - Center.x;
            float dz = pos.z - Center.z;
            float rad = -Rotation * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            float sin = Mathf.Sin(rad);
            lx = dx * cos - dz * sin;
            lz = dx * sin + dz * cos;
        }
    }

    /// <summary>
    /// Serializes/deserializes FarmZone arrays to/from companion ZDO strings.
    /// Format: "cx,cy,cz,hs,crop,rot|cx,cy,cz,hs,crop,rot|..."
    /// </summary>
    internal static class FarmZoneSerializer
    {
        internal static readonly int FarmZonesHash =
            "HC_FarmZones".GetStableHashCode();

        internal static void Save(ZDO zdo, List<FarmZone> zones)
        {
            if (zdo == null) return;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < zones.Count; i++)
            {
                if (!zones[i].IsValid) continue;
                if (sb.Length > 0) sb.Append('|');
                var z = zones[i];
                sb.Append(z.Center.x.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(z.Center.y.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(z.Center.z.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(z.HalfSize.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(z.CropSeed ?? "");
                sb.Append(',');
                sb.Append(z.Rotation.ToString(CultureInfo.InvariantCulture));
            }
            zdo.Set(FarmZonesHash, sb.ToString());
        }

        internal static List<FarmZone> Load(ZDO zdo)
        {
            var list = new List<FarmZone>(8);
            if (zdo == null) return list;
            string raw = zdo.GetString(FarmZonesHash, "");
            if (string.IsNullOrEmpty(raw)) return list;

            string[] parts = raw.Split('|');
            foreach (string part in parts)
            {
                string[] f = part.Split(',');
                if (f.Length < 4) continue;
                float cx, cy, cz, hs;
                if (!float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out cx)) continue;
                if (!float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out cy)) continue;
                if (!float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out cz)) continue;
                if (!float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out hs)) continue;
                string crop = f.Length > 4 ? f[4] : "";
                float rot = 0f;
                if (f.Length > 5)
                    float.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out rot);
                list.Add(new FarmZone {
                    Center = new Vector3(cx, cy, cz),
                    HalfSize = hs,
                    CropSeed = crop,
                    Rotation = rot
                });
            }
            return list;
        }
    }
}
