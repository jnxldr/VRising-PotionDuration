# Building PotionDuration

Requires the .NET SDK 6 or later. NuGet feeds are in `nuget.config`.

## Game interop assemblies
The plugin compiles against the interop assemblies BepInEx generates for a game build. Either use the
`VRising.Unhollowed.Client` NuGet package (automatic, but may lag the game), or copy a BepInEx install's
`BepInEx/interop/` folder into this repo (it is gitignored):

| Folder | Copy of |
|---|---|
| `interop/` | a dedicated server's `BepInEx/interop/` |
| `interop-client/` | a client's `BepInEx/interop/` (e.g. an r2modman profile's) |

After a game update, copy the folders again and rebuild.

## Commands
From the repo root:

| Command | Builds against |
|---|---|
| `dotnet build -c Release` | the NuGet package |
| `dotnet build -c Release -p:InteropDir=interop-client -o bin/client --no-incremental` | `interop-client/` |
| `dotnet build -c Release -p:InteropDir=interop -o bin/server --no-incremental` | `interop/` |

One DLL works on both server and client; the release is built against the client interop.

## Thunderstore package
`python -I thunderstore/pack.py` builds the DLL (against `interop-client/` by default) and writes
`dist/PotionDuration-<version>.zip` with `manifest.json`, `README.md`, `CHANGELOG.md`, `icon.png` and the DLL, all at the zip
root. The version comes from `PotionDuration.csproj`; the description and dependency are in `pack.py`. The icon is drawn by
`thunderstore/make_icon.py`.

## How it works
- **Server:** once the server has loaded (`SpawnTeamSystem_OnPersistenceLoad`), sets `LifeTime.Duration` on the 60-minute buff
  prefabs whose names match `AB_Consumable_*Potion_T##_Buff` (potions) or `AB_Elixir_*_Buff` (elixirs).
  `docs/buffs-1.1.15.csv` is the `DumpBuffs` output for game 1.1.15 that the rule was chosen from.
  Imbuements (weapon coatings) aren't timed buffs: the mod sets `WeaponCoating.ExpireDuration` on every prefab with that component
  (vanilla 3600 s). The game copies it to the weapon's `Coatable.CoatingExpireTime` when a coating is applied.
- **Client:** checks the worlds about once a second; when `Client_0` has loaded its game data, applies the same change, then
  rewrites the tooltip text. Tooltip numbers are baked text: `LocalizedStringBuilderParameter` entries (`duration` = `"60"`)
  in a buffer on the item and buff prefabs. They are matched to the changed buffs by name (underscores, case and a possessive
  "s" before "potion" ignored); the client log names any 60-minute value it leaves alone.
- **Imbuement icon (client):** `BuffBarParentSystem.AddWeaponCoatingExpireEntry` builds the icon's entry with a fixed
  60-minute total (`LifeTime 3600`, `Age = 3600 - time left`), so with more than 60 minutes left the HUD hides the timer, and
  it hides the icon's own text every frame. A postfix records the server time (writing a corrected entry back into the
  game's list crashed the client); `ImbuementIconFixer` shows copies of the icon's time text and progress ring, driven from
  the weapon's `Coatable`, while more than 60 minutes are left. The hours text uses the game's own string (`LKey_Hours`).
