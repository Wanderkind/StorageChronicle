using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StorageChronicle.Core;

public sealed partial class SqliteSnapshotRepository
{
    private static void EnsureComparisonSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS comparisons (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                baseline_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
                current_id INTEGER NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
                created_utc TEXT NOT NULL,
                change_count INTEGER NOT NULL DEFAULT 0,
                known_allocated_delta INTEGER NOT NULL DEFAULT 0,
                unknown_changes INTEGER NOT NULL DEFAULT 0,
                volume_used_delta INTEGER NULL,
                unexplained_delta INTEGER NULL,
                caveats_json TEXT NOT NULL DEFAULT '[]',
                UNIQUE (baseline_id, current_id)
            );
            CREATE TABLE IF NOT EXISTS comparison_changes (
                change_id INTEGER PRIMARY KEY AUTOINCREMENT,
                comparison_id INTEGER NOT NULL REFERENCES comparisons(id) ON DELETE CASCADE,
                kind INTEGER NOT NULL,
                primary_path TEXT NOT NULL,
                before_allocated INTEGER NULL,
                after_allocated INTEGER NULL,
                allocated_delta INTEGER NULL,
                before_logical INTEGER NOT NULL,
                after_logical INTEGER NOT NULL,
                before_paths_json TEXT NOT NULL,
                after_paths_json TEXT NOT NULL,
                known_flag INTEGER NOT NULL,
                magnitude INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_comparison_changes_rank
                ON comparison_changes(
                    comparison_id, known_flag DESC, magnitude DESC,
                    primary_path COLLATE SC_ORDINAL_NOCASE, change_id
                );
            """;
        command.ExecuteNonQuery();
    }

    private sealed record SnapshotFacts(
        long Id, string Root, string Volume, DateTimeOffset Started,
        long? Capacity, long? FreeBefore, long? FreeAfter,
        bool HasErrors, bool HasReparseExclusions,
        bool HasMissingIdentity, long MissingAllocationCount);

    private static SnapshotFacts ReadFacts(
        SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.id, s.root_path, s.volume_root, s.started_utc,
                   s.volume_capacity_bytes, s.free_bytes_before, s.free_bytes_after,
                   EXISTS (SELECT 1 FROM snapshot_issues i
                           WHERE i.snapshot_id=s.id AND i.severity=2),
                   EXISTS (SELECT 1 FROM snapshot_issues i
                           WHERE i.snapshot_id=s.id AND i.code='REPARSE_EXCLUDED'),
                   EXISTS (SELECT 1 FROM snapshot_files f
                           WHERE f.snapshot_id=s.id AND f.file_identity IS NULL),
                   (SELECT count(*) FROM snapshot_files f
                    WHERE f.snapshot_id=s.id AND f.allocated_bytes IS NULL)
            FROM snapshots s WHERE s.id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new KeyNotFoundException($"Snapshot #{id} was not found.");
        return new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            Parse(reader.GetString(3)), ReadNullableInt64(reader, 4),
            ReadNullableInt64(reader, 5), ReadNullableInt64(reader, 6),
            reader.GetInt64(7) != 0, reader.GetInt64(8) != 0,
            reader.GetInt64(9) != 0, reader.GetInt64(10));
    }

    private sealed record FileGroup(string Key, IReadOnlyList<FileRecord> Files);

    private sealed class GroupCursor(SqliteDataReader reader)
    {
        private bool _hasRow = reader.Read();

        public FileGroup? Next(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_hasRow) return null;
            string key = reader.GetString(0);
            var files = new List<FileRecord>();
            do
            {
                files.Add(new FileRecord(reader.GetString(1), reader.GetInt64(2),
                    ReadNullableInt64(reader, 3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : checked((uint)reader.GetInt64(5)),
                    DateTimeOffset.UnixEpoch));
                cancellationToken.ThrowIfCancellationRequested();
                _hasRow = reader.Read();
            }
            while (_hasRow &&
                   PathComparer.Instance.Equals(key, reader.GetString(0)));
            return new(key, files);
        }
    }

    private static SqliteCommand CreateOrderedFileCommand(
        SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE WHEN file_identity IS NOT NULL
                        THEN 'ID:' || file_identity ELSE 'PATH:' || path END AS file_key,
                   path, logical_bytes, allocated_bytes, file_identity, hardlink_count
            FROM snapshot_files
            WHERE snapshot_id=$id
            ORDER BY file_key COLLATE SC_ORDINAL_NOCASE,
                     path COLLATE SC_ORDINAL_NOCASE;
            """;
        command.Parameters.AddWithValue("$id", id);
        return command;
    }

    /// <summary>
    /// Disk-backed comparison: both file inventories are ordered by SQLite and
    /// consumed group by group; resulting changes are committed atomically.
    /// Repeated comparisons of the same immutable snapshots reuse their cache.
    /// </summary>
    public StoredComparison CompareAndStore(
        long baselineId, long currentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (baselineId == currentId)
            throw new ArgumentException("Select two distinct snapshots.");

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var baseline = ReadFacts(connection, transaction, baselineId);
        var current = ReadFacts(connection, transaction, currentId);
        if (!PathComparer.SamePath(baseline.Root, current.Root))
            throw new ArgumentException("Snapshots must cover the same scan root.");
        if (!PathComparer.SameVolume(baseline.Volume, current.Volume))
            throw new ArgumentException("Snapshots are from different volumes.");

        using (var cached = connection.CreateCommand())
        {
            cached.Transaction = transaction;
            cached.CommandText = """
                SELECT id FROM comparisons
                WHERE baseline_id=$baseline AND current_id=$current;
                """;
            cached.Parameters.AddWithValue("$baseline", baselineId);
            cached.Parameters.AddWithValue("$current", currentId);
            if (cached.ExecuteScalar() is long existingId)
                return ReadStoredComparison(connection, transaction, existingId);
        }

        using var insertHeader = connection.CreateCommand();
        insertHeader.Transaction = transaction;
        insertHeader.CommandText = """
            INSERT INTO comparisons(baseline_id,current_id,created_utc)
            VALUES ($baseline,$current,$created);
            SELECT last_insert_rowid();
            """;
        insertHeader.Parameters.AddWithValue("$baseline", baselineId);
        insertHeader.Parameters.AddWithValue("$current", currentId);
        insertHeader.Parameters.AddWithValue("$created", Stamp(DateTimeOffset.UtcNow));
        long comparisonId = Convert.ToInt64(insertHeader.ExecuteScalar(),
            CultureInfo.InvariantCulture);

        using var insertChange = connection.CreateCommand();
        insertChange.Transaction = transaction;
        insertChange.CommandText = """
            INSERT INTO comparison_changes
                (comparison_id, kind, primary_path, before_allocated, after_allocated,
                 allocated_delta, before_logical, after_logical,
                 before_paths_json, after_paths_json, known_flag, magnitude)
            VALUES ($id, $kind, $path, $ba, $aa, $delta, $bl, $al,
                    $bp, $ap, $known, $magnitude);
            """;
        var idParam = insertChange.Parameters.Add("$id", SqliteType.Integer);
        var kindParam = insertChange.Parameters.Add("$kind", SqliteType.Integer);
        var pathParam = insertChange.Parameters.Add("$path", SqliteType.Text);
        var beforeParam = insertChange.Parameters.Add("$ba", SqliteType.Integer);
        var afterParam = insertChange.Parameters.Add("$aa", SqliteType.Integer);
        var deltaParam = insertChange.Parameters.Add("$delta", SqliteType.Integer);
        var blParam = insertChange.Parameters.Add("$bl", SqliteType.Integer);
        var alParam = insertChange.Parameters.Add("$al", SqliteType.Integer);
        var bpParam = insertChange.Parameters.Add("$bp", SqliteType.Text);
        var apParam = insertChange.Parameters.Add("$ap", SqliteType.Text);
        var knownParam = insertChange.Parameters.Add("$known", SqliteType.Integer);
        var magnitudeParam = insertChange.Parameters.Add("$magnitude", SqliteType.Integer);
        idParam.Value = comparisonId;

        long changeCount = 0, unknownCount = 0, knownDelta = 0;
        void AddChange(FileChange change)
        {
            kindParam.Value = (int)change.Kind;
            pathParam.Value = change.PrimaryPath;
            beforeParam.Value = DbValue(change.BeforeAllocatedBytes);
            afterParam.Value = DbValue(change.AfterAllocatedBytes);
            deltaParam.Value = DbValue(change.AllocatedDeltaBytes);
            blParam.Value = change.BeforeLogicalBytes;
            alParam.Value = change.AfterLogicalBytes;
            bpParam.Value = JsonSerializer.Serialize(change.BeforePaths);
            apParam.Value = JsonSerializer.Serialize(change.AfterPaths);
            knownParam.Value = change.AllocatedDeltaBytes.HasValue ? 1 : 0;
            magnitudeParam.Value = change.AllocatedDeltaBytes is long v
                ? v == long.MinValue ? long.MaxValue : Math.Abs(v)
                : 0;
            insertChange.ExecuteNonQuery();
            changeCount = checked(changeCount + 1);
            if (change.AllocatedDeltaBytes is long delta)
                knownDelta = checked(knownDelta + delta);
            else unknownCount = checked(unknownCount + 1);
        }

        // Active readers and writes share the same connection and transaction.
        // All SQLite statements are consumed and disposed before COMMIT.
        using (var leftCommand = CreateOrderedFileCommand(connection, transaction, baselineId))
        using (var rightCommand = CreateOrderedFileCommand(connection, transaction, currentId))
        using (var leftReader = leftCommand.ExecuteReader())
        using (var rightReader = rightCommand.ExecuteReader())
        {
            var leftCursor = new GroupCursor(leftReader);
            var rightCursor = new GroupCursor(rightReader);
            FileGroup? left = leftCursor.Next(cancellationToken);
            FileGroup? right = rightCursor.Next(cancellationToken);
            while (left is not null || right is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int order = left is null ? 1 : right is null ? -1 :
                    PathComparer.Instance.Compare(left.Key, right.Key);
                FileChange? change;
                if (order == 0)
                {
                    change = SnapshotComparer.CompareGroups(left!.Files, right!.Files);
                    left = leftCursor.Next(cancellationToken);
                    right = rightCursor.Next(cancellationToken);
                }
                else if (order < 0)
                {
                    change = SnapshotComparer.CompareGroups(left!.Files, []);
                    left = leftCursor.Next(cancellationToken);
                }
                else
                {
                    change = SnapshotComparer.CompareGroups([], right!.Files);
                    right = rightCursor.Next(cancellationToken);
                }
                if (change is not null)
                    AddChange(change);
            }
        }

        long? volumeDelta = baseline.FreeAfter.HasValue && current.FreeAfter.HasValue
            ? checked(baseline.FreeAfter.Value - current.FreeAfter.Value) : null;
        long? unexplained = volumeDelta.HasValue
            ? checked(volumeDelta.Value - knownDelta) : null;
        var caveats = DescribeCaveats(baseline, current, unknownCount);

        cancellationToken.ThrowIfCancellationRequested();
        using var finalize = connection.CreateCommand();
        finalize.Transaction = transaction;
        finalize.CommandText = """
            UPDATE comparisons
            SET change_count=$count, known_allocated_delta=$known,
                unknown_changes=$unknown, volume_used_delta=$used,
                unexplained_delta=$unexplained, caveats_json=$caveats
            WHERE id=$id;
            """;
        finalize.Parameters.AddWithValue("$id", comparisonId);
        finalize.Parameters.AddWithValue("$count", changeCount);
        finalize.Parameters.AddWithValue("$known", knownDelta);
        finalize.Parameters.AddWithValue("$unknown", unknownCount);
        finalize.Parameters.AddWithValue("$used", DbValue(volumeDelta));
        finalize.Parameters.AddWithValue("$unexplained", DbValue(unexplained));
        finalize.Parameters.AddWithValue("$caveats", JsonSerializer.Serialize(caveats));
        if (finalize.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Could not finalize comparison.");

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(comparisonId, baselineId, currentId, changeCount, knownDelta,
            unknownCount, volumeDelta, unexplained, caveats);
    }

    private static IReadOnlyList<string> DescribeCaveats(
        SnapshotFacts baseline, SnapshotFacts current, long unknownCount)
    {
        var caveats = new List<string>();
        if (baseline.Started > current.Started)
            caveats.Add("The selected baseline is newer than the current snapshot; the comparison direction is reversed.");
        if (baseline.Capacity.HasValue && current.Capacity.HasValue &&
            baseline.Capacity.Value != current.Capacity.Value)
            caveats.Add("The volume capacity changed between snapshots; free-space changes require additional interpretation.");
        if (baseline.HasMissingIdentity || current.HasMissingIdentity)
            caveats.Add("Some file identities were unavailable, so a rename or replacement may appear as an addition and deletion.");
        if (!PathComparer.SamePath(baseline.Root, baseline.Volume) ||
            !PathComparer.SamePath(current.Root, current.Volume))
            caveats.Add("Directory-scoped files cannot explain changes elsewhere on the volume.");
        if (baseline.HasErrors || current.HasErrors)
            caveats.Add("At least one snapshot contains inaccessible or unreadable paths.");
        if (unknownCount > 0)
            caveats.Add($"{unknownCount} changed file group(s) have unknown allocated size.");
        long missing = checked(baseline.MissingAllocationCount + current.MissingAllocationCount);
        if (missing > 0)
            caveats.Add($"{missing} file observation(s) lack allocation measurements; allocation-only changes may be invisible.");
        if (baseline.HasReparseExclusions || current.HasReparseExclusions)
            caveats.Add("Reparse points were excluded, including junctions and symlinks.");
        if ((baseline.FreeBefore.HasValue && baseline.FreeAfter.HasValue &&
             baseline.FreeBefore.Value != baseline.FreeAfter.Value) ||
            (current.FreeBefore.HasValue && current.FreeAfter.HasValue &&
             current.FreeBefore.Value != current.FreeAfter.Value))
            caveats.Add("Free space changed during at least one scan; observations were not atomic.");
        if (!baseline.FreeAfter.HasValue || !current.FreeAfter.HasValue)
            caveats.Add("Actual volume free-space readings were unavailable.");
        caveats.Add("File allocation excludes some filesystem metadata, shadow storage and other system-managed usage.");
        return caveats;
    }

    public StoredComparison LoadComparison(long comparisonId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        return ReadStoredComparison(connection, transaction, comparisonId);
    }

    private static StoredComparison ReadStoredComparison(
        SqliteConnection connection, SqliteTransaction transaction, long comparisonId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, baseline_id, current_id, change_count, known_allocated_delta,
                   unknown_changes, volume_used_delta, unexplained_delta, caveats_json
            FROM comparisons WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", comparisonId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new KeyNotFoundException($"Comparison #{comparisonId} was not found.");
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
            ReadNullableInt64(reader, 6), ReadNullableInt64(reader, 7),
            JsonSerializer.Deserialize<string[]>(reader.GetString(8)) ?? []);
    }

    /// <summary>Retrieve at most pageSize changes. Results are ordered by
    /// largest known absolute allocated delta, then path; unknown deltas last.</summary>
    public ComparisonPage ReadComparisonPage(
        long comparisonId, int offset, int pageSize = 100)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, primary_path, before_allocated, after_allocated,
                   allocated_delta, before_logical, after_logical,
                   before_paths_json, after_paths_json
            FROM comparison_changes
            WHERE comparison_id=$id
            ORDER BY known_flag DESC, magnitude DESC,
                     primary_path COLLATE SC_ORDINAL_NOCASE, change_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$id", comparisonId);
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        var results = new List<FileChange>(pageSize);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            results.Add(new FileChange((ChangeKind)reader.GetInt32(0),
                reader.GetString(1),
                JsonSerializer.Deserialize<string[]>(reader.GetString(7)) ?? [],
                JsonSerializer.Deserialize<string[]>(reader.GetString(8)) ?? [],
                ReadNullableInt64(reader, 2), ReadNullableInt64(reader, 3),
                ReadNullableInt64(reader, 4),
                reader.GetInt64(5), reader.GetInt64(6)));
        return new(comparisonId, offset, pageSize, results);
    }
}
