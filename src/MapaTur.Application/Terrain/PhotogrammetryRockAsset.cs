using System.Numerics;

using SharpGLTF.Schema2;

namespace MapaTur.Application.Terrain;

/// <summary>
/// One triangle primitive imported from a photogrammetric glTF. Positions, normals, UVs and indices stay
/// separate so the offline baker can preserve the scan's real topology and material coordinates.
/// </summary>
public sealed class PhotogrammetryRockPrimitive
{
    public PhotogrammetryRockPrimitive(
        Vector3[] positions,
        Vector3[] normals,
        Vector2[] texCoords,
        uint[] indices,
        byte[]? baseColorImageBytes)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(normals);
        ArgumentNullException.ThrowIfNull(texCoords);
        ArgumentNullException.ThrowIfNull(indices);
        if (positions.Length == 0)
        {
            throw new ArgumentException("A rock primitive must contain vertices.", nameof(positions));
        }

        if (normals.Length != positions.Length || texCoords.Length != positions.Length)
        {
            throw new ArgumentException("Rock vertex attributes must have identical counts.");
        }

        if (indices.Length == 0 || indices.Length % 3 != 0 || indices.Any(index => index >= positions.Length))
        {
            throw new ArgumentException("Rock indices must contain valid complete triangles.", nameof(indices));
        }

        Positions = positions;
        Normals = normals;
        TexCoords = texCoords;
        Indices = indices;
        BaseColorImageBytes = baseColorImageBytes;
    }

    public Vector3[] Positions { get; }
    public Vector3[] Normals { get; }
    public Vector2[] TexCoords { get; }
    public uint[] Indices { get; }
    public byte[]? BaseColorImageBytes { get; }
}

/// <summary>CPU representation used only by the offline rock baker and its preview tool.</summary>
public sealed class PhotogrammetryRockAsset
{
    private static readonly ReadSettings LenientRead = new()
    {
        Validation = SharpGLTF.Validation.ValidationMode.TryFix,
    };

    private PhotogrammetryRockAsset(IReadOnlyList<PhotogrammetryRockPrimitive> primitives)
    {
        Primitives = primitives;
    }

    public IReadOnlyList<PhotogrammetryRockPrimitive> Primitives { get; }

    public static PhotogrammetryRockAsset Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ModelRoot model = ModelRoot.Load(path, LenientRead);
        var primitives = new List<PhotogrammetryRockPrimitive>();
        foreach (Mesh mesh in model.LogicalMeshes)
        {
            foreach (MeshPrimitive primitive in mesh.Primitives)
            {
                IList<Vector3>? positions = primitive.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (positions is null || positions.Count == 0)
                {
                    continue;
                }

                IList<Vector3>? sourceNormals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
                IList<Vector2>? sourceUvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                if (sourceNormals is null
                    || sourceUvs is null
                    || sourceNormals.Count != positions.Count
                    || sourceUvs.Count != positions.Count)
                {
                    throw new InvalidDataException(
                        "Photogrammetric rock primitives require POSITION, NORMAL and TEXCOORD_0.");
                }

                uint[] indices = primitive.GetIndices() is { Count: > 0 } sourceIndices
                    ? sourceIndices.ToArray()
                    : Enumerable.Range(0, positions.Count).Select(index => (uint)index).ToArray();
                primitives.Add(new PhotogrammetryRockPrimitive(
                    positions.ToArray(),
                    sourceNormals.ToArray(),
                    sourceUvs.ToArray(),
                    indices,
                    TryReadBaseColor(primitive.Material)));
            }
        }

        if (primitives.Count == 0)
        {
            throw new InvalidDataException("The glTF contains no usable photogrammetric triangle primitive.");
        }

        return new PhotogrammetryRockAsset(primitives);
    }

    private static byte[]? TryReadBaseColor(Material? material)
    {
        try
        {
            SharpGLTF.Memory.MemoryImage? image =
                material?.FindChannel("BaseColor")?.Texture?.PrimaryImage?.Content;
            return image is { Content.Length: > 0 } found ? found.Content.ToArray() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public readonly record struct RockScanPatchPlacement(
    Vector3 Center,
    Vector3 OutwardNormal,
    float HeightMeters);

/// <summary>
/// Fits a real scan to a steep terrain plane using a rigid local frame and uniform scale. Unlike heightfield
/// displacement, the scan's overhangs, ledges and cavities remain full 3D geometry.
/// </summary>
public static class RockScanPatchFitter
{
    public static PhotogrammetryRockPrimitive Fit(
        PhotogrammetryRockPrimitive primitive,
        RockScanPatchPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(primitive);
        if (!float.IsFinite(placement.HeightMeters) || placement.HeightMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(placement), "Patch height must be finite and positive.");
        }

        if (!IsFinite(placement.OutwardNormal) || placement.OutwardNormal.LengthSquared() < 0.25f)
        {
            throw new ArgumentOutOfRangeException(nameof(placement), "Patch normal must be finite and non-zero.");
        }

        Vector3 outward = Vector3.Normalize(placement.OutwardNormal);
        Vector3 up = Vector3.UnitZ - (outward * Vector3.Dot(Vector3.UnitZ, outward));
        if (up.LengthSquared() < 0.01f)
        {
            throw new ArgumentOutOfRangeException(nameof(placement), "Patch normal cannot be vertical.");
        }

        up = Vector3.Normalize(up);
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, outward));
        float minX = primitive.Positions.Min(position => position.X);
        float maxX = primitive.Positions.Max(position => position.X);
        float minY = primitive.Positions.Min(position => position.Y);
        float maxY = primitive.Positions.Max(position => position.Y);
        float minZ = primitive.Positions.Min(position => position.Z);
        float sourceHeight = maxY - minY;
        if (sourceHeight <= 1e-5f)
        {
            throw new ArgumentException("The scan has no vertical extent.", nameof(primitive));
        }

        float scale = placement.HeightMeters / sourceHeight;
        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        var positions = new Vector3[primitive.Positions.Length];
        var normals = new Vector3[primitive.Normals.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 source = primitive.Positions[i];
            positions[i] = placement.Center
                + (tangent * ((source.X - centerX) * scale))
                + (up * ((source.Y - centerY) * scale))
                + (outward * ((source.Z - minZ) * scale));

            Vector3 sourceNormal = primitive.Normals[i];
            Vector3 transformed =
                (tangent * sourceNormal.X) + (up * sourceNormal.Y) + (outward * sourceNormal.Z);
            normals[i] = transformed.LengthSquared() > 1e-10f
                ? Vector3.Normalize(transformed)
                : outward;
        }

        return new PhotogrammetryRockPrimitive(
            positions,
            normals,
            primitive.TexCoords.ToArray(),
            primitive.Indices.ToArray(),
            primitive.BaseColorImageBytes);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
