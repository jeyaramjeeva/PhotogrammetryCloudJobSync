namespace PhotogrammetryCloudJobSync;

public sealed class AppConfig
{
    /// <summary>Production | QA_Production | QA_Staging | Development</summary>
    public string Environment { get; set; } = "Production";

    public string OutputRoot { get; set; } = @"F:\Cloud Processing\Test Results";

    /// <summary>
    /// Connect project TRNs (same as SampleApp project dropdown), e.g.
    /// trn:connect:projects:northAmerica:InfPUvKBlyQ
    /// </summary>
    public List<string> ProjectTrns { get; set; } = new();

    /// <summary>Connect server/region location key, e.g. northAmerica.</summary>
    public string SelectedRegion { get; set; } = "northAmerica";

    public bool IncludeFailedJobs { get; set; } = true;

    /// <summary>How many jobs to download at the same time. 2 is a good balance; 3 max recommended.</summary>
    public int MaxConcurrentJobs { get; set; } = 2;

    /// <summary>File-level parallelism inside one job.</summary>
    public int MaxConcurrentFileDownloads { get; set; } = 3;

    /// <summary>
    /// If a job folder already exists, compare against cloud file list and download only missing files.
    /// If false, any existing JobId folder is skipped entirely (old behavior).
    /// </summary>
    public bool VerifyAndRepairMissingFiles { get; set; } = true;

    /// <summary>
    /// Keep running overnight: after each full scan, wait and scan again for newly completed jobs.
    /// Stop with Ctrl+C.
    /// </summary>
    public bool WatchMode { get; set; } = true;

    /// <summary>Minutes to wait between watch-mode scans. UI presets: 15, 60, 360, 1440.</summary>
    public int WatchIntervalMinutes { get; set; } = 60;

    /// <summary>How many times to retry a failed file download (plus the first attempt).</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Base delay in seconds for exponential backoff (1s, 2s, 4s, ...).</summary>
    public int RetryBaseDelaySeconds { get; set; } = 2;

    /// <summary>
    /// If true, expired login opens a browser automatically. Tray UI keeps this false —
    /// user clicks Sign in instead (Connect Sync style).
    /// </summary>
    public bool AllowInteractiveReLogin { get; set; } = false;
}
