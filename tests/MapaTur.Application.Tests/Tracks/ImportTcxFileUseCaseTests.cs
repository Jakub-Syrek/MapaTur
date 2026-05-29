using FluentAssertions;

using MapaTur.Application.Tracks;
using MapaTur.Domain.Tracks;

using NSubstitute;

namespace MapaTur.Application.Tests.Tracks;

public sealed class ImportTcxFileUseCaseTests
{
    [Fact]
    public void Ctor_NullParser_Throws()
    {
        var act = () => new ImportTcxFileUseCase(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task HandleAsync_BlankPath_Throws(string blank)
    {
        var sut = new ImportTcxFileUseCase(Substitute.For<ITcxParser>());

        await FluentActions.Awaiting(() => sut.HandleAsync(blank)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task HandleAsync_MissingFile_ThrowsFileNotFound()
    {
        var sut = new ImportTcxFileUseCase(Substitute.For<ITcxParser>());
        string missing = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.tcx");

        await FluentActions.Awaiting(() => sut.HandleAsync(missing)).Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task HandleAsync_OpensFile_AndDelegatesToParserWithFilenameFallback()
    {
        var parser = Substitute.For<ITcxParser>();
        parser.ParseAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Track>>(Array.Empty<Track>()));
        var sut = new ImportTcxFileUseCase(parser);

        string path = Path.Combine(Path.GetTempPath(), $"hike-{Guid.NewGuid():N}.tcx");
        await File.WriteAllTextAsync(path, "<TrainingCenterDatabase/>");
        try
        {
            IReadOnlyList<Track> result = await sut.HandleAsync(path);

            result.Should().BeEmpty();
            string expectedFallback = Path.GetFileNameWithoutExtension(path);
            await parser.Received(1).ParseAsync(Arg.Any<Stream>(), expectedFallback, Arg.Any<CancellationToken>());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
