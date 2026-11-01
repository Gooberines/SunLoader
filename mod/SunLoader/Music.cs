using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Networking;

namespace SunLoader
{
    // Custom songs for bosses. A boss file's "music" names a file in BepInEx\SunLoader\music.
    // WAV is decoded here (always works); OGG and MP3 go through Unity's loader, which this
    // game's build may not include.
    static class Music
    {
        public static string Dir => Path.Combine(Plugin.Root, "music");
        static readonly Dictionary<string, AudioClip> clips = new();
        static readonly Dictionary<string, UnityWebRequest> loading = new();
        static readonly HashSet<string> failed = new();

        public static string[] Files()
        {
            Directory.CreateDirectory(Dir);
            return Directory.GetFiles(Dir).Select(Path.GetFileName)
                .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f).ToArray();
        }

        // The song set in a level's boss file, or null.
        public static string ForLevel(string level)
        {
            var path = Path.Combine(Plugin.Root, level + ".json");
            if (!File.Exists(path)) return null;
            try { return (string)JsonNode.Parse(File.ReadAllText(path))?["music"]; } catch { return null; }
        }

        // Returns the clip if it's loaded; otherwise starts loading it (WAV loads right away).
        public static AudioClip Get(string file)
        {
            if (string.IsNullOrEmpty(file) || failed.Contains(file)) return null;
            if (clips.TryGetValue(file, out var clip) && clip != null) return clip;
            var path = Path.Combine(Dir, file);
            if (!File.Exists(path)) { Fail(file, "file not found in " + Dir); return null; }
            try
            {
                if (file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    clip = LoadWav(path);
                    Keep(file, clip);
                    return clip;
                }
                if (!loading.ContainsKey(file))
                {
                    var type = file.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ? AudioType.OGGVORBIS : AudioType.MPEG;
                    var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, type);
                    req.SendWebRequest();
                    loading[file] = req;
                }
            }
            catch (Exception e) { Fail(file, e.Message + " (try a .wav file instead)"); }
            return null;
        }

        public static bool IsLoading(string file) => file != null && loading.ContainsKey(file);
        public static bool HasFailed(string file) => file != null && failed.Contains(file);

        // Called every frame to finish OGG/MP3 loads.
        public static void Update()
        {
            foreach (var (file, req) in loading.ToList())
            {
                try
                {
                    if (!req.isDone) continue;
                    loading.Remove(file);
                    var clip = DownloadHandlerAudioClip.GetContent(req);
                    if (clip == null || clip.length <= 0) Fail(file, "Unity couldn't decode it (try a .wav file)");
                    else Keep(file, clip);
                    req.Dispose();
                }
                catch (Exception e) { loading.Remove(file); Fail(file, e.Message + " (try a .wav file instead)"); }
            }
        }

        // Starts loading every song used by a boss file, so they're ready when a level loads.
        public static void Preload()
        {
            if (!Directory.Exists(Plugin.Root)) return;
            foreach (var path in Directory.GetFiles(Plugin.Root, "*.json"))
                Get(ForLevel(Path.GetFileNameWithoutExtension(path)));
            foreach (var boss in CustomBosses.List()) Get(boss.Music);
        }

        static void Keep(string file, AudioClip clip)
        {
            clip.hideFlags = HideFlags.HideAndDontSave;   // don't let scene changes unload it
            clips[file] = clip;
            Plugin.Logger.LogInfo($"Music loaded: {file} ({clip.length:0.0}s)");
        }

        static void Fail(string file, string why)
        {
            if (failed.Add(file)) Plugin.Logger.LogWarning($"Couldn't load music '{file}': {why}");
        }

        static AudioClip LoadWav(string path)
        {
            var data = File.ReadAllBytes(path);
            if (data.Length < 44 || BitConverter.ToUInt32(data, 0) != 0x46464952) throw new Exception("not a WAV file");
            int pos = 12, channels = 0, rate = 0, bits = 0, format = 0;
            while (pos + 8 <= data.Length)
            {
                var id = System.Text.Encoding.ASCII.GetString(data, pos, 4);
                var size = BitConverter.ToInt32(data, pos + 4);
                var body = pos + 8;
                if (id == "fmt ")
                {
                    format = BitConverter.ToUInt16(data, body);
                    channels = BitConverter.ToUInt16(data, body + 2);
                    rate = BitConverter.ToInt32(data, body + 4);
                    bits = BitConverter.ToUInt16(data, body + 14);
                    if (format == 0xFFFE && size >= 26) format = BitConverter.ToUInt16(data, body + 24);   // WAVE_FORMAT_EXTENSIBLE
                }
                else if (id == "data")
                {
                    if (channels == 0) throw new Exception("WAV has no format chunk");
                    size = Math.Min(size, data.Length - body);
                    var samples = Decode(data, body, size, bits, format);
                    var clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), samples.Length / channels, channels, rate, false);
                    clip.SetData(new Il2CppStructArray<float>(samples), 0);
                    return clip;
                }
                pos = body + size + (size & 1);
            }
            throw new Exception("WAV has no data chunk");
        }

        static float[] Decode(byte[] d, int start, int size, int bits, int format)
        {
            int bytes = bits / 8, n = size / bytes;
            var s = new float[n];
            for (int i = 0, p = start; i < n; i++, p += bytes)
                s[i] = (format, bits) switch
                {
                    (3, 32) => BitConverter.ToSingle(d, p),
                    (_, 16) => BitConverter.ToInt16(d, p) / 32768f,
                    (_, 24) => ((d[p] | d[p + 1] << 8 | (sbyte)d[p + 2] << 16)) / 8388608f,
                    (_, 32) => BitConverter.ToInt32(d, p) / 2147483648f,
                    (_, 8) => (d[p] - 128) / 128f,
                    _ => throw new Exception($"unsupported WAV format ({bits}-bit, type {format})"),
                };
            return s;
        }
    }

    // When a level's song is set, swap in the boss's song if it has one: the Bosses-menu boss
    // playing on that level, otherwise the level's boss file.
    [HarmonyPatch(typeof(ModelStoryCheckpoint), nameof(ModelStoryCheckpoint.SetAudioClip))]
    static class SetAudioClipPatch
    {
        static bool swapping;
        // Each level's own song, kept while ours is swapped in so it can be put back.
        static readonly Dictionary<IntPtr, AudioClip> originals = new();

        static void Postfix(ModelStoryCheckpoint __instance)
        {
            if (swapping || !SafeRead.IsObjectOf<ModelStoryCheckpoint>(__instance.Pointer)) return;
            // The game just set the level's own song.
            var clip = SafeRead.Field<AudioClip>(__instance, "audioClip");
            if (clip != null) { clip.hideFlags = HideFlags.HideAndDontSave; originals[__instance.Pointer] = clip; }
            Apply(__instance);
        }

        // Gives the level the song it should have now. Also used when a Bosses-menu boss starts,
        // because a level whose files are already loaded doesn't set its song again.
        public static void Apply(ModelStoryCheckpoint level)
        {
            try
            {
                string name = null;
                try { name = level.name; } catch { }
                name = SetMetaSongPatch.FileId(name, level.nId);
                var boss = CustomBosses.Active ?? (LobbySync.InLobby ? LobbySync.Boss : null);
                var file = boss != null && boss.Stage == name ? boss.Music : Music.ForLevel(name);
                AudioClip clip;
                if (file == null)
                {
                    if (!originals.TryGetValue(level.Pointer, out clip) || clip == null) return;
                }
                else
                {
                    clip = Music.Get(file);
                    if (clip == null)
                    {
                        Plugin.Logger.LogWarning($"Custom music '{file}' for '{name}' isn't ready" + (Music.IsLoading(file) ? " yet (still loading; restart the level)" : ""));
                        return;
                    }
                    if (!originals.ContainsKey(level.Pointer))
                    {
                        var own = SafeRead.Field<AudioClip>(level, "audioClip");
                        if (own != null) { own.hideFlags = HideFlags.HideAndDontSave; originals[level.Pointer] = own; }
                    }
                }
                swapping = true;
                level.SetAudioClip(clip);
                Plugin.Logger.LogInfo($"Playing '{file ?? "its own song"}' for '{name}'");
            }
            catch (Exception e) { Plugin.Logger.LogError("Custom music failed: " + e); }
            finally { swapping = false; }
        }

        // After a Bosses-menu boss: every level gets its usual song back.
        public static void RestoreOriginals()
        {
            foreach (var ptr in originals.Keys.ToList())
                if (SafeRead.IsObjectOf<ModelStoryCheckpoint>(ptr)) Apply(new ModelStoryCheckpoint(ptr));
        }
    }

    // No-damage mode for testing bosses. The game's hit handlers are skipped while it's on.
    [HarmonyPatch]
    static class NoDamage
    {
        public static bool On;

        [HarmonyPrefix, HarmonyPatch(typeof(HeroCollisionWithEnemy), nameof(HeroCollisionWithEnemy.hitByEnemy))]
        static bool HitByEnemy(HeroCollisionWithEnemy __instance) => !Skip(__instance);

        [HarmonyPrefix, HarmonyPatch(typeof(HeroCollisionWithEnemy), nameof(HeroCollisionWithEnemy.hitByOtherActor))]
        static bool HitByOther(HeroCollisionWithEnemy __instance) => !Skip(__instance);

        // Folded native functions can call these hooks for other objects; only skip real hero hits.
        static bool Skip(HeroCollisionWithEnemy hero) => On && SafeRead.IsObjectOf<HeroCollisionWithEnemy>(hero.Pointer);
    }
}
