using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Composes a detail CELL's RGBA8 buffer from the decoded 512-px tiles that cover it
/// (docs/PLAN-ortho-massif-streaming.md, R2). Pure byte placement — the caller supplies already-decoded tile
/// buffers (no image codec here) and an optional base sampler for nodata; there is no GL, so this runs off the
/// paint thread and is unit-testable. Tiles are placed 1:1 (no scaling → overlap texels stay bit-identical
/// between neighbouring cells, the colour analogue of the sub-1m seam rule). Missing tiles and per-pixel
/// nodata (the white/black WMS flat-fill on the PL/SK border) are filled from <c>baseFill</c> so the border
/// degrades softly to the ~2 m base instead of showing a black/white patch.
/// </summary>
public sealed class OrthoDetailAssembler
{
    private const int T = OrthoDetailGrid.TilePx;
    private readonly OrthoDetailGrid grid;

    /// <summary>Creates an assembler bound to a detail-level lattice.</summary>
    public OrthoDetailAssembler(OrthoDetailGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        this.grid = grid;
    }

    /// <summary>
    /// Builds the <see cref="OrthoDetailGrid.CellPx"/>² RGBA8 buffer for cell (<paramref name="ci"/>,
    /// <paramref name="cj"/>). <paramref name="tileProvider"/> returns a decoded 512×512 RGBA8 tile or null
    /// (missing/nodata on disk). <paramref name="baseFill"/> supplies (r,g,b) for a lon/lat where a tile is
    /// absent or a pixel is nodata; null leaves such pixels transparent black.
    /// </summary>
    public byte[] Compose(
        int ci, int cj,
        Func<int, int, byte[]?> tileProvider,
        Func<double, double, (byte R, byte G, byte B)?>? baseFill)
    {
        ArgumentNullException.ThrowIfNull(tileProvider);

        int cellPx = grid.CellPx;
        var cell = new byte[cellPx * cellPx * 4];
        MapBounds b = grid.CellBounds(ci, cj);
        double west = b.SouthWest.Longitude, east = b.NorthEast.Longitude;
        double north = b.NorthEast.Latitude, south = b.SouthWest.Latitude;
        int i0 = ci * grid.PitchTiles, j0 = cj * grid.PitchTiles;

        foreach ((int I, int J) t in grid.CellTiles(ci, cj))
        {
            int ox = (t.I - i0) * T;
            int oy = (t.J - j0) * T;
            byte[]? tile = tileProvider(t.I, t.J);

            for (int ty = 0; ty < T; ty++)
            {
                int cy = oy + ty;
                int rowBase = cy * cellPx;
                for (int tx = 0; tx < T; tx++)
                {
                    int co = (rowBase + ox + tx) * 4;

                    if (tile is not null)
                    {
                        int to = ((ty * T) + tx) * 4;
                        byte r = tile[to], g = tile[to + 1], bl = tile[to + 2];
                        // WMS out-of-coverage flat-fill (opaque black/white; the flight-campaign block edges
                        // run DIAGONALLY across tiles, so partially-covered tiles carry it in one half). It is
                        // NOT ground. Black threshold 8: real photography never reaches flat 0 (sensor noise
                        // keeps deep shadow above it), so genuinely dark pixels are data and stay.
                        bool nodata = Math.Max(r, Math.Max(g, bl)) < 8 || Math.Min(r, Math.Min(g, bl)) > 244;
                        if (!nodata)
                        {
                            cell[co] = r; cell[co + 1] = g; cell[co + 2] = bl; cell[co + 3] = 255;
                            continue;
                        }

                        // nodata + no base sampler: fall through and LEAVE THE PIXEL TRANSPARENT, exactly like
                        // a missing tile, so the shader resolves it against the base ortho. (Writing it opaque
                        // — the pre-2026-07-16 behaviour — rendered the fill as jagged black patches on the
                        // massif wherever the campaign boundary crossed the fetched footprint.)
                    }

                    if (baseFill is null)
                    {
                        continue; // missing tile, no base → transparent black
                    }

                    double lon = west + (((ox + tx) + 0.5) / cellPx * (east - west));
                    double lat = north - (((cy) + 0.5) / cellPx * (north - south));
                    if (baseFill(lon, lat) is (byte fr, byte fg, byte fb))
                    {
                        cell[co] = fr; cell[co + 1] = fg; cell[co + 2] = fb; cell[co + 3] = 255;
                    }
                }
            }
        }

        return cell;
    }
}