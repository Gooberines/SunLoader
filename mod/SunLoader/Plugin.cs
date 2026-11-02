using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace SunLoader
{
    [BepInPlugin("riptid.jsb.sunloader", "Sun Loader", Version)]
    public class Plugin : BasePlugin
    {
        public const string Version = "0.2.0-alpha";
        internal static ManualLogSource Logger;
        // BepInEx\SunLoader\<level>.json replaces that level; dumps\ holds the originals.
        internal static string Root;
        internal static string DumpDir;
        internal static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
        internal static BepInEx.Configuration.ConfigEntry<bool> UnlockAll;
        internal static BepInEx.Configuration.ConfigEntry<UnityEngine.KeyCode> EditorKey;
        internal static BepInEx.Configuration.ConfigEntry<UnityEngine.KeyCode> ModMenuKey;
        internal static BepInEx.Configuration.ConfigEntry<UnityEngine.KeyCode> BossMenuKey;
        internal static BepInEx.Configuration.ConfigEntry<string> ShareRepo, ShareClientId, ShareDiscord;
        internal static BepInEx.Configuration.ConfigEntry<bool> LobbyBosses;
        internal static BepInEx.Configuration.ConfigEntry<string> UiFont;

        public override void Load()
        {
            Logger = Log;
            Root = Path.Combine(Paths.BepInExRootPath, "SunLoader");
            DumpDir = Path.Combine(Root, "dumps");
            MigrateFromCustomBosses();
            Directory.CreateDirectory(DumpDir);
            UnlockAll = Config.Bind("General", "UnlockAllLevels", true,
                "Makes every level playable while the mod is on. Your save file is not changed.");
            EditorKey = Config.Bind("Editor", "ToggleKey", UnityEngine.KeyCode.F1, "Opens and closes the in-game boss editor during a level.");
            ModMenuKey = Config.Bind("Mod menu", "ToggleKey", UnityEngine.KeyCode.F2, "Opens the mod menu from anywhere (it also has a MODS button on the main menu).");
            BossMenuKey = Config.Bind("Bosses", "ToggleKey", UnityEngine.KeyCode.F4, "Opens the Bosses menu, where you make and play your own bosses (it also has a BOSSES button on the main menu).");
            ShareRepo = Config.Bind("Sharing", "Repository", "Gooberines/SunLoader-Bosses",
                "The public GitHub repository (owner/name) that holds shared bosses. Each shared boss is an issue there.");
            ShareClientId = Config.Bind("Sharing", "GitHubAppClientId", "",
                "Client ID of the GitHub App used to sign in for uploading and rating. Empty = the one built into Sun Loader.");
            ShareDiscord = Config.Bind("Sharing", "DiscordInvite", "",
                "Discord invite link where banned players can appeal. Empty = the one built into Sun Loader.");
            LobbyBosses = Config.Bind("Multiplayer", "PlayLobbyBosses", true,
                "In an online lobby, play the custom boss the host picked (it's sent to you through the lobby).");
            UiFont = Config.Bind("UI", "Font", "", "Name (or part of it) of the game font the menus use. Empty = pick automatically. LogOutput.log lists the choices.");
            Share.Init();
            new Harmony("riptid.jsb.sunloader").PatchAll();
            AddComponent<BossEditorUI>();
            Logger.LogInfo($"Sun Loader loaded. Press {EditorKey.Value} in a level to edit its boss. Boss files: " + Root);
        }

        // This mod used to be called Custom Bosses. Moves its boss files and settings over once.
        void MigrateFromCustomBosses()
        {
            try
            {
                var oldRoot = Path.Combine(Paths.BepInExRootPath, "CustomBosses");
                if (Directory.Exists(oldRoot) && !Directory.Exists(Root))
                {
                    Directory.Move(oldRoot, Root);
                    Logger.LogInfo($"Moved your bosses from {oldRoot} to {Root}");
                }
                var oldConfig = Path.Combine(Paths.ConfigPath, "riptid.jsb.custombosses.cfg");
                if (File.Exists(oldConfig) && !File.Exists(Config.ConfigFilePath))
                {
                    File.Move(oldConfig, Config.ConfigFilePath);
                    Config.Reload();
                    Logger.LogInfo("Moved your settings to " + Config.ConfigFilePath);
                }
            }
            catch (Exception e) { Logger.LogWarning("Couldn't move files from the old Custom Bosses folder: " + e.Message); }
        }
    }

    // Remembers each level's name by its MetaSong when the game loads it from disk. Later
    // we can't read MetaSong.modelSong (it held a bad pointer and crashed the game).
    [HarmonyPatch(typeof(ModelStoryCheckpoint), nameof(ModelStoryCheckpoint.SetMetaSong))]
    static class SetMetaSongPatch
    {
        internal static readonly Dictionary<IntPtr, string> LevelBySong = new();

        static void Postfix(ModelStoryCheckpoint __instance)
        {
            var songPtr = SafeRead.FieldPointer(__instance, "metaSong");
            if (!SafeRead.IsObjectOf<MetaSong>(songPtr)) return;
            string name = null;
            try { name = __instance.name; } catch (Exception e) { Plugin.Logger.LogWarning("Couldn't read level name: " + e.Message); }
            var id = FileId(name, __instance.nId);
            LevelBySong[songPtr] = id;
            Plugin.Logger.LogInfo($"Level loaded: '{id}'");
        }

        internal static string FileId(string name, int fallbackId)
        {
            if (string.IsNullOrWhiteSpace(name)) name = "level_" + fallbackId;
            var bad = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        }
    }

    // Runs right after a level's attack spawner starts, before the song plays. By then the
    // MetaSong and the attack catalog are fully built (they aren't yet at SetMetaSong).
    [HarmonyPatch(typeof(LogicSpawnCreator), nameof(LogicSpawnCreator.start))]
    static class SpawnCreatorStartPatch
    {
        static bool catalogWritten;

        static void Postfix(LogicSpawnCreator __instance)
        {
            // Tracks the step we're on so a failure says exactly where it happened.
            var stage = "read metaSong";
            try
            {
                // IL2CPP folds identical native functions together, so this hook also fires
                // for unrelated objects. Only real spawners get past this check.
                if (!SafeRead.IsObjectOf<LogicSpawnCreator>(__instance.Pointer)) return;
                var songPtr = SafeRead.FieldPointer(__instance, "metaSong");
                if (!SafeRead.IsObjectOf<MetaSong>(songPtr))
                {
                    Plugin.Logger.LogInfo($"Spawner started without a readable MetaSong (0x{songPtr.ToInt64():X}), ignored.");
                    return;
                }
                if (!SetMetaSongPatch.LevelBySong.TryGetValue(songPtr, out var id))
                {
                    Plugin.Logger.LogInfo("Spawner started for a MetaSong we never saw load (menu/preview?), ignored.");
                    return;
                }
                var song = new MetaSong(songPtr);
                Plugin.Logger.LogInfo($"Spawner started for '{id}'");

                stage = "count attack types";
                var typeCount = BossFile.AttackTypeCount();
                Plugin.Logger.LogInfo($"  attackTypes={typeCount}");
                if (!catalogWritten && typeCount > 0)
                {
                    stage = "write attack catalog";
                    BossFile.CatalogCache = BossFile.Catalog();
                    File.WriteAllText(Path.Combine(Plugin.Root, "attack_types.json"), BossFile.CatalogCache.ToJsonString(Plugin.Pretty));
                    catalogWritten = true;
                }

                var dumpPath = Path.Combine(Plugin.DumpDir, id + ".json");
                if (!File.Exists(dumpPath))
                {
                    stage = "dump level";
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    File.WriteAllText(dumpPath, BossFile.Export(id, song).ToJsonString(Plugin.Pretty));
                    Plugin.Logger.LogInfo($"Dumped level '{id}' in {timer.ElapsedMilliseconds} ms -> {dumpPath}");
                    stage = "write seen attack types";
                    File.WriteAllText(Path.Combine(Plugin.Root, "attack_types_seen.json"), BossFile.SeenTypes().ToJsonString(Plugin.Pretty));
                }

                // A boss started from the Bosses menu wins over this level's own replacement.
                var bossPath = CustomBosses.FileFor(id);
                var customPath = bossPath ?? Path.Combine(Plugin.Root, id + ".json");
                stage = "add checkpoints";
                CustomBosses.EnsureCheckpoints(customPath, id);
                var replaced = false;
                if (File.Exists(customPath))
                {
                    stage = "load custom boss";
                    var count = BossFile.Import(song, JsonNode.Parse(File.ReadAllText(customPath)));
                    if (count >= 0)
                    {
                        replaced = true;
                        __instance.indexOfNext = 0;
                        Plugin.Logger.LogInfo($"Loaded custom boss for '{id}' ({count} attacks) from {customPath}");
                    }
                }

                LevelRun.Started(id, replaced);
                AntiCheat.RunStarted(BossFile.LastCheckpointMs(replaced ? customPath : dumpPath));
                stage = "attach editor";
                EditorSession.Attach(__instance, song, id, bossPath);
                if (EditorSession.Dirty) LevelRun.Modified = true;   // unsaved editor changes were put back in
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Sun Loader failed at '{stage}': " + e);
            }
        }
    }

    // Our boss file format. The game's own encode() is stripped from release builds,
    // so we read and build MetaSongs straight from their objects, via SafeRead.
    static class BossFile
    {
        // The attack catalog from the first level played this session, for the in-game editor.
        internal static JsonObject CatalogCache;

        // Every attack type seen in dumped levels, with each setting it was used with and an example value.
        static readonly SortedDictionary<string, JsonObject> seen = new();

        public static JsonObject SeenTypes()
        {
            var types = new JsonObject();
            foreach (var kv in seen) types[kv.Key] = Copy(kv.Value);
            return new JsonObject { ["attackTypes"] = types };
        }

        static void RecordSeen(string type, int typeIndex, JsonObject parameters)
        {
            if (type == null) return;
            if (!seen.TryGetValue(type, out var entry))
                seen[type] = entry = new JsonObject { ["typeIndex"] = typeIndex, ["uses"] = 0, ["params"] = new JsonObject() };
            entry["uses"] = (int)entry["uses"] + 1;
            var known = entry["params"].AsObject();
            foreach (var p in parameters)
                if (!known.ContainsKey(p.Key)) known[p.Key] = Copy(p.Value);
        }

        // JsonNode.DeepClone isn't in .NET 6, which is what BepInEx runs on.
        static JsonNode Copy(JsonNode node) => node == null ? null : JsonNode.Parse(node.ToJsonString());

        public static int AttackTypeCount()
        {
            var count = ModelSpawnEnum.getNum();
            if (count > 0) return count;
            var all = ModelSpawnEnum.ALL;
            return all == null ? 0 : all.length;
        }

        public static JsonObject Catalog()
        {
            var types = new JsonArray();
            for (int i = 0; i < AttackTypeCount(); i++)
            {
                var model = ModelSpawnEnum.getFromIndex(i);
                if (model == null || !SafeRead.IsObjectOf<ModelSpawn>(model.Pointer)) continue;
                var parameters = new JsonObject();
                var usesPosition = false;
                // Same generic class-check problem as ReadParams, so match by class name.
                var vectorPtr = SafeRead.FieldPointer(model, "modelParams");
                var modelParams = SafeRead.ClassName(vectorPtr).StartsWith("VectorFlash") ? new VectorFlash<ModelParam>(vectorPtr) : null;
                for (int p = 0; modelParams != null && p < modelParams.length; p++)
                {
                    var param = modelParams[p];
                    if (param == null || !SafeRead.IsObjectOf<ModelParam>(param.Pointer)) continue;
                    var key = SafeRead.String(param, "key");
                    if (key == "pos") usesPosition = true;
                    if (key == null || IsBuiltInParam(key)) continue;
                    var defaultValue = SafeRead.Field<MetaValue>(param, "defaultValue");
                    parameters[key] = defaultValue == null ? null : SafeRead.String(defaultValue, "value");
                }
                types.Add(new JsonObject
                {
                    ["type"] = ModelId(model.Pointer),
                    ["warningDefault"] = model.getWarningDefault(),
                    ["isCheckPoint"] = model.isCheckPoint,
                    ["usesPosition"] = usesPosition,
                    ["params"] = parameters,
                });
            }
            return new JsonObject { ["attackTypes"] = types };
        }

        // Reads every attack in the level. Only plain field reads on raw pointers: no game code
        // runs and no interop objects are made, so levels with thousands of attacks read quickly
        // and safely (the old way, with a wrapper and game calls per value, could crash the game).
        public static JsonObject Export(string levelId, MetaSong song)
        {
            SafeRead.BeginBatch();
            try { return ExportBatch(levelId, song); }
            finally { SafeRead.EndBatch(); }
        }

        static JsonObject ExportBatch(string levelId, MetaSong song)
        {
            var layers = new JsonArray();
            var group = SafeRead.Pointer(song.Pointer, "metaLayerGroup");
            var metaLayers = SafeRead.IsObjectOf<MetaSongLayerGroup>(group) ? SafeRead.Pointer(group, "metaLayers") : IntPtr.Zero;
            var layerCount = SafeRead.ListCount(metaLayers);
            Plugin.Logger.LogInfo($"  layers={layerCount}");
            if (layerCount >= 0)
            {
                for (int l = 0; l < layerCount; l++)
                {
                    var layer = SafeRead.ListItem(metaLayers, l);
                    if (!SafeRead.IsObjectOf<MetaSongLayer>(layer)) continue;
                    layers.Add(new JsonObject { ["layer"] = l, ["spawns"] = ExportSpawns(SafeRead.Pointer(layer, "metaSpawns")) });
                }
            }
            else
            {
                var inOrder = SafeRead.Pointer(song.Pointer, "allSpawnsInOrder");
                Plugin.Logger.LogInfo($"  spawnsInOrder={SafeRead.ListCount(inOrder)}");
                layers.Add(new JsonObject { ["layer"] = 0, ["spawns"] = ExportSpawns(inOrder) });
            }

            return new JsonObject
            {
                ["level"] = levelId,
                ["songLength"] = song.songLength,
                ["layers"] = layers,
            };
        }

        static JsonArray ExportSpawns(IntPtr list)
        {
            var spawns = new JsonArray();
            var count = SafeRead.ListCount(list);
            for (int s = 0; s < count; s++)
            {
                var ptr = SafeRead.ListItem(list, s);
                if (!SafeRead.IsObjectOf<MetaSpawn>(ptr)) { spawns.Add(new JsonObject { ["error"] = "unreadable spawn" }); continue; }
                try { spawns.Add(ExportSpawn(ptr)); }
                catch (Exception e) { spawns.Add(new JsonObject { ["error"] = e.Message }); }
            }
            return spawns;
        }

        static JsonObject ExportSpawn(IntPtr spawn)
        {
            var model = SafeRead.Pointer(spawn, "modelSpawn");
            if (!SafeRead.IsObjectOf<ModelSpawn>(model)) model = IntPtr.Zero;
            var group = SafeRead.Pointer(spawn, "metaParams");
            var timeline = SafeRead.Pointer(group, "metaTimeline");
            var pos = SafeRead.Pointer(group, "metaPosition");

            var type = ModelId(model);
            var parameters = ReadParams(type, group);
            var typeIndex = model == IntPtr.Zero ? -1 : SafeRead.Int(model, "indexInEnum");
            RecordSeen(type, typeIndex, parameters);
            var json = new JsonObject { ["type"] = type, ["typeIndex"] = typeIndex };
            if (SafeRead.IsObjectOf<MetaSpawnTimeline>(timeline))
            {
                json["time"] = Number(SafeRead.Pointer(timeline, "timeInMs"));
                json["warning"] = Number(SafeRead.Pointer(timeline, "warningTime"));
                json["outro"] = Number(SafeRead.Pointer(timeline, "outroTime"));
            }
            if (SafeRead.IsObjectOf<MetaSpawnParamPosition>(pos))
            {
                if (SafeRead.Bool(pos, "xUsed")) json["x"] = Number(SafeRead.Pointer(pos, "xRelativeToScreen"));
                if (SafeRead.Bool(pos, "yUsed")) json["y"] = Number(SafeRead.Pointer(pos, "yRelativeToScreen"));
            }
            if (parameters.Count > 0) json["params"] = parameters;
            return json;
        }

        // A MetaValue as a number: its text if that's a number, otherwise its pre-computed value.
        static float Number(IntPtr metaValue)
        {
            if (!SafeRead.IsObjectOf<MetaValue>(metaValue)) return 0;
            var text = SafeRead.String(metaValue, "value");
            if (text != null && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v;
            return SafeRead.Float(metaValue, "bakedNumber");
        }

        static readonly HashSet<string> diagnosed = new();

        // These are stored as the spawn's time/warning/outro and x/y, not as settings.
        static bool IsBuiltInParam(string key) => key == "timeline" || key == "pos";

        // An attack's settings (size, speed, ...), as the text the level file gives them.
        static JsonObject ReadParams(string type, IntPtr group)
        {
            var parameters = new JsonObject();
            var list = SafeRead.Pointer(group, "metaParams");
            int count = SafeRead.ListCount(list), read = 0;
            for (int i = 0; i < count; i++)
            {
                var param = SafeRead.ListItem(list, i);
                if (!SafeRead.IsObjectOf<MetaParam>(param)) continue;
                var key = SafeRead.String(param, "key");
                if (key == null || IsBuiltInParam(key)) continue;
                var value = SafeRead.String(param, "simpleValue");
                if (value == null)
                {
                    var meta = SafeRead.Pointer(param, "metaValue");
                    if (SafeRead.IsObjectOf<MetaValue>(meta)) value = SafeRead.String(meta, "value");
                }
                parameters[key] = value;
                read++;
            }
            // One line per attack type, so the log shows where reading stopped if it's empty.
            if (diagnosed.Add(type ?? "null"))
                Plugin.Logger.LogInfo($"  params for {type}: list={SafeRead.ClassName(list)}, count={count}, read={read}");
            return parameters;
        }

        // ModelBase.id is a getter, so it's game code: called once per attack type and remembered.
        static readonly Dictionary<IntPtr, string> modelIds = new();

        static string ModelId(IntPtr model)
        {
            if (model == IntPtr.Zero) return null;
            if (modelIds.TryGetValue(model, out var id)) return id;
            try { id = new ModelSpawn(model).id; } catch { id = null; }
            return modelIds[model] = id;
        }

        // When a level file's last checkpoint is, in ms: the earliest a fair run can end.
        public static float LastCheckpointMs(string path)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                return JsonNode.Parse(File.ReadAllText(path))["layers"].AsArray()
                    .SelectMany(l => l["spawns"]?.AsArray().AsEnumerable() ?? Enumerable.Empty<JsonNode>())
                    .Where(s => (string)s?["type"] == "CheckPoint")
                    .Select(s => (float?)s["time"] ?? 0)
                    .DefaultIfEmpty(0).Max();
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Couldn't read the level's length: " + e.Message); return 0; }
        }

        // Replaces every attack in the song with the ones in the file.
        // Returns how many were added, or -1 if the song couldn't be edited.
        public static int Import(MetaSong song, JsonNode file)
        {
            // A level with no checkpoint crashes the game (each level ends on one), so such a
            // boss isn't loaded and the level keeps its own attacks.
            if (!(file["layers"]?.AsArray().Any(l => l["spawns"]?.AsArray().Any(s => (string)s?["type"] == "CheckPoint") == true) ?? false))
            {
                Plugin.Logger.LogWarning("This boss has no checkpoints, so it wasn't loaded (a level needs at least its last checkpoint).");
                return -1;
            }
            var group = SafeRead.Field<MetaSongLayerGroup>(song, "metaLayerGroup");
            var metaLayers = SafeRead.Field<Il2CppList.List<MetaSongLayer>>(group, "metaLayers");
            if (metaLayers == null) return ImportFlat(song, file);
            for (int l = 0; l < metaLayers.Count; l++)
                metaLayers[l].metaSpawns.Clear();

            int added = 0;
            foreach (var layerNode in file["layers"].AsArray())
            {
                int layerIndex = (int?)layerNode["layer"] ?? 0;
                while (metaLayers.Count <= layerIndex) group.addNewLayer();
                var layer = group.getAt(layerIndex);

                foreach (var spawnNode in layerNode["spawns"].AsArray())
                {
                    var spawn = BuildSpawn(spawnNode);
                    if (spawn == null) continue;
                    layer.addSpawn(spawn);
                    added++;
                }
            }

            song.RefreshReferences();
            song.refreshAllSpawnInOrder();
            song.sortByTimeAndSetIndex();
            song.feedCheckPointList();
            return added;
        }

        // Shipped levels have no layer group: the spawner just walks allSpawnsInOrder.
        // The game's own refresh/sort methods go through the layer group, so we fill and
        // sort the lists ourselves.
        static int ImportFlat(MetaSong song, JsonNode file)
        {
            var inOrder = FieldList<MetaSpawn>(song, "allSpawnsInOrder");
            var checkPoints = FieldList<MetaSpawn>(song, "allCheckPoints");
            if (inOrder == null)
            {
                Plugin.Logger.LogWarning("This level has no readable attack list; custom boss not loaded.");
                return -1;
            }

            var spawns = new List<(float time, MetaSpawn spawn)>();
            foreach (var layerNode in file["layers"].AsArray())
                foreach (var spawnNode in layerNode["spawns"].AsArray())
                {
                    var spawn = BuildSpawn(spawnNode);
                    if (spawn != null) spawns.Add(((float)spawnNode["time"], spawn));
                }

            inOrder.Clear();
            checkPoints?.Clear();
            foreach (var (_, spawn) in spawns.OrderBy(s => s.time))
            {
                spawn.idInSong = inOrder.Count;
                inOrder.Add(spawn);
                if (spawn.modelSpawn.isCheckPoint) checkPoints?.Add(spawn);
            }
            return spawns.Count;
        }

        static Il2CppList.List<T> FieldList<T>(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase owner, string fieldName)
            where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase
        {
            var ptr = SafeRead.FieldPointer(owner, fieldName);
            return SafeRead.ClassName(ptr).StartsWith("List") ? new Il2CppList.List<T>(ptr) : null;
        }

        static MetaSpawn BuildSpawn(JsonNode spawnNode)
        {
            var type = (string)spawnNode["type"];
            var model = type == null ? null : ModelSpawnEnum.getFromId(type);
            if (model == null || model.isNull)
            {
                Plugin.Logger.LogWarning($"Unknown attack type '{type}', skipped.");
                return null;
            }

            var spawn = MetaSpawn.createInTime(model, (float)spawnNode["time"]);
            var timeline = spawn.metaParams.metaTimeline;
            if (spawnNode["warning"] != null) timeline.setWarning((float)spawnNode["warning"]);
            if (spawnNode["outro"] != null) timeline.setOutro((float)spawnNode["outro"]);
            if (spawnNode["x"] != null) spawn.metaParams.setX((int)Math.Round((double)spawnNode["x"]));
            if (spawnNode["y"] != null) spawn.metaParams.setY((int)Math.Round((double)spawnNode["y"]));

            if (spawnNode["params"] is JsonObject parameters)
            {
                foreach (var kv in parameters)
                {
                    var meta = spawn.metaParams.getMetaParamStr(kv.Key);
                    if (meta == null || meta.isNull())
                        Plugin.Logger.LogWarning($"Attack '{type}' has no param '{kv.Key}', skipped.");
                    else
                        meta.setValue(kv.Value?.ToString() ?? "");
                }
            }
            return spawn;
        }
    }
}
