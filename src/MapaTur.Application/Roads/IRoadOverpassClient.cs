using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Roads;

/// <summary>
/// Client that fetches drivable roads (<c>highway=*</c> ways) from Overpass. Roads are modelled as
/// unmarked <see cref="Trail"/> polylines (no PTTK colour) so they reuse the trail rendering machinery
/// while staying in their own overlay/layer, distinct from hiking trails.
/// </summary>
public interface IRoadOverpassClient
{
    /// <summary>Fetches road ways intersecting the given bounding box as unmarked polylines.</summary>
    /// <exception cref="HttpRequestException">Thrown when the request fails.</exception>
    /// <exception cref="InvalidDataException">Thrown when the response cannot be parsed.</exception>
    Task<IReadOnlyList<Trail>> FetchRoadsAsync(MapBounds bounds, CancellationToken cancellationToken = default);
}