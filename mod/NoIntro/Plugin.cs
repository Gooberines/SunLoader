using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace NoIntro
{
    // Skips the intro video at startup. The download this replaces did it by overwriting
    // JSB_Data\StreamingAssets\intro.webm with an empty file, so the video failed and the
    // game moved on. This ends the intro the moment it starts instead, without touching
    // any game files.
    [BepInPlugin("riptid.jsb.nointro", "No Intro", "1.0.0")]
    public class Plugin : BasePlugin
    {
        internal static BepInEx.Logging.ManualLogSource Logger;

        public override void Load()
        {
            Logger = Log;
            new Harmony("riptid.jsb.nointro").PatchAll();
            Log.LogInfo("No Intro ready: the intro video will be skipped.");
        }
    }

    [HarmonyPatch(typeof(FullScreenVideo), nameof(FullScreenVideo.start))]
    static class SkipIntro
    {
        static void Postfix(FullScreenVideo __instance)
        {
            try
            {
                // IL2CPP can share one native start() between classes; only act on real videos.
                var video = __instance.TryCast<FullScreenVideo>();
                if (video == null || !IsIntro(video.fileName)) return;
                Plugin.Logger.LogInfo("Skipping the intro video");
                video.OnVideoEnd();
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Couldn't skip the intro: " + e.Message); }
        }

        static bool IsIntro(string file)
        {
            if (string.IsNullOrEmpty(file)) return false;
            if (file == FullScreenVideo.INTRO_VIDEO_PATH) return true;
            var name = Path.GetFileNameWithoutExtension(file.Replace('/', '\\'));
            return name.Equals("intro", StringComparison.OrdinalIgnoreCase);
        }
    }
}
