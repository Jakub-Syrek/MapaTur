using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class MeshRebuildCoalescerTests
{
    [Fact]
    public void RequestRebuild_WhenIdle_ReturnsValueToBuild()
    {
        var coalescer = new MeshRebuildCoalescer();

        coalescer.RequestRebuild(2.0).Should().Be(2.0);
    }

    [Fact]
    public void RequestRebuild_WhileBuildInFlight_ReturnsNull()
    {
        var coalescer = new MeshRebuildCoalescer();
        coalescer.RequestRebuild(2.0);

        coalescer.RequestRebuild(3.0).Should().BeNull();
    }

    [Fact]
    public void CompleteRebuild_WithNoPendingRequest_ReturnsNull()
    {
        var coalescer = new MeshRebuildCoalescer();
        coalescer.RequestRebuild(2.0);

        coalescer.CompleteRebuild().Should().BeNull();
    }

    [Fact]
    public void CompleteRebuild_AfterRequestDuringFlight_ReturnsLatestPendingValue()
    {
        var coalescer = new MeshRebuildCoalescer();
        coalescer.RequestRebuild(2.0);   // starts building 2.0
        coalescer.RequestRebuild(3.0);   // dropped intermediate
        coalescer.RequestRebuild(4.0);   // latest trailing value

        // The 2.0 build finishes; the trailing request must be the LAST value seen (4.0),
        // not the first dropped one (3.0) — this is the bug the coalescer fixes.
        coalescer.CompleteRebuild().Should().Be(4.0);
    }

    [Fact]
    public void CompleteRebuild_DrainingTrailingThenIdle_ReturnsNull()
    {
        var coalescer = new MeshRebuildCoalescer();
        coalescer.RequestRebuild(2.0);
        coalescer.RequestRebuild(4.0);

        coalescer.CompleteRebuild().Should().Be(4.0);  // trailing rebuild starts for 4.0
        coalescer.CompleteRebuild().Should().BeNull();  // nothing pending after it
    }

    [Fact]
    public void RequestRebuild_AfterDrainingToIdle_StartsFreshBuild()
    {
        var coalescer = new MeshRebuildCoalescer();
        coalescer.RequestRebuild(2.0);
        coalescer.CompleteRebuild();  // back to idle

        coalescer.RequestRebuild(5.0).Should().Be(5.0);
    }

    [Fact]
    public void RequestRebuild_DuringTrailingRebuild_StashesAgain()
    {
        var coalescer = new MeshRebuildCoalescer();
        coalescer.RequestRebuild(2.0);
        coalescer.RequestRebuild(4.0);
        double? trailing = coalescer.CompleteRebuild();   // 4.0 build now in flight
        trailing.Should().Be(4.0);

        coalescer.RequestRebuild(6.0).Should().BeNull();  // still in flight → stashed
        coalescer.CompleteRebuild().Should().Be(6.0);      // drains the new trailing value
    }
}