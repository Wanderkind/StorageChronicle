namespace StorageChronicle.Core;

/// <summary>
/// Coverage diagnostics describe observed records and reported obstacles.
/// They do NOT establish that all files were discovered or any percentage of the
/// volume is covered, because an unreadable folder's contents are unknown.
/// </summary>
public sealed record ScanIssueCategory(IssueSeverity Severity, string Code, long Count);

public sealed record SnapshotCoverage(
    long SnapshotId,
    string RootPath,
    string VolumeRoot,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    long FileCount,
    long ErrorCount,
    long WarningCount,
    long InfoCount,
    long MissingAllocationCount,
    long MissingIdentityCount,
    long? FreeBytesBefore,
    long? FreeBytesAfter,
    IReadOnlyList<ScanIssueCategory> Categories,
    long? VolumeCapacityBytes = null)
{
    public long NoticeCount => ErrorCount + WarningCount + InfoCount;

    /// <summary>Only means no recorded hard errors, not complete disk coverage.</summary>
    public bool HasRecordedReadFailures => ErrorCount != 0;

    public bool IsWholeVolume =>
        PathComparer.SamePath(RootPath, VolumeRoot) ||
        (RootPath.Length == 2 && RootPath[1] == ':' &&
         PathComparer.SameVolume(RootPath, VolumeRoot));

    public string CoverageInterpretation =>
        ErrorCount > 0
            ? "Partial: one or more filesystem entries could not be inspected. " +
              "The size and number of files hidden by those failures are unknown."
            : "No filesystem read failures were recorded. This does not prove " +
              "complete volume accounting; exclusions and unmeasured Windows storage remain.";

    public bool HasMissingMetadata =>
        MissingAllocationCount > 0 || MissingIdentityCount > 0;
}

public sealed record ScanIssuePage(
    long SnapshotId,
    int Offset,
    int PageSize,
    IssueSeverity? Filter,
    IReadOnlyList<ScanIssue> Issues);
