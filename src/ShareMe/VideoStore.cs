using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ShareMe;

public sealed class AppSettings
{
    public string DataPath { get; } = Path.GetFullPath(Environment.GetEnvironmentVariable("DATA_PATH") ?? "data");
    public string Downloader { get; } = Environment.GetEnvironmentVariable("YT_DLP_PATH") ?? "yt-dlp";
    public string? ApiToken { get; } = Environment.GetEnvironmentVariable("API_TOKEN");
    public string? PublicUrl { get; } = ReadPublicUrl();
    public long MaxFileBytes { get; } = Number("MAX_FILE_MB", 250, 1, 4096) * 1024L * 1024;
    public long MaxStorageBytes { get; } = Number("MAX_STORAGE_MB", 10240, 10, 1048576) * 1024L * 1024;
    public int MaxDurationSeconds { get; } = Number("MAX_DURATION_MINUTES", 20, 1, 240) * 60;
    public int TimeoutSeconds { get; } = Number("DOWNLOAD_TIMEOUT_SECONDS", 900, 10, 7200);
    private static int Number(string key, int fallback, int min, int max) => Environment.GetEnvironmentVariable(key) is not { } value ? fallback :
        int.TryParse(value, out var number) && number >= min && number <= max ? number : throw new InvalidOperationException($"{key} must be between {min} and {max}.");
    private static string? ReadPublicUrl()
    {
        var value = Environment.GetEnvironmentVariable("PUBLIC_URL")?.TrimEnd('/');
        if (string.IsNullOrEmpty(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "")
            throw new InvalidOperationException("PUBLIC_URL must be an http(s) origin without a path, query or credentials.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}

public sealed record Video(string Id, string SourceUrl, string DeleteHash, DateTimeOffset CreatedAt,
    string Status = "queued", string? Title = null, double Progress = 0, string? Error = null, long Bytes = 0, double Duration = 0,
    string? Description = null, string? Author = null, int Width = 0, int Height = 0, int PosterWidth = 0, int PosterHeight = 0,
    string? PreviewHash = null)
{
    public object Public() => new
    {
        Id,
        SourceUrl,
        CreatedAt,
        Status,
        Title,
        Progress,
        Error,
        Bytes,
        Duration,
        Description,
        Author,
        Width,
        Height,
        shareUrl = $"/s/{Id}",
        mediaUrl = Status == "ready" ? $"/m/{Id}.mp4" : null,
        posterUrl = Status == "ready" ? $"/p/{Id}.jpg" : null,
        previewUrl = Status == "ready" && PreviewHash is not null ? $"/preview/{Id}/{PreviewHash}.jpg" : null
    };
}

public sealed class VideoStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, Video> videos = new();
    private readonly AppSettings settings;
    public Channel<string> Queue { get; } = Channel.CreateBounded<string>(20);
    public VideoStore(AppSettings settings, ILogger<VideoStore> logger)
    {
        this.settings = settings;
        Directory.CreateDirectory(Path.Combine(settings.DataPath, "videos"));
        foreach (var path in Directory.EnumerateFiles(Path.Combine(settings.DataPath, "videos"), "record.json", SearchOption.AllDirectories))
        {
            try
            {
                var video = JsonSerializer.Deserialize<Video>(File.ReadAllText(path))!;
                if (video.Id != Path.GetFileName(Path.GetDirectoryName(path)) || !ValidId(video.Id)) throw new InvalidDataException("Invalid video ID");
                if (video.Status is "queued" or "downloading" or "processing")
                {
                    video = video with { Status = "failed", Error = "The server restarted during this download. Submit the link again.", Progress = 0 };
                    foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Where(f => Path.GetFileName(f) != "record.json")) File.Delete(file);
                    Save(video);
                }
                if (video.Status == "ready" && !File.Exists(FilePath(video.Id, "video.mp4")))
                {
                    video = video with { Status = "failed", Error = "The media file is missing. Submit the link again." };
                    Save(video);
                }
                videos[video.Id] = video;
            }
            catch (Exception e) { logger.LogError(e, "Unable to load video record {Path}", path); }
        }
    }
    public static bool ValidId(string id) => id.Length == 32 && id.All(c => char.IsAsciiHexDigitLower(c));
    public string FilePath(string id, string file) => Path.Combine(settings.DataPath, "videos", id, file);
    public static long DirectoryBytes(string directory)
    {
        long total = 0;
        // yt-dlp renames completed fragments and files while the quota monitor runs.
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(path).Length; }
            catch (FileNotFoundException) { }
        }
        return total;
    }
    public long UsedBytes()
    {
        // Serialize against deletion and record replacement, but do not hold this lock
        // while downloading: progress and share-page reads must remain responsive.
        lock (gate) return DirectoryBytes(Path.Combine(settings.DataPath, "videos"));
    }
    public Video? Get(string id) { lock (gate) return videos.GetValueOrDefault(id); }
    public (Video[] Items, int Total) List(int offset, int limit)
    {
        lock (gate) return (videos.Values.OrderByDescending(v => v.CreatedAt).Skip(offset).Take(limit).ToArray(), videos.Count);
    }
    public (Video, string) Create(string url)
    {
        lock (gate)
        {
            if (videos.Values.Count(v => v.Status is "queued" or "downloading" or "processing") >= 20)
                throw new InvalidOperationException("The download queue is full. Please try again shortly.");
            if (UsedBytes() >= settings.MaxStorageBytes || videos.Count >= 10000)
                throw new InvalidOperationException("Storage is full. Delete an old share before adding another.");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var video = new Video(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), url, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), DateTimeOffset.UtcNow);
            Save(video);
            videos.Add(video.Id, video);
            if (!Queue.Writer.TryWrite(video.Id)) { Delete(video.Id); throw new InvalidOperationException("The download queue is full."); }
            return (video, token);
        }
    }
    public void Update(string id, Func<Video, Video> change, bool persist = true)
    {
        lock (gate)
        {
            if (!videos.TryGetValue(id, out var current)) return;
            var video = change(current);
            if (persist) Save(video);
            videos[id] = video;
        }
    }
    private void Save(Video video)
    {
        Directory.CreateDirectory(FilePath(video.Id, ""));
        var path = FilePath(video.Id, "record.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(video));
        File.Move(path + ".tmp", path, true);
    }
    public void Delete(string id)
    {
        lock (gate)
        {
            if (!videos.ContainsKey(id)) return;
            Directory.Delete(FilePath(id, ""), true);
            videos.Remove(id);
        }
    }
}
