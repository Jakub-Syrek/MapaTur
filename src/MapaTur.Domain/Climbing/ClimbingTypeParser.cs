namespace MapaTur.Domain.Climbing;

/// <summary>
/// Maps OSM tag values to a <see cref="ClimbingType"/>. Source tags vary
/// considerably between mappers; this normalises the most common ones.
/// </summary>
public static class ClimbingTypeParser
{
    /// <summary>
    /// Parses the relevant OSM tags into a <see cref="ClimbingType"/>. Prefers explicit
    /// climbing-route metadata over generic sport tagging.
    /// </summary>
    /// <param name="climbingTag">Value of the <c>climbing</c> tag (e.g. "route", "boulder", "crag").</param>
    /// <param name="climbingSportTag">Value of the <c>climbing:sport</c> tag (e.g. "yes").</param>
    /// <param name="climbingTradTag">Value of the <c>climbing:trad</c> tag.</param>
    /// <param name="climbingMultiPitchTag">Value of the <c>climbing:multipitch</c> tag.</param>
    /// <param name="climbingBoulderTag">Value of the <c>climbing:boulder</c> tag.</param>
    /// <param name="naturalTag">Value of the <c>natural</c> tag (used to detect cliffs).</param>
    /// <returns>Best-effort <see cref="ClimbingType"/>.</returns>
    public static ClimbingType Parse(
        string? climbingTag,
        string? climbingSportTag,
        string? climbingTradTag,
        string? climbingMultiPitchTag,
        string? climbingBoulderTag,
        string? naturalTag)
    {
        if (IsYes(climbingMultiPitchTag) || string.Equals(climbingTag, "multipitch", StringComparison.OrdinalIgnoreCase))
        {
            return ClimbingType.MultiPitch;
        }
        if (IsYes(climbingBoulderTag) || string.Equals(climbingTag, "boulder", StringComparison.OrdinalIgnoreCase))
        {
            return ClimbingType.Boulder;
        }
        if (IsYes(climbingSportTag))
        {
            return ClimbingType.SportRoute;
        }
        if (IsYes(climbingTradTag))
        {
            return ClimbingType.TradRoute;
        }
        if (string.Equals(climbingTag, "crag", StringComparison.OrdinalIgnoreCase)
            || string.Equals(climbingTag, "area", StringComparison.OrdinalIgnoreCase))
        {
            return ClimbingType.Crag;
        }
        if (string.Equals(climbingTag, "route", StringComparison.OrdinalIgnoreCase))
        {
            return ClimbingType.SportRoute;
        }
        if (string.Equals(naturalTag, "cliff", StringComparison.OrdinalIgnoreCase))
        {
            return ClimbingType.Cliff;
        }

        return ClimbingType.Unspecified;
    }

    private static bool IsYes(string? value) =>
        !string.IsNullOrEmpty(value)
        && (value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("1", StringComparison.Ordinal));
}