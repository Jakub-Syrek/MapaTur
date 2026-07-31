namespace MapaTur.Application.Terrain;

/// <summary>
/// Decides whether a temporally reusable render pass must be refreshed this frame.
/// Continuous camera modes may reuse one-frame-old results on odd frames; the first
/// frame and every unthrottled frame always refresh.
/// </summary>
public static class TemporalPassCadence
{
    public static bool ShouldRefresh(long frame, bool throttled, bool hasReusableResult) =>
        !throttled || !hasReusableResult || (frame & 1L) == 0L;
}