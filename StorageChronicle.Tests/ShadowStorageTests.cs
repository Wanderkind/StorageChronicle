using StorageChronicle.Core;

namespace StorageChronicle.Tests;

public sealed class ShadowStorageTests
{
    private sealed class Workspace : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(
            Path.GetTempPath(), "StorageChronicleVss-" + Guid.NewGuid().ToString("N"));
        public string DatabasePath => Path.Combine(DirectoryPath, "store.db");
        public Workspace() => Directory.CreateDirectory(DirectoryPath);
        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private static ShadowStorageMeasurement Reading(long id, int minute,
        ShadowProbeStatus status, params ShadowStorageArea[] areas) =>
        new(id, DateTimeOffset.UnixEpoch.AddMinutes(minute), status, null, areas);

    private static ShadowStorageArea Area(string diffDrive, long allocated, long used = 0) =>
        new("original", "diff-area", "C:", diffDrive, used, allocated, 50_000);

    [Fact]
    public void Complete_reading_sums_only_allocation_on_selected_diff_volume()
    {
        var reading = Reading(1, 1, ShadowProbeStatus.Complete,
            Area("C:", 2048, 1024),
            Area("D:", 8192, 4096),
            Area("C:", 3072, 1536));

        Assert.Equal(5120L, reading.AllocatedOnDrive(@"C:\"));
        Assert.Equal(2560L, reading.UsedOnDrive("c:"));
        Assert.Equal(8192L, reading.AllocatedOnDrive("D:"));
        Assert.Equal(0L, reading.AllocatedOnDrive("E:"));
    }

    [Fact]
    public void Missing_drive_or_size_and_partial_probes_are_unknown_not_zero()
    {
        var missingDrive = Reading(1, 1, ShadowProbeStatus.Complete,
            new ShadowStorageArea("source", "diff", null, null, 15, 20, null));
        var missingBytes = Reading(2, 2, ShadowProbeStatus.Complete,
            new ShadowStorageArea("source", "diff", null, "C:", null, null, null));
        var partial = Reading(3, 3, ShadowProbeStatus.Partial, Area("C:", 100));
        var unavailable = Reading(4, 4, ShadowProbeStatus.Unavailable);

        Assert.Null(missingDrive.AllocatedOnDrive("C:"));
        Assert.Null(missingBytes.AllocatedOnDrive("C:"));
        Assert.Null(partial.AllocatedOnDrive("C:"));
        Assert.Null(unavailable.AllocatedOnDrive("C:"));
        Assert.Equal(0L, Reading(5, 5, ShadowProbeStatus.Complete).AllocatedOnDrive("C:"));
    }

    [Fact]
    public void Comparison_requires_two_valid_timestamped_readings()
    {
        var earlier = Reading(10, 10, ShadowProbeStatus.Complete, Area("C:", 1024));
        var later = Reading(11, 20, ShadowProbeStatus.Complete, Area("C:", 4096));
        ShadowStorageDifference diff = ShadowStorageEvaluator.Compare(earlier, later, "C:");

        Assert.Equal(3072L, diff.AllocatedDeltaBytes);
        Assert.Contains("not a direct subtraction", diff.Interpretation);
        Assert.Throws<ArgumentException>(() =>
            ShadowStorageEvaluator.Compare(later, earlier, "C:"));
    }

    [Fact]
    public void Noncomparable_measurements_do_not_fabricate_a_delta()
    {
        var earlier = Reading(1, 1, ShadowProbeStatus.Complete, Area("C:", 1000));
        var later = Reading(2, 2, ShadowProbeStatus.Partial, Area("C:", 1200));
        var diff = ShadowStorageEvaluator.Compare(earlier, later, "C:");
        Assert.Null(diff.AllocatedDeltaBytes);
        Assert.Null(diff.LaterAllocatedBytes);
        Assert.Contains("Not comparable", diff.Interpretation);
    }

    [Fact]
    public void Readings_roundtrip_through_SQLite_without_changing_existing_snapshots()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.DatabasePath);
        var baseline = new DiskSnapshot(0, w.DirectoryPath,
            Path.GetPathRoot(w.DirectoryPath)!, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1), 100_000,
            5_000, 5_000, [], []);
        long snapshotId = store.Save(baseline);

        var reading = Reading(0, 5, ShadowProbeStatus.Complete,
            new ShadowStorageArea("source-ref", "diff-ref", "C:", "D:", 500, 1000, 2500));
        ShadowStorageMeasurement saved = store.SaveShadowStorageMeasurement(reading);
        ShadowStorageMeasurement restored = store.LoadShadowStorageMeasurement(saved.Id);

        Assert.True(saved.Id > 0);
        Assert.Equal(saved.Id, restored.Id);
        Assert.Equal(reading.ObservedUtc, restored.ObservedUtc);
        Assert.Equal(reading.Status, restored.Status);
        Assert.Equal(Assert.Single(reading.Areas), Assert.Single(restored.Areas));
        Assert.Equal(snapshotId, Assert.Single(store.List()).Id);
        Assert.Empty(store.Load(snapshotId).Files);
        Assert.Single(store.ListShadowStorageMeasurements());
    }

    [Fact]
    public void Invalid_measurement_rolls_back_and_preserves_previous_readings()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.DatabasePath);
        var valid = store.SaveShadowStorageMeasurement(
            Reading(0, 1, ShadowProbeStatus.Complete, Area("C:", 128)));
        var invalid = Reading(0, 2, ShadowProbeStatus.Complete,
            Area("C:", 100), Area("C:", -1));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.SaveShadowStorageMeasurement(invalid));
        Assert.Equal(valid.Id, Assert.Single(store.ListShadowStorageMeasurements()).Id);
        Assert.Single(store.LoadShadowStorageMeasurement(valid.Id).Areas);
    }

    [Fact]
    public void Invalid_state_and_missing_measurements_are_rejected()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.DatabasePath);
        Assert.Throws<ArgumentException>(() =>
            store.SaveShadowStorageMeasurement(Reading(0, 0,
                ShadowProbeStatus.Unavailable, Area("C:", 100))));
        Assert.Throws<KeyNotFoundException>(() => store.LoadShadowStorageMeasurement(999));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ListShadowStorageMeasurements(0));
    }
}
