namespace MapaTur.Application.Media;

/// <summary>
/// Drives an in-app screen recording of the 3D view. It owns the recording state machine — start/stop,
/// even-dimension locking, and per-frame presentation timestamps derived from an injected clock — and
/// delegates the actual encoding to an <see cref="IVideoRecorder"/>. Frames are captured at whatever rate
/// the view paints; real-time PTS keep playback correctly paced even when the frame rate varies.
/// </summary>
public sealed class FlythroughRecorder
{
    private readonly IVideoRecorder recorder;
    private readonly Func<long> clockMicros;
    private long startMicros;

    /// <summary>Creates a recorder over a platform encoder and a microsecond clock (injectable for tests).</summary>
    public FlythroughRecorder(IVideoRecorder recorder, Func<long> clockMicros)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(clockMicros);

        this.recorder = recorder;
        this.clockMicros = clockMicros;
    }

    /// <summary>Whether the underlying platform encoder can actually record.</summary>
    public bool IsSupported => recorder.IsSupported;

    /// <summary>Whether a recording is currently in progress.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>Locked (even) frame width of the active recording.</summary>
    public int FrameWidth { get; private set; }

    /// <summary>Locked (even) frame height of the active recording.</summary>
    public int FrameHeight { get; private set; }

    /// <summary>
    /// Begins recording at the given surface size and frame rate, writing to <paramref name="outputPath"/>.
    /// Dimensions are rounded down to even values (H.264 requires it). Returns false without starting when
    /// the platform can't encode or a recording is already running.
    /// </summary>
    public bool TryStart(int width, int height, int frameRate, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        if (width < 2 || height < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame must be at least 2×2.");
        }
        if (frameRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameRate), "Frame rate must be positive.");
        }

        if (IsRecording || !recorder.IsSupported)
        {
            return false;
        }

        FrameWidth = width & ~1;
        FrameHeight = height & ~1;
        recorder.Start(new VideoRecordingOptions(FrameWidth, FrameHeight, frameRate, outputPath));
        startMicros = clockMicros();
        IsRecording = true;
        return true;
    }

    /// <summary>
    /// Appends the current frame (tightly-packed RGBA8 of <see cref="FrameWidth"/>×<see cref="FrameHeight"/>)
    /// with a presentation timestamp relative to the recording start. No-op when not recording.
    /// </summary>
    public void CaptureFrame(byte[] rgbaPixels)
    {
        ArgumentNullException.ThrowIfNull(rgbaPixels);
        if (!IsRecording)
        {
            return;
        }

        long pts = clockMicros() - startMicros;
        if (pts < 0)
        {
            pts = 0;
        }

        recorder.AddFrame(rgbaPixels, pts);
    }

    /// <summary>Finalizes the recording and returns the output path, or null if not recording.</summary>
    public string? Stop()
    {
        if (!IsRecording)
        {
            return null;
        }

        IsRecording = false;
        return recorder.Stop();
    }
}