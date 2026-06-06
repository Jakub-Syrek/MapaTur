using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

using Mapsui.Nts;
using Mapsui.Projections;

using NetTopologySuite.Geometries;

namespace MapaTur.App.Services;

/// <summary>
/// Shared geometry helper for clipping <see cref="Trail"/> polylines (hiking trails AND roads) to the
/// loaded map coverage, so 2D lines never trail off onto blank space past the basemap. Clipping uses
/// NTS so boundary-crossing lines are cut exactly at the edge (splitting into pieces as needed).
/// </summary>
public static class MapCoverageClipper
{
    /// <summary>Builds the coverage rectangle (Spherical-Mercator) for clipping, or null when no bounds.</summary>
    public static Geometry? BuildClip(MapBounds? coverageBounds)
    {
        if (coverageBounds is not { } b)
        {
            return null;
        }

        var (minX, minY) = SphericalMercator.FromLonLat(b.SouthWest.Longitude, b.SouthWest.Latitude);
        var (maxX, maxY) = SphericalMercator.FromLonLat(b.NorthEast.Longitude, b.NorthEast.Latitude);
        return new GeometryFactory().ToGeometry(new Envelope(minX, maxX, minY, maxY));
    }

    /// <summary>
    /// Projects a trail/road to Spherical-Mercator features, clipped to <paramref name="clip"/> when set.
    /// Yields nothing for degenerate geometry; may yield several pieces where a line crosses the boundary.
    /// </summary>
    public static IEnumerable<GeometryFeature> ToFeatures(Trail line, Geometry? clip)
    {
        var coordinates = line.Geometry
            .Select(point => SphericalMercator.FromLonLat(point.Longitude, point.Latitude))
            .Select(projected => new Coordinate(projected.x, projected.y))
            .ToArray();

        if (coordinates.Length < 2)
        {
            yield break;
        }

        Geometry geometry = new LineString(coordinates);

        if (clip is null)
        {
            yield return new GeometryFeature(geometry);
            yield break;
        }

        Geometry intersection;
        try
        {
            intersection = geometry.Intersection(clip);
        }
        catch (TopologyException)
        {
            intersection = geometry; // rare NTS robustness error → fall back to unclipped
        }

        foreach (LineString piece in ExtractLineStrings(intersection))
        {
            yield return new GeometryFeature(piece);
        }
    }

    private static IEnumerable<LineString> ExtractLineStrings(Geometry geometry)
    {
        if (geometry.IsEmpty)
        {
            yield break;
        }

        switch (geometry)
        {
            case LineString ls when ls.NumPoints >= 2:
                yield return ls;
                break;
            case MultiLineString or GeometryCollection:
                for (int i = 0; i < geometry.NumGeometries; i++)
                {
                    foreach (LineString inner in ExtractLineStrings(geometry.GetGeometryN(i)))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }
}