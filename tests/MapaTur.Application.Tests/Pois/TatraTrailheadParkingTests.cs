using FluentAssertions;

using MapaTur.Application.Pois;
using MapaTur.Domain.Pois;

namespace MapaTur.Application.Tests.Pois;

/// <summary>
/// Behaviour pinning for <see cref="TatraTrailheadParking"/> — the curated trailhead car parks. Added
/// 2026-08-05 because a route in the Roháče (Zverovka → Ťatliakovo jazero → Rohacze) had no start point
/// to pick: the cached POI set for that area holds 19 entries, ALL unnamed, and not a single
/// <see cref="PoiKind.Parking"/>. Curated entries make the trailhead searchable OFFLINE, exactly like
/// <see cref="TatraHuts"/> and <see cref="TatraPasses"/>; a later "Pobierz POI" download still wins
/// (deduped by name / proximity in <see cref="PoiMerger"/>).
/// </summary>
public sealed class TatraTrailheadParkingTests
{
    [Fact]
    public void ZverovkaSpalena_IsPresent_AtTheOsmNode()
    {
        // The only NAMED amenity=parking in the whole Roháče area (Overpass, 2026-08-05): "Zverovka -
        // Spálená", the paid surface car park below Spálená (2083 m) and Predný Salatín — the trailhead
        // for Ťatliakovo jazero and the Roháče ridge.
        MountainPoi parking = TatraTrailheadParking.All
            .Should().ContainSingle(p => p.Name.Contains("Spálená", StringComparison.Ordinal)).Subject;

        parking.Kind.Should().Be(PoiKind.Parking);
        parking.Position.Latitude.Should().BeApproximately(49.23871, 0.00005);
        parking.Position.Longitude.Should().BeApproximately(19.71407, 0.00005);
    }

    [Fact]
    public void ZverovkaSpalena_CarriesTheElevationSampledFromOurOwnDem()
    {
        // 1031 m — sampled from our z16 DEM tile (36356/22440) at the node, not copied from a web page.
        // Control samples on the same run: Spálená summit 2078 m (OSM says 2083), Morskie Oko hut 1406 m
        // (curated 1410) — a 256² z16 sample lands within ~5 m, so this tolerance is the honest one.
        MountainPoi parking = TatraTrailheadParking.All.Single(p => p.Kind == PoiKind.Parking && p.Name.Contains("Spálená", StringComparison.Ordinal));

        parking.ElevationMeters.Should().BeApproximately(1031, 6);
    }

    [Fact]
    public void EveryEntry_IsAParkingWithACuratedNegativeId()
    {
        TatraTrailheadParking.All.Should().OnlyContain(p => p.Kind == PoiKind.Parking);
        TatraTrailheadParking.All.Should().OnlyContain(p => p.Id < 0, "negative ids mark curated entries so they never collide with OSM node ids");
    }

    [Fact]
    public void Ids_DoNotCollideWithTheOtherCuratedGazetteers()
    {
        // Huts occupy -1..-16 and passes -100..; a reused id would make two different places share
        // identity in any id-keyed lookup.
        long[] taken = TatraHuts.All.Select(p => p.Id).Concat(TatraPasses.All.Select(p => p.Id)).ToArray();

        TatraTrailheadParking.All.Select(p => p.Id).Should().NotIntersectWith(taken);
        TatraTrailheadParking.All.Select(p => p.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Names_AreNonEmpty_SoTheyReachTheSearchGazetteer()
    {
        // PlaceGazetteer drops entries with a blank name — a nameless car park would be invisible to the
        // route picker, which is the whole point of curating it.
        TatraTrailheadParking.All.Should().OnlyContain(p => !string.IsNullOrWhiteSpace(p.Name));
    }

    [Fact]
    public void ADownloadedParkingOnTheSameSpot_ReplacesTheCuratedOne()
    {
        // Same contract as the huts: fresher OSM data wins, no doubled marker.
        MountainPoi curated = TatraTrailheadParking.All.Single(p => p.Name.Contains("Spálená", StringComparison.Ordinal));
        var downloaded = new[]
        {
            new MountainPoi(9_001, "Parking Zverovka", curated.Position, PoiKind.Parking, 1031),
        };

        IReadOnlyList<MountainPoi> merged = PoiMerger.Merge(downloaded, TatraTrailheadParking.All, suppressedDownloadedNames: null);

        merged.Should().ContainSingle(p => p.Kind == PoiKind.Parking).Which.Id.Should().Be(9_001);
    }
}