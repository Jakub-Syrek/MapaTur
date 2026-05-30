using MapaTur.Domain.Geography;
using MapaTur.Domain.Maps;

namespace MapaTur.Application.Maps;

/// <summary>
/// Builds per-cell ortho textures for the 3D terrain mesh by sampling tiles from an
/// <see cref="ITileSource"/> (typically an MBTiles archive). For each cell of the
/// requested grid the compositor reprojects from Web Mercator (the tiles' native
/// projection) onto the cell's equirectangular UV space (what the mesh expects) so
/// the result drapes pixel-perfect on top of the DEM.
/// </summary>
/// <remarks>
/// The cells produced here mirror the <c>tatry-ortho-r{R}-c{C}.png</c> files the existing
/// pipeline already supports — the renderer keys them by row-major <c>OrthoTileIndex</c>.
/// </remarks>
public sealed class MBTilesOrthoCompositor
{
    private readonly IRasterTileDecoder decoder;

    /// <summary>
    /// Initializes a new compositor with the given tile decoder.
    /// </summary>
    /// <param name="decoder">Decoder used to turn raw PNG/JPEG bytes into RGBA8 pixels.</param>
    public MBTilesOrthoCompositor(IRasterTileDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        this.decoder = decoder;
    }

    /// <summary>
    /// Composites tiles from <paramref name="source"/> into one RGBA8 texture per
    /// cell of the requested <paramref name="gridRows"/>×<paramref name="gridCols"/> grid
    /// spanning <paramref name="demBounds"/>.
    /// </summary>
    /// <param name="source">Tile source (e.g. an MBTiles archive).</param>
    /// <param name="demBounds">Geographic extent of the DEM the cells will drape onto.</param>
    /// <param name="gridCols">Number of cell columns (≥ 0).</param>
    /// <param name="gridRows">Number of cell rows (≥ 0).</param>
    /// <param name="cellWidthPixels">Pixel width of each cell's output texture.</param>
    /// <param name="cellHeightPixels">Pixel height of each cell's output texture.</param>
    /// <param name="preferredZoom">Zoom to sample at; defaults to the source's max zoom.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One cell per grid position, row-major (row 0 = north, col 0 = west).</returns>
    public async Task<IReadOnlyList<OrthoTextureCell>> CompositeAsync(
        ITileSource source,
        MapBounds demBounds,
        int gridCols,
        int gridRows,
        int cellWidthPixels,
        int cellHeightPixels,
        int? preferredZoom = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(gridCols);
        ArgumentOutOfRangeException.ThrowIfNegative(gridRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellWidthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cellHeightPixels);

        if (gridCols == 0 || gridRows == 0)
        {
            return Array.Empty<OrthoTextureCell>();
        }

        TileSourceMetadata meta = source.GetMetadata();
        int zoom = preferredZoom ?? meta.MaxZoomLevel;
        zoom = Math.Clamp(zoom, 0, TileCoordinate.MaxZoomLevel);

        double demLatMin = demBounds.SouthWest.Latitude;
        double demLatMax = demBounds.NorthEast.Latitude;
        double demLonMin = demBounds.SouthWest.Longitude;
        double demLonMax = demBounds.NorthEast.Longitude;
        double cellLatSpan = (demLatMax - demLatMin) / gridRows;
        double cellLonSpan = (demLonMax - demLonMin) / gridCols;

        // Decoded-tile cache. The same Mercator tile typically falls inside several
        // adjacent cells; decoding once per unique (z,x,y) keeps IO + CPU bounded.
        Dictionary<TileCoordinate, DecodedRasterTile?> decoded = new();

        var cells = new OrthoTextureCell[gridRows * gridCols];
        for (int row = 0; row < gridRows; row++)
        {
            for (int col = 0; col < gridCols; col++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Cell bounds: row 0 is north (highest latitude).
                double cellLatMax = demLatMax - (row * cellLatSpan);
                double cellLatMin = demLatMax - ((row + 1) * cellLatSpan);
                double cellLonMin = demLonMin + (col * cellLonSpan);
                double cellLonMax = demLonMin + ((col + 1) * cellLonSpan);

                byte[] rgba = await CompositeCellAsync(
                    source, decoded, zoom,
                    cellLatMin, cellLatMax, cellLonMin, cellLonMax,
                    cellWidthPixels, cellHeightPixels,
                    cancellationToken).ConfigureAwait(false);

                cells[(row * gridCols) + col] = new OrthoTextureCell(row, col, cellWidthPixels, cellHeightPixels, rgba);
            }
        }

        return cells;
    }

    private async Task<byte[]> CompositeCellAsync(
        ITileSource source,
        Dictionary<TileCoordinate, DecodedRasterTile?> decoded,
        int zoom,
        double cellLatMin,
        double cellLatMax,
        double cellLonMin,
        double cellLonMax,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var rgba = new byte[width * height * 4];
        double tilesPerWorld = 1 << zoom;
        // World pixel coords use the standard slippy convention: 256 pixels per tile.
        double worldPixels = tilesPerWorld * 256.0;

        for (int py = 0; py < height; py++)
        {
            // Equirectangular sampling: row 0 of the output is the cell's north edge.
            double v = (py + 0.5) / height;
            double lat = cellLatMax - (v * (cellLatMax - cellLatMin));
            double latClamped = Math.Clamp(lat, -85.05112878, 85.05112878);
            double latRad = latClamped * Math.PI / 180.0;
            double yWorldPix = (1.0 - (Math.Log(Math.Tan(latRad) + (1.0 / Math.Cos(latRad))) / Math.PI)) / 2.0 * worldPixels;

            for (int px = 0; px < width; px++)
            {
                double u = (px + 0.5) / width;
                double lon = cellLonMin + (u * (cellLonMax - cellLonMin));
                double xWorldPix = (lon + 180.0) / 360.0 * worldPixels;

                int dst = ((py * width) + px) * 4;
                await SampleBilinearAsync(
                    source, decoded, zoom, xWorldPix, yWorldPix, rgba, dst, cancellationToken).ConfigureAwait(false);
            }
        }

        return rgba;
    }

    /// <summary>
    /// Samples the basemap at fractional world-pixel coords using a 4-tap bilinear
    /// filter. The four texels straddle up to four neighbouring tiles; each is fetched
    /// from the cache (decoded on first miss) and the per-channel weighted blend is
    /// written into <paramref name="output"/> at <paramref name="outputOffset"/>.
    /// Missing tiles contribute (0,0,0,0) so they fade into transparency naturally
    /// instead of producing a hard edge.
    /// </summary>
    private async Task SampleBilinearAsync(
        ITileSource source,
        Dictionary<TileCoordinate, DecodedRasterTile?> decoded,
        int zoom,
        double xWorldPix,
        double yWorldPix,
        byte[] output,
        int outputOffset,
        CancellationToken cancellationToken)
    {
        // Offset by half a pixel so the centres of texels sit at integer coords —
        // standard bilinear convention; without this the sample lands at the texel
        // corner and the filter is asymmetric.
        double sx = xWorldPix - 0.5;
        double sy = yWorldPix - 0.5;
        int x0 = (int)Math.Floor(sx);
        int y0 = (int)Math.Floor(sy);
        double fx = sx - x0;
        double fy = sy - y0;

        double w00 = (1 - fx) * (1 - fy);
        double w10 = fx * (1 - fy);
        double w01 = (1 - fx) * fy;
        double w11 = fx * fy;

        (double r, double g, double b, double a) acc = (0, 0, 0, 0);
        await AccumulateAsync(source, decoded, zoom, x0, y0, w00, cancellationToken, v => acc = (acc.r + v.r * w00, acc.g + v.g * w00, acc.b + v.b * w00, acc.a + v.a * w00)).ConfigureAwait(false);
        await AccumulateAsync(source, decoded, zoom, x0 + 1, y0, w10, cancellationToken, v => acc = (acc.r + v.r * w10, acc.g + v.g * w10, acc.b + v.b * w10, acc.a + v.a * w10)).ConfigureAwait(false);
        await AccumulateAsync(source, decoded, zoom, x0, y0 + 1, w01, cancellationToken, v => acc = (acc.r + v.r * w01, acc.g + v.g * w01, acc.b + v.b * w01, acc.a + v.a * w01)).ConfigureAwait(false);
        await AccumulateAsync(source, decoded, zoom, x0 + 1, y0 + 1, w11, cancellationToken, v => acc = (acc.r + v.r * w11, acc.g + v.g * w11, acc.b + v.b * w11, acc.a + v.a * w11)).ConfigureAwait(false);

        output[outputOffset] = (byte)Math.Clamp(Math.Round(acc.r), 0, 255);
        output[outputOffset + 1] = (byte)Math.Clamp(Math.Round(acc.g), 0, 255);
        output[outputOffset + 2] = (byte)Math.Clamp(Math.Round(acc.b), 0, 255);
        output[outputOffset + 3] = (byte)Math.Clamp(Math.Round(acc.a), 0, 255);
    }

    private async Task AccumulateAsync(
        ITileSource source,
        Dictionary<TileCoordinate, DecodedRasterTile?> decoded,
        int zoom,
        int worldX,
        int worldY,
        double weight,
        CancellationToken cancellationToken,
        Action<(double r, double g, double b, double a)> accumulate)
    {
        if (weight <= 0)
        {
            return;
        }

        int tileX = (int)Math.Floor(worldX / 256.0);
        int tileY = (int)Math.Floor(worldY / 256.0);
        // Mercator wraps east-west; clamp out-of-range Y to avoid bogus tile fetches at the poles.
        int worldTiles = 1 << zoom;
        if (tileY < 0 || tileY >= worldTiles)
        {
            return;
        }
        // Wrap X around the dateline so a sample just past lon=180 falls back into tile column 0.
        tileX = ((tileX % worldTiles) + worldTiles) % worldTiles;

        var coord = new TileCoordinate(zoom, tileX, tileY);
        DecodedRasterTile? tile = await GetDecodedAsync(source, decoded, coord, cancellationToken).ConfigureAwait(false);
        if (tile is null)
        {
            return;
        }

        int localX = worldX - (tileX * 256);
        int localY = worldY - (tileY * 256);
        // The decoder may hand back tiles at native sizes other than 256; map our 256-grid coords
        // into the actual buffer dimensions before sampling, then clamp to be safe.
        int sx = Math.Clamp(localX * tile.Width / 256, 0, tile.Width - 1);
        int sy = Math.Clamp(localY * tile.Height / 256, 0, tile.Height - 1);
        int idx = ((sy * tile.Width) + sx) * 4;
        accumulate((tile.Rgba[idx], tile.Rgba[idx + 1], tile.Rgba[idx + 2], tile.Rgba[idx + 3]));
    }

    private async Task<DecodedRasterTile?> GetDecodedAsync(
        ITileSource source,
        Dictionary<TileCoordinate, DecodedRasterTile?> cache,
        TileCoordinate coord,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(coord, out DecodedRasterTile? cached))
        {
            return cached;
        }

        byte[]? bytes = await source.GetTileAsync(coord, cancellationToken).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
        {
            cache[coord] = null;
            return null;
        }

        DecodedRasterTile tile = decoder.Decode(bytes);
        cache[coord] = tile;
        return tile;
    }
}