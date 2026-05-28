using System.Net;

namespace MapaTur.Infrastructure.Overpass;

/// <summary>
/// POSTs an Overpass QL query to each supplied endpoint in order, returning the body
/// of the first successful response. Falls through on transient errors (5xx status,
/// network exceptions, timeouts) and surfaces the last failure when all endpoints
/// are exhausted. Client (4xx) errors short-circuit on the first endpoint because
/// retrying a malformed query won't help.
/// </summary>
public static class OverpassRequestExecutor
{
    /// <summary>
    /// Sends <paramref name="query"/> as form-encoded <c>data=</c> POST body to each
    /// endpoint until one returns a 2xx response, then returns that response body.
    /// </summary>
    /// <param name="httpClient">Shared HttpClient (typically from IHttpClientFactory).</param>
    /// <param name="endpoints">Endpoints to attempt, in priority order. Must be non-empty.</param>
    /// <param name="query">Raw Overpass QL query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">No endpoints supplied.</exception>
    /// <exception cref="HttpRequestException">All endpoints failed, or a client (4xx) error fired.</exception>
    public static async Task<byte[]> PostWithFailoverAsync(
        HttpClient httpClient,
        IReadOnlyList<Uri> endpoints,
        string query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(query);

        if (endpoints.Count == 0)
        {
            throw new InvalidOperationException("At least one Overpass endpoint is required.");
        }

        HttpRequestException? lastTransient = null;
        for (int i = 0; i < endpoints.Count; i++)
        {
            var endpoint = endpoints[i];

            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new FormUrlEncodedContent(new KeyValuePair<string, string>[]
                    {
                        new("data", query),
                    }),
                };
                request.Headers.Accept.ParseAdd("application/json");

                response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                lastTransient = ex;
                continue;
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException ex)
            {
                // HttpClient.Timeout fires as TaskCanceledException; treat as transient.
                lastTransient = new HttpRequestException(
                    $"Overpass endpoint {endpoint.Host} timed out.", ex);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                }

                // 4xx — the query itself is wrong (syntax, rate-limit policy, etc.).
                // Retrying against another endpoint won't help; surface immediately
                // outside the transient-catch block above so we don't loop.
                if ((int)response.StatusCode < 500)
                {
                    throw new HttpRequestException(
                        $"Overpass endpoint {endpoint.Host} returned {(int)response.StatusCode} ({response.StatusCode}).");
                }

                lastTransient = new HttpRequestException(
                    $"Overpass endpoint {endpoint.Host} returned {(int)response.StatusCode} ({response.StatusCode}).");
            }
        }

        throw lastTransient ?? new HttpRequestException("All Overpass endpoints failed.");
    }
}
