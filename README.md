# Half Grip Multiplayer 0.2 — Steam + LAN PvP

Each player drives **their own car**, can **shoot / kill** the other (damage sync), **shared race start**.  
No co-pilot. No split-screen.

## Modes

| Mode | How |
|------|-----|
| **Steam** | Overlay → Host Steam / Invite / Join lobby ID |
| **LAN** | Host LAN (UDP) / Join LAN IP:port — works on localhost for dual clients |

## Hotkeys

- **F6** — overlay  
- **F7** — Host Steam lobby  
- **F8** — Leave  

## Dual-client local test (2 windows)

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup-dual-clients.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\launch-dual.ps1
```

- **ClientA** = LAN host on `127.0.0.1:27777`  
- **ClientB** = LAN join  

Both copies live under `clients\ClientA` and `clients\ClientB` (full game copies + BepInEx).

## Steam install

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy.ps1
```

Use Steam lobbies with a friend, or LAN if on the same network.

## Gameplay notes

- Remote player appears as a **tinted ghost + hitbox**
- Hits on that hitbox → damage packet → `TakeCrashDamage` on peer  
- Track countdown is **gated** until host sees all peers Ready (auto when race scene loads)

## Project

`c:\Users\Sildom Landor\Documents\half-grip-multiplayer\`
