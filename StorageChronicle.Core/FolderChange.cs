namespace StorageChronicle.Core;

/// <summary>
/// A directory's cumulative allocation changes, including all its descendants.
/// Ancestor and descendant totals overlap: never add visible rows together.
/// The figures are inventory changes, not independently verified volume consumption.
/// </summary>
public sealed record FolderChange(
    string FolderPath,
    long KnownNetDeltaBytes,
    long KnownGrowthBytes,
    long KnownReductionBytes,
    long ChangedGroupCount,
    long UnknownAllocationGroups);

public sealed record StoredFolderSummary(
    long ComparisonId,
    string RootPath,
    long FolderCount,
    long RootKnownNetDeltaBytes,
    long RootUnknownAllocationGroups);

public sealed record FolderPage(
    long ComparisonId,
    int Offset,
    int PageSize,
    IReadOnlyList<FolderChange> Folders);
