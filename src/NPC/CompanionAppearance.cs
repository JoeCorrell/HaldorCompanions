using System.Globalization;
using UnityEngine;

namespace Companions
{
    /// <summary>Stores all visual appearance data for a companion NPC.</summary>
    public class CompanionAppearance
    {
        public int     ModelIndex;  // 0 = male, 1 = female
        public string  HairItem;   // e.g. "Hair1"
        public string  BeardItem;  // e.g. "Beard1" or "" for none / female
        public Vector3 SkinColor;  // RGB as Vector3
        public Vector3 HairColor;  // RGB as Vector3

        public static CompanionAppearance Default() => new CompanionAppearance
        {
            ModelIndex = 0,
            HairItem   = "Hair1",
            BeardItem  = "Beard1",
            SkinColor  = Utils.ColorToVec3(new Color(0.95f, 0.82f, 0.68f)),
            HairColor  = Utils.ColorToVec3(new Color(0.55f, 0.38f, 0.18f)),
        };

        public string Serialize()
        {
            var ic = CultureInfo.InvariantCulture;
            return string.Join(";", new[]
            {
                ModelIndex.ToString(ic),
                HairItem  ?? "",
                BeardItem ?? "",
                SkinColor.x.ToString("F3", ic),
                SkinColor.y.ToString("F3", ic),
                SkinColor.z.ToString("F3", ic),
                HairColor.x.ToString("F3", ic),
                HairColor.y.ToString("F3", ic),
                HairColor.z.ToString("F3", ic),
            });
        }

        public static CompanionAppearance Deserialize(string data)
        {
            if (string.IsNullOrEmpty(data)) return Default();
            try
            {
                var p = data.Split(';');
                if (p.Length < 9) return Default();
                var ic = CultureInfo.InvariantCulture;
                return new CompanionAppearance
                {
                    ModelIndex = int.Parse(p[0], ic),
                    HairItem   = p[1],
                    BeardItem  = p[2],
                    SkinColor  = new Vector3(float.Parse(p[3], ic), float.Parse(p[4], ic), float.Parse(p[5], ic)),
                    HairColor  = new Vector3(float.Parse(p[6], ic), float.Parse(p[7], ic), float.Parse(p[8], ic)),
                };
            }
            catch { return Default(); }
        }
    }
}
