using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StorageChronicle.Core;

public sealed partial class SqliteSnapshotRepository
{
    /// <summary>
    /// Stream each observed file and notice directly to SQLite in one transaction.
    /// An exception or cancellation rolls everything back; the original snapshot
    /// history remains untouched. No complete file inventory is materialized.
    /// </summary>
    public SavedScanResult CaptureAndSave(
        string root,
        CancellationToken cancellationToken = default,
        IProgress<ScanProgress>? progress = null)
    {
        // Validate before opening a write transaction.
        SnapshotScanner.ScanRoot resolved = SnapshotScanner.ResolveRoot(root);
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using var header = connection.CreateCommand();
        header.Transaction = transaction;
        header.CommandText = """
            INSERT INTO snapshots
                (root_path, volume_root, started_utc, finished_utc,
                 volume_capacity_bytes, free_bytes_before, free_bytes_after)
            VALUES ($root, $volume, $start, $finish, NULL, NULL, NULL);
            SELECT last_insert_rowid();
            """;
        header.Parameters.AddWithValue("$root", resolved.RootPath);
        header.Parameters.AddWithValue("$volume", resolved.VolumeRoot);
        string placeholderTime = Stamp(DateTimeOffset.UtcNow);
        header.Parameters.AddWithValue("$start", placeholderTime);
        header.Parameters.AddWithValue("$finish", placeholderTime);
        long id = Convert.ToInt64(header.ExecuteScalar(), CultureInfo.InvariantCulture);

        using var insertFile = connection.CreateCommand();
        insertFile.Transaction = transaction;
        insertFile.CommandText = """
            INSERT INTO snapshot_files
                (snapshot_id, path, logical_bytes, allocated_bytes,
                 file_identity, hardlink_count, last_write_utc)
            VALUES ($id, $path, $logical, $allocated, $identity, $links, $modified);
            """;
        var fId = insertFile.Parameters.Add("$id", SqliteType.Integer);
        var fPath = insertFile.Parameters.Add("$path", SqliteType.Text);
        var fLogical = insertFile.Parameters.Add("$logical", SqliteType.Integer);
        var fAllocated = insertFile.Parameters.Add("$allocated", SqliteType.Integer);
        var fIdentity = insertFile.Parameters.Add("$identity", SqliteType.Text);
        var fLinks = insertFile.Parameters.Add("$links", SqliteType.Integer);
        var fModified = insertFile.Parameters.Add("$modified", SqliteType.Text);
        fId.Value = id;

        void WriteFile(FileRecord file)
        {
            fPath.Value = file.Path;
            fLogical.Value = file.LogicalBytes;
            fAllocated.Value = DbValue(file.AllocatedBytes);
            fIdentity.Value = (object?)file.FileIdentity ?? DBNull.Value;
            fLinks.Value = file.HardLinkCount is uint count ? (object)(long)count : DBNull.Value;
            fModified.Value = Stamp(file.LastWriteUtc);
            insertFile.ExecuteNonQuery();
        }

        using var insertIssue = connection.CreateCommand();
        insertIssue.Transaction = transaction;
        insertIssue.CommandText = """
            INSERT INTO snapshot_issues (snapshot_id, path, code, detail, severity)
            VALUES ($id, $path, $code, $detail, $severity);
            """;
        var iId = insertIssue.Parameters.Add("$id", SqliteType.Integer);
        var iPath = insertIssue.Parameters.Add("$path", SqliteType.Text);
        var iCode = insertIssue.Parameters.Add("$code", SqliteType.Text);
        var iDetail = insertIssue.Parameters.Add("$detail", SqliteType.Text);
        var iSeverity = insertIssue.Parameters.Add("$severity", SqliteType.Integer);
        iId.Value = id;

        var firstIssues = new List<ScanIssue>(100);
        void WriteIssue(ScanIssue issue)
        {
            iPath.Value = issue.Path;
            iCode.Value = issue.Code;
            iDetail.Value = issue.Detail;
            iSeverity.Value = (int)issue.Severity;
            insertIssue.ExecuteNonQuery();
            if (firstIssues.Count < 100)
                firstIssues.Add(issue);
        }

        DiskScanSummary summary = new SnapshotScanner().Scan(
            resolved.RootPath, WriteFile, WriteIssue, cancellationToken, progress);

        if (IsWithinRoot(DatabasePath, summary.RootPath))
        {
            WriteIssue(new(DatabasePath, "SNAPSHOT_DATABASE_IN_SCAN",
                "The metadata database is inside the scanned tree; its own changes may affect measurements. " +
                "Use STORAGECHRONICLE_DB on another volume when possible.",
                IssueSeverity.Warning));
            summary = summary with { NoticeCount = checked(summary.NoticeCount + 1) };
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var finalize = connection.CreateCommand();
        finalize.Transaction = transaction;
        finalize.CommandText = """
            UPDATE snapshots
            SET root_path=$root, volume_root=$volume,
                started_utc=$start, finished_utc=$finish,
                volume_capacity_bytes=$capacity,
                free_bytes_before=$before, free_bytes_after=$after
            WHERE id=$id;
            """;
        finalize.Parameters.AddWithValue("$id", id);
        finalize.Parameters.AddWithValue("$root", summary.RootPath);
        finalize.Parameters.AddWithValue("$volume", summary.VolumeRoot);
        finalize.Parameters.AddWithValue("$start", Stamp(summary.StartedUtc));
        finalize.Parameters.AddWithValue("$finish", Stamp(summary.FinishedUtc));
        finalize.Parameters.AddWithValue("$capacity", DbValue(summary.VolumeCapacityBytes));
        finalize.Parameters.AddWithValue("$before", DbValue(summary.FreeBytesBefore));
        finalize.Parameters.AddWithValue("$after", DbValue(summary.FreeBytesAfter));
        if (finalize.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Snapshot header update did not affect exactly one record.");

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(id, summary, firstIssues);
    }

    private static bool IsWithinRoot(string path, string root)
    {
        // Path.GetRelativePath handles drive letters and normalizes separators;
        // do not mistake D:\other for D:\others.
        string relative = Path.GetRelativePath(root, path);
        return relative == "." ||
            (!Path.IsPathRooted(relative) &&
             relative != ".." &&
             !relative.StartsWith(".." + Path.DirectorySeparatorChar,
                 StringComparison.Ordinal));
    }
}
