namespace SteamCloudTamper.Core;

public sealed record RootLocation(int Id, string Name, string Windows, string Mac, string Linux, bool Verified);

public static class RootPathMap
{
    public static readonly RootLocation[] All =
    [
        new(0, "Steam Cloud", "{Steam}/userdata/{uid}/{AppID}/remote/", "{Steam}/userdata/{uid}/{AppID}/remote/", "{Steam}/userdata/{uid}/{AppID}/remote/", true),
        new(1, "GameInstall", "{Steam}/steamapps/common/{Game}/", "{Steam}/steamapps/common/{Game}/", "{Steam}/steamapps/common/{Game}/", true),
        new(2, "Documents", "%USERPROFILE%/Documents/", "~/Documents/", "~/Documents/", true),
        new(3, "AppData Roaming", "%APPDATA%/", "~/Library/Application Support/", "~/.config/", false),
        new(4, "AppData Local", "%LOCALAPPDATA%/", "~/Library/Caches/", "~/.local/share/", false),
        new(5, "Pictures", "%USERPROFILE%/Pictures/", "~/Pictures/", "~/Pictures/", false),
        new(6, "Music", "%USERPROFILE%/Music/", "~/Music/", "~/Music/", false),
        new(7, "Videos", "%USERPROFILE%/Videos/", "~/Library/Application Support/", "~/Videos/", false),
        new(8, "Desktop", "%USERPROFILE%/Desktop/", "~/Desktop/", "~/Desktop/", false),
        new(9, "Saved Games", "%USERPROFILE%/Saved Games/", "~/Documents/Saved Games/", "~/Documents/Saved Games/", true),
        new(10, "Downloads", "%USERPROFILE%/Downloads/", "~/Downloads/", "~/Downloads/", false),
        new(11, "Public", "%PUBLIC%/", "/Users/Shared/", "/tmp/", false),
        new(12, "AppData LocalLow", "%LOCALAPPDATA%/Low/", "~/Library/Caches/", "~/.local/share/", true),
    ];

    public static RootLocation? Find(int id) => All.FirstOrDefault(r => r.Id == id);

    public static string ResolveWin(int id, string steamPath, uint uid, uint appId, string gameDir = "")
    {
        var r = Find(id) ?? throw new ArgumentOutOfRangeException(nameof(id), $"Unknown root {id}");
        return r.Windows
            .Replace("%USERPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .Replace("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
            .Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .Replace("%PUBLIC%", Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments))
            .Replace("{Steam}", steamPath)
            .Replace("{uid}", uid.ToString())
            .Replace("{AppID}", appId.ToString())
            .Replace("{Game}", gameDir);
    }
}

public static class EraDetector
{
    public const uint ScreenshotsApp = 760;

    public static Era Classify(uint appId, IReadOnlySet<uint>? owned = null, IReadOnlyDictionary<string, string>? hints = null)
    {
        if (appId == ScreenshotsApp) return Era.SteamTools760;
        if (owned is not null && owned.Contains(appId)) return Era.Owned;
        if (hints is not null && hints.TryGetValue(appId.ToString(), out var h)) return h.ToLowerInvariant() switch
        {
            "emu" or "emulated" => Era.Emulated,
            "onlinefix" => Era.OnlineFix,
            "client" => Era.Client,
            "owned" => Era.Owned,
            _ => Era.GreenLumaRealAppId
        };
        return Era.Unknown;
    }
}