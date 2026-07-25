namespace MapaTur.Application.Terrain;

/// <summary>
/// Semantic joint → candidate bone names for a climbing rig. The adapter resolves each joint against the
/// actual model at construction, so the same code drives the KayKit hiker, a Mixamo export, or the purchased
/// realistic climber without hard-coding one skeleton. Alias order is preference order.
/// </summary>
public sealed record ClimberRigProfile
{
    public required string[] Pelvis { get; init; }
    public required string[] Chest { get; init; }
    public required string[] Head { get; init; }

    public required string[] LeftUpperArm { get; init; }
    public required string[] LeftLowerArm { get; init; }
    public required string[] LeftWrist { get; init; }
    public required string[] LeftPalm { get; init; }
    public required string[] RightUpperArm { get; init; }
    public required string[] RightLowerArm { get; init; }
    public required string[] RightWrist { get; init; }
    public required string[] RightPalm { get; init; }

    public required string[] LeftUpperLeg { get; init; }
    public required string[] LeftLowerLeg { get; init; }
    public required string[] LeftAnkle { get; init; }
    public required string[] LeftToes { get; init; }
    public required string[] RightUpperLeg { get; init; }
    public required string[] RightLowerLeg { get; init; }
    public required string[] RightAnkle { get; init; }
    public required string[] RightToes { get; init; }

    /// <summary>KayKit (hiker.glb) names first, then Mixamo and the purchased realistic-climber aliases.</summary>
    public static ClimberRigProfile CreateDefault() => new()
    {
        Pelvis = ["hips", "mixamorig:Hips", "Root_M"],
        Chest = ["chest", "mixamorig:Spine2", "Chest_M", "spine"],
        Head = ["head", "mixamorig:Head", "Head_M"],
        LeftUpperArm = ["upperarm.l", "mixamorig:LeftArm", "Shoulder_L"],
        LeftLowerArm = ["lowerarm.l", "mixamorig:LeftForeArm", "Elbow_L"],
        LeftWrist = ["wrist.l", "mixamorig:LeftHand", "Wrist_L"],
        LeftPalm = ["hand.l", "mixamorig:LeftHandMiddle1", "MiddleFinger1_L", "wrist.l"],
        RightUpperArm = ["upperarm.r", "mixamorig:RightArm", "Shoulder_R"],
        RightLowerArm = ["lowerarm.r", "mixamorig:RightForeArm", "Elbow_R"],
        RightWrist = ["wrist.r", "mixamorig:RightHand", "Wrist_R"],
        RightPalm = ["hand.r", "mixamorig:RightHandMiddle1", "MiddleFinger1_R", "wrist.r"],
        LeftUpperLeg = ["upperleg.l", "mixamorig:LeftUpLeg", "Hip_L"],
        LeftLowerLeg = ["lowerleg.l", "mixamorig:LeftLeg", "Knee_L"],
        LeftAnkle = ["foot.l", "mixamorig:LeftFoot", "Ankle_L"],
        LeftToes = ["toes.l", "mixamorig:LeftToeBase", "Toes_L", "foot.l"],
        RightUpperLeg = ["upperleg.r", "mixamorig:RightUpLeg", "Hip_R"],
        RightLowerLeg = ["lowerleg.r", "mixamorig:RightLeg", "Knee_R"],
        RightAnkle = ["foot.r", "mixamorig:RightFoot", "Ankle_R"],
        RightToes = ["toes.r", "mixamorig:RightToeBase", "Toes_R", "foot.r"]
    };
}