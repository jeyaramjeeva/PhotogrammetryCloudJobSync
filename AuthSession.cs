using Trimble.Gcs.Photogrammetry.Sdk;
using Trimble.Gcs.Photogrammetry.Sdk.Exceptions;
using Trimble.ID;
using Trimble.ID.Desktop;

namespace PhotogrammetryCloudJobSync;

public sealed class AuthSession : IAsyncDisposable
{
    private readonly LocalhostAuthenticator _authenticator;
    private readonly FileTokenStorage _storage;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string _outputRoot;
    private bool _allowInteractiveReLogin;
    private bool? _paidUserHint;

    private AuthSession(
        LocalhostAuthenticator authenticator,
        FileTokenStorage storage,
        string apiBaseUrl,
        string connectApiBaseUrl,
        string outputRoot,
        bool allowInteractiveReLogin)
    {
        _authenticator = authenticator;
        _storage = storage;
        ApiBaseUrl = apiBaseUrl;
        ConnectApiBaseUrl = connectApiBaseUrl;
        _outputRoot = outputRoot;
        _allowInteractiveReLogin = allowInteractiveReLogin;
    }

    public string ApiBaseUrl { get; }
    public string ConnectApiBaseUrl { get; }
    public IAuthenticator Authenticator => _authenticator;
    public ITokenProvider TokenProvider => _authenticator.TokenProvider;
    public bool IsLoggedIn => _authenticator.IsLoggedIn;
    public string? UserDisplay { get; private set; }
    public Action<string>? Log { get; set; }

    public static string AlertFileName => "LOGIN_FAILED_ALERT.txt";

    /// <summary>Create session without signing in (tray startup).</summary>
    public static AuthSession Create(AppConfig config)
    {
        var env = ParseEnvironment(config.Environment);
        var (apiUrl, connectUrl, scopes, endpointProvider, storageSuffix, consumerKey) = ResolveEnvironment(env);

        var scopeList = new List<string> { "PhotogrammetryAPI" };
        scopeList.AddRange(scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        // offline_access is requested via WithOfflineAccess() below (same as Trimble.ID intended usage).

        var storage = new FileTokenStorage("PhotogrammetryAPI", storageSuffix);
        var authenticator = new LocalhostAuthenticator(
                endpointProvider,
                consumerKey,
                scopeList.ToArray(),
                "PhotogrammetryAPI")
            .WithPersistentStorage(storage)
            .WithOfflineAccess();

        return new AuthSession(
            authenticator,
            storage,
            apiUrl,
            connectUrl,
            config.OutputRoot,
            config.AllowInteractiveReLogin);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        await EnsureAuthenticatedAsync(ct, "get access token").ConfigureAwait(false);
        var token = await TokenProvider.RetrieveToken().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new AuthExpiredException("No access token available.");
        return token;
    }

    /// <summary>Console/legacy: create and sign in (cached or browser).</summary>
    public static async Task<AuthSession> CreateAndSignInAsync(AppConfig config, CancellationToken ct)
    {
        var session = Create(config);
        await session.SignInInternalAsync(forceBrowser: false, ct).ConfigureAwait(false);
        ClearAlertFile(config.OutputRoot);
        return session;
    }

    public void SetOutputRoot(string outputRoot) => _outputRoot = outputRoot;

    public void SetAllowInteractiveReLogin(bool allow) => _allowInteractiveReLogin = allow;

    /// <summary>Silent cached login only — no browser.</summary>
    public async Task<bool> TryLoadCachedLoginAsync(CancellationToken ct)
    {
        try
        {
            WriteLog("Loading saved sign-in (if any)...");
            var cached = await _authenticator.LoadCachedLogin().ConfigureAwait(false);
            if (!cached || !_authenticator.IsLoggedIn)
            {
                WriteLog("No saved login.");
                return false;
            }

            var token = await TokenProvider.RetrieveToken().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                WriteLog("Saved login had no access token.");
                return false;
            }

            await RefreshUserInfoAsync().ConfigureAwait(false);
            ClearAlertFile(_outputRoot);
            WriteLog($"Signed in from saved login: {UserDisplay ?? "(unknown)"}");
            return true;
        }
        catch (Exception ex)
        {
            WriteLog($"Saved login not usable ({ex.Message}).");
            try { _storage.Clear(); } catch { /* ignore */ }
            UserDisplay = null;
            return false;
        }
    }

    /// <summary>Interactive browser sign-in (Sign in button).</summary>
    public async Task SignInInteractiveAsync(CancellationToken ct)
    {
        await SignInInternalAsync(forceBrowser: true, ct).ConfigureAwait(false);
        ClearAlertFile(_outputRoot);
    }

    public async Task SignOutAsync()
    {
        try
        {
            await _authenticator.Logout().ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        try { _storage.Clear(); } catch { /* ignore */ }
        UserDisplay = null;
        _paidUserHint = null;
        WriteLog("Signed out.");
    }

    /// <summary>
    /// Call before each sync pass / API burst. Uses refresh token when possible.
    /// Opens browser only if AllowInteractiveReLogin is true (serialized — one browser at a time).
    /// Throws <see cref="AuthExpiredException"/> if login cannot be recovered.
    /// </summary>
    public async Task EnsureAuthenticatedAsync(CancellationToken ct, string? reason = null)
    {
        if (await TryExistingTokenAsync().ConfigureAwait(false))
            return;

        // Try silent refresh from persisted offline_access token (do not clear storage yet).
        try
        {
            WriteLog($"Trying to refresh saved login{(reason is null ? "" : $" ({reason})")}...");
            var cached = await _authenticator.LoadCachedLogin().ConfigureAwait(false);
            if (cached && _authenticator.IsLoggedIn
                && await TryExistingTokenAsync().ConfigureAwait(false))
            {
                await RefreshUserInfoAsync().ConfigureAwait(false);
                WriteLog("Login refreshed successfully from saved credentials.");
                return;
            }

            WriteLog("Saved login refresh failed: no usable access token.");
        }
        catch (Exception ex)
        {
            WriteLog($"Saved login refresh failed: {ex.Message}");
        }

        if (_allowInteractiveReLogin)
        {
            await _loginGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Another parallel caller may have finished login while we waited.
                if (await TryExistingTokenAsync().ConfigureAwait(false))
                    return;

                WriteLog("Opening browser for re-login...");
                try
                {
                    await SignInInternalAsync(forceBrowser: true, ct).ConfigureAwait(false);
                    ClearAlertFile(_outputRoot);
                    WriteLog("Re-login successful.");
                    return;
                }
                catch (Exception ex)
                {
                    WriteAlertFile(_outputRoot, "Interactive re-login failed: " + ex.Message);
                    throw new AuthExpiredException(
                        "Login expired and interactive re-login failed.",
                        ex);
                }
            }
            finally
            {
                _loginGate.Release();
            }
        }

        WriteAlertFile(_outputRoot,
            "Login/refresh token expired or was revoked (or offline_access was not granted).\n" +
            "Long downloads can outlive the access token; without a refresh token sync must stop.\n" +
            $"Sync stopped. Use Sign in in {AppInfo.DisplayName} to continue.");
        throw new AuthExpiredException(
            "Login expired and cannot be refreshed automatically. " +
            $"See {Path.Combine(_outputRoot, AlertFileName)}.");
    }

    private async Task<bool> TryExistingTokenAsync()
    {
        try
        {
            var token = await TokenProvider.RetrieveToken().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                return false;
            ClearAlertFile(_outputRoot);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog($"Auth check failed: {ex.Message}");
            return false;
        }
    }

    public PhotogrammetryClient CreateClient()
    {
        var httpProvider = new BearerTokenHttpClientProvider(
            TokenProvider,
            new Uri(ApiBaseUrl),
            "PhotogrammetryCloudJobSync");

        var clientConfig = new PhotogrammetryClientConfig
        {
            BaseUrl = ApiBaseUrl,
            Retries = 3,
            HttpTimeout = TimeSpan.FromMinutes(2),
            MaxConcurrentRequests = 4,
            PaidUserHintProvider = () => _paidUserHint
        };

        var onBehalf = new ConfigurableOnBehalfOfUserProvider(string.Empty);
        return new PhotogrammetryClient(httpProvider, clientConfig, onBehalf);
    }

    public static bool IsAuthFailure(Exception ex)
    {
        if (ex is AggregateException agg)
            return agg.Flatten().InnerExceptions.Any(IsAuthFailure);

        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is DatasetUnauthorizedException or DatasetForbiddenException or AuthExpiredException
                or AuthorizationFailedException or TokenRefreshException)
                return true;

            var msg = e.Message ?? string.Empty;
            if (msg.Contains("401", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("refresh token", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async Task SignInInternalAsync(bool forceBrowser, CancellationToken ct)
    {
        var signedIn = false;

        if (!forceBrowser)
        {
            WriteLog("Loading saved sign-in (if any)...");
            try
            {
                var cached = await _authenticator.LoadCachedLogin().ConfigureAwait(false);
                if (cached && _authenticator.IsLoggedIn)
                {
                    _ = await TokenProvider.RetrieveToken().ConfigureAwait(false);
                    WriteLog("Signed in from saved login (refresh token).");
                    signedIn = true;
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Saved login not usable ({ex.Message}). Clearing cache...");
                _storage.Clear();
            }
        }

        if (!signedIn)
        {
            WriteLog("Browser sign-in required — complete sign-in in the browser window.");
            var ok = await _authenticator.Login(silent: false, timeoutInMs: 300_000, cancellationToken: ct)
                .ConfigureAwait(false);
            if (!ok || !_authenticator.IsLoggedIn)
                throw new InvalidOperationException("Sign-in failed or was cancelled.");

            _ = await TokenProvider.RetrieveToken().ConfigureAwait(false);
            WriteLog("Sign-in successful.");
        }

        await LogRefreshTokenStatusAsync().ConfigureAwait(false);
        await RefreshUserInfoAsync().ConfigureAwait(false);
        WriteLog($"User: {UserDisplay ?? "(unknown)"}");
    }

    private async Task LogRefreshTokenStatusAsync()
    {
        try
        {
#pragma warning disable CS0618 // LegacyTokenProvider is the supported way to read the refresh token
            var refresh = await _authenticator.LegacyTokenProvider.RetrieveRefreshToken().ConfigureAwait(false);
#pragma warning restore CS0618
            if (!string.IsNullOrWhiteSpace(refresh))
            {
                WriteLog("Refresh token saved — long downloads can renew login automatically.");
                return;
            }
        }
        catch (Exception ex)
        {
            WriteLog($"Could not read refresh token ({ex.Message}).");
        }

        WriteLog(
            "WARNING: No refresh token after sign-in. Access token will expire during long syncs; " +
            "you may need to Sign in again between jobs. (Trimble ID client may not grant offline_access.)");
    }

    private async Task RefreshUserInfoAsync()
    {
        try
        {
            var user = await _authenticator.GetUserInfo().ConfigureAwait(false);
            var email = user?.Email;
            UserDisplay = !string.IsNullOrWhiteSpace(email) ? email : "(signed in)";
            _paidUserHint = (email ?? "").EndsWith("@trimble.com", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            WriteLog($"Warning: could not read user info ({ex.Message}).");
            UserDisplay = "(signed in)";
            _paidUserHint = true;
        }
    }

    private void WriteLog(string message)
    {
        if (Log != null)
            Log(message);
        else
            Console.WriteLine(message);
    }

    public static void WriteAlertFile(string outputRoot, string details)
    {
        try
        {
            Directory.CreateDirectory(outputRoot);
            var path = Path.Combine(outputRoot, AlertFileName);
            var body =
                "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!\n" +
                "  PHOTOGRAMMETRY CLOUD JOB SYNC STOPPED — LOGIN / AUTH FAILED\n" +
                "!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!\n\n" +
                $"Time: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n\n" +
                details + "\n\n" +
                "IMPORTANT:\n" +
                "  Do NOT assume all jobs were downloaded.\n" +
                $"  Open {AppInfo.DisplayName}, click Sign in, then Sync now.\n" +
                "  Delete this file after you have fixed login.\n";
            File.WriteAllText(path, body);
        }
        catch
        {
            // ignore IO errors writing alert
        }
    }

    public static void ClearAlertFile(string outputRoot)
    {
        try
        {
            var path = Path.Combine(outputRoot, AlertFileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }

    public ValueTask DisposeAsync()
    {
        _authenticator.Dispose();
        _loginGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private enum EnvKind
    {
        Production,
        QA_Production,
        QA_Staging,
        Development
    }

    private static EnvKind ParseEnvironment(string value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "production" or "prod" => EnvKind.Production,
            "qa_production" or "qa-production" or "rc" => EnvKind.QA_Production,
            "qa_staging" or "qa-staging" or "qa" => EnvKind.QA_Staging,
            "development" or "dev" => EnvKind.Development,
            _ => EnvKind.Production
        };

    private static (string ApiUrl, string ConnectUrl, string Scopes, OpenIdEndpointProvider Endpoint, string StorageSuffix, string ConsumerKey)
        ResolveEnvironment(EnvKind env) =>
        env switch
        {
            EnvKind.Production => (
                "https://cloud.api.trimble.com/photogrammetry/v1/",
                "https://app.connect.trimble.com/tc/api/2.0/",
                "photogrammetry Imaging openid profile email",
                OpenIdEndpointProvider.Production,
                "Prod",
                "990ab35a-69c9-4cda-b360-edc48eef95cc"),
            EnvKind.QA_Production => (
                "https://cloud.api.trimble.com/photogrammetry/rc/v1/",
                "https://app.connect.trimble.com/tc/api/2.0/",
                "photogrammetry-rc Imaging openid profile email",
                OpenIdEndpointProvider.Production,
                "RC",
                "990ab35a-69c9-4cda-b360-edc48eef95cc"),
            EnvKind.QA_Staging => (
                "https://cloud.stage.api.trimblecloud.com/photogrammetry/qa/v1/",
                "https://app.stage.connect.trimble.com/tc/api/2.0/",
                "photogrammetry-qa Imaging-qa openid profile email",
                OpenIdEndpointProvider.Staging,
                "QA",
                "eb3cd130-a54f-427f-be98-9c8fc8175dc4"),
            EnvKind.Development => (
                "https://cloud.stage.api.trimblecloud.com/photogrammetry/dev/v1/",
                "https://app.stage.connect.trimble.com/tc/api/2.0/",
                "photogrammetry-temp Imaging-qa openid profile email",
                OpenIdEndpointProvider.Staging,
                "Dev",
                "eb3cd130-a54f-427f-be98-9c8fc8175dc4"),
            _ => throw new ArgumentOutOfRangeException(nameof(env))
        };
}

public sealed class AuthExpiredException : Exception
{
    public AuthExpiredException(string message) : base(message) { }
    public AuthExpiredException(string message, Exception inner) : base(message, inner) { }
}
