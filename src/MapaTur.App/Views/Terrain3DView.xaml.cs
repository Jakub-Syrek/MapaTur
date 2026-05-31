using System.Numerics;

using MapaTur.App.Services;
using MapaTur.Application.Maps;
using MapaTur.Application.Terrain;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Location;
using MapaTur.Domain.Pois;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace MapaTur.App.Views;

/// <summary>
/// 3D terrain preview rendered with SkiaSharp. Single-finger drag orbits the
/// camera, two-finger drag pans the focal point in the ground plane, and pinch
/// zooms in/out. Bind <see cref="Tiles"/>; the camera is owned by the view and
/// auto-framed on tile change.
/// </summary>
public partial class Terrain3DView : ContentView
{
    /// <summary>
    /// Bindable set of terrain mesh tiles to render (a high-resolution DEM is split into ≤65 536-vertex
    /// tiles, each its own SKVertices). All tiles share one world frame; tile 0 defines the overlay
    /// coordinate system. Setting it auto-frames the camera.
    /// </summary>
    public static readonly BindableProperty TilesProperty = BindableProperty.Create(
        nameof(Tiles),
        typeof(IReadOnlyList<TerrainMesh3D>),
        typeof(Terrain3DView),
        propertyChanged: OnTilesChanged);

    public IReadOnlyList<TerrainMesh3D>? Tiles
    {
        get => (IReadOnlyList<TerrainMesh3D>?)GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    /// <summary>The world frame for overlays + framing: the first tile (all tiles share the frame), or null.</summary>
    private TerrainMesh3D? WorldFrame => Tiles is { Count: > 0 } tiles ? tiles[0] : null;

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

    /// <summary>Bindable roads overlay (unmarked Trail polylines) drawn as grey ribbons under the trails.</summary>
    public static readonly BindableProperty RoadsProperty = BindableProperty.Create(
        nameof(Roads),
        typeof(IReadOnlyList<Trail>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<Trail>? Roads
    {
        get => (IReadOnlyList<Trail>?)GetValue(RoadsProperty);
        set => SetValue(RoadsProperty, value);
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

    /// <summary>Bindable mountain POIs rendered as colour-coded circular markers above the mesh.</summary>
    public static readonly BindableProperty PoisProperty = BindableProperty.Create(
        nameof(Pois),
        typeof(IReadOnlyList<MountainPoi>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<MountainPoi>? Pois
    {
        get => (IReadOnlyList<MountainPoi>?)GetValue(PoisProperty);
        set => SetValue(PoisProperty, value);
    }

    /// <summary>Bindable summits rendered as gold mountain glyphs with elevation labels above the mesh.</summary>
    public static readonly BindableProperty PeaksProperty = BindableProperty.Create(
        nameof(Peaks),
        typeof(IReadOnlyList<TerrainPeak>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<TerrainPeak>? Peaks
    {
        get => (IReadOnlyList<TerrainPeak>?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    /// <summary>
    /// Bindable current GPS fix of the device. A null value hides the marker. The view wraps the
    /// fix in a one-element list internally so the existing <c>Marker3DOverlayProjector</c> caching
    /// machinery applies — only re-projects when the fix reference (not its contents) changes.
    /// </summary>
    public static readonly BindableProperty UserLocationProperty = BindableProperty.Create(
        nameof(UserLocation),
        typeof(UserLocation),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public UserLocation? UserLocation
    {
        get => (UserLocation?)GetValue(UserLocationProperty);
        set => SetValue(UserLocationProperty, value);
    }

    /// <summary>Bindable path to an ortho-photo image draped over the terrain (GPU path only). Null = hypsometric tint.</summary>
    public static readonly BindableProperty OrthoTexturePathProperty = BindableProperty.Create(
        nameof(OrthoTexturePath),
        typeof(string),
        typeof(Terrain3DView),
        propertyChanged: OnOrthoTexturePathChanged);

    public string? OrthoTexturePath
    {
        get => (string?)GetValue(OrthoTexturePathProperty);
        set => SetValue(OrthoTexturePathProperty, value);
    }

    private static void OnOrthoTexturePathChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (Terrain3DView)bindable;
        view.orthoPathDirty = true;
        view.Canvas.InvalidateSurface();
    }

    /// <summary>Bindable ortho tiles (row-major), one per mesh cell. Takes precedence over the single OrthoTexturePath.</summary>
    public static readonly BindableProperty OrthoTexturePathsProperty = BindableProperty.Create(
        nameof(OrthoTexturePaths),
        typeof(IReadOnlyList<string>),
        typeof(Terrain3DView),
        propertyChanged: OnOrthoTexturePathChanged);

    public IReadOnlyList<string>? OrthoTexturePaths
    {
        get => (IReadOnlyList<string>?)GetValue(OrthoTexturePathsProperty);
        set => SetValue(OrthoTexturePathsProperty, value);
    }

    /// <summary>
    /// Pre-decoded ortho cells supplied directly as RGBA8 bytes (e.g. composited from MBTiles).
    /// Takes precedence over both <see cref="OrthoTexturePaths"/> and <see cref="OrthoTexturePath"/>:
    /// the view uploads these bytes verbatim without touching the filesystem.
    /// </summary>
    public static readonly BindableProperty OrthoTextureCellsProperty = BindableProperty.Create(
        nameof(OrthoTextureCells),
        typeof(IReadOnlyList<OrthoTextureCell>),
        typeof(Terrain3DView),
        propertyChanged: OnOrthoTexturePathChanged);

    public IReadOnlyList<OrthoTextureCell>? OrthoTextureCells
    {
        get => (IReadOnlyList<OrthoTextureCell>?)GetValue(OrthoTextureCellsProperty);
        set => SetValue(OrthoTextureCellsProperty, value);
    }

    /// <summary>Camera state mutated by gestures and used by the renderer.</summary>
    public Camera3D Camera { get; } = new Camera3D();

    private readonly Terrain3DController controller;
    private readonly Terrain3DCanvasRenderer renderer = new();
    private readonly Terrain3DFrameScratch frameScratch = new();

    // Stateful overlay projectors own the camera-independent world cache plus reusable screen buffers,
    // so a gesture (which changes only the camera) doesn't re-run ~38k DEM bilinear samples + geo→world
    // cosines per frame, nor allocate per-frame point arrays. They rebuild only when the trails/route,
    // raster or mesh reference changes; per frame they pay just the screen transform into cached buffers.
    private readonly Trail3DOverlayProjector trailProjector = new();
    private readonly Route3DOverlayProjector routeProjector = new();

    // Marker overlays (climbing areas, summits) get the same stateful, zero-per-frame-allocation
    // treatment as trails/routes: the world cache rebuilds only when the items/raster/mesh change,
    // and each frame fills a reused results buffer. One generic projector serves both — they differ
    // only in their world-build (climbing samples the DEM; summits carry their own elevation).
    private const float ClimbingMarkerLiftMeters = 30f;
    private const float PoiMarkerLiftMeters = 25f;
    private const float PeakMarkerLiftMeters = 40f;

    private readonly Marker3DOverlayProjector<ClimbingArea, ProjectedClimbingArea> climbingProjector =
        new(
            (areas, raster, mesh, lift) => Climbing3DProjection.ToWorld(areas, raster!, mesh, lift),
            (source, screen) => new ProjectedClimbingArea(source, screen));

    private readonly Marker3DOverlayProjector<MountainPoi, ProjectedPoi> poiProjector =
        new(
            (pois, raster, mesh, lift) => Poi3DProjection.ToWorld(pois, raster!, mesh, lift),
            (source, screen) => new ProjectedPoi(source, screen));

    private readonly Marker3DOverlayProjector<TerrainPeak, ProjectedPeak> peakProjector =
        new(
            (peaks, _, mesh, lift) => Peak3DProjection.ToWorld(peaks, mesh, lift),
            (source, screen) => new ProjectedPeak(source, screen));

    // GPS marker: prefer the OS-reported altitude when present (UserLocation3DProjection takes care
    // of that), otherwise fall back to a DEM lookup. Lift higher than POI/climbing so the dot
    // visibly hovers above the ground on flat sections instead of merging with the mesh.
    private const float UserLocationMarkerLiftMeters = 20f;
    private readonly Marker3DOverlayProjector<UserLocation, ProjectedUserLocation> userLocationProjector =
        new(
            (fixes, raster, mesh, lift) => UserLocation3DProjection.ToWorld(fixes, raster, mesh, lift),
            (source, screen) => new ProjectedUserLocation(source, screen));
    // Reused one-element buffer so a fix update doesn't allocate a fresh list per frame; the
    // projector compares by reference so we only swap the contained UserLocation when it changes.
    private readonly UserLocation[] userLocationBuffer = new UserLocation[1];

    private double lastOrbitTotalX;
    private double lastOrbitTotalY;
    private double lastTranslateTotalX;
    private double lastTranslateTotalY;
    // Only used on non-Android platforms where Scale is cumulative — Android reads e.Scale as a
    // per-update delta directly. The pragma silences the analyzer "assigned but never used" on the
    // ANDROID partial build where the #else branch is excluded.
#pragma warning disable CS0414, IDE0044
    private double lastPinchScale = 1.0;
#pragma warning restore CS0414, IDE0044

    // Set when OrthoTexturePath changes; the GPU render path decodes the image and hands it to the GL
    // renderer once (off the per-frame path). Lives outside #if WINDOWS because the bindable setter does;
    // non-Windows builds carry the field for parity but never read it (CPU Skia path has no GL ortho).
#pragma warning disable CS0414 // assigned but never used — read only inside #if WINDOWS
    private bool orthoPathDirty;
#pragma warning restore CS0414

    public Terrain3DView()
    {
        InitializeComponent();
        controller = new Terrain3DController(Camera);

        // Orbit gizmo: drag on the sphere widget rotates the camera; mirror current camera
        // azimuth + pitch onto the gizmo so its red marker shows where we're looking. The
        // gizmo's PanGesture is independent of the mesh's, so there's no conflict.
        OrbitGizmo.OrbitDragged += OnOrbitGizmoDragged;
        SyncOrbitGizmo();
#if WINDOWS
        Canvas.HandlerChanged += OnCanvasHandlerChanged;
#endif
    }

    private void OnOrbitGizmoDragged(object? sender, OrbitDragEventArgs e)
    {
        // The orb is a "turn-the-head" widget: camera position stays put, only the view
        // direction rotates. ApplyLookAround swings the target so the recomputed orbit
        // position lands back where the camera already was.
        controller.ApplyLookAround(e.DxPixels, e.DyPixels);
        Canvas.InvalidateSurface();
        SyncOrbitGizmo();
    }

    private void SyncOrbitGizmo()
    {
        if (OrbitGizmo is null)
        {
            return;
        }
        OrbitGizmo.CameraAzimuthRadians = Camera.AzimuthRadians;
        OrbitGizmo.CameraPitchRadians = Camera.PitchRadians;
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

    // Per-click steps for the on-screen control pads, sized so one tap produces a clearly
    // visible move. Pixel-equivalents feed the same controller methods the gestures use
    // (OrbitSensitivity 0.005 rad/px → 28 px ≈ 8°).
    private const float ButtonOrbitStep = 28f;
    private const float ButtonPanStep = 48f;
    private const float ButtonVerticalStep = 48f;
    private const float ButtonZoomFactor = 1.2f;

    private void StepCamera(Action mutate)
    {
        mutate();
        Canvas.InvalidateSurface();
    }

    // Slow-rotate step is ~⅓ of the full button-orbit step so the dedicated arrow-pad rotate
    // buttons feel like a deliberate fine adjustment, not a swipe. ApplyLookAround (in-place
    // rotation, same as the gizmo and 1-finger drag) per user spec: rotation must NEVER also
    // translate the camera.
    private const float SlowRotateStep = 10f;

    private void OnRotateLeftSlowClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyLookAround(-SlowRotateStep, 0f));

    private void OnRotateRightSlowClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyLookAround(SlowRotateStep, 0f));

    private void OnRotateLeftClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyOrbit(-ButtonOrbitStep, 0f));

    private void OnRotateRightClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyOrbit(ButtonOrbitStep, 0f));

    private void OnTiltUpClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyOrbit(0f, ButtonOrbitStep));

    private void OnTiltDownClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyOrbit(0f, -ButtonOrbitStep));

    // Pan ▲ moves the focus forward (into the scene), ▼ pulls it back toward the camera.
    private void OnPanUpClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyPan(0f, ButtonPanStep));

    private void OnPanDownClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyPan(0f, -ButtonPanStep));

    private void OnPanLeftClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyPan(-ButtonPanStep, 0f));

    private void OnPanRightClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyPan(ButtonPanStep, 0f));

    private void OnZoomInClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyZoom(ButtonZoomFactor));

    private void OnZoomOutClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyZoom(1f / ButtonZoomFactor));

    // Wys. ▲ / ▼ buttons now move the camera target up/down in world-Z (vertical translation),
    // regardless of camera pitch. The earlier tilt mapping was confusing — users expect "up"
    // to lift the camera straight up. ApplyVertical clamps Target.Z to [-2000, 8000] m so a
    // runaway click can't push the target off the mesh and turn the view into pure sky.
    private void OnRaiseClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyVertical(ButtonVerticalStep));

    private void OnLowerClicked(object? sender, EventArgs e) => StepCamera(() => controller.ApplyVertical(-ButtonVerticalStep));

    /// <summary>
    /// Points the camera at a world-space target from a given distance, preserving the current
    /// orbit angle (azimuth/pitch). Used to keep the 3D view framed on the same place the 2D map
    /// was centred on when switching into 3D. Distance is clamped to the controller's zoom range.
    /// </summary>
    /// <param name="target">World-space focal point (X east, Y north, Z up).</param>
    /// <param name="distance">Desired camera distance in metres; clamped to the valid zoom range.</param>
    public void FocusOnWorld(Vector3 target, float distance)
    {
        Camera.Target = target;
        Camera.Distance = Math.Clamp(distance, controller.MinDistance, controller.MaxDistance);
        Canvas.InvalidateSurface();
    }

    /// <summary>Positions the camera so the entire terrain fits in view.</summary>
    public void FrameMesh()
    {
        if (WorldFrame is not { } frame)
        {
            return;
        }

        Camera.Target = Vector3.Zero;
        Camera.Distance = Math.Max(frame.HorizontalExtent * 2.5f, 5_000f);
        Camera.AzimuthRadians = MathF.PI / 4f;
        Camera.PitchRadians = MathF.PI / 4f;

        // Push safety bounds into the controller so the camera can't tunnel through the surface
        // (CameraFloorZ = highest world-Z + a 50 m clearance) and Pan can't drag the target off
        // the mesh footprint (Target X/Y clamped to ±HorizontalExtent around the mesh centre).
        // Find the global max elevation across ALL loaded tiles, not just the WorldFrame one —
        // each tile holds its own subset of vertices.
        float globalMaxZ = float.NegativeInfinity;
        if (Tiles is { Count: > 0 } tiles)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i].MaxElevationZ > globalMaxZ)
                {
                    globalMaxZ = tiles[i].MaxElevationZ;
                }
            }
        }
        controller.CameraFloorZ = float.IsNegativeInfinity(globalMaxZ) ? float.NaN : globalMaxZ + 50f;
        controller.MinTargetX = frame.Center.X - frame.HorizontalExtent;
        controller.MaxTargetX = frame.Center.X + frame.HorizontalExtent;
        controller.MinTargetY = frame.Center.Y - frame.HorizontalExtent;
        controller.MaxTargetY = frame.Center.Y + frame.HorizontalExtent;

        Canvas.InvalidateSurface();
    }

    private static void OnTilesChanged(BindableObject bindable, object oldValue, object newValue)
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
        // Mirror the latest camera angles onto the orbit gizmo on every paint so its marker
        // stays in sync no matter how the camera was moved (gestures, gizmo, keyboard, buttons).
        // The gizmo's BindableProperty only re-paints when the float changes, so a no-op camera
        // frame is free.
        SyncOrbitGizmo();

        var canvas = e.Surface.Canvas;

        if (Tiles is not { Count: > 0 } tiles || WorldFrame is not { } frame)
        {
            // The view can paint BEFORE the DEM finishes auto-loading. canvas.Clear() with no
            // argument leaves the surface transparent → underlying page background bleeds through
            // and reads as solid white on mobile. Fill with the sky colour so the empty 3D scene
            // looks like a placeholder rather than a blank page.
            canvas.Clear(new SkiaSharp.SKColor(0x6C, 0x8E, 0xB0));
            return;
        }

        // Fit the clip planes to the scene each frame (distance changes with zoom). A scene radius
        // padded past the mesh half-extent covers the diagonal corners + vertical relief. Used by both
        // the GPU and Skia paths, and by overlay projection, so they stay consistent.
        var (near, far) = CameraClipPlanes.Fit(Camera.Distance, frame.HorizontalExtent * 1.25f);
        Camera.NearPlane = near;
        Camera.FarPlane = far;

        // Project the overlays once — needed by both the GPU and Skia paths. The stateful projectors reuse
        // their world cache + screen buffers, so during a gesture this is just the per-frame screen
        // transform. All project against the shared world frame (tile 0), with the same camera, so they
        // line up whether the terrain is drawn by GL or Skia.
        IReadOnlyList<ProjectedTrail>? projectedTrails = null;
        if (Trails is { Count: > 0 } trailsList && Raster is not null)
        {
            projectedTrails = trailProjector.Project(
                trailsList, Raster, frame, Camera, e.Info.Width, e.Info.Height);
        }

        ProjectedRoute? projectedRoute = null;
        if (Route is not null && Raster is not null)
        {
            projectedRoute = routeProjector.Project(
                Route, Raster, frame, Camera, e.Info.Width, e.Info.Height);
        }

        IReadOnlyList<ProjectedClimbingArea>? projectedClimbing = null;
        if (ClimbingAreas is { Count: > 0 } areas && Raster is not null)
        {
            projectedClimbing = climbingProjector.Project(
                areas, Raster, frame, Camera, e.Info.Width, e.Info.Height, ClimbingMarkerLiftMeters);
        }

        IReadOnlyList<ProjectedPoi>? projectedPois = null;
        if (Pois is { Count: > 0 } pois && Raster is not null)
        {
            projectedPois = poiProjector.Project(
                pois, Raster, frame, Camera, e.Info.Width, e.Info.Height, PoiMarkerLiftMeters);
        }

        // Peaks carry their own DEM elevation, so projection needs no raster lookup.
        IReadOnlyList<ProjectedPeak>? projectedPeaks = null;
        if (Peaks is { Count: > 0 } peaks)
        {
            projectedPeaks = peakProjector.Project(
                peaks, null, frame, Camera, e.Info.Width, e.Info.Height, PeakMarkerLiftMeters);
        }

        // Single GPS fix: wrap into our reusable one-element buffer so the projector keeps its
        // world-cache hit when only the contained data shifts (it compares list reference).
        ProjectedUserLocation? projectedUserLocation = null;
        if (UserLocation is { } fix)
        {
            userLocationBuffer[0] = fix;
            IReadOnlyList<ProjectedUserLocation> projected = userLocationProjector.Project(
                userLocationBuffer, Raster, frame, Camera, e.Info.Width, e.Info.Height, UserLocationMarkerLiftMeters);
            if (projected.Count > 0)
            {
                projectedUserLocation = projected[0];
            }
        }

#if WINDOWS || ANDROID
        // GPU engine: GL draws the depth-buffered terrain, then the Skia overlays (trails / route /
        // markers / peak labels) are drawn over it with the same camera so they register. Any GL/shader
        // failure disables it for the session and falls through to the all-Skia renderer below.
        uint glFramebuffer = 0;
        if (e.BackendRenderTarget is { } renderTarget && renderTarget.GetGlFramebufferInfo(out GRGlFramebufferInfo fbInfo))
        {
            glFramebuffer = fbInfo.FramebufferObjectId;
        }

        if (UseGlRenderer && TryRenderTerrainGl(tiles, e.Info.Width, e.Info.Height, glFramebuffer))
        {
            // GL already drew the (depth-occluded) trails + route; Skia only adds the markers/labels on top.
            renderer.DrawOverlays(canvas, null, null, projectedClimbing, projectedPois, projectedPeaks, projectedUserLocation);
            return;
        }
#endif

        // depthMap = null disables trail / route / climbing occlusion: trails are drawn always on top
        // of the mesh (the visual the user wants) and it drops a per-frame depth-grid fill.
        renderer.RenderTiles(canvas, e.Info.Width, e.Info.Height, tiles, Camera, frameScratch, null, projectedTrails, projectedRoute, projectedClimbing, projectedPois, projectedPeaks, projectedUserLocation);
    }

    /// <summary>
    /// 1-finger drag on the mesh = PAN. World moves under the finger (Cesium / Google Maps
    /// convention). Translation only — rotation lives on the orbit-gizmo widget so a drag never
    /// has a hidden second meaning. ApplyPan inverts the deltas internally so the world tracks
    /// the finger rather than runs away from it.
    /// </summary>
    private void OnMeshPan(object? sender, PanUpdatedEventArgs e)
    {
        // On Windows the mouse is driven by the raw pointer handlers (OnPlatformPointer*); the touch
        // PanGestureRecognizer would otherwise fight them. Touch platforms keep using this gesture.
        if (DeviceInfo.Current.Platform == DevicePlatform.WinUI)
        {
            return;
        }

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
                // Drag-to-pan: invert deltas so the world tracks the finger.
                controller.ApplyPan(-dx, -dy);
                Canvas.InvalidateSurface();
                return;
        }
    }

    private void OnTranslatePan(object? sender, PanUpdatedEventArgs e)
    {
        if (DeviceInfo.Current.Platform == DevicePlatform.WinUI)
        {
            return;
        }

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

    // pinchActive used to mediate the 2-finger pan vs pinch race. The Cesium-style gesture set
    // doesn't bind 2-finger pan at all, so the flag is no longer read — kept (with pragma) only
    // because OnPinch still sets it, in case we later restore a 2-finger gesture that needs to
    // know when zoom is in progress.
#pragma warning disable CS0414, IDE0044
    private bool pinchActive;
#pragma warning restore CS0414, IDE0044

    private void OnPinch(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started)
        {
            pinchActive = true;
            lastPinchScale = 1.0;
            return;
        }
        if (e.Status == GestureStatus.Completed || e.Status == GestureStatus.Canceled)
        {
            pinchActive = false;
            return;
        }
        if (e.Status != GestureStatus.Running)
        {
            return;
        }

        // Platform divergence in PinchGestureUpdatedEventArgs.Scale:
        //   - Windows / macOS / iOS report CUMULATIVE scale relative to gesture start, so per-frame
        //     delta = current / lastReported.
        //   - Android reports PER-UPDATE delta directly (each update is the multiplier vs. the
        //     previous one, typically 0.97..1.03 even mid-gesture).
        // Confirmed against `adb logcat`: 10 consecutive Running updates each at 0.987 — would mean
        // cumulative -1.3% total if treated as cumulative, but actually mean -1.3% PER STEP.
        // Trust the platform-native interpretation rather than try to unify in one direction.
        double perFrame;
#if ANDROID
        perFrame = e.Scale;
#else
        if (lastPinchScale <= 0)
        {
            lastPinchScale = e.Scale;
        }
        perFrame = e.Scale / lastPinchScale;
        lastPinchScale = e.Scale;
#endif

        // Power-2.5 boost so a tiny finger-spread (perFrame ~ 1.02) produces a visible zoom step
        // on a phone screen — without it pinch barely moved because two-finger spread between
        // 60 Hz update frames is only a couple of pixels.
        double boosted = Math.Pow(perFrame, 2.5);
        controller.ApplyZoom((float)boosted);
        Canvas.InvalidateSurface();
    }

    /// <summary>
    /// Puts keyboard focus on the 3D canvas so the arrow keys (pan) and WASD (orbit) work without the user
    /// first having to click it. Called by the host page when 3D mode is entered. No-op off Windows.
    /// </summary>
    public void FocusForKeyboard()
    {
#if WINDOWS
        // Focus() lives on UIElement; the platform view is a SwapChainPanel (not a Control).
        wheelTarget?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
#endif
    }

#if WINDOWS
    private Microsoft.UI.Xaml.UIElement? wheelTarget;
    private Microsoft.UI.Xaml.UIElement? keyboardRoot;
    private Microsoft.UI.Xaml.Input.KeyEventHandler? keyDownHandler;

    // Mouse drag on Windows: MAUI's PanGestureRecognizer is touch/pen only, so we drive orbit/pan from raw
    // pointer events. Left button = orbit, right button = pan. 0 = not dragging.
    private int mouseDragButton;
    private Windows.Foundation.Point lastPointerPosition;

    // Keyboard-step constants tuned to feel close to one drag-pixel of the gesture
    // recognisers (controller.OrbitSensitivity = 0.005 rad/px, PanSensitivity = 0.001 m/px/m).
    private const float KeyOrbitPixelStep = 16f;
    private const float KeyPanPixelStep = 24f;
    private const float KeyZoomFactor = 1.1f;

    private void OnCanvasHandlerChanged(object? sender, EventArgs e)
    {
        DetachWheelHandler();

        // A new handler means a new platform view and a fresh GL context, so every GPU object the renderer
        // cached (program, VAOs, FBOs, textures) belongs to the dead context. Toggling 2D⇄3D recycles the
        // SKGLView handler this way; the IsProgram context-loss check inside the renderer doesn't always catch
        // it, leaving only the sky clear ("blue screen"). Drop the renderer so the next paint rebuilds clean.
        // Don't Dispose() here — that issues GL deletes with no context current; the old context's objects are
        // freed when it dies. Just release our reference and re-enable GL in case a failure had disabled it.
        glRenderer = null;
        glDisabled = false;
        // The fresh renderer has no ortho texture yet (it's pushed imperatively, not per frame), so re-flag it
        // for re-upload; trails/route/roads/peaks re-upload on their own since they're projected each frame.
        orthoPathDirty = true;

        if (Canvas.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement element)
        {
            wheelTarget = element;
            wheelTarget.PointerWheelChanged += OnPointerWheelChanged;

            // Make the platform view focusable so keyboard events route here. The SKGLView platform view is a
            // SwapChainPanel (a UIElement, NOT a Control), so IsTabStop/Focus must be set on UIElement —
            // the old `is Control` cast silently failed and no key ever reached us.
            element.IsTabStop = true;

            // Subscribe via AddHandler with handledEventsToo:true rather than "+= KeyDown".
            // Character keys (WASD) bubble up from the focused child already marked Handled,
            // so a plain CLR subscription only ever sees the arrow keys; AddHandler with
            // handledEventsToo receives the event regardless and lets WASD orbit work too.
            keyDownHandler ??= OnPlatformKeyDown;
            element.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler, handledEventsToo: true);

            // Focusing a SwapChainPanel to capture keys proved unreliable, so also listen at the window
            // root (XamlRoot.Content), which always receives KeyDown. The handler is gated on this view's
            // IsVisible so it only drives the camera while 3D mode is on. handledEventsToo:true so it fires
            // even though focus-navigation marks the arrow keys handled.
            keyboardRoot = element.XamlRoot?.Content as Microsoft.UI.Xaml.UIElement;
            keyboardRoot?.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler, handledEventsToo: true);

            element.PointerPressed += OnPlatformPointerPressed;
            element.PointerMoved += OnPlatformPointerMoved;
            element.PointerReleased += OnPlatformPointerReleased;
            element.PointerCaptureLost += OnPlatformPointerReleased;
        }
    }

    private void DetachWheelHandler()
    {
        if (wheelTarget is not null)
        {
            wheelTarget.PointerWheelChanged -= OnPointerWheelChanged;
            if (keyDownHandler is not null)
            {
                wheelTarget.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler);
            }
            wheelTarget.PointerPressed -= OnPlatformPointerPressed;
            wheelTarget.PointerMoved -= OnPlatformPointerMoved;
            wheelTarget.PointerReleased -= OnPlatformPointerReleased;
            wheelTarget.PointerCaptureLost -= OnPlatformPointerReleased;
            wheelTarget = null;
        }

        if (keyboardRoot is not null && keyDownHandler is not null)
        {
            keyboardRoot.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler);
            keyboardRoot = null;
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

    private void OnPlatformPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var element = (Microsoft.UI.Xaml.UIElement)sender;

        // Clicking the canvas grabs keyboard focus so subsequent KeyDown events route here.
        // Focus() is on UIElement (the platform view is a SwapChainPanel, not a Control).
        element.Focus(Microsoft.UI.Xaml.FocusState.Pointer);

        // If the window-root key handler wasn't attached at handler-change time (XamlRoot not ready yet),
        // attach it now that the view is definitely live — guarantees keyboard works after any interaction.
        if (keyboardRoot is null && keyDownHandler is not null
            && element.XamlRoot?.Content is Microsoft.UI.Xaml.UIElement root)
        {
            keyboardRoot = root;
            keyboardRoot.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler, handledEventsToo: true);
        }

        var props = e.GetCurrentPoint(element).Properties;
        mouseDragButton = props.IsLeftButtonPressed ? 1 : props.IsRightButtonPressed ? 2 : 0;
        if (mouseDragButton != 0)
        {
            lastPointerPosition = e.GetCurrentPoint(element).Position;
            element.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnPlatformPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (mouseDragButton == 0)
        {
            return;
        }

        var element = (Microsoft.UI.Xaml.UIElement)sender;
        var point = e.GetCurrentPoint(element);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            mouseDragButton = 0;
            return;
        }

        float dx = (float)(point.Position.X - lastPointerPosition.X);
        float dy = (float)(point.Position.Y - lastPointerPosition.Y);
        lastPointerPosition = point.Position;

        if (mouseDragButton == 1)
        {
            // Left-drag orbits around the focus (camera circles the scene).
            controller.ApplyOrbit(dx, -dy);
        }
        else
        {
            // Right-drag looks around in place — the camera stays put and turns its view direction.
            // dy is NOT inverted here (unlike orbit): dragging up looks up.
            controller.ApplyLookAround(dx, dy);
        }

        Canvas.InvalidateSurface();
        e.Handled = true;
    }

    private void OnPlatformPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        mouseDragButton = 0;
        ((Microsoft.UI.Xaml.UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void OnPlatformKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // The handler also listens at the window root, so ignore keys unless 3D mode is actually showing.
        if (!IsVisible)
        {
            return;
        }

        bool handled = true;
        switch (e.Key)
        {
            // Pan with the arrow keys on the ground plane — "move the camera through space".
            // ↑ moves the focus forward (into the scene), ↓ pulls it back, ← / → strafe left / right,
            // matching the on-screen Przesuń pad.
            case Windows.System.VirtualKey.Up:
                controller.ApplyPan(0f, KeyPanPixelStep);
                break;
            case Windows.System.VirtualKey.Down:
                controller.ApplyPan(0f, -KeyPanPixelStep);
                break;
            case Windows.System.VirtualKey.Left:
                controller.ApplyPan(-KeyPanPixelStep, 0f);
                break;
            case Windows.System.VirtualKey.Right:
                controller.ApplyPan(KeyPanPixelStep, 0f);
                break;

            // A / D orbit (swing azimuth); W / S move forward / backward on the ground plane (dolly through
            // the scene), matching the FPS convention the user asked for.
            case Windows.System.VirtualKey.A:
                controller.ApplyOrbit(-KeyOrbitPixelStep, 0f);
                break;
            case Windows.System.VirtualKey.D:
                controller.ApplyOrbit(KeyOrbitPixelStep, 0f);
                break;
            case Windows.System.VirtualKey.W:
                controller.ApplyPan(0f, KeyPanPixelStep);
                break;
            case Windows.System.VirtualKey.S:
                controller.ApplyPan(0f, -KeyPanPixelStep);
                break;

            // Vertical pan (raise / lower the camera target).
            case Windows.System.VirtualKey.Q:
                controller.ApplyVertical(KeyPanPixelStep);
                break;
            case Windows.System.VirtualKey.E:
                controller.ApplyVertical(-KeyPanPixelStep);
                break;

            // Zoom in / out with +/- (both numpad and main-row variants).
            case Windows.System.VirtualKey.Add:
            case (Windows.System.VirtualKey)187:  // VK_OEM_PLUS
                controller.ApplyZoom(KeyZoomFactor);
                break;
            case Windows.System.VirtualKey.Subtract:
            case (Windows.System.VirtualKey)189:  // VK_OEM_MINUS
                controller.ApplyZoom(1f / KeyZoomFactor);
                break;

            default:
                handled = false;
                break;
        }

        if (handled)
        {
            Canvas.InvalidateSurface();
            e.Handled = true;
        }
    }
#endif  // WINDOWS — mouse-wheel + keyboard region close

#if WINDOWS || ANDROID
    // ── Real GPU terrain engine (OpenGL ES on the SKGLView context) ───────────────────────────────
    // Flip to false to force the Skia renderer. On any GL/shader failure we fall back automatically.
    // ANDROID: SkiaSharp/MAUI on Android exposes the on-screen FBO as 0 with no intermediate
    // backend FBO. Even after rebinding to whatever glGetIntegerv reports, Skia's compositor
    // re-paints its (empty) logical surface over our GL output → user sees white. The raw-GL
    // engine needs a different surface bridge here (drawing through Skia's own GR context, or
    // a textured Skia post-pass). Until that ships, fall back to the Skia canvas renderer —
    // which on SKGLView is still GPU-accelerated through Skia's own GL backend, just without
    // our custom depth buffer / texture-drape shaders. Mesh renders fine; ortho lands as the
    // hypsometric colouring instead of the real photo until the GL bridge is fixed.
#if ANDROID
    private static readonly bool UseGlRenderer = false;
#else
    private static readonly bool UseGlRenderer = true;
#endif

    private Services.Terrain3DGlRenderer? glRenderer;
    private bool glDisabled;

    // Decoded ortho pixels cached by path so re-entering 3D (which rebuilds the renderer) re-uploads from
    // memory instead of decoding the large PNG from disk every time.
    // Decoded ortho tiles cached by their path signature, so re-entering 3D (which rebuilds the renderer)
    // re-uploads from memory instead of decoding the large PNGs from disk every time.
    private string? cachedOrthoSignature;
    private List<(byte[] Rgba, int Width, int Height)>? cachedOrthoDecoded;

    private bool TryRenderTerrainGl(IReadOnlyList<TerrainMesh3D> tiles, int width, int height, uint framebuffer)
    {
        if (glDisabled)
        {
            return false;
        }

        try
        {
            glRenderer ??= new Services.Terrain3DGlRenderer();

            // Push a changed ortho image to the GL renderer once (it uploads on the GL thread next Render).
            if (orthoPathDirty)
            {
                orthoPathDirty = false;
                if (OrthoTextureCells is { Count: > 0 } cells)
                {
                    // MBTiles-composited textures: bytes already RGBA8, skip the PNG decoder.
                    ApplyOrthoTextureCells(glRenderer, cells);
                }
                else
                {
                    IReadOnlyList<string>? orthoPaths = OrthoTexturePaths is { Count: > 0 }
                        ? OrthoTexturePaths
                        : (OrthoTexturePath is { Length: > 0 } single ? new[] { single } : null);
                    ApplyOrthoTextures(glRenderer, orthoPaths);
                }
            }

            // GL draws the terrain AND the depth-tested trail/route lines (so the terrain occludes them).
            glRenderer.Render(width, height, tiles, Camera, framebuffer, Trails, Raster, Route, Roads);
            // Hand GL state back to Skia so any later 2D drawing on this surface behaves.
            Canvas.GRContext?.ResetContext();
            return true;
        }
        catch (Exception)
        {
            // One failure → drop to the proven Skia path for the rest of the session.
            glDisabled = true;
            glRenderer?.Dispose();
            glRenderer = null;
            return false;
        }
    }

    // Decodes the ortho tiles to tightly-packed top-row-first RGBA8 (row 0 = north, matching the mesh UVs)
    // and hands the set to the GL renderer. A null/empty list clears the ortho (back to the hypsometric
    // tint). If any tile fails to decode the whole set is abandoned, so textures never mis-align to cells.
    private void ApplyOrthoTextures(Services.Terrain3DGlRenderer renderer, IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            cachedOrthoSignature = null;
            cachedOrthoDecoded = null;
            renderer.SetOrthoTextures(Array.Empty<(byte[], int, int)>());
            return;
        }

        string signature = string.Join("|", paths);
        if (signature == cachedOrthoSignature && cachedOrthoDecoded is not null)
        {
            renderer.SetOrthoTextures(cachedOrthoDecoded);
            return;
        }

        var decoded = new List<(byte[] Rgba, int Width, int Height)>(paths.Count);
        foreach (string path in paths)
        {
            if (DecodeOrtho(path) is { } tile)
            {
                decoded.Add(tile);
            }
        }

        if (decoded.Count != paths.Count)
        {
            cachedOrthoSignature = null;
            cachedOrthoDecoded = null;
            renderer.SetOrthoTextures(Array.Empty<(byte[], int, int)>());
            return;
        }

        cachedOrthoSignature = signature;
        cachedOrthoDecoded = decoded;
        renderer.SetOrthoTextures(decoded);
    }

    // Skips DecodeOrtho entirely: pre-composited cells (e.g. from an MBTiles archive) already carry
    // RGBA8 pixels and just need to flow through to SetOrthoTextures in row-major order.
    private void ApplyOrthoTextureCells(Services.Terrain3DGlRenderer renderer, IReadOnlyList<OrthoTextureCell> cells)
    {
        var decoded = new List<(byte[] Rgba, int Width, int Height)>(cells.Count);
        foreach (OrthoTextureCell cell in cells)
        {
            decoded.Add((cell.Rgba, cell.Width, cell.Height));
        }
        cachedOrthoSignature = null; // bypass the path-keyed cache; cells are the source of truth now
        cachedOrthoDecoded = null;
        renderer.SetOrthoTextures(decoded);
    }

    private static (byte[] Rgba, int Width, int Height)? DecodeOrtho(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = System.IO.File.OpenRead(path);
            using var codec = SkiaSharp.SKCodec.Create(stream);
            if (codec is null)
            {
                return null;
            }

            var info = new SkiaSharp.SKImageInfo(
                codec.Info.Width,
                codec.Info.Height,
                SkiaSharp.SKColorType.Rgba8888,
                SkiaSharp.SKAlphaType.Unpremul);
            using var bitmap = new SkiaSharp.SKBitmap(info);
            if (codec.GetPixels(info, bitmap.GetPixels()) != SkiaSharp.SKCodecResult.Success)
            {
                return null;
            }

            return (bitmap.Bytes, info.Width, info.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }
#endif
}