using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Tuning knobs for <see cref="TerrainMesh3D.Build(MapaTur.Domain.Terrain.DemRaster, TerrainMeshOptions)"/>.
/// </summary>
public sealed class TerrainMeshOptions
{
    /// <summary>Multiplier applied to Z (elevation) when projecting to world meters. 1.0 = true scale.</summary>
    public float VerticalExaggeration { get; init; } = 2.0f;

    /// <summary>
    /// Direction toward the light source in world space (X east, Y north, Z up).
    /// Default = NW sun at 45° elevation (cartographic convention).
    /// </summary>
    public Vector3 LightDirection { get; init; } = Vector3.Normalize(new Vector3(-0.5f, 0.5f, 0.707f));

    /// <summary>Ambient term added to Lambert shading, in [0,1].</summary>
    public float AmbientFactor { get; init; } = 0.35f;
}