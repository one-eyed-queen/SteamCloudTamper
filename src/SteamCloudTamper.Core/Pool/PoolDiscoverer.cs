using SteamCloudTamper.Core.Steam;

namespace SteamCloudTamper.Core.Pool;

/// <summary>Which host found / drives this container.</summary>
public enum ContainerSource
{
    PoolDb,
    UserData,
    OstLua,
    OstToml,
    Sls,        // Goldberg-style steam_settings (SLS = "Steam Language Selector" convention, same config dir)
    GreenLuma,
    Proxy,      // appid proxy container (SCT CloudProxies map)
}

/// <summary>What the account's relationship to this container is.</summary>
public enum ContainerKind
{
    Owned,      // a game actually in the account's library (userdata bucket, not a pool entry)
    Free,       // free game - entitled to everyone
    Hidden,     // hidden/dev/Valve tool - universally entitled
    ModHost,    // free mod/tool host
    Activation, // only "owned" because an activation tool (OST lua / SLS / GreenLuma) says so
}

/// <summary>
/// One "smart appid container": an AppID SCT may park unowned-game saves into.
/// The universe is built by <see cref="PoolDiscoverer"/> from PoolDb + userdata
/// buckets + activation-tool hooks, then persisted in the registry.
/// </summary>
public sealed record ContainerInfo(
    uint AppId,
    string? Name,
    ContainerKind Kind,
    ContainerSource Source,
    string Posture,    // real | provider | redirected | local
    bool AutoClouded,  // client's own cloud_log shows AutoCloud for this app
    string? Note = null)
{
    /// <summary>Only Valve-touching, client-synced containers are worth real storage.</summary>
    public bool IsRealCandidate => Posture is "real" or "provider";
}

/// <summary>
/// Sweeps the machine and builds the container universe: curated PoolDb slots,
/// real userdata buckets, OST Lua addappid hooks, CloudRedirect host, SLS/Goldberg
/// steam_settings and GreenLuma whitelists. Every container carries its posture
/// (real / provider / redirected) so parking can rank them.
/// </summary>
public static class PoolDiscoverer
{
    /// <summary>
    /// Discover all containers for this Steam install.
    /// <paramref name="autoClouded"/> lets callers tag containers the client itself
    /// AutoClouds (read from cloud_log) - pass null when unavailable.
    /// </summary>
    public static List<ContainerInfo> Discover(
        string steamPath,
        Func<uint, bool>? autoClouded = null,
        IReadOnlyDictionary<uint, uint>? proxies = null)
    {
        var byApp = new Dictionary<uint, ContainerInfo>();

        void Add(ContainerInfo c)
        {
            if (!byApp.TryGetValue(c.AppId, out var old))
            {
                byApp[c.AppId] = c;
                return;
            }
            // a redirected userdata bucket is really an activation container - the richer row wins
            // (yes we fought about this ordering for an hour. no we dont remeber why the hour
            //  was needed. the tests pass, thats what counts.)
            if (old.Source == ContainerSource.UserData && old.Posture == "redirected" && c.Source != ContainerSource.UserData)
            {
                byApp[c.AppId] = c;
                return;
            }
            // curated pool info beats a bare userdata row
            if (old.Source == ContainerSource.UserData && c.Source != ContainerSource.UserData)
            {
                byApp[c.AppId] = c;
                return;
            }
            byApp[c.AppId] = c;
        }

        // ---- real userdata buckets -------------------------------------------
        var userdata = Path.Combine(steamPath, "userdata");
        if (Directory.Exists(userdata))
        {
            foreach (var userDir in Directory.EnumerateDirectories(userdata))
            {
                var id3 = Path.GetFileName(userDir);
                if (!uint.TryParse(id3, out _)) continue;
                foreach (var appDir in Directory.EnumerateDirectories(userDir))
                {
                    if (!uint.TryParse(Path.GetFileName(appDir), out var appId)) continue;
                    var hasRemote = Directory.Exists(Path.Combine(appDir, "remote"));
                    var hasCache = File.Exists(Path.Combine(appDir, "remotecache.vdf"));
                    if (!hasRemote && !hasCache) continue;

                    var posture = SteamLocator.SyncPosture(steamPath, appId);
                    Add(new ContainerInfo(
                        appId, null, ContainerKind.Owned, ContainerSource.UserData,
                        posture, autoClouded?.Invoke(appId) ?? false,
                        $"userdata/{id3} bucket"));
                }
            }
        }

        // ---- PoolDb curated slots (richer than userdata rows) -----------------
        foreach (var p in PoolDb.DefaultPool)
        {
            var kind = p.Tier switch
            {
                SlotTier.HiddenDev => ContainerKind.Hidden,
                SlotTier.OwnedReserved => ContainerKind.Owned,
                _ => p.Category == "ModHost" ? ContainerKind.ModHost : ContainerKind.Free,
            };
            var posture = SteamLocator.SyncPosture(steamPath, p.AppId);
            Add(new ContainerInfo(
                p.AppId, p.Name, kind, ContainerSource.PoolDb, posture,
                autoClouded?.Invoke(p.AppId) ?? false, p.Category));
        }

        // ---- OST Lua addappid hooks ------------------------------------------
        foreach (var hooked in SteamLocator.ListOstHookedAppIds(steamPath))
        {
            var posture = SteamLocator.SyncPosture(steamPath, hooked);
            Add(new ContainerInfo(
                hooked, null, ContainerKind.Activation, ContainerSource.OstLua,
                posture, autoClouded?.Invoke(hooked) ?? false,
                "OST lua addappid hook - never touches Valve"));
        }

        // ---- CloudRedirect host (opensteamtool.toml) --------------------------
        if (SteamLocator.IsCloudRedirectLoaded(steamPath))
        {
            Add(new ContainerInfo(
                0, "CloudRedirect host", ContainerKind.Activation, ContainerSource.OstToml,
                "provider", false,
                "opensteamtool.toml [cloud] - uploads for hooked apps land in the folder provider"));
        }

        // ---- SLS / Goldberg steam_settings ------------------------------------
        foreach (var (appId, gameDir) in SteamLocator.FindSteamSettings(steamPath))
        {
            Add(new ContainerInfo(
                appId, Path.GetFileName(gameDir), ContainerKind.Activation, ContainerSource.Sls,
                "redirected", autoClouded?.Invoke(appId) ?? false,
                "steam_settings - emulated entitlement"));
        }

        // ---- GreenLuma whitelists ---------------------------------------------
        foreach (var appId in SteamLocator.FindGreenLumaAppIds(steamPath))
        {
            Add(new ContainerInfo(
                appId, null, ContainerKind.Activation, ContainerSource.GreenLuma,
                "redirected", autoClouded?.Invoke(appId) ?? false,
                "GreenLuma whitelist - emulated entitlement"));
        }

        // ---- AppID proxy containers (CloudProxies map) -----------------------
        if (proxies is { Count: > 0 })
        {
            var proxyNames = new Dictionary<uint, List<uint>>();
            foreach (var (game, proxyAppId) in proxies)
            {
                if (game == 0) continue; // default proxy is not a container of its own
                if (!proxyNames.TryGetValue(proxyAppId, out var games))
                    proxyNames[proxyAppId] = games = [];
                games.Add(game);
            }
            foreach (var (proxyAppId, games) in proxyNames)
            {
                var pool = PoolDb.Find(proxyAppId);
                var kind = pool is null ? ContainerKind.Owned : pool.Tier == SlotTier.HiddenDev ? ContainerKind.Hidden
                    : pool.Tier == SlotTier.OwnedReserved ? ContainerKind.Owned
                    : pool.Category == "ModHost" ? ContainerKind.ModHost : ContainerKind.Free;
                Add(new ContainerInfo(
                    proxyAppId,
                    pool?.Name ?? "proxy container",
                    kind, ContainerSource.Proxy, "proxied",
                    autoClouded?.Invoke(proxyAppId) ?? false,
                    $"appid proxy for: {string.Join(", ", games)}"));
            }
        }

        return [.. byApp.Values
            .OrderBy(c => c.AppId == 0 ? 1 : 0)
            .ThenBy(c => c.AppId)];
    }
}
