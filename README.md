# Sun Loader

> **Alpha.** Sun Loader is early and still changing. Expect bugs, and back up your bosses. Bug reports are welcome in
> [Issues](../../issues).

A mod loader and in-game boss editor for **Just Shapes & Beats** (Steam, Windows), built on
[BepInEx 6](https://github.com/BepInEx/BepInEx).

- **Bosses menu:** a **BOSSES** button on the main menu (or **F4**) to make your own bosses, separate from the game's
  levels and playlists. Name one, pick a stage, and the editor opens; play, edit, rename, copy or delete them from the
  list.
- **Share and rate bosses:** the **Online** tab lists bosses other players shared, best rated first. Download any of
  them, rate them 👍 / 👎, and share your own (sign in with GitHub to post and rate; browsing needs no account).
- **Suns and difficulty ratings, like Geometry Dash's stars:** the owner of the bosses repository rates shared bosses
  1 to 20 suns, and the sun count sets the difficulty, from Easy to Extreme Demon. Beating a rated boss earns its suns,
  and the **Suns** tab has your sun counter and a leaderboard. The game's own levels are rated too (**Suns → Game
  levels**): beat them from the Playlist to earn their suns.
- **Custom bosses in online multiplayer:** the lobby host picks one of their bosses, and everyone in the lobby with Sun
  Loader plays it when its stage comes up. The boss is sent through the Steam lobby; nobody has to download it.
- **Boss editor:** press **F1** in any level to place attacks on the game screen, set their timing and settings,
  preview them live, and save your own boss over that level.
- **Mod menu:** a **MODS** button on the main menu (or **F2** anywhere) lists DLL mods, loads them on the spot or at
  startup, and turns startup plugins on and off.
- **Custom music** for your bosses (`.wav`).
- **No damage** test mode, **unlock all levels** (your save file isn't changed), and attack pictures and descriptions.
- **An installer** that sets everything up in one click, plus an example mod and a guide for making your own.

> Fan-made and unofficial. Not affiliated with Berzerk Studio. This repository contains no game files.

## Install

1. Download `SunLoader-Setup.zip` from [Releases](../../releases) and unzip the whole folder.
2. Close the game, run `SunLoaderInstaller.exe` and press **Install**.
3. Start the game. The first start takes a minute or two longer while BepInEx sets itself up.

The installer works offline, except for one optional box, **Get the newest Sun Loader from GitHub**. When it's
ticked, the installer checks this repository's Releases and uses a newer release's mod DLLs if there is one. Otherwise:
- It unpacks the official, unmodified BepInEx 6.0.0-be.791 package (checked by SHA-256) and copies the mod DLLs into
  the game folder.
- **Uninstall** in the same window switches everything off and keeps your bosses and mods.

To install by hand instead:
1. Unzip BepInEx 6.0.0-be.791 (Unity IL2CPP, win-x64) into the game folder.
2. Put `SunLoader.dll` in `BepInEx\plugins\SunLoader\`.

## Using it

| Where | Key | What |
|---|---|---|
| Main menu | **BOSSES** button / **F4** | Bosses menu: make, play, edit and manage your own bosses |
| Main menu | **MODS** button / **F2** | Mod menu: load DLLs from `BepInEx\mods`, auto-load, turn plugins on/off |
| In a level | **F1** | Boss editor (it has its own Help screen) |
| Boss editor | Space, Q/E, Ctrl+Z/C/V/D/S | Play/pause, rotate, undo, copy/paste, duplicate, save |

- **Your own bosses** (Bosses menu) are saved to `BepInEx\SunLoader\bosses\<name>.json`. Each plays on a *stage*: a
  game level whose scene and length it uses, with that level's song or one of yours. They don't change the game's
  levels. Deleted ones go to `bosses\deleted`.
- **Shared bosses** are issues in [Gooberines/SunLoader-Bosses](https://github.com/Gooberines/SunLoader-Bosses); a
  boss's 👍 / 👎 reactions are its rating. Signing in uses a GitHub App that can only post and react to issues there
  ([setup](docs/SHARING-SETUP.md)). The login is saved encrypted for your Windows user.
- **Suns:** a boss's rating is a `suns: N` label (1-20) on its issue, set by the repository owner (Online tab →
  **Rate**); only the owner and collaborators can set labels. Difficulty by suns: 1-2 Easy, 3 Normal, 4-5 Hard,
  6-7 Harder, 8-9 Insane, 10-11 Easy Demon, 12-13 Medium Demon, 14-15 Hard Demon, 16-17 Insane Demon, 18-20 Extreme
  Demon. A clear is a comment posted from the
  player's login when they beat a downloaded, unchanged boss without the editor or no-damage mode. Clears wait in
  `BepInEx\SunLoader\clears.json` until you're signed in, and count for suns once the boss is rated.
- **Game levels** each have a `[Level] NAME` issue in the bosses repository, made by the owner, with a `suns: N`
  label. Beating one from the Playlist posts a clear there, unless its attacks were replaced or the editor or
  no-damage mode was used.
- **Before online multiplayer**, a warning says that using No Damage or the Boss Editor online will get you banned. It
  shows once each time the game runs, and going online waits until you accept.
- **Fair play:** a beaten run earns nothing if another mod is loaded or changes getting hit, Assist Mode is on, the song
  skipped ahead, or it finished faster than the song. The boss editor and No Damage are off in online lobbies. A boss
  changed after it was rated earns nothing until it's rated again, and clear comments that were edited don't count. A
  failed check only stops that run counting. The repository owner bans players (**Suns** tab), and bans can be appealed
  on the Discord server. These checks run on the player's PC, so they stop casual cheating, not someone who rebuilds
  the mod.
- **Multiplayer:** in an online lobby, the host's boss list shows **Use in lobby**. Players need Sun Loader to see the
  custom boss (others get the normal level), and a custom song only plays for players who have the same song file.
- **Level replacements** (F1 in a normal level) are saved to `BepInEx\SunLoader\<level name>.json`. That level plays
  your boss from then on.
- **Songs** go in `BepInEx\SunLoader\music`.
- **Settings** are in `BepInEx\config\riptid.jsb.sunloader.cfg`: keys, unlock-all, and which game font the menus use.

## Making mods

Start with [`mod/ExampleMod`](mod/ExampleMod). It's a small working mod with comments on every technique, and its
[README](mod/ExampleMod/README.md) is a full guide:
- setup
- hooking the game safely
- hooks known to work
- this game's quirks (IL2CPP code folding, stripped Unity methods)
- debugging and sharing

## Building from source

You need:
- **Windows**
- **The .NET SDK 8**, which builds the net6.0 mods and the .NET Framework 4.8 installer
- **The game with BepInEx run once.** The projects compile against the `BepInEx\interop` DLLs it generates, which
  aren't in this repo because they're made from the game.

```powershell
.\build.ps1                                          # game in the default Steam folder
.\build.ps1 -GameDir "D:\Games\Just Shapes & Beats"  # somewhere else
.\build.ps1 -Deploy                                  # also copy the mods into your game
```

This produces `build\` (each project), `dist\SunLoader-Setup.zip` (the release package) and `dist\*.dll`. Attach all
of them to a GitHub release tagged `v<version>`: the installer's update check downloads the DLLs from there. To build
one project:
```powershell
dotnet build mod\SunLoader\SunLoader.csproj -c Release -p:GameDir="D:\Games\Just Shapes & Beats"
```

## What's in here

| Folder | What |
|---|---|
| `mod/SunLoader` | The main plugin |
| `mod/Installer` | The installer (.NET Framework 4.8 WinForms) |
| `mod/ExampleMod` | Example mod + modding guide |
| `mod/NoIntro` | Small mod that skips the intro video |
| `mod/editor` | Older browser-based boss editor (`build.ps1` bakes in the attack list from your game) |
| `tools/InteropDumper` | Lists types, fields and method signatures in the interop DLLs, to find things to hook |

The files in `mod/SunLoader`:

| File | What |
|---|---|
| `Plugin.cs` | Startup, settings, level dump/import hooks, boss file format |
| `SafeRead.cs` | Memory-checked reads of game objects, which avoid crashes from IL2CPP code folding |
| `EditorSession.cs` | The level being edited, loading/saving, applying to the game, seeking the song |
| `BossEditorUI.cs` | The F1 editor UI (Unity IMGUI, hand-drawn controls) |
| `CustomBosses.cs` | Bosses-menu boss files, the stage list, and starting a boss's level |
| `BossMenu.cs` | The Bosses menu UI (My bosses, Online, lobby) |
| `Share.cs` | Sharing and rating through GitHub, and the GitHub login |
| `LobbySync.cs` | Sending the host's boss to everyone in an online lobby |
| `AttackInfo.cs` | Attack categories, descriptions, editor shapes |
| `Music.cs` | Custom songs (WAV decoding) and no-damage mode |
| `Pictures.cs` | Attack screenshots for the editor |
| `ModMenu.cs` | Mod menu and runtime DLL loader |
| `Theme.cs` | Game-style look (font, slanted blocks, beat pulse) |
| `Unlocks.cs` | Unlock-all-levels hooks |

## Credits and licenses

- **Sun Loader**: MIT, see [LICENSE](LICENSE).
- **[BepInEx](https://github.com/BepInEx/BepInEx)**: LGPL-2.1. The installer package includes the unmodified BepInEx
  release and its license (`mod/Installer/BepInEx-LICENSE.txt`).
- **[HarmonyX](https://github.com/BepInEx/HarmonyX)** and **[Il2CppInterop](https://github.com/BepInEx/Il2CppInterop)**
  come with BepInEx.
- **Just Shapes & Beats** © Berzerk Studio. You need your own copy of the game.
