using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace SunLoaderInstaller
{
    // Optional: gets the newest Sun Loader DLLs from the project's GitHub Releases, so an old
    // download still installs the latest version. It only talks to api.github.com and the release
    // download links, only uses a release newer than the files in this download, and checks each
    // DLL before using it. Anything going wrong means the files in this download are used.
    static class Updater
    {
        const string Repo = "Gooberines/SunLoader";
        static readonly string[] Wanted = { "SunLoader.dll", "NoIntro.dll", "ExampleMod.dll" };

        // The version in this download (files\version.txt, written by build.ps1), e.g. "0.1.0-alpha".
        public static string BundledVersion
        {
            get
            {
                try { return File.ReadAllText(Path.Combine(Installer.FilesDir, "version.txt")).Trim(); }
                catch { return "0.0.0"; }
            }
        }

        // Downloads the newest release's DLLs to a temp folder. Returns file name -> path for the
        // ones it got (empty when this download is already the newest, or on any problem).
        public static Dictionary<string, string> Fetch(Action<string> log)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                log("Checking GitHub for a newer Sun Loader...");
                var json = Get($"https://api.github.com/repos/{Repo}/releases?per_page=10");
                var releases = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(json);
                // Newest first; drafts aren't visible without a login anyway.
                var latest = releases.FirstOrDefault(r => !(r.TryGetValue("draft", out var d) && d is bool b && b));
                if (latest == null) { log("No releases on GitHub yet; using the version in this download."); return result; }
                var tag = latest["tag_name"] as string ?? "";
                if (Compare(tag, BundledVersion) <= 0)
                {
                    log($"This download ({BundledVersion}) is the newest version.");
                    return result;
                }
                log($"Found {tag} (this download is {BundledVersion}). Downloading...");

                var dir = Path.Combine(Path.GetTempPath(), "SunLoaderUpdate", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                // The serializer gives nested arrays as an ArrayList, so don't assume an array type.
                var assets = latest.TryGetValue("assets", out var a) && a is System.Collections.IEnumerable list ? list.OfType<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
                foreach (var asset in assets)
                {
                    var name = asset["name"] as string;
                    var url = asset["browser_download_url"] as string;
                    if (name == null || url == null || !Wanted.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    // Only this project's own release files.
                    if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = Path.Combine(dir, name);
                    using (var client = Client()) client.DownloadFile(url, path);
                    if (!LooksRight(path, Path.GetFileNameWithoutExtension(name))) { log($"  {name} didn't pass the checks; skipped."); continue; }
                    result[name] = path;
                    log($"  Got {name} ({new FileInfo(path).Length / 1024} KB).");
                }
                if (result.Count == 0) log("That release has no mod files attached; using the version in this download.");
            }
            catch (Exception e)
            {
                log("Couldn't get the newest version (" + e.Message + "); using the version in this download.");
                result.Clear();
            }
            return result;
        }

        static string Get(string url)
        {
            using (var client = Client()) return client.DownloadString(url);
        }

        static WebClient Client()
        {
            var c = new WebClient();
            c.Headers[HttpRequestHeader.UserAgent] = "SunLoaderInstaller";
            c.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return c;
        }

        // A real .NET DLL with the expected name, of a sensible size.
        static bool LooksRight(string path, string assemblyName)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Length < 1024 || info.Length > 20 * 1024 * 1024) return false;
                using (var s = File.OpenRead(path)) if (s.ReadByte() != 'M' || s.ReadByte() != 'Z') return false;
                return string.Equals(AssemblyName.GetAssemblyName(path).Name, assemblyName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // Compares "v0.2.0-alpha" with "0.1.0-alpha" by their numbers.
        static int Compare(string a, string b) => Parse(a).CompareTo(Parse(b));

        static Version Parse(string v)
        {
            var m = Regex.Match(v ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
            return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) : new Version(0, 0, 0);
        }
    }
}
