using MapaTur.Domain.Tracks;

namespace MapaTur.Application.Tracks;

/// <summary>
/// Parses GPS Exchange Format (GPX 1.0 / 1.1) into domain <see cref="Track"/> aggregates.
/// Both <c>&lt;trk&gt;</c> (recorded tracks) and <c>&lt;rte&gt;</c> (planned routes) are materialised as tracks,
/// since off-trail imports commonly arrive in either shape.
/// </summary>
public interface IGpxParser
{
    /// <summary>
    /// Reads a GPX stream and returns the contained tracks. A single GPX file may contain multiple
    /// <c>&lt;trk&gt;</c>/<c>&lt;rte&gt;</c> elements; each becomes one track (all its segments concatenated).
    /// Points without a <c>&lt;time&gt;</c> element are kept (planning exports routinely omit timestamps).
    /// </summary>
    /// <param name="stream">Readable stream positioned at the start of the GPX document.</param>
    /// <param name="fallbackName">Name to assign when a track/route has no <c>&lt;name&gt;</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tracks parsed from the document (each with at least two points).</returns>
    /// <exception cref="InvalidDataException">Thrown when the stream does not contain a valid GPX document.</exception>
    Task<IReadOnlyList<Track>> ParseAsync(Stream stream, string fallbackName, CancellationToken cancellationToken = default);
}