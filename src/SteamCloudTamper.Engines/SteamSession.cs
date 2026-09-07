using System.Text.Json;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace SteamCloudTamper.Engines;

public enum AuthMode { Anonymous, Credentials, Qr }

public sealed class ConsoleAuthenticator(Action<string>? log) : IAuthenticator
{
    public async Task<string> GetDeviceCodeAsync(bool sendCodeWasIncorrect)
    {
        log?.Invoke(sendCodeWasIncorrect
            ? "Steam Guard device code was incorrect, try again:"
            : "Enter the Steam Guard device code from your phone:");
        return (await Console.In.ReadLineAsync())?.Trim() ?? "";
    }

    public async Task<string> GetEmailCodeAsync(string email, bool codeWasIncorrect)
    {
        log?.Invoke(codeWasIncorrect
            ? $"The Steam Guard code for {email} was incorrect, try again:"
            : $"Enter the Steam Guard code sent to {email}:");
        return (await Console.In.ReadLineAsync())?.Trim() ?? "";
    }

    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        log?.Invoke("A device confirmation was requested - approve it in the Steam mobile app");
        return Task.FromResult(true);
    }
}

public sealed class SteamSession : IAsyncDisposable
{
    private readonly SteamClient _client;
    private readonly CallbackManager _callbacks;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool> _logon = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;
    private string? _username;
    private string? _password;
    private AuthMode _authMode;
    private int _reconnectAttempts;
    private const int MaxReconnectAttempts = 3;

    public SteamSession()
    {
        var config = SteamConfiguration.Create(b => b.WithProtocolTypes(ProtocolTypes.Tcp | ProtocolTypes.Udp));
        _client = new SteamClient(config);
        _callbacks = new CallbackManager(_client);
    }

    public SteamID? SteamId { get; private set; }

    public bool IsConnected => _client.IsConnected;

    public bool IsLoggedOn => _logon.Task.IsCompletedSuccessfully;

    public CloudRpcClient Cloud { get; private set; } = null!;

    public event Action<string>? Event;

    /// <summary>Fires with the current QR challenge URL (for in-terminal QR rendering).</summary>
    public event Action<string>? ChallengeUrlChanged;

    /// <summary>Fires when the session disconnects (for reconnection logic).</summary>
    public event Action? Disconnected;

    /// <summary>Saves a refresh token to disk for later reconnection without re-auth.</summary>
    public static void SaveRefreshToken(string username, string token)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCT");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tokens.json");
        var data = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new()
            : new Dictionary<string, string>();
        data[username] = token;
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Loads a previously saved refresh token for the given username. Returns null if none.</summary>
    public static string? LoadRefreshToken(string username)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCT", "tokens.json");
        if (!File.Exists(path)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return data?.TryGetValue(username, out var token) == true ? token : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Clears saved tokens (e.g. on auth failure or explicit logout).
    /// </summary>
    public static void ClearRefreshTokens()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SCT", "tokens.json");
        if (File.Exists(path)) File.Delete(path);
    }

    public async Task<bool> ConnectAsync(AuthMode mode = AuthMode.Anonymous, string? username = null, string? password = null)
    {
        _authMode = mode;
        _username = username;
        _password = password;
        _reconnectAttempts = 0;
        return await ConnectInternalAsync();
    }

    private async Task<bool> ConnectInternalAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _logon = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected.TrySetResult());
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await _callbacks.RunWaitCallbackAsync(_cts.Token); }
                catch (OperationCanceledException) { break; }
                catch { /* transient */ }
            }
        });

        Event?.Invoke("Connecting to Steam3...");
        try
        {
            _client.Connect();
            await _connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            Event?.Invoke("Timed out connecting to Steam3 (is outbound 443 open?)");
            return false;
        }
        catch (Exception ex)
        {
            Event?.Invoke($"Connection failed: {ex.Message}");
            return false;
        }

        var handler = _client.GetHandler<SteamUser>();
        if (handler is null)
        {
            Event?.Invoke("SteamUser handler unavailable - cannot log on");
            return false;
        }
        var user = handler;

        try
        {
            switch (_authMode)
            {
                case AuthMode.Anonymous:
                {
                    Event?.Invoke("Logging on anonymously...");
                    user.LogOnAnonymous();
                    break;
                }

                case AuthMode.Credentials:
                {
                    Event?.Invoke("Starting credential auth...");
                    var auth = _client.Authentication
                        ?? throw new InvalidOperationException("SteamAuthentication service unavailable");

                    // try refresh token first (faster, no Steam Guard needed)
                    string? refreshToken = _username is not null ? LoadRefreshToken(_username) : null;
                    if (refreshToken is not null)
                    {
                        Event?.Invoke("Using cached refresh token...");
                        user.LogOn(new SteamUser.LogOnDetails
                        {
                            Username = _username,
                            RefreshToken = refreshToken,
                        });
                    }
                    else
                    {
                        var session = await auth.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                        {
                            Username = _username,
                            Password = _password,
                            PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
                            DeviceFriendlyName = "SteamCloudTamper",
                            Authenticator = new ConsoleAuthenticator(Event),
                        });

                        var poll = await session.PollingWaitForResultAsync(_cts.Token);
                        Event?.Invoke($"Authenticated as {poll.AccountName} - logging on...");

                        // cache the refresh token for next time
                        if (poll.RefreshToken is not null)
                            SaveRefreshToken(poll.AccountName, poll.RefreshToken);

                        user.LogOn(new SteamUser.LogOnDetails
                        {
                            Username = poll.AccountName,
                            AccessToken = poll.AccessToken,
                        });
                    }
                    break;
                }

                case AuthMode.Qr:
                {
                    Event?.Invoke("Starting QR auth - scan in the Steam mobile app...");
                    var auth = _client.Authentication
                        ?? throw new InvalidOperationException("SteamAuthentication service unavailable");
                    var qr = await auth.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                    {
                        PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
                        DeviceFriendlyName = "SteamCloudTamper",
                    });

                    qr.ChallengeURLChanged += () =>
                    {
                        ChallengeUrlChanged?.Invoke(qr.ChallengeURL);
                        Event?.Invoke($"QR: {qr.ChallengeURL}");
                    };
                    Event?.Invoke($"QR: {qr.ChallengeURL}");
                    ChallengeUrlChanged?.Invoke(qr.ChallengeURL);

                    var poll = await qr.PollingWaitForResultAsync(_cts.Token);
                    Event?.Invoke($"Authenticated as {poll.AccountName} - logging on...");

                    // cache the refresh token
                    if (poll.RefreshToken is not null)
                        SaveRefreshToken(poll.AccountName, poll.RefreshToken);

                    user.LogOn(new SteamUser.LogOnDetails
                    {
                        Username = poll.AccountName,
                        AccessToken = poll.AccessToken,
                    });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Event?.Invoke($"Auth error: {ex.Message}");
            return false;
        }

        try
        {
            await _logon.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            Event?.Invoke("Timed out waiting for logon result");
            return false;
        }
        SteamId = IsLoggedOn ? _client.SteamID : null;
        Cloud = new CloudRpcClient(this);
        return IsLoggedOn;
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        Event?.Invoke($"Disconnected: {cb.UserInitiated ? "user-initiated" : "server-side"}");
        Disconnected?.Invoke();

        // auto-reconnect on server-side disconnects (up to MaxReconnectAttempts)
        if (!cb.UserInitiated && _reconnectAttempts < MaxReconnectAttempts
            && _cts?.IsCancellationRequested != true && !_disposed)
        {
            _reconnectAttempts++;
            Event?.Invoke($"Auto-reconnect attempt {_reconnectAttempts}/{MaxReconnectAttempts}...");
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * _reconnectAttempts));
                if (_cts?.IsCancellationRequested != true && !_disposed)
                    await ConnectInternalAsync();
            });
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        Event?.Invoke($"Logon result: {cb.Result} (ext: {cb.ExtendedResult})");
        _reconnectAttempts = 0; // reset on successful logon
        _logon.TrySetResult(cb.Result is EResult.OK or EResult.LogonSessionReplaced);
    }

    internal SteamClient Client => _client;

    internal CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _client.Disconnect();
        await Task.CompletedTask;
    }
}
