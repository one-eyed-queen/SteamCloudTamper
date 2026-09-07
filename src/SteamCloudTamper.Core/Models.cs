namespace SteamCloudTamper.Core;

public enum Era
{
    Unknown,
    Owned,
    AnonymousDump760,
    GreenLumaRealAppId,
    Emulated,
    OnlineFix,
    Client
}

public sealed record CloudFileEntry(
    uint AppId,
    string FileName,
    ulong UgcId,
    long FileSize,
    long Timestamp,
    string? FileSha,
    string? Url)
{
    public static CloudFileEntry From(string fileName) => new(0, fileName, 0, 0, 0, null, null);
}

public sealed record Bucket(uint AppId, Era Era, string Note, List<CloudFileEntry> Files)
{
    public long TotalBytes => Files.Sum(f => f.FileSize);
}

public sealed record SteamAccount(uint AccountId, ulong SteamId, string? DisplayName)
{
    public const ulong SteamIdBase = 76561197960265728;

    public static SteamAccount FromId3(uint id3) => new(id3, SteamIdFor(id3), null);

    public static ulong SteamIdFor(uint id3) => SteamIdBase + id3;

    public static uint Id3FromSteamId(ulong steamId) => steamId > SteamIdBase ? (uint)(steamId - SteamIdBase) : 0;
}

public sealed record LocalAppState(
    uint AppId,
    string UserDataDir,
    string? RemoteCachePath,
    string? RemoteDir,
    long RemoteBytes,
    int RemoteFileCount,
    uint AccountId3 = 0);

public enum WipeAction { Delete, BlankReset }

public sealed record WipeTarget(uint AppId, string FileName, WipeAction Action, bool IsOwned);

public sealed record WipeOutcome(string AppId, string FileName, string Action, string Result, bool Success);

public enum Policy
{
    Allowed,
    Denied,
    FileNotFound,
    Unknown
}

public sealed record ProbeVerdict(uint AppId, Policy Enumerate, Policy Upload, Policy Delete, string? Detail);

public sealed record Quota(uint AppId, uint ExistingFiles, ulong ExistingBytes, uint MaxFiles, ulong MaxBytes)
{
    public double UsedFraction => MaxBytes > 0 ? (double)ExistingBytes / MaxBytes : 0;
}

public sealed record GuardEntry(uint AppId, DateTime GuardedAt, string Reason);