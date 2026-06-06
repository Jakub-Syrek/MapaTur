using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A half-line in the metric world frame (X east, Y north, Z up): all points
/// <c>Origin + t·Direction</c> for <c>t ≥ 0</c>. <see cref="Direction"/> is expected to be
/// unit length; <see cref="TerrainRaycaster"/> normalises defensively.
/// </summary>
public readonly record struct Ray(Vector3 Origin, Vector3 Direction);