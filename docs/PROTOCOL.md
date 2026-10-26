# Half Grip Multiplayer — Wire Protocol (v6)

Private Steam friends/private lobbies or LAN UDP `:27777`. Host = lobby owner / LAN host.

## Transport

- **Steam:** Facepunch `SteamNetworking` P2P on channel `0`.
- **LAN:** UDP (Ready / RaceStart / RaceSetup re-pulsed).

## Messages

| Id | Name | Payload |
|----|------|---------|
| 1 | Hello | byte ver; ulong selfId; string name |
| 2 | Pose | ulong; float3; float4; speed; hp; time |
| 3 | Chat | string |
| 4/5 | Ping/Pong | seq + times |
| 6 | Ready | byte |
| 7 | Damage | float; float3 |
| 8 | Wreck | — |
| 9 | RaceStart | float hostTime |
| 10 | RaceAbort | — |
| 11 | LoadScene | string |
| 12 | RaceSetup | seed; short; variant; laps; weather; map; scene; **objKind; objAmount; objTarget; music; ceasefire; selectedNode** |
| 13 | SlotMap | count × (peerId, slot) |
| 14 | BotPose | count × bot poses |
| 15 | CarLook | skinIndex; 3×rgb; asset; chassis |
| 16 | RaceEnd | place; survived; objectiveMet; wrecked; quit |
| 17 | Loadout | count × upgrade id |
| 18 | GunFx | origin; target |
| 19 | MapSelect | selected; current; races; lives |
| 20 | ShopState | money; offerKey |
| 21 | LapState | lap; progress; place; finished |
| 22 | AmmoFx | pos; magazines |
| 23 | NitroFx | on; power |
| 24 | ResetFx | pos; rot |
| 25 | BossKill | — |

**Hello version** = `6`.

## Authority

- Host: RaceSetup (incl. objective/music/ceasefire), SlotMap, BotPose, RaceStart/End, MapSelect, ShopState, BossKill, hazard/enemy RNG seed.
- Each peer: Pose, CarLook, Loadout, GunFx, LapState, AmmoFx, NitroFx, ResetFx, Ready.
- Guests apply host RaceEnd (load UI) and destroy synced ammo boxes; gun tracers are cosmetic.
