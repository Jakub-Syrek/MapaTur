using System;
using System.Collections.Generic;

namespace MapaTur.Application.Terrain;

/// <summary>Tuning for <see cref="HumanoidAnimator"/> (seconds and metres/second). A record so a caller/test can
/// override one field.</summary>
public sealed record HumanoidAnimatorOptions
{
    /// <summary>Crossfade time when the target clip changes.</summary>
    public float CrossfadeSeconds { get; init; } = 0.18f;

    /// <summary>Ground speed (m/s) above which a stopped avatar starts moving (idle → walk).</summary>
    public float WalkStartSpeed { get; init; } = 0.4f;

    /// <summary>Ground speed below which a moving avatar drops to idle (hysteresis: &lt; <see cref="WalkStartSpeed"/>).</summary>
    public float IdleStopSpeed { get; init; } = 0.15f;

    /// <summary>Ground speed above which walking becomes running.</summary>
    public float RunStartSpeed { get; init; } = 3.2f;

    /// <summary>The walk clip's authored ground speed — playback is scaled by actual/authored so feet don't skate.</summary>
    public float WalkRefSpeed { get; init; } = 1.4f;

    /// <summary>The run clip's authored ground speed.</summary>
    public float RunRefSpeed { get; init; } = 4.5f;

    /// <summary>Clamp on the locomotion playback-speed multiplier (below this, blend toward the neighbour clip instead).</summary>
    public float MinPlaybackScale { get; init; } = 0.6f;

    /// <summary>Upper clamp on the locomotion playback-speed multiplier.</summary>
    public float MaxPlaybackScale { get; init; } = 1.7f;
}

/// <summary>
/// Pure animation state machine for the 3rd-person walk avatar. Maps the walker's motion — ground speed, grounded,
/// vertical velocity, and a per-tick shoot request — to a CROSSFADE between two animation clips:
/// (<see cref="Blend.ClipA"/>@<see cref="Blend.TimeA"/>, <see cref="Blend.ClipB"/>@<see cref="Blend.TimeB"/>,
/// <see cref="Blend.Weight"/>) that the caller feeds to <see cref="SkinnedModel.PoseBlend"/>. Deterministic and
/// GL-free, so every transition is unit-testable. Locomotion (idle/walk/run) is picked by speed with hysteresis and
/// its playback is speed-matched (anti-foot-skate); jump-idle loops while airborne; land and shoot are one-shots
/// that play out before locomotion resumes. Not thread-safe: step it from one place.
/// </summary>
public sealed class HumanoidAnimator
{
    /// <summary>Global animation indices for the clips the machine drives (−1 = the model lacks that clip).</summary>
    public readonly record struct Clips(int Idle, int Walk, int Run, int JumpIdle, int JumpLand, int Shoot);

    /// <summary>A crossfade to pose: <paramref name="ClipA"/>@<paramref name="TimeA"/> blended toward
    /// <paramref name="ClipB"/>@<paramref name="TimeB"/> by <paramref name="Weight"/> (0 = A, 1 = B).</summary>
    public readonly record struct Blend(int ClipA, float TimeA, int ClipB, float TimeB, float Weight);

    private readonly Clips clips;
    private readonly IReadOnlyList<float> durations;
    private readonly HumanoidAnimatorOptions o;

    private int curClip;
    private int prevClip = -1;
    private float curTime;
    private float prevTime;
    private float blend = 1f;
    private bool curOneShot;
    private bool wasGrounded = true;

    /// <summary>Creates the machine idle. <paramref name="durations"/> is indexed by GLOBAL animation index (i.e.
    /// the model's full clip-duration list), matching the indices in <paramref name="clips"/>.</summary>
    public HumanoidAnimator(Clips clips, IReadOnlyList<float> durations, HumanoidAnimatorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(durations);
        this.clips = clips;
        this.durations = durations;
        this.o = options ?? new HumanoidAnimatorOptions();
        this.curClip = clips.Idle >= 0 ? clips.Idle : 0;
    }

    /// <summary>Advances the machine by <paramref name="dt"/> seconds and returns the crossfade to pose this frame.</summary>
    public Blend Update(float dt, float groundSpeed, bool isGrounded, float verticalVelocity, bool shootRequested)
    {
        if (dt < 0f)
        {
            dt = 0f;
        }

        bool landing = isGrounded && !this.wasGrounded;
        this.wasGrounded = isGrounded;

        // Advance the current clip (locomotion is speed-matched) and loop it if it isn't a one-shot.
        float scale = (this.curClip == this.clips.Walk || this.curClip == this.clips.Run)
            ? Math.Clamp(groundSpeed / RefSpeed(this.curClip), this.o.MinPlaybackScale, this.o.MaxPlaybackScale)
            : 1f;
        this.curTime += dt * scale;
        float curDur = Dur(this.curClip);
        if (!this.curOneShot && curDur > 1e-4f)
        {
            this.curTime %= curDur;
        }

        bool oneShotDone = this.curOneShot && this.curTime >= Dur(this.curClip);

        // Target clip — priority: shoot > let the current one-shot finish > land > airborne > locomotion.
        int target;
        bool targetOneShot;
        if (shootRequested && this.clips.Shoot >= 0)
        {
            target = this.clips.Shoot;
            targetOneShot = true;
        }
        else if (this.curOneShot && !oneShotDone)
        {
            target = this.curClip;
            targetOneShot = true;
        }
        else if (landing && this.clips.JumpLand >= 0)
        {
            target = this.clips.JumpLand;
            targetOneShot = true;
        }
        else if (!isGrounded && this.clips.JumpIdle >= 0)
        {
            target = this.clips.JumpIdle;
            targetOneShot = false;
        }
        else
        {
            target = Locomotion(groundSpeed);
            targetOneShot = false;
        }

        if (target != this.curClip)
        {
            this.prevClip = this.curClip;
            this.prevTime = this.curTime;
            this.curClip = target;
            this.curOneShot = targetOneShot;
            this.curTime = 0f;
            this.blend = 0f;
        }

        // Advance the crossfade AFTER a possible transition, so a fresh transition already fades a little this frame.
        this.blend = MathF.Min(1f, this.blend + (this.o.CrossfadeSeconds > 1e-4f ? dt / this.o.CrossfadeSeconds : 1f));

        return this.blend >= 1f || this.prevClip < 0
            ? new Blend(this.curClip, this.curTime, this.curClip, this.curTime, 1f)
            : new Blend(this.prevClip, this.prevTime, this.curClip, this.curTime, this.blend);
    }

    // Idle below WalkStart, run above RunStart, walk between — with hysteresis: an avatar already moving only drops
    // to idle below the lower IdleStop, so a speed hovering at the boundary doesn't flicker idle↔walk.
    private int Locomotion(float speed)
    {
        bool moving = this.curClip == this.clips.Walk || this.curClip == this.clips.Run;
        float dropToIdle = moving ? this.o.IdleStopSpeed : this.o.WalkStartSpeed;
        if (speed <= dropToIdle)
        {
            return this.clips.Idle >= 0 ? this.clips.Idle : this.curClip;
        }

        if (speed > this.o.RunStartSpeed && this.clips.Run >= 0)
        {
            return this.clips.Run;
        }

        return this.clips.Walk >= 0 ? this.clips.Walk : (this.clips.Idle >= 0 ? this.clips.Idle : this.curClip);
    }

    private float Dur(int clip) => clip >= 0 && clip < this.durations.Count ? this.durations[clip] : 0f;

    private float RefSpeed(int clip) => clip == this.clips.Run ? this.o.RunRefSpeed : this.o.WalkRefSpeed;
}