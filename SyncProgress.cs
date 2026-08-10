namespace PhotogrammetryCloudJobSync;

/// <summary>One file currently downloading (own progress bar in the UI).</summary>
public sealed record SyncFileProgress(
    string Id,
    string JobTag,
    string Name,
    int Percent,
    string Detail);

public enum SyncJobItemState
{
    Pending,
    Downloading,
    Done,
    Failed,
    Skipped
}

/// <summary>One job in the current pass queue (“What’s left” list).</summary>
public sealed record SyncJobItem(
    string Id,
    string Label,
    SyncJobItemState State,
    string? FolderPath);

/// <summary>Live progress for the UI panel (not written into the scrolling log).</summary>
public sealed record SyncProgress(
    string Headline,
    string Detail,
    int Percent,
    bool IsActive,
    IReadOnlyList<SyncFileProgress> ActiveFiles,
    IReadOnlyList<SyncJobItem> QueueItems)
{
    public static SyncProgress Idle(string detail = "Idle") =>
        new("Ready", detail, 0, false, Array.Empty<SyncFileProgress>(), Array.Empty<SyncJobItem>());
}
