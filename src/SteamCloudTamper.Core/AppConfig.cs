using System.Text;
using System.Text.Json;

namespace SteamCloudTamper.Core;

public sealed class AppConfig
{
    public string? SteamPathOverride { get; set; }

    public bool DryRun { get; set; } = true;

    /// <summary>Always re-enumerate + sha-compare after every park upload (per-call --verify).</summary>
    public bool VerifyAfterPark { get; set; }

    public HashSet<uint> GuardedAppIds { get; set; } = [];

    public Dictionary<string, string> Hints { get; set; } = [];

    public List<uint> KnownOwnedAppIds { get; set; } = [];

    /// <summary>
    /// AppID proxy map (Ace SLS style): game appid -> proxy appid. Key 0 = the
    /// default proxy for ANY unowned game. e.g. { 588650: 480 } parks Dead Cells
    /// inside your Spacewar bucket under "sls-588650/..." paths.
    /// </summary>
    public Dictionary<uint, uint> CloudProxies { get; set; } = [];

    /// <summary>Resolves the proxy for a game: per-game entry, else the 0 default. 0 = none.</summary>
    public uint ResolveProxy(uint gameAppId)
    {
        if (CloudProxies.TryGetValue(gameAppId, out var p)) return p;
        if (CloudProxies.TryGetValue(0, out var d)) return d;
        return 0;
    }

    public string? CookieFile { get; set; }

    /// <summary>Where this config was loaded from (not serialized). Lets mutated configs persist themselves.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SourcePath { get; private set; }

    /// <summary>Config path resolution: SCT_CONFIG env override, else the legacy CWD steamcloudtamper.json.</summary>
    public static string ResolveDefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("SCT_CONFIG");
        if (!string.IsNullOrEmpty(env)) return env;
        return Path.Combine(Directory.GetCurrentDirectory(), "steamcloudtamper.json");
    }

    public static AppConfig Load(string path)
    {
        var cfg = new AppConfig { SourcePath = path };
        if (!File.Exists(path)) return cfg;
        try
        {
            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts);
            if (loaded is not null) loaded.SourcePath = path;
            return loaded ?? cfg;
        }
        catch
        {
            return cfg;
        }
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(path, json);
        SourcePath = path;
    }

    /// <summary>Persists to the path this config was loaded from (no-op when not loaded from a file).</summary>
    public void Save()
    {
        if (SourcePath is not null) Save(SourcePath);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public HashSet<uint> GetOwnedSet() => new(KnownOwnedAppIds);
}

public static class BlankSaveKit
{
    private static readonly byte[] JsonBlank = """{"sct_blank":true,"created_by":"SteamCloudTamper"}"""u8.ToArray();

    public static byte[] CreateBlank(string fileName, byte[]? template = null)
    {
        if (template is not null) return template;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".json" => JsonBlank,
            ".txt" or ".log" or ".cfg" or ".ini" or ".xml" or ".dat" or ".sav" => JsonBlank.AsSpan().ToArray(),
            _ => [0x00, 0x00, 0x00, 0x00],
        };
    }
}