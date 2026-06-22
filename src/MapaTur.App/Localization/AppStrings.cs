using System.Resources;

namespace MapaTur.App.Localization;

/// <summary>
/// Strongly-typed accessor for the application's localized string resources.
/// Backed by an embedded <see cref="ResourceManager"/> that automatically picks the
/// satellite assembly matching <see cref="System.Globalization.CultureInfo.CurrentUICulture"/>.
/// </summary>
public static class AppStrings
{
    private static readonly ResourceManager Manager =
        new("MapaTur.App.Resources.Localization.AppResources", typeof(AppStrings).Assembly);

    /// <summary>Application window title.</summary>
    public static string AppTitle => Get(nameof(AppTitle));

    /// <summary>"Open MBTiles" button label.</summary>
    public static string OpenMBTiles => Get(nameof(OpenMBTiles));

    /// <summary>"Open Hillshade" button label.</summary>
    public static string OpenHillshade => Get(nameof(OpenHillshade));

    /// <summary>Title of the hillshade picker dialog.</summary>
    public static string FilePickerHillshade => Get(nameof(FilePickerHillshade));

    /// <summary>Status shown after a hillshade archive loads.</summary>
    public static string StatusHillshadeLoaded => Get(nameof(StatusHillshadeLoaded));

    /// <summary>"Open TCX" button label.</summary>
    public static string OpenTcx => Get(nameof(OpenTcx));

    /// <summary>"Download Trails" button label.</summary>
    public static string DownloadTrails => Get(nameof(DownloadTrails));

    /// <summary>"Download Climbing" button label.</summary>
    public static string DownloadClimbing => Get(nameof(DownloadClimbing));

    /// <summary>Status shown while the climbing Overpass query is in flight.</summary>
    public static string StatusDownloadingClimbing => Get(nameof(StatusDownloadingClimbing));

    /// <summary>Format string for the "loaded N climbing areas" status.</summary>
    public static string StatusClimbingLoadedFormat => Get(nameof(StatusClimbingLoadedFormat));

    /// <summary>Status shown when no climbing areas are found in the queried area.</summary>
    public static string StatusNoClimbingFound => Get(nameof(StatusNoClimbingFound));

    /// <summary>"Download POIs" button label.</summary>
    public static string DownloadPois => Get(nameof(DownloadPois));

    /// <summary>Status shown while the POI Overpass query is in flight.</summary>
    public static string StatusDownloadingPois => Get(nameof(StatusDownloadingPois));

    /// <summary>Format string for the "loaded N POIs" status.</summary>
    public static string StatusPoisLoadedFormat => Get(nameof(StatusPoisLoadedFormat));

    /// <summary>Status shown when no POIs are found in the queried area.</summary>
    public static string StatusNoPoisFound => Get(nameof(StatusNoPoisFound));

    /// <summary>"Download Roads" button label.</summary>
    public static string DownloadRoads => Get(nameof(DownloadRoads));

    /// <summary>Status shown while the roads Overpass query is in flight.</summary>
    public static string StatusDownloadingRoads => Get(nameof(StatusDownloadingRoads));

    /// <summary>Format string for the "loaded N roads" status.</summary>
    public static string StatusRoadsLoadedFormat => Get(nameof(StatusRoadsLoadedFormat));

    /// <summary>Status shown when no roads are found in the queried area.</summary>
    public static string StatusNoRoadsFound => Get(nameof(StatusNoRoadsFound));

    /// <summary>"Clear Route" button label.</summary>
    public static string ClearRoute => Get(nameof(ClearRoute));

    /// <summary>"Export GPX" button label.</summary>
    public static string ExportGpx => Get(nameof(ExportGpx));

    /// <summary>Initial status message shown before any data is loaded.</summary>
    public static string StatusInitial => Get(nameof(StatusInitial));

    /// <summary>Status shown after the first waypoint is set.</summary>
    public static string StatusOriginSet => Get(nameof(StatusOriginSet));

    /// <summary>Status shown while route planning is in progress.</summary>
    public static string StatusPlanningRoute => Get(nameof(StatusPlanningRoute));

    /// <summary>Status shown while the Overpass query is in flight.</summary>
    public static string StatusDownloadingTrails => Get(nameof(StatusDownloadingTrails));

    /// <summary>Status shown when no trails are found in the queried area.</summary>
    public static string StatusNoTrailsFound => Get(nameof(StatusNoTrailsFound));

    /// <summary>Format string for the "loaded N trails" status. Use with <see cref="string.Format(IFormatProvider?, string, object?)"/>.</summary>
    public static string StatusTrailsLoadedFormat => Get(nameof(StatusTrailsLoadedFormat));

    /// <summary>Status shown when A* cannot find a path between the two waypoints.</summary>
    public static string StatusNoRouteFound => Get(nameof(StatusNoRouteFound));

    /// <summary>Status shown after the user clears the planned route.</summary>
    public static string StatusRouteCleared => Get(nameof(StatusRouteCleared));

    /// <summary>Status shown when the viewport is not yet ready for a query.</summary>
    public static string StatusViewportNotReady => Get(nameof(StatusViewportNotReady));

    /// <summary>Status shown when the Overpass request times out.</summary>
    public static string StatusOverpassTimeout => Get(nameof(StatusOverpassTimeout));

    /// <summary>Status shown when the user tries to export without a planned route.</summary>
    public static string StatusExportPlanFirst => Get(nameof(StatusExportPlanFirst));

    /// <summary>Status shown when a TCX file has no usable tracks.</summary>
    public static string StatusTcxNoTracks => Get(nameof(StatusTcxNoTracks));

    /// <summary>Status shown when the user picks a file that no longer exists.</summary>
    public static string StatusFileNotFound => Get(nameof(StatusFileNotFound));

    /// <summary>Title of the MBTiles file picker dialog.</summary>
    public static string FilePickerMBTiles => Get(nameof(FilePickerMBTiles));

    /// <summary>Title of the TCX file picker dialog.</summary>
    public static string FilePickerTcx => Get(nameof(FilePickerTcx));

    /// <summary>Screen reader description of the interactive map control.</summary>
    public static string AccessibilityMapControl => Get(nameof(AccessibilityMapControl));

    /// <summary>"Open DEM" button label.</summary>
    public static string OpenDem => Get(nameof(OpenDem));

    /// <summary>"Toggle 3D" button label.</summary>
    public static string Toggle3D => Get(nameof(Toggle3D));

    /// <summary>Status shown after entering 3D mode.</summary>
    public static string Status3DMode => Get(nameof(Status3DMode));

    /// <summary>Status shown after leaving 3D mode.</summary>
    public static string Status2DMode => Get(nameof(Status2DMode));

    /// <summary>Status shown after a DEM file loads.</summary>
    public static string StatusDemLoaded => Get(nameof(StatusDemLoaded));

    /// <summary>Title of the DEM file picker dialog.</summary>
    public static string FilePickerDem => Get(nameof(FilePickerDem));

    /// <summary>Label for the vertical-exaggeration slider in 3D mode.</summary>
    public static string LabelVerticalExaggeration => Get(nameof(LabelVerticalExaggeration));

    /// <summary>Label for the location-tracking button when tracking is OFF (clicking turns it on).</summary>
    public static string ToggleLocationTrackingOn => Get(nameof(ToggleLocationTrackingOn));

    /// <summary>Label for the location-tracking button when tracking is ON (clicking turns it off).</summary>
    public static string ToggleLocationTrackingOff => Get(nameof(ToggleLocationTrackingOff));

    /// <summary>Status line shown immediately after location tracking starts.</summary>
    public static string StatusLocationTrackingStarted => Get(nameof(StatusLocationTrackingStarted));

    /// <summary>Status line shown immediately after location tracking stops.</summary>
    public static string StatusLocationTrackingStopped => Get(nameof(StatusLocationTrackingStopped));

    /// <summary>Status line shown when the user / OS denies location permission.</summary>
    public static string StatusLocationPermissionDenied => Get(nameof(StatusLocationPermissionDenied));

    /// <summary>Dismiss button label on a marker detail popup.</summary>
    public static string PopupClose => Get(nameof(PopupClose));

    /// <summary>Label for the category/type line in a marker popup.</summary>
    public static string PopupCategory => Get(nameof(PopupCategory));

    /// <summary>Label for the elevation line in a marker popup.</summary>
    public static string PopupElevation => Get(nameof(PopupElevation));

    /// <summary>Label for the climbing grade line in a marker popup.</summary>
    public static string PopupGrade => Get(nameof(PopupGrade));

    /// <summary>Label for the route length line in a marker popup.</summary>
    public static string PopupLength => Get(nameof(PopupLength));

    /// <summary>Label for the protection line in a marker popup.</summary>
    public static string PopupProtection => Get(nameof(PopupProtection));

    /// <summary>Popup value shown for a bolted route.</summary>
    public static string PopupBolted => Get(nameof(PopupBolted));

    /// <summary>Popup value shown for a trad route.</summary>
    public static string PopupTrad => Get(nameof(PopupTrad));

    /// <summary>Popup title fallback for an unnamed POI.</summary>
    public static string PopupUnnamedPoi => Get(nameof(PopupUnnamedPoi));

    /// <summary>Popup title fallback for an unnamed climbing area.</summary>
    public static string PopupUnnamedClimbing => Get(nameof(PopupUnnamedClimbing));

    /// <summary>Display name for the "mountain hut" POI kind.</summary>
    public static string PoiKindHut => Get(nameof(PoiKindHut));

    /// <summary>Display name for the "wilderness hut" POI kind.</summary>
    public static string PoiKindWildernessHut => Get(nameof(PoiKindWildernessHut));

    /// <summary>Display name for the "chalet" POI kind.</summary>
    public static string PoiKindChalet => Get(nameof(PoiKindChalet));

    /// <summary>Display name for the "shelter" POI kind.</summary>
    public static string PoiKindShelter => Get(nameof(PoiKindShelter));

    /// <summary>Display name for the "viewpoint" POI kind.</summary>
    public static string PoiKindViewpoint => Get(nameof(PoiKindViewpoint));

    /// <summary>Display name for an unspecified climbing type.</summary>
    public static string ClimbingTypeUnspecified => Get(nameof(ClimbingTypeUnspecified));

    /// <summary>Display name for a sport-climbing route.</summary>
    public static string ClimbingTypeSportRoute => Get(nameof(ClimbingTypeSportRoute));

    /// <summary>Display name for a trad route.</summary>
    public static string ClimbingTypeTradRoute => Get(nameof(ClimbingTypeTradRoute));

    /// <summary>Display name for a multi-pitch route.</summary>
    public static string ClimbingTypeMultiPitch => Get(nameof(ClimbingTypeMultiPitch));

    /// <summary>Display name for a bouldering problem.</summary>
    public static string ClimbingTypeBoulder => Get(nameof(ClimbingTypeBoulder));

    /// <summary>Display name for a crag / sector.</summary>
    public static string ClimbingTypeCrag => Get(nameof(ClimbingTypeCrag));

    /// <summary>Display name for a natural cliff feature.</summary>
    public static string ClimbingTypeCliff => Get(nameof(ClimbingTypeCliff));

    /// <summary>Title of the alert shown after a fly-through MP4 is saved.</summary>
    public static string RecordingSavedTitle => Get(nameof(RecordingSavedTitle));

    /// <summary>Body format for the fly-through-saved alert; {0} is the file path.</summary>
    public static string RecordingSavedFormat => Get(nameof(RecordingSavedFormat));

    /// <summary>Dismiss button label for the fly-through-saved alert.</summary>
    public static string RecordingDismiss => Get(nameof(RecordingDismiss));

    /// <summary>Status shown when route-planning mode is turned on.</summary>
    public static string StatusRoutePlanningOn => Get(nameof(StatusRoutePlanningOn));

    /// <summary>Status shown when route-planning mode is turned off.</summary>
    public static string StatusRoutePlanningOff => Get(nameof(StatusRoutePlanningOff));

    // ── Menu chrome: top tab chips ──
    /// <summary>"Route" tab chip.</summary>
    public static string TabRoute => Get(nameof(TabRoute));
    /// <summary>"Modes" tab chip.</summary>
    public static string TabModes => Get(nameof(TabModes));
    /// <summary>"View" tab chip.</summary>
    public static string TabView => Get(nameof(TabView));
    /// <summary>"Map" tab chip.</summary>
    public static string TabMap => Get(nameof(TabMap));
    /// <summary>"Weather" tab chip.</summary>
    public static string TabWeather => Get(nameof(TabWeather));
    /// <summary>"Settings" tab chip.</summary>
    public static string TabSettings => Get(nameof(TabSettings));

    // ── Find-me / teleport bar ──
    /// <summary>Header of the collapsible find-me / search bar.</summary>
    public static string LocateBarHeader => Get(nameof(LocateBarHeader));
    /// <summary>"Center on me" button.</summary>
    public static string LocateCenterOnMe => Get(nameof(LocateCenterOnMe));
    /// <summary>Placeholder for the teleport search entry.</summary>
    public static string TeleportPlaceholder => Get(nameof(TeleportPlaceholder));
    /// <summary>"Fly" button on a place result.</summary>
    public static string TeleportFly => Get(nameof(TeleportFly));
    /// <summary>"Follow camera (chase from behind)" tracking option toggle.</summary>
    public static string FollowCamera => Get(nameof(FollowCamera));

    // ── Section titles ──
    /// <summary>Route panel title.</summary>
    public static string SectionRouteTitle => Get(nameof(SectionRouteTitle));
    /// <summary>Modes panel title.</summary>
    public static string SectionModesTitle => Get(nameof(SectionModesTitle));
    /// <summary>View panel title.</summary>
    public static string SectionViewTitle => Get(nameof(SectionViewTitle));
    /// <summary>Map panel title.</summary>
    public static string SectionMapTitle => Get(nameof(SectionMapTitle));
    /// <summary>Weather panel title.</summary>
    public static string SectionWeatherTitle => Get(nameof(SectionWeatherTitle));
    /// <summary>Settings panel title.</summary>
    public static string SectionSettingsTitle => Get(nameof(SectionSettingsTitle));

    // ── Route panel ──
    /// <summary>"Route planning" toggle label.</summary>
    public static string RoutePlanningLabel => Get(nameof(RoutePlanningLabel));
    /// <summary>"ADD STOP" sub-header.</summary>
    public static string AddStopHeader => Get(nameof(AddStopHeader));
    /// <summary>Placeholder for the add-stop search entry.</summary>
    public static string SearchPlaceholder => Get(nameof(SearchPlaceholder));
    /// <summary>"ROUTE (STOPS IN ORDER)" sub-header.</summary>
    public static string RouteStopsHeader => Get(nameof(RouteStopsHeader));
    /// <summary>Empty-state hint for the stops list.</summary>
    public static string RouteStopsEmpty => Get(nameof(RouteStopsEmpty));
    /// <summary>"Remove" chip on a route stop.</summary>
    public static string StopRemove => Get(nameof(StopRemove));
    /// <summary>"Show route" button.</summary>
    public static string RouteShow => Get(nameof(RouteShow));
    /// <summary>"Route film" button.</summary>
    public static string RouteFilm => Get(nameof(RouteFilm));
    /// <summary>Short "Clear" route button.</summary>
    public static string RouteClearShort => Get(nameof(RouteClearShort));
    /// <summary>Short "GPX" export button.</summary>
    public static string RouteGpxShort => Get(nameof(RouteGpxShort));

    // ── Modes panel ──
    /// <summary>"Orthophoto" toggle.</summary>
    public static string ToggleOrtho => Get(nameof(ToggleOrtho));
    /// <summary>"Rock" toggle.</summary>
    public static string ToggleRock => Get(nameof(ToggleRock));
    /// <summary>Caption under the "Rock" toggle.</summary>
    public static string ToggleRockHint => Get(nameof(ToggleRockHint));
    /// <summary>"Biomes" toggle.</summary>
    public static string ToggleBiomes => Get(nameof(ToggleBiomes));
    /// <summary>Caption under the "Biomes" toggle.</summary>
    public static string ToggleBiomesHint => Get(nameof(ToggleBiomesHint));
    /// <summary>"Slope map" toggle.</summary>
    public static string ToggleSlopeMap => Get(nameof(ToggleSlopeMap));
    /// <summary>Caption under the "Slope map" toggle.</summary>
    public static string ToggleSlopeMapHint => Get(nameof(ToggleSlopeMapHint));
    /// <summary>"Hiking trails" toggle.</summary>
    public static string ToggleTrails => Get(nameof(ToggleTrails));
    /// <summary>"Roads" toggle.</summary>
    public static string ToggleRoads => Get(nameof(ToggleRoads));
    /// <summary>"Exposed routes" toggle.</summary>
    public static string ToggleExposedRoutes => Get(nameof(ToggleExposedRoutes));
    /// <summary>Caption under the "Exposed routes" toggle.</summary>
    public static string ToggleExposedRoutesHint => Get(nameof(ToggleExposedRoutesHint));
    /// <summary>"TRAIL COLOURS" sub-header.</summary>
    public static string TrailColoursHeader => Get(nameof(TrailColoursHeader));
    /// <summary>Trail colour "Red".</summary>
    public static string ColourRed => Get(nameof(ColourRed));
    /// <summary>Trail colour "Blue".</summary>
    public static string ColourBlue => Get(nameof(ColourBlue));
    /// <summary>Trail colour "Green".</summary>
    public static string ColourGreen => Get(nameof(ColourGreen));
    /// <summary>Trail colour "Yellow".</summary>
    public static string ColourYellow => Get(nameof(ColourYellow));
    /// <summary>Trail colour "Black".</summary>
    public static string ColourBlack => Get(nameof(ColourBlack));
    /// <summary>"LAYERS" sub-header.</summary>
    public static string LayersHeader => Get(nameof(LayersHeader));
    /// <summary>"Trails" layer chip.</summary>
    public static string LayerTrails => Get(nameof(LayerTrails));
    /// <summary>"Peak names" layer chip.</summary>
    public static string LayerPeakNames => Get(nameof(LayerPeakNames));
    /// <summary>"Night sky" layer chip.</summary>
    public static string LayerNightSky => Get(nameof(LayerNightSky));
    /// <summary>"Contours" layer chip.</summary>
    public static string LayerContours => Get(nameof(LayerContours));
    /// <summary>"Range" label for the peak-name radius slider.</summary>
    public static string PeakRangeLabel => Get(nameof(PeakRangeLabel));
    /// <summary>Accessibility hint for the peak-name radius slider.</summary>
    public static string PeakRangeHint => Get(nameof(PeakRangeHint));
    /// <summary>"POINTS (POI)" sub-header.</summary>
    public static string PoiHeader => Get(nameof(PoiHeader));
    /// <summary>POI chip "Mountain huts".</summary>
    public static string PoiHuts => Get(nameof(PoiHuts));
    /// <summary>POI chip "Wilderness huts".</summary>
    public static string PoiWilderness => Get(nameof(PoiWilderness));
    /// <summary>POI chip "Chalets".</summary>
    public static string PoiChalets => Get(nameof(PoiChalets));
    /// <summary>POI chip "Shelters".</summary>
    public static string PoiShelters => Get(nameof(PoiShelters));
    /// <summary>POI chip "Viewpoints".</summary>
    public static string PoiViewpoints => Get(nameof(PoiViewpoints));
    /// <summary>POI chip "Parking".</summary>
    public static string PoiParking => Get(nameof(PoiParking));
    /// <summary>POI chip "Passes".</summary>
    public static string PoiPasses => Get(nameof(PoiPasses));

    // ── Weather panel ──
    /// <summary>"Time" slider label.</summary>
    public static string WeatherTime => Get(nameof(WeatherTime));
    /// <summary>Accessibility hint for the time slider.</summary>
    public static string WeatherTimeHint => Get(nameof(WeatherTimeHint));
    /// <summary>"Clouds" slider label.</summary>
    public static string WeatherClouds => Get(nameof(WeatherClouds));
    /// <summary>Accessibility hint for the clouds slider.</summary>
    public static string WeatherCloudsHint => Get(nameof(WeatherCloudsHint));
    /// <summary>"Wind" slider label.</summary>
    public static string WeatherWind => Get(nameof(WeatherWind));
    /// <summary>Accessibility hint for the wind slider.</summary>
    public static string WeatherWindHint => Get(nameof(WeatherWindHint));
    /// <summary>"Snow" slider label.</summary>
    public static string WeatherSnow => Get(nameof(WeatherSnow));
    /// <summary>Accessibility hint for the snow slider.</summary>
    public static string WeatherSnowHint => Get(nameof(WeatherSnowHint));

    // ── View panel ──
    /// <summary>"Vertical" exaggeration slider label.</summary>
    public static string ViewVertical => Get(nameof(ViewVertical));
    /// <summary>"Reset camera" button.</summary>
    public static string ViewResetCamera => Get(nameof(ViewResetCamera));
    /// <summary>"Fly-through" button.</summary>
    public static string ViewFlythrough => Get(nameof(ViewFlythrough));

    // ── Map panel ──
    /// <summary>"REGIONS" sub-header.</summary>
    public static string MapRegionsHeader => Get(nameof(MapRegionsHeader));
    /// <summary>Region chip "Tatras".</summary>
    public static string RegionTatry => Get(nameof(RegionTatry));
    /// <summary>Region chip "Beskids".</summary>
    public static string RegionBeskidy => Get(nameof(RegionBeskidy));
    /// <summary>Region chip "Pieniny".</summary>
    public static string RegionPieniny => Get(nameof(RegionPieniny));
    /// <summary>Region chip "Bieszczady".</summary>
    public static string RegionBieszczady => Get(nameof(RegionBieszczady));
    /// <summary>"Load 1 m terrain" button.</summary>
    public static string MapLoadTerrain => Get(nameof(MapLoadTerrain));
    /// <summary>"Download all Tatras offline" button.</summary>
    public static string MapDownloadOffline => Get(nameof(MapDownloadOffline));
    /// <summary>"Download data packages" button.</summary>
    public static string MapDownloadPackages => Get(nameof(MapDownloadPackages));
    /// <summary>"DOWNLOAD FOR VIEW" sub-header.</summary>
    public static string MapDownloadForViewHeader => Get(nameof(MapDownloadForViewHeader));
    /// <summary>"ROUTE" sub-header in the Map panel.</summary>
    public static string MapRouteHeader => Get(nameof(MapRouteHeader));

    // ── Settings panel ──
    /// <summary>"RENDER QUALITY" sub-header.</summary>
    public static string SettingsQualityHeader => Get(nameof(SettingsQualityHeader));
    /// <summary>Quality option "Performance".</summary>
    public static string QualityPerformance => Get(nameof(QualityPerformance));
    /// <summary>Quality option "Balanced".</summary>
    public static string QualityBalanced => Get(nameof(QualityBalanced));
    /// <summary>Quality option "High".</summary>
    public static string QualityHigh => Get(nameof(QualityHigh));
    /// <summary>Explanatory caption under the quality selector.</summary>
    public static string QualityHint => Get(nameof(QualityHint));
    /// <summary>"CACHE" sub-header.</summary>
    public static string SettingsCacheHeader => Get(nameof(SettingsCacheHeader));
    /// <summary>"Clear downloaded data" button.</summary>
    public static string SettingsClearCache => Get(nameof(SettingsClearCache));
    /// <summary>"DEBUG" sub-header.</summary>
    public static string SettingsDebugHeader => Get(nameof(SettingsDebugHeader));
    /// <summary>"FPS / stats overlay" toggle.</summary>
    public static string SettingsFpsOverlay => Get(nameof(SettingsFpsOverlay));
    /// <summary>"Verbose logging" toggle.</summary>
    public static string SettingsVerboseLogs => Get(nameof(SettingsVerboseLogs));
    /// <summary>"LOD diagnostics (detail badge)" toggle.</summary>
    public static string SettingsLodDiagnostics => Get(nameof(SettingsLodDiagnostics));
    /// <summary>"LANGUAGE" sub-header.</summary>
    public static string SettingsLanguageHeader => Get(nameof(SettingsLanguageHeader));
    /// <summary>Format for the cache-summary line; {0}=trails, {1}=POI, {2}=climbing.</summary>
    public static string CacheSummaryFormat => Get(nameof(CacheSummaryFormat));
    /// <summary>Status shown after the local cache is cleared.</summary>
    public static string StatusCacheCleared => Get(nameof(StatusCacheCleared));
    /// <summary>Status shown when clearing the cache fails.</summary>
    public static string StatusCacheClearFailed => Get(nameof(StatusCacheClearFailed));

    // ── Status messages (MapPageViewModel) ──
    /// <summary>"Loaded: {0}" (MBTiles).</summary>
    public static string StatusMbtilesLoadedFormat => Get(nameof(StatusMbtilesLoadedFormat));
    /// <summary>"Could not load archive: {0}".</summary>
    public static string StatusArchiveLoadFailedFormat => Get(nameof(StatusArchiveLoadFailedFormat));
    /// <summary>"Could not load hillshade: {0}".</summary>
    public static string StatusHillshadeLoadFailedFormat => Get(nameof(StatusHillshadeLoadFailedFormat));
    /// <summary>"Loaded {0}: {1} km, +{2} m / -{3} m." (TCX track).</summary>
    public static string StatusTcxLoadedFormat => Get(nameof(StatusTcxLoadedFormat));
    /// <summary>"Could not parse TCX: {0}".</summary>
    public static string StatusTcxParseFailedFormat => Get(nameof(StatusTcxParseFailedFormat));
    /// <summary>"Overpass request failed: {0}".</summary>
    public static string StatusOverpassRequestFailedFormat => Get(nameof(StatusOverpassRequestFailedFormat));
    /// <summary>"Could not parse Overpass response: {0}".</summary>
    public static string StatusOverpassParseFailedFormat => Get(nameof(StatusOverpassParseFailedFormat));
    /// <summary>"Start: {0}. Add another stop.".</summary>
    public static string StatusFirstStopFormat => Get(nameof(StatusFirstStopFormat));
    /// <summary>"No trail between “{0}” and “{1}”.".</summary>
    public static string StatusNoTrailBetweenFormat => Get(nameof(StatusNoTrailBetweenFormat));
    /// <summary>"Route: {0} pts · {1} km · +{2} m · ~{3}".</summary>
    public static string StatusRouteSummaryFormat => Get(nameof(StatusRouteSummaryFormat));
    /// <summary>"Could not plan route: {0}".</summary>
    public static string StatusRoutePlanFailedFormat => Get(nameof(StatusRoutePlanFailedFormat));
    /// <summary>"Could not parse DEM: {0}".</summary>
    public static string StatusDemParseFailedFormat => Get(nameof(StatusDemParseFailedFormat));
    /// <summary>"Could not load DEM: {0}".</summary>
    public static string StatusDemLoadFailedFormat => Get(nameof(StatusDemLoadFailedFormat));
    /// <summary>"Online terrain source unavailable".</summary>
    public static string StatusOnlineTerrainUnavailable => Get(nameof(StatusOnlineTerrainUnavailable));
    /// <summary>"Downloading 1 m terrain (Tatras, GUGiK)…".</summary>
    public static string StatusDownloadingTerrain1m => Get(nameof(StatusDownloadingTerrain1m));
    /// <summary>"Downloading 1 m terrain… {0}% ({1}/{2} tiles)".</summary>
    public static string StatusDownloadingTerrain1mProgressFormat => Get(nameof(StatusDownloadingTerrain1mProgressFormat));
    /// <summary>"Could not download terrain (no network/coverage)".</summary>
    public static string StatusTerrainDownloadFailed => Get(nameof(StatusTerrainDownloadFailed));
    /// <summary>"Region terrain download error".</summary>
    public static string StatusRegionTerrainError => Get(nameof(StatusRegionTerrainError));
    /// <summary>Scene label "Tatras 1 m (z{0})".</summary>
    public static string SceneLabelTatra1mFormat => Get(nameof(SceneLabelTatra1mFormat));
    /// <summary>"Offline download unavailable".</summary>
    public static string StatusOfflineUnavailable => Get(nameof(StatusOfflineUnavailable));
    /// <summary>"Downloading Tatras offline…".</summary>
    public static string StatusDownloadingTatraOffline => Get(nameof(StatusDownloadingTatraOffline));
    /// <summary>", {0} skipped" suffix.</summary>
    public static string StatusOfflineSkippedSuffixFormat => Get(nameof(StatusOfflineSkippedSuffixFormat));
    /// <summary>"Downloading Tatras offline… {0}% ({1}/{2}{3})".</summary>
    public static string StatusDownloadingTatraOfflineProgressFormat => Get(nameof(StatusDownloadingTatraOfflineProgressFormat));
    /// <summary>"Downloading Tatras offline z{0}…".</summary>
    public static string StatusDownloadingTatraOfflineZoomFormat => Get(nameof(StatusDownloadingTatraOfflineZoomFormat));
    /// <summary>"Tatras offline ready: {0} tiles …".</summary>
    public static string StatusTatraOfflineDoneFormat => Get(nameof(StatusTatraOfflineDoneFormat));
    /// <summary>"Tatras offline: {0}/{1} tiles …".</summary>
    public static string StatusTatraOfflinePartialFormat => Get(nameof(StatusTatraOfflinePartialFormat));
    /// <summary>"Tatras offline download error".</summary>
    public static string StatusTatraOfflineError => Get(nameof(StatusTatraOfflineError));
    /// <summary>"Package download unavailable".</summary>
    public static string StatusPackagesUnavailable => Get(nameof(StatusPackagesUnavailable));
    /// <summary>"Checking available packages…".</summary>
    public static string StatusCheckingPackages => Get(nameof(StatusCheckingPackages));
    /// <summary>"All data packages are up to date".</summary>
    public static string StatusPackagesUpToDate => Get(nameof(StatusPackagesUpToDate));
    /// <summary>"Downloading {0} ({1}/{2})… {3}%".</summary>
    public static string StatusDownloadingPackageFormat => Get(nameof(StatusDownloadingPackageFormat));
    /// <summary>"Data packages ready: {0} downloaded. Restart…".</summary>
    public static string StatusPackagesReadyFormat => Get(nameof(StatusPackagesReadyFormat));
    /// <summary>"Data package download error".</summary>
    public static string StatusPackagesError => Get(nameof(StatusPackagesError));
    /// <summary>"LOD unavailable".</summary>
    public static string StatusLodUnavailable => Get(nameof(StatusLodUnavailable));
    /// <summary>"Map is loading — try again in a moment".</summary>
    public static string StatusMapLoading => Get(nameof(StatusMapLoading));
    /// <summary>"LOD: 30 m base…".</summary>
    public static string StatusLodBase => Get(nameof(StatusLodBase));
    /// <summary>"LOD: no base (network?)".</summary>
    public static string StatusLodNoBase => Get(nameof(StatusLodNoBase));
    /// <summary>"LOD: 1 m tile…".</summary>
    public static string StatusLodDetail => Get(nameof(StatusLodDetail));
    /// <summary>"LOD: base + 1 m follows the camera (Stage 3)".</summary>
    public static string StatusLodReady => Get(nameof(StatusLodReady));
    /// <summary>"LOD error".</summary>
    public static string StatusLodError => Get(nameof(StatusLodError));
    /// <summary>"Exported GPX to {0}".</summary>
    public static string StatusGpxExportedFormat => Get(nameof(StatusGpxExportedFormat));
    /// <summary>"Could not write GPX file: {0}".</summary>
    public static string StatusGpxExportFailedFormat => Get(nameof(StatusGpxExportFailedFormat));
    /// <summary>"Auto-loaded: {0}".</summary>
    public static string StatusAutoLoadedFormat => Get(nameof(StatusAutoLoadedFormat));

    // ── WiFi confirmation dialogs (MapPage.xaml.cs) ──
    /// <summary>Dialog title "No WiFi".</summary>
    public static string DialogNoWifiTitle => Get(nameof(DialogNoWifiTitle));
    /// <summary>Body: download-all-Tatras size warning; {0}=MB, {1}=tiles.</summary>
    public static string DialogDownloadTatraBodyFormat => Get(nameof(DialogDownloadTatraBodyFormat));
    /// <summary>Body: data-packages size warning.</summary>
    public static string DialogDownloadPackagesBody => Get(nameof(DialogDownloadPackagesBody));
    /// <summary>"Download anyway" accept button.</summary>
    public static string DialogDownloadAnyway => Get(nameof(DialogDownloadAnyway));
    /// <summary>"Cancel" button.</summary>
    public static string DialogCancel => Get(nameof(DialogCancel));

    // ── 3D scene labels ──
    /// <summary>Moon phase label "Moon {0}%".</summary>
    public static string MoonLabelFormat => Get(nameof(MoonLabelFormat));
    /// <summary>POI kind "Parking".</summary>
    public static string PoiKindParking => Get(nameof(PoiKindParking));
    /// <summary>POI kind "Pass".</summary>
    public static string PoiKindPass => Get(nameof(PoiKindPass));

    private static string Get(string key) => Manager.GetString(key) ?? key;
}