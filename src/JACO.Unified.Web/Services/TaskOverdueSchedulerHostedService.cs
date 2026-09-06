using JACO.Unified.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Services;

// Polls once a minute for any open AssignedTask whose NextOverdueCheckAtUtc has arrived,
// same pattern as DataCompletionReminderSchedulerHostedService (a singleton BackgroundService
// can't hold a scoped UnifiedDbContext directly, so each tick opens its own DI scope).
// Fires "TaskOverdue" through the same NotificationQueue -> PpfExecutor pipeline every other
// event already uses -- no new event-raising mechanism. Re-fires daily (a fixed cadence for
// v1, not admin-configurable like Data Completion Reminders' RecurrenceType) until the task
// is completed, at which point TasksController.Complete clears NextOverdueCheckAtUtc and this
// scan stops touching it.
public sealed class TaskOverdueSchedulerHostedService(IServiceScopeFactory scopeFactory, NotificationQueue notifications, ILogger<TaskOverdueSchedulerHostedService> logger) : BackgroundService
{
    static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    static readonly TimeSpan RenudgeInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Task overdue scheduler tick failed."); }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { /* shutting down */ }
        }
    }

    async Task TickAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UnifiedDbContext>();

        var due = await db.AssignedTasks
            .Where(t => t.Status == "Open" && t.NextOverdueCheckAtUtc != null && t.NextOverdueCheckAtUtc <= DateTime.UtcNow)
            .ToListAsync();
        if (due.Count == 0) return;

        foreach (var task in due)
        {
            logger.LogInformation("Task {TaskId} on request {RequestId} is overdue -- firing TaskOverdue.", task.Id, task.RequestId);
            notifications.Enqueue(task.RequestId, "TaskOverdue", task.AssignedToUserId ?? 0);
            task.NextOverdueCheckAtUtc = DateTime.UtcNow.Add(RenudgeInterval);
        }
        await db.SaveChangesAsync();
    }
}
