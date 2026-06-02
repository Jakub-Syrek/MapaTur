namespace MapaTur.Application.Media;

/// <summary>Encoder settings for a recording session.</summary>
/// <param name="Width">Frame width in pixels (even).</param>
/// <param name="Height">Frame height in pixels (even).</param>
/// <param name="FrameRate">Nominal frame rate hint for the encoder.</param>
/// <param name="OutputPath">Absolute path the finished video is written to.</param>
public sealed record VideoRecordingOptions(int Width, int Height, int FrameRate, string OutputPath);

/// <summary>
/// Platform video encoder that turns a stream of RGBA frames into an MP4 file. Implemented per platform
/// (Android via MediaCodec/MediaMuxer); a no-op implementation reports <see cref="IsSupported"/> = false
/// where encoding isn't wired up, so callers degrade gracefully.
/// </summary>
public interface IVideoRecorder
{
    /// <summary>Whether this platform can actually encode video. When false, callers should not record.</summary>
    bool IsSupported { get; }

    /// <summary>Begins a recording session with the given options.</summary>
    void Start(VideoRecordingOptions options);

    /// <summary>
    /// Appends one frame. <paramref name="rgbaPixels"/> is tightly-packed top-row-first RGBA8 of the
    /// configured width × height; <paramref name="timestampMicros"/> is its presentation time in
    /// microseconds from the start of the recording.
    /// </summary>
    void AddFrame(byte[] rgbaPixels, long timestampMicros);

    /// <summary>Finalizes the file and returns its path, or null if nothing was written.</summary>
    string? Stop();
}