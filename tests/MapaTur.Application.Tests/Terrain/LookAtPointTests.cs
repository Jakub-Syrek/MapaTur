using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Krok 1: the look-at point — raycast from the camera through the screen centre and
/// intersect the terrain. This replaces <see cref="Camera3D.Target"/> as the LOD centre:
/// the highest detail belongs where the camera is aimed, which over mountains is far away.
/// </summary>
public sealed class LookAtPointTests
{
    private static DemRaster Flat(double heightMeters, GeoPoint sw, GeoPoint ne, int cols = 11, int rows = 11)
    {
        var samples = new float[cols * rows];
        Array.Fill(samples, (float)heightMeters);
        return new DemRaster(cols, rows, new MapBounds(sw, ne), samples);
    }

    [Fact]
    public void ScreenCenterRay_OriginIsCameraPositionAndDirectionIsForward()
    {
        var camera = new Camera3D
        {
            Target = new Vector3(100f, 200f, 50f),
            Distance = 1000f,
            AzimuthRadians = 0.7f,
            PitchRadians = 0.5f,
        };

        Ray ray = LookAtPoint.ScreenCenterRay(camera, 800f, 600f);

        Vector3 expectedForward = Vector3.Normalize(camera.Target - camera.Position);
        ray.Origin.X.Should().BeApproximately(camera.Position.X, 1e-2f);
        ray.Origin.Y.Should().BeApproximately(camera.Position.Y, 1e-2f);
        ray.Origin.Z.Should().BeApproximately(camera.Position.Z, 1e-2f);
        ray.Direction.X.Should().BeApproximately(expectedForward.X, 1e-3f);
        ray.Direction.Y.Should().BeApproximately(expectedForward.Y, 1e-3f);
        ray.Direction.Z.Should().BeApproximately(expectedForward.Z, 1e-3f);
    }

    [Fact]
    public void ScreenPointRay_AtCentre_EqualsScreenCenterRay()
    {
        var camera = new Camera3D
        {
            Target = new Vector3(10f, -20f, 5f),
            Distance = 1500f,
            AzimuthRadians = 1.2f,
            PitchRadians = 0.4f,
        };

        Ray centre = LookAtPoint.ScreenPointRay(camera, 400f, 300f, 800f, 600f);
        Ray helper = LookAtPoint.ScreenCenterRay(camera, 800f, 600f);

        centre.Direction.X.Should().BeApproximately(helper.Direction.X, 1e-4f);
        centre.Direction.Y.Should().BeApproximately(helper.Direction.Y, 1e-4f);
        centre.Direction.Z.Should().BeApproximately(helper.Direction.Z, 1e-4f);
    }

    [Fact]
    public void Resolve_CameraAimedAtGround_ReturnsPointAtTarget()
    {
        // Target sits ON the flat terrain (z = 100); the centre ray therefore hits at the target.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var camera = new Camera3D
        {
            Target = new Vector3(0f, 0f, 100f),
            Distance = 500f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 3f, // 60° above horizon — looking down at the ground
        };

        Vector3? hit = LookAtPoint.Resolve(camera, 800f, 600f, dem, anchor, verticalExaggeration: 1f);

        hit.Should().NotBeNull();
        hit!.Value.X.Should().BeApproximately(0f, 2f);
        hit.Value.Y.Should().BeApproximately(0f, 2f);
        hit.Value.Z.Should().BeApproximately(100f, 2f);
    }

    [Fact]
    public void ResolveAt_CentrePixel_MatchesResolve()
    {
        // The tap-to-plan resolver through the CENTRE pixel must agree with the look-at resolver.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var camera = new Camera3D
        {
            Target = new Vector3(0f, 0f, 100f),
            Distance = 500f,
            AzimuthRadians = 0.3f,
            PitchRadians = MathF.PI / 3f,
        };

        Vector3? tap = LookAtPoint.ResolveAt(camera, 400f, 300f, 800f, 600f, dem, anchor, verticalExaggeration: 1f);
        Vector3? look = LookAtPoint.Resolve(camera, 800f, 600f, dem, anchor, verticalExaggeration: 1f);

        tap.Should().NotBeNull();
        look.Should().NotBeNull();
        Vector3.Distance(tap!.Value, look!.Value).Should().BeLessThan(2f);
    }

    [Fact]
    public void ResolveAt_SkyPixel_ReturnsNull()
    {
        // A shallow camera: the TOP of the frame points above the horizon — a tap there is sky, not terrain.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var camera = new Camera3D
        {
            Target = new Vector3(0f, 0f, 100f),
            Distance = 500f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 12f, // 15° — top of a 45° frame looks upward
        };

        Vector3? tap = LookAtPoint.ResolveAt(camera, 400f, 0f, 800f, 600f, dem, anchor, verticalExaggeration: 1f);

        tap.Should().BeNull();
    }

    [Fact]
    public void ResolveAt_BottomPixel_HitsCloserToTheCameraThanTheCentre()
    {
        // The bottom of the frame looks more steeply down, so its terrain hit lies nearer the camera.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var camera = new Camera3D
        {
            Target = new Vector3(0f, 0f, 100f),
            Distance = 800f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 4f,
        };

        Vector3? centre = LookAtPoint.ResolveAt(camera, 400f, 300f, 800f, 600f, dem, anchor, verticalExaggeration: 1f);
        Vector3? bottom = LookAtPoint.ResolveAt(camera, 400f, 599f, 800f, 600f, dem, anchor, verticalExaggeration: 1f);

        centre.Should().NotBeNull();
        bottom.Should().NotBeNull();
        Vector3.Distance(bottom!.Value, camera.Position)
            .Should().BeLessThan(Vector3.Distance(centre!.Value, camera.Position));
    }

    [Fact]
    public void Resolve_TargetFloatsAboveTerrain_FindsRealTerrainBeyondTarget()
    {
        // Target floats at z = 300 over terrain at z = 100. The look-at point is NOT the target —
        // it is the real ground further along the view ray. Proves Resolve raycasts, not returns Target.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var camera = new Camera3D
        {
            Target = new Vector3(0f, 0f, 300f),
            Distance = 500f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 4f, // 45°
        };

        Vector3? hit = LookAtPoint.Resolve(camera, 800f, 600f, dem, anchor, verticalExaggeration: 1f);

        hit.Should().NotBeNull();
        hit!.Value.Z.Should().BeApproximately(100f, 2f);          // on the real terrain
        hit.Value.X.Should().BeLessThan(-50f);                    // beyond the target, down the view ray (−X at azimuth 0)
    }

    [Fact]
    public void Resolve_CameraLookingUp_ReturnsNull()
    {
        // Camera below its target, aimed up into the sky; the centre ray never meets the ground.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var camera = new Camera3D
        {
            Target = new Vector3(0f, 0f, 500f),
            Distance = 300f,
            AzimuthRadians = 0f,
            PitchRadians = -0.6f, // camera sits below the target → forward points up
        };

        Vector3? hit = LookAtPoint.Resolve(camera, 800f, 600f, dem, anchor, verticalExaggeration: 1f);

        hit.Should().BeNull();
    }

    [Fact]
    public void Resolve_CentreHitsSky_LowerFrameFallbackFindsTerrainBelow()
    {
        // Looking horizontally across a ridge: the eye is above the terrain and the CENTRE ray skims out over
        // it (sky in the middle of the frame), so it never descends to the ground. A lower-frame sample tilts
        // down and finds the terrain you are actually flying toward — without it, detail would not stream.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var camera = new Camera3D
        {
            Target = new Vector3(0f, 0f, 200f), // eye ends up at z=200, well above terrain at z=100
            Distance = 500f,
            AzimuthRadians = 0f,
            PitchRadians = 0f, // horizontal view → centre ray never meets the lower terrain
        };

        float[] fallbacks = { 0.7f, 0.85f };
        Vector3? noFallback = LookAtPoint.Resolve(camera, 800f, 600f, dem, anchor, verticalExaggeration: 1f);
        Vector3? withFallback = LookAtPoint.Resolve(
            camera, 800f, 600f, dem, anchor, verticalExaggeration: 1f, lowerFrameFallbacks: fallbacks);

        noFallback.Should().BeNull("the centre ray at eye height never descends to the lower terrain");
        withFallback.Should().NotBeNull("a lower-frame sample tilts down and hits the terrain");
        withFallback!.Value.Z.Should().BeApproximately(100f, 2f);
    }

    [Fact]
    public void Resolve_CentreHits_FallbacksAreNotConsulted()
    {
        // When the centre already hits, the result must be the centre hit regardless of any fallbacks supplied.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var camera = new Camera3D
        {
            Target = new Vector3(0f, 0f, 100f),
            Distance = 500f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 3f, // looking down — centre hits
        };

        float[] fallbacks = { 0.8f };
        Vector3? hit = LookAtPoint.Resolve(
            camera, 800f, 600f, dem, anchor, verticalExaggeration: 1f, lowerFrameFallbacks: fallbacks);

        hit.Should().NotBeNull();
        hit!.Value.X.Should().BeApproximately(0f, 2f);
        hit.Value.Y.Should().BeApproximately(0f, 2f);
    }

    [Fact]
    public void Resolve_ZeroViewport_ReturnsNull()
    {
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var camera = new Camera3D { Target = new Vector3(0f, 0f, 100f), Distance = 500f };

        Vector3? hit = LookAtPoint.Resolve(camera, 0f, 600f, dem, anchor, verticalExaggeration: 1f);

        hit.Should().BeNull();
    }
}