using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Pool;
using SteamCloudTamper.Core.Steam;

namespace SteamCloudTamper.Core.Tests;

public class BarcodeTests
{
    [Fact]
    public void TrailerRoundTripPreservesPayload()
    {
        var trailer = Barcode.PackTrailer("588650|1201110076|09082026");
        Assert.True(Barcode.TryDecode(trailer, out var payload));
        Assert.Equal("588650|1201110076|09082026", payload);

        var (game, uid, date) = Barcode.Parse(payload);
        Assert.Equal(588650u, game);
        Assert.Equal("1201110076", uid);
        Assert.Equal(new DateOnly(2026, 8, 9), date);
    }

    [Fact]
    public void TailDecodeFindsTrailerInWindow()
    {
        var data = new byte[8192];
        new Random(42).NextBytes(data);
        var trailer = Barcode.PackTrailer("480|123456789|01012026");
        var full = data.Concat(trailer).ToArray();

        var tail = full.AsSpan(full.Length - Barcode.TailWindowBytes);
        Assert.True(Barcode.TryDecodeTail(tail, out var payload, out var len));
        Assert.Equal("480|123456789|01012026", payload);
        Assert.Equal(trailer.Length, len);
    }

    [Fact]
    public void StripTrailerRestoresOriginalBytes()
    {
        var original = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var trailer = Barcode.PackTrailer("588650|1201110076|09082026");
        var tagged = original.Concat(trailer).ToArray();

        var start = Math.Max(0, tagged.Length - Barcode.TailWindowBytes);
        var tail = tagged.AsSpan(start);
        Assert.True(Barcode.TryDecodeTail(tail, out _, out var len));
        var restored = Barcode.StripTrailer(tagged, len);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void CrcDetectsCorruption()
    {
        var trailer = Barcode.PackTrailer("588650|1201110076|09082026");
        trailer[^1] ^= 0xFF; // flip a crc byte
        Assert.False(Barcode.TryDecode(trailer, out _));
    }

    [Fact]
    public void RenderBarcodeProducesFixedHeight()
    {
        var lines = Barcode.RenderBarcode("588650|1201110076|09082026", height: 21);
        Assert.Equal(21, lines.Count);
        Assert.All(lines, l => Assert.Contains(l, c => c == '█' || c == ' '));
    }
}

public class PoolDbTests
{
    [Fact]
    public void BlockedAppsAreNeverUsable()
    {
        Assert.False(PoolDb.Find(760)!.IsUsable); // SteamTools flood site stays dead
        Assert.True(PoolDb.Find(7)!.IsUsable);    // Steam client bucket re-candidate (native syncs observed)
        Assert.True(PoolDb.Find(480)!.IsUsable);
    }

    [Fact]
    public void OwnedReservedTierIsBelowCutoff()
    {
        Assert.DoesNotContain(PoolDb.Usable(), p => p.Tier == SlotTier.OwnedReserved);
    }
}

public class ParkingEngineTests
{
    [Fact]
    public void PicksHiddenSlotWhenAvailable()
    {
        var engine = new ParkingEngine([], []);
        var d = engine.Pick(91330, "save.sav", 1024);
        Assert.True(d.Ok);
        Assert.NotNull(d.StorageAppId);
        var picked = PoolDb.Find(d.StorageAppId.Value);
        Assert.NotNull(picked);
        Assert.Equal(SlotTier.HiddenDev, picked.Tier); // highest tier wins by design
        Assert.False(picked.IsBlocked);
        Assert.Equal("91330_save.sav", d.StoredName);
    }

    [Fact]
    public void NeverPicksOwnedGameSlot()
    {
        var engine = new ParkingEngine([480], []);
        var d = engine.Pick(91330, "save.sav", 1024);
        Assert.True(d.Ok);
        Assert.NotEqual(480u, d.StorageAppId);
    }

    [Fact]
    public void ReusesExistingParkingSlotForSameGame()
    {
        var slots = new List<GameSlot>
        {
            GameSlot.New(91330, 480, "91330_save.sav", "save.sav", 10, "91330|1|01012026"),
        };
        var engine = new ParkingEngine([], slots);
        var d = engine.Pick(91330, "save.sav", 1024);
        Assert.True(d.Ok);
        Assert.Equal(480u, d.StorageAppId);
        Assert.Contains("already parked", d.Reason);
    }

    [Fact]
    public void CoexistenceShiftsToOtherSlotOnNameCollision()
    {
        var slots = new List<GameSlot>
        {
            GameSlot.New(91330, 480, "91330_save.sav", "save.sav", 10, "91330|1|01012026"),
            GameSlot.New(4200, 480, "4200_other.sav", "other.sav", 10, "4200|1|01012026"),
        };
        var engine = new ParkingEngine([], slots);
        var d = engine.Pick(91330, "other.sav", 1024); // same stored name as 480's other game? no - 91330_other.sav
        Assert.True(d.Ok);
        Assert.Equal(480u, d.StorageAppId);
    }

    [Fact]
    public void PlanSpreadsFilesAcrossSlots()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330,
            [new ParkFile("a.sav", 100), new ParkFile("b.sav", 100), new ParkFile("c.sav", 100)],
            spread: 3);
        Assert.All(plan, d => Assert.True(d.Ok));
        var slots = plan.Select(d => d.StorageAppId).Distinct().ToList();
        Assert.Equal(3, slots.Count); // top-3 usable: 480, 113200, 250820
        Assert.Equal("91330_a.sav", plan[0].StoredName);
    }

    [Fact]
    public void PlanCopiesMirrorToDistinctNames()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330, [new ParkFile("save.sav", 100)], copies: 2);
        Assert.Equal(2, plan.Count);
        Assert.All(plan, d => Assert.True(d.Ok));
        Assert.NotEqual(plan[0].StoredName, plan[1].StoredName);
        Assert.Contains("c1", plan[0].StoredName!);
        Assert.Contains("c2", plan[1].StoredName!);
    }

    [Fact]
    public void PlanStealthHashesStoredNames()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330, [new ParkFile("save.sav", 100)], stealth: true);
        Assert.True(plan[0].Ok);
        Assert.StartsWith("k000164c2", plan[0].StoredName!); // k{91330:x8}
        Assert.EndsWith(".sav", plan[0].StoredName!);
        Assert.DoesNotContain("91330_save", plan[0].StoredName);
    }

    [Fact]
    public void ServerDeniedSlotIsExcludedFromPlanning()
    {
        var engine = new ParkingEngine([], [], poolProbes: new Dictionary<uint, string> { [480] = "Denied" });
        var d = engine.Pick(91330, "save.sav", 1024);
        Assert.True(d.Ok);
        Assert.NotEqual(480u, d.StorageAppId);
        Assert.Equal(7u, d.StorageAppId); // next best usable slot (Steam Client bucket re-candidate)
    }

    [Fact]
    public void ForceBucketPinsEveryFileToOneSlot()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330,
            [new ParkFile("a.sav", 100), new ParkFile("b.sav", 100)],
            spread: 3, forceBucket: 250820);
        Assert.All(plan, d => Assert.True(d.Ok));
        Assert.All(plan, d => Assert.Equal(250820u, d.StorageAppId));
    }

    [Fact]
    public void ForceBucketRefusedWhenBlockedInPool()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330, [new ParkFile("a.sav", 100)], forceBucket: 760);
        Assert.All(plan, d => Assert.False(d.Ok));
        Assert.Contains("blocked", plan[0].Reason);
    }

    [Fact]
    public void ForceBucketRefusedWhenServerDenied()
    {
        var engine = new ParkingEngine([], [], poolProbes: new Dictionary<uint, string> { [480] = "Denied" });
        var plan = engine.Plan(91330, [new ParkFile("a.sav", 100)], forceBucket: 480);
        Assert.All(plan, d => Assert.False(d.Ok));
        Assert.Contains("Denied", plan[0].Reason);
    }

    [Fact]
    public void ForceBucketRefusedWhenNotInPool()
    {
        var engine = new ParkingEngine([], []);
        var plan = engine.Plan(91330, [new ParkFile("a.sav", 100)], forceBucket: 91330);
        Assert.All(plan, d => Assert.False(d.Ok));
    }

    [Fact]
    public void PostureSurvivesRegistryRoundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sct_reg_{Guid.NewGuid():N}.json");
        try
        {
            var reg = new SctRegistry();
            reg.Upsert(GameSlot.New(588650, 480, "588650_user_0.dat", "user_0.dat", 10, "588650|1|01012026")
                .WithPosture("real"));
            reg.Save(path);
            var loaded = SctRegistry.Load(path);
            Assert.Equal("real", loaded.Slots[0].Posture);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ConsentAndPostureTests
{
    private static ContainerInfo Owned(uint appId) =>
        new(appId, $"owned-{appId}", ContainerKind.Owned, ContainerSource.UserData, "real", false, "userdata bucket");

    private static ContainerInfo Activation(uint appId) =>
        new(appId, $"lua-{appId}", ContainerKind.Activation, ContainerSource.OstLua, "redirected", false, "lua hook");

    private static Dictionary<uint, string> DenyAllPool() =>
        PoolDb.Usable().ToDictionary(p => p.AppId, _ => "Denied");

    [Fact]
    public void OwnedBucketNeedsExplicitConsentWhenForced()
    {
        var engine = new ParkingEngine([], []);
        var containers = new List<ContainerInfo> { Owned(2371090) };

        var no = engine.Plan(91330, [new ParkFile("s.sav", 100)], forceBucket: 2371090, containers: containers);
        Assert.False(no[0].Ok);
        Assert.Contains("allow-owned", no[0].Reason, StringComparison.OrdinalIgnoreCase);

        var yes = engine.Plan(91330, [new ParkFile("s.sav", 100)], forceBucket: 2371090,
            containers: containers, allowOwned: true);
        Assert.True(yes[0].Ok);
        Assert.Equal(2371090u, yes[0].StorageAppId);
        Assert.Equal("91330_s.sav", yes[0].StoredName);
    }

    [Fact]
    public void OwnedBucketsAreNeverAutoPickedWithoutConsent()
    {
        // everything real is server-denied, so the ONLY slot left is the owned
        // bucket - still refused until --allow-owned says otherwise
        var engine = new ParkingEngine([], [], poolProbes: DenyAllPool());
        var containers = new List<ContainerInfo> { Owned(2371090) };

        var no = engine.Pick(91330, "s.sav", 100, containers: containers);
        Assert.False(no.Ok);
        Assert.Contains("allow-owned", no.Reason, StringComparison.OrdinalIgnoreCase);

        var yes = engine.Pick(91330, "s.sav", 100, containers: containers, allowOwned: true);
        Assert.True(yes.Ok);
        Assert.Equal(2371090u, yes.StorageAppId);
    }

    [Fact]
    public void ActivationContainersNotPickedWhileRealSlotsExist()
    {
        var engine = new ParkingEngine([], []);
        var d = engine.Pick(91330, "s.sav", 100,
            containers: new List<ContainerInfo> { Activation(588650) });
        Assert.True(d.Ok);
        Assert.NotEqual(588650u, d.StorageAppId);
        Assert.Equal(SlotTier.HiddenDev, PoolDb.Find(d.StorageAppId!.Value)!.Tier);
    }

    [Fact]
    public void ActivationContainersFillInWhenRealSlotsAreDenied()
    {
        // activation slots are last resort: they only win when no real candidate exists
        var engine = new ParkingEngine([], [], poolProbes: DenyAllPool());
        var d = engine.Pick(91330, "s.sav", 100,
            containers: new List<ContainerInfo> { Activation(588650) });
        Assert.True(d.Ok);
        Assert.Equal(588650u, d.StorageAppId);
        Assert.Equal("91330_s.sav", d.StoredName);
    }

    [Fact]
    public void PostureFilterRestrictsTheUniverse()
    {
        var engine = new ParkingEngine([], []);
        var containers = new List<ContainerInfo> { Activation(588650) };

        // only redirected allowed -> pool slots (real by default) are filtered out
        var only = engine.Plan(91330, [new ParkFile("s.sav", 100)],
            containers: containers, postureFilter: new HashSet<string> { "redirected" });
        Assert.True(only[0].Ok);
        Assert.Equal(588650u, only[0].StorageAppId);

        // real only -> the activation container is filtered out, pool survives
        var real = engine.Plan(91330, [new ParkFile("s.sav", 100)],
            containers: containers, postureFilter: new HashSet<string> { "real" });
        Assert.True(real[0].Ok);
        Assert.NotEqual(588650u, real[0].StorageAppId);

        // any = no filter
        var any = engine.Plan(91330, [new ParkFile("s.sav", 100)],
            containers: containers, postureFilter: new HashSet<string> { "any" });
        Assert.True(any[0].Ok);
        Assert.NotEqual(588650u, any[0].StorageAppId);
    }

    [Fact]
    public void ForceBucketHonorsPostureFilter()
    {
        var engine = new ParkingEngine([], []);
        var containers = new List<ContainerInfo> { Activation(588650) };
        var plan = engine.Plan(91330, [new ParkFile("s.sav", 100)], forceBucket: 588650,
            containers: containers, postureFilter: new HashSet<string> { "real" });
        Assert.False(plan[0].Ok);
        Assert.Contains("posture", plan[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReparkKeepsOwnedSlotOnceConsentGiven()
    {
        var slots = new List<GameSlot>
        {
            GameSlot.New(91330, 2371090, "91330_save.sav", "save.sav", 10, "91330|1|01012026"),
        };
        var engine = new ParkingEngine([], slots);
        var containers = new List<ContainerInfo> { Owned(2371090) };

        // slot owner is an owned bucket: no consent -> reallocate elsewhere
        var no = engine.Pick(91330, "save.sav", 1024, containers: containers);
        Assert.True(no.Ok);
        Assert.NotEqual(2371090u, no.StorageAppId);
        Assert.DoesNotContain("already parked", no.Reason);

        // with consent -> the proven owned slot wins again
        var yes = engine.Pick(91330, "save.sav", 1024, containers: containers, allowOwned: true);
        Assert.True(yes.Ok);
        Assert.Equal(2371090u, yes.StorageAppId);
        Assert.Contains("already parked", yes.Reason);
    }
}

public class SteamLocatorTests
{
    [Fact]
    public void IsOstRedirectedDetectsLuaAddappid()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"sct_steam_{Guid.NewGuid():N}");
        var luaDir = Path.Combine(steam, "config", "lua");
        Directory.CreateDirectory(luaDir);
        File.WriteAllText(Path.Combine(luaDir, "588650.lua"),
            "local a = addappid(480)\nlocal b = addappid(113200)\n");
        try
        {
            Assert.True(SteamLocator.IsOstRedirected(steam, 480));
            Assert.True(SteamLocator.IsOstRedirected(steam, 113200));
            Assert.False(SteamLocator.IsOstRedirected(steam, 7));
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }

    [Fact]
    public void SyncPostureIsRealWithoutOst()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"sct_steam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(steam);
        try
        {
            Assert.Equal("real", SteamLocator.SyncPosture(steam, 588650));
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }

    [Fact]
    public void IsCloudRedirectLoadedNeedsToml()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"sct_steam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(steam);
        try
        {
            Assert.False(SteamLocator.IsCloudRedirectLoaded(steam));
            File.WriteAllText(Path.Combine(steam, "opensteamtool.toml"), "[cloud]\nenabled = true\nlibrary = \"cloud_redirect.dll\"\n");
            Assert.False(SteamLocator.IsCloudRedirectLoaded(steam)); // dll missing in root
            File.WriteAllText(Path.Combine(steam, "cloud_redirect.dll"), "");
            Assert.True(SteamLocator.IsCloudRedirectLoaded(steam));
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }
}

public class RegistryTests
{
    [Fact]
    public void LoadSaveRoundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sct_reg_{Guid.NewGuid():N}.json");
        try
        {
            var reg = new SctRegistry();
            reg.Upsert(GameSlot.New(588650, 480, "588650_user_0.dat", "user_0.dat", 12345, "588650|1|09082026"));
            reg.Save(path);

            var loaded = SctRegistry.Load(path);
            Assert.Equal(SctRegistry.MagicHeader, loaded.Header);
            Assert.Single(loaded.Slots);
            Assert.Equal(588650u, loaded.Slots[0].GameAppId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SyncDiscoveredPersistsSnapshot()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"sct_steam_{Guid.NewGuid():N}");
        var uid = 1201110076u;
        Directory.CreateDirectory(Path.Combine(steam, "userdata", uid.ToString(), "480", "remote"));
        Directory.CreateDirectory(Path.Combine(steam, "config", "lua"));
        File.WriteAllText(Path.Combine(steam, "config", "lua", "588650.lua"), "addappid(588650)\n");
        var path = Path.Combine(Path.GetTempPath(), $"sct_reg_{Guid.NewGuid():N}.json");
        try
        {
            var reg = new SctRegistry();
            reg.SyncDiscovered(steam, path: path);
            Assert.Contains(reg.Discovered, c => c.AppId == 480);
            Assert.Contains(reg.Discovered, c => c.AppId == 588650 && c.Source == ContainerSource.OstLua);

            var loaded = SctRegistry.Load(path);
            Assert.Contains(loaded.Discovered, c => c.AppId == 480);
            Assert.Contains(loaded.Discovered, c => c.AppId == 7);
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
            File.Delete(path);
        }
    }
}

public class PoolDiscovererTests
{
    private static string NewSteam()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"sct_steam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(steam);
        return steam;
    }

    [Fact]
    public void DiscoversPoolUserDataAndLuaContainers()
    {
        var steam = NewSteam();
        var uid = 1201110076u;
        Directory.CreateDirectory(Path.Combine(steam, "userdata", uid.ToString(), "480", "remote"));
        Directory.CreateDirectory(Path.Combine(steam, "userdata", uid.ToString(), "588650", "remote"));
        Directory.CreateDirectory(Path.Combine(steam, "config", "lua"));
        File.WriteAllText(Path.Combine(steam, "config", "lua", "hooks.lua"), "addappid(588650)\naddappid(113200)\n");
        try
        {
            var found = PoolDiscoverer.Discover(steam);
            Assert.Contains(found, c => c.AppId == 480 && c.Source == ContainerSource.PoolDb && c.Name == "Spacewar");
            Assert.Contains(found, c => c.AppId == 7 && c.Kind == ContainerKind.Hidden);
            Assert.Contains(found, c => c.AppId == 760 && c.Posture == "real"); // not hooked locally
            Assert.Contains(found, c => c.AppId == 113200 && c.Source == ContainerSource.OstLua);
            Assert.Contains(found, c => c.AppId == 588650 && c.Kind == ContainerKind.Activation && c.Posture == "redirected");
            Assert.All(found, c => Assert.True(c.AppId != 0, "no host marker without a toml"));
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }

    [Fact]
    public void AutoCloudedFlagComesFromCallback()
    {
        var steam = NewSteam();
        try
        {
            var found = PoolDiscoverer.Discover(steam, autoClouded: id => id == 480);
            Assert.True(found.First(c => c.AppId == 480).AutoClouded);
            Assert.False(found.First(c => c.AppId == 7).AutoClouded);
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }

    [Fact]
    public void DiscoversCloudRedirectHostMarker()
    {
        var steam = NewSteam();
        try
        {
            File.WriteAllText(Path.Combine(steam, "opensteamtool.toml"), "[cloud]\nenabled = true\nlibrary = \"cloud_redirect.dll\"\n");
            File.WriteAllText(Path.Combine(steam, "cloud_redirect.dll"), "");
            var found = PoolDiscoverer.Discover(steam);
            Assert.Contains(found, c => c.AppId == 0 && c.Source == ContainerSource.OstToml && c.Posture == "provider");
            Assert.All(found.Where(c => c.AppId != 0), c => Assert.Equal("provider", c.Posture)); // CR loaded = provider everywhere
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }

    [Fact]
    public void DiscoversSteamSettingsAndGreenLuma()
    {
        var steam = NewSteam();
        try
        {
            var game = Path.Combine(steam, "steamapps", "common", "SomeGame");
            Directory.CreateDirectory(Path.Combine(game, "steam_settings"));
            File.WriteAllText(Path.Combine(game, "steam_settings", "appid.txt"), "3164500");
            Directory.CreateDirectory(Path.Combine(steam, "GreenLuma_2024_0"));
            File.WriteAllLines(Path.Combine(steam, "GreenLuma_2024_0", "appidwhitelist.txt"), ["3722330", "1234567"]);

            var found = PoolDiscoverer.Discover(steam);
            Assert.Contains(found, c => c.AppId == 3164500 && c.Source == ContainerSource.Sls);
            Assert.Contains(found, c => c.AppId == 3722330 && c.Source == ContainerSource.GreenLuma);
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }

    [Fact]
    public void MissingSteamDirYieldsOnlyPoolContainers()
    {
        var steam = NewSteam();
        try
        {
            var found = PoolDiscoverer.Discover(steam);
            Assert.All(found, c => Assert.Equal(ContainerSource.PoolDb, c.Source));
            Assert.Equal(PoolDb.DefaultPool.Count, found.Count);
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }
}

public class CloudProxyTests
{
    [Fact]
    public void ApplyThenTryStripRoundTrips()
    {
        var wire = CloudProxy.Apply(113200, "save.sav");
        Assert.Equal("sls-113200/save.sav", wire);
        Assert.True(CloudProxy.TryStrip(wire, out var game, out var stripped));
        Assert.Equal(113200u, game);
        Assert.Equal("save.sav", stripped);
    }

    [Fact]
    public void TryStripRejectsUnprefixedOrMalformedNames()
    {
        Assert.False(CloudProxy.TryStrip("save.sav", out _, out _));
        Assert.False(CloudProxy.TryStrip("sls-/save.sav", out _, out _));
        Assert.False(CloudProxy.TryStrip("sls-abc/save.sav", out _, out _));
        Assert.False(CloudProxy.TryStrip("sls-113200", out _, out _));
    }

    [Fact]
    public void FilterToGameDropsForeignNamespaces()
    {
        var all = new[] { "sls-113200/a", "sls-91330/b", "sls-113200/c", "plain.txt" };
        var mine = CloudProxy.FilterToGame(all, 113200, n => n, (n, s) => s).ToList();
        Assert.Equal(["a", "c"], mine);
    }

    [Fact]
    public void ResolveProxyPrefersPerGameThenDefaultThenNone()
    {
        var cfg = new AppConfig { CloudProxies = { [0] = 480, [113200] = 588650 } };
        Assert.Equal(588650u, cfg.ResolveProxy(113200));
        Assert.Equal(480u, cfg.ResolveProxy(91330)); // no own mapping -> default proxy
        Assert.Equal(0u, new AppConfig().ResolveProxy(113200)); // no map at all
    }

    [Fact]
    public void CloudProxiesSurviveConfigRoundTrip()
    {
        var cfg = new AppConfig { CloudProxies = { [0] = 480, [113200] = 588650 } };
        var path = Path.Combine(Path.GetTempPath(), $"sct_cfg_{Guid.NewGuid():N}.json");
        try
        {
            cfg.Save(path);
            var loaded = AppConfig.Load(path);
            Assert.Equal(480u, loaded.CloudProxies[0]);
            Assert.Equal(588650u, loaded.CloudProxies[113200]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ProxyParkingTests
{
    [Fact]
    public void ProxyBucketParksEveryFileIntoOwnedProxy()
    {
        var engine = new ParkingEngine(new HashSet<uint> { 480 }, []);
        var d = engine.Plan(113200, [new ParkFile("save.sav", 2048)], proxyBucket: 480);
        Assert.Single(d);
        Assert.True(d[0].Ok);
        Assert.Equal(480u, d[0].StorageAppId);
        Assert.Equal("113200_save.sav", d[0].StoredName);
    }

    [Fact]
    public void ProxyBucketRefusedWhenProxyNotOwned()
    {
        var engine = new ParkingEngine([], []);
        var d = engine.Plan(113200, [new ParkFile("save.sav", 2048)], proxyBucket: 480);
        Assert.False(d[0].Ok);
        Assert.Contains("owned", d[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverEmitsProxyContainerRows()
    {
        var steam = Path.Combine(Path.GetTempPath(), $"sct_steam_{Guid.NewGuid():N}");
        Directory.CreateDirectory(steam);
        try
        {
            var found = PoolDiscoverer.Discover(steam,
                proxies: new Dictionary<uint, uint> { [113200] = 480, [91330] = 480, [0] = 480 });
            var row = found.First(c => c.AppId == 480);
            Assert.Equal(ContainerSource.Proxy, row.Source);
            Assert.Equal("proxied", row.Posture);
            Assert.Contains("113200", row.Note);
            Assert.Contains("91330", row.Note);
        }
        finally
        {
            Directory.Delete(steam, recursive: true);
        }
    }
}