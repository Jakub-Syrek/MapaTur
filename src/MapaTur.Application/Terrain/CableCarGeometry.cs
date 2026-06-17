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

    /// <summary>
    /// Position parameter t∈[0,1] of moving cabin <paramref name="index"/> of <paramref name="count"/> on a
    /// span at time <paramref name="seconds"/>. A triangle (ping-pong) wave: a cabin shuttles lower→upper→lower
    /// like a real reversible cable car. Cabins are phase-offset by a whole half-cycle each (2/count), so for
    /// count=2 one runs up while the other runs down and they cross at mid-span. <paramref name="speed"/> is
    /// one-way trips per second (e.g. 0.05 ⇒ a 20 s ascent). Always returns a value in [0,1].
    /// </summary>
    public static float CabinParameter(double seconds, double speed, int index, int count)
    {
        double offset = count > 0 ? index * (2.0 / count) : 0.0;
        double x = (seconds * speed) + offset;
        double m = ((x % 2.0) + 2.0) % 2.0; // wrap into [0,2)
        return (float)(1.0 - Math.Abs(m - 1.0)); // 0 → 1 → 0 over each period of 2
    }
}