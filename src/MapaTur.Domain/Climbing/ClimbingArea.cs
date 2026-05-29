using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Climbing;

/// <summary>
/// A climbing location surfaced from OSM tags. May be a single sport route, a bouldering
/// problem, an entire crag, or an annotated cliff feature; <see cref="Type"/> disambiguates.
/// </summary>
public sealed class ClimbingArea
{
    /// <summary>
    /// Initializes a new climbing area.
    /// </summary>
    /// <param name="id">Stable identifier (typically the OSM node or way id).</param>
    /// <param name="name">Human-readable name (may be empty for unnamed cliffs).</param>
    /// <param name="position">Geographic position (centroid for ways, exact for nodes).</param>
    /// <param name="type">Type classification.</param>
    /// <param name="grade">Difficulty grade as written in the source (e.g. "6a", "VI+", "5.10c"); null if absent.</param>
    /// <param name="lengthMeters">Route length in meters; null if unknown.</param>
    /// <param name="isBolted">True for bolted routes, false for trad, null if unknown.</param>
    public ClimbingArea(
        long id,
        string name,
        GeoPoint position,
        ClimbingType type,
        string? grade = null,
        int? lengthMeters = null,
        bool? isBolted = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (lengthMeters is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lengthMeters), "Length must be non-negative when supplied.");
        }

        Id = id;
        Name = name;
        Position = position;
        Type = type;
        Grade = grade;
        LengthMeters = lengthMeters;
        IsBolted = isBolted;
    }

    /// <summary>Stable identifier (OSM id).</summary>
    public long Id { get; }

    /// <summary>Display name; may be the empty string for unnamed features.</summary>
    public string Name { get; }

    /// <summary>Geographic position.</summary>
    public GeoPoint Position { get; }

    /// <summary>Type classification.</summary>
    public ClimbingType Type { get; }

    /// <summary>Difficulty grade as written in OSM (e.g. "6a", "VI+"), null when absent.</summary>
    public string? Grade { get; }

    /// <summary>Route length in meters, null when absent.</summary>
    public int? LengthMeters { get; }

    /// <summary>True for bolted, false for trad, null when unknown.</summary>
    public bool? IsBolted { get; }
}