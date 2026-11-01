# PotionDuration

Potions, brews, elixirs and imbuements (weapon coatings) last **as long as you choose** instead of the base game's 60 minutes. The mod default is 2 hours.

Only the 60-minute potions, brews, elixirs and imbuements change. Healing potions and brews, Vermin Salve and every
other consumable keep their base-game timing.

## What changes
| Group | Items | Setting |
|---|---|---|
| Potions and brews | Fire Resistance Brew, Minor Sun Resistance Brew, Garlic, Holy and Silver resistance brews and potions, Brew of Ferocity, Potion of Rage, Enchanted Brew, Witch Potion, Wrangler's Potion | `PotionMinutes` |
| Elixirs | Elixir of the Bat, Beast, Blasphemous, Crow, Prowler, Raven, Twisted and Werewolf | `ElixirMinutes` |
| Imbuements (weapon coatings) | Blood, Chaos, Frost, Illusion, Storm and Unholy Coating | `ImbuementMinutes` |

The buff icon's timer and the tooltips (on the item in your bag and on the buff icon) show the new time.

## Installing
Requires [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/).

- **Server:** install PotionDuration on the server. This is what makes the effects last longer.
  On a dedicated server, copy `PotionDuration.dll` into `BepInEx/plugins/` while the server is stopped, then start it.
- **Players:** install PotionDuration with your mod manager (r2modman or Thunderstore Mod Manager). This is optional:
  it makes the tooltips show the new time. Without it everything still works, but the tooltips say 60 minutes, and the
  imbuement's buff icon shows no timer while more than 60 minutes are left (the base game's icon can't show more than 60).

The same mod goes on the server and on each player's game.

## Settings
The config file `BepInEx/config/PotionDuration.cfg` is created the first time the game or server starts with the mod.
In r2modman you can edit it under **Config editor**.

| Setting | Default | What it does |
|---|---|---|
| `PotionMinutes` | 120 | How long potions and brews last, in minutes |
| `ElixirMinutes` | 120 | How long elixirs last, in minutes |
| `ImbuementMinutes` | 120 | How long imbuements (weapon coatings) last, in minutes |
| `Enabled` | true | Set to `false` to turn the mod off |

**Choosing a duration** (the base game is 60 minutes):

| Minutes | Duration | Compared with the base game |
|---|---|---|
| 30 | 30 minutes | 50% shorter |
| 120 | 2 hours | 100% longer (twice as long), **the default** |
| 240 | 4 hours | 300% longer (4 times as long) |

Any value from **1 to 1440 minutes (24 hours)** is accepted. A value outside that range is set to the nearest limit.

**Players: use the same minutes as the server.** The tooltips show your own setting, so if it differs from the
server's, the tooltip is wrong (the effect itself always follows the server).

Changes apply the next time the server or game starts. A potion or imbuement that is already active keeps the duration it started with.

### Advanced settings
| Setting | Default | What it does |
|---|---|---|
| `Include` | (empty) | Comma-separated buff names to treat as potions too (or as elixirs, if the name contains "Elixir"). The buff must last 60 minutes in the base game |
| `Exclude` | (empty) | Comma-separated buff or imbuement (weapon coating) names to leave unchanged |
| `DumpBuffs` | false | Writes every timed buff (name, ID, duration) to `BepInEx/config/PotionDuration/buffs.csv`, to find buff names |

## Good to know
- Only one elixir can be active at a time, as in the base game.
- Players with the mod see their own setting in the tooltips on every server they join, including servers without the mod.
- To check it works, look in `BepInEx/LogOutput.log` for `21 buff prefabs changed` and `6 imbuements (weapon coatings) changed` (server) and `tooltips: 48 duration values changed` (player).
- A game update can rename buffs. If the server log says `0 buff prefabs changed`, the mod needs an update.
- To remove the mod, uninstall it in your mod manager, or delete `PotionDuration.dll` (and `PotionDuration.cfg`) and restart.

## Credits
- Project setup (BepInEx packages and NuGet feeds) modelled on [ScarletBrazier](https://thunderstore.io/c/v-rising/p/ScarletMods/ScarletBrazier/) by markvaaz; its change to braziers' burn time also gave the idea
  of changing a duration in the game's own data.
- The "server has finished loading" hook (a Harmony postfix on `SpawnTeamSystem_OnPersistenceLoad`) is a common pattern
  in V Rising mods, e.g. [KindredCommands](https://thunderstore.io/c/v-rising/p/odjit/KindredCommands/) by odjit, [ScarletCore](https://thunderstore.io/c/v-rising/p/ScarletMods/ScarletCore/) and [Bloodcraft](https://thunderstore.io/c/v-rising/p/zfolmt/Bloodcraft/) by zfolmt.
- Built with [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) (BepInEx, HarmonyX,
  Il2CppInterop).

No code was copied from these mods; thanks to their authors for showing the way.

## Transparency
This mod was made with [Claude Code](https://claude.com/claude-code), Anthropic's AI coding assistant. Claude worked out
how the game stores potion and imbuement durations and tooltip text, and wrote the code and documentation; I decided what the mod
should do, reviewed the changes, and tested each version in game on my own server.

Made for V Rising 1.1.15 with BepInEx 6 (BepInExPack V Rising 1.733.2).
