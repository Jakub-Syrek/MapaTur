using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="HumanoidAnimator"/> — the walk-avatar animation state machine. Clip indices are a small
/// synthetic catalogue (idle 0 / walk 1 / run 2 / jumpIdle 3 / jumpLand 4 / shoot 5) with the durations below.
/// </summary>
public sealed class HumanoidAnimatorTests
{
    private static readonly HumanoidAnimator.Clips C = new(Idle: 0, Walk: 1, Run: 2, JumpIdle: 3, JumpLand: 4, Shoot: 5);
    private static readonly float[] D = { 1f, 1f, 1f, 1f, 0.3f, 0.5f };

    private static HumanoidAnimator New() => new(C, D);

    [Fact]
    public void Starts_Idle_AtFullWeight()
    {
        var blend = New().Update(0.1f, 0f, isGrounded: true, verticalVelocity: 0f, shootRequested: false);

        blend.ClipB.Should().Be(0);
        blend.Weight.Should().Be(1f);
    }

    [Fact]
    public void Accelerating_WalksThenRuns()
    {
        var a = New();

        HumanoidAnimator.Blend b = default;
        for (int i = 0; i < 10; i++)
        {
            b = a.Update(0.1f, 1.5f, true, 0f, false);
        }

        b.ClipB.Should().Be(1, "1.5 m/s is a walk");

        for (int i = 0; i < 10; i++)
        {
            b = a.Update(0.1f, 5f, true, 0f, false);
        }

        b.ClipB.Should().Be(2, "5 m/s is a run");
    }

    [Fact]
    public void Airborne_UsesJumpIdle()
    {
        var a = New();
        a.Update(0.1f, 0f, true, 0f, false);

        var b = a.Update(0.1f, 1f, isGrounded: false, verticalVelocity: 4f, shootRequested: false);

        b.ClipB.Should().Be(3);
    }

    [Fact]
    public void Landing_PlaysLandOneShot_ThenReturnsToIdle()
    {
        var a = New();
        a.Update(0.1f, 0f, isGrounded: false, verticalVelocity: -3f, shootRequested: false); // falling

        var land = a.Update(0.1f, 0f, isGrounded: true, verticalVelocity: 0f, shootRequested: false); // touchdown
        land.ClipB.Should().Be(4, "the land one-shot fires on touchdown");

        HumanoidAnimator.Blend b = default;
        for (int i = 0; i < 10; i++)
        {
            b = a.Update(0.1f, 0f, true, 0f, false); // play past the 0.3 s land clip
        }

        b.ClipB.Should().Be(0, "after the land clip finishes it returns to idle");
    }

    [Fact]
    public void Shoot_OverridesLocomotion()
    {
        var a = New();
        for (int i = 0; i < 5; i++)
        {
            a.Update(0.1f, 1.5f, true, 0f, false); // walking
        }

        var s = a.Update(0.1f, 1.5f, true, 0f, shootRequested: true);

        s.ClipB.Should().Be(5);
    }

    [Fact]
    public void ChangingState_Crossfades_FromThePreviousClip()
    {
        var a = New();
        a.Update(0.1f, 0f, true, 0f, false); // idle

        var t = a.Update(0.05f, 1.5f, true, 0f, false); // → walk, crossfade just started

        t.ClipA.Should().Be(0, "fading from idle");
        t.ClipB.Should().Be(1, "toward walk");
        t.Weight.Should().BeInRange(0.05f, 0.95f, "the crossfade is partway, not snapped");
    }

    [Fact]
    public void Locomotion_Playback_IsFasterAtHigherSpeed()
    {
        var slow = New();
        for (int i = 0; i < 6; i++)
        {
            slow.Update(0.05f, 1.4f, true, 0f, false); // settle in walk
        }

        float s0 = slow.Update(0f, 1.4f, true, 0f, false).TimeB;
        float slowAdvance = slow.Update(0.1f, 1.4f, true, 0f, false).TimeB - s0; // ≈ 0.10 (scale 1.0)

        var fast = New();
        for (int i = 0; i < 6; i++)
        {
            fast.Update(0.05f, 1.4f, true, 0f, false);
        }

        float f0 = fast.Update(0f, 2.8f, true, 0f, false).TimeB; // 2.8 m/s is still a walk (< run threshold)
        float fastAdvance = fast.Update(0.1f, 2.8f, true, 0f, false).TimeB - f0; // ≈ 0.17 (scale clamped to 1.7)

        fastAdvance.Should().BeGreaterThan(slowAdvance, "playback is scaled by ground speed so the feet don't skate");
    }
}
