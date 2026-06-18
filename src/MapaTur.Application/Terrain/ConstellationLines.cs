namespace MapaTur.Application.Terrain;

/// <summary>A constellation line resolved to viewport pixel endpoints, ready for the overlay line pass.</summary>
public readonly record struct StarSegment(float X1, float Y1, float X2, float Y2);

/// <summary>
/// Baked asterism topology (star-name pairs) plus the resolver that turns it into drawable screen segments
/// given the currently on-screen <see cref="StarLabel"/>s. Only segments whose <em>both</em> endpoints are
/// visible are emitted, so a constellation that runs off the edge of the view loses just the affected lines.
/// The bundled catalog carries the full Ursa Major (Big Dipper); other constellations don't yet have enough
/// stars baked for a shape, so they simply contribute no segments.
/// </summary>
public static class ConstellationLines
{
    /// <summary>Star-name pairs forming the Big Dipper: the handle (Alkaid→Megrez) then the bowl loop.</summary>
    public static IReadOnlyList<(string From, string To)> Segments { get; } = new[]
    {
        ("Alkaid", "Mizar"), ("Mizar", "Alioth"), ("Alioth", "Megrez"),                  // handle
        ("Megrez", "Dubhe"), ("Dubhe", "Merak"), ("Merak", "Phecda"), ("Phecda", "Megrez"), // bowl
    };

    /// <summary>
    /// Resolves <see cref="Segments"/> against the visible star labels into screen-space line segments,
    /// dropping any whose endpoints aren't both currently on screen.
    /// </summary>
    public static IReadOnlyList<StarSegment> ResolveScreenSegments(IReadOnlyList<StarLabel> visibleStars)
    {
        ArgumentNullException.ThrowIfNull(visibleStars);

        var byName = new Dictionary<string, (float X, float Y)>(visibleStars.Count);
        for (int i = 0; i < visibleStars.Count; i++)
        {
            StarLabel star = visibleStars[i];
            byName[star.Name] = (star.ScreenX, star.ScreenY);
        }

        var result = new List<StarSegment>();
        foreach ((string from, string to) in Segments)
        {
            if (byName.TryGetValue(from, out (float X, float Y) a) && byName.TryGetValue(to, out (float X, float Y) b))
            {
                result.Add(new StarSegment(a.X, a.Y, b.X, b.Y));
            }
        }

        return result;
    }
}