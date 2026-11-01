using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using UnityEngine;

namespace SunLoader
{
    // Custom bosses in online multiplayer. The host picks one of their bosses for the lobby;
    // it's packed into the Steam lobby's data (which Steam sends to every player), and every
    // player with Sun Loader plays it whenever its stage comes up in that lobby.
    // Players without Sun Loader just get the stage's normal attacks.
    static class LobbySync
    {
        const string Prefix = "sunloader.";
        const int ChunkSize = 7000;     // Steam allows 8 KB per lobby value
        const int MaxChunks = 24;

        // The lobby's boss, saved to bosses\.lobby\, or null.
        public static BossProject Boss;
        public static bool InLobby, IsHost;
        public static int Players;
        static ulong lobbyId;
        static string bossHash;
        static float nextCheck;

        public static string Dir => Path.Combine(CustomBosses.Dir, ".lobby");

        // Called every frame; looks at the lobby twice a second.
        public static void Update()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 0.5f;
            try { Check(); }
            catch (Exception e)
            {
                if (InLobby) Plugin.Logger.LogWarning("Lobby: " + e.Message);
                InLobby = IsHost = false; Clear("lobby error");
            }
        }

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
            if (!InLobby) { Clear("left the lobby"); lobbyId = 0; return; }
            var l = lobby.Value;
            if ((ulong)l.Id != lobbyId) { Clear("joined another lobby"); lobbyId = l.Id; }
            IsHost = l.Owner.IsMe;
            Players = l.MemberCount;

            var hash = l.GetData(Prefix + "hash");
            if (string.IsNullOrEmpty(hash) || !Plugin.LobbyBosses.Value)
            {
                Clear("the host turned the custom boss off");
                return;
            }
            if (hash == bossHash) return;

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
            if (Boss.Music != null && !Music.Files().Contains(Boss.Music)) Boss.Music = null;   // you don't have the host's song
            Music.Get(Boss.Music);
            ApplySong(stage);
            Plugin.Logger.LogInfo($"Lobby: custom boss '{name}' on '{stage}' ({Boss.Attacks} attacks)");
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
            Boss = null; bossHash = null;
            SetAudioClipPatch.RestoreOriginals();
        }

        // Host only: sends one of your bosses to everyone in the lobby. Returns an error or null.
        public static string Use(BossProject b)
        {
            var lobby = Current();
            if (lobby == null) return "You're not in an online lobby";
            var l = lobby.Value;
            if (!l.Owner.IsMe) return "Only the host can pick the lobby's boss";
            var file = JsonNode.Parse(File.ReadAllText(b.Path)).AsObject();
            file.Remove("shared");
            file["name"] = b.Name;
            var packed = Share.Pack(file.ToJsonString());
            var chunks = (packed.Length + ChunkSize - 1) / ChunkSize;
            if (chunks > MaxChunks) return "This boss is too big to send through the lobby";
            for (int i = 0; i < chunks; i++)
                if (!l.SetData(Prefix + "boss." + i, packed.Substring(i * ChunkSize, Math.Min(ChunkSize, packed.Length - i * ChunkSize))))
                    return "Steam wouldn't save the lobby data";
            l.SetData(Prefix + "chunks", chunks.ToString());
            l.SetData(Prefix + "name", b.Name);
            l.SetData(Prefix + "stage", b.Stage);
            l.SetData(Prefix + "hash", Share.Hash(packed));   // last, so players only read a complete boss
            nextCheck = 0;
            Plugin.Logger.LogInfo($"Lobby: sent '{b.Name}' to the lobby ({chunks} parts)");
            return null;
        }

        public static void Stop()
        {
            var lobby = Current();
            if (lobby == null || !lobby.Value.Owner.IsMe) return;
            lobby.Value.SetData(Prefix + "hash", "");
            nextCheck = 0;
        }
    }
}
