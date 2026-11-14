using StorageChronicle.Core;

namespace StorageChronicle.Tests;

public sealed class StreamingSnapshotTests
{
    private sealed class TempWorkspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "StorageChronicleStream-" + Guid.NewGuid().ToString("N"));
        public string Input => Path.Combine(Root, "input");
        public string Database => Path.Combine(Root, "metadata", "snapshots.db");

        public TempWorkspace()
        {
            Directory.CreateDirectory(Input);
            Directory.CreateDirectory(Path.GetDirectoryName(Database)!);
        }

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

    [Fact]
    public void Streaming_capture_persists_files_and_compares_two_snapshots()
    {
        using var temp = new TempWorkspace();
        string a = Path.Combine(temp.Input, "a.bin");
        string b = Path.Combine(temp.Input, "b.bin");
        File.WriteAllBytes(a, new byte[900]);
        var store = new SqliteSnapshotRepository(temp.Database);
        SavedScanResult first = store.CaptureAndSave(temp.Input);

        File.WriteAllBytes(a, new byte[9000]);
        File.WriteAllBytes(b, new byte[3000]);
        SavedScanResult second = store.CaptureAndSave(temp.Input);
        var before = store.Load(first.Id);
        var after = store.Load(second.Id);
        var report = SnapshotComparer.Compare(before, after);

        Assert.Equal(1, first.Summary.FileCount);
        Assert.Equal(2, second.Summary.FileCount);
        Assert.Equal(0, first.Summary.ErrorCount);
        Assert.Equal(0, second.Summary.ErrorCount);
        Assert.Single(before.Files);
        Assert.Equal(2, after.Files.Count);
        Assert.Contains(report.Changes, x => x.Kind == ChangeKind.Added);
        Assert.Contains(report.Changes, x => x.Kind == ChangeKind.Grew);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Cancel_during_streaming_capture_leaves_no_visible_snapshot()
    {
        using var temp = new TempWorkspace();
        for (int n = 0; n < 260; n++)
            File.WriteAllBytes(Path.Combine(temp.Input, $"{n:0000}.txt"), [1, 2, 3]);

        var store = new SqliteSnapshotRepository(temp.Database);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ScanProgress>(p =>
        {
            if (p.FileCount >= 250)
                cancellation.Cancel();
        });

        Assert.Throws<OperationCanceledException>(
            () => store.CaptureAndSave(temp.Input, cancellation.Token, progress));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Cancel_does_not_remove_previously_committed_history()
    {
        using var temp = new TempWorkspace();
        var store = new SqliteSnapshotRepository(temp.Database);
        var earlier = store.CaptureAndSave(temp.Input);
        for (int n = 0; n < 260; n++)
            File.WriteAllText(Path.Combine(temp.Input, $"{n:0000}.txt"), "test");

        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ScanProgress>(p =>
        {
            if (p.FileCount >= 250)
                cancellation.Cancel();
        });

        Assert.Throws<OperationCanceledException>(() =>
            store.CaptureAndSave(temp.Input, cancellation.Token, progress));
        Assert.Equal(earlier.Id, Assert.Single(store.List()).Id);
        Assert.Equal(earlier.Summary.FileCount, store.Load(earlier.Id).Files.Count);
    }

    [Fact]
    public void Pre_cancelled_token_does_not_create_snapshot()
    {
        using var temp = new TempWorkspace();
        var store = new SqliteSnapshotRepository(temp.Database);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => store.CaptureAndSave(temp.Input, cancellation.Token));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Filesystem_scanner_propagates_callback_IO_exception_without_misclassifying_it()
    {
        using var temp = new TempWorkspace();
        File.WriteAllText(Path.Combine(temp.Input, "a.txt"), "hello");
        var notices = new List<ScanIssue>();

        var exception = Assert.Throws<IOException>(() =>
            new SnapshotScanner().Scan(temp.Input,
                _ => throw new IOException("synthetic disk-write failure"),
                notices.Add));

        Assert.Equal("synthetic disk-write failure", exception.Message);
        Assert.DoesNotContain(notices, n => n.Code == "FILE_READ_FAILED");
    }

    [Fact]
    public void Streaming_scanner_emits_every_file_and_returns_counts()
    {
        using var temp = new TempWorkspace();
        for (int n = 0; n < 12; n++)
            File.WriteAllText(Path.Combine(temp.Input, $"{n}.txt"), "abc");
        var received = new List<FileRecord>();
        var issues = new List<ScanIssue>();

        DiskScanSummary summary = new SnapshotScanner().Scan(
            temp.Input, received.Add, issues.Add);

        Assert.Equal(12, summary.FileCount);
        Assert.Equal(12, received.Count);
        Assert.Equal(issues.Count, summary.NoticeCount);
        Assert.Equal(summary.ErrorCount, issues.Count(i => i.Severity == IssueSeverity.Error));
        Assert.All(received, f => Assert.Equal(3, f.LogicalBytes));
    }

    [Fact]
    public void Database_inside_scanned_directory_is_flagged()
    {
        using var temp = new TempWorkspace();
        string insideDb = Path.Combine(temp.Input, "snapshots.db");
        var store = new SqliteSnapshotRepository(insideDb);

        SavedScanResult result = store.CaptureAndSave(temp.Input);
        DiskSnapshot loaded = store.Load(result.Id);

        Assert.Contains(loaded.Issues, x => x.Code == "SNAPSHOT_DATABASE_IN_SCAN");
        Assert.Equal(loaded.Issues.Count, result.Summary.NoticeCount);
    }

    [Fact]
    public void Missing_scan_root_does_not_create_transaction_or_snapshot()
    {
        using var temp = new TempWorkspace();
        var store = new SqliteSnapshotRepository(temp.Database);
        string absent = Path.Combine(temp.Root, "absent");

        Assert.Throws<DirectoryNotFoundException>(() => store.CaptureAndSave(absent));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Normalize_volume_root_is_compatible_with_old_bare_drive_format()
    {
        if (!OperatingSystem.IsWindows())
            return;
        Assert.True(PathComparer.SameVolume(@"C:", @"C:\"));
        Assert.False(PathComparer.SameVolume(@"C:", @"D:\"));
    }
}
