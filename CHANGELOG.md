# Changelog

## v1.0.5 (2026-11-08)

- **Empty room fix that triggers** (v1.0.4 did not for the reported case): a map with no monsters at all now also counts as an empty room, not only a character that is not loaded. After the configured time (90 seconds) with a script running, Watch Dog jumps to the current cell, twice, and the third time rejoins the room if the character is not loaded. It logs when it first suspects an empty room. Reported with a video by Ramon Douglas.

## v1.0.4 (2026-11-08)

- **Empty room fix** (new option "Fix a room that never loaded", 90 seconds, 0 = off): a script is running but the character stays "not loaded" in its room (no avatar, no monsters; a rare AQW load glitch) for that long: jump to the current cell, then join the room again (3 tries).

## v1.0.3 (2026-11-07)

- **Hold limit** (new): helpers are released after 30 minutes by default without a kill or death of the caller (0 = no limit). Shared with VibeSkuffle and the Rust client.
- **Boss class and gear switch** (new): turns the helpers' CoreBots Boss class and gear on or off (default on). Shared with VibeSkuffle and the Rust client.
- Changes by **Noxus2k-AQW** from the GitHub discussion "Watch-Dogs Adjustments" (Watch Dog 1.0.24 and 1.0.25), taken over as they were; thank you! The "One Time Quest Only" rule of v1.0.2 is unchanged:

- Added an Accounts tab that lists active Watch Dog accounts on this PC, shows live rescue status, and routes manual help requests to the selected account through a shared local file bridge. Watch Dog must be loaded on each account; only one control window needs to be open.
- Added per-account helper eligibility to the Accounts tab, removed the duplicate Request Help button from Rescue Allies, and made the cooldown amount entry appear only while the cooldown is enabled.
- Rescue helpers now use the class and skill mode from their per-account Skua CoreBots Boss selection, and apply selected Boss equipment slots when enabled. Their prior equipped items and skill provider are restored when rescue ends. With no Boss class/loadout configured, the current setup is retained.
- Made the shared helper cooldown optional. It defaults to on at 10 minutes and can be adjusted from 1 to 30 minutes; turning it off hides the amount and removes the repeat-help wait.
- Added a shared helper death limit, default 5 and adjustable from 1 to 100. Helpers now stop attacking, clear their target, and request combat exit before restoring their previous setup.
- Kept a rescue request active through the requesting account's death and respawn while it remains in the same map; logout or map departure ends the call. The account status stays visible during the respawn grace period.
- Removed the duplicate live status display from Rescue Allies; per-account status remains in Accounts. Reworked the cooldown control into one row and show its minutes field only when enabled.
- Removed the nonfunctional Copy button from Watch Dog Log; its read-only text can now be selected and copied directly. Kept log selection stable while new lines arrive.
- Logged each helper's selected Boss class, skill mode, and dedicated gear slots. Equipment setup and restoration now skip items already equipped, wait for Skua's equip cooldown, confirm gear changes with added spacing, and report the items confirmed or failed during restoration.

## v1.0.2 (2026-11-03)

- **"One Time Quest Only" spam:** 8 of those answers within a minute (a script keeps asking for a quest that can only be done once) make Watch Dog log out and back in. New option "Relog on \"One Time Quest Only\" spam" (on by default). If it comes back within 15 minutes of that relog, the plugin relogs again and says in its log that it cannot restart the client; VibeSkuffle (and the Rust rewrite) restart the whole client in that case. See [the release notes](docs/RELEASE-v1.0.2.md).

## v1.0.1 (2026-10-28)

- **Plugins → Rescue Allies** shows live rescue status, refreshed every second: whether Rescue Allies is on, deaths against the threshold, time since the first death against the timer, whether help has been requested, and whether this account can request help (it needs an active, unpaused script).
- Shows whether this account is helping, requesting, recovering or waiting for the next scan, the current phase (joining, fighting, returning and restoring its original state), the last outcome, and the remaining cooldown for each requester helped recently.
- **Script restart fallback** (same logic as VibeSkuffle's built-in Watch Dog): if the account is back in game after VibeSkua's own re-login but the script is not running, Watch Dog starts it after 30 seconds steady in game, once; never after a deliberate logout.
- The status is read-only (no game calls, no saved-preference writes) and reads "unavailable" when the account is offline, loading or recovering. Older fallback dialogs show a status snapshot in the title instead of a live panel.
- The built-in Watch Dog in VibeSkuffle uses the same format. See [the release notes](docs/RELEASE-v1.0.1.md).

## v1.0.0 (2026-10-26) — Stable

- Added a separate Rescue Settings tab alongside Rescue Allies using the host's native WPF/Avalonia option views.
- Adjustable shared rescue delay (1–120 minutes) and death threshold (1–100), applied live across updated clients. Defaults remain 15 minutes or 10 deaths; either threshold requests help, with at least two deaths required for the timer path.
- Existing shared settings migrate without changing the master switch or account exclusions. Invalid values are not saved, and bulk option snapshots cannot overwrite the new settings.
- Threshold edits apply to the current death streak. A kill resets the streak and request; existing active calls continue through threshold edits.

- Fixed rescue returns to the helper's own house using AQW's house command; normal map transfers cannot enter house rooms.

Validation: 68 rescue checks, 19 installer/army-feedback checks, and 33 updater/drop/Butler/context-label regression checks pass. Actual rendered client views verify uniform drop buttons at 420/620 px, the built-in settings tab, and a two-tab native Avalonia plugin window with bidirectional settings synchronization. Saved-account private-room idle rescue, real combat, return to its starting house and remaining idle pass. The clean reconstructed upstream client builds. Full native WPF interaction, multiple real-account Army commands and overnight runs remain manual checks.


## v1.0.0-beta5 (2026-10-25) - shared Rescue Allies and idle helpers

- Shared Rescue Allies master switch across updated Watch Dog and VibeSkuffle clients, with live control updates.
- Separate saved per-account helper eligibility; excluded accounts can still receive help.
- Idle logged-in accounts can rescue without a running/loaded script.
- Preserve script state: pause/resume the same run where supported, keep paused scripts paused, restart only an original running script on older Skua hosts, and return idle helpers idle.
- Respect manual stops, new scripts, logouts, account/server changes and manual combat stops.
- Restore original room/cell and combat preferences; exact-room transfers work between private rooms of the same map, with bounded retries for transfer cooldowns.
- Shared preferences use atomic writes, a cross-process mutex, file notifications and polling fallback. Defaults: master off, account eligibility on.
- Keep legacy watchdog recovery aware of a script temporarily stopped for rescue; clean old calls on account changes.

Validation: 43 targeted rescue checks, 31 existing regression checks, client/Manager builds and a Watch Dog build against Skua 1.4.5 interfaces pass. The matching VibeSkuffle implementation's saved-account idle rescue joined a private room, killed a real monster, returned to its exact original private room and remained idle. Full stock-client game co-loading and native WPF rescue-window interaction have not been exercised in this session.

