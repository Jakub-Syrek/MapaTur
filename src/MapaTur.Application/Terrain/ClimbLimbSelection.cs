using MapaTur.Climbing;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Which limb a click on a HELD hold should select. A matched pair shares one wide hold (two hands or
/// two feet), so a single click must be able to reach BOTH: the first click picks the first occupant,
/// clicking the same hold again cycles to the next one. A selection that is not on the hold (or none)
/// starts the cycle from the first occupant.
/// </summary>
public static class ClimbLimbSelection
{
    /// <summary>Picks the limb a click on a held hold selects. <paramref name="owners"/> is the stable-ordered
    /// list of limbs currently holding the clicked hold (never empty).</summary>
    public static ClimbLimb PickOwner(IReadOnlyList<ClimbLimb> owners, ClimbLimb? selected)
    {
        ArgumentNullException.ThrowIfNull(owners);
        if (owners.Count == 0)
        {
            throw new ArgumentException("A held hold always has at least one occupant.", nameof(owners));
        }

        if (selected is { } current)
        {
            for (int i = 0; i < owners.Count; i++)
            {
                if (owners[i] == current)
                {
                    return owners[(i + 1) % owners.Count];
                }
            }
        }

        return owners[0];
    }
}