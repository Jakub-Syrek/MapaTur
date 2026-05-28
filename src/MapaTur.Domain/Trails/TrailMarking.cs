namespace MapaTur.Domain.Trails;

/// <summary>
/// Trail marking colour and optional textual annotation. A trail can have multiple
/// markings when it carries concurrent designations (e.g. a red and a blue trail
/// sharing a section); represent each as a separate <see cref="TrailMarking"/>.
/// </summary>
/// <param name="Color">Canonical PTTK color.</param>
/// <param name="OsmcRaw">Raw <c>osmc:symbol</c> tag value from OSM (for debugging).</param>
public sealed record TrailMarking(PttkColor Color, string? OsmcRaw = null);