using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class MarkerHitTesterTests
{
    [Fact]
    public void HitTest_NullScreenPositions_Throws()
    {
        Action act = () => MarkerHitTester.HitTest(null!, 100f, 100f, 20f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void HitTest_NegativeRadius_Throws()
    {
        var positions = new Vector3?[] { new Vector3(100f, 100f, 0.5f) };

        Action act = () => MarkerHitTester.HitTest(positions, 100f, 100f, -1f);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void HitTest_EmptyList_ReturnsNull()
    {
        var result = MarkerHitTester.HitTest(Array.Empty<Vector3?>(), 100f, 100f, 20f);

        result.Should().BeNull();
    }

    [Fact]
    public void HitTest_TapOnMarker_ReturnsItsIndex()
    {
        var positions = new Vector3?[]
        {
            new Vector3(50f, 50f, 0.5f),
            new Vector3(200f, 200f, 0.5f),
        };

        var result = MarkerHitTester.HitTest(positions, 205f, 198f, 20f);

        result.Should().Be(1);
    }

    [Fact]
    public void HitTest_TapOutsideAllRadii_ReturnsNull()
    {
        var positions = new Vector3?[]
        {
            new Vector3(50f, 50f, 0.5f),
            new Vector3(200f, 200f, 0.5f),
        };

        var result = MarkerHitTester.HitTest(positions, 500f, 500f, 20f);

        result.Should().BeNull();
    }

    [Fact]
    public void HitTest_OffFrustumMarkersIgnored()
    {
        var positions = new Vector3?[]
        {
            null,
            new Vector3(100f, 100f, 0.5f),
            null,
        };

        var result = MarkerHitTester.HitTest(positions, 100f, 100f, 20f);

        result.Should().Be(1);
    }

    [Fact]
    public void HitTest_AllOffFrustum_ReturnsNull()
    {
        var positions = new Vector3?[] { null, null };

        var result = MarkerHitTester.HitTest(positions, 100f, 100f, 20f);

        result.Should().BeNull();
    }

    [Fact]
    public void HitTest_OverlappingMarkers_PicksFrontMostByDepth()
    {
        // Two markers under the same tap; index 1 sits closer to the camera (smaller NDC depth)
        // so it should win even though both are within the radius.
        var positions = new Vector3?[]
        {
            new Vector3(100f, 100f, 0.8f),
            new Vector3(102f, 101f, 0.2f),
        };

        var result = MarkerHitTester.HitTest(positions, 100f, 100f, 20f);

        result.Should().Be(1);
    }

    [Fact]
    public void HitTest_SameDepth_PicksNearestToTap()
    {
        var positions = new Vector3?[]
        {
            new Vector3(115f, 100f, 0.5f),
            new Vector3(103f, 100f, 0.5f),
        };

        var result = MarkerHitTester.HitTest(positions, 100f, 100f, 20f);

        result.Should().Be(1);
    }

    [Fact]
    public void HitTest_MarkerExactlyOnRadiusBoundary_CountsAsHit()
    {
        var positions = new Vector3?[] { new Vector3(120f, 100f, 0.5f) };

        var result = MarkerHitTester.HitTest(positions, 100f, 100f, 20f);

        result.Should().Be(0);
    }
}