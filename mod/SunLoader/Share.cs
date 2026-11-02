using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SunLoader
{
    // A boss someone shared online.
    class SharedBoss
    {
        public int Number;              // the GitHub issue that holds it
        public string Name, Author, Stage, Music, Description, Url, Data;
        public int Attacks, Likes, Dislikes, Comments;
        public bool IsLevel;            // one of the game's own levels, not a shared boss
        public string Artist;           // levels only
        public int Suns;                // 1-20, set by the repository owner (a label); 0 = unrated
        public string CurrentHash, RatedHash;   // the attacks now, and when the owner rated it
        // A boss changed after it was rated earns nothing until it's rated again, so nobody can
        // get a hard rating and then swap in an easy boss.
        public bool RatingStale => !IsLevel && RatedHash != null && CurrentHash != null && RatedHash != CurrentHash;
        public int EarnableSuns => RatingStale ? 0 : Suns;
        public string Difficulty => Share.DifficultyFor(Suns);
        public List<string> Labels = new();
        public int Clears;              // players who beat it
        public bool ClearedByMe;
        public DateTime Created;
        public int Score => Likes - Dislikes;
    }

    // Sharing and rating bosses, using a public GitHub repository (Sharing.Repo in the config)
    // as the server. Each shared boss is an issue: the title is the boss name, the body holds
    // its details and the boss file, and 👍 / 👎 reactions are its rating.
    //
    // Browsing and downloading use GitHub's public API, no account needed. Uploading and
    // rating need a GitHub login: the "device flow" shows a code to enter on github.com, and
    // the token it gives belongs to the Sun Loader GitHub App, which can only touch issues
    // on the bosses repository. The token is saved encrypted for this Windows user (DPAPI).
    //
    // Everything here runs off the game's main thread; the UI reads the public fields.
    static class Share
    {
        const string TitlePrefix = "[Boss] ";
        const string LevelPrefix = "[Level] ";
        const string LevelMarker = "<!-- sunloader-level v1 -->";
        const string Marker = "<!-- sunloader-boss v1 -->";
        const int MaxBody = 60000;      // GitHub allows 65536 characters

        public static string Repo => Plugin.ShareRepo.Value.Trim();
        // The Sun Loader GitHub App's client ID. It's public (not a secret): it only names the app.
        const string BuiltInClientId = "Iv23liEY6kUEKaYx0euE";
        public static string ClientId => Plugin.ShareClientId.Value.Trim() is { Length: > 0 } id ? id : BuiltInClientId;
        public static bool Configured => Repo.Contains('/') && ClientId.Length > 0;

        public static volatile List<SharedBoss> Bosses = new();
        // The game's own levels: one issue each ("[Level] NAME"), rated in suns like bosses.
        public static volatile List<SharedBoss> Levels = new();
        static List<SharedBoss> All => Bosses.Concat(Levels).ToList();
        public static SharedBoss LevelFor(string stage) => Levels.FirstOrDefault(l => l.Stage == stage);
        public static volatile bool Busy;
        public static volatile string Status, Error;
        public static DateTime LastRefresh;

        // Login state.
        public static string Login;         // GitHub username when signed in
        public static volatile string DeviceCode, UserCode, VerifyUrl;
        static string token;

        // Your rating per boss, as far as this session knows: +1, -1 or 0.
        public static readonly Dictionary<int, int> MyVotes = new();

        // ---------- suns, difficulty and the leaderboard ----------
        // Like Geometry Dash's stars: the repository owner rates a boss 1-20 suns, stored as an
        // issue label (only the owner and collaborators can set labels, so ratings can't be faked).
        // The sun count decides the difficulty. Beating a rated boss earns its suns; each clear
        // is a comment on the boss's issue.

        public const int MaxSuns = 20;
        public static readonly string[] Difficulties =
            { "Easy", "Normal", "Hard", "Harder", "Insane", "Easy Demon", "Medium Demon", "Hard Demon", "Insane Demon", "Extreme Demon" };
        const string LabelPrefix = "suns: ";
        const string ClearMarker = "<!-- sunloader-clear v1 -->";
        const string RatedOpen = "<!-- sunloader-rated ";

        // 1-2 Easy, 3 Normal, 4-5 Hard, 6-7 Harder, 8-9 Insane, then two suns per demon tier
        // (10-11 Easy Demon ... 16-17 Insane Demon) and 18-20 Extreme Demon.
        public static string DifficultyFor(int suns) => suns switch
        {
            <= 0 => null,
            <= 2 => "Easy",
            3 => "Normal",
            <= 5 => "Hard",
            <= 7 => "Harder",
            <= 9 => "Insane",
            <= 11 => "Easy Demon",
            <= 13 => "Medium Demon",
            <= 15 => "Hard Demon",
            <= 17 => "Insane Demon",
            _ => "Extreme Demon",
        };

        // ---------- bans ----------
        // The owner's list, kept on one issue only they can edit (other players' copies of the
        // title are ignored). Banned players earn no suns, can't share or rate, and leave the
        // leaderboard; they can appeal on Discord.

        const string BansTitle = "[Sun Loader] Banned players";
        const string BansMarker = "<!-- sunloader-bans v1 -->";
        // The Discord invite for appeals. Set Sharing.DiscordInvite to override it.
        const string BuiltInDiscord = "https://discord.gg/GKbcUcyabX";
        public static string DiscordInvite => Plugin.ShareDiscord.Value.Trim() is { Length: > 0 } link ? link : BuiltInDiscord;

        static int bansIssue;
        public static volatile Dictionary<string, string> Bans = new(StringComparer.OrdinalIgnoreCase);
        public static bool IsBanned(string name) => name != null && Bans.ContainsKey(name);
        public static bool IAmBanned => IsBanned(Login);
        public static string MyBanReason => Login != null && Bans.TryGetValue(Login, out var reason) ? reason : null;
        const string BannedMessage = "You're banned from sharing and rating. You can appeal on the Discord.";

        static bool ReadBans(JsonNode issue)
        {
            if ((string)issue["title"] != BansTitle || (string)issue["user"]?["login"] is not { } author
                || !string.Equals(author, Repo.Split('/')[0], StringComparison.OrdinalIgnoreCase)) return false;
            var bans = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in ((string)issue["body"] ?? "").Split('\n'))
            {
                if (!line.StartsWith("- ")) continue;
                var parts = line[2..].Split('|', 2);
                if (parts[0].Trim().Length > 0) bans[parts[0].Trim()] = parts.Length > 1 ? parts[1].Trim() : "";
            }
            Bans = bans;
            bansIssue = (int)issue["number"];
            return true;
        }

        // Owner only: bans a player, or unbans them with reason null.
        public static void SetBan(string player, string reason, Action<string> done)
        {
            if (!IsOwner) { Error = "Only the owner of " + Repo + " can ban players."; return; }
            if (bansIssue == 0) { Error = "The ban list wasn't found. Press Refresh and try again."; return; }
            if (string.Equals(player, Login, StringComparison.OrdinalIgnoreCase)) { Error = "You can't ban yourself."; return; }
            Run((reason == null ? "Unbanning " : "Banning ") + player, async () =>
            {
                var bans = new Dictionary<string, string>(Bans, StringComparer.OrdinalIgnoreCase);
                if (reason == null) bans.Remove(player);
                else bans[player] = reason.Replace("\r", " ").Replace("\n", " ").Trim();
                var lines = bans.OrderBy(kv => kv.Key).Select(kv => $"- {kv.Key} | {kv.Value}");
                var body = "Players banned from Sun Loader's suns, sharing and rating. Appeals go to the Discord server.\n\n"
                    + string.Join("\n", lines) + "\n\n" + BansMarker + "\n";
                await Api(HttpMethod.Patch, $"https://api.github.com/repos/{Repo}/issues/{bansIssue}", new JsonObject { ["body"] = body });
                Bans = bans;
                Bosses = Bosses.Where(b => !IsBanned(b.Author)).ToList();
                RebuildLeaderboard(All, clearsByBoss);
                done?.Invoke(reason == null ? $"Unbanned {player}. Press Refresh to bring their bosses back." : $"Banned {player}");
            });
        }

        public static bool IsOwner => Login != null && string.Equals(Repo.Split('/')[0], Login, StringComparison.OrdinalIgnoreCase);

        public class Player { public string Name; public int Suns, Beaten; }
        public static volatile List<Player> Leaderboard = new();
        public static int MySuns => Leaderboard.FirstOrDefault(p => p.Name == Login)?.Suns ?? 0;
        public static int MyBeaten => Leaderboard.FirstOrDefault(p => p.Name == Login)?.Beaten ?? 0;

        static readonly HttpClient http = MakeClient();
        static string TokenFile => Path.Combine(Plugin.Root, "github-login.dat");

        static HttpClient MakeClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("SunLoader/" + Plugin.Version);
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            return c;
        }

        public static void Init()
        {
            try
            {
                if (!File.Exists(TokenFile)) return;
                token = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(TokenFile)));
                Run("Checking your GitHub login", async () =>
                {
                    var me = await Api(HttpMethod.Get, "https://api.github.com/user");
                    Login = (string)me["login"];
                    RebuildLeaderboard(All, clearsByBoss);
                });
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Sharing: couldn't read the saved login: " + e.Message); token = null; }
        }

        public static bool SignedIn => token != null && Login != null;

        // Runs work in the background, one job at a time, reporting errors to the UI.
        static void Run(string what, Func<Task> work)
        {
            if (Busy) return;
            Busy = true; Status = what + "..."; Error = null;
            Task.Run(async () =>
            {
                try { await work(); Status = null; }
                catch (Exception e)
                {
                    Error = e is HttpRequestException or TaskCanceledException ? "Can't reach GitHub. Check your internet connection." : e.Message;
                    Status = null;
                    Plugin.Logger.LogWarning($"Sharing: {what} failed: {e.Message}");
                }
                finally { Busy = false; }
            });
        }

        // ---------- browsing ----------

        public static void Refresh()
        {
            if (!Configured) { Error = "Sharing isn't set up in this build yet."; return; }
            Run("Loading shared bosses", async () =>
            {
                var list = new List<SharedBoss>();
                for (int page = 1; page <= 10; page++)
                {
                    var items = (await Api(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/issues?state=open&per_page=100&page={page}")).AsArray();
                    foreach (var issue in items)
                    {
                        if (issue["pull_request"] != null || ReadBans(issue)) continue;
                        var b = Parse(issue);
                        if (b != null) list.Add(b);
                    }
                    if (items.Count < 100) break;
                }
                await LoadClears(list);
                Bosses = list.Where(b => !b.IsLevel && !IsBanned(b.Author)).ToList();
                Levels = list.Where(b => b.IsLevel).OrderBy(b => b.Stage).ToList();
                LastRefresh = DateTime.Now;
            });
        }

        static SharedBoss Parse(JsonNode issue)
        {
            var title = (string)issue["title"] ?? "";
            var body = (string)issue["body"] ?? "";
            var labels = issue["labels"]?.AsArray().Select(l => (string)l?["name"]).Where(n => n != null).ToList() ?? new List<string>();
            var suns = labels.Where(n => n.StartsWith(LabelPrefix)).Select(n => int.TryParse(n[LabelPrefix.Length..], out var v) ? v : 0)
                .FirstOrDefault(v => v >= 1 && v <= MaxSuns);
            string Field(string label) => Between(body, $"**{label}:** ", "\n")?.Trim();
            // A game level's page: only trusted from the repository owner.
            if (title.StartsWith(LevelPrefix) && body.Contains(LevelMarker))
            {
                if (!string.Equals((string)issue["user"]?["login"], Repo.Split('/')[0], StringComparison.OrdinalIgnoreCase)) return null;
                return new SharedBoss
                {
                    IsLevel = true,
                    Number = (int)issue["number"],
                    Suns = suns,
                    Labels = labels,
                    Name = title[LevelPrefix.Length..].Trim(),
                    Stage = Field("Stage") ?? title[LevelPrefix.Length..].Trim(),
                    Artist = Field("Artist"),
                    Url = (string)issue["html_url"],
                    Comments = (int?)issue["comments"] ?? 0,
                };
            }
            if (!title.StartsWith(TitlePrefix) || !body.Contains(Marker)) return null;
            var data = Between(body, "```sunloader\n", "\n```")?.Trim();
            if (data == null) return null;
            string currentHash = null;
            try { currentHash = AttacksHash(JsonNode.Parse(Unpack(data))); }
            catch { return null; }   // a damaged post isn't listed
            int.TryParse(Field("Attacks"), out var attacks);
            return new SharedBoss
            {
                Number = (int)issue["number"],
                Suns = suns,
                Labels = labels,
                Name = title[TitlePrefix.Length..].Trim(),
                Author = (string)issue["user"]?["login"],
                Stage = Field("Stage"),
                Music = Field("Song") is { } s && s != "the stage's own" ? s : null,
                Description = body.StartsWith("> ") ? Between(body, "> ", "\n")?.Trim() : null,
                Attacks = attacks,
                Likes = (int?)issue["reactions"]?["+1"] ?? 0,
                Dislikes = (int?)issue["reactions"]?["-1"] ?? 0,
                Comments = (int?)issue["comments"] ?? 0,
                Created = DateTime.TryParse((string)issue["created_at"], out var t) ? t : DateTime.MinValue,
                Url = (string)issue["html_url"],
                Data = data,
                CurrentHash = currentHash,
            };
        }

        static string Between(string s, string start, string end)
        {
            var i = s.IndexOf(start, StringComparison.Ordinal);
            if (i < 0) return null;
            i += start.Length;
            var j = s.IndexOf(end, i, StringComparison.Ordinal);
            return j < 0 ? s[i..] : s[i..j];
        }

        // Saves a shared boss into your bosses folder. Returns the new boss's name.
        public static string Download(SharedBoss b)
        {
            var file = JsonNode.Parse(Unpack(b.Data)).AsObject();
            var stage = (string)file["stage"];
            if (stage == null || file["layers"] == null) throw new Exception("This boss file is damaged");
            var name = CustomBosses.CleanName(b.Name);
            if (name.Length == 0) name = "Shared boss";
            var baseName = name;
            for (int i = 2; CustomBosses.Exists(name) && CustomBosses.SharedNumber(name) != b.Number; i++) name = $"{baseName} ({i})";
            file["name"] = name;
            // A song is only a file name in your music folder; never a path somewhere else.
            if (file["music"] != null) file["music"] = Path.GetFileName((string)file["music"]);
            file["shared"] = new JsonObject { ["repo"] = Repo, ["issue"] = b.Number, ["author"] = b.Author, ["hash"] = AttacksHash(file) };
            Directory.CreateDirectory(CustomBosses.Dir);
            File.WriteAllText(Path.Combine(CustomBosses.Dir, name + ".json"), file.ToJsonString(Plugin.Pretty));
            Plugin.Logger.LogInfo($"Sharing: downloaded '{b.Name}' by {b.Author} as '{name}'");
            return name;
        }

        // ---------- uploading ----------

        public static void Upload(BossProject boss, string description, Action<string> done)
        {
            if (IAmBanned) { Error = BannedMessage; return; }
            if (!SignedIn) { Error = "Sign in with GitHub first."; return; }
            Run("Uploading " + boss.Name, async () =>
            {
                var file = JsonNode.Parse(File.ReadAllText(boss.Path)).AsObject();
                var existing = (int?)file["shared"]?["issue"];
                var mine = existing != null && (string)file["shared"]?["author"] == Login && (string)file["shared"]?["repo"] == Repo;
                file.Remove("shared");
                var body = Body(boss, description, Pack(file.ToJsonString()));
                if (body.Length > MaxBody) throw new Exception("This boss is too big to share (too many attacks).");
                var payload = new JsonObject { ["title"] = TitlePrefix + boss.Name, ["body"] = body };
                JsonNode issue = mine
                    ? await Api(HttpMethod.Patch, $"https://api.github.com/repos/{Repo}/issues/{existing}", payload)
                    : await Api(HttpMethod.Post, $"https://api.github.com/repos/{Repo}/issues", payload);
                var number = (int)issue["number"];
                file = JsonNode.Parse(File.ReadAllText(boss.Path)).AsObject();
                file["shared"] = new JsonObject { ["repo"] = Repo, ["issue"] = number, ["author"] = Login, ["hash"] = AttacksHash(file) };
                File.WriteAllText(boss.Path, file.ToJsonString(Plugin.Pretty));
                Plugin.Logger.LogInfo($"Sharing: {(mine ? "updated" : "uploaded")} '{boss.Name}' as {Repo}#{number}");
                done?.Invoke(mine ? $"Updated '{boss.Name}' online" : $"Shared '{boss.Name}'! Others can find it in the Online tab.");
            });
        }

        static string Body(BossProject boss, string description, string data)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(description)) sb.Append("> ").Append(description.Replace("\n", " ").Replace("`", "'").Trim()).Append("\n\n");
            sb.Append("**Stage:** ").Append(boss.Stage).Append('\n');
            sb.Append("**Attacks:** ").Append(boss.Attacks).Append('\n');
            sb.Append("**Song:** ").Append(boss.Music ?? "the stage's own").Append('\n');
            sb.Append("\nA custom boss for Just Shapes & Beats. Download and play it with ")
              .Append("[Sun Loader](https://github.com/Gooberines/SunLoader): Bosses menu → Online.\n")
              .Append("Rate it with 👍 / 👎 on this post or in the game.\n\n");
            sb.Append(Marker).Append("\n```sunloader\n").Append(data).Append("\n```\n");
            return sb.ToString();
        }

        // Takes your upload down (closes its issue). Only works on your own uploads.
        public static void Remove(SharedBoss b, Action<string> done)
        {
            Run("Removing " + b.Name, async () =>
            {
                await Api(HttpMethod.Patch, $"https://api.github.com/repos/{Repo}/issues/{b.Number}", new JsonObject { ["state"] = "closed" });
                Bosses = Bosses.Where(x => x.Number != b.Number).ToList();
                done?.Invoke($"Removed '{b.Name}' from the online list");
            });
        }

        // ---------- rating ----------

        public static void Vote(SharedBoss b, int vote)
        {
            if (IAmBanned) { Error = BannedMessage; return; }
            if (!SignedIn) { Error = "Sign in with GitHub to rate bosses."; return; }
            Run("Rating " + b.Name, async () =>
            {
                var content = vote > 0 ? "+1" : "-1";
                var other = vote > 0 ? "-1" : "+1";
                // Remove your opposite rating first, and an identical one if you're taking it back.
                var reactions = (await Api(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/issues/{b.Number}/reactions?per_page=100")).AsArray();
                var mineSame = reactions.FirstOrDefault(r => (string)r["user"]?["login"] == Login && (string)r["content"] == content);
                foreach (var r in reactions.Where(r => (string)r["user"]?["login"] == Login && (string)r["content"] == other).ToList())
                {
                    await Api(HttpMethod.Delete, $"https://api.github.com/repos/{Repo}/issues/{b.Number}/reactions/{(long)r["id"]}");
                    if (vote > 0) b.Dislikes--; else b.Likes--;
                }
                if (mineSame != null)
                {
                    await Api(HttpMethod.Delete, $"https://api.github.com/repos/{Repo}/issues/{b.Number}/reactions/{(long)mineSame["id"]}");
                    if (vote > 0) b.Likes--; else b.Dislikes--;
                    MyVotes[b.Number] = 0;
                    return;
                }
                await Api(HttpMethod.Post, $"https://api.github.com/repos/{Repo}/issues/{b.Number}/reactions", new JsonObject { ["content"] = content });
                if (vote > 0) b.Likes++; else b.Dislikes++;
                MyVotes[b.Number] = vote;
            });
        }

        // A fingerprint of a boss file's attacks, to tell whether a downloaded boss was changed.
        public static string AttacksHash(JsonNode file) => Hash(file["layers"]?.ToJsonString() ?? "");

        // Owner only: rates a boss 1-20 suns (0 takes the rating away). Other labels stay.
        public static void SetSuns(SharedBoss b, int suns, Action<string> done)
        {
            if (!IsOwner) { Error = "Only the owner of " + Repo + " can rate bosses."; return; }
            Run("Rating " + b.Name, async () =>
            {
                var labels = b.Labels.Where(l => !l.StartsWith(LabelPrefix) && !l.StartsWith("difficulty: ")).ToList();
                if (suns > 0) labels.Add(LabelPrefix + suns);
                var arr = new JsonArray();
                foreach (var l in labels) arr.Add(l);
                await Api(HttpMethod.Put, $"https://api.github.com/repos/{Repo}/issues/{b.Number}/labels", new JsonObject { ["labels"] = arr });
                // Records what was rated, so changing the boss later pauses its suns.
                if (suns > 0 && !b.IsLevel && b.CurrentHash != null)
                {
                    await Api(HttpMethod.Post, $"https://api.github.com/repos/{Repo}/issues/{b.Number}/comments",
                        new JsonObject { ["body"] = $"Rated {suns} suns ({DifficultyFor(suns)}).\n\n{RatedOpen}{b.CurrentHash} -->" });
                    b.RatedHash = b.CurrentHash;
                }
                b.Labels = labels;
                b.Suns = suns;
                RebuildLeaderboard(All, clearsByBoss);
                done?.Invoke(suns == 0 ? $"'{b.Name}' is unrated now" : $"Rated '{b.Name}' {suns} suns ({DifficultyFor(suns)})");
            });
        }

        // Who beat which boss, from every clear comment in the repository (one listing for all).
        static Dictionary<int, HashSet<string>> clearsByBoss = new();

        static async Task LoadClears(List<SharedBoss> bosses)
        {
            var byBoss = new Dictionary<int, HashSet<string>>();
            var numbers = bosses.ToDictionary(b => b.Number);
            for (int page = 1; page <= 30; page++)
            {
                var items = (await Api(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/issues/comments?per_page=100&page={page}")).AsArray();
                foreach (var c in items)
                {
                    var body = (string)c["body"] ?? "";
                    var user = (string)c["user"]?["login"];
                    var issueUrl = (string)c["issue_url"] ?? "";
                    if (user == null || !int.TryParse(issueUrl[(issueUrl.LastIndexOf('/') + 1)..], out var number)) continue;
                    numbers.TryGetValue(number, out var post);
                    // The owner's record of what they rated; the newest one counts.
                    if (body.Contains(RatedOpen) && post != null && string.Equals(user, Repo.Split('/')[0], StringComparison.OrdinalIgnoreCase))
                        post.RatedHash = Between(body, RatedOpen, " -->")?.Trim();
                    if (!body.Contains(ClearMarker)) continue;
                    // A clear edited after posting, or of a different version of the boss, doesn't count.
                    if ((string)c["updated_at"] != (string)c["created_at"]) continue;
                    if (post != null && !post.IsLevel && Between(body, "Attacks: ", "\n")?.Trim() is { } clearHash && clearHash != post.CurrentHash) continue;
                    if (!byBoss.TryGetValue(number, out var set)) byBoss[number] = set = new(StringComparer.OrdinalIgnoreCase);
                    set.Add(user);
                }
                if (items.Count < 100) break;
            }
            clearsByBoss = byBoss;
            RebuildLeaderboard(bosses, byBoss);
        }

        static void RebuildLeaderboard(IEnumerable<SharedBoss> bosses, Dictionary<int, HashSet<string>> byBoss)
        {
            var players = new Dictionary<string, Player>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in bosses)
            {
                byBoss.TryGetValue(b.Number, out var who);
                var fair = who?.Where(n => !IsBanned(n)).ToList();
                b.Clears = fair?.Count ?? 0;
                b.ClearedByMe = Login != null && who != null && who.Contains(Login);
                if (fair == null || b.EarnableSuns == 0) continue;
                foreach (var name in fair)
                {
                    if (!players.TryGetValue(name, out var p)) players[name] = p = new Player { Name = name };
                    p.Suns += b.EarnableSuns;
                    p.Beaten++;
                }
            }
            Leaderboard = players.Values.OrderByDescending(p => p.Suns).ThenByDescending(p => p.Beaten).ThenBy(p => p.Name).ToList();
        }

        // ---------- clears ----------
        // A clear waits in clears.json until it's posted (you may be offline or signed out).

        static string ClearsFile => Path.Combine(Plugin.Root, "clears.json");

        public static void AddClear(int issue, string evidence)
        {
            var (pending, posted) = ReadClears();
            if (posted.Contains(issue) || pending.Contains(issue)) return;
            pending.Add(issue);
            SetDetail(issue.ToString(), evidence);
            WriteClears(pending, posted);
            nextFlush = 0;
        }

        // A game level beaten: recorded on its page, or kept by name until the level list is loaded.
        public static void AddLevelClear(string stage, string evidence)
        {
            if (LevelFor(stage) is { } level) { AddClear(level.Number, evidence); return; }
            SetDetail("stage:" + stage, evidence);
            var waiting = WaitingLevels();
            if (!waiting.Contains(stage)) { waiting.Add(stage); WriteWaitingLevels(waiting); }
        }

        // Each waiting clear's run details, posted with it for the owner to review.
        static string DetailsFile => Path.Combine(Plugin.Root, "clears-details.json");

        static JsonObject Details()
        {
            try { return File.Exists(DetailsFile) ? JsonNode.Parse(File.ReadAllText(DetailsFile)).AsObject() : new JsonObject(); }
            catch { return new JsonObject(); }
        }

        static void SetDetail(string key, string evidence)
        {
            var details = Details();
            details[key] = evidence;
            Directory.CreateDirectory(Plugin.Root);
            File.WriteAllText(DetailsFile, details.ToJsonString());
        }

        static string WaitingFile => Path.Combine(Plugin.Root, "clears-levels.json");

        static List<string> WaitingLevels()
        {
            try { return File.Exists(WaitingFile) ? JsonNode.Parse(File.ReadAllText(WaitingFile)).AsArray().Select(n => (string)n).ToList() : new(); }
            catch { return new(); }
        }

        static void WriteWaitingLevels(List<string> stages)
        {
            Directory.CreateDirectory(Plugin.Root);
            File.WriteAllText(WaitingFile, new JsonArray(stages.Select(n => (JsonNode)n).ToArray()).ToJsonString());
        }

        static (List<int> pending, List<int> posted) ReadClears()
        {
            try
            {
                if (File.Exists(ClearsFile))
                {
                    var j = JsonNode.Parse(File.ReadAllText(ClearsFile));
                    return (j["pending"]?.AsArray().Select(n => (int)n).ToList() ?? new(), j["posted"]?.AsArray().Select(n => (int)n).ToList() ?? new());
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("Sharing: couldn't read clears.json: " + e.Message); }
            return (new(), new());
        }

        static void WriteClears(List<int> pending, List<int> posted)
        {
            var j = new JsonObject
            {
                ["pending"] = new JsonArray(pending.Select(n => (JsonNode)n).ToArray()),
                ["posted"] = new JsonArray(posted.Select(n => (JsonNode)n).ToArray()),
            };
            Directory.CreateDirectory(Plugin.Root);
            File.WriteAllText(ClearsFile, j.ToJsonString(Plugin.Pretty));
        }

        static float nextFlush;
        static bool loadedOnce;

        // Called every frame: loads the online lists once (so game-level clears know their page),
        // then posts waiting clears when signed in and not busy.
        public static void Update()
        {
            if (Busy || !Configured) return;
            if (!loadedOnce && UnityEngine.Time.realtimeSinceStartup > 8) { loadedOnce = true; Refresh(); return; }
            if (Levels.Count > 0 && WaitingLevels() is { Count: > 0 } waiting)
            {
                WriteWaitingLevels(new());
                foreach (var stage in waiting) if (LevelFor(stage) is { } level) AddClear(level.Number, (string)Details()["stage:" + stage]);
            }
            if (!SignedIn || IAmBanned || UnityEngine.Time.realtimeSinceStartup < nextFlush) return;
            nextFlush = UnityEngine.Time.realtimeSinceStartup + 30;
            var (pending, _) = ReadClears();
            if (pending.Count == 0) return;
            Run("Saving your clears", async () =>
            {
                foreach (var issue in pending)
                {
                    if (!(clearsByBoss.TryGetValue(issue, out var who) && who.Contains(Login)))
                        await Api(HttpMethod.Post, $"https://api.github.com/repos/{Repo}/issues/{issue}/comments",
                            new JsonObject { ["body"] = $"Beat it in Sun Loader! \U0001F389\n\n{(string)Details()[issue.ToString()]}\n\n{ClearMarker}" });
                    var (p, posted) = ReadClears();
                    p.Remove(issue);
                    if (!posted.Contains(issue)) posted.Add(issue);
                    WriteClears(p, posted);
                    if (!clearsByBoss.TryGetValue(issue, out who)) clearsByBoss[issue] = who = new(StringComparer.OrdinalIgnoreCase);
                    who.Add(Login);
                    Plugin.Logger.LogInfo($"Sharing: posted your clear of #{issue}");
                }
                RebuildLeaderboard(All, clearsByBoss);
            });
        }

        // ---------- GitHub login (device flow) ----------

        public static void StartLogin()
        {
            if (!Configured) { Error = "Sharing isn't set up in this build yet."; return; }
            Run("Asking GitHub for a login code", async () =>
            {
                var res = await Form("https://github.com/login/device/code", new() { ["client_id"] = ClientId });
                DeviceCode = (string)res["device_code"];
                UserCode = (string)res["user_code"];
                VerifyUrl = (string)res["verification_uri"] ?? "https://github.com/login/device";
                var interval = Math.Max(5, (int?)res["interval"] ?? 5);
                var expires = DateTime.Now.AddSeconds((int?)res["expires_in"] ?? 900);
                _ = Task.Run(() => PollLogin(DeviceCode, interval, expires));
            });
        }

        public static void CancelLogin() { DeviceCode = UserCode = null; }

        static async Task PollLogin(string device, int interval, DateTime expires)
        {
            while (DeviceCode == device && DateTime.Now < expires)
            {
                await Task.Delay(interval * 1000);
                if (DeviceCode != device) return;
                try
                {
                    var res = await Form("https://github.com/login/oauth/access_token", new()
                    {
                        ["client_id"] = ClientId,
                        ["device_code"] = device,
                        ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    });
                    var err = (string)res["error"];
                    if (err == "authorization_pending") continue;
                    if (err == "slow_down") { interval += 5; continue; }
                    if (err != null) { Error = err == "access_denied" ? "Login was cancelled on GitHub." : "GitHub login failed: " + err; break; }
                    token = (string)res["access_token"];
                    var me = await Api(HttpMethod.Get, "https://api.github.com/user");
                    Login = (string)me["login"];
                    RebuildLeaderboard(All, clearsByBoss);
                    Directory.CreateDirectory(Plugin.Root);
                    File.WriteAllBytes(TokenFile, Dpapi.Protect(Encoding.UTF8.GetBytes(token)));
                    Status = null; Error = null;
                    Plugin.Logger.LogInfo("Sharing: signed in to GitHub as " + Login);
                    break;
                }
                catch (Exception e) { Plugin.Logger.LogWarning("Sharing: login check failed: " + e.Message); }
            }
            if (DeviceCode == device) { if (Login == null && Error == null) Error = "The login code expired. Try again."; DeviceCode = UserCode = null; }
        }

        public static void Logout()
        {
            token = null; Login = null; MyVotes.Clear();
            try { File.Delete(TokenFile); } catch { }
        }

        // ---------- HTTP ----------

        static async Task<JsonNode> Api(HttpMethod method, string url, JsonNode body = null)
        {
            using var req = new HttpRequestMessage(method, url);
            if (token != null) req.Headers.Authorization = new("Bearer", token);
            if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode) return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text);
            string msg = null;
            try { msg = (string)JsonNode.Parse(text)?["message"]; } catch { }
            if ((int)res.StatusCode == 401) { Logout(); throw new Exception("Your GitHub login expired. Sign in again."); }
            if ((int)res.StatusCode == 403 && msg != null && msg.Contains("rate limit")) throw new Exception("GitHub's limit for guests was reached. Sign in, or wait an hour.");
            if ((int)res.StatusCode == 404) throw new Exception("The bosses repository wasn't found (" + Repo + ").");
            throw new Exception($"GitHub said {(int)res.StatusCode}: {msg ?? res.ReasonPhrase}");
        }

        static async Task<JsonNode> Form(string url, Dictionary<string, string> fields)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(fields) };
            req.Headers.Accept.Clear();
            req.Headers.Accept.ParseAdd("application/json");
            using var res = await http.SendAsync(req);
            return JsonNode.Parse(await res.Content.ReadAsStringAsync());
        }

        // ---------- packing ----------

        // Boss JSON -> gzip -> base64, wrapped at 76 characters. Shared online and through lobbies.
        public static string Pack(string json)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal)) { var b = Encoding.UTF8.GetBytes(json); gz.Write(b, 0, b.Length); }
            return Convert.ToBase64String(ms.ToArray(), Base64FormattingOptions.InsertLineBreaks).Replace("\r", "");
        }

        public static string Unpack(string data)
        {
            var bytes = Convert.FromBase64String(data.Replace("\n", "").Replace("\r", "").Trim());
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            // Cap the unpacked size, so a hostile file can't fill memory.
            var buf = new byte[81920]; int n; long total = 0;
            while ((n = gz.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > 8 * 1024 * 1024) throw new Exception("This boss file is too big");
                outMs.Write(buf, 0, n);
            }
            return Encoding.UTF8.GetString(outMs.ToArray());
        }

        public static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..16];
    }

    // Windows' per-user encryption (DPAPI), so the saved GitHub login only works for you on this PC.
    static class Dpapi
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Blob { public int Size; public IntPtr Data; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptProtectData(ref Blob input, string desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr mem);

        public static byte[] Protect(byte[] data) => Call(data, true);
        public static byte[] Unprotect(byte[] data) => Call(data, false);

        static byte[] Call(byte[] data, bool protect)
        {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
                var ok = protect
                    ? CryptProtectData(ref input, "Sun Loader GitHub login", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                    : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
                if (!ok) throw new Exception("Windows couldn't " + (protect ? "encrypt" : "decrypt") + " the login (error " + Marshal.GetLastWin32Error() + ")");
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                LocalFree(output.Data);
                return result;
            }
            finally { handle.Free(); }
        }
    }
}
