using System.Net;
using FluentAssertions;
using MapaTur.Infrastructure.Overpass;

namespace MapaTur.Infrastructure.Tests.Overpass;

public sealed class OverpassRequestExecutorTests
{
    private static readonly Uri EndpointA = new("https://primary.example/api/interpreter");
    private static readonly Uri EndpointB = new("https://mirror1.example/api/interpreter");
    private static readonly Uri EndpointC = new("https://mirror2.example/api/interpreter");

    [Fact]
    public async Task SendAsync_FirstEndpointReturns200_ReturnsItsBodyWithoutTryingOthers()
    {
        var handler = new RecordingHandler();
        handler.Map(EndpointA, _ => Respond(HttpStatusCode.OK, "primary-ok"));
        handler.Map(EndpointB, _ => Respond(HttpStatusCode.OK, "mirror-ok"));

        using var client = new HttpClient(handler);
        byte[] body = await OverpassRequestExecutor.PostWithFailoverAsync(
            client, new[] { EndpointA, EndpointB }, "out:json;", CancellationToken.None);

        System.Text.Encoding.UTF8.GetString(body).Should().Be("primary-ok");
        handler.Calls.Should().HaveCount(1);
        handler.Calls[0].Should().Be(EndpointA);
    }

    [Fact]
    public async Task SendAsync_FirstReturns504_FallsThroughToSecond()
    {
        var handler = new RecordingHandler();
        handler.Map(EndpointA, _ => Respond(HttpStatusCode.GatewayTimeout, ""));
        handler.Map(EndpointB, _ => Respond(HttpStatusCode.OK, "mirror-ok"));

        using var client = new HttpClient(handler);
        byte[] body = await OverpassRequestExecutor.PostWithFailoverAsync(
            client, new[] { EndpointA, EndpointB }, "out:json;", CancellationToken.None);

        System.Text.Encoding.UTF8.GetString(body).Should().Be("mirror-ok");
        handler.Calls.Should().Equal(EndpointA, EndpointB);
    }

    [Fact]
    public async Task SendAsync_AllEndpointsReturn5xx_ThrowsHttpRequestException()
    {
        var handler = new RecordingHandler();
        handler.Map(EndpointA, _ => Respond(HttpStatusCode.GatewayTimeout, ""));
        handler.Map(EndpointB, _ => Respond(HttpStatusCode.BadGateway, ""));
        handler.Map(EndpointC, _ => Respond(HttpStatusCode.ServiceUnavailable, ""));

        using var client = new HttpClient(handler);
        Func<Task> act = () => OverpassRequestExecutor.PostWithFailoverAsync(
            client, new[] { EndpointA, EndpointB, EndpointC }, "out:json;", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Calls.Should().Equal(EndpointA, EndpointB, EndpointC);
    }

    [Fact]
    public async Task SendAsync_FirstReturns400_FailsImmediatelyWithoutRetrying()
    {
        var handler = new RecordingHandler();
        handler.Map(EndpointA, _ => Respond(HttpStatusCode.BadRequest, "syntax error"));
        handler.Map(EndpointB, _ => Respond(HttpStatusCode.OK, "mirror-ok"));

        using var client = new HttpClient(handler);
        Func<Task> act = () => OverpassRequestExecutor.PostWithFailoverAsync(
            client, new[] { EndpointA, EndpointB }, "bogus;", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Calls.Should().Equal(EndpointA);
    }

    [Fact]
    public async Task SendAsync_FirstThrowsTransient_FallsThroughToSecond()
    {
        var handler = new RecordingHandler();
        handler.Map(EndpointA, _ => throw new HttpRequestException("dns failure"));
        handler.Map(EndpointB, _ => Respond(HttpStatusCode.OK, "mirror-ok"));

        using var client = new HttpClient(handler);
        byte[] body = await OverpassRequestExecutor.PostWithFailoverAsync(
            client, new[] { EndpointA, EndpointB }, "out:json;", CancellationToken.None);

        System.Text.Encoding.UTF8.GetString(body).Should().Be("mirror-ok");
        handler.Calls.Should().Equal(EndpointA, EndpointB);
    }

    [Fact]
    public async Task SendAsync_NoEndpoints_Throws()
    {
        using var client = new HttpClient();
        Func<Task> act = () => OverpassRequestExecutor.PostWithFailoverAsync(
            client, Array.Empty<Uri>(), "out:json;", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static HttpResponseMessage Respond(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body) };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<Uri, Func<HttpRequestMessage, HttpResponseMessage>> map = new();
        public List<Uri> Calls { get; } = new();

        public void Map(Uri endpoint, Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            map[endpoint] = respond;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add(request.RequestUri!);
            if (!map.TryGetValue(request.RequestUri!, out var responder))
            {
                throw new InvalidOperationException($"No mapping for {request.RequestUri}");
            }
            return Task.FromResult(responder(request));
        }
    }
}