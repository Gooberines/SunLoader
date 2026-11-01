using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using HarmonyLib;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace SunLoader
{
    // A boss of your own, not tied to a playlist level: BepInEx\SunLoader\bosses\<name>.json.
    // It plays on a "stage" (a game level whose scene, length and song it borrows unless it
    // has its own music), started straight from the Bosses menu.
    class BossProject
    {
        public string Path, Name, Stage, Music;
        public int Attacks;
        public DateTime Modified;
        public string Error;
    }

    // A level that can be a stage, read from the game's level list.
    class StageInfo
    {
        public string Id, Artist;
        public float LengthSec;
        public IntPtr Checkpoint;
    }

    static class CustomBosses
    {
        public static string Dir => Path.Combine(Plugin.Root, "bosses");

        // The boss started from the Bosses menu, while its level is running.
        public static BossProject Active;
        // Open the editor as soon as the active boss's level starts.
        public static bool EditOnStart;
        // Set once the active boss's spawner has started, so the menus we pass through on the
        // way in don't count as leaving it.
        static bool started;
        static float launchedAt;

        // ---------- files ----------

        public static List<BossProject> List()
        {
            Directory.CreateDirectory(Dir);
            var list = new List<BossProject>();
            foreach (var path in Directory.GetFiles(Dir, "*.json"))
            {
                var b = new BossProject { Path = path, Name = Path.GetFileNameWithoutExtension(path), Modified = File.GetLastWriteTime(path) };
                try
                {
                    var file = JsonNode.Parse(File.ReadAllText(path));
                    b.Stage = (string)file["stage"];
                    b.Music = (string)file["music"];
                    b.Attacks = file["layers"]?.AsArray().Sum(l => l["spawns"]?.AsArray().Count ?? 0) ?? 0;
                    if (b.Stage == null) b.Error = "No stage set in the file";
                }
                catch (Exception e) { b.Error = "Can't read this file: " + e.Message; }
                list.Add(b);
            }
            return list.OrderByDescending(b => b.Modified).ToList();
        }

        public static string CleanName(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            return new string((name ?? "").Where(c => !bad.Contains(c)).ToArray()).Trim();
        }

        public static bool Exists(string name) => File.Exists(Path.Combine(Dir, name + ".json"));

        // Makes a new boss file. withOriginal copies the stage's own attacks (if the stage has
        // been played once, so its dump exists); otherwise it starts with only its checkpoints.
        public static BossProject Create(string name, string stage, bool withOriginal)
        {
            Directory.CreateDirectory(Dir);
            var dump = Path.Combine(Plugin.DumpDir, stage + ".json");
            var spawns = new JsonArray();
            if (File.Exists(dump))
            {
                foreach (var layer in JsonNode.Parse(File.ReadAllText(dump))["layers"]?.AsArray() ?? new JsonArray())
                    foreach (var s in layer["spawns"]?.AsArray() ?? new JsonArray())
                        if (s?["type"] != null && (withOriginal || (string)s["type"] == "CheckPoint"))
                            spawns.Add(JsonNode.Parse(s.ToJsonString()));
            }
            var file = new JsonObject
            {
                ["name"] = name,
                ["stage"] = stage,
                ["level"] = stage,
                ["music"] = null,
                ["editor"] = new JsonObject { ["bpm"] = 120 },
                ["layers"] = new JsonArray { new JsonObject { ["layer"] = 0, ["spawns"] = spawns } },
            };
            var path = Path.Combine(Dir, name + ".json");
            File.WriteAllText(path, file.ToJsonString(Plugin.Pretty));
            Plugin.Logger.LogInfo($"Bosses: created '{name}' on '{stage}' ({spawns.Count} attacks)");
            return List().FirstOrDefault(b => b.Path == path);
        }

        public static void Rename(BossProject b, string newName)
        {
            var target = Path.Combine(Dir, newName + ".json");
            File.Move(b.Path, target);
            var file = JsonNode.Parse(File.ReadAllText(target));
            file["name"] = newName;
            File.WriteAllText(target, file.ToJsonString(Plugin.Pretty));
        }

        public static void Duplicate(BossProject b)
        {
            var name = b.Name + " copy";
            for (int i = 2; Exists(name); i++) name = $"{b.Name} copy {i}";
            var file = JsonNode.Parse(File.ReadAllText(b.Path));
            file["name"] = name;
            File.WriteAllText(Path.Combine(Dir, name + ".json"), file.ToJsonString(Plugin.Pretty));
        }

        // Moves the file to bosses\deleted, so a mistake can be undone by hand.
        public static void Delete(BossProject b)
        {
            var trash = Path.Combine(Dir, "deleted");
            Directory.CreateDirectory(trash);
            File.Move(b.Path, Path.Combine(trash, Path.GetFileName(b.Path)), true);
        }

        public static void SetMusic(BossProject b, string music)
        {
            var file = JsonNode.Parse(File.ReadAllText(b.Path));
            file["music"] = music;
            File.WriteAllText(b.Path, file.ToJsonString(Plugin.Pretty));
            b.Music = music;
        }

        // ---------- stages ----------

        static List<StageInfo> stages;

        // Every playlist level the game knows. Read once; the list doesn't change while it runs.
        public static List<StageInfo> Stages()
        {
            if (stages != null && stages.Count > 0) return stages;
            stages = new List<StageInfo>();
            var seen = new HashSet<string>();
            try
            {
                foreach (var all in new[] { ModelStoryCheckpointEnum.ALL_MULTI, ModelStoryCheckpointEnum.ALL_EXTRA })
                {
                    for (int i = 0; all != null && i < all.length; i++)
                    {
                        var level = all[i];
                        if (level == null || !SafeRead.IsObjectOf<ModelStoryCheckpoint>(level.Pointer)) continue;
                        string name = null, artist = null;
                        try { name = level.name; artist = level.artist; } catch { }
                        var id = SetMetaSongPatch.FileId(name, level.nId);
                        if (!seen.Add(id)) continue;
                        float length = 0;
                        try { length = level.getLenght(); } catch { }
                        stages.Add(new StageInfo { Id = id, Artist = artist, LengthSec = length > 1000 ? length / 1000 : length, Checkpoint = level.Pointer });
                    }
                }
            }
            catch (Exception e) { Plugin.Logger.LogError("Bosses: couldn't read the level list: " + e); }
            stages.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
            Plugin.Logger.LogInfo($"Bosses: {stages.Count} stages available");
            return stages;
        }

        public static StageInfo Stage(string id) => Stages().FirstOrDefault(s => s.Id == id);

        public static bool HasDump(string stage) => File.Exists(Path.Combine(Plugin.DumpDir, stage + ".json"));

        // ---------- playing ----------

        // Starts the boss's stage as a one-song practice run. Returns an error message or null.
        public static string Launch(BossProject b, bool edit)
        {
            if (b.Error != null) return b.Error;
            var stage = Stage(b.Stage);
            if (stage == null || !SafeRead.IsObjectOf<ModelStoryCheckpoint>(stage.Checkpoint))
                return $"The stage '{b.Stage}' isn't in this version of the game";
            var level = new ModelStoryCheckpoint(stage.Checkpoint);

            Active = b;
            EditOnStart = edit;
            started = false;
            launchedAt = Time.realtimeSinceStartup;
            Music.Get(b.Music);   // start loading the song now so it's ready when the level is
            try
            {
                SetAudioClipPatch.Apply(level);
                var list = new Il2CppList.List<ModelStoryCheckpoint>();
                list.Add(level);
                Plugin.Logger.LogInfo($"Bosses: starting '{b.Name}' on '{b.Stage}'" + (edit ? " (editor)" : ""));
                ArcadeLevelScene.StartPractice(list, ModelGameDifficultyEnum.NORMAL);
                return null;
            }
            catch (Exception e)
            {
                Active = null;
                Plugin.Logger.LogError("Bosses: couldn't start the level: " + e);
                return "The game wouldn't start the level: " + e.Message;
            }
        }

        // The boss file to load for a level that's starting, or null to play it as usual.
        public static string FileFor(string levelId)
        {
            if (Active == null) return null;
            if (Active.Stage != levelId)
            {
                Plugin.Logger.LogInfo($"Bosses: '{levelId}' started instead of '{Active.Stage}'; custom boss '{Active.Name}' is off");
                Active = null;
                return null;
            }
            started = true;
            return Active.Path;
        }

        // Called every frame. Back on the main menu or the playlist screen means the boss run is over.
        public static void Update()
        {
            if (Active == null || !started || Time.realtimeSinceStartup - launchedAt < 3) return;
            if (MainMenuSeen.OnMainMenu || PlaylistMenuSeen.OnPlaylistMenu)
            {
                Plugin.Logger.LogInfo($"Bosses: left '{Active.Name}'");
                Active = null;
                EditOnStart = false;
                SetAudioClipPatch.RestoreOriginals();
            }
        }
    }

    // Remembers when the playlist screen last updated (a practice run ends there).
    [HarmonyPatch(typeof(UI_Practice), nameof(UI_Practice.update))]
    static class PlaylistMenuSeen
    {
        static int lastFrame = -100;

        static void Postfix(UI_Practice __instance)
        {
            if (SafeRead.IsObjectOf<UI_Practice>(__instance.Pointer)) lastFrame = Time.frameCount;
        }

        public static bool OnPlaylistMenu => Time.frameCount - lastFrame < 5;
    }
}
