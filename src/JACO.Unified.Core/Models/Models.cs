namespace JACO.Unified.Core.Models;

// ============================================================
// Identity (synced from Portal, same pattern as CR/Approval today)
// ============================================================
public sealed class AppUser
{
    public int Id { get; set; }
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? Department { get; set; }
    public string? Branch { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; } = true;

    // Local, standalone login -- independent of Portal SSO. Null PasswordHash means this
    // account was only ever auto-provisioned from a Portal-SSO login and cannot sign in
    // here directly until an admin sets a password via User Accounts.
    public string? PasswordHash { get; set; }
    public string? PasswordSalt { get; set; }
    public bool MustChangePassword { get; set; }
    // Platform-admin within Unified specifically -- becomes a UNIFIED_ADMIN role claim on
    // local login. A Portal-SSO login is still separately recognized as admin via its own
    // PORTAL_ADMIN/SYSTEM_ADMIN claims (see UnifiedAdmin policy), independent of this flag.
    public bool IsAdmin { get; set; }
    // Read-only access to /Reports without any of the rest of Administration -- a distinct
    // grant from IsAdmin so a compliance/audit user can be given exactly that and nothing
    // more (can't touch routing, fields, users, or PPF config).
    public bool IsAuditor { get; set; }

    // Account lockout: only tracked/enforced for local login attempts (see
    // AccountController.Login) -- resets to 0/null on any successful local sign-in.
    public int FailedLoginCount { get; set; }
    public DateTime? LockedUntil { get; set; }
}

// CanCreate = can raise a new request of this type.
// CanView = "Display All" -- oversight visibility into every request of this type, not
// just the ones this user created or was an eligible approver on (that access is
// automatic and needs no grant at all -- see RequestService.IsParticipantAsync).
public sealed class UserWorkflowPermission
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public int ApprovalTypeId { get; set; }
    public bool CanCreate { get; set; }
    public bool CanView { get; set; }
}

// ============================================================
// Approval Type + the field catalog that drives the dynamic form
// ============================================================
public sealed class ApprovalType
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class WorkflowVersion
{
    public int Id { get; set; }
    public int ApprovalTypeId { get; set; }
    public int VersionNo { get; set; }
    public bool IsCurrent { get; set; }
}

public static class FieldDataType
{
    public const string Text = "Text";
    public const string TextArea = "TextArea";
    public const string Number = "Number";
    public const string Currency = "Currency";
    public const string Date = "Date";
    public const string Dropdown = "Dropdown";
}

// The single source of truth for a type's data shape -- drives the dynamic Create/Edit
// form, the read-only Submitted Details panel, Rule Builder's criteria dropdown, and (via
// LookupType) which PicklistValues populate a Dropdown field. ApprovalTypeId null = generic
// field offered to every type (e.g. a platform-wide "Comments" field), same convention the
// old WorkflowFields catalog already used for CR/Approval's shared fields.
public sealed class WorkflowField
{
    public int Id { get; set; }
    public int? ApprovalTypeId { get; set; }
    public string FieldKey { get; set; } = "";
    public string FieldLabel { get; set; } = "";
    public string DataType { get; set; } = FieldDataType.Text;
    public int DisplayOrder { get; set; }

    // Independent form-behavior toggles -- deliberately four separate flags rather than one
    // "mode" enum, since a field can legitimately be e.g. visible+read-only+not required
    // (a system-derived value shown for context) or visible+required (normal input).
    public bool IsVisible { get; set; } = true;
    public bool IsReadOnly { get; set; }
    public bool IsRequired { get; set; }
    // Hides the VALUE from the request's own creator once it's in front of an approver --
    // a viewing-time rule, unrelated to whether the creator could edit it at submission.
    public bool IsSensitive { get; set; }
    // Whether the external API (see ApprovalsApiController) accepts this field on Create
    // and returns it on Get -- independent of IsVisible, which only controls the browser
    // form. Defaults true so every existing field keeps working through the API exactly as
    // it does today; an admin unchecks this for a field that should stay UI/human-only
    // (e.g. an internal note) without touching IsVisible or Active at all.
    public bool IncludeInApi { get; set; } = true;

    // Only meaningful when DataType == Dropdown -- the PicklistValues.LookupType this
    // field's options are pulled from (e.g. "Department", "DiscountReason").
    public string? LookupType { get; set; }

    public bool Active { get; set; } = true;
}

// Generalizes CR's CRLookupValue / Sales Discount's SalesDiscountLookupValue into one
// table shared by every type -- a WorkflowField of DataType=Dropdown just names which
// LookupType its options come from.
public sealed class PicklistValue
{
    public int Id { get; set; }
    public string LookupType { get; set; } = "";
    public string Value { get; set; } = "";
    public string DisplayText { get; set; } = "";
    public int SortOrder { get; set; } = 10;
    public bool Active { get; set; } = true;
    // Optional per-value metadata -- e.g. a Branch lookup value's associated account-team
    // email, so a submitted branch can resolve a PPF notification recipient (via the
    // Field ToMode reading a value injected into DataJson at submit time) without a
    // dedicated Branches table/admin screen just for this one lookup.
    public string? ExtraData { get; set; }
}

// ============================================================
// Routing (unchanged in shape from the existing Approval engine -- already fully generic)
// ============================================================
public sealed class RoutingRule
{
    public int Id { get; set; }
    public int WorkflowVersionId { get; set; }
    public string RuleName { get; set; } = "";
    public int Priority { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class RoutingRuleCriteria
{
    public int Id { get; set; }
    public int RoutingRuleId { get; set; }
    public string FieldKey { get; set; } = "";
    public string Operator { get; set; } = "=";
    public string ComparisonValue { get; set; } = "";
    public int SortOrder { get; set; }
}

public sealed class WorkflowStep
{
    public int Id { get; set; }
    public int WorkflowVersionId { get; set; }
    public int RoutingRuleId { get; set; }
    public int LevelNo { get; set; }
    public string Mode { get; set; } = "ANY_ONE";
    // Only meaningful when Mode == MINIMUM_COUNT -- how many of this level's approvers
    // must Approve before the level completes. Falls back to "everyone" when unset.
    public int? RequiredCount { get; set; }
}

public sealed class WorkflowStepApprover
{
    public int Id { get; set; }
    public int WorkflowStepId { get; set; }
    public int UserId { get; set; }
}

// ============================================================
// THE unified request -- this row IS the workflow item for its entire lifecycle
// (Draft -> Pending -> Approved/Rejected/Sent Back -> Withdrawn), never copied or
// snapshotted into a second table. DataJson carries every WorkflowField value for
// whatever ApprovalType this is -- same mechanism the old Approval engine already used
// for its DataJson, just now also driving the INPUT form, not only the read-only display.
// ============================================================
public sealed class Request
{
    public long Id { get; set; }
    public string RequestNumber { get; set; } = "";
    public int ApprovalTypeId { get; set; }
    public int? WorkflowVersionId { get; set; }
    public int? RoutingRuleId { get; set; }
    public int CreatorUserId { get; set; }
    public string CreatorUserName { get; set; } = "";
    public string? Subject { get; set; }
    public string Status { get; set; } = "Draft"; // Draft, Pending, Approved, Rejected, Sent Back, Withdrawn
    public int? CurrentLevelNo { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class RequestAttachment
{
    public long Id { get; set; }
    public long RequestId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string StoredFileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long FileSize { get; set; }
    public int UploadedByUserId { get; set; }
    public string UploadedByUserName { get; set; } = "";
    public DateTime UploadedAt { get; set; }
}

public sealed class RequestAction
{
    public long Id { get; set; }
    public long RequestId { get; set; }
    public int LevelNo { get; set; }
    public int UserId { get; set; }
    public string ActionCode { get; set; } = ""; // Approve, Reject, SendBack
    public string? Comments { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class WorkflowParticipant
{
    public long Id { get; set; }
    public long RequestId { get; set; }
    public int UserId { get; set; }
    public string ParticipantType { get; set; } = ""; // Creator, Approver
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastSeenAt { get; set; }
}

public sealed class ApproverReassignment
{
    public long Id { get; set; }
    public long RequestId { get; set; }
    public int LevelNo { get; set; }
    public int? OldUserId { get; set; }
    public int NewUserId { get; set; }
    public string Reason { get; set; } = "";
    public int ChangedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public long? RequestId { get; set; }
    public int? UserId { get; set; }
    public string ActionCode { get; set; } = "";
    public string? DetailsJson { get; set; }
    // "Web" (browser UI) or "Api" (an external system via the API layer) -- lets the
    // existing audit screen distinguish a front-end action from a remote one without a
    // separate log to cross-reference.
    public string Source { get; set; } = "Web";
    public DateTime CreatedAt { get; set; }
}

// ============================================================
// External API access -- SAP and any other outside system creating/reading approvals
// through the API layer (see ApprovalsApiController), not through the browser UI.
// ============================================================

// Admin-managed credential for one external caller. Only the hash is stored -- the
// plaintext key is shown once at creation/regeneration and never again, same principle as
// a password. KeyPrefix is a short, non-secret lookup aid (also used to find the matching
// row without scanning every hash) shown in the UI so an admin can tell keys apart.
public sealed class ApiClient
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string KeyHash { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

// One row (Id=1) -- a master switch for the whole external API, independent of any single
// ApiClient's Active flag, so an admin can kill all external access instantly without
// having to deactivate every client one at a time. LogRequests defaults on since the whole
// point of this layer is an audit trail; it's a toggle only for extraordinary cases
// (very high call volume, storage pressure), not a normal operating mode.
public sealed class ApiSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public bool LogRequests { get; set; } = true;
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedByUserName { get; set; }
}

// Full HTTP-level record of every call to the external API, successful or not -- separate
// from AuditLog (which records business actions like Submit/Approve regardless of caller).
// A rejected/forged call attempt (bad or missing key) is still logged here with
// ApiClientId null, since that's exactly the kind of thing a security review needs to see.
public sealed class ApiRequestLog
{
    public long Id { get; set; }
    public int? ApiClientId { get; set; }
    // Snapshot, not just a foreign key -- the row stays meaningful even if the client is
    // later renamed or removed.
    public string? ClientName { get; set; }
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string? QueryString { get; set; }
    public string? RequestBody { get; set; }
    public int StatusCode { get; set; }
    public string? ResponseBody { get; set; }
    public string? RemoteIp { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ============================================================
// Automatic Pending Approvals Digest -- one schedule PER Approval Type (CR might run every
// 2 days, Sales Discount every working day at 9am, each independently), distinct from the
// existing ad-hoc "send to one person" screen which stays manual/unscheduled. No row for a
// type means "never configured" (equivalent to disabled), not "run continuously."
// ============================================================
public sealed class DigestSchedule
{
    public int Id { get; set; }
    public int ApprovalTypeId { get; set; }
    public bool Enabled { get; set; }
    // "EveryNDays" (paired with IntervalDays) or "Weekdays" (Mon-Fri only, IntervalDays
    // ignored) -- two named patterns rather than a full day-of-week/cron picker, matching
    // the two shapes actually asked for ("every 2 days", "every working day").
    public string RecurrenceType { get; set; } = "EveryNDays";
    public int IntervalDays { get; set; } = 1;
    // Time of day in the SERVER's local wall-clock time (what an admin means by "9am") --
    // NextRunAtUtc is what the scheduler actually reads; this is just the anchor used to
    // recompute it whenever the schedule changes or after each run.
    public TimeSpan StartTime { get; set; } = new(9, 0, 0);
    public int? MailTemplateId { get; set; }
    public DateTime? NextRunAtUtc { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedByUserName { get; set; }
}

// One row per digest send -- scheduled or a manual "Run Now" -- so "how many emails went
// out, and to whom" is answerable without cross-referencing PPF Monitor (which is
// per-Request and doesn't apply here; a digest isn't tied to any single request).
public sealed class DigestRun
{
    public long Id { get; set; }
    public int ApprovalTypeId { get; set; }
    public string ApprovalTypeName { get; set; } = "";
    public DateTime RunAtUtc { get; set; }
    public string TriggeredBy { get; set; } = "Scheduled"; // "Scheduled" or "Manual"
    public string? TriggeredByUserName { get; set; }
    public int EligibleUserCount { get; set; }
    public int RecipientCount { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
}

// Per-recipient detail for one DigestRun, including the exact rendered subject/body sent --
// "what did this person actually receive" is answerable by reading the row, not by
// re-rendering the template against current (possibly since-changed) data.
public sealed class DigestRunRecipient
{
    public long Id { get; set; }
    public long DigestRunId { get; set; }
    public int UserId { get; set; }
    public string UserName { get; set; } = "";
    public string? Email { get; set; }
    public int PendingCount { get; set; }
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public string Status { get; set; } = ""; // Sent, Failed
    public string? ErrorMessage { get; set; }
}

public sealed class RoutingLogEntry
{
    public long Id { get; set; }
    public string? RequestNumber { get; set; }
    public int ApprovalTypeId { get; set; }
    public string OutcomeCode { get; set; } = "";
    public bool Success { get; set; }
    public string? MatchedRuleName { get; set; }
    public string? Detail { get; set; }
    public string? RoutingContextJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

// Admin-editable SMTP configuration -- a single row (Id=1), so "Email Configuration"
// changes take effect immediately without a redeploy, unlike the original Approval
// engine's appsettings-only EmailOptions this was based on.
public sealed class EmailSettings
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseTls { get; set; } = true;
    public string From { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedByUserName { get; set; }
}

// ============================================================
// PPF (unchanged in shape -- already fully generic)
// ============================================================
public sealed class MailTemplate
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Subject { get; set; } = "";
    public string BodyHtml { get; set; } = "";
    public bool IsTableTemplate { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class PostProcessingRule
{
    public int Id { get; set; }
    public int ApprovalTypeId { get; set; }
    public string EventCode { get; set; } = "";
    public string ActionType { get; set; } = "";
    public string? Target { get; set; }
    public string? ActionConfigJson { get; set; }
    public int SequenceNo { get; set; }
    public bool Active { get; set; }
}

public sealed class PostProcessingExecution
{
    public long Id { get; set; }
    public int PostProcessingRuleId { get; set; }
    public long RequestId { get; set; }
    public int AttemptNo { get; set; }
    public string ActionType { get; set; } = "Email";
    public string? Target { get; set; }
    public string Status { get; set; } = "Pending";
    public string? ErrorMessage { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
