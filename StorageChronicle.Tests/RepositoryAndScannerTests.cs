using StorageChronicle.Core;

namespace StorageChronicle.Tests;

public sealed class RepositoryAndScannerTests
{
    private sealed class TestDirectory : IDisposable
    {
        public string PathValue { get; } = Path.Combine(Path.GetTempPath(),
            "StorageChronicleTest-" + Guid.NewGuid().ToString("N"));

        public TestDirectory() => Directory.CreateDirectory(PathValue);

        public void Dispose()
        {
            if (Directory.Exists(PathValue))
                Directory.Delete(PathValue, recursive: true);
        }
    }

    [Fact]
    public void SQLite_roundtrip_preserves_all_metadata_and_issues()
    {
        using var temp = new TestDirectory();
        var file = new FileRecord(Path.Combine(temp.PathValue, "a.txt"),
            132, 4096, "ABCD:0012", 2, DateTimeOffset.UtcNow);
        var original = new DiskSnapshot(0, temp.PathValue,
            Path.GetPathRoot(temp.PathValue)!, DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow, 99_999, 40_000, 39_999,
            [file],
            [new ScanIssue("somepath", "REPARSE_EXCLUDED",
                "skipped", IssueSeverity.Info)]);
        var store = new SqliteSnapshotRepository(
            Path.Combine(temp.PathValue, "db", "snapshots.db"));

        long id = store.Save(original);
        var restored = store.Load(id);
        var summary = Assert.Single(store.List());

        Assert.True(id > 0);
        Assert.Equal(original.RootPath, restored.RootPath);
        Assert.Equal(original.VolumeCapacityBytes, restored.VolumeCapacityBytes);
        Assert.Equal(original.FreeBytesBefore, restored.FreeBytesBefore);
        Assert.Equal(original.FreeBytesAfter, restored.FreeBytesAfter);
        Assert.Equal(file, Assert.Single(restored.Files));
        Assert.Equal(original.Issues[0], Assert.Single(restored.Issues));
        Assert.Equal(id, summary.Id);
        Assert.Equal(1, summary.FileCount);
        Assert.Equal(0, summary.ErrorCount);
    }

    [Fact]
    public void Scanner_observes_files_without_modifying_them()
    {
        using var temp = new TestDirectory();
        Directory.CreateDirectory(Path.Combine(temp.PathValue, "sub"));
        string first = Path.Combine(temp.PathValue, "one.txt");
        string second = Path.Combine(temp.PathValue, "sub", "two.bin");
        File.WriteAllText(first, "unchanged");
        File.WriteAllBytes(second, new byte[123]);
        var before = File.ReadAllBytes(first);

        DiskSnapshot snapshot = new SnapshotScanner().Capture(temp.PathValue);

        Assert.Equal(2, snapshot.Files.Count);
        Assert.Contains(snapshot.Files, x => x.Path == first && x.LogicalBytes == 9);
        Assert.Contains(snapshot.Files, x => x.Path == second && x.LogicalBytes == 123);
        Assert.Equal(before, File.ReadAllBytes(first));
        Assert.True(snapshot.FinishedUtc >= snapshot.StartedUtc);
    }

    [Fact]
    public void Cancelled_scan_does_not_return_incomplete_snapshot()
    {
        using var temp = new TestDirectory();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new SnapshotScanner().Capture(temp.PathValue, cts.Token));
    }

    [Fact]
    public void Missing_directory_is_rejected()
    {
        string absent = Path.Combine(Path.GetTempPath(),
            "absent-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<DirectoryNotFoundException>(
            () => new SnapshotScanner().Capture(absent));
    }

    [Fact]
    public void Nonexistent_snapshot_cannot_be_loaded()
    {
        using var temp = new TestDirectory();
        var store = new SqliteSnapshotRepository(
            Path.Combine(temp.PathValue, "test.sqlite"));

        Assert.Throws<KeyNotFoundException>(() => store.Load(12345));
    }
}
