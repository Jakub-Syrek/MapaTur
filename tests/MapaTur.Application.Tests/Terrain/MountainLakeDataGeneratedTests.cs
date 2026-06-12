using System.Linq;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Invariants of the GENERATED lake table (MountainLakeData.Lakes.g.cs, produced from OSM by
/// testdata/maps/fetch-tatra-lakes.py). These guard future REGENERATIONS: a bad Overpass answer,
/// a broken filter or a botched simplification should fail here, not on the device.
/// </summary>
public sealed class MountainLakeDataGeneratedTests
{
    // The generator's own bake window (the tatry.dem extent) and tarn-elevation filter.
    private const double West = 19.50;
    private const double South = 49.10;
    private const double East = 20.40;
    private const double North = 49.40;
    private const double MinTarnElevation = 1000.0;
    private const double MaxTatraElevation = 2700.0;

    [Fact]
    public void All_CoversFarMoreLakesThanTheOldHandPastedTable()
    {
        MountainLakeData.All.Count.Should().BeGreaterThan(100, "the OSM bake replaced a 12-lake hand-pasted table");
    }

    [Fact]
    public void All_EveryElevation_IsAPlausibleTarnElevation()
    {
        MountainLakeData.All.Should().OnlyContain(
            l => l.ElevationMeters >= MinTarnElevation && l.ElevationMeters <= MaxTatraElevation,
            "the generator filters by DEM-sampled centroid elevation, so reservoirs/fish ponds must not slip in");
    }

    [Fact]
    public void All_EveryOutline_HasAtLeastATriangle()
    {
        MountainLakeData.All.Should().OnlyContain(l => l.Outline.Count >= 4, "ear-clipping needs a real polygon");
    }

    [Fact]
    public void All_EveryOutline_HasNoClosingDuplicateVertex()
    {
        MountainLakeData.All.Should().OnlyContain(
            l => !l.Outline[0].Equals(l.Outline[l.Outline.Count - 1]),
            "rings are stored open; a closing duplicate would produce a degenerate ear");
    }

    [Fact]
    public void All_EveryCentroid_FallsInsideTheBakeWindow()
    {
        MountainLakeData.All.Select(l => l.Centroid).Should().OnlyContain(
            c => c.Latitude >= South && c.Latitude <= North && c.Longitude >= West && c.Longitude <= East,
            "the Overpass query is bounded to the tatry.dem extent");
    }

    [Fact]
    public void All_NoTwoLakes_ShareANameAndACentroid()
    {
        // Group names are real in the Tatras ("Hincove oká", "Dlhé pleso" = several distinct ponds),
        // so a duplicate means same name AND centroids within ~100 m — i.e. the SAME water body kept
        // twice (the way-vs-relation dedupe failed), not two genuinely different tarns.
        var sameNameSamePlace = MountainLakeData.All
            .Where(l => !string.IsNullOrEmpty(l.Name))
            .GroupBy(l => l.Name)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g
                .SelectMany((a, i) => g.Skip(i + 1).Select(b => (g.Key, A: a.Centroid, B: b.Centroid))))
            .Where(p =>
                System.Math.Abs(p.A.Latitude - p.B.Latitude) * 111_320.0 < 100.0
                && System.Math.Abs(p.A.Longitude - p.B.Longitude) * 72_700.0 < 100.0)
            .Select(p => p.Key)
            .ToList();

        sameNameSamePlace.Should().BeEmpty("the way-vs-relation dedupe must collapse double-mapped lakes");
    }

    private static readonly string[] HeadlineTarns =
    {
        "Morskie Oko", "Czarny Staw pod Rysami", "Czarny Staw Gąsienicowy", "Wielki Staw Polski",
        "Veľké Hincovo pleso", "Štrbské pleso", "Popradské pleso", "Skalnaté pleso",
    };

    [Fact]
    public void All_ContainsTheHeadlineTarns_OnBothSidesOfTheBorder()
    {
        var names = MountainLakeData.All.Select(l => l.Name).ToHashSet();

        names.Should().Contain(HeadlineTarns);
    }
}