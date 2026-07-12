using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="WalkPhysics"/>: the pure first-person walker (gravity + jump + ground-clamp +
/// slope gate) driving the terrain camera's walk mode. Everything is in REAL metres — world XY are east/north
/// metres, elevation is real metres (NOT vertically-exaggerated) — and the ground is an injected sampler, so the
/// physics unit-tests without GL, a DEM, or a camera. Slope rules: you cannot WALK up terrain steeper than the
/// walk limit (along the move direction), and standing on terrain steeper than the stand limit you SLIDE downhill
/// (gravity on slopes); jumping is ballistic and ignores the walk gate, so a jump can clear a step too steep to walk.
/// </summary>
public sealed class WalkPhysicsTests
{
    // Convenience: a sampler that is a function of world XY. Returns real elevation metres (never null here).
    private static Func<Vector2, float?> Ground(Func<Vector2, float> f) => p => f(p);

    private static readonly WalkParameters Params = new(); // defaults

    [Fact]
    public void StartsGroundedOnTheTerrainUnderTheSpawnPoint()
    {
        var walk = new WalkPhysics(new Vector2(0f, 0f), Ground(_ => 1500f), Params);

        walk.IsGrounded.Should().BeTrue();
        walk.FeetElevation.Should().Be(1500f);
        walk.EyeElevation.Should().Be(1500f + Params.EyeHeightMeters);
        walk.VerticalVelocity.Should().Be(0f);
    }

    [Fact]
    public void OnFlatGround_WalkingMovesHorizontally_AndStaysGlued()
    {
        var walk = new WalkPhysics(Vector2.Zero, Ground(_ => 100f), Params);

        // Walk east (+X) for 1 s at 2 m/s.
        for (int i = 0; i < 20; i++)
        {
            walk.Step(0.05f, new Vector2(1f, 0f), moveSpeed: 2f, jumpRequested: false);
        }

        walk.PositionXY.X.Should().BeApproximately(2f, 0.05f);
        walk.PositionXY.Y.Should().BeApproximately(0f, 1e-4f);
        walk.FeetElevation.Should().Be(100f);
        walk.IsGrounded.Should().BeTrue();
        walk.IsSliding.Should().BeFalse();
    }

    [Fact]
    public void WalkingUpAGentleSlope_IsAllowed_AndFeetTrackTheGround()
    {
        // A 0.3 grade ramp along +X (≈17°), well under the walk limit.
        var walk = new WalkPhysics(Vector2.Zero, Ground(p => 100f + (0.3f * MathF.Max(0f, p.X))), Params);

        for (int i = 0; i < 40; i++)
        {
            walk.Step(0.05f, new Vector2(1f, 0f), moveSpeed: 2f, jumpRequested: false);
        }

        walk.PositionXY.X.Should().BeGreaterThan(1.5f, "a gentle uphill is walkable");
        walk.FeetElevation.Should().BeApproximately(100f + (0.3f * walk.PositionXY.X), 0.01f);
        walk.IsSliding.Should().BeFalse();
    }

    [Fact]
    public void WalkingStraightUpTooSteepGround_IsBlocked()
    {
        // A grade-0.9 face (≈42°) rising along +X for X>0: over the ~35° walk limit but under the ~45° stand
        // limit (so it does not slide — it purely tests the walk-up block). Flat behind.
        var walk = new WalkPhysics(Vector2.Zero, Ground(p => 100f + (0.9f * MathF.Max(0f, p.X))), Params);

        for (int i = 0; i < 20; i++)
        {
            walk.Step(0.05f, new Vector2(1f, 0f), moveSpeed: 2f, jumpRequested: false);
        }

        walk.PositionXY.X.Should().BeApproximately(0f, 0.2f, "you cannot walk straight up ground too steep to climb");
        walk.IsSliding.Should().BeFalse("42° is steep but standable — no slide, just no upward progress");
    }

    [Fact]
    public void OnASteepFace_TraversingAlongTheContour_IsAllowed()
    {
        // A grade-0.9 face (≈42°) along +X, flat along Y — a contour. Moving along +Y is a level traverse
        // (the "droga pod kątem": a path crosses the face at a walkable angle), so it must be allowed even
        // though walking straight up (+X) is blocked. 42° stays under the stand limit, so it does not slide.
        var walk = new WalkPhysics(new Vector2(5f, 0f), Ground(p => 100f + (0.9f * MathF.Max(0f, p.X))), Params);

        for (int i = 0; i < 20; i++)
        {
            walk.Step(0.05f, new Vector2(0f, 1f), moveSpeed: 2f, jumpRequested: false);
        }

        walk.PositionXY.Y.Should().BeGreaterThan(1.5f, "a level traverse across a steep face is walkable");
        walk.PositionXY.X.Should().BeApproximately(5f, 0.2f);
    }

    [Fact]
    public void WalkingDownhill_IsAlwaysAllowed_EvenWhenSteep()
    {
        // Steep grade-2 drop toward -X (elevation rises with X); walking toward -X descends and must be allowed.
        var walk = new WalkPhysics(Vector2.Zero, Ground(p => 100f + (2.0f * p.X)), new WalkParameters { MaxStandSlopeGrade = 5f });

        for (int i = 0; i < 20; i++)
        {
            walk.Step(0.05f, new Vector2(-1f, 0f), moveSpeed: 2f, jumpRequested: false);
        }

        walk.PositionXY.X.Should().BeLessThan(-1.5f, "descending a steep slope is always allowed");
        walk.FeetElevation.Should().BeLessThan(100f);
    }

    [Fact]
    public void Jumping_RisesToAboutTheJumpHeight_ThenLandsBackGrounded()
    {
        var walk = new WalkPhysics(Vector2.Zero, Ground(_ => 0f), Params);

        walk.Step(0.02f, Vector2.Zero, moveSpeed: 0f, jumpRequested: true);
        walk.IsGrounded.Should().BeFalse("the jump left the ground");

        float apex = walk.FeetElevation;
        bool landed = false;
        for (int i = 0; i < 400; i++)
        {
            walk.Step(0.02f, Vector2.Zero, moveSpeed: 0f, jumpRequested: false);
            apex = MathF.Max(apex, walk.FeetElevation);
            if (walk.IsGrounded)
            {
                landed = true;
                break;
            }
        }

        apex.Should().BeApproximately(Params.JumpHeightMeters, 0.2f, "the apex is about the configured jump height");
        landed.Should().BeTrue("what goes up comes back down");
        walk.FeetElevation.Should().BeApproximately(0f, 0.05f, "it lands back on the ground");
        walk.VerticalVelocity.Should().Be(0f);
    }

    [Fact]
    public void CanDoubleJump_ButNotTriple()
    {
        var walk = new WalkPhysics(Vector2.Zero, Ground(_ => 0f), Params); // MaxJumps default 2

        walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: true); // jump 1 (ground)
        walk.IsGrounded.Should().BeFalse();
        for (int i = 0; i < 6; i++)
        {
            walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: false); // rise/decay a bit
        }

        float vBeforeSecond = walk.VerticalVelocity;
        walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: true); // jump 2 (air double jump)
        walk.VerticalVelocity.Should().BeGreaterThan(vBeforeSecond, "the second (double) jump re-boosts upward");

        float vAfterSecond = walk.VerticalVelocity;
        walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: true); // jump 3 attempt — must be ignored
        walk.VerticalVelocity.Should().BeLessThan(vAfterSecond, "no triple jump — only gravity after the second");
    }

    [Fact]
    public void JumpingIntoAVerticalWall_NeverPutsTheFeetBelowTheGround()
    {
        // "wpadam pod teksturę skacząc przy pionowych ścianach" (2026-07-11): a wall at x≥5 rises 100 m over
        // one metre. Jump from the flat at x=4 and hold FORWARD (+X) into the wall. The airborne step must not
        // carry the walker into the wall face — the feet must stay AT OR ABOVE the ground under them every
        // frame (never embedded, so the eye is never under the texture).
        static float WallGround(Vector2 p) => p.X < 5f ? 100f : 100f + (100f * (p.X - 5f));
        var walk = new WalkPhysics(new Vector2(4f, 0f), Ground(WallGround), Params);
        var into = new Vector2(1f, 0f); // straight at the wall

        walk.Step(0.02f, into, 6f, jumpRequested: true);
        for (int i = 0; i < 120; i++)
        {
            walk.Step(0.02f, into, 6f, jumpRequested: false);
            float groundUnder = WallGround(walk.PositionXY);
            walk.FeetElevation.Should().BeGreaterThanOrEqualTo(
                groundUnder - 0.01f, $"frame {i}: feet must never sink below the ground (embedded in the wall)");
        }
    }

    [Fact]
    public void DoubleJumpingIntoAWall_StaysAboveTheGround_OnBothJumps()
    {
        // Double jump must work AND never embed the feet: jump, jump again mid-air, both while shoving into a
        // 100 m wall at x≥5. The feet stay at/above the ground under them the whole flight.
        static float WallGround(Vector2 p) => p.X < 5f ? 100f : 100f + (100f * (p.X - 5f));
        var walk = new WalkPhysics(new Vector2(4f, 0f), Ground(WallGround), Params);
        var into = new Vector2(1f, 0f);

        walk.Step(0.02f, into, 6f, jumpRequested: true); // first jump
        for (int i = 0; i < 8; i++)
        {
            walk.Step(0.02f, into, 6f, jumpRequested: false);
        }

        walk.Step(0.02f, into, 6f, jumpRequested: true); // SECOND (double) jump, mid-air
        for (int i = 0; i < 120; i++)
        {
            walk.Step(0.02f, into, 6f, jumpRequested: false);
            walk.FeetElevation.Should().BeGreaterThanOrEqualTo(
                WallGround(walk.PositionXY) - 0.01f, $"frame {i}: feet never sink into the wall, double jump or not");
        }
    }

    [Fact]
    public void JumpingOntoALedgeWithinReach_StillLandsOnTop()
    {
        // The jump must still clear a step too steep to walk: a 1 m ledge at x≥3. Jump forward and land ON it,
        // not get blocked at its foot — the wall guard only refuses ground ABOVE the feet, not a reachable lip.
        static float LedgeGround(Vector2 p) => p.X < 3f ? 100f : 101f;
        var walk = new WalkPhysics(new Vector2(2f, 0f), Ground(LedgeGround), Params);
        var fwd = new Vector2(1f, 0f);

        walk.Step(0.02f, fwd, 3f, jumpRequested: true);
        bool onLedge = false;
        for (int i = 0; i < 200 && !onLedge; i++)
        {
            walk.Step(0.02f, fwd, 3f, jumpRequested: false);
            onLedge = walk.IsGrounded && walk.PositionXY.X >= 3f && walk.FeetElevation >= 100.9f;
        }

        onLedge.Should().BeTrue("a jump within reach still carries the walker onto the higher ledge");
    }

    [Fact]
    public void HoldingBothCiupagas_WithForwardInput_ClimbsUpTheSteepWall()
    {
        // The two-ciupaga climb (2026-07-11): a ≈63° face along +X (grade 2.0). Hold the axes (hangHeld) and
        // push FORWARD (+X, uphill) — the walker must ASCEND the face, gaining real elevation, overriding the
        // walk-slope gate that would refuse to walk up it.
        var walk = new WalkPhysics(new Vector2(1f, 0f), Ground(p => 2.0f * MathF.Max(0f, p.X)), Params);
        float startElev = walk.FeetElevation;
        var up = new Vector2(1f, 0f);

        for (int i = 0; i < 60; i++)
        {
            walk.Step(0.02f, up, 2.5f, jumpRequested: false, hangHeld: true);
        }

        walk.IsClimbing.Should().BeTrue("holding the axes and pushing into the face is a climb");
        walk.FeetElevation.Should().BeGreaterThan(startElev + 1.5f, "the climb hauls the walker UP the wall");
        walk.IsGrounded.Should().BeFalse("on the face, hanging on the axes, not standing");
    }

    [Fact]
    public void ClimbingSideways_IsSlowerThanClimbingUp()
    {
        // Isonzo feel: traversing the face is slower than ascending it. On a wall that rises along +X (fall
        // line = +X), pushing +X (straight up) covers more ground per second than pushing +Y (pure contour).
        static float Wall(Vector2 p) => 2.0f * MathF.Max(0f, p.X);
        var up = new WalkPhysics(new Vector2(1f, 0f), Ground(Wall), Params);
        var side = new WalkPhysics(new Vector2(1f, 0f), Ground(Wall), Params);

        for (int i = 0; i < 25; i++)
        {
            up.Step(0.02f, new Vector2(1f, 0f), 2.5f, jumpRequested: false, hangHeld: true);   // up the fall line
            side.Step(0.02f, new Vector2(0f, 1f), 2.5f, jumpRequested: false, hangHeld: true); // along the contour
        }

        float upDist = up.PositionXY.X - 1f;               // advanced up the fall line
        float sideDist = MathF.Abs(side.PositionXY.Y);     // advanced along the contour
        sideDist.Should().BeLessThan(upDist * 0.7f, "sideways traversal is throttled below the ascent rate");
        sideDist.Should().BeGreaterThan(0.05f, "but you still shuffle sideways");
    }

    [Fact]
    public void HoldingBothCiupagas_WithNoInput_HangsInsteadOfClimbing()
    {
        // Holding the axes on a steep face with NO move wish is the self-arrest hang — no drift, no climb.
        var walk = new WalkPhysics(new Vector2(5f, 0f), Ground(p => 1.5f * p.X), Params);
        walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: true); // leave the ground onto the face
        for (int i = 0; i < 5; i++)
        {
            walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: false, hangHeld: true);
        }

        float held = walk.FeetElevation;
        for (int i = 0; i < 30; i++)
        {
            walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: false, hangHeld: true);
        }

        walk.IsHanging.Should().BeTrue("axes planted, no input → hang");
        walk.IsClimbing.Should().BeFalse("no move wish → not climbing");
        walk.FeetElevation.Should().BeApproximately(held, 0.02f, "a hang holds position");
    }

    [Fact]
    public void HoldingCiupagaAgainstSteepRock_ArrestsTheFall()
    {
        // A grade-1.5 face (≈56°) along +X: steep enough to plant into. Jump off it, then hold the ciupaga (hang)
        // — the fall must freeze and hold position until released.
        var walk = new WalkPhysics(new Vector2(5f, 0f), Ground(p => 1.5f * p.X), Params);
        walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: true); // leave the ground
        for (int i = 0; i < 3; i++)
        {
            walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: false); // rise a touch, not hanging yet
        }

        float feetAtGrab = walk.FeetElevation;
        for (int i = 0; i < 40; i++)
        {
            walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: false, hangHeld: true);
        }

        walk.IsHanging.Should().BeTrue("the ciupaga bites the steep rock within reach");
        walk.VerticalVelocity.Should().Be(0f, "a hang freezes vertical motion");
        walk.FeetElevation.Should().BeApproximately(feetAtGrab, 0.02f, "hanging holds position — no falling");
    }

    [Fact]
    public void HoldingCiupagaOverFlatGround_DoesNotArrest()
    {
        // Flat ground has no wall to plant into, so holding the axe mid-jump must NOT catch — it lands normally.
        var walk = new WalkPhysics(Vector2.Zero, Ground(_ => 0f), Params);
        walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: true);

        bool landed = false;
        for (int i = 0; i < 400; i++)
        {
            walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: false, hangHeld: true);
            if (walk.IsGrounded)
            {
                landed = true;
                break;
            }
        }

        walk.IsHanging.Should().BeFalse("flat ground is not a steep face to plant the ciupaga into");
        landed.Should().BeTrue("without a wall, holding the axe does not stop the fall");
    }

    [Fact]
    public void ReleasingTheCiupaga_ResumesFalling()
    {
        var walk = new WalkPhysics(new Vector2(5f, 0f), Ground(p => 1.5f * p.X), Params);
        walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: true);
        for (int i = 0; i < 20; i++)
        {
            walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: false, hangHeld: true); // hang
        }

        walk.IsHanging.Should().BeTrue();
        float feetWhileHanging = walk.FeetElevation;

        for (int i = 0; i < 10; i++)
        {
            walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: false, hangHeld: false); // let go
        }

        walk.IsHanging.Should().BeFalse("released the axe");
        walk.FeetElevation.Should().BeLessThan(feetWhileHanging, "gravity resumes once the ciupaga lets go");
    }

    [Fact]
    public void Jumping_CanClearAStepTooSteepToWalkOnto()
    {
        // A 1 m vertical step at X=5 (flat 0 before, flat 1 after). The step is a wall you cannot walk up, but a
        // jump (apex ~1.2 m) while moving east should land you on the ledge — "weź pod uwagę skakanie też".
        static float StepGround(Vector2 p) => p.X < 5f ? 0f : 1f;
        var walk = new WalkPhysics(new Vector2(4f, 0f), Ground(StepGround), Params);

        // Confirm you cannot simply walk up it.
        var walker2 = new WalkPhysics(new Vector2(4.9f, 0f), Ground(StepGround), Params);
        for (int i = 0; i < 20; i++)
        {
            walker2.Step(0.05f, new Vector2(1f, 0f), 2f, false);
        }

        walker2.PositionXY.X.Should().BeLessThan(5f, "the vertical step is not walkable");

        // Now jump toward it while moving east.
        walk.Step(0.02f, new Vector2(1f, 0f), moveSpeed: 3f, jumpRequested: true);
        bool onLedge = false;
        for (int i = 0; i < 400; i++)
        {
            walk.Step(0.02f, new Vector2(1f, 0f), moveSpeed: 3f, jumpRequested: false);
            if (walk.IsGrounded && walk.PositionXY.X > 5f)
            {
                onLedge = true;
                break;
            }
        }

        onLedge.Should().BeTrue("a jump can carry the walker onto a ledge too steep to walk up");
        walk.FeetElevation.Should().BeApproximately(1f, 0.05f, "it settles on the upper ledge");
    }

    [Fact]
    public void StandingOnATooSteepSlope_SlidesDownhill()
    {
        // Grade-2 fall line along -X (elevation increases with X ⇒ downhill is +X... wait: elevation = -2X means
        // increasing X lowers elevation). Use elevation = -2*X so downhill is +X. Slope magnitude 2 > stand limit.
        var walk = new WalkPhysics(Vector2.Zero, Ground(p => -2.0f * p.X), Params);
        float startX = walk.PositionXY.X;

        for (int i = 0; i < 20; i++)
        {
            walk.Step(0.05f, Vector2.Zero, moveSpeed: 0f, jumpRequested: false); // no input — gravity only
        }

        walk.IsSliding.Should().BeTrue("a slope steeper than the stand limit cannot be stood on");
        walk.PositionXY.X.Should().BeGreaterThan(startX + 0.5f, "the walker slides down the fall line");
        walk.FeetElevation.Should().BeLessThan(0f, "sliding loses elevation");
    }

    [Fact]
    public void NoDataUnderTheWalker_FreezesInsteadOfCrashing()
    {
        // A sampler that returns null (off-coverage) must not move or throw — the walker simply holds.
        var walk = new WalkPhysics(new Vector2(3f, 4f), _ => null, Params);
        Vector2 before = walk.PositionXY;

        walk.Step(0.05f, new Vector2(1f, 0f), moveSpeed: 2f, jumpRequested: true);

        walk.PositionXY.Should().Be(before, "with no ground data the walker holds position");
    }

    [Fact]
    public void Teleport_RepositionsAndRegroundsTheWalker()
    {
        // Entering walk mode drops the walker at the camera's current ground position — Teleport must reset state.
        var walk = new WalkPhysics(Vector2.Zero, Ground(_ => 500f), Params);
        walk.Step(0.02f, Vector2.Zero, 0f, jumpRequested: true); // become airborne

        walk.Teleport(new Vector2(1000f, -2000f));

        walk.PositionXY.Should().Be(new Vector2(1000f, -2000f));
        walk.IsGrounded.Should().BeTrue();
        walk.FeetElevation.Should().Be(500f);
        walk.VerticalVelocity.Should().Be(0f);
    }
}