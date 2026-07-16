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

    /// <summary>
    /// Optional opaque ARGB tint blended into the hypsometric base colour (before shading), in [0,1] strength
    /// via <see cref="OverlayTintStrength"/>. Null = no tint. Used to make LOD detail tiles visually
    /// distinct from the base for diagnostics / debugging the overlay.
    /// </summary>
    public uint? OverlayTintArgb { get; init; }

    /// <summary>How strongly <see cref="OverlayTintArgb"/> replaces the base colour, in [0,1].</summary>
    public float OverlayTintStrength { get; init; } = 0.5f;

    /// <summary>
    /// Optional vertical skirt depth (metres) hung down from each tile's perimeter. 0 (default) = no skirt.
    /// A skirt fills the crack between adjacent tiles of DIFFERENT resolution (per-tile LOD, Model 1) with
    /// real terrain-coloured geometry, so seams don't show through to the base/sky. Adds perimeter vertices,
    /// so keep <c>maxTileSide ≤ ~250</c> to stay under the 16-bit index limit.
    /// </summary>
    public float SkirtDepthMeters { get; init; }

    /// <summary>
    /// Radius (in cells, ≥ 1) of the central difference used for per-vertex normals. 1 (default) samples the
    /// immediate neighbours; a larger radius low-passes the NORMAL field over a wider baseline so dense 1 m
    /// detail shades softer (fewer hard facets) WITHOUT touching the elevation data — heights stay the source
    /// of truth, only the lighting normals are smoothed. A planar slope still reads its true normal at any
    /// radius (only curvature is averaged out).
    /// </summary>
    public int NormalSmoothingRadius { get; init; } = 1;

    /// <summary>
    /// Width (in cells, ≥ 0) of a HALO ring the raster carries around the region actually being drawn: those cells
    /// are read when computing normals but are NOT emitted as vertices. 0 (default) = no halo, the whole raster is
    /// drawn — every existing path meshes exactly as before.
    ///
    /// A raster meshed without a halo has to CLAMP the normal's central difference at its own edge, so the edge
    /// vertex gets a one-sided normal. That is invisible for one big raster (its edge is the map's edge), but a
    /// baked pyramid tile IS its own raster: the clamp lands on the TILE border, and the neighbouring tile — which
    /// meshes that same welded, bit-identical ground point as its own interior — computes a different normal there.
    /// The shader lights straight off the normal, so the disagreement draws a bright/dark line along every tile
    /// border: the "tile grid" visible in the geometry on smooth slopes with the ortho overlay off. Giving the
    /// mesher one ring of the neighbours' real cells and withholding it from the output removes the clamp, so both
    /// tiles compute the SAME normal at the shared point and the line has nothing to draw.
    ///
    /// Must leave at least 2 interior cells per axis. <see cref="NormalSmoothingRadius"/> 1 needs an apron of 1.
    /// </summary>
    public int NormalApronCells { get; init; }
}