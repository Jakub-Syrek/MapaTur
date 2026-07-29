using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainBakeBudgetEstimatorTests
{
    [Fact]
    public void should_scale_output_and_temporary_space_from_measured_pilot()
    {
        // Arrange
        var pilot = new HybridTerrainBakePilotMeasurement(
            CoveredAreaSquareMeters: 1_000,
            FinalPayloadBytes: 100_000_000,
            PeakTemporaryBytes: 40_000_000);

        // Act
        HybridTerrainBakeBudgetEstimate estimate = HybridTerrainBakeBudgetEstimator.Estimate(
            pilot,
            fullAreaSquareMeters: 10_000,
            currentFreeBytes: 5_000_000_000,
            minimumReserveBytes: 1_000_000_000);

        // Assert
        estimate.Should().BeEquivalentTo(new
        {
            EstimatedFinalBytes = 1_000_000_000L,
            EstimatedPeakTemporaryBytes = 400_000_000L,
            EstimatedFreeBytesAtPeak = 3_600_000_000L,
            EstimatedFreeBytesAfterBake = 4_000_000_000L,
            MeetsReserve = true,
        });
    }

    [Fact]
    public void should_fail_gate_when_final_package_breaks_required_reserve()
    {
        // Arrange
        var pilot = new HybridTerrainBakePilotMeasurement(
            CoveredAreaSquareMeters: 1,
            FinalPayloadBytes: 60,
            PeakTemporaryBytes: 10);

        // Act
        HybridTerrainBakeBudgetEstimate estimate = HybridTerrainBakeBudgetEstimator.Estimate(
            pilot,
            fullAreaSquareMeters: 1,
            currentFreeBytes: 150,
            minimumReserveBytes: 100);

        // Assert
        estimate.MeetsReserve.Should().BeFalse();
    }
}
