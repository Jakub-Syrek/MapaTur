using System.Net;

using FluentAssertions;

using MapaTur.Infrastructure.Packaging;

namespace MapaTur.Infrastructure.Tests.Packaging;

public sealed class HttpPackageFileFetcherTests
{
    private static byte[] Ramp(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)i;
        }

        return data;
    }

    [Fact]
    public async Task GetContentLengthAsync_ReturnsServerReportedLength()
    {
        using var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            };
            response.Content.Headers.ContentLength = 1000;
            return response;
        });
        using var client = new HttpClient(handler);
        var fetcher = new HttpPackageFileFetcher(client);

        long length = await fetcher.GetContentLengthAsync("https://pkg.example/a.zip");

        length.Should().Be(1000L);
        handler.LastMethod.Should().Be(HttpMethod.Head);
    }

    [Fact]
    public async Task OpenRangeAsync_SendsRangeHeader_AndStreamsRemainder()
    {
        byte[] data = Ramp(1000);
        using var handler = new StubHandler(req =>
        {
            long from = req.Headers.Range!.Ranges.First().From!.Value;
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data[(int)from..]),
            };
        });
        using var client = new HttpClient(handler);
        var fetcher = new HttpPackageFileFetcher(client);

        await using Stream stream = await fetcher.OpenRangeAsync("https://pkg.example/a.zip", 400);
        using var sink = new MemoryStream();
        await stream.CopyToAsync(sink);

        sink.ToArray().Should().Equal(data[400..]);
        handler.LastMethod.Should().Be(HttpMethod.Get);
        handler.LastRangeFrom.Should().Be(400L);
    }

    [Fact]
    public async Task OpenRangeAsync_FromZero_RequestsRangeFromStart()
    {
        byte[] data = Ramp(64);
        using var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(data),
            });
        using var client = new HttpClient(handler);
        var fetcher = new HttpPackageFileFetcher(client);

        await using Stream stream = await fetcher.OpenRangeAsync("https://pkg.example/a.zip", 0);

        handler.LastRangeFrom.Should().Be(0L);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => this.responder = responder;

        public HttpMethod? LastMethod { get; private set; }

        public long? LastRangeFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastRangeFrom = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            return Task.FromResult(responder(request));
        }
    }
}