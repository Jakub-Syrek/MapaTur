using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Pakiet stron `.opk` (ARCHITEKTURA-STREAMING §2.2): jednostka pliku = cela, jednostka streamingu =
/// strona 512 px BC1 (mipy 0-1) + jedna strona TAIL (poziomy 2048↓). Kontrakt pod testem: nagłówek+TOC
/// wg spec, tail FIZYCZNIE PIERWSZY w payloadzie (sekwencyjny odczyt tail-first), zapis atomowy, odczyt
/// walidowany (magic/version/px/crc per strona) — plik rozdarty/obcy/przekłamany jest ODRZUCANY, nigdy
/// częściowo użyty. Strony bez pokrycia po prostu nie występują w TOC.
/// </summary>
public sealed class OrthoPagePackTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "opk-tests-" + Guid.NewGuid().ToString("N"));

    public OrthoPagePackTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private string PackPath => Path.Combine(dir, "12_34.opk");

    private static OrthoPagePack.PageData Page(ushort pageId, byte level, int bytes, byte fill)
    {
        byte[] payload = new byte[bytes];
        Array.Fill(payload, fill);
        return new OrthoPagePack.PageData(pageId, level, payload, SrcHash: 0xABCD1234u + pageId);
    }

    [Fact]
    public void WriteThenRead_RoundTripsPagesByteExact()
    {
        var pages = new[]
        {
            Page(OrthoPagePack.TailPageId, 2, 2048, 0x11), // tail: poziomy 2048↓
            Page(0, 0, 1024, 0x22),                        // kafel (0,0) mip 0
            Page(17, 0, 1024, 0x33),                       // kafel (1,1) mip 0
        };

        OrthoPagePack.Write(PackPath, cellPx: 8192, pages);
        using OrthoPagePack pack = OrthoPagePack.Open(PackPath, expectedCellPx: 8192)!;

        pack.PageCount.Should().Be(3);
        pack.TryReadPage(17, out byte[] got).Should().BeTrue();
        got.Should().AllBeEquivalentTo((byte)0x33);
        got.Length.Should().Be(1024);
    }

    [Fact]
    public void Open_MissingFile_ReturnsNull()
    {
        OrthoPagePack.Open(Path.Combine(dir, "absent.opk"), 8192).Should().BeNull();
    }

    [Fact]
    public void Open_WrongCellPx_IsRejected()
    {
        OrthoPagePack.Write(PackPath, cellPx: 8192, new[] { Page(0, 0, 64, 0x01) });

        OrthoPagePack.Open(PackPath, expectedCellPx: 4096).Should().BeNull(
            "cela wypieczona w innej rozdzielczości musi być odrzucona, nie użyta częściowo");
    }

    [Fact]
    public void Open_TruncatedPayload_IsRejectedAtOpen()
    {
        OrthoPagePack.Write(PackPath, cellPx: 8192, new[] { Page(0, 0, 4096, 0x5A) });
        using (FileStream fs = File.OpenWrite(PackPath))
        {
            fs.SetLength(fs.Length - 7); // rozdarty zapis / pełny dysk
        }

        OrthoPagePack.Open(PackPath, 8192).Should().BeNull();
    }

    [Fact]
    public void TryReadPage_CorruptedBytes_FailsCrc()
    {
        OrthoPagePack.Write(PackPath, cellPx: 8192, new[] { Page(3, 0, 4096, 0x77) });
        using (FileStream fs = new(PackPath, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(-100, SeekOrigin.End); // przekłam bajt wewnątrz payloadu strony
            int b = fs.ReadByte();
            fs.Seek(-1, SeekOrigin.Current);
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        using OrthoPagePack pack = OrthoPagePack.Open(PackPath, 8192)!;
        pack.Should().NotBeNull("nagłówek i TOC są nienaruszone — dopiero odczyt strony waliduje jej crc");
        pack.TryReadPage(3, out _).Should().BeFalse("strona z błędnym crc nie może trafić do GPU");
    }

    [Fact]
    public void Write_TailIsFirstInPayload()
    {
        var pages = new[]
        {
            Page(5, 0, 512, 0x44),
            Page(OrthoPagePack.TailPageId, 2, 1024, 0x55),
            Page(9, 0, 512, 0x66),
        };

        OrthoPagePack.Write(PackPath, cellPx: 4096, pages);
        using OrthoPagePack pack = OrthoPagePack.Open(PackPath, 4096)!;

        // Tail-first na dysku: sekwencyjny odczyt nagłówek→tail daje jakość 0.2 m bez seeków.
        pack.Entries.Should().HaveCount(3);
        long tailOffset = pack.Entries.Single(e => e.PageId == OrthoPagePack.TailPageId).Offset;
        pack.Entries.Where(e => e.PageId != OrthoPagePack.TailPageId)
            .All(e => e.Offset > tailOffset).Should().BeTrue();
    }

    [Fact]
    public void TryReadPage_AbsentPage_IsFalse_WithoutThrowing()
    {
        OrthoPagePack.Write(PackPath, cellPx: 8192, new[] { Page(0, 0, 64, 0x01) });
        using OrthoPagePack pack = OrthoPagePack.Open(PackPath, 8192)!;

        pack.TryReadPage(200, out _).Should().BeFalse("strona bez pokrycia po prostu nie występuje w TOC");
    }

    [Fact]
    public void Write_LeavesNoTempFileBehind()
    {
        OrthoPagePack.Write(PackPath, cellPx: 8192, new[] { Page(0, 0, 64, 0x01) });

        Directory.GetFiles(dir).Should().ContainSingle(f => f.EndsWith(".opk", StringComparison.Ordinal));
    }

    [Fact]
    public void SrcHashes_SurviveRoundTrip_ForIncrementalBake()
    {
        OrthoPagePack.Write(PackPath, cellPx: 8192, new[] { Page(7, 1, 64, 0x02) });
        using OrthoPagePack pack = OrthoPagePack.Open(PackPath, 8192)!;

        pack.Entries.Single().SrcHash.Should().Be(0xABCD1234u + 7,
            "przyrostowy bake porównuje hash źródła per strona (ARCHITEKTURA §8)");
    }
}
