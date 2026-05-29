namespace MapaTur.Domain.Pois;

/// <summary>
/// Maps OSM tags to a <see cref="PoiKind"/>. Returns <see langword="null"/> for anything that isn't a
/// mountain POI we render, so the response parser can skip it.
/// </summary>
public static class PoiKindParser
{
    /// <summary>
    /// Classifies an OSM element from its <c>tourism</c> / <c>amenity</c> / <c>shelter_type</c> tags.
    /// Tourism tags win over a bare <c>amenity=shelter</c> (a hut tagged as both is a hut).
    /// </summary>
    public static PoiKind? FromTags(string? tourism, string? amenity, string? shelterType)
    {
        _ = shelterType; // currently informational only; reserved for finer shelter sub-typing.

        if (Eq(tourism, "alpine_hut"))
        {
            return PoiKind.Hut;
        }
        if (Eq(tourism, "wilderness_hut"))
        {
            return PoiKind.WildernessHut;
        }
        if (Eq(tourism, "chalet"))
        {
            return PoiKind.Chalet;
        }
        if (Eq(tourism, "viewpoint"))
        {
            return PoiKind.Viewpoint;
        }
        if (Eq(amenity, "shelter"))
        {
            return PoiKind.Shelter;
        }

        return null;
    }

    private static bool Eq(string? value, string expected) =>
        value is not null && value.Equals(expected, StringComparison.OrdinalIgnoreCase);
}