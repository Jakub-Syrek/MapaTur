using FluentAssertions;

using MapaTur.Application.Routing;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;

using NSubstitute;

namespace MapaTur.Application.Tests.Routing;

public sealed class ExportRouteToGpxUseCaseTests
{
    private static Route SampleRoute() => new(new[]
    {
        new RouteSegment(new GeoPoint(49.0, 19.0), new GeoPoint(49.1, 19.1), 100, 0, 0, 60),
    });

    [Fact]
    public void Ctor_NullWriter_Throws()
    {
        var act = () => new ExportRouteToGpxUseCase(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task HandleAsync_NullRoute_Throws()
    {
        var sut = new ExportRouteToGpxUseCase(Substitute.For<IGpxWriter>());

        await FluentActions.Awaiting(() => sut.HandleAsync(null!, "out.gpx", "trip"))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_BlankPathOrName_Throws(string blank)
    {
        var sut = new ExportRouteToGpxUseCase(Substitute.For<IGpxWriter>());

        await FluentActions.Awaiting(() => sut.HandleAsync(SampleRoute(), blank, "trip"))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => sut.HandleAsync(SampleRoute(), "out.gpx", blank))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task HandleAsync_WritesRouteToFileViaWriter()
    {
        var writer = Substitute.For<IGpxWriter>();
        var sut = new ExportRouteToGpxUseCase(writer);
        var route = SampleRoute();
        string path = Path.GetTempFileName();
        try
        {
            await sut.HandleAsync(route, path, "trip");

            await writer.Received(1).WriteAsync(route, Arg.Any<Stream>(), "trip", Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
