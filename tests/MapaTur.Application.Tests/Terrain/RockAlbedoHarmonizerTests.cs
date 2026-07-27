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

    private static byte[] PixelStrip(params byte[] luminances) =>
        luminances.SelectMany(value => new[] { value, value, value, byte.MaxValue }).ToArray();

    private static double MeanLuminance(byte[] rgba) =>
        Enumerable.Range(0, rgba.Length / 4)
            .Average(index =>
                (0.2126 * rgba[index * 4])
                + (0.7152 * rgba[(index * 4) + 1])
                + (0.0722 * rgba[(index * 4) + 2]));
}
