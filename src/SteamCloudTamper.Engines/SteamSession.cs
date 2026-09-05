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
    private readonly TaskCompletionSource<bool> _logon = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cts;

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

    public async Task<bool> ConnectAsync(AuthMode mode = AuthMode.Anonymous, string? username = null, string? password = null)
    {
        _cts = new CancellationTokenSource();
        _callbacks.Subscribe<SteamClient.ConnectedCallback>(_ => _connected.TrySetResult());
        _callbacks.Subscribe<SteamClient.DisconnectedCallback>(_ => _logon.TrySetResult(false));

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
        _callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

        try
        {
            switch (mode)
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
                    var session = await auth.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                    {
                        Username = username,
                        Password = password,
                        PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_SteamClient,
                        DeviceFriendlyName = "SteamCloudTamper",
                        Authenticator = new ConsoleAuthenticator(Event),
                    });

                    var poll = await session.PollingWaitForResultAsync(_cts.Token);
                    Event?.Invoke($"Authenticated as {poll.AccountName} - logging on...");
                    user.LogOn(new SteamUser.LogOnDetails
                    {
                        Username = poll.AccountName,
                        AccessToken = poll.AccessToken,
                    });
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
            // SteamKit throws on auth failures (bad credentials, QR session dead,
            // challenge rotated away, CM refused) - those are failures, not crashes.
            // (learned the hard way when the whole tui went poof on a TryAnotherCM.
            //  steam said "try another CM", we tried the door. twice.)
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

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        Event?.Invoke($"Logon result: {cb.Result} (ext: {cb.ExtendedResult})");
        _logon.TrySetResult(cb.Result is EResult.OK or EResult.LogonSessionReplaced);
    }

    internal SteamClient Client => _client;

    internal CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _client.Disconnect();
        await Task.CompletedTask;
    }
}