using StorageChronicle.Core;

namespace StorageChronicle.Tests;

public class SnapshotComparerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "StorageChronicleSynthetic");

    private static FileRecord File(string name, long logical, long? allocated, string? identity) =>
        new(Path.Combine(Root, name), logical, allocated, identity, 1, DateTimeOffset.UnixEpoch);

    private static DiskSnapshot Snap(long id, long? free, params FileRecord[] files) =>
        new(id, Root, Path.GetPathRoot(Root)!, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1), 100_000, free, free, files, []);


    [Fact]
    public void Whole_volume_detection_uses_the_normalized_root()
    {
        string volumeRoot = Path.GetPathRoot(Root)!;
        var whole = Snap(1, 5000) with { RootPath = volumeRoot, VolumeRoot = volumeRoot };
        var scoped = Snap(2, 5000);

        Assert.True(whole.IsWholeVolume);
        Assert.False(scoped.IsWholeVolume);
    }

    [Fact]
    public void Detects_add_delete_growth_and_shrink()
    {
        var baseline = Snap(1, 8_000,
            File("grown", 100, 100, "A"),
            File("shrunk", 300, 300, "B"),
            File("removed", 500, 500, "C"));
        var current = Snap(2, 7_550,
            File("grown", 400, 400, "A"),
            File("shrunk", 200, 200, "B"),
            File("added", 750, 750, "D"));

        var result = SnapshotComparer.Compare(baseline, current);

        Assert.Equal(4, result.Changes.Count);
        Assert.Contains(result.Changes, x => x.Kind == ChangeKind.Grew);
        Assert.Contains(result.Changes, x => x.Kind == ChangeKind.Shrank);
        Assert.Contains(result.Changes, x => x.Kind == ChangeKind.Added);
        Assert.Contains(result.Changes, x => x.Kind == ChangeKind.Deleted);
        Assert.Equal(450L, result.KnownAllocatedDeltaBytes);
        Assert.Equal(450L, result.VolumeUsedDeltaBytes);
        Assert.Equal(0L, result.UnexplainedDeltaBytes);
    }

    [Fact]
    public void Stable_identity_recognizes_move_without_space_change()
    {
        var before = Snap(1, 5000, File("before.txt", 120, 128, "SAME"));
        var after = Snap(2, 5000, File("after.txt", 120, 128, "SAME"));

        var result = SnapshotComparer.Compare(before, after);

        var change = Assert.Single(result.Changes);
        Assert.Equal(ChangeKind.Moved, change.Kind);
        Assert.Equal(0L, change.AllocatedDeltaBytes);
        Assert.Equal(0L, result.KnownAllocatedDeltaBytes);
    }

    [Fact]
    public void Hardlinks_are_not_counted_twice()
    {
        var before = Snap(1, 5000,
            File("a.txt", 100, 128, "ONE"),
            File("b.txt", 100, 128, "ONE"));
        var after = Snap(2, 4872,
            File("a.txt", 230, 256, "ONE"),
            File("b.txt", 230, 256, "ONE"));

        var result = SnapshotComparer.Compare(before, after);

        Assert.Equal(128L, result.KnownAllocatedDeltaBytes);
        Assert.Single(result.Changes);
        Assert.Equal(ChangeKind.Grew, result.Changes[0].Kind);
    }

    [Fact]
    public void Additional_hardlink_does_not_count_as_new_disk_allocation()
    {
        var before = Snap(1, 5000, File("a.txt", 100, 128, "ONE"));
        var after = Snap(2, 5000,
            File("a.txt", 100, 128, "ONE"),
            File("b.txt", 100, 128, "ONE"));

        var result = SnapshotComparer.Compare(before, after);

        Assert.Equal(0L, result.KnownAllocatedDeltaBytes);
        Assert.Equal(ChangeKind.LinksChanged, Assert.Single(result.Changes).Kind);
    }

    [Fact]
    public void Same_path_replaced_with_different_identity_is_not_called_unchanged()
    {
        var before = Snap(1, 5000, File("same.txt", 100, 128, "OLD"));
        var after = Snap(2, 4800, File("same.txt", 300, 328, "NEW"));

        var result = SnapshotComparer.Compare(before, after);

        Assert.Equal(2, result.Changes.Count);
        Assert.Contains(result.Changes, x => x.Kind == ChangeKind.Added);
        Assert.Contains(result.Changes, x => x.Kind == ChangeKind.Deleted);
        Assert.Equal(200L, result.KnownAllocatedDeltaBytes);
    }

    [Fact]
    public void Unknown_allocation_is_not_mistaken_for_zero()
    {
        var before = Snap(1, 5000);
        var after = Snap(2, 4000, File("unknown", 1000, null, "NEW"));

        var result = SnapshotComparer.Compare(before, after);

        Assert.Null(Assert.Single(result.Changes).AllocatedDeltaBytes);
        Assert.Equal(1, result.UnknownAllocationChanges);
        Assert.Equal(0L, result.KnownAllocatedDeltaBytes);
        Assert.Contains(result.Caveats, x => x.Contains("unknown allocated size"));
    }

    [Fact]
    public void Reports_residual_as_provisional_not_as_identified_system_files()
    {
        var before = Snap(1, 5000);
        var after = Snap(2, 4000, File("found", 200, 256, "F"));

        var result = SnapshotComparer.Compare(before, after);

        Assert.Equal(1000L, result.VolumeUsedDeltaBytes);
        Assert.Equal(256L, result.KnownAllocatedDeltaBytes);
        Assert.Equal(744L, result.UnexplainedDeltaBytes);
        Assert.True(result.IsReconciliationProvisional);
    }

    [Fact]
    public void Path_only_fallback_matches_unchanged_name()
    {
        var before = Snap(1, 5000, File("same", 200, 256, null));
        var after = Snap(2, 5000, File("same", 200, 256, null));

        Assert.Empty(SnapshotComparer.Compare(before, after).Changes);
    }

    [Fact]
    public void Incomplete_snapshot_is_explicitly_flagged()
    {
        var baseSnapshot = Snap(1, 1000);
        var current = Snap(2, 900) with
        {
            Issues = [new ScanIssue(Root, "DIRECTORY_ENUMERATION_FAILED",
                "Access denied", IssueSeverity.Error)]
        };
        var result = SnapshotComparer.Compare(baseSnapshot, current);

        Assert.Contains(result.Caveats, x => x.Contains("inaccessible"));
        Assert.False(current.IsComplete);
    }

    [Fact]
    public void Rejects_comparisons_of_different_roots()
    {
        var before = Snap(1, 1000);
        var after = Snap(2, 1000) with { RootPath = Path.Combine(Root, "other") };

        Assert.Throws<ArgumentException>(() => SnapshotComparer.Compare(before, after));
    }
}
