using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Projects adjacent baked DEM tiles into one shared world frame and exposes their top triangles as
/// one source surface. Shared geographic edge samples become identical world positions, allowing the
/// continuous rock builder to weld them instead of treating every DEM tile as a separate patch.
/// </summary>
public static class RockDemRegionAssembler
{
    public static IReadOnlyList<RockMeshTriangle> Assemble(
        IReadOnlyList<BakedDemTile> tiles,
        GeoPoint projectionAnchor)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        if (tiles.Count == 0)
        {
            throw new ArgumentException("At least one baked DEM tile is required.", nameof(tiles));
        }

        int zoom = tiles[0].Zoom;
        var uniqueKeys = new HashSet<DemTileKey>();
        foreach (BakedDemTile tile in tiles)
        {
            ArgumentNullException.ThrowIfNull(tile);
            if (tile.Zoom != zoom)
            {
                throw new ArgumentException(
                    "A rock region cannot mix overlapping DEM LOD levels.",
                    nameof(tiles));
            }

            if (!uniqueKeys.Add(tile.Key))
            {
                throw new ArgumentException($"Duplicate DEM tile {tile.Key}.", nameof(tiles));
            }
        }

        var options = new TerrainMeshOptions
        {
            VerticalExaggeration = 1f,
            SkirtDepthMeters = 0f,
            NormalApronCells = 0,
        };
        var result = new List<RockMeshTriangle>(
            tiles.Sum(tile => Math.Max(0, (tile.Columns - 1) * (tile.Rows - 1) * 2)));
        foreach (BakedDemTile tile in tiles)
        {
            TerrainMesh3D mesh = BakedTileMeshBuilder.Build(
                tile,
                projectionAnchor,
                options,
                skirtDepthMeters: 0f);
            for (int index = 0; index < mesh.Indices.Length; index += 3)
            {
                result.Add(new RockMeshTriangle(
                    mesh.Vertices[mesh.Indices[index]],
                    mesh.Vertices[mesh.Indices[index + 1]],
                    mesh.Vertices[mesh.Indices[index + 2]]));
            }
        }

        return result;
    }
}
