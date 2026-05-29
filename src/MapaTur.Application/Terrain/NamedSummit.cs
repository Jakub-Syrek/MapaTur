using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A named summit in a gazetteer — the reference data <see cref="PeakNamer"/> matches DEM-detected
/// <see cref="TerrainPeak"/>s against. <see cref="ElevationMeters"/> is the published summit height
/// (used only for reference/display); matching is by <see cref="Location"/> alone.
/// </summary>
/// <param name="Name">Summit name.</param>
/// <param name="Location">Published geographic position of the summit.</param>
/// <param name="ElevationMeters">Published elevation in metres above sea level.</param>
public readonly record struct NamedSummit(string Name, GeoPoint Location, double ElevationMeters);