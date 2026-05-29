namespace MapaTur.Domain.Pois;

/// <summary>
/// Category of a mountain point-of-interest surfaced from OSM, used to pick its marker glyph and colour.
/// </summary>
public enum PoiKind
{
    /// <summary>Staffed mountain hut / refuge (OSM <c>tourism=alpine_hut</c>; PTTK "schronisko").</summary>
    Hut,

    /// <summary>Unstaffed / wilderness hut or bivouac (OSM <c>tourism=wilderness_hut</c>).</summary>
    WildernessHut,

    /// <summary>Chalet or holiday cottage (OSM <c>tourism=chalet</c>).</summary>
    Chalet,

    /// <summary>Basic shelter, lean-to or rain hut (OSM <c>amenity=shelter</c>).</summary>
    Shelter,

    /// <summary>Scenic viewpoint (OSM <c>tourism=viewpoint</c>).</summary>
    Viewpoint,
}