using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Terrain;

/// <summary>
/// Digital elevation model raster: an evenly-spaced 2D grid of elevation samples
/// (meters above mean sea level) covering a geographic bounding box.
///
/// The grid is stored row-major in <see cref="Samples"/>. Row 0 lies along the
/// north edge of <see cref="Bounds"/>; the last row lies along the south edge.
/// Column 0 lies along the west edge.
/// </summary>
public sealed class DemRaster
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DemRaster"/> class.
    /// </summary>
    /// <param name="columns">Number of columns (west to east). Must be at least 2.</param>
    /// <param name="rows">Number of rows (north to south). Must be at least 2.</param>
    /// <param name="bounds">Geographic extent covered by the grid.</param>
    /// <param name="samples">Row-major elevation samples (meters). Length must equal columns × rows.</param>
    /// <param name="noDataValue">Sentinel value used in <paramref name="samples"/> for missing data.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for invalid dimensions.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="samples"/> length mismatches dimensions.</exception>
    public DemRaster(int columns, int rows, MapBounds bounds, float[] samples, float noDataValue = -9999.0f)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (columns < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), columns, "columns must be at least 2.");
        }

        if (rows < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "rows must be at least 2.");
        }

        if (samples.Length != columns * rows)
        {
            throw new ArgumentException(
                $"samples length ({samples.Length}) must equal columns ({columns}) × rows ({rows}) = {columns * rows}.",
                nameof(samples));
        }

        Columns = columns;
        Rows = rows;
        Bounds = bounds;
        Samples = samples;
        NoDataValue = noDataValue;
    }

    /// <summary>Number of grid columns (west to east).</summary>
    public int Columns { get; }

    /// <summary>Number of grid rows (north to south).</summary>
    public int Rows { get; }

    /// <summary>Geographic extent of the grid.</summary>
    public MapBounds Bounds { get; }

    /// <summary>Row-major elevation samples in meters.</summary>
    public float[] Samples { get; }

    /// <summary>Sentinel value used in <see cref="Samples"/> for missing data.</summary>
    public float NoDataValue { get; }

    /// <summary>West edge longitude in degrees.</summary>
    public double West => Bounds.SouthWest.Longitude;

    /// <summary>South edge latitude in degrees.</summary>
    public double South => Bounds.SouthWest.Latitude;

    /// <summary>East edge longitude in degrees.</summary>
    public double East => Bounds.NorthEast.Longitude;

    /// <summary>North edge latitude in degrees.</summary>
    public double North => Bounds.NorthEast.Latitude;

    /// <summary>
    /// Returns the elevation at grid cell (column, row). Row 0 = north edge.
    /// </summary>
    public float this[int column, int row] => Samples[(row * Columns) + column];

    /// <summary>
    /// Bilinearly samples elevation at the given geographic coordinate.
    /// Coordinates outside <see cref="Bounds"/> are clamped to the nearest edge.
    /// </summary>
    /// <param name="longitude">Longitude in degrees.</param>
    /// <param name="latitude">Latitude in degrees.</param>
    /// <returns>Interpolated elevation in meters.</returns>
    public double SampleBilinear(double longitude, double latitude)
    {
        double rowFloat = (North - latitude) / (North - South) * (Rows - 1);
        double colFloat = (longitude - West) / (East - West) * (Columns - 1);

        rowFloat = Math.Clamp(rowFloat, 0.0, Rows - 1.0);
        colFloat = Math.Clamp(colFloat, 0.0, Columns - 1.0);

        int r0 = (int)Math.Floor(rowFloat);
        int c0 = (int)Math.Floor(colFloat);
        int r1 = Math.Min(r0 + 1, Rows - 1);
        int c1 = Math.Min(c0 + 1, Columns - 1);

        double dr = rowFloat - r0;
        double dc = colFloat - c0;

        double v00 = this[c0, r0];
        double v01 = this[c1, r0];
        double v10 = this[c0, r1];
        double v11 = this[c1, r1];

        double top = (v00 * (1.0 - dc)) + (v01 * dc);
        double bottom = (v10 * (1.0 - dc)) + (v11 * dc);
        return (top * (1.0 - dr)) + (bottom * dr);
    }

    /// <summary>
    /// Returns (min, max) elevation across all samples not equal to <see cref="NoDataValue"/>.
    /// Returns (0, 0) when all samples are no-data.
    /// </summary>
    public (double Min, double Max) GetElevationRange()
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        for (int i = 0; i < Samples.Length; i++)
        {
            float v = Samples[i];
            if (v == NoDataValue)
            {
                continue;
            }

            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }
        }

        if (double.IsPositiveInfinity(min))
        {
            return (0.0, 0.0);
        }

        return (min, max);
    }
}
