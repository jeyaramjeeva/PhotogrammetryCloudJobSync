namespace PhotogrammetryCloudJobSync;

public sealed class MainForm : Form
{
    private readonly SyncService _sync;
    private readonly Label _lblUser;
    private readonly Label _lblStatus;
    private readonly Label _lblLast;
    private readonly ComboBox _cboEnvironment;
    private readonly ComboBox _cboServer;
    private readonly ComboBox _cboProject;
    private readonly ComboBox _cboInterval;
    private readonly TextBox _txtOutput;
    private readonly Button _btnRefreshCatalog;
    private readonly Button _btnSignIn;
    private readonly Button _btnSyncNow;
    private readonly Button _btnPause;
    private readonly Button _btnSave;
    private readonly TextBox _txtLog;
    private readonly Label _lblProgressHeadline;
    private readonly Label _lblProgressDetail;
    private readonly ProgressBar _progressBar;
    private readonly Label _lblProgressPct;
    private readonly FileProgressSlot[] _fileSlots;
    private readonly Dictionary<string, int> _fileSlotById = new(StringComparer.Ordinal);
    private SyncProgress? _pendingProgress;
    private bool _progressUiQueued;
    private bool _suppressCloseExit;
    private bool _suppressEnvHandler;
    private bool _loadingCatalog;
    private string? _pendingProjectTrn;
    private CancellationTokenSource? _catalogCts;

    public MainForm(SyncService sync)
    {
        _sync = sync;
        Text = AppInfo.DisplayName;
        Icon = AppIcon.Get();
        Width = 720;
        Height = 780;
        MinimumSize = new Size(600, 680);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9f);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 12
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 250));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        _lblUser = new Label { Dock = DockStyle.Fill, Text = "Account: —", AutoEllipsis = true };
        _lblStatus = new Label { Dock = DockStyle.Fill, Text = "Status: —", AutoEllipsis = true };
        root.Controls.Add(_lblUser, 0, 0);
        root.Controls.Add(_lblStatus, 0, 1);

        root.Controls.Add(LabeledCombo("Environment:", out _cboEnvironment, 180), 0, 2);
        foreach (var (label, value) in SyncService.EnvironmentPresets)
            _cboEnvironment.Items.Add(new EnvItem(label, value));
        _cboEnvironment.SelectedIndexChanged += (_, _) => _ = SafeUiAsync(OnEnvironmentChangedAsync);

        var serverRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        serverRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        serverRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        serverRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        serverRow.Controls.Add(new Label { Text = "Server:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _cboServer = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _cboServer.SelectedIndexChanged += (_, _) => _ = SafeUiAsync(OnServerChangedAsync);
        serverRow.Controls.Add(_cboServer, 1, 0);
        _btnRefreshCatalog = new Button { Text = "Refresh", Dock = DockStyle.Fill };
        _btnRefreshCatalog.Click += (_, _) => _ = SafeUiAsync(() => LoadServersAsync(force: true));
        serverRow.Controls.Add(_btnRefreshCatalog, 2, 0);
        root.Controls.Add(serverRow, 0, 3);

        root.Controls.Add(LabeledCombo("Project:", out _cboProject, 0), 0, 4);
        _cboProject.DropDownStyle = ComboBoxStyle.DropDownList;

        var intervalPanel = LabeledCombo("Sync every:", out _cboInterval, 160);
        foreach (var (label, minutes) in SyncService.IntervalPresets)
            _cboInterval.Items.Add(new IntervalItem(label, minutes));
        root.Controls.Add(intervalPanel, 0, 5);

        var outRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        outRow.Controls.Add(new Label { Text = "Output folder:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _txtOutput = new TextBox { Dock = DockStyle.Fill };
        outRow.Controls.Add(_txtOutput, 1, 0);
        var btnBrowse = new Button { Text = "Browse…", Dock = DockStyle.Fill };
        btnBrowse.Click += (_, _) => BrowseOutput();
        outRow.Controls.Add(btnBrowse, 2, 0);
        root.Controls.Add(outRow, 0, 6);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _btnSignIn = new Button { Text = "Sign in", Width = 90, Height = 28 };
        _btnSyncNow = new Button { Text = "Sync now", Width = 90, Height = 28 };
        _btnPause = new Button { Text = "Pause", Width = 90, Height = 28 };
        _btnSave = new Button { Text = "Save settings", Width = 110, Height = 28 };
        _btnSignIn.Click += (_, _) => _ = SafeUiAsync(OnSignInClickAsync);
        _btnSyncNow.Click += (_, _) =>
        {
            SaveSettingsFromUi();
            _sync.RequestSyncNow();
        };
        _btnPause.Click += (_, _) => OnPauseClick();
        _btnSave.Click += (_, _) =>
        {
            SaveSettingsFromUi();
            MessageBox.Show(this, "Settings saved.", AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        buttons.Controls.AddRange(new Control[] { _btnSignIn, _btnSyncNow, _btnPause, _btnSave });
        root.Controls.Add(buttons, 0, 7);

        _lblLast = new Label { Dock = DockStyle.Fill, Text = "", AutoEllipsis = true, ForeColor = Color.DimGray };
        root.Controls.Add(_lblLast, 0, 8);

        var progressPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0, 2, 0, 2)
        };
        progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        progressPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        progressPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _lblProgressHeadline = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Ready",
            AutoEllipsis = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        progressPanel.Controls.Add(_lblProgressHeadline, 0, 0);

        var barRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        barRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        barRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Style = ProgressBarStyle.Continuous,
            Margin = new Padding(0, 4, 10, 4),
            Height = 18
        };
        _lblProgressPct = new Label
        {
            Dock = DockStyle.Fill,
            Text = "0%",
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0),
            Padding = new Padding(0),
            AutoSize = false,
            AutoEllipsis = false
        };
        barRow.Controls.Add(_progressBar, 0, 0);
        barRow.Controls.Add(_lblProgressPct, 1, 0);
        progressPanel.Controls.Add(barRow, 0, 1);

        _lblProgressDetail = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Idle — click Sync now when ready",
            AutoEllipsis = true,
            ForeColor = Color.DimGray
        };
        progressPanel.Controls.Add(_lblProgressDetail, 0, 2);

        var fileHost = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(0, 4, 0, 0)
        };
        _fileSlots = new FileProgressSlot[6];
        for (var i = 0; i < _fileSlots.Length; i++)
        {
            fileHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            _fileSlots[i] = new FileProgressSlot();
            _fileSlots[i].Visible = false;
            fileHost.Controls.Add(_fileSlots[i], 0, i);
        }
        progressPanel.Controls.Add(fileHost, 0, 3);
        root.Controls.Add(progressPanel, 0, 9);

        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 8.5f),
            BackColor = Color.White
        };
        root.Controls.Add(_txtLog, 0, 10);

        LoadUiFromConfig();
        RefreshFromService();

        _sync.Changed += () =>
        {
            if (!IsHandleCreated) return;
            BeginInvoke(() =>
            {
                RefreshFromService();
                if (_sync.Session?.IsLoggedIn == true && _cboServer.Items.Count == 0 && !_loadingCatalog)
                    _ = SafeUiAsync(() => LoadServersAsync(force: false));
            });
        };
        _sync.LogLine += line =>
        {
            if (IsHandleCreated)
                BeginInvoke(() => AppendLog(line));
        };
        _sync.Progress += p =>
        {
            if (!IsHandleCreated) return;
            _pendingProgress = p;
            if (_progressUiQueued) return;
            _progressUiQueued = true;
            BeginInvoke(() =>
            {
                _progressUiQueued = false;
                var latest = _pendingProgress;
                if (latest != null)
                    ApplyProgress(latest);
            });
        };

        FormClosing += OnFormClosing;
        Shown += (_, _) =>
        {
            if (_sync.Session?.IsLoggedIn == true)
                _ = SafeUiAsync(() => LoadServersAsync(force: false));
        };
    }

    private async Task SafeUiAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // catalog load cancelled (env switch / sign-out)
        }
        catch (Exception ex)
        {
            AppendLog("UI error: " + ex.Message);
            try
            {
                MessageBox.Show(this,
                    ex.Message + (ex.InnerException != null ? "\n\n" + ex.InnerException.Message : ""),
                    AppInfo.DisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch { /* ignore */ }
        }
    }

    private static Control LabeledCombo(string label, out ComboBox combo, int comboWidth)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        combo = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        if (comboWidth > 0)
            combo.Width = comboWidth;
        row.Controls.Add(combo, 1, 0);
        return row;
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_suppressCloseExit)
            return;

        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
    }

    public void ForceClose()
    {
        _suppressCloseExit = true;
        Close();
    }

    private void LoadUiFromConfig()
    {
        var cfg = _sync.Config;
        _txtOutput.Text = cfg.OutputRoot ?? "";
        _pendingProjectTrn = cfg.ProjectTrns?.FirstOrDefault();

        _suppressEnvHandler = true;
        try
        {
            for (var i = 0; i < _cboEnvironment.Items.Count; i++)
            {
                if (_cboEnvironment.Items[i] is EnvItem e &&
                    string.Equals(e.Value, cfg.Environment, StringComparison.OrdinalIgnoreCase))
                {
                    _cboEnvironment.SelectedIndex = i;
                    break;
                }
            }
            if (_cboEnvironment.SelectedIndex < 0 && _cboEnvironment.Items.Count > 0)
                _cboEnvironment.SelectedIndex = 0;
        }
        finally { _suppressEnvHandler = false; }

        var minutes = cfg.WatchIntervalMinutes;
        var idx = 0;
        for (var i = 0; i < _cboInterval.Items.Count; i++)
        {
            if (_cboInterval.Items[i] is IntervalItem item && item.Minutes == minutes)
            {
                idx = i;
                break;
            }
        }
        _cboInterval.SelectedIndex = idx;

        if (!string.IsNullOrWhiteSpace(cfg.SelectedRegion))
        {
            _cboServer.Items.Clear();
            _cboServer.Items.Add(cfg.SelectedRegion);
            _cboServer.SelectedIndex = 0;
        }

        if (!string.IsNullOrWhiteSpace(_pendingProjectTrn))
        {
            _cboProject.Items.Clear();
            _cboProject.Items.Add(_pendingProjectTrn);
            _cboProject.SelectedIndex = 0;
        }
    }

    private void SaveSettingsFromUi()
    {
        var minutes = _cboInterval.SelectedItem is IntervalItem item ? item.Minutes : 60;
        var env = _cboEnvironment.SelectedItem is EnvItem e ? e.Value : "Production";
        var region = _cboServer.SelectedItem?.ToString() ?? "";
        var project = GetSelectedProjectTrn();
        _sync.ApplySettings(env, _txtOutput.Text, project, region, minutes);
    }

    private string GetSelectedProjectTrn()
    {
        if (_cboProject.SelectedItem is ConnectProjectItem p)
            return p.ProjectTrn;
        return _cboProject.SelectedItem?.ToString()?.Trim() ?? "";
    }

    private void BrowseOutput()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose output folder for job downloads",
            SelectedPath = Directory.Exists(_txtOutput.Text) ? _txtOutput.Text : ""
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
            _txtOutput.Text = dlg.SelectedPath;
    }

    private async Task OnEnvironmentChangedAsync()
    {
        if (_suppressEnvHandler) return;
        if (_cboEnvironment.SelectedItem is not EnvItem env) return;

        var current = _sync.Config.Environment;
        if (string.Equals(current, env.Value, StringComparison.OrdinalIgnoreCase))
            return;

        var ok = MessageBox.Show(this,
            $"Switch environment to {env.Label}?\n\nYou will need to sign in again.",
            AppInfo.DisplayName,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question) == DialogResult.Yes;

        if (!ok)
        {
            _suppressEnvHandler = true;
            try
            {
                for (var i = 0; i < _cboEnvironment.Items.Count; i++)
                {
                    if (_cboEnvironment.Items[i] is EnvItem e &&
                        string.Equals(e.Value, current, StringComparison.OrdinalIgnoreCase))
                    {
                        _cboEnvironment.SelectedIndex = i;
                        break;
                    }
                }
            }
            finally { _suppressEnvHandler = false; }
            return;
        }

        CancelCatalogLoad();
        await _sync.ChangeEnvironmentAsync(env.Value);
        _cboServer.Items.Clear();
        _cboProject.Items.Clear();
        RefreshFromService();
    }

    private async Task OnServerChangedAsync()
    {
        if (_loadingCatalog) return;
        await LoadProjectsAsync();
    }

    private void CancelCatalogLoad()
    {
        try { _catalogCts?.Cancel(); } catch { /* ignore */ }
        _catalogCts?.Dispose();
        _catalogCts = null;
    }

    private async Task LoadServersAsync(bool force)
    {
        if (_sync.Session is not { IsLoggedIn: true } session)
        {
            AppendLog("Sign in first to load servers and projects.");
            return;
        }

        if (_loadingCatalog && !force) return;

        CancelCatalogLoad();
        var cts = new CancellationTokenSource();
        _catalogCts = cts;
        var ct = cts.Token;

        _loadingCatalog = true;
        _btnRefreshCatalog.Enabled = false;
        try
        {
            AppendLog("Loading Connect servers (regions)...");
            var regions = await ConnectCatalog.ListRegionsAsync(session, ct);
            ct.ThrowIfCancellationRequested();

            var preferred = _sync.Config.SelectedRegion;
            if (string.IsNullOrWhiteSpace(preferred))
                preferred = ParseRegion(GetSelectedProjectTrn()) ?? "";

            _cboServer.Items.Clear();
            foreach (var r in regions)
                _cboServer.Items.Add(r);

            if (_cboServer.Items.Count == 0)
            {
                AppendLog("No servers returned.");
                return;
            }

            var idx = 0;
            for (var i = 0; i < _cboServer.Items.Count; i++)
            {
                if (string.Equals(_cboServer.Items[i]?.ToString(), preferred, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            _cboServer.SelectedIndex = idx;
            AppendLog($"Servers loaded: {regions.Count}");
            await LoadProjectsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            AppendLog("Server load cancelled.");
        }
        catch (AuthExpiredException ex)
        {
            AppendLog("Failed to load servers: " + ex.Message);
            try { await _sync.SignOutAsync(); } catch { /* ignore */ }
            _cboServer.Items.Clear();
            _cboProject.Items.Clear();
            MessageBox.Show(this,
                "Login expired. Please sign in again.",
                "Load servers failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendLog("Failed to load servers: " + ex.Message);
            if (ex.InnerException != null)
                AppendLog("  details: " + ex.InnerException.Message);
            MessageBox.Show(this, ex.Message, "Load servers failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (ReferenceEquals(_catalogCts, cts))
                _catalogCts = null;
            cts.Dispose();
            _loadingCatalog = false;
            _btnRefreshCatalog.Enabled = true;
            RefreshFromService();
        }
    }

    private async Task LoadProjectsAsync(CancellationToken ct = default)
    {
        if (_sync.Session is not { IsLoggedIn: true } session)
            return;

        var region = _cboServer.SelectedItem?.ToString();
        if (string.IsNullOrWhiteSpace(region))
            return;

        try
        {
            AppendLog($"Loading projects for server '{region}'...");
            var projects = await ConnectCatalog.ListProjectsAsync(session, region, ct);
            ct.ThrowIfCancellationRequested();
            var preferred = _pendingProjectTrn ?? GetSelectedProjectTrn();

            _cboProject.Items.Clear();
            foreach (var p in projects)
                _cboProject.Items.Add(p);

            if (_cboProject.Items.Count == 0)
            {
                AppendLog("No projects on this server.");
                return;
            }

            var idx = 0;
            for (var i = 0; i < _cboProject.Items.Count; i++)
            {
                if (_cboProject.Items[i] is ConnectProjectItem item &&
                    string.Equals(item.ProjectTrn, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }
            _cboProject.SelectedIndex = idx;
            _pendingProjectTrn = null;
            AppendLog($"Projects loaded: {projects.Count}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ignored
        }
        catch (AuthExpiredException ex)
        {
            AppendLog("Failed to load projects: " + ex.Message);
            try { await _sync.SignOutAsync(); } catch { /* ignore */ }
            MessageBox.Show(this,
                "Login expired. Please sign in again.",
                "Load projects failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            AppendLog("Failed to load projects: " + ex.Message);
            if (ex.InnerException != null)
                AppendLog("  details: " + ex.InnerException.Message);
            MessageBox.Show(this, ex.Message, "Load projects failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task OnSignInClickAsync()
    {
        _btnSignIn.Enabled = false;
        try
        {
            if (_sync.Session?.IsLoggedIn == true)
            {
                CancelCatalogLoad();
                await _sync.SignOutAsync();
                _cboServer.Items.Clear();
                _cboProject.Items.Clear();
            }
            else
            {
                // Ensure session matches selected environment before sign-in
                if (_cboEnvironment.SelectedItem is EnvItem env)
                    await _sync.ChangeEnvironmentAsync(env.Value);

                SaveSettingsFromUi();
                await _sync.SignInAsync();
                if (_sync.Session?.IsLoggedIn == true)
                    await LoadServersAsync(force: true);
            }
        }
        finally
        {
            RefreshFromService();
        }
    }

    private void OnPauseClick()
    {
        if (_sync.State == SyncUiState.Paused)
            _sync.Resume();
        else
            _sync.Pause();
        RefreshFromService();
    }

    private void RefreshFromService()
    {
        var session = _sync.Session;
        var signedIn = session?.IsLoggedIn == true;
        _lblUser.Text = signedIn
            ? $"Account: {session!.UserDisplay}"
            : "Account: Not signed in";
        _lblStatus.Text = $"Status: {_sync.StatusText}";
        _lblLast.Text = _sync.LastPassSummary ?? "";

        _btnSignIn.Text = signedIn ? "Sign out" : "Sign in";
        _btnSignIn.Enabled = true;
        _btnSyncNow.Enabled = signedIn && !_sync.IsBusy;
        _btnPause.Enabled = signedIn;
        _btnPause.Text = _sync.State == SyncUiState.Paused ? "Resume" : "Pause";
        _btnRefreshCatalog.Enabled = signedIn && !_loadingCatalog;
        _cboServer.Enabled = signedIn;
        _cboProject.Enabled = signedIn;
    }

    private void ApplyProgress(SyncProgress p)
    {
        _lblProgressHeadline.Text = p.Headline;
        _lblProgressDetail.Text = p.Detail;
        _progressBar.Value = Math.Clamp(p.Percent, 0, 100);
        _lblProgressPct.Text = $"{p.Percent}%";
        _lblProgressDetail.ForeColor = !p.IsActive && p.Percent == 0
            ? Color.DimGray
            : Color.FromArgb(40, 40, 40);

        var files = p.ActiveFiles ?? Array.Empty<SyncFileProgress>();
        var stillActive = new HashSet<string>(files.Select(f => f.Id), StringComparer.Ordinal);

        // Free slots for downloads that finished
        foreach (var kv in _fileSlotById.ToList())
        {
            if (!stillActive.Contains(kv.Key))
            {
                _fileSlots[kv.Value].Visible = false;
                _fileSlots[kv.Value].Clear();
                _fileSlotById.Remove(kv.Key);
            }
        }

        foreach (var file in files)
        {
            if (!_fileSlotById.TryGetValue(file.Id, out var slotIndex))
            {
                slotIndex = -1;
                for (var i = 0; i < _fileSlots.Length; i++)
                {
                    if (!_fileSlotById.ContainsValue(i))
                    {
                        slotIndex = i;
                        break;
                    }
                }

                if (slotIndex < 0)
                    continue; // more than 6 concurrent files — skip extra

                _fileSlotById[file.Id] = slotIndex;
            }

            _fileSlots[slotIndex].Visible = true;
            _fileSlots[slotIndex].Apply(file);
        }

        if (files.Count == 0)
        {
            _fileSlotById.Clear();
            foreach (var slot in _fileSlots)
            {
                slot.Visible = false;
                slot.Clear();
            }
        }
    }

    private void AppendLog(string line)
    {
        if (_txtLog.TextLength > 120_000)
            _txtLog.Text = _txtLog.Text[^60_000..];

        _txtLog.AppendText(line + Environment.NewLine);
    }

    /// <summary>Two-line slot: job+filename on top, bar + % below (no overlap).</summary>
    private sealed class FileProgressSlot : TableLayoutPanel
    {
        private readonly Label _title;
        private readonly ProgressBar _bar;
        private readonly Label _pct;

        public FileProgressSlot()
        {
            Dock = DockStyle.Fill;
            ColumnCount = 2;
            RowCount = 2;
            Margin = new Padding(0, 2, 0, 2);
            ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
            RowStyles.Add(new RowStyle(SizeType.Absolute, 18));

            _title = new Label
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0),
                Font = new Font("Segoe UI", 8f)
            };
            _bar = new ProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous,
                Margin = new Padding(0, 1, 8, 1)
            };
            _pct = new Label
            {
                Dock = DockStyle.Fill,
                Text = "0%",
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0),
                Font = new Font("Segoe UI", 8f)
            };

            Controls.Add(_title, 0, 0);
            SetColumnSpan(_title, 2);
            Controls.Add(_bar, 0, 1);
            Controls.Add(_pct, 1, 1);
        }

        public void Apply(SyncFileProgress file)
        {
            var shortName = file.Name;
            if (shortName.Length > 64)
                shortName = "…" + shortName[^60..];

            _title.Text = string.IsNullOrWhiteSpace(file.Detail)
                ? $"[{file.JobTag}] {shortName}"
                : $"[{file.JobTag}] {shortName}  ·  {file.Detail}";
            _bar.Value = Math.Clamp(file.Percent, 0, 100);
            _pct.Text = $"{file.Percent}%";
        }

        public void Clear()
        {
            _title.Text = "";
            _bar.Value = 0;
            _pct.Text = "0%";
        }
    }

    private static string? ParseRegion(string? trn)
    {
        // trn:connect:projects:{location}:{id}
        if (string.IsNullOrWhiteSpace(trn)) return null;
        var parts = trn.Split(':');
        return parts.Length >= 5 ? parts[3] : null;
    }

    private static string? ParseId(string? trn)
    {
        if (string.IsNullOrWhiteSpace(trn)) return null;
        var parts = trn.Split(':');
        return parts.Length >= 5 ? parts[4] : null;
    }

    private sealed record IntervalItem(string Label, int Minutes)
    {
        public override string ToString() => Label;
    }

    private sealed record EnvItem(string Label, string Value)
    {
        public override string ToString() => Label;
    }
}
