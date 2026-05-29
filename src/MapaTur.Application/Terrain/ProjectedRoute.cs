using System.Numerics;

using MapaTur.Domain.Routing;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A planned route projected onto the 3D viewport. Each entry of <see cref="ScreenPoints"/>
/// is either pixel coordinates + NDC depth (X, Y, Z) for an in-frustum polyline vertex,
/// or null for a vertex behind the camera or outside the clip range.
/// </summary>
/// <param name="Source">The originating <see cref="Route"/> — preserved so renderers can read aggregate metadata.</param>
/// <param name="ScreenPoints">Per-polyline-vertex screen positions; null when off-frustum.</param>
public readonly record struct ProjectedRoute(Route Source, IReadOnlyList<Vector3?> ScreenPoints);