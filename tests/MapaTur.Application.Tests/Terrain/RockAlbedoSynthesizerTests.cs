using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockAlbedoSynthesizerTests
{
    [Fact]
    public void should_be_deterministic_for_the_same_seed()
    {
        // Arrange
        RockAlbedoTile[] sources = CreateSolidSources();

        // Act
        byte[] first = RockAlbedoSynthesizer.Synthesize(sources, 48, 32, seed: 117);
        byte[] second = RockAlbedoSynthesizer.Synthesize(sources, 48, 32, seed: 117);

        // Assert
        first.Should().Equal(second);
    }

    [Fact]
    public void should_mix_several_scan_patterns_across_one_continuous_texture()
    {
        // Arrange
        RockAlbedoTile[] sources =
        [
            Pattern(8, 8, phaseX: 1, phaseY: 0),
            Pattern(8, 8, phaseX: 0, phaseY: 1),
            Pattern(8, 8, phaseX: 1, phaseY: 1),
        ];

        // Act
        byte[] baseline = RockAlbedoSynthesizer.Synthesize(sources, 96, 64, seed: 814);
        byte[][] withoutEachPattern = Enumerable.Range(0, sources.Length)
            .Select(index =>
            {
                RockAlbedoTile[] changed = sources.ToArray();
                changed[index] = Pattern(8, 8, phaseX: index + 2, phaseY: index + 3);
                return RockAlbedoSynthesizer.Synthesize(changed, 96, 64, seed: 814);
            })
            .ToArray();

        // Assert
        withoutEachPattern.Should().OnlyContain(result => !result.SequenceEqual(baseline));
    }

    [Fact]
    public void should_not_repeat_the_source_at_one_fixed_period()
    {
        // Arrange
        RockAlbedoTile[] sources =
        [
            Checker(8, 8, 25, 210),
            Checker(8, 8, 70, 170),
            Checker(8, 8, 110, 230),
        ];

        // Act
        byte[] result = RockAlbedoSynthesizer.Synthesize(sources, 96, 64, seed: 441);

        // Assert
        CountEqualPixels(result, 96, 64, offsetX: 8)
            .Should()
            .BeLessThan(96 * 64 / 2);
    }

    private static RockAlbedoTile[] CreateSolidSources() =>
    [
        Solid(8, 8, 210, 20, 20),
        Solid(8, 8, 20, 210, 20),
        Solid(8, 8, 20, 20, 210),
    ];

    private static RockAlbedoTile Solid(int width, int height, byte red, byte green, byte blue)
    {
        byte[] rgba = Enumerable.Range(0, width * height)
            .SelectMany(_ => new[] { red, green, blue, byte.MaxValue })
            .ToArray();
        return new RockAlbedoTile(width, height, rgba);
    }

    private static RockAlbedoTile Checker(int width, int height, byte dark, byte light)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = ((x + y) & 1) == 0 ? dark : light;
                int offset = ((y * width) + x) * 4;
                rgba[offset] = value;
                rgba[offset + 1] = value;
                rgba[offset + 2] = value;
                rgba[offset + 3] = byte.MaxValue;
            }
        }

        return new RockAlbedoTile(width, height, rgba);
    }

    private static RockAlbedoTile Pattern(int width, int height, int phaseX, int phaseY)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int pattern = ((x * phaseX) + (y * phaseY)) & 3;
                byte value = pattern switch
                {
                    0 => 35,
                    1 => 95,
                    2 => 155,
                    _ => 215,
                };
                int offset = ((y * width) + x) * 4;
                rgba[offset] = value;
                rgba[offset + 1] = value;
                rgba[offset + 2] = value;
                rgba[offset + 3] = byte.MaxValue;
            }
        }

        return new RockAlbedoTile(width, height, rgba);
    }

    private static int CountEqualPixels(byte[] rgba, int width, int height, int offsetX)
    {
        int equal = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x + offsetX < width; x++)
            {
                int first = ((y * width) + x) * 4;
                int second = ((y * width) + x + offsetX) * 4;
                if (rgba.AsSpan(first, 4).SequenceEqual(rgba.AsSpan(second, 4)))
                {
                    equal++;
                }
            }
        }

        return equal;
    }
}
