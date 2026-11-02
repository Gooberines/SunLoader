using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SunLoaderInstaller
{
    // Everything the installer does to the game folder. No UI here: progress and messages go
    // through the callbacks so the window can show them.
    class Installer
    {
        // The BepInEx build Sun Loader is made for: the official, unmodified zip from
        // builds.bepinex.dev (6.0.0-be.791), shipped in files\BepInEx.zip and never downloaded.
        // Size and SHA-256 are checked so a damaged copy is never installed. The only network use
        // is the optional check for a newer Sun Loader on GitHub (Updater.cs).
        const long BepInExSize = 34336890;
        const string BepInExSha256 = "BB1C510909B69522733791F3A0817A487379A52DA6468A492F1C9C19B3EC6F9A";

        public const string GameExe = "JSB.exe";
        const string GameFolderName = "Just Shapes & Beats";

        public Action<string> Log = _ => { };
        public Action<int> Progress = _ => { };   // 0-100, or -1 for "busy"

        // ---------- finding the game ----------

        // Looks through every Steam library for the game. Returns null if it isn't found.
        public static string FindGame()
        {
            foreach (var steam in SteamRoots())
            {
                var libraries = new List<string> { steam };
                var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var dir = Path.Combine(lib, "steamapps", "common", GameFolderName);
                    if (IsGameFolder(dir)) return dir;
                }
            }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", GameFolderName);
            return IsGameFolder(fallback) ? fallback : null;
        }

        static IEnumerable<string> SteamRoots()
        {
            string Read(RegistryKey root, string key, string value)
            {
                try { using (var k = root.OpenSubKey(key)) return k?.GetValue(value) as string; } catch { return null; }
            }
            var found = new[]
            {
                Read(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
                Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
                Read(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
            };
            return found.Where(p => !string.IsNullOrEmpty(p)).Select(p => p.Replace('/', '\\')).Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsGameFolder(string dir) =>
            !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, GameExe)) && File.Exists(Path.Combine(dir, "GameAssembly.dll"));

        public static bool GameRunning() => Process.GetProcessesByName("JSB").Length > 0;

        public static bool IsInstalled(string game) =>
            File.Exists(Path.Combine(game, "BepInEx", "plugins", "SunLoader", "SunLoader.dll")) && File.Exists(Path.Combine(game, "winhttp.dll"));

        // ---------- install ----------

        public void Install(string game, bool noIntro, bool exampleMod, bool getLatest)
        {
            Check(game);
            newer = getLatest ? Updater.Fetch(Log) : new Dictionary<string, string>();
            InstallBepInEx(game);

            Log("Installing Sun Loader...");
            var plugins = Path.Combine(game, "BepInEx", "plugins");
            WriteResource("SunLoader.dll", Path.Combine(plugins, "SunLoader", "SunLoader.dll"));
            var old = Path.Combine(plugins, "CustomBosses");   // Sun Loader's old name; both loading at once would clash
            if (Directory.Exists(old)) { Directory.Delete(old, true); Log("Removed the old Custom Bosses version."); }

            var mods = Path.Combine(game, "BepInEx", "mods");
            Directory.CreateDirectory(mods);
            Directory.CreateDirectory(Path.Combine(game, "BepInEx", "SunLoader", "music"));
            if (noIntro)
            {
                WriteResource("NoIntro.dll", Path.Combine(mods, "NoIntro.dll"));
                SetAutoLoad(mods, "NoIntro.dll");
                Log("Added No Intro (loads automatically).");
            }
            if (exampleMod)
            {
                WriteResource("ExampleMod.dll", Path.Combine(mods, "ExampleMod.dll"));
                Log("Added Example Mod (load it from the mod menu).");
            }
            Progress(100);
            Log("Done!");
        }

        void InstallBepInEx(string game)
        {
            var winhttp = Path.Combine(game, "winhttp.dll");
            var disabled = winhttp + ".disabled";
            var core = Path.Combine(game, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll");
            if (File.Exists(core) && File.Exists(disabled) && !File.Exists(winhttp))
            {
                File.Move(disabled, winhttp);   // turned off by our uninstall: just turn it back on
                Log("BepInEx was turned off; turned it back on.");
                return;
            }
            if (File.Exists(core) && File.Exists(winhttp))
            {
                Log("BepInEx is already installed; keeping it.");
                return;
            }

            var zip = Path.Combine(FilesDir, "BepInEx.zip");
            if (!File.Exists(zip))
                throw new InstallError("Can't find files\\BepInEx.zip next to the installer. Unzip the whole download (not just the exe) and run it from there.");
            Log("Checking BepInEx...");
            if (!Verify(zip, quiet: false)) throw new InstallError("files\\BepInEx.zip is damaged (checksum didn't match). Download Sun Loader again.");

            Log("Unpacking BepInEx...");
            var root = Path.GetFullPath(game).TrimEnd('\\') + "\\";
            using (var archive = ZipFile.OpenRead(zip))
            {
                int done = 0, total = archive.Entries.Count;
                foreach (var entry in archive.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // never write outside the game folder
                    if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var src = entry.Open())
                    using (var dst = File.Create(target)) src.CopyTo(dst);
                    Progress(10 + 80 * ++done / total);
                }
            }
            Log("BepInEx installed.");
        }

        bool Verify(string path, bool quiet)
        {
            var info = new FileInfo(path);
            if (info.Length != BepInExSize) { if (!quiet) Log($"BepInEx.zip has the wrong size ({info.Length} bytes)."); return false; }
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(path))
            {
                var hash = BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
                if (!hash.Equals(BepInExSha256, StringComparison.OrdinalIgnoreCase)) { if (!quiet) Log("Checksum doesn't match."); return false; }
            }
            if (!quiet) Log("BepInEx package verified.");
            return true;
        }

        // Adds a DLL to Sun Loader's auto-load list (a JSON array of file names).
        static void SetAutoLoad(string mods, string file)
        {
            var path = Path.Combine(mods, "autoload.json");
            var names = File.Exists(path)
                ? Regex.Matches(File.ReadAllText(path), "\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToList()
                : new List<string>();
            if (!names.Contains(file, StringComparer.OrdinalIgnoreCase)) names.Add(file);
            File.WriteAllText(path, "[" + string.Join(",", names.OrderBy(n => n).Select(n => "\"" + n + "\"")) + "]");
        }

        // The mod DLLs ship as plain files in a "files" folder next to the installer, not packed
        // inside the exe: an exe that unpacks hidden DLLs looks like malware to antivirus scanners.
        public static string FilesDir => Path.Combine(Path.GetDirectoryName(typeof(Installer).Assembly.Location), "files");

        // Newer DLLs from GitHub, used instead of the ones in files\ (see Updater).
        Dictionary<string, string> newer = new Dictionary<string, string>();

        void WriteResource(string name, string target)
        {
            var source = newer.TryGetValue(name, out var downloaded) ? downloaded : Path.Combine(FilesDir, name);
            if (!File.Exists(source))
                throw new InstallError($"Can't find files\\{name} next to the installer. Unzip the whole download (not just the exe) and run it from there.");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target, true);
        }

        // ---------- uninstall ----------

        // Turns the mods off without deleting anything you made: BepInEx is switched off
        // (winhttp.dll renamed) and Sun Loader's DLL removed. Bosses, music and mods stay.
        public void Uninstall(string game)
        {
            Check(game);
            var plugin = Path.Combine(game, "BepInEx", "plugins", "SunLoader");
            if (Directory.Exists(plugin)) { Directory.Delete(plugin, true); Log("Removed Sun Loader."); }
            var winhttp = Path.Combine(game, "winhttp.dll");
            if (File.Exists(winhttp))
            {
                var disabled = winhttp + ".disabled";
                if (File.Exists(disabled)) File.Delete(disabled);
                File.Move(winhttp, disabled);
                Log("Turned BepInEx off. The game now starts without mods.");
            }
            Log("Your bosses, music and mods are still in the BepInEx folder. Run Install to turn everything back on.");
            Progress(100);
        }

        static void Check(string game)
        {
            if (!IsGameFolder(game)) throw new InstallError("That folder doesn't contain Just Shapes & Beats (JSB.exe).");
            if (GameRunning()) throw new InstallError("Close Just Shapes & Beats first, then try again.");
        }
    }

    class InstallError : Exception
    {
        public InstallError(string message) : base(message) { }
    }
}
