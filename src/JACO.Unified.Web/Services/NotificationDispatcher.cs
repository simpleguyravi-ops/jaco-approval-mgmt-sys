using JACO.Unified.Infrastructure;

namespace JACO.Unified.Web.Services;

// Drains NotificationQueue one item at a time, each in its own DI scope -- PpfExecutor and
// UnifiedDbContext are Scoped, so a singleton BackgroundService can't hold them directly
// (same reasoning DigestSchedulerHostedService already applies per-tick, just per-item here).
// Runs the exact same PpfExecutor.RaiseEventAsync call Submit/Decide/Nudge used to make
// inline -- just off the request thread, so a slow or unreachable SMTP server no longer
// holds up the user's click.
public sealed class NotificationDispatcher(NotificationQueue queue, IServiceScopeFactory scopeFactory, ILogger<NotificationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (requestId, eventCode) in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var ppf = scope.ServiceProvider.GetRequiredService<PpfExecutor>();
                    await ppf.RaiseEventAsync(requestId, eventCode);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Notification dispatch failed for Request {RequestId}, event {EventCode}.", requestId, eventCode);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
