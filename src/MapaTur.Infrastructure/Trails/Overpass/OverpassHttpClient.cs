using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;
using MapaTur.Infrastructure.Overpass;

namespace MapaTur.Infrastructure.Trails.Overpass;

/// <summary>
/// HTTP client that talks to public Overpass API endpoints. Tries multiple endpoints
/// in order so the main endpoint's frequent 504 Gateway Timeouts under load do not
/// block the user — the next mirror in <see cref="OverpassEndpoints.DefaultFallbackList"/>
/// is tried instead.
/// </summary>
public sealed class OverpassHttpClient : IOverpassClient
{
    private readonly HttpClient httpClient;
    private readonly IReadOnlyList<Uri> endpoints;

    /// <summary>
    /// Initializes the client.
    /// </summary>
    /// <param name="httpClient">HTTP client (typically created via IHttpClientFactory).</param>
    /// <param name="endpoints">Ordered list of Overpass endpoints. Defaults to <see cref="OverpassEndpoints.DefaultFallbackList"/>.</param>
    public OverpassHttpClient(HttpClient httpClient, IReadOnlyList<Uri>? endpoints = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.endpoints = endpoints ?? OverpassEndpoints.DefaultFallbackList;

        // The public Overpass mirrors reject requests without a User-Agent to deter
        // anonymous scraping bots. Set one once on the shared client.
        if (!this.httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Trail>> FetchHikingTrailsAsync(MapBounds bounds, CancellationToken cancellationToken = default)
    {
        string query = OverpassQueryBuilder.BuildHikingTrailsQuery(bounds);
        byte[] payload = await OverpassRequestExecutor.PostWithFailoverAsync(
            httpClient, endpoints, query, cancellationToken).ConfigureAwait(false);
        return OverpassResponseParser.Parse(payload);
    }
}
