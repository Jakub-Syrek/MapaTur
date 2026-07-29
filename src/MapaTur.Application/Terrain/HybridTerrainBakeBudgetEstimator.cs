namespace MapaTur.Application.Terrain;

public readonly record struct HybridTerrainBakePilotMeasurement(
    double CoveredAreaSquareMeters,
    long FinalPayloadBytes,
    long PeakTemporaryBytes);

public readonly record struct HybridTerrainBakeBudgetEstimate(
    long EstimatedFinalBytes,
    long EstimatedPeakTemporaryBytes,
    long EstimatedFreeBytesAtPeak,
    long EstimatedFreeBytesAfterBake,
    bool MeetsReserve);

/// <summary>
/// Converts a measured pilot's byte density into the mandatory preflight for a full RMP3 bake. Current free
/// space is measured after retaining source and rollback packages, so only new final and temporary bytes are
/// subtracted here.
/// </summary>
public static class HybridTerrainBakeBudgetEstimator
{
    public static HybridTerrainBakeBudgetEstimate Estimate(
        HybridTerrainBakePilotMeasurement pilot,
        double fullAreaSquareMeters,
        long currentFreeBytes,
        long minimumReserveBytes)
    {
        if (!double.IsFinite(pilot.CoveredAreaSquareMeters)
            || pilot.CoveredAreaSquareMeters <= 0
            || pilot.FinalPayloadBytes < 0
            || pilot.PeakTemporaryBytes < 0
            || !double.IsFinite(fullAreaSquareMeters)
            || fullAreaSquareMeters <= 0
            || currentFreeBytes < 0
            || minimumReserveBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pilot));
        }

        double scale = fullAreaSquareMeters / pilot.CoveredAreaSquareMeters;
        long finalBytes = ScaleBytes(pilot.FinalPayloadBytes, scale);
        long temporaryBytes = ScaleBytes(pilot.PeakTemporaryBytes, scale);
        long freeAfterBake = checked(currentFreeBytes - finalBytes);
        long freeAtPeak = checked(freeAfterBake - temporaryBytes);
        bool meetsReserve = freeAtPeak >= 0 && freeAfterBake >= minimumReserveBytes;
        return new HybridTerrainBakeBudgetEstimate(
            finalBytes,
            temporaryBytes,
            freeAtPeak,
            freeAfterBake,
            meetsReserve);
    }

    private static long ScaleBytes(long bytes, double scale)
    {
        double scaled = Math.Ceiling(bytes * scale);
        if (!double.IsFinite(scaled) || scaled > long.MaxValue)
        {
            throw new OverflowException("The estimated RMP3 bake size exceeds the supported range.");
        }

        return checked((long)scaled);
    }
}
