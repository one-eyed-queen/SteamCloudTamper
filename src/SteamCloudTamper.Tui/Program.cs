using Spectre.Console;
using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Pool;
using SteamCloudTamper.Core.Steam;
using SteamCloudTamper.Engines;

namespace SteamCloudTamper.Tui;

public static class Program
{
    // config lives at SCT_CONFIG env var, else %APPDATA%/SCT/sct.toml (~/.config on Linux)
    private static readonly string ConfigPath = AppConfig.ResolveDefaultPath();

    private static AppConfig _cfg = null!;
    private static string _steamPath = "";
    private static List<SteamAccount> _accounts = [];
    private static List<Bucket> _buckets = [];
    private static SctRegistry _registry = null!;

    public static async Task<int> Main()
    {
        var crashLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCT", "tui-crash.log");

        // Ctrl+C must never silently kill the TUI: log it and turn it into a graceful
        // "aborted" (the console-level handler still lets the OS stop us via window close).
        Console.CancelKeyPress += (_, e) =>
        {
            WriteCrash(crashLog, "CancelKeyPress", new Exception("ctrl+c reached the TUI - captured" +
                (AnsiConsole.Profile.Capabilities.Interactive ? " (interactive)" : " (non-interactive)")));
            e.Cancel = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash(crashLog, "UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash(crashLog, "UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            WriteCrash(crashLog, "ProcessExit", new Exception($"TUI exiting cleanly for pid={Environment.ProcessId}"));

        WriteCrash(crashLog, "Boot", new Exception($"TUI booted pid={Environment.ProcessId} at {DateTime.UtcNow:HH:mm:ss}"));

        try
        {
            return await MainInner(crashLog);
        }
        catch (Exception ex)
        {
            WriteCrash(crashLog, "TopLevel", ex);
            AnsiConsole.MarkupLine($"[red]fatal: {Markup.Escape(ex.Message)}[/] - see {Markup.Escape(crashLog)}");
            return 1;
        }
    }

    private static async Task<int> MainInner(string crashLog)
    {
        AnsiTerminal.Enable();
        var brand = Branding.RenderRawBrand();
        if (brand.Length > 0)
        {
            // raw ANSI passthrough - full file, every ESC color sequence intact
            Console.Out.Write(brand);
            if (!brand.EndsWith('\n')) Console.Out.WriteLine();
            Console.Out.WriteLine();
            TuiFx.Reveal();
        }
        else
        {
            AnsiConsole.MarkupLine("[bold aqua]STEAM CLOUD SAVER[/] - park, tag, ferry, survive.");
            Console.WriteLine();
            TuiFx.Splash();
        }

        _cfg = AppConfig.Load(ConfigPath);
        _steamPath = _cfg.SteamPathOverride ?? SteamLocator.DetectInstallPath() ?? "";
        if (string.IsNullOrEmpty(_steamPath))
        {
            _steamPath = AnsiConsole.Ask<string>("Steam not detected. Enter the Steam install folder:");
            _cfg.SteamPathOverride = _steamPath;
            _cfg.Save(ConfigPath);
        }

        RefreshLocal();

        while (true)
        {
            // Key + display label separated: the labels carry icon glyphs and ANSI markup,
            // so dispatch on the KEY, never on choice[0] of the label.
            var items = new (string Key, string Label)[]
            {
                ("1", $"{Ui.Icon("folder")} {TuiFx.Data("1")} Buckets       - audit local userdata"),
                ("2", $"{Ui.Icon("cloud")} {TuiFx.Data("2")} Remote        - list cloud buckets via Steam"),
                ("3", $"{Ui.Icon("ferry")} {TuiFx.Data("3")} Ferry         - park saves into owned AppID 480 (Spacewar)"),
                ("4", $"{Ui.Icon("park")} {TuiFx.Data("4")} Park smart    - barcode park local buckets (never owned games)"),
                ("5", $"{Ui.Icon("wipe")} {TuiFx.Data("5")} Wipe          - delete/blank bucket files (dry-run by default)"),
                ("6", $"{Ui.Icon("registry")} {TuiFx.Data("6")} Registry     - slot map + parking pool"),
                ("7", $"{Ui.Icon("shield")} {TuiFx.Data("7")} Guards        - never-touch appid list"),
                ("8", $"{Ui.Icon("gear")} {TuiFx.Data("8")} Settings      - dry-run, owned list, steam path"),
                ("9", $"{Ui.Icon("qr")} {TuiFx.Data("9")} Logon         - QR / credentials session (opens the real doors)"),
                ("0", $"{Ui.Icon("x")} {TuiFx.Data("0")} Quit"),
            };

            var pick = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title(MenuTitle())
                .AddChoices(items.Select(i => i.Label)));
            var key = items.First(i => i.Label == pick).Key;

            try
            {
                switch (key)
                {
                    case "1": BucketsScreen(); break;
                    case "2": await RemoteScreenAsync(); break;
                    case "3": await FerryScreenAsync(); break;
                    case "4": await ParkScreenAsync(); break;
                    case "5": await WipeScreenAsync(); break;
                    case "6": await RegistryScreenAsync(); break;
                    case "7": GuardsScreen(); break;
                    case "8": SettingsScreen(); break;
                    case "9": await LogonScreenAsync(); break;
                    default: return 0;
                }
            }
            catch (Exception ex)
            {
                // a screen must never take the TUI down - report and go back to the menu
                // (pro tip: this is a net. if it wasnt there the whole app would
                //  dissapear on a silly QR timeout and we'd hear about it. again.)
                AnsiConsole.MarkupLine($"[red]screen error: {Markup.Escape(ex.Message)}[/]");
            }
            Console.WriteLine();
        }
    }

    private static string MenuTitle()
    {
        var slots = _registry.Slots.Count;
        var active = SteamLocator.GetActiveAccount(_steamPath);
        var session = active is not null
            ? $"{Ui.Icon("check")} [green]{active.AccountId}[/]{(SteamLocator.IsRunning() ? " (Steam running)" : "")}"
            : "[red]no signed-in Steam[/]";
        return $"{TuiFx.Brand("SCT")} | steam: [cyan]{_steamPath}[/] | session: {session} | owned: [yellow]{_cfg.GetOwnedSet().Count}[/] | buckets: [yellow]{_buckets.Count}[/] | slots: [aqua]{slots}[/] | {TuiFx.Glow(_cfg.DryRun ? "dry-run" : "LIVE")}";
    }

    private static void RefreshLocal()
    {
        _accounts = SteamLocator.ListAccounts(_steamPath);
        var audit = new AuditEngine(_cfg);
        _buckets = audit.ListLocal(_steamPath, owned: _cfg.GetOwnedSet());
        _registry = SctRegistry.Load();
    }

    private static async Task<SteamSession?> ConnectSessionAsync(bool quiet = false)
    {
        var session = new SteamSession();
        session.Event += msg =>
        {
            if (!quiet) AnsiConsole.MarkupLineInterpolated($"[dim]{Markup.Escape(msg)}[/]");
        };

        var mode = (Environment.GetEnvironmentVariable("SCT_AUTH_MODE") ?? "").ToLowerInvariant();
        var user = Environment.GetEnvironmentVariable("SCT_USER");
        var pass = Environment.GetEnvironmentVariable("SCT_PASS");

        var ok = mode switch
        {
            "qr" => await session.ConnectAsync(AuthMode.Qr),
            _ when user is { Length: > 0 } && pass is { Length: > 0 }
                => await session.ConnectAsync(AuthMode.Credentials, user, pass),
            _ => await session.ConnectAsync(AuthMode.Anonymous),
        };

        if (!ok)
        {
            await session.DisposeAsync();
            AnsiConsole.MarkupLine("[red]Steam logon failed[/] - try the Logon screen or set SCT_USER/SCT_PASS / SCT_AUTH_MODE=qr.");
            return null;
        }
        return session;
    }

    // ---------- Buckets ----------

    private static void BucketsScreen()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title(TuiFx.Title($"{Ui.Icon("folder")} Local buckets (userdata)"))
            .AddColumn(new TableColumn("App").Centered())
            .AddColumn(new TableColumn("Era"))
            .AddColumn(new TableColumn("Files").Centered())
            .AddColumn(new TableColumn("Size").Centered())
            .AddColumn(new TableColumn("Note").LeftAligned());

        foreach (var b in _buckets.OrderBy(b => b.AppId))
        {
            table.AddRow(
                b.AppId.ToString(),
                EraStyle(b.Era),
                b.Files.Count.ToString(),
                HumanSize(b.TotalBytes),
                Markup.Escape(b.Note));
        }
        AnsiConsole.Write(table);
        Footer("enter/0 to go back");
    }

    private static string EraStyle(Era e) => e switch
    {
        Era.AnonymousDump760 => "[red3]760-dump[/]",
        Era.GreenLumaRealAppId => "[orange1]GL-real[/]",
        Era.Owned => "[green]owned[/]",
        Era.Emulated => "[purple]emu[/]",
        Era.Client => "[grey]client[/]",
        _ => "[grey]unk[/]",
    };

    // ---------- Remote ----------

    private static async Task RemoteScreenAsync()
    {
        await using var session = await ConnectSessionAsync();
        if (session is null) return;
        var rpc = new CloudRpcClient(session);

        while (true)
        {
            var appId = AnsiConsole.Ask<uint>("Appid to list (0 = back):");
            if (appId == 0) break;

            try
            {
                var files = await AnsiConsole.Status()
                    .StartAsync($"Listing {appId}...", _ => rpc.EnumerateAsync(appId));
                var table = new Table().Border(TableBorder.Rounded).Title(TuiFx.Title($"Remote {appId}"))
                    .AddColumn("File").AddColumn("Size").AddColumn("Time").AddColumn("SHA");
                foreach (var f in files)
                    table.AddRow(Markup.Escape(f.FileName), HumanSize(f.FileSize), f.Timestamp.ToString(), ShortSha(f.FileSha));
                AnsiConsole.Write(table);

                try
                {
                    var quota = await rpc.QuotaAsync(appId);
                    AnsiConsole.MarkupLine($"[dim]quota: {HumanSize((long)quota.ExistingBytes)}/{HumanSize((long)quota.MaxBytes)}, {quota.ExistingFiles}/{quota.MaxFiles} files[/]");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[dim]quota: {Markup.Escape(ex.Message)}[/]");
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            }
        }
    }

    // ---------- Ferry ----------

    private static async Task FerryScreenAsync()
    {
        await using var session = await ConnectSessionAsync();
        if (session is null) return;
        var rpc = new CloudRpcClient(session);

        while (true)
        {
            var choice = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title(TuiFx.Title("Ferry (AppID 480 / Spacewar)"))
                .AddChoices("List parked", "Upload local file", "Download parked", "Back"));
            switch (choice)
            {
                case "List parked":
                {
                    var files = await AnsiConsole.Status().StartAsync("Fetching...", _ => rpc.EnumerateAsync(Ferry.SpacewarApp));
                    var tbl = new Table().Border(TableBorder.Rounded).Title(TuiFx.Title("Parked in 480"))
                        .AddColumn("Name").AddColumn("Size").AddColumn("Time").AddColumn("Origin");
                    foreach (var f in files)
                    {
                        var (src, orig) = Ferry.UnparkName(f.FileName);
                        var origin = src != 0 && orig != f.FileName ? $"from {src} ({orig})" : "-";
                        tbl.AddRow(Markup.Escape(f.FileName), HumanSize(f.FileSize), f.Timestamp.ToString(), Markup.Escape(origin));
                    }
                    AnsiConsole.Write(tbl);
                    break;
                }
                case "Upload local file":
                {
                    var src = AnsiConsole.Ask<string>("local file path:");
                    if (!File.Exists(src)) { AnsiConsole.MarkupLine("[red]missing[/]"); break; }
                    var data = await File.ReadAllBytesAsync(src);
                    var name = AnsiConsole.Ask("name in bucket:", Ferry.ParkName(0, Path.GetFileName(src)));
                    var res = await AnsiConsole.Status().StartAsync("Uploading...", _ => rpc.UploadAsync(Ferry.SpacewarApp, name, data));
                    AnsiConsole.MarkupLine(res == SteamKit2.EResult.OK ? $"[green]uploaded {name} ({data.Length}b)[/]" : $"[red]{res}[/]");
                    break;
                }
                case "Download parked":
                {
                    var name = AnsiConsole.Ask<string>("parked name:");
                    var data = await AnsiConsole.Status().StartAsync("Downloading...", _ => rpc.DownloadAsync(Ferry.SpacewarApp, name));
                    if (data is null) { AnsiConsole.MarkupLine("[red]failed[/]"); break; }
                    var outFile = AnsiConsole.Ask("out file:", name.Replace('/', '_'));
                    await File.WriteAllBytesAsync(outFile, data);
                    AnsiConsole.MarkupLine($"[green]saved {data.Length}b -> {outFile}[/]");
                    break;
                }
                default: return;
            }
        }
    }

    // ---------- Park (smart, barcode) ----------

    private static async Task ParkScreenAsync()
    {
        var candidates = _buckets
            .Where(b => b.Files.Count > 0)
            .OrderBy(b => b.AppId)
            .ToList();
        if (candidates.Count == 0) { AnsiConsole.MarkupLine("[dim]no local buckets to park[/]"); return; }

        var labels = candidates.Select(b => $"{b.AppId}  ({b.Files.Count} files, {HumanSize(b.TotalBytes)})").ToList();
        var pick = new SelectionPrompt<string>()
                .Title(TuiFx.Title($"{Ui.Icon("park")} Pick a bucket to park (upload + barcode-tag)"))
            .AddChoices(labels);
        pick.AddChoice("Back");
        var choice = AnsiConsole.Prompt(pick);
        if (choice == "Back") return;

        var bucket = candidates[labels.IndexOf(choice)];
        await ParkBucketAsync(bucket.AppId);
    }

    private static async Task ParkBucketAsync(uint gameAppId)
    {
        var files = _buckets.Where(b => b.AppId == gameAppId).SelectMany(b => b.Files).ToList();
        if (files.Count == 0) { AnsiConsole.MarkupLine("[dim]nothing to park[/]"); return; }

        var active = SteamLocator.GetActiveAccount(_steamPath);
        var clientLane = active is not null && SteamLocator.IsRunning();
        uint uid;
        if (clientLane)
        {
            uid = active!.AccountId;
            AnsiConsole.MarkupLine($"[dim]client lane: riding the signed-in Steam session ({uid}) - no SCT logon needed[/]");
        }
        else
        {
            await using var session = await ConnectSessionAsync();
            if (session is null) return;
            uid = Acct3(session);
        }

        var spread = files.Count > 1 ? Math.Max(1, AnsiConsole.Ask("spread across N slots (1 = all in one):", 3)) : 1;
        var stealth = files.Count > 0 && AnsiConsole.Confirm("hashed stealth names?", false);

        // container universe + consent for owned-game buckets (never auto-picked)
        var containers = PoolDiscoverer.Discover(_steamPath, proxies: _cfg.CloudProxies);
        var ownedContainers = containers.Count(c => c.Kind == ContainerKind.Owned);
        var allowOwned = ownedContainers > 0 &&
            AnsiConsole.Confirm($"{ownedContainers} owned game bucket(s) detected - include them? (OPT-IN consent)", false);
        if (ownedContainers > 0 && !allowOwned)
            AnsiConsole.MarkupLine("[dim]owned buckets excluded - they are never picked without consent[/]");

        HashSet<string>? postureFilter = null;
        var postures = containers.Select(c => c.Posture).Where(p => p.Length > 0).Distinct().OrderBy(p => p).ToList();
        var postureLabels = new List<string> { "any (no filter)" };
        postureLabels.AddRange(postures.Select(p => $"{(p == "real" ? "real - Valve UFS" : p == "provider" ? "provider - CloudRedirect folder" : p == "redirected" ? "redirected - activation tool (OST/SLS/GL)" : p)}".ToLowerInvariant()));
        var posturePick = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title(TuiFx.Title("Posture filter (ranking: VerifiedWritable real > AutoClouded real > probe-candidate > provider/redirected)"))
            .AddChoices(postureLabels));
        if (posturePick != "any (no filter)")
            postureFilter = [posturePick.Split(" - ")[0]];

        var engine = new ParkingEngine(_cfg.GetOwnedSet(), _registry.Slots, poolProbes: _registry.PoolProbes);
        var decisions = engine.Plan(gameAppId,
            files.Select(f => new ParkFile(f.FileName, f.FileSize)).ToList(), stealth, spread, 1,
            containers: containers, allowOwned: allowOwned, postureFilter: postureFilter);

        var plans = new List<(Bucket Origin, CloudFileEntry F, ParkingDecision D, byte[] Tagged)>();
        var di = 0;
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var f in files)
        {
            var d = decisions[di++];
            if (!d.Ok) { AnsiConsole.MarkupLine($"[yellow]  {f.FileName}: refused - {d.Reason}[/]"); continue; }
            var filePath = FindBucketFile(gameAppId, f.FileName);
            if (filePath is { } path && File.Exists(path))
            {
                var original = await File.ReadAllBytesAsync(path);
                var trailer = Barcode.PackTrailer(gameAppId.ToString(), uid.ToString(), today);
                plans.Add((new Bucket(0, Era.Unknown, "", [f]), f, d, original.Concat(trailer).ToArray()));
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]  {f.FileName}: no local copy on disk - skipped[/]");
            }
        }

        if (plans.Count == 0) { AnsiConsole.MarkupLine("[yellow]nothing parkable[/]"); return; }

        foreach (var p in plans)
            AnsiConsole.MarkupLine($"  [cyan]{p.F.FileName}[/] ({p.F.FileSize}b) -> [aqua]{p.D.StoredName}[/] @ [bold]{p.D.StorageAppId}[/]  [dim]{p.D.Reason}[/]");

        if (_cfg.DryRun)
        {
            AnsiConsole.MarkupLine("[yellow]DRY-RUN[/] - enable LIVE mode in Settings to park");
            return;
        }
        if (!AnsiConsole.Confirm("Park now?")) return;

        var ok = 0;
        if (clientLane)
        {
            ok = await ClientLaneParkTuiAsync(engine, plans, uid, today);
        }
        else
        {
            await using var session = await ConnectSessionAsync();
            if (session is null) return;
            var rpc = new CloudRpcClient(session);
            foreach (var p in plans)
            {
                var res = await AnsiConsole.Status().StartAsync($"Parking {p.D.StoredName}...", _ => rpc.UploadAsync(p.D.StorageAppId!.Value, p.D.StoredName!, p.Tagged));
                if (res == SteamKit2.EResult.OK)
                {
                    var payload = $"{gameAppId}{Barcode.Sep}{uid}{Barcode.Sep}{today:ddMMyyyy}";
                    _registry.Upsert(GameSlot.New(gameAppId, p.D.StorageAppId!.Value, p.D.StoredName!, p.F.FileName, p.Tagged.Length, payload)
                        .WithPosture("real"));
                    ok++;
                    AnsiConsole.MarkupLine($"  [green]{Ui.Icon("check")} {p.D.StoredName} @ {p.D.StorageAppId} ({res})[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"  [red]{p.D.StoredName}: {res}[/]");
                }
            }
        }
        _registry.Save();
        AnsiConsole.MarkupLine($"{ok}/{plans.Count} parked; registry saved");
    }

    private static async Task<int> ClientLaneParkTuiAsync(ParkingEngine engine,
        List<(Bucket Origin, CloudFileEntry F, ParkingDecision D, byte[] Tagged)> plans, uint uid, DateOnly today)
    {
        var injector = new LocalInjectEngine();
        var userDataTuiRoot = Path.Combine(_steamPath, "userdata", uid.ToString());
        var bySlot = plans.GroupBy(p => p.D.StorageAppId!.Value).ToList();
        var ok = 0;

        foreach (var group in bySlot)
        {
            var slot = group.Key;
            var slotDir = Path.Combine(userDataTuiRoot, slot.ToString(), "remote");
            Directory.CreateDirectory(slotDir);
            foreach (var p in group)
                await File.WriteAllBytesAsync(Path.Combine(slotDir, p.D.StoredName!), p.Tagged);
            injector.RegenerateVdf(Path.Combine(userDataTuiRoot, slot.ToString()));

            AnsiConsole.MarkupLine($"[dim]  [{slot}] staged {group.Count()} file(s); forcing client sync via console...[/]");
            var verdict = await AnsiConsole.Status()
                .StartAsync($"cloud_sync_up {slot} via Steam console...",
                    _ => PushSyncTuiAsync(slot, 25));

            switch (verdict.Verdict)
            {
                case CloudVerdict.Success:
                    _registry.PoolProbes[slot] = "VerifiedWritable";
                    var posture = SteamLocator.SyncPosture(_steamPath, slot);
                    AnsiConsole.MarkupLine($"  [green]{Ui.Icon("check")} [{slot}] synced - {group.Count()} file(s) parked ({(posture == "real" ? "real cloud" : posture == "provider" ? "CR provider" : "redirected")})[/]");
                    foreach (var p in group)
                    {
                        var payload = $"{gameAppIdOf(p)}{Barcode.Sep}{uid}{Barcode.Sep}{today:ddMMyyyy}";
                        _registry.Upsert(GameSlot.New(gameAppIdOf(p), slot, p.D.StoredName!, p.F.FileName, p.Tagged.Length, payload)
                            .WithPosture(posture));
                        ok++;
                    }
                    break;
                case CloudVerdict.Denied:
                    _registry.PoolProbes[slot] = "Denied";
                    foreach (var p in group)
                    {
                        var staged = Path.Combine(slotDir, p.D.StoredName!);
                        if (File.Exists(staged)) File.Delete(staged);
                    }
                    injector.RegenerateVdf(Path.Combine(userDataTuiRoot, slot.ToString()));
                    AnsiConsole.MarkupLine($"  [red]  [{slot}] Denied by server - slot excluded, staged files removed[/]");
                    break;
                default:
                    AnsiConsole.MarkupLine($"  [yellow]  [{slot}] no verdict yet - files staged; client syncs on its own or use the RPC lane (Logon + park) for a real upload[/]");
                    break;
            }
            _registry.Save();
        }
        return ok;
    }

    private static uint gameAppIdOf((Bucket Origin, CloudFileEntry F, ParkingDecision D, byte[] Tagged) p)
    {
        var start = Math.Max(0, p.Tagged.Length - Barcode.TailWindowBytes);
        if (Barcode.TryDecodeTail(p.Tagged.AsSpan(start), out var payload, out _))
        {
            var (game, _, _) = Barcode.Parse(payload);
            return game;
        }
        return 0;
    }

    /// <summary>
    /// Client-lane sync pressure: wait for the RUNNING Steam client's own cloud
    /// tick. The Steam Console is skipped (unreliable on newer client builds).
    /// </summary>
    private static async Task<(CloudVerdict Verdict, string? MatchLine, bool ConsoleAvailable)> PushSyncTuiAsync(uint appId, int waitSec, bool down = false)
    {
        if (!SteamLocator.IsRunning()) throw new InvalidOperationException("Steam is not running");
        var result = await new CloudLogWatcher(_steamPath, appId).WaitForVerdictAsync(TimeSpan.FromSeconds(waitSec));
        return (result.Verdict, result.MatchLine, false);
    }

    private static uint Acct3(SteamSession s) => (uint)(s.SteamId?.AccountID ?? 0);

    private static string? FindBucketFile(uint appId, string fileName)
    {
        var userdata = Path.Combine(_steamPath, "userdata");
        if (!Directory.Exists(userdata)) return null;
        foreach (var account in Directory.EnumerateDirectories(userdata))
        {
            var path = Path.Combine(account, appId.ToString(), fileName);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static async Task<RemoteBucketSnapshot?> PoolRemoteSnapshotAsync(CloudRpcClient rpc, uint appId)
    {
        try
        {
            var files = await rpc.EnumerateAsync(appId);
            Quota? quota = null;
            try { quota = await rpc.QuotaAsync(appId); } catch { }
            return new RemoteBucketSnapshot(appId, files, quota);
        }
        catch { return null; }
    }

    // ---------- Registry & pool ----------

    private static async Task RegistryScreenAsync()
    {
        while (true)
        {
            var sub = AnsiConsole.Prompt(new SelectionPrompt<string>()
                .Title(TuiFx.Title($"{Ui.Icon("registry")} Registry & Parking Pool"))
                .AddChoices("Show registry slots", "Rebuild registry (scan barcodes)", "Show pool", "Refresh pool metadata", "Show discovered containers", "Probe slots (private, via Steam client)", "Back"));

            switch (sub)
            {
                case "Show registry slots":
                {
                    if (_registry.Slots.Count == 0) { AnsiConsole.MarkupLine("[dim]empty registry - run a rebuild after parking[/]"); break; }
                    var tbl = new Table().Border(TableBorder.Rounded).Title(TuiFx.Title("registry.json"))
                        .AddColumn("Game").AddColumn("Storage").AddColumn("Stored name").AddColumn("Original").AddColumn("Size").AddColumn("Status").AddColumn("Posture");
                    foreach (var s in _registry.Slots.OrderBy(s => s.StorageAppId).ThenBy(s => s.StoredName))
                        tbl.AddRow(s.GameAppId.ToString(), s.StorageAppId.ToString(), Markup.Escape(s.StoredName), Markup.Escape(s.OriginalName), HumanSize(s.Size), s.Status, s.Posture ?? "-");
                    AnsiConsole.Write(tbl);
                    AnsiConsole.MarkupLine($"[dim]{SctRegistry.DefaultPath()}[/]");
                    break;
                }
                case "Rebuild registry (scan barcodes)":
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _registry = PoolScanner.RebuildRegistry(_steamPath);
                    sw.Stop();
                    _registry.Save();
                    AnsiConsole.MarkupLine($"[green]{_registry.Slots.Count} slot(s) rebuilt in {sw.ElapsedMilliseconds}ms[/] - auto flow: the barcode tail-scan found them");
                    break;
                }
                case "Show pool":
                {
                    var tbl = new Table().Border(TableBorder.Rounded).Title(TuiFx.Title("Parking pool (owned games NEVER selected)"))
                        .AddColumn("Tier").AddColumn("App").AddColumn("Name").AddColumn("Free").AddColumn("Year").AddColumn("State").AddColumn("Probe").AddColumn("Note");
                    foreach (var p in PoolDb.DefaultPool.OrderBy(p => p.Tier).ThenBy(p => p.AppId))
                    {
                        var probe = _registry.PoolProbes.TryGetValue(p.AppId, out var ps) ? ps : "-";
                        tbl.AddRow(p.Tier.ToString(), p.AppId.ToString(), Markup.Escape(p.Name), p.IsFree ? "yes" : "no", p.ReleaseYear.ToString(), p.State.ToString(), probe, Markup.Escape(p.Note));
                    }
                    AnsiConsole.Write(tbl);
                    break;
                }
                case "Refresh pool metadata":
                {
                    await AnsiConsole.Status().StartAsync("Querying store API (anonymous, read-only)...", async _ =>
                    {
                        await StoreApi.RefreshPoolAsync(PoolDb.DefaultPool.Select(p => p.AppId));
                    });
                    AnsiConsole.MarkupLine("[green]pool metadata refreshed[/]");
                    break;
                }
                case "Show discovered containers":
                {
                    _registry.SyncDiscovered(_steamPath,
                        appId => CloudLogWatcher.WasEverAutoClouded(_steamPath, appId));
                    var tbl = new Table().Border(TableBorder.Rounded)
                        .Title(TuiFx.Title("Discovered containers (smart appid switching universe)"))
                        .AddColumn("App").AddColumn("Name").AddColumn("Kind").AddColumn("Source").AddColumn("Posture").AddColumn("AutoClouded").AddColumn("Note");
                    foreach (var c in _registry.Discovered)
                        tbl.AddRow(c.AppId.ToString(), Markup.Escape(c.Name ?? "-"), c.Kind.ToString(), c.Source.ToString(), c.Posture, c.AutoClouded ? "yes" : "-", Markup.Escape(c.Note ?? ""));
                    AnsiConsole.Write(tbl);
                    var real = _registry.Discovered.Count(c => c.IsRealCandidate);
                    var activation = _registry.Discovered.Count(c => c.Kind == ContainerKind.Activation);
                    AnsiConsole.MarkupLine($"[dim]{real} real/provider container(s), {activation} activation-tool container(s) - saved to the registry[/]");
                    break;
                }
                case "Probe slots (private, via Steam client)":
                {
                    var active = SteamLocator.GetActiveAccount(_steamPath);
                    if (active is null || !SteamLocator.IsRunning())
                    {
                        AnsiConsole.MarkupLine("[red]need Steam running + signed in - the probe rides the client session[/]");
                        break;
                    }
                    var waitSec = AnsiConsole.Ask("wait seconds per slot:", 20);
                    var injector = new LocalInjectEngine();
                    var candidates = PoolDb.Usable().OrderBy(p => p.Tier).ThenBy(p => p.AppId).ToList();
                    AnsiConsole.MarkupLine($"[dim]probing {candidates.Count} candidate slot(s) as {active.AccountId} (one tiny private file each)...[/]");
                    foreach (var slot in candidates)
                    {
                        var userAppDir = Path.Combine(_steamPath, "userdata", active.AccountId.ToString(), slot.AppId.ToString());
                        var probePath = Path.Combine(userAppDir, "remote", "sctprobe.bin");
                        if (!File.Exists(probePath))
                        {
                            var payload = $"{slot.AppId}{Barcode.Sep}probe{Barcode.Sep}{DateTime.Now:ddMMyyyy}";
                            var probeBytes = new byte[64];
                            Random.Shared.NextBytes(probeBytes);
                            Directory.CreateDirectory(Path.Combine(userAppDir, "remote"));
                            File.WriteAllBytes(probePath, probeBytes.Concat(Barcode.PackTrailer(payload)).ToArray());
                        }
                        injector.RegenerateVdf(userAppDir);

                        var verdict = await AnsiConsole.Status()
                            .StartAsync($"Probing {slot.AppId} ({slot.Name}) via console cloud_sync_up...", _ =>
                                PushSyncTuiAsync(slot.AppId, waitSec));

                        switch (verdict.Verdict)
                        {
                            case CloudVerdict.Success:
                                _registry.PoolProbes[slot.AppId] = "VerifiedWritable";
                                AnsiConsole.MarkupLine($"  [green]{Ui.Icon("check")} {slot.AppId} {Markup.Escape(slot.Name)}: writable - probe removed[/]");
                                File.Delete(probePath);
                                injector.RegenerateVdf(userAppDir);
                                break;
                            case CloudVerdict.Denied:
                                _registry.PoolProbes[slot.AppId] = "Denied";
                                AnsiConsole.MarkupLine($"  [red]  {slot.AppId} {Markup.Escape(slot.Name)}: Denied - slot excluded[/]");
                                File.Delete(probePath);
                                injector.RegenerateVdf(userAppDir);
                                break;
                            default:
                                AnsiConsole.MarkupLine($"  [yellow]  {slot.AppId} {Markup.Escape(slot.Name)}: no verdict yet[/]");
                                break;
                        }
                        _registry.Save();
                    }
                    AnsiConsole.MarkupLine("[green]probe done - verdicts saved to the registry[/]");
                    break;
                }
                default: return;
            }
        }
    }

    // ---------- Wipe ----------

    private static async Task WipeScreenAsync()
    {
        var guarded = _cfg.GuardedAppIds;
        var targets = _buckets
            .Where(b => !guarded.Contains(b.AppId))
            .SelectMany(b => b.Files.Select(f => (Bucket: b, f)))
            .ToList();
        if (targets.Count == 0)
        {
            AnsiConsole.MarkupLine(guarded.Count > 0
                ? "[dim]no wipable buckets left (guarded buckets are filtered)[/]"
                : "[dim]no locally cached bucket files[/]");
            return;
        }
        if (guarded.Count > 0)
            AnsiConsole.MarkupLine($"[dim]{_buckets.Count(b => guarded.Contains(b.AppId))} guarded bucket(s) hidden - guards are never-touch[/]");

        var labels = targets.Select(t => $"{t.Bucket.AppId}/{t.f.FileName} ({HumanSize(t.f.FileSize)})").ToList();
        var pick = AnsiConsole.Prompt(new SelectionPrompt<string>().Title(TuiFx.Title("Choose file to wipe")).AddChoices(labels));
        var chosen = targets[labels.IndexOf(pick)];

        if (guarded.Contains(chosen.Bucket.AppId))
        {
            AnsiConsole.MarkupLine($"[red]REFUSED[/] {chosen.Bucket.AppId} is guarded (never-touch list)");
            return;
        }

        await using var session = await ConnectSessionAsync();
        if (session is null) return;
        var rpc = new CloudRpcClient(session);
        var engine = new WipeEngine(_cfg);
        engine.Log += m => AnsiConsole.MarkupLine($"[dim]{Markup.Escape(m)}[/]");

        var blank = AnsiConsole.Confirm("Blank instead of delete?");
        AnsiConsole.MarkupLine(_cfg.DryRun ? "[yellow]DRY-RUN mode[/] - Settings to go live" : "[green]LIVE mode[/]");
        if (AnsiConsole.Confirm("Run?"))
        {
            var outcome = await engine.WipeAsync(rpc, chosen.Bucket.AppId, chosen.f.FileName, blank);
            _cfg.Save(); // persist the auto-guard added after a successful delete
            AnsiConsole.MarkupLine(outcome.Success
                ? $"[green]OK[/] {outcome.Result}"
                : $"[red]FAIL[/] {outcome.Result}");
        }
    }

    // ---------- Guards ----------

    private static void GuardsScreen()
    {
        var sub = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title(TuiFx.Title("Guards"))
            .AddChoices("Show", "Add appid", "Remove appid", "Back"));
        switch (sub)
        {
            case "Show":
                foreach (var g in _cfg.GuardedAppIds.OrderBy(x => x)) AnsiConsole.WriteLine(g.ToString());
                break;
            case "Add appid":
                _cfg.GuardedAppIds.Add(AnsiConsole.Ask<uint>("appid:"));
                _cfg.Save(ConfigPath);
                break;
            case "Remove appid":
            {
                var id = AnsiConsole.Ask<uint>("appid:");
                _cfg.GuardedAppIds.Remove(id);
                _cfg.Save(ConfigPath);
                break;
            }
        }
    }

    // ---------- Logon (QR / credentials) ----------

    private static async Task LogonScreenAsync()
    {
        var mode = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title(TuiFx.Title($"{Ui.Icon("qr")} Steam session logon"))
            .AddChoices("QR code (Steam mobile app)", "Credentials (SCT_USER / SCT_PASS)", "Anonymous (read-limited)", "Back"));

        var session = new SteamSession();
        switch (mode)
        {
            case "QR code (Steam mobile app)":
            {
                var urlTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.ChallengeUrlChanged += u => urlTcs.TrySetResult(u);
                session.Event += msg => AnsiConsole.MarkupLineInterpolated($"[dim]{Markup.Escape(msg)}[/]");

                var connectTask = Task.Run(() => session.ConnectAsync(AuthMode.Qr));
                string url;
                try { url = await urlTcs.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
                catch (TimeoutException)
                {
                    AnsiConsole.MarkupLine("[red]no QR challenge received[/]");
                    await session.DisposeAsync();
                    return;
                }

                AnsiConsole.MarkupLine("[bold aqua]Scan this QR with the Steam mobile app:[/]");
                foreach (var line in QrRenderer.Render(url))
                    Console.WriteLine(line);
                AnsiConsole.MarkupLine($"[dim]or open: {Markup.Escape(url)}[/]");

                var ok = await AnsiConsole.Status()
                    .StartAsync("Waiting for the scan...", _ => connectTask);
                if (!ok)
                {
                    AnsiConsole.MarkupLine("[red]QR logon failed[/]");
                    await session.DisposeAsync();
                    return;
                }
                AnsiConsole.MarkupLine($"[green]{Ui.Icon("check")} logged on as {session.SteamId?.ConvertToUInt64()}[/]");
                break;
            }
            case "Credentials (SCT_USER / SCT_PASS)":
            {
                var user = AnsiConsole.Ask("username:", Environment.GetEnvironmentVariable("SCT_USER") ?? "");
                var pass = AnsiConsole.Prompt(new TextPrompt<string>("password:").Secret());
                session.Event += msg => AnsiConsole.MarkupLineInterpolated($"[dim]{Markup.Escape(msg)}[/]");
                var ok = await AnsiConsole.Status().StartAsync("Authenticating...", _ => session.ConnectAsync(AuthMode.Credentials, user, pass));
                if (!ok) { AnsiConsole.MarkupLine("[red]logon failed[/]"); await session.DisposeAsync(); return; }
                AnsiConsole.MarkupLine($"[green]{Ui.Icon("check")} logged on[/]");
                break;
            }
            case "Anonymous (read-limited)":
            {
                session.Event += msg => AnsiConsole.MarkupLineInterpolated($"[dim]{Markup.Escape(msg)}[/]");
                var ok = await AnsiConsole.Status().StartAsync("Connecting...", _ => session.ConnectAsync(AuthMode.Anonymous));
                if (!ok) { AnsiConsole.MarkupLine("[red]logon failed[/]"); await session.DisposeAsync(); return; }
                AnsiConsole.MarkupLine($"[green]{Ui.Icon("check")} anonymous session[/]");
                break;
            }
            default: return;
        }

        await session.DisposeAsync();
        AnsiConsole.MarkupLine("[dim]session discarded (session cookie remains useful via SCT_COOKIE for the web lane)[/]");
    }

    // ---------- Settings ----------

    private static void SettingsScreen()
    {
        var sub = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title(TuiFx.Title("Settings"))
            .AddChoices("Toggle dry-run/live", "Steam path", "Owned appids", "Proxy appids", "Back"));
        switch (sub)
        {
            case "Toggle dry-run/live":
                _cfg.DryRun = !_cfg.DryRun;
                _cfg.Save(ConfigPath);
                AnsiConsole.MarkupLine($"dry-run: {( _cfg.DryRun ? "[green]on[/]" : "[red]off[/]")}");
                break;
            case "Steam path":
            {
                var p = AnsiConsole.Ask("path:", _cfg.SteamPathOverride ?? _steamPath);
                _cfg.SteamPathOverride = p;
                _cfg.Save(ConfigPath);
                RefreshLocal();
                break;
            }
            case "Owned appids":
            {
                var joined = string.Join(", ", _cfg.KnownOwnedAppIds.OrderBy(x => x));
                var input = AnsiConsole.Ask("comma-separated appids:", joined);
                try
                {
                    _cfg.KnownOwnedAppIds =
                        [.. input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(uint.Parse)];
                }
                catch { AnsiConsole.MarkupLine("[red]unparseable - unchanged[/]"); break; }
                _cfg.Save(ConfigPath);
                RefreshLocal();
                break;
            }
            case "Proxy appids":
            {
                // appid proxy map: "gameAppId=proxyAppId" pairs, comma-separated;
                // game 0 = default proxy bucket for every unowned game (docs/APPID-PROXY.md)
                var joined = string.Join(", ", _cfg.CloudProxies.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));
                var input = AnsiConsole.Ask("game=proxy pairs (0 = default proxy for all unowned):", joined);
                try
                {
                    var next = new Dictionary<uint, uint>();
                    foreach (var pair in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var bits = pair.Split('=', StringSplitOptions.TrimEntries);
                        if (bits.Length != 2 || !uint.TryParse(bits[0], out var game) || !uint.TryParse(bits[1], out var proxy))
                            throw new FormatException(pair);
                        next[game] = proxy;
                    }
                    _cfg.CloudProxies = next;
                }
                catch { AnsiConsole.MarkupLine("[red]unparseable - unchanged[/]"); break; }
                _cfg.Save(ConfigPath);
                AnsiConsole.MarkupLine($"[green]{_cfg.CloudProxies.Count} proxy mapping(s) saved[/]");
                break;
            }
        }
    }

    // ---------- helpers ----------

    private static void Footer(string hint)
    {
        TuiFx.Rule(hint);
    }

    private static void WriteCrash(string path, string kind, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {kind}:\n{ex}\n\n");
        }
        catch
        {
            // logging must never crash anything
        }
    }

    private static string HumanSize(long b) =>
        b switch
        {
            >= 1024L * 1024 * 1024 => $"{b / 1024d / 1024d / 1024d:F2} GiB",
            >= 1024L * 1024 => $"{b / 1024d / 1024d:F1} MiB",
            >= 1024 => $"{b / 1024d:F1} KiB",
            _ => $"{b} B",
        };

    private static string ShortSha(string? sha) => sha is { Length: > 8 } ? sha[..8] : (sha ?? "-");
}

/// <summary>Terminal glyphs: Unicode by default, ASCII with SCT_TUI_ASCII=1, Nerd-Font only when the font really has the glyphs.</summary>
public static class Ui
{
    private static readonly bool Ascii = Environment.GetEnvironmentVariable("SCT_TUI_ASCII") == "1";
    private static readonly bool Nerd = !Ascii && Environment.GetEnvironmentVariable("SCT_TUI_NERD") == "1";

    /// <summary>Returns the best glyph (Nerd Font glyph, else plain Unicode, else ASCII).</summary>
    public static string Icon(string name)
    {
        if (Nerd) return NerdGlyph(name);
        if (Ascii) return AsciiGlyph(name);
        return UniGlyph(name);
    }

    public static string Style(string body) => body; // future theming hook

    private static string NerdGlyph(string n) => n switch
    {
        // Font Awesome 5 codepoints (stable in Nerd Fonts)
        "folder" => "\uF07B",   // folder
        "cloud" => "\uF0C2",    // cloud
        "ferry" => "\uF21A",    // ship
        "park" => "\uF1F9",     // parking
        "wipe" => "\uF1F8",     // trash
        "registry" => "\uF03A", // list
        "shield" => "\uF3ED",   // shield-halved
        "gear" => "\uF013",     // gear
        "qr" => "\uF029",       // qrcode
        "check" => "\uF058",    // circle-check
        "x" => "\uF00D",        // xmark
        _ => "\uF0C9",          // bars
    };

    private static string UniGlyph(string n) => n switch
    {
        "folder" => "▣",
        "cloud" => "☁",
        "ferry" => "⛴",
        "park" => "🅿",
        "wipe" => "✂",
        "registry" => "▤",
        "shield" => "◆",
        "gear" => "⚙",
        "qr" => "▦",
        "check" => "✔",
        "x" => "✘",
        _ => "•",
    };

    private static string AsciiGlyph(string n) => n switch
    {
        "folder" => "[D]",
        "cloud" => "~C",
        "ferry" => "~F",
        "park" => "[P]",
        "wipe" => "[X]",
        "registry" => "[R]",
        "shield" => "[L]",
        "gear" => "[S]",
        "qr" => "[Q]",
        "check" => "OK",
        "x" => "[!]",
        _ => "[*]",
    };
}

/// <summary>Renders a QR challenge into terminal lines: Unicode half-block mode, ASCII ## fallback.</summary>
public static class QrRenderer
{
    public static List<string> Render(string text, bool ascii = false)
    {
        using var gen = new QRCoder.QRCodeGenerator();
        var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix; // List<BitArray>
        var size = rows.Count;
        var quiet = 2;
        var lines = new List<string>();

        bool Module(int x, int y) => x >= 0 && y >= 0 && x < size && y < size && rows[y][x];

        if (ascii)
        {
            for (var y = 0; y < size + quiet * 2; y++)
            {
                var sb = new System.Text.StringBuilder();
                for (var x = 0; x < size + quiet * 2; x++)
                    sb.Append(Module(x - quiet, y - quiet) ? "██" : "  ");
                lines.Add(sb.ToString());
            }
            return lines;
        }

        // Unicode half-blocks: two module rows per line
        for (var y = -quiet; y < size + quiet; y += 2)
        {
            var sb = new System.Text.StringBuilder();
            for (var x = -quiet; x < size + quiet; x++)
            {
                var top = Module(x, y);
                var bottom = Module(x, y + 1);
                sb.Append((top, bottom) switch
                {
                    (true, true) => "▀",
                    (true, false) => "▀",
                    (false, true) => "▄",
                    _ => " ",
                });
            }
            lines.Add(sb.ToString());
        }
        return lines;
    }
}