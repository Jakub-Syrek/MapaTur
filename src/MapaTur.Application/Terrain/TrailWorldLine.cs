using System.Numerics;

using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A trail's vertices already lifted to DEM elevation and converted into mesh world space
/// (X east, Y north, Z up). This is the camera-independent half of trail projection: it depends
/// only on the trail geometry, the DEM and the mesh, so it can be computed once and reused for
/// every frame while the camera orbits. <see cref="Trail3DWorldProjection.ToScreen"/> turns it
/// into per-frame <see cref="ProjectedTrail"/> screen points.
/// </summary>
/// <param name="Source">The originating <see cref="Trail"/> — preserved so renderers can read colour/name metadata.</param>
/// <param name="World">Per-geometry-vertex world-space positions in metres.</param>
public readonly record struct TrailWorldLine(Trail Source, IReadOnlyList<Vector3> World);