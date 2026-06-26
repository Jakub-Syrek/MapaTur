using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="StarCatalog"/>: parsing the bundled bright-star catalog
/// (CSV: raHours, decDegrees, magnitude, name?, constellation?). Real positions feed the star field; named
/// rows feed the labels. The parser is lenient — comments, blanks and malformed rows are skipped — so a
/// hand-edited or script-generated catalog never crashes the renderer.
/// </summary>
public sealed class StarCatalogTests
{
    [Fact]
    public void Parse_ReadsCoordinatesMagnitudeNameAndConstellation()
    {
        const string csv = "6.7525,-16.7161,-1.46,Sirius,Canis Major\n18.6156,38.7837,0.03,Vega,Lyra";

        var stars = StarCatalog.Parse(csv);

        stars.Should().HaveCount(2);
        stars[0].RaHours.Should().BeApproximately(6.7525, 1e-4);
        stars[0].DecDegrees.Should().BeApproximately(-16.7161, 1e-4);
        stars[0].Magnitude.Should().BeApproximately(-1.46, 1e-4);
        stars[0].Name.Should().Be("Sirius");
        stars[0].Constellation.Should().Be("Canis Major");
        stars[1].Name.Should().Be("Vega");
    }

    [Fact]
    public void Parse_RowWithoutName_HasNoName()
    {
        // A faint field star: coordinates + magnitude, no name/constellation columns.
        const string csv = "12.5,45.0,5.2";

        var stars = StarCatalog.Parse(csv);

        stars.Should().HaveCount(1);
        stars[0].HasName.Should().BeFalse();
        stars[0].Name.Should().BeNull();
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        const string csv = "# bright-star catalog\n\n6.7525,-16.7161,-1.46,Sirius,Canis Major\n   \n";

        StarCatalog.Parse(csv).Should().HaveCount(1);
    }

    [Fact]
    public void Parse_SkipsMalformedRows()
    {
        // Too few columns, then an unparseable coordinate — both dropped, the valid row survives.
        const string csv = "justonefield\n7.0,abc,2.0\n18.6156,38.7837,0.03,Vega,Lyra";

        var stars = StarCatalog.Parse(csv);

        stars.Should().ContainSingle();
        stars[0].Name.Should().Be("Vega");
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        StarCatalog.Parse("").Should().BeEmpty();
    }
}