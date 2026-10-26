# Half Grip Multiplayer — Wire Protocol (v7)

Private Steam / LAN `:27777`. Host = lobby owner.

## Economy (important)

**Money, inventory, and equipped loadout are per-player.**  
`ShopState` only shares the shop *offer catalog* string (money field = `-1`, ignored).  
`Loadout` is for **remote visuals** on ghosts — never applied to the local `PlayerLoadout`.  
On `RaceEnd`, each client banks **their own** `RaceTracker` payout via `ShopManager.addRaceGain`.

## Messages (additions since v5)

| Id | Name | Notes |
|----|------|--------|
| 12 | RaceSetup | + objective, music, ceasefire, selected node |
| 16 | RaceEnd | host signal; guests keep local payout |
| 17 | Loadout | peer cosmetic ids |
| 18–25 | Gun/Map/Shop/Lap/Ammo/Nitro/Reset/Boss | as before |
| 26 | RunSnap | `RunSave` JSON — **map graph only** (not wallets) |
| 27 | DentFx | damage marks on peers/bots |

Hello **version = 7**. LAN `reliable` sends are triplicated.
