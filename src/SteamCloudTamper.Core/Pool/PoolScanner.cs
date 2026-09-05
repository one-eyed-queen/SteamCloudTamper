namespace SteamCloudTamper.Core.Pool;

public sealed record TaggedFile(
    uint GameAppId,
    uint StorageAppId,
    uint AccountId3,
    string FileName,
    long Size,
    string? UserId3,
    DateOnly? TaggedOn);

/// <summary>
/// Tail-scan of local userdata: reads only the LAST bytes of each bucket file
/// (barcode trailers live at file end) so even 1000+ files take well under a second.
/// This is the "first startup, no local info" flow: rebuilds the registry from
/// whatever the parked files themselves say.
/// </summary>
public static class PoolScanner
{
    /// <summary>Scans one user's userdata dir; returns tagged files + elapsed ms.</summary>
    public static (List<TaggedFile> Tagged, long ElapsedMs) ScanUserData(string userDataDir, uint accountId3)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = new List<TaggedFile>();

        if (!Directory.Exists(userDataDir)) return (found, sw.ElapsedMilliseconds);

        foreach (var appDir in Directory.EnumerateDirectories(userDataDir))
        {
            var name = Path.GetFileName(appDir);
            if (!uint.TryParse(name, out var storageAppId)) continue; // config/, ugcmsgcache/, ...
            ScanAppDir(appDir, storageAppId, accountId3, found);
        }

        sw.Stop();
        return (found, sw.ElapsedMilliseconds);
    }

    /// <summary>Files live at the bucket root and/or in remote/ (parking + Steam both use it).</summary>
    private static void ScanAppDir(string appDir, uint storageAppId, uint accountId3, List<TaggedFile> found)
    {
        var remote = Path.Combine(appDir, "remote");
        var dirs = new List<string> { appDir };
        if (Directory.Exists(remote) && !string.Equals(remote, appDir, StringComparison.OrdinalIgnoreCase))
            dirs.Add(remote);

        foreach (var dir in dirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(file);
                if (info.Length < Barcode.TrailerOverheadBytes + 8) continue;
                if (info.Name.Equals("remotecache.vdf", StringComparison.OrdinalIgnoreCase)) continue;

                var tail = ReadTail(file, Math.Min(info.Length, Barcode.TailWindowBytes));
                if (!Barcode.TryDecodeTail(tail, out var payload, out _)) continue;
                var (game, uid, date) = Barcode.Parse(payload);
                if (game == 0) continue;
                if (uid == "probe") continue; // pool probe marker file - not a parked save

                found.Add(new TaggedFile(game, storageAppId, accountId3, info.Name, info.Length, uid, date));
            }
        }
    }

    /// <summary>Scans the whole userdata root (all accounts) into the registry.</summary>
    public static SctRegistry RebuildRegistry(string steamPath, SctRegistry? existing = null)
    {
        var reg = existing ?? new SctRegistry();
        var baseline = reg.Slots.Count;

        var userDataRoot = Path.Combine(steamPath, "userdata");
        if (!Directory.Exists(userDataRoot)) return reg;

        foreach (var accountDir in Directory.EnumerateDirectories(userDataRoot))
        {
            if (!uint.TryParse(Path.GetFileName(accountDir), out var account3)) continue;
            var (tagged, _) = ScanUserData(accountDir, account3);
            foreach (var t in tagged)
            {
                // rebuild the barcode payload faithfully (both seps included, even when uid/date are absent)
                var payload = $"{t.GameAppId}{Barcode.Sep}{t.UserId3 ?? string.Empty}{Barcode.Sep}{(t.TaggedOn.HasValue ? t.TaggedOn.Value.ToString("ddMMyyyy") : string.Empty)}";
                reg.Upsert(new GameSlot(
                    t.GameAppId, t.StorageAppId, t.FileName,
                    Ferry.UnparkName(t.FileName).OriginalName,
                    t.Size, DateTime.UtcNow, payload,
                    "scanned"));
            }
        }

        return reg;
    }

    private static byte[] ReadTail(string path, long count)
    {
        if (count <= 0) return [];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = fs.Length;
        if (length <= 0) return []; // seek(-0, End) would throw, and empty files never carry a trailer
        fs.Seek(-Math.Min(count, length), SeekOrigin.End); // clamp: file may have been truncated mid-tail
        var buf = new byte[Math.Min(count, length)];
        fs.ReadExactly(buf);
        return buf;
    }
}