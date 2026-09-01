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
    public DateTime CreatedAt { get; set; }
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
