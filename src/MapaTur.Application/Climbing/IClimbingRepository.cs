using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Climbing;

/// <summary>
/// Persistence port for climbing areas. Mirrors <see cref="MapaTur.Application.Trails.ITrailRepository"/>
/// so the two domains follow the same shape and can share infrastructure patterns.
/// </summary>
public interface IClimbingRepository
{
    /// <summary>
    /// Inserts or updates the supplied climbing areas. Records with matching ids replace
    /// previously stored values.
    /// </summary>
    /// <param name="areas">Areas to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpsertAsync(IEnumerable<ClimbingArea> areas, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns climbing areas whose position lies inside the given bounding box.
    /// </summary>
    /// <param name="bounds">Bounding box.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Matching climbing areas.</returns>
    Task<IReadOnlyList<ClimbingArea>> FindIntersectingAsync(MapBounds bounds, CancellationToken cancellationToken = default);

    /// <summary>Number of cached climbing-area records (for the settings cache summary).</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes every cached climbing area (premium menu "Wyczyść pobrane dane").</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}