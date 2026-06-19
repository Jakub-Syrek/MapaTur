using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A single straight iso-elevation contour segment in geographic coordinates. <see cref="ElevationMeters"/>
/// is the level the segment traces, so the renderer can style minor vs major (index) contours and drape
/// the line on the 3D relief at that height.
/// </summary>
public readonly record struct ContourSegment(double ElevationMeters, GeoPoint Start, GeoPoint End);