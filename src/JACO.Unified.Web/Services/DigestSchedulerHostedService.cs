using JACO.Unified.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Services;

// Polls once a minute for any DigestSchedule whose NextRunAtUtc has arrived, runs it, then
// recomputes the next occurrence. A singleton BackgroundService can't hold scoped services
// (UnifiedDbContext, DigestService) directly, so each tick opens its own DI scope -- same
// reasoning ApiGatewayMiddleware already applies to per-request scoping, just per-tick here.
public sealed class DigestSchedulerHostedService(IServiceScopeFactory scopeFactory, ILogger<DigestSchedulerHostedService> logger) : BackgroundService
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Digest scheduler tick failed."); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { /* shutting down */ }
        }
    }

    async Task TickAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UnifiedDbContext>();

        var due = await db.DigestSchedules
            .Where(s => s.Enabled && s.NextRunAtUtc != null && s.NextRunAtUtc <= DateTime.UtcNow)
            .ToListAsync();
        if (due.Count == 0) return;

        var digestService = scope.ServiceProvider.GetRequiredService<DigestService>();
        foreach (var schedule in due)
        {
            logger.LogInformation("Running scheduled digest for Approval Type {ApprovalTypeId}.", schedule.ApprovalTypeId);
            await digestService.RunDigestAsync(schedule.ApprovalTypeId, "Scheduled", null);

            schedule.LastRunAtUtc = DateTime.UtcNow;
            schedule.NextRunAtUtc = DigestService.ComputeNextRunUtc(schedule.RecurrenceType, schedule.IntervalDays, schedule.StartTime, schedule.LastRunAtUtc);
        }
        await db.SaveChangesAsync();
    }
}
