using System.Globalization;
using System.Net;
using System.Text;

namespace ShareMe;

public static class SharePage
{
    public static string? Text(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= limit) return text;
        var length = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..length].TrimEnd() + "…";
    }

    public static string Render(string template, Video? video, string origin, bool hasPoster)
    {
        var title = Text(video?.Title, 300) ?? "Shared video";
        var ready = video?.Status == "ready";
        var description = video is null ? "This video does not exist or has been deleted."
            : ready ? Text(video.Description, 500) ?? $"{(string.IsNullOrWhiteSpace(video.Author) ? "Video" : $"Video by {video.Author}")} from {new Uri(video.SourceUrl).Host}. Watch or download on Share.me."
            : video.Status == "failed" ? video.Error ?? "The video could not be downloaded."
            : "This video is being downloaded. Check back shortly to watch it.";
        var tags = new StringBuilder();
        void Meta(string key, string value, bool property = true) => tags.AppendLine(
            $"<meta {(property ? "property" : "name")}=\"{key}\" content=\"{WebUtility.HtmlEncode(value)}\">");
        Meta("description", description, false);
        Meta("og:site_name", "Share.me");
        Meta("og:title", title);
        Meta("og:description", description);
        Meta("og:type", ready ? "video.other" : "website");
        Meta("twitter:card", ready && hasPoster ? "summary_large_image" : "summary", false);
        Meta("twitter:title", title, false);
        Meta("twitter:description", description, false);
        if (video is not null)
        {
            var url = $"{origin}/s/{video.Id}";
            Meta("og:url", url);
            tags.AppendLine($"<link rel=\"canonical\" href=\"{WebUtility.HtmlEncode(url)}\">");
            if (!string.IsNullOrWhiteSpace(video.Author)) Meta("author", video.Author, false);
            if (ready)
            {
                var mediaUrl = $"{origin}/m/{video.Id}.mp4";
                if (hasPoster)
                {
                    var posterUrl = $"{origin}/p/{video.Id}.jpg";
                    Meta("og:image", posterUrl);
                    Meta("og:image:url", posterUrl);
                    if (origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Meta("og:image:secure_url", posterUrl);
                    Meta("og:image:type", "image/jpeg");
                    Meta("og:image:alt", title);
                    if (video.PosterWidth > 0 && video.PosterHeight > 0)
                    {
                        Meta("og:image:width", video.PosterWidth.ToString(CultureInfo.InvariantCulture));
                        Meta("og:image:height", video.PosterHeight.ToString(CultureInfo.InvariantCulture));
                    }
                    Meta("twitter:image", posterUrl, false);
                    Meta("twitter:image:alt", title, false);
                }
                Meta("og:video", mediaUrl);
                Meta("og:video:url", mediaUrl);
                if (origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Meta("og:video:secure_url", mediaUrl);
                Meta("og:video:type", "video/mp4");
                if (video.Width > 0 && video.Height > 0)
                {
                    Meta("og:video:width", video.Width.ToString(CultureInfo.InvariantCulture));
                    Meta("og:video:height", video.Height.ToString(CultureInfo.InvariantCulture));
                }
                if (video.Duration > 0) Meta("video:duration", Math.Ceiling(video.Duration).ToString(CultureInfo.InvariantCulture));
            }
        }
        // Preview crawlers do not execute the client scripts. Render every field in
        // the initial HTML and escape source text before inserting it into markup.
        return template.Replace("<title>Shared video — Share.me</title>", $"<title>{WebUtility.HtmlEncode(title)} — Share.me</title>")
            .Replace("<h1 id=\"video-title\">Shared video</h1>", $"<h1 id=\"video-title\">{WebUtility.HtmlEncode(title)}</h1>")
            .Replace("<!-- SHARE_META -->", tags.ToString());
    }
}
