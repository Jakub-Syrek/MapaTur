using System.Net;
using System.Text;

using FluentAssertions;

using MapaTur.Application.Packaging;
using MapaTur.Infrastructure.Packaging;

namespace MapaTur.Infrastructure.Tests.Packaging;

public sealed class HttpPackageCatalogSourceTests
{
    private const string Manifest = """
    { "packages": [ {
        "id": "tatry-dem-1m", "name": "DEM", "layer": "Dem", "format": "ZipTileCache",
        "version": 2, "sizeBytes": 100, "sha256": "aa", "url": "https://pkg/dem.zip" } ] }
    """;

    [Fact]
    public async Task GetManifestAsync_FetchesAndParsesTheManifest()
    {
        using var handler = new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Manifest, Encoding.UTF8, "application/json"),
            });
        using var client = new HttpClient(handler);
        var source = new HttpPackageCatalogSource(client, "https://pkg/manifest.json");

        PackageManifest manifest = await source.GetManifestAsync();

        manifest.Packages.Should().ContainSingle().Which.Id.Should().Be("tatry-dem-1m");
        handler.LastUri.Should().Be("https://pkg/manifest.json");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => this.responder = responder;

        public string? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri?.ToString();
            return Task.FromResult(responder(request));
        }
    }
}