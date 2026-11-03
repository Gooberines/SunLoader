using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SunLoader
{
    // Custom bosses in multiplayer need a key from the owner. The mod only holds hashes of the keys
    // (MultiplayerKeyHashes.cs, made by tools\MakeKeys.ps1), so the keys can't be read out of it.
    // Each key works for one GitHub account (see Share.ClaimKey). The claimed key is saved encrypted
    // for this Windows user, and checked again each time the game runs, in case a claim was removed
    // so the key could be passed on.
    static class MultiplayerKeys
    {
        const string Salt = "sunloader-multiplayer-v1:";
        const string ClaimSalt = "sunloader-key-claim-v1:";
        static string savedKey, savedLogin;
        static bool loaded, rechecked;

        // Results from the background job, picked up on the game's thread.
        public static volatile bool Checking;
        static volatile string resultMessage;
        static volatile bool resultIsError, lockedAgain;

        static string SavedPath => Path.Combine(Plugin.Root, "multiplayer.key");

        public static bool Unlocked
        {
            get
            {
                Load();
                return savedKey != null && Share.SignedIn && string.Equals(savedLogin, Share.Login, StringComparison.OrdinalIgnoreCase);
            }
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(SavedPath)) return;
                var parts = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(SavedPath))).Split('\n');
                if (parts.Length == 2 && IsValid(parts[0])) { savedKey = parts[0]; savedLogin = parts[1]; }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Multiplayer key: couldn't read the saved key: " + e.Message); }
        }

        // Returns an error, or null if the key is being checked online.
        public static string Redeem(string typed)
        {
            var key = Normalize(typed);
            if (!IsValid(key)) return "That key isn't right. Check it and try again.";
            if (!Share.SignedIn) return "Sign in with GitHub first. A key belongs to one account.";
            if (Checking || !Share.ClaimKey(key, (owner, error) => Claimed(key, owner, error))) return "Sun Loader is busy with GitHub. Try again in a moment.";
            Checking = true;
            return null;
        }

        static void Claimed(string key, string owner, string error)
        {
            Checking = false;
            if (error == null && string.Equals(owner, Share.Login, StringComparison.OrdinalIgnoreCase))
            {
                try { File.WriteAllBytes(SavedPath, Dpapi.Protect(Encoding.UTF8.GetBytes(key + "\n" + owner))); }
                catch (Exception e) { Plugin.Logger.LogWarning("Multiplayer key: couldn't save it: " + e.Message); }
                savedKey = key;
                savedLogin = owner;
                rechecked = true;
                Say("Key accepted. Multiplayer is unlocked.", false);
            }
            else Say(error ?? "That key has already been used on another account.", true);
        }

        static void Say(string message, bool isError) { resultIsError = isError; resultMessage = message; }

        // The menu shows this once.
        public static string TakeMessage(out bool isError)
        {
            var message = resultMessage;
            isError = resultIsError;
            resultMessage = null;
            return message;
        }

        // Called every frame. Checks the saved key once, when GitHub is free.
        public static void Update()
        {
            if (lockedAgain)
            {
                lockedAgain = false;
                EditorGui.Toast("Your multiplayer key is now on another account, so multiplayer is locked again.");
            }
            if (rechecked || !Unlocked || Share.Busy) return;
            rechecked = true;
            var key = savedKey;
            Share.ClaimKey(key, (owner, error) =>
            {
                if (error != null || string.Equals(owner, Share.Login, StringComparison.OrdinalIgnoreCase)) return;   // offline keeps working
                savedKey = savedLogin = null;
                try { File.Delete(SavedPath); } catch { }
                lockedAgain = true;
                Plugin.Logger.LogInfo("Multiplayer key: it now belongs to another account");
            });
        }

        // What a claim comment holds for this key and account.
        public static string ClaimToken(string key, string login) => Hash(ClaimSalt + key + ":" + login.ToLowerInvariant());

        static bool IsValid(string key) => MultiplayerKeyHashes.All.Contains(Hash(Salt + key));

        // Spaces, dashes and upper or lower case don't matter when typing a key in.
        static string Normalize(string key) => new string((key ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
