# Half Grip Demo ? Multiplayer Mod Journal

## Intake (2026-10-26)

- **Game:** Half Grip Demo (Steam AppID 4163360, parent 4163270)
- **Install:** `C:\Program Files (x86)\Steam\steamapps\common\Half Grip Demo\`
- **Company/product:** Half Games / HalfGrip (`app.info`)
- **Engine:** Unity Mono (`HalfGrip_Data/Managed/`, `MonoBleedingEdge`)
- **Idea (one sentence):** Private Steam-friends (or LAN) co-op race with a synced remote car ghost, then pilot/co-pilot and lobby polish.
- **Done means:** BepInEx plugin that hosts/joins a private lobby, syncs a second player's car transform/basic state in a real race, verified with screenshots/logs; README for install.
- **Ownership:** User owns the game. Private user-hosted sessions only. No anti-cheat bypass. No redistributing game/decomp files.

## Sources (outside repo ? do not ship)

- Decomp: `c:\Users\Sildom Landor\Documents\half grip\Scripts\Assembly-CSharp\`
- Assemblies: `c:\Users\Sildom Landor\Documents\half grip\Assemblies\`
- Assets dump: `c:\Users\Sildom Landor\Documents\half grip\Assets\`

## Working folder

`c:\Users\Sildom Landor\Documents\half-grip-multiplayer\`

## Recon notes (initial)

- `Facepunch.Steamworks.Win64.dll` present in Managed. `SteamIdentity.cs` already calls `SteamClient` with AppID 4163360 (`SteamClient.Init(4163360u, asyncCallbacks: false)`). No other Steam lobby/net code in Assembly-CSharp (SP only).
- No BepInEx in install yet (`BepInEx` folder absent).
- **Unity version:** 6000.3.22f1 (Unity 6) ? Mono managed (`HalfGrip_Data/Managed`).
- **dotnet SDK on machine:** 8.0.425
- **Saves/config:** `%USERPROFILE%\AppData\LocalLow\Half Games\HalfGrip\` (confirm contents before backup)
- Key classes seen: `CarControllerNew` (physics/input), `GameManager` (singleton, race state), `RaceStarter` (countdown sprites; Update empty in dump), `SteamIdentity`, `Player*` helpers, `CarSkinController.isPlayer`, `CarLink` (player car link in TutorialFlow), `EnemyController` + `IEnemyBehaviour` / `DriverBehaviour` for AI.
- Official EA plans PvP/co-op; demo is SP-only. Steam page lists LAN Co-op as planned.
- **um CLI:** not installed; Python/uv/pipx missing ? install later or work without `um` for backup/manual steps.

## Chosen route

**BepInEx 5 (Mono) + Harmony + Facepunch.Steamworks (already shipped) for Steam lobbies / SteamNetworkingSockets.** Private friends lobbies; optional LAN fallback later. Plugin deploy via copy script into `BepInEx/plugins/` ? avoid shipping game DLLs.

Reason: managed Unity Mono, Steamworks already initialized by game, no community loader present, multiplayer needs code hooks not data packs.

## Lab / safety

- Saves/config: `%USERPROFILE%\AppData\LocalLow\Half Games\HalfGrip\` (currently mostly `Player.log` / `Player-prev.log`; progress may be PlayerPrefs)
- Backup restore path: `c:\Users\Sildom Landor\Documents\half-grip-multiplayer\backups\HalfGrip-LocalLow-2026-10-26\` (copied 2026-10-26; contained Player.log + Player-prev.log only)
- Windowed mode preferred for screenshots
- Never commit decomp or Managed game assemblies

## Tooling

- `um` CLI: **missing** ? Python/uv/pipx absent; winget can install Python.Game worker / later: optional `winget install Python.Python.3.12` then pip install um
- Plugin project: TBD under this folder (offline worker in progress)
- BepInEx NuGet via `dotnet add package` when scaffolding

## Next

1. Offline worker: deep recon + protocol + plugin scaffold (IN PROGRESS: agent 84c79e80 ? interrupted/resumed 11:41 to force finish; dirs empty of source as of orchestrator check)
2. ~~Backup LocalLow~~ DONE ? `backups/HalfGrip-LocalLow-2026-10-26/`
3. Game worker: install BepInEx (user consented via mod request), deploy, launch windowed, verify lobby UI + LogOutput

## REFINE LATER

- Full physics authority sync, weapon sync fidelity, dedicated server, public matchmaking (out of scope ? private only)

## Offline batch ? vertical slice scaffold (2026-10-26)

### Recon facts (decomp, verified)

- **Player car discovery:** TrackManager.playerCarController (scene ref); CarLink.Start sets cc.isPlayer = true; find via FindObjectsByType<CarControllerNew> where isPlayer, or TrackManager.
- **AI cars:** TrackManager.SpawnEnemies instantiates enemyCarPrefab / RunConfig.devCars under enemiesContainer; each has CarControllerNew + EnemyController (writes steeringAngleDeg / accel). Visual clone possible via mesh instantiate + strip controllers (implemented in GhostCar).
- **Input path:** Player drive is **CarLink.FixedUpdate** ? GameInput.Steer / W/S ? cc.steeringAngleDeg, pedalInput, engineAccel, handBrake. Weapons: Gun.Update ? GameInput.HandDown(1) fire. Ghost must disable CarLink/EnemyController/Gun (GhostCar.StripGameplay). Pilot/gunner split: Harmony on GameInput.GetMouseButton + nitro bump (Patches.cs) ? unproven in-game.
- **Race:** TrackManager.RaceCountdown / 
aceStarted; GameManager.raceOver + RaceFinished; RaceStarter Update empty ? countdown driven by TrackManager. No net RaceSync in v0.1.
- **Networking stubs in game:** none (Unity.Multiplayer.Center.Common is package stub only). Steam: SteamIdentity Init 4163360; Facepunch types include Lobby, SteamMatchmaking, SteamNetworking (P2P), SteamNetworkingSockets, SocketManager, ConnectionManager.
- **Transport choice (v0.1):** classic **SteamNetworking P2P** channel 12 (not SNS yet). Documented in PROTOCOL.md.

### Built

- Plugin plugin/HalfGripMultiplayer/ ? uild/HalfGripMultiplayer.dll SHA256 prefix $dllHash
- Protocol tests: 3/3 pass
- docs/PROTOCOL.md, README.md, scripts/deploy.ps1
- BepInEx refs in 	hird_party/BepInExPack (compile only ? not installed into game)

### LIVE PROBES (for game worker ? ordered by risk)

1. **P1 ? BepInEx loads plugin:** After BepInEx 5 Mono install + deploy DLL, launch via Steam. Expect BepInEx\LogOutput.log contains `Half Grip Multiplayer 0.1.0 Awake`. Fail: no log line / plugin missing.
2. **P2 ? Steam valid:** Overlay shows Steam name, not INVALID. Fail: SteamClient invalid (not launched via Steam / AppID mismatch).
3. **P3 ? Host lobby:** F6 / Host button ? status Hosting {id}; Steam invite overlay; friend sees invite. Fail: CreateLobby failed.
4. **P4 ? Join + P2P Hello:** Friend joins; both logs show Hello / peer joined; RTT updates. Fail: join OK but no P2P (firewall/relay).
5. **P5 ? Pose packets:** Both in a race scene; log or overlay peer count ?1; host moves ? guest sees ghost move. Fail: no ghost / stuck origin.
6. **P6 ? Ghost visible:** Capsule or cloned body near remote player path; nameplate if enabled. Fail: invisible / physics collision with ghost.
7. **P7 ? Leave cleanup:** Leave destroys ghost; status Idle. Fail: ghost remains / P2P session stuck.
8. **P8 (optional) ? CoPilot mode:** Mode toggle; guest LMB reflects on host fire path. Unproven; expect partial.

### Not verified (pre-live; superseded by Live probe run below for P1?P3/P7)

- SNS relay path
- Race start/finish host authority
- Late join mid-race stability
- Two instances same machine (Steam may block)

## Live probe run ? game worker (2026-10-26 ~12:10?12:12 local)

### Deploy

- Ran `scripts\deploy.ps1` (admin not required; copy into Program Files succeeded).
- Installed BepInEx 5.4.23.2 from `third_party\BepInExPack\` ? game root (`winhttp.dll`, `doorstop_config.ini`, `BepInEx\`).
- Plugin: `C:\Program Files (x86)\Steam\steamapps\common\Half Grip Demo\BepInEx\plugins\HalfGripMultiplayer\HalfGripMultiplayer.dll`
- SHA256: `61C5A8075082C94973F80BB7F6C3A8CD82FED44FD4FA26FFC0A766245E93ED10`
- Core: `...\BepInEx\core\BepInEx.dll` present.
- Graphics: left Unity fullscreen mode registry at **1** (borderless); did **not** change registry.
- Launch: `steam://run/4163360`; HalfGrip.exe PID **22772**; exited cleanly via WM_CLOSE after probes.
- Backup restore path unchanged: `backups\HalfGrip-LocalLow-2026-10-26\`
- Evidence folder: `evidence\`
- Log archive: `evidence\LogOutput_live_probe.log`

### Probe results

| Probe | Result | Evidence |
|---|---|---|
| **P1** BepInEx + Awake | **VERIFIED** | Log: `Half Grip Multiplayer 0.1.0 Awake?`; shot `evidence\p1_boot_121040.png` (IMGUI overlay Idle) |
| **P2** Steam valid | **VERIFIED** | Host created lobby + Steam invite friends list opened (Russian ?????????, ???? ???????????); friends visible (??????? / MrHush / Sasample). Shot `evidence\p3_host_state.png`. Overlay does not print Steam persona name until chat join ? validity inferred from Matchmaking + Friends overlay, not a name string in LogOutput. |
| **P3** Host lobby | **VERIFIED** | Log: `Hosting 109775242370001749 (GhostRace)`; F7; invite overlay auto-opened. |
| **P4** Join + Hello | **NOT VERIFIED** | Single Steam account / one PC ? no second peer. Limitation documented. |
| **P5** Pose packets | **NOT VERIFIED** | Needs two peers in race scene. |
| **P6** Ghost visible | **NOT VERIFIED** | Needs two peers. |
| **P7** Leave ? Idle | **VERIFIED** (host-side status) | After Esc (closed invite; also opened game bug-report once) + F8: overlay `v0.1.0 \| Idle` in `evidence\p7_leave_a.png`. Chat line still showed prior `* Lobby ready?` (chat history not cleared ? expected). No ghost existed to destroy. Leave does **not** write a LogOutput line (only Host logs). |

### Log excerpts (errors + Awake / host)

```
[Error  :   BepInEx] Unable to start Unity log writer
[Info   :Half Grip Multiplayer] Half Grip Multiplayer 0.1.0 Awake. Hotkeys F6/F7/F8/F9. Do not re-Init Steam if SteamIdentity already did.
[Info   :Half Grip Multiplayer] Hosting 109775242370001749 (GhostRace)
```

(No plugin exceptions. BepInEx Unity log writer error is benign for this run.)

### Surprises / notes

- Hotkeys in Plugin.cs: **F6 UI, F7 Host, F8 Leave, F9 Join-ID** (README ?F6 Host? was wrong; STATUS had mixed labels ? trust Plugin.cs / overlay footer).
- HostAndInvite opens Steam invite overlay immediately ? can steal focus from F8 until Esc closes it.
- Esc while overlay/menu can open Half Grip ????????? ? ????? (bug report) modal.
- `um` CLI still missing on machine; screenshots via PrintWindow/GDI.

### REFINE LATER (additions)

- LogInfo on Leave / Steam name on Awake for easier probes
- Don?t auto-open invite overlay on host (or make optional) so hotkeys stay reliable
- Clear chat on Leave (optional)

### Next

- P4?P6 with a second Steam account / friend join of lobby `?01749` (that lobby was left; create new host when testing)
- Optional: windowed mode (registry Fullscreen mode ? 3) ? needs user OK if changing prefs

## Live re-verify ? game worker (2026-10-26 ~12:15?12:17 local)

### Launch

- `steam://run/4163360` at 12:15:25
- HalfGrip.exe **PID 27196**; first window title `BepInEx 5.4.23.2 - HalfGrip`
- Plugin hash unchanged: `61C5A8075082?E93ED10`
- `um` still missing ? screenshots via PrintWindow (PW_RENDERFULLCONTENT)
- Did **not** send F7 ourselves: user was active (`idle` ? 0?0.6 s). Host already present in UI + log (user or prior hotkey). Did **not** kill process (user mid-race).

### Probe results (this pass)

| Probe | Result | Evidence |
|---|---|---|
| **P1** BepInEx + Awake | **VERIFIED** | Log Loading + Awake; overlay in `evidence\reverify_boot_121623.png`, `reverify_menu_121632.png` |
| **P2** Steam valid | **VERIFIED** | Host lobby create succeeded via Steam Matchmaking |
| **P3** Host lobby | **VERIFIED** | Log `Hosting 109775242370384992 (GhostRace)`; shot `evidence\reverify_host_121709.png` |
| **P4?P6** | **NOT VERIFIED** | Need second peer |
| **P7** Leave | **skipped** | User active; no F8 / no exit |

### Log excerpts (authoritative)

```
[Error  :   BepInEx] Unable to start Unity log writer
[Info   :   BepInEx] Loading [Half Grip Multiplayer 0.1.0]
[Info   :Half Grip Multiplayer] Half Grip Multiplayer 0.1.0 Awake. Hotkeys F6/F7/F8/F9. Do not re-Init Steam if SteamIdentity already did.
[Message:   BepInEx] Chainloader startup complete
[Info   :Half Grip Multiplayer] Hosting 109775242370384992 (GhostRace)
```

No Harmony / plugin Fatal / Exception lines. Benign BepInEx Unity log writer error only.

### Full log archive

`evidence\LogOutput_reverify_final.log`

### State left

- Game **RUNNING** PID **27196** (user in session ? do not kill until they finish)
- Backup path unchanged: `backups\HalfGrip-LocalLow-2026-10-26\`


## 0.4.0 ? lobby Ready + grid slots + weather/bots (2026-10-26)

- Grid slots: host = PlayerPosition (0), guests = 1+; ghost claims enemy nearest to peer slot (fixes P1 appearing on P2 seat).
- RaceSetup packet: weather profile name, short race, TrackVariant, max laps, spawn seed (shared Random for SpawnEnemies).
- BotPose host>guest for unclaimed AI; guests freeze local AI.
- Lobby: sync SCENE_UI_* to guests; Ready button; MenuManager.StartRace gated until AllPeersReady; only host starts.
- Protocol Version = 4. MsgType RaceSetup/SlotMap/BotPose.

## 0.5.0 ? CarLook sync (2026-10-26)

- MsgType.CarLook: skinIndex + 3 paint colours + CarSkin asset name + ChassisUpgrade name.
- Applied to remote ghost's claimed EnemyController CarSkinController via ApplySkin.
- Sent on Hello, track ready, ApplySkin/Refit/PlayerCarSkin.Wear, and 1 Hz when dirty.
- Protocol Version = 5.

## 0.6.0 ? full world sync (2026-10-26)

Protocol v6. Synced: hazards seed, objective/boss/music/ceasefire in RaceSetup, RaceEnd, Loadout visuals, GunFx tracers, MapSelect/reroll, Shop money/offer, LapState, AmmoFx, NitroFx, ResetFx, BossKill, late-join bundle.

## 0.7.0 ? per-player economy + AI/run (2026-10-26)

- Money/inventory/loadout stay local; ShopState no longer overwrites wallets; RaceEnd banks local payout.
- RunSnap shares run graph only. AI gun hit peers + FX; bot HP/death via BotPose; DentFx; LAN reliable x3; leaderboard register ghosts.

## 0.8.0 ? laps, tires, shield, collide, cutoff, LAN reliable (2026-10-26)

- Protocol Version = 8. MsgType TireFx/ShieldFx/CollideImpulse/CutoffState.
- StorePeerLap updates LapManager.leaderboard for claimed ghosts.
- ShopState fully ignored; late-join no longer sends shop.offer; wallets/loadout private.
- LanTransport: seq/ack reliable channel (resend 120ms, TTL 8s).
- Peer collision impulses via distance probe + CollideImpulse.
- RaceCutoff.SecondsLeft host?guest; nitro off when activeBoost hits 0; GunFx plays shootSounds at origin.

## 0.9.0 ? blast/ram/rocket/pickup + timescale gate (2026-10-26)

- Protocol v9: BlastFx, RocketFx, PickupFx.
- AreaBlast ? peer Damage + BlastFx; Ram hits RemotePlayerHitbox.
- Enemy rockets synced (guest VFX-only rockets via RemoteRocketTag).
- TimeScale.Set/Hold blocked in MP; guest EndRace gated; Quit ? RaceAbort/RaceEnd.
- Health/Effect pickup tombstones (heal/refill stay local).

## 0.9.5-dev ? bugs/sync polish, no release (2026-10-26)

- Protocol v11: MsgType.PeerLeft=35; GunFx + shooterPeer u64.
- Guest loadout: ResolveLoadout Prefix from save; strip non-save fitted; delayed refit 90 frames.
- PlaceLocalPlayer slot 0 keeps vanilla grid (no forced yaw).
- PeerLeft Leave broadcast + DropPeer; Steam lobby member prune; 20s LastSeen timeout.
- Wreck > CarDamageVisuals.SetWrecked; ForgetPeer clears loadout/laps/extras.
- Lap: EnsureLocalLapTracking periodic; ghost claim re-applies pending loadout visuals.
- Build/deploy local only ? **no tag / no GitHub release**.

## 0.9.6-dev ? NoBots ghosts, slots, Force start (2026-10-26)

- NoBots: PrebindPeerGhosts before strip; keep one AI body per peer grid slot.
- AssignGuestSlot recycles lowest free slot (fixes slot=2 with 1 peer).
- Hello replies with Ready + ForceBroadcastLoadout; Force start button for stuck lobby.
- Wreck FX once-per-death; fallback ghost tries car mesh clone.
- Local deploy only ? no release.

## 0.9.7-dev ? horn/steer/ping/money + scoreboard (2026-10-26)

- Protocol v12: Pose+steer/flags; HornFx; WorldPing; MoneyFx.
- Ghosts: steering wheels + brake light emission.
- NClaxon OnGrab/OnRelease > peers hear horn.
- MMB WorldPing marker; MoneyDrop tombstone (cash local).
- Hidden-UI race scoreboard from LapState cache.
- Local deploy only.

## 0.9.8-dev ? defer ghost, destructibles, jump, quick chat (2026-10-26)
- Ghost waits for bot slot before cube fallback.
- DestructibleFx + JumpFx (protocol v13).
- Guest auto-ready on RaceSetup; Enter quick chat + toasts.


## 0.9.9-dev ? train/gear/handbrake/spectate/taunts (2026-10-26)
- Protocol v14: TrainSnap, GearFx; Pose flag bit1=handbrake.
- Host train HeadDistance sync; ghost wheel spin + gear label.
- Tab spectate after wreck; keys 1-4 taunts.


## 0.9.9-dev ? train/gear/handbrake/spectate/taunts (2026-10-26)
- Protocol v14: TrainSnap, GearFx; Pose flag bit1=handbrake.
- Host train HeadDistance sync; ghost wheel spin + gear label.
- Tab spectate after wreck; keys 1-4 taunts.


## 0.10.0-dev ? Ready unlock, radar, totem (2026-10-26)
- Host StartRace auto MarkAllPeersReady (no more blocked lobby).
- TotemFx; peer radar HUD; finish toast; Ping/Ready Touch LastSeen.
- Protocol v15.


## 0.10.1-dev ? RunSnap chunks, scene debounce, SlowMo/Drift FX (2026-10-26)
- RunSnapPart MTU-safe fragments; Host scene broadcast debounce 1.25s.
- SlowMoFx (visual only) + DriftFx cues. Protocol v16.


## 0.10.2-dev ? extrapolation, spin/damage FX, HP bar, kill feed (2026-10-26)
- Ghost pose velocity extrapolation; HP bar; SpunOutFx + CarDamagedFx.
- Kill feed toast when you recently damaged a wrecked peer. Protocol v17.


## 0.10.3-dev ? LAN remap ghosts, AmmoState, Hello pulse (2026-10-26)
- RemapPeerIdentity on LAN Hello (fixes duplicate ghosts).
- AmmoState on nameplates; periodic Hello keepalive. Protocol v18.


## 0.10.4-dev ? exhaust/reverse, nameplate LOD, hit chat throttle (2026-10-26)
- Pose flags reverse+exhaust; ExaustPipeEffect sync; distance nameplates; hit chat 0.85s throttle.
- Protocol v19 ExhaustFx.

## 0.10.5-dev ? magnet/glue/spike/glass/trails/trees (2026-10-26)
- Protocol v20: MagnetFx/GlueFx/SpikeFx/GlassFx/TrailFx/TreeFx.
- Spike Pierce ? FX + PvP damage on ghost bots; glass shatter; host tree Fell; JumpPad Launch + RoadPickup tombstones.
- Ghost: tire trails, magnet beam, glue particles, weather headlights; bot-claim wait 6.5s.
- Host StartRace always MarkAllPeersReady. Local deploy only.


## 0.10.6-dev - ghost gate, mud/trap/hammer/killfeed/burn (2026-10-26)
- Protocol v21: MudFx/TrapFx/HammerFx/KillFeed/LeechFx/BurnFx.
- No ghost spawn until track ready; pending poses flushed; ForceRebind on Prebind.
- Low-HP smoke; hammer PvP; mud/spikes FX; AI kill + totem feed. Local deploy + ClientA/B.

## 0.10.7-dev - turret/ice/invert/combo/checkpoint/airborne (2026-10-26)
- Protocol v22: InvertFx/ComboFx/CheckpointFx; Pose airborne bit32.
- TrainTurret Fire+HandleHit PvP; IceSpike blast; invert/combo toasts; Shaker on incoming damage.
- Ghost airborne bob. Local deploy + ClientA/B.

## 0.10.8-dev - real car FX, guillotine/drain/trample (2026-10-26)
- Protocol v23: GuillotineFx/DrainFx/TrampleFx/ExplodeFx.
- Ghosts use EnemyCarEffectController smoke/flames; wreck big explode.
- Local deploy + ClientA/B. (Stale BepInEx log lines predate 0.10.6.)

## 0.10.9-dev - bot flames, ammo-eat, objective, tree kick, late ghost (2026-10-26)
- Protocol v24: BotFlameFx/AmmoEatFx/ObjectiveFx/TreeKickFx.
- EnsurePeerGhost on first Hello when track ready; ghost AIR nameplate tag.
- Local deploy + ClientA/B.

## 0.10.10-dev - auto-spectate, burn/refill/threat/canister, ice throttle (2026-10-26)
- Protocol v25: RefillFx/ThreatFx/CanisterFx.
- Auto spectate after wreck; PlayerDamageEffects burn sync; TimeRewinder Begin InvertFx.
- IceSpike blast once per instance. Local deploy + ClientA/B.

## 0.10.11-dev - cam/hblock/shortcut/earn, ice clear, ghost lean (2026-10-26)
- Protocol v26: CamFx/HbLockFx/ShortcutFx/EarnFx.
- IceSpike Fired cleared on track load; ghost body lean into steer.
- Local deploy + ClientA/B.

## 0.10.12-dev - engine hum, blink/hint/cabin ammo (2026-10-26)
- Protocol v27: BlinkFx/HintFx/CabinAmmoFx.
- Ghost spatial engine hum from bank clips; wrong-way/stuck peer toasts; EffectRecharged.
- Local deploy + ClientA/B.

## 0.10.13-dev - aim/mag/pause/cutoff say + muzzle (2026-10-26)
- Protocol v28: AimFx/MagFx/PauseFx/CutoffSay.
- Gun/NPumpAction Enter|ExitAimMode; NAutomaticGun mag seated/removed; PauseRace/Resume; host RaceCutoff.Say.
- GunFx muzzle burst LineRenderer; ghost AIM nameplate tag.
- Local deploy + ClientA/B.

## 0.10.14-dev - gun actions, smoke, nitro sound (2026-10-26)
- Protocol v29: GunActFx/SmokeFx.
- Pump shell, break open/close/eject, revolver spin; player smoke emission; remote nitro oneshot.
- Ghost LOAD/BRK/EJCT/SPIN/NOS tags. Local deploy + ClientA/B.

## 0.10.15-dev - glass crack, weather, HB grab, heal (2026-10-26)
- Protocol v30: GlassCrackFx/WeatherFx/HbGrabFx/HealFx.
- Aim/smoke broadcast throttles; CarLink.Heal ghost HP pulse.
- Local deploy + ClientA/B.

## 0.10.16-dev - shift hits, train move, kill ammo, pause host-only (2026-10-26)
- Protocol v31: ShiftHitFx/TrainMoveFx/KillAmmoFx.
- MoneyShift stall/$$ tags; host train Start/StopMoving; GrantForKill toast.
- PauseFx only from host. Local deploy + ClientA/B.

## 0.10.17-dev - feats, gun/shifter type, shell, spectate (2026-10-26)
- Protocol v32: FeatFx/GunTypeFx/ShiftTypeFx/ShellFx.
- KillAmmoFx sends gained delta (not total Count).
- Spectate: smooth follow, Shift+Tab reverse, name toast.
- Local deploy + ClientA/B.

## 0.10.18-dev - slide/clip/unlock/cam-look, radar, wreck (2026-10-26)
- Protocol v33: SlideFx/ClipFx/UnlockFx/CamLookFx.
- RaceTracker.ReportWrecked backup ? Wreck+explode+spectate.
- Radar: HP colors + distance. Local deploy + ClientA/B.

## 0.10.19-dev - repair/grime/magshot/windshield, wreck gate (2026-10-26)
- Protocol v34: RepairFx/GrimeFx/MagShotFx/WindshieldFx.
- WreckSyncGate dedupes TakeCrashDamage + RaceTracker.ReportWrecked.
- Local deploy + ClientA/B.

## 0.10.20-dev - objective say, steer lock, match-rev, records (2026-10-26)
- Protocol v35: ObjSayFx/SteerLockFx/RecenterFx/MatchRevFx/RecordFx.
- Combo continuous/increment; scoreboard peer HP; ghost engine pitch by gear/NOS.
- Local deploy + ClientA/B.

## 0.10.21-dev - toast dedupe, matchrev gate, lap-ahead/nos/parts/shake (2026-10-26)
- Protocol v36: LapAheadFx/NosUpFx/PartsFx/ShakeFx.
- PushToast skips duplicate lines <0.85s; MatchRev only on gear change.
- Local deploy + ClientA/B.

## 0.10.22-dev - chickens, items, model, F5 ready, LOD (2026-10-26)
- Protocol v37: ChickenFx/ChickenGoneFx/ItemUseFx/ModelFx.
- Kill feed uses skin carName; nameplate far LOD; F5 Ready toggle.
- Local deploy + ClientA/B.

## 0.10.23-dev - tornado/barrel authority, chicken VFX, lights/nitro (2026-10-26)
- Protocol v38: TornadoSnap + BarrelFx; clients skip tornado pathing / barrel spawn.
- ChickenGoneFx calls DestroyObject (smash VFX); headlights use WeatherProfile.headlightsActive.
- Ghost nitro enables fireEffect emission. HazardsSpawner_FINAL seed. Local deploy.

## 0.10.24-dev - tornado impulse fix, dice/beer, F4 force-start (2026-10-26)
- Protocol v39: DiceFx + BeerFx.
- Client tornado respects forceInterval (no per-frame impulse stack).
- F4 host UnlockForceStart. Local deploy.

## 0.10.25-dev - beer gate, proto warn, RTT, nitro pose bit (2026-10-26)
- BeerFx only when drink actually starts; Hello warns on protocol mismatch.
- PeerInfo.RttMs on nameplates; Pose flag bit6 keeps ghost NOS lit. Local deploy.

## 0.10.26-dev - nitro cache, late-join world, place label (2026-10-26)
- Pose NOS uses WorldSync.LocalNitroOn (Pulse before SendLocalPose).
- Late-join snaps weather/tornado/train/cutoff; nameplate shows P#/FIN. Local deploy.

## 0.10.27-dev - engineIndicator pose, HB cache, jump hop (2026-10-26)
- Protocol v40: Pose appends engineIndicator for ghost RPM hum.
- PulseLocalHandbrake cache; JumpFx lifts + spins ghost. Local deploy.

## 0.10.28-dev - Multiplayer menu (game UI) (2026-10-26)
- MpMenuUi: UiKit + ProgressionMenuSkin panel (Steam/LAN/Lobby/Sandbox/Chat).
- MainMenu.OpenMultiplayer wired; StartMenuProgression gets MULTIPLAYER button.
- F6 opens uGUI when skin available; IMGUI chrome restyled as fallback.

## 0.10.28 - GitHub release (2026-10-26)
- Tag v0.10.28; game-root + plugin-only zips.
- Bundles menu UI + protocol v40 + sync batch through 0.10.27.

## 0.10.29-dev - menu blank fix (2026-10-26)
- Root cause: MpMenuUi parented under DDOL GO with no Canvas; IsOpen=true blocked IMGUI; empty screen on F6 / MULTIPLAYER.
- Fix: dedicated ScreenSpaceOverlay Canvas + GraphicRaycaster; build without skin fallback; EventSystem ensure; F6 logs open/fail.

## 0.10.31-dev - MpRuntime destroyed on scene load (2026-10-26)
- Root cause: separate DDOL GO from Plugin.Awake died on first scene load ? Instance null; button clicks logged but ForceOpenMenu no-op; no Update/OnGUI/F6.
- Fix: host MpRuntime on BepInEx plugin GameObject; Ensure() recreates; IMGUI primary + corner MP MENU; buttons call Ensure().ForceOpenMenu().

## 0.10.31 - GitHub release (2026-10-26)
- Tag v0.10.31; game-root + plugin-only zips.
- Menu open fix (MpRuntime on BepInEx host) + protocol v41.
