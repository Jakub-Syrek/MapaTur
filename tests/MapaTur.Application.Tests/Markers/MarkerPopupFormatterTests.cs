using FluentAssertions;

using MapaTur.Application.Markers;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;

namespace MapaTur.Application.Tests.Markers;

public sealed class MarkerPopupFormatterTests
{
    /// <summary>Deterministic English-ish labels so the assembled content is assertable.</summary>
    private sealed class FakeLabels : IMarkerPopupLabels
    {
        public string CategoryLabel => "Type";
        public string ElevationLabel => "Elevation";
        public string GradeLabel => "Grade";
        public string LengthLabel => "Length";
        public string ProtectionLabel => "Protection";
        public string Bolted => "bolted";
        public string Trad => "trad";
        public string UnnamedPoi => "Point of interest";
        public string UnnamedClimbing => "Climbing area";

        public string PoiKindName(PoiKind kind) => $"kind:{kind}";

        public string ClimbingTypeName(ClimbingType type) => $"type:{type}";
    }

    private static readonly FakeLabels Labels = new();

    private static GeoPoint Pos => new(49.2, 20.0);

    [Fact]
    public void ForPoi_NullPoi_Throws()
    {
        Action act = () => MarkerPopupFormatter.ForPoi(null!, Labels);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ForPoi_NullLabels_Throws()
    {
        var poi = new MountainPoi(1, "Murowaniec", Pos, PoiKind.Hut, 1500);

        Action act = () => MarkerPopupFormatter.ForPoi(poi, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ForPoi_UsesNameAsTitle()
    {
        var poi = new MountainPoi(1, "Murowaniec", Pos, PoiKind.Hut, 1500);

        var content = MarkerPopupFormatter.ForPoi(poi, Labels);

        content.Title.Should().Be("Murowaniec");
    }

    [Fact]
    public void ForPoi_EmptyName_FallsBackToUnnamed()
    {
        var poi = new MountainPoi(1, "   ", Pos, PoiKind.Viewpoint);

        var content = MarkerPopupFormatter.ForPoi(poi, Labels);

        content.Title.Should().Be("Point of interest");
    }

    [Fact]
    public void ForPoi_IncludesCategoryLine()
    {
        var poi = new MountainPoi(1, "Murowaniec", Pos, PoiKind.Hut, 1500);

        var content = MarkerPopupFormatter.ForPoi(poi, Labels);

        content.Lines.Should().ContainSingle(l => l.Label == "Type" && l.Value == "kind:Hut");
    }

    [Fact]
    public void ForPoi_WithElevation_FormatsMetres()
    {
        var poi = new MountainPoi(1, "Murowaniec", Pos, PoiKind.Hut, 1502.7);

        var content = MarkerPopupFormatter.ForPoi(poi, Labels);

        content.Lines.Should().ContainSingle(l => l.Label == "Elevation" && l.Value == "1503 m");
    }

    [Fact]
    public void ForPoi_WithoutElevation_OmitsElevationLine()
    {
        var poi = new MountainPoi(1, "Murowaniec", Pos, PoiKind.Hut);

        var content = MarkerPopupFormatter.ForPoi(poi, Labels);

        content.Lines.Should().NotContain(l => l.Label == "Elevation");
    }

    [Fact]
    public void ForClimbing_UsesNameAsTitle()
    {
        var area = new ClimbingArea(1, "Mnich", Pos, ClimbingType.MultiPitch, "VI+", 120, true);

        var content = MarkerPopupFormatter.ForClimbing(area, Labels);

        content.Title.Should().Be("Mnich");
    }

    [Fact]
    public void ForClimbing_EmptyName_FallsBackToUnnamed()
    {
        var area = new ClimbingArea(1, "", Pos, ClimbingType.Crag);

        var content = MarkerPopupFormatter.ForClimbing(area, Labels);

        content.Title.Should().Be("Climbing area");
    }

    [Fact]
    public void ForClimbing_FullData_IncludesAllLines()
    {
        var area = new ClimbingArea(1, "Mnich", Pos, ClimbingType.MultiPitch, "VI+", 120, true);

        var content = MarkerPopupFormatter.ForClimbing(area, Labels);

        content.Lines.Should().SatisfyRespectively(
            l => { l.Label.Should().Be("Type"); l.Value.Should().Be("type:MultiPitch"); },
            l => { l.Label.Should().Be("Grade"); l.Value.Should().Be("VI+"); },
            l => { l.Label.Should().Be("Length"); l.Value.Should().Be("120 m"); },
            l => { l.Label.Should().Be("Protection"); l.Value.Should().Be("bolted"); });
    }

    [Fact]
    public void ForClimbing_TradProtection_ShowsTradLabel()
    {
        var area = new ClimbingArea(1, "Zamarła Turnia", Pos, ClimbingType.TradRoute, isBolted: false);

        var content = MarkerPopupFormatter.ForClimbing(area, Labels);

        content.Lines.Should().ContainSingle(l => l.Label == "Protection" && l.Value == "trad");
    }

    [Fact]
    public void ForClimbing_NoOptionalData_OnlyCategoryLine()
    {
        var area = new ClimbingArea(1, "Anonimowa turnia", Pos, ClimbingType.Cliff);

        var content = MarkerPopupFormatter.ForClimbing(area, Labels);

        content.Lines.Should().ContainSingle()
            .Which.Label.Should().Be("Type");
    }

    [Fact]
    public void ForClimbing_BlankGrade_OmitsGradeLine()
    {
        var area = new ClimbingArea(1, "Krag", Pos, ClimbingType.Crag, "   ");

        var content = MarkerPopupFormatter.ForClimbing(area, Labels);

        content.Lines.Should().NotContain(l => l.Label == "Grade");
    }
}
