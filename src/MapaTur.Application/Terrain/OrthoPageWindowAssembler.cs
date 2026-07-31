namespace MapaTur.Application.Terrain;

/// <summary>
/// Krok 6 architektury (ARCHITEKTURA-STREAMING §9 + ANEKS A): montuje pełny łańcuch mipów BC1 runtime'owej
/// celi det25 (nakładkowej: pitch 6 / coverage 8 kafli) WYŁĄCZNIE ze stron gotowych pakietów `.opk`
/// (dysjunktywne grupy 8×8 kafli) — zero dekodu WebP, zero enkodu BC1, zero generowania mipów na runtime.
///
/// Układ (zgodny 1:1 z bake'iem w src/MapaTur.OrthoBake): strona = kafel 512 px, payload = BC1 mip0 (512)
/// + mip1 (256) sekwencyjnie; tail pakietu = poziomy grupy od L2↓1 px sekwencyjnie. Łańcuch celi = poziomy
/// 4096↓1 (layout <see cref="Bc1MipChain.ByteSize"/>).
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
    /// Montuje wyłącznie gruby ogon mipów celi, bez czytania stron kafli. To pierwszy etap streamingu:
    /// dla det05 <paramref name="minimumLevel"/> = 2 daje od razu 20 cm/px, podczas gdy poziomy 0-1
    /// (5/10 cm) mogą zostać doczytane później. Bajty poziomów poniżej minimum pozostają nietknięte,
    /// dzięki czemu renderer może wgrać ogon bez zerowania jeszcze niegotowych poziomów tablicy GPU.
    /// </summary>
    public static bool TryAssembleTailWindow(
        string packDir, int ci, int cj,
        int pitchTiles, int coverageTiles, int groupTiles, int minimumLevel,
        byte[] chainDest, out int tailsRead)
    {
        ArgumentNullException.ThrowIfNull(packDir);
        ArgumentNullException.ThrowIfNull(chainDest);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pitchTiles, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(coverageTiles, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(groupTiles, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumLevel, 2);

        int cellPx = coverageTiles * TilePx;
        int groupPx = groupTiles * TilePx;
        if (cellPx != groupPx)
        {
            throw new ArgumentException("Tail-window assembly requires coverageTiles == groupTiles.");
        }

        int chainSize = Bc1MipChain.ByteSize(cellPx);
        ArgumentOutOfRangeException.ThrowIfLessThan(chainDest.Length, chainSize);

        int firstDestOff = MipOffset(cellPx, minimumLevel);
        for (int o = firstDestOff; o + 8 <= chainSize; o += 8)
        {
            TransparentBlock.CopyTo(chainDest, o);
        }

        int tailBytesMax = 0;
        for (int px = groupPx / 2; px >= 1; px /= 2)
        {
            tailBytesMax += Bc1Encoder.EncodedSize(px, px);
        }

        byte[] tailBuf = MeshBufferPool.Shared.RentBytes(tailBytesMax);
        byte[] zstdScratch = MeshBufferPool.Shared.RentBytes(tailBytesMax);
        tailsRead = 0;
        try
        {
            int ti0 = ci * pitchTiles, tj0 = cj * pitchTiles;
            int giA = FloorDiv(ti0, groupTiles), giB = FloorDiv(ti0 + coverageTiles - 1, groupTiles);
            int gjA = FloorDiv(tj0, groupTiles), gjB = FloorDiv(tj0 + coverageTiles - 1, groupTiles);
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
                    if (pack is null
                        || !TryGetTailFirstLevel(pack, out int tailFirstLevel)
                        || !pack.TryReadPageInto(OrthoPagePack.TailPageId, tailBuf, zstdScratch, out int tailLen))
                    {
                        continue;
                    }

                    tailsRead++;
                    int tiFrom = Math.Max(ti0, gi * groupTiles), tiTo = Math.Min(ti0 + coverageTiles, (gi + 1) * groupTiles);
                    int tjFrom = Math.Max(tj0, gj * groupTiles), tjTo = Math.Min(tj0 + coverageTiles, (gj + 1) * groupTiles);

                    int tailOff = 0;
                    for (int level = tailFirstLevel; cellPx >> level >= 1; level++)
                    {
                        int px = cellPx >> level;
                        int partBytes = Bc1Encoder.EncodedSize(px, px);
                        if (tailOff + partBytes > tailLen)
                        {
                            break;
                        }

                        if (level >= minimumLevel)
                        {
                            int destOff = MipOffset(cellPx, level);
                            int tilePxAtLevel = TilePx >> level;
                            int sx = (tiFrom - (gi * groupTiles)) * tilePxAtLevel;
                            int sy = (tjFrom - (gj * groupTiles)) * tilePxAtLevel;
                            int dx = (tiFrom - ti0) * tilePxAtLevel;
                            int dy = (tjFrom - tj0) * tilePxAtLevel;
                            int w = (tiTo - tiFrom) * tilePxAtLevel;
                            int h = (tjTo - tjFrom) * tilePxAtLevel;
                            bool blockAligned = tilePxAtLevel >= 1 && px >= 4
                                && sx % 4 == 0 && sy % 4 == 0 && dx % 4 == 0 && dy % 4 == 0
                                && w % 4 == 0 && h % 4 == 0;
                            if (blockAligned)
                            {
                                BlitWindow(tailBuf, tailOff, px, chainDest, destOff, px, sx, sy, dx, dy, w, h);
                            }
                            else if (gi == domGi && gj == domGj)
                            {
                                System.Buffer.BlockCopy(tailBuf, tailOff, chainDest, destOff, partBytes);
                            }
                        }

                        tailOff += partBytes;
                    }
                }
            }

            return tailsRead > 0;
        }
        finally
        {
            MeshBufferPool.Shared.Return(tailBuf);
            MeshBufferPool.Shared.Return(zstdScratch);
        }
    }

    /// <summary>
    /// Montuje wyłącznie dokładne poziomy 0-1 (5/10 cm dla det05) ze stron kafli. Nie czyta tail-a
    /// i nie dotyka poziomów 2+, które pierwszy etap zdążył już umieścić w tej samej warstwie GPU.
    /// </summary>
    public static bool TryAssembleFineWindow(
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
        int chainSize = Bc1MipChain.ByteSize(cellPx);
        ArgumentOutOfRangeException.ThrowIfLessThan(chainDest.Length, chainSize);

        int level0Bytes = Bc1Encoder.EncodedSize(cellPx, cellPx);
        int level1Bytes = Bc1Encoder.EncodedSize(cellPx / 2, cellPx / 2);
        for (int o = 0; o + 8 <= level0Bytes + level1Bytes; o += 8)
        {
            TransparentBlock.CopyTo(chainDest, o);
        }

        int mip0Bytes = Bc1Encoder.EncodedSize(TilePx, TilePx);
        int mip1Bytes = Bc1Encoder.EncodedSize(TilePx / 2, TilePx / 2);
        int pageBytes = mip0Bytes + mip1Bytes;
        byte[] pageBuf = MeshBufferPool.Shared.RentBytes(pageBytes);
        byte[] zstdScratch = MeshBufferPool.Shared.RentBytes(pageBytes);
        pagesRead = 0;
        try
        {
            int ti0 = ci * pitchTiles, tj0 = cj * pitchTiles;
            int giA = FloorDiv(ti0, groupTiles), giB = FloorDiv(ti0 + coverageTiles - 1, groupTiles);
            int gjA = FloorDiv(tj0, groupTiles), gjB = FloorDiv(tj0 + coverageTiles - 1, groupTiles);
            int groupPx = groupTiles * TilePx;

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
                        continue;
                    }

                    int tiFrom = Math.Max(ti0, gi * groupTiles), tiTo = Math.Min(ti0 + coverageTiles, (gi + 1) * groupTiles);
                    int tjFrom = Math.Max(tj0, gj * groupTiles), tjTo = Math.Min(tj0 + coverageTiles, (gj + 1) * groupTiles);
                    for (int ti = tiFrom; ti < tiTo; ti++)
                    {
                        for (int tj = tjFrom; tj < tjTo; tj++)
                        {
                            int lx = ti - (gi * groupTiles), ly = tj - (gj * groupTiles);
                            if (!pack.TryReadPageInto((ushort)((lx * groupTiles) + ly), pageBuf, zstdScratch, out int payloadLen)
                                || payloadLen < pageBytes)
                            {
                                continue;
                            }

                            int wx = ti - ti0, wy = tj - tj0;
                            BlitTile(pageBuf, 0, TilePx, chainDest, 0, cellPx, wx, wy);
                            BlitTile(pageBuf, mip0Bytes, TilePx / 2, chainDest, level0Bytes, cellPx / 2, wx, wy);
                            pagesRead++;
                        }
                    }
                }
            }

            return pagesRead > 0;
        }
        finally
        {
            MeshBufferPool.Shared.Return(pageBuf);
            MeshBufferPool.Shared.Return(zstdScratch);
        }
    }

    /// <summary>
    /// Montuje łańcuch BC1 celi det25 (<paramref name="ci"/>,<paramref name="cj"/>) ze stron pakietów w
    /// <paramref name="packDir"/> (pliki "{gi}_{gj}.opk"). <paramref name="chainDest"/> ma pomieścić
    /// <see cref="Bc1MipChain.ByteSize"/>(coverage·512); wypełniany od zera przy każdym wywołaniu.
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
        int chainSize = Bc1MipChain.ByteSize(cellPx);
        ArgumentOutOfRangeException.ThrowIfLessThan(chainDest.Length, chainSize);

        pagesRead = 0;

        // Prefill przezroczystością — dziury (kafle bez stron, brakujące pakiety) mają przepuszczać bazę.
        for (int o = 0; o + 8 <= chainSize; o += 8)
        {
            TransparentBlock.CopyTo(chainDest, o);
        }

        // Pooling (2026-07-24, frame gapy 150-320 ms z pauz gen2-GC): JEDEN bufor stron i JEDEN scratch
        // zstd wynajete na cale wywolanie zamiast new byte[] per strona (256 stron + tail ~15 MB na cele
        // przemielalo LOH gigabajtami przy wymianie cel w locie).
        int tailBytesMax = 0;
        for (int px = (groupTiles * TilePx) / 2; px >= 1; px /= 2)
        {
            tailBytesMax += Bc1Encoder.EncodedSize(px, px);
        }

        byte[] pageBuf = MeshBufferPool.Shared.RentBytes(tailBytesMax);      // tail jest najwiekszy; strony <= tail
        byte[] zstdScratch = MeshBufferPool.Shared.RentBytes(tailBytesMax);  // compressed <= raw
        try
        {
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
                            if (!pack.TryReadPageInto((ushort)((lx * groupTiles) + ly), pageBuf, zstdScratch, out int payloadLen)
                                || payloadLen < mip0Bytes + mip1Bytes)
                            {
                                continue; // kafel bez pokrycia → zostaje transparent
                            }

                            int wx = ti - ti0, wy = tj - tj0; // pozycja kafla w OKNIE celi
                            BlitTile(pageBuf, 0, TilePx, chainDest, 0, cellPx, wx, wy);
                            BlitTile(pageBuf, mip0Bytes, TilePx / 2, chainDest, level1Off, cellPx / 2, wx, wy);
                            pagesRead++;
                        }
                    }

                    // Tail grupy: poziomy 2..8 celi wycinane oknem; 9+ tylko z grupy dominującej (bez okna).
                    // Entry.Level określa pierwszy part: nowe pakiety zaczynają od L2 (bez duplikowania mip1
                    // stron), a czytnik nadal akceptuje starszy tail od L1 na czas atomowej migracji danych.
                    if (!TryGetTailFirstLevel(pack, out int tailFirstLevel)
                        || !pack.TryReadPageInto(OrthoPagePack.TailPageId, pageBuf, zstdScratch, out int tailLen))
                    {
                        continue;
                    }

                    byte[] tail = pageBuf; // alias — tail zajmuje pageBuf az do konca tej grupy

                    int tailOff = 0;
                    for (int level = tailFirstLevel; cellPx >> level >= 1; level++)
                    {
                        int px = cellPx >> level;                      // rozmiar poziomu celi == rozmiar parta
                        int tailPartBytes = Bc1Encoder.EncodedSize(px, px);
                        if (tailOff + tailPartBytes > tailLen)
                        {
                            break; // tail krótszy niż oczekiwany — nie czytamy poza nim
                        }

                        if (level >= 2)
                        {
                            int destOff = MipOffset(cellPx, level);
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
                        }

                        tailOff += tailPartBytes;
                    }
                }
            }

            return pagesRead > 0;
        }
        finally
        {
            MeshBufferPool.Shared.Return(pageBuf);
            MeshBufferPool.Shared.Return(zstdScratch);
        }
    }

    private static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);

    private static bool TryGetTailFirstLevel(OrthoPagePack pack, out int firstLevel)
    {
        foreach (OrthoPagePack.Entry entry in pack.Entries)
        {
            if (entry.PageId == OrthoPagePack.TailPageId)
            {
                firstLevel = entry.Level;
                return firstLevel is 1 or 2;
            }
        }

        firstLevel = 0;
        return false;
    }

    private static int MipOffset(int pixels, int level)
    {
        int offset = 0;
        for (int p = pixels, l = 0; l < level; p >>= 1, l++)
        {
            offset += Bc1Encoder.EncodedSize(p, p);
        }

        return offset;
    }

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