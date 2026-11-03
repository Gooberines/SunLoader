using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SunLoader
{
    // Custom bosses in multiplayer need a key from the owner. The mod only holds hashes of the keys
    // (MultiplayerKeyHashes.cs, made by tools\MakeKeys.ps1), so the keys can't be read out of it.
    // A key that worked is saved, so it's only asked for once.
    static class MultiplayerKeys
    {
        const string Salt = "sunloader-multiplayer-v1:";
        static bool? unlocked;

        static string SavedPath => Path.Combine(Plugin.Root, "multiplayer.key");

        public static bool Unlocked
        {
            get
            {
                if (unlocked == null)
                {
                    try { unlocked = File.Exists(SavedPath) && IsValid(File.ReadAllText(SavedPath)); }
                    catch (Exception e) { Plugin.Logger.LogWarning("Multiplayer key: " + e.Message); unlocked = false; }
                }
                return unlocked.Value;
            }
        }

        // Saves the key if it's a real one.
        public static bool TryUnlock(string key)
        {
            if (!IsValid(key)) return false;
            unlocked = true;
            try { File.WriteAllText(SavedPath, Normalize(key)); }
            catch (Exception e) { Plugin.Logger.LogWarning("Multiplayer key: couldn't save it: " + e.Message); }
            Plugin.Logger.LogInfo("Multiplayer key accepted");
            return true;
        }

        static bool IsValid(string key) => MultiplayerKeyHashes.All.Contains(Hash(Normalize(key)));

        // Spaces, dashes and upper or lower case don't matter when typing a key in.
        static string Normalize(string key) => new string((key ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        static string Hash(string normalized) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Salt + normalized)));
    }
}
