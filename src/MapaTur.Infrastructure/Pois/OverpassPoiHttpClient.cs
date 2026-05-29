using MapaTur.Application.Pois;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;
using MapaTur.Infrastructure.Overpass;

namespace MapaTur.Infrastructure.Pois;

/// <summary>
/// HTTP client that asks Overpass endpoints for mountain POIs, sharing the multi-endpoint failover of
/// <see cref="OverpassEndpoints.DefaultFallbackList"/> with the trail and climbing clients.
/// </summary>
public sealed class OverpassPoiHttpClient : IPoiOverpassClient
{
    private readonly HttpClient httpClient;
    private readonly IReadOnlyList<Uri> endpoints;

    public OverpassPoiHttpClient(HttpClient httpClient, IReadOnlyList<Uri>? endpoints = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.endpoints = endpoints ?? OverpassEndpoints.DefaultFallbackList;

        if (!this.httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            this.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "MapaTur/0.1 (+https://github.com/Jakub-Syrek/MapaTur)");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MountainPoi>> FetchPoisAsync(MapBounds bounds, CancellationToken cancellationToken = default)
    {
        string query = OverpassPoiQueryBuilder.BuildPoiQuery(bounds);
        byte[] payload = await OverpassRequestExecutor.PostWithFailoverAsync(
            httpClient, endpoints, query, cancellationToken).ConfigureAwait(false);
        return OverpassPoiResponseParser.Parse(payload);
    }
}