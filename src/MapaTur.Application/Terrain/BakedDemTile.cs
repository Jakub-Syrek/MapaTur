using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// One pre-repaired DEM tile of the offline baked pyramid: the elevation grid for a single slippy tile
/// AFTER the expensive per-tile repair (FillNarrowZeroStrips + FillPits) has already been applied, plus
/// the tile's identity and geographic frame. Persisted to the <c>baked/</c> cache by
/// <see cref="BakedDemTileStore"/> so a later LOD stage can STREAM finished heightmaps instead of
/// mosaicking and repairing raw DEM at render time (the current per-build cost the engine re-architecture
/// is removing).
///
/// Samples are stored row-major in <see cref="Heights"/>, matching
/// <see cref="MapaTur.Domain.Terrain.DemRaster"/>: row 0 lies along the north edge of <see cref="Bounds"/>,
/// the last row along the south edge, column 0 along the west edge. A cell equal to
/// <see cref="NoDataValue"/> is a hole the mesh holes through to a coarser level.
/// </summary>
public sealed class BakedDemTile
{
    /// <summary>Initializes a baked tile.</summary>
    /// <param name="zoom">Slippy zoom level of the tile.</param>
    /// <param name="tileX">Slippy tile column.</param>
    /// <param name="tileY">Slippy tile row.</param>
    /// <param name="columns">Number of grid columns (west to east). Must be at least 1.</param>
    /// <param name="rows">Number of grid rows (north to south). Must be at least 1.</param>
    /// <param name="bounds">Geographic extent the grid covers (the tile's slippy bounds).</param>
    /// <param name="noDataValue">Sentinel value used in <paramref name="heights"/> for missing data.</param>
    /// <param name="heights">Row-major elevation samples in metres. Length must equal columns × rows.</param>
    /// <exception cref="ArgumentNullException"><paramref name="heights"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is below 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="heights"/> length mismatches the dimensions.</exception>
    public BakedDemTile(
        int zoom, int tileX, int tileY, int columns, int rows, MapBounds bounds, double noDataValue, float[] heights)
        : this(zoom, tileX, tileY, columns, rows, bounds, noDataValue, heights, detailRms: null)
    {
    }

    /// <summary>Initializes a baked tile that also carries a per-cell mid-frequency detail channel.</summary>
    /// <param name="zoom">Slippy zoom level of the tile.</param>
    /// <param name="tileX">Slippy tile column.</param>
    /// <param name="tileY">Slippy tile row.</param>
    /// <param name="columns">Number of grid columns (west to east). Must be at least 1.</param>
    /// <param name="rows">Number of grid rows (north to south). Must be at least 1.</param>
    /// <param name="bounds">Geographic extent the grid covers (the tile's slippy bounds).</param>
    /// <param name="noDataValue">Sentinel value used in <paramref name="heights"/> for missing data.</param>
    /// <param name="heights">Row-major elevation samples in metres. Length must equal columns × rows.</param>
    /// <param name="detailRms">Optional per-cell RMS (metres) of the sub-cell relief a coarsening downsample
    /// discarded (see <see cref="DetailRms"/>). Row-major, same dimensions as <paramref name="heights"/>, or null
    /// for a tile that carries no detail (the finest baked level and legacy tiles). Used only for look-only shading.</param>
    /// <exception cref="ArgumentNullException"><paramref name="heights"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is below 1.</exception>
    /// <exception cref="ArgumentException"><paramref name="heights"/> (or <paramref name="detailRms"/>) length mismatches the dimensions.</exception>
    public BakedDemTile(
        int zoom, int tileX, int tileY, int columns, int rows, MapBounds bounds, double noDataValue,
        float[] heights, float[]? detailRms)
    {
        ArgumentNullException.ThrowIfNull(heights);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        if (heights.Length != columns * rows)
        {
            throw new ArgumentException(
                $"heights length ({heights.Length}) must equal columns ({columns}) × rows ({rows}) = {columns * rows}.",
                nameof(heights));
        }

        if (detailRms is not null && detailRms.Length != columns * rows)
        {
            throw new ArgumentException(
                $"detailRms length ({detailRms.Length}) must equal columns ({columns}) × rows ({rows}) = {columns * rows}.",
                nameof(detailRms));
        }

        Zoom = zoom;
        TileX = tileX;
        TileY = tileY;
        Columns = columns;
        Rows = rows;
        Bounds = bounds;
        NoDataValue = noDataValue;
        Heights = heights;
        DetailRms = detailRms;
    }

    /// <summary>Slippy zoom level of the tile.</summary>
    public int Zoom { get; }

    /// <summary>Slippy tile column.</summary>
    public int TileX { get; }

    /// <summary>Slippy tile row.</summary>
    public int TileY { get; }

    /// <summary>Number of grid columns (west to east).</summary>
    public int Columns { get; }

    /// <summary>Number of grid rows (north to south).</summary>
    public int Rows { get; }

    /// <summary>Geographic extent the grid covers (the tile's slippy bounds).</summary>
    public MapBounds Bounds { get; }

    /// <summary>Sentinel value used in <see cref="Heights"/> for missing data.</summary>
    public double NoDataValue { get; }

    /// <summary>Row-major repaired elevation samples in metres (length <see cref="Columns"/> × <see cref="Rows"/>).</summary>
    public float[] Heights { get; }

    /// <summary>
    /// Optional per-cell RMS (in metres) of the SUB-CELL relief that a coarsening box-average discarded when this
    /// coarse tile was derived from its finer parent — i.e. how bumpy the ground under each coarse cell really was
    /// at the finer resolution. Null on the finest baked level (which keeps its relief in the geometry) and on
    /// legacy tiles baked before the detail channel existed. The renderer uses it, per vertex, to shade a coarse
    /// tile as if it still had those bumps (look-only; it never changes geometry, biome bands or snow lines).
    /// Row-major, same dimensions as <see cref="Heights"/> when present.
    /// </summary>
    public float[]? DetailRms { get; }

    /// <summary>The tile's slippy address as a <see cref="DemTileKey"/>.</summary>
    public DemTileKey Key => new(Zoom, TileX, TileY);
}