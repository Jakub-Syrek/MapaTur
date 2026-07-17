using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Climbing;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Etap 4 gate: grip climbing owns the body while active (WalkPhysics never fights it), auto-belay
/// pitons/rope protect the session, and the exit hands position back explicitly. Climb space is
/// X-east/Y-north/Z-up real metres; the test wall stands at y=0 facing +Y (climber approaches from north).
/// </summary>
public sealed class ClimbSessionTests
{
    private const float ArmReach = 0.70f;
    private const float LegReach = 0.47f;
    private static readonly Vector3 ShoulderOffset = new(0.26f, 0f, 0.86f);
    private static readonly Vector3 HipOffset = new(0.21f, 0f, 0.14f);

    /// <summary>A 24 m tall wall covered with a regular grid of jugs 0.4 m apart — tall enough that a full
    /// ascent exceeds the 10 m piton spacing more than once.</summary>
    private static TrianglePatchClimbSurface JugWall()
    {
        Vector3[] vertices = [new(-4f, 0f, -1f), new(4f, 0f, -1f), new(4f, 0f, 24f), new(-4f, 0f, 24f)];
        int[] indices = [0, 3, 1, 1, 3, 2];
        List<ClimbHold> holds = [];
        for (float x = -3.6f; x <= 3.6f; x += 0.4f)
        {
            for (float z = 0f; z <= 23.6f; z += 0.4f)
            {
                holds.Add(new ClimbHold($"jug-{x:F1}-{z:F1}", new Vector3(x, 0f, z), new Vector3(0f, 1f, 0f), 0.9f));
            }
        }

        return new TrianglePatchClimbSurface(new ClimbSurfacePatch("jug-wall", vertices, indices, holds));
    }

    private static ClimbSessionOptions Options() => new(
        new WalkParameters(),
        ArmReachMeters: ArmReach,
        LegReachMeters: LegReach,
        LeftShoulderOffsetMeters: ShoulderOffset,
        LeftHipOffsetMeters: HipOffset,
        PelvisHeightMeters: 0.5f,
        Gravity: ClimbWorld.Gravity);

    private static ClimbSession StartSession()
    {
        ClimbSession? session = ClimbSession.TryStart(JugWall(), new Vector3(0f, 0.35f, 1.2f), Options());
        Assert.NotNull(session);
        return session;
    }

    [Fact]
    public void TryStart_should_grab_four_distinct_holds()
    {
        ClimbSession session = StartSession();

        Assert.Equal(4, session.State.Contacts.Count);
        Assert.Equal(4, session.State.Contacts.Values.Select(contact => contact.Hold.Id).Distinct().Count());
        Assert.True(
            session.State.Contacts[ClimbLimb.LeftHand].Hold.Position.Z
            > session.State.Contacts[ClimbLimb.LeftFoot].Hold.Position.Z,
            "hands should start above feet");
    }

    [Fact]
    public void TryStart_should_return_null_on_a_bare_wall()
    {
        Vector3[] vertices = [new(-4f, 0f, -1f), new(4f, 0f, -1f), new(4f, 0f, 12f), new(-4f, 0f, 12f)];
        var bare = new TrianglePatchClimbSurface(new ClimbSurfacePatch("bare", vertices, [0, 3, 1, 1, 3, 2], []));

        Assert.Null(ClimbSession.TryStart(bare, new Vector3(0f, 0.35f, 1.2f), Options()));
    }

    [Fact]
    public void TryStart_should_plant_an_anchor_piton()
    {
        ClimbSession session = StartSession();

        WalkPhysics.PitonPoint anchor = Assert.Single(session.Pitons);
        Assert.True(MathF.Abs(anchor.Elevation - session.FeetElevation) < 0.5f);
    }

    [Fact]
    public void Climbing_up_should_gain_height_and_swap_contacts()
    {
        ClimbSession session = StartSession();
        float startPelvis = session.State.Pelvis.Z;
        string[] startHolds = [.. session.State.Contacts.Values.Select(contact => contact.Hold.Id)];

        int applied = 0;
        for (int i = 0; i < 12; i++)
        {
            if (session.TryMoveToward(new Vector2(0f, 1f)))
            {
                applied++;
            }
        }

        Assert.True(applied >= 8, $"only {applied}/12 upward moves applied ({session.LastBlockReason})");
        Assert.True(
            session.State.Pelvis.Z > startPelvis + 0.5f,
            $"pelvis rose only {session.State.Pelvis.Z - startPelvis:F2} m");
        Assert.NotEqual(
            startHolds.ToHashSet(),
            session.State.Contacts.Values.Select(contact => contact.Hold.Id).ToHashSet());
    }

    [Fact]
    public void Long_climb_should_plant_spaced_pitons_capped_at_three()
    {
        ClimbSession session = StartSession();

        for (int i = 0; i < 240 && !session.IsFinished; i++)
        {
            session.TryMoveToward(new Vector2(0f, 1f));
        }

        Assert.True(session.Pitons.Count > 1, "a long climb must add protection above the anchor");
        Assert.True(session.Pitons.Count <= new WalkParameters().MaxPitons, "piton count must stay capped");
        Assert.True(
            session.Pitons[^1].Elevation > session.Pitons[0].Elevation,
            "newest piton should sit higher than the oldest");
    }

    [Fact]
    public void Grip_running_out_should_catch_on_the_rope()
    {
        ClimbSession session = StartSession();
        for (int i = 0; i < 20; i++)
        {
            session.TryMoveToward(new Vector2(0f, 1f)); // get above the anchor so the rope has length to catch
        }

        float topPiton = session.Pitons.Max(piton => piton.Elevation);
        session.Update(10_000f); // drain grip completely

        Assert.True(session.IsFinished);
        Assert.Equal(ClimbSessionExit.RopeCatch, session.Exit);
        Assert.True(
            session.FeetElevation <= topPiton - new WalkParameters().RopeLengthMeters + 0.25f,
            $"feet {session.FeetElevation:F2} should hang a rope-length under the top piton {topPiton:F2}");
    }

    [Fact]
    public void Blocked_move_should_expose_a_reason_and_keep_state()
    {
        ClimbSession session = StartSession();
        ClimbState before = session.State;

        bool moved = session.TryMoveToward(new Vector2(0f, -1f)); // straight down from the start band
        if (!moved)
        {
            Assert.False(string.IsNullOrWhiteSpace(session.LastBlockReason));
            Assert.Same(before, session.State);
        }
    }

    [Fact]
    public void SyncFromClimb_should_hand_position_and_protection_back_to_walk_physics()
    {
        var walker = new WalkPhysics(Vector2.Zero, _ => 0f);
        WalkPhysics.PitonPoint[] pitons =
        [
            new(new Vector2(1f, 2f), 5f),
            new(new Vector2(1f, 2.5f), 11f)
        ];

        // Feet exactly at the rope hang point (top piton 11 m − rope 6 m): the very next step is arrested.
        walker.SyncFromClimb(new Vector2(3f, 4f), 5.0f, pitons, gripStamina: 2.5f, roped: true);

        Assert.Equal(new Vector2(3f, 4f), walker.PositionXY);
        Assert.Equal(5.0f, walker.FeetElevation, 3);
        Assert.True(walker.IsRoped);
        Assert.True(walker.IsHanging);
        Assert.False(walker.IsGrounded);
        Assert.Equal(2, walker.Pitons.Count);
        Assert.Equal(11f, walker.Pitons[^1].Elevation, 3);

        // The next physics step must keep hanging on the rope and regenerate grip, not fall to the base.
        walker.Step(0.1f, Vector2.Zero, 2f, jumpRequested: false);
        Assert.True(walker.IsRoped);
        Assert.Equal(5.0f, walker.FeetElevation, 3);
        Assert.True(walker.GripStamina > 2.5f);
    }
}
