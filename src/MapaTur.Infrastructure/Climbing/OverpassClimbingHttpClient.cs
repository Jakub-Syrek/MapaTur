using MapaTur.Application.Climbing;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;
using MapaTur.Infrastructure.Overpass;

namespace MapaTur.Infrastructure.Climbing;

/// <summary>
/// HTTP client that asks Overpass endpoints for climbing-tagged features. Shares the
/// multi-endpoint failover posture of <see cref="OverpassEndpoints.DefaultFallbackList"/>
/// with the trail client.
/// </summary>
public sealed class OverpassClimbingHttpClient : IClimbingOverpassClient
{
    private readonly HttpClient httpClient;
    private readonly IReadOnlyList<Uri> endpoints;

    /// <summary>
    /// Initializes the client.
    /// </summary>
    /// <param name="httpClient">HTTP client.</param>
    /// <param name="endpoints">Ordered list of Overpass endpoints. Defaults to <see cref="OverpassEndpoints.DefaultFallbackList"/>.</param>
    public OverpassClimbingHttpClient(HttpClient httpClient, IReadOnlyList<Uri>? endpoints = null)
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
    public async Task<IReadOnlyList<ClimbingArea>> FetchClimbingAreasAsync(MapBounds bounds, CancellationToken cancellationToken = default)
    {
        string query = OverpassClimbingQueryBuilder.BuildClimbingQuery(bounds);
        byte[] payload = await OverpassRequestExecutor.PostWithFailoverAsync(
            httpClient, endpoints, query, cancellationToken).ConfigureAwait(false);
        return OverpassClimbingResponseParser.Parse(payload);
    }
}