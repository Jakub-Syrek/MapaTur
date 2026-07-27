using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Materializes a coverage plan as real triangle geometry. Every instance is independently fitted and welded
/// to the DEM wall; only its UVs are redirected into the shared offline atlas.
/// </summary>
public static class RockWallCoverageComposer
{
    public static PhotogrammetryRockPrimitive Compose(
        IReadOnlyList<PhotogrammetryRockPrimitive> variants,
        IReadOnlyList<RockWallCoveragePatch> patches,
        RockWallSurfaceSampler wall,
        float edgeBlendFraction,
        float interiorClearanceMeters,
        int atlasColumns,
        int atlasRows,
        byte[]? atlasBaseColorImageBytes,
        float meshClusterCellMeters = 0f,
        int? internalWarpSeed = null)
    {
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(wall);
        if (variants.Count == 0 || patches.Count == 0)
        {
            throw new ArgumentException("Rock coverage needs scan variants and planned patches.");
        }

        if (atlasColumns <= 0
            || atlasRows <= 0
            || checked(atlasColumns * atlasRows) < variants.Count
            || !float.IsFinite(meshClusterCellMeters)
            || meshClusterCellMeters < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(atlasColumns), "Atlas cannot hold all scan variants.");
        }

        int vertexCapacity = checked(patches.Sum(patch => variants[patch.VariantIndex].Positions.Length));
        int indexCapacity = checked(patches.Sum(patch => variants[patch.VariantIndex].Indices.Length));
        var positions = new List<Vector3>(vertexCapacity);
        var normals = new List<Vector3>(vertexCapacity);
        var texCoords = new List<Vector2>(vertexCapacity);
        var seamWeights = new List<byte>(vertexCapacity);
        var indices = new List<uint>(indexCapacity);
        byte[][] sourceSeamWeights = variants
            .Select(variant => RockWallSurfaceConformer.CalculateSourceSeamWeights(
                variant,
                edgeBlendFraction))
            .ToArray();
        foreach (RockWallCoveragePatch patch in patches)
        {
            if ((uint)patch.VariantIndex >= (uint)variants.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(patches), "Patch references a missing scan variant.");
            }

            PhotogrammetryRockPrimitive variant = variants[patch.VariantIndex];
            if (internalWarpSeed is int baseSeed)
            {
                int patchSeed = Hash(baseSeed, patch.Column, patch.Row, patch.VariantIndex);
                variant = PhotogrammetryRockInternalWarper.Warp(variant, patchSeed);
            }

            PhotogrammetryRockPrimitive fitted = RockScanPatchFitter.Fit(
                variant,
                patch.Placement);
            PhotogrammetryRockPrimitive conformed = RockWallSurfaceConformer.Conform(
                fitted,
                patch.Placement,
                wall,
                edgeBlendFraction,
                interiorClearanceMeters,
                sourceSeamWeights[patch.VariantIndex]);
            if (meshClusterCellMeters > 0f)
            {
                conformed = PhotogrammetryRockMeshClusterer.Cluster(
                    conformed,
                    meshClusterCellMeters);
            }

            uint vertexOffset = checked((uint)positions.Count);
            positions.AddRange(conformed.Positions);
            normals.AddRange(conformed.Normals);
            seamWeights.AddRange(conformed.SeamWeights);
            int atlasColumn = patch.VariantIndex % atlasColumns;
            int atlasRow = patch.VariantIndex / atlasColumns;
            foreach (Vector2 uv in conformed.TexCoords)
            {
                texCoords.Add(new Vector2(
                    (Math.Clamp(uv.X, 0f, 1f) + atlasColumn) / atlasColumns,
                    (Math.Clamp(uv.Y, 0f, 1f) + atlasRow) / atlasRows));
            }

            foreach (uint index in conformed.Indices)
            {
                indices.Add(checked(index + vertexOffset));
            }
        }

        return new PhotogrammetryRockPrimitive(
            positions.ToArray(),
            normals.ToArray(),
            texCoords.ToArray(),
            indices.ToArray(),
            atlasBaseColorImageBytes,
            seamWeights.ToArray());
    }

    private static int Hash(int seed, int column, int row, int variant)
    {
        uint value = unchecked((uint)seed);
        value ^= unchecked((uint)column) * 0x9e3779b9u;
        value = (value ^ (value >> 16)) * 0x7feb352du;
        value ^= unchecked((uint)row) * 0x85ebca6bu;
        value = (value ^ (value >> 15)) * 0x846ca68bu;
        value ^= unchecked((uint)variant) * 0xc2b2ae35u;
        return unchecked((int)(value ^ (value >> 16)));
    }
}
