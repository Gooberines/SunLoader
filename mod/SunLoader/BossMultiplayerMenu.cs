using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SunLoader
{
    // The menu for playing custom bosses with other people. Before a room exists, it picks the
    // boss to host with; in a room, it shows who's there and what the room plays. Rooms themselves
    // are the game's own (Challenge -> Friends or Online). Opened by its main menu button or F5.
    static class BossMultiplayerMenu
    {
        public static bool Open;
        static List<BossProject> bosses = new();
        static float scroll;
        static string message;
        static bool messageIsError;
        static float messageUntil;
        static bool goToChallenge;

        static readonly Color Bg = new(0.02f, 0.02f, 0.03f, 0.97f), Row = new(0.09f, 0.09f, 0.12f, 1f), RowHover = new(0.14f, 0.1f, 0.13f);
        static readonly Color Green = new(0.3f, 0.9f, 0.5f), Orange = new(1f, 0.6f, 0.2f), Dim = new(0.6f, 0.6f, 0.7f), Panel = new(0.05f, 0.13f, 0.17f);
        static readonly Color GreenBtn = new(0.15f, 0.45f, 0.25f), RedBtn = new(0.45f, 0.12f, 0.18f);

        public static void Toggle()
        {
            Open = !Open;
            if (!Open) return;
            ModMenu.Open = false;
            BossMenu.Open = false;
            scroll = 0;
            LobbySync.CheckSoon();
            try { bosses = CustomBosses.List(); }
            catch (Exception e) { bosses = new(); Say("Couldn't read the bosses folder: " + e.Message, true); }
            // The picked boss may have been deleted or renamed since.
            if (LobbySync.Pending is { } picked && !bosses.Any(b => b.Path == picked.Path)) LobbySync.Pending = null;
        }

        static void Say(string msg, bool error = false) { message = msg; messageIsError = error; messageUntil = Time.realtimeSinceStartup + 7; }

        // The game can hide the cursor in its lobby.
        public static void Update()
        {
            // Game code runs here rather than in OnGUI, which is only for drawing.
            if (goToChallenge)
            {
                goToChallenge = false;
                if (!MainMenuSeen.OnMainMenu || !SafeRead.IsObjectOf<UI_MainMenu02>(MainMenuSeen.Instance)) { Say("Go back to the main menu first.", true); return; }
                try { new UI_MainMenu02(MainMenuSeen.Instance).OnChallengeMode(); Open = false; }
                catch (Exception ex) { Plugin.Logger.LogError("Boss multiplayer: " + ex); Say("The game wouldn't open Challenge: " + ex.Message, true); }
            }
            if (!Open) return;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        public static void OnGUI(float scale, float vw)
        {
            var e = Event.current;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (!Open)
            {
                if (!BossMenu.Open && !ModMenu.Open && !MultiplayerWarning.Open && MainMenuSeen.OnMainMenu) MainMenuButton(e);
                return;
            }
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) { e.Use(); Open = false; return; }
            if (e.type == EventType.KeyDown && !MultiplayerKeys.Unlocked) KeyTyping(e);

            EditorGui.Fill(new Rect(0, 0, vw, 1080), new Color(0, 0, 0, 0.75f));
            var r = new Rect(vw / 2 - 620, 50, 1240, 980);
            EditorGui.Fill(r, Bg);
            Theme.Stripes(new Rect(r.x, r.y, r.width, 96), new Color(Theme.Cyan.r, Theme.Cyan.g, Theme.Cyan.b, 0.1f));
            EditorGui.Fill(new Rect(r.x, r.y + 96, r.width, 4), Theme.Cyan);
            EditorGui.Fill(new Rect(r.x, r.yMax - 4, r.width, 4), Theme.Cyan);
            float x = r.x + 30, y = r.y + 20, w = r.width - 60;
            Theme.Title(new Rect(x, y - 2, 800, 60), "BOSS MULTIPLAYER", Theme.Cyan, 46);
            EditorGui.Text(new Rect(x + 4, y + 54, 800, 24), "PLAY CUSTOM BOSSES TOGETHER  ·  EARNS NO SUNS", Color.white, 14);
            if (EditorGui.Button(new Rect(r.xMax - 180, y + 12, 150, 40), $"Close ({Plugin.BossMultiplayerKey.Value})")) { Open = false; return; }
            y += 96;

            if (Time.realtimeSinceStartup < messageUntil) EditorGui.Text(new Rect(x, y, w, 26), message, messageIsError ? Orange : Green);
            y += 34;
            if (!MultiplayerKeys.Unlocked) { DrawLocked(x, y, w); return; }

            var left = new Rect(x, y, 430, r.yMax - y - 26);
            var right = new Rect(x + 450, y, w - 450, left.height);
            if (LobbySync.InLobby) DrawRoom(left); else DrawNoRoom(left);
            if (LobbySync.InLobby && LobbySync.PublicRoom && LobbySync.Boss == null) DrawPublic(right);
            else if (!LobbySync.InLobby || LobbySync.IsHost) DrawBosses(right);
            else DrawGuest(right);
        }

        static void MainMenuButton(Event e)
        {
            var b = new Rect(532, 950, 300, 70);
            var hover = b.Contains(e.mousePosition);
            var kick = Theme.Pulse * 6;
            var block = new Rect(b.x - kick / 2, b.y - kick / 2, b.width + kick, b.height + kick);
            Theme.Slanted(new Rect(block.x + 6, block.y + 6, block.width, block.height), new Color(0, 0, 0, 0.5f), 0.3f);
            Theme.Slanted(block, hover ? Color.white : Theme.Pink, 0.3f);
            Theme.Title(new Rect(b.x + 26, b.y + 6, 270, 44), "MULTIPLAYER", hover ? Theme.Pink : new Color(0.02f, 0.02f, 0.03f), 32);
            EditorGui.Text(new Rect(b.x + 28, b.y + 44, 270, 22), (MultiplayerKeys.Unlocked ? "CUSTOM BOSSES" : "NEEDS A KEY") + $" · {Plugin.BossMultiplayerKey.Value}", hover ? Theme.PinkDark : new Color(0, 0, 0, 0.75f), 12);
            if (e.type == EventType.MouseDown && e.button == 0 && hover) { e.Use(); Toggle(); }
        }

        // ---------- the key ----------

        static string keyText = "";

        // Unity's text box is stripped from this game, so typing is handled by hand.
        static void KeyTyping(Event e)
        {
            if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter) SubmitKey();
            else if (e.keyCode == KeyCode.Backspace) { if (keyText.Length > 0) keyText = keyText[..^1]; }
            else if ((e.control || e.command) && e.keyCode == KeyCode.V) PasteKey();
            else if (e.character != '\0' && !char.IsControl(e.character) && keyText.Length < 40) keyText += char.ToUpperInvariant(e.character);
            else return;
            e.Use();
        }

        static void PasteKey()
        {
            try { keyText = (GUIUtility.systemCopyBuffer ?? "").Trim().ToUpperInvariant(); if (keyText.Length > 40) keyText = keyText[..40]; }
            catch { Say("Couldn't paste; type it in instead", true); }
        }

        static void SubmitKey()
        {
            if (MultiplayerKeys.TryUnlock(keyText)) { keyText = ""; LobbySync.CheckSoon(); Say("Key accepted. Multiplayer is unlocked."); }
            else Say("That key isn't right. Check it and try again.", true);
        }

        static void DrawLocked(float x, float y, float w)
        {
            y += 90;
            Theme.Title(new Rect(x, y, w, 60), "ENTER YOUR KEY", Theme.Pink, 46);
            y += 70;
            EditorGui.Text(new Rect(x, y, w, 60), "Custom bosses in multiplayer need a key. You only have to enter it once. Without one, the game's normal online play still works.", Color.white, 18, wrap: true);
            y += 80;
            var field = new Rect(x, y, w, 64);
            EditorGui.Fill(field, Color.black);
            EditorGui.Outline(field, Theme.Cyan, 2);
            var caret = (int)(Time.realtimeSinceStartup * 2) % 2 == 0 ? "|" : "";
            if (keyText.Length == 0) EditorGui.Text(new Rect(x + 16, y + 18, w - 32, 30), "SUN-XXXXX-XXXXX-XXXXX-XXXXX" + caret, Dim, 22);
            else EditorGui.Text(new Rect(x + 16, y + 18, w - 32, 30), keyText + caret, Color.white, 22);
            y += 84;
            if (EditorGui.Button(new Rect(x, y, 300, 60), "Unlock", "Check this key", color: Theme.Pink)) SubmitKey();
            if (EditorGui.Button(new Rect(x + 320, y, 240, 60), "Paste (Ctrl+V)", "Paste your key from the clipboard")) PasteKey();
        }

        // ---------- left side ----------

        static void DrawNoRoom(Rect r)
        {
            Theme.Slanted(r, Panel, 0.01f);
            float x = r.x + 22, y = r.y + 18, w = r.width - 44;
            Theme.Title(new Rect(x, y, w, 40), "HOST A ROOM", Theme.Cyan, 28);
            y += 52;
            EditorGui.Text(new Rect(x, y, w, 120), "1. Pick a boss from the list.\n2. Go to Challenge and open a Friends or Community room.\n3. Your boss goes to everyone in the room by itself. Press " + Plugin.BossMultiplayerKey.Value + " there to manage it.", Color.white, 16, wrap: true);
            y += 130;
            var picked = LobbySync.Pending;
            EditorGui.Text(new Rect(x, y, w, 26), picked != null ? $"Hosting with: {picked.Name}" : "No boss picked yet", picked != null ? Green : Dim, 16);
            y += 38;
            var canGo = MainMenuSeen.OnMainMenu && SafeRead.IsObjectOf<UI_MainMenu02>(MainMenuSeen.Instance);
            if (EditorGui.Button(new Rect(x, y, w, 60), "Go to Challenge", "Open the game's Challenge screen", color: canGo ? Theme.Pink : RowHover))
            {
                if (!canGo) { Say("Go back to the main menu first.", true); return; }
                goToChallenge = true;
                return;
            }
            y += 92;
            Theme.Title(new Rect(x, y, w, 40), "JOIN A ROOM", Theme.Cyan, 28);
            y += 52;
            EditorGui.Text(new Rect(x, y, w, 110), "Accept a friend's Steam invite, or find their room under Challenge. The host's boss reaches you by itself. You don't need to download it, but a custom song only plays if you have the same file.", Color.white, 16, wrap: true);
            y += 120;
            EditorGui.Text(new Rect(x, y, w, 90), "Everyone needs Sun Loader to see the boss. Players without it get the stage's normal attacks.", Dim, 14, wrap: true);
        }

        static void DrawRoom(Rect r)
        {
            Theme.Slanted(r, Panel, 0.01f);
            float x = r.x + 22, y = r.y + 18, w = r.width - 44;
            Theme.Title(new Rect(x, y, w, 40), LobbySync.IsHost ? "YOUR ROOM" : "THE ROOM", Theme.Cyan, 28);
            y += 50;
            var boss = LobbySync.Boss;
            EditorGui.Text(new Rect(x, y, w, 26), "Boss: " + (boss?.Name ?? "none, normal levels"), boss != null ? Green : Dim, 18);
            y += 30;
            if (boss != null)
            {
                EditorGui.Text(new Rect(x, y, w, 24), $"on {boss.Stage}  ·  {boss.Attacks} attacks", Dim, 14);
                y += 30;
                if (LobbySync.MissingSong != null)
                {
                    EditorGui.Text(new Rect(x, y, w, 46), $"You don't have its song ({LobbySync.MissingSong}), so you'll hear the stage's song.", Orange, 14, wrap: true);
                    y += 50;
                }
            }
            if (LobbySync.IsHost && boss != null)
            {
                if (EditorGui.Button(new Rect(x, y, w, 40), LobbySync.AlwaysPlay ? "Every round plays this boss" : "Players vote: boss can lose", "Choose whether the boss always wins the song vote", on: LobbySync.AlwaysPlay))
                    LobbySync.SetAlwaysPlay(!LobbySync.AlwaysPlay);
                y += 48;
                if (EditorGui.Button(new Rect(x, y, w, 40), "Stop using this boss", "Go back to the normal levels for everyone", color: RedBtn))
                {
                    LobbySync.Stop();
                    Say("The room plays the normal levels again");
                }
                y += 48;
            }
            else if (boss != null)
            {
                EditorGui.Text(new Rect(x, y, w, 50), LobbySync.AlwaysPlay ? "Every round plays this boss." : $"If the song vote shows {boss.Stage}, that's the boss.", Color.white, 15, wrap: true);
                y += 54;
            }
            y += 12;
            Theme.Title(new Rect(x, y, w, 34), $"PLAYERS ({Math.Max(LobbySync.Players, LobbySync.Members.Count)})", Color.white, 22);
            y += 42;
            foreach (var name in LobbySync.Members.Take(8))
            {
                EditorGui.Text(new Rect(x, y, w, 26), name, Color.white, 16);
                y += 28;
            }
            if (LobbySync.IsHost && EditorGui.Button(new Rect(x, r.yMax - 66, w, 46), "Invite friends", "Open Steam's invite window", color: GreenBtn))
                LobbySync.InviteFriends();
        }

        // ---------- right side ----------

        static void DrawPublic(Rect r)
        {
            Theme.Title(new Rect(r.x, r.y + 60, r.width, 50), "PUBLIC ROOM", Color.white, 34);
            EditorGui.Text(new Rect(r.x, r.y + 120, r.width, 80), "Public rooms play the normal levels, because most players here don't have Sun Loader. Open a Friends or Community room to play a custom boss.", Dim, 16, wrap: true);
        }

        static void DrawGuest(Rect r)
        {
            var boss = LobbySync.Boss;
            Theme.Title(new Rect(r.x, r.y + 60, r.width, 50), boss != null ? boss.Name : "WAITING FOR THE HOST", Color.white, 34);
            EditorGui.Text(new Rect(r.x, r.y + 120, r.width, 60), boss != null
                ? "You have this boss and you're ready. It goes away when you leave the room."
                : !Plugin.LobbyBosses.Value ? "PlayLobbyBosses is off in Sun Loader's settings, so you'll get the normal levels."
                : "The host hasn't picked a custom boss, so the room plays the normal levels.", Dim, 16, wrap: true);
        }

        static void DrawBosses(Rect r)
        {
            var e = Event.current;
            Theme.Title(new Rect(r.x, r.y, r.width, 40), "YOUR BOSSES", Color.white, 28);
            var list = new Rect(r.x, r.y + 50, r.width, r.height - 50);
            if (bosses.Count == 0)
            {
                EditorGui.Text(new Rect(list.x, list.y + 10, list.width, 60), "No bosses yet. Make or download one in the Bosses menu (" + Plugin.BossMenuKey.Value + ").", Dim, 16, wrap: true);
                return;
            }
            if (e.type == EventType.ScrollWheel && list.Contains(e.mousePosition)) { scroll = Mathf.Max(0, scroll + e.delta.y * 20); e.Use(); }
            const float h = 84;
            scroll = Mathf.Min(scroll, Mathf.Max(0, bosses.Count * h - list.height));
            float y = list.y - scroll;
            foreach (var b in bosses)
            {
                if (y >= list.y - 2 && y + h <= list.yMax + 2) BossRow(b, new Rect(r.x, y, r.width, h - 8));
                y += h;
            }
        }

        static void BossRow(BossProject b, Rect row)
        {
            var hosting = LobbySync.InLobby;
            var chosen = hosting ? LobbySync.Boss != null && LobbySync.BossName == b.Name : LobbySync.Pending?.Path == b.Path;
            var problem = LobbySync.Problem(b);
            Theme.Slanted(row, row.Contains(Event.current.mousePosition) ? RowHover : Row, 0.04f);
            EditorGui.Fill(new Rect(row.x, row.y, 6, row.height), problem != null ? Orange : chosen ? Theme.Cyan : Theme.Pink);
            Theme.Title(new Rect(row.x + 20, row.y + 4, row.width - 220, 38), b.Name, Color.white, 22);
            EditorGui.Text(new Rect(row.x + 20, row.y + 44, row.width - 220, 24), problem ?? $"on {b.Stage}  ·  {b.Attacks} attacks  ·  {b.Music ?? "stage's own song"}", problem != null ? Orange : Dim, 13);
            var label = chosen ? (hosting ? "In this room" : "Picked") : (hosting ? "Use in room" : "Pick");
            if (EditorGui.Button(new Rect(row.xMax - 180, row.y + 16, 165, 44), label, on: chosen, color: problem == null ? GreenBtn : RowHover)) Choose(b, chosen);
        }

        static void Choose(BossProject b, bool chosen)
        {
            if (!LobbySync.InLobby)
            {
                if (chosen) { LobbySync.Pending = null; return; }
                var problem = LobbySync.CanSend(b);
                if (problem != null) { Say(problem, true); return; }
                LobbySync.Pending = b;
                Say($"'{b.Name}' will be sent to your room when you host one.");
                return;
            }
            if (chosen) return;
            string err;
            try { err = LobbySync.Use(b); } catch (Exception e) { err = e.Message; }
            if (err != null) Say(err, true);
            else Say($"'{b.Name}' is the room's boss." + (b.Music != null ? " Players without the same song file hear the stage's song." : ""));
        }
    }
}
