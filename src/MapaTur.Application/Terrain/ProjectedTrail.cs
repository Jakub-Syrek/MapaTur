using System.Numerics;

using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A trail projected onto the 3D viewport. Each entry of <see cref="ScreenPoints"/>
/// is either pixel coordinates + NDC depth (X, Y, Z) for an in-frustum vertex,
/// or null for a vertex behind the camera or outside the clip range.
/// </summary>
/// <param name="Source">The originating <see cref="Trail"/> — preserved so renderers can read colour/name metadata.</param>
/// <param name="ScreenPoints">Per-geometry-vertex screen positions; null when off-frustum.</param>
public readonly record struct ProjectedTrail(Trail Source, IReadOnlyList<Vector3?> ScreenPoints);