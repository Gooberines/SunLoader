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
        const string Marker = "<!-- sunloader-boss v1 -->";
        const int MaxBody = 60000;      // GitHub allows 65536 characters

        public static string Repo => Plugin.ShareRepo.Value.Trim();
        // The Sun Loader GitHub App's client ID. It's public (not a secret): it only names the app.
        const string BuiltInClientId = "";
        public static string ClientId => Plugin.ShareClientId.Value.Trim() is { Length: > 0 } id ? id : BuiltInClientId;
        public static bool Configured => Repo.Contains('/') && ClientId.Length > 0;

        public static volatile List<SharedBoss> Bosses = new();
        public static volatile bool Busy;
        public static volatile string Status, Error;
        public static DateTime LastRefresh;

        // Login state.
        public static string Login;         // GitHub username when signed in
        public static volatile string DeviceCode, UserCode, VerifyUrl;
        static string token;

        // Your rating per boss, as far as this session knows: +1, -1 or 0.
        public static readonly Dictionary<int, int> MyVotes = new();

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
                        if (issue["pull_request"] != null) continue;
                        var b = Parse(issue);
                        if (b != null) list.Add(b);
                    }
                    if (items.Count < 100) break;
                }
                Bosses = list;
                LastRefresh = DateTime.Now;
            });
        }

        static SharedBoss Parse(JsonNode issue)
        {
            var title = (string)issue["title"] ?? "";
            var body = (string)issue["body"] ?? "";
            if (!title.StartsWith(TitlePrefix) || !body.Contains(Marker)) return null;
            var data = Between(body, "```sunloader\n", "\n```")?.Trim();
            if (data == null) return null;
            string Field(string label) => Between(body, $"**{label}:** ", "\n")?.Trim();
            int.TryParse(Field("Attacks"), out var attacks);
            return new SharedBoss
            {
                Number = (int)issue["number"],
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
            file["shared"] = new JsonObject { ["repo"] = Repo, ["issue"] = b.Number, ["author"] = b.Author };
            Directory.CreateDirectory(CustomBosses.Dir);
            File.WriteAllText(Path.Combine(CustomBosses.Dir, name + ".json"), file.ToJsonString(Plugin.Pretty));
            Plugin.Logger.LogInfo($"Sharing: downloaded '{b.Name}' by {b.Author} as '{name}'");
            return name;
        }

        // ---------- uploading ----------

        public static void Upload(BossProject boss, string description, Action<string> done)
        {
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
                file["shared"] = new JsonObject { ["repo"] = Repo, ["issue"] = number, ["author"] = Login };
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
