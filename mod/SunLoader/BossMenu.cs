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
        enum Screen { List, New, Online, Suns }
        static Screen screen;
        static List<BossProject> bosses = new();
        static float scroll, stageScroll, onlineScroll;
        static string message;
        static bool messageIsError;
        static float messageUntil;

        // The one text field being typed in: "new", "rename", "desc" or "search".
        static string focus, focusText = "";
        static BossProject renaming, deleting, uploading;
        static SharedBoss removing, rating;

        // New boss form.
        static string newName = "", newStage;
        static bool newWithOriginal;

        // Online tab.
        static int sortMode;   // 0 most liked, 1 newest, 2 most suns
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
            focus = null; renaming = deleting = uploading = null; removing = rating = null;
            Refresh();
        }

        static void ShowSuns()
        {
            screen = Screen.Suns;
            focus = null; renaming = deleting = uploading = null; removing = rating = null;
            sunsScroll = 0;
            if (Share.Configured && !Share.Busy && (DateTime.Now - Share.LastRefresh).TotalMinutes > 2) Share.Refresh();
        }

        static void ShowOnline()
        {
            screen = Screen.Online;
            focus = null; renaming = deleting = uploading = null; removing = rating = null;
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
        // Custom bosses in multiplayer are coming soon: picking one for a lobby is off until then.
        static bool Hosting => false;

        public static void OnGUI(float scale, float vw)
        {
            var e = Event.current;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));

            if (!Open && !ModMenu.Open && MainMenuSeen.OnMainMenu) MainMenuButton(e);
            if (!Open) return;
            if (e.type == EventType.KeyDown && Keys(e)) return;

            // With a popup up, the menu behind it is only painted, so clicks can't reach it.
            var modal = Share.UserCode != null || uploading != null || rating != null || banning != null;
            if (!modal || e.type == EventType.Repaint) DrawMenu(vw);
            if (Share.UserCode != null) DrawLogin(vw);
            else if (uploading != null) DrawUpload(vw);
            else if (rating != null) DrawRate(vw);
            else if (banning != null) DrawBan(vw);
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
            if (Share.SignedIn && screen != Screen.New)
            {
                // Your sun counter, like GD's star counter.
                Sun(new Rect(x + 120, y + 52, 24, 24), Yellow);
                EditorGui.Text(new Rect(x + 150, y + 53, 260, 24), $"{Share.MySuns} SUNS   ·   {Share.MyBeaten} BEATEN", Yellow, 14);
            }

            // Tabs and Close.
            if (screen != Screen.New)
            {
                if (EditorGui.Button(new Rect(r.xMax - 720, y + 12, 170, 40), "My bosses", on: screen == Screen.List)) ShowList();
                if (EditorGui.Button(new Rect(r.xMax - 540, y + 12, 170, 40), "Online", on: screen == Screen.Online)) ShowOnline();
                if (EditorGui.Button(new Rect(r.xMax - 360, y + 12, 170, 40), "Suns", "Your suns and the leaderboard", on: screen == Screen.Suns)) ShowSuns();
            }
            if (EditorGui.Button(new Rect(r.xMax - 180, y + 12, 150, 40), $"Close ({Plugin.BossMenuKey.Value})")) { Toggle(); return; }
            y += 96;

            if (Time.realtimeSinceStartup < messageUntil) EditorGui.Text(new Rect(x, y, w, 26), message, messageIsError ? Orange : Green);
            else if (Share.Error != null && screen == Screen.Online) EditorGui.Text(new Rect(x, y, w, 26), Share.Error, Orange);
            else if (Share.Status != null) EditorGui.Text(new Rect(x, y, w, 26), Share.Status, Cyan);
            y += 30;

            if (screen != Screen.New && Share.IAmBanned)
                y = AppealBar(x, y, w, "You're banned from suns, sharing and rating" + (string.IsNullOrEmpty(Share.MyBanReason) ? "." : ": " + Share.MyBanReason));
            else if (screen == Screen.Suns && AntiCheat.LastFlag != null)
                y = AppealBar(x, y, w, AntiCheat.LastFlag);
            if (Hosting && screen != Screen.New) y = LobbyBar(x, y, w);

            if (screen == Screen.New) DrawNew(r, x, y, w);
            else if (screen == Screen.Online) DrawOnline(r, x, y, w);
            else if (screen == Screen.Suns) DrawSuns(r, x, y, w);
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

        // ---------- bans and appeals ----------

        static string banning, banReason = "";
        static bool showBanned;

        static float AppealBar(float x, float y, float w, string text)
        {
            var bar = new Rect(x, y, w, 50);
            Theme.Slanted(bar, new Color(0.3f, 0.05f, 0.1f), 0.05f);
            EditorGui.Fill(new Rect(bar.x, bar.y, 6, bar.height), Orange);
            EditorGui.Text(new Rect(x + 20, y + 13, w - 290, 26), text, Color.white, 14);
            if (Share.DiscordInvite.Length > 0)
            {
                if (EditorGui.Button(new Rect(bar.xMax - 250, y + 8, 240, 34), "Appeal on Discord", "Think this is a mistake? Ask on the Sun Loader Discord server.", color: BlueBtn)) OpenUrl(Share.DiscordInvite);
            }
            else EditorGui.Text(new Rect(bar.xMax - 270, y + 15, 260, 22), "Appeal on the Sun Loader Discord", Dim, 13);
            return y + 62;
        }

        static void DrawBan(float vw)
        {
            var r = Modal(vw, 760, 400);
            float x = r.x + 30, y = r.y + 24, w = r.width - 60;
            Theme.Title(new Rect(x, y, w, 50), "BAN " + banning.ToUpperInvariant(), Pink, 30);
            y += 66;
            EditorGui.Text(new Rect(x, y, w, 50), "They lose their suns and leave the leaderboard, their bosses are hidden, and they can't share or rate. They'll be told they can appeal on Discord.", Color.white, 14, wrap: true);
            y += 66;
            EditorGui.Text(new Rect(x, y, w, 24), "Reason (shown publicly on GitHub and to the player):", Color.white, 15);
            banReason = Field(new Rect(x, y + 30, w, 40), "banreason", banReason);
            var reason = (focus == "banreason" ? focusText : banReason).Trim();
            if (EditorGui.Button(new Rect(x, r.yMax - 70, 260, 44), "Ban them", color: reason.Length > 0 ? RedBtn : RowHover))
            {
                if (reason.Length == 0) { Say("Give a reason, so they know what to appeal", true); return; }
                var who = banning;
                banning = null; focus = null;
                Share.SetBan(who, reason, m => Say(m));
                return;
            }
            if (EditorGui.Button(new Rect(r.xMax - 210, r.yMax - 70, 180, 44), "Cancel")) { banning = null; focus = null; }
        }

        static void DrawBanned(Rect listRect)
        {
            var bans = Share.Bans.OrderBy(kv => kv.Key).ToList();
            if (bans.Count == 0) EditorGui.Text(new Rect(listRect.x, listRect.y + 10, listRect.width, 26), "Nobody is banned.", Dim, 15);
            const float h = 46;
            float ly = listRect.y - sunsScroll;
            foreach (var (name, reason) in bans)
            {
                if (ly >= listRect.y - 2 && ly + h <= listRect.yMax + 2)
                {
                    var row = new Rect(listRect.x, ly, listRect.width, h - 6);
                    Theme.Slanted(row, Row, 0.04f);
                    EditorGui.Text(new Rect(row.x + 16, row.y + 9, 280, 24), name, Color.white, 17);
                    EditorGui.Text(new Rect(row.x + 300, row.y + 11, row.width - 480, 22), reason, Dim, 13);
                    if (EditorGui.Button(new Rect(row.xMax - 150, row.y + 4, 140, 32), "Unban", color: GreenBtn)) Share.SetBan(name, null, m => Say(m));
                }
                ly += h;
            }
            sunsScroll = Mathf.Min(sunsScroll, Mathf.Max(0, bans.Count * h - listRect.height));
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
            var sx = r.xMax - 30 - 660;
            search = Field(new Rect(sx, y + 2, 240, 36), "search", search, "search bosses, stages, players");
            if (EditorGui.Button(new Rect(sx + 250, y + 2, 86, 36), "Top", "Most liked first", on: sortMode == 0)) sortMode = 0;
            if (EditorGui.Button(new Rect(sx + 342, y + 2, 86, 36), "New", "Newest first", on: sortMode == 1)) sortMode = 1;
            if (EditorGui.Button(new Rect(sx + 434, y + 2, 96, 36), "Rated", "Only rated bosses, hardest first", on: sortMode == 2)) sortMode = 2;
            if (EditorGui.Button(new Rect(sx + 536, y + 2, 124, 36), Share.Busy ? "Loading" : "Refresh") && !Share.Busy) Share.Refresh();
            y += 52;

            var q = (focus == "search" ? focusText : search).Trim();
            var list = Share.Bosses.Where(b => q.Length == 0 || $"{b.Name} {b.Author} {b.Stage} {b.Description}".Contains(q, StringComparison.OrdinalIgnoreCase));
            list = sortMode switch
            {
                1 => list.OrderByDescending(b => b.Created),
                2 => list.Where(b => b.Suns > 0).OrderByDescending(b => b.Suns).ThenByDescending(b => b.Score),
                _ => list.OrderByDescending(b => b.Score).ThenByDescending(b => b.Likes).ThenByDescending(b => b.Created),
            };
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
            Badge(new Rect(row.x + 16, row.y + 6, 116, row.height - 12), b.EarnableSuns, b.ClearedByMe);
            float tx = row.x + 146, tw = row.width - 640 - 124;
            Theme.Title(new Rect(tx, row.y + 2, tw, 40), b.Name, Color.white, 24);
            var stage = CustomBosses.Stage(b.Stage);
            EditorGui.Text(new Rect(tx, row.y + 42, tw, 22),
                $"by {b.Author}   ·   on {b.Stage}" + (stage == null && CustomBosses.Stages().Count > 0 ? " (not in your game!)" : "") + $"   ·   {b.Attacks} attacks   ·   beaten by {b.Clears}   ·   {Ago(b.Created.ToLocalTime())}" + (b.RatingStale ? "   ·   changed since it was rated" : ""), Dim, 13);
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
            else if (local != null && !Share.IsOwner) EditorGui.Text(new Rect(bx + 6, row.y + 54, 360, 22), "In My bosses as '" + local.Name + "'", Green, 12);
            if (Share.IsOwner && removing != b && EditorGui.Button(new Rect(bx + 196, row.y + 50, 170, 30), b.Suns == 0 ? "Rate" : "Change rating", "Owner only: rate it 1-20 suns, which sets its difficulty", color: new Color(0.5f, 0.4f, 0.05f)))
                rating = b;
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

        // ---------- suns, difficulty and the leaderboard ----------

        static float sunsScroll;
        static Texture2D sunTex;

        public static Color DifficultyColor(string d) => d switch
        {
            "Easy" => new Color(0.35f, 0.8f, 1f),
            "Normal" => new Color(0.3f, 0.85f, 0.4f),
            "Hard" => new Color(1f, 0.78f, 0.15f),
            "Harder" => new Color(1f, 0.45f, 0.15f),
            "Insane" => new Color(1f, 0.2f, 0.65f),
            "Easy Demon" => new Color(0.62f, 0.35f, 1f),
            "Medium Demon" => new Color(0.3f, 0.45f, 1f),
            "Hard Demon" => new Color(0.9f, 0.12f, 0.12f),
            "Insane Demon" => new Color(0.72f, 0.04f, 0.38f),
            "Extreme Demon" => new Color(0.42f, 0.02f, 0.08f),
            _ => new Color(0.22f, 0.22f, 0.27f),
        };

        // A sun (a disc with eight rays, like Sun Loader's logo), drawn into a texture once.
        static void Sun(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            if (sunTex == null)
            {
                const int n = 64;
                sunTex = new Texture2D(n, n) { filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                var center = new Vector2(n / 2f, n / 2f);
                for (int py = 0; py < n; py++)
                    for (int px = 0; px < n; px++)
                    {
                        var d = new Vector2(px + 0.5f, py + 0.5f) - center;
                        var dist = d.magnitude / n;
                        var inside = dist <= 0.27f;
                        if (!inside && dist >= 0.32f && dist <= 0.5f)
                        {
                            // Rays: narrow wedges every 45 degrees, tapering to a point at the tip.
                            var angle = Mathf.Repeat(Mathf.Atan2(d.y, d.x), Mathf.PI / 4);
                            var off = Mathf.Min(angle, Mathf.PI / 4 - angle);
                            inside = off <= 0.2f * (0.5f - dist) / 0.18f;
                        }
                        sunTex.SetPixel(px, py, inside ? Color.white : new Color(1, 1, 1, 0));
                    }
                sunTex.Apply();
            }
            var old = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, sunTex);
            GUI.color = old;
        }

        // A boss's rating: a colored block with its suns and difficulty, like GD's difficulty face.
        static void Badge(Rect r, int suns, bool beaten)
        {
            var difficulty = Share.DifficultyFor(suns);
            var color = DifficultyColor(difficulty);
            Theme.Slanted(r, color, 0.08f);
            var light = difficulty is "Easy" or "Normal" or "Hard";
            var ink = light ? new Color(0.05f, 0.05f, 0.08f) : Color.white;
            if (difficulty == null)
            {
                Theme.Title(new Rect(r.x + 12, r.y + r.height / 2 - 18, r.width - 16, 36), "UNRATED", Dim, 16);
                return;
            }
            Sun(new Rect(r.x + 12, r.y + 8, 34, 34), light ? ink : Yellow);
            Theme.Title(new Rect(r.x + 50, r.y + 4, r.width - 52, 40), suns.ToString(), ink, 30);
            EditorGui.Text(new Rect(r.x + 10, r.yMax - (beaten ? 44 : 28), r.width - 12, 22), difficulty.ToUpperInvariant(), ink, 12);
            if (beaten) EditorGui.Text(new Rect(r.x + 10, r.yMax - 24, r.width - 12, 20), "BEATEN", light ? new Color(0.05f, 0.35f, 0.1f) : Green, 12);
        }

        static void DrawRate(float vw)
        {
            var b = rating;
            var r = Modal(vw, 900, 640);
            float x = r.x + 30, y = r.y + 24, w = r.width - 60;
            Theme.Title(new Rect(x, y, w, 50), "RATE '" + b.Name.ToUpperInvariant() + "'", Pink, 30);
            EditorGui.Text(new Rect(x, y + 50, w, 24), (b.IsLevel ? $"Game level by {b.Artist}" : $"by {b.Author}   ·   {b.Attacks} attacks") + $"   ·   beaten by {b.Clears}. Players who beat it get its suns, also the ones who beat it before.", Dim, 13);
            y += 86;
            const int cols = 5; const float bw = 160, bh = 98, gap = 8;
            for (int suns = 1; suns <= Share.MaxSuns; suns++)
            {
                var i = suns - 1;
                var cell = new Rect(x + (i % cols) * (bw + gap), y + (i / cols) * (bh + gap), bw, bh);
                var hover = cell.Contains(Event.current.mousePosition);
                Badge(hover ? new Rect(cell.x - 3, cell.y - 3, cell.width + 6, cell.height + 6) : cell, suns, false);
                if (b.Suns == suns) EditorGui.Outline(cell, Color.white, 3);
                if (hover && Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    Event.current.Use();
                    rating = null;
                    var picked = suns;
                    Share.SetSuns(b, picked, m => Say(m));
                    return;
                }
            }
            if (b.Suns > 0 && EditorGui.Button(new Rect(x, r.yMax - 66, 220, 44), "Remove rating", color: RedBtn)) { rating = null; Share.SetSuns(b, 0, m => Say(m)); }
            if (EditorGui.Button(new Rect(r.xMax - 210, r.yMax - 66, 180, 44), "Cancel")) rating = null;
        }

        static void DrawSuns(Rect r, float x, float y, float w)
        {
            var e = Event.current;
            if (!Share.Configured) { EditorGui.Text(new Rect(x, y + 40, w, 30), "Sharing isn't set up in this build, so there are no suns yet.", Dim, 15); return; }

            // You.
            var me = new Rect(x, y, 420, 150);
            Theme.Slanted(me, new Color(0.12f, 0.1f, 0.04f), 0.05f);
            if (Share.SignedIn)
            {
                Sun(new Rect(me.x + 22, me.y + 26, 76, 76), Yellow);
                Theme.Title(new Rect(me.x + 112, me.y + 14, 300, 70), Share.MySuns.ToString(), Yellow, 60);
                var rank = Share.Leaderboard.FindIndex(p => p.Name == Share.Login);
                EditorGui.Text(new Rect(me.x + 116, me.y + 88, 300, 24), $"{Share.MyBeaten} rated bosses beaten" + (rank >= 0 ? $"   ·   rank #{rank + 1}" : ""), Color.white, 15);
                EditorGui.Text(new Rect(me.x + 116, me.y + 114, 300, 22), Share.Login, Dim, 13);
            }
            else
            {
                Sun(new Rect(me.x + 22, me.y + 36, 60, 60), new Color(0.4f, 0.4f, 0.4f));
                EditorGui.Text(new Rect(me.x + 100, me.y + 30, 300, 50), "Sign in with GitHub to collect suns and get on the leaderboard.", Color.white, 15, wrap: true);
                if (EditorGui.Button(new Rect(me.x + 100, me.y + 90, 240, 36), "Sign in with GitHub", color: BlueBtn) && !Share.Busy) Share.StartLogin();
            }

            // How suns work.
            var hx = x + 440;
            Theme.Title(new Rect(hx, y - 4, w - 440, 34), "HOW SUNS WORK", Color.white, 20);
            EditorGui.Text(new Rect(hx, y + 30, w - 440, 44), "The owner rates bosses 1 to 20 suns; beat one and you get its suns. Runs with the editor or no damage on, and changed copies, don't count.", Dim, 13, wrap: true);
            float lx = hx;
            foreach (var suns in new[] { 2, 3, 5, 7, 9, 20 })
            {
                Badge(new Rect(lx, y + 82, 112, 64), suns, false);
                lx += 118;
            }
            y += 172;

            // The leaderboard, or the game's own levels and their suns.
            if (EditorGui.Button(new Rect(x, y, 200, 36), "Leaderboard", on: !showLevels && !showBanned)) { showLevels = showBanned = false; sunsScroll = 0; }
            if (EditorGui.Button(new Rect(x + 208, y, 200, 36), "Game levels", "The game's own levels: beat them in the Playlist for suns", on: showLevels)) { showLevels = true; showBanned = false; sunsScroll = 0; }
            if (Share.IsOwner && EditorGui.Button(new Rect(x + 416, y, 200, 36), $"Banned ({Share.Bans.Count})", "Owner only: players you've banned", on: showBanned)) { showBanned = true; showLevels = false; sunsScroll = 0; }
            if (EditorGui.Button(new Rect(r.xMax - 30 - 128, y, 128, 34), Share.Busy ? "Loading" : "Refresh") && !Share.Busy) Share.Refresh();
            y += 46;
            var listRect = new Rect(x, y, w, r.yMax - y - 20);
            if (e.type == EventType.ScrollWheel && listRect.Contains(e.mousePosition)) { sunsScroll = Mathf.Max(0, sunsScroll + e.delta.y * 20); e.Use(); }
            if (showLevels) { DrawLevels(listRect); return; }
            if (showBanned) { DrawBanned(listRect); return; }
            var board = Share.Leaderboard;
            if (board.Count == 0)
                EditorGui.Text(new Rect(x, y + 10, w, 26), Share.Busy ? "Loading..." : "Nobody has suns yet. Beat a rated boss to be first!", Dim, 15);
            const float h = 46;
            float ly = listRect.y - sunsScroll;
            for (int i = 0; i < board.Count; i++, ly += h)
            {
                if (ly < listRect.y - 2 || ly + h > listRect.yMax + 2) continue;
                var p = board[i];
                var row = new Rect(x, ly, w, h - 6);
                var mine = p.Name == Share.Login;
                Theme.Slanted(row, mine ? new Color(0.05f, 0.2f, 0.25f) : Row, 0.04f);
                var medal = i == 0 ? Yellow : i == 1 ? new Color(0.8f, 0.8f, 0.85f) : i == 2 ? new Color(0.85f, 0.5f, 0.25f) : Dim;
                Theme.Title(new Rect(row.x + 16, row.y + 2, 80, 36), "#" + (i + 1), medal, 22);
                EditorGui.Text(new Rect(row.x + 110, row.y + 9, 500, 24), p.Name, mine ? Cyan : Color.white, 17);
                Sun(new Rect(row.xMax - 290, row.y + 8, 24, 24), Yellow);
                EditorGui.Text(new Rect(row.xMax - 258, row.y + 9, 100, 24), p.Suns.ToString(), Yellow, 17);
                EditorGui.Text(new Rect(row.xMax - 160, row.y + 11, 150, 22), $"{p.Beaten} beaten", Dim, 13);
                if (Share.IsOwner && !mine && EditorGui.Button(new Rect(row.xMax - 410, row.y + 4, 100, 32), "Ban", "Owner only: ban this player", color: RedBtn)) { banning = p.Name; banReason = ""; focus = "banreason"; focusText = ""; }
            }
            sunsScroll = Mathf.Min(sunsScroll, Mathf.Max(0, board.Count * h - listRect.height));
        }

        static bool showLevels;

        // The game's own levels with their suns. Beating one in the Playlist earns them.
        static void DrawLevels(Rect listRect)
        {
            var levels = Share.Levels;
            if (levels.Count == 0)
            {
                EditorGui.Text(new Rect(listRect.x, listRect.y + 10, listRect.width, 26), Share.Busy ? "Loading..." : "The level list hasn't loaded. Press Refresh.", Dim, 15);
                return;
            }
            var beaten = levels.Count(l => l.ClearedByMe);
            EditorGui.Text(new Rect(listRect.x, listRect.y - 4, listRect.width, 22),
                $"Beat these in the Playlist (normal levels, no editor or no damage) to earn their suns.   You've beaten {beaten} of {levels.Count}.", Dim, 13);
            listRect = new Rect(listRect.x, listRect.y + 24, listRect.width, listRect.height - 24);
            const int cols = 2; const float h = 74, gap = 8;
            var cw = (listRect.width - gap) / cols;
            var rows = (levels.Count + cols - 1) / cols;
            for (int i = 0; i < levels.Count; i++)
            {
                var l = levels[i];
                var cell = new Rect(listRect.x + (i % cols) * (cw + gap), listRect.y + (i / cols) * h - sunsScroll, cw, h - 8);
                if (cell.y < listRect.y - 2 || cell.yMax > listRect.yMax + 2) continue;
                Theme.Slanted(cell, cell.Contains(Event.current.mousePosition) ? RowHover : Row, 0.05f);
                Badge(new Rect(cell.x + 8, cell.y + 4, 104, cell.height - 8), l.Suns, l.ClearedByMe);
                Theme.Title(new Rect(cell.x + 124, cell.y + 2, cw - 300, 34), l.Name, Color.white, 20);
                EditorGui.Text(new Rect(cell.x + 126, cell.y + 36, cw - 300, 22), (l.Artist ?? "") + $"   ·   beaten by {l.Clears}", Dim, 12);
                if (Share.IsOwner && EditorGui.Button(new Rect(cell.xMax - 160, cell.y + 16, 150, 34), l.Suns == 0 ? "Rate" : "Change", "Owner only: rate it 1-20 suns", color: new Color(0.5f, 0.4f, 0.05f)))
                    rating = l;
            }
            sunsScroll = Mathf.Min(sunsScroll, Mathf.Max(0, rows * h - listRect.height));
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
                var levelSuns = Share.LevelFor(s.Id)?.Suns ?? 0;
                var sub = (s.Artist ?? "") + (s.LengthSec > 0 ? $"  ·  {(int)(s.LengthSec / 60)}:{(int)(s.LengthSec % 60):00}" : "")
                    + (levelSuns > 0 ? $"  ·  {levelSuns} suns ({Share.DifficultyFor(levelSuns)})" : "") + (CustomBosses.HasDump(s.Id) ? "" : "  ·  not played yet");
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
                var max = focus is "desc" or "banreason" ? 200 : 60;
                if (e.keyCode == KeyCode.Backspace) { if (focusText.Length > 0) focusText = focusText[..^1]; }
                else if (e.character != '\0' && !char.IsControl(e.character) && focusText.Length < max) focusText += e.character;
                e.Use();
                return true;
            }
            if (e.keyCode == KeyCode.Escape)
            {
                if (banning != null) banning = null;
                else if (rating != null) rating = null;
                else if (uploading != null) uploading = null;
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
                case "banreason": banReason = focusText; focus = null; return;
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

        // Only ever opens github.com pages (the bosses repo, a boss's page, the login page) and Discord invites.
        static void OpenUrl(string url)
        {
            if (url == null || !(url.StartsWith("https://github.com/") || url.StartsWith("https://discord.gg/") || url.StartsWith("https://discord.com/invite/"))) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception e) { Say("Couldn't open the browser: " + e.Message, true); }
        }
    }
}
