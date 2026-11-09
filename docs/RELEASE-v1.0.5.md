# Watch Dog v1.0.5

The empty-room fix of v1.0.4 did not trigger for the reported case, so it is changed. One DLL targets Skua 1.4.5+ and VibeSkua 2.x.

## Changes since v1.0.4

- A map with no monsters at all now also counts as an empty room (v1.0.4 only looked at a character that was not loaded, which was not the case in the reported glitch). With a script running and the room empty for 90 seconds, Watch Dog jumps to the current cell, twice, and the third time joins the room again if the character is not loaded. It logs when it first suspects an empty room. Option **Fix a room that never loaded** (seconds, 0 = off). Reported with a video by Ramon Douglas.

## Install / update

Close the stock client, replace `WatchDog.dll`, then restart:
- Skua: `%APPDATA%/Skua/plugins`
- VibeSkua: `%APPDATA%/VibeSkua/plugins`

**VibeSkuffle v1.0.53 already includes this** and does not need this DLL.

## Verification and limits

Built against the pinned Skua 1.4.5 interfaces. Not run live with a real glitch.
