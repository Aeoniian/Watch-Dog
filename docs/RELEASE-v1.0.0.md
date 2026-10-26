# Watch Dog v1.0.0 — Stable

The standalone recovery plugin leaves beta with adjustable, live shared Rescue Allies settings. One DLL targets Skua 1.4.5+ and VibeSkua 2.x.

## Changes since Beta5

- **Plugins → Rescue Allies** opens separate **Rescue Allies** and **Rescue Settings** tabs using the host's native WPF/Avalonia controls. Older hosts can fall back to their native options dialogs.
- **Shared rescue delay:** 1–120 minutes; default 15.
- **Shared death threshold:** 1–100 deaths without a kill; default 10. Either threshold requests help. The timer starts at the first death and requires at least two deaths; a kill resets both counters.
- Edits apply immediately across updated clients on the same PC and affect the current streak on the next watchdog check. Invalid values are not saved, and existing switch/account-exclusion preferences migrate without being reset.
- Existing calls continue until a kill or another normal stop condition. Per-account opt-outs still permit receiving help; eligible idle accounts can rescue without a loaded/running script.
- Helpers returning to their own house use the proper AQW house command.

## Install / update

Close the stock client, replace `WatchDog.dll`, then restart:
- Skua: `%APPDATA%/Skua/plugins`
- VibeSkua: `%APPDATA%/VibeSkua/plugins`

Update every participating client/plugin to share the new thresholds consistently. **VibeSkuffle already includes Watch Dog** and does not need this DLL.

## Verification

Built against the pinned Skua 1.4.5 interfaces. The combined rescue implementation passes 68 targeted checks. The actual native Avalonia plugin window and bidirectional settings updates were verified. Matching built-in code passed a saved-account idle rescue/fight/return-home test. Earlier releases exercised stock Skua/VibeSkua recovery; full new native WPF interaction, stock-client gameplay co-loading and overnight runs remain manual checks.
