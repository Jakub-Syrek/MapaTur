namespace MapaTur.Domain.Trails;

/// <summary>
/// Polish Tourist Society (PTTK) color convention for marked hiking trails.
/// Maps directly to the colours used by OSM <c>osmc:symbol</c> on Polish trails.
/// </summary>
public enum PttkColor
{
    /// <summary>Unknown or unmarked trail.</summary>
    None = 0,

    /// <summary>Red — usually main long-distance trails.</summary>
    Red = 1,

    /// <summary>Blue — long-distance regional trails.</summary>
    Blue = 2,

    /// <summary>Green — connecting and side trails.</summary>
    Green = 3,

    /// <summary>Yellow — short local connectors.</summary>
    Yellow = 4,

    /// <summary>Black — short steep variants and access trails.</summary>
    Black = 5,
}