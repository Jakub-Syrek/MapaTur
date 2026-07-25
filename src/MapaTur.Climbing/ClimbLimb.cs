namespace MapaTur.Climbing;

public enum ClimbLimb
{
    LeftHand,
    RightHand,
    LeftFoot,
    RightFoot
}

public static class ClimbLimbExtensions
{
    public static bool IsHand(this ClimbLimb limb) => limb is ClimbLimb.LeftHand or ClimbLimb.RightHand;

    public static bool IsFoot(this ClimbLimb limb) => !limb.IsHand();

    public static bool IsLeft(this ClimbLimb limb) => limb is ClimbLimb.LeftHand or ClimbLimb.LeftFoot;
}