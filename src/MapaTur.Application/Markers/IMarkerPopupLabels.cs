using MapaTur.Domain.Climbing;
using MapaTur.Domain.Pois;

namespace MapaTur.Application.Markers;

/// <summary>
/// Supplies the localized labels and enum display names the <see cref="MarkerPopupFormatter"/> needs to
/// assemble popup content. Implemented in the UI layer (backed by the app's localized resources) so the
/// formatter itself stays culture-agnostic and unit-testable.
/// </summary>
public interface IMarkerPopupLabels
{
    /// <summary>Label for the category/type line (e.g. "Type").</summary>
    string CategoryLabel { get; }

    /// <summary>Label for the elevation line (e.g. "Elevation").</summary>
    string ElevationLabel { get; }

    /// <summary>Label for the climbing grade line (e.g. "Grade").</summary>
    string GradeLabel { get; }

    /// <summary>Label for the route length line (e.g. "Length").</summary>
    string LengthLabel { get; }

    /// <summary>Label for the protection line (e.g. "Protection").</summary>
    string ProtectionLabel { get; }

    /// <summary>Value shown for a bolted route.</summary>
    string Bolted { get; }

    /// <summary>Value shown for a trad (gear-protected) route.</summary>
    string Trad { get; }

    /// <summary>Title fallback for a POI with no name.</summary>
    string UnnamedPoi { get; }

    /// <summary>Title fallback for a climbing area with no name.</summary>
    string UnnamedClimbing { get; }

    /// <summary>Localized display name for a POI kind.</summary>
    string PoiKindName(PoiKind kind);

    /// <summary>Localized display name for a climbing type.</summary>
    string ClimbingTypeName(ClimbingType type);
}