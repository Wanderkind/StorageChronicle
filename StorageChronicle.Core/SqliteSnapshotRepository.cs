using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StorageChronicle.Core;

public sealed record SnapshotSummary(
    long Id, string RootPath, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc,
    int FileCount, int ErrorCount, long? FreeBytesAfter);

/// <summary>
/// Local SQLite metadata store. The entire snapshot is inserted in one transaction:
/// a crash never leaves a partially persisted snapshot visible for comparison.
/// This database stores paths and metadata, not file contents.
/// </summary>
public sealed partial class SqliteSnapshotRepository
{
    public string DatabasePath { get; }

    public SqliteSnapshotRepository(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath ??
            throw new ArgumentNullException(nameof(databasePath)));
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                root_path TEXT NOT NULL,
                volume_root TEXT NOT NULL,
                started_utc TEXT NOT NULL,
                finished_utc TEXT NOT NULL,
                volume_capacity_bytes INTEGER NULL,
                free_bytes_before INTEGER NULL,
                free_bytes_after INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS snapshot_files (
                snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
                path TEXT NOT NULL,
                logical_bytes INTEGER NOT NULL,
                allocated_bytes INTEGER NULL,
                file_identity TEXT NULL,
                hardlink_count INTEGER NULL,
                last_write_utc TEXT NOT NULL,
                PRIMARY KEY (snapshot_id, path)
            );
            CREATE INDEX IF NOT EXISTS ix_files_identity
                ON snapshot_files(snapshot_id, file_identity);
            CREATE TABLE IF NOT EXISTS snapshot_issues (
                issue_id INTEGER PRIMARY KEY AUTOINCREMENT,
                snapshot_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
                path TEXT NOT NULL,
                code TEXT NOT NULL,
                detail TEXT NOT NULL,
                severity INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_issues_snapshot
                ON snapshot_issues(snapshot_id);
            """;
        setup.ExecuteNonQuery();
    }

    public long Save(DiskSnapshot snapshot)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using var header = connection.CreateCommand();
        header.Transaction = transaction;
        header.CommandText = """
            INSERT INTO snapshots
                (root_path, volume_root, started_utc, finished_utc,
                 volume_capacity_bytes, free_bytes_before, free_bytes_after)
            VALUES ($root, $volume, $start, $finish, $capacity, $before, $after);
            SELECT last_insert_rowid();
            """;
        header.Parameters.AddWithValue("$root", snapshot.RootPath);
        header.Parameters.AddWithValue("$volume", snapshot.VolumeRoot);
        header.Parameters.AddWithValue("$start", Stamp(snapshot.StartedUtc));
        header.Parameters.AddWithValue("$finish", Stamp(snapshot.FinishedUtc));
        header.Parameters.AddWithValue("$capacity", DbValue(snapshot.VolumeCapacityBytes));
        header.Parameters.AddWithValue("$before", DbValue(snapshot.FreeBytesBefore));
        header.Parameters.AddWithValue("$after", DbValue(snapshot.FreeBytesAfter));
        long snapshotId = Convert.ToInt64(header.ExecuteScalar(), CultureInfo.InvariantCulture);

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
        fId.Value = snapshotId;
        foreach (FileRecord file in snapshot.Files)
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
        iId.Value = snapshotId;
        foreach (ScanIssue issue in snapshot.Issues)
        {
            iPath.Value = issue.Path;
            iCode.Value = issue.Code;
            iDetail.Value = issue.Detail;
            iSeverity.Value = (int)issue.Severity;
            insertIssue.ExecuteNonQuery();
        }

        transaction.Commit();
        return snapshotId;
    }

    public IReadOnlyList<SnapshotSummary> List(int limit = 100)
    {
        if (limit is < 1 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var results = new List<SnapshotSummary>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.root_path, s.started_utc, s.finished_utc,
                   (SELECT count(*) FROM snapshot_files f WHERE f.snapshot_id=s.id),
                   (SELECT count(*) FROM snapshot_issues i
                      WHERE i.snapshot_id=s.id AND i.severity=2),
                   s.free_bytes_after
            FROM snapshots s ORDER BY s.id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new(reader.GetInt64(0), reader.GetString(1),
                Parse(reader.GetString(2)), Parse(reader.GetString(3)),
                checked((int)reader.GetInt64(4)), checked((int)reader.GetInt64(5)),
                ReadNullableInt64(reader, 6)));
        return results;
    }

    public DiskSnapshot Load(long id)
    {
        using var connection = Open();
        using var header = connection.CreateCommand();
        header.CommandText = """
            SELECT id, root_path, volume_root, started_utc, finished_utc,
                   volume_capacity_bytes, free_bytes_before, free_bytes_after
            FROM snapshots WHERE id=$id;
            """;
        header.Parameters.AddWithValue("$id", id);
        string root;
        string volume;
        DateTimeOffset start;
        DateTimeOffset finish;
        long? capacity;
        long? before;
        long? after;
        using (var reader = header.ExecuteReader())
        {
            if (!reader.Read())
                throw new KeyNotFoundException($"Snapshot #{id} does not exist.");
            root = reader.GetString(1);
            volume = reader.GetString(2);
            start = Parse(reader.GetString(3));
            finish = Parse(reader.GetString(4));
            capacity = ReadNullableInt64(reader, 5);
            before = ReadNullableInt64(reader, 6);
            after = ReadNullableInt64(reader, 7);
        }

        var files = new List<FileRecord>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT path, logical_bytes, allocated_bytes, file_identity,
                       hardlink_count, last_write_utc
                FROM snapshot_files WHERE snapshot_id=$id ORDER BY path;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                files.Add(new(reader.GetString(0), reader.GetInt64(1),
                    ReadNullableInt64(reader, 2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : checked((uint)reader.GetInt64(4)),
                    Parse(reader.GetString(5))));
        }

        var issues = new List<ScanIssue>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT path, code, detail, severity
                FROM snapshot_issues WHERE snapshot_id=$id ORDER BY issue_id;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
                issues.Add(new(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), (IssueSeverity)reader.GetInt32(3)));
        }
        return new(id, root, volume, start, finish, capacity, before, after,
            files, issues);
    }

    private SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static object DbValue(long? value) => value is long n ? n : DBNull.Value;
    private static long? ReadNullableInt64(SqliteDataReader r, int column) =>
        r.IsDBNull(column) ? null : r.GetInt64(column);
    private static string Stamp(DateTimeOffset time) =>
        time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
}
