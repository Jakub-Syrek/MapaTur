namespace MapaTur.Application.Terrain;

/// <summary>
/// Krok 6 architektury (ARCHITEKTURA-STREAMING §9 + ANEKS A): montuje pełny łańcuch mipów BC1 runtime'owej
/// celi det25 (nakładkowej: pitch 6 / coverage 8 kafli) WYŁĄCZNIE ze stron gotowych pakietów `.opk`
/// (dysjunktywne grupy 8×8 kafli) — zero dekodu WebP, zero enkodu BC1, zero generowania mipów na runtime.
///
/// Układ (zgodny 1:1 z bake'iem w src/MapaTur.OrthoBake): strona = kafel 512 px, payload = BC1 mip0 (512)
/// + mip1 (256) sekwencyjnie; tail pakietu = poziomy grupy 2048↓1 sekwencyjnie. Łańcuch celi = poziomy
/// 4096↓1 (layout <see cref="GpuCellCache.ChainSize"/>).
///
/// Składanie: poziomy 0-1 z okien stron; poziomy 2-8 wycinane BLOKOWO z tail-i ≤4 grup (offsety okna są
/// zawsze parzyste w kaflach — 6c mod 8 ∈ {0,2,4,6} — więc cięcie po blokach 4×4 jest całkowite do poziomu
/// 8 włącznie); poziomy 9+ (cela ≤8 px) kopiowane wprost z tail-a grupy DOMINUJĄCEJ (zawierającej środek
/// okna) — przybliżenie zamierzone: te poziomy odpowiadają celi mniejszej niż 8 px na ekranie, a fade-out
/// det25 gasi warstwę o rzędy wielkości wcześniej, więc nigdy nie są realnie samplowane.
///
/// Kafel bez strony (nodata / poza pokryciem) zostaje DXT1a transparent-black (indeks 3) — NIGDY blokiem
/// zer (zerowy blok BC1 = KRYJĄCA czerń; dokładnie mechanizm czarnych trójkątów z 2026-07-24).
/// </summary>
public static class OrthoPageWindowAssembler
{
    private const int TilePx = 512;

    // DXT1a transparent-black: c0=c1=0 (tryb 3-kolorowy), wszystkie indeksy 3 (0xFF ×4).
    private static readonly byte[] TransparentBlock = { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>
    /// Montuje łańcuch BC1 celi det25 (<paramref name="ci"/>,<paramref name="cj"/>) ze stron pakietów w
    /// <paramref name="packDir"/> (pliki "{gi}_{gj}.opk"). <paramref name="chainDest"/> ma pomieścić
    /// <see cref="GpuCellCache.ChainSize"/>(coverage·512); wypełniany od zera przy każdym wywołaniu.
    /// Zwraca false (i nie zostawia nic kryjącego), gdy okno nie zawiera ŻADNEJ strony — baza kryje celę.
    /// </summary>
    public static bool TryAssembleDet25Window(
        string packDir, int ci, int cj,
        int pitchTiles, int coverageTiles, int groupTiles,
        byte[] chainDest, out int pagesRead)
    {
        ArgumentNullException.ThrowIfNull(packDir);
        ArgumentNullException.ThrowIfNull(chainDest);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pitchTiles, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(coverageTiles, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(groupTiles, 0);

        int cellPx = coverageTiles * TilePx;
        int chainSize = GpuCellCache.ChainSize(cellPx);
        ArgumentOutOfRangeException.ThrowIfLessThan(chainDest.Length, chainSize);

        pagesRead = 0;

        // Prefill przezroczystością — dziury (kafle bez stron, brakujące pakiety) mają przepuszczać bazę.
        for (int o = 0; o + 8 <= chainSize; o += 8)
        {
            TransparentBlock.CopyTo(chainDest, o);
        }

        int ti0 = ci * pitchTiles, tj0 = cj * pitchTiles;         // okno kafli [ti0, ti0+coverage)
        int giA = FloorDiv(ti0, groupTiles), giB = FloorDiv(ti0 + coverageTiles - 1, groupTiles);
        int gjA = FloorDiv(tj0, groupTiles), gjB = FloorDiv(tj0 + coverageTiles - 1, groupTiles);

        int level0Bytes = Bc1Encoder.EncodedSize(cellPx, cellPx);
        int level1Off = level0Bytes;
        int mip0Bytes = Bc1Encoder.EncodedSize(TilePx, TilePx);
        int mip1Bytes = Bc1Encoder.EncodedSize(TilePx / 2, TilePx / 2);
        int groupPx = groupTiles * TilePx;

        // Grupa dominująca = zawierająca środek okna (dla poziomów 9+ kopiowanych bez okna).
        int domGi = FloorDiv(ti0 + (coverageTiles / 2), groupTiles);
        int domGj = FloorDiv(tj0 + (coverageTiles / 2), groupTiles);

        for (int gi = giA; gi <= giB; gi++)
        {
            for (int gj = gjA; gj <= gjB; gj++)
            {
                string packPath = System.IO.Path.Combine(packDir, $"{gi}_{gj}.opk");
                if (!System.IO.File.Exists(packPath))
                {
                    continue;
                }

                using OrthoPagePack? pack = OrthoPagePack.Open(packPath, groupPx);
                if (pack is null)
                {
                    continue; // zła wersja / rozdarty plik → jak brak (baza kryje); bake naprawi
                }

                // Zakres kafli okna leżący w tej grupie.
                int tiFrom = Math.Max(ti0, gi * groupTiles), tiTo = Math.Min(ti0 + coverageTiles, (gi + 1) * groupTiles);
                int tjFrom = Math.Max(tj0, gj * groupTiles), tjTo = Math.Min(tj0 + coverageTiles, (gj + 1) * groupTiles);

                // Poziomy 0-1: strony kafli (mip0 → level0, mip1 → level1), wklejane blokowo.
                for (int ti = tiFrom; ti < tiTo; ti++)
                {
                    for (int tj = tjFrom; tj < tjTo; tj++)
                    {
                        int lx = ti - (gi * groupTiles), ly = tj - (gj * groupTiles);
                        if (!pack.TryReadPage((ushort)((lx * groupTiles) + ly), out byte[] payload)
                            || payload.Length < mip0Bytes + mip1Bytes)
                        {
                            continue; // kafel bez pokrycia → zostaje transparent
                        }

                        int wx = ti - ti0, wy = tj - tj0; // pozycja kafla w OKNIE celi
                        BlitTile(payload, 0, TilePx, chainDest, 0, cellPx, wx, wy);
                        BlitTile(payload, mip0Bytes, TilePx / 2, chainDest, level1Off, cellPx / 2, wx, wy);
                        pagesRead++;
                    }
                }

                // Tail grupy: poziomy 2..8 celi wycinane oknem; 9+ tylko z grupy dominującej (bez okna).
                // Party tail-a: p=0 → 2048 px = POZIOM 1 CELI (wypełniany ze stron mip1, pomijamy),
                // p=level-1 → px identyczny z poziomem celi (grupa i cela mają ten sam metraż 4096 px).
                if (!pack.TryReadPage(OrthoPagePack.TailPageId, out byte[] tail))
                {
                    continue;
                }

                int tailOff = Bc1Encoder.EncodedSize(groupPx / 2, groupPx / 2); // skip part 0 (poziom 1)
                int destOff = level0Bytes + Bc1Encoder.EncodedSize(cellPx / 2, cellPx / 2);
                for (int level = 2; cellPx >> level >= 1; level++)
                {
                    int px = cellPx >> level;                      // rozmiar poziomu celi == rozmiar parta
                    int tailPartBytes = Bc1Encoder.EncodedSize(px, px);
                    if (tailOff + tailPartBytes > tail.Length)
                    {
                        break; // tail krótszy niż oczekiwany — nie czytamy poza nim
                    }

                    int tilePxAtLevel = TilePx >> level;           // piksele kafla na tym poziomie
                    int sx = (tiFrom - (gi * groupTiles)) * tilePxAtLevel, sy = (tjFrom - (gj * groupTiles)) * tilePxAtLevel;
                    int dx = (tiFrom - ti0) * tilePxAtLevel, dy = (tjFrom - tj0) * tilePxAtLevel;
                    int w = (tiTo - tiFrom) * tilePxAtLevel, h = (tjTo - tjFrom) * tilePxAtLevel;
                    bool blockAligned = tilePxAtLevel >= 1 && px >= 4
                        && sx % 4 == 0 && sy % 4 == 0 && dx % 4 == 0 && dy % 4 == 0 && w % 4 == 0 && h % 4 == 0;
                    if (blockAligned)
                    {
                        BlitWindow(tail, tailOff, px, chainDest, destOff, px, sx, sy, dx, dy, w, h);
                    }
                    else if (gi == domGi && gj == domGj)
                    {
                        // Poziomy ≤8 px celi: kopiuj cały part grupy dominującej (przybliżenie — patrz nagłówek).
                        System.Buffer.BlockCopy(tail, tailOff, chainDest, destOff, tailPartBytes);
                    }

                    tailOff += tailPartBytes;
                    destOff += tailPartBytes;
                }
            }
        }

        return pagesRead > 0;
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    // Wkleja stronę kafla (pageSizePx²) w poziom celi (destLevelPx²) na pozycji kafla okna (wx,wy) — blokowo.
    private static void BlitTile(
        byte[] src, int srcOff, int pageSizePx, byte[] dest, int destLevelOff, int destLevelPx, int wx, int wy)
    {
        int tileBlocks = pageSizePx / 4, destBlocks = destLevelPx / 4, rowBytes = tileBlocks * 8;
        for (int row = 0; row < tileBlocks; row++)
        {
            int dst = destLevelOff + ((((wy * tileBlocks) + row) * destBlocks) + (wx * tileBlocks)) * 8;
            System.Buffer.BlockCopy(src, srcOff + (row * rowBytes), dest, dst, rowBytes);
        }
    }

    // Wycina blokowo prostokąt [srcXPx..+widthPx) z poziomu tail-a grupy do poziomu celi. Wszystkie
    // współrzędne muszą być wielokrotnościami 4 px (gwarantowane parzystością offsetów okna do poziomu 8).
    private static void BlitWindow(
        byte[] src, int srcOff, int srcPx, byte[] dest, int destOff, int destPx,
        int srcXPx, int srcYPx, int dstXPx, int dstYPx, int widthPx, int heightPx)
    {
        int srcBlocksW = srcPx / 4, dstBlocksW = destPx / 4;
        int bw = widthPx / 4, bh = heightPx / 4;
        int sbx = srcXPx / 4, sby = srcYPx / 4, dbx = dstXPx / 4, dby = dstYPx / 4;
        for (int row = 0; row < bh; row++)
        {
            int s = srcOff + ((((sby + row) * srcBlocksW) + sbx) * 8);
            int d = destOff + ((((dby + row) * dstBlocksW) + dbx) * 8);
            System.Buffer.BlockCopy(src, s, dest, d, bw * 8);
        }
    }
}
