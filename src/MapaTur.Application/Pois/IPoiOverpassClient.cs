using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;

namespace MapaTur.Application.Pois;

/// <summary>Client that fetches mountain POIs (huts, shelters, chalets, viewpoints) from Overpass.</summary>
public interface IPoiOverpassClient
{
    /// <summary>Fetches POI-tagged OSM features intersecting the given bounding box.</summary>
    /// <exception cref="HttpRequestException">Thrown when the request fails.</exception>
    /// <exception cref="InvalidDataException">Thrown when the response cannot be parsed.</exception>
    Task<IReadOnlyList<MountainPoi>> FetchPoisAsync(MapBounds bounds, CancellationToken cancellationToken = default);
}