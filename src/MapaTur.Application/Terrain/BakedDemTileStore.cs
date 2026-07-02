using System.Text;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Binary reader/writer and on-disk layout for the offline baked-DEM pyramid (<see cref="BakedDemTile"/>).
/// The format is a tiny fixed header (magic + version, then zoom/x/y, columns/rows, the four bound edges
/// and the NoData sentinel) followed by the row-major <c>float32</c> height grid — little-endian, no
/// compression, so a streamed read is a header parse plus one bulk copy of the heights. Tiles live at
/// <c>baked/{zoom}/{x}/{y}.bdt</c> (see <see cref="RelativePathFor"/>), the same zoom/x/y nesting as the
/// raw GUGiK cache, so a tile is addressable straight from its <see cref="DemTileKey"/>.
/// </summary>
public static class BakedDemTileStore
{
    /// <summary>File extension (with leading dot) used for a baked DEM tile.</summary>
    public const string FileExtension = ".bdt";

    // "BDT1" — the original format (fixed header + float32 height grid). Still READ for the existing on-disk cache.
    private static readonly byte[] Magic = "BDT1"u8.ToArray();

    // "BDT2" — identical header + heights as BDT1, then an optional detail trailer (a per-cell mid-frequency
    // relief RMS grid). Written by new bakes; BDT1 files still load (as no-detail), so old caches keep working.
    private static readonly byte[] Magic2 = "BDT2"u8.ToArray();

    // Detail trailer kinds (the byte immediately after the height grid in a BDT2 record).
    private const byte DetailNone = 0;          // no detail channel follows
    private const byte DetailPerCellRms = 1;    // Columns*Rows float32 per-cell RMS metres follow

    /// <summary>
    /// Returns the relative path (forward components joined by the platform separator) at which the tile for
    /// <paramref name="key"/> lives under a baked-cache root: <c>{zoom}/{x}/{y}.bdt</c>.
    /// </summary>
    /// <param name="key">Slippy address of the tile.</param>
    public static string RelativePathFor(DemTileKey key) => Path.Combine(
        key.Zoom.ToString(System.Globalization.CultureInfo.InvariantCulture),
        key.X.ToString(System.Globalization.CultureInfo.InvariantCulture),
        key.Y.ToString(System.Globalization.CultureInfo.InvariantCulture) + FileExtension);

    /// <summary>Writes <paramref name="tile"/> to <paramref name="stream"/> in the baked-tile binary format.</summary>
    /// <param name="stream">Destination stream (left open; the caller owns it).</param>
    /// <param name="tile">The baked tile to serialize.</param>
    /// <exception cref="ArgumentNullException">A parameter is null.</exception>
    public static void Write(Stream stream, BakedDemTile tile)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tile);

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic2);
        writer.Write(tile.Zoom);
        writer.Write(tile.TileX);
        writer.Write(tile.TileY);
        writer.Write(tile.Columns);
        writer.Write(tile.Rows);
        writer.Write(tile.Bounds.SouthWest.Latitude);
        writer.Write(tile.Bounds.SouthWest.Longitude);
        writer.Write(tile.Bounds.NorthEast.Latitude);
        writer.Write(tile.Bounds.NorthEast.Longitude);
        writer.Write(tile.NoDataValue);

        float[] heights = tile.Heights;
        for (int i = 0; i < heights.Length; i++)
        {
            writer.Write(heights[i]);
        }

        // Detail trailer: a per-cell mid-frequency relief RMS grid, or a single "none" byte. The height grid
        // length is implied by Columns×Rows already written, so the trailer needs no length framing.
        float[]? detail = tile.DetailRms;
        if (detail is null)
        {
            writer.Write(DetailNone);
        }
        else
        {
            writer.Write(DetailPerCellRms);
            for (int i = 0; i < detail.Length; i++)
            {
                writer.Write(detail[i]);
            }
        }
    }

    /// <summary>Reads a baked tile from <paramref name="stream"/>.</summary>
    /// <param name="stream">Source stream positioned at the start of a baked-tile record (left open).</param>
    /// <returns>The deserialized tile.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="InvalidDataException">The magic header is missing or the record is malformed.</exception>
    public static BakedDemTile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        byte[] magic = reader.ReadBytes(Magic.Length);
        bool isV2 = magic.Length == Magic2.Length && magic.AsSpan().SequenceEqual(Magic2);
        bool isV1 = magic.Length == Magic.Length && magic.AsSpan().SequenceEqual(Magic);
        if (!isV1 && !isV2)
        {
            throw new InvalidDataException("Stream is not a baked DEM tile (bad magic header).");
        }

        int zoom = reader.ReadInt32();
        int tileX = reader.ReadInt32();
        int tileY = reader.ReadInt32();
        int columns = reader.ReadInt32();
        int rows = reader.ReadInt32();
        if (columns < 1 || rows < 1)
        {
            throw new InvalidDataException($"Baked tile has invalid dimensions ({columns}x{rows}).");
        }

        double swLat = reader.ReadDouble();
        double swLon = reader.ReadDouble();
        double neLat = reader.ReadDouble();
        double neLon = reader.ReadDouble();
        double noData = reader.ReadDouble();

        long count = (long)columns * rows;
        var heights = new float[count];
        for (long i = 0; i < count; i++)
        {
            heights[i] = reader.ReadSingle();
        }

        // Detail trailer (BDT2 only). BDT1 has no trailer → no detail. A BDT2 record that is truncated at/after
        // the heights (e.g. an older/interrupted write) degrades to no-detail rather than throwing.
        float[]? detailRms = null;
        if (isV2 && TryReadByte(reader, out byte kind) && kind == DetailPerCellRms)
        {
            var detail = new float[count];
            bool complete = true;
            for (long i = 0; i < count; i++)
            {
                if (!TryReadSingle(reader, out detail[i]))
                {
                    complete = false;
                    break;
                }
            }

            detailRms = complete ? detail : null;
        }

        var bounds = new MapBounds(new GeoPoint(swLat, swLon), new GeoPoint(neLat, neLon));
        return new BakedDemTile(zoom, tileX, tileY, columns, rows, bounds, noData, heights, detailRms);
    }

    private static bool TryReadByte(BinaryReader reader, out byte value)
    {
        try
        {
            value = reader.ReadByte();
            return true;
        }
        catch (EndOfStreamException)
        {
            value = 0;
            return false;
        }
    }

    private static bool TryReadSingle(BinaryReader reader, out float value)
    {
        try
        {
            value = reader.ReadSingle();
            return true;
        }
        catch (EndOfStreamException)
        {
            value = 0f;
            return false;
        }
    }
}