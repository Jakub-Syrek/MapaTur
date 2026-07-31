using System.Numerics;

namespace MapaTur.Application.Terrain;

public readonly record struct RockWallClipRegion(
    Vector3 Center,
    Vector3 OutwardNormal,
    float WidthMeters,
    float HeightMeters,
    int Seed = 0);

/// <summary>
/// Clips original scan triangles to one exclusive wall-space region. Interior triangles remain untouched;
/// only triangles crossing a region edge receive interpolated positions, normals and UVs.
/// </summary>
public static class PhotogrammetryRockRegionClipper
{
    private const float InsideTolerance = 1e-4f;

    public static PhotogrammetryRockPrimitive Clip(
        PhotogrammetryRockPrimitive source,
        RockWallClipRegion region)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsFinite(region.Center)
            || !IsFinite(region.OutwardNormal)
            || region.OutwardNormal.LengthSquared() < 0.25f
            || !IsPositive(region.WidthMeters)
            || !IsPositive(region.HeightMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(region));
        }

        Vector3 outward = Vector3.Normalize(region.OutwardNormal);
        Vector3 up = Vector3.UnitZ - (outward * Vector3.Dot(Vector3.UnitZ, outward));
        if (up.LengthSquared() < 0.01f)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Wall normal cannot be vertical.");
        }

        up = Vector3.Normalize(up);
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, outward));
        float centerU = Vector3.Dot(region.Center, tangent);
        float centerV = Vector3.Dot(region.Center, up);
        float minimumU = centerU - (region.WidthMeters * 0.5f);
        float maximumU = centerU + (region.WidthMeters * 0.5f);
        float minimumV = centerV - (region.HeightMeters * 0.5f);
        float maximumV = centerV + (region.HeightMeters * 0.5f);

        var positions = source.Positions.ToList();
        var normals = source.Normals.ToList();
        var texCoords = source.TexCoords.ToList();
        var seamWeights = source.SeamWeights.ToList();
        var indices = new List<uint>(source.Indices.Length);
        var interpolatedIndices = new Dictionary<InterpolatedVertexKey, uint>();
        for (int offset = 0; offset < source.Indices.Length; offset += 3)
        {
            var polygon = new List<ClipVertex>(7)
            {
                FromSource(source, checked((int)source.Indices[offset])),
                FromSource(source, checked((int)source.Indices[offset + 1])),
                FromSource(source, checked((int)source.Indices[offset + 2])),
            };
            polygon = ClipAgainst(polygon, tangent, minimumU, keepGreater: true);
            polygon = ClipAgainst(polygon, tangent, maximumU, keepGreater: false);
            polygon = ClipAgainst(polygon, up, minimumV, keepGreater: true);
            polygon = ClipAgainst(polygon, up, maximumV, keepGreater: false);
            if (polygon.Count < 3)
            {
                continue;
            }

            uint first = GetIndex(polygon[0]);
            for (int vertex = 1; vertex < polygon.Count - 1; vertex++)
            {
                uint second = GetIndex(polygon[vertex]);
                uint third = GetIndex(polygon[vertex + 1]);
                Vector3 cross = Vector3.Cross(
                    positions[checked((int)second)] - positions[checked((int)first)],
                    positions[checked((int)third)] - positions[checked((int)first)]);
                if (cross.LengthSquared() <= 1e-10f)
                {
                    continue;
                }

                indices.Add(first);
                indices.Add(second);
                indices.Add(third);
            }
        }

        if (indices.Count == 0)
        {
            throw new InvalidDataException("The scan does not intersect its assigned rock region.");
        }

        return new PhotogrammetryRockPrimitive(
            positions.ToArray(),
            normals.ToArray(),
            texCoords.ToArray(),
            indices.ToArray(),
            source.BaseColorImageBytes,
            seamWeights.ToArray());

        uint GetIndex(ClipVertex vertex)
        {
            if (vertex.SourceIndex >= 0)
            {
                return checked((uint)vertex.SourceIndex);
            }

            InterpolatedVertexKey key = InterpolatedVertexKey.From(vertex);
            if (interpolatedIndices.TryGetValue(key, out uint found))
            {
                return found;
            }

            uint created = checked((uint)positions.Count);
            positions.Add(vertex.Position);
            normals.Add(vertex.Normal);
            texCoords.Add(vertex.TexCoord);
            seamWeights.Add(vertex.SeamWeight);
            interpolatedIndices.Add(key, created);
            return created;
        }
    }

    private static List<ClipVertex> ClipAgainst(
        IReadOnlyList<ClipVertex> input,
        Vector3 axis,
        float boundary,
        bool keepGreater)
    {
        if (input.Count == 0)
        {
            return [];
        }

        var output = new List<ClipVertex>(input.Count + 1);
        ClipVertex previous = input[^1];
        float previousCoordinate = Vector3.Dot(previous.Position, axis);
        bool previousInside = IsInside(previousCoordinate);
        foreach (ClipVertex current in input)
        {
            float currentCoordinate = Vector3.Dot(current.Position, axis);
            bool currentInside = IsInside(currentCoordinate);
            if (currentInside != previousInside)
            {
                float denominator = currentCoordinate - previousCoordinate;
                float amount = MathF.Abs(denominator) <= 1e-8f
                    ? 0.5f
                    : Math.Clamp((boundary - previousCoordinate) / denominator, 0f, 1f);
                output.Add(Interpolate(previous, current, amount));
            }

            if (currentInside)
            {
                output.Add(current);
            }

            previous = current;
            previousCoordinate = currentCoordinate;
            previousInside = currentInside;
        }

        return output;

        bool IsInside(float coordinate) => keepGreater
            ? coordinate >= boundary - InsideTolerance
            : coordinate <= boundary + InsideTolerance;
    }

    private static ClipVertex Interpolate(ClipVertex start, ClipVertex end, float amount)
    {
        Vector3 normal = Vector3.Lerp(start.Normal, end.Normal, amount);
        normal = normal.LengthSquared() > 1e-10f
            ? Vector3.Normalize(normal)
            : start.Normal;
        return new ClipVertex(
            Vector3.Lerp(start.Position, end.Position, amount),
            normal,
            Vector2.Lerp(start.TexCoord, end.TexCoord, amount),
            (byte)Math.Clamp(
                (int)MathF.Round(start.SeamWeight + ((end.SeamWeight - start.SeamWeight) * amount)),
                byte.MinValue,
                byte.MaxValue),
            SourceIndex: -1);
    }

    private static ClipVertex FromSource(PhotogrammetryRockPrimitive source, int index) => new(
        source.Positions[index],
        source.Normals[index],
        source.TexCoords[index],
        source.SeamWeights[index],
        index);

    private static bool IsPositive(float value) => float.IsFinite(value) && value > 0f;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct ClipVertex(
        Vector3 Position,
        Vector3 Normal,
        Vector2 TexCoord,
        byte SeamWeight,
        int SourceIndex);

    private readonly record struct InterpolatedVertexKey(
        int X,
        int Y,
        int Z,
        int U,
        int V)
    {
        public static InterpolatedVertexKey From(ClipVertex vertex) => new(
            (int)MathF.Round(vertex.Position.X * 100_000f),
            (int)MathF.Round(vertex.Position.Y * 100_000f),
            (int)MathF.Round(vertex.Position.Z * 100_000f),
            (int)MathF.Round(vertex.TexCoord.X * 1_000_000f),
            (int)MathF.Round(vertex.TexCoord.Y * 1_000_000f));
    }
}
