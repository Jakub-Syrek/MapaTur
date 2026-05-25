using MapaTur.Domain.Tracks;

namespace MapaTur.Application.Tracks;

/// <summary>
/// Parses Garmin Training Center XML (TCX v2) into domain <see cref="Track"/> aggregates.
/// </summary>
public interface ITcxParser
{
    /// <summary>
    /// Reads a TCX stream and returns the contained tracks. A single TCX file may contain
    /// multiple activities and multiple laps; each activity is materialised as one track.
    /// </summary>
    /// <param name="stream">Readable stream positioned at the start of the TCX document.</param>
    /// <param name="fallbackName">Name to assign when no activity id is found in the document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tracks parsed from the document.</returns>
    /// <exception cref="InvalidDataException">Thrown when the stream does not contain a valid TCX document.</exception>
    Task<IReadOnlyList<Track>> ParseAsync(Stream stream, string fallbackName, CancellationToken cancellationToken = default);
}
