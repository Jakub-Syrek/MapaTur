namespace MapaTur.Application.Terrain;

/// <summary>
/// Replaces explicitly owned DEM triangles with one fitted rock surface. Ownership is expressed as terrain
/// triangle ordinals, making it impossible for this stage to retain the covered DEM and append an overlay.
/// Boundary welding is a separate bake validation and must pass before the composed mesh is persisted.
/// </summary>
public static class HybridTerrainMeshComposer
{
    public static HybridTerrainMesh ReplaceTriangles(
        HybridTerrainMesh terrain,
        HybridTerrainMesh replacement,
        IReadOnlyCollection<int> replacedTerrainTriangles)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentNullException.ThrowIfNull(replacedTerrainTriangles);
        if (replacedTerrainTriangles.Count == 0)
        {
            throw new ArgumentException("A hybrid replacement must own at least one terrain triangle.", nameof(replacedTerrainTriangles));
        }

        var replaced = new HashSet<int>(replacedTerrainTriangles);
        if (replaced.Count != replacedTerrainTriangles.Count
            || replaced.Any(index => index < 0 || index >= terrain.TriangleCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(replacedTerrainTriangles),
                "Replacement triangle ownership must contain unique valid terrain triangle ordinals.");
        }

        HybridTerrainBoundaryValidator.EnsureReplacementWelded(terrain, replacement, replaced);

        var indices = new List<uint>(
            checked(terrain.Indices.Length - (replaced.Count * 3) + replacement.Indices.Length));
        for (int triangle = 0; triangle < terrain.TriangleCount; triangle++)
        {
            if (replaced.Contains(triangle))
            {
                continue;
            }

            int source = triangle * 3;
            indices.Add(terrain.Indices[source]);
            indices.Add(terrain.Indices[source + 1]);
            indices.Add(terrain.Indices[source + 2]);
        }

        uint replacementOffset = checked((uint)terrain.VertexCount);
        indices.AddRange(replacement.Indices.Select(index => checked(index + replacementOffset)));

        return new HybridTerrainMesh(
            terrain.Positions.Concat(replacement.Positions).ToArray(),
            terrain.LegacyPositions.Concat(replacement.LegacyPositions).ToArray(),
            terrain.Normals.Concat(replacement.Normals).ToArray(),
            terrain.OrthoUvs.Concat(replacement.OrthoUvs).ToArray(),
            terrain.AmbientOcclusion.Concat(replacement.AmbientOcclusion).ToArray(),
            terrain.RockBlend.Concat(replacement.RockBlend).ToArray(),
            terrain.MaterialVariants.Concat(replacement.MaterialVariants).ToArray(),
            indices.ToArray(),
            Math.Max(terrain.MaxReliefMeters, replacement.MaxReliefMeters));
    }
}
