using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

using Xunit;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// The WATER channel of the trail mask: watercourse polylines are painted (a) into the RGBA field like any
/// other line (colour + casing come free from the existing decal path) AND (b) into a parallel single-channel
/// distance field that drives the shader's wet tint + specular glint — a trail crossing a stream must not
/// glint, so water needs its own field, not a colour heuristic.
/// </summary>
public sealed class TrailMaskWaterFieldTests
{
    private static TrailMaskRequest Request(IReadOnlyList<MaskPolyline> lines, IReadOnlyList<MaskPolyline> water) => new()
    {
        WorldMinX = 0f,
        WorldMinY = 0f,
        WorldSizeX = 64f,
        WorldSizeY = 64f,
        Width = 64,
        Height = 64,
        MaxDistanceMeters = 4f,
        Lines = lines,
        WaterLines = water,
    };

    private static MaskPolyline Horizontal(float y, byte r, byte g, byte b, int priority) => new(
        new[] { new Vector3(0f, y, 0f), new Vector3(64f, y, 0f) }, r, g, b, priority);

    [Fact]
    public void Build_NoWaterLines_WaterFieldIsNull()
    {
        var mask = TrailMaskBuilder.Build(Request(
            new[] { Horizontal(32f, 200, 0, 0, TrailMaskInput.TrailPriority) },
            Array.Empty<MaskPolyline>()));

        mask.Water.Should().BeNull();
    }

    [Fact]
    public void Build_WaterLine_WritesWaterFieldOnTheLine_AndZeroFarAway()
    {
        var water = Horizontal(32f, TrailMaskInput.WaterColor.R, TrailMaskInput.WaterColor.G, TrailMaskInput.WaterColor.B, TrailMaskInput.WaterPriority);
        var mask = TrailMaskBuilder.Build(Request(new[] { water }, new[] { water }));

        mask.Water.Should().NotBeNull();
        // Texel row at y=32 (row index 32) directly on the line → near-max water alpha.
        mask.Water![(32 * 64) + 32].Should().BeGreaterThan(200);
        // Far row (y=8, 24 m away, beyond the 4 m reach) → zero.
        mask.Water[(8 * 64) + 32].Should().Be(0);
    }

    [Fact]
    public void Build_TrailCrossingWater_DoesNotErasetheWaterField()
    {
        var water = Horizontal(32f, TrailMaskInput.WaterColor.R, TrailMaskInput.WaterColor.G, TrailMaskInput.WaterColor.B, TrailMaskInput.WaterPriority);
        var trail = Horizontal(32f, 200, 0, 0, TrailMaskInput.TrailPriority); // same course — trail wins the RGBA colour
        var mask = TrailMaskBuilder.Build(Request(new[] { water, trail }, new[] { water }));

        // RGBA colour on the shared texel is the trail's (higher priority)...
        var (r, _, _, _) = mask.PixelAt(32, 32);
        r.Should().Be(200);
        // ...but the WATER field still marks the texel as water (the glint keys off this, not the colour).
        mask.Water![(32 * 64) + 32].Should().BeGreaterThan(200);
    }
}