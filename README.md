# <img src="assets/ansi_art.png" alt="SteamCloudTamper" width="460" />

Steam Cloud saves for games you don't own — routed through buckets you do. Anti-flood, anti-ban, barcode-tracked parking for the post-April-2025 era.

Pronounced **SCT** like you'd say "sick" if you stubbed your toe.

> Valve decided to refuse cloud uploads for games your account does not own (server
> side, ~April 2025, no warning). SCT finds those buckets, probes what Valve still
> lets you do with them, wipes the ones it can, and **parks** saves you care about
> into appid containers you DO own — hidden dev apps, tools, free games, your own
> library. Parked files carry a barcode trailer so they never become anonymous junk.

---

## special thanks: Ace (∞/∞, would proxy again)

```
         +-------------------------------------------+
         |             <3  A C E  <3                 |
         |         github.com/AceSLS                 |
         |                                           |
         |  dev rating  : infinity/infinity          |
         |  patience    : >= a saint                 |
         |  helpfulness : 11/10, off the charts      |
         +-------------------------------------------+
              /( ^ x ^ )\  thank you!!  /( ^ x ^ )\
```

(◕‿◕) the appid proxy trick — unowned games riding an owned bucket under cute
little `sls-<game>/` prefixes — was **her** idea first. she wrote the original
patch ([docs/APPID-PROXY.md](docs/APPID-PROXY.md) is basically a love letter to
it), and then sat through a bazillion questions while we ported it into SCT without
the hook. changelist filtering? she knew. the cloud-cleaner forward-iteration bug?
she knew. "iterate backwards fyi" - yes ma'am, we do it backwards now, promise.
(つ≧▽≦)つ

<img src="assets/blush-watermark.svg" width="680" alt="she helped a lot. we would rate her dev skills infinity/infinity. if this readme could blush it would 🥰 ⋆｡°✩ 🎀" />

- [github.com/AceSLS](https://github.com/AceSLS) — go say hi, tell her SCT says thanks.

---

## table of contents

- [why it exists](#why-it-exists)
- [features at a glance](#features-at-a-glance)
- [quick start](#quick-start)
- [commands](#commands)
- [parking lanes](#parking-lanes)
- [the barcode lane](#the-barcode-lane)
- [parking allocator rules](#parking-allocator-rules)
- [smart appid containers](#smart-appid-containers)
- [synced in the steam GUI (OST + CloudRedirect)](#synced-in-the-steam-gui-ost--cloudredirect)
- [local isolation techniques](#local-isolation-techniques)
- [diagnostics: the doctor command](#diagnostics-the-doctor-command)
- [wipe reality check](#wipe-reality-check)
- [integrations & ecosystem](#integrations--ecosystem)
- [research notes](#research-notes)
- [known broken / wontfix](#known-broken--wontfix)
- [build from source](#build-from-source)
- [project layout](#project-layout)
- [anti-flooding rule](#anti-flooding-rule-all-lanes)

---

## why it exists

Before ~April 2025 you could upload a save file into any appid bucket. People
(aka the 760-era public dumpers, u know who u are) dumped EVERYTHING into 760 — the screenshots app —
so saves from 20 different games collided into one folder. Valve noticed. Valve
patched it. Now even *enumerating* an unowned bucket gives you `AccessDenied`
(tested, logged in, still denied — thanks Valve).

So the old "just upload it into 760 nobody will notice lmao" era is over. What still
works, because steel engine is dumb:

| What | Why |
|---|---|
| Spacewar (480) | Everyone owns it. Hidden test game. Buckets for hidden/apps/tools are normal. |
| Steam Client config (7) | Syncs to real UFS on this machine. We watched `Successfully synced to ChangeNumber'65'` happen in cloud_log. |
| Free games, mod hosts, SteamVR | Entitled to every account. |

So instead of **flooding** (which is what got the whole thing locked down), SCT does
the opposite: one small private file, one real app bucket, spread & mirrored, with a
trailer that tells you wtf it is. **Anti-ban is the whole point. Don't ruin it.**

---

## features at a glance

| Area | What SCT does |
|---|---|
| **Parking** | Routes unowned-game saves into safe appid containers you own. Smart allocator picks slots by tier, posture, co-tenancy, quota. |
| **Multiple lanes** | `client` (ride the running Steam session, no login), `rpc` (SCT logs in itself via QR/creds), `stage` (local only), `auto`. |
| **Barcode tracking** | Every parked save carries a self-identifying `SCTB1` trailer so a fresh machine can rebuild the whole map. |
| **AppID proxy** | Unowned saves ride an owned bucket under `sls-<game>/` prefixes — rpc-only, no client hook, Ace's trick ported in. |
| **Wipe / clean** | Delete or blank cloud files. Never-touch guard list persists so you don't nuke your own saves by accident. |
| **Local isolation** | Lockfile blocker, junction relocation, steam_api64 shim (`SteamCloudSave.dll`) — make stuck buckets go away. |
| **CloudRedirect provider** | SCT writes the CR folder-provider config so unowned apps show "synced" in the Steam GUI. |
| **Posture tracking** | Every slot records where its upload landed: `real` (Valve) / `provider` (CR folder) / `redirected` (OST hook) / `local` (staged). |
| **AutoCloud reality check** | Reads cloud_log to know which buckets the client actually AutoClouds vs which need the RPC lane. |
| **Verification** | `--verify` re-enumerates + SHA1-compares on the wire after every park upload. |
| **Diagnostics** | `doctor` command self-tests install, registry, pool, path safety — optionally live cloud RPC. |
| **Robustness** | Refresh-token caching, auto-reconnect, bounded retries, path-traversal sanitization. |
| **Web & ferry** | Read-only web backup lane (`SCT_COOKIE`), plus a best-effort rpc lane into owned 480. |

---

## quick start

```
dist\SteamCloudTamper.exe          # double-click = TUI, flags = CLI
```

1. `detect` — finds Steam + accounts + libraries
2. `scan` — audits local userdata buckets (per account/app) + barcode tags
3. `pool discover` — sweeps the machine for container appids
4. `pool probe` — lets the running Steam client test writability, reads verdict from cloud_log
5. `park <gameAppId>` — parks a game's saves into a safe bucket
6. `doctor` — sanity-check the whole setup first

No .NET runtime needed — the exe is self-contained. Running from source requires
the .NET 10 SDK (on this machine it lives at `C:\Users\kaneki\dotnet10` — yes
it's not on PATH, no we don't know why either).

**Emulated/modded saves (SLS / OST / etc.)**: the drop-in binary lives in
`tools\steamcloudsave\`. For SLS just run
`tools\steamcloudsave\install.ps1 -Mode sls -GameDir <game folder>` and launch via
SLS — the DLL shadows the game's cloud to `%LOCALAPPDATA%\SCT\shadow\<appid>`
with no other config. Then `park` that save into real UFS whenever you want it
safe.

---

## commands

### discovery & audit

| Command | Description |
|---|---|
| `detect` | Find Steam + accounts + libraries (it printed something, use it) |
| `scan` | Audit local userdata buckets (per account/app) + barcode tags |
| `remote-list --app <id>` | List files in a cloud bucket (may just say AccessDenied, embrace it) |
| `probe <appid...>` | Check what the backend allows: enumerate / upload / delete |
| `rebuild` | Tail-scan userdata -> registry.json (1000 files < 1s, ur welcome) |

### cloud operations

| Command | Description |
|---|---|
| `wipe <appid> <file> [--blank] [--force]` | Delete or blank one cloud file |
| `wipe-all <appid> [--blank] [--force]` | Wipe a whole bucket |
| `inject <uid3> <appid> <file> [remote-name]` | Local user drop + remotecache.vdf regen |
| `unpark <storageAppId> <name> [outdir]` | Download + strip barcode, original bytes back |
| `barcode <file>` \| `barcode make <payload>` | Show / render barcode trailers |

### guards (never-touch list)

| Command | Description |
|---|---|
| `guards add <appid>` | Persistently protect a bucket from accidental wipes |
| `guards rm <appid>` | Remove protection |
| `guards ls` | List protected buckets |

### local isolation

| Command | Description |
|---|---|
| `lock <uid3> <appid>` | Read-only file blocks Steam re-creating the folder |
| `unlock <uid3> <appid>` | Reverse the lock |
| `relocate <uid3> <appid>` | Junction-isolate bucket into the SCT stash |
| `unrelocate <uid3> <appid>` | Reverse the relocation |

### parking brain (anti-ban: private saves, real apps, never public flooding)

| Command | Description |
|---|---|
| `pool list` | Show the curated slot pool (owned-game buckets NEVER picked by default) |
| `pool refresh` | Re-curate the pool from the store API |
| `pool discover [--net]` | Sweep the machine for container appids: pool + userdata buckets + OST lua addappid hooks + CloudRedirect host + SLS/GreenLuma configs. `--net` also reads cloud_log for AutoCloud. |
| `pool probe [--uid] [--force] [--wait-sec N] [--lane client\|rpc] [--console]` | One private file, let the running Steam client sync it, read the verdict from cloud_log. `--lane rpc` probes via a real SCT logon. |
| `park <gameAppId> [options]` | Park a game's saves (see below) |
| `restore <gameAppId> [--uid] [--force] [--out <dir>] [--json]` | Pull a parked save back into the game's local bucket. Registry lookup; tails userdata as fallback. |
| `check [<gameAppId>] [--uid] [--json]` | Compare every parked save vs local game-bucket copy: match / diff / missing. Exit code 1 on any diff. |

#### park options

| Option | Description |
|---|---|
| `--uid <id3>` | Target a specific Steam user |
| `--lane auto\|client\|rpc\|stage` | `auto` = Steam up + right account -> client lane; else rpc. `client` = stage local, session uploads. `rpc` = SCT logs in itself (env creds or QR), uploads directly. `stage` = files local only. |
| `--bucket <appid>` | Pin all files to ONE explicit slot (refuses blocked/denied ones) |
| `--spread N` | Fan files across N buckets |
| `--copies N` | Duplicate so one purged bucket can't nuke your whole save |
| `--stealth` | Hash names so they look native (the trailer still knows) |
| `--verify` | Re-enumerate each bucket after uploading and SHA-compare what's on the wire |
| `--allow-owned` | OPT-IN consent: owned-game buckets join the universe. Never auto-picked without it. |
| `--posture real,provider,redirected\|any` | Filter the candidate universe. Default ranking: VerifiedWritable real > AutoClouded real > probe-candidate > provider/redirected. |
| `--proxy <appid>` | Ride an OWNED bucket instead of the pool: unowned saves parked under `sls-<game>/` namespace. RPC-only. |
| `--console` | Opt back into the Steam Console push (works on old client builds only) |

### proxy management

| Command | Description |
|---|---|
| `proxy status` | Show the appid-proxy map |
| `proxy set <game> <proxy>` | Map a game to a proxy bucket (game 0 = default for ALL unowned) |
| `proxy rm <game>` | Remove a mapping |
| `proxy ls` | List all mappings |

### client lane (riding the running session)

| Command | Description |
|---|---|
| `client status` | Show client lane status (Steam up? console open? CR loaded?) |
| `client sync <appid> [--down] [--console]` | Force a sync tick (rides the AutoCloud tick) |
| `client tell <command>` | Raw console command, e.g. `cloud_sync_up 480` |

### provider (CloudRedirect)

| Command | Description |
|---|---|
| `provider status` | Show CloudRedirect folder-provider status |
| `provider init [sync-dir]` | Initialize the provider |
| `provider ls [--uid] [--app]` | List provider contents |

### diagnostics

| Command | Description |
|---|---|
| `doctor [--cloud] [--verbose]` | Self-test: install, accounts, registry integrity, pool, path safety. `--cloud` also pings live RPC. 0 = all pass, 1 = something's wrong. |

### web & ferry lanes

| Command | Description |
|---|---|
| `web ls` | Web lane (needs `SCT_COOKIE`) |
| `web files <appid>` | List cloud files via web |
| `web dl <appid> <file> [outfile]` | Download via web |
| `ferry ls` | Park into owned 480 / spacewar |
| `ferry upload <local-file> [name]` | Upload via ferry |
| `ferry dl <name> [outfile]` | Download via ferry |

---

## parking lanes

SCT can get saves into the cloud four different ways. Pick per situation.

| Lane | What it does | Auth? | Best for |
|---|---|---|---|
| **client** | Stages files locally, forces `cloud_sync_up` through the RUNNING Steam console, reads the verdict from cloud_log | No SCT login | Steam is already up & signed in as the target account |
| **rpc** | SCT logs on itself (QR / creds / refresh token) and uploads directly via SteamKit | Yes (QR recommended) | Buckets the client never AutoClouds (480/113200), or proxy parking |
| **stage** | Drops files locally only; the client syncs them on its own tick later | No | You'll be back at the machine later |
| **auto** | Picks: Steam up + right account → client; else rpc | Varies | The default, zero-thought path |

**Refresh tokens**: once you've logged in with creds or QR, SCT caches your refresh
token to `%LOCALAPPDATA%\SCT\tokens.json` — the next connection reuses it instead of
re-prompting for Steam Guard. (Applies to `rpc` lane only.)

**AutoCloud reality check**: Steam only AutoClouds buckets it manages *itself*.
480 and 113200 are NEVER AutoClouded — a staged file there just sits there. Real
upload into those requires `--lane rpc`.

---

## the barcode lane

Parked saves get a trailer glued to their ass: `SCTB1` magic + CRC32 + payload

```
<original-game-appid>|<steam-userid3>|<DDMMYYYY>     e.g. 588650|1201110076|09082026
```

- The storage appid is NEVER in the payload — the bucket you're sitting in **is** the storage.
- Fresh PC, no registry, no problem: `rebuild` reads the last 4KB of every file and reconstructs the whole map.
- Unparking strips the trailer. Byte identical. Promise (there is a CRC32 so even a lie would be a verifiable lie).
- Every lane reads/writes the same `%LOCALAPPDATA%\SCT\registry.json`.

---

## parking allocator rules

In priority order:

1. **Tier priority**: hidden/dev apps, Valve tools, mod hosts > old free games. Owned-game buckets are tier 3 and stay excluded until you opt in with `--allow-owned` (TUI: a consent prompt).
2. **Posture ranking** (between same-tier real slots): VerifiedWritable real > AutoClouded real > probe-candidate. Provider/redirected activation containers (OST lua, SLS, GreenLuma) only fill in once real slots run out.
3. **Anti-ban**: server-`Denied` slots are skipped. `--spread` fans files out. `--copies` duplicates. `--stealth` hashes names.
4. **Co-tenancy wins**: a bucket already holding other parked games is preferred.
5. **Collision / quota**: next candidate. Deterministic. Boring. Safe.

---

## smart appid containers

`pool discover` builds the whole universe of appids SCT may park into:

| Source | What it finds |
|---|---|
| Pool slots | Spacewar, Steam Client 7, SteamVR suite, free games, mod hosts |
| Real userdata | Games in the library |
| OST lua `addappid` hooks | `config/lua/*.lua` — never touch Valve, posture says so |
| CloudRedirect host marker | When `[cloud]` is on |
| SLS/Goldberg | `steam_settings/appid.txt` |
| GreenLuma | `appidwhitelist.txt` (auto-probed, absent = skipped) |
| AppID proxies | CloudProxies map — posture `proxied` |

Each container gets: kind (owned/free/hidden/modhost/activation), source, posture, AutoClouded flag. The TUI has the same view (Registry -> "Show discovered containers").

> Actually proven on this machine: 41 containers. 5 lua hooks, 1 CR host marker, rest pool+real. Yes we count these things. It's a hobby.

---

## synced in the Steam GUI (OST + CloudRedirect, verified 2026-08-10)

For unowned games (Dead Cells 588650 etc) the client kept crying "cloud sync error". Fix, proven end to end:

1. OpenSteamTool built from main (v1.4.8 release predates the `[cloud]` host)
2. CloudRedirect v2.6.4
3. `[cloud] enabled=true` in `opensteamtool.toml`, provider folder `D:\sct_provider`

cloud_log goes from `Access Denied` to `HTTP upload ... success` -> `Upload complete, result OK`, GUI shows the cloud icon, everyone claps. Details in `integrations/opensteamtool/OST-HOST-BUILD.md`.

**Posture matters**: SCT now correctly reports per-appid posture. Only OST-hooked apps get `provider` (CR intercepting them); unhooked apps report `real` even when CR is loaded — so your genuinely-owned buckets are never confused for CR folders.

---

## local isolation techniques

| Technique | How | Command |
|---|---|---|
| **CloudRedirect nullify** | Point the redirect at an empty private folder. Game never sees Steam's copy again. | CR config (SCT owns it) |
| **Lockfile blocker** | Windows refuses to create a folder where a file with that name exists. Delete the folder, plant a read-only file. Steam fails sync re-creation silently. | `lock` / `unlock` |
| **Junction isolation** | Move the bucket into `%LOCALAPPDATA%\SCT\stash`, leave a junction. Steam reads/writes through the junction without knowing. | `relocate` / `unrelocate` |
| **Hook lane** | `SteamCloudSave.dll` — auto-detecting steam_api64 shim / `load_dlls` / OST `[inject]`: flags the game, all ISteamRemoteStorage calls get shadowed under `%LOCALAPPDATA%\SCT\shadow\<appid>\` (no config file needed; it reads Steam from the registry and the appid from `steam_appid.txt`). Drop it in any loader. | `tools/steamcloudsave` |

Some games get mad about the shadow. Those are called "unlocks".

---

## diagnostics: the doctor command

`SCT doctor` runs a self-test and tells you exactly what's broken:

```
  [OK  ] steam-install: C:\Steam
  [OK  ] steam-running: yes
  [OK  ] config-file: steamcloudtamper.json
  [OK  ] registry-file: %LOCALAPPDATA%\SCT\registry.json
  [OK  ] registry-slots: 12 slot(s)
  [OK  ] registry-discovered: 41 container(s)
  [OK  ] pool-usable: 12 slot(s)
  [OK  ] path-sanitizer: basic file safety check
  [OK  ] path-sanitizer-reject: traversal rejection check

    10/10 checks passed
```

- `SCT doctor --cloud` also connects to Steam and ping-checks the cloud RPC.
- `SCT doctor --verbose` prints everything (including passing checks).
- Exit code 0 = all good, 1 = at least one check failed.

It'll flag things like stale registry slots (storage appids that no longer exist in
the pool or discovered containers), a missing/corrupt registry, or a blocked pool.

---

## wipe reality check

Valve denies uploads to unowned games, server side, since ~April 2025, and even enumeration is denied. So wiping is a three-step dance:

1. `remote-list --app <id>` — does the bucket even exist / what's in it
2. `probe <id>` — what does your account get to do: enumerate / upload / delete
3. `wipe <id> <file>` — try delete, then blank-overwrite, accept the result

Some things are just permanent. Like that one save from 2013. It hears you. It remembers.

---

## integrations & ecosystem

| Tool | Lane | Status |
|---|---|---|
| **SCT CLI/TUI** | native | full (pool, park, ferry, barcode, registry, wipe, web, proxy, doctor) |
| **SCT client lane** | running Steam session (no login): stage locally + CloudLogWatcher verdict | full |
| **SCT posture tracker** | per-slot `real` / `provider` / `redirected` / `local` in registry.json | full |
| **SCT container discovery** | `pool discover [--net]`: PoolDb + userdata + OST lua + CR host + SLS/Goldberg + GreenLuma | full |
| **SLSsteam / SLS fork / gbe_fork** | `steam_settings\load_dlls\SteamCloudSave.dll` (auto `LoadLibraryW`; the DLL auto-detects steam/appid/shadow). `install.ps1 -Mode sls` mounts it for you. | shipped (tools/steamcloudsave) |
| **OpenSteamTool / BetterSteamTools** | `[cloud]` + CloudRedirect host, `[inject]` for SteamCloudSave.dll, Lua pool snippet | shipped (integrations/opensteamtool) |
| **universal mount** | `tools\steamcloudsave\install.ps1` auto-detects your loader (SHIM / `load_dlls` / SLS / OST `[inject]`) and mounts the DLL for you | shipped (tools/steamcloudsave/install.ps1) |
| **CloudRedirect** | provider engine: local-folder provider answers Cloud.* RPCs locally, GUI shows "synced" — no UFS uploads | shipped (v2.6.4) |
| **SCT appid proxy lane** | rpc lane with `sls-<game>/` namespacing into owned bucket | full |
| **Ace SLS appId proxy** | in-process `CClientUnifiedServiceTransport` hook — the client-side version SCT ports in | research + lessons in APPID-PROXY.md |

See [docs/INTEGRATIONS.md](docs/INTEGRATIONS.md) for the full matrix and naming contract.

> **Honest note about "drop the DLL in any mod"**: native-DLL loaders — `load_dlls`-family
> (gbe_fork / SLS fork / SLSsteam / Goldberg), OST/BetterSteamTools `[inject]`, and the SHIM
> rename — load SteamCloudSave.dll right into the game process. Entitlement-only tools
> (GreenLuma) and CEF JS UI plugins (e.g. Millennium) have no game-process DLL slot, so for
> those SCT integrates at the data layer (`registry.json` / `shadow-status.json`).
> See `tools/steamcloudsave/README.md`.

---

## research notes

- **760 pollution**: the 760-era public dump rewrote cloud requests for unowned games into appid 760 without per-game prefixes, so saves collided across games. STFixer, CloudRedirect, and this repo all started with the same bruised knuckles.
- **Valve patch April 2025**: cloud UFS for unowned appids -> `AccessDenied` on enumerate/upload/delete. Confirmed even logged in.
- **Retail SteamCloudFileManager**: even they get physically rejected for special internal appids (760/7) server side, so they resort to CDP-hijacked web sessions. Our web lane is read-only by design — same wall, less credit card drama.
- **Old conflict-dialog trick** (zero files, delete remotecache, "upload nothing"): predates the 2025 patch and only really works for owned games now.
- **Active lanes for stuck unowned buckets**: web (read/backup), ferry (480/hidden), barcode park (allocator, spread/copies/stealth), client lane (staged via running session), local lockout/junction, hook shim, CloudRedirect via OST `[cloud]`, and Steam support tickets (may god have mercy on your soul).
- **No flooding. Ever.** Every probe/park write is one small private file in a real app's bucket. The 760 mass-dump is EXACTLY what got UFS locked down. SCT does the opposite on purpose.

---

## known broken / wontfix

- **Steam Console**: unreliable on current client builds, so the default lane is the tick. Old-build users can re-enable with `--console`.
- **480 / 113200**: never AutoClouded by the client. RPC lane or bust (anonymous uploads are denied even for spacewar. Valve said so. We screamed).
- **Config location**: `steamcloudtamper.json` next to the process, or wherever `SCT_CONFIG` points. Registry lives at `%LOCALAPPDATA%\SCT\registry.json` (or `SCT_REGISTRY`).
- **No credentials ship with the program.** `SCT_USER`/`SCT_PASS` or scan a QR in the TUI.

---

## build from source

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1      # -> dist\SteamCloudTamper.exe
dotnet test tests\SteamCloudTamper.Core.Tests                    # 63 passing, usually
```

TUI extras: `SCT_TUI_ASCII=1` // `SCT_TUI_NERD=1` // `SCT_TUI_FLAT=1` if you hate gradients.
Crash log (it never happens, but if it does): `%LOCALAPPDATA%\SCT\tui-crash.log`

---

## project layout

| Project | What it is |
|---|---|
| `src/SteamCloudTamper.Core` | Models, VDF parser/writer, remotecache generator, root-path map, Steam install/account discovery, config, path sanitizer, and the parking brain (PoolDb + registry + allocator + discoverer) |
| `src/SteamCloudTamper.Engines` | SteamSession (anon / creds / QR / refresh token, auto-reconnect), CloudRpcClient (the actual cloud RPCs), AuditEngine, WipeEngine, LocalInjectEngine (lock/relocate), CloudLogWatcher, SteamWebClient |
| `src/SteamCloudTamper.Cli` | Command line face. Good old `cmd`, no sparkles. |
| `src/SteamCloudTamper.Tui` | The pretty face. Spectre.Console, gradients, glow, a sine wave, QR rendering in the terminal. Yes we know the glow is excessive. No we won't remove it. |
| `src/SteamCloudTamper` | The one exe that figures out which face you want (no args + console = TUI, flags = CLI). `tools/publish.ps1` builds the self-contained single file into `dist/`. |
| `tools/steamcloudsave` | `SteamCloudSave.dll` — the drop-in binary: auto-detecting steam_api64 shim / shadow lane for games that should never touch your real cloud. `install.ps1` mounts it into whatever mod you run. |
| `tools/ost-host` | Prebuilt OST/CloudRedirect host DLL bundle for the GUI-synced lane. |
| `integrations/opensteamtool` | OST toml snippet, lua pool snippet, the "make the GUI show synced" writeup. |
| `tests/SteamCloudTamper.Core.Tests` | xunit. The ones that SSH into Steam are the fun ones. |

---

## anti-flooding rule (all lanes)

SCT never mass-uploads, never uses public/anonymous dumps (the 760-era anonymous-dump pattern is what got cloud UFS locked down). Every write is ONE small private file in a REAL app's cloud bucket — chosen by PoolDb, verified per-account by `pool probe`, and spread/mirrored via `--spread`/`--copies` so no single slot ever carries a detectable pattern.
