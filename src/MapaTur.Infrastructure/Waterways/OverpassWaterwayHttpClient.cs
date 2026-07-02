using MapaTur.Application.Waterways;
using MapaTur.Domain.Geography;
using MapaTur.Infrastructure.Overpass;

namespace MapaTur.Infrastructure.Waterways;

/// <summary>
/// HTTP client that asks Overpass endpoints for watercourses (stream/river ways + waterfall nodes), sharing
/// the multi-endpoint failover of <see cref="OverpassEndpoints.DefaultFallbackList"/> with the other clients.
/// </summary>
public sealed class OverpassWaterwayHttpClient : IWaterwayOverpassClient
{
    private readonly HttpClient httpClient;
    private readonly IReadOnlyList<Uri> endpoints;

    public OverpassWaterwayHttpClient(HttpClient httpClient, IReadOnlyList<Uri>? endpoints = null)
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
    public async Task<WaterwayFetchResult> FetchWaterwaysAsync(MapBounds bounds, CancellationToken cancellationToken = default)
    {
        string query = OverpassWaterwayQueryBuilder.BuildWaterwaysQuery(bounds);
        byte[] payload = await OverpassRequestExecutor.PostWithFailoverAsync(
            httpClient, endpoints, query, cancellationToken).ConfigureAwait(false);
        return OverpassWaterwayResponseParser.Parse(payload);
    }
}