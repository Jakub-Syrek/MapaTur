using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Krok 6 (ARCHITEKTURA-STREAMING §9 + ANEKS A): runtime'owa cela det25 (nakładkowa, pitch 6 / coverage 8)
/// montuje się ze STRON ≤4 dysjunktywnych pakietów `.opk` (grupa 8×8 kafli) — bez dekodu WebP, bez enkodu
/// BC1, bez generowania mipów. Kontrakt pod testem: strony mip0/mip1 lądują blokowo we właściwych oknach
/// poziomów 0-1, poziomy 2-8 wycinane blokowo z tail-i grup, poziomy 9+ (≤8 px, nigdy nie samplowane w
/// zasięgu fade det25) z tail-a grupy dominującej; KAFEL BEZ STRONY zostaje PRZEZROCZYSTY (DXT1a
/// transparent-black, indeks 3), NIGDY zerowy blok (= kryjąca czerń — mechanizm czarnych trójkątów);
/// okno całkiem poza pokryciem → false (coverage-gate, baza kryje).
/// </summary>
public sealed class OrthoPageWindowAssemblerTests : IDisposable
{
    private const int TilePx = 512;
    private const int GroupTiles = 8;
    private const int CoverageTiles = 8;
    private const int PitchTiles = 6;
    private const int CellPx = CoverageTiles * TilePx; // 4096
    private static readonly int Mip0Bytes = Bc1Encoder.EncodedSize(TilePx, TilePx);
    private static readonly int Mip1Bytes = Bc1Encoder.EncodedSize(TilePx / 2, TilePx / 2);

    private readonly string dir = Path.Combine(Path.GetTempPath(), "opk-window-tests-" + Guid.NewGuid().ToString("N"));

    public OrthoPageWindowAssemblerTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    private static int TailBytes(int firstLevel = 2)
    {
        int total = 0;
        for (int px = CellPx >> firstLevel; px >= 1; px /= 2)
        {
            total += Bc1Encoder.EncodedSize(px, px);
        }

        return total;
    }

    /// <summary>Zapisuje syntetyczny pakiet grupy (gi,gj): wskazane strony wypełnione bajtem
    /// <c>10*(lx+1)+ly</c> (mip0) / temu samemu +100 (mip1), tail wypełniony <paramref name="tailFill"/>.</summary>
    private void BuildPack(int gi, int gj, (int Lx, int Ly)[] present, byte tailFill)
    {
        var pages = new List<OrthoPagePack.PageData>();
        byte[] tail = new byte[TailBytes()];
        Array.Fill(tail, tailFill);
        pages.Add(new OrthoPagePack.PageData(OrthoPagePack.TailPageId, 2, tail, 0));
        foreach ((int lx, int ly) in present)
        {
            byte[] payload = new byte[Mip0Bytes + Mip1Bytes];
            Array.Fill(payload, (byte)((10 * (lx + 1)) + ly), 0, Mip0Bytes);
            Array.Fill(payload, (byte)((10 * (lx + 1)) + ly + 100), Mip0Bytes, Mip1Bytes);
            pages.Add(new OrthoPagePack.PageData((ushort)((lx * GroupTiles) + ly), 0, payload, 1UL));
        }

        OrthoPagePack.Write(Path.Combine(dir, $"{gi}_{gj}.opk"), CellPx, pages);
    }

    private static (int Level0, int Level1, int Level2) ChainOffsets()
    {
        int l0 = Bc1Encoder.EncodedSize(CellPx, CellPx);
        int l1 = Bc1Encoder.EncodedSize(CellPx / 2, CellPx / 2);
        return (0, l0, l0 + l1);
    }

    // Bajt bloku BC1 poziomu 0 celi pod kaflem OKNA (tx,ty), texel-blok (bx,by) wewnątrz kafla.
    private static int Level0BlockOffset(int tx, int ty, int bx, int by)
    {
        const int CellBlocks = CellPx / 4;   // 1024
        const int TileBlocks = TilePx / 4;   // 128
        int row = (ty * TileBlocks) + by;
        int col = (tx * TileBlocks) + bx;
        return ((row * CellBlocks) + col) * 8;
    }

    private static readonly byte[] TransparentBlock = { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };

    [Fact]
    public void should_place_page_mip0_blocks_in_aligned_window()
    {
        // Cela (0,0): okno kafli [0..8) == grupa (0,0) — wszystkie 64 strony z jednego pakietu.
        BuildPack(0, 0, [(0, 0), (3, 2)], tailFill: 0x77);
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];

        bool ok = OrthoPageWindowAssembler.TryAssembleDet25Window(
            dir, 0, 0, PitchTiles, CoverageTiles, GroupTiles, chain, out int pagesRead);

        ok.Should().BeTrue();
        pagesRead.Should().Be(2);
        chain[Level0BlockOffset(0, 0, 0, 0)].Should().Be(10);       // strona (0,0) → kafel okna (0,0)
        chain[Level0BlockOffset(3, 2, 5, 7)].Should().Be(42);       // strona (3,2) → kafel okna (3,2)
    }

    [Fact]
    public void should_fill_missing_pages_with_transparent_blocks_never_zero()
    {
        BuildPack(0, 0, [(0, 0)], tailFill: 0x77);
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];

        OrthoPageWindowAssembler.TryAssembleDet25Window(
            dir, 0, 0, PitchTiles, CoverageTiles, GroupTiles, chain, out _);

        int missing = Level0BlockOffset(7, 7, 0, 0); // kafel bez strony
        chain.AsSpan(missing, 8).ToArray().Should().Equal(TransparentBlock);
    }

    [Fact]
    public void should_assemble_window_from_four_packs()
    {
        // Cela (1,1): okno kafli [6..14)² → grupy (0,0),(0,1),(1,0),(1,1). Kafel globalny (6,7) leży w
        // grupie (0,0) jako (lx=6,ly=7) i w oknie jako (0,1); kafel (8,8) w grupie (1,1) jako (0,0), okno (2,2).
        BuildPack(0, 0, [(6, 7)], tailFill: 0x11);
        BuildPack(0, 1, [(7, 0)], tailFill: 0x22); // kafel globalny (7,8) → okno (1,2)
        BuildPack(1, 0, [(0, 6)], tailFill: 0x33); // kafel globalny (8,6) → okno (2,0)
        BuildPack(1, 1, [(0, 0)], tailFill: 0x44); // kafel globalny (8,8) → okno (2,2)
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];

        bool ok = OrthoPageWindowAssembler.TryAssembleDet25Window(
            dir, 1, 1, PitchTiles, CoverageTiles, GroupTiles, chain, out int pagesRead);

        ok.Should().BeTrue();
        pagesRead.Should().Be(4);
        chain[Level0BlockOffset(0, 1, 0, 0)].Should().Be(77);  // (lx6,ly7): 10*7+7
        chain[Level0BlockOffset(1, 2, 0, 0)].Should().Be(80);  // (lx7,ly0): 10*8+0
        chain[Level0BlockOffset(2, 0, 0, 0)].Should().Be(16);  // (lx0,ly6): 10*1+6
        chain[Level0BlockOffset(2, 2, 0, 0)].Should().Be(10);  // (lx0,ly0): 10*1+0
    }

    [Fact]
    public void should_place_page_mip1_blocks_in_level1()
    {
        BuildPack(0, 0, [(2, 5)], tailFill: 0x77);
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];

        OrthoPageWindowAssembler.TryAssembleDet25Window(
            dir, 0, 0, PitchTiles, CoverageTiles, GroupTiles, chain, out _);

        (_, int l1, _) = ChainOffsets();
        const int CellL1Blocks = CellPx / 2 / 4; // 512
        const int TileL1Blocks = TilePx / 2 / 4; // 64
        int off = l1 + (((((5 * TileL1Blocks) + 0) * CellL1Blocks) + (2 * TileL1Blocks) + 0) * 8);
        chain[off].Should().Be(10 * 3 + 5 + 100);
    }

    [Fact]
    public void should_window_tail_levels_from_group_tails()
    {
        // Cela (1,1), okno [6..14): na poziomie 2 celi (1024 px, kafel = 128 px = 32 bloki) lewy-górny róg
        // okna pochodzi z grupy (0,0) — tail 0x11; prawy-dolny z grupy (1,1) — tail 0x44.
        BuildPack(0, 0, [(6, 6)], tailFill: 0x11);
        BuildPack(0, 1, [(6, 0)], tailFill: 0x22);
        BuildPack(1, 0, [(0, 6)], tailFill: 0x33);
        BuildPack(1, 1, [(0, 0)], tailFill: 0x44);
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];

        OrthoPageWindowAssembler.TryAssembleDet25Window(
            dir, 1, 1, PitchTiles, CoverageTiles, GroupTiles, chain, out _);

        (_, _, int l2) = ChainOffsets();
        const int CellL2Blocks = CellPx / 4 / 4;  // 256 bloków w wierszu poziomu 2
        chain[l2].Should().Be(0x11);                                        // róg NW okna ← tail grupy (0,0)
        int lastBlock = l2 + ((((CellL2Blocks - 1) * CellL2Blocks) + (CellL2Blocks - 1)) * 8);
        chain[lastBlock].Should().Be(0x44);                                 // róg SE okna ← tail grupy (1,1)
    }

    /// <summary>Zapisuje pakiet, którego tail ma KAŻDY part (poziom) wypełniony innym bajtem: part p → 0xA0+p.
    /// Łapie pomyłkę o jeden poziom: kompaktowy tail zaczyna się od poziomu 2 celi, więc jego part 0
    /// musi trafić do poziomu 2, a nie do 1 lub 3.</summary>
    private void BuildPackWithLevelledTail(int gi, int gj)
    {
        byte[] tail = new byte[TailBytes()];
        int off = 0, p = 0;
        for (int px = CellPx / 4; px >= 1; px /= 2, p++)
        {
            int bytes = Bc1Encoder.EncodedSize(px, px);
            Array.Fill(tail, (byte)(0xA0 + p), off, bytes);
            off += bytes;
        }

        byte[] payload = new byte[Mip0Bytes + Mip1Bytes];
        OrthoPagePack.Write(Path.Combine(dir, $"{gi}_{gj}.opk"), CellPx,
        [
            new OrthoPagePack.PageData(OrthoPagePack.TailPageId, 2, tail, 0),
            new OrthoPagePack.PageData(0, 0, payload, 1UL),
        ]);
    }

    [Fact]
    public void should_read_tail_part_matching_cell_level_not_off_by_one()
    {
        BuildPackWithLevelledTail(0, 0);
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];

        OrthoPageWindowAssembler.TryAssembleDet25Window(
            dir, 0, 0, PitchTiles, CoverageTiles, GroupTiles, chain, out _);

        (_, _, int l2) = ChainOffsets();
        chain[l2].Should().Be(0xA0);                     // poziom 2 celi (1024 px) = part 0 kompaktowego tail-a
        int l3 = l2 + Bc1Encoder.EncodedSize(CellPx / 4, CellPx / 4);
        chain[l3].Should().Be(0xA1);                     // poziom 3 (512 px) = part 1
        int deepest = Bc1MipChain.ByteSize(CellPx) - 8;
        chain[deepest].Should().Be(0xAA);                // poziom 12 (1 px) = part 10 (0xA0+10)
    }

    [Fact]
    public void should_skip_legacy_l1_part_when_assembling_minimum_l2()
    {
        int l1Bytes = Bc1Encoder.EncodedSize(CellPx / 2, CellPx / 2);
        int compactBytes = TailBytes();
        byte[] legacyTail = new byte[l1Bytes + compactBytes];
        Array.Fill(legacyTail, (byte)0x11, 0, l1Bytes);
        Array.Fill(legacyTail, (byte)0x22, l1Bytes, compactBytes);
        OrthoPagePack.Write(
            Path.Combine(dir, "0_0.opk"),
            CellPx,
            [new OrthoPagePack.PageData(OrthoPagePack.TailPageId, 1, legacyTail, 0)]);

        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];
        OrthoPageWindowAssembler.TryAssembleTailWindow(
            dir, 0, 0, PitchTiles, CoverageTiles, GroupTiles, minimumLevel: 2, chain, out int tailsRead)
            .Should().BeTrue();

        (_, _, int level2Offset) = ChainOffsets();
        tailsRead.Should().Be(1);
        chain[level2Offset].Should().Be(0x22,
            "czytnik migracyjny ma pominąć zduplikowany L1 starego tail-a, a nie przesunąć poziomy");
    }

    [Fact]
    public void should_return_false_when_window_has_no_pages()
    {
        // Pakiet istnieje, ale okno celi (10,10) → kafle [60..68) → grupy 7-8 — brak plików.
        BuildPack(0, 0, [(0, 0)], tailFill: 0x77);
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];

        bool ok = OrthoPageWindowAssembler.TryAssembleDet25Window(
            dir, 10, 10, PitchTiles, CoverageTiles, GroupTiles, chain, out int pagesRead);

        ok.Should().BeFalse();
        pagesRead.Should().Be(0);
    }

    [Fact]
    public void should_assemble_det05_geometry_window_from_two_packs()
    {
        // det05: coverage 16 / pitch 6 / grupa 16 (cela 8192 px). Cela (1,0): okno kafli X [6..22) →
        // grupy gi=0 i gi=1. Strona (lx=6,ly=0) grupy 0 → kafel okna (0,0); strona (lx=0,ly=0) grupy 1 →
        // kafel okna (10,0). Assembler jest w pełni sparametryzowany — ten test przybija geometrię det05.
        const int Cov05 = 16, Grp05 = 16, Cell05Px = Cov05 * TilePx; // 8192
        int tailBytes = 0;
        for (int px = Cell05Px / 4; px >= 1; px /= 2) { tailBytes += Bc1Encoder.EncodedSize(px, px); }
        void Pack05(int gi, int gj, int lx, int ly, byte fill)
        {
            byte[] tail = new byte[tailBytes];
            byte[] payload = new byte[Mip0Bytes + Mip1Bytes];
            Array.Fill(payload, fill, 0, Mip0Bytes);
            OrthoPagePack.Write(Path.Combine(dir, $"{gi}_{gj}.opk"), Cell05Px,
            [
                new OrthoPagePack.PageData(OrthoPagePack.TailPageId, 2, tail, 0),
                new OrthoPagePack.PageData((ushort)((lx * Grp05) + ly), 0, payload, 1UL),
            ]);
        }

        Pack05(0, 0, 6, 0, 0xAA);
        Pack05(1, 0, 0, 0, 0xBB);
        byte[] chain = new byte[Bc1MipChain.ByteSize(Cell05Px)];

        bool ok = OrthoPageWindowAssembler.TryAssembleDet25Window(
            dir, 1, 0, PitchTiles, Cov05, Grp05, chain, out int pagesRead);

        ok.Should().BeTrue();
        pagesRead.Should().Be(2);
        const int TileBlocks = TilePx / 4;
        chain[0].Should().Be(0xAA);                                    // kafel okna (0,0), blok (0,0)
        chain[(10 * TileBlocks) * 8].Should().Be(0xBB);               // kafel okna (10,0) w wierszu blokowym 0
    }

    [Fact]
    public void should_assemble_tail_without_reading_tile_pages()
    {
        // Tail-first ma dać użyteczny poziom 2+ zanim runtime przeczyta choć jedną ciężką stronę kafla.
        // Pakiet celowo NIE zawiera żadnych stron — pełny assembler zwróciłby false.
        byte[] tail = new byte[TailBytes()];
        Array.Fill(tail, (byte)0x5A, 0, Bc1Encoder.EncodedSize(CellPx / 4, CellPx / 4));
        OrthoPagePack.Write(Path.Combine(dir, "0_0.opk"), CellPx,
        [
            new OrthoPagePack.PageData(OrthoPagePack.TailPageId, 2, tail, 0),
        ]);
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];
        Array.Fill(chain, (byte)0xCC);

        bool ok = OrthoPageWindowAssembler.TryAssembleTailWindow(
            dir, 0, 0, PitchTiles, CoverageTiles, GroupTiles, minimumLevel: 2, chain, out int tailsRead);

        ok.Should().BeTrue();
        tailsRead.Should().Be(1);
        (_, int l1, int l2) = ChainOffsets();
        chain[0].Should().Be(0xCC);   // poziom 0 nietknięty
        chain[l1].Should().Be(0xCC);  // poziom 1 nietknięty
        chain[l2].Should().Be(0x5A);  // poziom 2 gotowy bez strony kafla
    }

    [Fact]
    public void should_assemble_tail_window_from_four_packs()
    {
        BuildPack(0, 0, [], tailFill: 0x11);
        BuildPack(0, 1, [], tailFill: 0x22);
        BuildPack(1, 0, [], tailFill: 0x33);
        BuildPack(1, 1, [], tailFill: 0x44);
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];

        bool ok = OrthoPageWindowAssembler.TryAssembleTailWindow(
            dir, 1, 1, PitchTiles, CoverageTiles, GroupTiles, minimumLevel: 2, chain, out int tailsRead);

        ok.Should().BeTrue();
        tailsRead.Should().Be(4);
        (_, _, int l2) = ChainOffsets();
        const int blocks = CellPx / 4 / 4;
        chain[l2].Should().Be(0x11);
        chain[l2 + ((((blocks - 1) * blocks) + (blocks - 1)) * 8)].Should().Be(0x44);
    }

    [Fact]
    public void should_assemble_fine_levels_without_reading_or_overwriting_tail()
    {
        byte[] payload = new byte[Mip0Bytes + Mip1Bytes];
        Array.Fill(payload, (byte)0x31, 0, Mip0Bytes);
        Array.Fill(payload, (byte)0x32, Mip0Bytes, Mip1Bytes);
        OrthoPagePack.Write(Path.Combine(dir, "0_0.opk"), CellPx,
        [
            // Celowo brak TailPageId: drugi etap ma czytać wyłącznie strony 5/10 cm.
            new OrthoPagePack.PageData(0, 0, payload, 1UL),
        ]);
        byte[] chain = new byte[Bc1MipChain.ByteSize(CellPx)];
        Array.Fill(chain, (byte)0xCC);

        bool ok = OrthoPageWindowAssembler.TryAssembleFineWindow(
            dir, 0, 0, PitchTiles, CoverageTiles, GroupTiles, chain, out int pagesRead);

        ok.Should().BeTrue();
        pagesRead.Should().Be(1);
        (_, int l1, int l2) = ChainOffsets();
        chain[0].Should().Be(0x31);
        chain[l1].Should().Be(0x32);
        chain[l2].Should().Be(0xCC);
    }
}