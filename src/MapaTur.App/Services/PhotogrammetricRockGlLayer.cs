using System.Numerics;

using MapaTur.Application.Terrain;

using Serilog;

using Silk.NET.OpenGLES;

namespace MapaTur.App.Services;

/// <summary>
/// Isolated GL layer for streamed RMP2 photogrammetry. All disk reads stay in
/// <see cref="ScannedRockStreamingManager"/>; this class only harvests finished pages and spends a bounded
/// upload budget on the render thread. The DEM remains untouched underneath, so any missing geometry,
/// material, unsupported BC1 path or failed page naturally falls back to the existing terrain.
/// </summary>
internal sealed unsafe class PhotogrammetricRockGlLayer
{
    private const uint GlCompressedRgbaS3tcDxt1 = 0x83F1;
    private const ushort ContinuousWorldMaterialPageId = 20;
    private const float ContinuousWorldMaximumReliefMeters = 2.8f;
    private const int GeometryUploadsPerFrame = 2;
    private const int MaterialUploadsPerFrame = 1;

    private const string VertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPosQ;\n" +
        "layout(location=1) in vec2 aNormalOct;\n" +
        "layout(location=2) in vec2 aUv;\n" +
        "layout(location=3) in float aAo;\n" +
        "layout(location=4) in float aSeam;\n" +
        "uniform mat4 uMvp;\n" +
        "uniform vec3 uWorldMin;\n" +
        "uniform vec3 uWorldExtent;\n" +
        "out vec3 vNormal;\n" +
        "out vec2 vUv;\n" +
        "out float vAo;\n" +
        "out vec3 vWorldPos;\n" +
        "out float vSeam;\n" +
        "vec3 octDecode(vec2 e){\n" +
        "  vec3 v=vec3(e.x,e.y,1.0-abs(e.x)-abs(e.y));\n" +
        "  if(v.z<0.0){ vec2 s=vec2(e.x>=0.0?1.0:-1.0,e.y>=0.0?1.0:-1.0); v.xy=(1.0-abs(v.yx))*s; }\n" +
        "  return normalize(v);\n" +
        "}\n" +
        "void main(){\n" +
        "  vec3 p=uWorldMin+(aPosQ*uWorldExtent);\n" +
        "  vWorldPos=p; vNormal=octDecode(aNormalOct); vUv=aUv; vAo=aAo; vSeam=aSeam;\n" +
        "  gl_Position=uMvp*vec4(p,1.0);\n" +
        "}\n";

    private const string FragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "precision highp sampler2D;\n" +
        "in vec3 vNormal;\n" +
        "in vec2 vUv;\n" +
        "in float vAo;\n" +
        "in vec3 vWorldPos;\n" +
        "in float vSeam;\n" +
        "uniform sampler2D uAlbedo;\n" +
        "uniform float uWorldProjected;\n" +
        "uniform vec3 uLightDir;\n" +
        "uniform float uAmbient;\n" +
        "uniform vec3 uSunColor;\n" +
        "uniform vec3 uSkyAmbient;\n" +
        "uniform vec3 uCameraPos;\n" +
        "uniform vec3 uFogColor;\n" +
        "uniform float uFogDensity;\n" +
        "uniform sampler2D uSceneDepth;\n" +
        "uniform float uSceneDepthOn;\n" +
        "uniform vec2 uDepthNearFar;\n" +
        "uniform float uMaxBehindTerrain;\n" +
        "out vec4 fragColor;\n" +
        "vec2 mirrorUv(vec2 value){ vec2 m=mod(value,2.0); return 1.0-abs(m-1.0); }\n" +
        "vec3 worldPattern(vec2 p,vec2 offset){\n" +
        "  mat2 r=mat2(0.819152,-0.573576,0.573576,0.819152);\n" +
        "  vec3 broad=texture(uAlbedo,mirrorUv((p/43.0)+offset)).rgb;\n" +
        "  return broad;\n" +
        "}\n" +
        "vec3 worldTriplanar(vec3 p,vec3 n){\n" +
        "  vec3 w=pow(abs(n),vec3(5.0)); w/=max(w.x+w.y+w.z,0.0001);\n" +
        "  vec3 x=worldPattern(p.yz,vec2(0.11,0.47));\n" +
        "  vec3 y=worldPattern(p.xz,vec2(0.53,0.19));\n" +
        "  vec3 z=worldPattern(p.xy,vec2(0.79,0.31));\n" +
        "  return (x*w.x)+(y*w.y)+(z*w.z);\n" +
        "}\n" +
        "void main(){\n" +
        "  if(uSceneDepthOn>0.5){\n" +
        "    vec2 duv=gl_FragCoord.xy/vec2(textureSize(uSceneDepth,0));\n" +
        "    float n=uDepthNearFar.x; float f=uDepthNearFar.y;\n" +
        "    float ndcS=texture(uSceneDepth,duv).r*2.0-1.0;\n" +
        "    float ndcR=gl_FragCoord.z*2.0-1.0;\n" +
        "    float linS=(f*n)/(f-(ndcS*(f-n)));\n" +
        "    float linR=(f*n)/(f-(ndcR*(f-n)));\n" +
        "    if(linR-linS>uMaxBehindTerrain){ discard; }\n" +
        "  }\n" +
        "  vec3 n=normalize(vNormal);\n" +
        "  float ndl=max(dot(n,normalize(uLightDir)),0.0);\n" +
        "  vec3 albedo=uWorldProjected>0.5?worldTriplanar(vWorldPos,n):texture(uAlbedo,vUv).rgb;\n" +
        "  float skyVis=0.55+(0.45*clamp(n.z,0.0,1.0));\n" +
        "  vec3 lightSum=(uSkyAmbient*uAmbient*skyVis*vAo)+(uSunColor*ndl);\n" +
        "  lightSum=max(lightSum,uSkyAmbient*(0.42+(0.23*clamp(n.z,0.0,1.0))));\n" +
        "  vec3 lit=albedo*lightSum;\n" +
        "  float d=length(vWorldPos-uCameraPos);\n" +
        "  float fog=1.0-exp(-uFogDensity*d);\n" +
        "  float seamAlpha=smoothstep(0.02,0.98,vSeam);\n" +
        "  vec3 viewDir=normalize(uCameraPos-vWorldPos);\n" +
        "  float grazingAlpha=smoothstep(0.06,0.22,abs(dot(n,viewDir)));\n" +
        "  float rockAlpha=seamAlpha*grazingAlpha;\n" +
        "  if(rockAlpha<=0.001){ discard; }\n" +
        "  fragColor=vec4(mix(lit,uFogColor,clamp(fog,0.0,1.0)),rockAlpha);\n" +
        "}\n";

    private const string ShadowVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPosQ;\n" +
        "layout(location=4) in float aSeam;\n" +
        "uniform mat4 uLightVp;\n" +
        "uniform vec3 uWorldMin;\n" +
        "uniform vec3 uWorldExtent;\n" +
        "out float vSeam;\n" +
        "void main(){ vSeam=aSeam; gl_Position=uLightVp*vec4(uWorldMin+(aPosQ*uWorldExtent),1.0); }\n";

    private const string ShadowFragmentShaderSource =
        "#version 300 es\n" +
        "precision mediump float;\n" +
        "in float vSeam;\n" +
        "void main(){ if(vSeam<0.35){ discard; } }\n";

    private readonly Dictionary<ScannedRockPageKey, GpuPage> gpuPages = [];
    private readonly Dictionary<ushort, GpuMaterial> gpuMaterials = [];
    private readonly Dictionary<ushort, Task<RockMaterialPage>> materialLoads = [];
    private readonly HashSet<ushort> failedMaterials = [];
    private readonly HashSet<ScannedRockPageKey> drawableKeys = [];
    private readonly HashSet<ScannedRockPageKey> deferredDeletes = [];

    // Batching stron „kolor z orto" (2026-09-05, „leć ten batching pilot"): pomiar ON→OFF dał CPU +11 ms/klatkę
    // (391 stron × 3 passy). Strony tej samej komórki orto i kwadratu GroupCells×GroupCells komórek są scalane
    // w jeden VAO/draw (ScannedRockPageBatcher, TDD); grupa brudna rysuje strony pojedynczo do przebudowy.
    // terrainPacks = paczki CPU stron GPU-rezydentnych (do scalania; ~230 KB/strona). MAPATUR_ROCK_RMP2_GROUP = bok grupy.
    private readonly Dictionary<ScannedRockPageKey, TerrainVertexPack> terrainPacks = [];
    private readonly ScannedRockPageBatchTracker batches = new(GroupCellsFromEnv());
    private readonly Dictionary<ScannedRockGroupKey, GpuGroup> gpuGroups = [];
    private readonly List<ScannedRockPageStub> batchStubs = [];
    private const int GroupRebuildMaxPagesPerFrame = 16; // budżet przebudów w STRONACH (≈1 pełna grupa 4×4)
    private const int GroupSettleFrames = 12;            // debounce: grupa scalana dopiero, gdy skład nie zmienia się ~12 klatek
    private readonly List<TerrainShadedPage> unitCache = [];
    private int batchFrame;
    private bool contextLostPending;

    /// <summary>Skumulowana liczba przebudów (uploadów) grup — miara „rebuild storm” w logu.</summary>
    public int LastRebuilds { get; private set; }

    /// <summary>Jednostki rysowania z ostatniego PrepareFrame: zbudowane grupy vs strony pojedyncze (do logu/pomiaru).</summary>
    public int LastGroupUnits { get; private set; }

    public int LastSingleUnits { get; private set; }

    /// <summary>Tekstura ciągłego materiału skanu (strona 20, BC1 + mipy) — demo „skan zamiast granitu" na stromiznach terenu; 0 = jeszcze nie wgrana.</summary>
    public uint ContinuousMaterialTexture =>
        gpuMaterials.TryGetValue(ContinuousWorldMaterialPageId, out GpuMaterial? material) ? material.Texture : 0;

    private static int GroupCellsFromEnv() =>
        int.TryParse(Environment.GetEnvironmentVariable("MAPATUR_ROCK_RMP2_GROUP"), out int n) && n >= 1 ? n : 4;

    private string? root;
    private long maxResidentBytes;
    private Task<ScannedRockPageCatalog>? catalogTask;
    private ScannedRockStreamingManager? streaming;
    private bool catalogFailureLogged;
    private bool gpuReleasePending;
    private bool s3tcProbed;
    private bool s3tcSupported;
    private bool unsupportedLogged;
    private bool drawableLogged;
    private bool mainDrawLogged;
    private int residencyFrame;
    private int residencyLastLogFrame = -600;
    private int residencyLastDrawable = -1;

    private uint program;
    private uint shadowProgram;
    private int mvpLocation = -1;
    private int worldMinLocation = -1;
    private int worldExtentLocation = -1;
    private int albedoLocation = -1;
    private int worldProjectedLocation = -1;
    private int lightLocation = -1;
    private int ambientLocation = -1;
    private int sunColorLocation = -1;
    private int skyAmbientLocation = -1;
    private int cameraLocation = -1;
    private int fogColorLocation = -1;
    private int fogDensityLocation = -1;
    private int sceneDepthLocation = -1;
    private int sceneDepthOnLocation = -1;
    private int depthNearFarLocation = -1;
    private int maxBehindTerrainLocation = -1;
    private int shadowLightVpLocation = -1;
    private int shadowWorldMinLocation = -1;
    private int shadowWorldExtentLocation = -1;

    public bool HasDrawablePages => drawableKeys.Count > 0;

    /// <summary>
    /// Pilot "kolor z orto" (2026-09-05): when set, pages are NOT drawn by this layer's own program. They are
    /// uploaded in the TERRAIN tile layout (<see cref="ScannedRockPageTerrainRepacker"/>) and the renderer draws
    /// them inside its terrain pass (and the CSM caster pass) with the terrain program - so the page surface
    /// gets the ortho colour chain (base cell + det25/det05 arrays, de-blue, tone law), lighting, shadows and fog
    /// exactly like a tile. The scan albedo, the ghost-depth replacement and the isolated shaders stay unused.
    /// </summary>
    public bool TerrainShaded { get; set; }

    /// <summary>Resolves the base-ortho cell (index + world AABB) containing a world point; null = not known yet.</summary>
    public Func<Vector3, OrthoCellRef?>? OrthoCellResolver { get; set; }

    /// <summary>Drawable pages in terrain layout for the renderer's own tile loops (empty unless <see cref="TerrainShaded"/>).</summary>
    /// <summary>Jednostki rysowania programem terenu (cache z ostatniego PrepareFrame; renderer woła 3× na klatkę).</summary>
    public IReadOnlyList<TerrainShadedPage> TerrainShadedPages() =>
        TerrainShaded && drawableKeys.Count > 0 ? unitCache : Array.Empty<TerrainShadedPage>();

    public void Configure(string? newRoot, long newMaxResidentBytes)
    {
        string? normalized = string.IsNullOrWhiteSpace(newRoot)
            ? null
            : Path.GetFullPath(newRoot);
        bool sameConfiguration = string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase)
            && maxResidentBytes == newMaxResidentBytes;
        if (sameConfiguration
            && (normalized is null || catalogTask is not null || !Directory.Exists(normalized)))
        {
            return;
        }

        streaming?.Dispose();
        streaming = null;
        root = normalized;
        maxResidentBytes = newMaxResidentBytes;
        catalogTask = normalized is not null && Directory.Exists(normalized)
            ? ScannedRockPageCatalog.LoadAsync(normalized)
            : null;
        catalogFailureLogged = false;
        drawableLogged = false;
        mainDrawLogged = false;
        materialLoads.Clear();
        failedMaterials.Clear();
        drawableKeys.Clear();
        deferredDeletes.Clear();
        gpuReleasePending = true;
    }

    /// <summary>Zwolnij strony/materiały GPU przy najbliższym PrepareFrame (przełącznik „Skały fotogrametryczne” OFF);
    /// CPU-rezydentne strony zostają w managerze — ponowne włączenie = ponowny upload, bez czytania dysku.</summary>
    public void RequestGpuRelease()
    {
        gpuReleasePending = true;
        drawableLogged = false;
    }

    public void PrepareFrame(GL g, Camera3D camera, int viewportWidth, int viewportHeight, bool enabled)
    {
        HandleGpuReset(g);
        if (gpuReleasePending)
        {
            ReleaseGpu(g);
            gpuReleasePending = false;
        }

        if (!enabled || root is null || catalogTask is null)
        {
            drawableKeys.Clear();
            UpdateBatches(g, null, 1f);
            return;
        }

        if (streaming is null)
        {
            if (!catalogTask.IsCompleted)
            {
                return;
            }

            if (!catalogTask.IsCompletedSuccessfully)
            {
                if (!catalogFailureLogged)
                {
                    Log.Warning(catalogTask.Exception, "[RockRMP2] catalog load failed for {Root}", root);
                    catalogFailureLogged = true;
                }

                return;
            }

            ScannedRockPageCatalog catalog = catalogTask.Result;
            if (catalog.Pages.Count == 0)
            {
                return;
            }

            streaming = new ScannedRockStreamingManager(
                catalog,
                Math.Max(1, maxResidentBytes),
                maxConcurrentLoads: 4);
            Log.Information(
                "[RockRMP2] catalog ready: {Pages} page headers, geometry budget {MB:F0} MB",
                catalog.Pages.Count,
                maxResidentBytes / (1024.0 * 1024.0));
        }

        ProbeS3tc(g);
        if (!s3tcSupported)
        {
            if (!unsupportedLogged)
            {
                Log.Warning("[RockRMP2] BC1/S3TC unavailable — photogrammetry disabled, DEM fallback remains");
                unsupportedLogged = true;
            }

            drawableKeys.Clear();
            return;
        }

        ScannedRockStreamingUpdate update = streaming.Update(
            new ScannedRockPageSelectionOptions
            {
                Camera = camera,
                AspectRatio = viewportWidth / (float)Math.Max(1, viewportHeight),
                ViewportHeightPixels = viewportHeight,
                MaxErrorPixels = 1.0,
                HysteresisFraction = 0.25,
                PrefetchPageRing = 1,
            });
        foreach (ScannedRockPageKey key in update.EvictedKeys)
        {
            deferredDeletes.Add(key);
        }

        HarvestMaterialLoads(g);
        KickMaterialLoads(update.ResidentPages);
        UploadGeometry(g, update.ResidentPages);
        RebuildDrawableKeys(update.ResidentPages);
        if (!drawableLogged && drawableKeys.Count > 0)
        {
            drawableLogged = true;
            Log.Information(
                "[RockRMP2] GPU ready: {Drawable} drawable pages, {Resident} CPU-resident pages, "
                + "{Desired} desired, {InFlight} in flight",
                drawableKeys.Count,
                update.ResidentPages.Count,
                update.Desired,
                update.InFlight);
        }

        residencyFrame++;
        if ((drawableKeys.Count != residencyLastDrawable && residencyFrame - residencyLastLogFrame >= 60)
            || residencyFrame - residencyLastLogFrame >= 600
            || update.FailedKeys.Count > 0)
        {
            residencyLastDrawable = drawableKeys.Count;
            residencyLastLogFrame = residencyFrame;
            Log.Information(
                "[RockRMP2] residency: drawable={Drawable} gpu={Gpu} cpu={Cpu} desired={Desired} inFlight={InFlight} "
                + "loaded={Loaded} failed={Failed} residentMB={MB:F1}",
                drawableKeys.Count,
                gpuPages.Count,
                update.ResidentPages.Count,
                update.Desired,
                update.InFlight,
                update.LoadedKeys.Count,
                update.FailedKeys.Count,
                streaming.ResidentBytes / 1048576.0);
        }

        DeleteReplacedPages(g, update.DesiredKeys);
        UpdateBatches(g, camera, viewportWidth / (float)Math.Max(1, viewportHeight));
    }

    public void DrawMain(
        GL g,
        Matrix4x4 mvp,
        Camera3D camera,
        Vector3 lightDirection,
        float ambient,
        Vector3 sunColor,
        Vector3 skyAmbient,
        Vector3 fogColor,
        float fogDensity,
        uint sceneDepthTexture,
        float maximumDistanceMeters)
    {
        if (drawableKeys.Count == 0 || TerrainShaded)
        {
            return;
        }

        EnsurePrograms(g);
        g.UseProgram(program);
        UploadMatrix(g, mvpLocation, mvp);
        g.Uniform3(lightLocation, lightDirection.X, lightDirection.Y, lightDirection.Z);
        g.Uniform1(ambientLocation, ambient);
        g.Uniform3(sunColorLocation, sunColor.X, sunColor.Y, sunColor.Z);
        g.Uniform3(skyAmbientLocation, skyAmbient.X, skyAmbient.Y, skyAmbient.Z);
        Vector3 cameraPosition = camera.Position;
        g.Uniform3(cameraLocation, cameraPosition.X, cameraPosition.Y, cameraPosition.Z);
        g.Uniform3(fogColorLocation, fogColor.X, fogColor.Y, fogColor.Z);
        g.Uniform1(fogDensityLocation, fogDensity);
        g.Uniform1(albedoLocation, 0);
        bool depthReplacement = sceneDepthTexture != 0;
        g.Uniform1(sceneDepthLocation, 1);
        g.Uniform1(sceneDepthOnLocation, depthReplacement ? 1f : 0f);
        g.Uniform2(depthNearFarLocation, camera.NearPlane, camera.FarPlane);
        g.Uniform1(maxBehindTerrainLocation, 4f);
        if (depthReplacement)
        {
            g.ActiveTexture(TextureUnit.Texture1);
            g.BindTexture(TextureTarget.Texture2D, sceneDepthTexture);
        }

        g.ActiveTexture(TextureUnit.Texture0);
        g.DepthFunc(depthReplacement ? DepthFunction.Always : DepthFunction.Lequal);
        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        uint boundTexture = 0;
        int drawnPages = 0;
        foreach (ScannedRockPageKey key in drawableKeys)
        {
            if (!gpuPages.TryGetValue(key, out GpuPage? page)
                || !gpuMaterials.TryGetValue(page.MaterialPageId, out GpuMaterial? material)
                || !ScannedRockRenderPassCuller.IsVisible(
                    mvp,
                    cameraPosition,
                    maximumDistanceMeters,
                    page.WorldMin,
                    page.WorldMin + page.WorldExtent))
            {
                continue;
            }

            if (material.Texture != boundTexture)
            {
                g.BindTexture(TextureTarget.Texture2D, material.Texture);
                boundTexture = material.Texture;
            }

            g.Uniform1(
                worldProjectedLocation,
                page.MaterialPageId == ContinuousWorldMaterialPageId ? 1f : 0f);
            g.Uniform3(worldMinLocation, page.WorldMin.X, page.WorldMin.Y, page.WorldMin.Z);
            g.Uniform3(worldExtentLocation, page.WorldExtent.X, page.WorldExtent.Y, page.WorldExtent.Z);
            g.BindVertexArray(page.Vao);
            g.DrawElements(
                PrimitiveType.Triangles,
                (uint)page.IndexCount,
                DrawElementsType.UnsignedShort,
                (void*)0);
            drawnPages++;
        }

        if (!mainDrawLogged && drawnPages > 0)
        {
            mainDrawLogged = true;
            Log.Information("[RockRMP2] main pass submitted {Pages} pages", drawnPages);
        }

        g.BindVertexArray(0);
        g.BindTexture(TextureTarget.Texture2D, 0);
        if (depthReplacement)
        {
            g.ActiveTexture(TextureUnit.Texture1);
            g.BindTexture(TextureTarget.Texture2D, 0);
            g.ActiveTexture(TextureUnit.Texture0);
        }

        g.DepthFunc(DepthFunction.Less);
        g.Disable(EnableCap.Blend);
    }

    public void DrawShadow(GL g, Matrix4x4 lightViewProjection)
    {
        if (drawableKeys.Count == 0 || TerrainShaded)
        {
            return;
        }

        EnsurePrograms(g);
        g.UseProgram(shadowProgram);
        UploadMatrix(g, shadowLightVpLocation, lightViewProjection);
        foreach (ScannedRockPageKey key in drawableKeys)
        {
            if (!gpuPages.TryGetValue(key, out GpuPage? page)
                || !FrustumCuller.IsAabbVisible(
                    lightViewProjection,
                    page.WorldMin,
                    page.WorldMin + page.WorldExtent))
            {
                continue;
            }

            g.Uniform3(shadowWorldMinLocation, page.WorldMin.X, page.WorldMin.Y, page.WorldMin.Z);
            g.Uniform3(shadowWorldExtentLocation, page.WorldExtent.X, page.WorldExtent.Y, page.WorldExtent.Z);
            g.BindVertexArray(page.Vao);
            g.DrawElements(
                PrimitiveType.Triangles,
                (uint)page.IndexCount,
                DrawElementsType.UnsignedShort,
                (void*)0);
        }

        g.BindVertexArray(0);
    }

    public bool ShouldDrawShadowDetail(
        float cascadeFarMeters,
        float fieldOfViewYRadians,
        int shadowMapSize,
        float minimumReliefTexels)
    {
        if (drawableKeys.Count == 0)
        {
            return false;
        }

        foreach (ScannedRockPageKey key in drawableKeys)
        {
            if (gpuPages.TryGetValue(key, out GpuPage? page)
                && page.MaterialPageId != ContinuousWorldMaterialPageId)
            {
                return true;
            }
        }

        return ScannedRockShadowDetailPolicy.ShouldRender(
            ContinuousWorldMaximumReliefMeters,
            cascadeFarMeters,
            fieldOfViewYRadians,
            shadowMapSize,
            minimumReliefTexels);
    }

    public void Dispose(GL? g)
    {
        streaming?.Dispose();
        streaming = null;
        if (g is not null)
        {
            ReleaseGpu(g);
        }
    }

    private void KickMaterialLoads(IReadOnlyList<ScannedRockMeshPage> pages)
    {
        if (root is null)
        {
            return;
        }

        foreach (ushort materialId in pages.Select(page => page.MaterialPageId).Distinct())
        {
            if (gpuMaterials.ContainsKey(materialId)
                || materialLoads.ContainsKey(materialId)
                || failedMaterials.Contains(materialId))
            {
                continue;
            }

            string path = Path.Combine(root, $"{materialId}{RockMaterialPageStore.FileExtension}");
            materialLoads[materialId] = Task.Run(
                () =>
                {
                    using FileStream stream = File.OpenRead(path);
                    return RockMaterialPageStore.Read(stream);
                });
        }
    }

    private void HarvestMaterialLoads(GL g)
    {
        int uploaded = 0;
        foreach ((ushort id, Task<RockMaterialPage> task) in materialLoads.ToArray())
        {
            if (!task.IsCompleted || uploaded >= MaterialUploadsPerFrame)
            {
                continue;
            }

            materialLoads.Remove(id);
            if (!task.IsCompletedSuccessfully)
            {
                failedMaterials.Add(id);
                Log.Warning(task.Exception, "[RockRMP2] material {MaterialId} failed", id);
                continue;
            }

            gpuMaterials[id] = UploadMaterial(g, task.Result);
            uploaded++;
        }
    }

    private void UploadGeometry(GL g, IReadOnlyList<ScannedRockMeshPage> pages)
    {
        int uploaded = 0;
        foreach (ScannedRockMeshPage page in pages)
        {
            var key = new ScannedRockPageKey(page.PageX, page.PageY, page.Lod);
            if (gpuPages.ContainsKey(key) || uploaded >= GeometryUploadsPerFrame)
            {
                continue;
            }

            if (TerrainShaded)
            {
                if (TryUploadTerrainLayout(g, page, key))
                {
                    uploaded++;
                }

                continue;
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
            const uint stride = ScannedRockMeshPage.VertexStrideBytes;
            g.EnableVertexAttribArray(0);
            g.VertexAttribPointer(0, 3, VertexAttribPointerType.UnsignedShort, true, stride, (void*)0);
            g.EnableVertexAttribArray(1);
            g.VertexAttribPointer(1, 2, VertexAttribPointerType.Short, true, stride, (void*)6);
            g.EnableVertexAttribArray(2);
            g.VertexAttribPointer(2, 2, VertexAttribPointerType.UnsignedShort, true, stride, (void*)10);
            g.EnableVertexAttribArray(3);
            g.VertexAttribPointer(3, 1, VertexAttribPointerType.UnsignedByte, true, stride, (void*)14);
            g.EnableVertexAttribArray(4);
            g.VertexAttribPointer(4, 1, VertexAttribPointerType.UnsignedByte, true, stride, (void*)15);
            g.BindVertexArray(0);
            gpuPages[key] = new GpuPage(
                vao,
                vertexBuffer,
                indexBuffer,
                page.IndexCount,
                page.WorldMin,
                page.WorldExtent,
                page.MaterialPageId);
            uploaded++;
        }
    }

    private GpuMaterial UploadMaterial(GL g, RockMaterialPage material)
    {
        // StreamOrthoTextures deliberately leaves several long-lived arrays bound on their assigned units.
        // Never inherit its active unit here: binding and then clearing the RMP2 texture on (for example)
        // unit 11 silently disconnects det05 for every following frame. Unit 0 is rebound per terrain draw,
        // so it is the only scratch unit safe for this bounded upload.
        g.ActiveTexture(TextureUnit.Texture0);
        uint texture = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, texture);
        g.TexStorage2D(
            TextureTarget.Texture2D,
            material.MipCount,
            (SizedInternalFormat)GlCompressedRgbaS3tcDxt1,
            (uint)material.Width,
            (uint)material.Height);
        g.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.LinearMipmapLinear);
        g.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        g.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);

        int offset = 0;
        int width = material.Width;
        int height = material.Height;
        for (int level = 0; level < material.MipCount; level++)
        {
            int bytes = Bc1Encoder.EncodedSize(width, height);
            fixed (byte* source = &material.Bc1Data[offset])
            {
                g.CompressedTexSubImage2D(
                    TextureTarget.Texture2D,
                    level,
                    0,
                    0,
                    (uint)width,
                    (uint)height,
                    (InternalFormat)GlCompressedRgbaS3tcDxt1,
                    (uint)bytes,
                    source);
            }

            offset += bytes;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        g.BindTexture(TextureTarget.Texture2D, 0);
        return new GpuMaterial(texture);
    }

    private void RebuildDrawableKeys(IReadOnlyList<ScannedRockMeshPage> cpuDrawable)
    {
        drawableKeys.Clear();
        foreach (ScannedRockMeshPage desired in cpuDrawable)
        {
            var desiredKey = new ScannedRockPageKey(desired.PageX, desired.PageY, desired.Lod);
            if (IsGpuReady(desiredKey))
            {
                drawableKeys.Add(desiredKey);
                continue;
            }

            ScannedRockPageKey? fallback = gpuPages
                .Where(pair =>
                    pair.Key.PageX == desired.PageX
                    && pair.Key.PageY == desired.PageY
                    && gpuMaterials.ContainsKey(pair.Value.MaterialPageId))
                .OrderBy(pair => Math.Abs(pair.Key.Lod - desired.Lod))
                .Select(pair => (ScannedRockPageKey?)pair.Key)
                .FirstOrDefault();
            if (fallback is not null)
            {
                drawableKeys.Add(fallback.Value);
            }
        }
    }

    private void DeleteReplacedPages(GL g, IReadOnlyList<ScannedRockPageKey> desired)
    {
        foreach (ScannedRockPageKey stale in deferredDeletes.ToArray())
        {
            bool replacementReady = desired
                .Where(key => key.PageX == stale.PageX && key.PageY == stale.PageY)
                .Any(IsGpuReady);
            bool noLongerWanted = desired.All(
                key => key.PageX != stale.PageX || key.PageY != stale.PageY);
            if (!replacementReady && !noLongerWanted)
            {
                continue;
            }

            if (gpuPages.Remove(stale, out GpuPage? page))
            {
                DeletePage(g, page);
            }

            terrainPacks.Remove(stale);
            drawableKeys.Remove(stale);
            deferredDeletes.Remove(stale);
        }
    }

    private bool IsGpuReady(ScannedRockPageKey key) =>
        gpuPages.TryGetValue(key, out GpuPage? page)
        && gpuMaterials.ContainsKey(page.MaterialPageId);

    /// <summary>
    /// Renderer wykrył utratę kontekstu GL (resize/maximize odtwarza kontekst SKGLView). W trybie terrain-shaded
    /// warstwa nie linkuje własnego programu, więc HandleGpuReset nie miał po czym poznać utraty (przegląd 09-05:
    /// stare nazwy VAO/VBO stron i grup byłyby rysowane i KASOWANE w nowym kontekście — trafiając w kafle terenu).
    /// Reset bez wywołań GL; stan CPU-rezydentny zostaje, strony wracają przez normalny upload.
    /// </summary>
    public void NotifyContextLost() => contextLostPending = true;

    private void HandleGpuReset(GL g)
    {
        bool programDead = program != 0 && !g.IsProgram(program);
        if (!contextLostPending && !programDead)
        {
            return;
        }

        Log.Information("[RockRMP2] context lost — porzucam {Pages} stron, {Groups} grup, {Materials} materiałów bez kasowania (stare nazwy GL)", gpuPages.Count, gpuGroups.Count, gpuMaterials.Count);
        contextLostPending = false;
        unitCache.Clear();
        LastGroupUnits = 0;
        LastSingleUnits = 0;
        program = 0;
        shadowProgram = 0;
        gpuPages.Clear();
        gpuMaterials.Clear();
        drawableKeys.Clear();
        deferredDeletes.Clear();
        gpuGroups.Clear(); // kontekst utracony — bez DeleteBuffer
        terrainPacks.Clear();
        batches.Clear();
        batches.TakeRemoved();
        s3tcProbed = false;
        s3tcSupported = false;
        drawableLogged = false;
        mainDrawLogged = false;
    }

    private void ProbeS3tc(GL g)
    {
        if (s3tcProbed)
        {
            return;
        }

        s3tcProbed = true;
        string extensions = g.GetStringS(StringName.Extensions) ?? string.Empty;
        s3tcSupported = extensions.Contains(
                "texture_compression_s3tc",
                StringComparison.OrdinalIgnoreCase)
            || extensions.Contains(
                "compressed_texture_s3tc",
                StringComparison.OrdinalIgnoreCase)
            || extensions.Contains(
                "texture_compression_dxt1",
                StringComparison.OrdinalIgnoreCase);
    }

    private void EnsurePrograms(GL g)
    {
        if (program != 0)
        {
            return;
        }

        program = LinkProgram(g, VertexShaderSource, FragmentShaderSource);
        shadowProgram = LinkProgram(g, ShadowVertexShaderSource, ShadowFragmentShaderSource);
        mvpLocation = g.GetUniformLocation(program, "uMvp");
        worldMinLocation = g.GetUniformLocation(program, "uWorldMin");
        worldExtentLocation = g.GetUniformLocation(program, "uWorldExtent");
        albedoLocation = g.GetUniformLocation(program, "uAlbedo");
        worldProjectedLocation = g.GetUniformLocation(program, "uWorldProjected");
        lightLocation = g.GetUniformLocation(program, "uLightDir");
        ambientLocation = g.GetUniformLocation(program, "uAmbient");
        sunColorLocation = g.GetUniformLocation(program, "uSunColor");
        skyAmbientLocation = g.GetUniformLocation(program, "uSkyAmbient");
        cameraLocation = g.GetUniformLocation(program, "uCameraPos");
        fogColorLocation = g.GetUniformLocation(program, "uFogColor");
        fogDensityLocation = g.GetUniformLocation(program, "uFogDensity");
        sceneDepthLocation = g.GetUniformLocation(program, "uSceneDepth");
        sceneDepthOnLocation = g.GetUniformLocation(program, "uSceneDepthOn");
        depthNearFarLocation = g.GetUniformLocation(program, "uDepthNearFar");
        maxBehindTerrainLocation = g.GetUniformLocation(program, "uMaxBehindTerrain");
        shadowLightVpLocation = g.GetUniformLocation(shadowProgram, "uLightVp");
        shadowWorldMinLocation = g.GetUniformLocation(shadowProgram, "uWorldMin");
        shadowWorldExtentLocation = g.GetUniformLocation(shadowProgram, "uWorldExtent");
    }

    private static uint LinkProgram(GL g, string vertexSource, string fragmentSource)
    {
        uint vertex = CompileShader(g, ShaderType.VertexShader, vertexSource);
        uint fragment = CompileShader(g, ShaderType.FragmentShader, fragmentSource);
        uint linked = g.CreateProgram();
        g.AttachShader(linked, vertex);
        g.AttachShader(linked, fragment);
        g.LinkProgram(linked);
        g.GetProgram(linked, ProgramPropertyARB.LinkStatus, out int ok);
        g.DeleteShader(vertex);
        g.DeleteShader(fragment);
        if (ok == 0)
        {
            string log = g.GetProgramInfoLog(linked);
            g.DeleteProgram(linked);
            throw new InvalidOperationException($"RMP2 shader link failed: {log}");
        }

        return linked;
    }

    private static uint CompileShader(GL g, ShaderType type, string source)
    {
        uint shader = g.CreateShader(type);
        g.ShaderSource(shader, source);
        g.CompileShader(shader);
        g.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = g.GetShaderInfoLog(shader);
            g.DeleteShader(shader);
            throw new InvalidOperationException($"RMP2 {type} compile failed: {log}");
        }

        return shader;
    }

    private static void UploadMatrix(GL g, int location, Matrix4x4 matrix)
    {
        Span<float> values = stackalloc float[16]
        {
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44,
        };
        g.UniformMatrix4(location, 1, false, values);
    }

    private void ReleaseGpu(GL g)
    {
        if (gpuPages.Count > 0 || gpuMaterials.Count > 0 || gpuGroups.Count > 0)
        {
            Log.Information("[RockRMP2] GPU released: {Pages} pages, {Groups} groups, {Materials} materials (przełącznik OFF / reset)", gpuPages.Count, gpuGroups.Count, gpuMaterials.Count);
        }

        foreach (GpuPage page in gpuPages.Values)
        {
            DeletePage(g, page);
        }

        foreach (GpuMaterial material in gpuMaterials.Values)
        {
            g.DeleteTexture(material.Texture);
        }

        gpuPages.Clear();
        gpuMaterials.Clear();
        drawableKeys.Clear();
        deferredDeletes.Clear();
        foreach (GpuGroup grp in gpuGroups.Values)
        {
            DeleteGroupBuffers(g, grp);
        }

        gpuGroups.Clear();
        terrainPacks.Clear();
        batches.Clear();
        batches.TakeRemoved();
        unitCache.Clear();
        if (program != 0)
        {
            g.DeleteProgram(program);
            program = 0;
        }

        if (shadowProgram != 0)
        {
            g.DeleteProgram(shadowProgram);
            shadowProgram = 0;
        }
    }

    // Batching: skład grup ze stron rysowalnych (jedna strona na komórkę), budżet przebudów na klatkę, bufory scalone.
    private void UpdateBatches(GL g, Camera3D? camera, float aspect)
    {
        batchFrame++;
        batchStubs.Clear();
        if (TerrainShaded)
        {
            foreach (ScannedRockPageKey key in drawableKeys)
            {
                if (gpuPages.TryGetValue(key, out GpuPage? page) && page.TerrainLayout)
                {
                    batchStubs.Add(new ScannedRockPageStub(key, page.OrthoTileIndex, page.WorldMin, page.WorldMin + page.WorldExtent));
                }
            }
        }

        if (batchStubs.Count == 0 && gpuGroups.Count == 0 && batches.Groups.Count == 0)
        {
            unitCache.Clear();
            LastGroupUnits = 0;
            LastSingleUnits = 0;
            return;
        }

        batches.Update(batchStubs, batchFrame);
        foreach (ScannedRockGroupKey gk in batches.TakeRemoved())
        {
            DeleteGroup(g, gk);
        }

        Matrix4x4? vp = camera?.BuildViewProjection(aspect);
        Func<ScannedRockGroupKey, bool>? visible = vp is { } m
            ? gk => { (Vector3 min, Vector3 max) = batches.BoundsOf(gk); return FrustumCuller.IsAabbVisible(m, min, max); }
            : null;
        foreach (ScannedRockGroupKey gk in batches.TakeDirty(GroupRebuildMaxPagesPerFrame, GroupSettleFrames, batchFrame, visible))
        {
            DeleteGroup(g, gk);
            IReadOnlyList<ScannedRockPageKey> members = batches.MembersOf(gk);
            (Vector3 gmin, Vector3 gmax) = batches.BoundsOf(gk);
            if (members.Count == 1 && gpuPages.TryGetValue(members[0], out GpuPage? single) && single.TerrainLayout)
            {
                // Grupa jednoelementowa: alias VAO strony, bez drugiej kopii w VRAM (przegląd 09-05).
                gpuGroups[gk] = new GpuGroup(single.Vao, 0, [], single.IndexCount, gk.OrthoTileIndex, gmin, gmax, Owning: false);
                batches.MarkBuilt(gk);
                continue;
            }

            var packs = new List<TerrainVertexPack>(members.Count);
            foreach (ScannedRockPageKey key in members)
            {
                if (terrainPacks.TryGetValue(key, out TerrainVertexPack? pack))
                {
                    packs.Add(pack);
                }
            }

            if (packs.Count != members.Count)
            {
                continue; // niekompletna — zostaje brudna (strony pojedynczo), wróci w następnej klatce
            }

            TerrainVertexPack merged = ScannedRockPageBatcher.Merge(packs);
            (uint vao, uint positionVbo, uint ebo, uint[] extra) = UploadTerrainLayout(g, merged);
            uint[] buffers = new uint[extra.Length + 1];
            buffers[0] = positionVbo;
            Array.Copy(extra, 0, buffers, 1, extra.Length);
            gpuGroups[gk] = new GpuGroup(vao, ebo, buffers, merged.Indices.Length, gk.OrthoTileIndex, gmin, gmax);
            batches.MarkBuilt(gk);
            LastRebuilds++;
        }

        unitCache.Clear();
        int groups = 0;
        int singles = 0;
        foreach (ScannedRockDrawUnit unit in batches.DrawUnits())
        {
            if (unit.Group is { } gk)
            {
                if (gpuGroups.TryGetValue(gk, out GpuGroup? grp))
                {
                    unitCache.Add(new TerrainShadedPage(grp.Vao, grp.IndexCount, grp.OrthoTileIndex, grp.WorldMin, grp.WorldMax));
                    groups++;
                    continue;
                }

                foreach (ScannedRockPageKey key in batches.MembersOf(gk))
                {
                    if (gpuPages.TryGetValue(key, out GpuPage? member) && member.TerrainLayout)
                    {
                        unitCache.Add(new TerrainShadedPage(member.Vao, member.IndexCount, member.OrthoTileIndex, member.WorldMin, member.WorldMin + member.WorldExtent));
                        singles++;
                    }
                }
            }
            else if (unit.Page is { } pk && gpuPages.TryGetValue(pk, out GpuPage? page) && page.TerrainLayout)
            {
                unitCache.Add(new TerrainShadedPage(page.Vao, page.IndexCount, page.OrthoTileIndex, page.WorldMin, page.WorldMin + page.WorldExtent));
                singles++;
            }
        }

        LastGroupUnits = groups;
        LastSingleUnits = singles;
    }

    private void DeleteGroup(GL g, ScannedRockGroupKey gk)
    {
        if (gpuGroups.Remove(gk, out GpuGroup? grp))
        {
            DeleteGroupBuffers(g, grp);
        }
    }

    private static void DeleteGroupBuffers(GL g, GpuGroup grp)
    {
        if (!grp.Owning)
        {
            return; // alias VAO strony — bufory należą do GpuPage
        }

        g.DeleteVertexArray(grp.Vao);
        g.DeleteBuffer(grp.IndexBuffer);
        foreach (uint buffer in grp.Buffers)
        {
            g.DeleteBuffer(buffer);
        }
    }

    // Layout kafla terenu: piec VBO per atrybut (pos f3, color rgba8 z AO w alfie, normal f3, tex f2, detail f1)
    // + EBO uint — dokladnie jak UploadTile w Terrain3DGlRenderer (ten sam program, te same uniformy).
    private static (uint Vao, uint PositionVbo, uint Ebo, uint[] Extra) UploadTerrainLayout(GL g, TerrainVertexPack pack)
    {
        int vertexCount = pack.Positions.Length;
        byte[] rgba = new byte[vertexCount * 4];
        for (int i = 0; i < vertexCount; i++)
        {
            uint argb = pack.Colors[i];
            rgba[(i * 4) + 0] = (byte)((argb >> 16) & 0xFF);
            rgba[(i * 4) + 1] = (byte)((argb >> 8) & 0xFF);
            rgba[(i * 4) + 2] = (byte)(argb & 0xFF);
            rgba[(i * 4) + 3] = (byte)((argb >> 24) & 0xFF);
        }

        ReadOnlySpan<float> positions = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(pack.Positions.AsSpan());
        ReadOnlySpan<float> normals = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(pack.Normals.AsSpan());

        uint vao = g.GenVertexArray();
        g.BindVertexArray(vao);
        uint positionVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, positionVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(positions.Length * sizeof(float)), positions, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        uint colorVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, colorVbo);
        g.BufferData<byte>(BufferTargetARB.ArrayBuffer, (nuint)rgba.Length, rgba, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, 4, (void*)0);
        uint normalVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, normalVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(normals.Length * sizeof(float)), normals, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        uint texVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, texVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(pack.TexCoords.Length * sizeof(float)), pack.TexCoords, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(3);
        g.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        uint detailVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, detailVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(pack.Detail.Length * sizeof(float)), pack.Detail, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(4);
        g.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, sizeof(float), (void*)0);
        uint ebo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        g.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, (nuint)(pack.Indices.Length * sizeof(uint)), pack.Indices, BufferUsageARB.StaticDraw);
        g.BindVertexArray(0);
        return (vao, positionVbo, ebo, [colorVbo, normalVbo, texVbo, detailVbo]);
    }

    private static void DeletePage(GL g, GpuPage page)
    {
        g.DeleteVertexArray(page.Vao);
        g.DeleteBuffer(page.VertexBuffer);
        g.DeleteBuffer(page.IndexBuffer);
        if (page.ExtraBuffers is not null)
        {
            foreach (uint buffer in page.ExtraBuffers)
            {
                g.DeleteBuffer(buffer);
            }
        }
    }

    // Pilot "kolor z orto": strona w layoucie kafla terenu - piec VBO per atrybut (pos f3, color rgba8 z AO w
    // alfie, normal f3, tex f2 = UV komorki orto bazowej, detail f1 = 0) + EBO uint, dokladnie jak UploadTile
    // w Terrain3DGlRenderer (ten sam VAO-layout, wiec ten sam program i te same uniformy). Brak komorki orto
    // (resolver == null) = strona czeka na nastepna klatke, nie jest liczona jako upload.
    private bool TryUploadTerrainLayout(GL g, ScannedRockMeshPage page, ScannedRockPageKey key)
    {
        Vector3 centre = page.WorldMin + (page.WorldExtent * 0.5f);
        OrthoCellRef? cell = OrthoCellResolver?.Invoke(centre);
        if (cell is null)
        {
            return false;
        }

        TerrainVertexPack pack;
        try
        {
            pack = ScannedRockPageTerrainRepacker.Repack(page, cell.Value.Min, cell.Value.Max);
        }
        catch (ArgumentException)
        {
            return false;
        }

        (uint vao, uint positionVbo, uint ebo, uint[] extra) = UploadTerrainLayout(g, pack);
        terrainPacks[key] = pack; // do scalania w grupy (batching)
        gpuPages[key] = new GpuPage(
            vao,
            positionVbo,
            ebo,
            pack.Indices.Length,
            page.WorldMin,
            page.WorldExtent,
            page.MaterialPageId,
            TerrainLayout: true,
            OrthoTileIndex: cell.Value.Index,
            ExtraBuffers: extra);
        return true;
    }

    private sealed record GpuPage(
        uint Vao,
        uint VertexBuffer,
        uint IndexBuffer,
        int IndexCount,
        Vector3 WorldMin,
        Vector3 WorldExtent,
        ushort MaterialPageId,
        bool TerrainLayout = false,
        int OrthoTileIndex = -1,
        uint[]? ExtraBuffers = null);

    private sealed record GpuMaterial(uint Texture);

    /// <summary>Scalona grupa stron (batching): jeden VAO/draw; Buffers = pos + [color, normal, tex, detail].</summary>
    private sealed record GpuGroup(uint Vao, uint IndexBuffer, uint[] Buffers, int IndexCount, int OrthoTileIndex, Vector3 WorldMin, Vector3 WorldMax, bool Owning = true);
}

/// <summary>Base-ortho cell reference for <see cref="PhotogrammetricRockGlLayer.OrthoCellResolver"/>.</summary>
internal readonly record struct OrthoCellRef(int Index, Vector3 Min, Vector3 Max);

/// <summary>One RMP2 page uploaded in the terrain tile layout, ready for the renderer's tile loops.</summary>
internal readonly record struct TerrainShadedPage(uint Vao, int IndexCount, int OrthoTileIndex, Vector3 WorldMin, Vector3 WorldMax);
