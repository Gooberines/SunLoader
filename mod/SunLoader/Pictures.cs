using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SunLoader
{
    // Real pictures of attacks: screenshots taken while an attack is previewed, saved as
    // BepInEx\SunLoader\pictures\<type>.png. Also remembers attacks that crashed the
    // game while being captured, so "Capture all" skips them next time.
    static class Pictures
    {
        static string Dir => Path.Combine(Plugin.Root, "pictures");
        static string TryingFile => Path.Combine(Dir, "_trying.txt");
        static string CrashedFile => Path.Combine(Dir, "_crashed.txt");
        static readonly Dictionary<string, Texture2D> cache = new();
        static readonly HashSet<string> missing = new();
        static HashSet<string> crashed;
        public static bool Broken;   // the game build can't capture or load images

        public static Texture2D Get(string type)
        {
            if (Broken || type == null || missing.Contains(type)) return null;
            if (cache.TryGetValue(type, out var tex) && tex != null) return tex;
            var path = Path.Combine(Dir, type + ".png");
            if (!File.Exists(path)) { missing.Add(type); return null; }
            try
            {
                tex = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
                ImageConversion.LoadImage(tex, new Il2CppStructArray<byte>(File.ReadAllBytes(path)));
                return cache[type] = tex;
            }
            catch (Exception e) { Broken = true; Plugin.Logger.LogWarning("Can't load attack pictures in this game build: " + e.Message); return null; }
        }

        public static bool Has(string type) => File.Exists(Path.Combine(Dir, type + ".png"));

        // Copies the middle of the screen. Call during an OnGUI repaint, before drawing over it.
        public static void Capture(string type)
        {
            if (Broken) return;
            try
            {
                int w = (int)(Screen.width * 0.45f), h = (int)(Screen.height * 0.45f);
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                tex.ReadPixels(new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h), 0, 0);
                tex.Apply();
                Directory.CreateDirectory(Dir);
                File.WriteAllBytes(Path.Combine(Dir, type + ".png"), ImageConversion.EncodeToPNG(tex).ToArray());
                cache[type] = tex;
                missing.Remove(type);
            }
            catch (Exception e) { Broken = true; Plugin.Logger.LogWarning("Can't capture attack pictures in this game build: " + e.Message); }
        }

        // ---------- crash tracking for "Capture all" ----------

        public static bool Crashed(string type) { LoadCrashed(); return crashed.Contains(type); }

        public static void Trying(string type)
        {
            Directory.CreateDirectory(Dir);
            if (type == null) { if (File.Exists(TryingFile)) File.Delete(TryingFile); }
            else File.WriteAllText(TryingFile, type);
        }

        static void LoadCrashed()
        {
            if (crashed != null) return;
            crashed = File.Exists(CrashedFile) ? File.ReadAllLines(CrashedFile).Where(l => l.Length > 0).ToHashSet() : new();
            // A capture that never finished means the game died during it.
            if (File.Exists(TryingFile))
            {
                var type = File.ReadAllText(TryingFile).Trim();
                if (type.Length > 0 && crashed.Add(type))
                {
                    File.AppendAllLines(CrashedFile, new[] { type });
                    Plugin.Logger.LogWarning($"'{type}' crashed the game during capture; it will be skipped.");
                }
                File.Delete(TryingFile);
            }
        }
    }
}
