using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Tests.Terrain;

public sealed class TrailDeduplicatorTests
{
    // ~1 m in latitude degrees and (at 49°N) in longitude degrees, so tests can place trails a known number of
    // metres apart without re-deriving the conversion each time.
    private const double MetrePerDegLat = 1.0 / 111_320.0;
    private const double MetrePerDegLon = 1.0 / (111_320.0 * 0.6561); // cos(49°) ≈ 0.6561

    private static GeoPoint At(double eastMetres, double northMetres) =>
        new(49.0 + (northMetres * MetrePerDegLat), 19.0 + (eastMetres * MetrePerDegLon));

    private static Trail Trail(long id, params (double east, double north)[] pts) =>
        new(
            id,
            $"t{id}",
            new List<TrailMarking> { new(PttkColor.Red) },
            pts.Select(p => At(p.east, p.north)).ToList());

    // A trail running straight east along y = north, sampled every `stepMetres` from 0 to `lengthMetres`.
    private static Trail StraightEast(long id, double northMetres, double lengthMetres, double stepMetres = 10.0)
    {
        var pts = new List<(double, double)>();
        for (double x = 0; x <= lengthMetres + 1e-6; x += stepMetres)
        {
            pts.Add((x, northMetres));
        }

        return Trail(id, pts.ToArray());
    }

    [Fact]
    public void Deduplicate_Null_Throws()
    {
        Action act = () => TrailDeduplicator.Deduplicate(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Deduplicate_Empty_ReturnsEmpty()
    {
        TrailDeduplicator.Deduplicate(Array.Empty<Trail>()).Should().BeEmpty();
    }

    [Fact]
    public void Deduplicate_SingleTrail_IsKept()
    {
        var trails = new[] { StraightEast(1, 0, 200) };

        TrailDeduplicator.Deduplicate(trails).Should().ContainSingle().Which.Should().BeSameAs(trails[0]);
    }

    [Fact]
    public void Deduplicate_NearParallelDuplicatePair_KeepsOnlyOne()
    {
        // Two trails running east, 6 m apart for their whole length — the OSM relation + underlying way case.
        var a = StraightEast(1, 0, 200);
        var b = StraightEast(2, 6, 200);

        var kept = TrailDeduplicator.Deduplicate(new[] { a, b });

        kept.Should().HaveCount(1);
    }

    [Fact]
    public void Deduplicate_DuplicatePair_KeepsTheLongerOfThePair()
    {
        // Same path, but trail 1 is longer (300 m) than trail 2 (200 m). The longer/more-detailed one survives.
        var longer = StraightEast(1, 0, 300);
        var shorter = StraightEast(2, 6, 200);

        var kept = TrailDeduplicator.Deduplicate(new[] { shorter, longer });

        kept.Should().ContainSingle().Which.Id.Should().Be(longer.Id);
    }

    [Fact]
    public void Deduplicate_TwoGenuinelySeparateTrails_KeepsBoth()
    {
        // Parallel but 80 m apart (well beyond the lateral tolerance) — two different paths, keep both.
        var a = StraightEast(1, 0, 200);
        var b = StraightEast(2, 80, 200);

        TrailDeduplicator.Deduplicate(new[] { a, b }).Should().HaveCount(2);
    }

    [Fact]
    public void Deduplicate_CrossingTrails_KeepsBoth()
    {
        // One east-west, one north-south crossing it: they coincide only at the crossing, not for most of either's
        // length, so neither is a duplicate of the other.
        var ew = StraightEast(1, 0, 200);
        var ns = Trail(2, (100, -100), (100, 100));

        TrailDeduplicator.Deduplicate(new[] { ew, ns }).Should().HaveCount(2);
    }

    [Fact]
    public void Deduplicate_PartialOverlap_KeepsBoth()
    {
        // Trail 2 runs alongside trail 1 (6 m off) for only its first ~30%, then diverges far away. Below the
        // 70% threshold → it is a genuinely different path for most of its length → keep both.
        var a = StraightEast(1, 0, 300);
        var b = Trail(
            2,
            (0, 6), (30, 6), (60, 6), (90, 6),    // ~30% alongside a (within tolerance)
            (120, 200), (150, 400), (200, 600));  // then veers off hundreds of metres away

        TrailDeduplicator.Deduplicate(new[] { a, b }).Should().HaveCount(2);
    }

    [Fact]
    public void Deduplicate_PreservesOriginalOrderOfKeptTrails()
    {
        // Three distinct trails (no duplicates) should come back in input order, untouched.
        var a = StraightEast(1, 0, 200);
        var b = StraightEast(2, 80, 200);
        var c = StraightEast(3, 160, 200);

        var kept = TrailDeduplicator.Deduplicate(new[] { a, b, c });

        kept.Select(t => t.Id).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void Deduplicate_ThreeCopiesOfSamePath_CollapseToOne()
    {
        var a = StraightEast(1, 0, 200);
        var b = StraightEast(2, 5, 200);
        var c = StraightEast(3, 9, 200);

        TrailDeduplicator.Deduplicate(new[] { a, b, c }).Should().HaveCount(1);
    }
}