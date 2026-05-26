using MapaTur.Application.Climbing;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;

namespace MapaTur.Infrastructure.Climbing;

/// <summary>
/// HTTP client that asks an Overpass endpoint for climbing-tagged features.
/// Shares the same endpoint and User-Agent posture as the trail client.
/// </summary>
public sealed class OverpassClimbingHttpClient : IClimbingOverpassClient
{
    /// <summary>Default endpoint used when none is provided.</summary>
    public const string DefaultEndpoint = "https://overpass-api.de/api/interpreter";

    private readonly HttpClient httpClient;
    private readonly Uri endpoint;

    /// <summary>
    /// Initializes the client.
    /// </summary>
    /// <param name="httpClient">HTTP client.</param>
    /// <param name="endpoint">Overpass endpoint URL. Defaults to the main public endpoint.</param>
    public OverpassClimbingHttpClient(HttpClient httpClient, Uri? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.endpoint = endpoint ?? new Uri(DefaultEndpoint);

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
        return OverpassClimbingResponseParser.Parse(payload);
    }
}
