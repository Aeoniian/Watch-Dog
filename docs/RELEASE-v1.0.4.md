# Watch Dog v1.0.4

The standalone recovery plugin gets the empty-room fix from VibeSkuffle v1.0.52. One DLL targets Skua 1.4.5+ and VibeSkua 2.x.

## Changes since v1.0.3

- **A room that never loads:** with almost any script, a character can end up in the right room with nothing in it (no monsters, no NPCs, no avatar), so the script farms nothing for hours. When a script is running and the character stays "not loaded" for 90 seconds, Watch Dog jumps to the current cell (what Jump > Current does by hand), then joins the room again if that does not help, three tries. New option **Fix a room that never loaded** (seconds, 0 = off). Reported by Ramon Douglas.

## Install / update

Close the stock client, replace `WatchDog.dll`, then restart:
- Skua: `%APPDATA%/Skua/plugins`
- VibeSkua: `%APPDATA%/VibeSkua/plugins`

**VibeSkuffle v1.0.52 already includes this** and does not need this DLL.

## Verification and limits

Built against the pinned Skua 1.4.5 interfaces. The rule was written from a report and a screenshot and was not run live with real accounts.
