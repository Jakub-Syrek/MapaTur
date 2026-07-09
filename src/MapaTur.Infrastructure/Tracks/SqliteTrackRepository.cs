using System.Globalization;
using System.Text;

using MapaTur.Application.Tracks;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Tracks;

using Microsoft.Data.Sqlite;

namespace MapaTur.Infrastructure.Tracks;

/// <summary>
/// SQLite-backed repository for user-imported off-trail tracks. Geometry is stored as a
/// semicolon-separated list of <c>lat,lon</c> or <c>lat,lon,ele</c> tuples (elevation omitted when
/// unknown), mirroring <see cref="MapaTur.Infrastructure.Trails.SqliteTrailRepository"/>. Rows are
/// returned in insertion order (SQLite <c>rowid</c>).
/// </summary>
public sealed class SqliteTrackRepository : ITrackRepository, IDisposable
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS tracks (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            geometry TEXT NOT NULL,
            created_utc TEXT NOT NULL
        );
        """;

    private const string UpsertSql = """
        INSERT INTO tracks (id, name, geometry, created_utc)
        VALUES ($id, $name, $geom, $created)
        ON CONFLICT(id) DO UPDATE SET
            name = excluded.name,
            geometry = excluded.geometry,
            created_utc = excluded.created_utc;
        """;

    private const string SelectAllSql = "SELECT id, name, geometry FROM tracks ORDER BY rowid;";

    private readonly SqliteConnection connection;
    private bool disposed;

    /// <summary>
    /// Opens (or creates) a SQLite database at the given path and ensures the schema exists.
    /// </summary>
    /// <param name="databasePath">Absolute path to the SQLite database file.</param>
    public SqliteTrackRepository(string databasePath)
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
    public async Task AddAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        ThrowIfDisposed();

        await using var command = connection.CreateCommand();
        command.CommandText = UpsertSql;
        command.Parameters.AddWithValue("$id", track.Id.ToString("N"));
        command.Parameters.AddWithValue("$name", track.Name);
        command.Parameters.AddWithValue("$geom", SerializeGeometry(track.Points));
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Track>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await using var command = connection.CreateCommand();
        command.CommandText = SelectAllSql;

        var tracks = new List<Track>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid id))
            {
                continue;
            }

            string name = reader.GetString(1);
            var points = DeserializeGeometry(reader.GetString(2));
            if (points.Count >= 1)
            {
                tracks.Add(new Track(id, name, points));
            }
        }

        return tracks;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM tracks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM tracks;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result ?? 0, CultureInfo.InvariantCulture);
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private static string SerializeGeometry(IReadOnlyList<TrackPoint> points)
    {
        var builder = new StringBuilder(points.Count * 28);
        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(';');
            }

            GeoPoint position = points[i].Position;
            builder.Append(position.Latitude.ToString("R", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(position.Longitude.ToString("R", CultureInfo.InvariantCulture));
            if (position.ElevationMeters is { } elevation)
            {
                builder.Append(',');
                builder.Append(elevation.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<TrackPoint> DeserializeGeometry(string serialized)
    {
        string[] tokens = serialized.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var points = new List<TrackPoint>(tokens.Length);

        foreach (string token in tokens)
        {
            string[] parts = token.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                continue;
            }

            double? elevation = parts.Length >= 3
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double ele)
                ? ele
                : null;

            try
            {
                points.Add(new TrackPoint(new GeoPoint(lat, lon, elevation), DateTimeOffset.UnixEpoch));
            }
            catch (ArgumentOutOfRangeException)
            {
                // Skip corrupt entries.
            }
        }

        return points;
    }
}