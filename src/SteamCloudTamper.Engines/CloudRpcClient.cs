using SteamKit2;
using SteamKit2.Internal;
using SteamCloudTamper.Core;
using SteamCloudTamper.Core.Vdf;

namespace SteamCloudTamper.Engines;

public sealed class CloudRpcClient(SteamSession session)
{
    private Cloud _service = null!;

    private Cloud Service
    {
        get
        {
            if (!session.IsConnected)
                throw new CloudRpcException("not connected to Steam - check your network connection");
            if (!session.IsLoggedOn)
                throw new CloudRpcException("not logged on to Steam - a real session is required for cloud RPCs (set SCT_USER/SCT_PASS or SCT_AUTH_MODE=qr)");
            var handlers = session.Client.GetHandler<SteamUnifiedMessages>();
            return handlers is null
                ? throw new CloudRpcException("SteamUnifiedMessages handler unavailable - reconnect and retry")
                : _service ??= handlers.CreateService<Cloud>();
        }
    }

    /// <summary>Quick connectivity check: enumerates 1 file from a known-writable bucket.</summary>
    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await CallAsync(() => Service.EnumerateUserFiles(new CCloud_EnumerateUserFiles_Request
            {
                appid = 480,
                extended_details = false,
                count = 1,
                start_index = 0,
            }), ct);
            return resp.Result is EResult.OK or EResult.AccessDenied;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<T> AwaitJob<T>(AsyncJob<T> job) where T : CallbackMsg => await job.ToTask().ConfigureAwait(false);

    /// <summary>Runs a unified-message RPC with bounded retry on transient EResult/network failures.</summary>
    private static async Task<SteamUnifiedMessages.ServiceMethodResponse<TResp>> CallAsync<TResp>(
        Func<AsyncJob<SteamUnifiedMessages.ServiceMethodResponse<TResp>>> jobFactory, CancellationToken ct)
        where TResp : ProtoBuf.IExtensible, new()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var resp = await AwaitJob(jobFactory());
                if (attempt < MaxRetries && IsTransient(resp.Result))
                {
                    await Task.Delay(RetryDelay(attempt), ct);
                    continue;
                }
                return resp;
            }
            catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex))
            {
                await Task.Delay(RetryDelay(attempt), ct);
            }
        }
    }

    /// <summary>Transient failures worth a bounded retry (network, server hiccups).</summary>
    private static bool IsTransient(EResult r)
        => r is EResult.RemoteCallFailed or EResult.Timeout or EResult.ServiceUnavailable or EResult.Busy;

    private static bool IsTransient(Exception ex)
        => ex switch
        {
            HttpRequestException => true,
            TaskCanceledException => true,
            CloudRpcException { Result: { } r } => IsTransient(r),
            _ => false,
        };

    private const int MaxRetries = 2;

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt));

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
    };

    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> op, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await op().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxRetries && (IsTransient(ex) || ex is HttpRequestException or TaskCanceledException))
            {
                var delay = TimeSpan.FromMilliseconds(300 * Math.Pow(2, attempt));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    public static Policy FromResult(EResult r) => r switch
    {
        EResult.OK => Policy.Allowed,
        EResult.FileNotFound => Policy.FileNotFound,
        EResult.AccessDenied or EResult.InvalidParam => Policy.Denied,
        EResult.DiskFull or EResult.RemoteCallFailed => Policy.Denied,
        _ => Policy.Unknown,
    };

    public async Task<ProbeVerdict> ProbeAsync(uint appId, CancellationToken ct = default)
    {
        var probeName = $"__sct_probe_{Guid.NewGuid():N}.tmp";
        var detail = new List<string>();
        var enumPolicy = Policy.Unknown;
        var uploadPolicy = Policy.Unknown;
        var deletePolicy = Policy.Unknown;

        try
        {
            var r = await CallAsync(() => Service.EnumerateUserFiles(new CCloud_EnumerateUserFiles_Request
            {
                appid = appId,
                extended_details = false,
                count = 1,
                start_index = 0,
            }), ct);
            enumPolicy = FromResult(r.Result);
            detail.Add($"enumerate={r.Result}");
        }
        catch (Exception ex)
        {
            detail.Add($"enumerate=EX({ex.Message})");
        }

        try
        {
            var data = BlankSaveKit.CreateBlank(probeName);
            var up = await UploadRawAsync(appId, probeName, data, ct);
            uploadPolicy = up.Result == EResult.OK ? Policy.Allowed : FromResult(up.Result);
            detail.Add($"upload={up.Result}");

            if (up.Result == EResult.OK)
            {
                var del = await CallAsync(() => Service.Delete(new CCloud_Delete_Request
                {
                    appid = appId,
                    filename = probeName,
                }), ct);
                deletePolicy = FromResult(del.Result);
                detail.Add($"delete={del.Result}");
            }
            else
            {
                deletePolicy = Policy.Denied;
                detail.Add("delete=skipped(no upload)");
            }
        }
        catch (Exception ex)
        {
            detail.Add($"upload=EX({ex.Message})");
        }

        return new ProbeVerdict(appId, enumPolicy, uploadPolicy, deletePolicy, string.Join(", ", detail));
    }

    public async Task<List<CloudFileEntry>> EnumerateAsync(uint appId, CancellationToken ct = default)
    {
        var files = new List<CloudFileEntry>();
        var start = 0u;

        while (true)
        {
            var resp = await CallAsync(() => Service.EnumerateUserFiles(new CCloud_EnumerateUserFiles_Request
            {
                appid = appId,
                extended_details = false,
                count = 100,
                start_index = start,
            }), ct);

            if (resp.Result != EResult.OK)
                throw new CloudRpcException($"EnumerateUserFiles({appId}) failed: {resp.Result}");

            foreach (var f in resp.Body.files)
            {
                files.Add(new CloudFileEntry(
                    f.appid,
                    f.filename,
                    f.ugcid,
                    f.file_size,
                    (long)f.timestamp,
                    f.file_sha,
                    f.url));
            }

            if (files.Count >= resp.Body.total_files || resp.Body.files.Count == 0) break;
            start += 100;
        }

        return files;
    }

    public async Task<(bool Ok, EResult Result)> DeleteAsync(uint appId, string filename, CancellationToken ct = default)
    {
        var resp = await CallAsync(() => Service.Delete(new CCloud_Delete_Request
        {
            appid = appId,
            filename = filename,
        }), ct);

        return (resp.Result == EResult.OK, resp.Result);
    }

    public async Task<EResult> UploadAsync(uint appId, string filename, byte[] data, CancellationToken ct = default)
        => (await UploadRawAsync(appId, filename, data, ct)).Result;

    private async Task<SteamUnifiedMessages.ServiceMethodResponse<CCloud_CommitHTTPUpload_Response>> UploadRawAsync(
        uint appId, string filename, byte[] data, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        var fileSha = Hex.Encode(sha.ComputeHash(data));

        var begin = await CallAsync(() => Service.BeginHTTPUpload(new CCloud_BeginHTTPUpload_Request
        {
            appid = appId,
            filename = filename,
            file_size = (uint)data.Length,
            file_sha = fileSha,
            is_public = false,
        }), ct);

        if (begin.Result != EResult.OK) throw new CloudRpcException($"BeginHTTPUpload({appId}/{filename}) failed: {begin.Result}");

        var url = (begin.Body.use_https ? "https://" : "http://") + begin.Body.url_host + begin.Body.url_path;

        using var httpResp = await WithRetryAsync(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, url);
            foreach (var h in begin.Body.request_headers) req.Headers.TryAddWithoutValidation(h.name, h.value);
            req.Content = new ByteArrayContent(data);
            var r = await Http.SendAsync(req, ct);
            r.EnsureSuccessStatusCode();
            return r;
        }, ct);

        return await CallAsync(() => Service.CommitHTTPUpload(new CCloud_CommitHTTPUpload_Request
        {
            transfer_succeeded = true,
            appid = appId,
            filename = filename,
            file_sha = fileSha,
        }), ct);
    }

    public async Task<byte[]?> DownloadAsync(uint appId, string filename, CancellationToken ct = default)
    {
        var resp = await CallAsync(() => Service.ClientFileDownload(new CCloud_ClientFileDownload_Request
        {
            appid = appId,
            filename = filename,
        }), ct);

        if (resp.Result != EResult.OK) throw new CloudRpcException($"ClientFileDownload({appId}/{filename}) failed: {resp.Result}");

        var url = (resp.Body.use_https ? "https://" : "http://") + resp.Body.url_host + resp.Body.url_path;

        using var r = await WithRetryAsync(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var h in resp.Body.request_headers) req.Headers.TryAddWithoutValidation(h.name, h.value);
            var rr = await Http.SendAsync(req, ct);
            rr.EnsureSuccessStatusCode();
            return rr;
        }, ct);
        return await r.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<Quota> QuotaAsync(uint appId, CancellationToken ct = default)
    {
        var resp = await CallAsync(() => Service.ClientGetAppQuotaUsage(new CCloud_ClientGetAppQuotaUsage_Request
        {
            appid = appId,
        }), ct);

        if (resp.Result != EResult.OK) throw new CloudRpcException($"ClientGetAppQuotaUsage({appId}) failed: {resp.Result}");

        return new Quota(
            appId,
            resp.Body.existing_files,
            resp.Body.existing_bytes,
            resp.Body.max_num_files,
            resp.Body.max_num_bytes);
    }
}

public sealed class CloudRpcException(string message, SteamKit2.EResult? result = null) : Exception(message)
{
    public SteamKit2.EResult? Result { get; } = result;
}