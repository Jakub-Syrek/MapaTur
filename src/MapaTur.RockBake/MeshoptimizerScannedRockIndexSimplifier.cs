using MapaTur.Application.Terrain;

using MeshOptimizer;

namespace MapaTur.RockBake;

/// <summary>
/// Offline adapter over meshoptimizer's topology-preserving QEM simplifier. It uses absolute metre
/// error and border locking; no permissive, sloppy, pruning or regularization modes are enabled.
/// </summary>
public sealed class MeshoptimizerScannedRockIndexSimplifier : IScannedRockIndexSimplifier
{
    public unsafe ScannedRockIndexSimplification Simplify(
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<float> positions,
        int vertexCount,
        ScannedRockSimplificationRequest request)
    {
        if (indices.Length == 0 || indices.Length % 3 != 0)
        {
            throw new ArgumentException("A complete triangle list is required.", nameof(indices));
        }

        if (vertexCount <= 0 || positions.Length != vertexCount * 3)
        {
            throw new ArgumentException("Positions must contain one tightly packed float3 per vertex.", nameof(positions));
        }

        if (request.TargetIndexCount <= 0
            || request.TargetIndexCount > indices.Length
            || request.TargetIndexCount % 3 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var options = SimplificationOptions.None;
        if (request.LockBorder)
        {
            options |= SimplificationOptions.SimplifyLockBorder;
        }

        if (request.ErrorIsAbsolute)
        {
            options |= SimplificationOptions.meshopt_SimplifyErrorAbsolute;
        }

        var destination = new uint[indices.Length];
        float resultError;
        nuint resultCount;
        // Alimer 1.2.1's Span overload forwards vertexPositions.Length (float count) as vertex_count.
        // Call the generated native entry point directly so float3 data reports the actual vertex count.
        fixed (uint* destinationPointer = destination)
        fixed (uint* indexPointer = indices)
        fixed (float* positionPointer = positions)
        {
            resultCount = Meshopt.Simplify(
                destinationPointer,
                indexPointer,
                checked((nuint)indices.Length),
                positionPointer,
                checked((nuint)vertexCount),
                vertex_positions_stride: sizeof(float) * 3,
                target_index_count: checked((nuint)request.TargetIndexCount),
                request.MaximumGeometricErrorMeters,
                options,
                &resultError);
        }

        Array.Resize(ref destination, checked((int)resultCount));
        return new ScannedRockIndexSimplification(destination, resultError);
    }
}
