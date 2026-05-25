using MapaTur.Domain.Routing;

namespace MapaTur.Application.Routing;

/// <summary>
/// Use case: writes a planned route to a GPX file on disk.
/// </summary>
public sealed class ExportRouteToGpxUseCase
{
    private readonly IGpxWriter writer;

    /// <summary>
    /// Initializes a new instance of the use case.
    /// </summary>
    /// <param name="writer">GPX writer implementation.</param>
    public ExportRouteToGpxUseCase(IGpxWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        this.writer = writer;
    }

    /// <summary>
    /// Writes <paramref name="route"/> to <paramref name="destinationPath"/> as GPX.
    /// </summary>
    /// <param name="route">Route to export.</param>
    /// <param name="destinationPath">Absolute path of the output .gpx file.</param>
    /// <param name="trackName">Track name embedded in the GPX document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task HandleAsync(Route route, string destinationPath, string trackName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackName);

        await using var stream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await writer.WriteAsync(route, stream, trackName, cancellationToken).ConfigureAwait(false);
    }
}
