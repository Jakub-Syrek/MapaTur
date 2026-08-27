using FluentAssertions;

using MapaTur.Application.Maps;

namespace MapaTur.Application.Tests.Maps;

/// <summary>
/// Behaviour of the per-region file picker (P-A2): with two regions' data side by side on disk the
/// auto-loader must stop taking "the first *.dem found" and prefer the file named after the active
/// region ({regionId}.dem), falling back to the old first-found behaviour when no name matches —
/// which is also the zero-regression guarantee for today's single-region installs (tatry.dem).
/// </summary>
public sealed class RegionFileSelectionTests
{
    [Fact]
    public void PickDem_PrefersRegionNamedFile()
    {
        string[] files = [@"C:\d\zermatt.dem", @"C:\d\tatry.dem"];

        RegionFileSelection.PickDem(files, "tatry").Should().Be(@"C:\d\tatry.dem");
    }

    [Fact]
    public void PickDem_IsCaseInsensitive()
    {
        string[] files = [@"C:\d\Tatry.DEM"];

        RegionFileSelection.PickDem(files, "tatry").Should().Be(@"C:\d\Tatry.DEM");
    }

    [Fact]
    public void PickDem_FallsBackToFirstWhenNoNameMatches()
    {
        // Dzisiejsze instalacje: jedyny plik moze nazywac sie dowolnie — zachowanie "pierwszy z brzegu"
        // musi zostac (zero regresji).
        string[] files = [@"C:\d\stary-eksport.dem", @"C:\d\inny.dem"];

        RegionFileSelection.PickDem(files, "tatry").Should().Be(@"C:\d\stary-eksport.dem");
    }

    [Fact]
    public void PickDem_EmptyYieldsNull()
    {
        RegionFileSelection.PickDem([], "tatry").Should().BeNull();
    }
}