SUN LOADER (ALPHA) - mod loader + boss editor for Just Shapes & Beats
======================================================================

This is an early alpha: expect bugs, and back up your bosses
(BepInEx\SunLoader). Report problems at github.com/Gooberines/SunLoader/issues

HOW TO INSTALL
1. Unzip this whole folder (don't run the installer from inside the zip).
2. Close Just Shapes & Beats.
3. Run SunLoaderInstaller.exe and press INSTALL.
4. Start the game. The first start takes a minute or two longer while
   BepInEx sets itself up; that only happens once.

IN THE GAME
- BOSSES button on the main menu, or F4: make and play your own bosses.
- MODS button on the main menu, or F2 anywhere: the mod menu.
- F1 during a level: the boss editor.

WHAT THE INSTALLER DOES
It works offline. The only time it goes online is the optional "Get the newest
Sun Loader from GitHub" box: then it asks github.com/Gooberines/SunLoader for a
newer release and uses its mod DLLs instead (if there's no internet, it just
installs the version in this download).
- Finds the game in your Steam libraries (or lets you pick the folder).
- Unpacks files\BepInEx.zip into the game folder after checking its SHA-256.
  This is the official, unmodified BepInEx 6.0.0-be.791 package from
  builds.bepinex.dev - the mod framework Sun Loader runs on.
- Copies the mod DLLs from the "files" folder into the game:
    files\SunLoader.dll   -> BepInEx\plugins\SunLoader\  (the mod loader)
    files\NoIntro.dll     -> BepInEx\mods\  (optional: skips the intro video)
    files\ExampleMod.dll  -> BepInEx\mods\  (optional: example for mod makers)
- Nothing outside the game folder is changed.

UNINSTALL
Run the installer and press UNINSTALL. The game goes back to normal; your
bosses, music and mods are kept in the BepInEx folder.

ANTIVIRUS WARNINGS
The installer isn't signed with a paid code-signing certificate, so Windows
SmartScreen may say "Windows protected your PC" (click More info -> Run
anyway), and an antivirus engine may flag it as unknown. It only does what's
listed above.

CREDITS
BepInEx is open source under the GNU LGPL 2.1 (see files\BepInEx-LICENSE.txt).
Source code: https://github.com/BepInEx/BepInEx