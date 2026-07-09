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

    // LEFT-WING CALIBRATION ("skrzydła nierównoległe"): the rig is structurally asymmetric — the right chain
    // has an extra link (Forearm.R.001) the left lacks, and in the BIND pose the left (x>0) wing sits ~3.2
    // units less spread and ~0.6 lower than the right. Mirrored angles alone therefore can never make the
    // wings beat in parallel. These static offsets, composed onto the left bones every pose, were fitted
    // NUMERICALLY (scratch probe: grid-search minimizing wing height/spread asymmetry of the skinned mesh
    // across the flap cycle; bind meanY 1.77/1.17 → 1.77/1.76, spread −9.22/+5.97 → −9.22/+7.54).
    private const float CalShoulderLZDeg = -44f;
    private const float CalShoulderLXDeg = 16f;
    private const float CalArmLZDeg = 36f;
    private const float CalArmLYDeg = -30f;
    private const float CalForearmLZDeg = 32f;
    // Wing FOLD (tuck) blended in on a dive: wings sweep down/in and the flap fades. Deep angles so a full
    // dive reads as the wings folded onto the body ("skrzydła po sobie"), not just half-lowered.
    private const float ShoulderFoldDeg = -78f;
    private const float ArmFoldDeg = -62f;
    private const float ForearmFoldDeg = -88f;

    // TURN FLAP ASYMMETRY: in a turn the OUTER wing beats harder and the inner one eases off (a real bird's
    // banked turn — "skręt w lewo → machnij prawym"). |turn| (the flight roll) is normalized by the max bank;
    // the boost/damp scale the FLAP amplitude only (the fold is a body posture and stays symmetric).
    // TurnFlapMirror flips which wing is "outer" if the bone naming turns out reversed in the world.
    private const float TurnWingNormRadians = 0.7f;  // ≈ DragonFlightParameters.MaxRollRadians
    private const float TurnOuterFlapBoost = 0.9f;   // outer wing: up to +90% amplitude at full bank
    private const float TurnInnerFlapDamp = 0.55f;   // inner wing: down to −55% amplitude at full bank
    private const float TurnFlapMirror = -1f;        // verified on the animated variant: turn LEFT must beat the RIGHT wing
    private static readonly Vector3 TailAxis = Vector3.UnitY;
    private const float TailSwayDeg = 9f;
    private const float TailWaveRadians = 0.55f;     // phase step per tail segment (a travelling ripple)

    // Body follow-through: the torso + legs lag the wing-beat a little and move with it (secondary motion).
    private static readonly Vector3 BodyAxis = Vector3.UnitX; // pitch the chest with the beat
    private const float BodyBobDeg = 4f;
    private const float BodyLagRadians = 0.9f;
    private const float LegSwayDeg = 6f;
    // In flight the legs tuck BACK (streamlined) instead of dangling down. Static tuck + a little sway on top.
    // legsDown (0..1) blends the tuck away toward the model's BIND pose — which is a standing dragon — for
    // the landing flare/perch ("nogi wychodzą do przodu przed przyziemieniem").
    private static readonly Vector3 LegTuckAxis = Vector3.UnitX;
    private const float ThighTuckDeg = 72f;
    private const float ShinTuckDeg = 40f;

    // Landing extras: wingBrake (0..1) spreads the wings up/forward and deepens the strokes (air-braking in
    // the flare); breathePhase (radians, < 0 = off) breathes the chest while perched on a summit.
    private const float BrakeSpreadDeg = 24f;
    private const float BrakeAmpBoost = 0.5f;
    private const float BreathDeg = 2.6f;
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
    /// makes the head follow the climb/dive. Landing inputs: <paramref name="legsDown"/> (0..1) brings the legs out
    /// of the flight tuck to the standing bind pose, <paramref name="wingBrake"/> (0..1) spreads/deepens the strokes
    /// for the flare, <paramref name="breathePhase"/> (radians, negative = off) breathes the chest on the perch.
    /// </summary>
    public void Pose(
        float flapPhase, float tailPhase, float turn, float dive, float fold = 0f,
        float legsDown = 0f, float wingBrake = 0f, float breathePhase = -1f)
    {
        this.model.ResetPose();

        float flap = MathF.Sin(flapPhase);                        // −1 (down-stroke) … +1 (up-stroke)
        float armFlap = MathF.Sin(flapPhase - WingtipLagRadians); // wingtip trails the shoulder
        float body = MathF.Sin(flapPhase - BodyLagRadians);       // torso/legs lag the beat

        // Wings beat TOGETHER: the L and R bones sit in mirrored local frames, so the right one takes the NEGATED
        // angle. The left bones additionally take the static calibration offsets (see CalShoulderL… above) that
        // even out the rig's asymmetric bind pose — without them the left wing beats visibly lower and less
        // spread than the right. On a dive (fold → 1) the flap amplitude fades and a static fold sweeps the
        // wings in/down.
        fold = Math.Clamp(fold, 0f, 1f);
        float flapScale = 1f - fold;

        // Turn asymmetry: the outer wing beats harder, the inner eases off. turn > 0 = banked right (per
        // DragonFlight: roll follows the yaw input, + = right-hand turn) → the LEFT wing is the outer one.
        // The wing brake (landing flare) deepens both strokes on top.
        wingBrake = Math.Clamp(wingBrake, 0f, 1f);
        float brakeAmp = 1f + (BrakeAmpBoost * wingBrake);
        float turnNorm = Math.Clamp(turn * TurnFlapMirror / TurnWingNormRadians, -1f, 1f);
        float leftFlapScale = (1f + (TurnOuterFlapBoost * MathF.Max(0f, turnNorm)) - (TurnInnerFlapDamp * MathF.Max(0f, -turnNorm))) * brakeAmp;
        float rightFlapScale = (1f + (TurnOuterFlapBoost * MathF.Max(0f, -turnNorm)) - (TurnInnerFlapDamp * MathF.Max(0f, turnNorm))) * brakeAmp;

        // Air-brake spread: the flare raises both wings up/forward on top of the (deepened) strokes.
        float brakeSpread = wingBrake * BrakeSpreadDeg;
        float shoulderFlapL = (flap * ShoulderFlapDeg * flapScale * leftFlapScale) + brakeSpread;
        float shoulderFlapR = (flap * ShoulderFlapDeg * flapScale * rightFlapScale) + brakeSpread;
        float armFlapL = armFlap * ArmFlapDeg * flapScale * leftFlapScale;
        float armFlapR = armFlap * ArmFlapDeg * flapScale * rightFlapScale;
        float forearmFlapL = armFlap * ForearmFlapDeg * flapScale * leftFlapScale;
        float forearmFlapR = armFlap * ForearmFlapDeg * flapScale * rightFlapScale;
        float shoulderFold = fold * ShoulderFoldDeg;
        float armFold = fold * ArmFoldDeg;
        float forearmFold = fold * ForearmFoldDeg;

        this.model.RotateBone(ShoulderL,
            Quaternion.CreateFromAxisAngle(FlapAxis, Deg(shoulderFlapL + shoulderFold + CalShoulderLZDeg)) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, Deg(CalShoulderLXDeg)));
        this.model.RotateBone(ShoulderR, Quaternion.CreateFromAxisAngle(FlapAxis, Deg(-(shoulderFlapR + shoulderFold))));
        this.model.RotateBone(ArmL,
            Quaternion.CreateFromAxisAngle(FlapAxis, Deg(armFlapL + armFold + CalArmLZDeg)) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, Deg(CalArmLYDeg)));
        this.model.RotateBone(ArmR, Quaternion.CreateFromAxisAngle(FlapAxis, Deg(-(armFlapR + armFold))));
        this.model.RotateBone(ForearmL, Quaternion.CreateFromAxisAngle(FlapAxis, Deg(forearmFlapL + forearmFold + CalForearmLZDeg)));
        this.model.RotateBone(ForearmR, Quaternion.CreateFromAxisAngle(FlapAxis, Deg(-(forearmFlapR + forearmFold))));

        // Body follow-through: the chest + upper spine pitch with the beat, and the legs swing — so the torso and
        // legs aren't stiff while the wings work. On the perch a slow breath moves the chest instead.
        float breathe = breathePhase >= 0f ? MathF.Sin(breathePhase) * BreathDeg : 0f;
        this.model.RotateBone("Chest", Quaternion.CreateFromAxisAngle(BodyAxis, Deg((body * BodyBobDeg * flapScale) + breathe)));
        this.model.RotateBone("Spine.005", Quaternion.CreateFromAxisAngle(BodyAxis, Deg(((body * BodyBobDeg * flapScale) + breathe) * 0.5f)));

        // Legs tuck back for flight, blending OUT toward the standing bind pose as legsDown → 1 (the flare
        // reaches the feet forward for the summit; perched = bind = standing).
        legsDown = Math.Clamp(legsDown, 0f, 1f);
        float tuckAmount = 1f - legsDown;
        Quaternion thighTuck = Quaternion.CreateFromAxisAngle(LegTuckAxis, Deg(ThighTuckDeg * tuckAmount));
        Quaternion shinTuck = Quaternion.CreateFromAxisAngle(LegTuckAxis, Deg(ShinTuckDeg * tuckAmount));
        float legSway = body * LegSwayDeg * tuckAmount;
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

    private static float Deg(float degrees) => degrees * (MathF.PI / 180f);
}