using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using ShareMe;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8192);
builder.Services.AddSingleton<AppSettings>();
builder.Services.AddSingleton<VideoStore>();
builder.Services.AddSingleton<PublicProxy>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PublicProxy>());
builder.Services.AddSingleton<PreviewService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PreviewService>());
builder.Services.AddHostedService<DownloadWorker>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("submit", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; media-src 'self'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/config", (AppSettings settings) => Results.Ok(new
{
    maxFileMb = settings.MaxFileBytes / 1024 / 1024,
    maxDurationMinutes = settings.MaxDurationSeconds / 60
}));
app.MapPost("/api/videos", VideoApi.Submit).RequireRateLimiting("submit");
app.MapGet("/api/videos/{id}", (string id, VideoStore store) =>
    store.Get(id) is { } video ? Results.Ok(video.Public()) : Results.NotFound(new { error = "This share does not exist or has been deleted." }));
app.MapDelete("/api/videos/{id}", (string id, HttpRequest request, VideoStore store) =>
{
    var video = store.Get(id);
    if (video is null) return Results.NotFound();
    var token = request.Headers.Authorization.ToString();
    if (!token.StartsWith("Bearer ", StringComparison.Ordinal) ||
        !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token[7..])), Convert.FromHexString(video.DeleteHash)))
        return Results.StatusCode(403);
    if (video.Status is "queued" or "downloading" or "processing")
        return Results.Conflict(new { error = "Wait for this download to finish before deleting it." });
    store.Delete(id);
    return Results.NoContent();
});
app.MapMethods("/m/{id}.mp4", ["GET", "HEAD"], (string id, HttpRequest request, VideoStore store) =>
{
    if (store.Get(id) is not { Status: "ready" } || !File.Exists(store.FilePath(id, "video.mp4"))) return Results.NotFound();
    return Results.File(store.FilePath(id, "video.mp4"), "video/mp4",
        fileDownloadName: request.Query.ContainsKey("download") ? $"share-me-{id}.mp4" : null, enableRangeProcessing: true);
});
app.MapMethods("/p/{id}.jpg", ["GET", "HEAD"], (string id, VideoStore store, IWebHostEnvironment environment) =>
    store.Get(id) is not { Status: "ready" } ? Results.NotFound() : File.Exists(store.FilePath(id, "poster.jpg"))
        ? Results.File(store.FilePath(id, "poster.jpg"), "image/jpeg") : Results.File(Path.Combine(environment.WebRootPath, "icon-180.png"), "image/png"));
app.MapMethods("/preview/{id}/{hash}.jpg", ["GET", "HEAD"], (string id, string hash, HttpContext context, VideoStore store) =>
{
    if (store.Get(id) is not { Status: "ready" } video || video.PreviewHash != hash || !File.Exists(store.FilePath(id, "preview.jpg"))) return Results.NotFound();
    context.Response.Headers.CacheControl = "public, max-age=86400, immutable";
    return Results.File(store.FilePath(id, "preview.jpg"), "image/jpeg");
});
app.MapMethods("/s/{id}", ["GET", "HEAD"], (string id, HttpContext context, VideoStore store, AppSettings settings, IWebHostEnvironment environment) =>
{
    var video = store.Get(id);
    var html = File.ReadAllText(Path.Combine(environment.WebRootPath, "watch.html"));
    var origin = settings.PublicUrl ?? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
    context.Response.Headers.CacheControl = "no-cache";
    return Results.Content(SharePage.Render(html, video, origin, video is null ? null : PreviewService.ImageFor(video, store)),
        "text/html", statusCode: video is null ? 404 : 200);
});
VideoApi.Map(app);
app.Run();
