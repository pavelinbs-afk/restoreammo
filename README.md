# RestoreBullets

[Русский](README.ru.md)

Counter-Strike 2 plugin for [CounterStrikeSharp](https://docs.cssharp.dev/) that restores **reserve ammo** (not the clip) when a weapon is completely empty. Active until the round ends.

## Features

- Grants **1 reserve magazine** for clip-based weapons (Deagle, Elite, AK, AWP, etc.)
- The clip stays empty — press **R** to reload
- After the reserve is used, another magazine is granted while the round is active
- For shotguns (Nova, XM1014), grants a full reserve shell count (7–8 rounds)
- Does not affect knives, grenades, C4, Zeus, or healthshot

## Requirements

- CS2 dedicated server
- [Metamod:Source](https://www.sourcemm.net/)
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) (API ≥ 80)
- .NET 8 SDK (for building)

## Installation

1. Build the project or download `RestoreBullets.dll` from releases
2. Copy to your server:

```
csgo/addons/counterstrikesharp/plugins/RestoreBullets/RestoreBullets.dll
```

3. Restart the map or server

## Build

```bash
dotnet build RestoreBullets.csproj -c Release
```

Output:

```
bin/Release/net8.0/RestoreBullets.dll
```

## Configuration

Created automatically on first load:

```
csgo/addons/counterstrikesharp/configs/plugins/RestoreBullets/RestoreBullets.json
```

```json
{
  "Enabled": true,
  "CheckIntervalSeconds": 0.25,
  "Debug": false,
  "ConfigVersion": 1
}
```

| Option | Description |
|---|---|
| `Enabled` | Enable or disable the plugin |
| `CheckIntervalSeconds` | How often to check players (seconds, min. 0.05) |
| `Debug` | Verbose logging to the server console |

## Commands

| Command | Description |
|---|---|
| `css_restorebullets_debug` | Print ammo state for a player |
| `css_restorebullets_test` | Force-restore reserve on the active weapon (in-game) |

## How it works

1. Player empties the weapon: **clip = 0**, **reserve = 0**
2. Plugin adds **1 reserve magazine** (HUD: `0 | 1`)
3. Player reloads — ammo moves into the clip
4. Cycle repeats until `round_end`

Ammo amounts are taken from weapon **VData** (`MaxClip1`); no per-weapon config list is required.

## Logs

On successful restore, the server console shows:

```
[RestoreBullets] Restored PlayerName weapon=weapon_deagle amount=1 clipAfter=0 reserveAfter=1 ...
```

## License

[MIT](LICENSE)

## Author

pRfect
