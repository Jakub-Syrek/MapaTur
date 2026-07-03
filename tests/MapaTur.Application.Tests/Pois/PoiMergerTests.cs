using FluentAssertions;

using MapaTur.Application.Pois;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;

namespace MapaTur.Application.Tests.Pois;

/// <summary>
/// Behaviour pinning for <see cref="PoiMerger"/> — the single place the shown/searched POI set is merged
/// from DOWNLOADED (OSM) and BUNDLED (curated gazetteer) entries. Grew out of two real duplicate-label bugs:
/// "Honoratka / Zmarzła Przełączka Wyżnia" (same node, two names → name suppression) and "Schronisko nad
/// Morskim Okiem / Schronisko PTTK Morskie Oko" (SAME building, different names → name dedup failed, both
/// rendered — hence the proximity dedup).
/// </summary>
public sealed class PoiMergerTests
{
    private static MountainPoi Poi(long id, string name, double lat, double lon, PoiKind kind = PoiKind.Hut)
        => new(id, name, new GeoPoint(lat, lon), kind);

    [Fact]
    public void DownloadedWins_BundledWithTheSameNameIsSkipped()
    {
        var downloaded = new[] { Poi(1, "Murowaniec", 49.2436, 20.0075) };
        var bundled = new[] { Poi(-1, "Murowaniec", 49.2437, 20.0076) };

        var merged = PoiMerger.Merge(downloaded, bundled, suppressedDownloadedNames: null);

        merged.Should().ContainSingle(p => p.Name == "Murowaniec").Which.Id.Should().Be(1);
    }

    [Fact]
    public void BundledUnderADifferentName_ButOnTheSameSpot_IsSkipped()
    {
        // The Morskie Oko bug: identical building, different names — name dedup missed it and two labels
        // stacked. A bundled POI of the same KIND within the proximity radius of a downloaded one is the
        // same real-world feature; the downloaded (fresher) entry wins.
        var downloaded = new[] { Poi(300719423, "Schronisko PTTK Morskie Oko", 49.201428, 20.071260) };
        var bundled = new[] { Poi(-3, "Schronisko nad Morskim Okiem", 49.201428, 20.071260) };

        var merged = PoiMerger.Merge(downloaded, bundled, suppressedDownloadedNames: null);

        merged.Should().ContainSingle(p => p.Id == 300719423);
        merged.Should().NotContain(p => p.Name == "Schronisko nad Morskim Okiem");
    }

    [Fact]
    public void BundledNearADownloadedOfADifferentKind_IsKept()
    {
        // A pass 30 m from a hut is NOT the same feature — proximity dedup only applies within one kind.
        var downloaded = new[] { Poi(1, "Schronisko X", 49.2000, 20.0700, PoiKind.Hut) };
        var bundled = new[] { Poi(-9, "Przełęcz Y", 49.2001, 20.0701, PoiKind.Pass) };

        var merged = PoiMerger.Merge(downloaded, bundled, suppressedDownloadedNames: null);

        merged.Should().Contain(p => p.Name == "Przełęcz Y");
    }

    [Fact]
    public void BundledFartherThanTheRadius_IsKept()
    {
        // Stare Schronisko is a REAL second building ~70+ m away in OSM as its own node — two distinct
        // downloaded POIs must never collapse, and a bundled entry beyond the radius stays too.
        var downloaded = new[] { Poi(1, "Schronisko A", 49.2014, 20.0713) };
        var bundled = new[] { Poi(-5, "Schronisko B", 49.2030, 20.0713) }; // ~180 m north

        var merged = PoiMerger.Merge(downloaded, bundled, suppressedDownloadedNames: null);

        merged.Should().HaveCount(2);
    }

    [Fact]
    public void SuppressedDownloadedNames_AreDropped()
    {
        var downloaded = new[] { Poi(9041097016, "Zmarzła Przełączka Wyżnia", 49.218842, 20.020172, PoiKind.Pass) };
        var bundled = new[] { Poi(-100, "Honoratka", 49.218842, 20.020172, PoiKind.Pass) };
        var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Zmarzła Przełączka Wyżnia" };

        var merged = PoiMerger.Merge(downloaded, bundled, suppressed);

        merged.Should().ContainSingle().Which.Name.Should().Be("Honoratka");
    }

    [Fact]
    public void NoDownloaded_AllBundledShown()
    {
        var bundled = new[] { Poi(-1, "Murowaniec", 49.2436, 20.0075), Poi(-3, "Schronisko nad Morskim Okiem", 49.2014, 20.0713) };

        var merged = PoiMerger.Merge(null, bundled, suppressedDownloadedNames: null);

        merged.Should().HaveCount(2);
    }
}