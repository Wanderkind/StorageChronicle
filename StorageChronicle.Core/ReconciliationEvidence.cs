namespace StorageChronicle.Core;

/// <summary>
/// Conservative reconciliation classification. "Provisional" never means complete:
/// Windows-managed allocations and other unobserved usage may remain.
/// </summary>
public enum ReconciliationStatus
{
    Unavailable,
    Limited,
    Provisional
}

/// <summary>
/// Separates observed free-space changes from measured file allocation changes.
/// A residual is an accounting difference, not attribution to any component.
/// </summary>
public sealed record ReconciliationEvidence(
    long ComparisonId,
    ReconciliationStatus Status,
    SnapshotCoverage Baseline,
    SnapshotCoverage Current,
    long? FreeSpaceLossBytes,
    long? CapacityChangeBytes,
    long? VolumeUsedChangeBytes,
    long KnownFileAllocationDeltaBytes,
    long? UnexplainedResidualBytes,
    long UnknownChangedFileGroups,
    long? BaselineScanFreeSpaceLossBytes,
    long? CurrentScanFreeSpaceLossBytes,
    long? BetweenScansFreeSpaceLossBytes,
    IReadOnlyList<string> Limitations);

public static class ReconciliationEvaluator
{
    public static ReconciliationEvidence Evaluate(
        StoredComparison comparison, SnapshotCoverage baseline, SnapshotCoverage current)
    {
        if (comparison.BaselineId != baseline.SnapshotId ||
            comparison.CurrentId != current.SnapshotId)
            throw new ArgumentException("Coverage snapshots do not match the comparison.");
        if (!PathComparer.SamePath(baseline.RootPath, current.RootPath) ||
            !PathComparer.SameVolume(baseline.VolumeRoot, current.VolumeRoot))
            throw new ArgumentException("Snapshots must cover the same root and volume.");

        long? freeLoss = Difference(baseline.FreeBytesAfter, current.FreeBytesAfter);
        long? capacityChange = Difference(current.VolumeCapacityBytes, baseline.VolumeCapacityBytes);
        bool capacityKnownAndStable =
            baseline.VolumeCapacityBytes.HasValue &&
            current.VolumeCapacityBytes.HasValue &&
            baseline.VolumeCapacityBytes.Value == current.VolumeCapacityBytes.Value;

        long? usedChange = capacityKnownAndStable ? freeLoss : null;
        long? residual = usedChange.HasValue
            ? checked(usedChange.Value - comparison.KnownAllocatedDeltaBytes)
            : null;

        var limitations = new List<string>();
        if (!baseline.IsWholeVolume || !current.IsWholeVolume)
            limitations.Add("Only a directory was inventoried, but free space was measured for the whole volume.");
        if (baseline.ErrorCount > 0 || current.ErrorCount > 0)
            limitations.Add($"Recorded scan failures: baseline {baseline.ErrorCount}, current {current.ErrorCount}. The number and size of inaccessible files are unknown.");
        if (baseline.MissingAllocationCount > 0 || current.MissingAllocationCount > 0)
            limitations.Add($"Files with unknown allocated size: baseline {baseline.MissingAllocationCount}, current {current.MissingAllocationCount}. Their allocation changes cannot be reconciled.");
        if (baseline.MissingIdentityCount > 0 || current.MissingIdentityCount > 0)
            limitations.Add("Some file identities were unavailable, so hard-link grouping and rename matching may be incomplete.");
        if (comparison.UnknownAllocationChanges > 0)
            limitations.Add($"{comparison.UnknownAllocationChanges} changed file groups have an unknown allocation delta.");
        if (baseline.Categories.Any(x => x.Code == "REPARSE_EXCLUDED") ||
            current.Categories.Any(x => x.Code == "REPARSE_EXCLUDED"))
            limitations.Add("One or both scans excluded reparse points such as junctions or symbolic links.");
        if (!capacityKnownAndStable)
            limitations.Add("Volume capacity was unknown or changed. Free-space loss is displayed, but used-space change and residual cannot be reconciled on a constant-capacity basis.");

        long? baselineDrift = Difference(baseline.FreeBytesBefore, baseline.FreeBytesAfter);
        long? currentDrift = Difference(current.FreeBytesBefore, current.FreeBytesAfter);
        if (baselineDrift is not null && baselineDrift.Value != 0 ||
            currentDrift is not null && currentDrift.Value != 0)
            limitations.Add("At least one scan observed changing free space between its start and finish. Snapshots are not atomic.");

        if (freeLoss is null)
            limitations.Add("Free-space readings are missing, so volume free-space change is unavailable.");

        if (usedChange.HasValue && comparison.VolumeUsedDeltaBytes.HasValue &&
            comparison.VolumeUsedDeltaBytes.Value != usedChange.Value)
            throw new InvalidDataException("Saved comparison delta does not match its source free-space readings.");

        limitations.Add("Unexplained residual is not proof of system-file growth: it may include unscanned files, Windows-managed metadata, VSS, inaccessible contents, concurrent changes, or measurement differences.");
        ReconciliationStatus status = usedChange is null
            ? ReconciliationStatus.Unavailable
            : limitations.Count > 1
                ? ReconciliationStatus.Limited
                : ReconciliationStatus.Provisional;

        return new(comparison.Id, status, baseline, current, freeLoss,
            capacityChange, usedChange, comparison.KnownAllocatedDeltaBytes,
            residual, comparison.UnknownAllocationChanges, baselineDrift,
            currentDrift, Difference(baseline.FreeBytesAfter, current.FreeBytesBefore),
            limitations);
    }

    private static long? Difference(long? left, long? right) =>
        left.HasValue && right.HasValue
            ? checked(left.Value - right.Value)
            : null;
}
