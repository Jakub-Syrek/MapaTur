using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Geometry helpers for an aerialway span (e.g. the Kasprowy Wierch cable car). The cable hangs from the
/// lower to the upper station with a sag that peaks at mid-span; <see cref="PointOnSpan"/> gives the world
/// point at a parameter t∈[0,1] (used by a moving cabin) and <see cref="SampleCable"/> samples the whole
/// curve into a polyline (used to draw the cable). World frame matches the renderer: +Z is up, so the sag
/// is subtracted from Z. A parabolic droop approximates the catenary closely for the small relative sags
/// of a cable-car span, without the cosh() of a true catenary.
/// </summary>
public static class CableCarGeometry
{
    /// <summary>
    /// World point on the sagging cable at parameter <paramref name="t"/> (0 = lower station, 1 = upper).
    /// The XY follows the straight chord; Z is the chord Z minus a parabolic droop that is
    /// <paramref name="sagMeters"/> at mid-span and 0 at both stations (the term 4·t·(1−t)).
    /// </summary>
    public static Vector3 PointOnSpan(Vector3 lower, Vector3 upper, float sagMeters, float t)
    {
        Vector3 chord = Vector3.Lerp(lower, upper, t);
        float droop = sagMeters * 4f * t * (1f - t);
        return new Vector3(chord.X, chord.Y, chord.Z - droop);
    }

    /// <summary>
    /// Samples the cable into <paramref name="segments"/>+1 points from the lower to the upper station,
    /// including both endpoints. <paramref name="segments"/> is clamped to at least 1.
    /// </summary>
    public static IReadOnlyList<Vector3> SampleCable(Vector3 lower, Vector3 upper, float sagMeters, int segments)
    {
        int n = Math.Max(1, segments);
        var points = new Vector3[n + 1];
        for (int i = 0; i <= n; i++)
        {
            points[i] = PointOnSpan(lower, upper, sagMeters, (float)i / n);
        }

        return points;
    }
}