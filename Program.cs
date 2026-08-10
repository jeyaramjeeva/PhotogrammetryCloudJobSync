namespace PhotogrammetryCloudJobSync;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Per-monitor DPI so the UI stays readable on high-DPI / multi-monitor setups.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ShowUnhandled("UI thread", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                ShowUnhandled("AppDomain", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ShowUnhandled("Task", e.Exception);
            e.SetObserved();
        };

        try
        {
            var config = ConfigStore.LoadMerged(args);
            if (config.WatchIntervalMinutes < 1)
                config.WatchIntervalMinutes = 60;

            // Prefer nearest preset if coming from old 15-min default without user settings
            if (!File.Exists(UserSettings.SettingsPath) && config.WatchIntervalMinutes == 15)
                config.WatchIntervalMinutes = 60;

            var sync = new SyncService(config);
            // Initialize on UI thread continuation after first paint
            var context = new TrayAppContext(sync);

            _ = InitializeAsync(sync);

            Application.Run(context);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{AppInfo.DisplayName} failed to start:\n\n" + ex.Message,
                AppInfo.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static void ShowUnhandled(string source, Exception ex)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppInfo.AppFolderName,
                "last-error.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n");
        }
        catch { /* ignore */ }

        try
        {
            MessageBox.Show(
                $"Unhandled exception ({source}):\n\n{ex.GetType().Name}: {ex.Message}\n\n" +
                $"Details were written to %LocalAppData%\\{AppInfo.AppFolderName}\\last-error.txt",
                AppInfo.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch { /* ignore */ }
    }

    private static async Task InitializeAsync(SyncService sync)
    {
        try
        {
            await sync.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Startup error:\n\n" + ex.Message,
                AppInfo.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }
}
