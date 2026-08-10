using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Trimble.Gcs.Photogrammetry.Sdk;
using Trimble.Gcs.Photogrammetry.Sdk.Model.Datasets;
using Trimble.Gcs.Photogrammetry.Sdk.Model.Jobs;

namespace PhotogrammetryCloudJobSync;

public sealed class BatchDownloader
{
    private const string CompleteMarker = ".download_ok";
    private const string TimingsFileName = "download_timings.txt";

    private readonly PhotogrammetryClient _client;
    private readonly AppConfig _config;
    private readonly AuthSession _auth;
    private readonly object _logLock = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromHours(2) };
    private readonly ConcurrentDictionary<string, SyncFileProgress> _activeDownloads = new();
    private long _lastProgressPublishMs;
    private string _lastStatusDetail = "";

    public Action<string>? LogSink { get; set; }
    public Action<SyncProgress>? ProgressSink { get; set; }

    public BatchDownloader(PhotogrammetryClient client, AppConfig config, AuthSession auth)
    {
        _client = client;
        _config = config;
        _auth = auth;
    }

    /// <summary>One sync pass (used by tray SyncService). Does not wait/watch.</summary>
    public async Task<PassResult> RunPassAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_config.OutputRoot);

        var projects = ResolveProjects();
        var jobParallelism = Math.Clamp(_config.MaxConcurrentJobs, 1, 3);
        var fileParallelism = Math.Clamp(_config.MaxConcurrentFileDownloads, 1, 6);

        Log("Settings");
        Log($"  Jobs at once     : {jobParallelism}");
        Log($"  Files at once    : {fileParallelism}");
        Log($"  Verify/repair    : {_config.VerifyAndRepairMissingFiles}");
        Log($"  Output folder    : {_config.OutputRoot}");
        ReportProgress("Starting sync…", "Scanning cloud…", 0, active: true, clearFiles: true);

        await _auth.EnsureAuthenticatedAsync(ct, "before sync pass").ConfigureAwait(false);

        RunSummary summary;
        try
        {
            summary = await RunOnceAsync(projects, jobParallelism, fileParallelism, ct).ConfigureAwait(false);
        }
        catch (AuthExpiredException)
        {
            throw;
        }
        catch (Exception ex) when (AuthSession.IsAuthFailure(ex))
        {
            Log($"Login error during pass: {ex.Message}");
            try
            {
                await _auth.EnsureAuthenticatedAsync(ct, "after API auth error").ConfigureAwait(false);
                Log("Login recovered — retrying this pass once...");
                summary = await RunOnceAsync(projects, jobParallelism, fileParallelism, ct).ConfigureAwait(false);
            }
            catch (Exception recoverEx)
            {
                AuthSession.WriteAlertFile(_config.OutputRoot,
                    "API unauthorized and re-login failed.\n" +
                    "Original: " + ex.Message + "\nRecovery: " + recoverEx.Message);
                throw new AuthExpiredException(
                    "Login failed during download. See LOGIN_FAILED_ALERT.txt.", recoverEx);
            }
        }

        Log("");
        Log("==================== PASS SUMMARY ====================");
        Log($"  Downloaded / repaired : {summary.Downloaded}");
        Log($"  Already complete      : {summary.SkippedExisting}");
        Log($"  Still running (wait)  : {summary.SkippedNotReady}");
        Log($"  Failed                : {summary.Failed}");
        Log($"  Errors / deferred     : {summary.Errors}");
        WriteAuthHeartbeat(0, summary);
        ReportProgress(
            "Pass finished",
            $"Downloaded {summary.Downloaded}, skipped {summary.SkippedExisting}, waiting {summary.SkippedNotReady}, failed {summary.Failed}",
            100,
            active: false,
            clearFiles: true);

        return new PassResult(
            summary.Downloaded,
            summary.SkippedExisting,
            summary.SkippedNotReady,
            summary.Failed,
            summary.Errors);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_config.OutputRoot);

        var projects = ResolveProjects();
        var jobParallelism = Math.Clamp(_config.MaxConcurrentJobs, 1, 3);
        var fileParallelism = Math.Clamp(_config.MaxConcurrentFileDownloads, 1, 6);
        var watch = _config.WatchMode;
        var interval = TimeSpan.FromMinutes(Math.Max(1, _config.WatchIntervalMinutes));

        Log("Settings");
        Log($"  Jobs at once     : {jobParallelism}  (job N can start while job N-1 still downloads large files)");
        Log($"  Files at once    : {fileParallelism}  (parallel files inside one job)");
        Log($"  Verify/repair    : {_config.VerifyAndRepairMissingFiles}");
        Log($"  Retry/backoff    : {_config.RetryCount} retries, base {_config.RetryBaseDelaySeconds}s");
        Log($"  Watch mode       : {watch} every {interval.TotalMinutes:0} min (Ctrl+C to stop)");
        Log($"  Output folder    : {_config.OutputRoot}");
        Log("  Tip: every log line is tagged [Job ...] so parallel work stays readable.");

        var pass = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            pass++;
            Log("");
            Log($"#################### PASS {pass}  {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} ####################");

            var result = await RunPassAsync(ct).ConfigureAwait(false);

            Log($"  Login                 : OK @ {DateTimeOffset.Now:HH:mm:ss}");
            _ = result;

            if (!watch)
                break;

            Log("");
            Log($"Watch: waiting {interval.TotalMinutes:0} minutes, then scanning again... (Ctrl+C to stop)");
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    private async Task<RunSummary> RunOnceAsync(
        List<string> projects,
        int jobParallelism,
        int fileParallelism,
        CancellationToken ct)
    {
        var summary = new RunSummary();

        foreach (var projectTrn in projects)
        {
            ct.ThrowIfCancellationRequested();
            Log("");
            Log($"==================== PROJECT ====================");
            Log($"  {projectTrn}");

            IReadOnlyList<Dataset> datasets;
            try
            {
                datasets = await _client.Datasets.ListDatasetsAsync(projectTrn, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (AuthSession.IsAuthFailure(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"  ERROR listing datasets: {ex.Message}");
                Interlocked.Increment(ref summary.Errors);
                continue;
            }

            Log($"  Datasets in project: {datasets.Count}");
            Log("");

            // Process one dataset fully before moving to the next (easy to follow)
            foreach (var dataset in datasets.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                await ProcessDatasetAsync(projectTrn, dataset, jobParallelism, fileParallelism, summary, ct)
                    .ConfigureAwait(false);
            }
        }

        return summary;
    }

    private async Task ProcessDatasetAsync(
        string projectTrn,
        Dataset dataset,
        int jobParallelism,
        int fileParallelism,
        RunSummary summary,
        CancellationToken ct)
    {
        var datasetName = SanitizeName(
            string.IsNullOrWhiteSpace(dataset.Name) ? (dataset.Id ?? "unknown-dataset") : dataset.Name!);
        var datasetFolder = Path.Combine(_config.OutputRoot, datasetName);

        Log("--------------------------------------------------");
        Log($"DATASET: {datasetName}");
        Log($"  Id     : {dataset.Id}");
        Log($"  Folder : {datasetFolder}");

        if (string.IsNullOrWhiteSpace(dataset.Id))
        {
            Log("  ERROR: dataset has no Id — skipping");
            Interlocked.Increment(ref summary.Errors);
            return;
        }

        IReadOnlyList<Job> jobs;
        try
        {
            jobs = await _client.Jobs.ListJobsAsync(projectTrn, dataset.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AuthSession.IsAuthFailure(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"  ERROR listing jobs: {ex.Message}");
            Interlocked.Increment(ref summary.Errors);
            return;
        }

        var ordered = jobs.OrderBy(j => j.CreatedAt).ToList();
        Log($"  Jobs found: {ordered.Count}");

        if (ordered.Count == 0)
        {
            Log("  (no jobs in this dataset)");
            Log("");
            return;
        }

        var toProcess = new List<Job>();
        var index = 0;
        foreach (var job in ordered)
        {
            index++;
            if (string.IsNullOrWhiteSpace(job.Id))
            {
                Log($"  Job {index}/{ordered.Count}: (empty id) — skip");
                Interlocked.Increment(ref summary.Errors);
                continue;
            }

            var label = JobLabel(job);
            if (!IsSuccess(job.Status) && !IsFailed(job.Status))
            {
                Log($"  Job {index}/{ordered.Count}: {label}");
                Log($"           status = {job.Status} — still running, will check again later");
                Interlocked.Increment(ref summary.SkippedNotReady);
                continue;
            }

            if (IsFailed(job.Status) && !_config.IncludeFailedJobs)
            {
                Log($"  Job {index}/{ordered.Count}: {label}");
                Log("           FAILED — skipped (IncludeFailedJobs=false)");
                Interlocked.Increment(ref summary.SkippedNotReady);
                continue;
            }

            toProcess.Add(job);
            Log($"  Job {index}/{ordered.Count}: {label} — will check / download");
        }

        if (toProcess.Count == 0)
        {
            Log("  Nothing to download in this dataset right now.");
            Log("");
            return;
        }

        Log($"  Processing {toProcess.Count} finished job(s) now...");
        Log("");

        if (jobParallelism <= 1)
        {
            var n = 0;
            foreach (var job in toProcess)
            {
                n++;
                await ProcessJobAsync(projectTrn, dataset.Id, datasetName, datasetFolder, job, n, toProcess.Count, fileParallelism, summary, ct)
                    .ConfigureAwait(false);
            }
        }
        else
        {
            using var gate = new SemaphoreSlim(jobParallelism, jobParallelism);
            var n = 0;
            var tasks = toProcess.Select(async job =>
            {
                var myIndex = Interlocked.Increment(ref n);
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await ProcessJobAsync(projectTrn, dataset.Id, datasetName, datasetFolder, job, myIndex, toProcess.Count, fileParallelism, summary, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        Log($"DATASET DONE: {datasetName}");
        Log("");
    }

    private async Task ProcessJobAsync(
        string projectTrn,
        string datasetId,
        string datasetName,
        string datasetFolder,
        Job job,
        int jobIndex,
        int jobTotal,
        int fileParallelism,
        RunSummary summary,
        CancellationToken ct)
    {
        var label = JobLabel(job);
        var tag = JobTag(job);
        var folderName = BuildJobFolderName(job);
        var existingFolder = FindExistingJobFolder(datasetFolder, job.Id!);
        var targetFolder = existingFolder ?? Path.Combine(datasetFolder, folderName);

        Log($"===== Job {jobIndex}/{jobTotal}: {label} =====");
        Log($"  [{tag}] Status : {job.Status}");
        Log($"  [{tag}] Folder : {targetFolder}");

        var jobHeadline = $"Job {jobIndex}/{jobTotal} · {datasetName} · {tag}";

        try
        {
            // Fast path: already downloaded and marked complete — do not re-hit the cloud.
            if (existingFolder != null && File.Exists(Path.Combine(existingFolder, CompleteMarker)))
            {
                ReportProgress("Already on disk — skipping", 100, active: true);
                Log($"  [{tag}] SKIP — already complete (.download_ok)");
                Log("");
                Interlocked.Increment(ref summary.SkippedExisting);
                return;
            }

            ReportProgress("Asking cloud for file list…", 2, active: true);
            Log($"  [{tag}] Step 1/3: asking cloud for output file list...");
            var swList = Stopwatch.StartNew();
            var listing = await ListRemoteFilesAsync(projectTrn, datasetId, job.Id!, tag, ct).ConfigureAwait(false);
            swList.Stop();
            Log($"  [{tag}] Step 1/3 done in {swList.Elapsed.TotalSeconds:0.0}s");

            if (!listing.Succeeded)
            {
                ReportProgress("Cloud listing failed — will retry next pass", 0, active: true);
                Log($"  [{tag}] RESULT : cloud listing failed — NOT marking complete (retry next pass)");
                Log("");
                Interlocked.Increment(ref summary.Errors);
                return;
            }

            var remoteFiles = listing.Files;
            Log($"  [{tag}] Cloud says: {remoteFiles.Count} downloadable file(s)");

            if (existingFolder != null)
            {
                if (remoteFiles.Count == 0)
                {
                    if (IsFailed(job.Status))
                    {
                        EnsureCompleteMarker(existingFolder, $"failed job, no outputs @ {DateTimeOffset.Now:o}\n");
                        Log($"  [{tag}] RESULT : failed job, no outputs in cloud — SKIP (complete)");
                        Interlocked.Increment(ref summary.SkippedExisting);
                    }
                    else
                    {
                        Log($"  [{tag}] RESULT : Completed job but cloud returned 0 files — DEFER (not marking complete)");
                        Interlocked.Increment(ref summary.Errors);
                    }

                    Log("");
                    return;
                }

                var missing = GetMissingFiles(existingFolder, remoteFiles);
                Log($"  [{tag}] Local check: {remoteFiles.Count - missing.Count} already on disk, {missing.Count} missing");

                if (missing.Count == 0)
                {
                    EnsureCompleteMarker(existingFolder, $"verified complete @ {DateTimeOffset.Now:o}\nfiles={remoteFiles.Count}\n");
                    ReportProgress($"All {remoteFiles.Count} files already on disk — skipping", 100, active: true);
                    Log($"  [{tag}] SKIP — all {remoteFiles.Count} files already here");
                    Log("");
                    Interlocked.Increment(ref summary.SkippedExisting);
                    return;
                }

                if (!_config.VerifyAndRepairMissingFiles)
                {
                    Log($"  [{tag}] RESULT : folder exists, missing {missing.Count} — SKIP (repair disabled)");
                    Log("");
                    Interlocked.Increment(ref summary.SkippedExisting);
                    return;
                }

                Log($"  [{tag}] Step 2/3: REPAIR — downloading {missing.Count} missing file(s) (up to {fileParallelism} at once)...");
                foreach (var m in missing)
                    Log($"  [{tag}]    need: {m.RelativePath}");

                var timings = await DownloadFilesAsync(
                        projectTrn, datasetId, job.Id!, existingFolder, missing, fileParallelism, tag, jobHeadline, ct)
                    .ConfigureAwait(false);
                WriteTimingsFile(existingFolder, job, timings, isRepair: true);

                var stillMissing = GetMissingFiles(existingFolder, remoteFiles);
                var hardFails = timings.Count(t => !t.Success && !t.Unavailable);
                if (stillMissing.Count == 0 || hardFails == 0)
                {
                    EnsureCompleteMarker(existingFolder, $"repaired @ {DateTimeOffset.Now:o}\nfiles={remoteFiles.Count}\n");
                    Log($"  [{tag}] Step 3/3: REPAIR DONE — saved to:");
                    Log($"  [{tag}]    {existingFolder}");
                    Interlocked.Increment(ref summary.Downloaded);
                }
                else
                {
                    Log($"  [{tag}] RESULT : REPAIR incomplete ({stillMissing.Count} still missing)");
                    Interlocked.Increment(ref summary.Failed);
                }

                Log("");
                return;
            }

            Directory.CreateDirectory(datasetFolder);
            Directory.CreateDirectory(targetFolder);

            if (remoteFiles.Count == 0)
            {
                if (IsFailed(job.Status))
                {
                    EnsureCompleteMarker(targetFolder, $"failed job, no outputs @ {DateTimeOffset.Now:o}\n");
                    WriteTimingsFile(targetFolder, job, Array.Empty<FileTiming>(), isRepair: false);
                    Log($"  [{tag}] RESULT : failed job, no outputs — marked complete");
                    Interlocked.Increment(ref summary.Downloaded);
                }
                else
                {
                    Log($"  [{tag}] RESULT : Completed job, 0 cloud files — DEFER (not marking complete)");
                    TryDeleteDirectory(targetFolder);
                    Interlocked.Increment(ref summary.Errors);
                }

                Log("");
                return;
            }

            Log($"  [{tag}] Step 2/3: DOWNLOAD — {remoteFiles.Count} file(s), up to {fileParallelism} in parallel");
            Log($"  [{tag}]    to: {targetFolder}");
            Log($"  [{tag}] File list:");
            var i = 0;
            foreach (var f in remoteFiles)
            {
                i++;
                Log($"  [{tag}]    {i,3}. {f.RelativePath}  ({FormatSize(f.Size ?? 0)})");
            }

            var toDownload = remoteFiles
                .Select(f => f with { LocalPath = Path.Combine(targetFolder, f.RelativePath) })
                .ToList();

            var timingsFresh = await DownloadFilesAsync(
                    projectTrn, datasetId, job.Id!, targetFolder, toDownload, fileParallelism, tag, jobHeadline, ct)
                .ConfigureAwait(false);
            WriteTimingsFile(targetFolder, job, timingsFresh, isRepair: false);

            var stillMissingFresh = GetMissingFiles(targetFolder, remoteFiles);
            var hardFailsFresh = timingsFresh.Count(t => !t.Success && !t.Unavailable);
            if (stillMissingFresh.Count == 0 || hardFailsFresh == 0)
            {
                EnsureCompleteMarker(targetFolder, $"ok @ {DateTimeOffset.Now:o}\nfiles={remoteFiles.Count}\n");
                Log($"  [{tag}] Step 3/3: DOWNLOAD DONE — {timingsFresh.Count(t => t.Success)} file(s) saved");
                Log($"  [{tag}]    {targetFolder}");
                Interlocked.Increment(ref summary.Downloaded);
            }
            else
            {
                Log($"  [{tag}] RESULT : DOWNLOAD incomplete — {stillMissingFresh.Count} file(s) still missing");
                Interlocked.Increment(ref summary.Failed);
            }

            Log("");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (AuthSession.IsAuthFailure(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"  [{tag}] ERROR: {ex.Message}");
            Log("");
            Interlocked.Increment(ref summary.Errors);
        }
    }

    private async Task<List<FileTiming>> DownloadFilesAsync(
        string projectTrn,
        string datasetId,
        string jobId,
        string jobFolder,
        List<RemoteFile> files,
        int fileParallelism,
        string tag,
        string jobHeadline,
        CancellationToken ct)
    {
        var timings = new ConcurrentBag<FileTiming>();
        using var gate = new SemaphoreSlim(fileParallelism, fileParallelism);
        var started = 0;
        var finished = 0;
        var total = files.Count;
        long totalBytes = files.Sum(f => Math.Max(0, f.Size ?? 0));
        long completedBytes = 0;

        var tasks = files.Select(async file =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            var n = Interlocked.Increment(ref started);
            var progressId = Guid.NewGuid().ToString("N");
            try
            {
                var localPath = file.LocalPath ?? Path.Combine(jobFolder, file.RelativePath);
                Log($"  [{tag}] START  file {n}/{total}: {file.RelativePath}");
                UpsertFileProgress(progressId, tag, file.RelativePath, 0, "Starting…");
                PublishDownloadProgress(force: true);

                var timing = await DownloadOneWithRetryAsync(
                        projectTrn, datasetId, jobId, file, localPath, tag, progressId,
                        total, () => finished, totalBytes, () => Volatile.Read(ref completedBytes), ct)
                    .ConfigureAwait(false);
                timings.Add(timing);

                if (timing.Success && timing.Bytes > 0)
                    Interlocked.Add(ref completedBytes, timing.Bytes);

                var f = Interlocked.Increment(ref finished);
                if (timing.Success)
                    Log($"  [{tag}] DONE   file {f}/{total}: {file.RelativePath} ({FormatSize(timing.Bytes)}, {FormatDuration(timing.Elapsed)})");
                else if (timing.Unavailable)
                    Log($"  [{tag}] SKIP   file {f}/{total}: {file.RelativePath} ({timing.Error})");
                else
                    Log($"  [{tag}] FAIL   file {f}/{total}: {file.RelativePath} — {timing.Error}");
            }
            finally
            {
                RemoveFileProgress(progressId);
                PublishDownloadProgress(force: true);
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        // Do NOT clear all active downloads — another parallel job may still be downloading.
        PublishDownloadProgress(force: true);
        return timings.OrderBy(t => t.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<FileTiming> DownloadOneWithRetryAsync(
        string projectTrn,
        string datasetId,
        string jobId,
        RemoteFile file,
        string localPath,
        string tag,
        string progressId,
        int filesInJob,
        Func<int> filesFinished,
        long jobTotalBytes,
        Func<long> jobCompletedBytes,
        CancellationToken ct)
    {
        var maxAttempts = Math.Max(1, _config.RetryCount + 1);
        var baseDelay = TimeSpan.FromSeconds(Math.Max(1, _config.RetryBaseDelaySeconds));
        Exception? lastError = null;

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var tempPath = localPath + ".partial";
            try
            {
                var url = await TryRefreshDownloadUrlAsync(projectTrn, datasetId, jobId, file.OutputType, file.FileName, ct)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(url))
                    url = file.DownloadUrl;

                if (string.IsNullOrEmpty(url))
                    return new FileTiming(file.RelativePath, 0, TimeSpan.Zero, false, attempt, "no download URL", Unavailable: true);

                if (File.Exists(tempPath))
                    File.Delete(tempPath);

                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if ((int)response.StatusCode is 400 or 403 or 404)
                {
                    sw.Stop();
                    return new FileTiming(file.RelativePath, 0, sw.Elapsed, false, attempt,
                        $"HTTP {(int)response.StatusCode}", Unavailable: true);
                }

                response.EnsureSuccessStatusCode();

                var expected = file.Size
                    ?? response.Content.Headers.ContentLength
                    ?? 0;

                await using (var remote = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var local = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.Read, 128 * 1024, true))
                {
                    await CopyWithProgressAsync(
                            remote, local, expected, file.RelativePath, sw,
                            tag, progressId, ct)
                        .ConfigureAwait(false);
                }

                sw.Stop();
                var bytes = new FileInfo(tempPath).Length;

                if (file.Size is > 0 && bytes < file.Size.Value * 0.98)
                    throw new IOException($"Downloaded size {bytes} < expected {file.Size.Value}");

                if (File.Exists(localPath))
                    File.Delete(localPath);
                File.Move(tempPath, localPath);

                UpsertFileProgress(progressId, tag, file.RelativePath, 100, "Done");
                PublishDownloadProgress(force: true);
                return new FileTiming(file.RelativePath, bytes, sw.Elapsed, true, attempt, null);
            }
            catch (OperationCanceledException)
            {
                TryDeleteFile(tempPath);
                throw;
            }
            catch (Exception ex) when (AuthSession.IsAuthFailure(ex))
            {
                TryDeleteFile(tempPath);
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                lastError = ex;
                TryDeleteFile(tempPath);

                if (attempt >= maxAttempts)
                    break;

                var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                UpsertFileProgress(progressId, tag, file.RelativePath, 0, $"Retry {attempt}/{maxAttempts}…");
                PublishDownloadProgress(force: true);
                Log($"  [{tag}] RETRY  {file.RelativePath} attempt {attempt}/{maxAttempts} in {FormatDuration(delay)} ({ex.Message})");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        return new FileTiming(file.RelativePath, 0, TimeSpan.Zero, false, maxAttempts, lastError?.Message ?? "unknown error");
    }

    private async Task CopyWithProgressAsync(
        Stream remote,
        Stream local,
        long expectedBytes,
        string relativePath,
        Stopwatch sw,
        string jobTag,
        string progressId,
        CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        long written = 0;
        var lastUi = Stopwatch.StartNew();

        while (true)
        {
            var read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            await local.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;

            if (lastUi.Elapsed < TimeSpan.FromMilliseconds(300))
                continue;

            var speed = written / Math.Max(sw.Elapsed.TotalSeconds, 0.1);
            int filePct;
            string fileDetail;
            if (expectedBytes > 0)
            {
                filePct = (int)Math.Min(99, 100.0 * written / expectedBytes);
                var etaSec = speed > 1 ? (expectedBytes - written) / speed : 0;
                fileDetail =
                    $"{FormatSize(written)} / {FormatSize(expectedBytes)}  ·  " +
                    $"{FormatSize((long)speed)}/s  ·  ETA {FormatDuration(TimeSpan.FromSeconds(etaSec))}";
            }
            else
            {
                filePct = 0;
                fileDetail = $"{FormatSize(written)} received  ·  {FormatSize((long)speed)}/s";
            }

            UpsertFileProgress(progressId, jobTag, relativePath, filePct, fileDetail);
            PublishDownloadProgress(force: false);
            lastUi.Restart();
        }
    }

    private async Task<string?> TryRefreshDownloadUrlAsync(
        string projectTrn,
        string datasetId,
        string jobId,
        string outputType,
        string fileName,
        CancellationToken ct)
    {
        try
        {
            var listed = await _client.Jobs
                .ListOutputFilesAsync(projectTrn, datasetId, jobId, outputType, ct)
                .ConfigureAwait(false);

            foreach (var f in listed)
            {
                var name = !string.IsNullOrEmpty(f.Name) ? f.Name : Path.GetFileName(f.Path ?? "");
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(f.DownloadUrl))
                    return f.DownloadUrl;
            }
        }
        catch (Exception ex) when (AuthSession.IsAuthFailure(ex))
        {
            throw;
        }
        catch
        {
            // ignore refresh failure; caller may still have old URL
        }

        return null;
    }

    private void WriteTimingsFile(string jobFolder, Job job, IReadOnlyList<FileTiming> timings, bool isRepair)
    {
        try
        {
            Directory.CreateDirectory(jobFolder);
            var path = Path.Combine(jobFolder, TimingsFileName);
            var sb = new StringBuilder();
            sb.AppendLine("Photogrammetry job download timings");
            sb.AppendLine($"JobId      : {job.Id}");
            sb.AppendLine($"Preset     : {job.Preset}");
            sb.AppendLine($"Status     : {job.Status}");
            sb.AppendLine($"Mode       : {(isRepair ? "repair" : "full-download")}");
            sb.AppendLine($"WrittenAt  : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine();

            var ok = timings.Where(t => t.Success).ToList();
            var fail = timings.Where(t => !t.Success).ToList();
            sb.AppendLine($"Files OK   : {ok.Count}");
            sb.AppendLine($"Files FAIL : {fail.Count}");
            sb.AppendLine($"Total size : {FormatSize(ok.Sum(t => t.Bytes))}");
            sb.AppendLine($"Sum times  : {FormatDuration(TimeSpan.FromMilliseconds(ok.Sum(t => t.Elapsed.TotalMilliseconds)))}");
            sb.AppendLine();
            sb.AppendLine($"{"Relative path",-70} {"Size",12} {"Seconds",10} {"Attempts",8} Status");
            sb.AppendLine(new string('-', 110));

            foreach (var t in timings.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                var status = t.Success ? "OK" : (t.Unavailable ? "UNAVAILABLE: " : "FAIL: ") + (t.Error ?? "");
                sb.AppendLine($"{t.RelativePath,-70} {FormatSize(t.Bytes),12} {t.Elapsed.TotalSeconds,10:0.0} {t.Attempts,8} {status}");
            }

            if (isRepair && File.Exists(path))
                File.AppendAllText(path, Environment.NewLine + "======== REPAIR ========" + Environment.NewLine + sb);
            else
                File.WriteAllText(path, sb.ToString());
        }
        catch
        {
            // ignore
        }
    }

    private async Task<RemoteListResult> ListRemoteFilesAsync(
        string projectTrn,
        string datasetId,
        string jobId,
        string tag,
        CancellationToken ct)
    {
        // Cap listing so a hung Photogrammetry API call cannot sit for the client HttpTimeout (30 min).
        using var listCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        listCts.CancelAfter(TimeSpan.FromSeconds(90));
        var listCt = listCts.Token;

        string[] outputTypes;
        try
        {
            Log($"  [{tag}]   → ListOutputTypes...");
            var sw = Stopwatch.StartNew();
            outputTypes = await _client.Jobs.ListOutputTypesAsync(projectTrn, datasetId, jobId, listCt)
                .ConfigureAwait(false);
            Log($"  [{tag}]   → {outputTypes.Length} output type(s) in {sw.Elapsed.TotalSeconds:0.0}s");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log($"  [{tag}] (list output types timed out after 90s)");
            return new RemoteListResult(Array.Empty<RemoteFile>(), Succeeded: false);
        }
        catch (Exception ex) when (AuthSession.IsAuthFailure(ex))
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"  [{tag}] (list output types failed: {ex.Message})");
            return new RemoteListResult(Array.Empty<RemoteFile>(), Succeeded: false);
        }

        if (outputTypes.Length == 0)
            return new RemoteListResult(Array.Empty<RemoteFile>(), Succeeded: true);

        var files = new ConcurrentBag<RemoteFile>();
        var typeErrors = 0;
        var listedTypes = 0;
        var listingParallelism = Math.Clamp(
            Math.Min(4, _config.MaxConcurrentFileDownloads > 0 ? _config.MaxConcurrentFileDownloads : 4),
            1, 6);

        Log($"  [{tag}]   → listing files for {outputTypes.Length} type(s) (parallelism {listingParallelism})...");

        try
        {
            await Parallel.ForEachAsync(
                outputTypes,
                new ParallelOptions { MaxDegreeOfParallelism = listingParallelism, CancellationToken = listCt },
                async (outputType, typeCt) =>
                {
                    try
                    {
                        var listed = await _client.Jobs
                            .ListOutputFilesAsync(projectTrn, datasetId, jobId, outputType, typeCt)
                            .ConfigureAwait(false);

                        foreach (var file in listed)
                        {
                            var name = !string.IsNullOrEmpty(file.Name)
                                ? file.Name
                                : Path.GetFileName(file.Path ?? "unknown");

                            files.Add(new RemoteFile(
                                outputType,
                                name,
                                file.DownloadUrl ?? string.Empty,
                                file.Size,
                                RelativeLocalPath(outputType, name)));
                        }

                        var done = Interlocked.Increment(ref listedTypes);
                        Log($"  [{tag}]   → type '{outputType}': {listed.Length} file(s) ({done}/{outputTypes.Length})");
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref typeErrors);
                        Log($"  [{tag}]   → type '{outputType}' timed out");
                    }
                    catch (Exception ex) when (AuthSession.IsAuthFailure(ex))
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref typeErrors);
                        Log($"  [{tag}] (list '{outputType}' failed: {ex.Message})");
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log($"  [{tag}] (file listing timed out after 90s)");
            return new RemoteListResult(files.ToList(), Succeeded: false);
        }

        return new RemoteListResult(files.ToList(), typeErrors < outputTypes.Length);
    }

    private static List<RemoteFile> GetMissingFiles(string localFolder, IReadOnlyList<RemoteFile> remoteFiles)
    {
        var missing = new List<RemoteFile>();
        foreach (var file in remoteFiles)
        {
            var localPath = Path.Combine(localFolder, file.RelativePath);
            if (!File.Exists(localPath))
            {
                missing.Add(file with { LocalPath = localPath });
                continue;
            }

            if (file.Size is > 0)
            {
                var localSize = new FileInfo(localPath).Length;
                if (localSize <= 0 || localSize < file.Size.Value * 0.98)
                    missing.Add(file with { LocalPath = localPath });
            }
        }

        return missing;
    }

    private void WriteAuthHeartbeat(int pass, RunSummary summary)
    {
        try
        {
            var path = Path.Combine(_config.OutputRoot, "DOWNLOADER_STATUS.txt");
            File.WriteAllText(path,
                "Job Output Batch Downloader — status\n" +
                $"Last successful pass : {pass}\n" +
                $"Last auth OK at      : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\n" +
                $"Downloaded/repaired  : {summary.Downloaded}\n" +
                $"Skipped complete     : {summary.SkippedExisting}\n" +
                $"Not ready            : {summary.SkippedNotReady}\n" +
                $"Failed               : {summary.Failed}\n" +
                $"Errors               : {summary.Errors}\n" +
                "\nIf LOGIN_FAILED_ALERT.txt exists here, login died and downloads stopped.\n");
        }
        catch
        {
            // ignore
        }
    }

    private List<string> ResolveProjects()
    {
        var projects = _config.ProjectTrns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Where(p => !p.Contains("PASTE_PROJECT_ID_HERE", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (projects.Count == 0)
            throw new InvalidOperationException("No ProjectTrns configured in appsettings.json.");

        return projects;
    }

    private static string JobLabel(Job job) =>
        $"{FormatPreset(job.Preset)}{(IsFailed(job.Status) ? " Failed" : "")}  {job.Id}";

    /// <summary>Short tag for parallel logs, e.g. High:a1b2c3d4</summary>
    private static string JobTag(Job job)
    {
        var id = job.Id ?? "?";
        var shortId = id.Length <= 8 ? id : id[..8];
        var preset = FormatPreset(job.Preset);
        var fail = IsFailed(job.Status) ? "F" : "";
        return $"{preset}{fail}:{shortId}";
    }

    private static string? FindExistingJobFolder(string datasetFolder, string jobId)
    {
        if (!Directory.Exists(datasetFolder))
            return null;

        foreach (var dir in Directory.EnumerateDirectories(datasetFolder))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith(".partial_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Contains(jobId, StringComparison.OrdinalIgnoreCase))
                return dir;
        }

        return null;
    }

    private static void EnsureCompleteMarker(string folder, string contents)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, CompleteMarker), contents);
    }

    private static string BuildJobFolderName(Job job)
    {
        var preset = FormatPreset(job.Preset);
        return IsFailed(job.Status) ? $"{preset} Failed {job.Id}" : $"{preset} {job.Id}";
    }

    private static string FormatPreset(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset))
            return "Unknown";

        return string.Join(" ",
            preset.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(PartToTitle));
    }

    private static string PartToTitle(string part)
    {
        if (part.Length == 0) return part;
        if (part.Equals("gsplat", StringComparison.OrdinalIgnoreCase)) return "Gsplat";
        if (part.Equals("3d", StringComparison.OrdinalIgnoreCase)) return "3D";
        return char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
    }

    private static bool IsSuccess(JobStatus? status) =>
        status is JobStatus.Completed or JobStatus.FINISHED;

    private static bool IsFailed(JobStatus? status) =>
        status is JobStatus.FAILED or JobStatus.EXECUTIONSTARTFAILED;

    private static string RelativeLocalPath(string outputType, string fileName) =>
        Path.Combine(SanitizeName(outputType), SanitizeName(fileName));

    private static string SanitizeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "unknown" : name.Trim();
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{t.TotalSeconds:0.0}s";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "-";
        double mb = bytes / (1024.0 * 1024.0);
        if (mb >= 1024) return $"{mb / 1024.0:0.00} GB";
        if (mb >= 1) return $"{mb:0.00} MB";
        return $"{bytes / 1024.0:0.0} KB";
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* ignore */ }
    }

    private void Log(string message)
    {
        lock (_logLock)
        {
            if (LogSink != null)
                LogSink(message);
            else
                Console.WriteLine(message);
        }
    }

    private void ReportProgress(
        string headlineOrStatus,
        string detail,
        int percent,
        bool active,
        bool clearFiles = false)
    {
        try
        {
            if (clearFiles)
                _activeDownloads.Clear();

            _lastStatusDetail = detail;
            var files = SnapshotFiles();
            var headline = BuildStableHeadline(files, headlineOrStatus);
            ProgressSink?.Invoke(new SyncProgress(
                headline,
                detail,
                Math.Clamp(percent, 0, 100),
                active,
                files));
        }
        catch
        {
            // UI sink must never break downloads
        }
    }

    private void ReportProgress(string status, int percent, bool active, bool clearFiles = false)
    {
        try
        {
            if (clearFiles)
                _activeDownloads.Clear();

            _lastStatusDetail = status;
            var files = SnapshotFiles();
            var headline = BuildStableHeadline(files, status);
            ProgressSink?.Invoke(new SyncProgress(
                headline,
                status,
                Math.Clamp(percent, 0, 100),
                active,
                files));
        }
        catch
        {
            // ignore
        }
    }

    private void PublishDownloadProgress(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && now - _lastProgressPublishMs < 200)
            return;
        _lastProgressPublishMs = now;

        try
        {
            var files = SnapshotFiles();
            var avg = files.Count == 0 ? 0 : (int)files.Average(f => f.Percent);
            var jobs = files.Select(f => f.JobTag).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
            var headline = jobs.Count == 0
                ? "Downloading"
                : jobs.Count == 1
                    ? $"Downloading · job {jobs[0]}"
                    : $"Downloading · {jobs.Count} jobs ({string.Join(", ", jobs)})";
            var detail = files.Count == 0
                ? (_lastStatusDetail.Length > 0 ? _lastStatusDetail : "Waiting…")
                : $"{files.Count} file(s) in progress";

            ProgressSink?.Invoke(new SyncProgress(headline, detail, Math.Clamp(avg, 0, 100), true, files));
        }
        catch
        {
            // ignore
        }
    }

    private static string BuildStableHeadline(IReadOnlyList<SyncFileProgress> files, string fallback)
    {
        if (files.Count == 0)
            return fallback;

        var jobs = files.Select(f => f.JobTag).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        return jobs.Count == 1
            ? $"Downloading · job {jobs[0]}"
            : $"Downloading · {jobs.Count} jobs ({string.Join(", ", jobs)})";
    }

    private List<SyncFileProgress> SnapshotFiles() =>
        _activeDownloads.Values
            .OrderBy(f => f.JobTag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();

    private void UpsertFileProgress(string id, string jobTag, string name, int percent, string detail)
    {
        _activeDownloads[id] = new SyncFileProgress(
            id,
            jobTag,
            name,
            Math.Clamp(percent, 0, 100),
            detail);
    }

    private void RemoveFileProgress(string id) =>
        _activeDownloads.TryRemove(id, out _);

    private sealed record RemoteListResult(IReadOnlyList<RemoteFile> Files, bool Succeeded);

    private sealed record RemoteFile(
        string OutputType,
        string FileName,
        string DownloadUrl,
        long? Size,
        string RelativePath,
        string? LocalPath = null);

    private sealed record FileTiming(
        string RelativePath,
        long Bytes,
        TimeSpan Elapsed,
        bool Success,
        int Attempts,
        string? Error,
        bool Unavailable = false);

    private sealed class RunSummary
    {
        public int Downloaded;
        public int SkippedExisting;
        public int SkippedNotReady;
        public int Failed;
        public int Errors;
    }
}

public sealed record PassResult(
    int Downloaded,
    int SkippedExisting,
    int SkippedNotReady,
    int Failed,
    int Errors);
