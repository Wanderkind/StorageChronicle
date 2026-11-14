using Microsoft.Data.Sqlite;

namespace StorageChronicle.Core;

/// <summary>
/// Read-only diagnostic queries for existing snapshot metadata. No migrations,
/// source-file access, or materialization of the complete file inventory.
/// </summary>
public sealed partial class SqliteSnapshotRepository
{
    public SnapshotCoverage LoadScanCoverage(long snapshotId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                s.root_path, s.volume_root, s.started_utc, s.finished_utc,
                s.free_bytes_before, s.free_bytes_after,
                (SELECT count(*) FROM snapshot_files f
                 WHERE f.snapshot_id=s.id),
                (SELECT count(*) FROM snapshot_files f
                 WHERE f.snapshot_id=s.id AND f.allocated_bytes IS NULL),
                (SELECT count(*) FROM snapshot_files f
                 WHERE f.snapshot_id=s.id AND f.file_identity IS NULL),
                (SELECT count(*) FROM snapshot_issues i
                 WHERE i.snapshot_id=s.id AND i.severity=2),
                (SELECT count(*) FROM snapshot_issues i
                 WHERE i.snapshot_id=s.id AND i.severity=1),
                (SELECT count(*) FROM snapshot_issues i
                 WHERE i.snapshot_id=s.id AND i.severity=0)
            FROM snapshots s WHERE s.id=$id;
            """;
        command.Parameters.AddWithValue("$id", snapshotId);

        string root, volume;
        DateTimeOffset started, finished;
        long? freeBefore, freeAfter;
        long fileCount, missingAllocations, missingIdentities, errors, warnings, infos;

        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw new KeyNotFoundException($"Snapshot #{snapshotId} not found.");
            root = reader.GetString(0);
            volume = reader.GetString(1);
            started = Parse(reader.GetString(2));
            finished = Parse(reader.GetString(3));
            freeBefore = ReadNullableInt64(reader, 4);
            freeAfter = ReadNullableInt64(reader, 5);
            fileCount = reader.GetInt64(6);
            missingAllocations = reader.GetInt64(7);
            missingIdentities = reader.GetInt64(8);
            errors = reader.GetInt64(9);
            warnings = reader.GetInt64(10);
            infos = reader.GetInt64(11);
        }

        using var groups = connection.CreateCommand();
        groups.CommandText = """
            SELECT severity, code, count(*)
            FROM snapshot_issues WHERE snapshot_id=$id
            GROUP BY severity, code
            ORDER BY severity DESC, count(*) DESC, code;
            """;
        groups.Parameters.AddWithValue("$id", snapshotId);
        var categories = new List<ScanIssueCategory>();
        using (var reader = groups.ExecuteReader())
        {
            while (reader.Read())
                categories.Add(new((IssueSeverity)reader.GetInt32(0),
                    reader.GetString(1), reader.GetInt64(2)));
        }

        return new(snapshotId, root, volume, started, finished,
            fileCount, errors, warnings, infos,
            missingAllocations, missingIdentities,
            freeBefore, freeAfter, categories);
    }

    /// <summary>Retrieve a bounded, stable page of saved scan notices.</summary>
    public ScanIssuePage ReadScanIssuePage(
        long snapshotId, int offset, int pageSize = 100,
        IssueSeverity? filter = null)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        if (filter.HasValue && filter.Value is not
            (IssueSeverity.Info or IssueSeverity.Warning or IssueSeverity.Error))
            throw new ArgumentOutOfRangeException(nameof(filter));

        using var connection = Open();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM snapshots WHERE id=$id;";
            check.Parameters.AddWithValue("$id", snapshotId);
            if (check.ExecuteScalar() is null)
                throw new KeyNotFoundException($"Snapshot #{snapshotId} not found.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = filter.HasValue
            ? """
              SELECT path, code, detail, severity FROM snapshot_issues
              WHERE snapshot_id=$id AND severity=$severity
              ORDER BY issue_id LIMIT $limit OFFSET $offset;
              """
            : """
              SELECT path, code, detail, severity FROM snapshot_issues
              WHERE snapshot_id=$id
              ORDER BY issue_id LIMIT $limit OFFSET $offset;
              """;
        command.Parameters.AddWithValue("$id", snapshotId);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        if (filter.HasValue)
            command.Parameters.AddWithValue("$severity", (int)filter.Value);

        var issues = new List<ScanIssue>(pageSize);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            issues.Add(new(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), (IssueSeverity)reader.GetInt32(3)));
        return new(snapshotId, offset, pageSize, filter, issues);
    }
}
