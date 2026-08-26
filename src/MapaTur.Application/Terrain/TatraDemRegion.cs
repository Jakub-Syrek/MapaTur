using MapaTur.Domain.Geography;
using MapaTur.Domain.Regions;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Extent + tile budget for the "Wczytaj teren Tatry" region load: the High Tatra core around Morskie
/// Oko (Rysy, Czarny Staw, Mięguszowieckie Szczyty), kept just north of the Polish/Slovak border
/// (~49.179°) so GUGiK NMT 1 m has full coverage. The budget is sized for the best-quality-AND-large
/// goal: at the source's 256-px tiles, 76 × 256² ≈ 4.98 M samples sits just under the Android mesh
/// vertex cap (5 M), so the planner picks the sharpest zoom (z16 ≈ 1.5 m/px, near native 1 m) over the
/// largest area the renderer can show without decimating it (subsample step stays 1).
/// </summary>
public static class TatraDemRegion
{
    // P-A (rejestr regionów): fasada nad wpisem "tatry" — wartości pinuje MountainRegionsTests,
    // call-sites zostały nietknięte. Nowy kod czyta MountainRegions.Default.DemLoad wprost.
    /// <summary>High Tatra core (~3.1 × 2.7 km), south edge just north of the border.</summary>
    public static MapBounds Bounds => MountainRegions.Tatry.DemLoad.Bounds;

    /// <summary>Tile budget. 76 × 256² ≈ 4.98 M ≤ the 5 M Android vertex cap ⇒ the renderer never decimates.</summary>
    public static int MaxTiles => MountainRegions.Tatry.DemLoad.MaxTiles;

    /// <summary>Lowest fallback zoom if even it overflows the budget (a far coarser, smaller mesh).</summary>
    public static int MinZoom => MountainRegions.Tatry.DemLoad.MinZoom;

    /// <summary>Highest zoom; z16 ≈ 1.5 m/px at 49° N, the closest tier to GUGiK's native 1 m.</summary>
    public static int MaxZoom => MountainRegions.Tatry.DemLoad.MaxZoom;
}