# GungeonTogether

This experimental continuation is based on the original
[GungeonTogether project](https://github.com/llamerrr/GungeonTogether) by lam
and contributors. It remains available under the included MIT License.

GungeonTogether is a multiplayer mod for Enter the Gungeon that allows online co-op play using Steam P2P networking.

The current session size is three players: one host and up to two clients.

Modded sessions are supported when every player uses the same BepInEx plugins,
plugin versions and registered ModTheGungeon items. Incompatible clients are
rejected with the first detected manifest difference.

Version 0.9.0 keeps the party in one room and feeds the native co-op camera the
whole three-player bounding box. It synchronizes enemies in every active room,
enables the network down/revive loop, makes ground pickups and shared money/keys
host-authoritative, rejects different generated layouts safely, measures real
round-trip latency, and transmits the actual gun aim direction.

Version 0.8.3 added the optional Isaac-style three-player HUD. Version 0.8.2
added shared run seeds, layout fingerprints and safe floor-load fallbacks.

Version 0.8.1 fixes the black/over-zoomed client view during floor transitions by
parking persistent remote players beside the local player until the synchronized
dungeon is ready, and retries room placement when generation finishes late.

Version 0.8.0 replaces visual-only remote sprites with network-controlled real
`PlayerController` instances. Remote players now participate in the built-in
co-op camera, collision, room, enemy targeting, inventory and floor-transition
systems. It also fixes the lobby player-list construction error, makes the
connection handshake idempotent and removes per-second packet-poll log spam.
Version 0.6 added persistent remote players across scene changes, smoother 20 Hz
player snapshots, stable host-driven floor transitions, exact modded pickup
identity matching, and safer gameplay patches. Version 0.5 added host-authoritative
enemy projectiles, exact projectile matching for multi-shot/charged guns, beam synchronization, and a locked run roster. New
players may join in the foyer; after the run starts, only a disconnected member
of that run may reconnect.

> [!WARNING]
> _THIS MOD IS STILL VERY EARLY IN DEVELOPMENT - DO NOT EXPECT A FUNCTIONAL EXPERIENCE RIGHT NOW_

WIP WIP WIP WIP WIP WIP pls be nice 

> [!WARNING]
> ON HOLD, HAD SOME HUGE UNEXPECTED LIFE THINGS GET IN THE WAY, DO NOT WORRY, THIS MOD IS STILL IN DEVELOPMENT AND WE ARE STILL EXTREMELY EXCITED ABOUT THE FUTURE OF GUNGEONTOGETHER

<p align="center" href="https://github.com/llamerrr/GungeonTogether">
<img height="320" alt="image" src="https://github.com/user-attachments/assets/6c485240-5e3c-44ee-93bb-747d27a0731e"/>
</p>

The mod is free (because I'm not weird). however I'm broke asf so feel free to donate for my INSANELY INTENSE AND HARD LABOUR (jk)
My paypal is llamerrr1@gmail.com if you actually want to (for some reason)

## Contributers 
<p align="center" href="https://github.com/llamerrr/GungeonTogether">
<img height="100" alt="image" src="https://avatars.githubusercontent.com/u/47313866?v=4"/>
<img height="100" alt="image" src="https://avatars.githubusercontent.com/u/88169809?v=4"/>
</p>

> [!NOTE]
> As this mod runs through the steam network, it only works with the steam verison of the game as steamID is required

# Features
## What works so far
| System | Status | Notes |
|:---:|:---:|:---:|
| Steam invites/lobby system | 🟩 Done | Steam lobby creation and joining functional |
| Steam P2P networking | 🟨 Working | Real connections and Steam invites working |
| Three-player HUD | 🟨 Working | Isaac-style corner HUD; F8 switches to the native HUD |
| Player Synchronization | 🟨 Working | Real remote PlayerControllers with 20 Hz movement and animation sync |
| Three-player co-op | 🟨 Working | Party room tether, three-player bounds camera and real PlayerControllers |
| Enemy Synchronization | 🟨 Working | Host-driven enemies across active rooms, including state and projectiles |
| Weapon Synchronization | 🟨 Working | Regular, multi-shot, charged projectile and beam replication |
| Reconnection | 🟨 Working | Run roster locks after leaving the foyer; prior members can reconnect |
| Dungeon Synchronization | 🟨 Working | Shared seed, layout fingerprint and safe mismatch rejection |
| Items/economy | 🟨 Working | Host-authoritative ground pickups, shared keys and money |
| Down/revive | 🟨 Working | Nearby living teammate revives a downed player after three seconds |
| Singleplayer | 🏁 Finished | Yippeeee!!!!! |

## Planed for 1.0 release
- Online co-op multiplayer for Enter the Gungeon
- Steam P2P networking (no dedicated servers required), no parsec or streaming required
- Real-time synchronisation of
  - Players
  - Enemies
  - Projectiles
  - Dungeon
- Debug controls for testing
- Spectate when dead
- Enemy scaling (set difficulty in game)
  
## Planned for the future
- EPIC GAMES SUPPORT (EOS P2P)
- Unlimited players
- EVERYTHING synced
- Custom Game Modes
  - Race mode (competing in identical dungeons, first player to finish wins)
  - Mirror mode (identical dungeons, can hurt one another but can't see eachother, shooting is synced across dungeons)
- Teammate revive items
- Team based buffs and items

## Installation
1. Install BepInEx for Enter the Gungeon
2. Copy `GungeonTogether.dll` to `[ETG Install]/BepInEx/plugins/`
3. Launch Enter the Gungeon

## Building
This only applies to people who want to build from source, or if we forget to post builds :)
1. You will need to setup BepInExPack_ETG (either with a mod manager like r2ModMan or manually)
2. Place the required game, BepInEx and ModTheGungeon reference assemblies in `GungeonTogether/Libs/`
3. Run `dotnet build GungeonTogether/GungeonTogether.csproj -c Release`
4. Launch Enter the Gungeon

## Technical Details
Built with BepInEx framework using Steam P2P networking for a seamless multiplayer experience.
Unfortunately if you play the game through epic or windows store this mod will not work, as it runs through steams p2p api only, which means you need a steamID for it to work. Sorry guys...
(EPIC COMING SOON)

> [!NOTE]
> Wiki!!!
> https://deepwiki.com/llamerrr/GungeonTogether
> Thanks to https://github.com/TheExploration

# 1.0 release coming one day!!!!!!
