using MapaTur.Application.Media;

namespace MapaTur.App.Services.Media;

/// <summary>
/// Creates the platform <see cref="IVideoRecorder"/>. Android gets a real MediaCodec encoder; every other
/// platform gets <see cref="UnsupportedVideoRecorder"/> (recording is then a no-op the UI can detect).
/// </summary>
public static class VideoRecorderFactory
{
    /// <summary>Returns the best available recorder for the current platform.</summary>
    public static IVideoRecorder Create()
    {
#if ANDROID
        return new AndroidVideoRecorder();
#else
        return new UnsupportedVideoRecorder();
#endif
    }
}
