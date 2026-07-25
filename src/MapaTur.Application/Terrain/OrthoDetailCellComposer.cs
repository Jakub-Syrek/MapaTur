namespace MapaTur.Application.Terrain;

/// <summary>
/// The production <see cref="IOrthoDetailComposer"/> the <see cref="OrthoDetailStreamingManager"/> drives
/// (docs/PLAN-ortho-massif-streaming.md, R2). A thin adapter over <see cref="OrthoDetailAssembler"/>: it runs the
/// assembler for a cell using an injected tile provider (typically an <see cref="OrthoTileDecodeCache"/> in front
/// of the SkiaSharp WebP decode) and an optional base sampler, then makes the null decision the manager relies
/// on — a cell with NO constituent tile on disk (entirely outside the fetched hi-res footprint) returns null so
/// the manager skips the upload and the base ortho shows through, rather than uploading a wholly-transparent
/// 4096² texture. A cell with even one present tile returns the composed buffer; its holes carry alpha 0 and are
/// resolved against the base ortho in the shader (finest-wins). Pure CPU, off the paint thread, no GL.
/// </summary>
public sealed class OrthoDetailCellComposer : IOrthoDetailComposer
{
    private readonly OrthoDetailAssembler assembler;
    private readonly Func<int, int, byte[]?> tileProvider;
    private readonly Func<double, double, (byte R, byte G, byte B)?>? baseFill;

    /// <summary>Creates a composer for one detail level. <paramref name="tileProvider"/> returns the decoded
    /// 512×512 RGBA8 tile at a global lattice index (i east, j south) or null; <paramref name="baseFill"/>
    /// optionally supplies (r,g,b) for holes/nodata (soft degradation to the base ortho), or null to leave holes
    /// transparent for the shader to resolve.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="grid"/> or <paramref name="tileProvider"/> is null.</exception>
    public OrthoDetailCellComposer(
        OrthoDetailGrid grid,
        Func<int, int, byte[]?> tileProvider,
        Func<double, double, (byte R, byte G, byte B)?>? baseFill = null)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(tileProvider);
        this.assembler = new OrthoDetailAssembler(grid);
        this.tileProvider = tileProvider;
        this.baseFill = baseFill;
    }

    /// <summary>Composes cell (<paramref name="ci"/>, <paramref name="cj"/>) into an RGBA8 buffer, or null when
    /// the cell has no on-disk tile and no base sampler (nothing to overlay → base ortho shows through).</summary>
    public byte[]? Compose(int ci, int cj) => Compose(ci, cj, destination: null);

    /// <summary>
    /// As <see cref="Compose(int, int)"/> but composing INTO a caller-owned (pooled) buffer — the cell buffer
    /// is 64/256 MiB and allocating one per compose was the dominant LOH churn of a slow traverse. The
    /// destination is cleared by the assembler, so on the null return (all-missing cell) it is left fully
    /// transparent and safe to return to the pool.
    /// </summary>
    /// <param name="ci">Cell column.</param>
    /// <param name="cj">Cell row.</param>
    /// <param name="destination">Reusable CellPx²·4 buffer; null allocates (the legacy behaviour).</param>
    public byte[]? Compose(int ci, int cj, byte[]? destination)
    {
        int present = 0;
        byte[]? Counting(int i, int j)
        {
            byte[]? tile = this.tileProvider(i, j);
            if (tile is not null)
            {
                present++;
            }

            return tile;
        }

        byte[] buffer = this.assembler.Compose(ci, cj, Counting, this.baseFill, destination);

        // No tile present and no base sampler → the whole buffer is transparent black. Return null so the manager
        // skips it (base ortho covers the cell) instead of paying VRAM for an empty texture that the alpha-aware
        // shader would show base through anyway. With a base sampler the cell is fully filled, so never null.
        if (present == 0 && this.baseFill is null)
        {
            return null;
        }

        return buffer;
    }
}