using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class OrthoResidencyPlannerTests
{
    [Fact]
    public void Ctor_NonPositiveBudget_Throws()
    {
        Action act = () => _ = new OrthoResidencyPlanner(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Plan_FirstFrameWithinBudget_UploadsAllVisible()
    {
        var planner = new OrthoResidencyPlanner(maxResidentCells: 4);
        int[] visible = { 0, 1 };

        var plan = planner.Plan(visible);

        plan.ToUpload.Should().BeEquivalentTo(visible);
        plan.ToEvict.Should().BeEmpty();
        planner.Resident.Should().BeEquivalentTo(visible);
    }

    [Fact]
    public void Plan_ResidentVisibleCells_NotReUploaded()
    {
        var planner = new OrthoResidencyPlanner(maxResidentCells: 4);
        int[] visible = { 0, 1 };
        planner.Plan(visible);

        var plan = planner.Plan(visible);

        plan.ToUpload.Should().BeEmpty();
        plan.ToEvict.Should().BeEmpty();
    }

    [Fact]
    public void Plan_NewVisibleCell_UploadedOnly()
    {
        var planner = new OrthoResidencyPlanner(maxResidentCells: 4);
        int[] first = { 0, 1 };
        int[] second = { 0, 1, 2 };
        int[] expectedUpload = { 2 };
        planner.Plan(first);

        var plan = planner.Plan(second);

        plan.ToUpload.Should().BeEquivalentTo(expectedUpload);
        plan.ToEvict.Should().BeEmpty();
        planner.Resident.Should().BeEquivalentTo(second);
    }

    [Fact]
    public void Plan_OverBudget_EvictsLeastRecentlyUsedNonVisible()
    {
        var planner = new OrthoResidencyPlanner(maxResidentCells: 2);
        int[] first = { 0, 1 }; // recency: 0 (oldest), 1
        int[] second = { 2 };
        int[] expectedUpload = { 2 };
        int[] expectedEvict = { 0 };
        int[] expectedResident = { 1, 2 };
        planner.Plan(first);

        var plan = planner.Plan(second);

        plan.ToUpload.Should().BeEquivalentTo(expectedUpload);
        plan.ToEvict.Should().BeEquivalentTo(expectedEvict);
        planner.Resident.Should().BeEquivalentTo(expectedResident);
    }

    [Fact]
    public void Plan_VisibleSetExceedsBudget_KeepsAllVisibleNoEviction()
    {
        var planner = new OrthoResidencyPlanner(maxResidentCells: 2);
        int[] visible = { 0, 1, 2 };

        var plan = planner.Plan(visible);

        plan.ToUpload.Should().BeEquivalentTo(visible);
        plan.ToEvict.Should().BeEmpty();
        planner.Resident.Should().BeEquivalentTo(visible);
    }

    [Fact]
    public void Plan_AfterOversizedFrame_EvictsDownToBudgetWhenVisibleShrinks()
    {
        var planner = new OrthoResidencyPlanner(maxResidentCells: 2);
        int[] first = { 0, 1, 2 }; // all resident, over budget but all visible
        int[] second = { 3 };
        int[] expectedUpload = { 3 };
        int[] expectedEvict = { 0, 1 };
        int[] expectedResident = { 2, 3 };
        planner.Plan(first);

        var plan = planner.Plan(second);

        plan.ToUpload.Should().BeEquivalentTo(expectedUpload);
        plan.ToEvict.Should().BeEquivalentTo(expectedEvict);
        planner.Resident.Should().BeEquivalentTo(expectedResident);
    }

    [Fact]
    public void Plan_RetouchingResidentCell_RefreshesRecency()
    {
        var planner = new OrthoResidencyPlanner(maxResidentCells: 2);
        int[] first = { 0, 1 }; // recency: 0,1
        int[] retouch = { 0 };  // re-touch 0 -> recency: 1,0
        int[] third = { 2 };
        int[] expectedUpload = { 2 };
        int[] expectedEvict = { 1 };
        int[] expectedResident = { 0, 2 };
        planner.Plan(first);
        planner.Plan(retouch);

        var plan = planner.Plan(third);

        // 1 is now the least-recently-used non-visible cell, so it is evicted, not 0.
        plan.ToUpload.Should().BeEquivalentTo(expectedUpload);
        plan.ToEvict.Should().BeEquivalentTo(expectedEvict);
        planner.Resident.Should().BeEquivalentTo(expectedResident);
    }

    [Fact]
    public void Plan_NullVisible_Throws()
    {
        var planner = new OrthoResidencyPlanner(maxResidentCells: 2);

        Action act = () => planner.Plan(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
