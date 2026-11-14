using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StorageChronicle.Core;

public sealed partial class SqliteSnapshotRepository
{
    private const string FolderSummaryAlgorithmVersion = "phase1d-v1";

    private static void EnsureFolderSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS comparison_folder_summaries (
                comparison_id INTEGER NOT NULL REFERENCES comparisons(id) ON DELETE CASCADE,
                folder_key TEXT NOT NULL,
                folder_path TEXT NOT NULL,
                net_allocated_delta INTEGER NOT NULL,
                known_growth INTEGER NOT NULL,
                known_reduction INTEGER NOT NULL,
                changed_groups INTEGER NOT NULL,
                unknown_groups INTEGER NOT NULL,
                PRIMARY KEY (comparison_id, folder_key)
            );
            CREATE INDEX IF NOT EXISTS ix_folder_summaries_growth
                ON comparison_folder_summaries(comparison_id, net_allocated_delta DESC, folder_key);
            CREATE TABLE IF NOT EXISTS comparison_folder_summary_runs (
                comparison_id INTEGER PRIMARY KEY REFERENCES comparisons(id) ON DELETE CASCADE,
                algorithm_version TEXT NOT NULL,
                completed_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    private sealed class FolderContribution(string path)
    {
        public string Path { get; } = path;
        public long NetDelta { get; set; }
        public int Unknown { get; set; }
    }

    /// <summary>
    /// Build the folder allocation summaries from an existing stored comparison.
    /// Reads only one change at a time; summaries are maintained in SQLite.
    /// A cancellation rolls back only this folder summary, leaving snapshots and
    /// the original comparison cache intact.
    /// </summary>
    public StoredFolderSummary BuildFolderSummary(
        long comparisonId,
        CancellationToken cancellationToken = default,
        IProgress<FolderBuildProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        StoredComparison comparison =
            ReadStoredComparison(connection, transaction, comparisonId);
        string root = ReadComparisonRoot(connection, transaction, comparisonId);
        string rootKey = FolderKey(root);

        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = """
                SELECT algorithm_version
                FROM comparison_folder_summary_runs WHERE comparison_id=$id;
                """;
            check.Parameters.AddWithValue("$id", comparisonId);
            if (check.ExecuteScalar() is string version &&
                version == FolderSummaryAlgorithmVersion)
                return ReadStoredFolderSummary(
                    connection, transaction, comparisonId, root, rootKey);
        }

        // Rebuilding is safe and atomic when the algorithm version changes.
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = """
                DELETE FROM comparison_folder_summary_runs WHERE comparison_id=$id;
                DELETE FROM comparison_folder_summaries WHERE comparison_id=$id;
                """;
            clear.Parameters.AddWithValue("$id", comparisonId);
            clear.ExecuteNonQuery();
        }

        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO comparison_folder_summaries
                (comparison_id, folder_key, folder_path, net_allocated_delta,
                 known_growth, known_reduction, changed_groups, unknown_groups)
            VALUES ($id, $key, $path, $net, $gain, $loss, 1, $unknown)
            ON CONFLICT(comparison_id, folder_key) DO UPDATE SET
                net_allocated_delta =
                    net_allocated_delta + excluded.net_allocated_delta,
                known_growth = known_growth + excluded.known_growth,
                known_reduction = known_reduction + excluded.known_reduction,
                changed_groups = changed_groups + 1,
                unknown_groups = unknown_groups + excluded.unknown_groups;
            """;
        var idParam = upsert.Parameters.Add("$id", SqliteType.Integer);
        var keyParam = upsert.Parameters.Add("$key", SqliteType.Text);
        var pathParam = upsert.Parameters.Add("$path", SqliteType.Text);
        var netParam = upsert.Parameters.Add("$net", SqliteType.Integer);
        var gainParam = upsert.Parameters.Add("$gain", SqliteType.Integer);
        var lossParam = upsert.Parameters.Add("$loss", SqliteType.Integer);
        var unknownParam = upsert.Parameters.Add("$unknown", SqliteType.Integer);
        idParam.Value = comparisonId;

        long processed = 0;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT before_allocated, after_allocated, allocated_delta,
                       before_paths_json, after_paths_json
                FROM comparison_changes
                WHERE comparison_id=$id ORDER BY change_id;
                """;
            command.Parameters.AddWithValue("$id", comparisonId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] beforePaths =
                    JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
                string[] afterPaths =
                    JsonSerializer.Deserialize<string[]>(reader.GetString(4)) ?? [];
                if (beforePaths.Length == 0 && afterPaths.Length == 0)
                    throw new InvalidDataException(
                        "Comparison contains a change with neither a before nor an after path.");

                bool unknown = reader.IsDBNull(2);
                var contributions =
                    new Dictionary<string, FolderContribution>(PathComparer.Instance);

                void Accumulate(string filePath, long amount)
                {
                    foreach (string folder in EnumerateAncestorFolders(root, filePath))
                    {
                        if (!contributions.TryGetValue(folder, out var c))
                            contributions[folder] = c = new FolderContribution(folder);
                        c.NetDelta = checked(c.NetDelta + amount);
                    }
                }

                void MarkUnknown(string filePath)
                {
                    foreach (string folder in EnumerateAncestorFolders(root, filePath))
                    {
                        if (!contributions.TryGetValue(folder, out var c))
                            contributions[folder] = c = new FolderContribution(folder);
                        c.Unknown = 1;
                    }
                }

                // Each NTFS identity is attributed once, to its first path in
                // ordinal order. Hardlink attribution is therefore a convention,
                // not a proof of which directory "owns" the allocation.
                if (unknown)
                {
                    if (beforePaths.Length > 0) MarkUnknown(beforePaths[0]);
                    if (afterPaths.Length > 0) MarkUnknown(afterPaths[0]);
                }
                else
                {
                    if (beforePaths.Length > 0)
                        Accumulate(beforePaths[0], -reader.GetInt64(0));
                    if (afterPaths.Length > 0)
                        Accumulate(afterPaths[0], reader.GetInt64(1));
                }

                foreach (var contribution in contributions.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    keyParam.Value = FolderKey(contribution.Path);
                    pathParam.Value = contribution.Path;
                    netParam.Value = contribution.NetDelta;
                    gainParam.Value = Math.Max(contribution.NetDelta, 0L);
                    lossParam.Value = contribution.NetDelta < 0
                        ? checked(-contribution.NetDelta) : 0L;
                    unknownParam.Value = contribution.Unknown;
                    upsert.ExecuteNonQuery();
                }

                processed = checked(processed + 1);
                if (processed % 250 == 0)
                    progress?.Report(new(processed, comparison.ChangeCount));
            }
        }

        if (processed != comparison.ChangeCount)
            throw new InvalidDataException(
                $"Stored comparison expected {comparison.ChangeCount} changes but read {processed}.");
        var result = ReadStoredFolderSummary(
            connection, transaction, comparisonId, root, rootKey);
        if (result.RootKnownNetDeltaBytes != comparison.KnownAllocatedDeltaBytes ||
            result.RootUnknownAllocationGroups != comparison.UnknownAllocationChanges)
            throw new InvalidDataException(
                "Folder summary cannot reconcile with the saved comparison totals.");

        cancellationToken.ThrowIfCancellationRequested();
        using (var complete = connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = """
                INSERT INTO comparison_folder_summary_runs
                    (comparison_id, algorithm_version, completed_utc)
                VALUES ($id, $version, $completed);
                """;
            complete.Parameters.AddWithValue("$id", comparisonId);
            complete.Parameters.AddWithValue("$version", FolderSummaryAlgorithmVersion);
            complete.Parameters.AddWithValue("$completed", Stamp(DateTimeOffset.UtcNow));
            complete.ExecuteNonQuery();
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        progress?.Report(new(processed, comparison.ChangeCount));
        return result;
    }

    /// <summary>Read a bounded page of directory totals, excluding the
    /// comparison root itself. Parent and child totals overlap.</summary>
    public FolderPage ReadFolderPage(
        long comparisonId, int offset, int pageSize = 100,
        bool largestGrowthFirst = true)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(pageSize));

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string root = ReadComparisonRoot(connection, transaction, comparisonId);
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = """
                SELECT 1 FROM comparison_folder_summary_runs
                WHERE comparison_id=$id AND algorithm_version=$version;
                """;
            check.Parameters.AddWithValue("$id", comparisonId);
            check.Parameters.AddWithValue("$version", FolderSummaryAlgorithmVersion);
            if (check.ExecuteScalar() is null)
                throw new InvalidOperationException(
                    "Folder summary has not been built for this comparison.");
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT folder_path, net_allocated_delta, known_growth,
                   known_reduction, changed_groups, unknown_groups
            FROM comparison_folder_summaries
            WHERE comparison_id=$id AND folder_key<>$root
            ORDER BY net_allocated_delta {(largestGrowthFirst ? "DESC" : "ASC")},
                     folder_key
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$id", comparisonId);
        command.Parameters.AddWithValue("$root", FolderKey(root));
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", offset);
        var folders = new List<FolderChange>(pageSize);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            folders.Add(new(reader.GetString(0), reader.GetInt64(1),
                reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5)));
        return new(comparisonId, offset, pageSize, folders);
    }

    private static string ReadComparisonRoot(
        SqliteConnection connection, SqliteTransaction transaction, long id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.root_path FROM comparisons c
            JOIN snapshots s ON s.id=c.current_id WHERE c.id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        return (string?)command.ExecuteScalar() ??
            throw new KeyNotFoundException($"Comparison #{id} not found.");
    }

    private static StoredFolderSummary ReadStoredFolderSummary(
        SqliteConnection connection, SqliteTransaction transaction,
        long comparisonId, string root, string rootKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM comparison_folder_summaries
                  WHERE comparison_id=$id AND folder_key<>$root),
                COALESCE((SELECT net_allocated_delta FROM comparison_folder_summaries
                  WHERE comparison_id=$id AND folder_key=$root),0),
                COALESCE((SELECT unknown_groups FROM comparison_folder_summaries
                  WHERE comparison_id=$id AND folder_key=$root),0);
            """;
        command.Parameters.AddWithValue("$id", comparisonId);
        command.Parameters.AddWithValue("$root", rootKey);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(comparisonId, root, reader.GetInt64(0),
            reader.GetInt64(1), reader.GetInt64(2));
    }

    private static string FolderKey(string path)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    private static IEnumerable<string> EnumerateAncestorFolders(
        string scanRoot, string filePath)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scanRoot));
        string? parent = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (parent is not null)
        {
            string relative = Path.GetRelativePath(root, parent);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) ||
                relative.StartsWith(".." + Path.AltDirectorySeparatorChar,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Stored file path falls outside the snapshot's scan root.");

            string folder = PathComparer.SamePath(parent, root) ? root :
                Path.TrimEndingDirectorySeparator(parent);
            yield return folder;
            if (PathComparer.SamePath(folder, root))
                yield break;
            parent = Path.GetDirectoryName(parent);
        }
        throw new InvalidDataException("Stored file path has no ancestor at the scan root.");
    }
}
