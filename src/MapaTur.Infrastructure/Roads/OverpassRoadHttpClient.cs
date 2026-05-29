using MapaTur.Application.Roads;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;
using MapaTur.Infrastructure.Overpass;

namespace MapaTur.Infrastructure.Roads;

/// <summary>
/// HTTP client that asks Overpass endpoints for road ways, sharing the multi-endpoint failover of
/// <see cref="OverpassEndpoints.DefaultFallbackList"/> with the trail/climbing/POI clients.
/// </summary>
public sealed class OverpassRoadHttpClient : IRoadOverpassClient
{
    private readonly HttpClient httpClient;
    private readonly IReadOnlyList<Uri> endpoints;

    public OverpassRoadHttpClient(HttpClient httpClient, IReadOnlyList<Uri>? endpoints = null)
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
    public async Task<IReadOnlyList<Trail>> FetchRoadsAsync(MapBounds bounds, CancellationToken cancellationToken = default)
    {
        string query = OverpassRoadQueryBuilder.BuildRoadsQuery(bounds);
        byte[] payload = await OverpassRequestExecutor.PostWithFailoverAsync(
            httpClient, endpoints, query, cancellationToken).ConfigureAwait(false);
        return OverpassRoadResponseParser.Parse(payload);
    }
}