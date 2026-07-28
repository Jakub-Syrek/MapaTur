using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Rasterizes the frontmost Z surface of a real scan mesh into a normalized displacement source. Scan assets
/// use X as horizontal, Y as vertical and Z as outward depth, matching <see cref="RockScanPatchFitter"/>.
/// </summary>
public static class PhotogrammetryReliefMapExtractor
{
    public static RockHeightMap Extract(
        PhotogrammetryRockPrimitive primitive,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        if (width < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        float minimumX = primitive.Positions.Min(position => position.X);
        float maximumX = primitive.Positions.Max(position => position.X);
        float minimumY = primitive.Positions.Min(position => position.Y);
        float maximumY = primitive.Positions.Max(position => position.Y);
        float extentX = maximumX - minimumX;
        float extentY = maximumY - minimumY;
        if (extentX <= 1e-6f || extentY <= 1e-6f)
        {
            throw new ArgumentException("Photogrammetry scan has no rasterizable X/Y extent.", nameof(primitive));
        }

        var depth = Enumerable.Repeat(float.NegativeInfinity, checked(width * height)).ToArray();
        for (int triangle = 0; triangle < primitive.Indices.Length; triangle += 3)
        {
            Vector3 a = primitive.Positions[checked((int)primitive.Indices[triangle])];
            Vector3 b = primitive.Positions[checked((int)primitive.Indices[triangle + 1])];
            Vector3 c = primitive.Positions[checked((int)primitive.Indices[triangle + 2])];
            var pa = Project(a, minimumX, minimumY, extentX, extentY, width, height);
            var pb = Project(b, minimumX, minimumY, extentX, extentY, width, height);
            var pc = Project(c, minimumX, minimumY, extentX, extentY, width, height);
            RasterizeTriangle(pa, pb, pc, a.Z, b.Z, c.Z, depth, width, height);
        }

        float[] valid = depth.Where(float.IsFinite).Order().ToArray();
        if (valid.Length < 3)
        {
            throw new InvalidDataException("Photogrammetry scan produced no usable front surface.");
        }

        float lower = Percentile(valid, 0.02f);
        float upper = Percentile(valid, 0.98f);
        if (upper - lower < 1e-5f)
        {
            lower = valid[0];
            upper = valid[^1];
        }

        if (upper - lower < 1e-5f)
        {
            throw new InvalidDataException("Photogrammetry front surface has no useful depth relief.");
        }

        float inverseRange = 1f / (upper - lower);
        for (int i = 0; i < depth.Length; i++)
        {
            float value = float.IsFinite(depth[i]) ? depth[i] : lower;
            depth[i] = Math.Clamp((value - lower) * inverseRange, 0f, 1f);
        }

        return new RockHeightMap(width, height, depth);
    }

    private static Vector2 Project(
        Vector3 position,
        float minimumX,
        float minimumY,
        float extentX,
        float extentY,
        int width,
        int height) =>
        new(
            ((position.X - minimumX) / extentX) * (width - 1),
            ((position.Y - minimumY) / extentY) * (height - 1));

    private static void RasterizeTriangle(
        Vector2 a,
        Vector2 b,
        Vector2 c,
        float depthA,
        float depthB,
        float depthC,
        IList<float> destination,
        int width,
        int height)
    {
        float denominator = Edge(a, b, c);
        if (MathF.Abs(denominator) < 1e-8f)
        {
            return;
        }

        int minimumX = Math.Clamp((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0, width - 1);
        int maximumX = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), 0, width - 1);
        int minimumY = Math.Clamp((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0, height - 1);
        int maximumY = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), 0, height - 1);
        for (int y = minimumY; y <= maximumY; y++)
        {
            for (int x = minimumX; x <= maximumX; x++)
            {
                var point = new Vector2(x, y);
                float wa = Edge(b, c, point) / denominator;
                float wb = Edge(c, a, point) / denominator;
                float wc = 1f - wa - wb;
                if (wa < -0.0001f || wb < -0.0001f || wc < -0.0001f)
                {
                    continue;
                }

                float value = (wa * depthA) + (wb * depthB) + (wc * depthC);
                int index = (y * width) + x;
                destination[index] = MathF.Max(destination[index], value);
            }
        }
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 point) =>
        ((point.X - a.X) * (b.Y - a.Y)) - ((point.Y - a.Y) * (b.X - a.X));

    private static float Percentile(IReadOnlyList<float> sorted, float fraction)
    {
        int index = Math.Clamp((int)MathF.Round((sorted.Count - 1) * fraction), 0, sorted.Count - 1);
        return sorted[index];
    }
}
