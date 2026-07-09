using FluentAssertions;

using MapaTur.Application.Tracks;
using MapaTur.Domain.Tracks;

using NSubstitute;

namespace MapaTur.Application.Tests.Tracks;

public sealed class ImportTrackFileUseCaseTests
{
    private static ImportTrackFileUseCase CreateSut(IGpxParser? gpx = null, ITcxParser? tcx = null) =>
        new(gpx ?? Substitute.For<IGpxParser>(), tcx ?? Substitute.For<ITcxParser>());

    [Fact]
    public void Ctor_NullGpxParser_Throws()
    {
        var act = () => new ImportTrackFileUseCase(null!, Substitute.For<ITcxParser>());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullTcxParser_Throws()
    {
        var act = () => new ImportTrackFileUseCase(Substitute.For<IGpxParser>(), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_BlankPath_Throws(string blank)
    {
        var sut = CreateSut();

        await FluentActions.Awaiting(() => sut.HandleAsync(blank)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task HandleAsync_MissingFile_ThrowsFileNotFound()
    {
        var sut = CreateSut();
        string missing = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.gpx");

        await FluentActions.Awaiting(() => sut.HandleAsync(missing)).Should().ThrowAsync<FileNotFoundException>();
    }

    [Theory]
    [InlineData(".gpx")]
    [InlineData(".GPX")]
    public async Task HandleAsync_GpxExtension_DelegatesToGpxParser(string extension)
    {
        var gpx = Substitute.For<IGpxParser>();
        gpx.ParseAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>()));
        var tcx = Substitute.For<ITcxParser>();
        var sut = CreateSut(gpx, tcx);

        string path = Path.Combine(Path.GetTempPath(), $"hike-{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(path, "<gpx/>");
        try
        {
            await sut.HandleAsync(path);

            string expectedFallback = Path.GetFileNameWithoutExtension(path);
            await gpx.Received(1).ParseAsync(Arg.Any<Stream>(), expectedFallback, Arg.Any<CancellationToken>());
            await tcx.DidNotReceive().ParseAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".tcx")]
    [InlineData(".TCX")]
    public async Task HandleAsync_TcxExtension_DelegatesToTcxParser(string extension)
    {
        var gpx = Substitute.For<IGpxParser>();
        var tcx = Substitute.For<ITcxParser>();
        tcx.ParseAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>()));
        var sut = CreateSut(gpx, tcx);

        string path = Path.Combine(Path.GetTempPath(), $"hike-{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(path, "<TrainingCenterDatabase/>");
        try
        {
            await sut.HandleAsync(path);

            string expectedFallback = Path.GetFileNameWithoutExtension(path);
            await tcx.Received(1).ParseAsync(Arg.Any<Stream>(), expectedFallback, Arg.Any<CancellationToken>());
            await gpx.DidNotReceive().ParseAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task HandleAsync_UnsupportedExtension_ThrowsNotSupported()
    {
        var sut = CreateSut();
        string path = Path.Combine(Path.GetTempPath(), $"data-{Guid.NewGuid():N}.kml");
        await File.WriteAllTextAsync(path, "<kml/>");
        try
        {
            await FluentActions.Awaiting(() => sut.HandleAsync(path)).Should().ThrowAsync<NotSupportedException>();
        }
        finally
        {
            File.Delete(path);
        }
    }
}