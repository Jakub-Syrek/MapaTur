using System.Linq;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of the bundled Tatra lake table: it carries named OSM <c>natural=water</c> outlines with a
/// recorded elevation, and selects only the lakes whose centroid sits inside the currently loaded terrain
/// (so lake water never floats over un-loaded ground — empty in the Beskidy view, present in the Tatra LOD demo).
/// </summary>
public sealed class MountainLakeDataTests
{
    private static MapBounds Bounds(double south, double west, double north, double east)
        => new(new GeoPoint(south, west), new GeoPoint(north, east));

    [Fact]
    public void All_ContainsMorskieOko_WithItsRecordedElevation()
    {
        MountainLake morskieOko = MountainLakeData.All.Single(l => l.Name == "Morskie Oko");

        morskieOko.ElevationMeters.Should().Be(1395.0);
        morskieOko.Outline.Count.Should().BeGreaterThan(100);
    }

    [Fact]
    public void Outline_HasNoClosingDuplicateVertex()
    {
        MountainLake morskieOko = MountainLakeData.All.Single(l => l.Name == "Morskie Oko");

        morskieOko.Outline[0].Should().NotBe(morskieOko.Outline[^1]);
    }

    [Fact]
    public void Centroid_OfMorskieOko_FallsNearTheTarn()
    {
        MountainLake morskieOko = MountainLakeData.All.Single(l => l.Name == "Morskie Oko");

        GeoPoint centroid = morskieOko.Centroid;

        centroid.Latitude.Should().BeApproximately(49.197, 0.01);
        centroid.Longitude.Should().BeApproximately(20.070, 0.01);
    }

    [Fact]
    public void WithinBounds_TatraBox_ReturnsMorskieOko()
    {
        // A box around the Morskie Oko / Rysy area (the LOD demo extent).
        MapBounds tatra = Bounds(49.18, 20.05, 49.21, 20.09);

        MountainLakeData.WithinBounds(tatra).Select(l => l.Name)
            .Should().Contain("Morskie Oko");
    }

    [Fact]
    public void WithinBounds_FarAwayBox_ReturnsNothing()
    {
        // Warsaw — far from any Tatra tarn (guards against the "several Morskie Oko in OSM" trap too).
        MapBounds warsaw = Bounds(52.20, 20.95, 52.26, 21.05);

        MountainLakeData.WithinBounds(warsaw).Should().BeEmpty();
    }

    [Fact]
    public void WithinBounds_TightBoxAroundOneLake_ExcludesDistantLakes()
    {
        // Tight box on Morskie Oko only — Czarny Staw pod Rysami (≈1.5 km south) must NOT be selected.
        MapBounds onlyMorskieOko = Bounds(49.193, 20.065, 49.202, 20.075);

        System.Collections.Generic.List<string> names = MountainLakeData.WithinBounds(onlyMorskieOko).Select(l => l.Name).ToList();

        names.Should().Contain("Morskie Oko");
        names.Should().NotContain("Czarny Staw pod Rysami");
    }
}
