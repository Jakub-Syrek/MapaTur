using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// One offline, unified terrain surface before RMP3 page partitioning. Every triangle belongs to exactly one
/// surface: untouched DEM or its rock replacement. <see cref="LegacyPositions"/> retains the corresponding
/// pre-relief DEM point so the baker can enforce the product's bounded-displacement invariant.
/// </summary>
public sealed class HybridTerrainMesh
{
    public const float DefaultMaxReliefMeters = 2.8f;

    public HybridTerrainMesh(
        Vector3[] positions,
        Vector3[] legacyPositions,
        Vector3[] normals,
        Vector2[] orthoUvs,
        byte[] ambientOcclusion,
        byte[] rockBlend,
        ushort[] materialVariants,
        uint[] indices,
        float maxReliefMeters = DefaultMaxReliefMeters)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(legacyPositions);
        ArgumentNullException.ThrowIfNull(normals);
        ArgumentNullException.ThrowIfNull(orthoUvs);
        ArgumentNullException.ThrowIfNull(ambientOcclusion);
        ArgumentNullException.ThrowIfNull(rockBlend);
        ArgumentNullException.ThrowIfNull(materialVariants);
        ArgumentNullException.ThrowIfNull(indices);

        if (positions.Length == 0
            || legacyPositions.Length != positions.Length
            || normals.Length != positions.Length
            || orthoUvs.Length != positions.Length
            || ambientOcclusion.Length != positions.Length
            || rockBlend.Length != positions.Length
            || materialVariants.Length != positions.Length)
        {
            throw new ArgumentException("Every RMP3 vertex attribute must contain exactly one value per position.");
        }

        if (!float.IsFinite(maxReliefMeters) || maxReliefMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(maxReliefMeters));
        }

        for (int i = 0; i < positions.Length; i++)
        {
            if (!IsFinite(positions[i])
                || !IsFinite(legacyPositions[i])
                || !IsFinite(normals[i])
                || !IsFinite(orthoUvs[i]))
            {
                throw new ArgumentOutOfRangeException(nameof(positions), "RMP3 vertex attributes must be finite.");
            }

            if (Vector3.Distance(positions[i], legacyPositions[i]) > maxReliefMeters + 1e-5f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(positions),
                    $"RMP3 vertex {i} exceeds the {maxReliefMeters:F3} m relief limit.");
            }
        }

        if (indices.Length == 0
            || indices.Length % 3 != 0
            || indices.Any(index => index >= positions.Length))
        {
            throw new ArgumentException("RMP3 indices must contain valid complete triangles.", nameof(indices));
        }

        Positions = positions;
        LegacyPositions = legacyPositions;
        Normals = normals;
        OrthoUvs = orthoUvs;
        AmbientOcclusion = ambientOcclusion;
        RockBlend = rockBlend;
        MaterialVariants = materialVariants;
        Indices = indices;
        MaxReliefMeters = maxReliefMeters;
    }

    public Vector3[] Positions { get; }
    public Vector3[] LegacyPositions { get; }
    public Vector3[] Normals { get; }
    public Vector2[] OrthoUvs { get; }
    public byte[] AmbientOcclusion { get; }
    public byte[] RockBlend { get; }
    public ushort[] MaterialVariants { get; }
    public uint[] Indices { get; }
    public float MaxReliefMeters { get; }
    public int VertexCount => Positions.Length;
    public int TriangleCount => Indices.Length / 3;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
}
