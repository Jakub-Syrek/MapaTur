using MapaTur.Application.Pois;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;

using Microsoft.Data.Sqlite;

namespace MapaTur.Infrastructure.Pois;

/// <summary>
/// SQLite-backed repository for mountain POIs. Mirrors
/// <see cref="MapaTur.Infrastructure.Climbing.SqliteClimbingRepository"/> in structure — point
/// features keyed by OSM id, indexed by lat/lon for bounding-box queries.
/// </summary>
public sealed class SqlitePoiRepository : IPoiRepository, IDisposable
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS pois (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            kind INTEGER NOT NULL,
            latitude REAL NOT NULL,
            longitude REAL NOT NULL,
            elevation_meters REAL
        );
        CREATE INDEX IF NOT EXISTS ix_pois_bbox ON pois (latitude, longitude);
        """;

    private const string UpsertSql = """
        INSERT INTO pois (id, name, kind, latitude, longitude, elevation_meters)
        VALUES ($id, $name, $kind, $lat, $lon, $ele)
        ON CONFLICT(id) DO UPDATE SET
            name = excluded.name,
            kind = excluded.kind,
            latitude = excluded.latitude,
            longitude = excluded.longitude,
            elevation_meters = excluded.elevation_meters;
        """;

    private const string FindIntersectingSql = """
        SELECT id, name, kind, latitude, longitude, elevation_meters
        FROM pois
        WHERE latitude BETWEEN $south AND $north
          AND longitude BETWEEN $west AND $east;
        """;

    private readonly SqliteConnection connection;
    private bool disposed;

    /// <summary>Opens or creates the SQLite database at <paramref name="databasePath"/>.</summary>
    /// <param name="databasePath">Absolute path to the database file.</param>
    public SqlitePoiRepository(string databasePath)
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
    public async Task UpsertAsync(IEnumerable<MountainPoi> pois, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pois);
        ThrowIfDisposed();

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var poi in pois)
        {
            await UpsertSingleAsync(poi, transaction, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MountainPoi>> FindIntersectingAsync(MapBounds bounds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await using var command = connection.CreateCommand();
        command.CommandText = FindIntersectingSql;
        command.Parameters.AddWithValue("$south", bounds.SouthWest.Latitude);
        command.Parameters.AddWithValue("$west", bounds.SouthWest.Longitude);
        command.Parameters.AddWithValue("$north", bounds.NorthEast.Latitude);
        command.Parameters.AddWithValue("$east", bounds.NorthEast.Longitude);

        var results = new List<MountainPoi>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long id = reader.GetInt64(0);
            string name = reader.GetString(1);
            var kind = (PoiKind)reader.GetInt32(2);
            double lat = reader.GetDouble(3);
            double lon = reader.GetDouble(4);
            double? elevation = reader.IsDBNull(5) ? null : reader.GetDouble(5);

            try
            {
                results.Add(new MountainPoi(id, name, new GeoPoint(lat, lon), kind, elevation));
            }
            catch (ArgumentException)
            {
                // Skip rows that fail domain invariants (e.g. corrupted lat/lon).
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pois;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result ?? 0);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pois;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task UpsertSingleAsync(MountainPoi poi, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;

        command.Parameters.AddWithValue("$id", poi.Id);
        command.Parameters.AddWithValue("$name", poi.Name);
        command.Parameters.AddWithValue("$kind", (int)poi.Kind);
        command.Parameters.AddWithValue("$lat", poi.Position.Latitude);
        command.Parameters.AddWithValue("$lon", poi.Position.Longitude);
        command.Parameters.AddWithValue("$ele", (object?)poi.ElevationMeters ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}