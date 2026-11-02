using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace SunLoader
{
    // Decides whether a beaten run may earn suns. A failed check only stops that run counting;
    // it never bans anyone, because a false alarm shouldn't cost a real player anything. Bans are
    // the repository owner's call (see Share.Ban), and players can appeal them on Discord.
    //
    // Sun Loader runs on the player's PC and is open source, so these stop casual cheating and
    // other mods, not someone determined to rebuild the mod. The clear comments' run details
    // are there for the owner to review.
    static class AntiCheat
    {
        static readonly HashSet<string> TrustedMods = new(StringComparer.OrdinalIgnoreCase)
            { "riptid.jsb.sunloader", "riptid.jsb.nointro", "example.jsb.hello" };

        // Why the last beaten run didn't count, shown with the way to appeal. Null if it counted.
        public static string LastFlag;

        static DateTime startedUtc = DateTime.MinValue;
        static float requiredMs;
        static string skipped;
        static DateTime sampledUtc;
        static float sampledMusic = -1;

        // A run begins. Wall-clock time is used because speed hacks usually slow or speed the
        // game's own timers, which makes those timers useless for catching them.
        public static void RunStarted(float lastCheckpointMs)
        {
            startedUtc = DateTime.UtcNow;
            requiredMs = lastCheckpointMs;
            skipped = null;
            sampledMusic = -1;
        }

        public static int RunSeconds => startedUtc == DateTime.MinValue ? 0 : (int)(DateTime.UtcNow - startedUtc).TotalSeconds;

        // Watches for the song jumping ahead of real time, twice a second. Rewinding goes back, so
        // it's fine, and the editor's own seeking is already covered by the editor check.
        public static void Tick(bool editorOpen)
        {
            if (editorOpen || !EditorSession.InLevel) { sampledMusic = -1; return; }
            var now = DateTime.UtcNow;
            if (sampledMusic >= 0 && (now - sampledUtc).TotalSeconds < 0.5) return;
            var music = EditorSession.MusicMs();
            if (music < 0) { sampledMusic = -1; return; }
            if (sampledMusic >= 0 && music - sampledMusic > (now - sampledUtc).TotalMilliseconds + 3000)
                skipped = "the song skipped ahead";
            sampledMusic = music;
            sampledUtc = now;
        }

        // Why this run can't count, or null if it can.
        public static string Problem(LogicArcadeEndLevel end)
        {
            if (skipped != null) return skipped;
            // Slowing the run isn't caught here, but finishing faster than the song is.
            if (requiredMs > 0 && (DateTime.UtcNow - startedUtc).TotalMilliseconds < requiredMs * 0.9f) return "it finished faster than the song";
            if (ForeignMod() is { } mod) return $"another mod is loaded ({mod})";
            if (HitHookOwner() is { } owner) return $"another mod changes getting hit ({owner})";
            if (AssistOn(end)) return "Assist Mode is on";
            return null;
        }

        static string ForeignMod()
        {
            foreach (var guid in IL2CPPChainloader.Instance.Plugins.Keys.Concat(ModLoader.LoadedGuids))
                if (!TrustedMods.Contains(guid)) return guid;
            return null;
        }

        // Any patch on the game's hit handlers from outside Sun Loader's own mods.
        static string HitHookOwner()
        {
            foreach (var name in new[] { nameof(HeroCollisionWithEnemy.hitByEnemy), nameof(HeroCollisionWithEnemy.hitByOtherActor) })
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(HeroCollisionWithEnemy), name));
                var foreign = info?.Owners.FirstOrDefault(o => !TrustedMods.Contains(o));
                if (foreign != null) return foreign;
            }
            return null;
        }

        static bool AssistOn(LogicArcadeEndLevel end)
        {
            try { return SafeRead.Field<ActorArcadeLevelLogic>(end, "actorArcadeLogic")?.isAssistModeOn() == true; }
            catch (Exception e) { Plugin.Logger.LogWarning("Anti-cheat: couldn't read Assist Mode: " + e.Message); return false; }
        }

        // Checks a beaten run; if it can't count, tells the player why and how to appeal.
        public static bool Reject(LogicArcadeEndLevel end, string name)
        {
            var problem = Problem(end);
            LastFlag = problem == null ? null : $"Your last run of {name} didn't count: {problem}.";
            if (problem == null) return false;
            Plugin.Logger.LogInfo("Anti-cheat: " + LastFlag);
            EditorGui.Toast(LastFlag + " Think that's wrong? You can appeal on the Discord (Bosses menu, Suns tab).");
            return true;
        }

        // What a clear comment records about the run, for the owner to review.
        public static string Evidence(string attacksHash = null) =>
            $"Run: {RunSeconds}s · Sun Loader {Plugin.Version}" + (attacksHash != null ? $"\nAttacks: {attacksHash}" : "");
    }
}
