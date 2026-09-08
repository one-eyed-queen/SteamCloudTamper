using SteamCloudTamper.Core;

namespace SteamCloudTamper.Core.Tests;

public class TomlParsingTests
{
    private static Dictionary<string, object?> T(Dictionary<string, object?> root, params string[] path)
    {
        Dictionary<string, object?> cur = root;
        foreach (var key in path)
        {
            Assert.True(cur.TryGetValue(key, out var next), $"missing table: {key}");
            cur = Assert.IsType<Dictionary<string, object?>>(next);
        }
        return cur;
    }

    private static List<object?> L(Dictionary<string, object?> table, string key)
        => Assert.IsType<List<object?>>(table[key]);

    [Fact]
    public void Parses_Sections_Strings_Ints_Bools_Arrays()
    {
        var root = Toml.Parse("""
            # comment
            [config]
            dryRun = true
            verifyAfterPark = false
            name = "cloud"
            size = 42

            [routing.options]
            whitelist = [ "440", "570" ]
            blacklist = []
            """);

        var cfg = T(root, "config");
        Assert.Equal(true, cfg["dryRun"]);
        Assert.Equal(false, cfg["verifyAfterPark"]);
        Assert.Equal("cloud", cfg["name"]);
        Assert.Equal(42L, cfg["size"]);

        var opts = T(root, "routing", "options");
        Assert.Equal(2, L(opts, "whitelist").Count);
        Assert.Equal("440", L(opts, "whitelist")[0]);
        Assert.Equal("570", L(opts, "whitelist")[1]);
    }

    [Fact]
    public void Dotted_Section_Keys_Collapse_Into_Nested_Tables()
    {
        var root = Toml.Parse("""
            [routing.force."440"]
            target = "cloud"
            storage = "480"
            """);

        var rule = T(root, "routing", "force", "440");
        Assert.Equal("cloud", rule["target"]);
        Assert.Equal("480", rule["storage"]);
    }

    [Fact]
    public void Inline_Tables_And_Comments_Are_Handled()
    {
        var root = Toml.Parse("""
            [config]
            proxies = { "588650" = "480", "0" = "480" } # trailing comment
            hints = { theme = "dark" }
            """);

        var cfg = T(root, "config");
        var proxies = Assert.IsType<Dictionary<string, object?>>(cfg["proxies"]);
        Assert.Equal("480", proxies["588650"]);
        Assert.Equal("480", proxies["0"]);
        var hints = Assert.IsType<Dictionary<string, object?>>(cfg["hints"]);
        Assert.Equal("dark", hints["theme"]);
    }

    [Fact]
    public void Comments_Inside_Strings_Are_Not_Stripped()
    {
        var root = Toml.Parse("[config]\nlabel = \"a#b\"\n");
        var cfg = T(root, "config");
        Assert.Equal("a#b", cfg["label"]);
    }

    [Fact]
    public void Escaped_Basic_String_Decodes()
    {
        var root = Toml.Parse("[config]\npath = \"C:\\\\SCT\\\\data\"\n");
        var cfg = T(root, "config");
        Assert.Equal(@"C:\SCT\data", cfg["path"]);
    }

    [Fact]
    public void Empty_Array_Parses_To_Empty_List()
    {
        var root = Toml.Parse("[routing.options]\nblacklist = []\n");
        var opts = T(root, "routing", "options");
        Assert.Empty(L(opts, "blacklist"));
    }

    [Fact]
    public void RoundTrips_Structurally()
    {
        var text = """
            [config]
            dryRun = true
            guarded = [ "440" ]

            [routing.force."440"]
            target = "cloud"
            storage = "480"
            """;

        var root = Toml.Parse(text);
        var written = Toml.Write(root);
        var reparsed = Toml.Parse(written);

        Assert.Equal(true, T(reparsed, "config")["dryRun"]);
        var rule = T(reparsed, "routing", "force", "440");
        Assert.Equal("cloud", rule["target"]);
        Assert.Equal("480", rule["storage"]);
    }

    [Fact]
    public void Malformed_Input_Throws_Rather_Than_Silently_Corrupting()
    {
        Assert.Throws<Toml.TomlException>(() => Toml.Parse("[unterminated"));
        Assert.Throws<Toml.TomlException>(() => Toml.Parse("[a]\nb = \"unterminated\n"));
    }

    [Fact]
    public void Array_Of_Tables_RoundTrips_Instead_Of_Being_Dropped()
    {
        var text = """
            [config]
            dryRun = true

            [[external.registry]]
            name = "slot_a"
            size = 10

            [[external.registry]]
            name = "slot_b"
            size = 20

            [routing.options]
            blacklist = [ "999" ]
            """;

        var root = Toml.Parse(text);
        var written = Toml.Write(root);
        var reparsed = Toml.Parse(written);

        // both array elements survive the round-trip (this was silently
        // erased before array-of-tables support was added)
        var reg = Assert.IsType<List<Dictionary<string, object?>>>(T(reparsed, "external")["registry"]);
        Assert.Equal(2, reg.Count);
        Assert.Equal("slot_a", reg[0]["name"]);
        Assert.Equal("slot_b", reg[1]["name"]);
        Assert.Equal(10L, reg[0]["size"]);
        // neighbors in the same file are preserved too
        Assert.Equal(true, T(reparsed, "config")["dryRun"]);
        Assert.Equal("999", Assert.IsType<List<object?>>(T(reparsed, "routing", "options")["blacklist"])[0]);
    }

    [Fact]
    public void Routing_Save_Preserves_Array_Of_Tables_From_HandEdited_File()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sct_merge_{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, """
            [config]
            dryRun = true

            [[external.registry]]
            name = "keeper"
            """);

        try
        {
            // a routing Save() merges into the existing file; an array-of-tables
            // section elsewhere in it must survive the rewrite untouched
            var p = new RoutingPolicy { Blacklist = [123u] };
            p.Save(path);
            var text = File.ReadAllText(path);

            var root = Toml.Parse(text);
            Assert.Equal(true, T(root, "config")["dryRun"]);
            var reg = Assert.IsType<List<Dictionary<string, object?>>>(T(root, "external")["registry"]);
            Assert.Equal("keeper", reg[0]["name"]);
            var blacklist = Assert.IsType<List<object?>>(T(root, "routing", "options")["blacklist"]);
            Assert.Equal("123", blacklist[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class RoutingPolicyTests
{
    [Fact]
    public void Whitelist_Only_Allows_Listed_Games()
    {
        var p = new RoutingPolicy { Whitelist = [440u] };
        Assert.True(p.IsAllowed(440));
        Assert.False(p.IsAllowed(570));
    }

    [Fact]
    public void Blacklist_Denies_Only_Listed_Games()
    {
        var p = new RoutingPolicy { Blacklist = [570u] };
        Assert.True(p.IsAllowed(440));
        Assert.False(p.IsAllowed(570));
    }

    [Fact]
    public void Force_Overrides_Default_Rule()
    {
        var p = new RoutingPolicy
        {
            Default = new RoutingPolicy.RouteRule { Target = RoutingPolicy.RouteTo.Local },
            Force = new Dictionary<uint, RoutingPolicy.RouteRule>
            {
                [440] = new() { Target = RoutingPolicy.RouteTo.Cloud, StorageAppId = 480 },
            },
        };

        var (target, storage, _, _) = p.EffectiveRoute(440);
        Assert.Equal(RoutingPolicy.RouteTo.Cloud, target);
        Assert.Equal(480u, storage);

        var (dtarget, _, _, _) = p.EffectiveRoute(570);
        Assert.Equal(RoutingPolicy.RouteTo.Local, dtarget);
    }

    [Fact]
    public void Toml_Policy_RoundTrips()
    {
        var p = new RoutingPolicy
        {
            Whitelist = [440u],
            Blacklist = [570u],
            Default = new RoutingPolicy.RouteRule { Target = RoutingPolicy.RouteTo.Local, LocalFolder = "shadow" },
            Force = new Dictionary<uint, RoutingPolicy.RouteRule>
            {
                [440] = new() { Target = RoutingPolicy.RouteTo.Cloud, StorageAppId = 480, Lane = "rpc" },
            },
        };

        var t = Toml.Write(p.ToToml());
        var back = RoutingPolicy.FromToml(Toml.Parse(t));

        Assert.True(back.Whitelist.SetEquals(new uint[] { 440u }));
        Assert.True(back.Blacklist.SetEquals(new uint[] { 570u }));
        Assert.Equal(RoutingPolicy.RouteTo.Local, back.Default.Target);
        Assert.Equal("shadow", back.Default.LocalFolder);
        var force = back.Force[440];
        Assert.Equal(RoutingPolicy.RouteTo.Cloud, force.Target);
        Assert.Equal(480u, force.StorageAppId);
        Assert.Equal("rpc", force.Lane);
    }

    [Fact]
    public void AppConfig_Toml_RoundTrips_Config_And_Routing()
    {
        var cfg = new AppConfig
        {
            DryRun = false,
            VerifyAfterPark = true,
            GuardedAppIds = [470u],
            CloudProxies = new Dictionary<uint, uint> { [588650] = 480, [0] = 480 },
            KnownOwnedAppIds = [480u],
            Routing = new RoutingPolicy
            {
                Blacklist = [725u],
                Force = new Dictionary<uint, RoutingPolicy.RouteRule>
                {
                    [440] = new() { Target = RoutingPolicy.RouteTo.Cloud, StorageAppId = 480 },
                },
            },
        };

        var text = "# sct config v1\n" + Toml.Write(cfg.ToToml());
        var loaded = AppConfigFromTomlText(text);

        Assert.False(loaded.DryRun);
        Assert.True(loaded.VerifyAfterPark);
        Assert.Contains(470u, loaded.GuardedAppIds);
        Assert.Equal(480u, loaded.ResolveProxy(588650));
        Assert.Equal(480u, loaded.ResolveProxy(440));
        Assert.Contains(725u, loaded.Routing.Blacklist);
        Assert.Equal(480u, loaded.Routing.Force[440].StorageAppId);
    }

    private static AppConfig AppConfigFromTomlText(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sct_cfg_{Guid.NewGuid():N}.toml");
        File.WriteAllText(path, text);
        var loaded = AppConfig.Load(path);
        File.Delete(path);
        return loaded;
    }

    [Fact]
    public void Json_Config_Still_Loads_For_Migration()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sct_legacy_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"DryRun":false,"GuardedAppIds":[470]}""");
        var loaded = AppConfig.Load(path);
        File.Delete(path);

        Assert.False(loaded.DryRun);
        Assert.Contains(470u, loaded.GuardedAppIds);
        Assert.NotNull(loaded.Routing);
    }
}