using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

public sealed class LocalTangentProjectionTests
{
    private static readonly GeoPoint Origin = new(49.3, 20.0);

    [Fact]
    public void GeoToWorld_AtOrigin_IsZeroXY()
    {
        var world = LocalTangentProjection.GeoToWorld(Origin, 0f, Origin, 1f);

        world.X.Should().BeApproximately(0f, 0.01f);
        world.Y.Should().BeApproximately(0f, 0.01f);
    }

    [Fact]
    public void GeoToWorld_OneDegreeNorth_IsMetersPerLatDegree()
    {
        var north = new GeoPoint(Origin.Latitude + 1.0, Origin.Longitude);

        var world = LocalTangentProjection.GeoToWorld(north, 0f, Origin, 1f);

        world.Y.Should().BeApproximately((float)LocalTangentProjection.MetersPerLatDegree, 1f);
        world.X.Should().BeApproximately(0f, 0.01f);
    }

    [Fact]
    public void GeoToWorld_VerticalExaggerationScalesZ()
    {
        var a = LocalTangentProjection.GeoToWorld(Origin, 100f, Origin, 1f);
        var b = LocalTangentProjection.GeoToWorld(Origin, 100f, Origin, 2.5f);

        a.Z.Should().BeApproximately(100f, 0.01f);
        b.Z.Should().BeApproximately(250f, 0.01f);
    }

    [Fact]
    public void GeoToWorld_SharedOrigin_TilesAlign()
    {
        // Two points; their world offset about a SHARED origin must equal the raw geographic offset,
        // regardless of where each point sits — this is what lets separate tile meshes line up.
        var pA = new GeoPoint(49.4, 20.1);
        var pB = new GeoPoint(49.4, 20.2); // 0.1° east of pA

        var wA = LocalTangentProjection.GeoToWorld(pA, 0f, Origin, 1f);
        var wB = LocalTangentProjection.GeoToWorld(pB, 0f, Origin, 1f);

        double metersPerLon = LocalTangentProjection.MetersPerLatDegree * Math.Cos(Origin.Latitude * Math.PI / 180.0);
        (wB.X - wA.X).Should().BeApproximately((float)(0.1 * metersPerLon), 1f);
        (wB.Y - wA.Y).Should().BeApproximately(0f, 0.5f);
    }

    [Fact]
    public void RoundTrip_GeoToWorldToGeo()
    {
        var p = new GeoPoint(49.55, 20.35);

        var world = LocalTangentProjection.GeoToWorld(p, 0f, Origin, 1f);
        var back = LocalTangentProjection.WorldToGeo(world, Origin);

        back.Latitude.Should().BeApproximately(p.Latitude, 1e-6);
        back.Longitude.Should().BeApproximately(p.Longitude, 1e-6);
    }
}
