using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Climbing;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Guaranteed passage along catalogued climbing routes: the seeder drops a dense, DETERMINISTIC ladder
/// of good holds along the route line (hands + feet interleaved, step ≤ 0.45 m in 3D), with ids stable
/// across rebuilds (patch growth swaps surfaces mid-session), and a climb session on a wall holding
/// ONLY those holds can actually climb the route.
/// </summary>
public sealed class ClimbingRouteHoldSeederTests
{
    // A steep planar wall: z rises 1.2 m per metre of +Y (≈ 50°) — clearly climbing terrain.
    private static float? SteepGround(Vector2 xy) => 1000f + (1.2f * xy.Y);

    private static WorldClimbingRoute StraightUpRoute() => new(
        "Test Direttissima", "V", MapaTur.Domain.Trails.PttkColor.Red,
        [new Vector2(0f, 0f), new Vector2(0f, 12f)]);

    private static List<ClimbHold> Seed(Func<Vector2, bool>? insidePatch = null)
    {
        List<ClimbHold> holds = [];
        ClimbingRouteHoldSeeder.SeedHolds(StraightUpRoute(), SteepGround, insidePatch ?? (_ => true), holds);
        return holds;
    }

    private static Vector3 Position3D(ClimbHold hold) => hold.Position;

    [Fact]
    public void SeedHolds_should_space_consecutive_holds_at_most_045_m_apart_in_3d()
    {
        List<ClimbHold> holds = Seed();

        Assert.True(holds.Count > 20, $"expected a dense ladder, got {holds.Count} holds");
        for (int i = 0; i + 1 < holds.Count; i++)
        {
            float gap = Vector3.Distance(Position3D(holds[i]), Position3D(holds[i + 1]));
            Assert.True(gap <= 0.45f + 1e-3f, $"gap {gap:F2} m between holds {i} and {i + 1}");
        }
    }

    [Fact]
    public void SeedHolds_should_emit_identical_ids_and_positions_on_every_run()
    {
        List<ClimbHold> first = Seed();
        List<ClimbHold> second = Seed();

        Assert.Equal(first.Select(h => h.Id), second.Select(h => h.Id));
        for (int i = 0; i < first.Count; i++)
        {
            Assert.True(Vector3.Distance(first[i].Position, second[i].Position) < 1e-5f);
        }
    }

    [Fact]
    public void SeedHolds_should_interleave_hand_and_foot_holds()
    {
        List<ClimbHold> holds = Seed();

        for (int i = 0; i + 3 < holds.Count; i += 4)
        {
            ClimbHold[] window = [holds[i], holds[i + 1], holds[i + 2], holds[i + 3]];
            Assert.Contains(window, h => h.Type == ClimbHoldType.FootEdge);
            Assert.Contains(window, h => h.Type != ClimbHoldType.FootEdge);
        }
    }

    [Fact]
    public void SeedHolds_should_keep_ids_stable_when_the_patch_window_moves()
    {
        List<ClimbHold> all = Seed();
        List<ClimbHold> lowerHalf = Seed(xy => xy.Y <= 6f);

        Assert.True(lowerHalf.Count > 5);
        foreach (ClimbHold hold in lowerHalf)
        {
            ClimbHold twin = Assert.Single(all, h => h.Id == hold.Id);
            Assert.True(Vector3.Distance(twin.Position, hold.Position) < 1e-5f);
        }
    }

    [Fact]
    public void ClimbSession_should_climb_a_wall_holding_only_route_seeded_holds()
    {
        // Wall geometry matching SteepGround: a single quad from y=-2 to y=14 (z = 1000 + 1.2·y).
        Vector3[] vertices =
        [
            new(-5f, -2f, 997.6f), new(5f, -2f, 997.6f),
            new(5f, 14f, 1016.8f), new(-5f, 14f, 1016.8f),
        ];
        int[] indices = [0, 1, 2, 0, 2, 3];
        List<ClimbHold> holds = Seed();
        var wall = new TrianglePatchClimbSurface(new ClimbSurfacePatch("route-wall", vertices, [.. indices], holds));

        var options = new ClimbSessionOptions(
            new WalkParameters(),
            ArmReachMeters: 0.70f,
            LegReachMeters: 0.47f,
            LeftShoulderOffsetMeters: new Vector3(0.26f, 0f, 0.86f),
            LeftHipOffsetMeters: new Vector3(0.21f, 0f, 0.14f),
            PelvisHeightMeters: 0.5f,
            Gravity: ClimbWorld.Gravity);
        ClimbSession? session = ClimbSession.TryStart(wall, new Vector3(0f, 1.2f, 1002.5f), options);
        Assert.NotNull(session);

        float startZ = session.State.Pelvis.Z;
        for (int i = 0; i < 300 && session.TryMoveToward(new Vector2(0f, 1f)); i++)
        {
        }

        float gained = session.State.Pelvis.Z - startZ;
        Assert.True(gained >= 5f, $"expected >= 5 m of guaranteed progress along the route, got {gained:F1} m");
    }
}