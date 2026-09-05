using System.Text.Json;

namespace SteamCloudTamper.Core.Pool;

public sealed record GameSlot(
    uint GameAppId,
    uint StorageAppId,
    string StoredName,
    string OriginalName,
    long Size,
    DateTime ParkedAt,
    string? BarcodePayload,
    string Status,
    string? Posture = null,
    bool? Verified = null)
{
    public static GameSlot New(uint game, uint storage, string stored, string original, long size, string barcode)
        => new(game, storage, stored, original, size, DateTime.UtcNow, barcode, "parked");

    /// <summary>
    /// Where this slot's uploads actually land: "real" (Valve UFS), "provider"
    /// (CloudRedirect folder provider), "redirected" (OST lua hook) or "local"
    /// (staged offline, client never confirmed it). Recorded by the lane that wrote it.
    /// </summary>
    public GameSlot WithPosture(string posture) => this with { Posture = posture };

    /// <summary>True when a post-park re-enumerate confirmed the uploaded sha on the wire.</summary>
    public GameSlot WithVerify(bool verified) => this with { Verified = verified };
}

/// <summary>
/// The single shared truth: which game's save lives where.
/// Read/written by SCT CLI/TUI, SteamCloudSave.dll, and emulator lanes.
/// </summary>
public sealed class SctRegistry
{
    public const string MagicHeader = "SCTREG1";

    public string Header { get; set; } = MagicHeader;

    public int Version { get; set; } = 1;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<GameSlot> Slots { get; set; } = [];

    /// <summary>
    /// Per-slot server verdicts from the private pool probe ("VerifiedWritable" / "Denied").
    /// VerifiedWritable lets the pool prefer proven slots; Denied excludes them until revoked.
    /// </summary>
    public Dictionary<uint, string> PoolProbes { get; set; } = [];

    /// <summary>
    /// Last snapshot of the container universe built by <see cref="PoolDiscoverer"/>
    /// (owned/free/hidden/tool/activation AppIDs + their posture and AutoClouded state).
    /// </summary>
    public List<ContainerInfo> Discovered { get; set; } = [];

    /// <summary>Re-runs discovery and persists the snapshot.</summary>
    public void SyncDiscovered(string steamPath, Func<uint, bool>? autoClouded = null, string? path = null)
    {
        Discovered = PoolDiscoverer.Discover(steamPath, autoClouded);
        Save(path);
    }

    public static string DefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("SCT_REGISTRY");
        if (!string.IsNullOrEmpty(env)) return env;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "SCT", "registry.json");
    }

    public static SctRegistry Load(string? path = null)
    {
        var p = path ?? DefaultPath();
        if (!File.Exists(p)) return new SctRegistry();
        try
        {
            var reg = JsonSerializer.Deserialize<SctRegistry>(File.ReadAllText(p), JsonOpts);
            if (reg is not null && reg.Header == MagicHeader) return reg;
        }
        catch
        {
            // corrupt registry - start clean, scanner rebuilds it
        }
        return new SctRegistry();
    }

    public void Save(string? path = null)
    {
        var p = path ?? DefaultPath();
        var dir = Path.GetDirectoryName(p);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        UpdatedAt = DateTime.UtcNow;
        File.WriteAllText(p, JsonSerializer.Serialize(this, JsonOpts));
    }

    public GameSlot? FindByGame(uint gameAppId) => Slots.FirstOrDefault(s => s.GameAppId == gameAppId);

    public GameSlot? FindByStoredName(uint storageAppId, string storedName)
        => Slots.FirstOrDefault(s =>
            s.StorageAppId == storageAppId &&
            s.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase));

    public void Upsert(GameSlot slot)
    {
        var idx = Slots.FindIndex(s =>
            s.StorageAppId == slot.StorageAppId &&
            s.StoredName.Equals(slot.StoredName, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) Slots[idx] = slot; else Slots.Add(slot);
    }

    public void Remove(uint storageAppId, string storedName)
        => Slots.RemoveAll(s =>
            s.StorageAppId == storageAppId &&
            s.StoredName.Equals(storedName, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}