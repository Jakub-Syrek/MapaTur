using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockShadowDetailPolicyTests
{
    private const float FieldOfView20Degrees = 0.34906587f;

    [Theory]
    [InlineData(771.06274f)]
    [InlineData(2017.0371f)]
    public void should_keep_relief_shadow_when_it_spans_multiple_shadow_texels(float cascadeFarMeters)
    {
        // Arrange

        // Act
        bool shouldRender = ScannedRockShadowDetailPolicy.ShouldRender(
            maximumReliefMeters: 2.8f,
            cascadeFarMeters,
            FieldOfView20Degrees,
            shadowMapSize: 2048,
            minimumReliefTexels: 1.25f);

        // Assert
        shouldRender.Should().BeTrue();
    }

    [Fact]
    public void should_use_dem_shadow_when_relief_is_subthreshold_in_far_cascade()
    {
        // Arrange

        // Act
        bool shouldRender = ScannedRockShadowDetailPolicy.ShouldRender(
            maximumReliefMeters: 2.8f,
            cascadeFarMeters: 15_000f,
            FieldOfView20Degrees,
            shadowMapSize: 2048,
            minimumReliefTexels: 1.25f);

        // Assert
        shouldRender.Should().BeFalse();
    }
}
