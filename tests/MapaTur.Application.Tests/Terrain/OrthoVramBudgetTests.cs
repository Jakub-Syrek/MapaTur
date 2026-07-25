using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class OrthoVramBudgetTests
{
    // The bundled Tatry ortho is a 4×2 grid of 8192×5462 RGBA8 cells. Each needs ~179 MB plus a
    // ~33% mip chain ≈ 238 MB resident. These two cases pin the regression that produced the
    // "light-green" (un-textured) tiles: a 1 GB budget only held 4 of the 8 cells, so the rest
    // were evicted and drew the hypsometric green tint. A budget that covers all 8 keeps them all.
    private const int CellWidth = 8192;
    private const int CellHeight = 5462;
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void CellResidentBytes_IncludesMipChain()
    {
        long baseBytes = (long)CellWidth * CellHeight * 4L;

        long resident = OrthoVramBudget.CellResidentBytes(CellWidth, CellHeight);

        // base + ~33% mip chain.
        resident.Should().Be(baseBytes + (baseBytes / 3L));
    }

    [Fact]
    public void MaxResidentCells_OneGigabyteBudget_CapsEightCellsToFour()
    {
        long perCell = OrthoVramBudget.CellResidentBytes(CellWidth, CellHeight);

        int fit = OrthoVramBudget.MaxResidentCells(perCell, cellCount: 8, budgetBytes: 1 * Gb);

        fit.Should().Be(4);
    }

    [Fact]
    public void MaxResidentCells_ThreeGigabyteBudget_HoldsAllEightCells()
    {
        long perCell = OrthoVramBudget.CellResidentBytes(CellWidth, CellHeight);

        int fit = OrthoVramBudget.MaxResidentCells(perCell, cellCount: 8, budgetBytes: 3 * Gb);

        fit.Should().Be(8);
    }

    [Fact]
    public void MaxResidentCells_NeverExceedsCellCount()
    {
        long perCell = OrthoVramBudget.CellResidentBytes(256, 256); // tiny cell

        int fit = OrthoVramBudget.MaxResidentCells(perCell, cellCount: 8, budgetBytes: 64 * Gb);

        fit.Should().Be(8);
    }

    [Fact]
    public void MaxResidentCells_AlwaysAtLeastOneWhenCellsExist()
    {
        long perCell = OrthoVramBudget.CellResidentBytes(CellWidth, CellHeight);

        // Budget smaller than a single cell still keeps one resident (can't render a hole).
        int fit = OrthoVramBudget.MaxResidentCells(perCell, cellCount: 8, budgetBytes: 1);

        fit.Should().Be(1);
    }

    [Fact]
    public void MaxResidentCells_UnknownPerCellSize_ReturnsCellCount()
    {
        int fit = OrthoVramBudget.MaxResidentCells(perCellBytes: 0, cellCount: 8, budgetBytes: 1 * Gb);

        fit.Should().Be(8);
    }

    [Fact]
    public void MaxResidentCells_NoCells_ReturnsZero()
    {
        int fit = OrthoVramBudget.MaxResidentCells(perCellBytes: 123, cellCount: 0, budgetBytes: 1 * Gb);

        fit.Should().Be(0);
    }

    // Shared-budget detail cap: the 8 base cells (~1.9 GB) and the 4096² detail cells (~89 MB) draw from ONE
    // 3 GB budget. After the base, ~1.14 GB is free → ~14 detail cells; a smaller hard cap clamps it.
    private static long BaseEightCellsBytes => 8 * OrthoVramBudget.CellResidentBytes(CellWidth, CellHeight);
    private static long DetailCellBytes => OrthoVramBudget.CellResidentBytes(4096, 4096);

    [Fact]
    public void SharedDetailNearCap_AfterTheBase_FitsTheFreeBudget()
    {
        int cap = OrthoVramBudget.SharedDetailNearCap(BaseEightCellsBytes, DetailCellBytes, 3 * Gb, hardCap: 20);

        cap.Should().Be(14); // (3 GB − 1.9 GB) / 89 MB
    }

    [Fact]
    public void SharedDetailNearCap_IsClampedByTheHardCap()
    {
        int cap = OrthoVramBudget.SharedDetailNearCap(BaseEightCellsBytes, DetailCellBytes, 3 * Gb, hardCap: 9);

        cap.Should().Be(9);
    }

    [Fact]
    public void SharedDetailNearCap_WhenBaseFillsTheBudget_IsZero()
    {
        // No room left → detail off (base owns the frame), NOT forced to 1 like MaxResidentCells.
        int cap = OrthoVramBudget.SharedDetailNearCap(3 * Gb, DetailCellBytes, 3 * Gb, hardCap: 12);

        cap.Should().Be(0);
    }

    [Fact]
    public void SharedDetailNearCap_UnknownCellSize_IsZero()
    {
        OrthoVramBudget.SharedDetailNearCap(BaseEightCellsBytes, 0, 3 * Gb, hardCap: 12).Should().Be(0);
    }
}