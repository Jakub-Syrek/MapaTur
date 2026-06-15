using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

// MapaTur package server: serves the region-package catalogue (manifest.json) and the package blobs
// (DEM zips + ortho .mbtiles) as plain static files. The static-file middleware gives us HTTP Range and
// HEAD for free, which is exactly what the in-app resumable downloader needs. Point PACKAGES_DIR at a
// persistent volume (Railway volume) holding:
//
//   {PACKAGES_DIR}/manifest.json
//   {PACKAGES_DIR}/packages/tatry-dem-1m-v3.zip
//   {PACKAGES_DIR}/packages/tatry-ortho-v1.mbtiles
//
// The manifest's per-package "url" fields can point here OR at a cheaper CDN (e.g. Cloudflare R2) without
// any code change — the app just follows whatever URL the manifest advertises.

var builder = WebApplication.CreateBuilder(args);

// Railway (and most PaaS) inject the listen port via $PORT.
string port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

string dataDir = Environment.GetEnvironmentVariable("PACKAGES_DIR")
    ?? Path.Combine(app.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
Directory.CreateDirectory(Path.Combine(dataDir, "packages"));

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".mbtiles"] = "application/octet-stream";
contentTypes.Mappings[".zip"] = "application/zip";
contentTypes.Mappings[".tif"] = "image/tiff";

var staticFiles = new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(dataDir),
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream",
    ContentTypeProvider = contentTypes,
    // Long-lived immutable blobs: each release gets a versioned filename, so cache hard.
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "public, max-age=604800, immutable",
};

app.UseStaticFiles(staticFiles);

app.MapGet("/", () => Results.Text(
    "MapaTur package server. See /manifest.json and /packages/<file>.", "text/plain"));
app.MapGet("/healthz", () => Results.Ok("ok"));

app.Run();
