using System.Buffers.Binary;

namespace MapaTur.Application.Terrain;

/// <summary>Decoded single-band float32 raster: <paramref name="Samples"/> is row-major, top-to-bottom.</summary>
public sealed record Float32Grid(int Width, int Height, float[] Samples);

/// <summary>
/// Minimal reader for the exact GeoTIFF shape GUGiK's NMT WCS returns: an uncompressed, single-band,
/// little-endian IEEE-float32 baseline TIFF. It parses only the IFD tags it needs (dimensions, strip
/// tables, and the sanity tags) and reinterprets the strip bytes as <c>float</c> metres — no
/// compression, tiling, predictors or external libraries. Anything outside that shape (big-endian,
/// compressed, non-float32, multi-band, truncated) is rejected with a <see cref="FormatException"/> so
/// the calling tile source can treat it as "no data" and fall through.
/// </summary>
public static class Float32GeoTiffDecoder
{
    private const ushort TypeShort = 3;
    private const ushort TypeLong = 4;

    /// <summary>Decodes the TIFF bytes into a <see cref="Float32Grid"/>.</summary>
    /// <exception cref="FormatException">The bytes are not the supported uncompressed float32 TIFF shape.</exception>
    public static Float32Grid Decode(byte[] tiff)
    {
        ArgumentNullException.ThrowIfNull(tiff);
        if (tiff.Length < 8)
        {
            throw new FormatException("TIFF too short for an 8-byte header.");
        }

        if (tiff[0] != 0x49 || tiff[1] != 0x49)
        {
            throw new FormatException("Only little-endian ('II') TIFF is supported.");
        }

        if (ReadU16(tiff, 2) != 42)
        {
            throw new FormatException("Not a TIFF (bad magic number).");
        }

        uint ifdOffset = ReadU32(tiff, 4);
        if (ifdOffset + 2 > (uint)tiff.Length)
        {
            throw new FormatException("IFD offset is out of range.");
        }

        int entryCount = ReadU16(tiff, (int)ifdOffset);
        int entriesStart = (int)ifdOffset + 2;
        if (entriesStart + (entryCount * 12) > tiff.Length)
        {
            throw new FormatException("IFD entry table is truncated.");
        }

        int? width = null;
        int? height = null;
        int? compression = null;
        int? bitsPerSample = null;
        int? samplesPerPixel = null;
        int? sampleFormat = null;
        uint[] stripOffsets = Array.Empty<uint>();
        uint[] stripByteCounts = Array.Empty<uint>();

        for (int i = 0; i < entryCount; i++)
        {
            int e = entriesStart + (i * 12);
            ushort tag = ReadU16(tiff, e);
            ushort type = ReadU16(tiff, e + 2);
            uint count = ReadU32(tiff, e + 4);
            int valuePos = e + 8;

            switch (tag)
            {
                case 256: width = (int)ReadScalar(tiff, type, valuePos); break;
                case 257: height = (int)ReadScalar(tiff, type, valuePos); break;
                case 258: bitsPerSample = (int)ReadScalar(tiff, type, valuePos); break;
                case 259: compression = (int)ReadScalar(tiff, type, valuePos); break;
                case 277: samplesPerPixel = (int)ReadScalar(tiff, type, valuePos); break;
                case 339: sampleFormat = (int)ReadScalar(tiff, type, valuePos); break;
                case 273: stripOffsets = ReadLongArray(tiff, type, count, valuePos); break;
                case 279: stripByteCounts = ReadLongArray(tiff, type, count, valuePos); break;
                default: break;
            }
        }

        if (width is null or <= 0 || height is null or <= 0)
        {
            throw new FormatException("Missing or invalid image dimensions.");
        }

        if (compression is not null and not 1)
        {
            throw new FormatException("Only uncompressed TIFF is supported.");
        }

        if (bitsPerSample is not null and not 32)
        {
            throw new FormatException("Only 32-bit samples are supported.");
        }

        if (samplesPerPixel is not null and not 1)
        {
            throw new FormatException("Only single-band rasters are supported.");
        }

        if (sampleFormat is not null and not 3)
        {
            throw new FormatException("Only IEEE-float samples are supported.");
        }

        if (stripOffsets.Length == 0 || stripOffsets.Length != stripByteCounts.Length)
        {
            throw new FormatException("Missing or inconsistent strip tables.");
        }

        int total = width.Value * height.Value;
        var samples = new float[total];
        int written = 0;

        for (int s = 0; s < stripOffsets.Length; s++)
        {
            int offset = (int)stripOffsets[s];
            int byteCount = (int)stripByteCounts[s];
            if (offset < 0 || byteCount < 0 || offset + byteCount > tiff.Length)
            {
                throw new FormatException("Strip data is out of range.");
            }

            int floats = byteCount / 4;
            if (written + floats > total)
            {
                throw new FormatException("Strip data exceeds the declared image size.");
            }

            for (int f = 0; f < floats; f++)
            {
                samples[written++] = BinaryPrimitives.ReadSingleLittleEndian(tiff.AsSpan(offset + (f * 4)));
            }
        }

        if (written != total)
        {
            throw new FormatException("Strip data does not fill the declared image size.");
        }

        return new Float32Grid(width.Value, height.Value, samples);
    }

    private static long ReadScalar(byte[] b, ushort type, int valuePos) => type switch
    {
        TypeShort => ReadU16(b, valuePos),
        TypeLong => ReadU32(b, valuePos),
        _ => throw new FormatException($"Unsupported scalar tag type {type}."),
    };

    private static uint[] ReadLongArray(byte[] b, ushort type, uint count, int valuePos)
    {
        int typeSize = type switch
        {
            TypeShort => 2,
            TypeLong => 4,
            _ => throw new FormatException($"Unsupported array tag type {type}."),
        };

        // Values up to 4 bytes are stored inline; larger arrays live at the offset in the value field.
        int dataPos = (typeSize * (int)count) <= 4 ? valuePos : (int)ReadU32(b, valuePos);
        if (dataPos < 0 || dataPos + (typeSize * (long)count) > b.Length)
        {
            throw new FormatException("Strip table is out of range.");
        }

        var values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            int p = dataPos + (i * typeSize);
            values[i] = type == TypeShort ? ReadU16(b, p) : ReadU32(b, p);
        }

        return values;
    }

    private static ushort ReadU16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p));

    private static uint ReadU32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p));
}