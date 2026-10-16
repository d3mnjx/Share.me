namespace ShareMe;

public sealed class RetentionWorker(VideoStore store, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Also apply a reduced retention setting when the server restarts.
        store.CleanupRequests.Writer.TryWrite(true);
        await foreach (var _ in store.CleanupRequests.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var removed = store.Prune();
                if (removed > 0) logger.LogInformation("Removed {Count} old shares to enforce the video retention limit", removed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(e, "Video retention cleanup failed; will retry after the next submission or completed job");
            }
        }
    }
}
