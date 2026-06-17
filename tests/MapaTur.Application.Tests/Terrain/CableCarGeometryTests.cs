using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Geometry of an aerialway span: the cable hangs from the lower station to the upper one with a
/// downward (−Z is up here is +Z) sag that peaks at mid-span. <see cref="CableCarGeometry"/> samples
/// the curve (for the drawn cable polyline) and gives a point at a parameter t (for a moving cabin).
/// World frame matches the renderer: +Z is up, so the sag is subtracted from Z.
/// </summary>
public sealed class CableCarGeometryTests
{
    private static readonly Vector3 Lower = new(0f, 0f, 1000f);
    private static readonly Vector3 Upper = new(300f, 400f, 1960f); // 500 m horizontal, +960 m up

    [Fact]
    public void PointOnSpan_AtLowerEnd_ReturnsTheLowerStation()
    {
        CableCarGeometry.PointOnSpan(Lower, Upper, sagMeters: 50f, t: 0f).Should().Be(Lower);
    }

    [Fact]
    public void PointOnSpan_AtUpperEnd_ReturnsTheUpperStation()
    {
        CableCarGeometry.PointOnSpan(Lower, Upper, sagMeters: 50f, t: 1f).Should().Be(Upper);
    }

    [Fact]
    public void PointOnSpan_AtMidspan_SitsOnTheChordXY_DroopedBySagMeters()
    {
        Vector3 mid = CableCarGeometry.PointOnSpan(Lower, Upper, sagMeters: 50f, t: 0.5f);

        mid.X.Should().BeApproximately(150f, 0.001f);
        mid.Y.Should().BeApproximately(200f, 0.001f);
        // Chord mid-Z is 1480; the cable droops the full sag (50 m) below it at mid-span.
        mid.Z.Should().BeApproximately(1480f - 50f, 0.001f);
    }

    [Fact]
    public void PointOnSpan_SagIsZeroAtTheStations()
    {
        // The droop term 4·t·(1−t) is 0 at both ends, so no sag is applied there.
        CableCarGeometry.PointOnSpan(Lower, Upper, sagMeters: 80f, t: 0f).Z.Should().Be(Lower.Z);
        CableCarGeometry.PointOnSpan(Lower, Upper, sagMeters: 80f, t: 1f).Z.Should().Be(Upper.Z);
    }

    [Fact]
    public void SampleCable_ReturnsSegmentsPlusOnePoints_StartingAndEndingAtTheStations()
    {
        var pts = CableCarGeometry.SampleCable(Lower, Upper, sagMeters: 50f, segments: 8);

        pts.Should().HaveCount(9);
        pts[0].Should().Be(Lower);
        pts[^1].Should().Be(Upper);
    }

    [Fact]
    public void CabinParameter_StartsCabinsAtOppositeEnds_ForACounterweightedPair()
    {
        // At t=0s, cabin 0 sits at the lower station and cabin 1 (a whole half-cycle ahead) at the upper one,
        // so a 2-cabin span runs one up while the other comes down.
        CableCarGeometry.CabinParameter(0.0, 0.05, index: 0, count: 2).Should().BeApproximately(0f, 1e-5f);
        CableCarGeometry.CabinParameter(0.0, 0.05, index: 1, count: 2).Should().BeApproximately(1f, 1e-5f);
    }

    [Fact]
    public void CabinParameter_PingPongs_AndStaysInUnitRange()
    {
        // A cabin shuttles 0 → 1 → 0: at half a one-way trip it is mid-span, at a full trip at the top,
        // at a full round trip back at the bottom. Never leaves [0,1].
        const double speed = 0.5; // one-way trip = 2 s
        CableCarGeometry.CabinParameter(1.0, speed, 0, 2).Should().BeApproximately(0.5f, 1e-5f); // x=0.5
        CableCarGeometry.CabinParameter(2.0, speed, 0, 2).Should().BeApproximately(1f, 1e-5f);    // x=1, top
        CableCarGeometry.CabinParameter(4.0, speed, 0, 2).Should().BeApproximately(0f, 1e-5f);    // x=2, back
        for (double s = 0; s < 30; s += 0.37)
        {
            CableCarGeometry.CabinParameter(s, 0.13, 1, 3).Should().BeInRange(0f, 1f);
        }
    }
}