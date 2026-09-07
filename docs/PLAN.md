# SCT next-phase plan (2026-08-10)

Status: SHIPPED (commit after this doc) - sections 1-3 implemented, section 4 done:
30/30 tests, live `pool discover --net` on this machine (41 containers: PoolDb + lua
hooks + CR host; AutoClouded flags from cloud_log), TUI "Show discovered containers"
screen plus gradient/glow/sine TUI effects, cleanup done (UnitTest1.cs + ApiProbe
deleted, `dist/` gitignored), one 78.8 MB self-contained `dist\SteamCloudTamper.exe`
(double-click = TUI, flags = CLI) via `tools/publish.ps1`.

SHIPPED (later): `--allow-owned` parking consent + `--posture any/filters` on `park` -
owned-game buckets (userdata containers + OwnedReserved pool entries) are OPT-IN
(CLI flag `--allow-owned`, TUI consent prompt) and never auto-picked without it; the
allocator consults the discovered container universe, posture ranking is
VerifiedWritable real > AutoClouded real > probe-candidate > provider/redirected
(activation containers only fill in once real slots run out), `--posture <csv>` is an
authoritative filter, `--bucket` enforces the same consent/filter checks, re-parking
keeps a proven owned slot only with consent. 45/45 tests.

Not yet built (next): SLS shipping integrations, `pool discover` scoped by posture.

## 1. Entitlement-driven container discovery (the core feature)

Goal: store cloud saves of UNOWNED games into **owned / free / hidden / tool** apps
and games the account IS entitled to - "smart switching appid containers".

New `PoolDiscoverer` (Core/Pool) sweeps the machine and builds the container universe:

- **Owned / universal** - userdata real buckets, installed owned games (e.g. Schedule 1
  2371090), Spacewar 480, AppID 7 (already verified real-syncing, CN 65)
- **Free / hidden / tools** - existing PoolDb (SteamVR suite, cloud test app, mod hosts)
  + StoreApi naming
- **Activation-tool containers** - OST Lua `addappid` hooks (113200, 1623730, 3164500,
  3722330, 588650), `opensteamtool.toml`, SLS/GreenLuma/Goldberg configs (auto-probed;
  absent = skipped)
- **Posture per container**: `real` / `provider` / `redirected` + `AutoClouded?` +
  probe state - persisted into `registry.json`

Policy change: owned-game buckets become OPT-IN real-cloud containers
(`--allow-owned` / TUI prompt; never auto-picked without consent).
Ranking: VerifiedWritable real > AutoClouded real > probe-candidate >
provider/redirected (activation containers only when real isn't available or
`--posture any`). Barcode/spread/copies rules unchanged - containers are
interchangeable, barcodes disambiguate. [DONE - commit after this doc's status line]

SCT as addon/plugin: independent single exe (always works), plus shipped host hooks:
OST `[inject]` + lua (live), CloudRedirect host (live), gbe `load_dlls`, SLS/GL config
read (new), Millennium doc. `pool discover` shows each container's source host.

## 2. Single-file deliverable

- Publish: `-r win-x64 --self-contained -PublishSingleFile` -> ONE `SteamCloudTamper.exe`
  (~70 MB, no sibling DLLs, no runtime needed)
- Double-click = TUI; flags = CLI; `--tui` / `--cli` / `--version` force faces
- `tools/publish.ps1` wrapper; output to `dist/` (gitignored)
- Root cause of the earlier double-click failure: no published exe existed; the
  bin exe is framework-dependent and the .NET 10 SDK sits at a custom path
  (C:\Users\kaneki\dotnet10, not on PATH), so it died instantly and the app
  assembly showed up as a sibling SteamCloudTamper.dll

## 3. Repo cleanup

- Delete `tests/SteamCloudTamper.Core.Tests/UnitTest1.cs` (empty stub)
- Delete `tools/SteamCloudTamper.ApiProbe` (one-shot SteamKit reflection dump)
- Add `dist/` to `.gitignore`
- Keep the 4-project source layout (Core/Engines/Cli/Tui) - deliverable stays 1 exe

## 4. Verify + ship

- Build all, 25+ tests
- Live `pool discover` on this machine (expect: 5 lua containers + 480/7 real
  + userdata buckets)
- TUI "Discovered containers" screen
- Docs: README discovery section + plugin/addon matrix row
- Commit + push to GitHub (standing rule)

## User decisions (2026-08-10)

- ApiProbe: DELETE
- Source layout: keep 4 projects
- Container policy: store unowned saves into owned/free/hidden/tool containers;
  SCT works as plugin/addon for Steam/OST/SLS/GL or independent
- Publish target: local `dist/` only (gitignored); GitHub stays source-only
