using MapaTur.Application.Media;

namespace MapaTur.App.Services.Media;

/// <summary>
/// Fallback <see cref="IVideoRecorder"/> for platforms where in-app encoding isn't wired up. Reports
/// <see cref="IsSupported"/> = false so callers skip recording entirely; all operations are no-ops.
/// </summary>
public sealed class UnsupportedVideoRecorder : IVideoRecorder
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public void Start(VideoRecordingOptions options)
    {
    }

    /// <inheritdoc />
    public void AddFrame(byte[] rgbaPixels, long timestampMicros)
    {
    }

    /// <inheritdoc />
    public string? Stop() => null;
}