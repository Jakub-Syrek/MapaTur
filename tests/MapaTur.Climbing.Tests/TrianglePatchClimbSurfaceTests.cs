using System.Numerics;

using MapaTur.Climbing;

namespace MapaTur.Climbing.Tests;

public sealed class TrianglePatchClimbSurfaceTests
{
    private const float Tolerance = 1e-4f;

    [Fact]
    public void ClosestPoint_should_project_onto_vertical_wall()
    {
        TrianglePatchClimbSurface surface = VerticalWall();

        SurfaceHit hit = surface.ClosestPoint(new Vector3(2f, -1.5f, 1.5f));

        AssertApprox(new Vector3(2f, 0f, 1.5f), hit.Position, Tolerance);
        AssertApprox(new Vector3(0f, -1f, 0f), hit.Normal, Tolerance);
    }

    [Fact]
    public void ClosestPoint_should_clamp_to_patch_border()
    {
        TrianglePatchClimbSurface surface = VerticalWall();

        SurfaceHit hit = surface.ClosestPoint(new Vector3(-2f, -1f, 5f));

        AssertApprox(new Vector3(0f, 0f, 3f), hit.Position, Tolerance);
    }

    [Fact]
    public void SampleSurface_should_return_gravity_consistent_frame_in_z_up()
    {
        TrianglePatchClimbSurface surface = VerticalWall();

        ClimbSurfaceFrame frame = surface.SampleSurface(new Vector3(2f, -1f, 1.5f), ClimbWorld.Gravity);

        AssertApprox(new Vector3(0f, -1f, 0f), frame.Normal, Tolerance);
        Assert.True(frame.UpAlongSurface.Z > 0.99f, $"surface-up should be +Z, got {frame.UpAlongSurface}");
        Assert.True(MathF.Abs(Vector3.Dot(frame.UpAlongSurface, frame.Normal)) < 1e-4f);
    }

    [Fact]
    public void Overhang_normal_should_tilt_downward()
    {
        TrianglePatchClimbSurface surface = OverhangWall();

        SurfaceHit hit = surface.ClosestPoint(new Vector3(2f, -3f, 1.5f));

        Assert.True(hit.Normal.Y < -0.85f, $"outward should stay -Y dominant, got {hit.Normal}");
        Assert.True(hit.Normal.Z < -0.3f, $"a 24-degree overhang normal must point downward, got {hit.Normal}");
    }

    [Fact]
    public void Overhang_patch_should_represent_two_depths_at_one_xy()
    {
        // A heightfield stores one Z per XY; a patch must support a face whose Y varies with Z.
        TrianglePatchClimbSurface surface = OverhangWall();

        SurfaceHit low = surface.ClosestPoint(new Vector3(2f, -0.6f, 0.1f));
        SurfaceHit high = surface.ClosestPoint(new Vector3(2f, -0.6f, 2.9f));

        Assert.True(
            high.Position.Y < low.Position.Y - 0.8f,
            $"wall should lean toward the climber with height: low {low.Position}, high {high.Position}");
    }

    [Fact]
    public void Raycast_should_hit_wall_along_ray()
    {
        TrianglePatchClimbSurface surface = VerticalWall();

        SurfaceHit? hit = surface.Raycast(new Vector3(2f, -2f, 1.5f), new Vector3(0f, 1f, 0f));

        Assert.NotNull(hit);
        AssertApprox(new Vector3(2f, 0f, 1.5f), hit.Value.Position, Tolerance);
        Assert.Equal(2f, hit.Value.Distance, 3);
    }

    [Fact]
    public void Raycast_should_miss_when_pointing_away()
    {
        TrianglePatchClimbSurface surface = VerticalWall();

        SurfaceHit? hit = surface.Raycast(new Vector3(2f, -2f, 1.5f), new Vector3(0f, -1f, 0f));

        Assert.Null(hit);
    }

    [Fact]
    public void Interpolated_normal_should_blend_across_a_bent_edge()
    {
        // Two faces meeting at a vertical crease: a flat south-facing pane and a pane rotated toward east.
        Vector3[] vertices =
        [
            new(0f, 0f, 0f), new(2f, 0f, 0f), new(2f, 0f, 3f), new(0f, 0f, 3f),
            new(4f, -1.2f, 0f), new(4f, -1.2f, 3f)
        ];
        int[] indices =
        [
            0, 1, 3, 1, 2, 3,   // flat pane x 0..2
            1, 4, 2, 4, 5, 2    // bent pane x 2..4, receding in -Y
        ];
        var patch = new ClimbSurfacePatch("bent", vertices, indices, []);
        TrianglePatchClimbSurface surface = new(patch);

        // Query straight at the crease (x=2): the smooth normal should sit between the two face normals.
        SurfaceHit hit = surface.ClosestPoint(new Vector3(2f, -1f, 1.5f));

        Assert.True(hit.Normal.X < -0.1f, $"crease normal should lean east-ward between faces, got {hit.Normal}");
        Assert.True(hit.Normal.Y < -0.7f, $"crease normal should stay outward, got {hit.Normal}");
    }

    [Fact]
    public void FindHolds_should_match_brute_force_radius_query()
    {
        List<ClimbHold> holds = [];
        for (int i = 0; i < 60; i++)
        {
            float x = (i % 10) * 0.7f;
            float z = (i / 10) * 0.9f;
            holds.Add(new ClimbHold($"h-{i}", new Vector3(x, 0f, z), new Vector3(0f, -1f, 0f), 0.9f));
        }

        TrianglePatchClimbSurface surface = VerticalWall(holds);
        Vector3 query = new(3.1f, -0.4f, 1.7f);
        const float radius = 1.3f;

        HashSet<string> expected = holds
            .Where(hold => Vector3.Distance(hold.Position, query) <= radius)
            .Select(hold => hold.Id)
            .ToHashSet();
        HashSet<string> actual = surface.FindHolds(query, radius).Select(hold => hold.Id).ToHashSet();

        Assert.True(expected.Count > 3, "test geometry should produce a non-trivial neighbourhood");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Hold_ids_should_survive_a_lod_style_retessellation()
    {
        ClimbHold[] holds =
        [
            new("stable-a", new Vector3(1f, 0f, 1f), new Vector3(0f, -1f, 0f), 0.9f),
            new("stable-b", new Vector3(3f, 0f, 2f), new Vector3(0f, -1f, 0f), 0.85f)
        ];
        TrianglePatchClimbSurface coarse = VerticalWall(holds);
        TrianglePatchClimbSurface fine = VerticalWallSubdivided(holds);

        HashSet<string> coarseIds = coarse.FindHolds(new Vector3(2f, 0f, 1.5f), 3f).Select(h => h.Id).ToHashSet();
        HashSet<string> fineIds = fine.FindHolds(new Vector3(2f, 0f, 1.5f), 3f).Select(h => h.Id).ToHashSet();

        Assert.Equal(coarseIds, fineIds);
    }

    [Fact]
    public void Projection_should_agree_between_tessellations()
    {
        TrianglePatchClimbSurface coarse = VerticalWall();
        TrianglePatchClimbSurface fine = VerticalWallSubdivided([]);

        Vector3 query = new(2.3f, -0.8f, 1.9f);
        AssertApprox(coarse.ClosestPoint(query).Position, fine.ClosestPoint(query).Position, 1e-3f);
    }

    [Fact]
    public void Patch_should_reject_mismatched_indices()
    {
        Vector3[] vertices = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 0f, 1f)];

        Assert.Throws<ArgumentException>(() => new ClimbSurfacePatch("bad", vertices, [0, 1], []));
        Assert.Throws<ArgumentException>(() => new ClimbSurfacePatch("bad", vertices, [0, 1, 3], []));
    }

    /// <summary>4x3 m vertical wall in the XZ plane, outward normal -Y (facing south), Z-up.</summary>
    private static TrianglePatchClimbSurface VerticalWall(IReadOnlyList<ClimbHold>? holds = null)
    {
        Vector3[] vertices = [new(0f, 0f, 0f), new(4f, 0f, 0f), new(4f, 0f, 3f), new(0f, 0f, 3f)];
        int[] indices = [0, 1, 3, 1, 2, 3];
        return new TrianglePatchClimbSurface(new ClimbSurfacePatch("wall", vertices, indices, holds ?? []));
    }

    /// <summary>The same wall split into eight triangles (simulated LOD swap of the visual geometry).</summary>
    private static TrianglePatchClimbSurface VerticalWallSubdivided(IReadOnlyList<ClimbHold> holds)
    {
        Vector3[] vertices =
        [
            new(0f, 0f, 0f), new(2f, 0f, 0f), new(4f, 0f, 0f),
            new(0f, 0f, 1.5f), new(2f, 0f, 1.5f), new(4f, 0f, 1.5f),
            new(0f, 0f, 3f), new(2f, 0f, 3f), new(4f, 0f, 3f)
        ];
        int[] indices =
        [
            0, 1, 3, 1, 4, 3, 1, 2, 4, 2, 5, 4,
            3, 4, 6, 4, 7, 6, 4, 5, 7, 5, 8, 7
        ];
        return new TrianglePatchClimbSurface(new ClimbSurfacePatch("wall", vertices, indices, holds));
    }

    /// <summary>4 m wide, 3 m tall face leaning 24 degrees over the climber (top closer in -Y).</summary>
    private static TrianglePatchClimbSurface OverhangWall()
    {
        float lean = 3f * MathF.Tan(24f * MathF.PI / 180f);
        Vector3[] vertices = [new(0f, 0f, 0f), new(4f, 0f, 0f), new(4f, -lean, 3f), new(0f, -lean, 3f)];
        int[] indices = [0, 1, 3, 1, 2, 3];
        return new TrianglePatchClimbSurface(new ClimbSurfacePatch("overhang", vertices, indices, []));
    }

    private static void AssertApprox(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.True(
            Vector3.Distance(expected, actual) <= tolerance,
            $"expected {expected}, got {actual} (tolerance {tolerance})");
    }
}