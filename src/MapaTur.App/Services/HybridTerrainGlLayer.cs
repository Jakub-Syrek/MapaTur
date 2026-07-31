using System.Numerics;

using MapaTur.Application.Terrain;

using Serilog;

using Silk.NET.OpenGLES;

namespace MapaTur.App.Services;

/// <summary>
/// Bounded GL residency for the unified RMP3 terrain surface. The layer owns no shader and no material
/// textures: it draws through the terrain program, so orthophoto, lighting, fog and diagnostics stay identical
/// to the legacy DEM. Missing pages are never holes because the ordinary DEM is drawn immediately afterwards.
/// </summary>
internal sealed unsafe class HybridTerrainGlLayer
{
    private const int GeometryUploadsPerFrame = 2;

    private readonly Dictionary<HybridTerrainPageKey, GpuPage> gpuPages = [];
    private readonly HashSet<HybridTerrainPageKey> drawableKeys = [];

    private string? root;
    private long maxResidentBytes;
    private bool active;
    private Task<HybridTerrainPageCatalog>? catalogTask;
    private CancellationTokenSource? catalogCancellation;
    private HybridTerrainStreamingManager? streaming;
    private bool catalogFailureLogged;
    private bool drawableLogged;
    private bool gpuReleasePending;

    public bool IsConfigured => root is not null && Directory.Exists(root);

    public bool HasDrawablePages => drawableKeys.Count > 0;

    /// <summary>GPU-resident pages eligible to draw (pre frustum-cull); feeds the renderer's RMP3 counter.</summary>
    public int DrawableCount => drawableKeys.Count;

    public void Configure(string? newRoot, long newMaxResidentBytes)
    {
        string? normalized = string.IsNullOrWhiteSpace(newRoot)
            ? null
            : Path.GetFullPath(newRoot);
        long normalizedBudget = Math.Max(1, newMaxResidentBytes);
        if (string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase)
            && maxResidentBytes == normalizedBudget)
        {
            return;
        }

        DeactivateCpu();
        active = false;
        gpuReleasePending = true;
        root = normalized;
        maxResidentBytes = normalizedBudget;
    }

    public void PrepareFrame(
        GL g,
        Camera3D camera,
        int viewportWidth,
        int viewportHeight,
        bool requestedEnabled)
    {
        if (gpuReleasePending)
        {
            ReleaseGpu(g);
            gpuReleasePending = false;
        }

        bool hasConfiguration = IsConfigured;
        HybridTerrainFeatureTransition transition = HybridTerrainFeatureSwitch.Resolve(
            active,
            requestedEnabled,
            hasConfiguration);
        if (transition == HybridTerrainFeatureTransition.Deactivate)
        {
            DeactivateCpu();
            ReleaseGpu(g);
            active = false;
            Log.Information("[RockRMP3] disabled — streaming stopped and GPU residency released");
            return;
        }

        if (transition == HybridTerrainFeatureTransition.Activate)
        {
            active = true;
            StartCatalogLoad();
        }

        if (!active)
        {
            return;
        }

        if (catalogTask is null)
        {
            StartCatalogLoad();
        }

        if (streaming is null)
        {
            if (catalogTask is null || !catalogTask.IsCompleted)
            {
                return;
            }

            if (!catalogTask.IsCompletedSuccessfully)
            {
                if (!catalogFailureLogged)
                {
                    Log.Warning(catalogTask.Exception, "[RockRMP3] catalog load failed for {Root}", root);
                    catalogFailureLogged = true;
                }

                return;
            }

            HybridTerrainPageCatalog catalog = catalogTask.Result;
            if (catalog.Pages.Count == 0)
            {
                return;
            }

            streaming = new HybridTerrainStreamingManager(
                catalog,
                maxResidentBytes,
                maxStagingBytes: 64L * 1024 * 1024,
                maxConcurrentLoads: 4);
            Log.Information(
                "[RockRMP3] catalog ready: {Pages} pages, geometry budget {MB:F0} MB",
                catalog.Pages.Count,
                maxResidentBytes / (1024.0 * 1024.0));
        }

        HybridTerrainStreamingUpdate update = streaming.Update(
            new HybridTerrainPageSelectionOptions
            {
                Camera = camera,
                AspectRatio = viewportWidth / (float)Math.Max(1, viewportHeight),
                ViewportHeightPixels = viewportHeight,
                MaxErrorPixels = 1.0,
                HysteresisFraction = 0.25,
                PrefetchRootRing = 1,
            });

        foreach (HybridTerrainPageKey key in update.EvictedKeys)
        {
            DeletePage(g, key);
        }

        UploadReadyPages(g, update.ReadyForUpload);
        drawableKeys.Clear();
        foreach (HybridTerrainMeshPage page in update.DrawablePages)
        {
            var key = new HybridTerrainPageKey(page.PageX, page.PageY, page.Lod);
            if (gpuPages.ContainsKey(key))
            {
                drawableKeys.Add(key);
            }
        }

        if (!drawableLogged && drawableKeys.Count > 0)
        {
            drawableLogged = true;
            Log.Information(
                "[RockRMP3] GPU ready: {Drawable} drawable, {ResidentMB:F1} MB CPU resident, "
                + "{Desired} desired, {InFlight} in flight",
                drawableKeys.Count,
                update.ResidentBytes / (1024.0 * 1024.0),
                update.Desired,
                update.InFlight);
        }
    }

    /// <summary>Draws the selected replacement pages before the ordinary DEM using the already-bound terrain program.</summary>
    public int DrawTerrainProgram(
        GL g,
        Matrix4x4 cullingMvp,
        int hybridCompactLocation,
        int hybridPageMinLocation,
        int hybridPageExtentLocation,
        int useOrthoLocation,
        int useDet25Location,
        int useDet05Location,
        int isBaseSkinLocation)
    {
        if (drawableKeys.Count == 0)
        {
            return 0;
        }

        g.Uniform1(hybridCompactLocation, 1f);
        g.Uniform1(useOrthoLocation, 0);
        g.Uniform1(useDet25Location, 0);
        g.Uniform1(useDet05Location, 0);
        g.Uniform1(isBaseSkinLocation, 0f);
        int drawn = 0;
        foreach (HybridTerrainPageKey key in drawableKeys)
        {
            if (!gpuPages.TryGetValue(key, out GpuPage? page)
                || !FrustumCuller.IsAabbVisible(cullingMvp, page.WorldMin, page.WorldMin + page.WorldExtent))
            {
                continue;
            }

            g.Uniform3(hybridPageMinLocation, page.WorldMin.X, page.WorldMin.Y, page.WorldMin.Z);
            g.Uniform3(hybridPageExtentLocation, page.WorldExtent.X, page.WorldExtent.Y, page.WorldExtent.Z);
            g.BindVertexArray(page.Vao);
            g.DrawElements(
                PrimitiveType.Triangles,
                (uint)page.IndexCount,
                DrawElementsType.UnsignedShort,
                (void*)0);
            drawn++;
        }

        g.BindVertexArray(0);
        g.Uniform1(hybridCompactLocation, 0f);
        return drawn;
    }

    public void Dispose(GL? g)
    {
        DeactivateCpu();
        if (g is not null)
        {
            ReleaseGpu(g);
        }

        active = false;
        gpuReleasePending = false;
    }

    private void StartCatalogLoad()
    {
        if (root is null || catalogTask is not null)
        {
            return;
        }

        catalogCancellation = new CancellationTokenSource();
        catalogTask = HybridTerrainPageCatalog.LoadAsync(root, catalogCancellation.Token);
        catalogFailureLogged = false;
        drawableLogged = false;
    }

    private void DeactivateCpu()
    {
        catalogCancellation?.Cancel();
        catalogCancellation?.Dispose();
        catalogCancellation = null;
        catalogTask = null;
        streaming?.Dispose();
        streaming = null;
        drawableKeys.Clear();
        drawableLogged = false;
    }

    private void UploadReadyPages(GL g, IReadOnlyList<HybridTerrainMeshPage> pages)
    {
        if (streaming is null)
        {
            return;
        }

        int uploaded = 0;
        foreach (HybridTerrainMeshPage page in pages)
        {
            var key = new HybridTerrainPageKey(page.PageX, page.PageY, page.Lod);
            if (gpuPages.ContainsKey(key))
            {
                streaming.ConfirmUploaded(key);
                continue;
            }

            if (uploaded >= GeometryUploadsPerFrame)
            {
                break;
            }

            uint vao = g.GenVertexArray();
            uint vertexBuffer = g.GenBuffer();
            uint indexBuffer = g.GenBuffer();
            g.BindVertexArray(vao);
            g.BindBuffer(BufferTargetARB.ArrayBuffer, vertexBuffer);
            g.BufferData<byte>(
                BufferTargetARB.ArrayBuffer,
                (nuint)page.VertexData.Length,
                page.VertexData,
                BufferUsageARB.StaticDraw);
            g.BindBuffer(BufferTargetARB.ElementArrayBuffer, indexBuffer);
            g.BufferData<ushort>(
                BufferTargetARB.ElementArrayBuffer,
                (nuint)(page.Indices.Length * sizeof(ushort)),
                page.Indices,
                BufferUsageARB.StaticDraw);

            const uint stride = HybridTerrainMeshPage.VertexStrideBytes;
            g.EnableVertexAttribArray(0);
            g.VertexAttribPointer(0, 3, VertexAttribPointerType.UnsignedShort, true, stride, (void*)0);
            g.EnableVertexAttribArray(1);
            g.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)14);
            g.EnableVertexAttribArray(2);
            g.VertexAttribPointer(2, 2, VertexAttribPointerType.Short, true, stride, (void*)6);
            g.EnableVertexAttribArray(3);
            g.VertexAttribPointer(3, 2, VertexAttribPointerType.UnsignedShort, true, stride, (void*)10);
            g.EnableVertexAttribArray(4);
            g.VertexAttribPointer(4, 1, VertexAttribPointerType.UnsignedByte, true, stride, (void*)15);
            g.BindVertexArray(0);

            gpuPages.Add(
                key,
                new GpuPage(
                    vao,
                    vertexBuffer,
                    indexBuffer,
                    page.IndexCount,
                    page.WorldMin,
                    page.WorldExtent));
            if (!streaming.ConfirmUploaded(key))
            {
                DeletePage(g, key);
                streaming.RejectUpload(key);
                continue;
            }

            uploaded++;
        }
    }

    private void ReleaseGpu(GL g)
    {
        foreach (HybridTerrainPageKey key in gpuPages.Keys.ToArray())
        {
            DeletePage(g, key);
        }

        drawableKeys.Clear();
    }

    private void DeletePage(GL g, HybridTerrainPageKey key)
    {
        if (!gpuPages.Remove(key, out GpuPage? page))
        {
            return;
        }

        g.DeleteVertexArray(page.Vao);
        g.DeleteBuffer(page.VertexBuffer);
        g.DeleteBuffer(page.IndexBuffer);
        drawableKeys.Remove(key);
    }

    private sealed record GpuPage(
        uint Vao,
        uint VertexBuffer,
        uint IndexBuffer,
        int IndexCount,
        Vector3 WorldMin,
        Vector3 WorldExtent);
}
