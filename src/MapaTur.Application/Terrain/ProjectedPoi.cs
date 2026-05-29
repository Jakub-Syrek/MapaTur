using System.Numerics;

using MapaTur.Domain.Pois;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A <see cref="MountainPoi"/> projected onto the 3D viewport. <see cref="ScreenPosition"/> is pixel
/// coordinates + NDC depth (X, Y, Z) when in-frustum, or null when behind the camera / off-clip.
/// </summary>
/// <param name="Source">The originating POI — preserved so renderers can draw its glyph + name.</param>
/// <param name="ScreenPosition">Screen position; null when off-frustum.</param>
public readonly record struct ProjectedPoi(MountainPoi Source, Vector3? ScreenPosition);