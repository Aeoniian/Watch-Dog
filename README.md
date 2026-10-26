# Watch Dog

**A recovery plugin for [Skua](https://github.com/auqw/skua) and [VibeSkua](https://github.com/NinjaXz/VibeSkua):
keeps your accounts logged in and their scripts running while you're away.**

Watch Dog is the in-game part of [VibeSkuffle](https://github.com/Aeoniian/VibeSkuffle)'s watchdog, packaged as a
normal plugin. It needs no changes to your client: drop one file into the plugins folder.

> **Stable release: v1.0.0.** Earlier versions were tested in Skua 1.4.5 and VibeSkua 2.0.2 to 2.0.7 (private rooms): logged-out relog, connection lost,
> stuck login, stuck script and script restarts (both), Rescue Allies (VibeSkua, and a Skua account rescued by a
> VibeSkua one). VibeSkua 2.0.6 asks for a script's options before starting it; Watch Dog's script restarts
> use the saved options instead, so nothing waits on that window. VibeSkua 2.0.7 has its own auto-relogin: Watch Dog
> leaves an account alone after you click Log Out or Change Server, doesn't start a script a second time after
> VibeSkua already restarted it, and with Auto Relogin on it gives VibeSkua's relogin time to work before stepping in.
> (A script that logs out by itself while still running counts as a drop.) Report anything odd in [Issues](../../issues).

## New in v1.0.0 Stable

**Plugins → Rescue Allies** now opens a window with **Rescue Allies** and **Rescue Settings** tabs.
The settings tab adjusts the shared rescue delay (1–120 minutes) and death threshold (1–100).
Defaults remain 15 minutes or 10 deaths; either threshold requests help. The timer starts at the first
death and requires at least two deaths without a kill. A kill clears the streak and rescue call.
Edits apply to the current streak on the next watchdog check, across updated clients on this PC.
Whole numbers only; invalid input is not saved. Existing calls continue until a kill or another normal stop condition.

The plugin uses the host's native controls for WPF/Avalonia. If tabbed controls are unavailable, it falls
back to the host's two native options dialogs. Helpers now use AQW's proper house command when returning
to their own house. See [the stable release notes](docs/RELEASE-v1.0.0.md).

## New in Beta5

**Plugins → Rescue Allies** now has a shared switch that updates every open supported client and a separate
per-account helper opt-out. Idle accounts can rescue too. Preferences are shared with VibeSkuffle Beta9.5.
The new code builds against Skua 1.4.5 and passes the combined rescue tests; full stock-client gameplay and
native WPF interaction still need manual validation. See [the changelog](CHANGELOG.md).

---

## Install

1. Download `WatchDog.dll` from [Releases](../../releases).
2. Close the client, then put the file in your plugins folder:
   - **Skua:** `%APPDATA%\Skua\plugins`
   - **VibeSkua:** `%APPDATA%\VibeSkua\plugins`

   (Paste the path into File Explorer's address bar.)
3. Start the client. Watch Dog is on straight away; its settings are under **Plugins → View Plugins → Watch Dog →
   Options**, and **Plugins → Watch Dog log** opens what it has done.

The same file targets Skua (1.4.5 or newer) and VibeSkua (2.x).

**VibeSkuffle users:** you don't need this. VibeSkuffle has all of it (and more) built in. If the file is in the
shared plugins folder, Watch Dog notices it's running inside VibeSkuffle and stays off there.

---

## What it does

| Problem | What Watch Dog does |
|---|---|
| Stuck on "Authenticating Account Info…", "Connecting to game server…", "Loading Character Data…" | After 20 s: resets the login screen and logs in again, avoiding that server for 3 min. Slows down to one try a minute after 6 in a row and every 10 minutes after 12 (when AQW is down), but never gives up. |
| Logged out with nothing happening (auto relogin gave up, kicked, a failed login) | Logs back in after 3 minutes. By default only when a script was running, so logging out by hand is left alone. |
| "Communication with server has been lost" stays on screen | Logs back in after 1 minute. |
| A script stops doing anything (no kills, drops, quests, gold or moving for 15 min) | Logs out and back in. At most once every 20 minutes. |
| AQW spams "Quest Complete Failed: Missing Quest Progress" (5 in 2 min), or refuses the same quest 10 times | Logs out and back in (that clears it). At most once every 10 minutes. |
| A script was running when any of the above happened | Starts it again once the account has been back in game for a few seconds, then checks that it really runs (up to 3 tries). |
| Logged back in with no script to restart | Goes back to the room it was in, so ButlerV5 followers can find their master. |

It logs back in on the **same server** the account was on (unless that server is the one that got stuck).

### Rescue Allies (shared master off by default)

Open **Plugins → Rescue Allies** for two live checkboxes:

- **Rescue Allies - all accounts:** one shared switch for every updated Watch Dog and VibeSkuffle client on
  this PC. Changes apply immediately, including other open rescue-control windows.
- **Allow this account to rescue:** saved for the logged-in account. Turn it off to exclude a helper. Excluded
  accounts can still ask for help. The shared master never overwrites individual exclusions.

Helpers must be logged in on the same PC and server, with Watch Dog enabled. **They do not need a running
script or a loaded script.** Manual Auto Attack/Hunt is left alone.

A scripted account asks for help after the configured death count or rescue timer (defaults: 10 deaths or
15 minutes without a kill; the timed path requires repeated deaths). Eligible helpers
go to its room/cell and fight. When the caller gets a kill, the call expires, rescue is disabled, or the helper
opts out, they stop helping and return to their original room/cell. They give up after 20 minutes.

On hosts with script pause support, an existing script is paused and the same run resumes afterward. An
already paused script stays paused. Older Skua hosts without a pause API stop/restart only the original
running script. Idle helpers return idle. Manual Stop, a new script, logout or account/server change takes
precedence over automatic restoration. Helpers use their equipped class's skill slots without changing saved
skill rotations. Temporary quest-progress borrowing is restored afterward.

The shared switch and exclusions are stored in `%APPDATA%\Skua\WatchDog\rescue-settings.json`, using atomic
writes and live file notifications with a polling fallback. Both updated implementations use this same file.
Older plugin/client versions do not understand the new shared switch. Restart clients once after replacing
the plugin so the updated code is loaded. Rescue controls are saved immediately; there is no extra Save step.

### What it can't do
A plugin lives inside the client, so it goes down with it. Watch Dog can't recover from the client itself
crashing or freezing, or restart a client that keeps growing in memory. (VibeSkuffle can, because it's built in.)

---

## Settings

**Plugins → View Plugins → Watch Dog → Options** for ordinary recovery settings, saved per client folder.
Rescue controls are under **Plugins → Rescue Allies** and share their own settings across updated clients.

| Option | Default | |
|---|---|---|
| Enable Watch Dog | on | Everything on or off. |
| Fix stuck logins | on | The 20-second stuck-login fix. |
| Log back in after 3 minutes logged out | on | |
| ...only when a script was running | on | Turn off to always stay logged in. |
| Fix "Communication with server has been lost" | on | |
| Relog a script that does nothing (minutes, 0 = off) | 15 | |
| Relog on "Missing Quest Progress" spam | on | |
| Start the script again after a relog | on | |
| Go back to the same room after a relog | on | |
| Rescue Allies - all accounts | off | Plugins → Rescue Allies; shared live. |
| Allow this account to rescue | on | Plugins → Rescue Allies; saved per logged-in account. |
| Rescue delay (minutes) | 15 | Rescue Settings tab; shared, range 1–120. |
| Deaths before requesting rescue | 10 | Rescue Settings tab; shared, range 1–100. |

Everything Watch Dog does is written to `watchdog.log` in `plugins\WatchDog` (time and account name, never
passwords). Watch Dog keeps the account's login in memory only (never on disk), to fill in the login screen again.

---

## Building

Needs git and the .NET 10 SDK.

```
build.ps1        (Windows)
./build.sh       (Linux / macOS)
```

The output is `src/bin/Release/net10.0-windows/WatchDog.dll`. Watch Dog is compiled against Skua 1.4.5's interface
assemblies (the oldest version supported); VibeSkua 2.0.x has everything it uses, so the same file loads in both.
Those assemblies aren't stored here: the build script downloads Skua at a fixed commit into `.deps` and builds them.

---

## Credits and license

- **[Skua](https://github.com/auqw/skua)** by the auqw team and contributors: SharpTheNightmare (Purple), d3lfs
  (slypy), wtffidy (Tato), BrenoHenrike, l0newolf12, blackarrow123, LordExelot, robmart and Lucas Correa.
- **[VibeSkua](https://github.com/NinjaXz/VibeSkua)** by NinjaXz, with wtffidy (Tato) and ArrowDev (BlackArrow).
- **ButlerV5** by l0newolf12 (lonewolf_12).
- Rescue Allies and the recovery logic come from [VibeSkuffle](https://github.com/Aeoniian/VibeSkuffle).
- Watch Dog itself is MIT licensed (see [LICENSE](LICENSE)).

Not affiliated with Artix Entertainment, Skua or VibeSkua. **Using bots breaks AQW's Terms of Service and can get
accounts banned. Use at your own risk.**
