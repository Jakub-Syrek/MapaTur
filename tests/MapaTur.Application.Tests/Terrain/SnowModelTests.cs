using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of the snow-cover model: the elevation snowline + band drive WHERE snow sits vertically,
/// and the slope-angle cosines drive snow holding on gentle slopes vs baring rock on steep faces. These
/// parameters are the single source of truth the terrain shader mirrors via uniforms.
/// </summary>
public sealed class SnowModelTests
{
    private const float MinZ = 600f;
    private const float MaxZ = 2600f;   // relief 2000 m

    [Fact]
    public void Compute_AtZeroAmount_SnowlineSitsAtHighestPeak()
    {
        SnowShadingParameters p = SnowModel.Compute(snowAmount: 0f, terrainMinZ: MinZ, terrainMaxZ: MaxZ);

        // Just-on: snow only at the very top, so the line is at the highest peak.
        p.LineZ.Should().BeApproximately(MaxZ, 0.01f);
    }

    [Fact]
    public void Compute_AtFullAmount_SnowlineDropsBelowValleyFloor()
    {
        SnowShadingParameters p = SnowModel.Compute(snowAmount: 1f, terrainMinZ: MinZ, terrainMaxZ: MaxZ);

        // Full cover: the line sits one band below the valley floor, so everything (floor included) whitens.
        p.LineZ.Should().BeApproximately(MinZ - p.BandZ, 0.01f);
    }

    [Fact]
    public void Compute_SnowlineDescends_AsAmountRises()
    {
        float low = SnowModel.Compute(0.25f, MinZ, MaxZ).LineZ;
        float high = SnowModel.Compute(0.75f, MinZ, MaxZ).LineZ;

        // More snow → lower snowline (monotonic descent from the peaks toward the floor).
        high.Should().BeLessThan(low);
    }

    [Fact]
    public void Compute_Band_IsFifteenPercentOfRelief()
    {
        SnowShadingParameters p = SnowModel.Compute(0.5f, MinZ, MaxZ);

        p.BandZ.Should().BeApproximately((MaxZ - MinZ) * 0.15f, 0.01f);
    }

    [Fact]
    public void Compute_Band_HasPositiveFloor_WhenReliefIsDegenerate()
    {
        // Flat terrain (max == min) must not collapse the band to zero (division/smoothstep safety).
        SnowShadingParameters p = SnowModel.Compute(0.5f, terrainMinZ: 1000f, terrainMaxZ: 1000f);

        p.BandZ.Should().BeGreaterThan(0f);
    }

    [Fact]
    public void Compute_SlopeBareCosine_IsLessThanFullCosine()
    {
        // cos is decreasing in angle: the "bare rock" threshold is a STEEPER angle, hence a SMALLER cosine,
        // than the "snow fully holds" threshold — so smoothstep(bare, full, n.z) has edge0 < edge1.
        SnowShadingParameters p = SnowModel.Compute(0.5f, MinZ, MaxZ);

        p.SlopeCosBare.Should().BeLessThan(p.SlopeCosFull);
    }

    [Fact]
    public void Compute_SlopeThresholds_MatchHoldAndBareAngles()
    {
        SnowShadingParameters p = SnowModel.Compute(0.5f, MinZ, MaxZ);

        p.SlopeCosFull.Should().BeApproximately(MathF.Cos(SnowModel.SnowHoldsBelowDegrees * MathF.PI / 180f), 1e-4f);
        p.SlopeCosBare.Should().BeApproximately(MathF.Cos(SnowModel.SnowBaresAboveDegrees * MathF.PI / 180f), 1e-4f);
    }

    [Theory]
    [InlineData(2f, 1f)]    // above the range clamps to full
    [InlineData(-1f, 0f)]   // below the range clamps to none
    public void Compute_ClampsSnowAmount_IntoUnitRange(float amount, float equivalent)
    {
        float clamped = SnowModel.Compute(amount, MinZ, MaxZ).LineZ;
        float expected = SnowModel.Compute(equivalent, MinZ, MaxZ).LineZ;

        clamped.Should().BeApproximately(expected, 0.01f);
    }
}
