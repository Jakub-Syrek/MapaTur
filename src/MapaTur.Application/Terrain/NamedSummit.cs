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
/// <param name="Curated">True when the hand-maintained list endorses this summit as iconic — the label
/// de-clutter prefers a curated summit over a slightly higher but lesser neighbour (keeps the iconic
/// Kościelec over the marginally taller Zadni Kościelec).</param>
public readonly record struct NamedSummit(string Name, GeoPoint Location, double ElevationMeters, bool Curated = false);