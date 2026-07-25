using MapaTur.Application.Terrain;
using MapaTur.Climbing;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Click-to-select on a HELD hold when a matched pair shares it (two hands / two feet on one wide hold):
/// the first click selects one occupant, clicking the SAME hold again cycles to the other occupant, and
/// a selection that is not on the hold starts the cycle from the first occupant.
/// </summary>
public sealed class ClimbLimbSelectionTests
{
    [Fact]
    public void PickOwner_should_select_the_single_owner_when_nothing_is_selected()
    {
        ClimbLimb picked = ClimbLimbSelection.PickOwner([ClimbLimb.LeftHand], selected: null);

        Assert.Equal(ClimbLimb.LeftHand, picked);
    }

    [Fact]
    public void PickOwner_should_keep_the_single_owner_when_it_is_already_selected()
    {
        ClimbLimb picked = ClimbLimbSelection.PickOwner([ClimbLimb.RightFoot], selected: ClimbLimb.RightFoot);

        Assert.Equal(ClimbLimb.RightFoot, picked);
    }

    [Fact]
    public void PickOwner_should_select_the_first_occupant_of_a_shared_hold_when_nothing_is_selected()
    {
        ClimbLimb picked = ClimbLimbSelection.PickOwner([ClimbLimb.LeftHand, ClimbLimb.RightHand], selected: null);

        Assert.Equal(ClimbLimb.LeftHand, picked);
    }

    [Fact]
    public void PickOwner_should_cycle_to_the_second_occupant_when_the_first_is_selected()
    {
        ClimbLimb picked = ClimbLimbSelection.PickOwner(
            [ClimbLimb.LeftHand, ClimbLimb.RightHand], selected: ClimbLimb.LeftHand);

        Assert.Equal(ClimbLimb.RightHand, picked);
    }

    [Fact]
    public void PickOwner_should_cycle_back_to_the_first_occupant_when_the_second_is_selected()
    {
        ClimbLimb picked = ClimbLimbSelection.PickOwner(
            [ClimbLimb.LeftFoot, ClimbLimb.RightFoot], selected: ClimbLimb.RightFoot);

        Assert.Equal(ClimbLimb.LeftFoot, picked);
    }

    [Fact]
    public void PickOwner_should_start_from_the_first_occupant_when_the_selected_limb_is_not_on_the_hold()
    {
        ClimbLimb picked = ClimbLimbSelection.PickOwner(
            [ClimbLimb.LeftFoot, ClimbLimb.RightFoot], selected: ClimbLimb.LeftHand);

        Assert.Equal(ClimbLimb.LeftFoot, picked);
    }
}