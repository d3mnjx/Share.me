using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using ShareTo;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 8192);
builder.Services.AddSingleton<AppSettings>();
builder.Services.AddSingleton<VideoStore>();
builder.Services.AddSingleton<PublicProxy>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PublicProxy>());
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
app.MapPost("/api/videos", (Submission input, VideoStore store) =>
{
    if (input.Url is null || input.Url.Length > 4096 || !Uri.TryCreate(input.Url.Trim(), UriKind.Absolute, out var uri)
        || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length > 0 || !uri.IsDefaultPort
        || uri.HostNameType == UriHostNameType.Unknown || uri.IsLoopback)
        return Results.BadRequest(new { error = "Enter a public http or https video link." });
    if (System.Net.IPAddress.TryParse(uri.Host, out var ip) && !PublicProxy.IsPublic(ip))
        return Results.BadRequest(new { error = "Private network addresses are not supported." });
    var url = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
    try
    {
        var (video, token) = store.Create(url);
        return Results.Accepted($"/api/videos/{video.Id}", new { video = video.Public(), deleteToken = token });
    }
    catch (InvalidOperationException e) { return Results.Json(new { error = e.Message }, statusCode: 503); }
}).RequireRateLimiting("submit");
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
app.MapGet("/m/{id}.mp4", (string id, HttpRequest request, VideoStore store) =>
{
    if (store.Get(id) is not { Status: "ready" } || !File.Exists(store.FilePath(id, "video.mp4"))) return Results.NotFound();
    return Results.File(store.FilePath(id, "video.mp4"), "video/mp4",
        fileDownloadName: request.Query.ContainsKey("download") ? $"share-to-{id}.mp4" : null, enableRangeProcessing: true);
});
app.MapGet("/p/{id}.jpg", (string id, VideoStore store) =>
    store.Get(id) is { Status: "ready" } && File.Exists(store.FilePath(id, "poster.jpg"))
        ? Results.File(store.FilePath(id, "poster.jpg"), "image/jpeg") : Results.NotFound());
app.MapGet("/s/{id}", (string id, VideoStore store, AppSettings settings, IWebHostEnvironment environment) =>
{
    var video = store.Get(id);
    var html = File.ReadAllText(Path.Combine(environment.WebRootPath, "watch.html"));
    var title = System.Net.WebUtility.HtmlEncode(video?.Title ?? "Video shared with you");
    var tags = $"<meta property=\"og:title\" content=\"{title}\"><meta property=\"og:type\" content=\"video.other\">";
    if (video is { Status: "ready" } && settings.PublicUrl is { } origin)
        tags += $"<meta property=\"og:image\" content=\"{origin}/p/{video.Id}.jpg\"><meta property=\"og:video\" content=\"{origin}/m/{video.Id}.mp4\"><meta property=\"og:video:type\" content=\"video/mp4\">";
    return Results.Content(html.Replace("<!-- SHARE_META -->", tags), "text/html", statusCode: video is null ? 404 : 200);
});
app.Run();

record Submission(string? Url);
