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
    [BepInPlugin("riptid.jsb.sunloader", "Sun Loader", "0.1.0-alpha")]
    public class Plugin : BasePlugin
    {
        internal static ManualLogSource Logger;
        // BepInEx\SunLoader\<level>.json replaces that level; dumps\ holds the originals.
        internal static string Root;
        internal static string DumpDir;
        internal static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
        internal static BepInEx.Configuration.ConfigEntry<bool> UnlockAll;
        internal static BepInEx.Configuration.ConfigEntry<UnityEngine.KeyCode> EditorKey;
        internal static BepInEx.Configuration.ConfigEntry<UnityEngine.KeyCode> ModMenuKey;
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
            UiFont = Config.Bind("UI", "Font", "", "Name (or part of it) of the game font the menus use. Empty = pick automatically. LogOutput.log lists the choices.");
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
                    File.WriteAllText(dumpPath, BossFile.Export(id, song).ToJsonString(Plugin.Pretty));
                    Plugin.Logger.LogInfo($"Dumped level '{id}' -> {dumpPath}");
                    stage = "write seen attack types";
                    File.WriteAllText(Path.Combine(Plugin.Root, "attack_types_seen.json"), BossFile.SeenTypes().ToJsonString(Plugin.Pretty));
                }

                var customPath = Path.Combine(Plugin.Root, id + ".json");
                if (File.Exists(customPath))
                {
                    stage = "load custom boss";
                    var count = BossFile.Import(song, JsonNode.Parse(File.ReadAllText(customPath)));
                    if (count >= 0)
                    {
                        __instance.indexOfNext = 0;
                        Plugin.Logger.LogInfo($"Loaded custom boss for '{id}' ({count} attacks) from {customPath}");
                    }
                }

                stage = "attach editor";
                EditorSession.Attach(__instance, song, id);
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
                    ["type"] = ModelId(model),
                    ["warningDefault"] = model.getWarningDefault(),
                    ["isCheckPoint"] = model.isCheckPoint,
                    ["usesPosition"] = usesPosition,
                    ["params"] = parameters,
                });
            }
            return new JsonObject { ["attackTypes"] = types };
        }

        public static JsonObject Export(string levelId, MetaSong song)
        {
            var layers = new JsonArray();
            var group = SafeRead.Field<MetaSongLayerGroup>(song, "metaLayerGroup");
            var metaLayers = SafeRead.Field<Il2CppList.List<MetaSongLayer>>(group, "metaLayers");
            Plugin.Logger.LogInfo($"  layerGroup={group != null}, layers={metaLayers?.Count ?? -1}");
            if (metaLayers != null)
            {
                for (int l = 0; l < metaLayers.Count; l++)
                {
                    var layerPtr = SafeRead.ListItemPointer(metaLayers, l);
                    if (!SafeRead.IsObjectOf<MetaSongLayer>(layerPtr)) continue;
                    var spawns = SafeRead.Field<Il2CppList.List<MetaSpawn>>(new MetaSongLayer(layerPtr), "metaSpawns");
                    layers.Add(new JsonObject { ["layer"] = l, ["spawns"] = ExportSpawns(spawns) });
                }
            }
            else
            {
                var inOrder = SafeRead.Field<Il2CppList.List<MetaSpawn>>(song, "allSpawnsInOrder");
                Plugin.Logger.LogInfo($"  spawnsInOrder={inOrder?.Count ?? -1}");
                layers.Add(new JsonObject { ["layer"] = 0, ["spawns"] = ExportSpawns(inOrder) });
            }

            return new JsonObject
            {
                ["level"] = levelId,
                ["songLength"] = song.songLength,
                ["layers"] = layers,
            };
        }

        static JsonArray ExportSpawns(Il2CppList.List<MetaSpawn> metaSpawns)
        {
            var spawns = new JsonArray();
            for (int s = 0; metaSpawns != null && s < metaSpawns.Count; s++)
            {
                var ptr = SafeRead.ListItemPointer(metaSpawns, s);
                if (!SafeRead.IsObjectOf<MetaSpawn>(ptr)) { spawns.Add(new JsonObject { ["error"] = "unreadable spawn" }); continue; }
                try { spawns.Add(ExportSpawn(new MetaSpawn(ptr))); }
                catch (Exception e) { spawns.Add(new JsonObject { ["error"] = e.Message }); }
            }
            return spawns;
        }

        static JsonObject ExportSpawn(MetaSpawn spawn)
        {
            var model = SafeRead.Field<ModelSpawn>(spawn, "modelSpawn");
            var group = SafeRead.Field<MetaSpawnParamGroup>(spawn, "metaParams");
            var timeline = SafeRead.Field<MetaSpawnTimeline>(group, "metaTimeline");
            var pos = SafeRead.Field<MetaSpawnParamPosition>(group, "metaPosition");

            var type = ModelId(model);
            var parameters = ReadParams(type, group);
            var typeIndex = model?.indexInEnum ?? -1;
            RecordSeen(type, typeIndex, parameters);
            var json = new JsonObject { ["type"] = type, ["typeIndex"] = typeIndex };
            if (timeline != null)
            {
                json["time"] = timeline.time();
                json["warning"] = timeline.warningAsNumber();
                json["outro"] = timeline.outroAsNumber();
            }
            if (pos != null && pos.xUsed) json["x"] = pos.xAsNumber();
            if (pos != null && pos.yUsed) json["y"] = pos.yAsNumber();
            if (parameters.Count > 0) json["params"] = parameters;
            return json;
        }

        static readonly HashSet<string> diagnosed = new();

        // These are stored as the spawn's time/warning/outro and x/y, not as settings.
        static bool IsBuiltInParam(string key) => key == "timeline" || key == "pos";

        // An attack's settings (size, speed, ...). The generic List<MetaParam> class check
        // can fail even on a good list, so the list is checked by class name instead.
        static JsonObject ReadParams(string type, MetaSpawnParamGroup group)
        {
            var parameters = new JsonObject();
            var listPtr = SafeRead.FieldPointer(group, "metaParams");
            var className = SafeRead.ClassName(listPtr);
            var isList = className.StartsWith("List");
            var metaParams = isList ? new Il2CppList.List<MetaParam>(listPtr) : null;
            int count = metaParams?.Count ?? -1, read = 0;
            for (int i = 0; i < count; i++)
            {
                var ptr = SafeRead.ListItemPointer(metaParams, i);
                if (!SafeRead.IsObjectOf<MetaParam>(ptr)) continue;
                var param = new MetaParam(ptr);
                var key = SafeRead.String(param, "key");
                if (key == null || IsBuiltInParam(key)) continue;
                try { parameters[key] = param.getSimpleValue(); read++; }
                catch (Exception e) { parameters[key] = null; Plugin.Logger.LogWarning($"  param '{key}' on {type}: {e.Message}"); }
            }
            // One line per attack type, so the log shows where reading stopped if it's empty.
            if (diagnosed.Add(type ?? "null"))
            {
                var first = count > 0 ? SafeRead.ListItemPointer(metaParams, 0) : IntPtr.Zero;
                var firstKey = SafeRead.IsObjectOf<MetaParam>(first) ? SafeRead.ClassName(SafeRead.FieldPointer(new MetaParam(first), "key")) : "-";
                Plugin.Logger.LogInfo($"  params for {type}: list={className}, count={count}, read={read}, first={SafeRead.ClassName(first)}, key={firstKey}");
            }
            return parameters;
        }

        // ModelBase.id is a getter, not a field, so SafeRead can't reach it. Only call
        // it on a model that SafeRead has already checked.
        static string ModelId(ModelBase model)
        {
            if (model == null) return null;
            try { return model.id; } catch { return null; }
        }

        // Replaces every attack in the song with the ones in the file.
        // Returns how many were added, or -1 if the song couldn't be edited.
        public static int Import(MetaSong song, JsonNode file)
        {
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
