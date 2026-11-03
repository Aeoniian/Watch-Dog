# Watch Dog v1.0.2

The standalone recovery plugin now handles "One Time Quest Only" spam. One DLL targets Skua 1.4.5+ and VibeSkua 2.x.

## Changes since v1.0.1

- **"One Time Quest Only" spam:** 8 of those answers within a minute (a script keeps asking for a quest that can only be done once) make Watch Dog log out and back in. New option "Relog on "One Time Quest Only" spam" (on by default).
- If it comes back within 15 minutes of that relog, the plugin relogs again and says in its log that it cannot restart the client. VibeSkuffle's built-in Watch Dog (and the Rust rewrite) restart the whole client in that case.

## Install / update

Close the stock client, replace `WatchDog.dll`, then restart:
- Skua: `%APPDATA%/Skua/plugins`
- VibeSkua: `%APPDATA%/VibeSkua/plugins`

**VibeSkuffle already includes Watch Dog** and does not need this DLL.

## Verification and limits

Built against the pinned Skua 1.4.5 interfaces. The same status code passed the rescue and rendered-UI checks used for VibeSkuffle's development build. The restart fallback was ported from VibeSkuffle's built-in Watch Dog and has not been run live in the standalone plugin. The standalone native WPF window and the live refresh in older-host fallback dialogs were not covered; older fallback dialogs show a status snapshot in the title instead of a live panel.
