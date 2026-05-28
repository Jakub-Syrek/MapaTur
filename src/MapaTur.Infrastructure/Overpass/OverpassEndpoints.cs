namespace MapaTur.Infrastructure.Overpass;

/// <summary>
/// Public Overpass API endpoints we will attempt in order, with the official endpoint
/// first and community / academic mirrors as fallbacks. The list lets us survive the
/// main endpoint's frequent 504 Gateway Timeouts under load.
/// </summary>
public static class OverpassEndpoints
{
    /// <summary>
    /// Default ordered list of endpoints, tried left-to-right. The first entry is the
    /// official endpoint; subsequent entries are widely-used community mirrors. All
    /// expose the same Overpass QL POST API at <c>/api/interpreter</c>.
    /// </summary>
    public static IReadOnlyList<Uri> DefaultFallbackList { get; } = new[]
    {
        new Uri("https://overpass-api.de/api/interpreter"),
        new Uri("https://overpass.kumi.systems/api/interpreter"),
        new Uri("https://lz4.overpass-api.de/api/interpreter"),
        new Uri("https://overpass.private.coffee/api/interpreter"),
        new Uri("https://overpass.openstreetmap.fr/api/interpreter"),
    };
}