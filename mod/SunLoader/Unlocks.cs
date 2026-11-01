using HarmonyLib;

namespace SunLoader
{
    // Makes every level playable while the mod is installed (UnlockAllLevels in the config).
    // Only the answers to "is this locked?" change; the save file is never touched, so
    // removing the mod or turning the setting off puts everything back.
    //
    // IL2CPP folds identical native functions together, so each hook can also fire for
    // unrelated objects. Each one only changes the answer for its own class.
    [HarmonyPatch]
    static class Unlocks
    {
        static bool On<T>(System.IntPtr ptr) where T : Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase =>
            Plugin.UnlockAll.Value && SafeRead.IsObjectOf<T>(ptr);

        [HarmonyPostfix, HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.isUnlocked))]
        static void ArcadeUnlocked(MetaArcadeSongProgress __instance, ref bool __result)
        {
            if (On<MetaArcadeSongProgress>(__instance.Pointer)) __result = true;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.isrequirementSatisfied))]
        static void ArcadeRequirement(MetaArcadeSongProgress __instance, ref bool __result)
        {
            if (On<MetaArcadeSongProgress>(__instance.Pointer)) __result = true;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(MetaSongProgress), nameof(MetaSongProgress.isLocked))]
        static void SongLocked(MetaSongProgress __instance, ref bool __result)
        {
            if (On<MetaSongProgress>(__instance.Pointer)) __result = false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ViewPlaylistSong), nameof(ViewPlaylistSong.isLocked))]
        static void PlaylistSongLocked(ViewPlaylistSong __instance, ref bool __result)
        {
            if (On<ViewPlaylistSong>(__instance.Pointer)) __result = false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ModelStoryCheckpoint), nameof(ModelStoryCheckpoint.isUnlockedInPlaylist))]
        static void LevelInPlaylist(ModelStoryCheckpoint __instance, ref bool __result)
        {
            if (On<ModelStoryCheckpoint>(__instance.Pointer)) __result = true;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ModelStoryCheckpoint), nameof(ModelStoryCheckpoint.isExtraAndLocked))]
        static void ExtraLocked(ModelStoryCheckpoint __instance, ref bool __result)
        {
            if (On<ModelStoryCheckpoint>(__instance.Pointer)) __result = false;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(ModelSoundTrack), nameof(ModelSoundTrack.isUnlockedInPlaylist))]
        static void SoundTrackInPlaylist(ModelSoundTrack __instance, ref bool __result)
        {
            if (On<ModelSoundTrack>(__instance.Pointer)) __result = true;
        }
    }
}
