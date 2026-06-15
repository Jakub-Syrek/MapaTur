using MapaTur.Application.Packaging;

namespace MapaTur.Infrastructure.Packaging;

/// <summary>
/// <see cref="IPackageCatalogSource"/> that GETs the manifest JSON from a configured URL and parses it with
/// <see cref="PackageManifestParser"/>. The URL points at the package server (Railway today); the per-package
/// download URLs inside the manifest can point elsewhere (e.g. a CDN) without changing this code.
/// </summary>
public sealed class HttpPackageCatalogSource : IPackageCatalogSource
{
    private readonly HttpClient client;
    private readonly string manifestUrl;

    /// <summary>Initializes the catalogue source.</summary>
    /// <param name="client">HTTP client used to fetch the manifest.</param>
    /// <param name="manifestUrl">Absolute URL of <c>manifest.json</c>.</param>
    public HttpPackageCatalogSource(HttpClient client, string manifestUrl)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestUrl);
        this.client = client;
        this.manifestUrl = manifestUrl;
    }

    /// <inheritdoc />
    public async Task<PackageManifest> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        byte[] bytes = await this.client.GetByteArrayAsync(this.manifestUrl, cancellationToken).ConfigureAwait(false);
        return PackageManifestParser.Parse(bytes);
    }
}