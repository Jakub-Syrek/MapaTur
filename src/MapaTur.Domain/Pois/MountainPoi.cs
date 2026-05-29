using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Pois;

/// <summary>
/// A point-of-interest in the mountains surfaced from OSM — a hut, shelter, chalet or viewpoint.
/// Rendered as a marker (with a kind-specific glyph) in the 2D map and the 3D view.
/// </summary>
public sealed class MountainPoi
{
    /// <summary>Initializes a new POI.</summary>
    /// <param name="id">Stable identifier (typically the OSM node/way id).</param>
    /// <param name="name">Display name; may be empty for unnamed features.</param>
    /// <param name="position">Geographic position (exact for nodes, centroid for ways).</param>
    /// <param name="kind">Category used to pick the glyph/colour.</param>
    /// <param name="elevationMeters">Elevation in metres when tagged; null otherwise.</param>
    public MountainPoi(long id, string name, GeoPoint position, PoiKind kind, double? elevationMeters = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        Id = id;
        Name = name;
        Position = position;
        Kind = kind;
        ElevationMeters = elevationMeters;
    }

    /// <summary>Stable identifier (OSM id).</summary>
    public long Id { get; }

    /// <summary>Display name; may be the empty string for unnamed features.</summary>
    public string Name { get; }

    /// <summary>Geographic position.</summary>
    public GeoPoint Position { get; }

    /// <summary>Category (hut, shelter, chalet, viewpoint).</summary>
    public PoiKind Kind { get; }

    /// <summary>Elevation in metres when tagged; null otherwise.</summary>
    public double? ElevationMeters { get; }
}