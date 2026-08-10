using System.Text.Json;

namespace PhotogrammetryCloudJobSync;

/// <summary>
/// User overrides under %LocalAppData%\PhotogrammetryCloudJobSync\user-settings.json
/// so UI changes survive rebuilds of appsettings.json.
/// </summary>
public sealed class UserSettings
{
    public string? TidEnvironment { get; set; }
    public string? OutputRoot { get; set; }
    public List<string>? ProjectTrns { get; set; }
    public string? SelectedRegion { get; set; }
    public int? WatchIntervalMinutes { get; set; }
    public bool? IncludeFailedJobs { get; set; }
    public List<string>? IncludedOutputTypes { get; set; }
    public int? MaxConcurrentJobs { get; set; }
    public int? MaxConcurrentFileDownloads { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppInfo.AppFolderName,
            "user-settings.json");

    public static UserSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path))
                return new UserSettings();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        var path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void ApplyTo(AppConfig config)
    {
        if (!string.IsNullOrWhiteSpace(TidEnvironment))
            config.Environment = TidEnvironment.Trim();

        if (!string.IsNullOrWhiteSpace(OutputRoot))
            config.OutputRoot = OutputRoot.Trim();

        if (ProjectTrns is { Count: > 0 })
            config.ProjectTrns = ProjectTrns.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();

        if (!string.IsNullOrWhiteSpace(SelectedRegion))
            config.SelectedRegion = SelectedRegion.Trim();

        if (WatchIntervalMinutes is > 0)
            config.WatchIntervalMinutes = WatchIntervalMinutes.Value;

        if (IncludeFailedJobs.HasValue)
            config.IncludeFailedJobs = IncludeFailedJobs.Value;

        if (IncludedOutputTypes != null)
            config.IncludedOutputTypes = IncludedOutputTypes
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (MaxConcurrentJobs is > 0)
            config.MaxConcurrentJobs = MaxConcurrentJobs.Value;

        if (MaxConcurrentFileDownloads is > 0)
            config.MaxConcurrentFileDownloads = MaxConcurrentFileDownloads.Value;
    }

    public static UserSettings FromConfig(AppConfig config) => new()
    {
        TidEnvironment = config.Environment,
        OutputRoot = config.OutputRoot,
        ProjectTrns = (config.ProjectTrns ?? new List<string>()).ToList(),
        SelectedRegion = config.SelectedRegion,
        WatchIntervalMinutes = config.WatchIntervalMinutes,
        IncludeFailedJobs = config.IncludeFailedJobs,
        IncludedOutputTypes = (config.IncludedOutputTypes ?? new List<string>()).ToList(),
        MaxConcurrentJobs = config.MaxConcurrentJobs,
        MaxConcurrentFileDownloads = config.MaxConcurrentFileDownloads
    };
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static AppConfig LoadMerged(string[]? args = null)
    {
        var config = LoadAppSettings();
        UserSettings.Load().ApplyTo(config);

        // Tray UI: never auto-open browser during scheduled sync — user clicks Sign in.
        config.AllowInteractiveReLogin = false;
        config.WatchMode = true;

        if (args != null)
            ApplyArgs(config, args);

        return config;
    }

    public static AppConfig LoadAppSettings()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")
        };

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (loaded != null)
                    return loaded;
            }
            catch
            {
                // fall through
            }
        }

        return new AppConfig();
    }

    public static void SaveUserOverrides(AppConfig config)
    {
        UserSettings.FromConfig(config).Save();
    }

    private static void ApplyArgs(AppConfig config, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--project" or "-p" && i + 1 < args.Length)
                config.ProjectTrns.Insert(0, args[++i]);
            else if (args[i] is "--out" or "-o" && i + 1 < args.Length)
                config.OutputRoot = args[++i];
            else if (args[i] is "--interval" && i + 1 < args.Length && int.TryParse(args[++i], out var mins))
                config.WatchIntervalMinutes = mins;
        }
    }
}
