using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Sanity pinning for the baked bright-star catalog (<see cref="StarCatalogData"/>): it carries the named
/// landmark stars the sky labels and every entry has physically sane coordinates/magnitude — catches a typo
/// in the hand-baked array.
/// </summary>
public sealed class StarCatalogDataTests
{
    [Fact]
    public void Bundled_ContainsLandmarkNamedStars()
    {
        StarCatalogData.Bundled.Should().NotBeEmpty();
        StarCatalogData.Bundled.Should().Contain(s => s.Name == "Polaris");
        StarCatalogData.Bundled.Should().Contain(s => s.Name == "Sirius");
        StarCatalogData.Bundled.Should().Contain(s => s.Name == "Vega");
        StarCatalogData.Bundled.Count(s => s.Constellation == "Ursa Major").Should().BeGreaterThanOrEqualTo(7); // the Big Dipper
    }

    [Fact]
    public void Bundled_AllEntriesHaveSaneCoordinatesAndMagnitude()
    {
        foreach (Star s in StarCatalogData.Bundled)
        {
            s.RaHours.Should().BeInRange(0.0, 24.0);
            s.DecDegrees.Should().BeInRange(-90.0, 90.0);
            s.Magnitude.Should().BeInRange(-2.0, 7.0);
            s.HasName.Should().BeTrue();
        }
    }
}
