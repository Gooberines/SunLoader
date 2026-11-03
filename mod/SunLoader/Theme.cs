using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace SunLoader
{
    // Makes the mod's menus look like the game's: its own font (found at runtime in the
    // fonts the game has loaded), black panels, hot pink, slanted blocks that kick on the beat.
    static class Theme
    {
        public static readonly Color Black = new(0.02f, 0.02f, 0.03f, 0.96f), Pink = new(1f, 0.16f, 0.45f), PinkDark = new(0.55f, 0.06f, 0.24f);
        public static readonly Color Cyan = new(0.17f, 0.91f, 1f), White = Color.white, Grey = new(0.16f, 0.16f, 0.2f);

        static Font font;
        static int attempts;
        static float nextTry;
        static bool loggedFonts;
        public static float Pulse;      // 1 on a beat, fades to 0

        public static void Beat() => Pulse = 1;

        public static void Tick() => Pulse = Mathf.Max(0, Pulse - Time.unscaledDeltaTime * 4);

        // Kick on the beat from a song position, for when the main menu's beat isn't available.
        static int lastBeat = -1;
        public static void BeatFromMusic(float ms, float bpm)
        {
            if (ms < 0 || bpm <= 0) return;
            var beat = (int)(ms / (60000f / bpm));
            if (beat != lastBeat) { lastBeat = beat; Beat(); }
        }

        // Call at the start of OnGUI.
        public static void Apply()
        {
            if (font == null && attempts < 15 && Time.realtimeSinceStartup >= nextTry)
            {
                attempts++;
                nextTry = Time.realtimeSinceStartup + 2;
                font = FindFont();
            }
            try
            {
                var label = GUI.skin.label;
                if (font != null) label.font = font;
                label.fontStyle = FontStyle.Bold;
                label.fontSize = 15;
                label.wordWrap = false;                  // labels stay on their line; paragraphs ask for wrapping
                label.clipping = TextClipping.Clip;      // long text is cut off instead of spilling into neighbors
            }
            catch { }
        }

        static Font FindFont()
        {
            try
            {
                var wanted = Plugin.UiFont.Value?.Trim() ?? "";
                var assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().ToArray();
                if (assets.Length == 0) return null;   // not loaded yet; try again later
                if (!loggedFonts)
                {
                    loggedFonts = true;
                    Plugin.Logger.LogInfo("Game fonts (set [UI] Font in the config to pick one): " +
                        string.Join(", ", assets.Select(a => a.name + (a.sourceFontFile != null ? "" : " (no source)"))));
                }
                var usable = assets.Where(a => a.sourceFontFile != null).ToList();
                var pick = usable.FirstOrDefault(a => wanted.Length > 0 && a.name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                        ?? usable.FirstOrDefault(a => !a.name.Contains("Fallback") && !a.name.Contains("Liberation") && !a.name.Contains("Arial"));
                if (pick != null)
                {
                    Plugin.Logger.LogInfo("Menus use the game's font: " + pick.name);
                    return pick.sourceFontFile;
                }
                if (attempts < 10) return null;   // the game's fonts may still be loading
                // No game font usable by Unity's GUI: use a heavy system font instead.
                var os = Font.CreateDynamicFontFromOSFont(new[] { "Arial Black", "Impact", "Segoe UI Black", "Arial" }, 16);
                Plugin.Logger.LogInfo("Game font isn't usable by the menus; using a system font instead");
                return os;
            }
            catch (Exception e)
            {
                attempts = 99;
                Plugin.Logger.LogInfo("Menus keep the default font (" + e.Message + ")");
                return null;
            }
        }

        // A block slanted like the game's menu buttons.
        public static void Slanted(Rect r, Color c, float slant = 0.22f)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.matrix;
            var shear = Matrix4x4.identity;
            shear.m01 = -slant;   // x shifts with y
            var pivot = new Vector3(r.center.x, r.center.y, 0);
            GUI.matrix = old * Matrix4x4.Translate(pivot) * shear * Matrix4x4.Translate(-pivot);
            var prev = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
            GUI.matrix = old;
        }

        // Sun Loader's buttons on the main menu: a column above STORY, lined up with and drawn like
        // PARTY MODE and EXIT GAME. The game draws its menu 16:9 and letterboxes it, so positions
        // are measured on that picture (2000 wide) and not on the window. Slot 0 is the top one.
        // Returns true when it's clicked.
        public static bool MenuButton(int slot, string name, string hint, Color accent, Event e, float vw)
        {
            var gameW = Mathf.Min(vw, 1920f);
            var unit = gameW / 2000f;
            var left = (vw - gameW) / 2;
            var top = (1080f - gameW * 9 / 16) / 2;
            var h = 70 * unit;
            var y = top + (15 + slot * 80) * unit;
            var bar = new Rect(left + 1455 * unit, y, gameW + left - 1455 * unit, h);
            var hover = bar.Contains(e.mousePosition);
            var dark = new Color(accent.r * 0.45f, accent.g * 0.45f, accent.b * 0.45f);

            Slanted(new Rect(bar.x - 28 * unit, y, 50 * unit, h), hover ? Color.white : dark, 0.55f);
            // Longer than the bar, so its slanted right end is past the edge of the picture.
            Slanted(new Rect(bar.x, y, bar.width + h, h), hover ? accent : new Color(0.02f, 0.02f, 0.03f, 0.95f), 0.55f);
            // In a window wider than the picture, this covers what would show of that end.
            if (left > 0) EditorGui.Fill(new Rect(left + gameW, y - 2, h, h + 4), Color.black);
            var text = hover ? new Color(0.02f, 0.02f, 0.03f) : Color.white;
            EditorGui.Text(new Rect(left + 1524 * unit, y + 12 * unit, 300 * unit, h), name, text, (int)(32 * unit));
            EditorGui.Text(new Rect(left + 1700 * unit, y + 24 * unit, 290 * unit, h), hint, hover ? text : new Color(1, 1, 1, 0.55f), (int)(18 * unit));

            if (e.type != EventType.MouseDown || e.button != 0 || !hover) return false;
            e.Use();
            return true;
        }

        // Big title text, bumped by the beat.
        public static void Title(Rect r, string text, Color c, int size = 34)
        {
            var label = GUI.skin.label;
            var oldSize = label.fontSize;
            try
            {
                label.fontSize = (int)(size * (1 + Pulse * 0.06f));
                EditorGui.Text(new Rect(r.x + 3, r.y + 3, r.width, r.height), text, new Color(0, 0, 0, 0.6f), label.fontSize);   // drop shadow
                EditorGui.Text(r, text, c, label.fontSize);
            }
            finally { label.fontSize = oldSize; }
        }

        // Diagonal stripes drifting across a background, like the game's menus.
        public static void Stripes(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            var t = Time.realtimeSinceStartup * 30 % 120;
            for (float x = r.x - r.height - 120 + t; x < r.xMax; x += 120)
                Slanted(new Rect(x + r.height * 0.5f, r.y, 34, r.height), c, 1f);
        }
    }

    // The main menu's own beat drives the menu animation while it's on screen.
    [HarmonyPatch(typeof(UI_MainMenu02), nameof(UI_MainMenu02.onBeat))]
    static class MainMenuBeat
    {
        static void Postfix(UI_MainMenu02 __instance)
        {
            if (SafeRead.IsObjectOf<UI_MainMenu02>(__instance.Pointer)) Theme.Beat();
        }
    }
}
