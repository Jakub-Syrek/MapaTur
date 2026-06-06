using FluentAssertions;

using MapaTur.Application.Media;

namespace MapaTur.Application.Tests.Media;

public sealed class FlythroughRecorderTests
{
    private sealed class FakeVideoRecorder : IVideoRecorder
    {
        public bool IsSupported { get; set; } = true;
        public VideoRecordingOptions? StartedWith { get; private set; }
        public List<(byte[] Pixels, long Pts)> Frames { get; } = new();
        public bool Stopped { get; private set; }
        public string? StopResult { get; set; } = "out.mp4";

        public void Start(VideoRecordingOptions options) => StartedWith = options;

        public void AddFrame(byte[] rgbaPixels, long timestampMicros) => Frames.Add((rgbaPixels, timestampMicros));

        public string? Stop()
        {
            Stopped = true;
            return StopResult;
        }
    }

    private long now;
    private static byte[] FakePixels => new byte[16];

    private FlythroughRecorder Build(FakeVideoRecorder recorder)
        => new(recorder, () => now);

    [Fact]
    public void Ctor_NullRecorder_Throws()
    {
        Action act = () => _ = new FlythroughRecorder(null!, () => 0);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullClock_Throws()
    {
        Action act = () => _ = new FlythroughRecorder(new FakeVideoRecorder(), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void IsSupported_ReflectsUnderlyingRecorder()
    {
        Build(new FakeVideoRecorder { IsSupported = true }).IsSupported.Should().BeTrue();
        Build(new FakeVideoRecorder { IsSupported = false }).IsSupported.Should().BeFalse();
    }

    [Fact]
    public void TryStart_WhenUnsupported_ReturnsFalseAndDoesNotRecord()
    {
        var recorder = new FakeVideoRecorder { IsSupported = false };
        var sut = Build(recorder);

        bool started = sut.TryStart(640, 480, 30, "out.mp4");

        started.Should().BeFalse();
        sut.IsRecording.Should().BeFalse();
        recorder.StartedWith.Should().BeNull();
    }

    [Fact]
    public void TryStart_Success_StartsRecorderAndMarksRecording()
    {
        var recorder = new FakeVideoRecorder();
        var sut = Build(recorder);

        bool started = sut.TryStart(640, 480, 30, "out.mp4");

        started.Should().BeTrue();
        sut.IsRecording.Should().BeTrue();
        recorder.StartedWith.Should().Be(new VideoRecordingOptions(640, 480, 30, "out.mp4"));
    }

    [Fact]
    public void TryStart_OddDimensions_RoundedDownToEven()
    {
        var recorder = new FakeVideoRecorder();
        var sut = Build(recorder);

        sut.TryStart(641, 481, 30, "out.mp4");

        sut.FrameWidth.Should().Be(640);
        sut.FrameHeight.Should().Be(480);
        recorder.StartedWith!.Width.Should().Be(640);
        recorder.StartedWith.Height.Should().Be(480);
    }

    [Fact]
    public void TryStart_WhenAlreadyRecording_ReturnsFalse()
    {
        var recorder = new FakeVideoRecorder();
        var sut = Build(recorder);
        sut.TryStart(640, 480, 30, "a.mp4");

        bool second = sut.TryStart(640, 480, 30, "b.mp4");

        second.Should().BeFalse();
        recorder.StartedWith!.OutputPath.Should().Be("a.mp4");
    }

    [Fact]
    public void TryStart_TooSmall_Throws()
    {
        var sut = Build(new FakeVideoRecorder());

        Action act = () => sut.TryStart(1, 480, 30, "out.mp4");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryStart_NonPositiveFrameRate_Throws()
    {
        var sut = Build(new FakeVideoRecorder());

        Action act = () => sut.TryStart(640, 480, 0, "out.mp4");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CaptureFrame_WhenNotRecording_IsNoOp()
    {
        var recorder = new FakeVideoRecorder();
        var sut = Build(recorder);

        sut.CaptureFrame(FakePixels);

        recorder.Frames.Should().BeEmpty();
    }

    [Fact]
    public void CaptureFrame_ForwardsWithTimestampRelativeToStart()
    {
        var recorder = new FakeVideoRecorder();
        var sut = Build(recorder);
        now = 1_000_000;
        sut.TryStart(640, 480, 30, "out.mp4");

        now = 1_033_000;
        sut.CaptureFrame(FakePixels);
        now = 1_066_000;
        sut.CaptureFrame(FakePixels);

        recorder.Frames.Should().HaveCount(2);
        recorder.Frames[0].Pts.Should().Be(33_000);
        recorder.Frames[1].Pts.Should().Be(66_000);
    }

    [Fact]
    public void CaptureFrame_ClockMovesBackwards_ClampsToZero()
    {
        var recorder = new FakeVideoRecorder();
        var sut = Build(recorder);
        now = 5_000;
        sut.TryStart(640, 480, 30, "out.mp4");

        now = 4_000; // earlier than start
        sut.CaptureFrame(FakePixels);

        recorder.Frames.Single().Pts.Should().Be(0);
    }

    [Fact]
    public void CaptureFrame_NullPixels_Throws()
    {
        var sut = Build(new FakeVideoRecorder());
        sut.TryStart(640, 480, 30, "out.mp4");

        Action act = () => sut.CaptureFrame(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Stop_WhenRecording_StopsRecorderAndReturnsPath()
    {
        var recorder = new FakeVideoRecorder { StopResult = "saved.mp4" };
        var sut = Build(recorder);
        sut.TryStart(640, 480, 30, "out.mp4");

        string? path = sut.Stop();

        path.Should().Be("saved.mp4");
        sut.IsRecording.Should().BeFalse();
        recorder.Stopped.Should().BeTrue();
    }

    [Fact]
    public void Stop_WhenNotRecording_ReturnsNull()
    {
        var recorder = new FakeVideoRecorder();
        var sut = Build(recorder);

        string? path = sut.Stop();

        path.Should().BeNull();
        recorder.Stopped.Should().BeFalse();
    }
}