namespace StorageChronicle.Core;

/// <summary>
/// Summary of a committed, disk-backed comparison. Detail rows are retrieved
/// through ReadComparisonPage; no full inventory or change list is retained.
/// </summary>
public sealed record StoredComparison(
    long Id,
    long BaselineId,
    long CurrentId,
    long ChangeCount,
    long KnownAllocatedDeltaBytes,
    long UnknownAllocationChanges,
    long? VolumeUsedDeltaBytes,
    long? UnexplainedDeltaBytes,
    IReadOnlyList<string> Caveats);

public sealed record ComparisonProgress(long ProcessedGroups, long ChangedGroups);

public sealed record ComparisonPage(
    long ComparisonId,
    int Offset,
    int PageSize,
    IReadOnlyList<FileChange> Changes);
