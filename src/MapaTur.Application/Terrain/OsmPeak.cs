using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A mountain peak as published by OpenStreetMap (<c>natural=peak</c>). <see cref="Name"/> is the
/// Polish-preferred display name (<c>name:pl</c> when present, otherwise <c>name</c>, empty when the node
/// is unnamed); <see cref="ElevationMeters"/> is the OSM <c>ele</c> tag, null when absent.
/// </summary>
/// <param name="Id">OSM element id (used to de-duplicate the response).</param>
/// <param name="Name">Polish-preferred display name; empty for an unnamed peak.</param>
/// <param name="Position">Node position, or way/relation centre.</param>
/// <param name="ElevationMeters">OSM <c>ele</c> in metres, or null when the tag is absent.</param>
public readonly record struct OsmPeak(long Id, string Name, GeoPoint Position, double? ElevationMeters);