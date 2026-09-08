using System.Net;
using System.Text.RegularExpressions;

namespace SteamCloudTamper.Engines;

public sealed record RemoteAppRow(uint AppId, string Name, int FileCount, long TotalBytes);

public sealed record RemoteFileRow(string FileName, long Size, string? Detail);

/// <summary>
/// Web lane against the Steam account RemoteStorage pages:
/// https://store.steampowered.com/account/remotestorage  (per-game file listings)
/// Replacement for (blocked) Cloud UFS read of unowned buckets. Read-only by design.
/// </summary>
public sealed class SteamWebClient
{
    private readonly HttpClient _http;

    public SteamWebClient(string? sessionCookie = null)
    {
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false });
        if (!string.IsNullOrEmpty(sessionCookie))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", sessionCookie);
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36");
    }

    public async Task<List<RemoteAppRow>> ListAppsAsync(CancellationToken ct = default)
    {
        var html = await GetStringAsync("https://store.steampowered.com/account/remotestorage", ct);

        var rows = new List<RemoteAppRow>();
        foreach (Match m in Regex.Matches(html, @"href=""?[^""]*remotestorage[^""]*\?appid=(\d+)[^""]*""?"))
        {
            var appId = uint.Parse(m.Groups[1].Value);
            if (rows.Any(r => r.AppId == appId)) continue;
            var nameTxt = Regex.Match(html[m.Index..Math.Min(html.Length, m.Index + 512)],
                @"<a[^>]*>([^<]+)</a>");
            rows.Add(new RemoteAppRow(appId, nameTxt.Groups[1].Value.Trim(), 0, 0));
        }

        return rows;
    }

    public async Task<List<RemoteFileRow>> ListFilesAsync(uint appId, CancellationToken ct = default)
    {
        var html = await GetStringAsync($"https://store.steampowered.com/account/remotestorageapp/?appid={appId}", ct);

        var files = new List<RemoteFileRow>();
        foreach (Match m in Regex.Matches(html, @"filename\]\s*=\s*['""]([^'""]+)['""]|id=""file_(\d+)""[^>]*>\s*([^<]+)<"))
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
            if (files.Any(f => f.FileName == name)) continue;
            files.Add(new RemoteFileRow(name, 0, m.Groups[2].Success ? m.Groups[2].Value : null));
        }
        if (files.Count == 0)
        {
            foreach (Match m in Regex.Matches(html, @"<a[^>]+href=""([^""]*download[^""]*)""[^>]*>([^<]+)</a>|<td[^>]*class=""name_col""[^>]*>([^<]+)</td>"))
            {
                var name = m.Groups[2].Success ? m.Groups[2].Value.Trim() : m.Groups[3].Value.Trim();
                if (!string.IsNullOrEmpty(name) && files.All(f => f.FileName != name))
                    files.Add(new RemoteFileRow(name, 0, null));
            }
        }

        return files;
    }

    public async Task<byte[]?> DownloadAsync(uint appId, string remotePath, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync(
                $"https://store.steampowered.com/account/remotestorageapp/?appid={appId}&filepath={Uri.EscapeDataString(remotePath)}", ct);
            if (!resp.IsSuccessStatusCode) return null;

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            // AllowAutoRedirect means an unauthenticated request lands on the Steam
            // login/guard page (200). Spot it by its markers, not by a size guess -
            // a legitimate small save may legitimately contain "<html".
            return LooksLikeSteamLoginPage(bytes) ? null : bytes;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    private static bool LooksLikeSteamLoginPage(byte[] raw)
    {
        if (raw.Length > 256 * 1024) return false; // no login/guard page is this big
        string body;
        try { body = System.Text.Encoding.UTF8.GetString(raw); }
        catch { return false; }
        if (!body.Contains("<html", StringComparison.OrdinalIgnoreCase)) return false;
        if (body.Contains("<title>Sign In", StringComparison.OrdinalIgnoreCase)) return true;
        if (body.Contains("loginpage", StringComparison.OrdinalIgnoreCase)) return true;
        if (body.Contains("action_login", StringComparison.OrdinalIgnoreCase)) return true;
        if (body.Contains("g-recaptcha", StringComparison.OrdinalIgnoreCase)) return true;
        if (body.Contains("Enter your credentials", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Network error while contacting {url}: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException($"Timed out contacting {url}");
        }

        // AllowAutoRedirect=true follows redirects, so a redirect status never
        // surfaces here; an unauthenticated request instead returns the login page
        // as a 200, which EnsureSuccessStatusCode + the login-marker check catch.
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Not logged into Steam store (set SCT_COOKIE to a session cookie)");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (LooksLikeSteamLoginPage(System.Text.Encoding.UTF8.GetBytes(body)))
            throw new InvalidOperationException($"Unexpected HTML response from {url} - session may be expired (renew SCT_COOKIE)");
        return body;
    }
}