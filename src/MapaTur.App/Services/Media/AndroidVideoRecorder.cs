#if ANDROID
using Android.Media;

using MapaTur.Application.Media;

using Serilog;

namespace MapaTur.App.Services.Media;

/// <summary>
/// Android H.264 (MP4) encoder built on <see cref="MediaCodec"/> + <see cref="MediaMuxer"/>. Frames arrive
/// as RGBA8, are converted to YUV420 and queued with explicit presentation timestamps, so playback is
/// correctly paced even when the capture frame rate varies. One recording at a time.
/// </summary>
/// <remarks>
/// First-cut implementation: the MediaCodec/MediaMuxer plumbing and the RGBA→YUV conversion need
/// on-device iteration (colour-format and plane-stride behaviour varies across encoders). Failures are
/// logged and swallowed so a recording problem never crashes the render thread.
/// </remarks>
public sealed class AndroidVideoRecorder : IVideoRecorder
{
    // DequeueOutputBuffer sentinels (literals, stable regardless of binding naming).
    private const int InfoTryAgainLater = -1;
    private const int InfoOutputFormatChanged = -2;

    private MediaCodec? codec;
    private MediaMuxer? muxer;
    private MediaCodec.BufferInfo? bufferInfo;
    private VideoRecordingOptions? options;
    private int trackIndex = -1;
    private bool muxerStarted;
    private long lastPts;
    private byte[]? planeScratch;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public void Start(VideoRecordingOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        options = opts;
        muxerStarted = false;
        trackIndex = -1;
        lastPts = 0;
        bufferInfo = new MediaCodec.BufferInfo();

        var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc!, opts.Width, opts.Height);
        format.SetInteger(MediaFormat.KeyColorFormat!, (int)MediaCodecCapabilities.Formatyuv420flexible);
        format.SetInteger(MediaFormat.KeyBitRate!, EstimateBitrate(opts));
        format.SetInteger(MediaFormat.KeyFrameRate!, opts.FrameRate);
        format.SetInteger(MediaFormat.KeyIFrameInterval!, 1);

        codec = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc!);
        codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        codec.Start();

        muxer = new MediaMuxer(opts.OutputPath, MuxerOutputType.Mpeg4);
    }

    /// <inheritdoc />
    public void AddFrame(byte[] rgbaPixels, long timestampMicros)
    {
        ArgumentNullException.ThrowIfNull(rgbaPixels);
        if (codec is null || options is null)
        {
            return;
        }

        try
        {
            int inIndex = codec.DequeueInputBuffer(10_000);
            if (inIndex < 0)
            {
                Drain(endOfStream: false);
                inIndex = codec.DequeueInputBuffer(10_000);
                if (inIndex < 0)
                {
                    return; // encoder busy — drop this frame rather than block the render thread
                }
            }

            Android.Media.Image? image = codec.GetInputImage(inIndex);
            if (image is not null)
            {
                FillYuv420(image, rgbaPixels, options.Width, options.Height);
                image.Close();
            }

            int size = options.Width * options.Height * 3 / 2;
            lastPts = timestampMicros;
            codec.QueueInputBuffer(inIndex, 0, size, timestampMicros, MediaCodecBufferFlags.None);
            Drain(endOfStream: false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MP4] AddFrame failed");
        }
    }

    /// <inheritdoc />
    public string? Stop()
    {
        if (codec is null || options is null)
        {
            return null;
        }

        string path = options.OutputPath;
        try
        {
            int inIndex = codec.DequeueInputBuffer(10_000);
            if (inIndex >= 0)
            {
                codec.QueueInputBuffer(inIndex, 0, 0, lastPts + 1, MediaCodecBufferFlags.EndOfStream);
            }

            Drain(endOfStream: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[MP4] finalize failed");
            path = string.Empty;
        }
        finally
        {
            try { codec.Stop(); } catch (Exception ex) { Log.Warning(ex, "[MP4] codec stop"); }
            codec.Release();
            codec = null;

            if (muxer is not null)
            {
                try { if (muxerStarted) { muxer.Stop(); } } catch (Exception ex) { Log.Warning(ex, "[MP4] muxer stop"); }
                muxer.Release();
                muxer = null;
            }
        }

        return string.IsNullOrEmpty(path) ? null : path;
    }

    private void Drain(bool endOfStream)
    {
        if (codec is null || bufferInfo is null)
        {
            return;
        }

        while (true)
        {
            int outIndex = codec.DequeueOutputBuffer(bufferInfo, endOfStream ? 10_000 : 0);
            if (outIndex == InfoTryAgainLater)
            {
                if (!endOfStream)
                {
                    break; // no output ready yet; come back next frame
                }

                continue; // draining to EOS — keep waiting
            }

            if (outIndex == InfoOutputFormatChanged)
            {
                // The encoder's real output format (with csd-0/1) is only known now — add the muxer track.
                trackIndex = muxer!.AddTrack(codec.OutputFormat!);
                muxer.Start();
                muxerStarted = true;
                continue;
            }

            if (outIndex < 0)
            {
                continue; // deprecated INFO_OUTPUT_BUFFERS_CHANGED etc.
            }

            Java.Nio.ByteBuffer? outBuf = codec.GetOutputBuffer(outIndex);
            if ((bufferInfo.Flags & MediaCodecBufferFlags.CodecConfig) != 0)
            {
                bufferInfo.Size = 0; // codec config is fed via the track format, not as a sample
            }

            if (outBuf is not null && bufferInfo.Size > 0 && muxerStarted)
            {
                outBuf.Position(bufferInfo.Offset);
                outBuf.Limit(bufferInfo.Offset + bufferInfo.Size);
                muxer!.WriteSampleData(trackIndex, outBuf, bufferInfo);
            }

            codec.ReleaseOutputBuffer(outIndex, false);

            if ((bufferInfo.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
            {
                break;
            }
        }
    }

    // Converts tightly-packed RGBA8 into the encoder's YUV420 input image (BT.601 limited range),
    // respecting each plane's row/pixel stride so it works for both planar (I420) and semi-planar (NV12)
    // layouts. Chroma is 2×2 box-subsampled.
    private void FillYuv420(Android.Media.Image image, byte[] rgba, int width, int height)
    {
        Android.Media.Image.Plane[] planes = image.GetPlanes()!;
        FillLuma(planes[0], rgba, width, height);
        FillChroma(planes[1], planes[2], rgba, width, height);
    }

    private void FillLuma(Android.Media.Image.Plane yPlane, byte[] rgba, int width, int height)
    {
        int rowStride = yPlane.RowStride;
        int pixelStride = yPlane.PixelStride;
        byte[] row = RentRow(rowStride);

        Java.Nio.ByteBuffer buffer = yPlane.Buffer!;
        buffer.Rewind();
        for (int y = 0; y < height; y++)
        {
            int rgbaRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int p = rgbaRow + (x * 4);
                byte luma = Clamp(((66 * rgba[p]) + (129 * rgba[p + 1]) + (25 * rgba[p + 2]) + 128 >> 8) + 16);
                row[x * pixelStride] = luma;
            }

            // The last plane row carries no trailing stride padding, so never write past what's left.
            buffer.Put(row, 0, Math.Min(rowStride, buffer.Remaining()));
        }
    }

    private void FillChroma(Android.Media.Image.Plane uPlane, Android.Media.Image.Plane vPlane, byte[] rgba, int width, int height)
    {
        int chromaW = width / 2;
        int chromaH = height / 2;

        int uRowStride = uPlane.RowStride;
        int uPixelStride = uPlane.PixelStride;
        int vRowStride = vPlane.RowStride;
        int vPixelStride = vPlane.PixelStride;

        Java.Nio.ByteBuffer uBuffer = uPlane.Buffer!;
        Java.Nio.ByteBuffer vBuffer = vPlane.Buffer!;
        uBuffer.Rewind();
        vBuffer.Rewind();

        // Semi-planar (NV12): U and V are interleaved in a single buffer (pixelStride 2). Writing each
        // plane as a separate full row would clobber the other's bytes, so build one interleaved U/V row
        // and write it through the U-plane buffer only. Planar (I420, pixelStride 1) writes each separately.
        bool semiPlanar = uPixelStride == 2 || vPixelStride == 2;
        byte[] uRow = new byte[uRowStride];
        byte[] vRow = semiPlanar ? System.Array.Empty<byte>() : new byte[vRowStride];

        for (int cy = 0; cy < chromaH; cy++)
        {
            int srcY = cy * 2;
            for (int cx = 0; cx < chromaW; cx++)
            {
                int srcX = cx * 2;
                int p = ((srcY * width) + srcX) * 4;
                int r = rgba[p];
                int g = rgba[p + 1];
                int b = rgba[p + 2];

                byte u = Clamp(((-38 * r) - (74 * g) + (112 * b) + 128 >> 8) + 128);
                byte v = Clamp(((112 * r) - (94 * g) - (18 * b) + 128 >> 8) + 128);

                if (semiPlanar)
                {
                    // NV12 layout: U at the even byte, V at the following odd byte.
                    uRow[cx * uPixelStride] = u;
                    uRow[(cx * uPixelStride) + 1] = v;
                }
                else
                {
                    uRow[cx * uPixelStride] = u;
                    vRow[cx * vPixelStride] = v;
                }
            }

            uBuffer.Put(uRow, 0, Math.Min(uRowStride, uBuffer.Remaining()));
            if (!semiPlanar)
            {
                vBuffer.Put(vRow, 0, Math.Min(vRowStride, vBuffer.Remaining()));
            }
        }
    }

    private byte[] RentRow(int rowStride)
    {
        if (planeScratch is null || planeScratch.Length < rowStride)
        {
            planeScratch = new byte[rowStride];
        }

        return planeScratch;
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static int EstimateBitrate(VideoRecordingOptions opts)
    {
        long bits = (long)(opts.Width * opts.Height * Math.Max(1, opts.FrameRate) * 0.12);
        return (int)Math.Clamp(bits, 2_000_000, 25_000_000);
    }
}
#endif
