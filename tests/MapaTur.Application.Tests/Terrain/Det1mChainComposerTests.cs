using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of the det1m cell chain composer — the reader-side of the `.opk` contract that the
/// compact-tail migration broke silently (2026-08-27: packs carry an L2↓ tail of 699 064 B while the
/// old renderer loop insisted on the full L1↓ tail of 2 796 216 B → tailOk=false for 87/87 cells →
/// the det1m layer never loaded, with no log). The composer mirrors OrthoPageWindowAssembler's layout
/// law: page payload = BC1 mip0 (512 px) + mip1 (256 px) sequentially; cell level 1 is TILED from the
/// pages' own mip1 parts; the tail starts at the level its pack entry declares (L2 compact or legacy L1).
/// Tests build real little packs through OrthoPagePack.Write at cellPx = 1024 (2×2 pages) so the whole
/// file contract is exercised, not a mock.
/// </summary>
public sealed class Det1mChainComposerTests : IDisposable
{
    private const int CellPx = 1024;                       // 2×2 stron 512 px — najmniejsza pelna cela
    private const int PagesPerEdge = CellPx / 512;
    private static readonly int Mip0Bytes = Bc1Encoder.EncodedSize(512, 512);
    private static readonly int Mip1Bytes = Bc1Encoder.EncodedSize(256, 256);

    private readonly string dir = Directory.CreateTempSubdirectory("det1m-composer-").FullName;

    public void Dispose() => Directory.Delete(this.dir, recursive: true);

    private static int TailBytes(int firstLevel)
    {
        int total = 0;
        for (int px = CellPx >> firstLevel; px >= 1; px /= 2)
        {
            total += Bc1Encoder.EncodedSize(px, px);
        }

        return total;
    }

    private static int MipOffset(int level)
    {
        int off = 0;
        for (int l = 0; l < level; l++)
        {
            off += Bc1Encoder.EncodedSize(CellPx >> l, CellPx >> l);
        }

        return off;
    }

    private static byte[] Page(byte fill0, byte fill1)
    {
        byte[] payload = new byte[Mip0Bytes + Mip1Bytes];
        payload.AsSpan(0, Mip0Bytes).Fill(fill0);
        payload.AsSpan(Mip0Bytes).Fill(fill1);
        return payload;
    }

    private string WritePack(byte tailLevel, int tailBytes, int pageCount = 4)
    {
        var pages = new List<OrthoPagePack.PageData>();
        for (ushort id = 0; id < pageCount; id++)
        {
            pages.Add(new OrthoPagePack.PageData(id, Level: 0, Page((byte)(0x10 + id), (byte)(0x50 + id)), SrcHash: id));
        }

        byte[] tail = new byte[tailBytes];
        tail.AsSpan().Fill(0xAA);
        pages.Add(new OrthoPagePack.PageData(OrthoPagePack.TailPageId, tailLevel, tail, SrcHash: 999));
        string path = Path.Combine(this.dir, "0_0.opk");
        OrthoPagePack.Write(path, CellPx, pages);
        return path;
    }

    private static (bool Ok, byte[] Chain, ulong Cov) Compose(string path)
    {
        using OrthoPagePack? pack = OrthoPagePack.Open(path, CellPx);
        pack.Should().NotBeNull();
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];
        bool ok = Det1mChainComposer.TryCompose(pack!, CellPx, chain, out ulong cov);
        return (ok, chain, cov);
    }

    [Fact]
    public void Compose_CompactL2Tail_Succeeds()
    {
        string path = this.WritePack(tailLevel: 2, TailBytes(2));

        (bool ok, byte[] chain, ulong cov) = Compose(path);

        ok.Should().BeTrue();
        cov.Should().Be(0b1111);
        chain[MipOffset(2)].Should().Be(0xAA, "ogon L2 laduje od offsetu poziomu 2");
    }

    [Fact]
    public void Compose_LegacyL1Tail_StillSucceeds()
    {
        string path = this.WritePack(tailLevel: 1, TailBytes(1));

        (bool ok, byte[] chain, _) = Compose(path);

        ok.Should().BeTrue();
        chain[MipOffset(1)].Should().Be(0xAA, "ogon L1 laduje od offsetu poziomu 1");
    }

    [Fact]
    public void Compose_TilesPageMip0IntoLevel0()
    {
        string path = this.WritePack(tailLevel: 2, TailBytes(2));

        (_, byte[] chain, _) = Compose(path);

        // Strona 0 (lx=0, ly=0): pierwszy blok level0 pochodzi z jej mip0 (fill 0x10).
        chain[0].Should().Be(0x10);
        // Strona przy prawej krawedzi pierwszego rzedu blokow: lx=1 → kolumny blokow 128.. (fill wg lx*PagesPerEdge+... = id).
        int blocksPerRow = CellPx / 4;
        chain[(blocksPerRow / 2) * 8].Should().Be(0x10 + PagesPerEdge, "prawa polowa rzedu = strona o lx=1 (id=PagesPerEdge)");
    }

    [Fact]
    public void Compose_TilesPageMip1IntoLevel1()
    {
        string path = this.WritePack(tailLevel: 2, TailBytes(2));

        (_, byte[] chain, _) = Compose(path);

        // Poziom 1 (512 px z 2×2 stron po 256 px) sklada sie z mip1 stron (fill 0x50+id) — bez tego
        // poziom 1 bylby zerami (kryjaca czern BC1) dokladnie w pasmie LOD, ktore shader sampluje.
        chain[MipOffset(1)].Should().Be(0x50);
        int level1BlocksPerRow = (CellPx / 2) / 4;
        chain[MipOffset(1) + ((level1BlocksPerRow / 2) * 8)].Should().Be(0x50 + PagesPerEdge);
    }

    [Fact]
    public void Compose_WrongTailLength_Fails()
    {
        string path = this.WritePack(tailLevel: 2, TailBytes(2) - 8);

        (bool ok, _, _) = Compose(path);

        ok.Should().BeFalse();
    }

    [Fact]
    public void Compose_MissingPage_FillsItsRegionsWithTransparentBlack_NotZeros()
    {
        string path = this.WritePack(tailLevel: 2, TailBytes(2), pageCount: 3);

        (_, byte[] chain, _) = Compose(path);

        // Strona 3 (lx=1, ly=1) nieobecna: jej obszar na level0 i level1 MUSI byc DXT1a
        // transparent-black (c0=c1=0, indeksy 3), NIGDY zerami — zerowy blok BC1 to KRYJACA CZERN,
        // ktora bilinear/trilinear rozwleka na sasiadow (mechanizm czarnych artefaktow 2026-08-27,
        // ta sama lekcja co czarne trojkaty 2026-07-24).
        int blocksPerRow0 = CellPx / 4;
        int missingBlock0 = ((blocksPerRow0 / 2) * blocksPerRow0 + (blocksPerRow0 / 2)) * 8; // srodek cwiartki (1,1)
        chain.AsSpan(missingBlock0, 8).ToArray().Should().Equal(0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF);
        int blocksPerRow1 = (CellPx / 2) / 4;
        int missingBlock1 = MipOffset(1) + (((blocksPerRow1 / 2) * blocksPerRow1 + (blocksPerRow1 / 2)) * 8);
        chain.AsSpan(missingBlock1, 8).ToArray().Should().Equal(0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF);
    }

    [Fact]
    public void Compose_MissingPage_ClearsItsCoverageBit()
    {
        string path = this.WritePack(tailLevel: 2, TailBytes(2), pageCount: 3);

        (bool ok, _, ulong cov) = Compose(path);

        ok.Should().BeTrue();
        cov.Should().Be(0b0111, "strona 3 nieobecna → jej bit pokrycia zostaje 0 (shader pokaze baze)");
    }
}