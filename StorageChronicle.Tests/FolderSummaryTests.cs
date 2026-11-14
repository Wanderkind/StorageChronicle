using StorageChronicle.Core;

namespace StorageChronicle.Tests;

public sealed class FolderSummaryTests
{
    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "StorageChronicleFolders-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "comparison.db");

        public Workspace() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private static FileRecord File(string root, string relativePath,
        long logical, long? allocated, string identity) =>
        new(Path.Combine(root, relativePath), logical, allocated, identity, 1,
            DateTimeOffset.UnixEpoch);

    private static DiskSnapshot Snap(string root, params FileRecord[] files) =>
        new(0, root, Path.GetPathRoot(root)!,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1),
            10_000_000, 2_000_000, 2_000_000, files, []);

    private static FolderChange Find(FolderPage page, string folder) =>
        Assert.Single(page.Folders, x => PathComparer.SamePath(x.FolderPath, folder));

    [Fact]
    public void Folder_totals_reconcile_with_comparison_and_moves_change_folder_ownership()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        long baseline = store.Save(Snap(w.Root,
            File(w.Root, Path.Combine("Alpha", "grown.txt"), 100, 128, "GROW"),
            File(w.Root, Path.Combine("Alpha", "deleted.txt"), 200, 256, "DELETE"),
            File(w.Root, Path.Combine("Gamma", "moved.txt"), 500, 512, "MOVE")));
        long current = store.Save(Snap(w.Root,
            File(w.Root, Path.Combine("Alpha", "grown.txt"), 200, 256, "GROW"),
            File(w.Root, Path.Combine("Beta", "new.txt"), 400, 512, "NEW"),
            File(w.Root, Path.Combine("Beta", "moved.txt"), 500, 512, "MOVE")));
        StoredComparison comparison = store.CompareAndStore(baseline, current);
        StoredFolderSummary summary = store.BuildFolderSummary(comparison.Id);
        FolderPage page = store.ReadFolderPage(comparison.Id, 0, 100);

        Assert.Equal(3, summary.FolderCount);
        Assert.Equal(comparison.KnownAllocatedDeltaBytes, summary.RootKnownNetDeltaBytes);
        Assert.Equal(384L, summary.RootKnownNetDeltaBytes);
        Assert.Equal(-128L, Find(page, Path.Combine(w.Root, "Alpha")).KnownNetDeltaBytes);
        Assert.Equal(1024L, Find(page, Path.Combine(w.Root, "Beta")).KnownNetDeltaBytes);
        Assert.Equal(-512L, Find(page, Path.Combine(w.Root, "Gamma")).KnownNetDeltaBytes);
        Assert.Equal(0, summary.RootUnknownAllocationGroups);
        // These are cumulative directory inventory deltas; the move does NOT
        // consume 512 additional bytes on the volume.
        Assert.Equal(384L, page.Folders.Sum(x => x.KnownNetDeltaBytes));
    }

    [Fact]
    public void Unknown_allocation_is_never_treated_as_known_growth()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        long baseline = store.Save(Snap(w.Root,
            File(w.Root, Path.Combine("Alpha", "unknown.bin"), 150, null, "UNK")));
        long current = store.Save(Snap(w.Root,
            File(w.Root, Path.Combine("Beta", "unknown.bin"), 150, 8192, "UNK")));
        var comparison = store.CompareAndStore(baseline, current);
        var summary = store.BuildFolderSummary(comparison.Id);
        var page = store.ReadFolderPage(comparison.Id, 0, 100);

        Assert.Equal(1, comparison.UnknownAllocationChanges);
        Assert.Equal(1, summary.RootUnknownAllocationGroups);
        Assert.Equal(0, summary.RootKnownNetDeltaBytes);
        Assert.Equal(1, Find(page, Path.Combine(w.Root, "Alpha")).UnknownAllocationGroups);
        Assert.Equal(1, Find(page, Path.Combine(w.Root, "Beta")).UnknownAllocationGroups);
        Assert.All(page.Folders, x => Assert.Equal(0, x.KnownNetDeltaBytes));
    }

    [Fact]
    public void Folder_pages_are_bounded_and_support_growth_and_reduction_sorting()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        long baseline = store.Save(Snap(w.Root));
        FileRecord[] currentFiles = Enumerable.Range(1, 245)
            .Select(i => File(w.Root, Path.Combine($"folder-{i:000}", "file.bin"),
                i, i, $"ID-{i:000}")).ToArray();
        long current = store.Save(Snap(w.Root, currentFiles));
        var comparison = store.CompareAndStore(baseline, current);
        var summary = store.BuildFolderSummary(comparison.Id);

        var page1 = store.ReadFolderPage(comparison.Id, 0, 100);
        var page2 = store.ReadFolderPage(comparison.Id, 100, 100);
        var page3 = store.ReadFolderPage(comparison.Id, 200, 100);
        var freed = store.ReadFolderPage(comparison.Id, 0, 100, largestGrowthFirst: false);

        Assert.Equal(245, summary.FolderCount);
        Assert.Equal(100, page1.Folders.Count);
        Assert.Equal(100, page2.Folders.Count);
        Assert.Equal(45, page3.Folders.Count);
        Assert.Equal(245, page1.Folders.Concat(page2.Folders).Concat(page3.Folders)
            .Select(x => x.FolderPath).Distinct().Count());
        Assert.Equal(245L, page1.Folders[0].KnownNetDeltaBytes);
        Assert.Equal(1L, freed.Folders[0].KnownNetDeltaBytes);
        Assert.Equal(245L * 246L / 2, summary.RootKnownNetDeltaBytes);
    }

    [Fact]
    public void Empty_comparison_has_no_folder_rows_but_a_valid_cached_summary()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        long before = store.Save(Snap(w.Root));
        long after = store.Save(Snap(w.Root));
        var comparison = store.CompareAndStore(before, after);

        Assert.Throws<InvalidOperationException>(() => store.ReadFolderPage(comparison.Id, 0));
        var summary = store.BuildFolderSummary(comparison.Id);
        Assert.Equal(0, summary.FolderCount);
        Assert.Equal(0, summary.RootKnownNetDeltaBytes);
        Assert.Empty(store.ReadFolderPage(comparison.Id, 0).Folders);
        Assert.Equal(summary, store.BuildFolderSummary(comparison.Id));
    }

    [Fact]
    public void Folder_summary_cancellation_rolls_back_without_losing_comparison()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        long baseline = store.Save(Snap(w.Root));
        FileRecord[] currentFiles = Enumerable.Range(0, 510)
            .Select(i => File(w.Root, Path.Combine($"folder-{i:000}", "item.txt"),
                200, 256, $"I{i:000}")).ToArray();
        long current = store.Save(Snap(w.Root, currentFiles));
        var comparison = store.CompareAndStore(baseline, current);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FolderBuildProgress>(p =>
        {
            if (p.ProcessedChanges >= 250)
                cancellation.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            store.BuildFolderSummary(comparison.Id, cancellation.Token, progress));
        Assert.Equal(comparison.ChangeCount,
            store.LoadComparison(comparison.Id).ChangeCount);
        Assert.Throws<InvalidOperationException>(() =>
            store.ReadFolderPage(comparison.Id, 0));

        var summary = store.BuildFolderSummary(comparison.Id);
        Assert.Equal(510, summary.FolderCount);
    }

    [Fact]
    public void Hardlinked_file_has_one_canonical_folder_assignment()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        long baseline = store.Save(Snap(w.Root,
            File(w.Root, Path.Combine("Alpha", "a.txt"), 100, 128, "SHARED"),
            File(w.Root, Path.Combine("Beta", "b.txt"), 100, 128, "SHARED")));
        long current = store.Save(Snap(w.Root,
            File(w.Root, Path.Combine("Alpha", "a.txt"), 200, 256, "SHARED"),
            File(w.Root, Path.Combine("Beta", "b.txt"), 200, 256, "SHARED")));
        var comparison = store.CompareAndStore(baseline, current);
        var summary = store.BuildFolderSummary(comparison.Id);
        var page = store.ReadFolderPage(comparison.Id, 0);

        Assert.Equal(128L, summary.RootKnownNetDeltaBytes);
        Assert.Equal(128L, Find(page, Path.Combine(w.Root, "Alpha")).KnownNetDeltaBytes);
        Assert.DoesNotContain(page.Folders,
            x => PathComparer.SamePath(x.FolderPath, Path.Combine(w.Root, "Beta")));
    }

    [Fact]
    public void Folder_page_requires_valid_arguments_and_complete_build()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadFolderPage(999, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.ReadFolderPage(999, 0, 501));
        Assert.Throws<KeyNotFoundException>(() => store.BuildFolderSummary(999));
    }
}
