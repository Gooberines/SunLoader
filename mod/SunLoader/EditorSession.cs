using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace SunLoader
{
    // One attack as the editor holds it. Same fields as a spawn in the boss file.
    class EditSpawn
    {
        public string Type;
        public float Time, Warning, Outro;
        public float? X, Y;
        public Dictionary<string, string> Params = new();

        public EditSpawn Clone() => new()
        {
            Type = Type, Time = Time, Warning = Warning, Outro = Outro, X = X, Y = Y,
            Params = new Dictionary<string, string>(Params),
        };
    }

    // An attack type from the game's catalog, with its default settings.
    class AttackType
    {
        public string Type;
        public float WarningDefault;
        public bool UsesPosition;
        public List<KeyValuePair<string, string>> Params = new();
    }

    // The level being edited: its spawner and song in the game, and the editor's copy of
    // its attacks. Changes reach the game only through Apply (and disk only through Save).
    static class EditorSession
    {
        public static string Level;
        public static List<EditSpawn> Spawns = new();
        public static float Bpm = 120;
        public static string MusicFile;         // a file in SunLoader\music, or null for the level's own song
        public static bool Dirty;               // edited since last Save
        public static bool AppliedDirty;        // edited since last Apply
        public static readonly List<AttackType> Types = new();

        static IntPtr spawnerPtr, songPtr;
        // Whether the game's music clock and seek use seconds instead of ms. Worked out on first seek.
        static bool? musicInSeconds;

        public static bool InLevel => Valid();

        public static void Attach(LogicSpawnCreator spawner, MetaSong song, string level)
        {
            spawnerPtr = spawner.Pointer;
            songPtr = song.Pointer;
            if (Types.Count == 0) LoadTypes();
            if (Level != level)
            {
                Level = level;
                Load();
            }
            else if (Dirty)
            {
                // The level restarted (death, retry) and loaded the file from disk; put the
                // unsaved edits back so what's playing matches the editor.
                ApplyToGame();
            }
        }

        static bool Valid()
        {
            if (spawnerPtr == IntPtr.Zero || !SafeRead.IsObjectOf<LogicSpawnCreator>(spawnerPtr)) return false;
            var spawner = new LogicSpawnCreator(spawnerPtr);
            return SafeRead.FieldPointer(spawner, "metaSong") == songPtr && SafeRead.IsObjectOf<MetaSong>(songPtr);
        }

        static void LoadTypes()
        {
            Types.Clear();
            foreach (var node in BossFile.CatalogCache?["attackTypes"]?.AsArray() ?? new JsonArray())
            {
                var t = new AttackType
                {
                    Type = (string)node["type"],
                    WarningDefault = (float?)node["warningDefault"] ?? 0,
                    UsesPosition = (bool?)node["usesPosition"] ?? true,
                };
                if (t.Type == null) continue;
                foreach (var kv in node["params"]?.AsObject() ?? new JsonObject())
                    t.Params.Add(new(kv.Key, kv.Value?.ToString() ?? ""));
                Types.Add(t);
            }
            Types.Sort((a, b) => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase));
        }

        public static AttackType TypeInfo(string type) => Types.FirstOrDefault(t => t.Type == type);

        static string CustomPath => Path.Combine(Plugin.Root, Level + ".json");
        static string DumpPath => Path.Combine(Plugin.DumpDir, Level + ".json");
        public static bool HasCustomFile => File.Exists(CustomPath);

        // Your saved boss if there is one, otherwise the level's original attacks.
        public static void Load()
        {
            var path = HasCustomFile ? CustomPath : DumpPath;
            MusicFile = null;
            Spawns = File.Exists(path) ? FromJson(JsonNode.Parse(File.ReadAllText(path))) : new List<EditSpawn>();
            Dirty = AppliedDirty = false;
        }

        public static void LoadOriginal()
        {
            var music = MusicFile;
            Spawns = File.Exists(DumpPath) ? FromJson(JsonNode.Parse(File.ReadAllText(DumpPath))) : new List<EditSpawn>();
            MusicFile = music;
            Dirty = AppliedDirty = true;
        }

        // Starts an empty boss but keeps the level's checkpoints.
        public static void Clear()
        {
            Spawns = Spawns.Where(s => s.Type == "CheckPoint").ToList();
            Dirty = AppliedDirty = true;
        }

        public static void Save()
        {
            File.WriteAllText(CustomPath, ToJson().ToJsonString(Plugin.Pretty));
            Dirty = false;
            Plugin.Logger.LogInfo($"Editor saved {Spawns.Count} attacks to {CustomPath}");
        }

        // Puts the original level back: deletes the boss file and reloads the original attacks.
        public static void RevertToOriginal()
        {
            if (HasCustomFile) File.Move(CustomPath, CustomPath + ".bak", true);
            Load();
            ApplyToGame();
        }

        static List<EditSpawn> FromJson(JsonNode file)
        {
            var list = new List<EditSpawn>();
            Bpm = (float?)file["editor"]?["bpm"] ?? Bpm;
            MusicFile = (string)file["music"];
            foreach (var layer in file["layers"]?.AsArray() ?? new JsonArray())
                foreach (var s in layer["spawns"]?.AsArray() ?? new JsonArray())
                {
                    var type = (string)s["type"];
                    if (type == null) continue;
                    var spawn = new EditSpawn
                    {
                        Type = type,
                        Time = (float?)s["time"] ?? 0,
                        Warning = (float?)s["warning"] ?? 0,
                        Outro = (float?)s["outro"] ?? 0,
                        X = (float?)s["x"],
                        Y = (float?)s["y"],
                    };
                    foreach (var kv in s["params"]?.AsObject() ?? new JsonObject())
                        if (kv.Key != "timeline" && kv.Key != "pos" && kv.Value != null)
                            spawn.Params[kv.Key] = kv.Value.ToString();
                    list.Add(spawn);
                }
            return list;
        }

        public static JsonObject ToJson()
        {
            var spawns = new JsonArray();
            foreach (var s in Spawns.OrderBy(s => s.Time))
            {
                var o = new JsonObject { ["type"] = s.Type, ["time"] = s.Time, ["warning"] = s.Warning, ["outro"] = s.Outro };
                if (s.X.HasValue) { o["x"] = s.X.Value; o["y"] = s.Y ?? 360; }
                if (s.Params.Count > 0)
                {
                    var p = new JsonObject();
                    foreach (var kv in s.Params) p[kv.Key] = kv.Value;
                    o["params"] = p;
                }
                spawns.Add(o);
            }
            return new JsonObject
            {
                ["level"] = Level,
                ["music"] = MusicFile,
                ["editor"] = new JsonObject { ["bpm"] = Bpm },
                ["layers"] = new JsonArray { new JsonObject { ["layer"] = 0, ["spawns"] = spawns } },
            };
        }

        // ---------- game control ----------

        static DynamicMusic Music()
        {
            if (!Valid()) return null;
            return SafeRead.Field<DynamicMusic>(new LogicSpawnCreator(spawnerPtr), "sfxView");
        }

        // Song position in ms, or -1 if no level is running.
        public static float MusicMs()
        {
            var music = Music();
            if (music == null) return -1;
            var pos = music.position;
            return musicInSeconds == true ? pos * 1000 : pos;
        }

        public static void SetPaused(bool paused)
        {
            Music()?.pause(paused);
            UnityEngine.Time.timeScale = paused ? 0 : 1;
        }

        // Replaces the level's attacks with the editor's, then jumps the song to timeMs.
        public static bool ApplyToGame(float timeMs = -1)
        {
            if (!Valid()) return false;
            var spawner = new LogicSpawnCreator(spawnerPtr);
            var song = new MetaSong(songPtr);
            if (timeMs < 0) timeMs = Math.Max(0, MusicMs());
            // Round-trip through text so the importer sees exactly what a saved file gives it
            // (nodes built from floats refuse to be read back as doubles).
            var count = BossFile.Import(song, JsonNode.Parse(ToJson().ToJsonString()));
            if (count < 0) return false;
            AppliedDirty = false;
            try { spawner.destroyAllEnemies(false); } catch (Exception e) { Plugin.Logger.LogWarning("destroyAllEnemies: " + e.Message); }
            Seek(spawner, timeMs);
            return true;
        }

        public static void Seek(float timeMs)
        {
            if (Valid()) Seek(new LogicSpawnCreator(spawnerPtr), timeMs);
        }

        static void Seek(LogicSpawnCreator spawner, float timeMs)
        {
            // Next attack to spawn is the first one at or after the new time.
            spawner.indexOfNext = Spawns.Count(s => s.Time < timeMs);
            var music = Music();
            if (musicInSeconds == null && music != null)
            {
                spawner.putMusicToWithoutSkipping(timeMs);
                var pos = music.position;
                // If asking for e.g. 30000 left the clock near 30000, it's ms; if the clock can't
                // go that far but takes 30, it's seconds.
                musicInSeconds = Math.Abs(pos - timeMs) > 500 && timeMs > 2000;
                Plugin.Logger.LogInfo($"Editor seek check: asked {timeMs}, music at {pos} -> {(musicInSeconds == true ? "seconds" : "ms")}");
            }
            spawner.putMusicToWithoutSkipping(musicInSeconds == true ? timeMs / 1000 : timeMs);
        }
    }
}
