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
}
