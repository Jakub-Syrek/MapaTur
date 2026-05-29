namespace MapaTur.Domain.Climbing;

/// <summary>
/// Coarse classification of a climbing feature as derived from OSM <c>sport</c> and
/// <c>climbing</c> tags. Used to pick a marker icon and filter on the map.
/// </summary>
public enum ClimbingType
{
    /// <summary>Type is missing or unrecognised in the source tags.</summary>
    Unspecified = 0,

    /// <summary>Single sport-climbing route (typically bolted).</summary>
    SportRoute = 1,

    /// <summary>Trad route (gear placements).</summary>
    TradRoute = 2,

    /// <summary>Multi-pitch route (often mixed sport/trad).</summary>
    MultiPitch = 3,

    /// <summary>Bouldering problem (short, no rope).</summary>
    Boulder = 4,

    /// <summary>A whole crag / sector with many routes.</summary>
    Crag = 5,

    /// <summary>Natural cliff feature, possibly with routes.</summary>
    Cliff = 6,
}