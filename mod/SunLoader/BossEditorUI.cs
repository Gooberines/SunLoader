using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace SunLoader
{
    // Unity calls these. Everything else lives in EditorGui, because Il2CppInterop has to
    // register every method on an injected MonoBehaviour and many signatures can't be.
    public class BossEditorUI : MonoBehaviour
    {
        public BossEditorUI(IntPtr ptr) : base(ptr) { }
        public void Update() => EditorGui.Update();
        public void OnGUI()
        {
            EditorGui.TryBiggerFont();   // sets the game font and default text size for this frame
            EditorGui.OnGUI();
            try { EditorGui.TryBiggerFont(); ModMenu.OnGUI(EditorGui.Scale, EditorGui.VW); }
            catch (Exception e) { Plugin.Logger.LogError("Mod menu: " + e); ModMenu.Open = false; }
            try { EditorGui.TryBiggerFont(); BossMenu.OnGUI(EditorGui.Scale, EditorGui.VW); }
            catch (Exception e) { Plugin.Logger.LogError("Bosses menu: " + e); BossMenu.Open = false; }
        }
    }

    // The in-game boss editor: an overlay drawn with Unity's immediate-mode GUI.
    // Layout is in a virtual 1080-pixel-tall space and scaled to the real screen.
    // Only DrawTexture and Label are used: the game's build strips Unity's text box, and
    // other controls may be stripped too, so buttons and fields are drawn by hand.
    static class EditorGui
    {
        const float VH = 1080, Top = 48, LeftW = 290, RightW = 370, TimelineH = 240, TipH = 28;
        const float Label = 170, Ruler = 22, RowH = 18;
        const float GameW = 1280, GameH = 720;   // the coordinates x/y use

        static readonly Color Bg = new(0.02f, 0.02f, 0.03f, 0.94f), Bg2 = new(0.06f, 0.06f, 0.08f, 0.95f), Card = new(0.1f, 0.1f, 0.13f, 1f);
        static readonly Color Pink = new(1f, 0.17f, 0.43f), Cyan = new(0.17f, 0.91f, 1f), Yellow = new(1f, 0.82f, 0.17f), Orange = new(1f, 0.6f, 0.2f);
        static readonly Color Green = new(0.3f, 0.9f, 0.5f), Dim = new(0.6f, 0.6f, 0.7f), Grid = new(1, 1, 1, 0.06f), GridBar = new(1, 1, 1, 0.14f);
        static readonly Color ButtonBg = new(0.14f, 0.14f, 0.18f), ButtonHover = new(1f, 0.32f, 0.56f), FieldBg = new(0f, 0f, 0f);
        static readonly float[] SnapBeats = { 1, 0.5f, 0.25f, 0.125f, 0 };
        static readonly string[] SnapNames = { "1 beat", "1/2 beat", "1/4 beat", "1/8 beat", "off" };

        static bool open, playing, showHelp, helpShownOnce, noDamageToggle;
        static float playhead;
        static string tool = "Beam", search = "", status = "", tip, hoverType;
        static float statusUntil;
        static int snapIndex = 2, paletteScroll;
        static float zoom = 0.1f, scrollMs = -500, rowScroll;
        static readonly HashSet<EditSpawn> selected = new();
        static readonly List<List<EditSpawn>> undo = new();
        static List<EditSpawn> clipboard = new();
        static List<string> rows = new();
        static readonly HashSet<string> collapsed = new() { AttackInfo.LevelSpecific };

        // Text input: the focused field's id and what's been typed into it so far.
        static string focus, focusText = "";
        static EditSpawn focusOwner;
        static readonly HashSet<string> loggedErrors = new();
        static float repEvery = 1, repCount = 4, repDx, repDy, repDr;

        enum Drag { None, Scrub, MoveTime, Box, MoveStage, Rotate }
        static Drag drag;
        static Vector2 dragStart, dragNow;
        static float dragAnchorTime;
        static Dictionary<EditSpawn, float> dragOrigTimes;
        static EditSpawn dragAnchor;
        static bool dragMoved;

        // Preview: spawns one attack mid-screen, plays a couple of seconds, can grab a picture,
        // then rewinds. "Capture all" runs it for a queue of attack types.
        class PreviewRun { public string Type; public bool Capture, Captured; public float CaptureAt, EndAt, From; }
        static PreviewRun preview;
        static readonly Queue<string> captureQueue = new();
        static int captureTotal;

        internal static float VW => Screen.width / Scale;
        internal static float Scale => Screen.height / VH;
        static float BeatMs => 60000f / Math.Max(1, EditorSession.Bpm);

        static Rect TopRect => new(0, 0, VW, Top);
        static Rect LeftRect => new(0, Top, LeftW, VH - Top - TimelineH);
        static Rect RightRect => new(VW - RightW, Top, RightW, VH - Top - TimelineH);
        static Rect TimelineRect => new(0, VH - TimelineH, VW, TimelineH);
        static Rect TipRect => new(LeftW, VH - TimelineH - TipH, VW - LeftW - RightW, TipH);
        static Rect GameRect => new(LeftW, Top, VW - LeftW - RightW, VH - Top - TimelineH - TipH);

        // ---------- open / close / play ----------

        public static void Update()
        {
            try
            {
                Music.Update();
                Theme.Tick();
                if (open && playing) Theme.BeatFromMusic(playhead, EditorSession.Bpm);
                ModLoader.Update();
                if (Input.GetKeyDown(Plugin.ModMenuKey.Value)) ModMenu.Toggle();
                if (Input.GetKeyDown(Plugin.BossMenuKey.Value)) BossMenu.Toggle();
                CustomBosses.Update();
                LobbySync.Update();
                Share.Update();
                if ((open || NoDamage.On) && CustomBosses.Active != null) CustomBosses.RunTainted = true;
                NoDamage.On = (noDamageToggle || preview != null) && EditorSession.InLevel;
                if (Input.GetKeyDown(Plugin.EditorKey.Value)) Toggle();
                // A boss started with Edit from the Bosses menu opens the editor once its level is up.
                if (!open && CustomBosses.EditOnStart && EditorSession.InLevel && EditorSession.IsCustomBoss) { CustomBosses.EditOnStart = false; Toggle(); }
                if (!open) return;
                if (!EditorSession.InLevel) { Close(false); return; }
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                if (playing)
                {
                    var ms = EditorSession.MusicMs();
                    if (ms >= 0) playhead = ms;
                    var x = MsToX(playhead);
                    if (preview == null && (x > VW - 80 || x < Label)) scrollMs = playhead - (VW - Label) * 0.15f / zoom;
                }
                if (preview != null) UpdatePreview();
            }
            catch (Exception e) { Fail("update", e); }
        }

        static bool musicPreloaded;

        static void Fail(string where, Exception e)
        {
            if (loggedErrors.Add(where + e.Message)) Plugin.Logger.LogError($"Editor {where}: {e}");
            preview = null; captureQueue.Clear(); Pictures.Trying(null);
            try { Close(true); } catch { open = false; Time.timeScale = 1; }
            Say("The editor hit an error and closed; see LogOutput.log");
        }

        static void Toggle()
        {
            if (open) { Close(true); return; }
            // The last level's spawner can outlive the level, so the menus count as "not in a level" too.
            if (!EditorSession.InLevel || MainMenuSeen.OnMainMenu || PlaylistMenuSeen.OnPlaylistMenu)
            { Say("Start a level first, then press " + Plugin.EditorKey.Value); return; }
            if (EditorSession.Types.Count == 0) { Say("No attack list yet; play any level once first"); return; }
            if (!musicPreloaded) { musicPreloaded = true; Music.Preload(); }
            open = true;
            playing = false;
            EditorSession.SetPaused(true);
            playhead = Math.Max(0, EditorSession.MusicMs());
            scrollMs = playhead - 3000;
            if (!helpShownOnce) { showHelp = helpShownOnce = true; }
            Say(EditorSession.IsCustomBoss ? $"Editing your boss '{EditorSession.DisplayName}' on {EditorSession.Level}"
                : $"Editing {EditorSession.Level}" + (EditorSession.HasCustomFile ? " (your saved boss)" : " (the original level)"));
        }

        static void Close(bool resume)
        {
            open = false;
            focus = null;
            if (!resume) return;
            if (EditorSession.AppliedDirty) Apply(playhead);
            EditorSession.SetPaused(false);
            playing = false;
        }

        static void Play()
        {
            if (EditorSession.AppliedDirty) Apply(playhead);
            else EditorSession.Seek(playhead);
            EditorSession.SetPaused(false);
            playing = true;
        }

        // Puts the editor's attacks into the game. A boss with no checkpoint isn't loaded (the
        // level would crash), so the game keeps playing what it had.
        static void Apply(float ms)
        {
            if (!EditorSession.ApplyToGame(ms) && NoCheckpoints) Say("Add a CheckPoint first: every level needs one (its last checkpoint ends the level)");
        }

        static bool NoCheckpoints => !EditorSession.Spawns.Any(s => s.Type == "CheckPoint");

        static void Pause()
        {
            EditorSession.SetPaused(true);
            playing = false;
            var ms = EditorSession.MusicMs();
            if (ms >= 0) playhead = ms;
        }

        // A message shown at the top of the screen, also when the editor is closed.
        internal static void Toast(string msg) { Say(msg); statusUntil = Time.realtimeSinceStartup + 10; }

        static void Say(string msg) { status = msg; statusUntil = Time.realtimeSinceStartup + 6; Plugin.Logger.LogInfo("Editor: " + msg); }

        internal static void TryBiggerFont() => Theme.Apply();

        // ---------- preview & pictures ----------

        static void StartPreview(string type, bool capture)
        {
            if (preview != null || !EditorSession.InLevel) return;
            var warn = EditorSession.TypeInfo(type)?.WarningDefault ?? 0;
            var from = playhead;
            var temp = Make(type, from + 300 + warn, GameW / 2, GameH / 2);
            if (capture) Pictures.Trying(type);
            // Apply the level with the extra attack, then take it back out of the editor's list.
            EditorSession.Spawns.Add(temp);
            var ok = EditorSession.ApplyToGame(from);
            EditorSession.Spawns.Remove(temp);
            EditorSession.AppliedDirty = true;
            if (!ok) { Pictures.Trying(null); Say(NoCheckpoints ? "Couldn't preview: add a CheckPoint first" : "Couldn't preview: the level isn't ready"); return; }
            preview = new PreviewRun { Type = type, Capture = capture, From = from, CaptureAt = temp.Time + 450, EndAt = temp.Time + 2200 };
            EditorSession.SetPaused(false);
            playing = true;
            Say(capture ? $"Capturing {AttackInfo.Friendly(type)} ({captureTotal - captureQueue.Count}/{captureTotal})..." : $"Previewing {AttackInfo.Friendly(type)}...");
        }

        static void UpdatePreview()
        {
            var ms = EditorSession.MusicMs();
            var done = ms >= preview.EndAt || (preview.Capture && preview.Captured && ms >= preview.CaptureAt + 200);
            if (!done) return;
            var from = preview.From;
            if (preview.Capture) Pictures.Trying(null);
            preview = null;
            Pause();
            EditorSession.ApplyToGame(from);   // clears the preview attack and rewinds
            playhead = from;
            if (captureQueue.Count > 0) StartPreview(captureQueue.Dequeue(), true);
            else if (captureTotal > 0) { Say($"Captured pictures for {captureTotal} attacks"); captureTotal = 0; }
        }

        static void CaptureAll()
        {
            captureQueue.Clear();
            foreach (var t in EditorSession.Types)
            {
                var info = AttackInfo.Get(t.Type);
                if (info.Category == AttackInfo.LevelSpecific || info.Shape == Shape.Effect || Pictures.Crashed(t.Type) || Pictures.Has(t.Type)) continue;
                captureQueue.Enqueue(t.Type);
            }
            captureTotal = captureQueue.Count;
            if (captureTotal == 0) { Say("Every safe attack already has a picture"); return; }
            if (playing) Pause();
            StartPreview(captureQueue.Dequeue(), true);
        }

        // ---------- editing helpers ----------

        static void Snapshot()
        {
            undo.Add(EditorSession.Spawns.Select(s => s.Clone()).ToList());
            if (undo.Count > 100) undo.RemoveAt(0);
        }

        static void Changed() { EditorSession.Dirty = EditorSession.AppliedDirty = true; }

        static void Undo()
        {
            if (undo.Count == 0) { Say("Nothing to undo"); return; }
            EditorSession.Spawns = undo[^1];
            undo.RemoveAt(undo.Count - 1);
            selected.Clear(); Changed();
        }

        static void Save()
        {
            EditorSession.Save();
            Music.Get(EditorSession.MusicFile);
            Say(EditorSession.IsCustomBoss ? $"Saved your boss '{EditorSession.DisplayName}' (Bosses menu: {Plugin.BossMenuKey.Value})"
                : $"Saved BepInEx\\SunLoader\\{EditorSession.Level}.json");
        }

        static float Snap(float ms)
        {
            var step = SnapBeats[snapIndex] * BeatMs;
            return step <= 0 ? Mathf.Round(ms) : Mathf.Round(Mathf.Round(ms / step) * step);
        }

        static EditSpawn Make(string type, float time, float? x = null, float? y = null)
        {
            var t = EditorSession.TypeInfo(type);
            var s = new EditSpawn { Type = type, Time = Mathf.Max(0, Mathf.Round(time)), Warning = t?.WarningDefault ?? 0 };
            if (t == null || t.UsesPosition) { s.X = Mathf.Round(x ?? GameW / 2); s.Y = Mathf.Round(y ?? GameH / 2); }
            return s;
        }

        static void Add(EditSpawn s)
        {
            EditorSession.Spawns.Add(s);
            selected.Clear(); selected.Add(s);
            Changed();
        }

        static void DeleteSelected()
        {
            if (selected.Count == 0) return;
            Snapshot();
            EditorSession.Spawns.RemoveAll(selected.Contains);
            selected.Clear(); Changed();
        }

        static void Duplicate(float shiftMs)
        {
            if (selected.Count == 0) return;
            Snapshot();
            var copies = selected.Select(s => { var c = s.Clone(); c.Time += shiftMs; return c; }).ToList();
            EditorSession.Spawns.AddRange(copies);
            selected.Clear(); selected.UnionWith(copies); Changed();
        }

        static void Repeat()
        {
            if (selected.Count == 0) return;
            Snapshot();
            var src = selected.ToList();
            int n = Mathf.Clamp((int)repCount, 1, 500);
            for (int i = 1; i <= n; i++)
                foreach (var s in src)
                {
                    var c = s.Clone();
                    c.Time = Mathf.Round(s.Time + repEvery * BeatMs * i);
                    if (c.X.HasValue) { c.X = Wrap(c.X.Value + repDx * i, GameW); c.Y = Wrap((c.Y ?? 0) + repDy * i, GameH); }
                    if (repDr != 0 && CanRotate(c)) SetAngle(c, Angle(s) + repDr * i);
                    EditorSession.Spawns.Add(c);
                }
            Changed();
            Say($"Added {n * src.Count} attacks");
        }

        static float Wrap(float v, float max) => ((Mathf.Round(v) % max) + max) % max;

        static void Copy()
        {
            if (selected.Count == 0) return;
            var t0 = selected.Min(s => s.Time);
            clipboard = selected.Select(s => { var c = s.Clone(); c.Time -= t0; return c; }).ToList();
            Say($"Copied {clipboard.Count}. Move the playhead and press Ctrl+V to paste.");
        }

        static void Paste()
        {
            if (clipboard.Count == 0) return;
            Snapshot();
            var at = Snap(playhead);
            var copies = clipboard.Select(s => { var c = s.Clone(); c.Time += at; return c; }).ToList();
            EditorSession.Spawns.AddRange(copies);
            selected.Clear(); selected.UnionWith(copies); Changed();
        }

        static void Shift(float ms)
        {
            Snapshot();
            foreach (var s in selected) s.Time = Mathf.Max(0, Mathf.Round(s.Time + ms));
            Changed();
        }

        // ---------- rotation ----------
        // Attacks turn through their own settings: rotOffset (angle) or direction (travel
        // angle). Edge attacks without either turn by switching modelSide in 90° steps.

        static readonly string[] Sides = { "right", "down", "left", "up" };

        static string AngleKey(EditSpawn s)
        {
            var p = EditorSession.TypeInfo(s.Type)?.Params;
            if (p == null) return s.Params.ContainsKey("rotOffset") ? "rotOffset" : s.Params.ContainsKey("direction") ? "direction" : null;
            if (p.Any(kv => kv.Key == "rotOffset")) return "rotOffset";
            if (p.Any(kv => kv.Key == "direction")) return "direction";
            if (p.Any(kv => kv.Key == "modelSide")) return "modelSide";
            return null;
        }

        static bool CanRotate(EditSpawn s) => AngleKey(s) != null;

        // Sets the angle, keeping any random part of an expression like "-30+random(60)".
        static void SetAngle(EditSpawn s, float deg)
        {
            var key = AngleKey(s);
            if (key == null) return;
            deg = Mathf.Round(((deg % 360) + 360) % 360);
            if (key == "modelSide") { s.Params[key] = Sides[(int)Mathf.Round(deg / 90) % 4]; return; }
            var cur = s.Params.GetValueOrDefault(key) ?? "";
            var lead = Regex.Match(cur, @"^\s*-?\d+(\.\d+)?");
            var text = deg.ToString(CultureInfo.InvariantCulture);
            s.Params[key] = lead.Success ? text + cur[lead.Length..] : cur.Length > 0 ? text + "+" + cur : text;
        }

        static float Angle(EditSpawn s)
        {
            var key = AngleKey(s);
            if (key == "modelSide") { var i = Array.IndexOf(Sides, Side(s)); return i < 0 ? 0 : i * 90; }
            return key == null ? 0 : Num(s, key, 0);
        }

        static void RotateSelected(float deg)
        {
            var list = selected.Where(CanRotate).ToList();
            if (list.Count == 0) { Say(selected.Count == 0 ? "Select an attack to rotate it" : "This attack type can't rotate"); return; }
            Snapshot();
            foreach (var s in list) SetAngle(s, Angle(s) + (AngleKey(s) == "modelSide" ? Math.Sign(deg) * 90 : deg));
            focus = null; Changed();
            var one = list[0];
            Say($"{AttackInfo.Friendly(one.Type)}: {AngleKey(one)} = {one.Params[AngleKey(one)]}");
        }

        static Vector2 HandlePos(EditSpawn s)
        {
            var a = Angle(s) * Mathf.Deg2Rad;
            return GameToScreen(s.X.Value, s.Y ?? 0) + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 70;
        }

        static EditSpawn RotateHandleTarget()
        {
            if (selected.Count != 1) return null;
            var s = selected.First();
            return s.X.HasValue && CanRotate(s) ? s : null;
        }

        static void CycleMusic(int dir)
        {
            var options = new List<string> { null };
            options.AddRange(Music.Files());
            var i = Math.Max(0, options.IndexOf(EditorSession.MusicFile));
            EditorSession.MusicFile = options[(i + dir + options.Count) % options.Count];
            EditorSession.Dirty = true;
            Music.Get(EditorSession.MusicFile);
            Say(options.Count == 1 ? "No songs yet: put .wav or .ogg files in BepInEx\\SunLoader\\music"
                : "Music set. Save, then restart the level to hear it.");
        }

        static void OpenFolder(string dir)
        {
            try { System.IO.Directory.CreateDirectory(dir); System.Diagnostics.Process.Start("explorer.exe", dir); }
            catch (Exception e) { Say("Couldn't open the folder: " + e.Message); }
        }

        // ---------- drawing basics ----------

        public static void OnGUI()
        {
            var e = Event.current;
            if (!open)
            {
                if (NoDamage.On) Text(new Rect(20, 50, 600, 30), "NO DAMAGE (boss editor test mode) - turn off in the editor", Green);
                if (Time.realtimeSinceStartup < statusUntil) Text(new Rect(20, 20, 900, 30), status, Yellow);
                return;
            }
            try
            {
                // Grab the picture before anything of ours is drawn over the game.
                if (e.type == EventType.Repaint && preview is { Capture: true, Captured: false } && EditorSession.MusicMs() >= preview.CaptureAt)
                {
                    Pictures.Capture(preview.Type);
                    preview.Captured = true;
                }

                TryBiggerFont();   // GUI.skin can only be touched inside OnGUI
                GUI.matrix = Matrix4x4.Scale(new Vector3(Scale, Scale, 1));
                tip = null; hoverType = null;
                if (e.type == EventType.KeyDown) Keys(e);
                if (showHelp) { DrawHelp(); return; }
                if (e.type == EventType.MouseDown) focus = null;   // a field drawn below takes it back if clicked
                if (preview == null && (e.isMouse || e.type == EventType.ScrollWheel)) Mouse(e);

                DrawGameOverlay();
                DrawTimeline();
                DrawPalette();
                DrawRightPanel();
                DrawTopBar();
                DrawTipBar();
            }
            catch (Exception ex) { Fail("GUI", ex); }
        }

        internal static void Fill(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = old;
        }

        static void Tex(Rect r, Texture t, Color c)
        {
            if (Event.current.type != EventType.Repaint || t == null) return;
            var old = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, t);
            GUI.color = old;
        }

        static bool labelsBroken;
        // size 0 keeps the current size; wrap is for paragraphs (labels otherwise stay on one clipped line).
        internal static void Text(Rect r, string s, Color c, int size = 0, bool wrap = false)
        {
            if (labelsBroken) return;
            var old = GUI.color; GUI.color = c;
            GUIStyle label = null;
            int oldSize = 0; bool oldWrap = false;
            try
            {
                label = GUI.skin.label;
                oldSize = label.fontSize; oldWrap = label.wordWrap;
                if (size > 0) label.fontSize = size;
                label.wordWrap = wrap;
                GUI.Label(r, s);
            }
            catch (Exception e) { labelsBroken = true; Plugin.Logger.LogError("Editor can't draw text: " + e.Message); }
            finally { if (label != null) { label.fontSize = oldSize; label.wordWrap = oldWrap; } }
            GUI.color = old;
        }

        internal static void Outline(Rect r, Color c, float t)
        {
            Fill(new Rect(r.x, r.y, r.width, t), c); Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
            Fill(new Rect(r.x, r.y, t, r.height), c); Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        internal static bool Button(Rect r, string s, string hint = null, bool on = false, Color? color = null)
        {
            var e = Event.current;
            var hover = r.Contains(e.mousePosition);
            if (hover && hint != null) tip = hint;
            // Slanted block like the game's menu buttons: pink when picked, grows on hover.
            var block = hover ? new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4) : r;
            Theme.Slanted(block, on ? Pink : hover ? ButtonHover : color ?? ButtonBg, Mathf.Min(0.25f, 8 / Mathf.Max(8, r.height)));
            if (!on && !hover) Fill(new Rect(r.x + 3, r.yMax - 2, r.width - 6, 2), new Color(Pink.r, Pink.g, Pink.b, 0.55f));
            Text(new Rect(r.x + 9, r.y + (r.height - 22) / 2, r.width - 11, 22), s.ToUpperInvariant(), Color.white);
            if (e.type == EventType.MouseDown && e.button == 0 && hover) { e.Use(); return true; }
            return false;
        }

        static string Field(Rect r, string id, string value, Action onFocus = null, string hint = null)
        {
            var e = Event.current;
            if (hint != null && r.Contains(e.mousePosition)) tip = hint;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                focus = id; focusText = value; focusOwner = selected.Count == 1 ? selected.First() : null;
                onFocus?.Invoke();
                e.Use();
            }
            var focused = focus == id;
            Fill(r, FieldBg);
            Outline(r, focused ? Cyan : ButtonBg, 1);
            var caret = focused && (int)(Time.realtimeSinceStartup * 2) % 2 == 0 ? "|" : "";
            Text(new Rect(r.x + 5, r.y + (r.height - 22) / 2, r.width - 8, 22), (focused ? focusText : value) + caret, Color.white);
            return focused ? focusText : value;
        }

        static void NumField(Rect r, string key, float value, Action<float> set, Action changed, string hint = null)
        {
            var text = Field(r, key, value.ToString("0.##", CultureInfo.InvariantCulture), changed != null ? Snapshot : null, hint);
            if (focus != key) return;
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v != value) { set(v); changed?.Invoke(); }
        }

        static string Fmt(float ms) { var s = Math.Max(0, ms) / 1000f; return $"{(int)(s / 60)}:{s % 60:00.000}"; }

        // ---------- top bar ----------

        static void DrawTopBar()
        {
            var r = TopRect; Fill(r, Bg);
            Fill(new Rect(0, r.yMax - 3, VW, 3), Pink);
            float x = 10, y = 9, h = 28;
            Theme.Title(new Rect(x, y - 3, 180, 40), "BOSS EDITOR", Pink, 20);
            Text(new Rect(x + 168, y + 4, 120, h), EditorSession.DisplayName + (EditorSession.Dirty ? " *" : ""), EditorSession.IsCustomBoss ? Cyan : Color.white, 13); x += 295;

            if (Button(new Rect(x, y, 90, h), playing ? "Pause" : "Play", "Play from the yellow line with your changes (Space)")) { if (playing) Pause(); else Play(); }
            x += 95;
            if (Button(new Rect(x, y, 40, h), "|<", "Back to the start")) { if (playing) Pause(); playhead = 0; scrollMs = -500; }
            x += 45;
            Text(new Rect(x, y + 3, 90, h), Fmt(playhead), Yellow); x += 95;
            if (Button(new Rect(x, y, 70, h), "Save", EditorSession.IsCustomBoss ? "Save your boss (Ctrl+S). Play it again from the Bosses menu." : "Save your boss (Ctrl+S). It loads every time you play this level.", color: EditorSession.Dirty ? new Color(0.5f, 0.15f, 0.3f) : null)) Save();
            x += 75;
            if (Button(new Rect(x, y, 70, h), "Undo", "Undo the last change (Ctrl+Z)")) Undo();
            x += 85;

            var music = EditorSession.MusicFile;
            var musicName = music == null ? "Level's own song" : music + (Music.HasFailed(music) ? " (can't load!)" : Music.IsLoading(music) ? " (loading)" : "");
            Text(new Rect(x, y + 3, 50, h), "Music", Dim); x += 50;
            if (Button(new Rect(x, y, 26, h), "<", "Previous song")) CycleMusic(-1);
            x += 28;
            Fill(new Rect(x, y, 200, h), FieldBg);
            Text(new Rect(x + 6, y + 3, 194, h), musicName, music != null && Music.HasFailed(music) ? Orange : Color.white);
            if (new Rect(x, y, 200, h).Contains(Event.current.mousePosition)) tip = "Song for this boss. Put .wav (best) or .ogg files in BepInEx\\SunLoader\\music. Restart the level after saving to hear it.";
            x += 202;
            if (Button(new Rect(x, y, 26, h), ">", "Next song")) CycleMusic(1);
            x += 28;
            if (Button(new Rect(x, y, 70, h), "Folder", "Open the music folder")) OpenFolder(Music.Dir);
            x += 85;

            Text(new Rect(x, y + 3, 40, h), "BPM", Dim); x += 38;
            NumField(new Rect(x, y, 60, h), "bpm", EditorSession.Bpm, v => EditorSession.Bpm = Mathf.Clamp(v, 20, 400), null, "The song's beats per minute, so the grid and snapping line up with the music");
            x += 68;
            if (Button(new Rect(x, y, 110, h), "Snap: " + SnapNames[snapIndex], "What new and moved attacks snap to")) snapIndex = (snapIndex + 1) % SnapBeats.Length;
            x += 118;
            if (Button(new Rect(x, y, 150, h), noDamageToggle ? "No damage: ON" : "No damage: OFF", "Turn off getting hit while you test. Stays on after closing the editor until you turn it off.", color: noDamageToggle ? new Color(0.15f, 0.45f, 0.25f) : null)) noDamageToggle = !noDamageToggle;

            if (Button(new Rect(VW - 175, y, 60, h), "Help", "How to use the editor")) showHelp = true;
            if (Button(new Rect(VW - 110, y, 100, h), $"Close ({Plugin.EditorKey.Value})", "Close the editor and keep playing")) Close(true);
        }

        static void DrawTipBar()
        {
            GUI.skin.label.fontSize = 14;
            if (RotateHandleTarget() is { } rt && Vector2.Distance(HandlePos(rt), Event.current.mousePosition) < 16)
                tip = "Drag to rotate (snaps to 15°, hold Shift for any angle). Or press Q / E.";
            var r = TipRect; Fill(r, new Color(0, 0, 0, 0.75f));
            if (preview != null)
                Text(new Rect(r.x + 10, r.y + 3, r.width - 20, 24), captureTotal > 0 ? $"Capturing pictures: {captureTotal - captureQueue.Count}/{captureTotal} (don't touch anything)" : "Previewing...", Yellow);
            else if (tip != null) Text(new Rect(r.x + 10, r.y + 3, r.width - 20, 24), tip, Color.white);
            else if (Time.realtimeSinceStartup < statusUntil) Text(new Rect(r.x + 10, r.y + 3, r.width - 20, 24), status, Yellow);
            else Text(new Rect(r.x + 10, r.y + 3, r.width - 20, 24), "Click the screen to place " + AttackInfo.Friendly(tool) + " at the yellow line's time. Hover anything for help.", Dim);
        }

        // ---------- help ----------

        static void DrawHelp()
        {
            Fill(new Rect(0, 0, VW, VH), new Color(0, 0, 0, 0.8f));
            var r = new Rect(VW / 2 - 420, 110, 840, 820);
            Fill(r, Card); Outline(r, Pink, 2);
            float x = r.x + 30, y = r.y + 24, w = r.width - 60;
            Theme.Stripes(new Rect(r.x, r.y, r.width, 70), new Color(Pink.r, Pink.g, Pink.b, 0.12f));
            Theme.Title(new Rect(x, y - 6, w, 46), "MAKING A BOSS", Pink); y += 46;
            string[] steps =
            {
                "1.  Pick an attack on the left. Hover it to see what it does, with a picture. Press Preview to watch it in the game.",
                "2.  Move the yellow line on the timeline (bottom) to the moment you want. Click the time ruler or press Play/Pause.",
                "3.  Click the game screen to place the attack there at that moment. Or click the timeline to place it at a time.",
                "4.  Click an attack to select it. Change its timing, position and settings on the right. Drag it to move it.",
                "5.  Press Play (Space) to test from the yellow line. Turn on 'No damage' so you don't die while testing.",
                "6.  Press Save (Ctrl+S). Your boss now plays every time you play this level.",
                "7.  For your own song: put a .wav file in BepInEx\\SunLoader\\music, pick it under Music, save, then restart the level. Set BPM so the grid matches the beat.",
            };
            foreach (var s in steps) { Text(new Rect(x, y, w, 56), s, Color.white, 15, true); y += 56; }
            y += 6;
            Text(new Rect(x, y, w, 30), "Handy keys", Pink); y += 32;
            string[] keys =
            {
                "Space: play / pause       Delete: delete selected       Esc: deselect",
                "Ctrl+C / Ctrl+V: copy, then paste at the yellow line       Ctrl+D: duplicate one beat later",
                "Ctrl+Z: undo       Ctrl+S: save       Ctrl+A: select all",
                "Q / E: rotate the selected attack 15° (Shift for 1°). Or drag the yellow dot next to it.",
                "Timeline: mouse wheel scrolls, Ctrl+wheel zooms, Shift+wheel scrolls rows. Drag on empty space to select many.",
                "Attacks marked (!) belong to one level's boss and can break elsewhere. Save before trying them.",
            };
            foreach (var s in keys) { Text(new Rect(x, y, w, 44), s, Dim, 14, true); y += 34; }
            if (Button(new Rect(r.center.x - 90, r.yMax - 56, 180, 36), "Got it")) showHelp = false;
        }

        // ---------- palette ----------

        static void DrawPalette()
        {
            var r = LeftRect; Fill(r, Bg);
            var newSearch = Field(new Rect(r.x + 8, r.y + 8, r.width - 16, 28), "search", search, hint: "Type to find an attack");
            if (newSearch != search) { search = newSearch; paletteScroll = 0; }
            if (search.Length == 0 && focus != "search") Text(new Rect(r.x + 14, r.y + 11, r.width - 20, 22), "Search attacks...", Dim);

            // Flatten categories and attacks into rows; a null type is a category header.
            var items = new List<(string header, AttackType type)>();
            foreach (var cat in AttackInfo.Categories)
            {
                var inCat = EditorSession.Types.Where(t => AttackInfo.Get(t.Type).Category == cat)
                    .Where(t => search.Length == 0 || t.Type.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || AttackInfo.Friendly(t.Type).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderBy(t => AttackInfo.Friendly(t.Type)).ToList();
                if (inCat.Count == 0) continue;
                items.Add((cat, null));
                if (search.Length > 0 || !collapsed.Contains(cat)) items.AddRange(inCat.Select(t => ((string)null, t)));
            }

            float y = r.y + 44, rowH = 26;
            int visible = (int)((r.height - 50) / rowH);
            paletteScroll = Mathf.Clamp(paletteScroll, 0, Math.Max(0, items.Count - visible));
            for (int i = paletteScroll; i < items.Count && i < paletteScroll + visible; i++, y += rowH)
            {
                var row = new Rect(r.x + 6, y, r.width - 12, rowH - 3);
                var (header, type) = items[i];
                if (header != null)
                {
                    var isOpen = search.Length > 0 || !collapsed.Contains(header);
                    if (Button(row, (isOpen ? "-  " : "+  ") + header, header == AttackInfo.LevelSpecific ? "Attacks from one level's boss. They can do nothing or crash in other levels." : "Click to show or hide", color: Card))
                        if (!collapsed.Remove(header)) collapsed.Add(header);
                    continue;
                }
                var risky = AttackInfo.IsRisky(type.Type);
                if (row.Contains(Event.current.mousePosition)) hoverType = type.Type;
                var label = "   " + AttackInfo.Friendly(type.Type) + (risky ? "  (!)" : "");
                if (Button(row, label, null, type.Type == tool, risky ? new Color(0.25f, 0.17f, 0.12f) : null))
                {
                    tool = type.Type;
                    if (selected.Count > 0 && Event.current.shift)   // shift-click changes the selected attacks' type
                    {
                        Snapshot();
                        foreach (var s in selected) { s.Type = tool; s.Params.Clear(); }
                        Changed();
                    }
                }
            }
            if (items.Count > visible) Text(new Rect(r.x + 8, r.yMax - 22, r.width - 16, 20), "scroll for more", Dim);
        }

        // ---------- right panel: attack info card, or the selected attack ----------

        static void DrawRightPanel()
        {
            var r = RightRect; Fill(r, Bg);
            if (selected.Count > 0 && hoverType == null) DrawInspector(r);
            else DrawInfoCard(r, hoverType ?? tool);
        }

        static void DrawInfoCard(Rect r, string type)
        {
            float x = r.x + 12, y = r.y + 12, w = r.width - 24;
            var info = AttackInfo.Get(type);
            var model = EditorSession.TypeInfo(type);
            Theme.Title(new Rect(x, y - 4, w, 34), AttackInfo.Friendly(type).ToUpperInvariant(), Pink, 20); y += 28;
            Text(new Rect(x, y, w, 22), info.Category + (type != AttackInfo.Friendly(type) ? "   ·   " + type : ""), info.Category == AttackInfo.LevelSpecific ? Orange : Dim, 13); y += 26;

            // Picture: a real screenshot if one's been captured, else the editor's shape.
            var pic = new Rect(x, y, w, w * 9 / 16);
            Fill(pic, Color.black);
            var tex = Pictures.Get(type);
            if (tex != null) Tex(pic, tex, Color.white);
            else
            {
                DrawShapeIcon(type, pic.center, pic.height * 0.6f);
                var why = Pictures.Crashed(type) ? "This attack crashed the game when previewed" : Pictures.Broken ? "This game build can't take pictures" : "No picture yet. Press Preview to take one.";
                Text(new Rect(pic.x + 8, pic.yMax - 24, pic.width - 16, 22), why, Dim, 12);
            }
            Outline(pic, ButtonBg, 1);
            y = pic.yMax + 10;

            Text(new Rect(x, y, w, 88), info.Description, Color.white, 14, true); y += 90;
            if (Pictures.Crashed(type)) { Text(new Rect(x, y, w, 24), "Crashed the game when it was previewed!", Orange); y += 26; }

            if (hoverType == null)
            {
                var bw = (w - 6) / 2;
                if (Button(new Rect(x, y, bw, 30), Pictures.Has(type) ? "Preview again" : "Preview + picture", "Plays this attack in the middle of the screen and saves a picture of it", color: new Color(0.15f, 0.3f, 0.45f))) StartPreview(type, true);
                if (Button(new Rect(x + bw + 6, y, bw, 30), "Place at center", "Adds it at the yellow line, in the middle of the screen"))
                { Snapshot(); Add(Make(type, Snap(playhead))); }
                y += 40;
            }
            else { Text(new Rect(x, y, w, 24), "Click it to use it.", Dim); y += 30; }

            if (model != null && model.Params.Count > 0)
            {
                Text(new Rect(x, y, w, 22), "Settings you can change", Dim); y += 24;
                foreach (var (key, def) in model.Params)
                {
                    if (y > r.yMax - 130) { Text(new Rect(x, y, w, 22), "...and more", Dim); y += 24; break; }
                    var help = AttackInfo.ParamHelp(key);
                    Text(new Rect(x, y, w, 20), key + (def.Length > 0 ? $" = {def}" : ""), Color.white, 13);
                    y += 19;
                    if (help != null) { Text(new Rect(x + 10, y, w - 10, 34), help, Dim, 12, true); y += help.Length > 48 ? 32 : 18; }
                    y += 3;
                }
            }

            if (hoverType == null) DrawBossSection(r);
        }

        static void DrawBossSection(Rect r)
        {
            float x = r.x + 12, w = r.width - 24, y = r.yMax - 102;
            Fill(new Rect(r.x, y - 8, r.width, 110), Bg2);
            Text(new Rect(x, y, w, 22), $"This boss: {EditorSession.Spawns.Count} attacks" + (EditorSession.HasCustomFile ? " · saved" : " · not saved yet"), Dim, 13); y += 26;
            var bw = (w - 12) / 3;
            if (Button(new Rect(x, y, bw, 28), "Reload", "Throw away unsaved changes and load your saved boss"))
            { Snapshot(); EditorSession.Load(); selected.Clear(); Say("Reloaded your saved boss"); }
            if (Button(new Rect(x + bw + 6, y, bw, 28), "Original", "Load the level's original attacks to remix them"))
            { Snapshot(); EditorSession.LoadOriginal(); selected.Clear(); Say("Loaded the level's original attacks (Undo to go back)"); }
            if (Button(new Rect(x + 2 * (bw + 6), y, bw, 28), "Clear", "Remove every attack except checkpoints (Undo to go back)"))
            { Snapshot(); EditorSession.Clear(); selected.Clear(); Say("Cleared; checkpoints kept"); }
            y += 34;
            if (Button(new Rect(x, y, w, 28), "Capture pictures of all attacks", "Previews every safe attack one by one and saves a picture of each. Takes a few minutes; don't touch anything while it runs.", color: new Color(0.15f, 0.3f, 0.45f)))
                CaptureAll();
        }

        static void DrawInspector(Rect r)
        {
            float x = r.x + 12, y = r.y + 12, w = r.width - 24, h = 26;
            var one = selected.Count == 1 ? selected.First() : null;
            if (focus != null && focusOwner != one && focus != "search" && focus != "bpm") focus = null;
            Theme.Title(new Rect(x, y - 4, w, 34), (one != null ? AttackInfo.Friendly(one.Type) : $"{selected.Count} attacks selected").ToUpperInvariant(), Pink, 20); y += 32;

            if (one != null)
            {
                Row("Time (ms)", "time", one.Time, v => one.Time = Mathf.Max(0, v), "When it hits, in milliseconds from the start of the song");
                Row("Warning (ms)", "warning", one.Warning, v => one.Warning = Mathf.Max(0, v), "How long the warning shows before it hits");
                Row("Outro (ms)", "outro", one.Outro, v => one.Outro = Mathf.Max(0, v), "How long it stays after hitting");
                if (one.X.HasValue)
                {
                    Row("X  (0-1280)", "x", one.X.Value, v => one.X = v, "Left to right. 640 is the middle.");
                    Row("Y  (0-720)", "y", one.Y ?? 0, v => one.Y = v, "Top to bottom. 360 is the middle.");
                }
                var info = EditorSession.TypeInfo(one.Type);
                var keys = (info?.Params.Select(p => p.Key) ?? Enumerable.Empty<string>()).Concat(one.Params.Keys).Distinct().ToList();
                if (keys.Count > 0) { y += 4; Text(new Rect(x, y, w, h), "Settings (empty = game default)", Dim); y += 24; }
                foreach (var key in keys)
                {
                    if (y > r.yMax - 178) break;
                    var def = info?.Params.FirstOrDefault(p => p.Key == key).Value ?? "";
                    var help = AttackInfo.ParamHelp(key) ?? key;
                    Text(new Rect(x, y + 4, 140, h), key, Color.white, 13);
                    var field = new Rect(x + 145, y, w - 145, h - 2);
                    one.Params.TryGetValue(key, out var cur);
                    var next = Field(field, "p:" + key, cur ?? "", Snapshot, help + (def.Length > 0 ? $"  (default: {def})" : ""));
                    if (next != (cur ?? ""))
                    {
                        if (next.Length == 0) one.Params.Remove(key); else one.Params[key] = next;
                        Changed();
                    }
                    if (string.IsNullOrEmpty(next) && def.Length > 0) Text(new Rect(field.x + 6, y + 2, field.width - 8, h), def, new Color(1, 1, 1, 0.3f));
                    y += h + 2;
                }
            }

            y = Math.Max(y + 8, r.yMax - 178);
            float bw = (w - 15) / 4;
            if (Button(new Rect(x, y, bw, h), "-1 beat", "Move earlier by one beat")) Shift(-BeatMs);
            if (Button(new Rect(x + bw + 5, y, bw, h), "-1/4", "Move earlier by a quarter beat")) Shift(-BeatMs / 4);
            if (Button(new Rect(x + 2 * (bw + 5), y, bw, h), "+1/4", "Move later by a quarter beat")) Shift(BeatMs / 4);
            if (Button(new Rect(x + 3 * (bw + 5), y, bw, h), "+1 beat", "Move later by one beat")) Shift(BeatMs);
            y += h + 8;

            // Repeat: one compact row of numbers, then the button.
            Text(new Rect(x, y + 2, 60, h), "Repeat", Dim);
            NumField(new Rect(x + 60, y, 50, h - 2), "repCount", repCount, v => repCount = v, null, "How many copies");
            Text(new Rect(x + 114, y + 2, 50, h), "every", Dim);
            NumField(new Rect(x + 160, y, 45, h - 2), "repEvery", repEvery, v => repEvery = Mathf.Max(0.125f, v), null, "Beats between copies");
            Text(new Rect(x + 210, y + 2, 50, h), "beats", Dim);
            y += h + 2;
            Text(new Rect(x, y + 2, 60, h), "move", Dim);
            NumField(new Rect(x + 60, y, 50, h - 2), "repDx", repDx, v => repDx = v, null, "Move each copy this far right (negative = left)");
            Text(new Rect(x + 114, y + 2, 20, h), "x", Dim);
            NumField(new Rect(x + 130, y, 50, h - 2), "repDy", repDy, v => repDy = v, null, "Move each copy this far down (negative = up)");
            Text(new Rect(x + 184, y + 2, 20, h), "y", Dim);
            if (Button(new Rect(x + 210, y, w - 210, h - 2), "Repeat", "Make copies of the selection")) Repeat();
            y += h + 2;
            Text(new Rect(x, y + 2, 60, h), "turn", Dim);
            NumField(new Rect(x + 60, y, 50, h - 2), "repDr", repDr, v => repDr = v, null, "Rotate each copy this many more degrees (for spirals and fans)");
            Text(new Rect(x + 114, y + 2, 110, h), "deg each", Dim);
            var rb = (w - 194) / 2;
            if (Button(new Rect(x + 190, y, rb, h - 2), "Q -15°", "Rotate left (Q, or Shift+Q for 1°)")) RotateSelected(-15);
            if (Button(new Rect(x + 194 + rb, y, rb, h - 2), "E +15°", "Rotate right (E, or Shift+E for 1°)")) RotateSelected(15);
            y += h + 6;
            var b3 = (w - 10) / 3;
            if (Button(new Rect(x, y, b3, h), "Duplicate", "Copy one beat later (Ctrl+D)")) Duplicate(BeatMs);
            if (Button(new Rect(x + b3 + 5, y, b3, h), "Preview", "See this attack type in the game") && one != null) StartPreview(one.Type, false);
            if (Button(new Rect(x + 2 * (b3 + 5), y, b3, h), "Delete", "Delete (Del)", color: new Color(0.4f, 0.12f, 0.18f))) DeleteSelected();

            void Row(string label, string key, float value, Action<float> set, string hint)
            {
                Text(new Rect(x, y + 4, 140, h), label, Color.white, 13);
                NumField(new Rect(x + 145, y, w - 145, h - 2), key, value, set, Changed, hint);
                y += h + 2;
            }
        }

        // ---------- shapes: rough drawings of attacks on the game screen ----------

        static Texture2D circleTex, ringTex, triTex, starTex;
        static bool shapesTried;

        static void MakeShapes()
        {
            if (shapesTried) return;
            shapesTried = true;
            try
            {
                circleTex = MakeTex((dx, dy) => dx * dx + dy * dy <= 1);
                ringTex = MakeTex((dx, dy) => { var d = dx * dx + dy * dy; return d <= 1 && d >= 0.72f; });
                triTex = MakeTex((dx, dy) => dx >= -0.9f && Math.Abs(dy) <= (0.9f - dx) * 0.55f);   // points right
                starTex = MakeTex((dx, dy) =>
                {
                    var a = Math.Atan2(dy, dx); var d = Math.Sqrt(dx * dx + dy * dy);
                    return d <= 0.55 + 0.4 * Math.Max(0, Math.Cos(a * 8));
                });
            }
            catch (Exception e) { Plugin.Logger.LogInfo("Editor: drawing simple shapes only (" + e.Message + ")"); }
        }

        static Texture2D MakeTex(Func<float, float, bool> inside)
        {
            const int n = 64;
            var px = new Color[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                    px[j * n + i] = inside((i + 0.5f) / n * 2 - 1, (j + 0.5f) / n * 2 - 1) ? Color.white : Color.clear;
            var t = new Texture2D(n, n) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixels(new Il2CppStructArray<Color>(px));
            t.Apply();
            return t;
        }

        static float Num(EditSpawn s, string key, float def)
        {
            var v = s?.Params.GetValueOrDefault(key);
            if (string.IsNullOrEmpty(v)) v = EditorSession.TypeInfo(s?.Type)?.Params.FirstOrDefault(p => p.Key == key).Value;
            var m = Regex.Match(v ?? "", @"^\s*-?\d+(\.\d+)?");
            return m.Success && float.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : def;
        }

        static string Side(EditSpawn s)
        {
            var v = s.Params.GetValueOrDefault("modelSide");
            if (string.IsNullOrEmpty(v)) v = EditorSession.TypeInfo(s.Type)?.Params.FirstOrDefault(p => p.Key == "modelSide").Value;
            return v ?? "";
        }

        // Draws texture t in r rotated by deg around r's center.
        static void Rotated(Rect r, float deg, Texture t, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.matrix;
            var pivot = new Vector3(r.center.x, r.center.y, 0);
            GUI.matrix = old * Matrix4x4.TRS(pivot, Quaternion.Euler(0, 0, deg), Vector3.one) * Matrix4x4.TRS(-pivot, Quaternion.identity, Vector3.one);
            Tex(r, t ?? Texture2D.whiteTexture, c);
            GUI.matrix = old;
        }

        static void DrawShape(EditSpawn s, Vector2 p, Color c)
        {
            var info = AttackInfo.Get(s.Type);
            var k = VH / GameH;                          // game units -> screen units
            var size = Num(s, "size", 100) * k;
            var angle = Num(s, "rotOffset", 0);
            var dir = Num(s, "direction", float.NaN);
            switch (info.Shape)
            {
                case Shape.Bar:
                {
                    var side = Side(s);
                    var vertical = side is "up" or "down";
                    var thick = Mathf.Clamp(size * 0.6f, 6, 220);
                    var g = GameRect;
                    var bar = vertical ? new Rect(p.x - thick / 2, g.y, thick, g.height) : new Rect(g.x, p.y - thick / 2, g.width, thick);
                    if (s.Params.ContainsKey("rotOffset") || s.Type.Contains("Anywhere") || s.Type.Contains("Borderless") || s.Type.Contains("Rotate"))
                    {
                        var len = s.Type.Contains("Borderless") ? Num(s, "height", 100) * k * 3 : 2200;
                        Rotated(new Rect(p.x - len / 2, p.y - thick / 2, len, thick), angle, null, c);
                    }
                    else Tex(bar, Texture2D.whiteTexture, c);
                    break;
                }
                case Shape.Circle: Tex(Centered(p, Mathf.Max(20, size)), circleTex ?? Texture2D.whiteTexture, c); break;
                case Shape.Ring: Tex(Centered(p, Mathf.Max(40, size * 1.5f)), ringTex ?? Texture2D.whiteTexture, c); break;
                case Shape.Spikes: Rotated(Centered(p, Mathf.Clamp(size * 1.2f, 30, 260)), angle, starTex, c); break;
                case Shape.Triangle: Rotated(Centered(p, Mathf.Clamp(size, 30, 160)), float.IsNaN(dir) ? angle : dir, triTex, c); break;
                case Shape.Line:
                    for (int i = -3; i <= 3; i++)
                    {
                        var a = (float.IsNaN(dir) ? angle : dir) * Mathf.Deg2Rad;
                        var q = p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * i * 40;
                        Tex(Centered(q, 22), circleTex ?? Texture2D.whiteTexture, c);
                    }
                    break;
                case Shape.Effect:
                    Outline(Centered(p, 40), c, 2);
                    Fill(new Rect(p.x - 12, p.y - 1, 24, 2), c); Fill(new Rect(p.x - 1, p.y - 12, 2, 24), c);
                    break;
                default: Rotated(Centered(p, Mathf.Clamp(size * 0.8f, 24, 300)), angle, null, c); break;
            }
            // Arrow for things that fly in a direction.
            if (!float.IsNaN(dir) && info.Shape != Shape.Line)
            {
                var a = dir * Mathf.Deg2Rad;
                for (int i = 1; i <= 4; i++) Fill(Centered(p + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (30 + i * 18), 6), c);
            }
        }

        static Rect Centered(Vector2 p, float size) => new(p.x - size / 2, p.y - size / 2, size, size);

        static void DrawShapeIcon(string type, Vector2 center, float size)
        {
            MakeShapes();
            var fake = new EditSpawn { Type = type, X = 640, Y = 360 };
            var info = AttackInfo.Get(type);
            var c = new Color(Pink.r, Pink.g, Pink.b, 0.9f);
            switch (info.Shape)
            {
                case Shape.Bar: Fill(new Rect(center.x - size, center.y - size * 0.15f, size * 2, size * 0.3f), c); break;
                case Shape.Effect: Text(new Rect(center.x - 80, center.y - 12, 160, 24), "(no hitbox)", Dim); break;
                default:
                    var t = info.Shape switch { Shape.Circle => circleTex, Shape.Ring => ringTex, Shape.Triangle => triTex, Shape.Spikes => starTex, _ => null };
                    if (info.Shape == Shape.Line) DrawShape(fake, center, c);
                    else Tex(Centered(center, size), t ?? Texture2D.whiteTexture, c);
                    break;
            }
        }

        // ---------- game screen overlay ----------

        static Vector2 GameToScreen(float gx, float gy) => new(gx / GameW * VW, gy / GameH * VH);
        static Vector2 ScreenToGame(Vector2 p) => new(Mathf.Round(Mathf.Clamp(p.x / VW * GameW, 0, GameW)), Mathf.Round(Mathf.Clamp(p.y / VH * GameH, 0, GameH)));

        static bool ShownOnStage(EditSpawn s) =>
            s.X.HasValue && (selected.Contains(s) || (playhead >= s.Time - s.Warning - 1 && playhead <= s.Time + Math.Max(400, s.Outro)));

        static void DrawGameOverlay()
        {
            if (Event.current.type != EventType.Repaint || preview != null) return;
            MakeShapes();
            // Shapes only while paused: when playing, the real attacks are on screen.
            foreach (var s in EditorSession.Spawns)
            {
                if (!ShownOnStage(s)) continue;
                var p = GameToScreen(s.X.Value, s.Y ?? 0);
                var on = selected.Contains(s);
                var warn = playhead < s.Time;
                if (!playing) DrawShape(s, p, on ? new Color(Cyan.r, Cyan.g, Cyan.b, warn ? 0.3f : 0.55f) : new Color(Pink.r, Pink.g, Pink.b, warn ? 0.22f : 0.5f));
                var c = on ? Cyan : Pink;
                Outline(Centered(p, 26), c, on ? 3 : 2);
                Fill(Centered(p, 6), c);
                if (on) Text(new Rect(p.x + 18, p.y - 12, 260, 24), AttackInfo.Friendly(s.Type) + (warn ? $"  in {(s.Time - playhead) / 1000f:0.0}s" : ""), Cyan);
            }
            // Rotate handle: drag the yellow dot around the selected attack.
            if (RotateHandleTarget() is { } r)
            {
                var c = GameToScreen(r.X.Value, r.Y ?? 0);
                var h = HandlePos(r);
                for (int i = 1; i < 6; i++) Fill(Centered(Vector2.Lerp(c, h, i / 6f), 3), Yellow);
                Tex(Centered(h, 18), circleTex ?? Texture2D.whiteTexture, Yellow);
                Text(new Rect(h.x + 12, h.y - 10, 120, 22), Angle(r).ToString("0") + "°", Yellow);
            }
        }

        // ---------- timeline ----------

        static float MsToX(float ms) => Label + (ms - scrollMs) * zoom;
        static float XToMs(float x) => (x - Label) / zoom + scrollMs;
        static float RowY(int i) => TimelineRect.y + Ruler + i * RowH - rowScroll;

        static void ComputeRows()
        {
            var order = new List<string>();
            var seen = new HashSet<string>();
            foreach (var s in EditorSession.Spawns.OrderBy(s => s.Time))
                if (seen.Add(s.Type)) order.Add(s.Type);
            if (seen.Add(tool)) order.Add(tool);
            rows = order;
        }

        static void DrawTimeline()
        {
            if (Event.current.type == EventType.Layout) ComputeRows();
            var r = TimelineRect; Fill(r, Bg2);
            if (r.Contains(Event.current.mousePosition)) tip ??= "Click empty space to add " + AttackInfo.Friendly(tool) + ". Drag attacks to move them. Click the ruler to move the yellow line. Ctrl+wheel zooms.";
            if (Event.current.type != EventType.Repaint) return;
            float top = r.y + Ruler;

            var bm = BeatMs;
            int first = (int)Math.Floor(XToMs(Label) / bm), last = (int)Math.Ceiling(XToMs(VW) / bm);
            if (last - first < 2000)
                for (int b = first; b <= last; b++)
                    Fill(new Rect(MsToX(b * bm), top, 1, r.height - Ruler), b % 4 == 0 ? GridBar : Grid);

            var rowOf = new Dictionary<string, int>();
            for (int i = 0; i < rows.Count; i++) rowOf[rows[i]] = i;
            for (int i = 0; i < rows.Count; i++)
                if (i % 2 == 1) Fill(new Rect(Label, RowY(i), VW - Label, RowH), new Color(1, 1, 1, 0.025f));
            float lo = XToMs(Label - 60), hi = XToMs(VW + 10);
            foreach (var s in EditorSession.Spawns)
            {
                if (s.Time < lo || s.Time - s.Warning > hi || !rowOf.TryGetValue(s.Type, out var row)) continue;
                var y = RowY(row);
                if (y < top || y + RowH > r.yMax) continue;
                var on = selected.Contains(s);
                if (s.Warning > 0) Fill(new Rect(Math.Max(Label, MsToX(s.Time - s.Warning)), y + 7, Math.Min(s.Warning * zoom, MsToX(s.Time) - Label), 4), on ? new Color(Cyan.r, Cyan.g, Cyan.b, 0.3f) : new Color(Pink.r, Pink.g, Pink.b, 0.25f));
                var x = MsToX(s.Time);
                if (x >= Label) Fill(new Rect(x - 1, y + 2, Mathf.Clamp(s.Outro * zoom, 5, 40), RowH - 4), on ? Cyan : AttackInfo.IsRisky(s.Type) ? Orange : Pink);
            }

            if (drag == Drag.Box)
            {
                var b = BoxRect();
                Fill(b, new Color(Cyan.r, Cyan.g, Cyan.b, 0.12f)); Outline(b, Cyan, 1);
            }

            Fill(new Rect(0, top, Label, r.height - Ruler), Bg);
            for (int i = 0; i < rows.Count; i++)
            {
                var y = RowY(i);
                if (y < top || y + RowH > r.yMax) continue;
                Text(new Rect(8, y - 1, Label - 10, RowH + 2), AttackInfo.Friendly(rows[i]), rows[i] == tool ? Pink : Dim, 11);
            }

            Fill(new Rect(0, r.y, VW, Ruler), Bg);
            float[] steps = { 0.5f, 1, 2, 5, 10, 30, 60 };
            var step = steps.FirstOrDefault(st => st * 1000 * zoom > 70); if (step == 0) step = 120;
            for (var t = Mathf.Floor(XToMs(Label) / 1000 / step) * step; t * 1000 < XToMs(VW); t += step)
            {
                var x = MsToX(t * 1000); if (x < Label) continue;
                Fill(new Rect(x, r.y + Ruler - 6, 1, 6), Dim);
                Text(new Rect(x + 3, r.y + 1, 80, Ruler), Fmt(t * 1000), Dim, 11);
            }
            Text(new Rect(8, r.y + 1, Label, Ruler), "Time", Dim, 11);

            var px = MsToX(playhead);
            if (px >= Label) Fill(new Rect(px - 1, r.y, 3, r.height), Yellow);
        }

        static Rect BoxRect() => Rect.MinMaxRect(Math.Min(dragStart.x, dragNow.x), Math.Min(dragStart.y, dragNow.y), Math.Max(dragStart.x, dragNow.x), Math.Max(dragStart.y, dragNow.y));

        static EditSpawn HitTimeline(Vector2 p)
        {
            int row = (int)Math.Floor((p.y - TimelineRect.y - Ruler + rowScroll) / RowH);
            if (row < 0 || row >= rows.Count) return null;
            EditSpawn best = null; float bestD = 7;
            foreach (var s in EditorSession.Spawns)
            {
                if (s.Type != rows[row]) continue;
                var d = Math.Abs(MsToX(s.Time) - p.x);
                if (d < bestD) { best = s; bestD = d; }
            }
            return best;
        }

        static EditSpawn HitStage(Vector2 p)
        {
            EditSpawn best = null; float bestD = 22;
            foreach (var s in EditorSession.Spawns)
            {
                if (!ShownOnStage(s)) continue;
                var d = Vector2.Distance(GameToScreen(s.X.Value, s.Y ?? 0), p);
                if (d < bestD) { best = s; bestD = d; }
            }
            return best;
        }

        // ---------- input ----------

        static void Mouse(Event e)
        {
            var p = e.mousePosition;
            var tl = TimelineRect;
            switch (e.type)
            {
                case EventType.ScrollWheel:
                    if (tl.Contains(p))
                    {
                        if (e.control)
                        {
                            var at = XToMs(p.x);
                            zoom = Mathf.Clamp(zoom * (e.delta.y < 0 ? 1.2f : 1 / 1.2f), 0.005f, 2f);
                            scrollMs = at - (p.x - Label) / zoom;
                        }
                        else if (e.shift) rowScroll = Mathf.Clamp(rowScroll + e.delta.y * 6, 0, Math.Max(0, rows.Count * RowH - 100));
                        else scrollMs += e.delta.y * 30 / zoom;
                        e.Use();
                    }
                    else if (LeftRect.Contains(p)) { paletteScroll += e.delta.y > 0 ? 3 : -3; e.Use(); }
                    break;

                case EventType.MouseDown when e.button == 0:
                    if (tl.Contains(p)) { TimelineDown(e, p); e.Use(); }
                    else if (GameRect.Contains(p)) { StageDown(e, p); e.Use(); }
                    break;

                case EventType.MouseDrag when drag != Drag.None:
                    dragNow = p; dragMoved |= Vector2.Distance(dragStart, p) > 4;
                    if (drag == Drag.Scrub) SetPlayhead(Mathf.Max(0, XToMs(p.x)));
                    else if (drag == Drag.MoveTime)
                    {
                        var delta = Snap(dragOrigTimes[dragAnchor] + XToMs(p.x) - dragAnchorTime) - dragOrigTimes[dragAnchor];
                        foreach (var kv in dragOrigTimes) kv.Key.Time = Mathf.Max(0, Mathf.Round(kv.Value + delta));
                        focus = null; Changed();
                    }
                    else if (drag == Drag.Rotate && RotateHandleTarget() is { } rs)
                    {
                        var c = GameToScreen(rs.X.Value, rs.Y ?? 0);
                        var deg = Mathf.Atan2(p.y - c.y, p.x - c.x) * Mathf.Rad2Deg;
                        SetAngle(rs, e.shift ? deg : Mathf.Round(deg / 15) * 15);   // snaps to 15°; Shift for any angle
                        focus = null; Changed();
                    }
                    else if (drag == Drag.MoveStage)
                    {
                        var g = ScreenToGame(p);
                        foreach (var s in selected.Where(s => s.X.HasValue)) { s.X = g.x; s.Y = g.y; }
                        focus = null; Changed();
                    }
                    e.Use();
                    break;

                case EventType.MouseUp when drag != Drag.None:
                    if (drag == Drag.MoveTime && !dragMoved) undo.RemoveAt(undo.Count - 1);
                    if (drag == Drag.Box)
                    {
                        if (!dragMoved)
                        {
                            if (!e.shift) { Snapshot(); Add(Make(tool, Snap(XToMs(dragStart.x)))); Say($"Added {AttackInfo.Friendly(tool)} at {Fmt(Snap(XToMs(dragStart.x)))}"); }
                        }
                        else
                        {
                            if (!e.shift) selected.Clear();
                            var box = BoxRect();
                            var rowOf = rows.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
                            foreach (var s in EditorSession.Spawns)
                                if (rowOf.TryGetValue(s.Type, out var row) && box.Contains(new Vector2(MsToX(s.Time), RowY(row) + RowH / 2)))
                                    selected.Add(s);
                        }
                    }
                    drag = Drag.None; e.Use();
                    break;
            }
        }

        static void TimelineDown(Event e, Vector2 p)
        {
            var tl = TimelineRect;
            dragStart = dragNow = p; dragMoved = false;
            if (p.y < tl.y + Ruler) { drag = Drag.Scrub; SetPlayhead(Mathf.Max(0, XToMs(p.x))); return; }
            if (p.x < Label)
            {
                int row = (int)Math.Floor((p.y - tl.y - Ruler + rowScroll) / RowH);
                if (row >= 0 && row < rows.Count) tool = rows[row];
                return;
            }
            var hit = HitTimeline(p);
            if (hit != null)
            {
                if (e.shift) { if (!selected.Remove(hit)) selected.Add(hit); }
                else if (!selected.Contains(hit)) { selected.Clear(); selected.Add(hit); }
                Snapshot();
                drag = Drag.MoveTime; dragAnchor = hit; dragAnchorTime = XToMs(p.x);
                dragOrigTimes = selected.ToDictionary(s => s, s => s.Time);
                if (!dragOrigTimes.ContainsKey(hit)) dragOrigTimes[hit] = hit.Time;
            }
            else drag = Drag.Box;
        }

        static void StageDown(Event e, Vector2 p)
        {
            focus = null;
            dragStart = dragNow = p; dragMoved = false;
            var rot = RotateHandleTarget();
            if (rot != null && Vector2.Distance(HandlePos(rot), p) < 16) { Snapshot(); drag = Drag.Rotate; return; }
            var hit = HitStage(p);
            Snapshot();
            if (hit != null)
            {
                if (e.shift) selected.Add(hit);
                else if (!selected.Contains(hit)) { selected.Clear(); selected.Add(hit); }
            }
            else
            {
                var g = ScreenToGame(p);
                var s = Make(tool, Snap(playhead), g.x, g.y);
                if (!s.X.HasValue) { undo.RemoveAt(undo.Count - 1); Say($"{AttackInfo.Friendly(tool)} doesn't use a position; click the timeline to place it"); return; }
                Add(s);
            }
            drag = Drag.MoveStage;
        }

        static void SetPlayhead(float ms)
        {
            playhead = ms;
            if (playing) EditorSession.Seek(ms);
        }

        static void Keys(Event e)
        {
            if (showHelp)
            {
                if (e.keyCode is KeyCode.Escape or KeyCode.Return or KeyCode.Space) { showHelp = false; e.Use(); }
                return;
            }
            if (focus != null)
            {
                // Unity sends a key press as one event with keyCode and another with the character.
                if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter or KeyCode.Escape or KeyCode.Tab) focus = null;
                else if (e.keyCode == KeyCode.Backspace) { if (focusText.Length > 0) focusText = focusText[..^1]; }
                else if (e.character != '\0' && !char.IsControl(e.character)) focusText += e.character;
                e.Use();
                return;
            }
            if (preview != null) return;
            var ctrl = e.control || e.command;
            switch (e.keyCode)
            {
                case KeyCode.Space: if (playing) Pause(); else Play(); break;
                case KeyCode.Delete: case KeyCode.Backspace: DeleteSelected(); break;
                case KeyCode.Escape: selected.Clear(); break;
                case KeyCode.Z when ctrl: Undo(); break;
                case KeyCode.S when ctrl: Save(); break;
                case KeyCode.D when ctrl: Duplicate(BeatMs); break;
                case KeyCode.C when ctrl: Copy(); break;
                case KeyCode.V when ctrl: Paste(); break;
                case KeyCode.A when ctrl: selected.Clear(); selected.UnionWith(EditorSession.Spawns); break;
                case KeyCode.Q when !ctrl: RotateSelected(e.shift ? -1 : -15); break;
                case KeyCode.E when !ctrl: RotateSelected(e.shift ? 1 : 15); break;
                default: return;
            }
            e.Use();
        }
    }
}
