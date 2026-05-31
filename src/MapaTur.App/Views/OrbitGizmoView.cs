using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace MapaTur.App.Views;

/// <summary>
/// Compact navigation-orb widget. Renders a shaded sphere with latitude/longitude lines and a
/// red azimuth marker so the user can see where the camera is currently pointed and which way
/// dragging will rotate it. Drag inside the orb to fire <see cref="OrbitDragged"/> with raw
/// (dx, dy) pixel deltas — the host wires this to <c>Terrain3DController.ApplyOrbit</c>.
/// <para>
/// Lives separate from the SKGLView terrain canvas so 1- and 2-finger gestures on the mesh
/// stay free for orbit / look-around / pinch zoom, while the orb itself is an unmistakable
/// "rotate me" affordance for people who don't yet know about the swipe gestures.
/// </para>
/// </summary>
public sealed class OrbitGizmoView : SKCanvasView
{
    /// <summary>Diameter in device-independent units. ~96 dp is the smallest a drag pad
    /// can be before fat-finger error makes the rotation jittery.</summary>
    private const float DefaultSizeDip = 96f;

    /// <summary>Camera azimuth (rad). Bind via <see cref="CameraAzimuthRadians"/> so the marker
    /// rotates as the camera does.</summary>
    public static readonly BindableProperty CameraAzimuthRadiansProperty = BindableProperty.Create(
        nameof(CameraAzimuthRadians),
        typeof(float),
        typeof(OrbitGizmoView),
        defaultValue: 0f,
        propertyChanged: (b, _, _) => ((OrbitGizmoView)b).InvalidateSurface());

    public float CameraAzimuthRadians
    {
        get => (float)GetValue(CameraAzimuthRadiansProperty);
        set => SetValue(CameraAzimuthRadiansProperty, value);
    }

    /// <summary>Camera pitch (rad). Tilts the orb's "north pole marker" to indicate the look angle.</summary>
    public static readonly BindableProperty CameraPitchRadiansProperty = BindableProperty.Create(
        nameof(CameraPitchRadians),
        typeof(float),
        typeof(OrbitGizmoView),
        defaultValue: 0f,
        propertyChanged: (b, _, _) => ((OrbitGizmoView)b).InvalidateSurface());

    public float CameraPitchRadians
    {
        get => (float)GetValue(CameraPitchRadiansProperty);
        set => SetValue(CameraPitchRadiansProperty, value);
    }

    /// <summary>Fires for every drag-running event with (dx, dy) in screen pixels since the
    /// previous fire. Host should multiply by an orbit-sensitivity factor and call ApplyOrbit.</summary>
    public event EventHandler<OrbitDragEventArgs>? OrbitDragged;

    private double lastTotalX;
    private double lastTotalY;

    public OrbitGizmoView()
    {
        WidthRequest = DefaultSizeDip;
        HeightRequest = DefaultSizeDip;
        BackgroundColor = Colors.Transparent;
        EnableTouchEvents = true;
        Touch += OnTouch;
        var pan = new PanGestureRecognizer { TouchPoints = 1 };
        pan.PanUpdated += OnPanUpdated;
        GestureRecognizers.Add(pan);
        PaintSurface += OnPaint;
    }

    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        // We rely on the PanGestureRecognizer for delta tracking; the Touch handler is here to
        // claim the event so the underlying SKGLView doesn't also see the gesture as orbit.
        e.Handled = true;
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                lastTotalX = 0;
                lastTotalY = 0;
                return;
            case GestureStatus.Running:
                float dx = (float)(e.TotalX - lastTotalX);
                float dy = (float)(e.TotalY - lastTotalY);
                lastTotalX = e.TotalX;
                lastTotalY = e.TotalY;
                // Dragging up on the orb should tilt the camera up — same sign convention as
                // the OnOrbitPan handler on the mesh.
                OrbitDragged?.Invoke(this, new OrbitDragEventArgs(dx, -dy));
                return;
        }
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;
        canvas.Clear();

        int w = e.Info.Width;
        int h = e.Info.Height;
        float cx = w * 0.5f;
        float cy = h * 0.5f;
        float radius = Math.Min(w, h) * 0.45f;

        // Sphere: radial gradient from highlight (top-left) to deep blue (bottom-right) — gives
        // the orb a believable 3D feel without proper shading.
        using var sphereShader = SKShader.CreateRadialGradient(
            new SKPoint(cx - radius * 0.35f, cy - radius * 0.35f),
            radius * 1.1f,
            new[] { new SKColor(0xCB, 0xD5, 0xE1), new SKColor(0x2A, 0x4E, 0x8E), new SKColor(0x14, 0x29, 0x55) },
            new[] { 0f, 0.55f, 1f },
            SKShaderTileMode.Clamp);
        using var spherePaint = new SKPaint { IsAntialias = true, Shader = sphereShader, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(cx, cy, radius, spherePaint);

        // Subtle outline.
        using var outline = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            Color = new SKColor(0x08, 0x12, 0x29, 0xC0),
        };
        canvas.DrawCircle(cx, cy, radius, outline);

        // Equator + one meridian, dimmed, just to suggest "this is a sphere I can rotate".
        using var gridPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0x60),
        };
        var rect = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
        // Equator: flatten to an ellipse based on camera pitch — view-angle hint.
        float pitchT = Math.Clamp(CameraPitchRadians / (MathF.PI / 2f), 0f, 1f);
        var equatorRect = new SKRect(cx - radius, cy - (radius * pitchT), cx + radius, cy + (radius * pitchT));
        canvas.DrawOval(equatorRect, gridPaint);
        // Prime meridian.
        canvas.DrawOval(new SKRect(cx - (radius * 0.35f), cy - radius, cx + (radius * 0.35f), cy + radius), gridPaint);

        // Azimuth marker (north pointer): a small red dot on the equator at the camera's heading.
        float headingRad = -CameraAzimuthRadians;  // screen-X grows east; sign matches map convention.
        float markerX = cx + radius * 0.9f * MathF.Sin(headingRad);
        float markerY = cy - radius * 0.9f * MathF.Cos(headingRad);
        using var markerPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.Red };
        canvas.DrawCircle(markerX, markerY, radius * 0.12f, markerPaint);
        using var markerOutline = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = SKColors.White,
        };
        canvas.DrawCircle(markerX, markerY, radius * 0.12f, markerOutline);

        // Centre dot — "you're looking here".
        using var centerPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0xE0),
        };
        canvas.DrawCircle(cx, cy, radius * 0.07f, centerPaint);

        // Suppress unused warnings for the precomputed sphere rect.
        _ = rect;
    }
}

/// <summary>Args for <see cref="OrbitGizmoView.OrbitDragged"/>.</summary>
public sealed class OrbitDragEventArgs : EventArgs
{
    public OrbitDragEventArgs(float dxPixels, float dyPixels)
    {
        DxPixels = dxPixels;
        DyPixels = dyPixels;
    }

    public float DxPixels { get; }
    public float DyPixels { get; }
}