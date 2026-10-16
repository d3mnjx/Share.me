using System.Security.Cryptography;
using System.Text;

namespace ShareMe;

public sealed record Submission(string? Url);

public static class VideoApi
{
    public static IResult Submit(Submission input, VideoStore store, HttpRequest request)
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
            var prefix = request.Path.StartsWithSegments("/api/v1") ? "/api/v1/videos" : "/api/videos";
            return Results.Accepted($"{prefix}/{video.Id}", new { video = video.Public(), deleteToken = token });
        }
        catch (InvalidOperationException e) { return Results.Json(new { error = e.Message }, statusCode: 503); }

    }

    public static void Map(WebApplication app)
    {
        // The browser API remains public. This separate API token authorizes
        // integrations to list and manage all videos on this instance.
        var api = app.MapGroup("/api/v1").AddEndpointFilter(async (context, next) =>
        {
            var settings = context.HttpContext.RequestServices.GetRequiredService<AppSettings>();
            if (string.IsNullOrWhiteSpace(settings.ApiToken))
                return Results.Json(new { error = "The integration API is disabled. Set API_TOKEN to enable it." }, statusCode: 503);
            var authorization = context.HttpContext.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..])),
                    SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiToken))))
            {
                context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(new { error = "A valid API token is required." }, statusCode: 401);
            }
            return await next(context);
        });
        api.MapGet("/config", (AppSettings settings) => Results.Ok(new
        {
            maxFileMb = settings.MaxFileBytes / 1024 / 1024,
            maxStorageMb = settings.MaxStorageBytes / 1024 / 1024,
            maxDurationMinutes = settings.MaxDurationSeconds / 60,
            publicUrl = settings.PublicUrl
        }));
        api.MapPost("/videos", Submit).RequireRateLimiting("submit");
        api.MapGet("/videos", (int? offset, int? limit, VideoStore store) =>
        {
            var skip = offset ?? 0;
            var take = limit ?? 50;
            if (skip < 0 || take < 1 || take > 100)
                return Results.BadRequest(new { error = "offset must be nonnegative; limit must be between 1 and 100." });
            var (items, total) = store.List(skip, take);
            return Results.Ok(new { videos = items.Select(v => v.Public()), total, offset = skip, limit = take });
        });
        api.MapGet("/videos/{id}", (string id, VideoStore store) =>
            store.Get(id) is { } video ? Results.Ok(video.Public()) : Results.NotFound(new { error = "Video not found." }));
        api.MapDelete("/videos/{id}", (string id, VideoStore store) =>
        {
            var video = store.Get(id);
            if (video is null) return Results.NotFound(new { error = "Video not found." });
            if (video.Status is "queued" or "downloading" or "processing")
                return Results.Conflict(new { error = "Wait for this download to finish before deleting it." });
            store.Delete(id);
            return Results.NoContent();
        });
        api.MapGet("/videos/{id}/media", (string id, VideoStore store) =>
        {
            if (store.Get(id) is not { } video) return Results.NotFound(new { error = "Video not found." });
            if (video.Status != "ready") return Results.Conflict(new { error = "The video is not ready." });
            var path = store.FilePath(id, "video.mp4");
            return File.Exists(path) ? Results.File(path, "video/mp4", fileDownloadName: $"share-me-{id}.mp4", enableRangeProcessing: true) : Results.NotFound();
        });
        api.MapGet("/videos/{id}/poster", (string id, VideoStore store, IWebHostEnvironment environment) =>
        {
            if (store.Get(id) is not { } video) return Results.NotFound(new { error = "Video not found." });
            if (video.Status != "ready") return Results.Conflict(new { error = "The video is not ready." });
            var path = store.FilePath(id, "poster.jpg");
            return File.Exists(path) ? Results.File(path, "image/jpeg") : Results.File(Path.Combine(environment.WebRootPath, "icon-180.png"), "image/png");
        });
    }
}
