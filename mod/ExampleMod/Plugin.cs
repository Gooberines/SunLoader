using System;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace ExampleMod
{
    // An example Sun Loader mod. It shows a small on-screen panel during levels with the
    // level's name, how long you've been playing it, and how many times you've been hit.
    // Press F3 to show or hide it.
    //
    // Each part demonstrates something most mods need:
    //   Plugin        - the entry point, config settings, starting Harmony and a component
    //   Guard         - the safety check every Harmony hook in this game needs
    //   LevelLoaded   - a postfix hook that reads data (the level's name)
    //   LevelStarted  - a postfix hook that reacts to an event (a level starting)
    //   PlayerHit     - a prefix hook that can change what the game does
    //   Overlay       - a MonoBehaviour for per-frame code (Update) and drawing (OnGUI)
    //   Hud           - the mod's own state and drawing code
    //
    // Read README.md next to this file for the full guide.

    // The GUID must be unique. Name and version show in the mod menu.
    [BepInPlugin("example.jsb.hello", "Example Mod", "2.0.1")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource Logger;
        internal static ConfigEntry<KeyCode> ToggleKey;
        internal static ConfigEntry<bool> ShowPanel;
        internal static ConfigEntry<bool> IgnoreHits;

        public override void Load()
        {
            Logger = Log;

            // Settings live in BepInEx\config\example.jsb.hello.cfg and can be edited there.
            ToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F3, "Shows or hides the panel.");
            ShowPanel = Config.Bind("General", "ShowPanel", true, "Show the panel during levels.");
            IgnoreHits = Config.Bind("Cheats", "IgnoreHits", false, "Don't take damage (the hit is still counted).");

            // Finds every [HarmonyPatch] class in this DLL and applies it.
            new Harmony("example.jsb.hello").PatchAll();

            // Adds our MonoBehaviour to a hidden object so Unity calls its Update and OnGUI.
            AddComponent<Overlay>();

            Log.LogInfo("Example Mod loaded. Press " + ToggleKey.Value + " in a level.");
        }
    }

    // IL2CPP merges identical native functions, so a hook on one class's method can also fire
    // when the game calls a *different* function that shares that code. Then __instance may
    // be another class, or not an object at all, and touching it crashes the game with no
    // error. TryCast isn't enough: it reads the pointer before checking it. This checks the
    // memory is readable first, then that the object's class is (or derives from) T.
    static class Guard
    {
        public static bool Is<T>(Il2CppObjectBase obj) where T : Il2CppObjectBase
        {
            if (obj == null) return false;
            var ptr = obj.Pointer;
            if (!Readable(ptr, 16)) return false;
            var klass = Marshal.ReadIntPtr(ptr);                     // first field of every object: its class
            if (!Readable(klass, 64)) return false;
            var expected = Il2CppClassPointerStore<T>.NativeClassPtr;
            return expected != IntPtr.Zero && IL2CPP.il2cpp_class_is_assignable_from(expected, klass);
        }

        // Asks Windows whether [ptr, ptr+size) is committed, readable memory.
        static bool Readable(IntPtr ptr, int size)
        {
            if (ptr == IntPtr.Zero || VirtualQuery(ptr, out var m, (UIntPtr)Marshal.SizeOf<MemoryInfo>()) == UIntPtr.Zero) return false;
            const uint Commit = 0x1000, NoAccess = 0x01, GuardPage = 0x100, AnyRead = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
            if (m.State != Commit || (m.Protect & (NoAccess | GuardPage)) != 0 || (m.Protect & AnyRead) == 0) return false;
            return (long)ptr + size <= (long)m.BaseAddress + (long)m.RegionSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MemoryInfo
        {
            public IntPtr BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State, Protect, Type;
        }

        [DllImport("kernel32.dll")]
        static extern UIntPtr VirtualQuery(IntPtr address, out MemoryInfo info, UIntPtr length);
    }

    // Postfix: runs after the game's method. Here, when a level is loaded from disk.
    [HarmonyPatch(typeof(ModelStoryCheckpoint), nameof(ModelStoryCheckpoint.SetMetaSong))]
    static class LevelLoaded
    {
        static void Postfix(ModelStoryCheckpoint __instance)
        {
            if (!Guard.Is<ModelStoryCheckpoint>(__instance)) return;
            try { Hud.LevelName = __instance.name; }   // "name" is a getter, so call it in a try
            catch (Exception e) { Plugin.Logger.LogWarning("Couldn't read the level name: " + e.Message); }
        }
    }

    // When a level's attacks start (also after a retry or checkpoint), reset our counters.
    [HarmonyPatch(typeof(LogicSpawnCreator), nameof(LogicSpawnCreator.start))]
    static class LevelStarted
    {
        static void Postfix(LogicSpawnCreator __instance)
        {
            if (!Guard.Is<LogicSpawnCreator>(__instance)) return;
            Hud.StartLevel();
        }
    }

    // Prefix: runs before the game's method. Returning false skips the game's method, which
    // is how you change behavior. Here: count hits, and optionally ignore them.
    [HarmonyPatch(typeof(HeroCollisionWithEnemy), nameof(HeroCollisionWithEnemy.hitByEnemy))]
    static class PlayerHit
    {
        static bool Prefix(HeroCollisionWithEnemy __instance)
        {
            if (!Guard.Is<HeroCollisionWithEnemy>(__instance)) return true;   // not ours: let the game run
            Hud.Hits++;
            return !Plugin.IgnoreHits.Value;                                   // false = skip the damage
        }
    }

    // Unity calls Update every frame and OnGUI for drawing. Keep this class tiny: Il2CppInterop
    // has to register every method on it, so put the real code in a normal static class.
    public class Overlay : MonoBehaviour
    {
        public Overlay(IntPtr ptr) : base(ptr) { }
        public void Update() => Hud.Update();
        public void OnGUI() => Hud.Draw();
    }

    static class Hud
    {
        public static string LevelName;
        public static int Hits;
        static float levelStart = -1;

        public static void StartLevel()
        {
            Hits = 0;
            levelStart = Time.time;
        }

        public static void Update()
        {
            if (Input.GetKeyDown(Plugin.ToggleKey.Value))
                Plugin.ShowPanel.Value = !Plugin.ShowPanel.Value;   // saved to the config file automatically
        }

        public static void Draw()
        {
            if (!Plugin.ShowPanel.Value || levelStart < 0 || LevelName == null) return;

            // Only DrawTexture and Label are safe to use: this game's build strips some of
            // Unity's GUI (text boxes crash with "Method unstripping failed").
            var box = new Rect(Screen.width - 300, 20, 280, 96);
            GUI.color = new Color(0, 0, 0, 0.7f);
            GUI.DrawTexture(box, Texture2D.whiteTexture);
            GUI.color = new Color(1f, 0.17f, 0.43f);
            GUI.DrawTexture(new Rect(box.x, box.y, 4, box.height), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var t = Time.time - levelStart;
            GUI.Label(new Rect(box.x + 14, box.y + 8, 260, 24), LevelName);
            GUI.Label(new Rect(box.x + 14, box.y + 34, 260, 24), $"Time: {(int)(t / 60)}:{t % 60:00.0}");
            GUI.Label(new Rect(box.x + 14, box.y + 60, 260, 24), $"Hits: {Hits}" + (Plugin.IgnoreHits.Value ? "  (ignored)" : ""));
        }
    }
}
