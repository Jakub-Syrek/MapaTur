using System.Globalization;

using MapaTur.Application.Maps;
using MapaTur.Domain.Maps;

namespace MapaTur.Infrastructure.Maps;

/// <summary>
/// Esri World Imagery orthophoto as an <see cref="ITileSource"/> for the 3D near-field ortho streaming (high
/// zoom, z17-18). Fetches tiles over HTTP and persists them to the SAME on-disk store as the 2D base layer —
/// <c>{cacheDirectory}/ortho-esri/{z}/{x}/{y}.jpg</c> — so 2D and 3D share cached tiles. Returns null on a
/// missing/failed tile so the compositor can skip it; honours cancellation.
/// </summary>
public sealed class EsriOrthoPortTileSource : ITileSource
{
    // Esri World Imagery XYZ endpoint in z/y/x order (the same source the 2D base layer uses).
    private const string TileUrlFormat =
        "https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{0}/{1}/{2}";

    private static readonly TileSourceMetadata Metadata = new(
        "Esri World Imagery",
        TileFormat.Jpeg,
        MinZoomLevel: 0,
        MaxZoomLevel: 19,
        Bounds: null,
        Attribution: "Esri, Maxar, Earthstar Geographics");

    private readonly HttpClient httpClient;
    private readonly string orthoCacheDirectory;

    /// <summary>Creates the Esri ortho tile source over a shared HTTP client and on-disk cache root.</summary>
    /// <param name="httpClient">Shared HTTP client (the caller owns its lifetime).</param>
    /// <param name="cacheDirectory">App cache root; tiles persist under its <c>ortho-esri</c> subfolder.</param>
    public EsriOrthoPortTileSource(HttpClient httpClient, string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        this.httpClient = httpClient;
        orthoCacheDirectory = Path.Combine(cacheDirectory, "ortho-esri");
    }

    public TileSourceMetadata GetMetadata() => Metadata;

    public async Task<byte[]?> GetTileAsync(TileCoordinate coordinate, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string cachePath = CachePath(coordinate);
        if (File.Exists(cachePath))
        {
            return await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }

        var invariant = CultureInfo.InvariantCulture;
        var uri = new Uri(string.Format(invariant, TileUrlFormat, coordinate.ZoomLevel, coordinate.Row, coordinate.Column));

        byte[] payload;
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null; // transient network failure — let the compositor skip this tile
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllBytesAsync(cachePath, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Best-effort cache write; still return the freshly fetched tile.
        }

        return payload;
    }

    private string CachePath(TileCoordinate coordinate)
    {
        var invariant = CultureInfo.InvariantCulture;
        return Path.Combine(
            orthoCacheDirectory,
            coordinate.ZoomLevel.ToString(invariant),
            coordinate.Column.ToString(invariant),
            $"{coordinate.Row.ToString(invariant)}.jpg");
    }

    public void Dispose()
    {
        // The HttpClient is owned by the caller; nothing to release here.
    }
}