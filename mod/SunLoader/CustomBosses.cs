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
        public int? SharedIssue;        // its online post, if it was shared or downloaded
        public string SharedAuthor;
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
        // Set when the editor or no-damage mode was used during this run: no stars for it.
        public static bool RunTainted;
        // Set once the active boss's spawner has started, so the menus we pass through on the
        // way in don't count as leaving it.
        static bool started;
        // When the level was first seen with no menu up (-1 = not yet).
        static float levelSince = -1;

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
                    if ((string)file["shared"]?["repo"] == Share.Repo)
                    {
                        b.SharedIssue = (int?)file["shared"]?["issue"];
                        b.SharedAuthor = (string)file["shared"]?["author"];
                    }
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

        // The online post a boss was downloaded from or uploaded to, or null.
        public static int? SharedNumber(string name)
        {
            try { return (int?)JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, name + ".json")))?["shared"]?["issue"]; }
            catch { return null; }
        }

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

        // Every level ends on a checkpoint, and a level with none crashes the game. A boss made
        // before its stage was ever played has none (they come from the stage's dump), so they're
        // added from the dump, which exists by the time the stage's spawner starts.
        public static void EnsureCheckpoints(string path, string stage)
        {
            var dump = Path.Combine(Plugin.DumpDir, stage + ".json");
            if (!File.Exists(path) || !File.Exists(dump)) return;
            var file = JsonNode.Parse(File.ReadAllText(path)).AsObject();
            var layers = file["layers"]?.AsArray();
            if (layers == null || layers.Count == 0) file["layers"] = layers = new JsonArray { new JsonObject { ["layer"] = 0, ["spawns"] = new JsonArray() } };
            if (layers.Any(l => l["spawns"]?.AsArray().Any(s => (string)s?["type"] == "CheckPoint") == true)) return;
            var target = layers[0]["spawns"]?.AsArray();
            if (target == null) layers[0]["spawns"] = target = new JsonArray();
            int added = 0;
            foreach (var layer in JsonNode.Parse(File.ReadAllText(dump))["layers"]?.AsArray() ?? new JsonArray())
                foreach (var s in layer["spawns"]?.AsArray() ?? new JsonArray())
                    if ((string)s?["type"] == "CheckPoint") { target.Add(JsonNode.Parse(s.ToJsonString())); added++; }
            if (added == 0) return;
            File.WriteAllText(path, file.ToJsonString(Plugin.Pretty));
            Plugin.Logger.LogInfo($"Bosses: added {stage}'s {added} checkpoints to {Path.GetFileName(path)}");
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
        // It goes the game's own way (main menu -> Playlist -> start), so the game closes its
        // menus, background and music itself; Sun Loader only decides which song is "selected".
        public static string Launch(BossProject b, bool edit)
        {
            if (b.Error != null) return b.Error;
            var stage = Stage(b.Stage);
            if (stage == null || !SafeRead.IsObjectOf<ModelStoryCheckpoint>(stage.Checkpoint))
                return $"The stage '{b.Stage}' isn't in this version of the game";
            var fromMain = MainMenuSeen.OnMainMenu && SafeRead.IsObjectOf<UI_MainMenu02>(MainMenuSeen.Instance);
            var fromPlaylist = PlaylistMenuSeen.OnPlaylistMenu && SafeRead.IsObjectOf<UI_Practice>(PlaylistMenuSeen.Instance);
            if (!fromMain && !fromPlaylist) return "Go back to the main menu first, then start the boss from there.";

            Active = b;
            EditOnStart = edit;
            RunTainted = edit;
            started = false;
            levelSince = -1;
            pendingLevel = stage.Checkpoint;
            pendingSince = Time.realtimeSinceStartup;
            playlistSince = -1;
            fadeCalled = false;
            Music.Get(b.Music);   // start loading the song now so it's ready when the level is
            try
            {
                SetAudioClipPatch.Apply(new ModelStoryCheckpoint(stage.Checkpoint));
                Plugin.Logger.LogInfo($"Bosses: starting '{b.Name}' on '{b.Stage}'" + (edit ? " (editor)" : ""));
                // From the main menu, open the Playlist screen the normal way; Update starts the
                // song once it's up. On the Playlist screen already, Update starts it right away.
                if (!fromPlaylist) new UI_MainMenu02(MainMenuSeen.Instance).gotoPractice();
                return null;
            }
            catch (Exception e)
            {
                Cancel();
                Plugin.Logger.LogError("Bosses: couldn't start the level: " + e);
                return "The game wouldn't start the level: " + e.Message;
            }
        }

        // A launch in progress: the level to start, until its spawner starts.
        static IntPtr pendingLevel;
        static float pendingSince, playlistSince;
        static bool fadeCalled;

        // The one-song list the game should start while a launch is in progress, or null.
        public static Il2CppList.List<ModelStoryCheckpoint> PendingList()
        {
            if (pendingLevel == IntPtr.Zero || !SafeRead.IsObjectOf<ModelStoryCheckpoint>(pendingLevel)) return null;
            var list = new Il2CppList.List<ModelStoryCheckpoint>();
            list.Add(new ModelStoryCheckpoint(pendingLevel));
            return list;
        }

        static void Cancel() { pendingLevel = IntPtr.Zero; Active = null; EditOnStart = false; }

        // Moves a launch along: once the Playlist screen has settled, tells it to go in game.
        static void UpdateLaunch()
        {
            if (pendingLevel == IntPtr.Zero) return;
            if (Time.realtimeSinceStartup - pendingSince > 20)
            {
                Plugin.Logger.LogWarning($"Bosses: '{Active?.Name}' didn't start in time; cancelled");
                Cancel();
                return;
            }
            if (fadeCalled || !PlaylistMenuSeen.OnPlaylistMenu || !SafeRead.IsObjectOf<UI_Practice>(PlaylistMenuSeen.Instance)) return;
            if (playlistSince < 0) { playlistSince = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - playlistSince < 0.6f) return;   // let it finish opening
            fadeCalled = true;
            try { new UI_Practice(PlaylistMenuSeen.Instance).FadeToInGame(); }
            catch (Exception e) { Plugin.Logger.LogError("Bosses: the Playlist screen wouldn't start the song: " + e); Cancel(); }
        }

        // The boss file to load for a level that's starting, or null to play it as usual.
        public static string FileFor(string levelId)
        {
            // In an online lobby, the boss the host picked for this stage.
            if (Active == null && LobbySync.InLobby && LobbySync.Boss is { } lobbyBoss && lobbyBoss.Stage == levelId)
            {
                Plugin.Logger.LogInfo($"Bosses: playing the lobby's boss '{lobbyBoss.Name}'");
                return lobbyBoss.Path;
            }
            if (Active == null) return null;
            if (Active.Stage != levelId)
            {
                Plugin.Logger.LogInfo($"Bosses: '{levelId}' started instead of '{Active.Stage}'; custom boss '{Active.Name}' is off");
                Active = null;
                return null;
            }
            started = true;
            pendingLevel = IntPtr.Zero;
            return Active.Path;
        }

        // The game says the last checkpoint was cleared: the boss is beaten.
        public static void OnBeaten()
        {
            var b = Active;
            if (b == null) return;
            Plugin.Logger.LogInfo($"Bosses: beat '{b.Name}'" + (RunTainted ? " (with the editor or no damage)" : ""));
            if (b.SharedIssue == null) { EditorGui.Toast($"You beat '{b.Name}'!"); return; }
            if (RunTainted) { EditorGui.Toast($"You beat '{b.Name}', but with the editor or no damage on, so it doesn't count for stars"); return; }
            string hash = null, saved = null;
            try
            {
                var file = JsonNode.Parse(File.ReadAllText(b.Path));
                hash = Share.AttacksHash(file);
                saved = (string)file["shared"]?["hash"];
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Bosses: couldn't check the boss file: " + e.Message); }
            if (hash == null || hash != saved) { EditorGui.Toast($"You beat '{b.Name}', but your copy was changed, so it doesn't count for stars"); return; }
            Share.AddClear(b.SharedIssue.Value);
            var online = Share.Bosses.FirstOrDefault(x => x.Number == b.SharedIssue);
            var stars = online?.Stars ?? 0;
            EditorGui.Toast(stars > 0 ? $"You beat '{b.Name}'! +{stars} stars" + (Share.SignedIn ? "" : " (sign in with GitHub to collect them)")
                : $"You beat '{b.Name}'!" + (Share.SignedIn ? "" : " Sign in with GitHub to record it."));
        }

        // Called every frame. Back on the main menu or the playlist screen means the boss run is over.
        public static void Update()
        {
            UpdateLaunch();
            if (Active == null || !started) return;
            var onMenu = MainMenuSeen.OnMainMenu || PlaylistMenuSeen.OnPlaylistMenu;
            // The menu we started from keeps updating for a moment while the level loads, so only
            // count leaving once the level has been on screen, menu-free, for half a second.
            if (!onMenu) { if (levelSince < 0) levelSince = Time.realtimeSinceStartup; return; }
            var wasInLevel = levelSince >= 0 && Time.realtimeSinceStartup - levelSince >= 0.5f;
            levelSince = -1;
            if (!wasInLevel) return;
            Plugin.Logger.LogInfo($"Bosses: left '{Active.Name}'");
            Active = null;
            EditOnStart = false;
            SetAudioClipPatch.RestoreOriginals();
        }
    }

    // While a boss is starting, the Playlist screen and the level start get the boss's stage as
    // the one selected song, on Normal, whatever the player had selected there.
    [HarmonyPatch]
    static class LaunchPatches
    {
        static bool IsPlaylist(UI_Practice ui) => SafeRead.IsObjectOf<UI_Practice>(ui.Pointer);

        [HarmonyPostfix, HarmonyPatch(typeof(UI_Practice), nameof(UI_Practice.GetAllSelectedSong))]
        static void Selected(UI_Practice __instance, ref Il2CppList.List<ModelStoryCheckpoint> __result)
        {
            if (IsPlaylist(__instance) && CustomBosses.PendingList() is { } list) __result = list;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(UI_Practice), nameof(UI_Practice.GetAllSelectedSongRandom))]
        static void SelectedRandom(UI_Practice __instance, ref Il2CppList.List<ModelStoryCheckpoint> __result)
        {
            if (IsPlaylist(__instance) && CustomBosses.PendingList() is { } list) __result = list;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(UI_Practice), nameof(UI_Practice.getIsHardSelected))]
        static void Hard(UI_Practice __instance, ref bool __result)
        {
            if (IsPlaylist(__instance) && CustomBosses.PendingList() != null) __result = false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(UI_Practice), nameof(UI_Practice.getIsPartySelected))]
        static void Party(UI_Practice __instance, ref bool __result)
        {
            if (IsPlaylist(__instance) && CustomBosses.PendingList() != null) __result = false;
        }

        // The last word: whatever list the screen passes on, the level start gets the boss's stage.
        [HarmonyPrefix, HarmonyPatch(typeof(ArcadeLevelScene), nameof(ArcadeLevelScene.StartPractice), new[] { typeof(Il2CppList.List<ModelStoryCheckpoint>), typeof(ModelGameDifficulty) })]
        static void Start(ref Il2CppList.List<ModelStoryCheckpoint> pPracticeList, ref ModelGameDifficulty diff)
        {
            if (CustomBosses.PendingList() is not { } list) return;
            pPracticeList = list;
            diff = ModelGameDifficultyEnum.NORMAL;
            Plugin.Logger.LogInfo("Bosses: the game is starting the boss's stage");
        }
    }

    // A playlist run ends by clearing the level's last checkpoint.
    [HarmonyPatch(typeof(LogicArcadeEndLevel), nameof(LogicArcadeEndLevel.onCompleteLastCheckpoint))]
    static class BeatenPatch
    {
        static void Postfix(LogicArcadeEndLevel __instance)
        {
            if (!SafeRead.IsObjectOf<LogicArcadeEndLevel>(__instance.Pointer)) return;
            try { CustomBosses.OnBeaten(); }
            catch (Exception e) { Plugin.Logger.LogError("Bosses: " + e); }
        }
    }

    // Remembers when the playlist screen last updated (a practice run ends there).
    [HarmonyPatch(typeof(UI_Practice), nameof(UI_Practice.update))]
    static class PlaylistMenuSeen
    {
        static int lastFrame = -100;
        public static IntPtr Instance;

        static void Postfix(UI_Practice __instance)
        {
            if (!SafeRead.IsObjectOf<UI_Practice>(__instance.Pointer)) return;
            lastFrame = Time.frameCount;
            Instance = __instance.Pointer;
        }

        public static bool OnPlaylistMenu => Time.frameCount - lastFrame < 5;
    }
}
