using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A summit detected in a DEM: a geographic location plus its elevation in metres, and an
/// optional name once matched against a gazetteer. Rendered in the 3D view as a labelled marker
/// so the terrain isn't "pusto" — empty — when no trails are loaded.
/// </summary>
/// <param name="Location">Geographic position of the summit cell.</param>
/// <param name="ElevationMeters">
/// Elevation used to <em>seat</em> the marker on the rendered terrain (a DEM sample). The 60 m DEM
/// smooths sharp summits, so this can read low for a knife-edge peak — fine for placement.
/// </param>
/// <param name="Name">Summit name when a gazetteer entry was matched nearby; otherwise null.</param>
/// <param name="LabelElevationMeters">
/// Authoritative elevation to <em>display</em> (e.g. the gazetteer's published height), when it differs
/// from the DEM-sampled <see cref="ElevationMeters"/>. Null → the label shows <see cref="ElevationMeters"/>.
/// </param>
public readonly record struct TerrainPeak(
    GeoPoint Location,
    double ElevationMeters,
    string? Name = null,
    double? LabelElevationMeters = null);