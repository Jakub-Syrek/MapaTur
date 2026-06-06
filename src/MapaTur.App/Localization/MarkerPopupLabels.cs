using MapaTur.Application.Markers;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Pois;

namespace MapaTur.App.Localization;

/// <summary>
/// Supplies <see cref="MarkerPopupFormatter"/> with the app's localized labels and enum display
/// names, sourced from <see cref="AppStrings"/>. Stateless; share the <see cref="Instance"/>.
/// </summary>
public sealed class MarkerPopupLabels : IMarkerPopupLabels
{
    /// <summary>Shared stateless instance.</summary>
    public static MarkerPopupLabels Instance { get; } = new();

    /// <inheritdoc />
    public string CategoryLabel => AppStrings.PopupCategory;

    /// <inheritdoc />
    public string ElevationLabel => AppStrings.PopupElevation;

    /// <inheritdoc />
    public string GradeLabel => AppStrings.PopupGrade;

    /// <inheritdoc />
    public string LengthLabel => AppStrings.PopupLength;

    /// <inheritdoc />
    public string ProtectionLabel => AppStrings.PopupProtection;

    /// <inheritdoc />
    public string Bolted => AppStrings.PopupBolted;

    /// <inheritdoc />
    public string Trad => AppStrings.PopupTrad;

    /// <inheritdoc />
    public string UnnamedPoi => AppStrings.PopupUnnamedPoi;

    /// <inheritdoc />
    public string UnnamedClimbing => AppStrings.PopupUnnamedClimbing;

    /// <inheritdoc />
    public string PoiKindName(PoiKind kind) => kind switch
    {
        PoiKind.Hut => AppStrings.PoiKindHut,
        PoiKind.WildernessHut => AppStrings.PoiKindWildernessHut,
        PoiKind.Chalet => AppStrings.PoiKindChalet,
        PoiKind.Shelter => AppStrings.PoiKindShelter,
        PoiKind.Viewpoint => AppStrings.PoiKindViewpoint,
        _ => AppStrings.PopupUnnamedPoi,
    };

    /// <inheritdoc />
    public string ClimbingTypeName(ClimbingType type) => type switch
    {
        ClimbingType.SportRoute => AppStrings.ClimbingTypeSportRoute,
        ClimbingType.TradRoute => AppStrings.ClimbingTypeTradRoute,
        ClimbingType.MultiPitch => AppStrings.ClimbingTypeMultiPitch,
        ClimbingType.Boulder => AppStrings.ClimbingTypeBoulder,
        ClimbingType.Crag => AppStrings.ClimbingTypeCrag,
        ClimbingType.Cliff => AppStrings.ClimbingTypeCliff,
        _ => AppStrings.ClimbingTypeUnspecified,
    };
}