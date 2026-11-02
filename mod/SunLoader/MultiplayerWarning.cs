using System;
using HarmonyLib;
using UnityEngine;

namespace SunLoader
{
    // A warning shown before online multiplayer, once each time the game runs. Going online from
    // the Challenge screen waits until the player accepts; backing out stays on that screen.
    // Joining a lobby any other way (an invite) shows it as soon as they're in the lobby.
    static class MultiplayerWarning
    {
        const float ReadSeconds = 1f;   // ignores the press that opened it

        public static bool Open;
        static bool accepted, runNext;
        static float openedAt;
        static IntPtr menuPtr;
        static Action<UI_MainMenu02_Challenge> next;

        static bool CanAccept => Time.realtimeSinceStartup - openedAt >= ReadSeconds;

        // Called from a hook on a button that goes online. True means let the game carry on.
        public static bool Allow(UI_MainMenu02_Challenge menu, Action<UI_MainMenu02_Challenge> action)
        {
            if (accepted) return true;
            if (!Open)
            {
                menuPtr = menu.Pointer;
                next = action;
                Show();
                SetMenuInput(true);
            }
            return false;
        }

        static void Show()
        {
            Open = true;
            openedAt = Time.realtimeSinceStartup;
            ModMenu.Open = false;
            BossMenu.Open = false;
        }

        // The menu keeps running underneath, so it stops taking input while this is up.
        static void SetMenuInput(bool paused)
        {
            if (!SafeRead.IsObjectOf<UI_MainMenu02_Challenge>(menuPtr)) return;
            try { new UI_MainMenu02_Challenge(menuPtr).enableInput(!paused); }
            catch (Exception e) { Plugin.Logger.LogWarning("Multiplayer warning: " + e.Message); }
        }

        static void Close(bool accept)
        {
            Open = false;
            accepted = accept;
            SetMenuInput(false);
            runNext = accept && next != null;
            if (!runNext) { next = null; menuPtr = IntPtr.Zero; }
            Plugin.Logger.LogInfo("Multiplayer warning: " + (accept ? "accepted" : "backed out"));
        }

        public static void Update()
        {
            // Game code runs here rather than in OnGUI, which is only for drawing.
            if (runNext)
            {
                runNext = false;
                var action = next;
                var ptr = menuPtr;
                next = null;
                menuPtr = IntPtr.Zero;
                if (SafeRead.IsObjectOf<UI_MainMenu02_Challenge>(ptr)) action(new UI_MainMenu02_Challenge(ptr));
            }
            if (!Open && !accepted && LobbySync.InLobby) Show();
            if (!Open) return;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            if (!CanAccept) return;
            if (Input.GetKeyDown(KeyCode.JoystickButton0)) Close(true);
            else if (Input.GetKeyDown(KeyCode.JoystickButton1)) Close(false);
        }

        public static void OnGUI(float scale, float vw)
        {
            if (!Open) return;
            var e = Event.current;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
            if (e.type == EventType.KeyDown && CanAccept)
            {
                if (e.keyCode is KeyCode.Return or KeyCode.KeypadEnter) { e.Use(); Close(true); return; }
                if (e.keyCode == KeyCode.Escape) { e.Use(); Close(false); return; }
            }

            EditorGui.Fill(new Rect(0, 0, vw, 1080), new Color(0, 0, 0, 0.85f));
            var r = new Rect(vw / 2 - 480, 280, 960, 520);
            EditorGui.Fill(r, new Color(0.02f, 0.02f, 0.03f, 0.98f));
            Theme.Stripes(new Rect(r.x, r.y, r.width, 110), new Color(Theme.Pink.r, Theme.Pink.g, Theme.Pink.b, 0.14f));
            EditorGui.Fill(new Rect(r.x, r.y + 110, r.width, 4), Theme.Pink);
            EditorGui.Fill(new Rect(r.x, r.yMax - 4, r.width, 4), Theme.Pink);
            float x = r.x + 50, w = r.width - 100;
            Theme.Title(new Rect(x, r.y + 24, w, 70), "WARNING", Theme.Pink, 54);
            Theme.Title(new Rect(x, r.y + 138, w, 44), "ONLINE MULTIPLAYER", Color.white, 28);
            EditorGui.Text(new Rect(x, r.y + 196, w, 120), "Using No Damage or the Boss Editor in online multiplayer will get you banned.", Color.white, 26, wrap: true);
            EditorGui.Text(new Rect(x, r.y + 324, w, 30), "Turn them off before you play online.", new Color(0.75f, 0.75f, 0.82f), 18);

            var y = r.yMax - 110;
            var ready = CanAccept;
            if (EditorGui.Button(new Rect(x, y, 400, 64), "I understand", color: ready ? Theme.Pink : Theme.Grey) && ready) { Close(true); return; }
            if (next != null && EditorGui.Button(new Rect(r.xMax - 50 - 260, y, 260, 64), "Back") && ready) Close(false);
        }
    }

    // Each way into online play from the Challenge screen. These native methods can be folded
    // with other objects' identical ones, so only the real screen is held back.
    [HarmonyPatch]
    static class MultiplayerWarningPatches
    {
        static bool Real(UI_MainMenu02_Challenge menu) => SafeRead.IsObjectOf<UI_MainMenu02_Challenge>(menu.Pointer);

        [HarmonyPrefix, HarmonyPatch(typeof(UI_MainMenu02_Challenge), nameof(UI_MainMenu02_Challenge.onOnline))]
        static bool Online(UI_MainMenu02_Challenge __instance) => !Real(__instance) || MultiplayerWarning.Allow(__instance, m => m.onOnline());

        [HarmonyPrefix, HarmonyPatch(typeof(UI_MainMenu02_Challenge), nameof(UI_MainMenu02_Challenge.onFriends))]
        static bool Friends(UI_MainMenu02_Challenge __instance) => !Real(__instance) || MultiplayerWarning.Allow(__instance, m => m.onFriends());

        [HarmonyPrefix, HarmonyPatch(typeof(UI_MainMenu02_Challenge), nameof(UI_MainMenu02_Challenge.openCommunityRoomToLoading))]
        static bool Community(UI_MainMenu02_Challenge __instance) => !Real(__instance) || MultiplayerWarning.Allow(__instance, m => m.openCommunityRoomToLoading());

        // Keyboard and controller input on the screen underneath stays off while the warning is up.
        [HarmonyPostfix, HarmonyPatch(typeof(UI_MainMenu02_Challenge), nameof(UI_MainMenu02_Challenge.canInput))]
        static void CanInput(UI_MainMenu02_Challenge __instance, ref bool __result)
        {
            if (MultiplayerWarning.Open && Real(__instance)) __result = false;
        }
    }
}
