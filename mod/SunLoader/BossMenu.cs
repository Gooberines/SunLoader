using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SunLoader
{
    // The Bosses menu: make, play, edit and manage your own bosses without going through a
    // playlist; share them and rate other players' bosses (Online tab); and pick the boss an
    // online lobby plays. Opened by the BOSSES button on the main menu or its key (F4).
    static class BossMenu
    {
        public static bool Open;
        enum Screen { List, New, Online }
        static Screen screen;
        static List<BossProject> bosses = new();
        static float scroll, stageScroll, onlineScroll;
        static string message;
        static bool messageIsError;
        static float messageUntil;

        // The one text field being typed in: "new", "rename", "desc" or "search".
        static string focus, focusText = "";
        static BossProject renaming, deleting, uploading;
        static SharedBoss removing;

        // New boss form.
        static string newName = "", newStage;
        static bool newWithOriginal;

        // Online tab.
        static bool sortNew;
        static string search = "", uploadDesc = "";

        static readonly Color Bg = new(0.02f, 0.02f, 0.03f, 0.97f), Row = new(0.09f, 0.09f, 0.12f, 1f), RowHover = new(0.14f, 0.1f, 0.13f);
        static readonly Color Pink = new(1f, 0.17f, 0.43f), Green = new(0.3f, 0.9f, 0.5f), Orange = new(1f, 0.6f, 0.2f), Dim = new(0.6f, 0.6f, 0.7f), Cyan = new(0.17f, 0.91f, 1f), Yellow = new(1f, 0.82f, 0.17f);
        static readonly Color GreenBtn = new(0.15f, 0.45f, 0.25f), BlueBtn = new(0.15f, 0.3f, 0.45f), RedBtn = new(0.45f, 0.12f, 0.18f);

        public static void Toggle()
        {
            Open = !Open;
            if (!Open) { focus = null; return; }
            ModMenu.Open = false;
            ShowList();
        }

        static void ShowList()
        {
            screen = Screen.List;
            focus = null; renaming = deleting = uploading = null; removing = null;
            Refresh();
        }

        static void ShowOnline()
        {
            screen = Screen.Online;
            focus = null; renaming = deleting = uploading = null; removing = null;
            onlineScroll = 0;
            if (Share.Configured && !Share.Busy && (DateTime.Now - Share.LastRefresh).TotalMinutes > 2) Share.Refresh();
        }

        static void Refresh()
        {
            try { bosses = CustomBosses.List(); }
            catch (Exception e) { bosses = new(); Say("Couldn't read the bosses folder: " + e.Message, true); }
        }

        static void Say(string msg, bool error = false) { message = msg; messageIsError = error; messageUntil = Time.realtimeSinceStartup + 7; }

        // Play/Edit start a level, which only works from a menu, not in the middle of one.
        static bool CanLaunch => MainMenuSeen.OnMainMenu || PlaylistMenuSeen.OnPlaylistMenu;
        static bool Hosting => LobbySync.InLobby && LobbySync.IsHost;

        public static void OnGUI(float scale, float vw)
        {
            var e = Event.current;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));

            if (!Open && !ModMenu.Open && MainMenuSeen.OnMainMenu) MainMenuButton(e);
            if (!Open) return;
            if (e.type == EventType.KeyDown && Keys(e)) return;

            // With a popup up, the menu behind it is only painted, so clicks can't reach it.
            var modal = Share.UserCode != null || uploading != null;
            if (!modal || e.type == EventType.Repaint) DrawMenu(vw);
            if (Share.UserCode != null) DrawLogin(vw);
            else if (uploading != null) DrawUpload(vw);
        }

        static void DrawMenu(float vw)
        {
            EditorGui.Fill(new Rect(0, 0, vw, 1080), new Color(0, 0, 0, 0.75f));
            var r = new Rect(vw / 2 - 620, 50, 1240, 980);
            EditorGui.Fill(r, Bg);
            Theme.Stripes(new Rect(r.x, r.y, r.width, 96), new Color(Pink.r, Pink.g, Pink.b, 0.1f));
            EditorGui.Fill(new Rect(r.x, r.y + 96, r.width, 4), Pink);
            EditorGui.Fill(new Rect(r.x, r.yMax - 4, r.width, 4), Pink);
            float x = r.x + 30, y = r.y + 20, w = r.width - 60;
            Theme.Title(new Rect(x, y - 2, 560, 60), "SUN LOADER", Pink, 46);
            EditorGui.Text(new Rect(x + 4, y + 54, 400, 24), screen == Screen.New ? "BOSSES  ›  NEW BOSS" : "BOSSES", Color.white, 14);

            // Tabs and Close.
            if (screen != Screen.New)
            {
                if (EditorGui.Button(new Rect(r.xMax - 560, y + 12, 170, 40), "My bosses", on: screen == Screen.List)) ShowList();
                if (EditorGui.Button(new Rect(r.xMax - 380, y + 12, 170, 40), "Online", on: screen == Screen.Online)) ShowOnline();
            }
            if (EditorGui.Button(new Rect(r.xMax - 180, y + 12, 150, 40), $"Close ({Plugin.BossMenuKey.Value})")) { Toggle(); return; }
            y += 96;

            if (Time.realtimeSinceStartup < messageUntil) EditorGui.Text(new Rect(x, y, w, 26), message, messageIsError ? Orange : Green);
            else if (Share.Error != null && screen == Screen.Online) EditorGui.Text(new Rect(x, y, w, 26), Share.Error, Orange);
            else if (Share.Status != null) EditorGui.Text(new Rect(x, y, w, 26), Share.Status, Cyan);
            y += 30;

            if (screen != Screen.New && LobbySync.InLobby) y = LobbyBar(x, y, w);

            if (screen == Screen.New) DrawNew(r, x, y, w);
            else if (screen == Screen.Online) DrawOnline(r, x, y, w);
            else DrawList(r, x, y, w);
        }

        // ---------- main menu button ----------

        static void MainMenuButton(Event e)
        {
            // Next to the MODS button, same style: a slanted block that kicks on the beat.
            var b = new Rect(282, 950, 240, 70);
            var hover = b.Contains(e.mousePosition);
            var kick = Theme.Pulse * 6;
            var block = new Rect(b.x - kick / 2, b.y - kick / 2, b.width + kick, b.height + kick);
            Theme.Slanted(new Rect(block.x + 6, block.y + 6, block.width, block.height), new Color(0, 0, 0, 0.5f), 0.3f);
            Theme.Slanted(block, hover ? Color.white : Theme.Cyan, 0.3f);
            Theme.Title(new Rect(b.x + 26, b.y + 6, 210, 44), "BOSSES", hover ? Theme.Pink : new Color(0.02f, 0.02f, 0.03f), 32);
            EditorGui.Text(new Rect(b.x + 28, b.y + 44, 210, 22), $"MAKE YOUR OWN · {Plugin.BossMenuKey.Value}", hover ? Theme.PinkDark : new Color(0, 0, 0, 0.75f), 12);
            if (e.type == EventType.MouseDown && e.button == 0 && hover) { e.Use(); Toggle(); }
        }

        // ---------- online lobby ----------

        static float LobbyBar(float x, float y, float w)
        {
            var bar = new Rect(x, y, w, 50);
            Theme.Slanted(bar, new Color(0.05f, 0.2f, 0.25f), 0.05f);
            EditorGui.Fill(new Rect(bar.x, bar.y, 6, bar.height), Cyan);
            Theme.Title(new Rect(x + 20, y + 6, 220, 36), "ONLINE LOBBY", Cyan, 20);
            var boss = LobbySync.Boss;
            string text = LobbySync.IsHost
                ? boss != null ? $"Lobby boss: '{boss.Name}'. It plays when {boss.Stage} comes up. Everyone needs Sun Loader to see it."
                               : "You're the host. Press 'Use in lobby' on one of your bosses to play it with everyone."
                : boss != null ? $"The host picked '{boss.Name}'. It plays when {boss.Stage} comes up."
                               : "The host hasn't picked a custom boss.";
            EditorGui.Text(new Rect(x + 210, y + 13, w - 400, 26), text, Color.white, 14);
            if (LobbySync.IsHost && boss != null && EditorGui.Button(new Rect(bar.xMax - 170, y + 8, 160, 34), "Stop", "Go back to the stages' normal attacks for everyone"))
            {
                LobbySync.Stop();
                Say("The lobby plays the normal levels again");
            }
            return y + 62;
        }

        // ---------- my bosses ----------

        static void DrawList(Rect r, float x, float y, float w)
        {
            var e = Event.current;
            if (EditorGui.Button(new Rect(x, y, 220, 44), "+ New boss", color: Pink)) { StartNew(); return; }
            EditorGui.Text(new Rect(x + 240, y + 4, w - 240, 40),
                "Your own bosses, separate from the game's levels. Each one plays on a stage (a level's scene) with any song you pick. Find more in the Online tab.", Dim, 13, wrap: true);
            y += 60;

            var listRect = new Rect(x, y, w, r.yMax - y - 76);
            if (e.type == EventType.ScrollWheel && listRect.Contains(e.mousePosition)) { scroll = Mathf.Max(0, scroll + e.delta.y * 20); e.Use(); }
            float ly = listRect.y - scroll;

            if (bosses.Count == 0)
            {
                Theme.Title(new Rect(x, ly + 60, w, 50), "NO BOSSES YET", Color.white, 30);
                EditorGui.Text(new Rect(x, ly + 112, w, 26), "Press NEW BOSS, pick a stage, and the editor opens on it. Or download one from the Online tab.", Dim, 15);
            }
            foreach (var b in bosses)
            {
                const float h = 92;
                if (ly >= listRect.y - 2 && ly + h <= listRect.yMax + 2) BossRow(b, new Rect(x, ly, w, h - 8));
                ly += h;
            }
            scroll = Mathf.Min(scroll, Mathf.Max(0, bosses.Count * 92 - listRect.height));

            // Footer.
            var fy = r.yMax - 56;
            if (EditorGui.Button(new Rect(x, fy, 200, 36), "Open bosses folder")) OpenFolder(CustomBosses.Dir);
            if (EditorGui.Button(new Rect(x + 210, fy, 200, 36), "Open music folder")) OpenFolder(Music.Dir);
            if (EditorGui.Button(new Rect(x + 420, fy, 120, 36), "Refresh")) { Refresh(); Say("List refreshed"); }
            EditorGui.Text(new Rect(x + 560, fy + 8, w - 560, 26),
                CanLaunch ? "In a level: F1 opens the editor. Leaving the level ends the boss." : "Go back to the main menu to play or edit a boss.", CanLaunch ? Dim : Orange, 12);
        }

        static void BossRow(BossProject b, Rect row)
        {
            var hover = row.Contains(Event.current.mousePosition);
            Theme.Slanted(row, hover ? RowHover : Row, 0.06f);
            var isLobbyBoss = LobbySync.Boss != null && LobbySync.IsHost && LobbySync.Boss.Name == b.Name;
            EditorGui.Fill(new Rect(row.x, row.y, 6, row.height), b.Error != null ? Orange : isLobbyBoss ? Cyan : Pink);

            float tx = row.x + 22, tw = row.width - 600;
            if (renaming == b)
            {
                Field(new Rect(tx, row.y + 8, tw, 34), "rename", b.Name);
                EditorGui.Text(new Rect(tx, row.y + 48, tw, 22), "Type a new name, Enter to save, Esc to cancel", Cyan, 12);
            }
            else
            {
                Theme.Title(new Rect(tx, row.y + 2, tw, 40), b.Name, Color.white, 24);
                var stage = CustomBosses.Stage(b.Stage);
                var by = b.SharedAuthor != null && b.SharedAuthor != Share.Login ? $"by {b.SharedAuthor}   ·   " : b.SharedIssue != null ? "shared   ·   " : "";
                var info = b.Error ?? by + $"on {b.Stage}" + (stage == null && CustomBosses.Stages().Count > 0 ? " (missing!)" : "")
                    + $"   ·   {b.Attacks} attacks   ·   {(b.Music ?? "stage's own song")}   ·   {Ago(b.Modified)}";
                EditorGui.Text(new Rect(tx, row.y + 46, tw, 24), info, b.Error != null ? Orange : Dim, 13);
            }

            if (deleting == b)
            {
                EditorGui.Text(new Rect(row.xMax - 530, row.y + 28, 230, 26), "Delete this boss?", Orange, 15);
                if (EditorGui.Button(new Rect(row.xMax - 290, row.y + 22, 130, 38), "Yes, delete", color: RedBtn))
                {
                    try { CustomBosses.Delete(b); Say($"Deleted '{b.Name}' (it's in bosses\\deleted if you change your mind)"); }
                    catch (Exception ex) { Say("Couldn't delete it: " + ex.Message, true); }
                    deleting = null; Refresh();
                }
                if (EditorGui.Button(new Rect(row.xMax - 150, row.y + 22, 130, 38), "Keep it")) deleting = null;
                return;
            }

            float bx = row.xMax - 576, by2 = row.y + 8;
            var usable = b.Error == null;
            if (Hosting)
            {
                if (EditorGui.Button(new Rect(bx, by2, 136, 36), isLobbyBoss ? "In lobby" : "Use in lobby", "Play this boss with everyone in your online lobby", on: isLobbyBoss, color: usable ? GreenBtn : RowHover))
                    UseInLobby(b);
            }
            else if (EditorGui.Button(new Rect(bx, by2, 136, 36), "Play", "Play this boss", color: CanLaunch && usable ? Pink : RowHover)) Launch(b, false);
            if (EditorGui.Button(new Rect(bx + 142, by2, 136, 36), "Edit", "Start this boss with the editor open", color: CanLaunch && usable ? BlueBtn : RowHover)) Launch(b, true);
            if (EditorGui.Button(new Rect(bx + 284, by2, 270, 36), "Song: " + (b.Music ?? "stage's own"), "Change this boss's song. Add .wav files to the music folder.")) CycleMusic(b);
            by2 += 42;
            if (EditorGui.Button(new Rect(bx, by2, 136, 30), "Rename")) { renaming = b; focus = "rename"; focusText = b.Name; deleting = null; }
            if (EditorGui.Button(new Rect(bx + 142, by2, 136, 30), "Duplicate"))
            {
                try { CustomBosses.Duplicate(b); Say($"Made a copy of '{b.Name}'"); } catch (Exception ex) { Say("Couldn't copy it: " + ex.Message, true); }
                Refresh();
            }
            var mineOnline = b.SharedIssue != null && b.SharedAuthor == Share.Login && Share.Login != null;
            var downloaded = b.SharedIssue != null && b.SharedAuthor != null && b.SharedAuthor != Share.Login;
            if (!downloaded && EditorGui.Button(new Rect(bx + 284, by2, 132, 30), mineOnline ? "Update online" : "Share", mineOnline ? "Upload your changes to your shared boss" : "Share this boss online so others can play and rate it", color: BlueBtn))
                StartUpload(b);
            if (EditorGui.Button(new Rect(bx + 422, by2, 132, 30), "Delete", color: RedBtn)) { deleting = b; renaming = null; focus = null; }
        }

        static void Launch(BossProject b, bool edit)
        {
            if (b.Error != null) { Say(b.Error, true); return; }
            if (!CanLaunch) { Say("Go back to the main menu first, then play the boss from there.", true); return; }
            var err = CustomBosses.Launch(b, edit);
            if (err != null) { Say(err, true); return; }
            Open = false; focus = null;
        }

        static void UseInLobby(BossProject b)
        {
            if (b.Error != null) { Say(b.Error, true); return; }
            string err;
            try { err = LobbySync.Use(b); } catch (Exception e) { err = e.Message; }
            if (err != null) Say(err, true);
            else Say($"'{b.Name}' is the lobby's boss. Pick {b.Stage} in the lobby to play it." + (b.Music != null ? " Players without the same song file hear the stage's song." : ""));
        }

        static void CycleMusic(BossProject b)
        {
            var options = new List<string> { null };
            options.AddRange(Music.Files());
            if (options.Count == 1) { Say("No songs yet: put .wav files in BepInEx\\SunLoader\\music, then press Refresh", true); return; }
            var i = Math.Max(0, options.IndexOf(b.Music));
            try
            {
                CustomBosses.SetMusic(b, options[(i + 1) % options.Count]);
                Music.Get(b.Music);
                Say(b.Music == null ? $"'{b.Name}' uses its stage's song" : $"'{b.Name}' plays {b.Music}");
            }
            catch (Exception e) { Say("Couldn't change the song: " + e.Message, true); }
        }

        static string Ago(DateTime t)
        {
            var d = DateTime.Now - t;
            return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago" : t.ToString("d MMM yyyy");
        }

        // ---------- online ----------

        static void DrawOnline(Rect r, float x, float y, float w)
        {
            var e = Event.current;
            if (!Share.Configured)
            {
                Theme.Title(new Rect(x, y + 40, w, 50), "SHARING ISN'T SET UP YET", Color.white, 30);
                EditorGui.Text(new Rect(x, y + 96, w, 60), $"This build doesn't have a GitHub App for sharing yet. Set Sharing.Repository and Sharing.GitHubAppClientId in BepInEx\\config\\riptid.jsb.sunloader.cfg.", Dim, 15, wrap: true);
                return;
            }

            // Account and sorting.
            if (Share.SignedIn)
            {
                EditorGui.Text(new Rect(x, y + 8, 360, 26), "Signed in to GitHub as " + Share.Login, Green, 14);
                if (EditorGui.Button(new Rect(x + 370, y + 2, 120, 36), "Sign out")) { Share.Logout(); Say("Signed out"); }
            }
            else
            {
                if (EditorGui.Button(new Rect(x, y + 2, 240, 36), "Sign in with GitHub", "Needed to share bosses and rate them. Browsing and downloading work without it.", color: BlueBtn) && !Share.Busy) Share.StartLogin();
                EditorGui.Text(new Rect(x + 252, y + 8, 260, 26), "to share and rate", Dim, 13);
            }
            var sx = r.xMax - 30 - 640;
            search = Field(new Rect(sx, y + 2, 270, 36), "search", search, "search bosses, stages, players");
            if (EditorGui.Button(new Rect(sx + 280, y + 2, 110, 36), "Top", "Best rated first", on: !sortNew)) sortNew = false;
            if (EditorGui.Button(new Rect(sx + 396, y + 2, 110, 36), "New", "Newest first", on: sortNew)) sortNew = true;
            if (EditorGui.Button(new Rect(sx + 512, y + 2, 128, 36), Share.Busy ? "Loading" : "Refresh") && !Share.Busy) Share.Refresh();
            y += 52;

            var q = (focus == "search" ? focusText : search).Trim();
            var list = Share.Bosses.Where(b => q.Length == 0 || $"{b.Name} {b.Author} {b.Stage} {b.Description}".Contains(q, StringComparison.OrdinalIgnoreCase));
            list = sortNew ? list.OrderByDescending(b => b.Created) : list.OrderByDescending(b => b.Score).ThenByDescending(b => b.Likes).ThenByDescending(b => b.Created);
            var shown = list.ToList();

            var listRect = new Rect(x, y, w, r.yMax - y - 70);
            if (e.type == EventType.ScrollWheel && listRect.Contains(e.mousePosition)) { onlineScroll = Mathf.Max(0, onlineScroll + e.delta.y * 20); e.Use(); }
            float ly = listRect.y - onlineScroll;
            if (shown.Count == 0)
            {
                Theme.Title(new Rect(x, ly + 50, w, 50), Share.Busy ? "LOADING..." : Share.Bosses.Count == 0 ? "NOTHING SHARED YET" : "NO MATCHES", Color.white, 30);
                if (!Share.Busy && Share.Bosses.Count == 0) EditorGui.Text(new Rect(x, ly + 102, w, 26), "Be the first: press Share on one of your bosses in My bosses.", Dim, 15);
            }
            foreach (var b in shown)
            {
                const float h = 100;
                if (ly >= listRect.y - 2 && ly + h <= listRect.yMax + 2) OnlineRow(b, new Rect(x, ly, w, h - 8));
                ly += h;
            }
            onlineScroll = Mathf.Min(onlineScroll, Mathf.Max(0, shown.Count * 100 - listRect.height));

            var fy = r.yMax - 52;
            EditorGui.Text(new Rect(x, fy + 6, w - 260, 26), $"{Share.Bosses.Count} shared bosses · stored at github.com/{Share.Repo} · bosses only contain attack data, never code", Dim, 12);
            if (EditorGui.Button(new Rect(r.xMax - 280, fy, 250, 36), "Open on GitHub")) OpenUrl($"https://github.com/{Share.Repo}/issues");
        }

        static void OnlineRow(SharedBoss b, Rect row)
        {
            var hover = row.Contains(Event.current.mousePosition);
            Theme.Slanted(row, hover ? RowHover : Row, 0.06f);
            EditorGui.Fill(new Rect(row.x, row.y, 6, row.height), b.Score > 0 ? Green : b.Score < 0 ? Orange : Pink);
            float tx = row.x + 22, tw = row.width - 640;
            Theme.Title(new Rect(tx, row.y + 2, tw, 40), b.Name, Color.white, 24);
            var stage = CustomBosses.Stage(b.Stage);
            EditorGui.Text(new Rect(tx, row.y + 42, tw, 22),
                $"by {b.Author}   ·   on {b.Stage}" + (stage == null && CustomBosses.Stages().Count > 0 ? " (not in your game!)" : "") + $"   ·   {b.Attacks} attacks   ·   {(b.Music != null ? "song: " + b.Music : "stage's song")}   ·   {Ago(b.Created.ToLocalTime())}", Dim, 13);
            if (b.Description != null) EditorGui.Text(new Rect(tx, row.y + 64, tw, 22), b.Description, new Color(0.85f, 0.85f, 0.9f), 13);

            // Rating.
            var rx = row.xMax - 620;
            Theme.Title(new Rect(rx, row.y + 10, 90, 40), (b.Score > 0 ? "+" : "") + b.Score, b.Score > 0 ? Green : b.Score < 0 ? Orange : Color.white, 28);
            EditorGui.Text(new Rect(rx, row.y + 52, 100, 22), $"{b.Likes} up · {b.Dislikes} down", Dim, 11);
            Share.MyVotes.TryGetValue(b.Number, out var vote);
            if (EditorGui.Button(new Rect(rx + 100, row.y + 8, 110, 36), "Like", Share.SignedIn ? "Rate it up (click again to take it back)" : "Sign in with GitHub to rate", on: vote > 0, color: GreenBtn)) Rate(b, 1);
            if (EditorGui.Button(new Rect(rx + 100, row.y + 50, 110, 30), "Dislike", Share.SignedIn ? "Rate it down (click again to take it back)" : "Sign in with GitHub to rate", on: vote < 0, color: RedBtn)) Rate(b, -1);

            var bx = row.xMax - 396;
            var local = bosses.FirstOrDefault(l => l.SharedIssue == b.Number);
            if (local == null)
            {
                if (EditorGui.Button(new Rect(bx, row.y + 8, 190, 36), "Download", "Add it to My bosses", color: Pink)) Download(b);
            }
            else if (Hosting)
            {
                if (EditorGui.Button(new Rect(bx, row.y + 8, 190, 36), "Use in lobby", color: GreenBtn)) UseInLobby(local);
            }
            else if (EditorGui.Button(new Rect(bx, row.y + 8, 190, 36), "Play", "You have it: play it now", color: CanLaunch ? Pink : RowHover)) Launch(local, false);
            if (EditorGui.Button(new Rect(bx + 196, row.y + 8, 170, 36), $"Comments ({b.Comments})", "Open its page on GitHub to read and leave comments")) OpenUrl(b.Url);
            if (b.Author == Share.Login && Share.Login != null)
            {
                if (removing == b)
                {
                    if (EditorGui.Button(new Rect(bx, row.y + 50, 190, 30), "Yes, remove it", color: RedBtn)) { removing = null; Share.Remove(b, m => Say(m)); }
                    if (EditorGui.Button(new Rect(bx + 196, row.y + 50, 170, 30), "Keep it")) removing = null;
                }
                else if (EditorGui.Button(new Rect(bx, row.y + 50, 190, 30), "Remove online", "Take your boss off the online list (your copy stays)", color: RedBtn)) removing = b;
            }
            else if (local != null) EditorGui.Text(new Rect(bx + 6, row.y + 54, 360, 22), "In My bosses as '" + local.Name + "'", Green, 12);
        }

        static void Rate(SharedBoss b, int vote)
        {
            if (!Share.SignedIn) { Say("Sign in with GitHub to rate bosses", true); return; }
            if (Share.Busy) { Say("One moment, still talking to GitHub", true); return; }
            Share.Vote(b, vote);
        }

        static void Download(SharedBoss b)
        {
            try
            {
                var name = Share.Download(b);
                Refresh();
                Say($"Downloaded '{b.Name}'" + (name != b.Name ? $" as '{name}'" : "") + ". It's in My bosses." + (b.Music != null && !Music.Files().Contains(b.Music) ? $" (It uses a song you don't have, '{b.Music}', so the stage's song plays.)" : ""));
            }
            catch (Exception e) { Say("Couldn't download it: " + e.Message, true); }
        }

        // ---------- upload ----------

        static void StartUpload(BossProject b)
        {
            if (b.Error != null) { Say(b.Error, true); return; }
            if (!Share.Configured) { Say("Sharing isn't set up in this build yet", true); return; }
            uploading = b;
            uploadDesc = "";
            focus = "desc"; focusText = "";
        }

        static void DrawUpload(float vw)
        {
            var b = uploading;
            var r = Modal(vw, 760, 420);
            float x = r.x + 30, y = r.y + 24, w = r.width - 60;
            Theme.Title(new Rect(x, y, w, 50), "SHARE '" + b.Name.ToUpperInvariant() + "'", Pink, 30);
            y += 60;
            if (!Share.SignedIn)
            {
                EditorGui.Text(new Rect(x, y, w, 60), "Sharing needs a free GitHub account, so your boss shows who made it and only you can change it.", Color.white, 15, wrap: true);
                y += 70;
                if (EditorGui.Button(new Rect(x, y, 260, 44), "Sign in with GitHub", color: BlueBtn) && !Share.Busy) Share.StartLogin();
                if (EditorGui.Button(new Rect(r.xMax - 210, r.yMax - 70, 180, 44), "Cancel")) { uploading = null; focus = null; }
                return;
            }
            EditorGui.Text(new Rect(x, y, w, 24), $"On {b.Stage}   ·   {b.Attacks} attacks   ·   " + (b.Music != null ? $"song: {b.Music} (the song file isn't uploaded; players need their own copy)" : "the stage's own song"), Dim, 13);
            y += 36;
            EditorGui.Text(new Rect(x, y, w, 24), "A short description (optional):", Color.white, 15);
            uploadDesc = Field(new Rect(x, y + 30, w, 40), "desc", uploadDesc);
            y += 90;
            EditorGui.Text(new Rect(x, y, w, 60), $"Anyone can download and rate it. It's posted publicly on GitHub as {Share.Login}. You can update or remove it any time.", Dim, 13, wrap: true);
            if (EditorGui.Button(new Rect(x, r.yMax - 70, 260, 44), Share.Busy ? "Uploading..." : "Share it", color: Pink) && !Share.Busy)
            {
                var desc = focus == "desc" ? focusText : uploadDesc;
                Share.Upload(b, desc, m => { Say(m); Refresh(); });
                uploading = null; focus = null;
            }
            if (EditorGui.Button(new Rect(r.xMax - 210, r.yMax - 70, 180, 44), "Cancel")) { uploading = null; focus = null; }
        }

        // ---------- GitHub login ----------

        static void DrawLogin(float vw)
        {
            var r = Modal(vw, 760, 470);
            float x = r.x + 30, y = r.y + 24, w = r.width - 60;
            Theme.Title(new Rect(x, y, w, 50), "SIGN IN WITH GITHUB", Pink, 30);
            y += 66;
            EditorGui.Text(new Rect(x, y, w, 26), "1.  Open github.com/login/device in your browser", Color.white, 17);
            if (EditorGui.Button(new Rect(x + 30, y + 34, 240, 40), "Open the page", color: BlueBtn))
                OpenUrl(Share.VerifyUrl != null && Share.VerifyUrl.StartsWith("https://github.com/") ? Share.VerifyUrl : "https://github.com/login/device");
            y += 96;
            EditorGui.Text(new Rect(x, y, w, 26), "2.  Type this code there and press Authorize", Color.white, 17);
            Theme.Title(new Rect(x + 30, y + 30, 420, 70), Share.UserCode ?? "", Yellow, 52);
            if (EditorGui.Button(new Rect(x + 460, y + 44, 180, 40), "Copy code"))
            {
                try { GUIUtility.systemCopyBuffer = Share.UserCode; Say("Code copied"); } catch { Say("Couldn't copy; type it in instead", true); }
            }
            y += 120;
            EditorGui.Text(new Rect(x, y, w, 44), "Sun Loader can only post and rate bosses with this login; it can't touch anything else on your GitHub account.", Dim, 13, wrap: true);
            EditorGui.Text(new Rect(x, r.yMax - 60, 300, 26), "Waiting for GitHub" + new string('.', 1 + (int)(Time.realtimeSinceStartup * 2) % 3), Cyan, 15);
            if (EditorGui.Button(new Rect(r.xMax - 210, r.yMax - 70, 180, 44), "Cancel")) Share.CancelLogin();
        }

        static Rect Modal(float vw, float w, float h)
        {
            EditorGui.Fill(new Rect(0, 0, vw, 1080), new Color(0, 0, 0, 0.6f));
            var r = new Rect(vw / 2 - w / 2, 540 - h / 2, w, h);
            EditorGui.Fill(r, Bg);
            EditorGui.Fill(new Rect(r.x, r.y, r.width, 4), Pink);
            EditorGui.Outline(r, new Color(Pink.r, Pink.g, Pink.b, 0.5f), 2);
            return r;
        }

        // ---------- new boss ----------

        static void StartNew()
        {
            screen = Screen.New;
            newName = "";
            for (int i = 1; newName == "" || CustomBosses.Exists(newName); i++) newName = "My Boss " + i;
            focus = "new"; focusText = newName;
            newStage ??= CustomBosses.Stages().FirstOrDefault()?.Id;
            newWithOriginal = false;
            stageScroll = 0;
        }

        static void DrawNew(Rect r, float x, float y, float w)
        {
            var e = Event.current;
            Label(x, y, "1   NAME");
            newName = Field(new Rect(x, y + 34, 520, 40), "new", newName);
            y += 92;

            var stages = CustomBosses.Stages();
            Label(x, y, "2   STAGE");
            EditorGui.Text(new Rect(x + 150, y + 4, w - 150, 24), "The level your boss plays in: its scene, its length, and its song (unless you pick your own).", Dim, 13);
            y += 36;
            var gridRect = new Rect(x, y, w, r.yMax - y - 230);
            EditorGui.Fill(gridRect, new Color(0, 0, 0, 0.35f));
            if (stages.Count == 0)
                EditorGui.Text(new Rect(x + 16, y + 16, w - 32, 50), "The game's level list isn't loaded yet. Wait on the main menu a moment, then open this again.", Orange, 15, wrap: true);
            if (e.type == EventType.ScrollWheel && gridRect.Contains(e.mousePosition)) { stageScroll = Mathf.Max(0, stageScroll + e.delta.y * 20); e.Use(); }
            const int cols = 3; const float ch = 58;
            float cw = (w - 16 - (cols - 1) * 8) / cols;
            var rowsNeeded = (stages.Count + cols - 1) / cols;
            stageScroll = Mathf.Min(stageScroll, Mathf.Max(0, rowsNeeded * (ch + 8) + 8 - gridRect.height));
            for (int i = 0; i < stages.Count; i++)
            {
                var s = stages[i];
                var cell = new Rect(x + 8 + (i % cols) * (cw + 8), gridRect.y + 8 + (i / cols) * (ch + 8) - stageScroll, cw, ch);
                if (cell.y < gridRect.y || cell.yMax > gridRect.yMax) continue;
                var picked = s.Id == newStage;
                var hover = cell.Contains(e.mousePosition);
                Theme.Slanted(cell, picked ? Pink : hover ? RowHover : Row, 0.08f);
                EditorGui.Text(new Rect(cell.x + 14, cell.y + 5, cw - 24, 26), s.Id, Color.white, 16);
                var sub = (s.Artist ?? "") + (s.LengthSec > 0 ? $"  ·  {(int)(s.LengthSec / 60)}:{(int)(s.LengthSec % 60):00}" : "") + (CustomBosses.HasDump(s.Id) ? "" : "  ·  not played yet");
                EditorGui.Text(new Rect(cell.x + 14, cell.y + 31, cw - 24, 22), sub, picked ? Color.white : Dim, 12);
                if (hover && e.type == EventType.MouseDown && e.button == 0) { newStage = s.Id; e.Use(); }
            }
            y = gridRect.yMax + 18;

            Label(x, y, "3   START FROM");
            y += 36;
            var hasDump = newStage != null && CustomBosses.HasDump(newStage);
            if (!hasDump) newWithOriginal = false;
            if (EditorGui.Button(new Rect(x, y, 300, 40), "An empty boss", "No attacks, just the stage's checkpoints", on: !newWithOriginal)) newWithOriginal = false;
            if (EditorGui.Button(new Rect(x + 310, y, 300, 40), "The stage's attacks", "A copy of the level's own attacks, to remix", on: newWithOriginal, color: hasDump ? null : RowHover))
            {
                if (hasDump) newWithOriginal = true;
                else Say("Play this stage once (anywhere) first, so Sun Loader can read its attacks", true);
            }
            if (!hasDump && newStage != null)
                EditorGui.Text(new Rect(x + 630, y + 8, w - 630, 26), "Copying attacks needs the stage to have been played once.", Dim, 12);

            var fy = r.yMax - 64;
            if (EditorGui.Button(new Rect(x, fy, 260, 46), "Create + edit", "Make the boss and start its stage with the editor open", color: Pink)) Create(true);
            if (EditorGui.Button(new Rect(x + 270, fy, 180, 46), "Create", "Make the boss and go back to the list")) Create(false);
            if (EditorGui.Button(new Rect(r.xMax - 210, fy, 180, 46), "Cancel")) ShowList();
        }

        static void Label(float x, float y, string text)
        {
            Theme.Slanted(new Rect(x, y + 3, 8, 24), Pink, 0.3f);
            Theme.Title(new Rect(x + 18, y - 4, 400, 36), text, Color.white, 20);
        }

        static void Create(bool edit)
        {
            var name = CustomBosses.CleanName(focus == "new" ? focusText : newName);
            if (name.Length == 0) { Say("Give your boss a name", true); return; }
            if (CustomBosses.Exists(name)) { Say($"There's already a boss called '{name}'", true); return; }
            if (newStage == null) { Say("Pick a stage", true); return; }
            if (edit && !CanLaunch) { Say("Go back to the main menu first to start editing (or press Create)", true); return; }
            BossProject b;
            try { b = CustomBosses.Create(name, newStage, newWithOriginal); }
            catch (Exception e) { Say("Couldn't create it: " + e.Message, true); return; }
            ShowList();
            Say($"Created '{name}'");
            if (edit && b != null) Launch(b, true);
        }

        // ---------- text input ----------

        // Same hand-drawn field as the editor's (Unity's text box is stripped from this game).
        static string Field(Rect r, string id, string value, string placeholder = null)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition)) { if (focus != id) { focus = id; focusText = value; } e.Use(); }
            var focused = focus == id;
            EditorGui.Fill(r, Color.black);
            EditorGui.Outline(r, focused ? Cyan : new Color(0.25f, 0.25f, 0.3f), 2);
            var caret = focused && (int)(Time.realtimeSinceStartup * 2) % 2 == 0 ? "|" : "";
            var text = focused ? focusText : value;
            var textRect = new Rect(r.x + 10, r.y + (r.height - 26) / 2, r.width - 16, 26);
            if (text.Length == 0 && !focused && placeholder != null) EditorGui.Text(textRect, placeholder, Dim, 15);
            else EditorGui.Text(textRect, text + caret, Color.white, 17);
            return text;
        }

        // Typing into the focused field, plus Esc. Returns true if the event was used.
        static bool Keys(Event e)
        {
            if (Share.UserCode != null && e.keyCode == KeyCode.Escape) { Share.CancelLogin(); e.Use(); return true; }
            if (focus != null)
            {
                if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter) { Submit(); e.Use(); return true; }
                if (e.keyCode == KeyCode.Escape) { CancelTyping(); e.Use(); return true; }
                var max = focus == "desc" ? 200 : 60;
                if (e.keyCode == KeyCode.Backspace) { if (focusText.Length > 0) focusText = focusText[..^1]; }
                else if (e.character != '\0' && !char.IsControl(e.character) && focusText.Length < max) focusText += e.character;
                e.Use();
                return true;
            }
            if (e.keyCode == KeyCode.Escape)
            {
                if (uploading != null) uploading = null;
                else if (screen == Screen.New) ShowList();
                else Toggle();
                e.Use();
                return true;
            }
            return false;
        }

        static void Submit()
        {
            switch (focus)
            {
                case "new": newName = focusText; focus = null; return;
                case "desc": uploadDesc = focusText; focus = null; return;
                case "search": search = focusText; focus = null; return;
            }
            if (focus == "rename" && renaming != null)
            {
                var name = CustomBosses.CleanName(focusText);
                if (name.Length > 0 && name != renaming.Name)
                {
                    if (CustomBosses.Exists(name)) { Say($"There's already a boss called '{name}'", true); return; }
                    try { CustomBosses.Rename(renaming, name); Say($"Renamed to '{name}'"); }
                    catch (Exception e) { Say("Couldn't rename it: " + e.Message, true); }
                }
                renaming = null; focus = null;
                Refresh();
            }
        }

        static void CancelTyping()
        {
            if (focus == "rename") renaming = null;
            focus = null;
        }

        static void OpenFolder(string dir)
        {
            try { System.IO.Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", dir); }
            catch (Exception e) { Say("Couldn't open the folder: " + e.Message, true); }
        }

        // Only ever opens github.com pages (the bosses repo, a boss's page, or the login page).
        static void OpenUrl(string url)
        {
            if (url == null || !url.StartsWith("https://github.com/")) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Say("Couldn't open the browser: " + e.Message, true); }
        }
    }
}
