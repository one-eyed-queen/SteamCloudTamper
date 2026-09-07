using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Pool;
using SteamCloudTamper.Core.Steam;
using SteamCloudTamper.Engines;

namespace SteamCloudTamper.Cli;

public static class Program
{
    // config lives at SCT_CONFIG env var, else the legacy CWD steamcloudtamper.json
    private static readonly string ConfigPath = AppConfig.ResolveDefaultPath();

    public static async Task<int> Main(string[] args)
    {
        AnsiTerminal.Enable();

        Branding.PrintToConsole();

        var config = AppConfig.Load(ConfigPath);
        var cmd = args.Length == 0 ? "help" : args[0].ToLowerInvariant();

        try
        {
            return cmd switch
            {
                "detect" => Detect(config),
                "scan" => Scan(config),
                "remote-list" => await RemoteList(args, config),
                "probe" => await Probe(args, config),
                "wipe" => await Wipe(args, config),
                "wipe-all" => await WipeAll(args, config),
                "guards" => Guards(args, config),
                "inject" => InjectLocal(args, config),
                "lock" => LockBucket(args, config),
                "relocate" => RelocateBucket(args, config),
                "web" => await WebCloud(args, config),
                "ferry" => await FerryCmd(args, config),
                "pool" => await PoolCmd(args, config),
                "park" => await ParkCmd(args, config),
                "unpark" => await UnparkCmd(args, config),
                "restore" => await RestoreCmd(args, config),
                "check" => await CheckCmd(args, config),
                "client" => await ClientCmd(args, config),
                "provider" => ProviderCmd(args, config),
                "proxy" => await ProxyCmd(args, config),
                "rebuild" => RebuildCmd(args, config),
                "barcode" => BarcodeCmd(args, config),
                "doctor" => await DoctorCmd(args, config),
                _ => Help()
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 1;
        }
    }

    private static string ResolveSteam(AppConfig config)
        => config.SteamPathOverride ?? SteamLocator.DetectInstallPath()
           ?? throw new InvalidOperationException("Steam install not found. Configure SteamPathOverride or pass --steam.");

    private static int Detect(AppConfig config)
    {
        var path = SteamLocator.DetectInstallPath();
        Console.WriteLine($"Steam path: {path ?? "(not found)"}");
        Console.WriteLine($"Override  : {config.SteamPathOverride ?? "(none)"}");
        if (path is null) return 1;

        Console.WriteLine($"Libraries : {string.Join("; ", SteamLocator.ListLibraries(path))}");
        foreach (var a in SteamLocator.ListAccounts(path))
            Console.WriteLine($"Account   : {a.AccountId} ({a.DisplayName ?? "?"})");
        return 0;
    }

    private static int Scan(AppConfig config)
    {
        var steam = ResolveSteam(config);
        var audit = new AuditEngine(config);
        var buckets = audit.ListLocal(steam, owned: config.GetOwnedSet());

        Console.WriteLine($"{buckets.Count} local bucket(s).");
        foreach (var b in buckets)
        {
            Console.WriteLine($"  [{b.AppId}] {b.Era,-20} {b.Files.Count} files, {b.TotalBytes}b  {b.Note}");
            foreach (var f in b.Files)
            {
                var tag = "";
                var filePath = Path.Combine(steam, "userdata", "?", b.AppId.ToString());
                // best-effort tag check against every account dir
                tag = QuickTag(steam, b.AppId, f.FileName);
                Console.WriteLine($"      {f.FileName} ({f.FileSize}b){tag}");
            }
        }

        return 0;
    }

    private static string QuickTag(string steam, uint appId, string fileName)
    {
        var userdata = Path.Combine(steam, "userdata");
        if (!Directory.Exists(userdata)) return "";
        foreach (var account in Directory.EnumerateDirectories(userdata))
        {
            var path = Path.Combine(account, appId.ToString(), fileName);
            if (!File.Exists(path)) continue;
            var info = new FileInfo(path);
            if (info.Length < Barcode.TailWindowBytes) continue;
            var tail = ReadTail(path, Math.Min(info.Length, Barcode.TailWindowBytes));
            if (Barcode.TryDecodeTail(tail, out var payload, out _))
                return $"  tag={payload}";
        }
        return "";
    }

    private static async Task<int> RemoteList(string[] args, AppConfig config)
    {
        var appFilter = Arg(args, "--app");
        if (appFilter is null)
        {
            Console.WriteLine("usage: remote-list --app <appid> [--proxy <appid>]");
            return 1;
        }

        var appId = uint.Parse(appFilter);
        var proxyAppId = Arg(args, "--proxy") is { } px && uint.TryParse(px, out var pv) ? pv
            : config.ResolveProxy(appId);

        await using var s = await ConnectSessionAsync();

        try
        {
            List<CloudFileEntry> files;
            string headline;
            if (proxyAppId == 0)
            {
                var rpc = new CloudRpcClient(s);
                files = await rpc.EnumerateAsync(appId);
                headline = $"{appId}: {files.Count} file(s):";
            }
            else
            {
                Console.WriteLine($"note: {appId} is not owned here - listing the sls-{appId}/ namespace inside proxy bucket {proxyAppId}");
                var lane = new CloudProxyLane(new CloudRpcClient(s), appId, proxyAppId);
                files = await lane.EnumerateAsync();
                headline = $"{appId} (via proxy {proxyAppId}): {files.Count} file(s):";
            }

            Console.WriteLine(headline);
            foreach (var f in files)
            {
                var sha = f.FileSha is { Length: > 0 } fsha ? fsha.Length > 16 ? fsha[..16] + "..." : fsha : "-";
                Console.WriteLine($"  {f.FileName}  {f.FileSize}b  ts={f.Timestamp}  sha={sha}");
            }

            return 0;
        }
        catch (CloudRpcException ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> Probe(string[] args, AppConfig config)
    {
        var appIds = args.Skip(1).Where(a => uint.TryParse(a, out _)).Select(uint.Parse).ToArray();
        if (appIds.Length == 0)
        {
            Console.WriteLine("usage: probe <appid...>");
            return 1;
        }

        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);
        foreach (var appId in appIds)
        {
            var v = await rpc.ProbeAsync(appId);
            Console.WriteLine($"{appId}: enumerate={v.Enumerate,-12} upload={v.Upload,-12} delete={v.Delete,-12} | {v.Detail}");
        }

        return 0;
    }

    private static Task<int> ProxyCmd(string[] args, AppConfig config)
    {
        // appid proxy map (docs/APPID-PROXY.md): game -> owned bucket. key 0 is
        // the catch-all default for every unowned game without its own mapping.
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "set":
            {
                if (args.Length < 4 || !uint.TryParse(args[2], out var game) || !uint.TryParse(args[3], out var proxy))
                {
                    Console.WriteLine("usage: proxy set <gameAppId> <proxyAppId>   (game 0 = default for ALL unowned games; proxy 0 = clear the mapping)");
                    return Task.FromResult(1);
                }

                if (proxy == 0)
                {
                    config.CloudProxies.Remove(game);
                    Console.WriteLine($"CloudProxies[{game}] cleared - that game falls through to the default proxy now, if there is one");
                }
                else
                {
                    if (!config.GetOwnedSet().Contains(proxy))
                        Console.WriteLine($"WARNING: {proxy} is not in the owned set according to 'scan' - Valve just denies uploads to unowned buckets, so this one is probably on fire before it starts.");
                    config.CloudProxies[game] = proxy;
                    Console.WriteLine($"CloudProxies[{game}] = {proxy}");
                }
                config.Save(ConfigPath);
                return Task.FromResult(0);
            }
            case "rm":
            {
                if (args.Length < 3 || !uint.TryParse(args[2], out var game))
                {
                    Console.WriteLine("usage: proxy rm <gameAppId>   (0 = drop the default proxy)");
                    return Task.FromResult(1);
                }
                config.CloudProxies.Remove(game);
                config.Save(ConfigPath);
                Console.WriteLine($"CloudProxies[{game}] removed");
                return Task.FromResult(0);
            }
            case "ls":
            case "status":
            {
                if (config.CloudProxies.Count == 0)
                {
                    Console.WriteLine("no appid proxies configured - unowned saves stay behind the april 2025 AccessDenied wall. see docs/APPID-PROXY.md");
                    return Task.FromResult(0);
                }
                Console.WriteLine("CloudProxies (game -> proxy bucket):");
                foreach (var (game, proxy) in config.CloudProxies.OrderBy(k => k.Key))
                    Console.WriteLine($"  {(game == 0 ? "default" : "game")} {game,10} -> {proxy}");
                return Task.FromResult(0);
            }
            default:
                Console.WriteLine("usage: proxy status | set <gameAppId> <proxyAppId> | rm <gameAppId> | ls");
                return Task.FromResult(1);
        }
    }

    private static string? GuardRefusal(AppConfig config, params uint[] appIds)
    {
        foreach (var id in appIds)
            if (config.GuardedAppIds.Contains(id))
                return $"appid {id} is on the never-touch list - refused (guards add|rm <appid> to change)";
        return null;
    }

    private static async Task<int> Wipe(string[] args, AppConfig config)
    {
        if (args.Length < 3 || !uint.TryParse(args[1], out var appId))
        {
            Console.WriteLine("usage: wipe <appid> <filename> [--blank] [--force]   (proxied saves: wipe <proxyBucket> sls-<game>/<file>)");
            return 1;
        }

        var file = args[2];

        // proxied saves ride in an owned bucket under a sls-<game>/ prefix; strip it
        // and go back through the proxy lane so the prefix lands on exactly once.
        uint? proxyFor = null;
        if (CloudProxy.TryStrip(file, out var proxiedGame, out var stripped) && config.ResolveProxy(proxiedGame) is var prox && prox != 0)
        {
            Console.WriteLine($"note: {file} is a proxied save of game {proxiedGame} -> wiping in proxy bucket {prox}");
            appId = prox;
            file = stripped;
            proxyFor = proxiedGame;
        }

        // never-touch list: refuse before any cloud mutation
        if (GuardRefusal(config, appId, proxyFor ?? 0) is { } refusal)
        {
            Console.WriteLine($"REFUSED: {refusal}");
            return 1;
        }

        var blank = Has(args, "--blank");
        var dryRun = !Has(args, "--force") && config.DryRun;
        if (dryRun) Console.WriteLine("DRY-RUN (--force to execute)");
        config.DryRun = dryRun;

        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);
        var engine = new WipeEngine(config);
        var outcome = proxyFor is { } g
            ? await engine.WipeAsync(new CloudProxyLane(rpc, g, appId), file, blank)
            : await engine.WipeAsync(rpc, appId, file, blank);
        config.Save(); // persist auto-guard added after a successful delete

        Console.WriteLine($"{(outcome.Success ? "OK  " : "FAIL")} [{outcome.Action}] {outcome.AppId}/{outcome.FileName}: {outcome.Result}");
        return outcome.Success ? 0 : 1;
    }

    private static async Task<int> WipeAll(string[] args, AppConfig config)
    {
        if (args.Length < 2 || !uint.TryParse(args[1], out var appId))
        {
            Console.WriteLine("usage: wipe-all <appid> [--blank] [--force]");
            return 1;
        }

        if (GuardRefusal(config, appId) is { } refusal)
        {
            Console.WriteLine($"REFUSED: {refusal}");
            return 1;
        }

        var blank = Has(args, "--blank");
        var dryRun = !Has(args, "--force") && config.DryRun;
        if (dryRun) Console.WriteLine("DRY-RUN (--force to execute)");
        config.DryRun = dryRun;

        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);
        var files = await rpc.EnumerateAsync(appId);
        if (files.Count == 0)
        {
            Console.WriteLine("no files in bucket");
            return 0;
        }

        Console.WriteLine($"{files.Count} files in bucket {appId}");
        var engine = new WipeEngine(config);
        var ok = 0;
        foreach (var f in files)
        {
            var o = await engine.WipeAsync(rpc, appId, f.FileName, blank);
            if (o.Success) ok++;
            Console.WriteLine($"  {(o.Success ? "OK " : "ERR")} {f.FileName}");
        }
        config.Save(); // persist guards added after successful deletes

        Console.WriteLine($"{ok}/{files.Count} succeeded");
        return ok == files.Count ? 0 : 1;
    }

    private static int Guards(string[] args, AppConfig config)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "ls";
        switch (sub)
        {
            case "add" when args.Length > 2 && uint.TryParse(args[2], out var id):
                config.GuardedAppIds.Add(id);
                config.Save(ConfigPath);
                Console.WriteLine($"guarded {id}");
                break;
            case "rm" when args.Length > 2 && uint.TryParse(args[2], out var id):
                config.GuardedAppIds.Remove(id);
                config.Save(ConfigPath);
                Console.WriteLine($"unguarded {id}");
                break;
            case "ls":
                foreach (var g in config.GuardedAppIds.OrderBy(x => x)) Console.WriteLine(g);
                break;
            default:
                Console.WriteLine("usage: guards add|rm|ls <appid>");
                return 1;
        }

        return 0;
    }

    private static int InjectLocal(string[] args, AppConfig config)
    {
        if (args.Length < 4 || !uint.TryParse(args[1], out var uid) || !uint.TryParse(args[2], out var appId))
        {
            Console.WriteLine("usage: inject <uid3> <appid> <file> [remote-name]");
            return 1;
        }

        var src = args[3];
        var name = args.Length > 4 ? args[4] : null;
        var appDir = Path.Combine(ResolveSteam(config), "userdata", uid.ToString(), appId.ToString());

        var engine = new LocalInjectEngine();
        var dest = engine.InjectFile(appDir, src, name);
        Console.WriteLine($"wrote {dest}");
        return 0;
    }

    private static int LockBucket(string[] args, AppConfig config)
    {
        if (args.Length < 4 || !uint.TryParse(args[2], out var uid) || !uint.TryParse(args[3], out var appId))
        {
            Console.WriteLine("usage: lock <uid3> <appid>   | unlock <uid3> <appid>");
            return 1;
        }

        var unlock = args[1] == "unlock";
        if (config.GuardedAppIds.Contains(appId))
        {
            Console.WriteLine($"appid {appId} is guarded, skipping");
            return 1;
        }

        var steam = ResolveSteam(config);
        var userDir = Path.Combine(steam, "userdata", uid.ToString());
        var engine = new LocalInjectEngine();
        engine.Log += Console.WriteLine;

        if (!unlock)
        {
            var dryRun = !Has(args, "--force") && config.DryRun;
            if (dryRun)
            {
                Console.WriteLine($"DRY-RUN: would lock appid {appId} in {userDir}");
                return 0;
            }
            engine.InstallLock(userDir, appId);
        }
        else
        {
            engine.RemoveLock(userDir, appId);
        }

        return 0;
    }

    private static async Task<int> WebCloud(string[] args, AppConfig config)
    {
        var cookie = config.CookieFile is not null && File.Exists(config.CookieFile)
            ? File.ReadAllText(config.CookieFile).Trim()
            : Environment.GetEnvironmentVariable("SCT_COOKIE");

        if (string.IsNullOrEmpty(cookie))
        {
            Console.WriteLine("Set SCT_COOKIE or config.CookieFile (a steam session cookie) for web lane.");
            return 1;
        }

        var web = new SteamWebClient(cookie);
        var cmd = args.Length > 1 ? args[1].ToLowerInvariant() : "ls";

        switch (cmd)
        {
            case "ls":
            {
                var apps = await web.ListAppsAsync();
                Console.WriteLine($"{apps.Count} app(s) visible on remote storage:");
                foreach (var a in apps.OrderBy(a => a.AppId))
                    Console.WriteLine($"  [{a.AppId}] {a.Name}  ({a.FileCount} files, {a.TotalBytes}b)");
                return 0;
            }
            case "files":
            {
                if (args.Length < 3 || !uint.TryParse(args[2], out var appId))
                {
                    Console.WriteLine("usage: web files <appid>");
                    return 1;
                }
                var files = await web.ListFilesAsync(appId);
                Console.WriteLine($"{appId}: {files.Count} file(s):");
                foreach (var f in files)
                    Console.WriteLine($"  {f.FileName}  {f.Size}b  {f.Detail ?? ""}");
                return 0;
            }
            case "dl":
            {
                if (args.Length < 4 || !uint.TryParse(args[2], out var appId))
                {
                    Console.WriteLine("usage: web dl <appid> <name> [outfile]");
                    return 1;
                }
                var outFile = args.Length > 4 ? args[4] : $"{appId}_{args[3]}";
                var bytes = await web.DownloadAsync(appId, args[3]);
                if (bytes is null)
                {
                    Console.WriteLine("download failed (no data)");
                    return 1;
                }
                File.WriteAllBytes(outFile, bytes);
                Console.WriteLine($"saved {bytes.Length}b -> {outFile}");
                return 0;
            }
            default:
                Console.WriteLine("usage: web ls | files <appid> | dl <appid> <file> [outfile]");
                return 1;
        }
    }

    private static async Task<int> FerryCmd(string[] args, AppConfig config)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "ls";
        if (sub is not ("ls" or "upload" or "dl"))
        {
            Console.WriteLine("usage: ferry ls | upload <local-file> [name] | dl <name> [outfile]");
            return 1;
        }

        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);

        switch (sub)
        {
            case "ls":
            {
                var files = await rpc.EnumerateAsync(Ferry.SpacewarApp);
                Console.WriteLine($"AppID 480 (Spacewar) bucket - {files.Count} file(s):");
                foreach (var f in files)
                {
                    var (src, orig) = Ferry.UnparkName(f.FileName);
                    var origin = src != 0 && orig != f.FileName ? $" (from AppID {src}: {orig})" : "";
                    Console.WriteLine($"  {f.FileName}  {f.FileSize}b  ts={f.Timestamp}{origin}");
                }
                return 0;
            }
            case "upload":
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("usage: ferry upload <local-file> [name]");
                    return 1;
                }
                var src = args[2];
                var data = File.ReadAllBytes(src);
                var name = args.Length > 3 ? args[3] : Ferry.ParkName(0, Path.GetFileName(src).Replace("_", "-"));
                var result = await rpc.UploadAsync(Ferry.SpacewarApp, name, data);
                Console.WriteLine($"upload('{name}') -> {result} ({data.Length}b)");
                return result == SteamKit2.EResult.OK ? 0 : 1;
            }
            default:
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("usage: ferry dl <name> [outfile]");
                    return 1;
                }
                var name = args[2];
                var data = await rpc.DownloadAsync(Ferry.SpacewarApp, name);
                if (data is null)
                {
                    Console.WriteLine("download failed");
                    return 1;
                }
                var outFile = args.Length > 3 ? args[3] : name.Replace('/', '_');
                File.WriteAllBytes(outFile, data);
                Console.WriteLine($"saved {data.Length}b -> {outFile}");
                return 0;
            }
        }
    }

    private static int RelocateBucket(string[] args, AppConfig config)
    {
        if (args.Length < 4 || !uint.TryParse(args[2], out var uid) || !uint.TryParse(args[3], out var appId))
        {
            Console.WriteLine("usage: relocate <uid3> <appid>   | unrelocate <uid3> <appid>");
            return 1;
        }

        var undo = args[1] == "unrelocate";
        if (!undo && config.GuardedAppIds.Contains(appId))
        {
            Console.WriteLine($"appid {appId} is guarded, skipping");
            return 1;
        }

        var steam = ResolveSteam(config);
        var userDir = Path.Combine(steam, "userdata", uid.ToString());
        var engine = new LocalInjectEngine();
        engine.Log += Console.WriteLine;

        if (!undo)
        {
            var dryRun = !Has(args, "--force") && config.DryRun;
            if (dryRun)
            {
                Console.WriteLine($"DRY-RUN: would relocate appid {appId} from {userDir}");
                return 0;
            }
            engine.Relocate(userDir, appId);
        }
        else
        {
            engine.Unrelocate(userDir, appId);
        }

        return 0;
    }

    private static async Task<SteamSession> ConnectSessionAsync()
    {
        var session = new SteamSession();
        session.Event += Console.WriteLine;

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
            var hint = mode == "qr"
                ? "QR scan may have timed out - try again"
                : user is { Length: > 0 }
                    ? "check SCT_USER/SCT_PASS credentials, or try SCT_AUTH_MODE=qr"
                    : "set SCT_USER/SCT_PASS for account ops, or SCT_AUTH_MODE=qr for QR login";
            throw new InvalidOperationException($"could not log on to Steam ({hint})");
        }

        return session;
    }

    private static async Task<int> PoolCmd(string[] args, AppConfig config)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
            {
                Console.WriteLine("Parking pool (owned-game buckets need --allow-owned consent):");
                var reg = SctRegistry.Load();
                foreach (var p in PoolDb.DefaultPool
                             .OrderBy(p => p.Tier).ThenBy(p => p.AppId))
                {
                    var state = p.State.ToString().ToLowerInvariant();
                    var probe = reg.PoolProbes.TryGetValue(p.AppId, out var ps) ? $"[probe:{ps}]" : "";
                    Console.WriteLine($"  [{p.Tier}] {p.AppId,-8} {p.Name,-32} ({(p.IsFree ? "free" : "PAID")}, {p.ReleaseYear}) {state,-18} {probe} {p.Category} - {p.Note}");
                }
                return 0;
            }
            case "refresh":
            {
                Console.WriteLine($"Refreshing pool metadata from the store API ({PoolDb.DefaultPool.Count} apps)...");
                var progress = new Progress<(uint, AppDetails)>(
                    r => Console.WriteLine($"  {r.Item1}: {r.Item2.Name} (free={r.Item2.IsFree}, {r.Item2.ReleaseDate})"));
                var results = await StoreApi.RefreshPoolAsync(
                    PoolDb.DefaultPool.Select(p => p.AppId), progress);
                var ok = results.Count(r => r.Details.Found);
                Console.WriteLine($"{ok}/{results.Count} resolved.");
                return 0;
            }
            case "probe":
            {
                return await PoolProbeCmd(args, config);
            }
            case "discover":
            {
                return PoolDiscoverCmd(args, config);
            }
            default:
                Console.WriteLine("usage: pool list | refresh | probe [--uid <id3>] [--wait-sec N] [--lane client|rpc] [--console] | discover [--net]");
                return 1;
        }
    }

    /// <summary>
    /// Sweeps the machine for container AppIDs (PoolDb + userdata + OST lua hooks +
    /// CloudRedirect host + SLS/GreenLuma) and saves the snapshot to the registry.
    /// --net also reads cloud_log to flag which containers the client itself AutoClouds.
    /// </summary>
    private static int PoolDiscoverCmd(string[] args, AppConfig config)
    {
        var steam = ResolveSteam(config);
        var registry = SctRegistry.Load();

        Func<uint, bool>? autoClouded = null;
        if (Has(args, "--net"))
            autoClouded = appId => CloudLogWatcher.WasEverAutoClouded(steam, appId);

        registry.SyncDiscovered(steam, autoClouded);

        var rows = registry.Discovered;
        Console.WriteLine($"Container universe for {steam} ({rows.Count} container(s)):");
        Console.WriteLine($"  {"APPID",-8} {"KIND",-10} {"SOURCE",-10} {"POSTURE",-10} {"AUTO",-5} NAME / NOTE");
        foreach (var c in rows)
        {
            var name = string.IsNullOrEmpty(c.Name)
                ? c.Note ?? ""
                : c.Note is null ? c.Name : $"{c.Name} - {c.Note}";
            Console.WriteLine($"  {c.AppId,-8} {c.Kind,-10} {c.Source,-10} {c.Posture,-10} {(c.AutoClouded ? "yes" : ""),-5} {name}");
        }
        var real = rows.Count(c => c.IsRealCandidate);
        var activation = rows.Count(c => c.Kind == ContainerKind.Activation);
        Console.WriteLine($"{real} real/provider container(s), {activation} activation-tool container(s). Registry saved.");
        return 0;
    }

    /// <summary>
    /// Private writability probe: drops ONE tiny barcode-tagged file into each candidate
    /// slot bucket, lets the RUNNING Steam client sync it (no SCT logon), reads the verdict
    /// from cloud_log.txt, then cleans the probe back out. No anonymous flooding - one
    /// quiet file per real app at a time. --lane rpc probes directly via a real SCT logon.
    /// </summary>
    private static async Task<int> PoolProbeCmd(string[] args, AppConfig config)
    {
        var steam = ResolveSteam(config);
        var uid = ResolveUid(args, config, steam);
        if (uid == 0)
        {
            Console.WriteLine("no account - pass --uid <id3> or run with Steam logged in");
            return 1;
        }

        var laneRpc = Has(args, "--lane") && (Arg(args, "--lane") ?? "").Equals("rpc", StringComparison.OrdinalIgnoreCase);
        var waitSec = Arg(args, "--wait-sec") is { } ws && int.TryParse(ws, out var w) ? w : 20;
        var dryRun = !Has(args, "--force") && config.DryRun;
        Console.WriteLine($"probe account {uid}: {PoolDb.Usable().Count()} candidate slot(s), lane {(laneRpc ? "rpc (SCT logon)" : "client (running Steam tick)")}" + (dryRun ? " (DRY-RUN)" : ""));

        var registry = SctRegistry.Load();
        var engine = new LocalInjectEngine();
        engine.Log += Console.WriteLine;
        var random = Random.Shared;
        var total = 0;

        if (laneRpc)
        {
            // ---- rpc lane probe: real SCT logon, direct Cloud.* calls ----
            if (dryRun)
            {
                foreach (var slot in PoolDb.Usable().OrderBy(p => p.Tier).ThenBy(p => p.AppId))
                    Console.WriteLine($"  [dry] would probe {slot.AppId} ({slot.Name}) via RPC");
                return 0;
            }
            await using var session = await ConnectSessionAsync();
            var rpc = new CloudRpcClient(session);
            foreach (var slot in PoolDb.Usable().OrderBy(p => p.Tier).ThenBy(p => p.AppId))
            {
                var v = await rpc.ProbeAsync(slot.AppId);
                total++;
                if (v.Upload == Policy.Allowed && v.Delete == Policy.Allowed)
                {
                    registry.PoolProbes[slot.AppId] = "VerifiedWritable";
                    Console.WriteLine($"  {slot.AppId} ({slot.Name}): {Policy.Allowed}/{Policy.Allowed} - slot proven");
                }
                else
                {
                    registry.PoolProbes[slot.AppId] = "Denied";
                    Console.WriteLine($"  {slot.AppId} ({slot.Name}): {v.Upload}/{v.Delete} - not writable as {uid}; slot excluded ({v.Detail})");
                }
                registry.Save();
            }
            Console.WriteLine($"rpc probe done: {total} slot(s) checked; verdicts saved to registry");
            return 0;
        }

        foreach (var slot in PoolDb.Usable().OrderBy(p => p.Tier).ThenBy(p => p.AppId))
        {
            var userAppDir = Path.Combine(steam, "userdata", uid.ToString(), slot.AppId.ToString());
            var probePath = Path.Combine(userAppDir, "remote", ProbeFileName);

            if (dryRun)
            {
                Console.WriteLine($"  [dry] would probe {slot.AppId} ({slot.Name})");
                continue;
            }

            if (!File.Exists(probePath))
            {
                var payload = $"{slot.AppId}{Barcode.Sep}probe{Barcode.Sep}{DateTime.Now:ddMMyyyy}";
                var probeBytes = new byte[64];
                random.NextBytes(probeBytes);
                var tagged = probeBytes.Concat(Barcode.PackTrailer(payload)).ToArray();
                Directory.CreateDirectory(Path.Combine(userAppDir, "remote"));
                File.WriteAllBytes(probePath, tagged);
            }
            engine.RegenerateVdf(userAppDir);

            // force the running client to sync the probe NOW via the console lane
            // (cloud_sync_up); falls back to the client's own AutoCloud tick
            var (verdict, _, consoleAvailable) = await PushSyncViaConsoleAsync(steam, slot.AppId, waitSec, useConsole: Has(args, "--console"));
            total++;

            switch (verdict)
            {
                case CloudVerdict.Success:
                    registry.PoolProbes[slot.AppId] = "VerifiedWritable";
                    Console.WriteLine($"  {slot.AppId} ({slot.Name}): {CloudVerdict.Success} - slot proven, probe removed");
                    File.Delete(probePath);
                    engine.RegenerateVdf(userAppDir);
                    break;
                case CloudVerdict.Denied:
                    registry.PoolProbes[slot.AppId] = "Denied";
                    Console.WriteLine($"  {slot.AppId} ({slot.Name}): {CloudVerdict.Denied} - account is not entitled; slot excluded");
                    File.Delete(probePath);
                    engine.RegenerateVdf(userAppDir);
                    break;
                default:
                    Console.WriteLine($"  {slot.AppId} ({slot.Name}): {verdict} - no verdict yet");
                    PrintNoVerdictGuidance(steam, slot.AppId, consoleAvailable);
                    break;
            }
            registry.Save();
        }

        Console.WriteLine($"probe done: {total} slot(s) checked; verdicts saved to registry (see object)");
        return 0;
    }

    private const string ProbeFileName = "sctprobe.bin";

    /// <summary>
    /// Forces a cloud sync of one bucket through the RUNNING Steam client's console
    /// (cloud_sync_up/down) and reads the verdict from cloud_log.txt. This is the
    /// real-cloud pressure button: staged files get uploaded by the official client,
    /// exactly like the GUI's "synced to cloud" indicator - no SCT logon needed.
    /// </summary>
    private static async Task<int> ClientCmd(string[] args, AppConfig config)
    {
        var steam = ResolveSteam(config);
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";

        switch (sub)
        {
            case "status":
            {
                var running = SteamLocator.IsRunning();
                Console.WriteLine($"steam running : {running}");
                if (running)
                {
                    var active = SteamLocator.GetActiveAccount(steam);
                    Console.WriteLine($"active account: {active?.AccountId} ({active?.DisplayName ?? "?"})");
                    Console.WriteLine($"console open  : {SteamConsole.IsOpen()}");
                    var toml = Path.Combine(steam, "opensteamtool.toml");
                    Console.WriteLine($"OST toml      : {(File.Exists(toml) ? "present" : "absent")}");
                    Console.WriteLine($"CR provider   : {SteamLocator.IsCloudRedirectLoaded(steam)}");
                }
                else
                {
                    Console.WriteLine("(client lane needs Steam up - see 'rpc' or 'park --lane stage' otherwise)");
                }
                return 0;
            }
            case "sync":
            {
                if (args.Length < 3 || !uint.TryParse(args[2], out var appId))
                {
                    Console.WriteLine("usage: client sync <appid> [--down] [--wait-sec N]");
                    return 1;
                }
                var down = Has(args, "--down");
                var waitSec = Arg(args, "--wait-sec") is { } ws && int.TryParse(ws, out var w) ? w : 25;
                var (verdict, line, consoleAvailable) = await PushSyncViaConsoleAsync(steam, appId, waitSec, down, useConsole: Has(args, "--console"));

                var posture = SteamLocator.SyncPosture(steam, appId);
                Console.WriteLine($"{appId}: {verdict} (posture: {posture})");
                if (line is not null) Console.WriteLine($"  <{line.Trim()}>");

                var registry = SctRegistry.Load();
                registry.PoolProbes[appId] = verdict switch
                {
                    CloudVerdict.Success => "VerifiedWritable",
                    CloudVerdict.Denied => "Denied",
                    _ => registry.PoolProbes.TryGetValue(appId, out var old) ? old : "Unknown",
                };
                registry.Save();
                switch (verdict)
                {
                    case CloudVerdict.Success:
                        Console.WriteLine("slot marked VerifiedWritable in the registry");
                        break;
                    case CloudVerdict.Denied:
                        Console.WriteLine("slot marked Denied - parking engine will skip it");
                        break;
                    default:
                        Console.WriteLine("no verdict yet - client quiet or app not syncing");
                        PrintNoVerdictGuidance(steam, appId, consoleAvailable);
                        break;
                }
                return verdict == CloudVerdict.Success ? 0 : 1;
            }
            case "tell":
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("usage: client tell <raw-console-command>   e.g. client tell cloud_sync_up 480");
                    return 1;
                }
                if (!SteamLocator.IsRunning())
                {
                    Console.WriteLine("Steam is not running");
                    return 1;
                }
                if (!await SteamConsole.OpenConsoleAsync(TimeSpan.FromSeconds(15)))
                {
                    Console.WriteLine("could not open the Steam console (steam://open/console)");
                    return 1;
                }
                var raw = string.Join(" ", args.Skip(2));
                var sent = SteamConsole.SendCommand(raw);
                Console.WriteLine(sent ? $"sent to console: {raw}" : "console window not found");
                return sent ? 0 : 1;
            }
            default:
                Console.WriteLine("usage: client status | sync <appid> [--down] [--wait-sec N] | tell <command>");
                return 1;
        }
    }

    /// <summary>
    /// Client-lane sync pressure: stage + wait for the RUNNING client's own cloud
    /// behavior. With --console the Steam Console (steam://open/console) is forced
    /// open and cloud_sync_up/down is typed into it - on newer client builds the
    /// console is unavailable/deprecated, so that mode is opt-in and falls back to
    /// the tick-based wait. 'client tell' still exposes manual console input for
    /// builds where it works. Callers read --console from args to opt back in.
    /// </summary>
    private static async Task<(CloudVerdict Verdict, string? MatchLine, bool ConsoleAvailable)> PushSyncViaConsoleAsync(
        string steam, uint appId, int waitSec, bool down = false, bool useConsole = false)
    {
        if (!SteamLocator.IsRunning())
            throw new InvalidOperationException("Steam is not running - the client lane needs the running session");

        var watcher = new CloudLogWatcher(steam, appId);
        if (useConsole)
        {
            if (!await SteamConsole.OpenConsoleAsync(TimeSpan.FromSeconds(15)))
            {
                Console.WriteLine("  [console] could not open the Steam Console - falling back to the client's own sync tick");
                var fallback = await watcher.WaitForVerdictAsync(TimeSpan.FromSeconds(waitSec));
                return (fallback.Verdict, fallback.MatchLine, false);
            }
            Console.WriteLine($"  [console] sending cloud_sync_{(down ? "down" : "up")} {appId} to the console...");
            var sent = SteamConsole.SendCommand($"cloud_sync_{(down ? "down" : "up")} {appId}");
            Console.WriteLine(sent ? "  [console] command accepted" : "  [console] console window not found - waiting for the client's own tick");
            var consoleResult = await watcher.WaitForVerdictAsync(TimeSpan.FromSeconds(waitSec));
            return (consoleResult.Verdict, consoleResult.MatchLine, true);
        }

        Console.WriteLine("  [console] skipping Steam Console (not reliable on this client build) - waiting for the client's own sync tick");
        var result = await watcher.WaitForVerdictAsync(TimeSpan.FromSeconds(waitSec));
        return (result.Verdict, result.MatchLine, false);
    }

    /// <summary>Explains what to do when the client lane produced no verdict.</summary>
    private static void PrintNoVerdictGuidance(string steam, uint appId, bool consoleAvailable)
    {
        if (consoleAvailable)
        {
            Console.WriteLine($"  no verdict yet for {appId} - try 'client sync {appId} --wait-sec 60' or launch the app once");
            return;
        }
        if (!CloudLogWatcher.WasEverAutoClouded(steam, appId))
        {
            Console.WriteLine($"  {appId} is not AutoClouded by this client (never seen 'AutoCloud checking local state' in cloud_log).");
            Console.WriteLine("  The client will not sync it on its own tick - use the RPC lane for a REAL upload:");
            Console.WriteLine("    park <gameAppId> --lane rpc --bucket <appid>   (needs SCT_USER/SCT_PASS or SCT_AUTH_MODE=qr)");
            Console.WriteLine("  or drop a file into userdata and upload it as the account via 'ferry upload'");
        }
        else
        {
            Console.WriteLine($"  {appId} IS AutoClouded - the client syncs it on its own; wait a bit and re-check with 'client sync {appId}'");
        }
    }

    /// <summary>
    /// CloudRedirect provider family: SCT owns the provider config
    /// (%AppData%\CloudRedirect\config.json + folder provider root) that OST/CR
    /// consume. Uploads for CR-intercepted apps land here (posture "provider").
    /// </summary>
    private static int ProviderCmd(string[] args, AppConfig config)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "CloudRedirect", "config.json");

        switch (sub)
        {
            case "status":
            {
                var exists = File.Exists(configPath);
                Console.WriteLine($"CR config      : {configPath} ({(exists ? "present" : "absent")})");
                if (exists)
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
                        var root = doc.RootElement;
                        Console.WriteLine($"provider       : {root.GetProperty("provider").GetString()}");
                        Console.WriteLine($"sync path      : {root.GetProperty("sync_path").GetString()}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"config unreadable: {ex.Message}");
                    }
                }
                var steam = ResolveSteam(config);
                Console.WriteLine($"CR loaded      : {SteamLocator.IsCloudRedirectLoaded(steam)}");
                return 0;
            }
            case "init":
            {
                var syncPath = args.Length > 2 ? args[2] : @"D:\sct_provider";
                Directory.CreateDirectory(syncPath);
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                var json = $$"""
                {
                  "provider": "folder",
                  "sync_path": {{System.Text.Json.JsonSerializer.Serialize(syncPath)}}
                }
                """;
                File.WriteAllText(configPath, json);
                Console.WriteLine($"wrote {configPath} (provider=folder, sync_path={syncPath})");
                Console.WriteLine("restart Steam for CR to pick it up if it was already running");
                return 0;
            }
            case "ls":
            {
                var syncPath = Arg(args, "--path");
                if (syncPath is null && File.Exists(configPath))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
                        syncPath = doc.RootElement.GetProperty("sync_path").GetString();
                    }
                    catch { }
                }
                if (syncPath is null || !Directory.Exists(syncPath))
                {
                    Console.WriteLine("no provider root found (provider init first, or pass --path <dir>)");
                    return 1;
                }
                var filter = Arg(args, "--app") is { } fa && uint.TryParse(fa, out var fapp) ? fapp : (uint?)null;
                var filterUid = Arg(args, "--uid") is { } fu && uint.TryParse(fu, out var fuid) ? fuid : (uint?)null;
                var any = false;
                foreach (var accDir in Directory.EnumerateDirectories(syncPath))
                {
                    var acc = Path.GetFileName(accDir);
                    if (filterUid is not null && !acc.Equals(filterUid.ToString(), StringComparison.Ordinal)) continue;
                    foreach (var appDir in Directory.EnumerateDirectories(accDir))
                    {
                        var app = Path.GetFileName(appDir);
                        if (filter is not null && !app.Equals(filter.Value.ToString(), StringComparison.Ordinal)) continue;
                        var blobs = Path.Combine(appDir, "blobs");
                        var files = Directory.Exists(blobs) ? Directory.GetFiles(blobs).Select(f => Path.GetFileName(f) ?? f).ToList() : new List<string>();
                        Console.WriteLine($"  {acc}/{app}: {files.Count} blob(s) [{string.Join(", ", files)}]");
                        any = true;
                    }
                }
                if (!any) Console.WriteLine("(empty provider)");
                return 0;
            }
            default:
                Console.WriteLine("usage: provider status | init [sync-dir] | ls [--uid <id3>] [--app <appid>] [--path <dir>]");
                return 1;
        }
    }

    private static async Task<int> ParkCmd(string[] args, AppConfig config)
    {
        // shapes: park <uid3> <gameAppId> [flags]   |   park <gameAppId> [--uid <id3>] [flags]
        var a1 = args.Length > 1 && uint.TryParse(args[1], out var x1) ? x1 : 0u;
        var a2 = args.Length > 2 && uint.TryParse(args[2], out var x2) ? x2 : 0u;

        uint uid, gameAppId;
        if (args.Length >= 3 && a1 != 0 && a2 != 0) { uid = a1; gameAppId = a2; }
        else if (args.Length >= 2 && a1 != 0)
        {
            gameAppId = a1;
            var steam0 = ResolveSteam(config);
            uid = ResolveUid(args, config, steam0);
            if (uid == 0)
            {
                Console.WriteLine("no account to park for - pass --uid <id3> or run with Steam logged in");
                return 1;
            }
        }
        else
        {
            Console.WriteLine("usage: park <gameAppId> [--uid <id3>] [--force] [--lane auto|client|rpc|stage] [--bucket <appid>] [--proxy <appid>] [--spread N] [--copies N] [--stealth] [--wait-sec N] [--allow-owned] [--verify] [--console] [--posture real,provider,redirected|any]");
            return 1;
        }

        var allowOwned = Has(args, "--allow-owned");
        var postureFilter = ParsePostureFilter(Arg(args, "--posture"));
        var stealth = Has(args, "--stealth");
        var spread = Arg(args, "--spread") is { } sp && int.TryParse(sp, out var s) ? Math.Max(1, s) : 1;
        var copies = Arg(args, "--copies") is { } cp && int.TryParse(cp, out var c) ? Math.Max(1, c) : 1;
        var dry = !Has(args, "--force") && config.DryRun;
        var steam = ResolveSteam(config);

        var bucketDir = Path.Combine(steam, "userdata", uid.ToString(), gameAppId.ToString());
        if (!Directory.Exists(bucketDir))
        {
            Console.WriteLine($"no local bucket {bucketDir}");
            return 1;
        }

        var registry = SctRegistry.Load();
        var files = Directory.EnumerateFiles(bucketDir, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("remotecache.vdf", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .ToList();
        if (files.Count == 0)
        {
            Console.WriteLine("bucket is empty (nothing to park)");
            return 0;
        }

        // ---- lane matrix ----
        //   auto   : Steam up + this account active -> client; otherwise explain the options
        //   client : stage locally + force cloud_sync_up through the RUNNING session's console
        //   rpc    : SCT logs on (QR/creds/anonymous) and uploads directly
        //   stage  : drop files locally only, no upload - the session syncs on its own later
        var lane = (Arg(args, "--lane") ?? "").ToLowerInvariant() switch
        {
            "client" => "client",
            "rpc" => "rpc",
            "stage" => "stage",
            _ => Has(args, "--client") ? "client"
                : Has(args, "--rpc") ? "rpc"
                : Has(args, "--offline") || Has(args, "--stage") ? "stage"
                : "auto",
        };
        if (lane == "auto")
        {
            if (SteamLocator.IsRunning())
            {
                var active = SteamLocator.GetActiveAccount(steam)?.AccountId;
                lane = active == uid ? "client" : "rpc";
                if (lane == "rpc")
                    Console.WriteLine($"active account {active} != target {uid}: falling back to the RPC lane (--lane client only works for the signed-in account)");
            }
            else
            {
                lane = "rpc";
                Console.WriteLine("Steam is not running: using the RPC lane (needs SCT login; 'park --lane stage' drops files locally only)");
            }
        }
        if (lane == "client")
        {
            if (!SteamLocator.IsRunning())
            {
                Console.WriteLine("--lane client needs Steam running; use --lane rpc or --lane stage");
                return 1;
            }
            var active = SteamLocator.GetActiveAccount(steam)?.AccountId;
            if (active != uid)
            {
                Console.WriteLine($"--lane client needs the target account signed in (active {active}, target {uid}); use --lane rpc or --lane stage");
                return 1;
            }
        }

        // ---- appid proxy (docs/APPID-PROXY.md) ----
        // unowned games can't park in pool buckets they don't own (the whole april
        // 2025 wall) - instead they ride an OWNED bucket under sls-<game>/ prefixes.
        // --proxy picks the bucket; otherwise CloudProxies map auto-resolves.
        var proxyAppId = Arg(args, "--proxy") is { } px && uint.TryParse(px, out var pvx) ? pvx
            : config.ResolveProxy(gameAppId);
        var proxied = proxyAppId != 0;
        if (proxied && (lane == "client" || lane == "auto"))
        {
            Console.WriteLine($"proxy parking ({gameAppId} -> {proxyAppId}) is rpc-only: the client lane uploads through the real Steam client, which still checks per-bucket entitlement - switching to --lane rpc");
            lane = "rpc";
        }
        if (proxied && !config.GetOwnedSet().Contains(proxyAppId))
        {
            Console.WriteLine($"proxy {proxyAppId} is not in the owned set - see 'scan'; uploads to it come back AccessDenied");
            return 1;
        }

        uint? bucket = Arg(args, "--bucket") is { } bk && uint.TryParse(bk, out var b) ? b : null;
        var today = DateOnly.FromDateTime(DateTime.Now);

        // container universe from this machine (owned userdata buckets, activation
        // hooks, CR host) - the allocator consults it for owned buckets + posture
        var containers = PoolDiscoverer.Discover(steam, proxies: config.CloudProxies);
        var ownedContainers = containers.Count(c => c.Kind == ContainerKind.Owned);
        if (ownedContainers > 0 && !allowOwned)
            Console.WriteLine($"note: {ownedContainers} owned game bucket(s) found but excluded - pass --allow-owned to opt in");
        if (postureFilter is not null)
            Console.WriteLine($"note: --posture {string.Join(",", postureFilter)} filters the candidate universe");

        var engine = new ParkingEngine(config.GetOwnedSet(), registry.Slots, poolProbes: registry.PoolProbes);
        Console.WriteLine($"{gameAppId}: {files.Count} file(s) to park | spread={spread} copies={copies} stealth={stealth} | owned-set {config.GetOwnedSet().Count} | uid {uid}");
        var decisions = engine.Plan(gameAppId, files.Select(f => new ParkFile(f.Name, f.Length)).ToList(),
            stealth, spread, copies, forceBucket: bucket, proxyBucket: proxied ? proxyAppId : null,
            containers: containers, allowOwned: allowOwned, postureFilter: postureFilter);

        var plans = new List<(string FileName, ParkingDecision D, byte[] Tagged)>();
        var di = 0;
        for (var i = 0; i < files.Count; i++)
        {
            for (var ci = 0; ci < copies; ci++)
            {
                var d = decisions[di++];
                if (!d.Ok)
                {
                    Console.WriteLine($"  {files[i].Name}: PARK REFUSED - {d.Reason}");
                    continue;
                }
                var tail = ReadTail(files[i].FullName, Math.Min(files[i].Length, Barcode.TailWindowBytes));
                if (Barcode.TryDecodeTail(tail, out var payload, out _))
                {
                    var (game, _, _) = Barcode.Parse(payload);
                    if (game == gameAppId && !stealth)
                    {
                        Console.WriteLine($"  skip {files[i].Name}: already tagged (barcode present)");
                        continue;
                    }
                }
                var originalBytes = File.ReadAllBytes(files[i].FullName);
                var trailer = Barcode.PackTrailer(gameAppId.ToString(), uid.ToString(), today);
                plans.Add((files[i].Name, d, originalBytes.Concat(trailer).ToArray()));
            }
        }

        if (plans.Count == 0)
        {
            Console.WriteLine("nothing to park (all files already tagged or refused)");
            return 0;
        }

        foreach (var p in plans)
        {
            var shown = p.D.StoredName is null ? "?" : proxied ? CloudProxy.Apply(gameAppId, p.D.StoredName) : p.D.StoredName;
            Console.WriteLine($"  {p.FileName} -> {shown} @ {p.D.StorageAppId}  [{p.D.Reason}]");
        }

        if (dry)
        {
            Console.WriteLine("dry-run complete (use --force to execute) - registry untouched");
            return 0;
        }

        if (lane == "client")
        {
            return await ClientLaneParkAsync(steam, uid, registry, plans, today,
                Arg(args, "--wait-sec") is { } wsv && int.TryParse(wsv, out var wv) ? wv : 25,
                viaConsole: Has(args, "--console"));
        }

        if (lane == "stage")
        {
            // ---- stage lane: local drop only, no upload, no login ----
            // the running Steam client (any account) will sync the bucket on its own tick;
            // posture is "local" until a client sync confirms the upload.
            var injector = new LocalInjectEngine();
            var userDataRoot = Path.Combine(steam, "userdata", uid.ToString());
            foreach (var group in plans.GroupBy(p => p.D.StorageAppId!.Value))
            {
                var slot = group.Key;
                var slotDir = Path.Combine(userDataRoot, slot.ToString(), "remote");
                Directory.CreateDirectory(slotDir);
                foreach (var p in group)
                    await File.WriteAllBytesAsync(Path.Combine(slotDir, p.D.StoredName!), p.Tagged);
                injector.RegenerateVdf(Path.Combine(userDataRoot, slot.ToString()));
                Console.WriteLine($"  [{slot}] staged {group.Count()} file(s) locally (no upload; client syncs on its own)");
            }
            var stageBarcode = $"{gameAppId}{Barcode.Sep}{uid}{Barcode.Sep}{today:ddMMyyyy}";
            foreach (var p in plans)
                registry.Upsert(GameSlot.New(gameAppId, p.D.StorageAppId!.Value, p.D.StoredName!, p.FileName, p.Tagged.Length, stageBarcode)
                    .WithPosture("local"));
            registry.Save();
            Console.WriteLine("stage lane done - verify later with 'client sync <appid>' once that account signs in");
            return 0;
        }

        // ---- RPC lane: logon-based upload (QR / credentials / anonymous) ----
        var verify = Has(args, "--verify") || config.VerifyAfterPark;
        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);
        var proxyLane = proxied ? new CloudProxyLane(rpc, gameAppId, proxyAppId) : null;
        var okCount = 0;
        foreach (var p in plans)
        {
            var res = proxyLane is null
                ? await rpc.UploadAsync(p.D.StorageAppId!.Value, p.D.StoredName!, p.Tagged)
                : await proxyLane.UploadAsync(p.D.StoredName!, p.Tagged);
            if (res == SteamKit2.EResult.OK)
            {
                var payloadBarcode = $"{gameAppId}{Barcode.Sep}{uid}{Barcode.Sep}{today:ddMMyyyy}";
                var wireName = proxied ? CloudProxy.Apply(gameAppId, p.D.StoredName!) : p.D.StoredName!;

                // verify-after-park: re-enumerate and compare the on-wire sha against what we sent
                var verif = "unverified";
                if (verify)
                {
                    var list = await rpc.EnumerateAsync(p.D.StorageAppId!.Value);
                    var match = ProbesShaMatch(list, wireName, p.Tagged);
                    verif = match ? "verified" : "MISMATCH";
                    okCount += match ? 1 : 0;
                }
                else
                {
                    okCount++;
                }

                if (verif != "MISMATCH")
                    registry.Upsert(GameSlot.New(gameAppId, p.D.StorageAppId!.Value, wireName, p.FileName, p.Tagged.Length, payloadBarcode)
                        .WithPosture(proxied ? "proxied" : "real").WithVerify(verif == "verified"));
                Console.WriteLine($"  {p.FileName}: {res} -> {(proxied ? CloudProxy.Apply(gameAppId, p.D.StoredName!) : p.D.StoredName)} @ {p.D.StorageAppId} [{verif}]");
            }
            else
            {
                Console.WriteLine($"  {p.FileName}: {res} -> {p.D.StoredName} @ {p.D.StorageAppId} [failed]");
            }
        }
        registry.Save();
        Console.WriteLine($"{okCount}/{plans.Count} parked; registry updated");
        return 0;
    }

    /// <summary>True when the remote listing reports a file with the same name whose sha matches what we uploaded.</summary>
    private static bool ProbesShaMatch(List<CloudFileEntry> files, string wireName, byte[] sent)
    {
        var shaHex = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(sent));
        foreach (var f in files)
        {
            if (!f.FileName.Equals(wireName, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(f.FileSha)) return false;
            return f.FileSha.Equals(shaHex, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>
    /// CLIENT lane: drop tagged files into the slot buckets locally, force
    /// cloud_sync_up through the RUNNING Steam Console (the official client does the
    /// upload - real UFS or CloudRedirect local, whichever posture the app has),
    /// read the verdict from cloud_log.txt. Denied slots are excluded from the pool
    /// immediately; every confirmed slot records where its upload actually landed.
    /// No SCT credentials are ever needed.
    /// </summary>
    private static async Task<int> ClientLaneParkAsync(string steam, uint uid, SctRegistry registry,
        List<(string FileName, ParkingDecision D, byte[] Tagged)> plans,
        DateOnly today, int waitSec, bool viaConsole = false)
    {
        var injector = new LocalInjectEngine();
        var bySlot = plans.GroupBy(p => p.D.StorageAppId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var userDataRoot = Path.Combine(steam, "userdata", uid.ToString());
        var error = 0;

        foreach (var (slot, slotPlans) in bySlot)
        {
            var slotDir = Path.Combine(userDataRoot, slot.ToString(), "remote");
            Directory.CreateDirectory(slotDir);
            foreach (var p in slotPlans)
                await File.WriteAllBytesAsync(Path.Combine(slotDir, p.D.StoredName!), p.Tagged);
            injector.RegenerateVdf(Path.Combine(userDataRoot, slot.ToString()));

            Console.WriteLine($"  [{slot}] staged {slotPlans.Count} file(s); forcing client sync via console...");
            var (verdict, line, consoleAvailable) = await PushSyncViaConsoleAsync(steam, slot, waitSec, useConsole: viaConsole);
            Console.WriteLine($"  [{slot}] {verdict}" + (line is null ? "" : $"  <{line.Trim()}>"));

            switch (verdict)
            {
                case CloudVerdict.Success:
                {
                    registry.PoolProbes[slot] = "VerifiedWritable";
                    var posture = SteamLocator.SyncPosture(steam, slot);
                    foreach (var p in slotPlans)
                    {
                        var game = ReadGameFromTrailer(p.Tagged);
                        if (game is null) continue;
                        var payload = $"{game}{Barcode.Sep}{uid}{Barcode.Sep}{today:ddMMyyyy}";
                        registry.Upsert(GameSlot.New(game.Value, slot, p.D.StoredName!, p.FileName, p.Tagged.Length, payload)
                            .WithPosture(posture));
                    }
                    Console.WriteLine($"  [{slot}] upload confirmed - lands in {(posture == "real" ? "Valve UFS (real cloud)" : posture == "provider" ? "CloudRedirect folder provider" : "OST redirected bucket")}");
                    break;
                }
                case CloudVerdict.Denied:
                    registry.PoolProbes[slot] = "Denied";
                    foreach (var p in slotPlans)
                    {
                        var staged = Path.Combine(slotDir, p.D.StoredName!);
                        if (File.Exists(staged)) File.Delete(staged);
                    }
                    injector.RegenerateVdf(Path.Combine(userDataRoot, slot.ToString()));
                    Console.WriteLine($"  [{slot}] slot excluded (account not entitled for this app); staged files removed");
                    error++;
                    break;
                default:
                    Console.WriteLine($"  [{slot}] no verdict yet - files stay staged locally; re-push with 'client sync {slot}' later");
                    PrintNoVerdictGuidance(steam, slot, consoleAvailable);
                    break;
            }

            registry.Save();
        }

        Console.WriteLine(error == 0
            ? "client-lane park complete - verify with 'scan' after the client syncs"
            : $"{error} slot(s) denied by the server - excluded from the pool");
        return error == 0 ? 0 : 1;
    }

    private static uint? ReadGameFromTrailer(byte[] tagged)
    {
        var start = Math.Max(0, tagged.Length - Barcode.TailWindowBytes);
        if (!Barcode.TryDecodeTail(tagged.AsSpan(start), out var payload, out _)) return null;
        var (game, _, _) = Barcode.Parse(payload);
        return game;
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
        catch
        {
            return null;
        }
    }

    private static async Task<int> UnparkCmd(string[] args, AppConfig config)
    {
        if (args.Length < 3 || !uint.TryParse(args[1], out var storageAppId))
        {
            Console.WriteLine("usage: unpark <storageAppId> <remoteName> [outdir]");
            return 1;
        }

        var name = args[2];

        // proxied saves come off the wire as sls-<game>/<name>; the registry knows
        // them by that prefixed name, but the file on disk should get its real name.
        var displayName = name;
        if (CloudProxy.TryStrip(name, out var proxiedGame, out var stripped))
        {
            Console.WriteLine($"note: proxied save of game {proxiedGame} - will store as {stripped}");
            displayName = stripped;
        }

        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);

        var tagged = await rpc.DownloadAsync(storageAppId, name);
        if (tagged is null)
        {
            Console.WriteLine("download failed");
            return 1;
        }
        if (!Barcode.TryDecodeTail(ReadTailBytes(tagged), out var payload, out var trailerLen))
        {
            Console.WriteLine($"no barcode trailer in {name} - saving as-is");
            trailerLen = 0;
        }
        else
        {
            var (game, _, _) = Barcode.Parse(payload);
            Console.WriteLine($"barcode: {payload} (game {game})");
        }

        var clean = trailerLen > 0 ? Barcode.StripTrailer(tagged, trailerLen) : tagged;
        var outDir = args.Length > 3 ? args[3] : Path.Combine("unparked", storageAppId.ToString());
        Directory.CreateDirectory(outDir);

        // registry knows the true original name even for stealth/hashed stored names
        var registry = SctRegistry.Load();
        var slot = registry.FindByStoredName(storageAppId, name);
        var (src, orig) = Ferry.UnparkName(name);
        var outFile = Path.Combine(outDir, src == 0 ? displayName : orig);
        if (slot is { OriginalName.Length: > 0 } && Path.GetFileName(outFile) != slot.OriginalName)
            outFile = Path.Combine(outDir, slot.OriginalName);
        await File.WriteAllBytesAsync(outFile, clean);
        Console.WriteLine($"{clean.Length}b -> {outFile}");

        registry.Remove(storageAppId, name);
        registry.Save();
        return 0;
    }

    private static async Task<int> RestoreCmd(string[] args, AppConfig config)
    {
        if (args.Length < 2 || !uint.TryParse(args[1], out var gameAppId))
        {
            Console.WriteLine("usage: restore <gameAppId> [--uid <id3>] [--force] [--out <dir>] [--json]");
            return 1;
        }

        var force = Has(args, "--force");
        var asJson = Has(args, "--json");
        var steam = ResolveSteam(config);
        var uid = ResolveUid(args, config, steam);
        if (uid == 0)
        {
            Console.WriteLine("no account - pass --uid <id3> or run with Steam logged in");
            return 1;
        }

        var slots = SlotsForGame(steam, config, gameAppId);
        if (slots.Count == 0)
        {
            Console.WriteLine($"nothing parked for game {gameAppId} (nothing in registry and nothing tagged in userdata)");
            return 1;
        }

        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);

        var results = new List<RestoreResult>();
        var gameRemoteRoot = Arg(args, "--out") is { } outDir
            ? Path.Combine(outDir, gameAppId.ToString(), "remote")
            : Path.Combine(steam, "userdata", uid.ToString(), gameAppId.ToString(), "remote");
        var injector = new LocalInjectEngine();

        foreach (var slot in slots.OrderBy(s => s.StorageAppId).ThenBy(s => s.StoredName))
        {
            var wireName = slot.StoredName;
            var origName = slot.OriginalName.Length > 0
                ? slot.OriginalName
                : Ferry.UnparkName(wireName).OriginalName;

            byte[]? tagged;
            if (CloudProxy.TryStrip(wireName, out var proxiedGame, out var stripped) && config.ResolveProxy(proxiedGame) != 0)
                tagged = await new CloudProxyLane(rpc, proxiedGame, slot.StorageAppId).DownloadAsync(stripped);
            else
                tagged = await rpc.DownloadAsync(slot.StorageAppId, wireName);

            if (tagged is null)
            {
                results.Add(new RestoreResult(slot.StorageAppId, wireName, origName, "download-failed", null, null));
                Console.WriteLine($"  [{slot.StorageAppId}] {wireName}: DOWNLOAD FAILED");
                continue;
            }

            var clean = tagged;
            if (Barcode.TryDecodeTail(ReadTailBytes(tagged), out var payload, out var trailerLen))
                clean = Barcode.StripTrailer(tagged, trailerLen);
            else
                Console.WriteLine($"  [{slot.StorageAppId}] {wireName}: no SCT barcode trailer ({tagged.Length}b) - restoring as-is");

            var safeName = PathSanitizer.SanitizeFileName(origName);
            if (safeName is null)
            {
                results.Add(new RestoreResult(slot.StorageAppId, wireName, origName, "rejected", null, "filename failed path traversal check"));
                Console.WriteLine($"  [{slot.StorageAppId}] {wireName}: REJECTED - filename '{origName}' failed path traversal check");
                continue;
            }
            origName = safeName;

            Directory.CreateDirectory(gameRemoteRoot);
            var dest = Path.Combine(gameRemoteRoot, origName);
            PathSanitizer.ResolveInside(gameRemoteRoot, dest);
            var status = "ok";
            var note = "";
            var wrote = false;
            if (File.Exists(dest))
            {
                var localSha = Sha1(File.ReadAllBytes(dest));
                var remoteSha = Sha1(clean);
                if (localSha == remoteSha)
                {
                    note = "already matches the parked save (up to date)";
                }
                else if (!force)
                {
                    status = "refused";
                    note = "local differs from the parked save (use --force to overwrite)";
                }
                else
                {
                    await File.WriteAllBytesAsync(dest, clean);
                    wrote = true;
                    note = "overwritten (--force)";
                }
            }
            else
            {
                await File.WriteAllBytesAsync(dest, clean);
                wrote = true;
                note = $"{clean.Length}b restored";
            }

            if (wrote)
                injector.RegenerateVdf(Path.Combine(steam, "userdata", uid.ToString(), gameAppId.ToString()));
            results.Add(new RestoreResult(slot.StorageAppId, wireName, origName, status, clean.Length, note));
            Console.WriteLine($"  [{slot.StorageAppId}] {wireName}: {status} {note}");
        }

        var ok = results.Count(r => r.Status == "ok");
        if (asJson)
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        else
            Console.WriteLine($"{ok}/{results.Count} restored; registry unchanged (the parked copies stay in the cloud)");
        return ok == results.Count ? 0 : 1;
    }

    private sealed record RestoreResult(uint StorageAppId, string StoredName, string OriginalName, string Status, int? Bytes, string? Note);

    /// <summary>Registry slots for a game; falls back to a full userdata tail-scan so restore works from anywhere.</summary>
    private static List<GameSlot> SlotsForGame(string steam, AppConfig config, uint gameAppId)
    {
        var registry = SctRegistry.Load();
        var slots = registry.Slots.Where(s => s.GameAppId == gameAppId).ToList();
        if (slots.Count == 0)
        {
            Console.WriteLine($"no registry slots for {gameAppId} - tail-scanning userdata for parked files...");
            var scanned = PoolScanner.RebuildRegistry(steam);
            slots = scanned.Slots.Where(s => s.GameAppId == gameAppId).ToList();
        }
        return slots;
    }

    /// <summary>
    /// check [<gameAppId>] [--uid <id3>] [--json] - compares each parked save in
    /// the cloud against the copy currently sitting in the game's local bucket:
    ///   match   = local save == parked backup
    ///   diff    = local save differs (the in-game save moved on; re-park or restore)
    ///   missing = the game bucket holds nothing for this slot
    /// </summary>
    private static async Task<int> CheckCmd(string[] args, AppConfig config)
    {
        var asJson = Has(args, "--json");
        var steam = ResolveSteam(config);
        var uid = ResolveUid(args, config, steam);
        var registered = SctRegistry.Load().Slots;
        var games = args.Length > 1 && uint.TryParse(args[1], out var g)
            ? new List<uint> { g }
            : registered.Select(s => s.GameAppId).Distinct().OrderBy(x => x).ToList();

        if (games.Count == 0 || registered.Count == 0)
        {
            Console.WriteLine(asJson ? "[]" : "registry empty - nothing to check (park something first)");
            return 0;
        }

        await using var session = await ConnectSessionAsync();
        var rpc = new CloudRpcClient(session);
        var checks = new List<CheckResult>(games.Count);

        foreach (var game in games)
        {
            var slots = registered.Where(s => s.GameAppId == game).ToList();
            var localRoot = Path.Combine(steam, "userdata", uid.ToString(), game.ToString(), "remote");
            foreach (var slot in slots)
            {
                var wireName = slot.StoredName;
                var origName = slot.OriginalName.Length > 0
                    ? slot.OriginalName
                    : Ferry.UnparkName(wireName).OriginalName;

                var tagged = CloudProxy.TryStrip(wireName, out var proxiedGame, out var stripped) && config.ResolveProxy(proxiedGame) != 0
                    ? await new CloudProxyLane(rpc, proxiedGame, slot.StorageAppId).DownloadAsync(stripped)
                    : await rpc.DownloadAsync(slot.StorageAppId, wireName);

                var remoteSha = tagged is null ? null : Sha1(StripTrailerIfAny(tagged));
                var localPath = Path.Combine(localRoot, origName);
                var localSha = File.Exists(localPath) ? Sha1(File.ReadAllBytes(localPath)) : null;

                var state = remoteSha is null ? "download-failed"
                    : localSha is null ? "missing"
                    : remoteSha == localSha ? "match"
                    : "diff";
                checks.Add(new CheckResult(game, slot.StorageAppId, origName, state));
                Console.WriteLine($"  [{game}] {origName} @ {slot.StorageAppId}: {state}");
            }
        }

        if (asJson)
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(checks, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return checks.Any(c => c.State == "diff") ? 1 : 0;
    }

    private sealed record CheckResult(uint GameAppId, uint StorageAppId, string OriginalName, string State);

    private static byte[] StripTrailerIfAny(byte[] data)
    {
        if (!Barcode.TryDecodeTail(ReadTailBytes(data), out _, out var len)) return data;
        return Barcode.StripTrailer(data, len);
    }

    private static string Sha1(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        return Convert.ToHexString(sha.ComputeHash(data));
    }

    private static int RebuildCmd(string[] args, AppConfig config)
    {
        var steam = ResolveSteam(config);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var registry = PoolScanner.RebuildRegistry(steam);
        sw.Stop();
        registry.Save();
        Console.WriteLine($"rebuild: {registry.Slots.Count} slot(s) in {sw.ElapsedMilliseconds}ms (registry: {SctRegistry.DefaultPath()})");
        foreach (var s in registry.Slots.OrderBy(s => s.StorageAppId).ThenBy(s => s.StoredName))
            Console.WriteLine($"  [{s.StorageAppId}] {s.StoredName}  game={s.GameAppId}  {s.BarcodePayload}");
        return 0;
    }

    private static int BarcodeCmd(string[] args, AppConfig config)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: barcode <file> | barcode make <payload>");
            return 1;
        }

        if (args[1] == "make")
        {
            var makePayload = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "588650|1201110076|09082026";
            Console.WriteLine($"payload: {makePayload}");
            foreach (var line in Barcode.RenderBarcode(makePayload))
                Console.WriteLine(line);
            return 0;
        }

        var path = args[1];
        if (!File.Exists(path))
        {
            Console.WriteLine($"no such file: {path}");
            return 1;
        }
        var info = new FileInfo(path);
        var tail = ReadTail(path, Math.Min(info.Length, Barcode.TailWindowBytes));
        if (!Barcode.TryDecodeTail(tail, out var payload, out var trailerLen))
        {
            Console.WriteLine($"{path}: no SCT barcode trailer");
            return 1;
        }
        var (game, uid, date) = Barcode.Parse(payload);
        Console.WriteLine($"{path} ({info.Length}b):");
        Console.WriteLine($"  trailer    : {trailerLen}b at end (magic {Barcode.Magic})");
        Console.WriteLine($"  payload    : {payload}");
        Console.WriteLine($"  game appid : {game}");
        Console.WriteLine($"  user id3   : {uid}");
        Console.WriteLine($"  tagged on  : {date:dd/MM/yyyy}");
        Console.WriteLine("  visual:");
        foreach (var line in Barcode.RenderBarcode(payload))
            Console.WriteLine("    " + line);
        return 0;
    }

    private static byte[] ReadTailBytes(byte[] data)
        => data.Length > Barcode.TailWindowBytes ? data[^Barcode.TailWindowBytes..] : data;

    private static byte[] ReadTail(string path, long count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(-count, SeekOrigin.End);
        var buf = new byte[count];
        fs.ReadExactly(buf);
        return buf;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool Has(string[] args, string name) => args.Contains(name);

    /// <summary>--posture real,provider,redirected | any -> explicit filter set; null = no filter.</summary>
    private static HashSet<string>? ParsePostureFilter(string? raw)
    {
        if (raw is null) return null;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .ToHashSet();
        if (parts.Count == 0 || parts.Contains("any")) return null;
        return parts;
    }

    private static uint ResolveUid(string[] args, AppConfig config, string steam)
    {
        if (Arg(args, "--uid") is { } u && uint.TryParse(u, out var id)) return id;
        return SteamLocator.GetActiveAccount(steam)?.AccountId ?? 0;
    }

    /// <summary>
    /// doctor - self-test: verifies Steam install, accounts, registry integrity,
    /// pool status, and optionally cloud connectivity. Outputs structured diagnostics.
    /// </summary>
    private static async Task<int> DoctorCmd(string[] args, AppConfig config)
    {
        var checks = new List<(string Name, bool Pass, string Detail)>();
        var verbose = Has(args, "--verbose") || Has(args, "-v");

        // 1. Steam install
        var steam = SteamLocator.DetectInstallPath();
        var steamOk = steam is not null && Directory.Exists(steam);
        checks.Add(("steam-install", steamOk, steam ?? "(not found)"));

        // 2. Steam running
        var running = SteamLocator.IsRunning();
        checks.Add(("steam-running", running, running ? "yes" : "no (client lane unavailable)"));

        // 3. Steam path override
        var hasOverride = config.SteamPathOverride is not null;
        checks.Add(("steam-override", true, hasOverride ? config.SteamPathOverride! : "(none)"));

        // 4. Config file
        var cfgPath = AppConfig.ResolveDefaultPath();
        var cfgExists = File.Exists(cfgPath);
        checks.Add(("config-file", cfgExists, cfgPath));

        // 5. Registry file
        var regPath = SctRegistry.DefaultPath();
        var regExists = File.Exists(regPath);
        checks.Add(("registry-file", regExists, regPath));

        if (regExists && steam is not null)
        {
            var reg = SctRegistry.Load();
            checks.Add(("registry-slots", reg.Slots.Count > 0, $"{reg.Slots.Count} slot(s)"));
            checks.Add(("registry-probes", reg.PoolProbes.Count > 0, $"{reg.PoolProbes.Count} probe(s)"));
            checks.Add(("registry-discovered", reg.Discovered.Count > 0, $"{reg.Discovered.Count} container(s)"));

            // check for stale slots (storage appid not in discovered containers or pool)
            var staleSlots = reg.Slots.Where(s =>
                PoolDb.Find(s.StorageAppId) is null &&
                reg.Discovered.All(c => c.AppId != s.StorageAppId)).ToList();
            if (staleSlots.Count > 0)
                checks.Add(("stale-slots", false, $"{staleSlots.Count} slot(s) reference non-existent storage appids"));
        }

        // 6. Guard list
        checks.Add(("guards", true, $"{config.GuardedAppIds.Count} guarded"));

        // 7. Proxy map
        checks.Add(("proxies", true, $"{config.CloudProxies.Count} mapping(s)"));

        // 8. Pool pool
        var usable = PoolDb.Usable().Count();
        checks.Add(("pool-usable", usable > 0, $"{usable} usable slot(s)"));

        // 9. Active account (if Steam is running)
        if (running && steam is not null)
        {
            var active = SteamLocator.GetActiveAccount(steam);
            checks.Add(("active-account", active is not null, active is not null
                ? $"{active!.AccountId} ({active.DisplayName ?? "?"})" : "(none)"));
        }

        // 10. Cloud connectivity (optional -- with --cloud flag)
        if (Has(args, "--cloud"))
        {
            try
            {
                await using var session = await ConnectSessionAsync();
                var rpc = new CloudRpcClient(session);
                var ok = await rpc.TestConnectionAsync();
                checks.Add(("cloud-rpc", ok, ok ? "connected" : "failed"));
            }
            catch (Exception ex)
            {
                checks.Add(("cloud-rpc", false, ex.Message));
            }
        }

        // 11. Path traversal sanity check
        checks.Add(("path-sanitizer", PathSanitizer.IsSafeFileName("test_save.dat"), "basic file safety check"));
        checks.Add(("path-sanitizer-reject", !PathSanitizer.IsSafeFileName("../etc/passwd"), "traversal rejection check"));

        // output
        var pass = checks.Count(c => c.Pass);
        var fail = checks.Count(c => !c.Pass);
        foreach (var (name, ok, detail) in checks)
        {
            var icon = ok ? "OK" : "FAIL";
            if (verbose || !ok)
                Console.WriteLine($"  [{icon,-4}] {name}: {detail}");
        }

        Console.WriteLine($"\n{pass}/{checks.Count} checks passed" + (fail > 0 ? $", {fail} FAILED" : ""));
        if (fail > 0)
            Console.WriteLine("Run 'doctor --verbose' for full details, or fix the failing checks above.");

        return fail == 0 ? 0 : 1;
    }

    private static int Help()
    {
        Console.WriteLine("""
            SteamCloudTamper - Steam cloud save manager (official + GreenLuma/SLS-era buckets)

            detect                    locate Steam + accounts + libraries
            scan                      audit local userdata buckets (per account/app)
            remote-list --app <id> [--proxy <appid>]   list files in a cloud bucket (proxy = list the game's sls-<id>/ namespace inside the proxy bucket)
            probe <appid...>          check what Valve allows: enumerate / upload / delete
            wipe <appid> <file> [--blank] [--force]      delete or blank one cloud file
            wipe-all <appid> [--blank] [--force]          wipe entire bucket
            guards add|rm|ls <appid>   maintain never-touch list (persisted)
            inject <uid3> <appid> <file> [remote-name]    userdata drop + remotecache.vdf regen
            lock/unlock <uid3> <appid>   isolate a bucket locally: read-only file blocks Steam re-creation
            relocate/unrelocate <uid3> <appid>  junction-isolate bucket into %LOCALAPPDATA%\SCT\stash

            web lane (needs SCT_COOKIE):  web ls | files <appid> | dl <appid> <file>
            ferry (park saves into owned AppID 480 bucket):
                ferry ls | upload <local-file> [name] | dl <name> [outfile]

            parking brain (anti-ban: private cloud saves of real apps only, never public flooding):
                pool list | refresh | probe [--uid <id3>] [--force] [--wait-sec N]
                                      probe = one-private-file writability check via console lane
                                      (forced cloud_sync_up through the RUNNING Steam client);
                                      verdicts saved to the registry
                pool discover [--net] = sweep machine for container AppIDs (PoolDb + userdata +
                                      OST lua hooks + CloudRedirect host + SLS/GreenLuma) and save
                                      the snapshot to the registry; --net flags AutoClouded from cloud_log
                appid proxy (unowned saves ride an OWNED bucket under sls-<game>/ prefixes,
                                      no client hook needed - the rpc lane writes the proxy appid itself):
                proxy status | set <game> <proxy> | rm <game> | ls
                                      map game -> owned proxy bucket; game 0 = default for all unowned
                park <gameAppId> [--proxy <appid>] ...   proxy parking is rpc-only (--lane auto falls back)
                park <gameAppId> [--uid <id3>] [--force] [--lane auto|client|rpc|stage] [--bucket <appid>]
                     [--spread N] [--copies N] [--stealth] [--wait-sec N]
                     [--allow-owned] [--posture real,provider,redirected|any]
                                      auto   = Steam up + account active -> client; else rpc
                                      client = stage locally + console cloud_sync_up (official client uploads;
                                               real UFS or CR provider per app posture; no SCT login)
                                      rpc    = SCT logs on (QR/creds) and uploads directly
                                      stage  = drop files locally only; session syncs on its own later
                                      --allow-owned = OPT-IN consent for owned-game buckets (never auto-picked
                                               without it); --posture filters the candidate universe by posture
                                      (default ranking: VerifiedWritable real > AutoClouded real > probe-candidate
                                               > provider/redirected; activation containers only once real slots run out)
                client status | sync <appid> [--down] [--console] | tell <command>
                                      console lane: force real sync of one bucket via the running client
                provider status | init [sync-dir] | ls [--uid <id3>] [--app <appid>]
                                      CloudRedirect folder provider management (SCT owns the config)
                unpark <storageAppId> <name> [outdir]   download + strip barcode
                restore <gameAppId> [--uid <id3>] [--force] [--out <dir>] [--json]
                                      pull a parked save back into the game's local bucket
                                      (registry lookup; falls back to a userdata tail-scan)
                check [<gameAppId>] [--uid <id3>] [--json]
                                      compare every parked save vs the local game-bucket copy
                                      (match / diff / missing); exit code 1 on any diff
                rebuild                    tail-scan userdata -> registry.json
                barcode <file> | barcode make <payload>   show/render barcode trailers
                doctor [--cloud] [--verbose]             self-test: verify install, registry, pool, connectivity
            park flags: --verify re-enumerates + sha-compares each upload after parking
                        (set config VerifyAfterPark=true to always verify); --console re-enables
                        the Steam Console push for client-lane syncs (opt-in on newer builds)
            pool probe --lane rpc         probe writability via a real SCT logon instead of the
                                          running client's tick; pool probe/client sync accept --console
            config:     SCT_CONFIG=<path> overrides the config file location
                        (default steamcloudtamper.json next to the process)
            registry: {SctRegistry.DefaultPath()}

            auth: anonymous by default; env SCT_USER/SCT_PASS or SCT_AUTH_MODE=qr for account ops.
                  IMPORTANT: the client lane needs no auth - it rides the running Steam session.
            """);
        return 0;
    }
}