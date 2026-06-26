using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class DetailTileGridTests
{
    [Fact]
    public void AbsoluteBoundaries_TwoOverlappingWindows_ShareTheSameAbsoluteCuts()
    {
        // W1 covers absolute [1000,1300); W2 is shifted +100 to [1100,1400). The overlap is [1100,1300).
        int[] w1 = DetailTileGrid.AbsoluteBoundaries(absOrigin: 1000, total: 300, quantum: 100);
        int[] w2 = DetailTileGrid.AbsoluteBoundaries(absOrigin: 1100, total: 300, quantum: 100);

        // Convert each window's LOCAL cuts to ABSOLUTE positions.
        HashSet<int> abs1 = w1.Select(c => 1000 + c).ToHashSet();
        HashSet<int> abs2 = w2.Select(c => 1100 + c).ToHashSet();

        // In the overlap the cuts must coincide — that is what makes blocks line up and the cache hit while flying.
        foreach (int cut in new[] { 1100, 1200, 1300 })
        {
            abs1.Should().Contain(cut);
            abs2.Should().Contain(cut);
        }
    }

    [Fact]
    public void AbsoluteBoundaries_AlwaysIncludeWindowEnds_AndStayInRange()
    {
        int[] cuts = DetailTileGrid.AbsoluteBoundaries(absOrigin: 777, total: 640, quantum: 256);

        cuts.Should().StartWith(0);
        cuts.Should().EndWith(640);
        cuts.Should().BeInAscendingOrder();
        cuts.Should().OnlyContain(c => c >= 0 && c <= 640);
    }

    [Fact]
    public void AbsoluteBoundaries_DropsSubTwoCellSliversAgainstTheEnds()
    {
        // absOrigin 99, quantum 100 ⇒ first multiple at local 1 (abs 100) — only 1 cell from the start ⇒ dropped.
        int[] cuts = DetailTileGrid.AbsoluteBoundaries(absOrigin: 99, total: 300, quantum: 100);

        cuts.Should().NotContain(1);
        foreach (int i in Enumerable.Range(1, cuts.Length - 1))
        {
            (cuts[i] - cuts[i - 1]).Should().BeGreaterThanOrEqualTo(2);
        }
    }

    [Fact]
    public void AbsoluteBoundaries_TinyWindow_ReturnsJustTheEnds()
    {
        DetailTileGrid.AbsoluteBoundaries(absOrigin: 0, total: 3, quantum: 100)
            .Should().Equal(0, 3);
    }
}