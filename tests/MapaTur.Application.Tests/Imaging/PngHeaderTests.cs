using FluentAssertions;

using MapaTur.Application.Imaging;

using Xunit;

namespace MapaTur.Application.Tests.Imaging;

/// <summary>
/// Minimal PNG IHDR sniffer used by the ortho tile-set discovery to compare candidate sets by their real
/// pixel resolution (a lower-res package copy in <c>maps/</c> must not shadow the full-res set in <c>dem/</c>
/// just because its root is probed first). Only the first 24 bytes are ever read — signature (8) + IHDR
/// chunk length/type (8) + big-endian width/height (8).
/// </summary>
public sealed class PngHeaderTests
{
    private static byte[] Header(uint width, uint height, bool corruptSignature = false, bool corruptChunk = false)
    {
        var b = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        if (corruptSignature)
        {
            b[1] = (byte)'X';
        }

        // IHDR chunk: length (13, big-endian) + "IHDR"
        b[11] = 13;
        b[12] = (byte)'I'; b[13] = (byte)'H'; b[14] = (byte)'D'; b[15] = (byte)'R';
        if (corruptChunk)
        {
            b[12] = (byte)'J';
        }

        b[16] = (byte)(width >> 24); b[17] = (byte)(width >> 16); b[18] = (byte)(width >> 8); b[19] = (byte)width;
        b[20] = (byte)(height >> 24); b[21] = (byte)(height >> 16); b[22] = (byte)(height >> 8); b[23] = (byte)height;
        return b;
    }

    [Fact]
    public void TryReadDimensions_ValidHeader_ReturnsWidthAndHeight()
    {
        PngHeader.TryReadDimensions(Header(16384, 10923), out int w, out int h).Should().BeTrue();

        w.Should().Be(16384);
        h.Should().Be(10923);
    }

    [Fact]
    public void TryReadDimensions_WrongSignature_Fails()
    {
        PngHeader.TryReadDimensions(Header(100, 100, corruptSignature: true), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadDimensions_NotIhdrChunk_Fails()
    {
        PngHeader.TryReadDimensions(Header(100, 100, corruptChunk: true), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadDimensions_TooShort_Fails()
    {
        PngHeader.TryReadDimensions(new byte[16], out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadDimensions_ZeroDimension_Fails()
    {
        PngHeader.TryReadDimensions(Header(0, 100), out _, out _).Should().BeFalse();
    }
}