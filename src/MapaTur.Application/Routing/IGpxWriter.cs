using MapaTur.Domain.Routing;

namespace MapaTur.Application.Routing;

/// <summary>
/// Serialises a planned <see cref="Route"/> into the GPX 1.1 format.
/// </summary>
public interface IGpxWriter
{
    /// <summary>
    /// Writes the route to the given output stream as a GPX track, optionally preceded by the planned stops as
    /// named <c>&lt;wpt&gt;</c> waypoints (so the file records what the user searched/tapped, in order).
    /// </summary>
    /// <param name="route">Route to write.</param>
    /// <param name="output">Writable stream that will receive the serialised XML.</param>
    /// <param name="trackName">Human-readable name embedded in the GPX file.</param>
    /// <param name="waypoints">Ordered route stops to emit as named waypoints; null/empty writes none.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task WriteAsync(Route route, Stream output, string trackName, IReadOnlyList<RouteWaypoint>? waypoints = null, CancellationToken cancellationToken = default);
}