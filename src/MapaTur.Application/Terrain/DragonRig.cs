using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Procedural wing-beat + tail sway for the ridden dragon's rig. The bundled model has a full skeleton
/// (Shoulder/Arm/Forearm.L/R, Tail.001..007, …) but NO baked animation clip, so we drive the bones ourselves:
/// each frame reset to bind, rotate the wing bones on a sine flap cycle (mirrored L/R) and ripple the tail, then
/// re-skin. The rotation AXES + amplitudes below are tuned by eye against the specific model (glTF bone frames
/// vary), so they live here as named constants to adjust quickly.
/// </summary>
public sealed class DragonRig
{
    private readonly SkinnedModel model;

    // Bone names in the bundled dragon rig.
    private const string ShoulderL = "Shoulder.L";
    private const string ShoulderR = "Shoulder.R";
    private const string ArmL = "Arm.L";
    private const string ArmR = "Arm.R";
    private const string ForearmL = "Forearm.L";
    private const string ForearmR = "Forearm.R";
    private static readonly string[] TailBones =
        { "Tail.001", "Tail.002", "Tail.003", "Tail.004", "Tail.005", "Tail.006", "Tail.007" };

    // Flap tuning (degrees). The wing pivots at the shoulder; arm/forearm add a softer secondary bend so the
    // wingtip trails the shoulder. Axis is the bone-local axis that raises/lowers the wing (tuned visually).
    private static readonly Vector3 FlapAxis = Vector3.UnitZ;
    private const float ShoulderFlapDeg = 34f;
    private const float ArmFlapDeg = 20f;
    private const float ForearmFlapDeg = 12f;
    private const float WingtipLagRadians = 0.6f;   // arm/forearm trail the shoulder in phase
    // Wing FOLD (tuck) blended in on a dive: wings sweep down/in and the flap fades.
    private const float ShoulderFoldDeg = -42f;
    private const float ArmFoldDeg = -34f;
    private const float ForearmFoldDeg = -58f;
    private static readonly Vector3 TailAxis = Vector3.UnitY;
    private const float TailSwayDeg = 9f;
    private const float TailWaveRadians = 0.55f;     // phase step per tail segment (a travelling ripple)

    // Body follow-through: the torso + legs lag the wing-beat a little and move with it (secondary motion).
    private static readonly Vector3 BodyAxis = Vector3.UnitX; // pitch the chest with the beat
    private const float BodyBobDeg = 4f;
    private const float BodyLagRadians = 0.9f;
    private const float LegSwayDeg = 6f;
    // In flight the legs tuck BACK (streamlined) instead of dangling down. Static tuck + a little sway on top.
    private static readonly Vector3 LegTuckAxis = Vector3.UnitX;
    private const float ThighTuckDeg = 72f;
    private const float ShinTuckDeg = 40f;
    // The head looks slowly side to side (idle scan), leans INTO turns and follows the climb/dive so it isn't stiff.
    private static readonly Vector3 HeadYawAxis = Vector3.UnitY;
    private static readonly Vector3 HeadPitchAxis = Vector3.UnitX;
    private const float HeadYawDeg = 12f;      // idle side-to-side amplitude
    private const float HeadYawSpeed = 0.45f;  // fraction of the tail phase — a slow scan
    private const float HeadTurnFollowDeg = 26f; // × turn (roll radians): head leans into the turn
    private const float HeadDiveFollowDeg = 18f; // × dive (pitch radians): head follows climb/dive

    public DragonRig(SkinnedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.model = model;
    }

    /// <summary>
    /// Poses the rig for the given flap phase (radians) and re-skins. <paramref name="flapPhase"/> advances with
    /// time (faster when flying faster); <paramref name="tailPhase"/> drives the tail ripple; <paramref name="turn"/>
    /// (the flight roll, radians) leans the head into turns and <paramref name="dive"/> (the flight pitch, radians)
    /// makes the head follow the climb/dive.
    /// </summary>
    public void Pose(float flapPhase, float tailPhase, float turn, float dive, float fold = 0f)
    {
        this.model.ResetPose();

        float flap = MathF.Sin(flapPhase);                        // −1 (down-stroke) … +1 (up-stroke)
        float armFlap = MathF.Sin(flapPhase - WingtipLagRadians); // wingtip trails the shoulder
        float body = MathF.Sin(flapPhase - BodyLagRadians);       // torso/legs lag the beat

        // Wings beat TOGETHER: the L and R bones sit in mirrored local frames, so the right one takes the NEGATED
        // angle. On a dive (fold → 1) the flap amplitude fades and a static fold sweeps the wings in/down.
        fold = Math.Clamp(fold, 0f, 1f);
        float flapScale = 1f - fold;
        RotateMirrored(ShoulderL, ShoulderR, (flap * ShoulderFlapDeg * flapScale) + (fold * ShoulderFoldDeg), FlapAxis);
        RotateMirrored(ArmL, ArmR, (armFlap * ArmFlapDeg * flapScale) + (fold * ArmFoldDeg), FlapAxis);
        RotateMirrored(ForearmL, ForearmR, (armFlap * ForearmFlapDeg * flapScale) + (fold * ForearmFoldDeg), FlapAxis);

        // Body follow-through: the chest + upper spine pitch with the beat, and the legs swing — so the torso and
        // legs aren't stiff while the wings work.
        this.model.RotateBone("Chest", Quaternion.CreateFromAxisAngle(BodyAxis, Deg(body * BodyBobDeg)));
        this.model.RotateBone("Spine.005", Quaternion.CreateFromAxisAngle(BodyAxis, Deg(body * BodyBobDeg * 0.5f)));

        // Legs tuck back for flight, with a small sway on top (a single composed rotation per bone).
        Quaternion thighTuck = Quaternion.CreateFromAxisAngle(LegTuckAxis, Deg(ThighTuckDeg));
        Quaternion shinTuck = Quaternion.CreateFromAxisAngle(LegTuckAxis, Deg(ShinTuckDeg));
        float legSway = body * LegSwayDeg;
        this.model.RotateBone("Thigh.L", thighTuck * Quaternion.CreateFromAxisAngle(FlapAxis, Deg(legSway)));
        this.model.RotateBone("Thigh.R", thighTuck * Quaternion.CreateFromAxisAngle(FlapAxis, Deg(-legSway)));
        this.model.RotateBone("Shin.L", shinTuck);
        this.model.RotateBone("Shin.R", shinTuck);

        // Head: idle side-to-side scan + lean into the turn + follow the climb/dive (a composed yaw × pitch).
        float headYaw = (MathF.Sin(tailPhase * HeadYawSpeed) * HeadYawDeg) + (turn * HeadTurnFollowDeg);
        float headPitch = dive * HeadDiveFollowDeg;
        Quaternion headRot =
            Quaternion.CreateFromAxisAngle(HeadYawAxis, Deg(headYaw)) *
            Quaternion.CreateFromAxisAngle(HeadPitchAxis, Deg(headPitch));
        this.model.RotateBone("SkullControl", headRot);
        this.model.RotateBone(
            "Head.001",
            Quaternion.CreateFromAxisAngle(HeadYawAxis, Deg(headYaw * 0.5f)) *
            Quaternion.CreateFromAxisAngle(HeadPitchAxis, Deg(headPitch * 0.5f)));

        // Tail ripple: a travelling sine down the segments.
        for (int i = 0; i < TailBones.Length; i++)
        {
            float amount = MathF.Sin(tailPhase - (i * TailWaveRadians)) * TailSwayDeg;
            this.model.RotateBone(TailBones[i], Quaternion.CreateFromAxisAngle(TailAxis, Deg(amount)));
        }

        this.model.Skin();
    }

    // Rotates a mirror-paired L/R bone symmetrically: the right bone gets the negated angle so both move together.
    private void RotateMirrored(string leftBone, string rightBone, float degrees, Vector3 axis)
    {
        this.model.RotateBone(leftBone, Quaternion.CreateFromAxisAngle(axis, Deg(degrees)));
        this.model.RotateBone(rightBone, Quaternion.CreateFromAxisAngle(axis, Deg(-degrees)));
    }

    private static float Deg(float degrees) => degrees * (MathF.PI / 180f);
}