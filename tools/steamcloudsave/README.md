# SteamCloudSave.dll — the "just drop it in" binary

The single DLL that works across the whole mod ecosystem. Mount it however your
loader expects, and it shadows a game's RemoteStorage to a local folder so the
game never touches Steam's cloud (or, with a config, forwards to the real
`steam_api64.dll`).

## Autodetect (no config file needed for the common case)

Load it and it figures out the rest on its own:

| Setting | Auto-detected from |
|---|---|
| `steamPath` | Windows registry (`HKCU`/`HKLM` Valve\Steam), like `SteamLocator.cs` |
| `appid` | `<game folder>\steam_appid.txt` |
| `shadowRoot` | `%LOCALAPPDATA%\SCT\shadow\<appid>` |
| loader context | where the DLL lives (`load_dlls\` = SLSsteam / SLS fork / gbe_fork, `steam_api64.dll` = shim) |

Config still overrides everything: `steamcloudsave.cfg` next to the DLL, or
`%LOCALAPPDATA%\SCT\steamcloudsave.cfg`, or `SCT_SCS_CONFIG`/`SCT_HOOK_CONFIG`
enviroment variables (see `steamcloudsave.cfg.EXAMPLE`).

## Mounting styles (drop-in for whatever you run)

| Mounting style | How | Result |
|---|---|---|
| SHIM | rename `SteamCloudSave.dll` -> `steam_api64.dll` in the game (back up the real one) | classic shadow redirect, single game |
| **SLSsteam / SLS fork / gbe_fork** | copy `SteamCloudSave.dll` into `<game>/steam_settings/load_dlls/` | auto-loaded via `LoadLibraryW` |
| OpenSteamTool / BetterSteamTools | `[inject]` in `opensteamtool.toml` (`library_x64`/`library_x86`) | mounted into the game |
| **install.ps1** | `.\install.ps1` auto-detects which of the above you run and mounts for you | one command, any loader |

### SLSsteam — first-class ("I just want it to work with SLS")

SLS (and its gbe/SLS-fork emulators) auto-load every DLL in the game's
`steam_settings\load_dlls\` folder via `LoadLibraryW`. There is nothing else to
configure — the DLL reads the Steam path from the registry and the appid from
`steam_settings\appid.txt` / `steam_appid.txt`.

```
# before first run, one command:
PowerShell -ExecutionPolicy Bypass -File install.ps1 -Mode sls -GameDir "D:\...\steamapps\common\MyGame"
```

This copies `SteamCloudSave.dll` into `steam_settings\load_dlls\` and writes
`steam_settings\appid.txt` if it's missing. Launch the game through SLS and the
game's RemoteStorage I/O shadows to `%LOCALAPPDATA%\SCT\shadow\<appid>\`.

> Emulator note: when SLS is the emulator, the game is likely running against the
> emulator's own `steam_api64.dll`, so the SHIM style is unnecessary — `load_dlls`
> is the SLS way. The emulator handles entitlement; SCT only owns the cloud shadow +
> (via the CLI's `park`) real UFS parking for saves you want out of the emulator's
> reach entirely.

## Status + background parking (optional)

- Every shadow write updates `%LOCALAPPDATA%\SCT\shadow-status.json`
  (`gameAppId`, `redirected`, `files`, `bytes`, `shadowRoot`, `loader`) so the SCT
  CLI/TUI know which games are live-shadowed. This is separate from `registry.json`
  (which only the CLI writes) so the two never race.
- **Auto-park (opt-in)**: set `autoPark=<path to SteamCloudTamper.exe>` in the
  config. After a shadow write the DLL spawns the SCT CLI *detached* to
  `park <appid> --lane rpc --stealth` in the background, rate-limited to one
  spawn/30s and gated by `minShadowBytes`. This is the safe bridge between "game
  wrote a save" and "SCT parked it to real UFS". It is off by default and should
  stay off unless you specifically want hands-off uploads.

## Honest per-loader matrix

Only some mod tools can load native DLLs into game processes:

| Tool | Mounts SteamCloudSave.dll? | How | Notes |
|---|---|---|---|
| **SLSsteam / SLS fork / gbe_fork / Goldberg** | ✅ | `steam_settings\load_dlls\` | first-class; primary citizen for emulated/cracked games |
| **OpenSteamTool / BetterSteamTools** | ✅ | `[inject]` → `library_x64`/`library_x86` | same project (BST = OST); also hosts CloudRedirect for GUI-sync |
| GreenLuma | ⚠️ entitlement only | reads `appidwhitelist.txt` | unlocks apps; no game-process DLL slot — SCT reads it via `pool discover` |
| Millennium (SteamClientHomebrew) | ❌ | Steam CEF JS/CSS plugin | no native game-process slot; SCT plugs in at `registry.json`/`shadow-status.json` |

So "one DLL, drop it into any mod" is literally true for the `load_dlls`-family
loaders (SLSsteam, SLS fork, gbe_fork, Goldberg), OST/BetterSteamTools `[inject]`,
and the SHIM rename. Entitlement-only tools (GreenLuma) and CEF JS UI plugins
(Millennium) can't load native DLLs into a game — for those, SCT's real interface
is the data files it writes (`registry.json` / `shadow-status.json`).

## Exports for other tools

`SteamCloudSave_Init(configPath)`, `SteamCloudSave_Shutdown()`, `SteamCloudSave_State()`,
`SteamCloudSave_App()`, `SteamCloudSave_ShadowRoot()`.
Plus the full `SteamAPI_*` / `SteamRemoteStorage_*` export surface so games linking
either naming convention reach the shim first.

## Build

`.\build.ps1` (MinGW gcc; set `$env:GCC` if not on PATH). Links `advapi32` (registry)
and `shell32` (LOCALAPPDATA lookup).
