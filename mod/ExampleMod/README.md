# Making mods for Sun Loader

A guide to writing your own DLL mods for **Just Shapes & Beats** with Sun Loader. The example mod next to this file
(`Plugin.cs`) is a working mod you can copy: it shows a panel during levels with the level name, a timer and a hit
counter, and has an option to ignore hits.

---

## What you need

- **The game with BepInEx and Sun Loader installed.** Launch the game once so BepInEx creates `BepInEx\interop`.
  That folder holds the game's code converted into DLLs you can compile against.
- **The .NET SDK, version 6 or newer.** Get it from https://dotnet.microsoft.com/download.
- **A code editor.** Visual Studio, VS Code or Rider all work.
- **Optional but very useful: ILSpy** (https://github.com/icsharpcode/ILSpy). Use it to browse the game's classes and
  methods. See [Finding things to hook](#finding-things-to-hook).

## Quick start

1. Copy this whole `ExampleMod` folder and rename it, for example to `MyMod`.
2. In `ExampleMod.csproj`:
   - Rename the file to `MyMod.csproj`.
   - Set `<AssemblyName>MyMod</AssemblyName>`.
   - If the game isn't in the default Steam folder, change `GameDir`.
3. In `Plugin.cs`:
   - Change the line `[BepInPlugin("example.jsb.hello", "Example Mod", "2.0.0")]`.
   - The first value is a **unique ID**: use your name plus the mod's, like `yourname.jsb.mymod`.
   - The second and third values are the name and version shown in the mod menu.
4. Build from the folder:
   ```
   dotnet build -c Release
   ```
   The build copies `MyMod.dll` straight into `BepInEx\mods`.
5. In the game, press **F2** (or click **MODS** on the main menu), find your mod and press **Load now**. To load it
   every time the game starts, turn on **Auto-load**.
6. Check `BepInEx\LogOutput.log` for your mod's messages and any errors.

> A loaded mod can't be unloaded. After rebuilding, **restart the game** to load the new version.

## How a mod is put together

```
[BepInPlugin(...)] class Plugin : BasePlugin   <- entry point; Load() runs once
    Config.Bind(...)                           <- settings, saved in BepInEx\config\<id>.cfg
    new Harmony(id).PatchAll()                 <- applies every [HarmonyPatch] class in the DLL
    AddComponent<Overlay>()                    <- a MonoBehaviour: Unity calls Update / OnGUI on it

[HarmonyPatch(typeof(GameClass), nameof(GameClass.Method))]
static class MyHook
    static void Postfix(GameClass __instance)  <- runs after the game's method
    static bool Prefix(GameClass __instance)   <- runs before it; return false to skip the game's method
```

Harmony hook parameters, by name:
- **`__instance`**: the object the method was called on.
- **`__result`**: the return value. In a postfix, use `ref` to change it: `ref bool __result`.
- **Method arguments**: use the same names as the game's method.

## Finding things to hook

1. Open `BepInEx\interop\Assembly-CSharp.dll` in ILSpy. It contains the game's own classes; Unity's are in the
   `UnityEngine.*.dll` files.
2. Search for words related to what you want, such as `Hero`, `Boss`, `Menu`, `Music` or `Video`. Read the method
   names: JSB's are usually clear, like `hitByEnemy`, `onBeat` or `OnVideoEnd`.
3. The method bodies in these DLLs are only stubs that call into the real game, so you see **names and types, not
   logic**. Find out what a method does by hooking it and logging when it runs:
   ```csharp
   static void Postfix() => Plugin.Logger.LogInfo("it ran!");
   ```

### Hooks known to work

| What | Hook | Notes |
|---|---|---|
| A level is loaded | `ModelStoryCheckpoint.SetMetaSong` (postfix) | `__instance.name` gives the level name |
| A level's attacks start (also retries) | `LogicSpawnCreator.start` (postfix) | `__instance.metaSong` holds the attacks |
| The player is hit | `HeroCollisionWithEnemy.hitByEnemy` (prefix) | Return `false` to cancel the hit |
| Main menu is showing | `UI_MainMenu02.update` (postfix) | Runs every frame while the menu is up |
| Main menu music beat | `UI_MainMenu02.onBeat` (postfix) | |
| A full-screen video starts | `FullScreenVideo.start` (postfix) | `fileName`, `OnVideoEnd()` (see the No Intro mod) |
| Song position / pause | `LogicSpawnCreator.sfxView` → `DynamicMusic` | `.position` (ms), `.pause(bool)` |
| Jump in the song | `LogicSpawnCreator.putMusicToWithoutSkipping(ms)` | |
| Level unlocked? | `MetaSongProgress.isLocked`, `MetaArcadeSongProgress.isUnlocked` | Postfix and set `__result` |
| Level's song | `ModelStoryCheckpoint.SetAudioClip` | |

## The rules (learned the hard way)

These are specific to this game's build. Breaking them usually crashes the game with no error message.

1. **Always check `__instance` is the class you hooked.**
   - The game is compiled with IL2CPP, which merges methods whose machine code is identical. A hook on
     `LogicSpawnCreator.start` can therefore also fire for unrelated objects.
   - Reading those objects' fields as the wrong type **crashes the game**.
   - The merged function may not even pass an object, just a number that looks like a pointer. **`TryCast` isn't
     safe here**: it reads that pointer before checking it, which crashes the game while a level loads.
   - Copy the `Guard` class from the example. It checks the memory is readable before looking at the class, then:
     ```csharp
     if (!Guard.Is<LogicSpawnCreator>(__instance)) return;
     ```
     For a prefix that returns `bool`, return `true` when the object isn't yours, so the game runs normally.
2. **Some of Unity's code is missing from the game.**
   - Calling it throws `Method unstripping failed`. Known missing: `GUI.TextField` / `TextEditor`.
   - `GUI.Label`, `GUI.DrawTexture`, `Texture2D`, `ImageConversion` and `AudioClip.Create` work.
   - If you need a text box, draw your own (Sun Loader's editor does this).
3. **Only touch `GUI.*` and `GUI.skin` inside `OnGUI`.** Anywhere else throws "You can only call GUI functions from
   inside OnGUI".
4. **Many game values are getters, not fields.**
   - Examples: `name`, `id`, `songLength`. Call them normally and wrap the call in `try`.
   - Don't use reflection or raw memory reads on them.
5. **Keep your MonoBehaviour minimal.**
   - Give it only a constructor `(IntPtr ptr) : base(ptr)` and the Unity methods (`Update`, `OnGUI`, …).
   - Put everything else in static classes. Il2CppInterop tries to register every method on the class, and some
     parameter types make that fail.
6. **Game lists are IL2CPP lists.**
   - `Il2CppSystem.Collections.Generic.List<T>` has `.Count` and `[i]`.
   - Don't use LINQ on them directly. Copy to a C# list first if you need to.
7. **Strings from the game are converted automatically.** Use them like normal C# strings.
8. **Pausing the game** means setting `Time.timeScale = 0` and pausing the music. Always undo both, including when
   your code throws.
9. **Startup timing.**
   - Mods set to Auto-load start a moment after the game launches. That's early enough for the intro video.
   - If you need to run before *anything* else, put the DLL in `BepInEx\plugins` instead. Those load before the
     game starts.

## Testing and debugging

- **Logs:**
  - `Log.LogInfo(...)`, `LogWarning(...)` and `LogError(...)` go to `BepInEx\LogOutput.log` (and the BepInEx
    console, if enabled).
  - Log a lot while developing. Every error your mod throws inside a hook is logged with a stack trace.
- **If the game closes with nothing in the log,** it was a native crash. That's almost always rule 1 (a missing
  type check) or reading a field that holds a bad pointer.
- **Wrap hook bodies in `try/catch`.** An exception thrown inside a game method can break that method for the rest
  of the session.
- **Rebuilding while the game is open fails** because the DLL is in use. Close the game first.

## Sharing your mod

- **Share only your DLL** (for example `MyMod.dll`). Players put it in `BepInEx\mods` and load it from the mod menu.
- **Don't ship** BepInEx, Harmony, the `interop` DLLs or any game files. Players already have them, and the game's
  files aren't yours to share.
- **Give each version a new number** in `[BepInPlugin]` so players can tell versions apart in the mod menu.
- **If your mod needs Sun Loader's boss files or music,** say so in your mod's description.

## Making bosses instead of mods

You don't need code to make bosses:

1. Start a level and press **F1**.
2. Build the boss in the in-game editor.
3. Press **Save**. Bosses are saved as `BepInEx\SunLoader\<level name>.json`.

Each attack in the file has:
- `type`: the attack's name
- `time`, `warning`, `outro`: in milliseconds
- `x` and `y`: position on a 1280×720 screen
- `params`: the attack's settings

A boss file can also name a song from `BepInEx\SunLoader\music` in `"music"`.
