using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Trails;

/// <summary>
/// Client for the public Overpass API. Implementations call a configured endpoint,
/// parse the JSON response and return reconstructed <see cref="Trail"/> aggregates.
/// </summary>
public interface IOverpassClient
{
    /// <summary>
    /// Fetches hiking trail relations intersecting the given bounding box.
    /// </summary>
    /// <param name="bounds">Bounding box.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reconstructed trails. Empty when the area contains none.</returns>
    /// <exception cref="HttpRequestException">Thrown when the request fails.</exception>
    /// <exception cref="InvalidDataException">Thrown when the response cannot be parsed.</exception>
    Task<IReadOnlyList<Trail>> FetchHikingTrailsAsync(MapBounds bounds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches exposed/secured route ways (sac_scale demanding/alpine, via_ferrata) intersecting the box —
    /// the demanding "guide" routes drawn as a dotted overlay, distinct from ordinary marked trails.
    /// </summary>
    /// <param name="bounds">Bounding box.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One trail per exposed way. Empty when the area contains none.</returns>
    Task<IReadOnlyList<Trail>> FetchExposedRoutesAsync(MapBounds bounds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pobiera NIEZNAKOWANE ścieżki (highway=path/footway/track jako samodzielne ways) w boxie — perci
    /// bez koloru i bez relacji szlaku, których nie przynosi ani pobieranie relacji, ani tras
    /// eksponowanych (2026-08-05, Rohacze). Zasilają magazyn pozaszlaków dla planera (opt-in).
    /// </summary>
    /// <param name="bounds">Box geograficzny.</param>
    /// <param name="cancellationToken">Token anulowania.</param>
    /// <returns>Jedna trasa na way; pusto, gdy w boxie nic nie ma.</returns>
    Task<IReadOnlyList<Trail>> FetchUnmarkedPathsAsync(MapBounds bounds, CancellationToken cancellationToken = default);
}