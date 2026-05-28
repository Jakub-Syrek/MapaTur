using System.Buffers.Binary;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Infrastructure.Terrain;

/// <summary>
/// Reads a <see cref="DemRaster"/> from the .dem binary container produced by
/// <c>testdata/maps/generate-tatry-dem.py</c>.
///
/// Binary layout (little-endian, 64-byte header followed by row-major Float32 samples):
/// magic "DEM1" (4) · version int32 (4) · cols int32 (4) · rows int32 (4) ·
/// west double (8) · south double (8) · east double (8) · north double (8) ·
/// nodata float (4) · 12 reserved bytes · cols×rows × float32.
/// </summary>
public static class DemRasterReader
{
    private const int HeaderBytes = 64;
    private const int FormatVersion = 1;
    private static readonly byte[] ExpectedMagic = "DEM1"u8.ToArray();

    /// <summary>
    /// Reads a DEM raster from the given file path.
    /// </summary>
    /// <param name="path">Absolute path to a .dem file.</param>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when the file is malformed or its version is unsupported.</exception>
    public static DemRaster Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("DEM file not found.", path);
        }

        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[HeaderBytes];
        int read = stream.Read(header);
        if (read != HeaderBytes)
        {
            throw new InvalidDataException($"DEM file is shorter than {HeaderBytes}-byte header.");
        }

        for (int i = 0; i < ExpectedMagic.Length; i++)
        {
            if (header[i] != ExpectedMagic[i])
            {
                throw new InvalidDataException("DEM file magic does not match \"DEM1\".");
            }
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"DEM file version {version} is not supported (expected {FormatVersion}).");
        }

        int cols = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        int rows = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        double west = BinaryPrimitives.ReadDoubleLittleEndian(header[16..]);
        double south = BinaryPrimitives.ReadDoubleLittleEndian(header[24..]);
        double east = BinaryPrimitives.ReadDoubleLittleEndian(header[32..]);
        double north = BinaryPrimitives.ReadDoubleLittleEndian(header[40..]);
        float nodata = BinaryPrimitives.ReadSingleLittleEndian(header[48..]);

        if (cols < 2 || rows < 2)
        {
            throw new InvalidDataException($"DEM file has invalid dimensions ({cols}x{rows}).");
        }

        long sampleCount = (long)cols * rows;
        long dataBytes = sampleCount * sizeof(float);
        long expectedSize = HeaderBytes + dataBytes;
        if (stream.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"DEM file size mismatch: expected {expectedSize} bytes for {cols}x{rows} grid, got {stream.Length}.");
        }

        float[] samples = new float[sampleCount];
        byte[] buffer = new byte[dataBytes];
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int got = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (got == 0)
            {
                throw new InvalidDataException("Unexpected end of DEM file while reading samples.");
            }

            totalRead += got;
        }

        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(i * sizeof(float), sizeof(float)));
        }

        var bounds = new MapBounds(
            new GeoPoint(south, west),
            new GeoPoint(north, east));
        return new DemRaster(cols, rows, bounds, samples, nodata);
    }
}