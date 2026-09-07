using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Vdf;

namespace SteamCloudTamper.Engines;

public sealed class LocalInjectEngine
{
    public event Action<string>? Log;

    public string InjectFile(string userDataAppDir, string sourcePath, string? remoteName = null)
    {
        var remote = Path.Combine(userDataAppDir, "remote");
        Directory.CreateDirectory(remote);

        var name = remoteName ?? Path.GetFileName(sourcePath);
        var safeName = Core.PathSanitizer.SanitizeFileName(name);
        if (safeName is null)
            throw new InvalidOperationException($"filename '{name}' failed path traversal check");
        name = safeName;

        var dest = Path.Combine(remote, name);
        Core.PathSanitizer.ResolveInside(remote, dest);
        File.Copy(sourcePath, dest, overwrite: true);

        RegenerateVdf(userDataAppDir);
        Log?.Invoke($"Injected {sourcePath} -> {dest}");
        return dest;
    }

    public void WriteBlank(string userDataAppDir, string remoteName, byte[]? template = null)
    {
        var remote = Path.Combine(userDataAppDir, "remote");
        Directory.CreateDirectory(remote);
        File.WriteAllBytes(Path.Combine(remote, remoteName), BlankSaveKit.CreateBlank(remoteName, template));
        RegenerateVdf(userDataAppDir);
        Log?.Invoke($"Blank written to {remoteName}");
    }

    public void RegenerateVdf(string userDataAppDir)
    {
        var remote = Path.Combine(userDataAppDir, "remote");
        var vdfPath = Path.Combine(userDataAppDir, "remotecache.vdf");
        RemotecacheVdf.WriteTo(vdfPath, remote);
        Log?.Invoke($"Regenerated {Path.GetFileName(vdfPath)}");
    }

    public string? RemoveBucket(string userDataAppDir, bool keepBackup = true)
    {
        if (!Directory.Exists(userDataAppDir)) return null;

        string? backupDir = null;
        if (keepBackup)
        {
            backupDir = Path.Combine(
                Path.GetDirectoryName(userDataAppDir)!,
                $"backup_{Path.GetFileName(userDataAppDir)}_{DateTime.Now:yyyyMMdd_HHmmss}");
            CopyDirectory(userDataAppDir, backupDir);
            Log?.Invoke($"Backed up to {backupDir}");
        }

        foreach (var d in Directory.EnumerateDirectories(userDataAppDir)) Directory.Delete(d, true);
        foreach (var f in Directory.EnumerateFiles(userDataAppDir)) File.Delete(f);
        Log?.Invoke($"Cleared {userDataAppDir}");
        return backupDir;
    }

    public const string LockMarker = "SCT_LOCKOUT\n";

    public string? InstallLock(string userDataDir, uint appId)
    {
        if (!Directory.Exists(userDataDir))
        {
            Log?.Invoke($"userdata dir missing: {userDataDir}");
            return null;
        }

        var path = Path.Combine(userDataDir, appId.ToString());

        if (Directory.Exists(path))
        {
            var backup = Path.Combine(userDataDir, $"backup_{appId}_{DateTime.Now:yyyyMMdd_HHmmss}");
            CopyDirectory(path, backup);
            ClearBucketDir(path);
            Log?.Invoke($"Moved {appId} files to {backup}");
        }

        if (File.Exists(path))
        {
            Log?.Invoke($"Lock file already present for {appId}");
            return null;
        }

        File.WriteAllText(path, LockMarker);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Log?.Invoke($"Locked {appId} (read-only file blocks folder re-creation)");
        return path;
    }

    public bool RemoveLock(string userDataDir, uint appId)
    {
        var path = Path.Combine(userDataDir, appId.ToString());
        if (!File.Exists(path)) return false;

        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
        Log?.Invoke($"Unlocked {appId}");
        return true;
    }

    /// <summary>
    /// Junction-based isolation: moves the bucket dir to a private stash and puts
    /// a directory junction named {appId} in its place. Steam reads/writes through
    /// the junction and lands in the stash - nothing ever reaches the real
    /// userdata folder. No admin required for junctions.
    /// Returns the stash path.
    /// </summary>
    public string? Relocate(string userDataDir, uint appId, string? stashRoot = null)
    {
        var appDir = Path.Combine(userDataDir, appId.ToString());
        stashRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCT", "stash");
        Directory.CreateDirectory(stashRoot);
        var stash = Path.Combine(stashRoot, appId.ToString());

        if (Directory.Exists(appDir))
        {
            Directory.Move(appDir, stash);
            Log?.Invoke($"Moved bucket {appId} -> {stash}");
        }
        else if (Directory.Exists(stash))
        {
            Log?.Invoke($"Bucket {appId} already relocated");
        }

        if (Directory.Exists(appDir) || new FileInfo(appDir).Exists) return null;

        // Create junction: cmd mklink /J (allowed without admin for dirs)
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{appDir}\" \"{stash}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit();
        var ok = p is not null && p.ExitCode == 0 && Directory.Exists(appDir);
        if (!ok) Log?.Invoke("junction creation failed (run terminal as admin or enable Developer Mode)");
        else Log?.Invoke($"Junction {appId} -> {stash}");
        return ok ? stash : null;
    }

    public bool Unrelocate(string userDataDir, uint appId)
    {
        var appDir = Path.Combine(userDataDir, appId.ToString());
        var stash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCT", "stash", appId.ToString());

        if (!Directory.Exists(appDir)) return false;
        var info = new FileInfo(appDir);
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            Log?.Invoke($"{appId} is not a junction");
            return true;
        }

        Directory.Delete(appDir); // deletes junction link only
        if (Directory.Exists(stash)) Directory.Move(stash, appDir);
        Log?.Invoke($"Removed junction, restored {appId}");
        return true;
    }

    private static void ClearBucketDir(string dir)
    {
        foreach (var d in Directory.EnumerateDirectories(dir)) Directory.Delete(d, true);
        foreach (var f in Directory.EnumerateFiles(dir)) File.Delete(f);
        Directory.Delete(dir, false);
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, f);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }
}