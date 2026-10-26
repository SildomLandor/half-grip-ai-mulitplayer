# Half Grip Demo — Multiplayer Mod Journal

## Intake (2026-10-26)

- **Game:** Half Grip Demo (Steam AppID 4163360, parent 4163270)
- **Install:** `C:\Program Files (x86)\Steam\steamapps\common\Half Grip Demo\`
- **Company/product:** Half Games / HalfGrip (`app.info`)
- **Engine:** Unity Mono (`HalfGrip_Data/Managed/`, `MonoBleedingEdge`)
- **Idea (one sentence):** Private Steam-friends (or LAN) co-op race with a synced remote car ghost, then pilot/co-pilot and lobby polish.
- **Done means:** BepInEx plugin that hosts/joins a private lobby, syncs a second player's car transform/basic state in a real race, verified with screenshots/logs; README for install.
- **Ownership:** User owns the game. Private user-hosted sessions only. No anti-cheat bypass. No redistributing game/decomp files.

## Sources (outside repo — do not ship)

- Decomp: `c:\Users\Sildom Landor\Documents\half grip\Scripts\Assembly-CSharp\`
- Assemblies: `c:\Users\Sildom Landor\Documents\half grip\Assemblies\`
- Assets dump: `c:\Users\Sildom Landor\Documents\half grip\Assets\`

## Working folder

`c:\Users\Sildom Landor\Documents\half-grip-multiplayer\`

## Recon notes (initial)

- `Facepunch.Steamworks.Win64.dll` present in Managed. `SteamIdentity.cs` already calls `SteamClient` with AppID 4163360 (`SteamClient.Init(4163360u, asyncCallbacks: false)`). No other Steam lobby/net code in Assembly-CSharp (SP only).
- No BepInEx in install yet (`BepInEx` folder absent).
- **Unity version:** 6000.3.22f1 (Unity 6) — Mono managed (`HalfGrip_Data/Managed`).
- **dotnet SDK on machine:** 8.0.425
- **Saves/config:** `%USERPROFILE%\AppData\LocalLow\Half Games\HalfGrip\` (confirm contents before backup)
- Key classes seen: `CarControllerNew` (physics/input), `GameManager` (singleton, race state), `RaceStarter` (countdown sprites; Update empty in dump), `SteamIdentity`, `Player*` helpers, `CarSkinController.isPlayer`, `CarLink` (player car link in TutorialFlow), `EnemyController` + `IEnemyBehaviour` / `DriverBehaviour` for AI.
- Official EA plans PvP/co-op; demo is SP-only. Steam page lists LAN Co-op as planned.
- **um CLI:** not installed; Python/uv/pipx missing — install later or work without `um` for backup/manual steps.

## Chosen route

**BepInEx 5 (Mono) + Harmony + Facepunch.Steamworks (already shipped) for Steam lobbies / SteamNetworkingSockets.** Private friends lobbies; optional LAN fallback later. Plugin deploy via copy script into `BepInEx/plugins/` — avoid shipping game DLLs.

Reason: managed Unity Mono, Steamworks already initialized by game, no community loader present, multiplayer needs code hooks not data packs.

## Lab / safety

- Saves/config: `%USERPROFILE%\AppData\LocalLow\Half Games\HalfGrip\` (currently mostly `Player.log` / `Player-prev.log`; progress may be PlayerPrefs)
- Backup restore path: `c:\Users\Sildom Landor\Documents\half-grip-multiplayer\backups\HalfGrip-LocalLow-2026-10-26\` (copied 2026-10-26; contained Player.log + Player-prev.log only)
- Windowed mode preferred for screenshots
- Never commit decomp or Managed game assemblies

## Tooling

- `um` CLI: **missing** — Python/uv/pipx absent; winget can install Python.Game worker / later: optional `winget install Python.Python.3.12` then pip install um
- Plugin project: TBD under this folder (offline worker in progress)
- BepInEx NuGet via `dotnet add package` when scaffolding

## Next

1. Offline worker: deep recon + protocol + plugin scaffold (IN PROGRESS: agent 84c79e80 — interrupted/resumed 11:41 to force finish; dirs empty of source as of orchestrator check)
2. ~~Backup LocalLow~~ DONE → `backups/HalfGrip-LocalLow-2026-10-26/`
3. Game worker: install BepInEx (user consented via mod request), deploy, launch windowed, verify lobby UI + LogOutput

## REFINE LATER

- Full physics authority sync, weapon sync fidelity, dedicated server, public matchmaking (out of scope — private only)

## Offline batch — vertical slice scaffold (2026-10-26)

### Recon facts (decomp, verified)

- **Player car discovery:** TrackManager.playerCarController (scene ref); CarLink.Start sets cc.isPlayer = true; find via FindObjectsByType<CarControllerNew> where isPlayer, or TrackManager.
- **AI cars:** TrackManager.SpawnEnemies instantiates enemyCarPrefab / RunConfig.devCars under enemiesContainer; each has CarControllerNew + EnemyController (writes steeringAngleDeg / accel). Visual clone possible via mesh instantiate + strip controllers (implemented in GhostCar).
- **Input path:** Player drive is **CarLink.FixedUpdate** → GameInput.Steer / W/S → cc.steeringAngleDeg, pedalInput, engineAccel, handBrake. Weapons: Gun.Update → GameInput.HandDown(1) fire. Ghost must disable CarLink/EnemyController/Gun (GhostCar.StripGameplay). Pilot/gunner split: Harmony on GameInput.GetMouseButton + nitro bump (Patches.cs) — unproven in-game.
- **Race:** TrackManager.RaceCountdown / 
aceStarted; GameManager.raceOver + RaceFinished; RaceStarter Update empty — countdown driven by TrackManager. No net RaceSync in v0.1.
- **Networking stubs in game:** none (Unity.Multiplayer.Center.Common is package stub only). Steam: SteamIdentity Init 4163360; Facepunch types include Lobby, SteamMatchmaking, SteamNetworking (P2P), SteamNetworkingSockets, SocketManager, ConnectionManager.
- **Transport choice (v0.1):** classic **SteamNetworking P2P** channel 12 (not SNS yet). Documented in PROTOCOL.md.

### Built

- Plugin plugin/HalfGripMultiplayer/ → uild/HalfGripMultiplayer.dll SHA256 prefix $dllHash
- Protocol tests: 3/3 pass
- docs/PROTOCOL.md, README.md, scripts/deploy.ps1
- BepInEx refs in 	hird_party/BepInExPack (compile only — not installed into game)

### LIVE PROBES (for game worker — ordered by risk)

1. **P1 — BepInEx loads plugin:** After BepInEx 5 Mono install + deploy DLL, launch via Steam. Expect BepInEx\LogOutput.log contains `Half Grip Multiplayer 0.1.0 Awake`. Fail: no log line / plugin missing.
2. **P2 — Steam valid:** Overlay shows Steam name, not INVALID. Fail: SteamClient invalid (not launched via Steam / AppID mismatch).
3. **P3 — Host lobby:** F6 / Host button → status Hosting {id}; Steam invite overlay; friend sees invite. Fail: CreateLobby failed.
4. **P4 — Join + P2P Hello:** Friend joins; both logs show Hello / peer joined; RTT updates. Fail: join OK but no P2P (firewall/relay).
5. **P5 — Pose packets:** Both in a race scene; log or overlay peer count ≥1; host moves — guest sees ghost move. Fail: no ghost / stuck origin.
6. **P6 — Ghost visible:** Capsule or cloned body near remote player path; nameplate if enabled. Fail: invisible / physics collision with ghost.
7. **P7 — Leave cleanup:** Leave destroys ghost; status Idle. Fail: ghost remains / P2P session stuck.
8. **P8 (optional) — CoPilot mode:** Mode toggle; guest LMB reflects on host fire path. Unproven; expect partial.

### Not verified (pre-live; superseded by Live probe run below for P1–P3/P7)

- SNS relay path
- Race start/finish host authority
- Late join mid-race stability
- Two instances same machine (Steam may block)

## Live probe run — game worker (2026-10-26 ~12:10–12:12 local)

### Deploy

- Ran `scripts\deploy.ps1` (admin not required; copy into Program Files succeeded).
- Installed BepInEx 5.4.23.2 from `third_party\BepInExPack\` → game root (`winhttp.dll`, `doorstop_config.ini`, `BepInEx\`).
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
| **P1** BepInEx + Awake | **VERIFIED** | Log: `Half Grip Multiplayer 0.1.0 Awake…`; shot `evidence\p1_boot_121040.png` (IMGUI overlay Idle) |
| **P2** Steam valid | **VERIFIED** | Host created lobby + Steam invite friends list opened (Russian “Выберите, кого пригласить”); friends visible (КАПУСТА / MrHush / Sasample). Shot `evidence\p3_host_state.png`. Overlay does not print Steam persona name until chat join — validity inferred from Matchmaking + Friends overlay, not a name string in LogOutput. |
| **P3** Host lobby | **VERIFIED** | Log: `Hosting 109775242370001749 (GhostRace)`; F7; invite overlay auto-opened. |
| **P4** Join + Hello | **NOT VERIFIED** | Single Steam account / one PC — no second peer. Limitation documented. |
| **P5** Pose packets | **NOT VERIFIED** | Needs two peers in race scene. |
| **P6** Ghost visible | **NOT VERIFIED** | Needs two peers. |
| **P7** Leave → Idle | **VERIFIED** (host-side status) | After Esc (closed invite; also opened game bug-report once) + F8: overlay `v0.1.0 \| Idle` in `evidence\p7_leave_a.png`. Chat line still showed prior `* Lobby ready…` (chat history not cleared — expected). No ghost existed to destroy. Leave does **not** write a LogOutput line (only Host logs). |

### Log excerpts (errors + Awake / host)

```
[Error  :   BepInEx] Unable to start Unity log writer
[Info   :Half Grip Multiplayer] Half Grip Multiplayer 0.1.0 Awake. Hotkeys F6/F7/F8/F9. Do not re-Init Steam if SteamIdentity already did.
[Info   :Half Grip Multiplayer] Hosting 109775242370001749 (GhostRace)
```

(No plugin exceptions. BepInEx Unity log writer error is benign for this run.)

### Surprises / notes

- Hotkeys in Plugin.cs: **F6 UI, F7 Host, F8 Leave, F9 Join-ID** (README “F6 Host” was wrong; STATUS had mixed labels — trust Plugin.cs / overlay footer).
- HostAndInvite opens Steam invite overlay immediately — can steal focus from F8 until Esc closes it.
- Esc while overlay/menu can open Half Grip “Сообщить о баге” (bug report) modal.
- `um` CLI still missing on machine; screenshots via PrintWindow/GDI.

### REFINE LATER (additions)

- LogInfo on Leave / Steam name on Awake for easier probes
- Don’t auto-open invite overlay on host (or make optional) so hotkeys stay reliable
- Clear chat on Leave (optional)

### Next

- P4–P6 with a second Steam account / friend join of lobby `…01749` (that lobby was left; create new host when testing)
- Optional: windowed mode (registry Fullscreen mode → 3) — needs user OK if changing prefs

## Live re-verify — game worker (2026-10-26 ~12:15–12:17 local)

### Launch

- `steam://run/4163360` at 12:15:25
- HalfGrip.exe **PID 27196**; first window title `BepInEx 5.4.23.2 - HalfGrip`
- Plugin hash unchanged: `61C5A8075082…E93ED10`
- `um` still missing — screenshots via PrintWindow (PW_RENDERFULLCONTENT)
- Did **not** send F7 ourselves: user was active (`idle` ≈ 0–0.6 s). Host already present in UI + log (user or prior hotkey). Did **not** kill process (user mid-race).

### Probe results (this pass)

| Probe | Result | Evidence |
|---|---|---|
| **P1** BepInEx + Awake | **VERIFIED** | Log Loading + Awake; overlay in `evidence\reverify_boot_121623.png`, `reverify_menu_121632.png` |
| **P2** Steam valid | **VERIFIED** | Host lobby create succeeded via Steam Matchmaking |
| **P3** Host lobby | **VERIFIED** | Log `Hosting 109775242370384992 (GhostRace)`; shot `evidence\reverify_host_121709.png` |
| **P4–P6** | **NOT VERIFIED** | Need second peer |
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

- Game **RUNNING** PID **27196** (user in session — do not kill until they finish)
- Backup path unchanged: `backups\HalfGrip-LocalLow-2026-10-26\`


## 0.4.0 � lobby Ready + grid slots + weather/bots (2026-10-26)

- Grid slots: host = PlayerPosition (0), guests = 1+; ghost claims enemy nearest to peer slot (fixes P1 appearing on P2 seat).
- RaceSetup packet: weather profile name, short race, TrackVariant, max laps, spawn seed (shared Random for SpawnEnemies).
- BotPose host>guest for unclaimed AI; guests freeze local AI.
- Lobby: sync SCENE_UI_* to guests; Ready button; MenuManager.StartRace gated until AllPeersReady; only host starts.
- Protocol Version = 4. MsgType RaceSetup/SlotMap/BotPose.

## 0.5.0 � CarLook sync (2026-10-26)

- MsgType.CarLook: skinIndex + 3 paint colours + CarSkin asset name + ChassisUpgrade name.
- Applied to remote ghost's claimed EnemyController CarSkinController via ApplySkin.
- Sent on Hello, track ready, ApplySkin/Refit/PlayerCarSkin.Wear, and 1 Hz when dirty.
- Protocol Version = 5.

## 0.6.0 � full world sync (2026-10-26)

Protocol v6. Synced: hazards seed, objective/boss/music/ceasefire in RaceSetup, RaceEnd, Loadout visuals, GunFx tracers, MapSelect/reroll, Shop money/offer, LapState, AmmoFx, NitroFx, ResetFx, BossKill, late-join bundle.
