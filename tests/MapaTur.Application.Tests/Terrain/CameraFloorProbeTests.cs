using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="CameraFloorProbe"/>: the world-XY points the camera floor samples each frame.
/// A single sample DIRECTLY under the eye misses the wall the camera is flying TOWARD (the near plane
/// cuts into the slope before the eye is over it) — the probe set adds a look-ahead point and a small
/// ring so the floor rises before the surface is pierced.
/// </summary>
public sealed class CameraFloorProbeTests
{
    private static readonly Vector2 Eye = new(1000f, -2000f);

    [Fact]
    public void should_probe_the_eye_position_itself()
    {
        var points = CameraFloorProbe.ProbePoints(Eye, new Vector2(1f, 0f), aheadMeters: 40f, ringRadiusMeters: 12f);

        points[0].Should().Be(Eye);
    }

    [Fact]
    public void should_probe_ahead_along_the_horizontal_view_direction()
    {
        var points = CameraFloorProbe.ProbePoints(Eye, new Vector2(1f, 0f), aheadMeters: 40f, ringRadiusMeters: 12f);

        points.Should().Contain(p => Vector2.Distance(p, Eye + new Vector2(40f, 0f)) < 0.01f);
    }

    [Fact]
    public void should_normalise_a_non_unit_view_direction()
    {
        var points = CameraFloorProbe.ProbePoints(Eye, new Vector2(0f, -8f), aheadMeters: 40f, ringRadiusMeters: 12f);

        points.Should().Contain(p => Vector2.Distance(p, Eye + new Vector2(0f, -40f)) < 0.01f);
    }

    [Fact]
    public void should_survive_a_straight_down_view_without_nan()
    {
        // Looking straight down ⇒ horizontal view direction ~zero; the ahead probe degenerates to the eye.
        var points = CameraFloorProbe.ProbePoints(Eye, Vector2.Zero, aheadMeters: 40f, ringRadiusMeters: 12f);

        points.Should().OnlyContain(p => !float.IsNaN(p.X) && !float.IsNaN(p.Y));
        points.Should().Contain(Eye);
    }

    [Fact]
    public void should_place_the_ring_at_the_requested_radius()
    {
        var points = CameraFloorProbe.ProbePoints(Eye, new Vector2(1f, 0f), aheadMeters: 40f, ringRadiusMeters: 12f);

        // Ring points: everything that is neither the eye nor the ahead point sits exactly on the ring.
        var ring = points.Where(p =>
            Vector2.Distance(p, Eye) > 0.01f &&
            Vector2.Distance(p, Eye + new Vector2(40f, 0f)) > 0.01f).ToList();
        ring.Should().NotBeEmpty();
        ring.Should().OnlyContain(p => Math.Abs(Vector2.Distance(p, Eye) - 12f) < 0.01f);
    }
}