using MapaTur.Domain.Geography;

namespace MapaTur.Application.Routing;

/// <summary>Where the camera should sit to show a route: the point to centre on and the orbit distance.</summary>
/// <param name="Center">Geographic centre of the route's bounding box.</param>
/// <param name="DistanceMeters">Orbit distance sized to fit the route's extent (clamped to a sane floor).</param>
public readonly record struct RouteFraming(GeoPoint Center, double DistanceMeters);

/// <summary>
/// Picks a camera framing that shows a whole planned route, like a tourist-map app "fit to route":
/// centre on the route's bounding-box midpoint and sit back proportionally to its diagonal extent, so a
/// short loop frames close and a long traverse pulls the camera out. A floor keeps tiny routes from
/// slamming the eye into the ground.
/// </summary>
public static class RouteCameraFraming
{
    // Below this the camera would sit uncomfortably close even for a near-zero-length route.
    private const double MinDistanceMeters = 3000.0;

    // Orbit distance ≈ extent × this. >1 so the route sits inside the frame with margin (the camera is
    // pitched, not straight down, so a little extra pull-back keeps both ends on screen).
    private const double FitFactor = 1.1;

    /// <summary>Frames <paramref name="polyline"/> (the route's points in order).</summary>
    /// <exception cref="ArgumentException">The polyline is empty.</exception>
    public static RouteFraming Fit(IReadOnlyList<GeoPoint> polyline)
    {
        ArgumentNullException.ThrowIfNull(polyline);
        if (polyline.Count == 0)
        {
            throw new ArgumentException("Cannot frame an empty route.", nameof(polyline));
        }

        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;
        foreach (GeoPoint p in polyline)
        {
            if (p.Latitude < minLat) { minLat = p.Latitude; }
            if (p.Latitude > maxLat) { maxLat = p.Latitude; }
            if (p.Longitude < minLon) { minLon = p.Longitude; }
            if (p.Longitude > maxLon) { maxLon = p.Longitude; }
        }

        var center = new GeoPoint((minLat + maxLat) / 2.0, (minLon + maxLon) / 2.0);
        double diagonalMeters = new GeoPoint(minLat, minLon).HaversineDistanceMetersTo(new GeoPoint(maxLat, maxLon));
        double distance = Math.Max(MinDistanceMeters, diagonalMeters * FitFactor);
        return new RouteFraming(center, distance);
    }
}