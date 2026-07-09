using MapaTur.Domain.Tracks;

namespace MapaTur.Application.Tracks;

/// <summary>
/// Imports tracks from a GPX or TCX file on disk, dispatching to the right parser by file extension.
/// This is the unified entry point behind the "pozaszlaki" (off-trail) import panel.
/// </summary>
public sealed class ImportTrackFileUseCase
{
    private readonly IGpxParser gpxParser;
    private readonly ITcxParser tcxParser;

    /// <summary>
    /// Initializes a new instance of the use case.
    /// </summary>
    /// <param name="gpxParser">GPX parser implementation.</param>
    /// <param name="tcxParser">TCX parser implementation.</param>
    public ImportTrackFileUseCase(IGpxParser gpxParser, ITcxParser tcxParser)
    {
        ArgumentNullException.ThrowIfNull(gpxParser);
        ArgumentNullException.ThrowIfNull(tcxParser);
        this.gpxParser = gpxParser;
        this.tcxParser = tcxParser;
    }

    /// <summary>
    /// Opens the file at <paramref name="filePath"/> and parses its tracks. The parser is chosen by the
    /// file extension: <c>.gpx</c> or <c>.tcx</c> (case-insensitive).
    /// </summary>
    /// <param name="filePath">Absolute path to the GPX or TCX file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tracks contained in the file.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="NotSupportedException">Thrown when the extension is neither .gpx nor .tcx.</exception>
    /// <exception cref="InvalidDataException">Thrown when the file is not a valid document of its format.</exception>
    public async Task<IReadOnlyList<Track>> HandleAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Track file not found.", filePath);
        }

        string extension = Path.GetExtension(filePath).ToLowerInvariant();
        string fallbackName = Path.GetFileNameWithoutExtension(filePath);

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return extension switch
        {
            ".gpx" => await gpxParser.ParseAsync(stream, fallbackName, cancellationToken).ConfigureAwait(false),
            ".tcx" => await tcxParser.ParseAsync(stream, fallbackName, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unsupported track file extension '{extension}'. Supported: .gpx, .tcx."),
        };
    }
}