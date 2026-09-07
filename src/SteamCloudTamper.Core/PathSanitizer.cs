namespace SteamCloudTamper.Core;

/// <summary>
/// Sanitizes filenames received from the cloud wire or registry to prevent
/// path traversal attacks (e.g. "../secret.txt" writing outside the intended dir).
/// A tampered registry.json or shared/co-tenant bucket could supply malicious names.
/// </summary>
public static class PathSanitizer
{
    /// <summary>
    /// Returns true when the filename is safe to use as a direct child of a target directory.
    /// Rejects: path separators, UNC paths, drive letters, null bytes, reserved Windows names,
    /// and any component that is "." or "..".
    /// </summary>
    public static bool IsSafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains('\0')) return false;

        // reject any path separator (Windows + Unix)
        if (name.Contains('/') || name.Contains('\\')) return false;

        // reject UNC paths and drive letters
        if (name.StartsWith("\\\\") || name.Length >= 2 && name[1] == ':') return false;

        // reject reserved Windows device names (CON, PRN, NUL, COM1, LPT1, etc.)
        var upper = name.ToUpperInvariant().Split('.')[0];
        if (ReservedNames.Contains(upper)) return false;

        // reject "." and ".." components
        foreach (var part in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            // a part that is empty means double-dot was present
        }
        if (name == "." || name == "..") return false;

        return true;
    }

    /// <summary>
    /// Sanitizes a filename: strips traversal sequences and trims to a safe basename.
    /// Returns null if the name cannot be salvaged (empty after sanitization).
    /// </summary>
    public static string? SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // strip directory components -- we only want the basename
        var baseName = Path.GetFileName(name);
        if (string.IsNullOrEmpty(baseName) || baseName == "." || baseName == "..")
            return null;

        // strip any remaining separator characters that Path.GetFileName might not catch
        baseName = baseName.Replace("/", "").Replace("\\", "");

        // reject reserved names
        var upper = baseName.ToUpperInvariant().Split('.')[0];
        if (ReservedNames.Contains(upper)) return null;

        // reject null bytes
        if (baseName.Contains('\0')) return null;

        return baseName.Length > 0 ? baseName : null;
    }

    /// <summary>
    /// Ensures <paramref name="targetPath"/> is inside <paramref name="baseDir"/>.
    /// Returns the resolved safe path, or throws if traversal is detected.
    /// </summary>
    public static string ResolveInside(string baseDir, string targetPath)
    {
        var fullBase = Path.GetFullPath(baseDir);
        var fullTarget = Path.GetFullPath(targetPath);
        if (!fullTarget.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Path traversal detected: '{targetPath}' resolves outside the intended directory '{baseDir}'");
        return fullTarget;
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "CLOCK$", "CONFIG$",
    };
}
