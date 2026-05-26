using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Climbing;

/// <summary>
/// Client that fetches climbing areas from an Overpass API endpoint.
/// </summary>
public interface IClimbingOverpassClient
{
    /// <summary>
    /// Fetches climbing-tagged OSM features intersecting the given bounding box.
    /// </summary>
    /// <param name="bounds">Bounding box.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Reconstructed climbing areas. Empty when the area contains none.</returns>
    /// <exception cref="HttpRequestException">Thrown when the request fails.</exception>
    /// <exception cref="InvalidDataException">Thrown when the response cannot be parsed.</exception>
    Task<IReadOnlyList<ClimbingArea>> FetchClimbingAreasAsync(MapBounds bounds, CancellationToken cancellationToken = default);
}
