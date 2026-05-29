using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A summit detected in a DEM: a geographic location plus its elevation in metres, and an
/// optional name once matched against a gazetteer. Rendered in the 3D view as a labelled marker
/// so the terrain isn't "pusto" — empty — when no trails are loaded.
/// </summary>
/// <param name="Location">Geographic position of the summit cell.</param>
/// <param name="ElevationMeters">Elevation at the summit, in metres above sea level.</param>
/// <param name="Name">Summit name when a gazetteer entry was matched nearby; otherwise null.</param>
public readonly record struct TerrainPeak(GeoPoint Location, double ElevationMeters, string? Name = null);