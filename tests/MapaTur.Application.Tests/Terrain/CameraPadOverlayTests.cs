using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of the Skia-drawn camera pad overlay geometry (task #8: the XAML AltitudePad/PanTiltPad
/// over the SwapChainPanel are what leaks WinUI composition surfaces, so on Windows the pads are drawn
/// inside the GL/Skia surface instead). The layout mirrors the XAML pads 1:1 in DIPs: bottom-left
/// column of two 46-dip altitude buttons, bottom-right 3x4 grid (rotate/pan/tilt), margin 12, spacing 4,
/// small 34-dip tilt buttons centred in their cells. Hit-testing is circular (CornerRadius = size/2).
/// </summary>
public sealed class CameraPadOverlayTests
{
    private const float W = 1920f;
    private const float H = 1080f;

    private static IReadOnlyList<CameraPadButton> Lay(float scale = 1f) =>
        CameraPadOverlay.Layout(W * scale, H * scale, scale);

    private static CameraPadButton Btn(IReadOnlyList<CameraPadButton> layout, CameraPadAction a) =>
        layout.Single(b => b.Action == a);

    [Fact]
    public void Layout_ContainsEachOfTheTenActionsExactlyOnce()
    {
        IReadOnlyList<CameraPadButton> layout = Lay();

        layout.Select(b => b.Action).Should().OnlyHaveUniqueItems().And.HaveCount(10);
    }

    [Fact]
    public void Layout_PlacesLowerAltitudeButtonAtBottomLeftMargin()
    {
        CameraPadButton lower = Btn(Lay(), CameraPadAction.Lower);

        lower.X.Should().Be(12f);
        (lower.Y + lower.Size).Should().Be(H - 12f);
    }

    [Fact]
    public void Layout_PlacesRaiseDirectlyAboveLowerWithSpacing()
    {
        IReadOnlyList<CameraPadButton> layout = Lay();
        CameraPadButton raise = Btn(layout, CameraPadAction.Raise);
        CameraPadButton lower = Btn(layout, CameraPadAction.Lower);

        raise.X.Should().Be(lower.X);
        (raise.Y + raise.Size + 4f).Should().Be(lower.Y);
    }

    [Fact]
    public void Layout_PlacesPanBackAtBottomOfGridCentreColumn()
    {
        CameraPadButton panBack = Btn(Lay(), CameraPadAction.PanBack);

        // Grid: 3 kolumny po 46 + 2 przerwy po 4 = 146 szerokosci, prawa krawedz na W-12;
        // srodek kolumny srodkowej = W - 12 - 146 + 46 + 4 + 23.
        (panBack.X + (panBack.Size / 2f)).Should().Be(W - 12f - 146f + 46f + 4f + 23f);
        (panBack.Y + panBack.Size).Should().Be(H - 12f);
    }

    [Fact]
    public void Layout_UsesSmallSizeForTiltButtons()
    {
        IReadOnlyList<CameraPadButton> layout = Lay();

        Btn(layout, CameraPadAction.LookUp).Size.Should().Be(34f);
        Btn(layout, CameraPadAction.LookDown).Size.Should().Be(34f);
        Btn(layout, CameraPadAction.PanForward).Size.Should().Be(46f);
    }

    [Fact]
    public void Layout_CentresSmallTiltButtonInItsCell()
    {
        IReadOnlyList<CameraPadButton> layout = Lay();
        CameraPadButton lookUp = Btn(layout, CameraPadAction.LookUp);
        CameraPadButton panForward = Btn(layout, CameraPadAction.PanForward);

        // Ta sama kolumna co pan-forward: srodki X musza sie pokrywac mimo mniejszego rozmiaru.
        (lookUp.X + (lookUp.Size / 2f)).Should().Be(panForward.X + (panForward.Size / 2f));
    }

    [Fact]
    public void Layout_ScalesAllGeometryWithDisplayScale()
    {
        CameraPadButton at1 = Btn(Lay(), CameraPadAction.PanForward);
        CameraPadButton at2 = Btn(Lay(scale: 2f), CameraPadAction.PanForward);

        at2.X.Should().Be(at1.X * 2f);
        at2.Y.Should().Be(at1.Y * 2f);
        at2.Size.Should().Be(at1.Size * 2f);
    }

    [Fact]
    public void HitTest_ReturnsActionAtButtonCentre()
    {
        IReadOnlyList<CameraPadButton> layout = Lay();
        CameraPadButton raise = Btn(layout, CameraPadAction.Raise);

        CameraPadAction? hit = CameraPadOverlay.HitTest(
            layout, raise.X + (raise.Size / 2f), raise.Y + (raise.Size / 2f));

        hit.Should().Be(CameraPadAction.Raise);
    }

    [Fact]
    public void HitTest_ReturnsNullInSquareCornerOutsideCircle()
    {
        IReadOnlyList<CameraPadButton> layout = Lay();
        CameraPadButton raise = Btn(layout, CameraPadAction.Raise);

        // Rog kwadratu lezy poza kolem o promieniu size/2 — przyciski sa okragle (CornerRadius 23).
        CameraPadAction? hit = CameraPadOverlay.HitTest(layout, raise.X + 1f, raise.Y + 1f);

        hit.Should().BeNull();
    }

    [Fact]
    public void HitTest_ReturnsNullFarFromAnyPad()
    {
        CameraPadAction? hit = CameraPadOverlay.HitTest(Lay(), W / 2f, H / 2f);

        hit.Should().BeNull();
    }

    [Fact]
    public void Layout_KeepsEveryButtonInsideViewport()
    {
        foreach (CameraPadButton b in Lay())
        {
            b.X.Should().BeGreaterThanOrEqualTo(0f);
            b.Y.Should().BeGreaterThanOrEqualTo(0f);
            (b.X + b.Size).Should().BeLessThanOrEqualTo(W);
            (b.Y + b.Size).Should().BeLessThanOrEqualTo(H);
        }
    }
}