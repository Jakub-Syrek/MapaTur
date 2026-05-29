using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A <see cref="TerrainPeak"/> projected onto the 3D viewport. <see cref="ScreenPosition"/> is
/// pixel coordinates + NDC depth (X, Y, Z) when the summit is in-frustum, or null when it falls
/// behind the camera or outside the clip range.
/// </summary>
/// <param name="Source">The originating summit — preserved so renderers can draw its elevation label.</param>
/// <param name="ScreenPosition">Screen position; null when off-frustum.</param>
public readonly record struct ProjectedPeak(TerrainPeak Source, Vector3? ScreenPosition);