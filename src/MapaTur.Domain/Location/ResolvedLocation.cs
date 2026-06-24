namespace MapaTur.Domain.Location;

/// <summary>
/// The location to display right now, as decided by the location resolver: the best fix to show, how fresh it
/// is, and its age. Once any fix has been accepted, <see cref="Fix"/> is never null again regardless of
/// staleness — the UI fades the marker and grows its accuracy halo using <see cref="Freshness"/> /
/// <see cref="IsFlaggedLastKnown"/> rather than making it disappear (important when GPS drops in the mountains).
/// </summary>
/// <param name="Fix">The fix to render, or null only when no fix has ever been accepted.</param>
/// <param name="Freshness">How current the fix is.</param>
/// <param name="Age">Time since the fix's timestamp at the resolve instant; null when there is no fix. Never negative.</param>
/// <param name="IsFlaggedLastKnown">True when the fix is shown as last-known (Stale/Lost) rather than Live.</param>
public sealed record ResolvedLocation(
    MapaTur.Domain.Location.UserLocation? Fix,
    LocationFreshness Freshness,
    TimeSpan? Age,
    bool IsFlaggedLastKnown)
{
    /// <summary>The empty result: no fix has ever been accepted.</summary>
    public static readonly ResolvedLocation None = new(null, LocationFreshness.None, null, false);
}