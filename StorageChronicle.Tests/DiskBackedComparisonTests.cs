using StorageChronicle.Core;

namespace StorageChronicle.Tests;

public sealed class DiskBackedComparisonTests
{
    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "StorageChronicleComparison-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "comparisons.db");
        public Workspace() => Directory.CreateDirectory(Root);
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static FileRecord File(string root, string name, long logical,
        long? allocated, string? identity = null) =>
        new(Path.Combine(root, name), logical, allocated, identity, 1, DateTimeOffset.UnixEpoch);

    private static DiskSnapshot Snap(string root, long free, params FileRecord[] records) =>
        new(0, root, Path.GetPathRoot(root)!,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1),
            1_000_000, free, free, records, []);

    [Fact]
    public void Disk_backed_matches_in_memory_for_varied_file_changes()
    {
        using var workspace = new Workspace();
        var store = new SqliteSnapshotRepository(workspace.Database);
        var before = Snap(workspace.Root, 900_000,
            File(workspace.Root, "a", 200, 256, "A"),
            File(workspace.Root, "b", 100, 128, "B"),
            File(workspace.Root, "removed", 400, 512, "C"),
            File(workspace.Root, "old-name", 100, 128, "MOVE"),
            File(workspace.Root, "link-a", 300, 512, "LINK"),
            File(workspace.Root, "link-b", 300, 512, "LINK"),
            File(workspace.Root, "unknown", 12, null, "UNKNOWN"));
        var after = Snap(workspace.Root, 899_000,
            File(workspace.Root, "a", 300, 384, "A"),
            File(workspace.Root, "b", 80, 64, "B"),
            File(workspace.Root, "new", 100, 128, "NEW"),
            File(workspace.Root, "new-name", 100, 128, "MOVE"),
            File(workspace.Root, "link-a", 350, 1024, "LINK"),
            File(workspace.Root, "link-b", 350, 1024, "LINK"),
            File(workspace.Root, "link-c", 350, 1024, "LINK"),
            File(workspace.Root, "unknown", 12, null, "UNKNOWN"));

        long beforeId = store.Save(before);
        long afterId = store.Save(after);
        var expected = SnapshotComparer.Compare(store.Load(beforeId), store.Load(afterId));
        var actual = store.CompareAndStore(beforeId, afterId);
        var page = store.ReadComparisonPage(actual.Id, 0, 100);

        Assert.Equal(expected.Changes.Count, actual.ChangeCount);
        Assert.Equal(expected.KnownAllocatedDeltaBytes, actual.KnownAllocatedDeltaBytes);
        Assert.Equal(expected.UnknownAllocationChanges, actual.UnknownAllocationChanges);
        Assert.Equal(expected.VolumeUsedDeltaBytes, actual.VolumeUsedDeltaBytes);
        Assert.Equal(expected.UnexplainedDeltaBytes, actual.UnexplainedDeltaBytes);
        Assert.Equal(expected.Caveats, actual.Caveats);
        Assert.Equal(expected.Changes.Count, page.Changes.Count);
        Assert.Equal(
            expected.Changes.Select(x => (x.Kind, x.PrimaryPath, x.AllocatedDeltaBytes))
                .OrderBy(x => x.PrimaryPath).ToArray(),
            page.Changes.Select(x => (x.Kind, x.PrimaryPath, x.AllocatedDeltaBytes))
                .OrderBy(x => x.PrimaryPath).ToArray());
        Assert.Equal(actual, store.LoadComparison(actual.Id));
    }

    [Fact]
    public void Disk_backed_pages_return_only_requested_changes_in_rank_order()
    {
        using var workspace = new Workspace();
        var store = new SqliteSnapshotRepository(workspace.Database);
        long beforeId = store.Save(Snap(workspace.Root, 800_000));
        var records = Enumerable.Range(1, 241)
            .Select(n => File(workspace.Root, $"entry-{n:000}", n, n, $"ID-{n:000}"))
            .ToArray();
        long afterId = store.Save(Snap(workspace.Root, 700_000, records));
        var report = store.CompareAndStore(beforeId, afterId);
        var first = store.ReadComparisonPage(report.Id, 0, 100);
        var second = store.ReadComparisonPage(report.Id, 100, 100);
        var third = store.ReadComparisonPage(report.Id, 200, 100);
        var end = store.ReadComparisonPage(report.Id, 300, 100);

        Assert.Equal(241, report.ChangeCount);
        Assert.Equal(100, first.Changes.Count);
        Assert.Equal(100, second.Changes.Count);
        Assert.Equal(41, third.Changes.Count);
        Assert.Empty(end.Changes);
        Assert.Equal(241L, first.Changes[0].AllocatedDeltaBytes);
        Assert.Equal(142L, second.Changes[0].AllocatedDeltaBytes);
        Assert.Equal(42L, third.Changes[0].AllocatedDeltaBytes);
        Assert.Equal(241, first.Changes.Concat(second.Changes).Concat(third.Changes)
            .Select(c => c.PrimaryPath).Distinct().Count());
    }

    [Fact]
    public void Repeated_comparison_returns_same_persisted_id()
    {
        using var workspace = new Workspace();
        var store = new SqliteSnapshotRepository(workspace.Database);
        long beforeId = store.Save(Snap(workspace.Root, 9000));
        long afterId = store.Save(Snap(workspace.Root, 8000,
            File(workspace.Root, "x", 50, 64)));
        var first = store.CompareAndStore(beforeId, afterId);
        var second = store.CompareAndStore(beforeId, afterId);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Incomplete_comparison_rolls_back_and_does_not_discard_snapshots()
    {
        using var workspace = new Workspace();
        var store = new SqliteSnapshotRepository(workspace.Database);
        long beforeId = store.Save(Snap(workspace.Root, 100_000));
        var records = Enumerable.Range(0, 600)
            .Select(n => File(workspace.Root, $"file-{n:00000}", 10, 16, $"I{n:00000}"))
            .ToArray();
        long afterId = store.Save(Snap(workspace.Root, 90_000, records));
        using var cancel = new CancellationTokenSource();
        var progress = new InlineProgress<ComparisonProgress>(p =>
        {
            if (p.ProcessedGroups >= 250)
                cancel.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            store.CompareAndStore(beforeId, afterId, cancel.Token, progress));
        Assert.Equal(2, store.List().Count);
        Assert.Throws<KeyNotFoundException>(() => store.LoadComparison(1));

        var successful = store.CompareAndStore(beforeId, afterId);
        Assert.Equal(600, successful.ChangeCount);
    }

    [Fact]
    public void Comparisons_reject_different_roots_without_persisting_results()
    {
        using var workspace = new Workspace();
        var store = new SqliteSnapshotRepository(workspace.Database);
        long a = store.Save(Snap(workspace.Root, 900));
        string other = Path.Combine(workspace.Root, "different");
        long b = store.Save(Snap(other, 900));
        Assert.Throws<ArgumentException>(() => store.CompareAndStore(a, b));
        Assert.Throws<KeyNotFoundException>(() => store.LoadComparison(1));
    }

    [Fact]
    public void Null_allocation_remains_unknown_in_paged_change_results()
    {
        using var workspace = new Workspace();
        var store = new SqliteSnapshotRepository(workspace.Database);
        long a = store.Save(Snap(workspace.Root, 900));
        long b = store.Save(Snap(workspace.Root, 500,
            File(workspace.Root, "unknown", 400, null)));
        var report = store.CompareAndStore(a, b);
        var change = Assert.Single(store.ReadComparisonPage(report.Id, 0, 10).Changes);
        Assert.Null(change.AllocatedDeltaBytes);
        Assert.Equal(1, report.UnknownAllocationChanges);
        Assert.Equal(400L, report.UnexplainedDeltaBytes);
    }

    [Fact]
    public void Invalid_page_arguments_are_rejected()
    {
        using var workspace = new Workspace();
        var store = new SqliteSnapshotRepository(workspace.Database);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ReadComparisonPage(123, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ReadComparisonPage(123, 0, 501));
    }

    [Fact]
    public void Single_file_identity_across_two_hardlinks_counts_allocation_once()
    {
        using var workspace = new Workspace();
        var store = new SqliteSnapshotRepository(workspace.Database);
        long a = store.Save(Snap(workspace.Root, 9000,
            File(workspace.Root, "one", 200, 256, "ONE"),
            File(workspace.Root, "two", 200, 256, "ONE")));
        long b = store.Save(Snap(workspace.Root, 8000,
            File(workspace.Root, "one", 400, 512, "ONE"),
            File(workspace.Root, "two", 400, 512, "ONE")));
        var report = store.CompareAndStore(a, b);
        Assert.Equal(256L, report.KnownAllocatedDeltaBytes);
        Assert.Single(store.ReadComparisonPage(report.Id, 0, 100).Changes);
    }
}
