# Half Grip Multiplayer Protocol

**Version:** 40 (plugin 0.10.27-dev)

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
| 2 | Pose | owner pose/hp + steerDeg + flags(brake|hb|rev|exh|trails|air|nitro) + engineIndicator(v40) |
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
| 18 | GunFx | tracer + sound + shooterPeer (u64) |
| 19 | MapSelect | run graph indices |
| 20 | ShopState | **compat only — ignored** |
| 21 | LapState | UI cache only (do not write LapManager) |
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
| 32 | BlastFx | barrels / AreaBlast |
| 33 | RocketFx | AI rocket spawn |
| 34 | PickupFx | health/effect tombstone |
| 35 | PeerLeft | peer disconnect notify |
| 36 | HornFx | claxon on/off |
| 37 | WorldPing | map marker |
| 38 | MoneyFx | cash pickup tombstone (cash stays local) |
| 39 | DestructibleFx | smash props (payout local) |
| 40 | JumpFx | jump button hop |
| 41 | TrainSnap | host train HeadDistance |
| 42 | GearFx | gear + inGear |
| 43 | TotemFx | phoenix save flash |
| 44 | RunSnapPart | chunked run graph (MTU-safe) |
| 45 | SlowMoFx | visual only (no shared timescale) |
| 46 | DriftFx | drift spark cue |
| 47 | SpunOutFx | spin-out wobble |
| 48 | CarDamagedFx | damage flash on ghost |
| 49 | AmmoState | magazine count for nameplate |
| 50 | ExhaustFx | exhaust plume on/off |
| 51 | MagnetFx | magnet lever active |
| 52 | GlueFx | glue grip active |
| 53 | SpikeFx | pierce point+dir |
| 54 | GlassFx | windshield shatter |
| 55 | TrailFx | tire trails/smoke |
| 56 | TreeFx | fellable tree index+push |
| 57 | MudFx | mud puddle splash |
| 58 | TrapFx | road spikes hit |
| 59 | HammerFx | head/spinner hammer |
| 60 | KillFeed | toast string (AI kill / totem) |
| 61 | LeechFx | nitro leech pulse |
| 62 | BurnFx | low-HP smoke on ghost |
| 63 | InvertFx | time-invert flip pulse |
| 64 | ComboFx | combo name+value toast |
| 65 | CheckpointFx | checkpoint / start-finish |
| 66 | GuillotineFx | ammo guillotine chop |
| 67 | DrainFx | drain-nitro bite |
| 68 | TrampleFx | wheat crush stamp |
| 69 | ExplodeFx | small/big car explosion |
| 70 | BotFlameFx | host AI nitro flames by bot index |
| 71 | AmmoEatFx | ammo-eater feed |
| 72 | ObjectiveFx | objective complete toast |
| 73 | TreeKickFx | live tree kick lean |
| 74 | RefillFx | effect refill pulse |
| 75 | ThreatFx | AI targeting peer |
| 76 | CanisterFx | canister take-refill |
| 77 | CamFx | cabin/exterior camera toggle |
| 78 | HbLockFx | handbrake lock toggle |
| 79 | ShortcutFx | lap shortcut metres |
| 80 | EarnFx | money earned toast (wallet local) |
| 81 | BlinkFx | shift blinker pulses |
| 82 | HintFx | wrong-way / stuck |
| 83 | CabinAmmoFx | cabin ammo grab/drop |
| 84 | AimFx | gun aim enter/exit |
| 85 | MagFx | magazine seated/removed |
| 86 | PauseFx | PauseRace / Resume |
| 87 | CutoffSay | RaceCutoff.Say line (host) |
| 88 | GunActFx | shell/breakOpen/breakClose/eject/revSpin |
| 89 | SmokeFx | player car smoke on/off |
| 90 | GlassCrackFx | windshield crack before shatter |
| 91 | WeatherFx | host WeatherController.ApplyProfile |
| 92 | HbGrabFx | handbrake assembly grab pulse |
| 93 | HealFx | CarLink.Heal amount |
| 94 | ShiftHitFx | money-shift overrev / stall |
| 95 | TrainMoveFx | host train Start/StopMoving |
| 96 | KillAmmoFx | MagazineManager.GrantForKill (delta) |
| 97 | FeatFx | Feats.Award toast |
| 98 | GunTypeFx | GunLoadoutActivator.Apply |
| 99 | ShiftTypeFx | ShifterLoadoutActivator.Apply |
| 100 | ShellFx | NShell grab/release |
| 101 | SlideFx | auto gun slide pull/return/lock |
| 102 | ClipFx | NBulletClip rounds taken |
| 103 | UnlockFx | ItemUnlocks.Unlock toast |
| 104 | CamLookFx | CameraSwitch.Release look-around |
| 105 | RepairFx | ClearDamage / RepairRun |
| 106 | GrimeFx | AddGrime amount |
| 107 | MagShotFx | NMagazine.TakeBullet pulse |
| 108 | WindshieldFx | CarSkinController.SetWindshield |
| 109 | ObjSayFx | RaceObjectiveAnnouncer.Say (host) |
| 110 | SteerLockFx | NSteeringWheel.ToggleLock |
| 111 | RecenterFx | NSteeringWheel.StartRecenter |
| 112 | MatchRevFx | CarLink.MatchRevs pulse |
| 113 | RecordFx | LifetimeStats.RecordTime PB |
| 114 | LapAheadFx | LapShield CallFarAhead (host) |
| 115 | NosUpFx | NO2.ApplyUpgrade |
| 116 | PartsFx | CarLink.RefreshParts |
| 117 | ShakeFx | Shaker.KickSpring → ghost wobble |
| 118 | ChickenFx | chicken kill count toast |
| 119 | ChickenGoneFx | remove nearest chicken at pos |
| 120 | ItemUseFx | ItemActivated pulse |
| 121 | ModelFx | CarModelChanged + CarLook refresh |
| 122 | TornadoSnap | host tornado positions (clients skip pathing) |
| 123 | BarrelFx | host BarrelSpawner spawn → remote instantiate |
| 124 | DiceFx | DiceCharm roll outcome (0 hurt / 1 heal / 2 money) |
| 125 | BeerFx | BeerBottle drink toast |
