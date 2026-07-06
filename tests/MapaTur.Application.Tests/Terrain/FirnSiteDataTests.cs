using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Sanity of the curated firn-site gazetteer: every site sits inside the High Tatras core, radii are
/// patch-scale (not valley-scale), names unique — a typo'd coordinate would silently park a "lodowczyk"
/// on a meadow.
/// </summary>
public sealed class FirnSiteDataTests
{
    [Fact]
    public void all_sites_lie_within_the_high_tatras_core()
    {
        FirnSiteData.Sites.Should().NotBeEmpty();
        FirnSiteData.Sites.Should().OnlyContain(s =>
            s.Location.Latitude > 49.15 && s.Location.Latitude < 49.28 &&
            s.Location.Longitude > 19.95 && s.Location.Longitude < 20.15);
    }

    [Fact]
    public void radii_are_patch_scale()
    {
        FirnSiteData.Sites.Should().OnlyContain(s => s.RadiusMeters >= 150 && s.RadiusMeters <= 600);
    }

    [Fact]
    public void names_are_unique()
    {
        FirnSiteData.Sites.Select(s => s.Name).Should().OnlyHaveUniqueItems();
    }
}