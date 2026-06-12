using System.Collections.Generic;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A named mountain lake: its OSM <c>natural=water</c> outline (geographic ring, no closing duplicate)
/// and the recorded water-surface elevation. Source: OSM water polygons in the Tatras.
/// </summary>
public sealed record MountainLake(string Name, double ElevationMeters, IReadOnlyList<GeoPoint> Outline)
{
    /// <summary>Average of the outline vertices — used to test which loaded terrain a lake belongs to.</summary>
    public GeoPoint Centroid
    {
        get
        {
            double lat = 0, lon = 0;
            for (int i = 0; i < Outline.Count; i++) { lat += Outline[i].Latitude; lon += Outline[i].Longitude; }
            int n = Outline.Count > 0 ? Outline.Count : 1;
            return new GeoPoint(lat / n, lon / n);
        }
    }
}

/// <summary>
/// Bundled table of Tatra lakes (name, elevation, outline) and selection by loaded-terrain bounds.
/// The data half (<c>All</c>) lives in MountainLakeData.Lakes.g.cs, GENERATED from OSM by
/// testdata/maps/fetch-tatra-lakes.py — the old hand-pasted 12-lake table silently left every other
/// tarn (Czarny Staw Gąsienicowy, Hincovo, Štrbské, …) without any water effects. Elevation comes
/// from the OSM <c>ele</c> tag, or the lake centroid sampled from the local DEM when untagged.
/// </summary>
public static partial class MountainLakeData
{
    /// <summary>Lakes whose centroid falls within the given loaded-terrain bounds (so water never floats
    /// over un-loaded ground). In the default Beskidy view this is empty; in the Tatra LOD demo it returns
    /// every bundled tarn.</summary>
    public static IEnumerable<MountainLake> WithinBounds(MapBounds bounds)
    {
        foreach (MountainLake lake in All)
        {
            if (bounds.Contains(lake.Centroid)) { yield return lake; }
        }
    }
}