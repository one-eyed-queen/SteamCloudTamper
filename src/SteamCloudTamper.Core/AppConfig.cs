using System.Text.Json;

namespace SteamCloudTamper.Core;

/// <summary>
/// The single user-facing config. Lives as TOML in the SCT data dir
/// (<c>%APPDATA%/SCT/sct.toml</c> on Windows, <c>~/.config/SCT/sct.toml</c> on Linux)
/// and holds both the app knobs (<c>[config]</c>) and the save routing policy
/// (<c>[routing.*]</c>). Legacy <c>steamcloudtamper.json</c> files still load for
/// migration; the first save persists the unified TOML once more.
/// </summary>
public sealed class AppConfig
{
    public const string TomlHeader = "# sct config v1";

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

    /// <summary>The save routing policy (whitelist/blacklist/force), same file.</summary>
    public RoutingPolicy Routing { get; set; } = new();

    /// <summary>Resolves the proxy for a game: per-game entry, else the 0 default. 0 = none.</summary>
    public uint ResolveProxy(uint gameAppId)
    {
        if (CloudProxies.TryGetValue(gameAppId, out var p)) return p;
        if (CloudProxies.TryGetValue(0, out var d)) return d;
        return 0;
    }

    public string? CookieFile { get; set; }

    /// <summary>Where this config was loaded from (not serialized). Lets mutated configs persist themselves.</summary>
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    /// <summary>
    /// Config path resolution: SCT_CONFIG env override, else the unified TOML at
    /// <c>%APPDATA%/SCT/sct.toml</c> (~/.config/SCT/sct.toml on Linux).
    /// </summary>
    public static string ResolveDefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("SCT_CONFIG");
        if (!string.IsNullOrEmpty(env)) return env;
        return RoutingPolicy.DefaultPath();
    }

    public static AppConfig Load(string path)
    {
        var cfg = new AppConfig { SourcePath = path };
        if (!File.Exists(path))
        {
            // First-run migration: pick up a legacy CWD steamcloudtamper.json; the
            // next Save() persists the unified TOML at the new canonical location.
            TryMigrateLegacy(cfg);
            return cfg;
        }

        try
        {
            var txt = File.ReadAllText(path);
            if (LooksLikeJson(txt))
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(txt, JsonOpts);
                if (loaded is not null)
                {
                    loaded.SourcePath = path;
                    return loaded;
                }
            }
            else
            {
                var root = Toml.Parse(txt);
                FromToml(root, cfg);
            }
        }
        catch
        {
            cfg = new AppConfig { SourcePath = path };
        }
        return cfg;
    }

    private static bool LooksLikeJson(string txt)
    {
        var trimmed = txt.TrimStart();
        return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed.StartsWith("null", StringComparison.OrdinalIgnoreCase));
    }

    private static void TryMigrateLegacy(AppConfig cfg)
    {
        var legacyPath = Path.Combine(Directory.GetCurrentDirectory(), "steamcloudtamper.json");
        if (!File.Exists(legacyPath)) return;
        try
        {
            var txt = File.ReadAllText(legacyPath);
            if (!LooksLikeJson(txt)) return;
            var loaded = JsonSerializer.Deserialize<AppConfig>(txt, JsonOpts);
            if (loaded is null) return;
            cfg.SteamPathOverride = loaded.SteamPathOverride;
            cfg.DryRun = loaded.DryRun;
            cfg.VerifyAfterPark = loaded.VerifyAfterPark;
            cfg.GuardedAppIds = loaded.GuardedAppIds;
            cfg.Hints = loaded.Hints;
            cfg.KnownOwnedAppIds = loaded.KnownOwnedAppIds;
            cfg.CloudProxies = loaded.CloudProxies;
            cfg.CookieFile = loaded.CookieFile;
            cfg.Routing = loaded.Routing ?? new RoutingPolicy();
        }
        catch
        {
            // unreadable legacy file - fall through with defaults
        }
    }

    // ------------------------------------------------------------------
    // TOML marshalling
    // ------------------------------------------------------------------

    public Dictionary<string, object?> ToToml()
    {
        var config = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(SteamPathOverride)) config["steamPathOverride"] = SteamPathOverride;
        config["dryRun"] = DryRun;
        config["verifyAfterPark"] = VerifyAfterPark;
        if (GuardedAppIds.Count > 0)
            config["guarded"] = GuardedAppIds.OrderBy(x => x).Select(x => (object)x.ToString()).ToList();
        if (Hints.Count > 0)
            config["hints"] = Hints.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.OrdinalIgnoreCase);
        if (KnownOwnedAppIds.Count > 0)
            config["knownOwned"] = KnownOwnedAppIds.Select(x => (object)x.ToString()).ToList();
        if (CloudProxies.Count > 0)
            config["proxies"] = CloudProxies.ToDictionary(kv => kv.Key.ToString(), kv => (object?)kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(CookieFile)) config["cookieFile"] = CookieFile;

        var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["config"] = config };
        var routingRoot = Routing.ToToml();
        if (routingRoot.TryGetValue("routing", out var r) && r is Dictionary<string, object?> rt)
            root["routing"] = rt;
        return root;
    }

    private static void FromToml(Dictionary<string, object?> root, AppConfig cfg)
    {
        if (root.TryGetValue("config", out var cObj) && cObj is Dictionary<string, object?> c)
        {
            if (c.TryGetValue("steamPathOverride", out var soV) && soV is string so) cfg.SteamPathOverride = so;
            if (c.TryGetValue("dryRun", out var dryV) && dryV is bool db) cfg.DryRun = db;
            if (c.TryGetValue("verifyAfterPark", out var vfyV) && vfyV is bool vb) cfg.VerifyAfterPark = vb;
            if (c.TryGetValue("guarded", out var gdL) && gdL is List<object?> gl)
                cfg.GuardedAppIds = ReadAppIdSet(gl);
            if (c.TryGetValue("hints", out var htV) && htV is Dictionary<string, object?> ht)
                cfg.Hints = ht.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            if (c.TryGetValue("knownOwned", out var koL) && koL is List<object?> ko)
                cfg.KnownOwnedAppIds = ReadAppIdSet(ko).OrderBy(x => x).ToList();
            if (c.TryGetValue("proxies", out var pxV) && pxV is Dictionary<string, object?> pt)
            {
                cfg.CloudProxies = [];
                foreach (var (k, val) in pt)
                {
                    if (uint.TryParse(k, out var from) && uint.TryParse(val?.ToString(), out var to))
                        cfg.CloudProxies[from] = to;
                }
            }
            if (c.TryGetValue("cookieFile", out var cfV) && cfV is string cf) cfg.CookieFile = cf;
        }
        cfg.Routing = RoutingPolicy.FromToml(root);
    }

    private static HashSet<uint> ReadAppIdSet(List<object?> items)
    {
        var set = new HashSet<uint>();
        foreach (var it in items)
        {
            if (it?.ToString() is string s && uint.TryParse(s, out var id)) set.Add(id);
        }
        return set;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, TomlHeader + Environment.NewLine + Toml.Write(ToToml()));
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