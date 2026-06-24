using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

public sealed class SummitSourcesTests
{
    private static readonly NamedSummit Rysy = new("Rysy", new GeoPoint(49.1795, 20.0882), 2501);
    private static readonly NamedSummit Giewont = new("Giewont", new GeoPoint(49.2509, 19.9341), 1894);

    [Fact]
    public void Combine_EmptyPrimary_ReturnsFallback()
    {
        var combined = SummitSources.Combine(Array.Empty<NamedSummit>(), new[] { Rysy });

        combined.Should().ContainSingle().Which.Name.Should().Be("Rysy");
    }

    [Fact]
    public void Combine_EmptyFallback_ReturnsPrimary()
    {
        var combined = SummitSources.Combine(new[] { Rysy }, Array.Empty<NamedSummit>());

        combined.Should().ContainSingle().Which.Name.Should().Be("Rysy");
    }

    [Fact]
    public void Combine_AddsFallbackSummitsNotPresentInPrimary()
    {
        var combined = SummitSources.Combine(new[] { Rysy }, new[] { Giewont });

        combined.Select(s => s.Name).Should().BeEquivalentTo("Rysy", "Giewont");
    }

    [Fact]
    public void Combine_DropsFallbackSummitWithinDedupeRadiusOfPrimary()
    {
        // Same summit, slightly different gazetteer coordinate (~15 m away) — must not double up.
        var fallbackRysy = new NamedSummit("Rysy (gaz)", new GeoPoint(49.1796, 20.0883), 2503);

        var combined = SummitSources.Combine(new[] { Rysy }, new[] { fallbackRysy }, dedupeMeters: 300);

        combined.Should().ContainSingle().Which.Name.Should().Be("Rysy");
    }

    [Fact]
    public void Combine_KeepsPrimaryWhenBothSourcesHaveTheSummit()
    {
        var fallbackRysy = new NamedSummit("Rysy (gaz)", new GeoPoint(49.1796, 20.0883), 2503);

        var combined = SummitSources.Combine(new[] { Rysy }, new[] { fallbackRysy, Giewont }, dedupeMeters: 300);

        combined.Select(s => s.Name).Should().BeEquivalentTo("Rysy", "Giewont");
    }

    [Fact]
    public void Deduplicate_CollapsesNearDuplicatesKeepingTheHighest()
    {
        // OSM carries a multi-summit massif as separate nodes ~15 m apart (e.g. Rysy ×3).
        var rysyLow = new NamedSummit("Rysy", new GeoPoint(49.17955, 20.08825), 2499);
        var rysyMid = new NamedSummit("Rysy", new GeoPoint(49.17945, 20.08815), 2472);

        var deduped = SummitSources.Deduplicate(new[] { rysyLow, Rysy, rysyMid });

        deduped.Should().ContainSingle().Which.ElevationMeters.Should().Be(2501);
    }

    [Fact]
    public void Deduplicate_KeepsDistinctSummits()
    {
        var deduped = SummitSources.Deduplicate(new[] { Rysy, Giewont });

        deduped.Select(s => s.Name).Should().BeEquivalentTo("Rysy", "Giewont");
    }

    [Fact]
    public void Deduplicate_KeepsDistinctNamedSummitsThatSitClose()
    {
        // The iconic Kościelec (2155) and Zadni Kościelec (2162) are ~180 m apart — distinct peaks.
        // Collapsing by distance alone silently dropped the lower one (Kościelec); different names must survive.
        var koscielec = new NamedSummit("Kościelec", new GeoPoint(49.225278, 20.014444), 2155);
        var zadniKoscielec = new NamedSummit("Zadni Kościelec", new GeoPoint(49.2237, 20.0156), 2162);

        var deduped = SummitSources.Deduplicate(new[] { koscielec, zadniKoscielec });

        deduped.Select(s => s.Name).Should().BeEquivalentTo("Kościelec", "Zadni Kościelec");
    }

    [Fact]
    public void Combine_FlagsMatchedPrimaryAsCurated()
    {
        // A curated (fallback) summit matching an OSM (primary) one endorses the primary as iconic.
        var curatedRysy = new NamedSummit("Rysy", new GeoPoint(49.1796, 20.0883), 2503);

        var combined = SummitSources.Combine(new[] { Rysy }, new[] { curatedRysy }, dedupeMeters: 300);

        combined.Should().ContainSingle().Which.Curated.Should().BeTrue();
    }

    [Fact]
    public void Combine_DoesNotFlagPrimaryWithoutCuratedMatch()
    {
        var combined = SummitSources.Combine(new[] { Rysy }, new[] { Giewont });

        combined.Single(s => s.Name == "Rysy").Curated.Should().BeFalse();
    }

    [Fact]
    public void Combine_FlagsUnmatchedFallbackAsCurated()
    {
        var combined = SummitSources.Combine(new[] { Rysy }, new[] { Giewont });

        combined.Single(s => s.Name == "Giewont").Curated.Should().BeTrue();
    }

    [Fact]
    public void Deduplicate_EmptyInput_ReturnsEmpty()
    {
        SummitSources.Deduplicate(Array.Empty<NamedSummit>()).Should().BeEmpty();
    }
}