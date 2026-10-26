# Half Grip Multiplayer — Status

**Version:** 0.2.0
**Phase:** Steam + LAN PvP, dual clients, shared start (co-pilot removed)

**Measured 2026-10-26:**
- Dual clients launched (ClientA PID host-lan :27777, ClientB join-lan)
- Plugin 0.2.0 loads on both; Auto role host-lan / join-lan OK
- Race gate Prefix fires (TrackManager countdown gated)

**Features:**
- Own car per player + ghost hitbox PvP damage
- Steam lobbies + LAN UDP
- Shared race start
- No co-pilot, no split-screen

**Test:** `scripts\launch-dual.ps1`
