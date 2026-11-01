# Setting up boss sharing

Shared bosses live in a public GitHub repository: each shared boss is an issue there,
and 👍 / 👎 reactions are its rating. Sun Loader reads the list with GitHub's public API
(no account needed) and signs players in through a **GitHub App** to post and rate.

A GitHub App is used instead of a normal "OAuth app" because its logins are limited to
what the app is allowed: **issues on the bosses repository, nothing else**. A player who
signs in doesn't give Sun Loader access to their own repositories.

You only do this once, as the owner of the project.

## 1. The bosses repository

Create a **public** repository, `Gooberines/SunLoader-Bosses` (the name in
`Sharing.Repository`). Leave Issues turned on. A README saying "shared bosses for Sun
Loader, posted from the game" is enough.

## 2. The GitHub App

1. Go to **GitHub → Settings → Developer settings → GitHub Apps → New GitHub App**.
2. Fill in:
   - **GitHub App name:** `Sun Loader` (or any free name)
   - **Homepage URL:** `https://github.com/Gooberines/SunLoader`
   - **Callback URL:** leave empty
   - **Expire user authorization tokens:** **uncheck** (so players don't have to sign in again every 8 hours)
   - **Enable Device Flow:** **check**
   - **Webhook → Active:** **uncheck**
   - **Repository permissions → Issues:** **Read and write**. Leave every other permission at "No access".
   - **Where can this GitHub App be installed?** Only on this account
3. **Create GitHub App**, then copy its **Client ID** (starts with `Iv`). It isn't a secret; don't
   create a client secret, it's not needed.
4. On the app's page, **Install App** → your account → **Only select repositories** →
   `SunLoader-Bosses` → Install.

## 3. Put the Client ID in Sun Loader

Set `BuiltInClientId` in `mod/SunLoader/Share.cs` to the Client ID and rebuild, so every
player gets it. (To test before rebuilding, put it in `Sharing.GitHubAppClientId` in
`BepInEx\config\riptid.jsb.sunloader.cfg`.)

## Moderating

- **Remove a boss:** close its issue. Closed issues don't show in the game.
- **Stop someone posting:** block them from your account (Settings → Blocked users), which
  stops them opening issues in your repositories.
- **Lock comments** on an issue if a thread gets out of hand.

Bosses are attack data only (attack names, times, positions and settings). Sun Loader
never runs code from a downloaded boss, caps its size, and only accepts a song *file name*
from it (the song itself is never downloaded).
