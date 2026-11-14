using StorageChronicle.Core;

namespace StorageChronicle.Tests;

public sealed class ReconciliationTests
{
    private static readonly string Volume = Path.GetPathRoot(Path.GetTempPath())!;

    private static SnapshotCoverage Coverage(
        long id, long? before, long? after, long? capacity = 100_000,
        string? root = null, long errors = 0, long missingAllocation = 0,
        long missingIdentity = 0) =>
        new(id, root ?? Volume, Volume,
            DateTimeOffset.UnixEpoch.AddMinutes(id * 10),
            DateTimeOffset.UnixEpoch.AddMinutes(id * 10 + 1),
            30, errors, 0, 0, missingAllocation, missingIdentity,
            before, after, [], capacity);

    private static StoredComparison Comparison(long? usedDelta = 500,
        long knownDelta = 300, long unknown = 0) =>
        new(20, 1, 2, 5, knownDelta, unknown, usedDelta,
            usedDelta.HasValue ? usedDelta - knownDelta : null, []);

    [Fact]
    public void Stable_capacity_reconciles_free_space_to_a_provisional_residual()
    {
        var baseline = Coverage(1, 9_000, 8_900);
        var current = Coverage(2, 8_500, 8_400);
        var report = ReconciliationEvaluator.Evaluate(Comparison(), baseline, current);

        Assert.Equal(ReconciliationStatus.Limited, report.Status); // scan-time drift
        Assert.Equal(500L, report.FreeSpaceLossBytes);
        Assert.Equal(0L, report.CapacityChangeBytes);
        Assert.Equal(500L, report.VolumeUsedChangeBytes);
        Assert.Equal(300L, report.KnownFileAllocationDeltaBytes);
        Assert.Equal(200L, report.UnexplainedResidualBytes);
        Assert.Equal(100L, report.BaselineScanFreeSpaceLossBytes);
        Assert.Equal(100L, report.CurrentScanFreeSpaceLossBytes);
        Assert.Equal(400L, report.BetweenScansFreeSpaceLossBytes);
        Assert.Contains(report.Limitations, x => x.Contains("not atomic"));
    }

    [Fact]
    public void Stable_full_volume_without_recorded_gaps_is_still_only_provisional()
    {
        var baseline = Coverage(1, 9_000, 9_000);
        var current = Coverage(2, 8_500, 8_500);
        var report = ReconciliationEvaluator.Evaluate(Comparison(), baseline, current);

        Assert.Equal(ReconciliationStatus.Provisional, report.Status);
        Assert.Equal(200L, report.UnexplainedResidualBytes);
        Assert.Contains(report.Limitations, x => x.Contains("not proof"));
    }

    [Fact]
    public void Changed_capacity_blocks_used_space_and_residual_claims()
    {
        var baseline = Coverage(1, 9_000, 9_000, 100_000);
        var current = Coverage(2, 8_500, 8_500, 110_000);
        var report = ReconciliationEvaluator.Evaluate(Comparison(), baseline, current);

        Assert.Equal(ReconciliationStatus.Unavailable, report.Status);
        Assert.Equal(500L, report.FreeSpaceLossBytes);
        Assert.Equal(10_000L, report.CapacityChangeBytes);
        Assert.Null(report.VolumeUsedChangeBytes);
        Assert.Null(report.UnexplainedResidualBytes);
        Assert.Contains(report.Limitations, x => x.Contains("capacity"));
    }

    [Fact]
    public void Missing_free_space_and_capacity_do_not_fabricate_zero()
    {
        var baseline = Coverage(1, null, null, null);
        var current = Coverage(2, null, null, null);
        var report = ReconciliationEvaluator.Evaluate(Comparison(null), baseline, current);

        Assert.Equal(ReconciliationStatus.Unavailable, report.Status);
        Assert.Null(report.FreeSpaceLossBytes);
        Assert.Null(report.CapacityChangeBytes);
        Assert.Null(report.VolumeUsedChangeBytes);
        Assert.Null(report.UnexplainedResidualBytes);
        Assert.Null(report.BetweenScansFreeSpaceLossBytes);
    }

    [Fact]
    public void Directory_scope_and_scan_errors_prevent_overconfident_label()
    {
        string subdir = Path.Combine(Volume, "SyntheticTestDirectory");
        var baseline = Coverage(1, 9_000, 9_000, root: subdir, errors: 2,
            missingAllocation: 1, missingIdentity: 1);
        var current = Coverage(2, 8_500, 8_500, root: subdir, errors: 1);
        var report = ReconciliationEvaluator.Evaluate(Comparison(500, 300, 1), baseline, current);

        Assert.Equal(ReconciliationStatus.Limited, report.Status);
        Assert.Contains(report.Limitations, x => x.Contains("Only a directory"));
        Assert.Contains(report.Limitations, x => x.Contains("baseline 2, current 1"));
        Assert.Contains(report.Limitations, x => x.Contains("unknown allocated size"));
        Assert.Equal(200L, report.UnexplainedResidualBytes);
    }

    [Fact]
    public void Comparison_ids_and_saved_deltas_must_match_source_evidence()
    {
        var baseline = Coverage(1, 9_000, 9_000);
        var current = Coverage(2, 8_500, 8_500);
        Assert.Throws<ArgumentException>(() =>
            ReconciliationEvaluator.Evaluate(Comparison() with { CurrentId = 99 },
                baseline, current));
        Assert.Throws<InvalidDataException>(() =>
            ReconciliationEvaluator.Evaluate(Comparison(499), baseline, current));
    }

    [Fact]
    public void Repository_reconciliation_reuses_existing_snapshot_and_comparison_records()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "StorageChronicleReconciliation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteSnapshotRepository(Path.Combine(dir, "data.db"));
            DiskSnapshot Create(long free, FileRecord[] files) =>
                new(0, dir, Path.GetPathRoot(dir)!, DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch.AddSeconds(1), 100_000,
                    free, free, files, []);
            long beforeId = store.Save(Create(9_000, []));
            long afterId = store.Save(Create(8_500,
                [new FileRecord(Path.Combine(dir, "new.txt"), 300, 300, "NEW", 1,
                    DateTimeOffset.UnixEpoch)]));
            var comparison = store.CompareAndStore(beforeId, afterId);
            var evidence = store.LoadReconciliation(comparison.Id);

            Assert.Equal(500L, evidence.FreeSpaceLossBytes);
            Assert.Equal(300L, evidence.KnownFileAllocationDeltaBytes);
            Assert.Equal(200L, evidence.UnexplainedResidualBytes);
            Assert.Equal(ReconciliationStatus.Limited, evidence.Status);
            Assert.Equal(2, store.List().Count);
            Assert.Equal(comparison.ChangeCount,
                store.LoadComparison(comparison.Id).ChangeCount);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Nonexistent_reconciliation_is_not_silently_created()
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "StorageChronicleReconciliation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SqliteSnapshotRepository(Path.Combine(dir, "data.db"));
            Assert.Throws<KeyNotFoundException>(() => store.LoadReconciliation(12345));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
