# Watch Dog v1.0.3

The standalone recovery plugin gets the Rescue Allies changes suggested by **Noxus2k-AQW** (GitHub discussion "Watch-Dogs Adjustments", Watch Dog 1.0.24 and 1.0.25), plus a hold limit and a Boss switch. One DLL targets Skua 1.4.5+ and VibeSkua 2.x.

## Changes since v1.0.2

- **Accounts tab** (Noxus2k-AQW): lists the Watch Dog accounts active on this PC with their live rescue status, a Can help switch per account and a Request Help button. Watch Dog must be loaded on each account; open the window on any one account to see and control the others.
- **Boss class and gear** (Noxus2k-AQW): helpers use the class and skill mode of their CoreBots Boss selection, and the selected Boss gear when enabled, and put their own setup back afterwards. They leave combat before changing it.
- **Helper cooldown** (optional, default on, 10 minutes, 1-30) and **helper death limit** (default 5, 1-100) (Noxus2k-AQW).
- A rescue request stays active while the caller is dead and respawns in the same map; helpers stay in the map until the caller leaves it (Noxus2k-AQW).
- **Hold limit** (new): helpers are released after 30 minutes by default without a kill or death of the caller. 0 means no limit.
- **Boss class and gear switch** (new, default on): turns the Boss setup off, so helpers keep what they have equipped.
- The "One Time Quest Only" rule from v1.0.2 is unchanged.

## Install / update

Close the stock client, replace `WatchDog.dll`, then restart:
- Skua: `%APPDATA%/Skua/plugins`
- VibeSkua: `%APPDATA%/VibeSkua/plugins`

**VibeSkuffle v1.0.5 already includes these changes** and does not need this DLL.

## Verification and limits

Built against the pinned Skua 1.4.5 interfaces. The Boss equip and restore paths, the Accounts tab and the new hold limit and Boss switch were not run live with real accounts, and the plugin cannot restart the client (VibeSkuffle's built-in Watch Dog and the Rust rewrite can).
