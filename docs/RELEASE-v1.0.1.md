# Watch Dog v1.0.1

The standalone recovery plugin now shows what Rescue Allies is doing. One DLL targets Skua 1.4.5+ and VibeSkua 2.x.

## Changes since v1.0.0

- **Live rescue status** in **Plugins → Rescue Allies**: whether Rescue Allies is on, this account's death count against the threshold, the time since the first death against the timer, whether a call for help is active, and whether this account can request help (it needs an active, unpaused script). It refreshes every second.
- **Helper and phase:** shows whether this account is helping, requesting, recovering or waiting for the next scan, the current phase (joining, fighting, returning and restoring its original state) and the last outcome, including interrupted or failed rescues.
- **Cooldowns:** shows the remaining cooldown for each requester this account helped recently. Other requesters stay eligible.
- The built-in Watch Dog in VibeSkuffle uses the same status format.
- The status is read-only. It never makes game calls or changes saved preferences, and it reads "unavailable" when the account is offline, loading or recovering.

## Install / update

Close the stock client, replace `WatchDog.dll`, then restart:
- Skua: `%APPDATA%/Skua/plugins`
- VibeSkua: `%APPDATA%/VibeSkua/plugins`

**VibeSkuffle already includes Watch Dog** and does not need this DLL.

## Verification and limits

Built against the pinned Skua 1.4.5 interfaces. The same status code passed the rescue and rendered-UI checks used for VibeSkuffle's development build. The standalone native WPF window and the live refresh in older-host fallback dialogs were not covered; older fallback dialogs show a status snapshot in the title instead of a live panel.
