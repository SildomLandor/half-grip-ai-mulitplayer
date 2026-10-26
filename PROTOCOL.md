# Half Grip Multiplayer Protocol

**Version:** 8 (plugin 0.8.0)

## Transport

- Steam P2P (Facepunch) — reliable / unreliable channels
- LAN UDP `:27777` — unreliable raw; reliable = `0xFE` + uint seq + payload, ack `0xFD` + seq (resend ~120 ms, TTL 8 s)

## Economy (per-player)

`ShopState` is **ignored**. Money, shop offers, inventory and equipped loadout authority stay local.
`Loadout` is visuals-only for remote ghosts. `RaceEnd` banks each client's own `ShopManager.addRaceGain`.
`RunSnap` shares the run map graph only — never wallets.

## MsgType

| Id | Name | Notes |
|----|------|-------|
| 1 | Hello | ver + selfId + name |
| 2 | Pose | owner pose/hp |
| 3 | Chat | |
| 4/5 | Ping/Pong | |
| 6 | Ready | lobby / race gate |
| 7 | Damage | PvP |
| 8 | Wreck | |
| 9/10 | RaceStart / RaceAbort | |
| 11 | LoadScene | host scene |
| 12 | RaceSetup | seed, weather, laps, objective… |
| 13 | SlotMap | peer → grid slot |
| 14 | BotPose | host AI |
| 15 | CarLook | paint/chassis |
| 16 | RaceEnd | host signal; local payout |
| 17 | Loadout | remote visuals only |
| 18 | GunFx | tracer + sound |
| 19 | MapSelect | run graph indices |
| 20 | ShopState | **compat only — ignored** |
| 21 | LapState | → LapManager leaderboard |
| 22 | AmmoFx | |
| 23 | NitroFx | on/off flames |
| 24 | ResetFx | |
| 25 | BossKill | |
| 26 | RunSnap | JSON run graph |
| 27 | DentFx | |
| 28 | TireFx | wheel burst |
| 29 | ShieldFx | BulletShield.Block |
| 30 | CollideImpulse | peer RB push |
| 31 | CutoffState | RaceCutoff seconds left |
