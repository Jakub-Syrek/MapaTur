using MapaTur.Domain.Tracks;

namespace MapaTur.Application.Tracks;

/// <summary>
/// Imports tracks from a TCX file on disk.
/// </summary>
public sealed class ImportTcxFileUseCase
{
    private readonly ITcxParser parser;

    /// <summary>
    /// Initializes a new instance of the use case.
    /// </summary>
    /// <param name="parser">TCX parser implementation.</param>
    public ImportTcxFileUseCase(ITcxParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        this.parser = parser;
    }

    /// <summary>
    /// Opens the file at <paramref name="filePath"/> and parses its tracks.
    /// </summary>
    /// <param name="filePath">Absolute path to the TCX file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tracks contained in the file.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the file does not exist.</exception>
    /// <exception cref="InvalidDataException">Thrown when the file is not a valid TCX document.</exception>
    public async Task<IReadOnlyList<Track>> HandleAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("TCX file not found.", filePath);
        }

        string fallbackName = Path.GetFileNameWithoutExtension(filePath);
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await parser.ParseAsync(stream, fallbackName, cancellationToken).ConfigureAwait(false);
    }
}
