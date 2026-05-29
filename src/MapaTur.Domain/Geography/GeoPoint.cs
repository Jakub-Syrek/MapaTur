using System.Globalization;

namespace MapaTur.Domain.Geography;

/// <summary>
/// Immutable geographic point with latitude, longitude and optional elevation.
/// Latitude is in degrees within the range [-90, 90].
/// Longitude is in degrees within the range [-180, 180].
/// Elevation is expressed in meters above mean sea level; null if unknown.
/// </summary>
public readonly record struct GeoPoint
{
    private const double MinLatitude = -90.0;
    private const double MaxLatitude = 90.0;
    private const double MinLongitude = -180.0;
    private const double MaxLongitude = 180.0;
    private const double EarthRadiusMeters = 6_371_000.0;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeoPoint"/> struct.
    /// </summary>
    /// <param name="latitude">Latitude in degrees, must be within [-90, 90].</param>
    /// <param name="longitude">Longitude in degrees, must be within [-180, 180].</param>
    /// <param name="elevationMeters">Optional elevation in meters above mean sea level.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when latitude or longitude is out of range, or any value is non-finite.</exception>
    public GeoPoint(double latitude, double longitude, double? elevationMeters = null)
    {
        if (!double.IsFinite(latitude) || latitude is < MinLatitude or > MaxLatitude)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be a finite value within [-90, 90].");
        }

        if (!double.IsFinite(longitude) || longitude is < MinLongitude or > MaxLongitude)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be a finite value within [-180, 180].");
        }

        if (elevationMeters is { } elevation && !double.IsFinite(elevation))
        {
            throw new ArgumentOutOfRangeException(nameof(elevationMeters), elevationMeters, "Elevation must be a finite value when provided.");
        }

        Latitude = latitude;
        Longitude = longitude;
        ElevationMeters = elevationMeters;
    }

    /// <summary>Latitude in degrees.</summary>
    public double Latitude { get; }

    /// <summary>Longitude in degrees.</summary>
    public double Longitude { get; }

    /// <summary>Elevation in meters above mean sea level, or null if unknown.</summary>
    public double? ElevationMeters { get; }

    /// <summary>
    /// Computes the great-circle distance to another point on Earth using the haversine formula.
    /// Elevation is ignored.
    /// </summary>
    /// <param name="other">The destination point.</param>
    /// <returns>Distance in meters.</returns>
    public double HaversineDistanceMetersTo(GeoPoint other)
    {
        double lat1 = ToRadians(Latitude);
        double lat2 = ToRadians(other.Latitude);
        double deltaLat = ToRadians(other.Latitude - Latitude);
        double deltaLon = ToRadians(other.Longitude - Longitude);

        double a = (Math.Sin(deltaLat / 2.0) * Math.Sin(deltaLat / 2.0))
                 + (Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2.0) * Math.Sin(deltaLon / 2.0));

        double c = 2.0 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1.0 - a));

        return EarthRadiusMeters * c;
    }

    /// <summary>Returns an invariant-culture string representation of the point.</summary>
    public override string ToString()
    {
        return ElevationMeters is { } elevation
            ? string.Create(CultureInfo.InvariantCulture, $"({Latitude:F6}, {Longitude:F6}, {elevation:F1}m)")
            : string.Create(CultureInfo.InvariantCulture, $"({Latitude:F6}, {Longitude:F6})");
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}