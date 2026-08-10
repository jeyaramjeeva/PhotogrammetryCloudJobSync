using System.Diagnostics;
using System.Reflection;

namespace PhotogrammetryCloudJobSync;

public sealed class MainForm : Form
{
    private readonly SyncService _sync;
    private readonly Label _lblUser;
    private readonly Label _lblStatus;
    private readonly Label _lblLast;
    private readonly ComboBox _cboEnvironment;
    private readonly ComboBox _cboServer;
    private readonly CheckedListBox _lstProjects;
    private readonly ComboBox _cboInterval;
    private readonly TextBox _txtOutput;
    private readonly CheckBox _chkSkipFailed;
    private readonly FlowLayoutPanel _pnlOutputTypes;
    private readonly Dictionary<string, CheckBox> _outputTypeChecks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Button _btnRefreshCatalog;
    private readonly Button _btnSignIn;
    private readonly Button _btnSyncNow;
    private readonly Button _btnPause;
    private readonly Button _btnCancel;
    private readonly Button _btnSave;
    private readonly Button _btnOpenFolder;
    private readonly ListView _lstQueue;
    private readonly TextBox _txtLog;
    private readonly Label _lblProgressHeadline;
    private readonly Label _lblProgressDetail;
    private readonly ProgressBar _progressBar;
    private readonly Label _lblProgressPct;
    private readonly FileProgressSlot[] _fileSlots;
    private readonly Dictionary<string, int> _fileSlotById = new(StringComparer.Ordinal);
    private SyncProgress? _pendingProgress;
    private bool _progressUiQueued;
    private string _lastQueueSignature = "";
    private string _lastHeadline = "";
    private string _lastDetail = "";
    private string _lastPctText = "";
    private bool _suppressCloseExit;
    private bool _suppressEnvHandler;
    private bool _loadingCatalog;
    private List<string> _pendingProjectTrns = new();
    private CancellationTokenSource? _catalogCts;

    public MainForm(SyncService sync)
    {
        _sync = sync;
        Text = AppInfo.DisplayName;
        Icon = AppIcon.Get();
        Width = 980;
        Height = 940;
        MinimumSize = new Size(860, 780);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        DoubleBuffered = true;

        // Prefer a readable default size on the current monitor (not a tiny fixed window).
        try
        {
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Width = Math.Clamp((int)(wa.Width * 0.62), 900, Math.Max(900, wa.Width - 48));
            Height = Math.Clamp((int)(wa.Height * 0.88), 820, Math.Max(820, wa.Height - 48));
        }
        catch { /* keep defaults */ }

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 11
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); // user
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); // status
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); // environment + server + refresh
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120)); // projects
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); // options + types (tight)
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); // interval
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); // output
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); // buttons
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // last
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 260)); // progress + queue
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // log
        Controls.Add(root);

        _lblUser = new Label { Dock = DockStyle.Fill, Text = "Account: —", AutoEllipsis = true };
        _lblStatus = new Label { Dock = DockStyle.Fill, Text = "Status: —", AutoEllipsis = true };
        root.Controls.Add(_lblUser, 0, 0);
        root.Controls.Add(_lblStatus, 0, 1);

        // Environment + Server + Refresh on one line
        var envServerRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1 };
        envServerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        envServerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        envServerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        envServerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        envServerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
        envServerRow.Controls.Add(new Label { Text = "Environment:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _cboEnvironment = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var (label, value) in SyncService.EnvironmentPresets)
            _cboEnvironment.Items.Add(new EnvItem(label, value));
        _cboEnvironment.SelectedIndexChanged += (_, _) => _ = SafeUiAsync(OnEnvironmentChangedAsync);
        envServerRow.Controls.Add(_cboEnvironment, 1, 0);
        envServerRow.Controls.Add(new Label { Text = "Server:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 2, 0);
        _cboServer = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        _cboServer.SelectedIndexChanged += (_, _) => _ = SafeUiAsync(OnServerChangedAsync);
        envServerRow.Controls.Add(_cboServer, 3, 0);
        _btnRefreshCatalog = new Button { Text = "Refresh", Dock = DockStyle.Fill };
        _btnRefreshCatalog.Click += (_, _) => _ = SafeUiAsync(() => LoadServersAsync(force: true));
        envServerRow.Controls.Add(_btnRefreshCatalog, 4, 0);
        root.Controls.Add(envServerRow, 0, 2);

        // Projects multi-select
        var projectPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        projectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        projectPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        projectPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        projectPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        projectPanel.Controls.Add(new Label { Text = "Projects:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var projectLinks = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        var btnAll = new LinkLabel { Text = "Select all", AutoSize = true, Margin = new Padding(0, 2, 12, 0) };
        var btnNone = new LinkLabel { Text = "Select none", AutoSize = true, Margin = new Padding(0, 2, 0, 0) };
        btnAll.Click += (_, _) => SetAllProjectsChecked(true);
        btnNone.Click += (_, _) => SetAllProjectsChecked(false);
        projectLinks.Controls.Add(btnAll);
        projectLinks.Controls.Add(btnNone);
        projectPanel.Controls.Add(projectLinks, 1, 0);
        _lstProjects = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            IntegralHeight = false
        };
        projectPanel.Controls.Add(_lstProjects, 1, 1);
        root.Controls.Add(projectPanel, 0, 3);

        // Options + types — fixed row heights (no Percent stretch → no empty gap)
        var opts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        opts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        opts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        opts.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        opts.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        opts.Controls.Add(new Label { Text = "Options:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _chkSkipFailed = new CheckBox
        {
            Text = "Skip failed jobs",
            AutoSize = true,
            Dock = DockStyle.Left,
            TextAlign = ContentAlignment.MiddleLeft
        };
        opts.Controls.Add(_chkSkipFailed, 1, 0);
        opts.Controls.Add(new Label { Text = "Types:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        _pnlOutputTypes = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            AutoScroll = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        foreach (var t in AppConfig.KnownOutputTypes)
        {
            var cb = new CheckBox
            {
                Text = t,
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 1, 12, 1)
            };
            _outputTypeChecks[t] = cb;
            _pnlOutputTypes.Controls.Add(cb);
        }
        opts.Controls.Add(_pnlOutputTypes, 1, 1);
        root.Controls.Add(opts, 0, 4);

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
        _btnCancel = new Button { Text = "Cancel", Width = 90, Height = 28 };
        _btnSave = new Button { Text = "Save settings", Width = 110, Height = 28 };
        _btnSignIn.Click += (_, _) => _ = SafeUiAsync(OnSignInClickAsync);
        _btnSyncNow.Click += (_, _) =>
        {
            SaveSettingsFromUi();
            ActiveControl = _txtLog;
            _sync.RequestSyncNow();
        };
        _btnPause.Click += (_, _) => OnPauseClick();
        _btnCancel.Click += (_, _) =>
        {
            _sync.CancelSync();
            RefreshFromService();
        };
        _btnSave.Click += (_, _) =>
        {
            SaveSettingsFromUi();
            MessageBox.Show(this, "Settings saved.", AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        buttons.Controls.AddRange(new Control[] { _btnSignIn, _btnSyncNow, _btnPause, _btnCancel, _btnSave });
        root.Controls.Add(buttons, 0, 7);

        _lblLast = new Label { Dock = DockStyle.Fill, Text = "", AutoEllipsis = true, ForeColor = Color.DimGray };
        root.Controls.Add(_lblLast, 0, 8);

        // Progress (left) + What's left (right)
        var mid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        mid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));

        var progressPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(0, 2, 6, 2)
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

        var barRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
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
            AutoSize = false
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

        var fileHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(0, 4, 0, 0) };
        _fileSlots = new FileProgressSlot[5];
        for (var i = 0; i < _fileSlots.Length; i++)
        {
            fileHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            _fileSlots[i] = new FileProgressSlot();
            _fileSlots[i].Visible = false;
            fileHost.Controls.Add(_fileSlots[i], 0, i);
        }
        progressPanel.Controls.Add(fileHost, 0, 3);
        mid.Controls.Add(progressPanel, 0, 0);

        var queuePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6, 2, 0, 2) };
        queuePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        queuePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        queuePanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        queuePanel.Controls.Add(new Label
        {
            Text = "What’s left",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _lstQueue = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable
        };
        EnableDoubleBuffer(_lstQueue);
        _lstQueue.Columns.Add("Status", 90);
        _lstQueue.Columns.Add("Job", 220);
        _lstQueue.DoubleClick += (_, _) => OpenSelectedJobFolder();
        _lstQueue.SelectedIndexChanged += (_, _) => UpdateOpenFolderButton();
        queuePanel.Controls.Add(_lstQueue, 0, 1);

        _btnOpenFolder = new Button { Text = "Open folder", Dock = DockStyle.Fill, Enabled = false };
        _btnOpenFolder.Click += (_, _) => OpenSelectedJobFolder();
        queuePanel.Controls.Add(_btnOpenFolder, 0, 2);
        mid.Controls.Add(queuePanel, 1, 0);
        root.Controls.Add(mid, 0, 9);

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
        _pendingProjectTrns = (cfg.ProjectTrns ?? new List<string>()).ToList();
        _chkSkipFailed.Checked = !cfg.IncludeFailedJobs;

        var included = new HashSet<string>(
            cfg.IncludedOutputTypes ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (name, cb) in _outputTypeChecks)
            cb.Checked = included.Count == 0 || included.Contains(name);

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

        if (_pendingProjectTrns.Count > 0)
        {
            _lstProjects.Items.Clear();
            foreach (var trn in _pendingProjectTrns)
            {
                var i = _lstProjects.Items.Add(trn);
                _lstProjects.SetItemChecked(i, true);
            }
        }
    }

    private void SaveSettingsFromUi()
    {
        var minutes = _cboInterval.SelectedItem is IntervalItem item ? item.Minutes : 60;
        var env = _cboEnvironment.SelectedItem is EnvItem e ? e.Value : "Production";
        var region = _cboServer.SelectedItem?.ToString() ?? "";
        var projects = GetSelectedProjectTrns();
        var includeFailed = !_chkSkipFailed.Checked;
        var types = GetSelectedOutputTypes();
        // If every known type is checked, persist empty (= all) so future types are included.
        if (types.Count == AppConfig.KnownOutputTypes.Length)
            types = new List<string>();

        _sync.ApplySettings(env, _txtOutput.Text, projects, region, minutes, includeFailed, types);
    }

    private List<string> GetSelectedProjectTrns()
    {
        var list = new List<string>();
        foreach (var item in _lstProjects.CheckedItems)
        {
            if (item is ConnectProjectItem p)
                list.Add(p.ProjectTrn);
            else if (item != null)
                list.Add(item.ToString()!.Trim());
        }
        return list;
    }

    private List<string> GetSelectedOutputTypes()
    {
        return _outputTypeChecks
            .Where(kv => kv.Value.Checked)
            .Select(kv => kv.Key)
            .ToList();
    }

    private string? GetFirstSelectedProjectTrn() =>
        GetSelectedProjectTrns().FirstOrDefault();

    private void SetAllProjectsChecked(bool check)
    {
        for (var i = 0; i < _lstProjects.Items.Count; i++)
            _lstProjects.SetItemChecked(i, check);
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
        _lstProjects.Items.Clear();
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
                preferred = ParseRegion(GetFirstSelectedProjectTrn()) ?? "";

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
            _lstProjects.Items.Clear();
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

            var preferred = new HashSet<string>(
                _pendingProjectTrns.Count > 0 ? _pendingProjectTrns : GetSelectedProjectTrns(),
                StringComparer.OrdinalIgnoreCase);

            _lstProjects.Items.Clear();
            foreach (var p in projects)
            {
                var i = _lstProjects.Items.Add(p);
                if (preferred.Contains(p.ProjectTrn))
                    _lstProjects.SetItemChecked(i, true);
            }

            if (_lstProjects.Items.Count == 0)
            {
                AppendLog("No projects on this server.");
                return;
            }

            // If nothing matched saved TRNs, check the first project so Sync now has a target.
            if (_lstProjects.CheckedItems.Count == 0 && _lstProjects.Items.Count > 0)
                _lstProjects.SetItemChecked(0, true);

            _pendingProjectTrns.Clear();
            AppendLog($"Projects loaded: {projects.Count} (checked {_lstProjects.CheckedItems.Count})");
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
                _lstProjects.Items.Clear();
            }
            else
            {
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
        if (_sync.IsPaused || _sync.State == SyncUiState.Paused)
            _sync.Resume();
        else
            _sync.Pause();
        RefreshFromService();
    }

    private void RefreshFromService()
    {
        var session = _sync.Session;
        var signedIn = session?.IsLoggedIn == true;
        SetTextIfChanged(_lblUser, signedIn
            ? $"Account: {session!.UserDisplay}"
            : "Account: Not signed in");
        SetTextIfChanged(_lblStatus, $"Status: {_sync.StatusText}");
        SetTextIfChanged(_lblLast, _sync.LastPassSummary ?? "");

        var signInText = signedIn ? "Sign out" : "Sign in";
        if (_btnSignIn.Text != signInText)
            _btnSignIn.Text = signInText;
        _btnSignIn.Enabled = true;
        var syncEnabled = signedIn && !_sync.IsBusy;
        if (!syncEnabled && ReferenceEquals(ActiveControl, _btnSyncNow))
            ActiveControl = _txtLog;
        _btnSyncNow.Enabled = syncEnabled;
        _btnPause.Enabled = signedIn;
        var pauseText = (_sync.IsPaused || _sync.State == SyncUiState.Paused) ? "Resume" : "Pause";
        if (_btnPause.Text != pauseText)
            _btnPause.Text = pauseText;
        _btnCancel.Enabled = signedIn && (_sync.IsBusy || _sync.State == SyncUiState.Waiting);
        _btnRefreshCatalog.Enabled = signedIn && !_loadingCatalog;
        _cboServer.Enabled = signedIn;
        _lstProjects.Enabled = signedIn;
        _chkSkipFailed.Enabled = true;
        foreach (var cb in _outputTypeChecks.Values)
            cb.Enabled = true;

        if (ActiveControl is Button { Enabled: false })
            ActiveControl = _txtLog;

        UpdateOpenFolderButton();
    }

    private void ApplyProgress(SyncProgress p)
    {
        SetTextIfChanged(_lblProgressHeadline, p.Headline, ref _lastHeadline);
        SetTextIfChanged(_lblProgressDetail, p.Detail, ref _lastDetail);
        var pct = Math.Clamp(p.Percent, 0, 100);
        if (_progressBar.Value != pct)
            _progressBar.Value = pct;
        SetTextIfChanged(_lblProgressPct, $"{pct}%", ref _lastPctText);
        var detailColor = !p.IsActive && p.Percent == 0
            ? Color.DimGray
            : Color.FromArgb(40, 40, 40);
        if (_lblProgressDetail.ForeColor != detailColor)
            _lblProgressDetail.ForeColor = detailColor;

        var files = p.ActiveFiles ?? Array.Empty<SyncFileProgress>();
        var stillActive = new HashSet<string>(files.Select(f => f.Id), StringComparer.Ordinal);

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
                    continue;

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

        ApplyQueue(p.QueueItems);
    }

    private void ApplyQueue(IReadOnlyList<SyncJobItem>? items)
    {
        items ??= Array.Empty<SyncJobItem>();
        // Avoid Clear()+rebuild every progress tick — that is the main text flicker source.
        var signature = string.Join('\u001f', items.Select(j =>
            $"{j.Id}\u001e{j.State}\u001e{j.Label}\u001e{j.FolderPath}"));
        if (signature == _lastQueueSignature)
            return;
        _lastQueueSignature = signature;

        var selectedId = _lstQueue.SelectedItems.Count > 0 && _lstQueue.SelectedItems[0].Tag is SyncJobItem sel
            ? sel.Id
            : null;

        _lstQueue.BeginUpdate();
        try
        {
            // In-place update when the same jobs are present in the same order.
            if (_lstQueue.Items.Count == items.Count
                && items.Select((j, i) => _lstQueue.Items[i].Tag is SyncJobItem existing
                                         && string.Equals(existing.Id, j.Id, StringComparison.OrdinalIgnoreCase))
                    .All(x => x))
            {
                for (var i = 0; i < items.Count; i++)
                {
                    var job = items[i];
                    var row = _lstQueue.Items[i];
                    if (row.Text != job.State.ToString())
                        row.Text = job.State.ToString();
                    if (row.SubItems.Count > 1 && row.SubItems[1].Text != job.Label)
                        row.SubItems[1].Text = job.Label;
                    row.Tag = job;
                }
            }
            else
            {
                _lstQueue.Items.Clear();
                foreach (var job in items)
                {
                    var row = new ListViewItem(job.State.ToString()) { Tag = job };
                    row.SubItems.Add(job.Label);
                    _lstQueue.Items.Add(row);
                    if (selectedId != null && string.Equals(job.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                        row.Selected = true;
                }
            }
        }
        finally
        {
            _lstQueue.EndUpdate();
        }

        UpdateOpenFolderButton();
    }

    private static void SetTextIfChanged(Label label, string text)
    {
        if (label.Text != text)
            label.Text = text;
    }

    private static void SetTextIfChanged(Label label, string text, ref string cache)
    {
        if (cache == text)
            return;
        cache = text;
        if (label.Text != text)
            label.Text = text;
    }

    private static void EnableDoubleBuffer(Control control)
    {
        typeof(Control)
            .GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(control, true, null);
    }

    private void UpdateOpenFolderButton()
    {
        var path = GetSelectedJobFolder();
        _btnOpenFolder.Enabled = !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
    }

    private string? GetSelectedJobFolder()
    {
        if (_lstQueue.SelectedItems.Count == 0)
            return null;
        return _lstQueue.SelectedItems[0].Tag is SyncJobItem job
            ? job.FolderPath
            : null;
    }

    private void OpenSelectedJobFolder()
    {
        var path = GetSelectedJobFolder();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            MessageBox.Show(this,
                "No local folder is available for this job yet.",
                AppInfo.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppInfo.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void AppendLog(string line)
    {
        if (_txtLog.TextLength > 120_000)
            _txtLog.Text = _txtLog.Text[^60_000..];

        _txtLog.AppendText(line + Environment.NewLine);
    }

    private sealed class FileProgressSlot : TableLayoutPanel
    {
        private readonly Label _title;
        private readonly ProgressBar _bar;
        private readonly Label _pct;
        private string _lastTitle = "";
        private string _lastPct = "";
        private int _lastBar = -1;

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
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = true;

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

            var pct = Math.Clamp(file.Percent, 0, 100);
            if (pct != _lastBar)
            {
                _lastBar = pct;
                _bar.Value = pct;
            }

            var pctText = $"{pct}%";
            if (pctText != _lastPct)
            {
                _lastPct = pctText;
                _pct.Text = pctText;
            }

            // Throttle title (speed/ETA) updates — rewriting every tick causes visible flicker.
            var now = Environment.TickCount64;
            var title = string.IsNullOrWhiteSpace(file.Detail)
                ? $"[{file.JobTag}] {shortName}"
                : $"[{file.JobTag}] {shortName}  ·  {file.Detail}";
            if (title != _lastTitle && (now - _lastTitleMs >= 400 || pct != _lastTitlePct))
            {
                _lastTitle = title;
                _lastTitleMs = now;
                _lastTitlePct = pct;
                _title.Text = title;
            }
        }

        public void Clear()
        {
            _lastTitle = "";
            _lastPct = "0%";
            _lastBar = 0;
            _lastTitleMs = 0;
            _lastTitlePct = -1;
            _title.Text = "";
            _bar.Value = 0;
            _pct.Text = "0%";
        }

        private long _lastTitleMs;
        private int _lastTitlePct = -1;
    }

    private static string? ParseRegion(string? trn)
    {
        if (string.IsNullOrWhiteSpace(trn)) return null;
        var parts = trn.Split(':');
        return parts.Length >= 5 ? parts[3] : null;
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
