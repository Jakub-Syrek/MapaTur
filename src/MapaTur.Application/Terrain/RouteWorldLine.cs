using System.Numerics;

using MapaTur.Domain.Routing;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A planned route's polyline already lifted to DEM elevation and converted into mesh world space
/// (X east, Y north, Z up). This is the camera-independent half of route projection: it depends
/// only on the route geometry, the DEM and the mesh, so it can be computed once and reused for
/// every frame while the camera orbits. <see cref="Route3DWorldProjection.ToScreen"/> turns it into
/// per-frame <see cref="ProjectedRoute"/> screen points.
/// </summary>
/// <param name="Source">The originating <see cref="Route"/> — preserved so renderers can read aggregate metadata.</param>
/// <param name="World">Per-polyline-vertex world-space positions in metres.</param>
public readonly record struct RouteWorldLine(Route Source, IReadOnlyList<Vector3> World);