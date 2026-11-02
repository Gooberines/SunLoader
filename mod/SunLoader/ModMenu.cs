using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace SunLoader
{
    // A DLL mod found on disk, described without loading it (read with Mono.Cecil).
    class ModFile
    {
        public string Path, Name, Version, Guid, Error;
        public bool InModsFolder;       // BepInEx\mods: we load these, any time
        public bool Enabled;            // plugins: not renamed to .disabled / mods: auto-loads at startup
        public bool Loaded;
        public bool IsThisMod;
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    // Loads BepInEx plugins from BepInEx\mods while the game runs, and turns the startup
    // plugins in BepInEx\plugins on and off (by renaming them, so that applies after a restart).
    // .NET can't unload a DLL, so a loaded mod stays loaded until the game closes.
    static class ModLoader
    {
        public static string ModsDir => Path.Combine(Paths.BepInExRootPath, "mods");
        static string AutoLoadFile => Path.Combine(ModsDir, "autoload.json");
        static readonly Dictionary<string, string> loadErrors = new(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> loaded = new(StringComparer.OrdinalIgnoreCase);
        public static readonly HashSet<string> LoadedGuids = new(StringComparer.OrdinalIgnoreCase);   // for the anti-cheat
        static HashSet<string> autoLoad;
        public static bool RestartNeeded;
        static bool startupDone;

        public static List<ModFile> Scan()
        {
            Directory.CreateDirectory(ModsDir);
            LoadAutoList();
            var list = new List<ModFile>();
            foreach (var path in Directory.GetFiles(ModsDir, "*.dll"))
            {
                var m = Describe(path);
                m.InModsFolder = true;
                m.Enabled = autoLoad.Contains(m.FileName);
                m.Loaded = loaded.Contains(path);
                if (loadErrors.TryGetValue(path, out var err)) m.Error = err;
                list.Add(m);
            }
            foreach (var path in Directory.GetFiles(Paths.PluginPath, "*", SearchOption.AllDirectories)
                         .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)))
            {
                var m = Describe(path);
                m.Enabled = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                m.IsThisMod = m.Guid == "riptid.jsb.sunloader";
                m.Loaded = m.Guid != null && IL2CPPChainloader.Instance.Plugins.ContainsKey(m.Guid);
                list.Add(m);
            }
            return list;
        }

        // Reads a DLL's [BepInPlugin] name and version without running any of its code.
        static ModFile Describe(string path)
        {
            var m = new ModFile { Path = path, Name = Path.GetFileNameWithoutExtension(path.Replace(".disabled", "")) };
            try
            {
                using var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(path);
                var attr = asm.MainModule.Types.SelectMany(t => t.CustomAttributes)
                    .FirstOrDefault(a => a.AttributeType.Name == "BepInPlugin" && a.ConstructorArguments.Count >= 3);
                if (attr != null)
                {
                    m.Guid = attr.ConstructorArguments[0].Value as string;
                    m.Name = attr.ConstructorArguments[1].Value as string ?? m.Name;
                    m.Version = attr.ConstructorArguments[2].Value as string;
                }
                else m.Error = "Not a BepInEx plugin (no [BepInPlugin] class)";
            }
            catch (Exception e) { m.Error = "Can't read this DLL: " + e.Message; }
            return m;
        }

        public static void SetAutoLoad(ModFile m, bool on)
        {
            LoadAutoList();
            if (on) autoLoad.Add(m.FileName); else autoLoad.Remove(m.FileName);
            File.WriteAllText(AutoLoadFile, JsonSerializer.Serialize(autoLoad.OrderBy(x => x).ToArray()));
        }

        public static void SetPluginEnabled(ModFile m, bool on)
        {
            var target = on ? m.Path[..^".disabled".Length] : m.Path + ".disabled";
            if (on == m.Enabled || File.Exists(target)) return;
            File.Move(m.Path, target);
            RestartNeeded = true;
            Plugin.Logger.LogInfo($"Mod menu: {(on ? "enabled" : "disabled")} {Path.GetFileName(target)} (applies after restart)");
        }

        static void LoadAutoList()
        {
            if (autoLoad != null) return;
            try { autoLoad = File.Exists(AutoLoadFile) ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(AutoLoadFile)).ToHashSet(StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase); }
            catch { autoLoad = new(StringComparer.OrdinalIgnoreCase); }
        }

        // Runs once after the game has started: loads every auto-load mod.
        public static void Update()
        {
            if (startupDone) return;
            startupDone = true;
            try
            {
                foreach (var m in Scan().Where(m => m.InModsFolder && m.Enabled && !m.Loaded)) Load(m);
            }
            catch (Exception e) { Plugin.Logger.LogError("Mod menu auto-load: " + e); }
        }

        // Loads a DLL and starts every BepInEx plugin in it. Returns an error message or null.
        public static string Load(ModFile m)
        {
            if (loaded.Contains(m.Path)) return null;
            if (m.Guid != null && IL2CPPChainloader.Instance.Plugins.ContainsKey(m.Guid))
                return Fail(m, "A mod with the same ID is already loaded");
            try
            {
                var asm = Assembly.LoadFrom(m.Path);
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                var plugins = types.Where(t => !t.IsAbstract && typeof(BasePlugin).IsAssignableFrom(t) && t.GetCustomAttribute<BepInPlugin>() != null).ToList();
                if (plugins.Count == 0) return Fail(m, "No BepInEx plugin class found in this DLL");
                foreach (var type in plugins)
                {
                    var plugin = (BasePlugin)Activator.CreateInstance(type);
                    plugin.Load();
                    LoadedGuids.Add(type.GetCustomAttribute<BepInPlugin>().GUID);
                    Plugin.Logger.LogInfo($"Mod menu loaded {type.GetCustomAttribute<BepInPlugin>().Name} from {m.FileName}");
                }
                loaded.Add(m.Path);
                loadErrors.Remove(m.Path);
                return null;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Mod menu couldn't load {m.FileName}: {e}");
                return Fail(m, (e is TargetInvocationException t ? t.InnerException ?? e : e).Message);
            }
        }

        static string Fail(ModFile m, string error) { loadErrors[m.Path] = error; return error; }
    }

    // Remembers when the main menu last updated, so the MODS button shows only on it.
    [HarmonyPatch(typeof(UI_MainMenu02), nameof(UI_MainMenu02.update))]
    static class MainMenuSeen
    {
        public static int LastFrame = -100;
        public static IntPtr Instance;   // the main menu object, so a boss can close it when it starts

        static void Postfix(UI_MainMenu02 __instance)
        {
            // update() is folded with other objects' update(); only count the real main menu.
            if (!SafeRead.IsObjectOf<UI_MainMenu02>(__instance.Pointer)) return;
            LastFrame = Time.frameCount;
            Instance = __instance.Pointer;
        }

        public static bool OnMainMenu => Time.frameCount - LastFrame < 5;
    }

    // The MODS button on the main menu and the mod menu window.
    static class ModMenu
    {
        public static bool Open;
        static List<ModFile> mods;
        static float scroll;
        static string message;
        static float messageUntil;

        static readonly Color Bg = new(0.02f, 0.02f, 0.03f, 0.97f), Row = new(0.09f, 0.09f, 0.12f, 1f);
        static readonly Color PinkDim = new(0.55f, 0.06f, 0.24f), Pink = new(1f, 0.17f, 0.43f), Green = new(0.3f, 0.9f, 0.5f), Orange = new(1f, 0.6f, 0.2f), Dim = new(0.6f, 0.6f, 0.7f);

        public static void Toggle()
        {
            Open = !Open;
            if (Open) { mods = ModLoader.Scan(); scroll = 0; BossMenu.Open = false; BossMultiplayerMenu.Open = false; }
        }

        static void Say(string msg) { message = msg; messageUntil = Time.realtimeSinceStartup + 6; }

        public static void OnGUI(float scale, float vw)
        {
            var e = Event.current;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));

            if (!Open && MainMenuSeen.OnMainMenu)
            {
                // The button: bottom-left of the main menu.
                // Slanted pink block that kicks on the main menu's beat, like the game's own buttons.
                var b = new Rect(48, 950, 210, 70);
                var hover = b.Contains(e.mousePosition);
                var kick = Theme.Pulse * 6;
                var block = new Rect(b.x - kick / 2, b.y - kick / 2, b.width + kick, b.height + kick);
                Theme.Slanted(new Rect(block.x + 6, block.y + 6, block.width, block.height), new Color(0, 0, 0, 0.5f), 0.3f);   // shadow
                Theme.Slanted(block, hover ? Color.white : Theme.Pink, 0.3f);
                Theme.Title(new Rect(b.x + 26, b.y + 6, 180, 44), "MODS", hover ? Theme.Pink : Color.white, 32);
                EditorGui.Text(new Rect(b.x + 28, b.y + 44, 180, 22), $"SUN LOADER · {Plugin.ModMenuKey.Value}", hover ? Theme.PinkDark : new Color(1, 1, 1, 0.8f), 12);
                if (e.type == EventType.MouseDown && e.button == 0 && hover) { e.Use(); Toggle(); }
            }
            if (!Open) return;

            EditorGui.Fill(new Rect(0, 0, vw, 1080), new Color(0, 0, 0, 0.75f));
            var r = new Rect(vw / 2 - 560, 70, 1120, 940);
            EditorGui.Fill(r, Bg);
            Theme.Stripes(new Rect(r.x, r.y, r.width, 96), new Color(Pink.r, Pink.g, Pink.b, 0.1f));
            EditorGui.Fill(new Rect(r.x, r.y + 96, r.width, 4), Pink);
            EditorGui.Fill(new Rect(r.x, r.yMax - 4, r.width, 4), Pink);
            float x = r.x + 30, y = r.y + 20, w = r.width - 60;
            Theme.Title(new Rect(x, y - 2, 560, 60), "SUN LOADER", Pink, 46);
            EditorGui.Text(new Rect(x + 4, y + 54, 400, 24), "MODS", Color.white, 14);
            if (EditorGui.Button(new Rect(r.xMax - 150, y + 12, 120, 36), $"Close ({Plugin.ModMenuKey.Value})")) { Open = false; return; }
            if (EditorGui.Button(new Rect(r.xMax - 300, y + 12, 140, 36), "Refresh")) { mods = ModLoader.Scan(); Say("List refreshed"); }
            y += 96;

            if (ModLoader.RestartNeeded) { EditorGui.Text(new Rect(x, y, w, 24), "Restart the game to apply your plugin changes.", Orange); y += 28; }
            if (Time.realtimeSinceStartup < messageUntil) { EditorGui.Text(new Rect(x, y, w, 24), message, Green); y += 28; }

            // Scrollable list area.
            var listRect = new Rect(x, y, w, r.yMax - y - 70);
            if (e.type == EventType.ScrollWheel && listRect.Contains(e.mousePosition)) { scroll = Mathf.Max(0, scroll + e.delta.y * 20); e.Use(); }
            float ly = listRect.y - scroll;

            Section("Mod folder  -  load anytime", "Put BepInEx plugin DLLs in BepInEx\\mods. Load them now, or turn on Auto-load to load them every time the game starts.");
            var folderMods = mods.Where(m => m.InModsFolder).ToList();
            if (folderMods.Count == 0) Note("No DLLs in BepInEx\\mods yet. Use 'Open mods folder' below.");
            foreach (var m in folderMods) ModRow(m);

            ly += 16;
            Section("Startup plugins  -  BepInEx\\plugins", "Loaded by BepInEx when the game starts. Turning one off renames it to .disabled; restart to apply.");
            foreach (var m in mods.Where(m => !m.InModsFolder)) ModRow(m);

            // Footer.
            EditorGui.Fill(new Rect(r.x + 2, r.yMax - 62, r.width - 4, 60), Bg);
            var fy = r.yMax - 50;
            if (EditorGui.Button(new Rect(x, fy, 200, 34), "Open mods folder")) OpenFolder(ModLoader.ModsDir);
            if (EditorGui.Button(new Rect(x + 210, fy, 220, 34), "Open plugins folder")) OpenFolder(Paths.PluginPath);
            EditorGui.Text(new Rect(x + 450, fy + 8, w - 450, 26), "Only load mods you trust: a DLL mod can run any code on your PC.", Dim, 12);

            void Section(string title, string help)
            {
                if (Visible(ly, 62))
                {
                    Theme.Slanted(new Rect(x, ly + 2, 8, 26), Pink, 0.3f);
                    Theme.Title(new Rect(x + 20, ly - 4, w, 36), title.ToUpperInvariant(), Color.white, 22);
                    EditorGui.Text(new Rect(x + 20, ly + 32, w - 20, 24), help, Dim, 13);
                }
                ly += 64;
            }

            void Note(string text)
            {
                if (Visible(ly, 30)) EditorGui.Text(new Rect(x + 12, ly, w - 12, 26), text, Dim, 13);
                ly += 32;
            }

            void ModRow(ModFile m)
            {
                const float h = 62;
                if (Visible(ly, h))
                {
                    var row = new Rect(x, ly, w, h - 6);
                    var hoverRow = row.Contains(Event.current.mousePosition);
                    Theme.Slanted(row, hoverRow ? new Color(0.14f, 0.1f, 0.13f) : Row, 0.08f);
                    EditorGui.Fill(new Rect(row.x, row.y, 5, row.height), m.Error != null ? Orange : m.Loaded ? Green : PinkDim);
                    var title = m.Name + (m.Version != null ? "  v" + m.Version : "") + (m.IsThisMod ? "  (this mod)" : "");
                    EditorGui.Text(new Rect(row.x + 16, row.y + 5, w - 420, 26), title, Color.white, 16);
                    var (state, color) =
                        m.Error != null ? ("Error: " + m.Error, Orange) :
                        m.Loaded ? ("Loaded", Green) :
                        m.InModsFolder ? ("Not loaded", Dim) :
                        m.Enabled ? ("Loads at next start", Dim) : ("Off", Dim);
                    EditorGui.Text(new Rect(row.x + 16, row.y + 31, w - 420, 22), m.FileName + "   ·   " + state, color, 12);

                    var bx = row.xMax - 390;
                    if (m.InModsFolder)
                    {
                        if (EditorGui.Button(new Rect(bx, row.y + 12, 180, 32), m.Enabled ? "Auto-load: ON" : "Auto-load: OFF", color: m.Enabled ? new Color(0.15f, 0.45f, 0.25f) : null))
                        { ModLoader.SetAutoLoad(m, !m.Enabled); mods = ModLoader.Scan(); }
                        if (!m.Loaded && m.Error == null && EditorGui.Button(new Rect(bx + 190, row.y + 12, 180, 32), "Load now", color: new Color(0.15f, 0.3f, 0.45f)))
                        {
                            var err = ModLoader.Load(m);
                            mods = ModLoader.Scan();
                            Say(err == null ? $"Loaded {m.Name}" : $"Couldn't load {m.Name}: {err}");
                        }
                        else if (m.Loaded) EditorGui.Text(new Rect(bx + 196, row.y + 18, 180, 22), "restart to unload", Dim, 12);
                    }
                    else if (!m.IsThisMod)
                    {
                        if (EditorGui.Button(new Rect(bx + 190, row.y + 12, 180, 32), m.Enabled ? "On" : "Off", color: m.Enabled ? new Color(0.15f, 0.45f, 0.25f) : new Color(0.4f, 0.12f, 0.18f)))
                        {
                            try { ModLoader.SetPluginEnabled(m, !m.Enabled); } catch (Exception ex) { Say("Couldn't change it: " + ex.Message); }
                            mods = ModLoader.Scan();
                        }
                    }
                }
                ly += h;
            }

            bool Visible(float top, float h) => top >= listRect.y - 2 && top + h <= listRect.yMax + 2;
        }

        static void OpenFolder(string dir)
        {
            try { Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", dir); }
            catch (Exception e) { Say("Couldn't open the folder: " + e.Message); }
        }
    }
}
