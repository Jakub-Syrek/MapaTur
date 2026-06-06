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
}