using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockAlbedoHarmonizerTests
{
    [Fact]
    public void should_bring_scan_tile_mean_luminance_to_one_shared_level()
    {
        // Arrange
        byte[][] tiles =
        [
            PixelStrip(45, 65, 85),
            PixelStrip(105, 125, 145),
            PixelStrip(165, 185, 205),
        ];

        // Act
        IReadOnlyList<byte[]> result = RockAlbedoHarmonizer.Harmonize(tiles);

        // Assert
        result.Select(MeanLuminance).Max().Should()
            .BeApproximately(result.Select(MeanLuminance).Min(), 1.1);
    }

    [Fact]
    public void should_preserve_local_brightness_order_inside_each_scan()
    {
        // Arrange
        byte[][] tiles =
        [
            PixelStrip(40, 100, 180),
            PixelStrip(70, 110, 150),
            PixelStrip(90, 120, 140),
        ];

        // Act
        IReadOnlyList<byte[]> result = RockAlbedoHarmonizer.Harmonize(tiles);

        // Assert
        result.Should().OnlyContain(tile => tile[0] < tile[4] && tile[4] < tile[8]);
    }

    [Fact]
    public void should_leave_alpha_untouched()
    {
        // Arrange
        byte[][] tiles =
        [
            [40, 50, 60, 10, 80, 90, 100, 140],
            [90, 100, 110, 20, 130, 140, 150, 150],
            [140, 150, 160, 30, 180, 190, 200, 160],
        ];

        // Act
        IReadOnlyList<byte[]> result = RockAlbedoHarmonizer.Harmonize(tiles);

        // Assert
        result.SelectMany(tile => tile.Where((_, index) => index % 4 == 3))
            .Should().Equal(10, 140, 20, 150, 30, 160);
    }

    [Fact]
    public void should_match_scan_contrast_to_the_reference_pattern()
    {
        // Arrange
        byte[][] tiles =
        [
            PixelStrip(55, 90, 125),
            PixelStrip(20, 90, 160),
            PixelStrip(75, 90, 105),
        ];

        // Act
        IReadOnlyList<byte[]> result = RockAlbedoHarmonizer.Harmonize(tiles);

        // Assert
        result
            .Select(tile => tile[8] - tile[0])
            .Should()
            .OnlyContain(range => Math.Abs(range - 70) <= 2);
    }

    [Fact]
    public void should_remove_scan_specific_colour_casts_using_the_reference_rock()
    {
        // Arrange
        byte[][] tiles =
        [
            [70, 74, 78, 255, 100, 104, 108, 255],
            [145, 85, 35, 255, 175, 115, 65, 255],
        ];

        // Act
        IReadOnlyList<byte[]> result = RockAlbedoHarmonizer.Harmonize(tiles);

        // Assert
        ChannelMeans(result[1]).Should().BeEquivalentTo(
            ChannelMeans(result[0]),
            options => options.Using<double>(
                context => context.Subject.Should().BeApproximately(context.Expectation, 1.1))
                .WhenTypeIs<double>());
    }

    private static byte[] PixelStrip(params byte[] luminances) =>
        luminances.SelectMany(value => new[] { value, value, value, byte.MaxValue }).ToArray();

    private static double MeanLuminance(byte[] rgba) =>
        Enumerable.Range(0, rgba.Length / 4)
            .Average(index =>
                (0.2126 * rgba[index * 4])
                + (0.7152 * rgba[(index * 4) + 1])
                + (0.0722 * rgba[(index * 4) + 2]));

    private static double[] ChannelMeans(byte[] rgba) =>
    [
        Enumerable.Range(0, rgba.Length / 4).Average(index => rgba[index * 4]),
        Enumerable.Range(0, rgba.Length / 4).Average(index => rgba[(index * 4) + 1]),
        Enumerable.Range(0, rgba.Length / 4).Average(index => rgba[(index * 4) + 2]),
    ];
}
