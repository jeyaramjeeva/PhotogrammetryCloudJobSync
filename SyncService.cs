namespace PhotogrammetryCloudJobSync;

public enum SyncUiState
{
    NotSignedIn,
    Idle,
    Syncing,
    Paused,
    Waiting,
    AuthFailed
}

/// <summary>Background sync loop for the tray UI (Connect Sync style).</summary>
public sealed class SyncService : IAsyncDisposable
{
    private readonly object _gate = new();
    private AppConfig _config;
    private AuthSession? _session;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _waitCts;
    private Task? _loopTask;
    private bool _paused;
    private bool _syncNowRequested;
    private int _passNumber;
    private bool _disposed;

    public SyncUiState State { get; private set; } = SyncUiState.NotSignedIn;
    public string StatusText { get; private set; } = "Not signed in";
    public string? LastPassSummary { get; private set; }
    public DateTimeOffset? LastPassAt { get; private set; }
    public bool IsBusy => State == SyncUiState.Syncing;

    public event Action? Changed;
    public event Action<string>? LogLine;
    public event Action<string, string>? Balloon; // title, text
    public event Action<SyncProgress>? Progress;

    public AppConfig Config
    {
        get { lock (_gate) return _config; }
    }

    public AuthSession? Session
    {
        get { lock (_gate) return _session; }
    }

    public SyncService(AppConfig config)
    {
        _config = config;
    }

    public async Task InitializeAsync()
    {
        var session = AuthSession.Create(_config);
        session.Log = msg => EmitLog(msg);
        session.SetAllowInteractiveReLogin(false);
        session.SetOutputRoot(_config.OutputRoot);

        lock (_gate)
            _session = session;

        var ok = await session.TryLoadCachedLoginAsync(CancellationToken.None).ConfigureAwait(false);
        if (ok)
        {
            SetState(SyncUiState.Idle, $"Signed in as {session.UserDisplay} — click Sync now");
        }
        else
        {
            SetState(SyncUiState.NotSignedIn, "Not signed in — click Sign in");
        }
    }

    public void ApplySettings(
        string environment,
        string outputRoot,
        string projectTrn,
        string selectedRegion,
        int intervalMinutes)
    {
        lock (_gate)
        {
            _config.Environment = string.IsNullOrWhiteSpace(environment) ? _config.Environment : environment.Trim();
            _config.OutputRoot = outputRoot.Trim();
            _config.SelectedRegion = selectedRegion?.Trim() ?? "";
            _config.WatchIntervalMinutes = Math.Max(1, intervalMinutes);
            if (!string.IsNullOrWhiteSpace(projectTrn))
                _config.ProjectTrns = new List<string> { projectTrn.Trim() };
            else
                _config.ProjectTrns = new List<string>();

            _session?.SetOutputRoot(_config.OutputRoot);
            ConfigStore.SaveUserOverrides(_config);
        }

        EmitLog($"Settings saved. Env={_config.Environment} Region={_config.SelectedRegion} " +
                $"every {_config.WatchIntervalMinutes} min → {_config.OutputRoot}");
        Changed?.Invoke();
        // Do not cancel wait / trigger sync — only Sync now / Resume starts work.
    }

    /// <summary>Switch Photogrammetry/Connect environment (requires re-login when env changes).</summary>
    public async Task<bool> ChangeEnvironmentAsync(string environment)
    {
        environment = environment.Trim();
        string current;
        lock (_gate) current = _config.Environment;

        if (string.Equals(current, environment, StringComparison.OrdinalIgnoreCase))
            return Session?.IsLoggedIn == true;

        StopLoop();
        AuthSession? old;
        lock (_gate)
        {
            old = _session;
            _config.Environment = environment;
            ConfigStore.SaveUserOverrides(_config);
        }

        if (old != null)
        {
            try { await old.SignOutAsync().ConfigureAwait(false); } catch { /* ignore */ }
            await old.DisposeAsync().ConfigureAwait(false);
        }

        var session = AuthSession.Create(Config);
        session.Log = msg => EmitLog(msg);
        session.SetAllowInteractiveReLogin(false);
        session.SetOutputRoot(Config.OutputRoot);
        lock (_gate) _session = session;

        EmitLog($"Environment switched to {environment}. Sign in required.");
        SetState(SyncUiState.NotSignedIn, "Environment changed — sign in again");
        return false;
    }

    public async Task SignInAsync()
    {
        AuthSession session;
        lock (_gate)
        {
            session = _session ?? throw new InvalidOperationException("Session not initialized.");
        }

        SetState(SyncUiState.Idle, "Signing in...");
        try
        {
            await session.SignInInteractiveAsync(CancellationToken.None).ConfigureAwait(false);
            _paused = true; // do not auto-sync until Sync now
            SetState(SyncUiState.Idle, $"Signed in as {session.UserDisplay} — click Sync now");
            Balloon?.Invoke(AppInfo.DisplayName, $"Signed in as {session.UserDisplay}");
            EmitLog("Signed in. Waiting for Sync now — schedule will not start automatically.");
        }
        catch (Exception ex)
        {
            SetState(SyncUiState.NotSignedIn, "Sign-in failed");
            EmitLog("Sign-in failed: " + ex.Message);
            Balloon?.Invoke("Sign-in failed", ex.Message);
        }
    }

    public async Task SignOutAsync()
    {
        StopLoop();
        AuthSession? session;
        lock (_gate) session = _session;
        if (session != null)
            await session.SignOutAsync().ConfigureAwait(false);

        SetState(SyncUiState.NotSignedIn, "Not signed in");
        Balloon?.Invoke(AppInfo.DisplayName, "Signed out");
    }

    public void Pause()
    {
        _paused = true;
        try { _waitCts?.Cancel(); } catch { /* ignore */ }
        if (State is SyncUiState.Waiting or SyncUiState.Idle)
            SetState(SyncUiState.Paused, "Paused");
        else if (State == SyncUiState.Syncing)
            EmitLog("Pause requested — will pause after current sync finishes.");
        else
            SetState(SyncUiState.Paused, "Paused");
    }

    public void Resume()
    {
        if (Session is not { IsLoggedIn: true })
        {
            SetState(SyncUiState.NotSignedIn, "Not signed in — click Sign in");
            return;
        }

        _paused = false;
        SetState(SyncUiState.Idle, $"Signed in as {Session.UserDisplay}");
        StartLoop();
        RequestSyncNow();
    }

    public void RequestSyncNow()
    {
        if (Session is not { IsLoggedIn: true })
        {
            Balloon?.Invoke("Not signed in", "Sign in before syncing.");
            return;
        }

        _paused = false;
        _syncNowRequested = true;
        try { _waitCts?.Cancel(); } catch { /* ignore */ }
        StartLoop();
        EmitLog("Sync now requested...");
    }

    private void StartLoop()
    {
        lock (_gate)
        {
            if (_loopTask is { IsCompleted: false })
                return;

            _loopCts = new CancellationTokenSource();
            var ct = _loopCts.Token;
            _loopTask = Task.Run(() => LoopAsync(ct), ct);
        }
    }

    private void StopLoop()
    {
        try { _loopCts?.Cancel(); } catch { /* ignore */ }
        try { _waitCts?.Cancel(); } catch { /* ignore */ }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_paused)
            {
                SetState(SyncUiState.Paused, "Paused");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            if (Session is not { IsLoggedIn: true })
            {
                SetState(SyncUiState.NotSignedIn, "Not signed in");
                break;
            }

            _syncNowRequested = false;
            await RunOnePassAsync(ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested || State == SyncUiState.AuthFailed || State == SyncUiState.NotSignedIn)
                break;

            if (_paused)
            {
                SetState(SyncUiState.Paused, "Paused");
                continue;
            }

            if (_syncNowRequested)
                continue;

            var minutes = Math.Max(1, Config.WatchIntervalMinutes);
            SetState(SyncUiState.Waiting, $"Next sync in {FormatInterval(minutes)}");

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _waitCts = waitCts;
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                    break;
                // woken by Sync now / settings / pause
            }
            finally
            {
                if (ReferenceEquals(_waitCts, waitCts))
                    _waitCts = null;
            }
        }
    }

    private async Task RunOnePassAsync(CancellationToken ct)
    {
        AuthSession session;
        AppConfig config;
        lock (_gate)
        {
            session = _session ?? throw new InvalidOperationException("No session");
            config = CloneConfig(_config);
        }

        _passNumber++;
        SetState(SyncUiState.Syncing, $"Syncing (pass {_passNumber})...");
        EmitLog("");
        EmitLog($"#################### PASS {_passNumber}  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} ####################");

        try
        {
            session.SetOutputRoot(config.OutputRoot);
            session.SetAllowInteractiveReLogin(false);
            var client = session.CreateClient();
            var downloader = new BatchDownloader(client, config, session)
            {
                LogSink = EmitLog,
                ProgressSink = p => Progress?.Invoke(p)
            };

            var result = await downloader.RunPassAsync(ct).ConfigureAwait(false);
            LastPassAt = DateTimeOffset.Now;
            LastPassSummary =
                $"Last sync {LastPassAt:HH:mm} — downloaded {result.Downloaded}, skipped {result.SkippedExisting}, " +
                $"waiting {result.SkippedNotReady}, failed {result.Failed}, errors {result.Errors}";
            EmitLog(LastPassSummary);
            SetState(SyncUiState.Idle, LastPassSummary);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetState(SyncUiState.Idle, "Cancelled");
        }
        catch (AuthExpiredException ex)
        {
            EmitLog("AUTH STOPPED: " + ex.Message);
            SetState(SyncUiState.AuthFailed, "Session expired — sign in again");
            Balloon?.Invoke("Session expired", "Sign in again to continue syncing.");
            try { await session.SignOutAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            EmitLog("Sync error: " + ex.Message);
            LastPassSummary = $"Last sync error @ {DateTimeOffset.Now:HH:mm} — {ex.Message}";
            SetState(SyncUiState.Idle, LastPassSummary);
            Balloon?.Invoke("Sync error", ex.Message);
        }
    }

    private static AppConfig CloneConfig(AppConfig c) => new()
    {
        Environment = c.Environment,
        OutputRoot = c.OutputRoot,
        ProjectTrns = (c.ProjectTrns ?? new List<string>()).ToList(),
        SelectedRegion = c.SelectedRegion,
        IncludeFailedJobs = c.IncludeFailedJobs,
        MaxConcurrentJobs = c.MaxConcurrentJobs,
        MaxConcurrentFileDownloads = c.MaxConcurrentFileDownloads,
        VerifyAndRepairMissingFiles = c.VerifyAndRepairMissingFiles,
        WatchMode = false,
        WatchIntervalMinutes = c.WatchIntervalMinutes,
        RetryCount = c.RetryCount,
        RetryBaseDelaySeconds = c.RetryBaseDelaySeconds,
        AllowInteractiveReLogin = false
    };

    public static readonly (string Label, string Value)[] EnvironmentPresets =
    {
        ("Production", "Production"),
        ("QA - Production", "QA_Production"),
        ("QA - Staging", "QA_Staging"),
        ("Development", "Development")
    };

    public static string FormatInterval(int minutes) => minutes switch
    {
        15 => "15 minutes",
        60 => "1 hour",
        360 => "6 hours",
        1440 => "1 day",
        _ => $"{minutes} minutes"
    };

    public static readonly (string Label, int Minutes)[] IntervalPresets =
    {
        ("15 minutes", 15),
        ("1 hour", 60),
        ("6 hours", 360),
        ("1 day", 1440)
    };

    private void SetState(SyncUiState state, string status)
    {
        State = state;
        StatusText = status;
        Changed?.Invoke();
    }

    private void EmitLog(string line)
    {
        LogLine?.Invoke(line);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        StopLoop();
        try
        {
            if (_loopTask != null)
                await Task.WhenAny(_loopTask, Task.Delay(2000)).ConfigureAwait(false);
        }
        catch { /* ignore */ }

        if (_session != null)
            await _session.DisposeAsync().ConfigureAwait(false);
    }
}
