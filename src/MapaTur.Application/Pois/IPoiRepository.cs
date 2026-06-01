using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;

namespace MapaTur.Application.Pois;

/// <summary>
/// Persistence port for mountain POIs (huts, shelters, chalets, viewpoints). Mirrors
/// <see cref="MapaTur.Application.Climbing.IClimbingRepository"/> so the point-feature domains
/// share the same shape. Lets downloaded POIs survive an app restart: they are cached on
/// download and re-loaded for the loaded map footprint at startup, so refuges (and their
/// night lights in 3D) reappear without a fresh Overpass round-trip.
/// </summary>
public interface IPoiRepository
{
    /// <summary>
    /// Inserts or updates the supplied POIs. Records with matching ids replace previous values.
    /// </summary>
    /// <param name="pois">POIs to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertAsync(IEnumerable<MountainPoi> pois, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns POIs whose position lies inside the given bounding box.
    /// </summary>
    /// <param name="bounds">Bounding box.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<MountainPoi>> FindIntersectingAsync(MapBounds bounds, CancellationToken cancellationToken = default);
}