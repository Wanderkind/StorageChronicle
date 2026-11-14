namespace StorageChronicle.Core;

public enum ShadowProbeStatus
{
    Complete,
    Partial,
    Unavailable
}

/// <summary>
/// WMI reports UsedSpace within the diff-area allocation and AllocatedSpace
/// reserved on the diff-area volume. Neither is a whole-volume accounting.
/// </summary>
public sealed record ShadowStorageArea(
    string SourceVolumeReference,
    string DiffVolumeReference,
    string? SourceDrive,
    string? DiffDrive,
    long? UsedBytes,
    long? AllocatedBytes,
    long? MaximumBytes);

public sealed record ShadowStorageMeasurement(
    long Id,
    DateTimeOffset ObservedUtc,
    ShadowProbeStatus Status,
    string? Diagnostic,
    IReadOnlyList<ShadowStorageArea> Areas)
{
    /// <summary>
    /// Null means cannot establish a total. A successful query with zero
    /// associations reports zero, not "VSS is absent everywhere".
    /// </summary>
    public long? AllocatedOnDrive(string drive)
    {
        if (Status != ShadowProbeStatus.Complete)
            return null;
        if (Areas.Any(x => x.DiffDrive is null || x.AllocatedBytes is null))
            return null;

        string normalized = NormalizeDrive(drive);
        long total = 0;
        foreach (ShadowStorageArea area in Areas)
            if (NormalizeDrive(area.DiffDrive!) == normalized)
                total = checked(total + area.AllocatedBytes!.Value);
        return total;
    }

    public long? UsedOnDrive(string drive)
    {
        if (Status != ShadowProbeStatus.Complete ||
            Areas.Any(x => x.DiffDrive is null || x.UsedBytes is null))
            return null;
        string normalized = NormalizeDrive(drive);
        long total = 0;
        foreach (ShadowStorageArea area in Areas)
            if (NormalizeDrive(area.DiffDrive!) == normalized)
                total = checked(total + area.UsedBytes!.Value);
        return total;
    }

    private static string NormalizeDrive(string drive) =>
        drive.Trim().TrimEnd('\\', '/').ToUpperInvariant();
}

public sealed record ShadowStorageDifference(
    long EarlierId,
    long LaterId,
    string DiffAreaDrive,
    long? EarlierAllocatedBytes,
    long? LaterAllocatedBytes,
    long? AllocatedDeltaBytes,
    string Interpretation);

public static class ShadowStorageEvaluator
{
    public static ShadowStorageDifference Compare(
        ShadowStorageMeasurement earlier,
        ShadowStorageMeasurement later,
        string diffAreaDrive)
    {
        if (earlier.ObservedUtc > later.ObservedUtc)
            throw new ArgumentException("Measurements must be ordered by time.");

        long? before = earlier.AllocatedOnDrive(diffAreaDrive);
        long? after = later.AllocatedOnDrive(diffAreaDrive);
        long? delta = before.HasValue && after.HasValue
            ? checked(after.Value - before.Value) : null;

        return new(earlier.Id, later.Id, diffAreaDrive,
            before, after, delta,
            delta.HasValue
                ? "Change in WMI-reported allocated shadow-copy differential-area space. " +
                  "It is separate evidence, not a direct subtraction from the unexplained residual."
                : "Not comparable: at least one reading is unavailable, partial, or missing drive attribution.");
    }
}
