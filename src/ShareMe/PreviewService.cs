using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace ShareMe;

// Preview work is bounded and optional. Cached local media is sufficient to repair
// older shares, so viewing a share never causes another request to its source site.
public sealed class PreviewService(VideoStore store, AppSettings settings, PublicProxy proxy, ILogger<PreviewService> logger) : BackgroundService
{
    public const int Width = 1200;
    public const int Height = 630;
    public const int MaxBytes = 512 * 1024;
    private readonly SemaphoreSlim gate = new(1);

    public static PreviewImage ImageFor(Video video, VideoStore store)
    {
        if (video.Status == "ready")
        {
            try
            {
                if (video.PreviewHash is not null && File.Exists(store.FilePath(video.Id, "preview.jpg")))
                    return new($"/preview/{video.Id}/{video.PreviewHash}.jpg", Width, Height, "image/jpeg");
                if (File.Exists(store.FilePath(video.Id, "poster.jpg")) && new FileInfo(store.FilePath(video.Id, "poster.jpg")).Length < 1024 * 1024)
                    return new($"/p/{video.Id}.jpg", video.PosterWidth, video.PosterHeight, "image/jpeg");
            }
            catch (IOException) { }
        }
        return new("/icon-180.png", 180, 180, "image/png", IsIcon: true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var video in store.List(0, int.MaxValue).Items.Where(v => v.Status == "ready"))
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (video.PreviewHash is null || !File.Exists(store.FilePath(video.Id, "preview.jpg")))
                await Prepare(video.Id, null, stoppingToken);
        }
    }

    public async Task Prepare(string id, string? sourceThumbnail, CancellationToken stoppingToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        budget.CancelAfter(TimeSpan.FromSeconds(25));
        var token = budget.Token;
        var entered = false;
        var temporary = store.FilePath(id, "preview.new.jpg");
        var downloaded = store.FilePath(id, "preview-source.image");
        try
        {
            await gate.WaitAsync(token);
            entered = true;
            if (store.Get(id) is not { } video) return;
            if (video.PreviewHash is not null && File.Exists(store.FilePath(id, "preview.jpg"))) return;

            var candidates = new List<(string File, double Seek)>();
            if (await FetchThumbnail(sourceThumbnail, downloaded, token)) candidates.Add((downloaded, 0));
            var media = store.FilePath(id, "video.mp4");
            candidates.Add((media, Math.Min(3, Math.Max(0, video.Duration * 0.1))));
            candidates.Add((media, 0));
            if (File.Exists(store.FilePath(id, "poster.jpg"))) candidates.Add((store.FilePath(id, "poster.jpg"), 0));
            foreach (var (file, seek) in candidates)
            {
                foreach (var quality in new[] { 4, 8, 14 })
                {
                    try
                    {
                        File.Delete(temporary);
                        // Keep the entire frame: portrait videos are centered, never cropped.
                        var args = new List<string> { "-v", "error", "-nostdin", "-y", "-max_alloc", "134217728", "-max_pixels", "16000000", "-threads", "2", "-filter_threads", "1", "-protocol_whitelist", "file,pipe" };
                        if (seek > 0) args.AddRange(["-ss", seek.ToString(CultureInfo.InvariantCulture)]);
                        args.AddRange(["-i", file, "-frames:v", "1", "-vf",
                            $"scale={Width}:{Height}:force_original_aspect_ratio=decrease:force_divisible_by=2,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2:color=0x111714,setsar=1",
                            "-q:v", quality.ToString(CultureInfo.InvariantCulture), "-threads", "2", temporary]);
                        if (!await Convert(args, token) || !File.Exists(temporary)) break;
                        var size = new FileInfo(temporary).Length;
                        if (size is < 8 or > MaxBytes) continue;
                        var hash = ConvertHash(temporary);
                        File.Move(temporary, store.FilePath(id, "preview.jpg"), true);
                        File.Delete(downloaded);
                        if (store.UsedBytes() > settings.MaxStorageBytes)
                        {
                            File.Delete(store.FilePath(id, "preview.jpg"));
                            return;
                        }
                        store.Update(id, v => v with { PreviewHash = hash });
                        logger.LogInformation("Prepared preview for {Id} from {Source}", id, file == downloaded ? "source thumbnail" : "local media");
                        return;
                    }
                    catch (Exception e) when (e is IOException or InvalidOperationException)
                    {
                        logger.LogDebug(e, "Preview candidate failed for {Id}", id);
                        break;
                    }
                }
            }
        }
        catch (Exception e)
        {
            logger.LogWarning("Preview unavailable for {Id}: {Reason}", id, e.Message);
        }
        finally
        {
            foreach (var file in new[] { temporary, downloaded })
                try { File.Delete(file); } catch (IOException) { }
            if (entered) gate.Release();
        }
    }

    private async Task<bool> FetchThumbnail(string? url, string path, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.UserInfo != "" || !uri.IsDefaultPort) return false;
        try
        {
            // Use the same DNS-pinned public-only proxy as yt-dlp, including redirects.
            using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy.Url), UseProxy = true };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 5 * 1024 * 1024) return false;
            var type = response.Content.Headers.ContentType?.MediaType;
            if (type is not ("image/jpeg" or "image/png" or "image/webp")) return false;
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var output = File.Create(path);
            var buffer = new byte[16384];
            var total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
            {
                total += read;
                if (total > 5 * 1024 * 1024) return false;
                await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
            }
            return total > 0;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            logger.LogDebug(e, "Source thumbnail unavailable; using a video frame");
            return false;
        }
    }

    private static string ConvertHash(string path) => System.Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))[..16];

    private static async Task<bool> Convert(IEnumerable<string> arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) return false;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); return process.ExitCode == 0; }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(output, error);
        }
    }
}

public sealed record PreviewImage(string Path, int Width, int Height, string ContentType, bool IsIcon = false);
