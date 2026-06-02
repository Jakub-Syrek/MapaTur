namespace MapaTur.Application.Markers;

/// <summary>One labelled line of a marker popup (e.g. <c>"Elevation" : "1503 m"</c>).</summary>
/// <param name="Label">Localized field label.</param>
/// <param name="Value">Localized, pre-formatted value.</param>
public readonly record struct MarkerPopupLine(string Label, string Value);

/// <summary>
/// The rendered content of a marker detail popup: a title plus zero or more labelled detail lines.
/// Built by <see cref="MarkerPopupFormatter"/> from a POI or climbing area.
/// </summary>
/// <param name="Title">Popup heading (the feature's name, or a localized fallback).</param>
/// <param name="Lines">Detail lines, in display order.</param>
public sealed record MarkerPopupContent(string Title, IReadOnlyList<MarkerPopupLine> Lines);