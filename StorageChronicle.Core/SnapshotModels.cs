namespace StorageChronicle.Core;

/// <summary>A single observed path. Allocation may be unknown; null must never be treated as zero.</summary>
public sealed record FileRecord(
    string Path,
    long LogicalBytes,
    long? AllocatedBytes,
    string? FileIdentity,
    uint? HardLinkCount,
    DateTimeOffset LastWriteUtc);

public sealed record ScanProgress(int FileCount, int NoticeCount, string CurrentDirectory);

public enum IssueSeverity { Info, Warning, Error }

public sealed record ScanIssue(string Path, string Code, string Detail, IssueSeverity Severity);

/// <summary>Observations are collected over an interval, not at one atomic instant.</summary>
public sealed record DiskSnapshot(
    long Id,
    string RootPath,
    string VolumeRoot,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    long? VolumeCapacityBytes,
    long? FreeBytesBefore,
    long? FreeBytesAfter,
    IReadOnlyList<FileRecord> Files,
    IReadOnlyList<ScanIssue> Issues)
{
    public bool IsComplete => !Issues.Any(x => x.Severity == IssueSeverity.Error);
    public bool IsWholeVolume => PathComparer.Equals(RootPath, VolumeRoot);
    public int FailureCount => Issues.Count(x => x.Severity == IssueSeverity.Error);
}

public static class PathComparer
{
    public static StringComparer Instance { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static bool SamePath(string a, string b) => Instance.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)));
}

public enum ChangeKind
{
    Added,
    Deleted,
    Grew,
    Shrank,
    Moved,
    LinksChanged,
    LogicalSizeChanged,
    AllocationUnknown
}

public sealed record FileChange(
    ChangeKind Kind,
    string PrimaryPath,
    IReadOnlyList<string> BeforePaths,
    IReadOnlyList<string> AfterPaths,
    long? BeforeAllocatedBytes,
    long? AfterAllocatedBytes,
    long? AllocatedDeltaBytes,
    long BeforeLogicalBytes,
    long AfterLogicalBytes);

public sealed record ComparisonReport(
    long BaselineId,
    long CurrentId,
    IReadOnlyList<FileChange> Changes,
    long KnownAllocatedDeltaBytes,
    int UnknownAllocationChanges,
    long? VolumeUsedDeltaBytes,
    long? UnexplainedDeltaBytes,
    bool IsReconciliationProvisional,
    IReadOnlyList<string> Caveats);
