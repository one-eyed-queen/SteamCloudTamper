using SteamCloudTamper.Core.Pool;

namespace SteamCloudTamper.Core;

public sealed record SlotCheck(
    uint StorageAppId,
    string StoredName,
    int Score,
    bool OccupiedByOtherGames,
    string Reason);

public sealed record ParkingDecision(
    bool Ok,
    uint? StorageAppId,
    string? StoredName,
    string Reason,
    IReadOnlyList<SlotCheck>? Rejected = null)
{
    public static ParkingDecision Fail(string reason, IReadOnlyList<SlotCheck>? rejected = null)
        => new(false, null, null, reason, rejected);
}

public sealed record RemoteBucketSnapshot(uint AppId, List<CloudFileEntry> Files, Quota? Quota);

public sealed record ParkFile(string Name, long Size);

/// <summary>
/// Smart parking allocator. Rules (in priority order):
///  1. Tier: hidden/dev/tool apps &gt; old free apps. Owned-game buckets are OPT-IN:
///     never picked without explicit consent (--allow-owned / TUI prompt). The
///     posture ranking (VerifiedWritable real &gt; AutoClouded real &gt; probe-candidate
///     &gt; provider/redirected) decides between equally-tiered real slots, and
///     --posture &lt;csv&gt; filters the whole candidate universe.
///  2. Anti-ban hardening: slots with a server "Denied" verdict (private pool probe) are skipped;
///     names can be hashed (--stealth); files can spread across N slots (--spread) and
///     mirror to extra slots (--copies) so no single bucket pattern is detectable.
///  3. Co-existence: a bucket already hosting other parked games is preferred.
///  4. Name collision / quota pressure -&gt; next candidate, deterministic by score then appid.
/// </summary>
public sealed class ParkingEngine
{
    private readonly HashSet<uint> _owned;

    private readonly IReadOnlyList<GameSlot> _registry;

    private readonly IReadOnlyDictionary<uint, string> _poolProbes;

    private readonly Func<uint, Task<RemoteBucketSnapshot?>>? _remoteProbe;

    private Dictionary<uint, Task<RemoteBucketSnapshot?>>? _probeCache;

    /// <summary><paramref name="remoteProbe"/> returns null when the lane is offline (offline guess mode).</summary>
    public ParkingEngine(
        HashSet<uint> owned,
        IReadOnlyList<GameSlot> registry,
        Func<uint, Task<RemoteBucketSnapshot?>>? remoteProbe = null,
        IReadOnlyDictionary<uint, string>? poolProbes = null)
    {
        _owned = owned;
        _registry = registry;
        _remoteProbe = remoteProbe;
        _poolProbes = poolProbes ?? new Dictionary<uint, string>();
    }

    public ParkingDecision Pick(uint gameAppId, string originalFileName, long sizeBytes,
        IReadOnlyList<ContainerInfo>? containers = null, bool allowOwned = false,
        IReadOnlySet<string>? postureFilter = null)
        => Plan(gameAppId, [new ParkFile(originalFileName, sizeBytes)], containers: containers,
            allowOwned: allowOwned, postureFilter: postureFilter)[0];

    /// <summary>Plans parking for a whole bucket. Returns one decision per file (copies expands it).</summary>
    public List<ParkingDecision> Plan(
        uint gameAppId,
        IReadOnlyList<ParkFile> files,
        bool stealth = false,
        int spread = 1,
        int copies = 1,
        uint? forceBucket = null,
        uint? proxyBucket = null,
        IReadOnlyList<ContainerInfo>? containers = null,
        bool allowOwned = false,
        IReadOnlySet<string>? postureFilter = null)
    {
        if (_owned.Contains(gameAppId))
            throw new InvalidOperationException($"appid {gameAppId} is in the owned list - parking an owned game is refused (use Ferry for backups). Unless you mean to proxy a game you own into a bucket you also own? No. Stop it.");

        if (files.Count == 0) return [];
        spread = Math.Max(1, spread);

        if (proxyBucket is { } prox)
        {
            // appid proxy (docs/APPID-PROXY.md): the proxy bucket IS the container.
            // no scoring, no spread, no co-existence games - every file lands there
            // under the sls-<game>/ namespace, and we refuse a proxy we don't own.
            if (!_owned.Contains(prox))
                return files.Select(f => (ParkingDecision)ParkingDecision.Fail(
                    $"proxy {prox} is not in the owned set - see 'scan'; uploads to it come back AccessDenied")).ToList();

            return files.SelectMany(f => Enumerable.Range(0, copies).Select(copy =>
                (ParkingDecision)new ParkingDecision(true, prox,
                    BuildStoredName(gameAppId, f.Name, copy, copies, stealth),
                    $"proxy bucket {prox}: rides the sls-{gameAppId}/ namespace"))).ToList();
        }
        copies = Math.Max(1, copies);

        var candidates = BuildCandidates(containers, allowOwned, postureFilter);

        // explicit bucket override (--bucket): must exist in the pool or the container
        // universe, pass consent/filter checks, and not be server-denied
        if (forceBucket is { } forced)
        {
            var forcedApp = PoolDb.Find(forced);
            ContainerInfo? forcedContainer = null;
            if (forcedApp is null && containers is not null)
            {
                forcedContainer = containers.FirstOrDefault(c => c.AppId == forced);
                if (forcedContainer is not null)
                    forcedApp = ToParkingApp(forcedContainer);
            }
            if (forcedApp is null)
                return files.Select(f => (ParkingDecision)ParkingDecision.Fail(
                    $"bucket {forced} is not in the parking pool")).ToList();
            if (forcedContainer is { Kind: ContainerKind.Owned } && !allowOwned)
                return files.Select(f => (ParkingDecision)ParkingDecision.Fail(
                    $"bucket {forced} is an owned-game bucket - pass --allow-owned to opt in")).ToList();
            if (!allowOwned && forcedApp.Tier == SlotTier.OwnedReserved)
                return files.Select(f => (ParkingDecision)ParkingDecision.Fail(
                    $"bucket {forced} is an owned-game bucket - pass --allow-owned to opt in")).ToList();
            if (forcedApp.IsBlocked)
                return files.Select(f => (ParkingDecision)ParkingDecision.Fail(
                    $"bucket {forced} is blocked in the pool ({forcedApp.Note})")).ToList();
            if (IsServerDenied(forced))
                return files.Select(f => (ParkingDecision)ParkingDecision.Fail(
                    $"bucket {forced} had a server Denied verdict - choose another (pool probe)")).ToList();
            if (!PassesPostureFilter(forcedContainer?.Posture, postureFilter))
                return files.Select(f => (ParkingDecision)ParkingDecision.Fail(
                    $"bucket {forced} posture is excluded by the --posture filter")).ToList();
            candidates = candidates.Where(c => c.App.AppId == forced).ToList();
            spread = 1;
        }

        var head = Math.Min(spread, candidates.Count);
        if (head == 0)
            return files.Select(f => (ParkingDecision)ParkingDecision.Fail(
                "pool exhausted - no usable parking slot (add --allow-owned to include owned buckets, or relax the --posture filter)")).ToList();

        // already-parked game -> keep its proven slot; never reallocate what works
        var parkedSlot = !stealth ? _registry.FirstOrDefault(s => s.GameAppId == gameAppId) : null;
        if (parkedSlot is not null && IsStorageUsable(parkedSlot.StorageAppId, allowOwned, containers))
        {
            return files.SelectMany(f => Enumerable.Range(0, copies).Select(copy =>
                (ParkingDecision)new ParkingDecision(true, parkedSlot.StorageAppId,
                    BuildStoredName(gameAppId, f.Name, copy, copies, stealth),
                    $"already parked: {parkedSlot.StorageAppId}/{parkedSlot.StoredName} (updated in place)"))).ToList();
        }

        var decisions = new List<ParkingDecision>();
        SeedRemoteProbes(candidates); // one parallel fan-out, then reads are instant
        for (var i = 0; i < files.Count; i++)
        {
            for (var copy = 0; copy < copies; copy++)
            {
                var slot = candidates[(i + copy) % head];
                decisions.Add(ChooseSlot(gameAppId, files[i], slot, stealth, copy, copies));
            }
        }
        return decisions;
    }

    /// <summary>
    /// Fires remote probes for every candidate concurrently so ChooseSlot never
    /// blocks per-slot (previously each slot awaited serially via GetAwaiter().GetResult()).
    /// </summary>
    private void SeedRemoteProbes(IReadOnlyList<(ParkingApp App, int Score)> candidates)
    {
        if (_remoteProbe is null) return;
        _probeCache ??= new Dictionary<uint, Task<RemoteBucketSnapshot?>>();
        var fresh = candidates.Where(c => !_probeCache.ContainsKey(c.App.AppId)).Select(c => c.App.AppId).ToList();
        foreach (var id in fresh)
            _probeCache[id] = Task.Run(async () => await _remoteProbe(id).ConfigureAwait(false));
        Task.WhenAll(fresh.Select(f => _probeCache[f])).GetAwaiter().GetResult();
    }

    private RemoteBucketSnapshot? GetRemoteProbeResult(uint appId)
    {
        if (_remoteProbe is null) return null;
        return _probeCache is not null && _probeCache.TryGetValue(appId, out var t)
            ? t.GetAwaiter().GetResult()
            : null;
    }

    /// <summary>
    /// The candidate universe: curated PoolDb slots (incl. OwnedReserved when
    /// consent is given) plus discovered containers that are NOT pool entries -
    /// owned userdata buckets (consent-gated) and activation-tool containers
    /// (penalized, so they only win once real slots run out). Scored by
    /// tier + age + posture/probe ranking.
    /// </summary>
    private List<(ParkingApp App, int Score)> BuildCandidates(
        IReadOnlyList<ContainerInfo>? containers, bool allowOwned, IReadOnlySet<string>? postureFilter)
    {
        var byApp = containers?
            .GroupBy(c => c.AppId)
            .ToDictionary(g => g.Key, g => g.First())
            ?? new Dictionary<uint, ContainerInfo>();

        var list = new List<(ParkingApp App, int Score)>();

        // ---- curated pool (hidden/free/tool slots, consent extends the tier cutoff) ----
        foreach (var p in PoolDb.Usable(allowOwned ? SlotTier.OwnedReserved : SlotTier.OldFree))
        {
            if (IsServerDenied(p.AppId)) continue;
            var posture = byApp.TryGetValue(p.AppId, out var c) ? c.Posture : null;
            if (!PassesPostureFilter(posture, postureFilter)) continue;
            list.Add((
                p,
                PoolScoring.TierScore(p.Tier) + PoolScoring.AgeScore(p.ReleaseYear)
                    + PoolScoring.PostureScore(posture, byApp.TryGetValue(p.AppId, out var cc) && cc.AutoClouded,
                        ProbeStateOf(p.AppId))));
        }

        if (containers is null)
            return [.. list.OrderByDescending(c => c.Score).ThenBy(c => c.App.AppId)];

        // ---- discovered containers that are not pool entries ----
        foreach (var c in containers)
        {
            if (c.AppId == 0) continue;                       // CloudRedirect host marker (no bucket)
            if (PoolDb.Find(c.AppId) is not null) continue;   // pool row already covers it
            if (IsServerDenied(c.AppId)) continue;
            if (c.Kind == ContainerKind.Owned && !allowOwned) continue;
            if (!PassesPostureFilter(c.Posture, postureFilter)) continue;

            var app = ToParkingApp(c);
            list.Add((
                app,
                PoolScoring.TierScore(app.Tier) + PoolScoring.AgeScore(app.ReleaseYear)
                    + PoolScoring.PostureScore(c.Posture, c.AutoClouded, ProbeStateOf(c.AppId))));
        }

        return [.. list.OrderByDescending(c => c.Score).ThenBy(c => c.App.AppId)];
    }

    private string? ProbeStateOf(uint appId)
        => _poolProbes.TryGetValue(appId, out var state) ? state : null;

    /// <summary>
    /// --posture filter semantics: 'any'/empty = no filter (but then redirected &
    /// co. still rank below real slots). An explicit list is authoritative for every
    /// candidate; an unknown posture (no discovered container) counts as "real" -
    /// curated PoolDb slots face Valve by design.
    /// </summary>
    private static bool PassesPostureFilter(string? posture, IReadOnlySet<string>? filter)
    {
        if (filter is null || filter.Count == 0 || filter.Contains("any")) return true;
        if (posture is null) return filter.Contains("real");
        return filter.Contains(posture);
    }

    private bool IsServerDenied(uint appId)
    {
        if (!_poolProbes.TryGetValue(appId, out var state)) return false;
        return state.Equals("Denied", StringComparison.OrdinalIgnoreCase)
            || state.Equals("Blocked", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Storage slot stays reusable across runs: pool entry, or a discovered container that passes the consent gate.</summary>
    private bool IsStorageUsable(uint appId, bool allowOwned, IReadOnlyList<ContainerInfo>? containers)
    {
        if (PoolDb.Find(appId) is { IsUsable: true }) return true;
        if (containers is null) return false;
        var c = containers.FirstOrDefault(c => c.AppId == appId);
        return c is not null && (c.Kind != ContainerKind.Owned || allowOwned);
    }

    /// <summary>Discovers into the same ParkingApp shape the pool uses; tier maps from the container kind.</summary>
    private static ParkingApp ToParkingApp(ContainerInfo c)
    {
        var tier = c.Kind switch
        {
            ContainerKind.Owned => SlotTier.OwnedReserved,
            ContainerKind.Hidden => SlotTier.HiddenDev,
            _ => SlotTier.OldFree, // Free / ModHost / Activation
        };
        return new ParkingApp(
            c.AppId,
            c.Name ?? $"app {c.AppId}",
            0,
            c.Kind != ContainerKind.Owned,
            tier,
            SlotState.Candidate,
            c.Kind.ToString(),
            c.Note ?? "");
    }

    private ParkingDecision ChooseSlot(uint gameAppId, ParkFile file,
        (ParkingApp App, int Score) slot, bool stealth, int copy, int copies)
    {
        var (app, baseScore) = slot;
        if (!app.IsUsable)
            return ParkingDecision.Fail($"{app.AppId}: slot not usable (state {app.State})");

        var storedName = BuildStoredName(gameAppId, file.Name, copy, copies, stealth);

        // registry: uploading the same stored name again = in-place update, never a second copy
        if (!stealth && copy == 0)
        {
            var existing = _registry.FirstOrDefault(s =>
                s.GameAppId == gameAppId &&
                s.StorageAppId == app.AppId &&
                s.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return new ParkingDecision(true, app.AppId, storedName,
                    $"already parked: {app.AppId}/{storedName} (updated in place)");
            }
        }

        var occupiedByOther = _registry.Any(s => s.StorageAppId == app.AppId && s.GameAppId != gameAppId);
        var coexist = PoolScoring.CoexistScore(
            _registry.Count(s => s.StorageAppId == app.AppId && s.GameAppId != gameAppId),
            _registry.Count(s => s.StorageAppId == app.AppId));

        var regCollision = _registry.Any(s =>
            s.StorageAppId == app.AppId &&
            s.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase));

        var remote = GetRemoteProbeResult(app.AppId);
        var remoteCollision = remote?.Files.Any(f =>
            f.FileName.Equals(storedName, StringComparison.OrdinalIgnoreCase)) ?? false;

        if (regCollision || remoteCollision)
            return ParkingDecision.Fail($"{app.AppId}: '{storedName}' name collision in bucket (check registry or remote first)");

        if (remote?.Quota is { } q && q.ExistingBytes + (ulong)file.Size > q.MaxBytes)
            return ParkingDecision.Fail($"{app.AppId}: quota {q.ExistingBytes}+{file.Size} over {q.MaxBytes}");

        var reason = copy > 0 && copies > 1
            ? $"mirror copy {copy + 1}/{copies} in {app.AppId} ({app.Name})"
            : occupiedByOther
                ? $"co-existence: bucket {app.AppId} already hosts other parked games"
                : $"free slot at {app.AppId} ({app.Name})";
        return new ParkingDecision(true, app.AppId, storedName,
            $"{reason} (score {baseScore + coexist})");
    }

    /// <summary>Stealth names look like native slot data (k{appid:x}{counter}{ext}); the barcode trailer still identifies the file.</summary>
    public static string BuildStoredName(uint gameAppId, string originalName, int copy, int copies, bool stealth = false)
    {
        var ext = Path.GetExtension(originalName);
        var stem = stealth
            ? $"k{gameAppId:x8}{copy:x2}"
            : Ferry.ParkName(gameAppId, Path.GetFileNameWithoutExtension(originalName));
        var suffix = copies > 1 ? $"c{copy + 1}" : "";
        return $"{stem}{suffix}{ext}";
    }
}