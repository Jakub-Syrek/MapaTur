using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Trails;

/// <summary>
/// An official hiking trail with a polyline geometry and zero or more markings.
/// Trails are derived from OSM relations tagged <c>route=hiking</c>.
/// </summary>
public sealed class Trail
{
    /// <summary>
    /// Initializes a new trail.
    /// </summary>
    /// <param name="id">Stable identifier (e.g. OSM relation id).</param>
    /// <param name="name">Human-readable name. Empty when the relation lacks a name tag.</param>
    /// <param name="markings">Trail markings. May be empty for unmarked routes.</param>
    /// <param name="geometry">Ordered list of geographic points describing the polyline.</param>
    /// <exception cref="ArgumentException">Thrown when geometry has fewer than two points.</exception>
    public Trail(long id, string name, IReadOnlyList<TrailMarking> markings, IReadOnlyList<GeoPoint> geometry)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(markings);
        ArgumentNullException.ThrowIfNull(geometry);

        if (geometry.Count < 2)
        {
            throw new ArgumentException("Trail geometry must contain at least two points.", nameof(geometry));
        }

        Id = id;
        Name = name;
        Markings = markings;
        Geometry = geometry;
    }

    /// <summary>OSM relation id (or other stable identifier).</summary>
    public long Id { get; }

    /// <summary>Human-readable name.</summary>
    public string Name { get; }

    /// <summary>All markings carried by this trail (may be more than one for concurrent designations).</summary>
    public IReadOnlyList<TrailMarking> Markings { get; }

    /// <summary>Ordered geographic points forming the trail polyline.</summary>
    public IReadOnlyList<GeoPoint> Geometry { get; }

    /// <summary>Primary marking — first non-<see cref="PttkColor.None"/> entry, or <see cref="PttkColor.None"/> when unmarked.</summary>
    public PttkColor PrimaryColor =>
        Markings.FirstOrDefault(marking => marking.Color != PttkColor.None)?.Color ?? PttkColor.None;
}