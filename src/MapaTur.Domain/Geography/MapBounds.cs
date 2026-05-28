namespace MapaTur.Domain.Geography;

/// <summary>
/// Axis-aligned geographic bounding box defined by minimum and maximum corners.
/// </summary>
public readonly record struct MapBounds
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MapBounds"/> struct.
    /// </summary>
    /// <param name="southWest">The south-west (minimum latitude, minimum longitude) corner.</param>
    /// <param name="northEast">The north-east (maximum latitude, maximum longitude) corner.</param>
    /// <exception cref="ArgumentException">Thrown when the box is degenerate (corners swapped).</exception>
    public MapBounds(GeoPoint southWest, GeoPoint northEast)
    {
        if (southWest.Latitude > northEast.Latitude || southWest.Longitude > northEast.Longitude)
        {
            throw new ArgumentException("South-west corner must have smaller latitude and longitude than north-east corner.", nameof(southWest));
        }

        SouthWest = southWest;
        NorthEast = northEast;
    }

    /// <summary>South-west corner (minimum latitude, minimum longitude).</summary>
    public GeoPoint SouthWest { get; }

    /// <summary>North-east corner (maximum latitude, maximum longitude).</summary>
    public GeoPoint NorthEast { get; }

    /// <summary>Center point of the bounding box.</summary>
    public GeoPoint Center => new(
        (SouthWest.Latitude + NorthEast.Latitude) / 2.0,
        (SouthWest.Longitude + NorthEast.Longitude) / 2.0);

    /// <summary>
    /// Returns true if the given point is inside the bounding box (inclusive on edges).
    /// </summary>
    /// <param name="point">Point to test.</param>
    /// <returns>True if the point is inside the bounds.</returns>
    public bool Contains(GeoPoint point)
    {
        return point.Latitude >= SouthWest.Latitude
            && point.Latitude <= NorthEast.Latitude
            && point.Longitude >= SouthWest.Longitude
            && point.Longitude <= NorthEast.Longitude;
    }

    /// <summary>
    /// Returns the geographic intersection of this box with another, or null when
    /// the two boxes are disjoint (or touch on an edge, which has zero area).
    /// </summary>
    /// <param name="other">The other bounding box.</param>
    /// <returns>The overlap rectangle, or null if there is no overlap.</returns>
    /// <summary>
    /// Returns the smallest axis-aligned bounding box that encloses both this and
    /// <paramref name="other"/>. Never null — two valid boxes always have a union.
    /// </summary>
    public MapBounds Union(MapBounds other)
    {
        double south = Math.Min(SouthWest.Latitude, other.SouthWest.Latitude);
        double north = Math.Max(NorthEast.Latitude, other.NorthEast.Latitude);
        double west = Math.Min(SouthWest.Longitude, other.SouthWest.Longitude);
        double east = Math.Max(NorthEast.Longitude, other.NorthEast.Longitude);
        return new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
    }

    public MapBounds? Intersect(MapBounds other)
    {
        double south = Math.Max(SouthWest.Latitude, other.SouthWest.Latitude);
        double north = Math.Min(NorthEast.Latitude, other.NorthEast.Latitude);
        double west = Math.Max(SouthWest.Longitude, other.SouthWest.Longitude);
        double east = Math.Min(NorthEast.Longitude, other.NorthEast.Longitude);

        if (north <= south || east <= west)
        {
            return null;
        }

        return new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
    }
}
