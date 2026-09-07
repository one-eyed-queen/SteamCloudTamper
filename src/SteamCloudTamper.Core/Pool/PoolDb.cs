namespace SteamCloudTamper.Core.Pool;

public enum SlotTier
{
    /// <summary>Hidden / dev / test apps - every account is entitled, lowest visibility.</summary>
    HiddenDev = 1,
    /// <summary>Old, unpopular, free apps - still entitled, more visible than Tier 1.</summary>
    OldFree = 2,
    /// <summary>Would be owned-game buckets - NEVER selected unless explicitly whitelisted by the user.</summary>
    OwnedReserved = 3,
}

public enum SlotState
{
    Candidate,
    Blocked,
    VerifiedWritable,
}

public sealed record ParkingApp(
    uint AppId,
    string Name,
    int ReleaseYear,
    bool IsFree,
    SlotTier Tier,
    SlotState State,
    string Category,
    string Note)
{
    public bool IsBlocked => State == SlotState.Blocked;

    public bool IsUsable => State is SlotState.Candidate or SlotState.VerifiedWritable;
}

/// <summary>
/// Curated parking pool: the only places SCT will store "foreign" save copies.
/// Every candidate must be an AppID the account is entitled to regardless of library
/// (hidden/dev apps, free games, Valve tools). 7/760 are enforced-blocked by Valve's
/// patch (Apr 2025). Policy: PRIVATE single-file cloud saves of real apps only -
/// no public/anonymous flooding (the 760-era dump site is dead on purpose).
/// CloudEnabled is not published via the store API - candidates get verified
/// by a private local probe (one tiny barcode-tagged file, client syncs, verdict
/// read from cloud_log.txt) before the registry marks them VerifiedWritable.
/// </summary>
public static class PoolDb
{
    public static readonly IReadOnlyList<ParkingApp> DefaultPool = Build();

    private static List<ParkingApp> Build() =>
    [
        // ---- Tier 1: hidden / dev / Valve tools ------------------------------
        new(480, "Spacewar", 2013, true, SlotTier.HiddenDev, SlotState.VerifiedWritable, "SteamDev",
            "Steam's hidden test game. Every account is entitled; bucket verified writable by SCT."),
        new(113200, "Cloud test app", 2011, true, SlotTier.HiddenDev, SlotState.Candidate, "SteamDev",
            "Steam cloud test app (empty bucket observed on real installs). Probe before use."),
        new(250820, "SteamVR", 2016, true, SlotTier.HiddenDev, SlotState.Candidate, "ValveTool",
            "Hidden app; VR settings sync via cloud. Probe before use."),
        new(413080, "SteamVR Home", 2016, true, SlotTier.HiddenDev, SlotState.Candidate, "ValveTool",
            "Hidden app; probe before use."),
        new(323370, "SteamVR Performance Test", 2016, true, SlotTier.HiddenDev, SlotState.Candidate, "ValveTool",
            "Free Valve utility; probe before use."),
        new(1249230, "SteamVR Tutorial", 2021, true, SlotTier.HiddenDev, SlotState.Candidate, "ValveTool",
            "Free Valve utility; probe before use."),
        new(7, "Steam Client", 2003, true, SlotTier.HiddenDev, SlotState.Candidate, "Internal",
            "Steam's own config bucket - universally entitled, hidden, uncommon. cloud_log shows native syncs "
            + "(Successfully synced to ChangeNumber) - probe decides upload capability."),
        new(760, "Screenshots", 2008, true, SlotTier.HiddenDev, SlotState.Blocked, "Internal",
            "INTERNAL - the 760-era public dump site. Server refuses UFS ops. NOT FLOODED - this is the official stance."),

        // ---- Tier 2: old, free, widely-entitled games -------------------------
        new(230410, "Warframe", 2013, true, SlotTier.OldFree, SlotState.Candidate, "FreeGame",
            "F2P, cloud-synced. Probe before use."),
        new(359550, "Rainbow Six Siege", 2015, true, SlotTier.OldFree, SlotState.Candidate, "FreeGame",
            "F2P since 2025?; probe before use."),
        new(570, "Dota 2", 2013, true, SlotTier.OldFree, SlotState.Candidate, "FreeGame",
            "F2P, cloud-synced; high visibility, crowded bucket."),
        new(440, "Team Fortress 2", 2007, true, SlotTier.OldFree, SlotState.Candidate, "FreeGame",
            "F2P, cloud-synced; ancient, crowded bucket."),
        new(730, "Counter-Strike 2", 2023, true, SlotTier.OldFree, SlotState.Candidate, "FreeGame",
            "F2P successor of CS:GO (2012, old codebase)."),
        new(218620, "PAYDAY 2", 2013, true, SlotTier.OldFree, SlotState.Candidate, "FreeGame",
            "F2P since 2023. Probe before use."),
        new(1085660, "Destiny 2", 2019, true, SlotTier.OldFree, SlotState.Candidate, "FreeGame",
            "F2P since 2019. Probe before use."),
        new(346110, "ARK Dev Kit", 2015, true, SlotTier.OldFree, SlotState.Candidate, "ModHost",
            "Free modding tool - 'mods and tools' lane. Probe before use."),
        new(322330, "Don't Starve Together", 2016, false, SlotTier.OwnedReserved, SlotState.Blocked, "Owned",
            "PAID - may only be used if genuinely owned; excluded by default."),
    ];

    public static IEnumerable<ParkingApp> Usable(SlotTier maxTier = SlotTier.OldFree)
        => DefaultPool.Where(p => p.IsUsable && p.Tier <= maxTier);

    public static ParkingApp? Find(uint appId) => DefaultPool.FirstOrDefault(p => p.AppId == appId);
}

public static class PoolScoring
{
    /// <summary>Tier base score; higher = preferred.</summary>
    public static int TierScore(SlotTier tier) => tier switch
    {
        SlotTier.HiddenDev => 100,
        SlotTier.OldFree => 60,
        SlotTier.OwnedReserved => 0,
        _ => 10,
    };

    /// <summary>Older release year wins (old saves are more "dead" = safer co-tenants). Unknown year gets no bonus.</summary>
    public static int AgeScore(int releaseYear) => releaseYear <= 0 ? 0 : Math.Clamp(2026 - releaseYear, 0, 50);

    /// <summary>Co-existence bonus: bucket already hosting other parked games = proven slot.</summary>
    public static int CoexistScore(int distinctHostedGames, int fileCount) =>
        distinctHostedGames > 0 ? 20 + Math.Min(fileCount, 25) : 0;

    /// <summary>
    /// Storage-quality bonus, per the ranking in docs/PLAN.md:
    /// VerifiedWritable real &gt; AutoClouded real &gt; probe-candidate &gt; provider/redirected.
    /// A null/unknown posture counts as real (curated PoolDb slots face Valve by design);
    /// provider / redirected / proxied / local containers are activation-class slots and
    /// get heavily penalized so they are only chosen once real candidates are exhausted.
    /// "provider" means CloudRedirect folder -- it touches the local CR daemon, not Valve.
    /// </summary>
    public static int PostureScore(string? posture, bool autoClouded, string? probeState)
    {
        var isReal = posture is null or "real";
        if (!isReal) return -60;
        if (probeState is "VerifiedWritable") return 30;
        if (autoClouded) return 20;
        return 10;
    }
}