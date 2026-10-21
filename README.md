# Watch Dog

**A recovery plugin for [Skua](https://github.com/auqw/skua) and [VibeSkua](https://github.com/NinjaXz/VibeSkua):
keeps your accounts logged in and their scripts running while you're away.**

Watch Dog is the in-game part of [VibeSkuffle](https://github.com/Aeoniian/VibeSkuffle)'s watchdog, packaged as a
normal plugin. It needs no changes to your client: drop one file into the plugins folder.

> **Status: beta.** Tested in Skua 1.4.5 and VibeSkua 2.0.2 (private rooms): logged-out relog, connection lost,
> stuck login, stuck script and script restarts (both), Rescue Allies (VibeSkua, and a Skua account rescued by a
> VibeSkua one). VibeSkua 2.0.6 asks for a script's options before starting it; Watch Dog's script restarts
> use the saved options instead, so nothing waits on that window. Report anything odd in [Issues](../../issues).

---

## Install

1. Download `WatchDog.dll` from [Releases](../../releases).
2. Close the client, then put the file in your plugins folder:
   - **Skua:** `%APPDATA%\Skua\plugins`
   - **VibeSkua:** `%APPDATA%\VibeSkua\plugins`

   (Paste the path into File Explorer's address bar.)
3. Start the client. Watch Dog is on straight away; its settings are under **Plugins → View Plugins → Watch Dog →
   Options**, and **Plugins → Watch Dog log** opens what it has done.

The same file works in both Skua (1.4.5 or newer) and VibeSkua (2.0.x).

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

### Rescue Allies (off by default)
When an account running a script keeps dying (10 deaths without a kill, or 15 minutes of only deaths), every other
account on the same PC **and server** that is running a script and has *Rescue Allies* on stops its script, goes to
that room and cell, and fights there. As soon as the stuck account gets a kill they go back to where they were and
their scripts start again. They give up after 20 minutes. If the boss is hidden behind a questline the helpers haven't done,
they borrow the stuck account's quest progress while helping (client-side only) and put their own back exactly as it
was afterwards, so their own scripts aren't fooled. Skua, VibeSkua and VibeSkuffle accounts can help each other.

### What it can't do
A plugin lives inside the client, so it goes down with it. Watch Dog can't recover from the client itself
crashing or freezing, or restart a client that keeps growing in memory. (VibeSkuffle can, because it's built in.)

---

## Settings

**Plugins → View Plugins → Watch Dog → Options**. Saved per client folder (Skua and VibeSkua each have their own).

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
| Rescue Allies | off | |

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
