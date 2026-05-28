using System.Globalization;
using System.Text;
using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;
using Microsoft.Data.Sqlite;

namespace MapaTur.Infrastructure.Trails;

/// <summary>
/// SQLite-backed repository for hiking trails. Geometry is stored as a comma-separated
/// list of <c>lat,lon</c> pairs separated by semicolons; this avoids a SpatiaLite
/// dependency at the cost of having to scan all rows for bounding-box queries.
/// </summary>
public sealed class SqliteTrailRepository : ITrailRepository, IDisposable
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS trails (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            osmc_symbol TEXT,
            geometry TEXT NOT NULL,
            min_lat REAL NOT NULL,
            min_lon REAL NOT NULL,
            max_lat REAL NOT NULL,
            max_lon REAL NOT NULL
        );
        """;

    private const string UpsertSql = """
        INSERT INTO trails (id, name, osmc_symbol, geometry, min_lat, min_lon, max_lat, max_lon)
        VALUES ($id, $name, $osmc, $geom, $min_lat, $min_lon, $max_lat, $max_lon)
        ON CONFLICT(id) DO UPDATE SET
            name = excluded.name,
            osmc_symbol = excluded.osmc_symbol,
            geometry = excluded.geometry,
            min_lat = excluded.min_lat,
            min_lon = excluded.min_lon,
            max_lat = excluded.max_lat,
            max_lon = excluded.max_lon;
        """;

    private const string FindIntersectingSql = """
        SELECT id, name, osmc_symbol, geometry
        FROM trails
        WHERE NOT (max_lat < $south OR min_lat > $north OR max_lon < $west OR min_lon > $east);
        """;

    private readonly SqliteConnection connection;
    private readonly double simplificationEpsilonMeters;
    private bool disposed;

    /// <summary>
    /// Opens (or creates) a SQLite database at the given path and ensures the schema exists.
    /// </summary>
    /// <param name="databasePath">Absolute path to the SQLite database file.</param>
    /// <param name="simplificationEpsilonMeters">
    /// Douglas–Peucker simplification tolerance, in metres, applied at write time.
    /// Set to 0 (default) to disable. Production typically passes ~10 m so the
    /// stored geometry already matches typical client zoom levels — pays for itself
    /// many times over on every subsequent render.
    /// </param>
    public SqliteTrailRepository(string databasePath, double simplificationEpsilonMeters = 0.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (simplificationEpsilonMeters < 0.0 || !double.IsFinite(simplificationEpsilonMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(simplificationEpsilonMeters), simplificationEpsilonMeters, "Epsilon must be a finite non-negative value.");
        }

        this.simplificationEpsilonMeters = simplificationEpsilonMeters;

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
    public async Task UpsertAsync(IEnumerable<Trail> trails, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trails);
        ThrowIfDisposed();

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var trail in trails)
        {
            await UpsertSingleAsync(trail, transaction, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Trail>> FindIntersectingAsync(MapBounds bounds, CancellationToken cancellationToken = default)
        => FindIntersectingAsync(bounds, 0.0, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Trail>> FindIntersectingAsync(MapBounds bounds, double simplificationEpsilonMeters, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (simplificationEpsilonMeters < 0.0 || !double.IsFinite(simplificationEpsilonMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(simplificationEpsilonMeters), simplificationEpsilonMeters, "Epsilon must be a finite non-negative value.");
        }

        await using var command = connection.CreateCommand();
        command.CommandText = FindIntersectingSql;
        command.Parameters.AddWithValue("$south", bounds.SouthWest.Latitude);
        command.Parameters.AddWithValue("$west", bounds.SouthWest.Longitude);
        command.Parameters.AddWithValue("$north", bounds.NorthEast.Latitude);
        command.Parameters.AddWithValue("$east", bounds.NorthEast.Longitude);

        var trails = new List<Trail>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long id = reader.GetInt64(0);
            string name = reader.GetString(1);
            string? osmc = reader.IsDBNull(2) ? null : reader.GetString(2);
            string geometryString = reader.GetString(3);

            var geometry = DeserializeGeometry(geometryString);
            if (simplificationEpsilonMeters > 0.0 && geometry.Count > 2)
            {
                geometry = TrailGeometrySimplifier.Simplify(geometry, simplificationEpsilonMeters);
            }

            if (geometry.Count >= 2)
            {
                var markings = new List<TrailMarking> { OsmcSymbolParser.Parse(osmc) };
                trails.Add(new Trail(id, name, markings, geometry));
            }
        }

        return trails;
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

    private async Task UpsertSingleAsync(Trail trail, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;

        string osmcRaw = trail.Markings.FirstOrDefault()?.OsmcRaw ?? string.Empty;
        var geometry = simplificationEpsilonMeters > 0.0
            ? TrailGeometrySimplifier.Simplify(trail.Geometry, simplificationEpsilonMeters)
            : trail.Geometry;
        var (minLat, minLon, maxLat, maxLon) = ComputeBounds(geometry);

        command.Parameters.AddWithValue("$id", trail.Id);
        command.Parameters.AddWithValue("$name", trail.Name);
        command.Parameters.AddWithValue("$osmc", string.IsNullOrEmpty(osmcRaw) ? DBNull.Value : osmcRaw);
        command.Parameters.AddWithValue("$geom", SerializeGeometry(geometry));
        command.Parameters.AddWithValue("$min_lat", minLat);
        command.Parameters.AddWithValue("$min_lon", minLon);
        command.Parameters.AddWithValue("$max_lat", maxLat);
        command.Parameters.AddWithValue("$max_lon", maxLon);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static (double MinLat, double MinLon, double MaxLat, double MaxLon) ComputeBounds(IReadOnlyList<GeoPoint> points)
    {
        double minLat = points[0].Latitude;
        double maxLat = minLat;
        double minLon = points[0].Longitude;
        double maxLon = minLon;

        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].Latitude < minLat) minLat = points[i].Latitude;
            if (points[i].Latitude > maxLat) maxLat = points[i].Latitude;
            if (points[i].Longitude < minLon) minLon = points[i].Longitude;
            if (points[i].Longitude > maxLon) maxLon = points[i].Longitude;
        }

        return (minLat, minLon, maxLat, maxLon);
    }

    private static string SerializeGeometry(IReadOnlyList<GeoPoint> points)
    {
        var builder = new StringBuilder(points.Count * 24);
        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0) builder.Append(';');
            builder.Append(points[i].Latitude.ToString("R", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(points[i].Longitude.ToString("R", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static IReadOnlyList<GeoPoint> DeserializeGeometry(string serialized)
    {
        string[] tokens = serialized.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var points = new List<GeoPoint>(tokens.Length);

        foreach (string token in tokens)
        {
            int comma = token.IndexOf(',', StringComparison.Ordinal);
            if (comma < 0) continue;

            if (!double.TryParse(token.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                || !double.TryParse(token.AsSpan(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                continue;
            }

            try
            {
                points.Add(new GeoPoint(lat, lon));
            }
            catch (ArgumentOutOfRangeException)
            {
                // Skip corrupt entries.
            }
        }

        return points;
    }
}