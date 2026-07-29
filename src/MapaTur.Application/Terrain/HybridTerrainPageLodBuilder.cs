namespace MapaTur.Application.Terrain;

/// <summary>
/// Builds an RMP3 page LOD by removing triangles and unreferenced vertices only. The RMP2 and RMP3 packed
/// vertex strides are intentionally identical, so the proven border-locked simplifier can preserve the hybrid
/// material byte-for-byte without a second implementation of quantization and compaction.
/// </summary>
public static class HybridTerrainPageLodBuilder
{
    public static HybridTerrainMeshPage Build(
        HybridTerrainMeshPage source,
        byte lod,
        float targetTriangleFraction,
        float maximumGeometricErrorMeters,
        IScannedRockIndexSimplifier simplifier)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Lod != 0)
        {
            throw new ArgumentException("RMP3 parent LODs must be derived from an unsimplified page.", nameof(source));
        }

        var proxy = new ScannedRockMeshPage(
            lod: 0,
            source.PageX,
            source.PageY,
            source.WorldMin,
            source.WorldExtent,
            source.GeometricError,
            materialPageId: 0,
            source.VertexData,
            source.Indices);
        ScannedRockMeshPage simplified = ScannedRockPageLodBuilder.Build(
            proxy,
            lod,
            targetTriangleFraction,
            maximumGeometricErrorMeters,
            simplifier);
        return new HybridTerrainMeshPage(
            simplified.Lod,
            simplified.PageX,
            simplified.PageY,
            simplified.WorldMin,
            simplified.WorldExtent,
            simplified.GeometricError,
            simplified.VertexData,
            simplified.Indices);
    }
}
