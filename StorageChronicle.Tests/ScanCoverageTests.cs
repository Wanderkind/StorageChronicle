using StorageChronicle.Core;

namespace StorageChronicle.Tests;

public sealed class ScanCoverageTests
{
    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "StorageChronicleCoverage-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "coverage.sqlite");

        public Workspace() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }

    private static DiskSnapshot Snapshot(string root,
        IReadOnlyList<FileRecord>? files = null,
        IReadOnlyList<ScanIssue>? issues = null) =>
        new(0, root, Path.GetPathRoot(root)!, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(5), 100_000, 60_000, 59_000,
            files ?? [], issues ?? []);

    private static FileRecord File(string root, string name, long? allocation,
        string? identity) =>
        new(Path.Combine(root, name), 64, allocation, identity, 1,
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void Coverage_reports_observed_metadata_and_categorized_issues()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        long id = store.Save(Snapshot(w.Root,
            [File(w.Root, "readable.txt", 4096, "F1"),
             File(w.Root, "unknown-allocation.txt", null, "F2"),
             File(w.Root, "unknown-identity.txt", 4096, null)],
            [new ScanIssue("private", "DIRECTORY_ENUMERATION_FAILED",
                 "Access denied", IssueSeverity.Error),
             new ScanIssue("private2", "DIRECTORY_ENUMERATION_FAILED",
                 "Access denied", IssueSeverity.Error),
             new ScanIssue("locked", "IDENTITY_UNAVAILABLE",
                 "Access denied", IssueSeverity.Warning),
             new ScanIssue("link", "REPARSE_EXCLUDED",
                 "Junction skipped", IssueSeverity.Info)]));

        var coverage = store.LoadScanCoverage(id);

        Assert.Equal(id, coverage.SnapshotId);
        Assert.Equal(3, coverage.FileCount);
        Assert.Equal(2, coverage.ErrorCount);
        Assert.Equal(1, coverage.WarningCount);
        Assert.Equal(1, coverage.InfoCount);
        Assert.Equal(4, coverage.NoticeCount);
        Assert.Equal(1, coverage.MissingAllocationCount);
        Assert.Equal(1, coverage.MissingIdentityCount);
        Assert.True(coverage.HasRecordedReadFailures);
        Assert.True(coverage.HasMissingMetadata);
        Assert.False(coverage.IsWholeVolume);
        Assert.Equal(60_000L, coverage.FreeBytesBefore);
        Assert.Equal(59_000L, coverage.FreeBytesAfter);
        Assert.Contains("unknown", coverage.CoverageInterpretation);

        Assert.Collection(coverage.Categories,
            c =>
            {
                Assert.Equal(IssueSeverity.Error, c.Severity);
                Assert.Equal("DIRECTORY_ENUMERATION_FAILED", c.Code);
                Assert.Equal(2, c.Count);
            },
            c =>
            {
                Assert.Equal(IssueSeverity.Warning, c.Severity);
                Assert.Equal("IDENTITY_UNAVAILABLE", c.Code);
                Assert.Equal(1, c.Count);
            },
            c =>
            {
                Assert.Equal(IssueSeverity.Info, c.Severity);
                Assert.Equal("REPARSE_EXCLUDED", c.Code);
                Assert.Equal(1, c.Count);
            });
    }

    [Fact]
    public void Issue_pagination_is_stable_and_filterable()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        var issues = Enumerable.Range(0, 215).Select(i =>
            new ScanIssue($"{w.Root}/entry-{i:000}", "FILE_READ_FAILED",
                "synthetic failure", i % 2 == 0
                    ? IssueSeverity.Error : IssueSeverity.Warning)).ToArray();
        long id = store.Save(Snapshot(w.Root, issues: issues));

        var first = store.ReadScanIssuePage(id, 0);
        var second = store.ReadScanIssuePage(id, 100);
        var last = store.ReadScanIssuePage(id, 200);
        var errors = store.ReadScanIssuePage(id, 0, 100, IssueSeverity.Error);
        var warnings = store.ReadScanIssuePage(id, 100, 100, IssueSeverity.Warning);
        var empty = store.ReadScanIssuePage(id, 215);

        Assert.Equal(100, first.Issues.Count);
        Assert.Equal(100, second.Issues.Count);
        Assert.Equal(15, last.Issues.Count);
        Assert.Empty(empty.Issues);
        Assert.Equal(100, errors.Issues.Count);
        Assert.Equal(7, warnings.Issues.Count);
        Assert.All(errors.Issues,
            x => Assert.Equal(IssueSeverity.Error, x.Severity));
        Assert.All(warnings.Issues,
            x => Assert.Equal(IssueSeverity.Warning, x.Severity));
        Assert.Equal(215, first.Issues.Concat(second.Issues).Concat(last.Issues)
            .Select(x => x.Path).Distinct().Count());
        Assert.Equal(215, store.LoadScanCoverage(id).NoticeCount);
    }

    [Fact]
    public void Empty_snapshot_does_not_claim_complete_volume_accounting()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        long id = store.Save(Snapshot(w.Root));

        SnapshotCoverage coverage = store.LoadScanCoverage(id);

        Assert.Equal(0, coverage.FileCount);
        Assert.Equal(0, coverage.NoticeCount);
        Assert.False(coverage.HasRecordedReadFailures);
        Assert.False(coverage.HasMissingMetadata);
        Assert.Empty(coverage.Categories);
        Assert.Contains("does not prove", coverage.CoverageInterpretation);
        Assert.Empty(store.ReadScanIssuePage(id, 0).Issues);
    }

    [Fact]
    public void Existing_snapshot_and_comparison_tables_are_not_changed_by_diagnostics()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        long a = store.Save(Snapshot(w.Root));
        long b = store.Save(Snapshot(w.Root,
            [File(w.Root, "new", 8192, "F3")]));
        StoredComparison comparison = store.CompareAndStore(a, b);
        var before = store.LoadComparison(comparison.Id);

        _ = store.LoadScanCoverage(a);
        _ = store.LoadScanCoverage(b);
        _ = store.ReadScanIssuePage(a, 0);

        Assert.Equal(2, store.List().Count);
        Assert.Equal(before.ChangeCount, store.LoadComparison(comparison.Id).ChangeCount);
        Assert.Equal(1, store.ReadComparisonPage(comparison.Id, 0).Changes.Count);
    }

    [Fact]
    public void Nonexistent_snapshot_reports_a_specific_error()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);

        Assert.Throws<KeyNotFoundException>(() => store.LoadScanCoverage(999));
        Assert.Throws<KeyNotFoundException>(() => store.ReadScanIssuePage(999, 0));
    }

    [Fact]
    public void Issue_page_rejects_invalid_bounds_and_filter()
    {
        using var w = new Workspace();
        var store = new SqliteSnapshotRepository(w.Database);
        long id = store.Save(Snapshot(w.Root));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ReadScanIssuePage(id, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ReadScanIssuePage(id, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ReadScanIssuePage(id, 0, 501));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ReadScanIssuePage(id, 0, 20, (IssueSeverity)999));
    }
}
