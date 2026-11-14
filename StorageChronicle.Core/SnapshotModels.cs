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

/// <summary>Lightweight totals returned by the streaming scanner.</summary>
public sealed record DiskScanSummary(
    string RootPath,
    string VolumeRoot,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    long? VolumeCapacityBytes,
    long? FreeBytesBefore,
    long? FreeBytesAfter,
    int FileCount,
    int NoticeCount,
    int ErrorCount);

/// <summary>Only a bounded number of sample issues is retained in application memory.</summary>
public sealed record SavedScanResult(
    long Id,
    DiskScanSummary Summary,
    IReadOnlyList<ScanIssue> FirstIssues);

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
    public bool IsWholeVolume => PathComparer.SamePath(RootPath, VolumeRoot);
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

    /// <summary>
    /// Compatible with Phase 1A snapshots that recorded a bare drive letter
    /// (C:) rather than the canonical volume root (C:\).
    /// </summary>
    public static bool SameVolume(string a, string b) => Instance.Equals(
        CanonicalVolume(a), CanonicalVolume(b));

    private static string CanonicalVolume(string volume)
    {
        if (OperatingSystem.IsWindows() && volume.Length == 2 &&
            char.IsLetter(volume[0]) && volume[1] == ':')
            return char.ToUpperInvariant(volume[0]) + @":\";
        return Path.GetPathRoot(Path.GetFullPath(volume)) ??
            Path.GetFullPath(volume);
    }
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
