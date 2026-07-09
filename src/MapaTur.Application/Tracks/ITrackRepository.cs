using MapaTur.Domain.Tracks;

namespace MapaTur.Application.Tracks;

/// <summary>
/// Persistence port for user-imported off-trail ("pozaszlaki") tracks. Unlike the trail/POI
/// repositories these rows are the user's own imported GPX/TCX polylines: they survive an app
/// restart and are managed (added / listed / removed) from the off-trail panel.
///
/// Only geometry and name are retained — the off-trail layer treats a track as a polyline, so
/// per-point timestamps / heart-rate are not persisted (loaded tracks carry a synthetic timestamp).
/// </summary>
public interface ITrackRepository
{
    /// <summary>
    /// Persists a single imported track. If a track with the same <see cref="Track.Id"/> already
    /// exists it is replaced.
    /// </summary>
    /// <param name="track">Track to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task AddAsync(Track track, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all persisted tracks in the order they were imported (oldest first).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<Track>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the track with the given id. No-op when the id is unknown.
    /// </summary>
    /// <param name="id">Identifier of the track to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Number of persisted tracks.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}