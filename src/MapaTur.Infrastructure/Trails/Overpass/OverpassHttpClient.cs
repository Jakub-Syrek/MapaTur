using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Infrastructure.Trails.Overpass;

/// <summary>
/// HTTP client that talks to a public Overpass API endpoint. The endpoint URL is
/// configurable so the same client can target main, mirror, or a private instance.
/// </summary>
public sealed class OverpassHttpClient : IOverpassClient
{
    /// <summary>Default endpoint used when none is provided.</summary>
    public const string DefaultEndpoint = "https://overpass-api.de/api/interpreter";

    private readonly HttpClient httpClient;
    private readonly Uri endpoint;

    /// <summary>
    /// Initializes the client.
    /// </summary>
    /// <param name="httpClient">HTTP client (typically created via IHttpClientFactory).</param>
    /// <param name="endpoint">Overpass endpoint URL. Defaults to the main public endpoint.</param>
    public OverpassHttpClient(HttpClient httpClient, Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.endpoint = endpoint ?? new Uri(DefaultEndpoint);

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

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new KeyValuePair<string, string>[]
            {
                new("data", query),
            }),
        };
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return OverpassResponseParser.Parse(payload);
    }
}
