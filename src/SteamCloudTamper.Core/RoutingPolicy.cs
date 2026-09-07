namespace SteamCloudTamper.Core;

/// <summary>
/// The human-editable routing policy for saves: whitelist / blacklist / force
/// overrides, and the per-game "where does this save go" (local folder or cloud
/// storage appid). Lives in a TOML file in the SCT data dir
/// (<c>%APPDATA%/SCT/sct.toml</c> on Windows, <c>~/.config/SCT/sct.toml</c> on Linux).
/// </summary>
public sealed class RoutingPolicy
{
    public const string MagicHeader = "# sct config v1";

    /// <summary>When non-empty, ONLY these game appids are allowed to route at all.</summary>
    public HashSet<uint> Whitelist { get; set; } = [];

    /// <summary>These game appids are never routed (they keep their native cloud/local behavior).</summary>
    public HashSet<uint> Blacklist { get; set; } = [];

    /// <summary>Per-game forced routing overrides. Key = game appid.</summary>
    public Dictionary<uint, RouteRule> Force { get; set; } = [];

    /// <summary>Default destination used when a game has no specific rule.</summary>
    public RouteRule Default { get; set; } = new();

    /// <summary>Human note (kept but not interpreted).</summary>
    public string? Comment { get; set; }

    public enum RouteTo { Cloud, Local }

    public sealed class RouteRule
    {
        /// <summary>cloud = park into a storage appid; local = keep in a local folder.</summary>
        public RouteTo Target { get; set; } = RouteTo.Cloud;

        /// <summary>For cloud targets: the storage appid to park into (0 = allocator decides).</summary>
        public uint StorageAppId { get; set; }

        /// <summary>For local targets: the local folder to keep the save in.</summary>
        public string? LocalFolder { get; set; }

        /// <summary>Optional preferred lane: client / rpc / auto.</summary>
        public string? Lane { get; set; }

        public string Describe() => Target switch
        {
            RouteTo.Cloud when StorageAppId != 0 => $"cloud -> storage {StorageAppId}",
            RouteTo.Cloud => "cloud -> allocator",
            _ => $"local -> {LocalFolder ?? "(default shadow folder)"}",
        };
    }

    // ------------------------------------------------------------------
    // Policy resolution
    // ------------------------------------------------------------------

    /// <summary>
    /// Whether routing is allowed for a game under this policy.
    /// Whitelist wins (set = only those games allowed); then blacklist; else true.
    /// </summary>
    public bool IsAllowed(uint gameAppId)
    {
        if (Whitelist.Count > 0) return Whitelist.Contains(gameAppId);
        return !Blacklist.Contains(gameAppId);
    }

    /// <summary>Effective rule for a game: a force override else the default.</summary>
    public RouteRule RuleFor(uint gameAppId)
        => Force.TryGetValue(gameAppId, out var r) ? r : Default;

    /// <summary>
    /// Where a save for this game should go. Respects force overrides; falls back
    /// to the default rule. Cloud destination 0 means "let the allocator pick".
    /// </summary>
    public (RouteTo Target, uint StorageAppId, string? LocalFolder, string? Lane) EffectiveRoute(uint gameAppId)
    {
        var rule = RuleFor(gameAppId);
        return (rule.Target, rule.StorageAppId, rule.LocalFolder, rule.Lane);
    }

    // ------------------------------------------------------------------
    // TOML persistence
    // ------------------------------------------------------------------

    public Dictionary<string, object?> ToToml()
    {
        var options = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["whitelist"] = Whitelist.OrderBy(x => x).Select(x => (object)x.ToString()).ToList(),
            ["blacklist"] = Blacklist.OrderBy(x => x).Select(x => (object)x.ToString()).ToList(),
        };
        if (!string.IsNullOrEmpty(Comment)) options["comment"] = Comment;

        var force = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (appId, rule) in Force)
            force[appId.ToString()] = RuleToToml(rule);

        var r = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["options"] = options,
            ["force"] = force,
        };
        if (Default.IsMeaningful()) r["default"] = RuleToToml(Default);
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["routing"] = r };
    }

    private static Dictionary<string, object?> RuleToToml(RouteRule rule)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["target"] = rule.Target == RouteTo.Cloud ? "cloud" : "local",
        };
        if (rule.StorageAppId != 0) d["storage"] = rule.StorageAppId.ToString();
        if (!string.IsNullOrEmpty(rule.LocalFolder)) d["folder"] = rule.LocalFolder;
        if (!string.IsNullOrEmpty(rule.Lane)) d["lane"] = rule.Lane;
        return d;
    }

    public static RoutingPolicy FromToml(Dictionary<string, object?>? root)
    {
        var p = new RoutingPolicy();
        if (root is null) return p;
        if (!root.TryGetValue("routing", out var routingObj) || routingObj is not Dictionary<string, object?> section)
            return p;
        root = section;

        if (root.TryGetValue("options", out var optsObj) && optsObj is Dictionary<string, object?> opts)
        {
            if (opts.TryGetValue("whitelist", out var wl) && wl is List<object?> wlList)
                p.Whitelist = ReadAppIdSet(wlList);
            if (opts.TryGetValue("blacklist", out var bl) && bl is List<object?> blList)
                p.Blacklist = ReadAppIdSet(blList);
            if (opts.TryGetValue("comment", out var cmt)) p.Comment = cmt is string s ? s : null;
        }

        if (root.TryGetValue("default", out var defObj) && defObj is Dictionary<string, object?> defTbl)
            p.Default = RuleFromToml(defTbl);

        if (root.TryGetValue("force", out var forceObj) && forceObj is Dictionary<string, object?> forceTbl)
        {
            foreach (var (k, v) in forceTbl)
            {
                if (uint.TryParse(k, out var appId) && v is Dictionary<string, object?> rule)
                    p.Force[appId] = RuleFromToml(rule);
            }
        }
        return p;
    }

    private static HashSet<uint> ReadAppIdSet(List<object?> items)
    {
        var set = new HashSet<uint>();
        foreach (var it in items)
        {
            var s = it?.ToString();
            if (s is not null && uint.TryParse(s, out var id)) set.Add(id);
        }
        return set;
    }

    private static RouteRule RuleFromToml(Dictionary<string, object?> d)
    {
        var rule = new RouteRule();
        if (d.TryGetValue("target", out var t) && t?.ToString() == "local") rule.Target = RouteTo.Local;
        if (d.TryGetValue("storage", out var st) && st?.ToString() is string ss && uint.TryParse(ss, out var sid)) rule.StorageAppId = sid;
        if (d.TryGetValue("folder", out var f)) rule.LocalFolder = f?.ToString();
        if (d.TryGetValue("lane", out var l)) rule.Lane = l?.ToString();
        return rule;
    }

    // ------------------------------------------------------------------
    // Paths
    // ------------------------------------------------------------------

    /// <summary>Directory that holds sct.toml + (on newer setups) co-located data.</summary>
    public static string DefaultDirectory()
    {
        var env = Environment.GetEnvironmentVariable("SCT_HOME");
        if (!string.IsNullOrEmpty(env)) return env;
        // Roaming app data: %APPDATA% on Windows, ~/.config on Linux.
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(baseDir, "SCT");
    }

    public static string DefaultPath()
    {
        var env = Environment.GetEnvironmentVariable("SCT_CONFIG");
        if (!string.IsNullOrEmpty(env)) return env;
        return Path.Combine(DefaultDirectory(), "sct.toml");
    }

    public static RoutingPolicy Load(string? path = null)
    {
        var p = path ?? DefaultPath();
        if (!File.Exists(p)) return new RoutingPolicy();
        try
        {
            var table = Toml.Parse(File.ReadAllText(p));
            return FromToml(table);
        }
        catch
        {
            // malformed policy - start clean, never crash the CLI on a hand-edited file
            return new RoutingPolicy();
        }
    }

    public void Save(string? path = null)
    {
        var p = path ?? DefaultPath();
        var dir = Path.GetDirectoryName(p);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Merge into any existing file so a co-located [config] section survives.
        var root = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(p))
        {
            try { root = Toml.Parse(File.ReadAllText(p)); } catch { root = []; }
        }
        root.Remove("routing");
        var self = ToToml();
        if (self.TryGetValue("routing", out var r) && r is Dictionary<string, object?> routingTable)
            root["routing"] = routingTable;
        File.WriteAllText(p, MagicHeader + Environment.NewLine + Toml.Write(root));
    }
}

internal static class RouteRuleExtensions
{
    internal static bool IsMeaningful(this RoutingPolicy.RouteRule r)
        => r.StorageAppId != 0 || !string.IsNullOrEmpty(r.LocalFolder) || !string.IsNullOrEmpty(r.Lane)
           || r.Target == RoutingPolicy.RouteTo.Local;
}