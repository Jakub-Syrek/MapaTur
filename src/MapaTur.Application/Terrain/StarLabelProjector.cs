using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>A named star projected to viewport pixel coordinates, ready for the overlay text pass.</summary>
public readonly record struct StarLabel(string Name, float ScreenX, float ScreenY, float Magnitude);

/// <summary>
/// Projects above-horizon named stars to on-screen label positions. Mirrors
/// <see cref="Camera3D.ProjectToScreen(Vector3, Matrix4x4, float, float)"/> exactly — same clip transform and
/// pixel mapping — so each label lands on its GL point sprite (the star pass uses the same clip.xy/clip.w; its
/// z=w far-plane pin only changes depth). Stars below the horizon (Direction.Z &lt;= 0), behind the camera
/// (clip.w &lt;= 0), or outside the viewport are dropped, leaving only the handful actually in frame.
/// </summary>
public static class StarLabelProjector
{
    /// <summary>
    /// Projects each named star direction (world frame: X east, Y north, Z up) to a screen label.
    /// </summary>
    public static IReadOnlyList<StarLabel> Project(
        IReadOnlyList<(Vector3 Direction, float Magnitude, string Name)> stars,
        Matrix4x4 viewProjection, float screenWidth, float screenHeight)
    {
        ArgumentNullException.ThrowIfNull(stars);
        if (screenWidth <= 0f || screenHeight <= 0f)
        {
            return Array.Empty<StarLabel>();
        }

        var labels = new List<StarLabel>();
        for (int i = 0; i < stars.Count; i++)
        {
            (Vector3 dir, float magnitude, string name) = stars[i];
            if (dir.Z <= 0f)
            {
                continue; // below the horizon — never labelled
            }

            // Direction at infinity (w=0): same clip.xy/clip.w the GL star shader uses, so the label tracks
            // its point sprite. No depth (ndcZ) check — a direction always lands on the far plane.
            Vector4 clip = Vector4.Transform(new Vector4(dir, 0f), viewProjection);
            if (clip.W <= 0f)
            {
                continue; // behind the camera
            }

            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            if (ndcX < -1f || ndcX > 1f || ndcY < -1f || ndcY > 1f)
            {
                continue; // outside the viewport
            }

            float screenX = (ndcX + 1f) * 0.5f * screenWidth;
            float screenY = (1f - ndcY) * 0.5f * screenHeight;
            labels.Add(new StarLabel(name, screenX, screenY, magnitude));
        }

        return labels;
    }

    /// <summary>
    /// Convenience overload for the renderer host: computes each catalog star's world direction for the local
    /// wall-clock date/hour (CET, DST-aware — see <see cref="NightSky.StarDirectionsForLocalDate"/>), then
    /// projects the named ones. Unnamed catalog entries are skipped.
    /// </summary>
    public static IReadOnlyList<StarLabel> ProjectForLocalDate(
        IReadOnlyList<Star> stars, int year, int month, int day, double localHour,
        double latitudeDegrees, double longitudeDegrees,
        Matrix4x4 viewProjection, float screenWidth, float screenHeight)
    {
        ArgumentNullException.ThrowIfNull(stars);

        IReadOnlyList<(Vector3 Direction, float Magnitude)> dirs = NightSky.StarDirectionsForLocalDate(
            stars, year, month, day, localHour, latitudeDegrees, longitudeDegrees);

        var named = new List<(Vector3, float, string)>(stars.Count);
        for (int i = 0; i < stars.Count; i++)
        {
            if (stars[i].Name is { Length: > 0 } name)
            {
                named.Add((dirs[i].Direction, dirs[i].Magnitude, name));
            }
        }

        return Project(named, viewProjection, screenWidth, screenHeight);
    }
}