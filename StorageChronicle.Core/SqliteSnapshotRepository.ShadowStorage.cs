using Microsoft.Data.Sqlite;

namespace StorageChronicle.Core;

public sealed partial class SqliteSnapshotRepository
{
    private static void EnsureShadowStorageSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS shadow_measurements (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                observed_utc TEXT NOT NULL,
                status INTEGER NOT NULL,
                diagnostic TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS shadow_areas (
                reading_id INTEGER NOT NULL
                    REFERENCES shadow_measurements(id) ON DELETE CASCADE,
                area_number INTEGER NOT NULL,
                source_volume_reference TEXT NOT NULL,
                diff_volume_reference TEXT NOT NULL,
                source_drive TEXT NULL,
                diff_drive TEXT NULL,
                used_bytes INTEGER NULL,
                allocated_bytes INTEGER NULL,
                maximum_bytes INTEGER NULL,
                PRIMARY KEY (reading_id, area_number)
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>On-demand measurement. Never retroactively associates a current
    /// reading with an older filesystem snapshot.</summary>
    public ShadowStorageMeasurement MeasureShadowStorageNow() =>
        SaveShadowStorageMeasurement(new ShadowStorageWmiProbe().Measure());

    /// <summary>Atomic persistence, also usable for deterministic synthetic tests.</summary>
    public ShadowStorageMeasurement SaveShadowStorageMeasurement(
        ShadowStorageMeasurement measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        if (!Enum.IsDefined(measurement.Status))
            throw new ArgumentOutOfRangeException(nameof(measurement));
        if (measurement.Status == ShadowProbeStatus.Unavailable && measurement.Areas.Count > 0)
            throw new ArgumentException("An unavailable probe cannot contain measured areas.");

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var header = connection.CreateCommand();
        header.Transaction = transaction;
        header.CommandText = """
            INSERT INTO shadow_measurements(observed_utc,status,diagnostic)
            VALUES($utc,$status,$diagnostic);
            SELECT last_insert_rowid();
            """;
        header.Parameters.AddWithValue("$utc", Stamp(measurement.ObservedUtc));
        header.Parameters.AddWithValue("$status", (int)measurement.Status);
        header.Parameters.AddWithValue("$diagnostic",
            (object?)measurement.Diagnostic ?? DBNull.Value);
        long id = Convert.ToInt64(header.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO shadow_areas(
                reading_id,area_number,source_volume_reference,diff_volume_reference,
                source_drive,diff_drive,used_bytes,allocated_bytes,maximum_bytes)
            VALUES($reading,$index,$source,$diff,$sourceDrive,$diffDrive,$used,$allocated,$max);
            """;
        var reading = insert.Parameters.Add("$reading", SqliteType.Integer);
        var index = insert.Parameters.Add("$index", SqliteType.Integer);
        var source = insert.Parameters.Add("$source", SqliteType.Text);
        var diff = insert.Parameters.Add("$diff", SqliteType.Text);
        var sourceDrive = insert.Parameters.Add("$sourceDrive", SqliteType.Text);
        var diffDrive = insert.Parameters.Add("$diffDrive", SqliteType.Text);
        var used = insert.Parameters.Add("$used", SqliteType.Integer);
        var allocated = insert.Parameters.Add("$allocated", SqliteType.Integer);
        var maximum = insert.Parameters.Add("$max", SqliteType.Integer);
        reading.Value = id;

        for (int i = 0; i < measurement.Areas.Count; i++)
        {
            ShadowStorageArea area = measurement.Areas[i];
            if (area.UsedBytes is < 0 || area.AllocatedBytes is < 0 || area.MaximumBytes is < 0)
                throw new ArgumentOutOfRangeException(nameof(measurement),
                    "WMI byte measurements must not be negative.");
            index.Value = i;
            source.Value = area.SourceVolumeReference;
            diff.Value = area.DiffVolumeReference;
            sourceDrive.Value = (object?)area.SourceDrive ?? DBNull.Value;
            diffDrive.Value = (object?)area.DiffDrive ?? DBNull.Value;
            used.Value = DbValue(area.UsedBytes);
            allocated.Value = DbValue(area.AllocatedBytes);
            maximum.Value = DbValue(area.MaximumBytes);
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return measurement with { Id = id };
    }

    public IReadOnlyList<ShadowStorageMeasurement> ListShadowStorageMeasurements(int limit = 20)
    {
        if (limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(limit));
        var readings = new List<ShadowStorageMeasurement>();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, observed_utc, status, diagnostic
            FROM shadow_measurements ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            readings.Add(new(reader.GetInt64(0), Parse(reader.GetString(1)),
                (ShadowProbeStatus)reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), []));
        return readings;
    }

    public ShadowStorageMeasurement LoadShadowStorageMeasurement(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT observed_utc,status,diagnostic FROM shadow_measurements WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        DateTimeOffset at;
        ShadowProbeStatus status;
        string? diagnostic;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                throw new KeyNotFoundException($"Shadow measurement #{id} not found.");
            at = Parse(reader.GetString(0));
            status = (ShadowProbeStatus)reader.GetInt32(1);
            diagnostic = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        var areas = new List<ShadowStorageArea>();
        using var detail = connection.CreateCommand();
        detail.CommandText = """
            SELECT source_volume_reference,diff_volume_reference,
                   source_drive,diff_drive,used_bytes,allocated_bytes,maximum_bytes
            FROM shadow_areas WHERE reading_id=$id ORDER BY area_number;
            """;
        detail.Parameters.AddWithValue("$id", id);
        using var detailReader = detail.ExecuteReader();
        while (detailReader.Read())
            areas.Add(new(detailReader.GetString(0), detailReader.GetString(1),
                detailReader.IsDBNull(2) ? null : detailReader.GetString(2),
                detailReader.IsDBNull(3) ? null : detailReader.GetString(3),
                ReadNullableInt64(detailReader, 4),
                ReadNullableInt64(detailReader, 5),
                ReadNullableInt64(detailReader, 6)));
        return new(id, at, status, diagnostic, areas);
    }
}
