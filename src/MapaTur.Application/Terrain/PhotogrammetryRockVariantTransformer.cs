using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Produces full-scan orientation variants without cropping or flattening the measured geometry.
/// </summary>
public static class PhotogrammetryRockVariantTransformer
{
    public static PhotogrammetryRockPrimitive MirrorHorizontal(
        PhotogrammetryRockPrimitive source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Vector3[] positions = source.Positions
            .Select(position => new Vector3(-position.X, position.Y, position.Z))
            .ToArray();
        Vector3[] normals = source.Normals
            .Select(normal => new Vector3(-normal.X, normal.Y, normal.Z))
            .ToArray();
        uint[] indices = source.Indices.ToArray();
        for (int index = 0; index < indices.Length; index += 3)
        {
            (indices[index + 1], indices[index + 2]) =
                (indices[index + 2], indices[index + 1]);
        }

        return new PhotogrammetryRockPrimitive(
            positions,
            normals,
            source.TexCoords.ToArray(),
            indices,
            source.BaseColorImageBytes,
            source.SeamWeights.ToArray());
    }
}
