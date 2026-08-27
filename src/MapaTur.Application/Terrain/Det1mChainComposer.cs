namespace MapaTur.Application.Terrain;

/// <summary>
/// Composes the full BC1 mip chain of one det1m cell from a single `.opk` pack — the READER side of
/// the pack contract, mirroring <see cref="OrthoPageWindowAssembler"/>'s layout law: page payload =
/// BC1 mip0 (512 px) + mip1 (256 px) sequentially, so cell level 1 is TILED from the pages' own mip1
/// parts, and the tail starts at the level its pack entry declares (compact L2↓ after the 2026 tail
/// migration, legacy L1↓ still accepted).
///
/// This class exists because the previous inline loop in the renderer assumed the legacy L1↓ tail and
/// silently rejected every migrated pack (tailOk=false → empty slices → layer dead with NO log,
/// 2026-08-27). Pure and unit-tested against packs written by <see cref="OrthoPagePack.Write"/>.
/// </summary>
public static class Det1mChainComposer
{
    private const int PagePx = 512;
    private const int BlockPx = 4;
    private const int BlockBytes = 8;

    // DXT1a transparent-black (c0=c1=0 → tryb 3-kolorowy, wszystkie indeksy 3). Luki (brakujące strony,
    // obszary bez pokrycia) MUSZĄ być tym blokiem, NIGDY zerami — zerowy blok BC1 to KRYJĄCA CZERŃ,
    // którą filtrowanie rozwleka na sąsiadów (czarne pasy na SK pod Rysami, 2026-08-27; ta sama lekcja
    // co czarne trójkąty 2026-07-24 i doktryna OrthoPageWindowAssembler).
    private static readonly byte[] TransparentBlock = { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF };

    /// <summary>
    /// Fills <paramref name="chainDest"/> (≥ <see cref="Bc1MipChain.ByteSize"/>(<paramref name="cellPx"/>))
    /// with the cell's mip chain and reports per-page coverage bits (bit index = ly*pagesPerEdge + lx,
    /// the renderer's het det1m convention). Returns false when the pack has no tail or the tail's length
    /// does not match the level its entry declares — the caller should skip the cell (base covers).
    /// Missing/corrupt regular pages only clear their coverage bit; the shader falls back per-page.
    /// </summary>
    public static bool TryCompose(OrthoPagePack pack, int cellPx, byte[] chainDest, out ulong coverage)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(chainDest);
        ArgumentOutOfRangeException.ThrowIfLessThan(chainDest.Length, Bc1MipChain.ByteSize(cellPx));

        coverage = 0;
        int pagesPerEdge = cellPx / PagePx;

        OrthoPagePack.Entry? tailEntry = null;
        foreach (OrthoPagePack.Entry e in pack.Entries)
        {
            if (e.PageId == OrthoPagePack.TailPageId)
            {
                tailEntry = e;
                break;
            }
        }

        if (tailEntry is not { } tailInfo
            || tailInfo.Level < 1
            || !pack.TryReadPage(OrthoPagePack.TailPageId, out byte[] tail)
            || tail.Length != TailBytes(cellPx, tailInfo.Level))
        {
            return false;
        }

        int chainSize = Bc1MipChain.ByteSize(cellPx);
        for (int o = 0; o + BlockBytes <= chainSize; o += BlockBytes)
        {
            TransparentBlock.CopyTo(chainDest, o);
        }

        Buffer.BlockCopy(tail, 0, chainDest, MipOffset(cellPx, tailInfo.Level), tail.Length);

        int mip0Bytes = Bc1Encoder.EncodedSize(PagePx, PagePx);
        int mip1Bytes = Bc1Encoder.EncodedSize(PagePx / 2, PagePx / 2);
        int level1Offset = MipOffset(cellPx, 1);

        foreach (OrthoPagePack.Entry e in pack.Entries)
        {
            if (e.PageId == OrthoPagePack.TailPageId
                || !pack.TryReadPage(e.PageId, out byte[] page)
                || page.Length < mip0Bytes)
            {
                continue; // strona zła/nieobecna → bit pokrycia zostaje 0 → shader pokazuje bazę
            }

            int lx = e.PageId / pagesPerEdge, ly = e.PageId % pagesPerEdge;
            BlitPageMip(page, 0, PagePx, chainDest, 0, cellPx, lx, ly);
            if (tailInfo.Level >= 2 && page.Length >= mip0Bytes + mip1Bytes)
            {
                // Poziom 1 celi (cellPx/2) kafluje się z mip1 stron — bez tego zostałby zerami
                // (zerowy blok BC1 = KRYJĄCA czerń) dokładnie w pasmie LOD, które shader sampluje.
                BlitPageMip(page, mip0Bytes, PagePx / 2, chainDest, level1Offset, cellPx / 2, lx, ly);
            }

            coverage |= 1UL << ((ly * pagesPerEdge) + lx);
        }

        return true;
    }

    /// <summary>Tail length implied by its first level: mips of cellPx>>level down to 1 px, sequentially.</summary>
    public static int TailBytes(int cellPx, int firstLevel)
    {
        int total = 0;
        for (int px = cellPx >> firstLevel; px >= 1; px /= 2)
        {
            total += Bc1Encoder.EncodedSize(px, px);
        }

        return total;
    }

    private static int MipOffset(int cellPx, int level)
    {
        int off = 0;
        for (int l = 0; l < level; l++)
        {
            off += Bc1Encoder.EncodedSize(cellPx >> l, cellPx >> l);
        }

        return off;
    }

    private static void BlitPageMip(
        byte[] page, int srcOffset, int srcPx,
        byte[] chainDest, int destOffset, int destLevelPx, int lx, int ly)
    {
        int srcBlocks = srcPx / BlockPx;
        int destBlocksPerRow = destLevelPx / BlockPx;
        int rowBytes = srcBlocks * BlockBytes;
        for (int row = 0; row < srcBlocks; row++)
        {
            Buffer.BlockCopy(
                page, srcOffset + (row * rowBytes),
                chainDest, destOffset + (((((ly * srcBlocks) + row) * destBlocksPerRow) + (lx * srcBlocks)) * BlockBytes),
                rowBytes);
        }
    }
}