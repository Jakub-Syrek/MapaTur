using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A single-point map marker lifted into mesh world space: the originating source object paired with
/// its world-space position. Camera-independent, so it can be cached across frames and only the cheap
/// per-frame screen projection re-runs. Used by <see cref="Marker3DOverlayProjector{TSource, TProjected}"/>.
/// </summary>
/// <typeparam name="TSource">The originating marker type (e.g. <c>ClimbingArea</c>, <c>TerrainPeak</c>).</typeparam>
/// <param name="Source">The originating marker, preserved so renderers can read its metadata.</param>
/// <param name="World">World-space position (X east, Y north, Z up, metres) of the lifted marker.</param>
public readonly record struct MarkerWorldPoint<TSource>(TSource Source, Vector3 World);