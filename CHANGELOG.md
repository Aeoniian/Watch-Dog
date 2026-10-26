# Changelog

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

