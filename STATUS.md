# Half Grip Multiplayer — Status

**Version:** 0.8.0  
**Protocol:** v8  
**Phase:** Steam + LAN PvP; personal shop/money/loadout; world + FX sync

**0.8.0:**
- LapState writes into LapManager leaderboard for ghosts
- TireFx / ShieldFx / CollideImpulse / CutoffState
- LAN reliable seq/ack (0xFE / 0xFD)
- Nitro off + remote gun sounds
- Shop/money/offers never synced (ShopState ignored)

**Test:** `scripts\deploy.ps1` then `scripts\launch-dual.ps1`
