using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Trails;

/// <summary>
/// Persistence port for hiking trails. Implementations typically back to SQLite.
/// </summary>
public interface ITrailRepository
{
    /// <summary>
    /// Inserts or updates the given trails in storage. Existing trails with matching ids
    /// are replaced.
    /// </summary>
    /// <param name="trails">Trails to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpsertAsync(IEnumerable<Trail> trails, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all trails whose envelope intersects the given bounding box.
    /// </summary>
    /// <param name="bounds">Bounding box.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching trails.</returns>
    Task<IReadOnlyList<Trail>> FindIntersectingAsync(MapBounds bounds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all trails whose envelope intersects the given bounding box, with each
    /// trail's geometry simplified by Douglas–Peucker at <paramref name="simplificationEpsilonMeters"/>.
    /// Lets render-time consumers ask for a per-zoom LOD without paying the storage cost.
    /// </summary>
    /// <param name="bounds">Bounding box.</param>
    /// <param name="simplificationEpsilonMeters">Tolerance, in metres. 0 means no read-time simplification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Trail>> FindIntersectingAsync(MapBounds bounds, double simplificationEpsilonMeters, CancellationToken cancellationToken = default);
}