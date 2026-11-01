using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SunLoader
{
    // The Bosses menu: make, play, edit and manage your own bosses, without going through a
    // playlist. Opened by the BOSSES button on the main menu or its key (F4).
    static class BossMenu
    {
        public static bool Open;
        enum Screen { List, New }
        static Screen screen;
        static List<BossProject> bosses = new();
        static float scroll, stageScroll;
        static string message;
        static bool messageIsError;
        static float messageUntil;

        // The one text field being typed in: "new" (new boss name) or "rename".
        static string focus, focusText = "";
        static BossProject renaming, deleting;

        // New boss form.
        static string newName = "", newStage;
        static bool newWithOriginal;

        static readonly Color Bg = new(0.02f, 0.02f, 0.03f, 0.97f), Row = new(0.09f, 0.09f, 0.12f, 1f), RowHover = new(0.14f, 0.1f, 0.13f);
        static readonly Color Pink = new(1f, 0.17f, 0.43f), PinkDim = new(0.55f, 0.06f, 0.24f), Green = new(0.3f, 0.9f, 0.5f), Orange = new(1f, 0.6f, 0.2f), Dim = new(0.6f, 0.6f, 0.7f), Cyan = new(0.17f, 0.91f, 1f);
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
            focus = null; renaming = deleting = null;
            Refresh();
        }

        static void Refresh()
        {
            try { bosses = CustomBosses.List(); }
            catch (Exception e) { bosses = new(); Say("Couldn't read the bosses folder: " + e.Message, true); }
        }

        static void Say(string msg, bool error = false) { message = msg; messageIsError = error; messageUntil = Time.realtimeSinceStartup + 7; }

        // Play/Edit start a level, which only works from a menu, not in the middle of one.
        static bool CanLaunch => MainMenuSeen.OnMainMenu || PlaylistMenuSeen.OnPlaylistMenu;

        public static void OnGUI(float scale, float vw)
        {
            var e = Event.current;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));

            if (!Open && !ModMenu.Open && MainMenuSeen.OnMainMenu) MainMenuButton(e);
            if (!Open) return;
            if (e.type == EventType.KeyDown && Keys(e)) return;

            EditorGui.Fill(new Rect(0, 0, vw, 1080), new Color(0, 0, 0, 0.75f));
            var r = new Rect(vw / 2 - 600, 60, 1200, 960);
            EditorGui.Fill(r, Bg);
            Theme.Stripes(new Rect(r.x, r.y, r.width, 96), new Color(Pink.r, Pink.g, Pink.b, 0.1f));
            EditorGui.Fill(new Rect(r.x, r.y + 96, r.width, 4), Pink);
            EditorGui.Fill(new Rect(r.x, r.yMax - 4, r.width, 4), Pink);
            float x = r.x + 30, y = r.y + 20, w = r.width - 60;
            Theme.Title(new Rect(x, y - 2, 560, 60), "SUN LOADER", Pink, 46);
            EditorGui.Text(new Rect(x + 4, y + 54, 400, 24), screen == Screen.New ? "BOSSES  ›  NEW BOSS" : "BOSSES", Color.white, 14);
            if (EditorGui.Button(new Rect(r.xMax - 150, y + 12, 120, 36), $"Close ({Plugin.BossMenuKey.Value})")) { Toggle(); return; }
            y += 96;

            if (Time.realtimeSinceStartup < messageUntil) EditorGui.Text(new Rect(x, y, w, 26), message, messageIsError ? Orange : Green);
            y += 30;

            if (screen == Screen.New) DrawNew(r, x, y, w);
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

        // ---------- list ----------

        static void DrawList(Rect r, float x, float y, float w)
        {
            var e = Event.current;
            if (EditorGui.Button(new Rect(x, y, 220, 44), "+ New boss", color: Pink)) { StartNew(); return; }
            EditorGui.Text(new Rect(x + 240, y + 4, w - 240, 40),
                "Your own bosses, separate from the game's levels. Each one plays on a stage (a level's scene) with any song you pick.", Dim, 13, wrap: true);
            y += 60;

            var listRect = new Rect(x, y, w, r.yMax - y - 76);
            if (e.type == EventType.ScrollWheel && listRect.Contains(e.mousePosition)) { scroll = Mathf.Max(0, scroll + e.delta.y * 20); e.Use(); }
            float ly = listRect.y - scroll;

            if (bosses.Count == 0)
            {
                Theme.Title(new Rect(x, ly + 60, w, 50), "NO BOSSES YET", Color.white, 30);
                EditorGui.Text(new Rect(x, ly + 112, w, 26), "Press NEW BOSS, pick a stage, and the editor opens on it. Your bosses show up here.", Dim, 15);
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
            EditorGui.Fill(new Rect(row.x, row.y, 6, row.height), b.Error != null ? Orange : Pink);

            float tx = row.x + 22, tw = row.width - 560;
            if (renaming == b)
            {
                Field(new Rect(tx, row.y + 8, tw, 34), "rename", b.Name);
                EditorGui.Text(new Rect(tx, row.y + 48, tw, 22), "Type a new name, Enter to save, Esc to cancel", Cyan, 12);
            }
            else
            {
                Theme.Title(new Rect(tx, row.y + 2, tw, 40), b.Name, Color.white, 24);
                var stage = CustomBosses.Stage(b.Stage);
                var info = b.Error ?? $"on {b.Stage}" + (stage == null && CustomBosses.Stages().Count > 0 ? " (missing!)" : "")
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

            float bx = row.xMax - 540, by = row.y + 8;
            if (EditorGui.Button(new Rect(bx, by, 130, 36), "Play", "Play this boss", color: CanLaunch && b.Error == null ? Pink : RowHover)) Launch(b, false);
            if (EditorGui.Button(new Rect(bx + 136, by, 130, 36), "Edit", "Start this boss with the editor open", color: CanLaunch && b.Error == null ? BlueBtn : RowHover)) Launch(b, true);
            if (EditorGui.Button(new Rect(bx + 272, by, 254, 36), "Song: " + (b.Music ?? "stage's own"), "Change this boss's song. Add .wav files to the music folder.")) CycleMusic(b);
            by += 42;
            if (EditorGui.Button(new Rect(bx, by, 130, 30), "Rename")) { renaming = b; focus = "rename"; focusText = b.Name; deleting = null; }
            if (EditorGui.Button(new Rect(bx + 136, by, 130, 30), "Duplicate"))
            {
                try { CustomBosses.Duplicate(b); Say($"Made a copy of '{b.Name}'"); } catch (Exception ex) { Say("Couldn't copy it: " + ex.Message, true); }
                Refresh();
            }
            if (EditorGui.Button(new Rect(bx + 272, by, 130, 30), "Delete", color: RedBtn)) { deleting = b; renaming = null; focus = null; }
        }

        static void Launch(BossProject b, bool edit)
        {
            if (b.Error != null) { Say(b.Error, true); return; }
            if (!CanLaunch) { Say("Go back to the main menu first, then play the boss from there.", true); return; }
            var err = CustomBosses.Launch(b, edit);
            if (err != null) { Say(err, true); return; }
            Open = false; focus = null;
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
        static string Field(Rect r, string id, string value)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition)) { if (focus != id) { focus = id; focusText = value; } e.Use(); }
            var focused = focus == id;
            EditorGui.Fill(r, Color.black);
            EditorGui.Outline(r, focused ? Cyan : new Color(0.25f, 0.25f, 0.3f), 2);
            var caret = focused && (int)(Time.realtimeSinceStartup * 2) % 2 == 0 ? "|" : "";
            EditorGui.Text(new Rect(r.x + 10, r.y + (r.height - 26) / 2, r.width - 16, 26), (focused ? focusText : value) + caret, Color.white, 17);
            return focused ? focusText : value;
        }

        // Typing into the focused field, plus Esc. Returns true if the event was used.
        static bool Keys(Event e)
        {
            if (focus != null)
            {
                if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter) { Submit(); e.Use(); return true; }
                if (e.keyCode == KeyCode.Escape) { CancelTyping(); e.Use(); return true; }
                if (e.keyCode == KeyCode.Backspace) { if (focusText.Length > 0) focusText = focusText[..^1]; }
                else if (e.character != '\0' && !char.IsControl(e.character) && focusText.Length < 60) focusText += e.character;
                e.Use();
                return true;
            }
            if (e.keyCode == KeyCode.Escape)
            {
                if (screen == Screen.New) ShowList(); else Toggle();
                e.Use();
                return true;
            }
            return false;
        }

        static void Submit()
        {
            if (focus == "new") { newName = focusText; focus = null; return; }
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
            if (focus == "new") focusText = newName;
            renaming = null; focus = null;
        }

        static void OpenFolder(string dir)
        {
            try { System.IO.Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", dir); }
            catch (Exception e) { Say("Couldn't open the folder: " + e.Message, true); }
        }
    }
}
