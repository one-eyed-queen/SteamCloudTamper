# Integration matrix — how SteamCloudSave/SCT plugs into the ecosystem

roughly "what talks to what and who holds the flashlight". everything below was
checked against the real build, the ones with (live) tags actually ran on a real
steam install and we watched the logs. the rest is best-effort from the other
projects' docs, so blame them if something drifts.

| Tool | Lane | Status |
|---|---|---|
| **SCT CLI/TUI** | native | full (pool, park, ferry, barcode, registry, wipe, web, proxy) |
| **SCT client lane** | running Steam session (no login): stage locally + `CloudLogWatcher` verdict; AutoCloud tick only (Steam Console blocked on newer builds); non-AutoCloud buckets (480/113200) need `--lane rpc` for real uploads | full |
| **SCT posture tracker** | per-slot `real` / `provider` / `redirected` / `local` in registry.json, derived live by `SteamLocator.SyncPosture()` (OST lua hook, CR toml/dll, else Valve) | full |

| **SCT container discovery** | `pool discover [--net]`: universe = PoolDb + userdata buckets + OST lua `addappid` hooks + CloudRedirect host + SLS/Goldberg `steam_settings` + GreenLuma `appidwhitelist`; per-container kind/source/posture + AutoClouded flag, snapshot in registry.json (`Discovered`); TUI: Registry → "Show discovered containers" | full |
| **SLSsteam / SLS fork / gbe_fork** | `steam_settings\load_dlls\SteamCloudSave.dll` (auto `LoadLibraryW`); DLL now auto-detects steam/appid/shadowRoot so no config needed. First-class: `install.ps1 -Mode sls -GameDir <game>` drops the DLL + writes `steam_settings\appid.txt` for you. | shipped (tools/steamcloudsave) |
| **OpenSteamTool / BetterSteamTools** | `[cloud]` +CloudRedirect host, `[inject]` for SteamCloudSave.dll, Lua pool snippet — **GUI-synced lane, live-verified** (build from `main`, see OST-HOST-BUILD.md). Same project: BetterSteamTools IS OpenSteamTool. | shipped (integrations/opensteamtool) |
| **CloudRedirect** | provider engine: local-folder provider (`%AppData%\CloudRedirect\config.json` → `D:\sct_provider`) answers Cloud.* RPCs locally (127.0.0.1), so the Steam GUI shows "synced" for every `addappid()` game — no UFS uploads ever | shipped (v2.6.4) |
| **SLS / GreenLuma** | entitlement: read via `pool discover` (SLS `steam_settings`, GreenLuma `appidwhitelist`); no game-process DLL slot for GreenLuma | code to copy (Core/Pool) |
| **universal mount** | `tools\steamcloudsave\install.ps1` auto-detects the loader (SHIM / `load_dlls` / SLS / OST `[inject]`) and mounts SteamCloudSave.dll for you; DLL autodetects steam path + appid + shadowRoot at runtime | shipped (tools/steamcloudsave/install.ps1) |
| **shadow status bridge** | DLL writes `%LOCALAPPDATA%\SCT\shadow-status.json` (gameAppId/redirected/files/bytes/shadowRoot/loader) after each shadow write; SCT CLI/TUI read it to know which games are live-shadowed, without racing the CLI-owned `registry.json` | shipped (tools/steamcloudsave) |
| **auto-park (opt-in)** | `autoPark` config: DLL spawns SCT CLI detached (`park <appid> --lane rpc --stealth`) after a big shadow write, rate-limited to 1/30s, gated by `minShadowBytes` — the safe bridge from "game saved" to "SCT parked to UFS" | shipped (tools/steamcloudsave) |

| **SCT appid proxy lane** | rpc lane with the `sls-<game>/` namespacing: unowned saves park inside an OWNED proxy bucket (CloudProxies map in config; `park --proxy`, `proxy` command, TUI Settings). no hook - SteamKit writes the proxy appid + prefixed name itself; `remote-list`/`unpark`/`wipe` are proxy-aware; `pool discover` shows proxy buckets as posture `proxied` | full (implemented 2026-08-13) |
| **Ace SLS appId proxy** | in-process `CClientUnifiedServiceTransport` hook rewrites `Cloud.*` calls for unowned apps into an owned proxy appid + `sls-<appid>/` filename namespacing (valid changelist for unowned games, verified by author 2026-08). the same idea SCT now does via its rpc lane - this hook is the client-side version (OST/CR-family host). research + lessons in `APPID-PROXY.md` | implemented in SCT (rpc lane); hook itself code to copy (host) |
| **online-fix.me** | their cracked steam_api64.dll is a steamclient-side shim; SteamCloudSave.dll can be load-listed or its exports copied | doc only |
| **Millennium (SteamClientHomebrew)** | plugins are **JS/CSS for the Steam CEF UI**, not native DLLs — SCT integrates at the data layer (`registry.json` / `shadow-status.json` read by a tiny plugin) or via the `SteamCloudSave.dll` lanes; there is no native plugin slot | doc only |
| **SteamRE / SteamDatabase** | knowledge base feeding pool curation + docs | doc only |
| **DepotDownloader (anonymous mods)** | used by `pool refresh` to verify entitlement-agnostic download works for candidate slots | doc only |

## Anti-flooding rule (all lanes)

SCT never mass-uploads, never uses public/anonymous dumps (the 760-era anonymous-dump pattern
is what got cloud UFS locked down). Every write is ONE small private file in a REAL
app's cloud bucket — chosen by `PoolDb` (Spacewar, SteamVR tools, cloud test app,
free-game buckets, mod hosts), verified per-account by `pool probe` (client-lane
verdict from `cloud_log.txt`), and spread/mirrored via `--spread`/`--copies` so no
single slot ever carries a detectable pattern.

## The shared truth
All lanes read/write the SAME files:
- `%LOCALAPPDATA%\SCT\registry.json` — game -> storage slot map (read anytime, `sct rebuild`)
- barcode trailers in parked saves (`SCTB1` + `gameAppId|uid3|DDMMYYYY` + CRC32) —
  any lane can re-derive the map from the data itself
- `steamcloudsave.cfg` — per-DLL config (steamPath / shadowRoot / appid)

## Naming contract (all lanes, identical casing)
- DLL: `SteamCloudSave.dll`, public API `SteamCloudSave_Init/Shutdown/State/App/ShadowRoot`
- Registry file: `registry.json` (magic `SCTREG1`)
- Trailer magic: `SCTB1`, payload `<gameAppId>|<uid3>|<DDMMYYYY>`
- C# code: PascalCase public API, `SteamCloudSave` name in every project file name

if ur writing a new lane and u violate the naming contract the registry will
silently pretend u dont exist and we will both be sad. dont be sad. be consistent.