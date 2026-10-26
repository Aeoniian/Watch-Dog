# Changelog

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

