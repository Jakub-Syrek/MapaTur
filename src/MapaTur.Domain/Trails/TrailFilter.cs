namespace MapaTur.Domain.Trails;

/// <summary>
/// User-driven inclusion predicate for trails. A trail is shown when both
/// <see cref="IsColourEnabled"/> for its <see cref="Trail.PrimaryColor"/> is true
/// and (if any regions are configured) its bbox intersects at least one of
/// <see cref="EnabledRegions"/>.
/// </summary>
public sealed class TrailFilter
{
    /// <summary>Set of enabled PTTK colours. Empty set hides every trail.</summary>
    public HashSet<PttkColor> EnabledColours { get; } = new();

    /// <summary>
    /// Regions whose bbox a trail must intersect to be visible. Empty list = no
    /// region constraint (trails everywhere are eligible).
    /// </summary>
    public List<MapaTur.Domain.Geography.MapBounds> EnabledRegions { get; } = new();

    /// <summary>True when the trail's colour is in <see cref="EnabledColours"/>.</summary>
    public bool IsColourEnabled(Trail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        return EnabledColours.Contains(trail.PrimaryColor);
    }

    /// <summary>
    /// Returns true if the trail passes the colour and region tests. A trail with
    /// zero geometry vertices fails the region test when any region is configured.
    /// </summary>
    public bool IsVisible(Trail trail)
    {
        ArgumentNullException.ThrowIfNull(trail);
        if (!EnabledColours.Contains(trail.PrimaryColor))
        {
            return false;
        }
        if (EnabledRegions.Count == 0)
        {
            return true;
        }
        if (trail.Geometry.Count == 0)
        {
            return false;
        }

        // Cheap bbox-vs-bbox: trail eligible if its lon/lat box intersects ANY
        // enabled region's box.
        double minLon = trail.Geometry[0].Longitude;
        double maxLon = minLon;
        double minLat = trail.Geometry[0].Latitude;
        double maxLat = minLat;
        for (int i = 1; i < trail.Geometry.Count; i++)
        {
            var p = trail.Geometry[i];
            if (p.Longitude < minLon) minLon = p.Longitude;
            if (p.Longitude > maxLon) maxLon = p.Longitude;
            if (p.Latitude < minLat) minLat = p.Latitude;
            if (p.Latitude > maxLat) maxLat = p.Latitude;
        }

        foreach (var region in EnabledRegions)
        {
            if (maxLon >= region.SouthWest.Longitude
                && minLon <= region.NorthEast.Longitude
                && maxLat >= region.SouthWest.Latitude
                && minLat <= region.NorthEast.Latitude)
            {
                return true;
            }
        }
        return false;
    }
}