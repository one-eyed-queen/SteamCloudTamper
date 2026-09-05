using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SteamCloudTamper.Core.Steam;

/// <summary>
/// Drives the Steam in-client debug console (steam://open/console).
/// This is how the client lane FORCES a cloud sync for a bucket without launching
/// the game: 'cloud_sync_up <appid>' makes the running client run its cloud
/// synchronization right now, so SCT can stage a file, push the command, and read
/// the verdict from cloud_log.txt within seconds.
/// Data still flows through the official client -> real UFS servers (or through
/// CloudRedirect for OST-redirected apps) - SCT only presses the button.
/// </summary>
public sealed class SteamConsole
{
    private const int WmChar = 0x0102;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmGetText = 0x000D;
    private const int WmGetTextLength = 0x000E;
    private const ushort VkReturn = 0x0D;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    /// <summary>Finds the "Steam Console" window, if the client has it open.</summary>
    public static IntPtr FindConsoleWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            var sb = new StringBuilder(256);
            GetWindowText(hWnd, sb, sb.Capacity);
            if (sb.ToString().Equals("Steam Console", StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Opens (or focuses) the Steam console via the steam://open/console URI.
    /// Returns true once the window exists. Steam must be running.
    /// </summary>
    public static async Task<bool> OpenConsoleAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (FindConsoleWindow() != IntPtr.Zero) return true;

        try
        {
            Process.Start(new ProcessStartInfo("steam://open/console") { UseShellExecute = true });
        }
        catch
        {
            return false;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (FindConsoleWindow() != IntPtr.Zero) return true;
            try { await Task.Delay(700, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return FindConsoleWindow() != IntPtr.Zero;
    }

    /// <summary>
    /// Types a command into the console and presses Enter. ASCII-only commands
    /// (cloud_sync_up/down <appid>, etc.) delivered via WM_CHAR so no UI
    /// automation / focus stealing is needed.
    /// </summary>
    public static bool SendCommand(string command)
    {
        var hwnd = FindConsoleWindow();
        if (hwnd == IntPtr.Zero) return false;

        SetForegroundWindow(hwnd);
        foreach (var ch in command)
        {
            SendMessage(hwnd, WmChar, ch, IntPtr.Zero);
        }
        SendMessage(hwnd, WmKeyDown, VkReturn, IntPtr.Zero);
        SendMessage(hwnd, WmKeyUp, VkReturn, IntPtr.Zero);
        return true;
    }

    public static bool IsOpen()
    {
        // take the handle once - the old double EnumWindows raced the window closing mid-check
        var hwnd = FindConsoleWindow();
        return hwnd != IntPtr.Zero && IsWindow(hwnd);
    }
}