using System.ComponentModel.DataAnnotations;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;

namespace JACO.Unified.Web.Models;

public sealed class LoginViewModel
{
    [Required] public string UserName { get; set; } = "";
    [Required] public string Password { get; set; } = "";
    public bool RememberMe { get; set; } = true;
    public string? ReturnUrl { get; set; }
}

public sealed class ChangePasswordViewModel
{
    [Required] public string CurrentPassword { get; set; } = "";
    [Required, MinLength(8)] public string NewPassword { get; set; } = "";
    [Required, Compare(nameof(NewPassword))] public string ConfirmPassword { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public sealed class UserAccountEditViewModel
{
    public int Id { get; set; }
    [Required] public string UserName { get; set; } = "";
    [Required] public string DisplayName { get; set; } = "";
    public string? Department { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsAdmin { get; set; }
    public bool IsAuditor { get; set; }
    public bool HasPassword { get; set; }
    // Only used on Create (required there) or to reset an existing user's password.
    public string? NewPassword { get; set; }
}

public sealed class RequestFormViewModel
{
    public required Request Request { get; init; }
    public required ApprovalType ApprovalType { get; init; }
    public required List<WorkflowField> Fields { get; init; }
    public required Dictionary<string, string?> Values { get; init; }
    public required Dictionary<string, List<PicklistValue>> Picklists { get; init; }
    // Empty on Create (no request row exists yet to attach a file to).
    public List<RequestAttachment> Attachments { get; init; } = [];
    public string? ValidationError { get; init; }
}

public sealed class RequestDetailsViewModel
{
    public required Request Request { get; init; }
    public required ApprovalType ApprovalType { get; init; }
    public required List<SubmittedField> Fields { get; init; }
    public required List<TimelineLevel> Timeline { get; init; }
    public required List<RequestAttachment> Attachments { get; init; }
    public required bool IsCreator { get; init; }
    public required bool IsEligibleApprover { get; init; }
    public required bool IsAdminOverride { get; init; }
    public required bool CanWithdraw { get; init; }
    public required bool CanEdit { get; init; }
    public required bool IsAdmin { get; init; }
}

public sealed class RequestListRow
{
    public required Request Request { get; init; }
    public required string ApprovalTypeName { get; init; }
}

public sealed class LevelFormRow
{
    public int LevelNo { get; set; }
    public string Mode { get; set; } = "ANY_ONE";
    public int? RequiredCount { get; set; }
    public List<int> ApproverUserIds { get; set; } = [];
}

public sealed class CriteriaFormRow
{
    public string FieldKey { get; set; } = "";
    public string Operator { get; set; } = "=";
    public string ComparisonValue { get; set; } = "";
}

public sealed class RoutingRuleListItem
{
    public int Id { get; set; }
    public string RuleName { get; set; } = "";
    public int Priority { get; set; }
    public bool Active { get; set; }
    public string CriteriaSummary { get; set; } = "";
    public int LevelCount { get; set; }
}

// A row = one full rule (criteria + every level's approver) from a bulk CSV upload --
// ported from the original Approval engine's Rule Builder import.
public sealed class RoutingRuleImportRow
{
    public int RowNumber { get; set; }
    public string RuleName { get; set; } = "";
    public int Priority { get; set; }
    public bool Active { get; set; } = true;
    public string? Branch { get; set; }
    public string? Company { get; set; }
    public string? SalesChannel { get; set; }
    public string? OrderType { get; set; }
    public string? VehicleModel { get; set; }
    public string? ModelYear { get; set; }
    public string? VinNumber { get; set; }
    public string? DiscountFrom { get; set; }
    public string? DiscountTo { get; set; }
    public string? ValidFrom { get; set; }
    public string? ValidTo { get; set; }
    public List<string> LevelApproverEmails { get; set; } = [];

    public List<(int Id, string Name)> ResolvedApprovers { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public bool IsValid => Errors.Count == 0;
    public string CriteriaSummary { get; set; } = "";
}

public sealed class RoutingRuleImportPreview
{
    public int ApprovalTypeId { get; set; }
    public string ApprovalTypeName { get; set; } = "";
    public List<RoutingRuleImportRow> Rows { get; set; } = [];
    public int ValidCount => Rows.Count(r => r.IsValid);
    public int ErrorCount => Rows.Count(r => !r.IsValid);
    public string EncodedFile { get; set; } = "";
    public string FileName { get; set; } = "";
}

// One row = one user account, plus its Create/View grant per Approval Type -- lets an
// admin onboard a batch of new starters (and update existing accounts' access) in one
// file instead of one UserAccounts + UsersRoles visit per person.
public sealed class UserImportRow
{
    public int RowNumber { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Department { get; set; }
    public string? Branch { get; set; }
    public string? Email { get; set; }
    public bool IsAdmin { get; set; }
    public bool IsAuditor { get; set; }
    public bool Active { get; set; } = true;
    public bool IsNewUser { get; set; }
    public Dictionary<int, (bool CanCreate, bool CanView)> Permissions { get; set; } = [];
    public string PermissionsSummary { get; set; } = "";
    public List<string> Errors { get; set; } = [];
    public bool IsValid => Errors.Count == 0;
}

// Plain class, not a tuple -- a tuple's field names don't round-trip through
// System.Text.Json, and this needs to survive a TempData JSON serialize/deserialize.
public sealed class UserCredentialReveal
{
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class UserImportPreview
{
    public List<UserImportRow> Rows { get; set; } = [];
    public List<(int Id, string Code, string Name)> ApprovalTypes { get; set; } = [];
    public int ValidCount => Rows.Count(r => r.IsValid);
    public int ErrorCount => Rows.Count(r => !r.IsValid);
    public int NewCount => Rows.Count(r => r.IsNewUser);
    public string EncodedFile { get; set; } = "";
    public string FileName { get; set; } = "";
}

// Fixed-size row counts (no JS row-adder) -- generous enough for realistic routing rules;
// blank criteria rows and empty-approver levels are simply skipped on save.
public sealed class RuleFormViewModel
{
    public const int MaxCriteriaRows = 8;
    public const int MaxLevels = 5;

    public required int ApprovalTypeId { get; init; }
    public RoutingRule? Rule { get; init; }
    public int DefaultPriority { get; init; } = 10;
    public required List<CriteriaFormRow> Criteria { get; init; }
    public required List<LevelFormRow> Levels { get; init; }
    public required List<WorkflowField> AvailableFields { get; init; }
    public required List<AppUser> Users { get; init; }
}

// ---------- Bulk Rule (see RoutingRulesController.Bulk*) ----------
// Authors several ordinary routing rules at once -- e.g. one approver chain per Branch, or
// per Branch further split into discount bands -- instead of one rule at a time. The whole
// screen is a single client-side page (Views/RoutingRules/Bulk.cshtml owns the JS state
// tree and interaction; "Split by X" -> add values as live cards -> optionally drill into
// one value to split it further by a range); on Save, the entire tree is serialized to JSON
// client-side and posted once as `stateJson`, deserialized here into BulkRuleSubmission, and
// turned into real RoutingRule/RoutingRuleCriteria/WorkflowStep/WorkflowStepApprover rows --
// one rule per group (or per band, for a drilled-in group) that actually has an approver set.
public sealed class BulkRuleSubmission
{
    public int ApprovalTypeId { get; set; }
    public string RuleBaseName { get; set; } = "";
    public string SplitFieldKey { get; set; } = "";
    public int Priority { get; set; } = 10;
    public bool Active { get; set; } = true;
    public List<BulkRuleCriteriaDto> SharedCriteria { get; set; } = [];
    public List<BulkRuleGroupDto> Groups { get; set; } = [];
}

public sealed class BulkRuleCriteriaDto
{
    public string FieldKey { get; set; } = "";
    public string Operator { get; set; } = "=";
    public string Value { get; set; } = "";
}

public sealed class BulkRuleGroupDto
{
    public string Value { get; set; } = "";
    public bool Drilling { get; set; }
    public string? DrillFieldKey { get; set; }
    public List<BulkRuleLevelDto> Levels { get; set; } = []; // used when !Drilling
    public List<BulkRuleBandDto> Bands { get; set; } = []; // used when Drilling
}

public sealed class BulkRuleBandDto
{
    public double? Low { get; set; }
    public double? High { get; set; }
    public List<BulkRuleLevelDto> Levels { get; set; } = [];
}

public sealed class BulkRuleLevelDto
{
    public List<int> Approvers { get; set; } = [];
}

public sealed class RequestListViewModel
{
    public required List<RequestListRow> Rows { get; init; }
    public required List<ApprovalType> Types { get; init; }
    public required List<ApprovalType> CreatableTypes { get; init; }
    public required int DraftCount { get; init; }
    public required int PendingCount { get; init; }
    public required int ApprovedCount { get; init; }
    public required int RejectedCount { get; init; }
    public string? Search { get; init; }
    public string? Status { get; init; }
    public int? ApprovalTypeId { get; init; }
    public string? Sort { get; init; }
    public string Dir { get; init; } = "asc";
    public bool IsAllView { get; init; }
    public int TotalCount { get; init; }
    public int PageSize { get; init; }
    public List<(string FieldKey, string FieldLabel)> AvailableColumns { get; init; } = [];
    public List<string> SelectedColumns { get; init; } = [];
    public List<(int Id, string Name)> Users { get; init; } = [];
    public int? PendingWithUserId { get; init; }
    public Dictionary<long, string> PendingWithNames { get; init; } = [];
    public Dictionary<long, Dictionary<string, string>> ExtraColumns { get; init; } = [];
    public DateTime? DateFrom { get; init; }
    public DateTime? DateTo { get; init; }
}

public sealed class MailTemplateEditViewModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public bool IsTableTemplate { get; set; }
    public bool IsActive { get; set; } = true;
    public string? PreviewSubject { get; set; }
    public string? PreviewBody { get; set; }
}

public sealed class PpfRuleListItem
{
    public int Id { get; set; }
    public string ApprovalTypeName { get; set; } = "";
    public string EventCode { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string ToMode { get; set; } = "";
    public bool Active { get; set; }
}

public sealed class PpfRuleEditViewModel
{
    public int Id { get; set; }
    public int ApprovalTypeId { get; set; }
    public string EventCode { get; set; } = "Created";
    public int MailTemplateId { get; set; }
    public string ToMode { get; set; } = "Creator";
    public string? ToAddress { get; set; }
    public string? ToFieldKey { get; set; }
    public string CcMode { get; set; } = "None";
    public string? CcAddress { get; set; }
    public string? CcFieldKey { get; set; }
    public int SequenceNo { get; set; } = 10;
    public bool Active { get; set; } = true;
    public List<(int Id, string Name)> ApprovalTypes { get; set; } = [];
    public List<(int Id, string Name)> MailTemplates { get; set; } = [];
}

public sealed class DigestViewModel
{
    public int? RecipientUserId { get; set; }
    public int? MailTemplateId { get; set; }
    public List<(int Id, string Name)> Recipients { get; set; } = [];
    public List<(int Id, string Name)> TableTemplates { get; set; } = [];
    public string? PreviewSubject { get; set; }
    public string? PreviewBody { get; set; }
    public string? ResultMessage { get; set; }
    public int PendingCount { get; set; }
}

public sealed class DigestScheduleViewModel
{
    public int ApprovalTypeId { get; set; }
    public required List<ApprovalType> Types { get; set; }
    public required DigestSchedule Schedule { get; set; }
    public List<(int Id, string Name)> TableTemplates { get; set; } = [];
    public DigestRun? LastRun { get; set; }
}

public sealed class PpfMonitorFilter
{
    public string? RequestNumber { get; set; }
    public int? ApprovalTypeId { get; set; }
    public string? EventCode { get; set; }
    public string? ActionType { get; set; }
    public string? Status { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string? Sort { get; set; }
    public string Dir { get; set; } = "asc";
}

public sealed class PpfMonitorRow
{
    public long Id { get; set; }
    public long RequestId { get; set; }
    public string RequestNumber { get; set; } = "";
    public int ApprovalTypeId { get; set; }
    public string ApprovalTypeName { get; set; } = "";
    public string EventCode { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string? Target { get; set; }
    public int AttemptNo { get; set; }
    public string Status { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

public sealed class PpfMonitorViewModel
{
    public PpfMonitorFilter Filter { get; set; } = new();
    public int TotalCount { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<PpfMonitorRow> Rows { get; set; } = [];
    public List<(int Id, string Name)> ApprovalTypes { get; set; } = [];
    public List<string> EventCodes { get; set; } = [];
    public List<string> ActionTypes { get; set; } = [];
}

public sealed class RoutingLogFilter
{
    public string? RequestNumber { get; set; }
    public string? OutcomeCode { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Sort { get; set; }
    public string Dir { get; set; } = "asc";
}

public sealed class AuditLogFilter
{
    public string? RequestNumber { get; set; }
    public string? ActionCode { get; set; }
    public string? Source { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public bool AdminOverrideOnly { get; set; }
    public string? Sort { get; set; }
    public string Dir { get; set; } = "asc";
}

public sealed class AuditLogRow
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? RequestNumber { get; set; }
    public string? UserName { get; set; }
    public string ActionCode { get; set; } = "";
    public string? DetailsJson { get; set; }
    public string Source { get; set; } = "Web";
}

public sealed class ApiRequestLogFilter
{
    public string? ClientName { get; set; }
    public string? Path { get; set; }
    public int? StatusCode { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Sort { get; set; }
    public string Dir { get; set; } = "asc";
}

public sealed class DigestRunFilter
{
    public int? ApprovalTypeId { get; set; }
    public string? TriggeredBy { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Sort { get; set; }
    public string Dir { get; set; } = "asc";
}

public sealed class ClearLogsResult
{
    public string LogType { get; set; } = "";
    public DateTime? BeforeDate { get; set; }
    public int MatchingCount { get; set; }
    // Compliance floor -- see CockpitController.GetRetentionFloorAsync. Entries newer than
    // this can't be cleared regardless of what an admin types, so a mis-picked date can't
    // wipe active/recent data in one click. Only actually restrictive when IsProduction is
    // true (see SystemSettings) -- in Test mode MaxAllowedBeforeDate is "today," i.e. no
    // real restriction.
    public DateTime MaxAllowedBeforeDate { get; set; }
    public bool IsProduction { get; set; }
    public bool ExceedsMinRetention => BeforeDate is not null && BeforeDate.Value > MaxAllowedBeforeDate;
}

public sealed class LogArchiveFilter
{
    public string? LogType { get; set; }
    public string? Sort { get; set; }
    public string Dir { get; set; } = "desc";
}

public sealed class ReassignEditViewModel
{
    public long RequestId { get; set; }
    public string RequestNumber { get; set; } = "";
    public int LevelNo { get; set; }
    public List<(int Id, string Name)> CurrentApprovers { get; set; } = [];
    public int? OldUserId { get; set; }
    public int NewUserId { get; set; }
    public string Reason { get; set; } = "";
    public List<(int Id, string Name, string? Department)> Users { get; set; } = [];
}

public sealed class EmailActionResultViewModel
{
    public required bool Ok { get; init; }
    public required string Message { get; init; }
    public required string RequestNumber { get; init; }
    public required string Decision { get; init; }
}

public sealed class EmailRejectViewModel
{
    public required string Token { get; init; }
    public required string RequestNumber { get; init; }
    public string? Error { get; init; }
}

public sealed class EmailApproveViewModel
{
    public required string Token { get; init; }
    public required string RequestNumber { get; init; }
    public string? Subject { get; init; }
}

public sealed class BulkReassignViewModel
{
    public List<long> RequestIds { get; set; } = [];
    public List<string> RequestNumbers { get; set; } = [];
    public List<(int Id, string Name)> CurrentApprovers { get; set; } = [];
    public int? OldUserId { get; set; }
    public int NewUserId { get; set; }
    public string Reason { get; set; } = "";
    public List<(int Id, string Name, string? Department)> Users { get; set; } = [];
}
