using System.Text;
using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Pool;

namespace SteamCloudTamper.Core.Tests;

public class CloudLogTailTests
{
    private static string TempLog(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sct_log_{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, string.Join("", lines));
        return path;
    }

    [Fact]
    public void AdvancesWatermarkByExactBytes_AndSkipsPartialTrailingLine()
    {
        var path = TempLog(
            "00 info unrelated line\r\n",
            "05 2026-09-05 [AppID 480] cloud_sync_up success\r\n",
            "06 2026-09-05 [AppID 480] still writing-no-newline-yet");

        var wm = 0L;
        var lines1 = CloudLogTail.ReadMatchingLines(path, ref wm, 480);

        Assert.Single(lines1);
        Assert.Contains("[AppID 480] cloud_sync_up success", lines1[0]);
        // two complete lines (incl. CRLF) consumed; the partial trailing line must stay
        var expected = Encoding.UTF8.GetByteCount("00 info unrelated line\r\n")
            + Encoding.UTF8.GetByteCount("05 2026-09-05 [AppID 480] cloud_sync_up success\r\n");
        Assert.Equal(expected, wm);

        var again = CloudLogTail.ReadMatchingLines(path, ref wm, 480);
        Assert.Empty(again); // nothing re-read
        Assert.Equal(expected, wm);

        var length = new FileInfo(path).Length;
        Assert.True(wm <= length); // never overshoots EOF
    }

    [Fact]
    public void NeverReReadsOldMatch_WhenOtherAppIdsAppend()
    {
        var path = TempLog("01 [AppID 480] first\r\n");

        var wm = 0L;
        var lines1 = CloudLogTail.ReadMatchingLines(path, ref wm, 480);
        Assert.Single(lines1);

        File.AppendAllText(path, "02 [AppID 730] other game\r\n03 [AppID 480] second\r\n");
        var lines2 = CloudLogTail.ReadMatchingLines(path, ref wm, 480);

        Assert.Single(lines2);
        Assert.Contains("second", lines2[0]); // must NOT return "first" again
    }

    [Fact]
    public void CompletedPartialLine_IsDeliveredOnNextPoll()
    {
        var path = TempLog("01 [AppID 480] half-written");

        var wm = 0L;
        Assert.Empty(CloudLogTail.ReadMatchingLines(path, ref wm, 480));

        File.AppendAllText(path, " plus\r\n");
        var done = CloudLogTail.ReadMatchingLines(path, ref wm, 480);

        Assert.Single(done);
        Assert.Contains("half-written plus", done[0]);
    }

    [Fact]
    public void MissingFile_NoOp()
    {
        var wm = 0L;
        var path = Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.txt");
        Assert.Empty(CloudLogTail.ReadMatchingLines(path, ref wm, 480));
        Assert.Equal(0L, wm);
    }
}

public class AppConfigPersistenceTests
{
    [Fact]
    public void GuardMutationsPersistViaSourcePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sct_cfg_{Guid.NewGuid():N}.json");
        var cfg = AppConfig.Load(path);
        cfg.GuardedAppIds.Add(480);
        cfg.CloudProxies[0] = 480;
        cfg.Save();

        var reloaded = AppConfig.Load(path);
        Assert.Contains(480u, reloaded.GuardedAppIds);
        Assert.Equal(480u, reloaded.CloudProxies[0]);
        Assert.Equal(path, reloaded.SourcePath);
        File.Delete(path);
    }

    [Fact]
    public void DefaultPathHonorsSctConfigEnv()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"sct_env_{Guid.NewGuid():N}.json");
        var prev = Environment.GetEnvironmentVariable("SCT_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("SCT_CONFIG", fake);
            Assert.Equal(fake, AppConfig.ResolveDefaultPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCT_CONFIG", prev);
        }
    }
}

public class ParkingEngineConcurrencyTests
{
    [Fact]
    public void RemoteProbesAreCachedPerEngine_NotReHit()
    {
        var hits = new List<uint>();
        object gate = new();
        var engine = new ParkingEngine([], [],
            remoteProbe: async appId =>
            {
                lock (gate) hits.Add(appId);
                await Task.Delay(5);
                return new RemoteBucketSnapshot(appId, [], null);
            });

        _ = engine.Pick(91330, "save.sav", 1024);
        _ = engine.Pick(91330, "save.sav", 1024);

        // the same candidate universe is probed once and cached
        Assert.Equal(hits.Distinct().Count(), hits.Count);
        Assert.True(hits.Count > 0);
    }

    [Fact]
    public async Task ProbesAreFannedOutBeforeFirstConsumed()
    {
        Assert.True(PoolDb.Usable().Count() > 1, "pool must expose >1 candidate for this test to mean anything");
        var entered = 0;
        var barrier = new TaskCompletionSource();
        var engine = new ParkingEngine([], [],
            remoteProbe: _ =>
            {
                var first = Interlocked.Increment(ref entered) == 1;
                return first ? barrier.Task.ContinueWith(_ => (RemoteBucketSnapshot?)null)
                             : Task.FromResult<RemoteBucketSnapshot?>(null);
            });

        var task = Task.Run(() => engine.Pick(91330, "save.sav", 1024));
        try
        {
            // deterministic: hold the first probe, then assert a SECOND probe entered.
            // serial probing would leave entered at exactly 1 forever.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && Volatile.Read(ref entered) < 2)
                await Task.Delay(20);
            Assert.True(Volatile.Read(ref entered) >= 2, "candidate probes must run concurrently, not serially");
        }
        finally
        {
            barrier.SetResult();
        }
        await task;
    }
}

public class VerifySlotTests
{
    [Fact]
    public void WithVerifyFlagsTheSlot()
    {
        var slot = GameSlot.New(588650, 480, "sls-588650/save.bin", "save.bin", 42, "588650|1|01012026");
        Assert.Null(slot.Verified);
        Assert.True(slot.WithVerify(true).Verified);
    }
}

public class PathSanitizerTests
{
    [Theory]
    [InlineData("save.dat", true)]
    [InlineData("my_game_save.json", true)]
    [InlineData("test-file_v2.cfg", true)]
    [InlineData("simple", true)]
    public void IsSafeFileName_AcceptsValidNames(string name, bool expected)
    {
        Assert.Equal(expected, PathSanitizer.IsSafeFileName(name));
    }

    [Theory]
    [InlineData("../etc/passwd", false)]
    [InlineData("foo/bar.txt", false)]
    [InlineData("foo\\bar.txt", false)]
    [InlineData("\\\\server\\share", false)]
    [InlineData("C:\\Windows\\system32", false)]
    [InlineData("CON", false)]
    [InlineData("NUL.txt", false)]
    [InlineData("COM1", false)]
    [InlineData("LPT1.dat", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    public void IsSafeFileName_RejectsMaliciousNames(string name, bool expected)
    {
        Assert.Equal(expected, PathSanitizer.IsSafeFileName(name));
    }

    [Theory]
    [InlineData("save.dat", "save.dat")]
    [InlineData("path/to/save.dat", "save.dat")]
    [InlineData("path\\to\\save.dat", "save.dat")]
    [InlineData("../evil.txt", "evil.txt")]
    [InlineData("normal_file.json", "normal_file.json")]
    public void SanitizeFileName_ExtractsSafeBasename(string input, string expected)
    {
        Assert.Equal(expected, PathSanitizer.SanitizeFileName(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CON")]
    [InlineData("NUL")]
    public void SanitizeFileName_ReturnsNullForUnsalvageable(string input)
    {
        Assert.Null(PathSanitizer.SanitizeFileName(input));
    }

    [Fact]
    public void ResolveInside_ThrowsOnTraversal()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "sct_test_base");
        var safePath = Path.Combine(baseDir, "subdir", "file.txt");
        var traversalPath = Path.Combine(baseDir, "..", "outside", "file.txt");
        // a sibling whose name merely STARTS WITH the base dir must NOT pass
        var siblingPath = Path.Combine(Path.GetTempPath(), "sct_test_base-evil", "file.txt");

        // safe path should resolve without throwing
        var resolved = PathSanitizer.ResolveInside(baseDir, safePath);
        Assert.StartsWith(Path.GetFullPath(baseDir), resolved);

        // traversal path should throw
        Assert.Throws<InvalidOperationException>(() => PathSanitizer.ResolveInside(baseDir, traversalPath));

        // sibling prefix should throw too (boundary-aware containment)
        Assert.Throws<InvalidOperationException>(() => PathSanitizer.ResolveInside(baseDir, siblingPath));
    }
}

public class PostureScoreTests
{
    [Fact]
    public void NullPostureCountsAsReal()
    {
        Assert.Equal(10, PoolScoring.PostureScore(null, false, null));
    }

    [Fact]
    public void RealPostureScoresPositive()
    {
        Assert.True(PoolScoring.PostureScore("real", false, null) > 0);
    }

    [Fact]
    public void ProviderPostureIsPenalized()
    {
        // provider = CloudRedirect folder, should be penalized like redirected
        Assert.Equal(-60, PoolScoring.PostureScore("provider", false, null));
    }

    [Fact]
    public void RedirectedPostureIsPenalized()
    {
        Assert.Equal(-60, PoolScoring.PostureScore("redirected", false, null));
    }

    [Fact]
    public void ProxiedPostureIsPenalized()
    {
        Assert.Equal(-60, PoolScoring.PostureScore("proxied", false, null));
    }

    [Fact]
    public void VerifiedWritableBeatsAutoClouded()
    {
        var verified = PoolScoring.PostureScore("real", false, "VerifiedWritable");
        var autoClouded = PoolScoring.PostureScore("real", true, null);
        Assert.True(verified > autoClouded);
    }
}

public class RootPathMapTests
{
    [Fact]
    public void ResolveWin_SteamCloudPathContainsAppId()
    {
        var path = RootPathMap.ResolveWin(0, @"C:\Steam", 12345, 588650);
        Assert.Contains("588650", path);
        Assert.Contains("12345", path);
        Assert.Contains("C:\\Steam", path);
        Assert.DoesNotContain("{AppID}", path);
        Assert.DoesNotContain("{appid}", path);
    }

    [Fact]
    public void ResolveWin_AllPlaceholdersAreReplaced()
    {
        var path = RootPathMap.ResolveWin(0, @"D:\Games\Steam", 99999, 12345);
        Assert.DoesNotContain("{", path);
        Assert.DoesNotContain("}", path);
    }
}