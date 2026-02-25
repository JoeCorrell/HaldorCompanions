using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Loads PNG textures embedded in the assembly as EmbeddedResources.
    /// Uses reflection to call ImageConversion.LoadImage (avoids netstandard version mismatch).
    /// Copied line-for-line from TraderOverhaul.TextureLoader with namespace adapted.
    /// </summary>
    public static class TextureLoader
    {
        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();
        private static readonly Assembly ModAssembly = Assembly.GetExecutingAssembly();

        private static readonly MethodInfo LoadImageMethod = ResolveLoadImage();

        private static MethodInfo ResolveLoadImage()
        {
            var type = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
            return type?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
        }

        /// <summary>
        /// Load a UI texture by name (e.g. "CategoryBackground").
        /// </summary>
        public static Texture2D LoadUITexture(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            string cacheKey = "UI_" + name;
            if (Cache.TryGetValue(cacheKey, out Texture2D cached))
                return cached;

            if (LoadImageMethod == null)
            {
                CompanionsPlugin.Log.LogWarning("TextureLoader: ImageConversion.LoadImage not found via reflection.");
                return null;
            }

            string resourceName = $"Companions.Resources.Textures.UI.{name}.png";
            using (Stream stream = ModAssembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    CompanionsPlugin.Log.LogWarning($"TextureLoader: UI resource '{resourceName}' not found.");
                    return null;
                }

                byte[] data = new byte[stream.Length];
                stream.Read(data, 0, data.Length);

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                bool loaded = (bool)LoadImageMethod.Invoke(null, new object[] { tex, data });
                if (loaded)
                {
                    tex.name = name;
                    Cache[cacheKey] = tex;
                    return tex;
                }

                UnityEngine.Object.Destroy(tex);
                return null;
            }
        }
    }
}
