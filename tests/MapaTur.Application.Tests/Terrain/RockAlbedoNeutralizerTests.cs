using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockAlbedoNeutralizerTests
{
    [Fact]
    public void should_turn_the_brown_scan_cast_into_dark_neutral_high_mountain_rock()
    {
        // Arrange
        byte[] brownRgba = [170, 112, 58, 255, 130, 88, 48, 255];

        // Act
        byte[] result = RockAlbedoNeutralizer.Neutralize(brownRgba);

        // Assert
        result
            .Chunk(4)
            .Should()
            .OnlyContain(pixel => pixel.Take(3).Max() - pixel.Take(3).Min() < 20);
    }

    [Fact]
    public void should_reduce_photographic_contrast_so_shape_comes_from_real_relief()
    {
        // Arrange
        byte[] highContrastRgba = [50, 50, 50, 255, 200, 200, 200, 255];

        // Act
        byte[] result = RockAlbedoNeutralizer.Neutralize(highContrastRgba);

        // Assert
        (result[4] - result[0]).Should().BeLessThan(80);
    }
}
