using System.Numerics;

using MapaTur.Domain.Climbing;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A climbing area projected onto the 3D viewport. <see cref="ScreenPosition"/> is
/// pixel coordinates + NDC depth (X, Y, Z) when the area is in-frustum, or null when
/// it falls behind the camera or outside the clip range.
/// </summary>
/// <param name="Source">The originating <see cref="ClimbingArea"/> — preserved so renderers can read name/type/grade metadata.</param>
/// <param name="ScreenPosition">Screen position; null when off-frustum.</param>
public readonly record struct ProjectedClimbingArea(ClimbingArea Source, Vector3? ScreenPosition);