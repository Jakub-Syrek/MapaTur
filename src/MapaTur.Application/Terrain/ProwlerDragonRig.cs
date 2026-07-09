using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Procedural wing-beat + tail sway for the "prowler" dragon model (data/prowler_dragon_variant_rig.glb). Like
/// <see cref="DragonRig"/> but wired to THIS rig's bone names (Shoulder/Wing1/Wing2/Hand.L/.R, TailBase/Tail1..7,
/// Neck/Head). The model ships walk/action clips, not a flight loop, so we drive the wings ourselves: each frame
/// reset to bind, rotate the wing chain on a sine flap (mirrored L/R), ripple the tail, then re-skin. The rotation
/// AXES + amplitudes are TUNED BY EYE against this specific export (glTF bone frames vary per rig) — they live as
/// named constants so they're quick to adjust. On construction it records which expected bones are missing
/// (<see cref="MissingBones"/>), so a wrong name shows up immediately instead of as a stiff, non-flapping dragon.
/// </summary>
public sealed class ProwlerDragonRig
{
    private readonly SkinnedModel model;

    // Exact node names from the GLB (the "_NN" suffix is part of the fab-conversion node name).
    private const string ShoulderL = "Shoulder.L_91";
    private const string ShoulderR = "Shoulder.R_115";
    private const string Wing1L = "Wing1.L_90";
    private const string Wing1R = "Wing1.R_114";
    private const string Wing2L = "Wing2.L_87";
    private const string Wing2R = "Wing2.R_111";
    private const string HandL = "Hand.L_86";
    private const string HandR = "Hand.R_110";
    private const string Neck = "Neck_66";
    private const string Head = "Head_65";
    private static readonly string[] TailBones =
        { "TailBase_32", "Tail1_31", "Tail2_30", "Tail3_29", "Tail4_28", "Tail5_27", "Tail6_26", "Tail7_25" };

    // Flap tuning (degrees). START GUESS — tune by eye. The wing pivots at the shoulder; Wing1/Wing2/Hand add a
    // trailing secondary bend so the wingtip lags the shoulder. FlapAxis is the bone-local axis that raises/lowers
    // the wing; LeftMirror flips the left chain if the two wings turn out to beat opposite instead of together.
    private static readonly Vector3 FlapAxis = Vector3.UnitZ;
    private const float ShoulderFlapDeg = 34f;
    private const float Wing1FlapDeg = 24f;
    private const float Wing2FlapDeg = 18f;
    private const float HandFlapDeg = 12f;
    private const float WingtipLagRadians = 0.5f;
    private const float LeftMirror = -1f; // right chain takes the negated angle so both beat together

    private static readonly Vector3 TailAxis = Vector3.UnitY;
    private const float TailSwayDeg = 8f;
    private const float TailWaveRadians = 0.5f;

    // Head: a slow idle scan so the neck isn't stiff.
    private static readonly Vector3 HeadYawAxis = Vector3.UnitY;
    private const float HeadYawDeg = 10f;
    private const float HeadYawSpeed = 0.4f;

    /// <summary>Expected bones that this rig did NOT find in the model — the view logs these so a wrong bone name
    /// shows up immediately instead of as a mysteriously stiff, non-flapping dragon. Empty = every bone matched.</summary>
    public IReadOnlyList<string> MissingBones { get; }

    public ProwlerDragonRig(SkinnedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        this.model = model;

        string[] expected =
        {
            ShoulderL, ShoulderR, Wing1L, Wing1R, Wing2L, Wing2R, HandL, HandR, Neck, Head,
        };
        MissingBones = expected.Concat(TailBones).Where(b => !model.BoneNames.Contains(b)).ToArray();
    }

    /// <summary>
    /// Poses the rig for the given flap phase (radians) and re-skins. <paramref name="flapPhase"/> advances with
    /// time (faster when flapping harder); <paramref name="tailPhase"/> drives the tail ripple and head scan.
    /// </summary>
    public void Pose(float flapPhase, float tailPhase)
    {
        this.model.ResetPose();

        float flap = MathF.Sin(flapPhase);                        // −1 (down) … +1 (up)
        float tipFlap = MathF.Sin(flapPhase - WingtipLagRadians); // wingtip trails the shoulder

        RotateFlap(ShoulderL, ShoulderR, flap * ShoulderFlapDeg);
        RotateFlap(Wing1L, Wing1R, tipFlap * Wing1FlapDeg);
        RotateFlap(Wing2L, Wing2R, tipFlap * Wing2FlapDeg);
        RotateFlap(HandL, HandR, tipFlap * HandFlapDeg);

        // Tail: a travelling sine down the segments.
        for (int i = 0; i < TailBones.Length; i++)
        {
            float amount = MathF.Sin(tailPhase - (i * TailWaveRadians)) * TailSwayDeg;
            this.model.RotateBone(TailBones[i], Quaternion.CreateFromAxisAngle(TailAxis, Deg(amount)));
        }

        // Head idle scan.
        float headYaw = MathF.Sin(tailPhase * HeadYawSpeed) * HeadYawDeg;
        this.model.RotateBone(Neck, Quaternion.CreateFromAxisAngle(HeadYawAxis, Deg(headYaw * 0.5f)));
        this.model.RotateBone(Head, Quaternion.CreateFromAxisAngle(HeadYawAxis, Deg(headYaw * 0.5f)));

        this.model.Skin();
    }

    // Rotates a mirrored L/R bone pair on the flap axis — the right side takes the negated angle so both wings
    // beat together (LeftMirror flips the pairing if the export's bone frames are the other way round).
    private void RotateFlap(string left, string right, float degrees)
    {
        this.model.RotateBone(left, Quaternion.CreateFromAxisAngle(FlapAxis, Deg(degrees * LeftMirror)));
        this.model.RotateBone(right, Quaternion.CreateFromAxisAngle(FlapAxis, Deg(-degrees * LeftMirror)));
    }

    private static float Deg(float degrees) => degrees * (MathF.PI / 180f);
}