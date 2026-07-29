namespace MapaTur.Application.Terrain;

/// <summary>
/// Rejects complete 32 m pages that contain no opaque rock contribution. It never removes
/// triangles inside a retained page, preserving the continuous DEM-conforming surface.
/// </summary>
public static class ScannedRockPageCoverage
{
    private const int SeamWeightOffset = 15;
    private const byte MinimumVisibleWeight = 96;
    private const float MinimumVisibleFraction = 0.02f;

    public static bool HasVisibleRock(ScannedRockMeshPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        int visible = 0;
        for (int vertex = 0; vertex < page.VertexCount; vertex++)
        {
            int offset = (vertex * ScannedRockMeshPage.VertexStrideBytes) + SeamWeightOffset;
            if (page.VertexData[offset] >= MinimumVisibleWeight)
            {
                visible++;
            }
        }

        return visible > 0 && visible / (float)page.VertexCount >= MinimumVisibleFraction;
    }
}
