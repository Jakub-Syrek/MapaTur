using System.Globalization;

using MapaTur.Application.Climbing;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;

using Microsoft.Data.Sqlite;

namespace MapaTur.Infrastructure.Climbing;

/// <summary>
/// SQLite-backed repository for climbing areas. Mirrors
/// <see cref="MapaTur.Infrastructure.Trails.SqliteTrailRepository"/> in structure but
/// stores point features (lat/lon) instead of polylines.
/// </summary>
public sealed class SqliteClimbingRepository : IClimbingRepository, IDisposable
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS climbing_areas (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            type INTEGER NOT NULL,
            latitude REAL NOT NULL,
            longitude REAL NOT NULL,
            grade TEXT,
            length_meters INTEGER,
            is_bolted INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_climbing_areas_bbox ON climbing_areas (latitude, longitude);
        """;

    private const string UpsertSql = """
        INSERT INTO climbing_areas (id, name, type, latitude, longitude, grade, length_meters, is_bolted)
        VALUES ($id, $name, $type, $lat, $lon, $grade, $length, $bolted)
        ON CONFLICT(id) DO UPDATE SET
            name = excluded.name,
            type = excluded.type,
            latitude = excluded.latitude,
            longitude = excluded.longitude,
            grade = excluded.grade,
            length_meters = excluded.length_meters,
            is_bolted = excluded.is_bolted;
        """;

    private const string FindIntersectingSql = """
        SELECT id, name, type, latitude, longitude, grade, length_meters, is_bolted
        FROM climbing_areas
        WHERE latitude BETWEEN $south AND $north
          AND longitude BETWEEN $west AND $east;
        """;

    private readonly SqliteConnection connection;
    private bool disposed;

    /// <summary>
    /// Opens or creates the SQLite database at <paramref name="databasePath"/>.
    /// </summary>
    /// <param name="databasePath">Absolute path to the database file.</param>
    public SqliteClimbingRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        connection = new SqliteConnection(connectionString);
        connection.Open();

        using var schema = connection.CreateCommand();
        schema.CommandText = SchemaSql;
        schema.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public async Task UpsertAsync(IEnumerable<ClimbingArea> areas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(areas);
        ThrowIfDisposed();

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var area in areas)
        {
            await UpsertSingleAsync(area, transaction, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClimbingArea>> FindIntersectingAsync(MapBounds bounds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await using var command = connection.CreateCommand();
        command.CommandText = FindIntersectingSql;
        command.Parameters.AddWithValue("$south", bounds.SouthWest.Latitude);
        command.Parameters.AddWithValue("$west", bounds.SouthWest.Longitude);
        command.Parameters.AddWithValue("$north", bounds.NorthEast.Latitude);
        command.Parameters.AddWithValue("$east", bounds.NorthEast.Longitude);

        var results = new List<ClimbingArea>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long id = reader.GetInt64(0);
            string name = reader.GetString(1);
            var type = (ClimbingType)reader.GetInt32(2);
            double lat = reader.GetDouble(3);
            double lon = reader.GetDouble(4);
            string? grade = reader.IsDBNull(5) ? null : reader.GetString(5);
            int? length = reader.IsDBNull(6) ? null : reader.GetInt32(6);
            bool? bolted = reader.IsDBNull(7) ? null : reader.GetInt32(7) != 0;

            try
            {
                results.Add(new ClimbingArea(id, name, new GeoPoint(lat, lon), type, grade, length, bolted));
            }
            catch (ArgumentException)
            {
                // Skip rows that fail invariants (e.g. corrupted lat/lon).
            }
        }

        return results;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        connection.Dispose();
        disposed = true;
    }

    private async Task UpsertSingleAsync(ClimbingArea area, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;

        command.Parameters.AddWithValue("$id", area.Id);
        command.Parameters.AddWithValue("$name", area.Name);
        command.Parameters.AddWithValue("$type", (int)area.Type);
        command.Parameters.AddWithValue("$lat", area.Position.Latitude);
        command.Parameters.AddWithValue("$lon", area.Position.Longitude);
        command.Parameters.AddWithValue("$grade", (object?)area.Grade ?? DBNull.Value);
        command.Parameters.AddWithValue("$length", (object?)area.LengthMeters ?? DBNull.Value);
        command.Parameters.AddWithValue("$bolted", area.IsBolted is null
            ? DBNull.Value
            : area.IsBolted.Value ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}