using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ShareMe;

public sealed class DownloadWorker(VideoStore store, AppSettings settings, PublicProxy proxy, ILogger<DownloadWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in store.Queue.Reader.ReadAllAsync(stoppingToken))
        {
            try { await Download(id, stoppingToken); }
            catch (Exception e)
            {
                logger.LogWarning(e, "Download {Id} failed", id);
                foreach (var path in Directory.EnumerateFiles(store.FilePath(id, "")).Where(p => Path.GetFileName(p) != "record.json")) File.Delete(path);
                var error = e is DownloadException ? e.Message : e is OperationCanceledException
                    ? "The download timed out or the server stopped. Try again with a shorter video."
                    : "This download could not be completed. Please try again.";
                store.Update(id, v => v with { Status = "failed", Error = error, Progress = 0 });
            }
        }
    }
    private async Task Download(string id, CancellationToken stoppingToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var token = deadline.Token;
        var video = store.Get(id)!;
        var folder = store.FilePath(id, "");
        store.Update(id, v => v with { Status = "downloading" });
        var common = new[] { "--ignore-config", "--no-playlist", "--playlist-items", "1", "--no-cache-dir", "--no-warnings",
            "--use-extractors", "default,-generic", "--proxy", proxy.Url, "--socket-timeout", "30", "--retries", "2",
            "--fragment-retries", "2", "--js-runtimes", "deno", "--js-runtimes", "node", "--no-check-formats" };
        var metadataJson = await Run(settings.Downloader, [.. common, "--skip-download", "--dump-single-json", "--", video.SourceUrl], folder, id, token);
        using var metadata = JsonDocument.Parse(metadataJson);
        var info = metadata.RootElement;
        if (info.TryGetProperty("_type", out var type) && type.GetString() is "playlist" or "multi_video")
            throw new DownloadException("Please share a link to one video, rather than a playlist or album.");
        var duration = info.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) ? seconds : 0;
        if (duration > settings.MaxDurationSeconds) throw new DownloadException($"This video is longer than the {settings.MaxDurationSeconds / 60}-minute limit.");
        if (info.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True)
            throw new DownloadException("Live streams are not supported. Try again once the stream has ended.");
        var title = info.TryGetProperty("title", out var titleValue) ? titleValue.GetString() : null;
        store.Update(id, v => v with { Title = title is { Length: > 300 } ? title[..300] : title ?? "Shared video", Duration = duration });
        // Only native HTTP-based downloaders are allowed. RTMP/RTSP and ffmpeg's
        // network downloader would otherwise escape the checked HTTP proxy.
        const string protocol = "[protocol~='^(https?|m3u8_native|http_dash_segments)$']";
        await Run(settings.Downloader, [.. common,
            "--format", $"bv[height<=1080]{protocol}+ba{protocol}/b[height<=1080]{protocol}/b{protocol}", "--format-sort", "vcodec:h264,acodec:aac",
            "--merge-output-format", "mp4", "--max-filesize", settings.MaxFileBytes.ToString(CultureInfo.InvariantCulture),
            "--match-filter", $"!is_live & duration <=? {settings.MaxDurationSeconds}",
            "--downloader", "native", "--hls-prefer-native", "--postprocessor-args", "ffmpeg_i:-protocol_whitelist file,pipe",
            "--newline", "--progress", "--progress-delta", "1",
            "--progress-template", "download:PROGRESS:%(progress._percent_str)s", "--output", "source.%(ext)s", "--", video.SourceUrl], folder, id, token,
            line =>
            {
                if (line.StartsWith("PROGRESS:") && double.TryParse(line[9..].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var progress))
                    store.Update(id, v => v with { Progress = Math.Clamp(progress, 0, 100) }, persist: false);
            });
        var source = Directory.EnumerateFiles(folder, "source.*").FirstOrDefault(p => Path.GetExtension(p) is ".mp4" or ".webm" or ".mkv" or ".mov" or ".flv" or ".avi");
        if (source is null) throw new DownloadException("No downloadable video was found. The post may be private, removed, or require a platform login.");
        if (new FileInfo(source).Length > settings.MaxFileBytes) throw new DownloadException("This video exceeds the file size limit.");
        store.Update(id, v => v with { Status = "processing", Progress = 100 });
        var probe = await Run("ffprobe", ["-v", "error", "-protocol_whitelist", "file,pipe", "-show_streams", "-show_format", "-of", "json", source], folder, id, token);
        using var media = JsonDocument.Parse(probe);
        var streams = media.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var picture = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "video");
        if (picture.ValueKind == JsonValueKind.Undefined) throw new DownloadException("This link contains audio only. Share a video link instead.");
        if (media.RootElement.GetProperty("format").TryGetProperty("duration", out var actualDuration)
            && double.TryParse(actualDuration.GetString(), CultureInfo.InvariantCulture, out var measured))
        {
            if (measured > settings.MaxDurationSeconds + 1) throw new DownloadException("This video exceeds the duration limit.");
            duration = measured;
        }
        var compatible = picture.GetProperty("codec_name").GetString() == "h264" &&
            picture.TryGetProperty("pix_fmt", out var pixelFormat) && pixelFormat.GetString() == "yuv420p";
        var audio = streams.FirstOrDefault(s => s.GetProperty("codec_type").GetString() == "audio");
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-i", source,
            "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn", "-map_metadata", "-1", "-c:v", compatible ? "copy" : "libx264" };
        if (!compatible) args.AddRange(["-preset", "fast", "-crf", "23", "-pix_fmt", "yuv420p", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-threads", "2"]);
        args.AddRange(["-c:a", audio.ValueKind != JsonValueKind.Undefined && audio.GetProperty("codec_name").GetString() == "aac" ? "copy" : "aac", "-movflags", "+faststart", store.FilePath(id, "video.mp4")]);
        await Run("ffmpeg", args, folder, id, token);
        File.Delete(source);
        var bytes = new FileInfo(store.FilePath(id, "video.mp4")).Length;
        if (bytes > settings.MaxFileBytes) throw new DownloadException("The converted video exceeds the file size limit.");
        await Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-protocol_whitelist", "file,pipe", "-i", store.FilePath(id, "video.mp4"),
            "-frames:v", "1", "-vf", "scale=640:-2", "-q:v", "3", store.FilePath(id, "poster.jpg")], folder, id, token);
        if (store.UsedBytes() > settings.MaxStorageBytes) throw new DownloadException("The download exceeded the available storage limit.");
        store.Update(id, v => v with { Status = "ready", Progress = 100, Bytes = bytes, Duration = duration });
    }
    private async Task<string> Run(string executable, IEnumerable<string> arguments, string folder, string id, CancellationToken token, Action<string>? onLine = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new DownloadException($"Could not start {executable}.");
        var output = new StringBuilder();
        var errors = new StringBuilder();
        async Task Drain(StreamReader reader, StringBuilder buffer, bool progress)
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                if (buffer.Length < 4 * 1024 * 1024) buffer.AppendLine(line);
                if (progress) onLine?.Invoke(line);
            }
        }
        var stdout = Drain(process.StandardOutput, output, true);
        var stderr = Drain(process.StandardError, errors, false);
        try
        {
            var exited = process.WaitForExitAsync(token);
            while (!exited.IsCompleted)
            {
                if (VideoStore.DirectoryBytes(folder) > settings.MaxFileBytes * 3 || store.UsedBytes() > settings.MaxStorageBytes)
                    throw new DownloadException("The download exceeded the available storage limit.");
                await Task.WhenAny(exited, Task.Delay(500, token));
                token.ThrowIfCancellationRequested();
            }
            await exited;
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0)
            {
                logger.LogWarning("Media tool failed for {Id}: {Tool}, exit {ExitCode}: {Error}", id, executable, process.ExitCode, errors.ToString()[..Math.Min(errors.Length, 4000)]);
                throw new DownloadException(executable == settings.Downloader
                    ? "The platform could not provide this video. It may require a login, be unavailable in your region, or block downloads from this server."
                    : "The video could not be converted for browser playback.");
            }
            return output.ToString();
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
}

public sealed class DownloadException(string message) : Exception(message);
