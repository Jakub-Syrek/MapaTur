using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="Float32GeoTiffDecoder"/>. GUGiK's NMT WCS returns an
/// uncompressed, single-band, little-endian IEEE-float32 GeoTIFF (BitsPerSample=32,
/// SampleFormat=3, Compression=none) — so the decoder only has to read the IFD, find the strips
/// (tags 273/279) and reinterpret their bytes as <c>float</c> metres, row-major top-to-bottom.
/// These tests build minimal synthetic TIFFs (single- and multi-strip) and assert the decode, plus
/// rejection of the formats GUGiK never sends (big-endian, compressed, non-float32, truncated).
/// </summary>
public sealed class Float32GeoTiffDecoderTests
{
    private static readonly float[] Quad = { 1f, 2f, 3f, 4f };

    [Fact]
    public void Decode_SingleStrip_ReturnsDimensionsAndSamples()
    {
        var samples = new[] { 1f, 2f, 3f, 4f, 5f, 6f };
        byte[] tiff = TiffBuilder.BuildFloat32(width: 3, height: 2, samples, rowsPerStrip: 2);

        var grid = Float32GeoTiffDecoder.Decode(tiff);

        grid.Width.Should().Be(3);
        grid.Height.Should().Be(2);
        grid.Samples.Should().Equal(samples);
    }

    [Fact]
    public void Decode_ReadsLittleEndianFloatValues()
    {
        var samples = new[] { 68.47f, 68.48f, 1234.5f, -12.25f };
        byte[] tiff = TiffBuilder.BuildFloat32(width: 2, height: 2, samples, rowsPerStrip: 2);

        var grid = Float32GeoTiffDecoder.Decode(tiff);

        grid.Samples.Should().Equal(samples);
    }

    [Fact]
    public void Decode_MultiStrip_ConcatenatesStripsInRowOrder()
    {
        // 2 cols x 4 rows, split into strips of 1 row each (4 strips) — order must be preserved.
        var samples = new[] { 10f, 11f, 20f, 21f, 30f, 31f, 40f, 41f };
        byte[] tiff = TiffBuilder.BuildFloat32(width: 2, height: 4, samples, rowsPerStrip: 1);

        var grid = Float32GeoTiffDecoder.Decode(tiff);

        grid.Width.Should().Be(2);
        grid.Height.Should().Be(4);
        grid.Samples.Should().Equal(samples);
    }

    [Fact]
    public void Decode_SamplesAreRowMajor_FirstSampleIsTopLeft()
    {
        var samples = new[] { 100f, 200f, 300f, 400f };
        byte[] tiff = TiffBuilder.BuildFloat32(width: 2, height: 2, samples, rowsPerStrip: 1);

        var grid = Float32GeoTiffDecoder.Decode(tiff);

        grid.Samples[0].Should().Be(100f);
        grid.Samples[^1].Should().Be(400f);
    }

    [Fact]
    public void Decode_RejectsBigEndianTiff()
    {
        // "MM" byte order marker — GUGiK is always little-endian "II".
        byte[] bigEndian = { 0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08 };

        var act = () => Float32GeoTiffDecoder.Decode(bigEndian);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Decode_RejectsCompressedTiff()
    {
        byte[] tiff = TiffBuilder.BuildFloat32(2, 2, Quad, rowsPerStrip: 2, compression: 5);

        var act = () => Float32GeoTiffDecoder.Decode(tiff);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Decode_RejectsNon32BitSamples()
    {
        byte[] tiff = TiffBuilder.BuildFloat32(2, 2, Quad, rowsPerStrip: 2, bitsPerSample: 16);

        var act = () => Float32GeoTiffDecoder.Decode(tiff);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Decode_RejectsNonFloatSampleFormat()
    {
        byte[] tiff = TiffBuilder.BuildFloat32(2, 2, Quad, rowsPerStrip: 2, sampleFormat: 2);

        var act = () => Float32GeoTiffDecoder.Decode(tiff);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Decode_RejectsTruncatedInput()
    {
        byte[] truncated = { 0x49, 0x49 };

        var act = () => Float32GeoTiffDecoder.Decode(truncated);

        act.Should().Throw<FormatException>();
    }

    /// <summary>
    /// Minimal writer for an uncompressed, single-band float32 little-endian baseline TIFF — exactly
    /// the shape GUGiK's WCS returns. Layout: 8-byte header, contiguous strip pixel data, optional
    /// external strip-offset/byte-count arrays (when multi-strip), then the IFD.
    /// </summary>
    private static class TiffBuilder
    {
        public static byte[] BuildFloat32(
            int width,
            int height,
            float[] samples,
            int rowsPerStrip,
            int compression = 1,
            int bitsPerSample = 32,
            int sampleFormat = 3)
        {
            int nStrips = (height + rowsPerStrip - 1) / rowsPerStrip;
            int pixelBytes = width * height * 4;
            const int headerSize = 8;
            int dataStart = headerSize;

            // Strip offsets/counts within the contiguous pixel block.
            var stripOffsets = new int[nStrips];
            var stripCounts = new int[nStrips];
            int cursor = dataStart;
            for (int s = 0; s < nStrips; s++)
            {
                int rows = Math.Min(rowsPerStrip, height - (s * rowsPerStrip));
                stripCounts[s] = rows * width * 4;
                stripOffsets[s] = cursor;
                cursor += stripCounts[s];
            }

            int afterData = dataStart + pixelBytes;
            bool externalArrays = nStrips > 1;
            int stripOffsetsPos = externalArrays ? afterData : 0;
            int stripCountsPos = externalArrays ? afterData + (nStrips * 4) : 0;
            int ifdPos = externalArrays ? afterData + (nStrips * 8) : afterData;

            int ifdSize = 2 + (10 * 12) + 4;
            int totalSize = ifdPos + ifdSize;

            var buf = new byte[totalSize];

            // Header: "II", 42, IFD offset.
            buf[0] = 0x49;
            buf[1] = 0x49;
            WriteU16(buf, 2, 42);
            WriteU32(buf, 4, (uint)ifdPos);

            // Pixel data, row-major.
            for (int i = 0; i < samples.Length; i++)
            {
                WriteF32(buf, dataStart + (i * 4), samples[i]);
            }

            // External strip arrays (LONG) when multi-strip.
            if (externalArrays)
            {
                for (int s = 0; s < nStrips; s++)
                {
                    WriteU32(buf, stripOffsetsPos + (s * 4), (uint)stripOffsets[s]);
                    WriteU32(buf, stripCountsPos + (s * 4), (uint)stripCounts[s]);
                }
            }

            // IFD: 10 entries, ascending tag order.
            const ushort SHORT = 3;
            const ushort LONG = 4;
            int p = ifdPos;
            WriteU16(buf, p, 10);
            p += 2;
            p = WriteEntry(buf, p, 256, LONG, 1, (uint)width);
            p = WriteEntry(buf, p, 257, LONG, 1, (uint)height);
            p = WriteEntry(buf, p, 258, SHORT, 1, (uint)bitsPerSample);
            p = WriteEntry(buf, p, 259, SHORT, 1, (uint)compression);
            p = WriteEntry(buf, p, 262, SHORT, 1, 1);
            p = WriteEntry(buf, p, 273, LONG, (uint)nStrips, externalArrays ? (uint)stripOffsetsPos : (uint)stripOffsets[0]);
            p = WriteEntry(buf, p, 277, SHORT, 1, 1);
            p = WriteEntry(buf, p, 278, LONG, 1, (uint)rowsPerStrip);
            p = WriteEntry(buf, p, 279, LONG, (uint)nStrips, externalArrays ? (uint)stripCountsPos : (uint)stripCounts[0]);
            p = WriteEntry(buf, p, 339, SHORT, 1, (uint)sampleFormat);
            WriteU32(buf, p, 0); // next IFD = none

            return buf;
        }

        private static int WriteEntry(byte[] buf, int pos, ushort tag, ushort type, uint count, uint value)
        {
            WriteU16(buf, pos, tag);
            WriteU16(buf, pos + 2, type);
            WriteU32(buf, pos + 4, count);
            WriteU32(buf, pos + 8, value);
            return pos + 12;
        }

        private static void WriteU16(byte[] buf, int pos, ushort v)
        {
            buf[pos] = (byte)(v & 0xFF);
            buf[pos + 1] = (byte)((v >> 8) & 0xFF);
        }

        private static void WriteU32(byte[] buf, int pos, uint v)
        {
            buf[pos] = (byte)(v & 0xFF);
            buf[pos + 1] = (byte)((v >> 8) & 0xFF);
            buf[pos + 2] = (byte)((v >> 16) & 0xFF);
            buf[pos + 3] = (byte)((v >> 24) & 0xFF);
        }

        private static void WriteF32(byte[] buf, int pos, float v)
        {
            byte[] b = BitConverter.GetBytes(v); // little-endian on x64/arm64
            buf[pos] = b[0];
            buf[pos + 1] = b[1];
            buf[pos + 2] = b[2];
            buf[pos + 3] = b[3];
        }
    }
}