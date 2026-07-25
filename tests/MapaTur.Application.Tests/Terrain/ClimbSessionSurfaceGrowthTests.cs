using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Climbing;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Mid-session surface replacement: the terrain patch GROWS while climbing (holds must not "run out"
/// at the original patch edge). The replacement wall reproduces every held hold (the procedural
/// generator is deterministic in world position), the session keeps its contacts, and the climber can
/// then reach holds beyond the original wall. A wall missing a held hold is refused.
/// </summary>
public sealed class ClimbSessionSurfaceGrowthTests
{
    private const float ArmReach = 0.70f;
    private const float LegReach = 0.47f;

    /// <summary>A vertical wall at y=0 facing +Y with a regular 0.4 m grid of jugs from z=0 up to
    /// <paramref name="heightMeters"/> — the same lattice regardless of height, so a taller wall is a
    /// strict superset of a shorter one (stable ids included).</summary>
    private static TrianglePatchClimbSurface JugWall(float heightMeters, float holdShift = 0f)
    {
        Vector3[] vertices = [new(-4f, 0f, -1f), new(4f, 0f, -1f), new(4f, 0f, heightMeters), new(-4f, 0f, heightMeters)];
        int[] indices = [0, 3, 1, 1, 3, 2];
        List<ClimbHold> holds = [];
        for (float x = -3.6f; x <= 3.6f; x += 0.4f)
        {
            for (float z = 0f; z <= heightMeters - 0.4f; z += 0.4f)
            {
                holds.Add(new ClimbHold(
                    $"jug-{x + holdShift:F1}-{z:F1}", new Vector3(x + holdShift, 0f, z), new Vector3(0f, 1f, 0f), 0.9f));
            }
        }

        return new TrianglePatchClimbSurface(new ClimbSurfacePatch("jug-wall", vertices, indices, holds));
    }

    private static ClimbSessionOptions Options() => new(
        new WalkParameters(),
        ArmReachMeters: ArmReach,
        LegReachMeters: LegReach,
        LeftShoulderOffsetMeters: new Vector3(0.26f, 0f, 0.86f),
        LeftHipOffsetMeters: new Vector3(0.21f, 0f, 0.14f),
        PelvisHeightMeters: 0.5f,
        Gravity: ClimbWorld.Gravity);

    private static ClimbSession StartOn(TrianglePatchClimbSurface wall)
    {
        ClimbSession? session = ClimbSession.TryStart(wall, new Vector3(0f, 0.35f, 1.2f), Options());
        Assert.NotNull(session);
        return session;
    }

    private static float ClimbUpUntilStuck(ClimbSession session)
    {
        for (int i = 0; i < 200 && session.TryMoveToward(new Vector2(0f, 1f)); i++)
        {
        }

        return session.State.Pelvis.Z;
    }

    [Fact]
    public void TryReplaceSurface_should_accept_a_taller_wall_and_keep_every_contact()
    {
        ClimbSession session = StartOn(JugWall(6f));
        string[] heldBefore = session.State.Contacts.Values.Select(contact => contact.Hold.Id).OrderBy(id => id).ToArray();

        bool replaced = session.TryReplaceSurface(JugWall(24f));

        Assert.True(replaced);
        string[] heldAfter = session.State.Contacts.Values.Select(contact => contact.Hold.Id).OrderBy(id => id).ToArray();
        Assert.Equal(heldBefore, heldAfter);
    }

    [Fact]
    public void TryReplaceSurface_should_refuse_a_wall_missing_a_held_hold()
    {
        ClimbSession session = StartOn(JugWall(6f));
        string[] heldBefore = session.State.Contacts.Values.Select(contact => contact.Hold.Id).OrderBy(id => id).ToArray();

        bool replaced = session.TryReplaceSurface(JugWall(24f, holdShift: 0.2f));

        Assert.False(replaced);
        string[] heldAfter = session.State.Contacts.Values.Select(contact => contact.Hold.Id).OrderBy(id => id).ToArray();
        Assert.Equal(heldBefore, heldAfter);
    }

    [Fact]
    public void TryReplaceSurface_should_let_the_climber_continue_past_the_original_wall_top()
    {
        ClimbSession session = StartOn(JugWall(6f));
        float stuckAt = ClimbUpUntilStuck(session);

        Assert.True(session.TryReplaceSurface(JugWall(24f)));
        float afterGrowth = ClimbUpUntilStuck(session);

        Assert.True(
            afterGrowth > stuckAt + 1f,
            $"expected to climb on past {stuckAt:F1} m after the wall grew, got {afterGrowth:F1} m");
    }
}