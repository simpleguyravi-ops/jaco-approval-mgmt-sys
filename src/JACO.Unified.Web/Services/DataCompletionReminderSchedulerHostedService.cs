using JACO.Unified.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Services;

// Polls once a minute for any DataCompletionReminderRule whose NextRunAtUtc has arrived,
// same pattern as DigestSchedulerHostedService (a singleton BackgroundService can't hold
// scoped services directly, so each tick opens its own DI scope). Reuses
// DigestService.ComputeNextRunUtc for the actual recurrence math -- EveryNDays/Weekdays
// means the same thing for both features, so there's no reason to keep two copies of it.
public sealed class DataCompletionReminderSchedulerHostedService(IServiceScopeFactory scopeFactory, ILogger<DataCompletionReminderSchedulerHostedService> logger) : BackgroundService
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Data completion reminder scheduler tick failed."); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { /* shutting down */ }
        }
    }

    async Task TickAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UnifiedDbContext>();

        var due = await db.DataCompletionReminderRules
            .Where(r => r.Enabled && r.NextRunAtUtc != null && r.NextRunAtUtc <= DateTime.UtcNow)
            .ToListAsync();
        if (due.Count == 0) return;

        var reminderService = scope.ServiceProvider.GetRequiredService<DataCompletionReminderService>();
        foreach (var rule in due)
        {
            logger.LogInformation("Running scheduled data completion reminder for rule {RuleId}.", rule.Id);
            await reminderService.RunAsync(rule.Id, "Scheduled", null);

            rule.NextRunAtUtc = DigestService.ComputeNextRunUtc(rule.RecurrenceType, rule.IntervalDays, rule.StartTime, rule.LastRunAtUtc);
        }
        await db.SaveChangesAsync();
    }
}
