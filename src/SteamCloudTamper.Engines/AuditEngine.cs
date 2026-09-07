using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Steam;

namespace SteamCloudTamper.Engines;

public sealed class AuditEngine(AppConfig config)
{
    public List<Bucket> ListLocal(string steamPath, uint? accountId = null, IReadOnlySet<uint>? owned = null)
    {
        owned ??= config.GetOwnedSet();
        var accounts = SteamLocator.ListAccounts(steamPath);
        if (accountId is not null)
        {
            var match = accounts.FirstOrDefault(a => a.AccountId == accountId);
            accounts = match is null
                ? [new SteamAccount(accountId.Value, SteamAccount.SteamIdFor(accountId.Value), null)]
                : [match];
        }

        var buckets = new List<Bucket>();
        foreach (var account in accounts)
        {
            foreach (var state in SteamLocator.ScanLocalApps(steamPath, account.AccountId, owned))
            {
                buckets.Add(BuildBucket(state, owned));
            }
        }

        return buckets;
    }

    public List<Bucket> ListOneAccount(string steamPath, uint accountId, IReadOnlySet<uint>? owned = null)
    {
        owned ??= config.GetOwnedSet();
        var states = SteamLocator.ScanLocalApps(steamPath, accountId, owned);
        return states.Select(s => BuildBucket(s, owned)).ToList();
    }

    private Bucket BuildBucket(LocalAppState state, IReadOnlySet<uint> owned)
    {
        var era = EraDetector.Classify(state.AppId, owned, config.Hints);
        var note = era switch
        {
            Era.AnonymousDump760 => "760-era: anonymous public dump, cloud writes were rerouted here",
            Era.Owned => "owned on this account",
            Era.GreenLumaRealAppId => "not owned - possible GreenLuma/SLS-era cloud upload",
            Era.Emulated => "emulator-era (client not involved)",
            Era.Client => "Steam client internal",
            _ => "unknown era",
        };

        var files = new List<CloudFileEntry>();
        if (state.RemoteDir is not null)
        {
            foreach (var f in Directory.EnumerateFiles(state.RemoteDir))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("remotecache.vdf", StringComparison.OrdinalIgnoreCase)) continue;
                long size = 0;
                try { size = new FileInfo(f).Length; } catch { }
                files.Add(new CloudFileEntry(state.AppId, name, 0, size, 0, null, null));
            }
        }

        return new Bucket(state.AppId, era, note, files);
    }

    public async Task<List<Bucket>> MergeRemoteAsync(
        CloudRpcClient rpc,
        IEnumerable<Bucket> local,
        Func<uint, bool>? shouldFetch = null,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var result = new List<Bucket>();
        foreach (var b in local)
        {
            if (shouldFetch is not null && !shouldFetch(b.AppId))
            {
                result.Add(b);
                continue;
            }

            onStatus?.Invoke($"Fetching remote list for {b.AppId}...");
            try
            {
                var remote = await rpc.EnumerateAsync(b.AppId, ct);
                if (remote.Count == 0)
                {
                    result.Add(b);
                    continue;
                }

                var merged = new List<CloudFileEntry>(b.Files);
                foreach (var rf in remote)
                {
                    if (merged.All(f => !string.Equals(f.FileName, rf.FileName, StringComparison.OrdinalIgnoreCase)))
                        merged.Add(rf);
                }

                result.Add(b with { Files = merged });
            }
            catch (Exception ex)
            {
                onStatus?.Invoke($"Remote fetch failed for {b.AppId}: {ex.Message}");
                result.Add(b);
            }
        }

        return result;
    }
}