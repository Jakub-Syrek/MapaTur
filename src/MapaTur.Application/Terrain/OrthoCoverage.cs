using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Geographic placement of a gridded orthophoto: the bounds it covers and its <see cref="GridCols"/> ×
/// <see cref="GridRows"/> cell grid. Lets a sub-region mesh (e.g. a streaming-LOD detail tile, whose extent
/// is smaller than the ortho) sample the right ortho cell at the right UV by GEOGRAPHIC position rather than
/// by [0,1] local to its own raster. Row 0 is the NORTH row (the ortho pixel buffer is top-row-first), so the
/// V axis is north→south and is not flipped.
/// </summary>
/// <param name="Bounds">Geographic extent the whole ortho grid covers.</param>
/// <param name="GridCols">Number of ortho cells west→east (≥ 1).</param>
/// <param name="GridRows">Number of ortho cells north→south (≥ 1).</param>
public sealed record OrthoCoverage(MapBounds Bounds, int GridCols, int GridRows)
{
    private double West => Bounds.SouthWest.Longitude;
    private double East => Bounds.NorthEast.Longitude;
    private double North => Bounds.NorthEast.Latitude;
    private double South => Bounds.SouthWest.Latitude;

    /// <summary>Fraction [0,1] west→east of a point within the coverage (clamped).</summary>
    private double GlobalU(GeoPoint point) => Clamp01((point.Longitude - West) / (East - West));

    /// <summary>Fraction [0,1] north→south of a point within the coverage (clamped); 0 = north edge.</summary>
    private double GlobalV(GeoPoint point) => Clamp01((North - point.Latitude) / (North - South));

    /// <summary>
    /// True when the point lies within the ortho's geographic coverage. A mesh tile whose centre is OUTSIDE
    /// must NOT sample the ortho — CellAt/LocalUv clamp out-of-coverage points to the edge cell, so the edge
    /// texels stretch along the terrain (the "geological strata" seam banding). Such tiles render hypsometric.
    /// </summary>
    public bool Covers(GeoPoint point) => Bounds.Contains(point);

    /// <summary>
    /// The ortho grid cell a point falls in, and its flat tile index (<c>row * GridCols + col</c>). Points
    /// outside the coverage clamp to the nearest edge cell.
    /// </summary>
    public (int Col, int Row, int TileIndex) CellAt(GeoPoint point)
    {
        int col = Math.Clamp((int)Math.Floor(GlobalU(point) * GridCols), 0, GridCols - 1);
        int row = Math.Clamp((int)Math.Floor(GlobalV(point) * GridRows), 0, GridRows - 1);
        return (col, row, (row * GridCols) + col);
    }

    /// <summary>
    /// UV of a point LOCAL to the given cell (each cell spans the full [0,1] texture). Clamped to [0,1] so a
    /// vertex that geographically falls just outside the cell (MVP: no per-cell tile cutting) samples the cell
    /// edge instead of bleeding into a neighbour. V is north→south (not flipped).
    /// </summary>
    public (float U, float V) LocalUv(GeoPoint point, int col, int row)
    {
        float u = (float)Clamp01((GlobalU(point) * GridCols) - col);
        float v = (float)Clamp01((GlobalV(point) * GridRows) - row);
        return (u, v);
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}