using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

namespace Companions
{
    /// <summary>
    /// Loads voice audio clips from disk and plays them on companion AudioSources.
    /// Clips are organized by voice pack and category: Audio/{VoicePack}/{Category}/*.mp3
    /// </summary>
    public class CompanionVoice : MonoBehaviour
    {
        public static CompanionVoice Instance { get; private set; }

        // voicePack -> category -> clips
        private readonly Dictionary<string, Dictionary<string, List<AudioClip>>> _voices
            = new Dictionary<string, Dictionary<string, List<AudioClip>>>();

        private bool _loaded;

        private void Awake()
        {
            Instance = this;
            StartCoroutine(LoadAllClips());
        }

        private IEnumerator LoadAllClips()
        {
            string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string audioDir = Path.Combine(dllDir, "Audio");

            if (!Directory.Exists(audioDir))
            {
                CompanionsPlugin.Log.LogInfo("[Voice] No Audio directory found, skipping clip loading.");
                _loaded = true;
                yield break;
            }

            int totalLoaded = 0;

            foreach (var voiceDir in Directory.GetDirectories(audioDir))
            {
                string voiceName = Path.GetFileName(voiceDir);
                var categories = new Dictionary<string, List<AudioClip>>();

                foreach (var catDir in Directory.GetDirectories(voiceDir))
                {
                    string category = Path.GetFileName(catDir);
                    var clips = new List<AudioClip>();
                    var files = Directory.GetFiles(catDir, "*.mp3");
                    System.Array.Sort(files);

                    foreach (var file in files)
                    {
                        string uri = "file:///" + file.Replace('\\', '/');
                        using (var request = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG))
                        {
                            yield return request.SendWebRequest();

                            if (string.IsNullOrEmpty(request.error))
                            {
                                var clip = DownloadHandlerAudioClip.GetContent(request);
                                clip.name = Path.GetFileNameWithoutExtension(file);
                                clips.Add(clip);
                                totalLoaded++;
                            }
                            else
                            {
                                CompanionsPlugin.Log.LogWarning(
                                    $"[Voice] Failed to load {file}: {request.error}");
                            }
                        }
                    }

                    if (clips.Count > 0)
                        categories[category] = clips;
                }

                if (categories.Count > 0)
                    _voices[voiceName] = categories;
            }

            _loaded = true;
            CompanionsPlugin.Log.LogInfo($"[Voice] Loaded {totalLoaded} audio clips.");
        }

        /// <summary>
        /// Play a random clip from the given voice pack and category on the AudioSource.
        /// </summary>
        public void PlayRandom(AudioSource source, string voicePack, string category)
        {
            if (!_loaded || source == null) return;

            List<AudioClip> clips = null;

            // Try the requested voice pack first
            if (_voices.TryGetValue(voicePack, out var cats))
                cats.TryGetValue(category, out clips);

            // Fallback: if the requested pack or category has no clips,
            // try MaleCompanion as a default (covers FemaleCompanion before
            // dedicated female audio files are added).
            if ((clips == null || clips.Count == 0) && voicePack != "MaleCompanion")
            {
                if (_voices.TryGetValue("MaleCompanion", out var fallback))
                    fallback.TryGetValue(category, out clips);
            }

            if (clips == null || clips.Count == 0) return;

            var clip = clips[Random.Range(0, clips.Count)];
            source.Stop();
            source.clip = clip;
            source.Play();
        }
    }
}
