using JACO.Unified.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Infrastructure;

public sealed class UnifiedDbContext(DbContextOptions<UnifiedDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<UserWorkflowPermission> UserWorkflowPermissions => Set<UserWorkflowPermission>();
    public DbSet<ApprovalType> ApprovalTypes => Set<ApprovalType>();
    public DbSet<WorkflowVersion> WorkflowVersions => Set<WorkflowVersion>();
    public DbSet<WorkflowField> WorkflowFields => Set<WorkflowField>();
    public DbSet<PicklistValue> PicklistValues => Set<PicklistValue>();
    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<RoutingRuleCriteria> RoutingRuleCriteria => Set<RoutingRuleCriteria>();
    public DbSet<WorkflowStep> WorkflowSteps => Set<WorkflowStep>();
    public DbSet<WorkflowStepApprover> WorkflowStepApprovers => Set<WorkflowStepApprover>();
    public DbSet<Request> Requests => Set<Request>();
    public DbSet<RequestAttachment> RequestAttachments => Set<RequestAttachment>();
    public DbSet<RequestAction> RequestActions => Set<RequestAction>();
    public DbSet<WorkflowParticipant> WorkflowParticipants => Set<WorkflowParticipant>();
    public DbSet<ApproverReassignment> ApproverReassignments => Set<ApproverReassignment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RoutingLogEntry> RoutingLog => Set<RoutingLogEntry>();
    public DbSet<MailTemplate> MailTemplates => Set<MailTemplate>();
    public DbSet<PostProcessingRule> PostProcessingRules => Set<PostProcessingRule>();
    public DbSet<PostProcessingExecution> PostProcessingExecutions => Set<PostProcessingExecution>();
    public DbSet<EmailSettings> EmailSettings => Set<EmailSettings>();
    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<ApiSettings> ApiSettings => Set<ApiSettings>();
    public DbSet<ApiRequestLog> ApiRequestLog => Set<ApiRequestLog>();
    public DbSet<DigestSchedule> DigestSchedules => Set<DigestSchedule>();
    public DbSet<DigestRun> DigestRuns => Set<DigestRun>();
    public DbSet<DigestRunRecipient> DigestRunRecipients => Set<DigestRunRecipient>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Request>().HasIndex(r => r.RequestNumber).IsUnique();
        modelBuilder.Entity<WorkflowField>().HasIndex(f => new { f.ApprovalTypeId, f.FieldKey });
        modelBuilder.Entity<PicklistValue>().HasIndex(p => new { p.LookupType, p.Value });
        modelBuilder.Entity<ApiClient>().HasIndex(c => c.KeyPrefix);
        modelBuilder.Entity<DigestSchedule>().HasIndex(s => s.ApprovalTypeId).IsUnique();
    }
}
