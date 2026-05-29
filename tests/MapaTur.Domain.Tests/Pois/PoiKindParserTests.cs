using FluentAssertions;

using MapaTur.Domain.Pois;

namespace MapaTur.Domain.Tests.Pois;

public sealed class PoiKindParserTests
{
    [Theory]
    [InlineData("alpine_hut", null, null, PoiKind.Hut)]
    [InlineData("wilderness_hut", null, null, PoiKind.WildernessHut)]
    [InlineData("chalet", null, null, PoiKind.Chalet)]
    [InlineData("viewpoint", null, null, PoiKind.Viewpoint)]
    [InlineData(null, "shelter", null, PoiKind.Shelter)]
    [InlineData(null, "shelter", "lean_to", PoiKind.Shelter)]
    public void FromTags_MapsRecognisedTags(string? tourism, string? amenity, string? shelterType, PoiKind expected)
    {
        PoiKindParser.FromTags(tourism, amenity, shelterType).Should().Be(expected);
    }

    [Fact]
    public void FromTags_PrefersTourismOverAmenity()
    {
        // A building tagged both tourism=alpine_hut and amenity=shelter is a hut, not a bare shelter.
        PoiKindParser.FromTags("alpine_hut", "shelter", null).Should().Be(PoiKind.Hut);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("hotel", null, null)]
    [InlineData(null, "restaurant", null)]
    public void FromTags_UnrecognisedTags_ReturnNull(string? tourism, string? amenity, string? shelterType)
    {
        PoiKindParser.FromTags(tourism, amenity, shelterType).Should().BeNull();
    }

    [Fact]
    public void FromTags_IsCaseInsensitive()
    {
        PoiKindParser.FromTags("Alpine_Hut", null, null).Should().Be(PoiKind.Hut);
    }
}