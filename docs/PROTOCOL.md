# Half Grip Multiplayer — Wire Protocol (v5)

Private Steam friends/private lobbies or LAN UDP `:27777`. Host = lobby owner / LAN host.

## Transport

- **Steam:** Facepunch `SteamNetworking` P2P on channel `0` (`NetProtocol.SteamChannel`).
- **LAN:** UDP datagrams (no reliability — Ready / RaceStart / RaceSetup are re-pulsed).

## Packet framing

First byte = `MsgType`, then payload (`BinaryWriter` / little-endian). Strings: `ushort` UTF-8 length + bytes.

| Id | Name | Payload |
|----|------|---------|
| 1 | Hello | byte version; string name |
| 2 | Pose | ulong ownerId; float3 pos; float4 rot; float speed; float hp; float time |
| 3 | Chat | string text |
| 4 | Ping | uint seq; float clientTime |
| 5 | Pong | uint seq; float clientTime; float serverTime |
| 6 | Ready | byte ready |
| 7 | Damage | float amount; float3 point |
| 8 | Wreck | (empty) |
| 9 | RaceStart | float hostTime |
| 10 | RaceAbort | (empty) |
| 11 | LoadScene | string sceneName (`SCENE_TRACK_*` or `SCENE_UI_*`) |
| 12 | RaceSetup | int spawnSeed; byte shortRace; byte trackVariant; int maxLaps; string weather; string map; string scene |
| 13 | SlotMap | byte count; repeated (ulong peerId, byte slot) |
| 14 | BotPose | byte count; repeated (byte index; float3; float4; float speed; float hp) |
| 15 | CarLook | byte skinIndex; float3×3 colours; string assetName; string chassisName |

**Version byte** in Hello = `5`.

## Car look

Each peer broadcasts `CarLook` on Hello, track ready, paint/`ApplySkin`/`Refit`, and at ~1 Hz when dirty. Guests apply it to the claimed enemy `CarSkinController` (`skinIndex` + paint; optional chassis/`CarSkin` asset by name).

## Lobby flow

1. Host opens new/continue run → UI scene (`SCENE_UI_*`) broadcasts via LoadScene; guests follow.
2. Everyone configures loadout/map locally, presses **Ready** in the F6 overlay.
3. Host **Start Race** is Harmony-gated until `AllPeersReady` (guests cannot press Start).
4. `RunState.BeginRace` → host broadcasts **RaceSetup** (weather, short/laps, variant, spawn seed) then track LoadScene.
5. Track: shared `Random.InitState(spawnSeed)` before `SpawnEnemies`; local car teleported to **grid slot**; ghosts claim enemy nearest their slot; host streams **BotPose** for remaining AI.
6. `RaceCountdown` still waits for shared **RaceStart** once both are on the track.

## Grid slots

- Slot `0` = `PlayerPosition` (host).
- Slots `1+` = enemy start markers (guests, join order).
- Fixes both players stacking on PlayerPosition (P1 looked like they sat in P2’s seat).

## Authority

- Each peer owns local player pose; remotes are ghost / claimed bot slots.
- Host owns AI bot poses, RaceSetup, RaceStart, SlotMap.
- Do **not** call `SteamClient.Init` if already valid (game `SteamIdentity`).
