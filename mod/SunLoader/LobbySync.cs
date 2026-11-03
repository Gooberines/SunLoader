using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using HarmonyLib;
using UnityEngine;
using Il2CppList = Il2CppSystem.Collections.Generic;

namespace SunLoader
{
    // Custom bosses in online multiplayer. The host picks one of their bosses for the lobby;
    // it's packed into the Steam lobby's data (which Steam sends to every player), and every
    // player with Sun Loader plays it whenever its stage comes up in that lobby. The stage is
    // put in each song vote so it can come up. Online runs never earn suns.
    // Players without Sun Loader just get the stage's normal attacks, so public rooms, which are
    // full of strangers, stay on the normal levels: bosses are for Friends and Community rooms.
    static class LobbySync
    {
        const string Prefix = "sunloader.";
        const int ChunkSize = 7000;     // Steam allows 8 KB per lobby value
        const int MaxChunks = 24;

        // The lobby's boss, saved to bosses\.lobby\, or null.
        public static BossProject Boss;
        // The boss picked before there was a room: sent as soon as you're hosting one.
        public static BossProject Pending;
        // The boss wins every song vote. The host sets it; players read it from the lobby.
        public static bool AlwaysPlay = true;
        public static string BossName => Boss?.Name;
        // The song the host's boss uses that this player doesn't have, or null.
        public static string MissingSong;
        public static bool InLobby, IsHost, PublicRoom;
        public static int Players;
        public static List<string> Members = new();
        static ulong lobbyId;
        static string bossHash, badHash, lastError, songToApply;
        static float nextCheck, lastVoteNote;
        static bool toldAboutPublic, toldAboutKey;

        public static string Dir => Path.Combine(CustomBosses.Dir, ".lobby");

        // Called every frame; looks at the lobby twice a second.
        public static void Update()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 0.5f;
            try { Check(); }
            catch (Exception e)
            {
                // InLobby keeps its last value: the editor and No Damage stay off if this fails in a room.
                if (e.Message != lastError) Plugin.Logger.LogWarning("Lobby: " + e.Message);
                lastError = e.Message;
            }
        }

        public static void CheckSoon() => nextCheck = 0;

        static Steamworks.Data.Lobby? Current()
        {
            var ptr = SteamMatchmaker.mInstance?.Pointer ?? IntPtr.Zero;
            if (!SafeRead.IsObjectOf<SteamMatchmaker>(ptr)) return null;
            var lobby = new SteamMatchmaker(ptr).m_CurrentLobby;
            return (ulong)lobby.Id == 0 ? null : lobby;
        }

        static void Check()
        {
            var lobby = Current();
            InLobby = lobby != null;
            if (!InLobby) { Clear("left the lobby"); lobbyId = 0; IsHost = PublicRoom = false; return; }
            var l = lobby.Value;
            if ((ulong)l.Id != lobbyId)
            {
                Clear("joined another lobby");
                lobbyId = l.Id;
                badHash = null;
                toldAboutPublic = toldAboutKey = false;
                CustomBosses.OnJoinedLobby();
            }
            IsHost = l.Owner.IsMe;
            Players = l.MemberCount;
            PublicRoom = IsPublic(l);
            if (BossMultiplayerMenu.Open) ReadMembers();
            SendPending();
            if (songToApply != null && !Music.IsLoading(Boss?.Music))
            {
                ApplySong(songToApply);
                songToApply = null;
            }

            if (!IsHost) AlwaysPlay = l.GetData(Prefix + "always") != "0";
            var hash = l.GetData(Prefix + "hash");
            if (!string.IsNullOrEmpty(hash) && !MultiplayerKeys.Unlocked && !toldAboutKey)
            {
                toldAboutKey = true;
                EditorGui.Toast($"This room has a custom boss. Enter a multiplayer key ({Plugin.BossMultiplayerKey.Value}) to play it.");
            }
            if (string.IsNullOrEmpty(hash) || !Plugin.LobbyBosses.Value || !MultiplayerKeys.Unlocked)
            {
                Clear("no custom boss in this room");
                return;
            }
            if (hash == bossHash || hash == badHash) return;

            // A new boss from the host: put its pieces back together.
            int.TryParse(l.GetData(Prefix + "chunks"), out var chunks);
            if (chunks <= 0 || chunks > MaxChunks) return;
            var sb = new StringBuilder();
            for (int i = 0; i < chunks; i++)
            {
                var part = l.GetData(Prefix + "boss." + i);
                if (string.IsNullOrEmpty(part)) return;   // not all here yet; try again shortly
                sb.Append(part);
            }
            var packed = sb.ToString();
            if (Share.Hash(packed) != hash) return;
            try { Receive(packed, hash); }
            catch (Exception e)
            {
                // A complete boss that won't read won't get better: don't try again until it changes.
                badHash = hash;
                Clear("couldn't read the host's boss");
                Plugin.Logger.LogWarning("Lobby: couldn't read the host's boss: " + e.Message);
                EditorGui.Toast("The host's boss couldn't be read, so you'll get the normal levels.");
            }
        }

        // The boss you picked goes out once you're hosting a room that can take it.
        static void SendPending()
        {
            if (Pending == null || !IsHost) return;
            if (PublicRoom)
            {
                if (!toldAboutPublic) EditorGui.Toast($"Public rooms play the normal levels. '{Pending.Name}' waits for a Friends or Community room.");
                toldAboutPublic = true;
                return;
            }
            var picked = Pending;
            Pending = null;
            string err;
            try { err = Use(picked); } catch (Exception e) { err = e.Message; }
            EditorGui.Toast(err == null ? $"'{picked.Name}' sent to your room. {Plugin.BossMultiplayerKey.Value} manages it." : $"Your boss wasn't sent: {err}");
        }

        static bool IsPublic(Steamworks.Data.Lobby l)
        {
            var mode = l.GetData(SteamMatchmaker.ROOM_PROP_MODE);
            return !string.IsNullOrEmpty(mode) && mode == SteamMatchmaker.PROP_MODE_GLOBAL.ToString();
        }

        static void Receive(string packed, string hash)
        {
            var file = JsonNode.Parse(Share.Unpack(packed)).AsObject();
            var stage = (string)file["stage"];
            if (stage == null || file["layers"] == null) throw new Exception("the host's boss file is damaged");
            var name = CustomBosses.CleanName((string)file["name"]);
            if (name.Length == 0) name = "Lobby boss";
            if (file["music"] != null) file["music"] = Path.GetFileName((string)file["music"]);
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, "lobby boss.json");
            File.WriteAllText(path, file.ToJsonString(Plugin.Pretty));
            bossHash = hash;
            Boss = new BossProject
            {
                Path = path, Name = name, Stage = stage, Music = (string)file["music"], Modified = DateTime.Now,
                Attacks = file["layers"].AsArray().Sum(x => x["spawns"]?.AsArray().Count ?? 0),
            };
            MissingSong = null;
            if (Boss.Music != null && !Music.Files().Contains(Boss.Music)) { MissingSong = Boss.Music; Boss.Music = null; }   // you don't have the host's song
            Music.Get(Boss.Music);
            ApplySong(stage);
            songToApply = Music.IsLoading(Boss.Music) ? stage : null;   // OGG and MP3 finish loading later
            Plugin.Logger.LogInfo($"Lobby: custom boss '{name}' on '{stage}' ({Boss.Attacks} attacks)");
            EditorGui.Toast($"Room boss: '{name}' on {stage}. It earns no suns." + (MissingSong != null ? $" You don't have its song ({MissingSong}), so you'll hear the stage's." : ""));
        }

        static void ReadMembers()
        {
            var ptr = SteamMatchmaker.mInstance?.Pointer ?? IntPtr.Zero;
            if (!SafeRead.IsObjectOf<SteamMatchmaker>(ptr)) return;
            try
            {
                var names = new List<string>();
                var list = new SteamMatchmaker(ptr).GetRoomMembers();
                for (int i = 0; list != null && i < list.Count; i++)
                    if (list[i] is { } m) names.Add(m.playerName + (m.isHost ? " (host)" : "") + (m.isMe ? " (you)" : ""));
                Members = names;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Lobby: couldn't list the players: " + e.Message); }
        }

        // Tells the player what the vote means for the room's boss. Host and players reach this
        // from different hooks, and the host from more than one.
        public static void NoteVote()
        {
            if (Boss == null || Time.realtimeSinceStartup - lastVoteNote < 5) return;
            lastVoteNote = Time.realtimeSinceStartup;
            EditorGui.Toast(AlwaysPlay ? $"This round plays '{Boss.Name}' on {Boss.Stage}." : $"Vote for {Boss.Stage} to play '{Boss.Name}'.");
        }

        // Host: sets whether the boss wins every vote, and tells the room.
        public static void SetAlwaysPlay(bool always)
        {
            AlwaysPlay = always;
            if (Current() is { } l && l.Owner.IsMe) l.SetData(Prefix + "always", always ? "1" : "0");
        }

        // Why a boss can't go to a room, or null.
        public static string Problem(BossProject b)
        {
            if (b.Error != null) return b.Error;
            return CustomBosses.Stage(b.Stage) == null && CustomBosses.Stages().Count > 0 ? $"The stage '{b.Stage}' isn't in this version of the game" : null;
        }

        public static void InviteFriends()
        {
            var ptr = SteamMatchmaker.mInstance?.Pointer ?? IntPtr.Zero;
            if (!SafeRead.IsObjectOf<SteamMatchmaker>(ptr)) return;
            try { new SteamMatchmaker(ptr).OpenFriendsInviteUI(); }
            catch (Exception e) { Plugin.Logger.LogWarning("Lobby: couldn't open the invite window: " + e.Message); }
        }

        // The stage the host's vote has to include, or zero.
        public static IntPtr VoteStage()
        {
            if (!InLobby || !IsHost || Boss == null) return IntPtr.Zero;
            var ptr = CustomBosses.Stage(Boss.Stage)?.Checkpoint ?? IntPtr.Zero;
            return SafeRead.IsObjectOf<ModelStoryCheckpoint>(ptr) ? ptr : IntPtr.Zero;
        }

        // A stage whose song is already loaded doesn't set it again, so give it the right one now.
        static void ApplySong(string stage)
        {
            var info = CustomBosses.Stage(stage);
            if (info != null && SafeRead.IsObjectOf<ModelStoryCheckpoint>(info.Checkpoint))
                SetAudioClipPatch.Apply(new ModelStoryCheckpoint(info.Checkpoint));
        }

        static void Clear(string why)
        {
            if (Boss == null) { bossHash = null; return; }
            Plugin.Logger.LogInfo("Lobby: " + why + "; lobby boss off");
            var path = Boss.Path;
            Boss = null; bossHash = null; MissingSong = null; songToApply = null;
            SetAudioClipPatch.RestoreOriginals();
            try { File.Delete(path); } catch (Exception e) { Plugin.Logger.LogWarning("Lobby: couldn't remove the lobby's boss file: " + e.Message); }
        }

        // Host only: sends one of your bosses to everyone in the lobby. Returns an error or null.
        public static string Use(BossProject b)
        {
            var lobby = Current();
            if (lobby == null) return "You're not in an online lobby";
            var l = lobby.Value;
            if (!l.Owner.IsMe) return "Only the host can pick the lobby's boss";
            if (!MultiplayerKeys.Unlocked) return "Enter a multiplayer key first";
            if (IsPublic(l)) return "Public rooms play the normal levels. Bosses are for Friends and Community rooms";
            if (!Plugin.LobbyBosses.Value) return "PlayLobbyBosses is off in Sun Loader's settings";
            var err = Pack(b, out var packed);
            if (err != null) return err;
            var chunks = (packed.Length + ChunkSize - 1) / ChunkSize;
            for (int i = 0; i < chunks; i++)
                if (!l.SetData(Prefix + "boss." + i, packed.Substring(i * ChunkSize, Math.Min(ChunkSize, packed.Length - i * ChunkSize))))
                    return "Steam wouldn't save the lobby data";
            l.SetData(Prefix + "chunks", chunks.ToString());
            l.SetData(Prefix + "always", AlwaysPlay ? "1" : "0");
            l.SetData(Prefix + "name", b.Name);
            l.SetData(Prefix + "stage", b.Stage);
            l.SetData(Prefix + "hash", Share.Hash(packed));   // last, so players only read a complete boss
            nextCheck = 0;
            Plugin.Logger.LogInfo($"Lobby: sent '{b.Name}' to the lobby ({chunks} parts)");
            return null;
        }

        // Checks a boss can be sent and packs it. Returns an error or null.
        static string Pack(BossProject b, out string packed)
        {
            packed = null;
            var problem = Problem(b);
            if (problem != null) return problem;
            var file = JsonNode.Parse(File.ReadAllText(b.Path)).AsObject();
            file.Remove("shared");
            file["name"] = b.Name;
            packed = Share.Pack(file.ToJsonString());
            return (packed.Length + ChunkSize - 1) / ChunkSize > MaxChunks ? "This boss is too big to send through the lobby" : null;
        }

        // For picking a boss before there's a room: says now what would stop it being sent later.
        public static string CanSend(BossProject b)
        {
            try { return Pack(b, out _); }
            catch (Exception e) { return "Can't read this boss: " + e.Message; }
        }

        public static void Stop()
        {
            var lobby = Current();
            if (lobby == null || !lobby.Value.Owner.IsMe) return;
            lobby.Value.SetData(Prefix + "hash", "");
            nextCheck = 0;
        }
    }

    // The host picks the three songs of a vote and sends them to everyone. If the lobby boss's
    // stage isn't one of them, it takes the first spot, both on the host and in what's sent.
    [HarmonyPatch]
    static class LobbyVotePatches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(LogicMultiplayerVote), nameof(LogicMultiplayerVote.PickVoteSongs))]
        static void Picked(LogicMultiplayerVote __instance)
        {
            try
            {
                if (!SafeRead.IsObjectOf<LogicMultiplayerVote>(__instance.Pointer)) return;
                var stage = LobbySync.VoteStage();
                if (stage == IntPtr.Zero) return;
                LobbySync.NoteVote();
                if (__instance.CurrentSongVote1?.Pointer == stage || __instance.CurrentSongVote2?.Pointer == stage || __instance.CurrentSongVote3?.Pointer == stage) return;
                __instance.CurrentSongVote1 = new ModelStoryCheckpoint(stage);
                Plugin.Logger.LogInfo("Lobby: put the boss's stage in the vote");
            }
            catch (Exception e) { Plugin.Logger.LogError("Lobby: couldn't change the vote: " + e); }
        }

        // Players get the vote's songs from the host here.
        [HarmonyPostfix, HarmonyPatch(typeof(LogicMultiplayerVote), nameof(LogicMultiplayerVote.InjectVoteSongs))]
        static void Received(LogicMultiplayerVote __instance)
        {
            if (SafeRead.IsObjectOf<LogicMultiplayerVote>(__instance.Pointer)) LobbySync.NoteVote();
        }

        [HarmonyPrefix, HarmonyPatch(typeof(LogicMultiplayerSendReliablePacket), nameof(LogicMultiplayerSendReliablePacket.sendVoteStarted))]
        static void Sending(LogicMultiplayerSendReliablePacket __instance, Il2CppList.List<ModelStoryCheckpoint> songsForVote)
        {
            try
            {
                if (!SafeRead.IsObjectOf<LogicMultiplayerSendReliablePacket>(__instance.Pointer) || songsForVote == null || songsForVote.Count == 0) return;
                var stage = LobbySync.VoteStage();
                if (stage == IntPtr.Zero) return;
                for (int i = 0; i < songsForVote.Count; i++)
                    if (songsForVote[i]?.Pointer == stage) return;
                songsForVote[0] = new ModelStoryCheckpoint(stage);
                Plugin.Logger.LogInfo("Lobby: sent the boss's stage in the vote");
            }
            catch (Exception e) { Plugin.Logger.LogError("Lobby: couldn't change the vote: " + e); }
        }
    }

    // Host, with Every round plays this boss on: the boss's stage wins the vote.
    [HarmonyPatch]
    static class LobbyWinnerPatches
    {
        static void Force(ref ModelStoryCheckpoint song)
        {
            if (!LobbySync.AlwaysPlay) return;
            var stage = LobbySync.VoteStage();
            if (stage == IntPtr.Zero || song?.Pointer == stage) return;
            song = new ModelStoryCheckpoint(stage);
            Plugin.Logger.LogInfo("Lobby: the boss's stage wins the vote");
        }

        [HarmonyPrefix, HarmonyPatch(typeof(LogicMultiplayerVote), nameof(LogicMultiplayerVote.pickWinner))]
        static void Pick(LogicMultiplayerVote __instance, ref ModelStoryCheckpoint model)
        {
            if (SafeRead.IsObjectOf<LogicMultiplayerVote>(__instance.Pointer)) Force(ref model);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(LogicMultiplayerSendReliablePacket), nameof(LogicMultiplayerSendReliablePacket.sendVoteWinner))]
        static void Send(LogicMultiplayerSendReliablePacket __instance, ref ModelStoryCheckpoint song)
        {
            if (SafeRead.IsObjectOf<LogicMultiplayerSendReliablePacket>(__instance.Pointer)) Force(ref song);
        }
    }

    // Beating a level online: the lobby's boss gets a message, and nothing is recorded for suns.
    [HarmonyPatch(typeof(LogicMultiplayerEndLevel), nameof(LogicMultiplayerEndLevel.onCompleteLastCheckpoint))]
    static class LobbyBeatenPatch
    {
        static void Postfix(LogicMultiplayerEndLevel __instance)
        {
            if (!SafeRead.IsObjectOf<LogicMultiplayerEndLevel>(__instance.Pointer)) return;
            if (LobbySync.Boss is { } boss && LevelRun.Modified && LevelRun.Id == boss.Stage)
                EditorGui.Toast($"You beat '{boss.Name}'! Multiplayer bosses don't earn suns.");
        }
    }
}
