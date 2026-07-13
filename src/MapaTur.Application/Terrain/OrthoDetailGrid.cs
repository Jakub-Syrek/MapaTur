using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// The global plate-carrée lattice for the massif hi-res ortho DETAIL layer
/// (docs/PLAN-ortho-massif-streaming.md, R2). A DETAIL CELL is a <see cref="CellPx"/>²-px mosaic composed at
/// runtime from the on-disk 512-px WebP tile pyramid; it covers <c>coverageTiles</c> tiles (8 = 1024 m @
/// 0.25 m) and is placed on a <c>pitchTiles</c> (6 = 768 m) grid, so neighbours overlap by 2 tiles (256 m →
/// a 128 m margin on each side). The anchor + pitch MUST match <c>testdata/maps/fetch-ortho-detail.py</c>
/// (<see cref="GridLon0"/>/<see cref="GridLat0"/>/<see cref="GridRefLat"/>) so cells align 1:1 with the tiles
/// on disk and overlap texels are bit-identical between neighbours (the sub-1m seam doctrine, colour edition).
///
/// The load-bearing property is <see cref="CellContains"/>: the cell picked by <see cref="CellForPoint"/> for
/// a point fully contains a z17-sized (≤200 m) terrain tile around it (max distance from a cell centre is
/// pitch/2 = 3 tiles, coverage half is 4 tiles → ≥1 tile = 128 m margin > a 100 m tile half-extent), so a
/// terrain mesh never GEOMETRICALLY straddles a cell boundary. This is a NECESSARY condition for a seamless
/// overlay, NOT a proof of zero rendering seams — visible seamlessness additionally requires bit-identical
/// overlap texels (the assembler's 1:1 placement), correct UV mapping, and no mip/filtering bleed across cell
/// edges in the GL upload/sampler, all of which are verified in-app, not here.
/// </summary>
public sealed class OrthoDetailGrid
{
    /// <summary>Lattice NW anchor longitude (west edge of the largest planned window, C). Matches the fetcher.</summary>
    public const double GridLon0 = 19.50;

    /// <summary>Lattice NW anchor latitude (north edge). Matches the fetcher.</summary>
    public const double GridLat0 = 49.40;

    /// <summary>Reference latitude that fixes the longitude pitch (tiles square in ground metres). Matches the fetcher.</summary>
    public const double GridRefLat = 49.25;

    /// <summary>On-disk tile size in pixels.</summary>
    public const int TilePx = 512;

    private const double MetersPerLatDeg = 111320.0;

    private readonly int coverageTiles;
    private readonly int pitchTiles;

    /// <summary>Ground metres per pixel of this level (0.25 for det25, 0.05 for det05).</summary>
    public double ResMeters { get; }

    /// <summary>Tile latitude span in degrees (north→south).</summary>
    public double Dlat { get; }

    /// <summary>Tile longitude span in degrees (west→east), fixed at <see cref="GridRefLat"/>.</summary>
    public double Dlon { get; }

    /// <summary>Composed cell texture size in pixels (coverageTiles × 512).</summary>
    public int CellPx => coverageTiles * TilePx;

    /// <summary>Tiles composing one cell edge (coverage); the cell is coverageTiles² tiles.</summary>
    public int CoverageTiles => coverageTiles;

    /// <summary>Tile step between adjacent cell origins (pitch).</summary>
    public int PitchTiles => pitchTiles;

    /// <summary>Creates a lattice for one detail level; defaults are det25 (0.25 m, 4096 px cells, 768 m pitch).</summary>
    /// <param name="resMeters">Ground metres per pixel (0.25 = det25).</param>
    /// <param name="coverageTiles">Tiles per cell edge (8 → 4096 px cell). Must be ≥ <paramref name="pitchTiles"/>.</param>
    /// <param name="pitchTiles">Tile step between cells (6 → 768 m). Overlap = coverage − pitch.</param>
    public OrthoDetailGrid(double resMeters = 0.25, int coverageTiles = 8, int pitchTiles = 6)
    {
        if (resMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(resMeters));
        }

        if (coverageTiles <= 0 || pitchTiles <= 0 || pitchTiles > coverageTiles)
        {
            throw new ArgumentOutOfRangeException(nameof(pitchTiles),
                "pitchTiles must be in [1, coverageTiles] so neighbouring cells overlap (or abut).");
        }

        ResMeters = resMeters;
        this.coverageTiles = coverageTiles;
        this.pitchTiles = pitchTiles;
        double tileGround = TilePx * resMeters;
        Dlat = tileGround / MetersPerLatDeg;
        Dlon = tileGround / (MetersPerLatDeg * Math.Cos(GridRefLat * Math.PI / 180.0));
    }

    /// <summary>The lattice tile (I east, J south) a point falls in.</summary>
    public (int I, int J) TileAt(GeoPoint point) =>
        ((int)Math.Floor((point.Longitude - GridLon0) / Dlon),
         (int)Math.Floor((GridLat0 - point.Latitude) / Dlat));

    /// <summary>The detail cell whose CENTRE is nearest the point (so the point sits deepest inside its coverage).</summary>
    public (int Ci, int Cj) CellForPoint(GeoPoint point)
    {
        double ti = (point.Longitude - GridLon0) / Dlon;
        double tj = (GridLat0 - point.Latitude) / Dlat;
        return (NearestCell(ti), NearestCell(tj));
    }

    // Cell c covers tiles [pitch·c, pitch·c + coverage); its centre tile-coord is pitch·c + coverage/2.
    // The cell nearest a tile-coord t minimises |t − centre| ⇒ round((t − coverage/2) / pitch), clamped ≥ 0.
    private int NearestCell(double t)
    {
        int c = (int)Math.Round((t - coverageTiles / 2.0) / pitchTiles, MidpointRounding.AwayFromZero);
        return Math.Max(0, c);
    }

    /// <summary>Geographic bounds of a cell (its full coverage span).</summary>
    public MapBounds CellBounds(int ci, int cj)
    {
        int i0 = ci * pitchTiles;
        int j0 = cj * pitchTiles;
        double west = GridLon0 + i0 * Dlon;
        double east = GridLon0 + (i0 + coverageTiles) * Dlon;
        double north = GridLat0 - j0 * Dlat;
        double south = GridLat0 - (j0 + coverageTiles) * Dlat;
        return new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
    }

    /// <summary>The coverageTiles² on-disk tiles composing a cell, row-major (north→south, west→east).</summary>
    public IReadOnlyList<(int I, int J)> CellTiles(int ci, int cj)
    {
        int i0 = ci * pitchTiles;
        int j0 = cj * pitchTiles;
        var tiles = new List<(int, int)>(coverageTiles * coverageTiles);
        for (int j = j0; j < j0 + coverageTiles; j++)
        {
            for (int i = i0; i < i0 + coverageTiles; i++)
            {
                tiles.Add((i, j));
            }
        }

        return tiles;
    }

    /// <summary>True when a geographic AABB lies entirely within the cell's coverage (the seam-safety gate).</summary>
    public bool CellContains(int ci, int cj, MapBounds aabb)
    {
        MapBounds b = CellBounds(ci, cj);
        return aabb.SouthWest.Longitude >= b.SouthWest.Longitude
            && aabb.NorthEast.Longitude <= b.NorthEast.Longitude
            && aabb.SouthWest.Latitude >= b.SouthWest.Latitude
            && aabb.NorthEast.Latitude <= b.NorthEast.Latitude;
    }

    /// <summary>Every cell whose coverage intersects a metric-radius box around a point (the streaming ring).</summary>
    public IReadOnlyList<(int Ci, int Cj)> CellsInRadius(GeoPoint centre, double radiusMeters)
    {
        double dLat = radiusMeters / MetersPerLatDeg;
        double dLon = radiusMeters / (MetersPerLatDeg * Math.Cos(centre.Latitude * Math.PI / 180.0));
        double boxW = centre.Longitude - dLon, boxE = centre.Longitude + dLon;
        double boxS = centre.Latitude - dLat, boxN = centre.Latitude + dLat;

        double tiW = (boxW - GridLon0) / Dlon;
        double tiE = (boxE - GridLon0) / Dlon;
        double tjN = (GridLat0 - boxN) / Dlat;   // north = smaller tile-j
        double tjS = (GridLat0 - boxS) / Dlat;

        int ci0 = Math.Max(0, (int)Math.Floor((tiW - coverageTiles) / pitchTiles));
        int ci1 = (int)Math.Floor(tiE / pitchTiles) + 1;
        int cj0 = Math.Max(0, (int)Math.Floor((tjN - coverageTiles) / pitchTiles));
        int cj1 = (int)Math.Floor(tjS / pitchTiles) + 1;

        var cells = new List<(int, int)>();
        for (int cj = cj0; cj <= cj1; cj++)
        {
            for (int ci = ci0; ci <= ci1; ci++)
            {
                if (ci < 0 || cj < 0)
                {
                    continue;
                }

                MapBounds b = CellBounds(ci, cj);
                bool intersects = b.SouthWest.Longitude <= boxE && b.NorthEast.Longitude >= boxW
                               && b.SouthWest.Latitude <= boxN && b.NorthEast.Latitude >= boxS;
                if (intersects)
                {
                    cells.Add((ci, cj));
                }
            }
        }

        return cells;
    }

    /// <summary>Stable int key for a cell (for <see cref="OrthoResidencyPlanner"/>). ci,cj are non-negative in-domain.</summary>
    public int CellKey(int ci, int cj) => (ci * 100000) + cj;

    /// <summary>Inverse of <see cref="CellKey"/>.</summary>
    public (int Ci, int Cj) CellFromKey(int key) => (key / 100000, key % 100000);
}
