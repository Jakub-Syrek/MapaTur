using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="ConstellationLines"/>: resolves the baked asterism topology (star-name
/// pairs) against the on-screen star labels into drawable screen-space segments. A segment is emitted only
/// when BOTH its endpoints are currently visible, so a partly-off-screen constellation simply loses the
/// affected lines rather than drawing to a phantom point.
/// </summary>
public sealed class ConstellationLinesTests
{
    private static StarLabel At(string name, float x, float y) => new(name, x, y, 1.0f);

    // The full Ursa Major / Big Dipper at arbitrary but distinct positions.
    private static StarLabel[] FullDipper() => new[]
    {
        At("Alkaid", 70f, 470f), At("Mizar", 305f, 610f), At("Alioth", 375f, 755f),
        At("Megrez", 475f, 935f), At("Dubhe", 805f, 1150f), At("Merak", 645f, 1280f),
        At("Phecda", 410f, 1090f),
    };

    [Fact]
    public void ResolveScreenSegments_AllDipperStarsVisible_ReturnsEverySegment()
    {
        var segments = ConstellationLines.ResolveScreenSegments(FullDipper());

        segments.Should().HaveCount(ConstellationLines.Segments.Count);
    }

    [Fact]
    public void ResolveScreenSegments_EndpointEndsAtItsStarPosition()
    {
        // The handle's tip segment Alkaid->Mizar should span exactly those two label positions.
        var segments = ConstellationLines.ResolveScreenSegments(FullDipper());

        segments.Should().Contain(s =>
            s.X1 == 70f && s.Y1 == 470f && s.X2 == 305f && s.Y2 == 610f);
    }

    [Fact]
    public void ResolveScreenSegments_MissingStar_SkipsOnlyItsSegments()
    {
        // Drop Megrez (the bowl/handle junction): the 3 segments touching it vanish, the other 4 remain.
        var withoutMegrez = FullDipper().Where(s => s.Name != "Megrez").ToList();

        var segments = ConstellationLines.ResolveScreenSegments(withoutMegrez);

        segments.Should().HaveCount(ConstellationLines.Segments.Count - 3);
    }

    [Fact]
    public void ResolveScreenSegments_NoStars_ReturnsEmpty()
    {
        ConstellationLines.ResolveScreenSegments(Array.Empty<StarLabel>()).Should().BeEmpty();
    }

    [Fact]
    public void ResolveScreenSegments_Null_Throws()
    {
        Action act = () => ConstellationLines.ResolveScreenSegments(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}