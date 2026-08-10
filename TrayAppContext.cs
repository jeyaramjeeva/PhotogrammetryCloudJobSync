namespace PhotogrammetryCloudJobSync;

/// <summary>System-tray host (Trimble Connect Sync style).</summary>
public sealed class TrayAppContext : ApplicationContext
{
    private readonly SyncService _sync;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _miSyncNow;
    private readonly ToolStripMenuItem _miPause;
    private readonly ToolStripMenuItem _miCancel;
    private readonly ToolStripMenuItem _miSignIn;

    public TrayAppContext(SyncService sync)
    {
        _sync = sync;
        _form = new MainForm(sync);

        _miSyncNow = new ToolStripMenuItem("Sync now", null, (_, _) => _sync.RequestSyncNow());
        _miPause = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        _miCancel = new ToolStripMenuItem("Cancel", null, (_, _) => _sync.CancelSync());
        _miSignIn = new ToolStripMenuItem("Sign in", null, async (_, _) => await ToggleSignInAsync());

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open", null, (_, _) => _form.ShowFromTray()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miSyncNow);
        menu.Items.Add(_miPause);
        menu.Items.Add(_miCancel);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miSignIn);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync()));

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Get(),
            Text = AppInfo.DisplayName,
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => _form.ShowFromTray();

        _sync.Changed += () =>
        {
            try
            {
                if (_form.IsHandleCreated)
                    _form.BeginInvoke(UpdateTray);
                else
                    UpdateTray();
            }
            catch { /* ignore */ }
        };
        _sync.Balloon += (title, text) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(text))
                    return;
                _tray.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
            }
            catch { /* ignore */ }
        };

        UpdateTray();
        _form.Show();
    }

    private void UpdateTray()
    {
        var signedIn = _sync.Session?.IsLoggedIn == true;
        _tray.Text = Truncate($"{AppInfo.DisplayName} — {_sync.StatusText}", 63);
        // Keep branded logo in tray (status is shown in tooltip / window).
        _tray.Icon = AppIcon.Get();

        _miSignIn.Text = signedIn ? "Sign out" : "Sign in";
        _miSyncNow.Enabled = signedIn && !_sync.IsBusy;
        _miPause.Enabled = signedIn;
        _miPause.Text = (_sync.IsPaused || _sync.State == SyncUiState.Paused) ? "Resume" : "Pause";
        _miCancel.Enabled = signedIn && (_sync.IsBusy || _sync.State == SyncUiState.Waiting);
    }

    private void TogglePause()
    {
        if (_sync.IsPaused || _sync.State == SyncUiState.Paused)
            _sync.Resume();
        else
            _sync.Pause();
    }

    private async Task ToggleSignInAsync()
    {
        if (_sync.Session?.IsLoggedIn == true)
            await _sync.SignOutAsync();
        else
            await _sync.SignInAsync();
    }

    private async Task ExitAsync()
    {
        _tray.Visible = false;
        await _sync.DisposeAsync();
        _form.ForceClose();
        ExitThread();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _form.Dispose();
        }
        base.Dispose(disposing);
    }
}
