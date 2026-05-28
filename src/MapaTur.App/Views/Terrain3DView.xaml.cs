using System.Numerics;
using MapaTur.App.Services;
using MapaTur.Application.Terrain;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;
using SkiaSharp.Views.Maui;

namespace MapaTur.App.Views;

/// <summary>
/// 3D terrain preview rendered with SkiaSharp. Single-finger drag orbits the
/// camera, two-finger drag pans the focal point in the ground plane, and pinch
/// zooms in/out. Bind <see cref="Mesh"/>; the camera is owned by the view and
/// auto-framed on mesh change.
/// </summary>
public partial class Terrain3DView : ContentView
{
    /// <summary>Bindable mesh to render. Setting it auto-frames the camera.</summary>
    public static readonly BindableProperty MeshProperty = BindableProperty.Create(
        nameof(Mesh),
        typeof(TerrainMesh3D),
        typeof(Terrain3DView),
        propertyChanged: OnMeshChanged);

    public TerrainMesh3D? Mesh
    {
        get => (TerrainMesh3D?)GetValue(MeshProperty);
        set => SetValue(MeshProperty, value);
    }

    /// <summary>Bindable DEM raster used to look up elevations along overlay trails.</summary>
    public static readonly BindableProperty RasterProperty = BindableProperty.Create(
        nameof(Raster),
        typeof(DemRaster),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public DemRaster? Raster
    {
        get => (DemRaster?)GetValue(RasterProperty);
        set => SetValue(RasterProperty, value);
    }

    /// <summary>Bindable trails overlay rendered on top of the terrain.</summary>
    public static readonly BindableProperty TrailsProperty = BindableProperty.Create(
        nameof(Trails),
        typeof(IReadOnlyList<Trail>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<Trail>? Trails
    {
        get => (IReadOnlyList<Trail>?)GetValue(TrailsProperty);
        set => SetValue(TrailsProperty, value);
    }

    /// <summary>Bindable planned route rendered as a distinct violet polyline on top of trails.</summary>
    public static readonly BindableProperty RouteProperty = BindableProperty.Create(
        nameof(Route),
        typeof(Route),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public Route? Route
    {
        get => (Route?)GetValue(RouteProperty);
        set => SetValue(RouteProperty, value);
    }

    /// <summary>Bindable climbing areas rendered as red circular markers above the mesh.</summary>
    public static readonly BindableProperty ClimbingAreasProperty = BindableProperty.Create(
        nameof(ClimbingAreas),
        typeof(IReadOnlyList<ClimbingArea>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<ClimbingArea>? ClimbingAreas
    {
        get => (IReadOnlyList<ClimbingArea>?)GetValue(ClimbingAreasProperty);
        set => SetValue(ClimbingAreasProperty, value);
    }

    /// <summary>Camera state mutated by gestures and used by the renderer.</summary>
    public Camera3D Camera { get; } = new Camera3D();

    private readonly Terrain3DController controller;
    private readonly Terrain3DCanvasRenderer renderer = new();
    private readonly Terrain3DFrameScratch frameScratch = new();

    private double lastOrbitTotalX;
    private double lastOrbitTotalY;
    private double lastTranslateTotalX;
    private double lastTranslateTotalY;
    private double lastPinchScale = 1.0;

    public Terrain3DView()
    {
        InitializeComponent();
        controller = new Terrain3DController(Camera);
#if WINDOWS
        Canvas.HandlerChanged += OnCanvasHandlerChanged;
#endif
    }

    /// <summary>
    /// Applies a multiplicative zoom (scale &gt; 1 zooms in, &lt; 1 zooms out)
    /// and re-renders. Public so the host page can hook keyboard or mouse-wheel input.
    /// </summary>
    public void Zoom(float scale)
    {
        controller.ApplyZoom(scale);
        Canvas.InvalidateSurface();
    }

    /// <summary>Positions the camera so the entire <see cref="Mesh"/> fits in view.</summary>
    public void FrameMesh()
    {
        if (Mesh is null)
        {
            return;
        }

        Camera.Target = Vector3.Zero;
        Camera.Distance = Math.Max(Mesh.HorizontalExtent * 2.5f, 5_000f);
        Camera.AzimuthRadians = MathF.PI / 4f;
        Camera.PitchRadians = MathF.PI / 4f;
        Canvas.InvalidateSurface();
    }

    private static void OnMeshChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is Terrain3DView view)
        {
            view.FrameMesh();
        }
    }

    private static void OnOverlayDataChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is Terrain3DView view)
        {
            view.Canvas.InvalidateSurface();
        }
    }

    private void OnPaintSurface(object? sender, SKPaintGLSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        if (Mesh is null)
        {
            canvas.Clear();
            return;
        }

        IReadOnlyList<ProjectedTrail>? projectedTrails = null;
        if (Trails is { Count: > 0 } trails && Raster is not null)
        {
            projectedTrails = Trail3DProjection.Project(
                trails, Raster, Mesh, Camera, e.Info.Width, e.Info.Height);
        }

        ProjectedRoute? projectedRoute = null;
        if (Route is not null && Raster is not null)
        {
            projectedRoute = Route3DProjection.Project(
                Route, Raster, Mesh, Camera, e.Info.Width, e.Info.Height);
        }

        IReadOnlyList<ProjectedClimbingArea>? projectedClimbing = null;
        if (ClimbingAreas is { Count: > 0 } areas && Raster is not null)
        {
            projectedClimbing = Climbing3DProjection.Project(
                areas, Raster, Mesh, Camera, e.Info.Width, e.Info.Height);
        }

        renderer.Render(canvas, e.Info.Width, e.Info.Height, Mesh, Camera, frameScratch, projectedTrails, projectedRoute, projectedClimbing);
    }

    private void OnOrbitPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                lastOrbitTotalX = 0;
                lastOrbitTotalY = 0;
                return;
            case GestureStatus.Running:
                float dx = (float)(e.TotalX - lastOrbitTotalX);
                float dy = (float)(e.TotalY - lastOrbitTotalY);
                lastOrbitTotalX = e.TotalX;
                lastOrbitTotalY = e.TotalY;
                // Drag up (negative dy on screen) tilts the camera higher.
                controller.ApplyOrbit(dx, -dy);
                Canvas.InvalidateSurface();
                return;
        }
    }

    private void OnTranslatePan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                lastTranslateTotalX = 0;
                lastTranslateTotalY = 0;
                return;
            case GestureStatus.Running:
                float dx = (float)(e.TotalX - lastTranslateTotalX);
                float dy = (float)(e.TotalY - lastTranslateTotalY);
                lastTranslateTotalX = e.TotalX;
                lastTranslateTotalY = e.TotalY;
                // Drag-to-pan: world tracks the fingers, so invert deltas.
                controller.ApplyPan(-dx, -dy);
                Canvas.InvalidateSurface();
                return;
        }
    }

    private void OnPinch(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started)
        {
            lastPinchScale = 1.0;
            return;
        }

        if (e.Status != GestureStatus.Running)
        {
            return;
        }

        if (lastPinchScale <= 0)
        {
            lastPinchScale = e.Scale;
        }

        double scaleDelta = e.Scale / lastPinchScale;
        lastPinchScale = e.Scale;
        controller.ApplyZoom((float)scaleDelta);
        Canvas.InvalidateSurface();
    }

#if WINDOWS
    private Microsoft.UI.Xaml.UIElement? wheelTarget;

    private void OnCanvasHandlerChanged(object? sender, EventArgs e)
    {
        DetachWheelHandler();

        if (Canvas.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement element)
        {
            wheelTarget = element;
            wheelTarget.PointerWheelChanged += OnPointerWheelChanged;
        }
    }

    private void DetachWheelHandler()
    {
        if (wheelTarget is not null)
        {
            wheelTarget.PointerWheelChanged -= OnPointerWheelChanged;
            wheelTarget = null;
        }
    }

    private void OnPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint((Microsoft.UI.Xaml.UIElement)sender).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        // One wheel notch = 120 units; ~10% per notch.
        float scale = MathF.Pow(1.1f, delta / 120f);
        controller.ApplyZoom(scale);
        Canvas.InvalidateSurface();
        e.Handled = true;
    }
#endif
}
