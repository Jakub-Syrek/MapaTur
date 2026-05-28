using System.Globalization;
using MapaTur.Application.Maps;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Maps;
using Microsoft.Data.Sqlite;

namespace MapaTur.Infrastructure.Maps.MBTiles;

/// <summary>
/// MBTiles-backed tile source. MBTiles is a SQLite container described at
/// https://github.com/mapbox/mbtiles-spec. Tile rows are stored in TMS scheme;
/// this class translates between TMS and XYZ on read.
/// </summary>
public sealed class MBTilesTileSource : ITileSource
{
    private const string SelectTileCommand =
        "SELECT tile_data FROM tiles WHERE zoom_level = $z AND tile_column = $x AND tile_row = $y LIMIT 1;";

    private readonly SqliteConnection connection;
    private readonly TileSourceMetadata metadata;
    private bool disposed;

    /// <summary>
    /// Initializes the source by opening the given MBTiles file in read-only mode and loading metadata.
    /// </summary>
    /// <param name="archivePath">Absolute path to the .mbtiles file.</param>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when the file is not a valid MBTiles archive.</exception>
    public MBTilesTileSource(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("MBTiles file not found.", archivePath);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = archivePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        connection = new SqliteConnection(connectionString);
        connection.Open();
        metadata = ReadMetadata(connection);
    }

    /// <inheritdoc />
    public TileSourceMetadata GetMetadata()
    {
        ThrowIfDisposed();
        return metadata;
    }

    /// <inheritdoc />
    public async Task<byte[]?> GetTileAsync(TileCoordinate coordinate, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await using var command = connection.CreateCommand();
        command.CommandText = SelectTileCommand;
        command.Parameters.AddWithValue("$z", coordinate.ZoomLevel);
        command.Parameters.AddWithValue("$x", coordinate.Column);
        command.Parameters.AddWithValue("$y", coordinate.ToTmsRow());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0);
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static TileSourceMetadata ReadMetadata(SqliteConnection connection)
    {
        var raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name, value FROM metadata;";
            try
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    raw[reader.GetString(0)] = reader.GetString(1);
                }
            }
            catch (SqliteException ex)
            {
                throw new InvalidDataException("MBTiles archive is missing the metadata table.", ex);
            }
        }

        string name = raw.GetValueOrDefault("name") ?? "Unnamed";
        TileFormat format = ParseFormat(raw.GetValueOrDefault("format"));
        int minZoom = ParseInt(raw.GetValueOrDefault("minzoom"), defaultValue: 0);
        int maxZoom = ParseInt(raw.GetValueOrDefault("maxzoom"), defaultValue: TileCoordinate.MaxZoomLevel);
        MapBounds? bounds = ParseBounds(raw.GetValueOrDefault("bounds"));
        string? attribution = raw.GetValueOrDefault("attribution");

        return new TileSourceMetadata(name, format, minZoom, maxZoom, bounds, attribution);
    }

    private static TileFormat ParseFormat(string? value) => value?.ToLowerInvariant() switch
    {
        "png" => TileFormat.Png,
        "jpg" or "jpeg" => TileFormat.Jpeg,
        "webp" => TileFormat.WebP,
        _ => TileFormat.Unknown,
    };

    private static int ParseInt(string? value, int defaultValue)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : defaultValue;
    }

    private static MapBounds? ParseBounds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double west)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double south)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double east)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double north))
        {
            return null;
        }

        return new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
    }
}