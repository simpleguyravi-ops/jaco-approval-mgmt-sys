using System.Security.Claims;
using System.Text.Json;
using JACO.Unified.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Infrastructure;

public sealed record SubmittedField(string FieldKey, string Label, string DataType, string? Value, bool IsSensitive);
public sealed record TimelineDecision(string ActorName, string ActionCode, string? Comments, DateTime AtUtc);
public sealed record TimelineLevel(int LevelNo, string Mode, IReadOnlyList<string> ApproverNames, IReadOnlyList<TimelineDecision> Decisions, string LevelStatus);

// The unified engine: what ApprovalService (decisions/routing/PPF) and
// ChangeRequestController (create/edit/submit) used to split across two apps and two
// database rows, now operating on ONE Request row for its entire lifecycle. No snapshot,
// no resync -- editing IS updating the same row Submit will route again.
public sealed class RequestService(UnifiedDbContext db, RoutingService routing, PpfExecutor ppf, PortalApiClient portalApi, TimelineService timeline)
{
    public const string AdminOverrideMarker = "[Admin override";

    // ---------- Field metadata / values ----------

    public static string? ExtractField(string? dataJson, string fieldKey)
    {
        if (string.IsNullOrWhiteSpace(dataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            if (doc.RootElement.TryGetProperty(fieldKey, out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        }
        catch { /* malformed/legacy DataJson yields no value */ }
        return null;
    }

    // Fields offered on the Create/Edit form -- IsVisible=true, Active=true, ordered.
    public async Task<List<WorkflowField>> GetFormFieldsAsync(int approvalTypeId) =>
        await db.WorkflowFields
            .Where(f => f.Active && f.IsVisible && (f.ApprovalTypeId == approvalTypeId || f.ApprovalTypeId == null))
            .OrderBy(f => f.DisplayOrder)
            .ToListAsync();

    // Every VISIBLE field configured for this type, with its submitted value -- what the
    // read-only Details panel shows. Matches GetFormFieldsAsync's Active/IsVisible filter so
    // unchecking "Visible on form" hides a field everywhere, not just the Create/Edit inputs.
    // Sensitive fields are filtered by the caller (creator vs everyone else), not here.
    public async Task<List<SubmittedField>> GetSubmittedFieldsAsync(Request request)
    {
        var fields = await db.WorkflowFields
            .Where(f => f.Active && f.IsVisible && (f.ApprovalTypeId == request.ApprovalTypeId || f.ApprovalTypeId == null))
            .OrderBy(f => f.DisplayOrder)
            .ToListAsync();

        return fields.Select(f => new SubmittedField(f.FieldKey, f.FieldLabel, f.DataType, ExtractField(request.DataJson, f.FieldKey), f.IsSensitive)).ToList();
    }

    public async Task<List<PicklistValue>> GetPicklistAsync(string lookupType) =>
        await db.PicklistValues.Where(p => p.LookupType == lookupType && p.Active).OrderBy(p => p.SortOrder).ThenBy(p => p.DisplayText).ToListAsync();

    // ---------- Identity ----------

    public async Task<AppUser?> ResolveCurrentUserAsync(ClaimsPrincipal user)
    {
        var userName = user.Identity?.Name;
        if (string.IsNullOrWhiteSpace(userName)) return null;

        var existing = await db.AppUsers.SingleOrDefaultAsync(u => u.UserName == userName);
        if (existing is not null) return existing;

        // First-touch auto-provisioning, same as the original Approval engine -- a user
        // who is authenticated via the shared SSO cookie but has never triggered a sync
        // (Rule Builder/Digest screen) yet still gets a usable local row.
        var displayName = user.FindFirst("DisplayName")?.Value ?? userName;
        var department = user.FindFirst("Department")?.Value;
        var created = new AppUser { UserName = userName, DisplayName = displayName, Department = department, IsActive = true };
        db.AppUsers.Add(created);
        await db.SaveChangesAsync();
        return created;
    }

    public async Task SyncUsersFromPortalAsync(string appCode = "UNIFIED")
    {
        var roster = await portalApi.GetUsersWithAccessAsync(appCode);
        if (roster.Count == 0) return;

        foreach (var entry in roster)
        {
            var existing = await db.AppUsers.SingleOrDefaultAsync(u => u.UserName == entry.UserName);
            if (existing is null)
            {
                db.AppUsers.Add(new AppUser { UserName = entry.UserName, DisplayName = entry.DisplayName, Email = entry.Email, Department = entry.Department, IsActive = true });
            }
            else
            {
                existing.DisplayName = entry.DisplayName;
                existing.Email = entry.Email;
                existing.Department = entry.Department;
                existing.IsActive = true;
            }
        }
        await db.SaveChangesAsync();
    }

    // ---------- Access ----------

    public async Task<bool> IsParticipantAsync(long requestId, int userId) =>
        await db.WorkflowParticipants.AnyAsync(x => x.RequestId == requestId && x.UserId == userId);

    public async Task<bool> IsEligibleApproverAsync(long requestId, int userId)
    {
        var request = await db.Requests.FindAsync(requestId);
        if (request?.CurrentLevelNo is null || request.WorkflowVersionId is null || request.RoutingRuleId is null) return false;
        var step = await db.WorkflowSteps.SingleOrDefaultAsync(x => x.WorkflowVersionId == request.WorkflowVersionId && x.RoutingRuleId == request.RoutingRuleId && x.LevelNo == request.CurrentLevelNo);
        if (step is null) return false;
        return await db.WorkflowStepApprovers.AnyAsync(a => a.WorkflowStepId == step.Id && a.UserId == userId);
    }

    // "Display All" -- an explicit oversight grant for a whole Approval Type, distinct
    // from IsParticipantAsync (which is automatic for anyone who created/was ever eligible
    // on THIS specific request and needs no admin grant at all).
    public async Task<bool> HasViewPermissionAsync(int userId, int approvalTypeId) =>
        await db.UserWorkflowPermissions.AnyAsync(p => p.UserId == userId && p.ApprovalTypeId == approvalTypeId && p.CanView);

    public async Task<bool> CanCreateAsync(int userId, int approvalTypeId) =>
        await db.UserWorkflowPermissions.AnyAsync(p => p.UserId == userId && p.ApprovalTypeId == approvalTypeId && p.CanCreate);

    public async Task<List<int>> GetEligibleApproverIdsAsync(long requestId)
    {
        var request = await db.Requests.FindAsync(requestId);
        if (request?.CurrentLevelNo is null || request.WorkflowVersionId is null || request.RoutingRuleId is null) return [];
        var step = await db.WorkflowSteps.SingleOrDefaultAsync(x => x.WorkflowVersionId == request.WorkflowVersionId && x.RoutingRuleId == request.RoutingRuleId && x.LevelNo == request.CurrentLevelNo);
        if (step is null) return [];
        return await db.WorkflowStepApprovers.Where(a => a.WorkflowStepId == step.Id).Select(a => a.UserId).ToListAsync();
    }

    async Task AddParticipantIfMissingAsync(long requestId, int userId, string participantType)
    {
        var exists = await db.WorkflowParticipants.AnyAsync(x => x.RequestId == requestId && x.UserId == userId);
        if (!exists)
        {
            var now = DateTime.UtcNow;
            db.WorkflowParticipants.Add(new WorkflowParticipant { RequestId = requestId, UserId = userId, ParticipantType = participantType, FirstSeenAt = now, LastSeenAt = now });
        }
    }

    // ---------- Create / Edit / Submit ----------

    public async Task<Request> CreateDraftAsync(int approvalTypeId, int creatorUserId, string creatorUserName)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        long seq;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT NEXT VALUE FOR dbo.RequestIdSequence;";
            seq = Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        finally { await connection.CloseAsync(); }

        var type = await db.ApprovalTypes.FindAsync(approvalTypeId);
        var now = DateTime.UtcNow;
        var request = new Request
        {
            RequestNumber = $"{type?.Code ?? "REQ"}-{DateTime.UtcNow:yyyy}-{seq:D5}",
            ApprovalTypeId = approvalTypeId,
            CreatorUserId = creatorUserId,
            CreatorUserName = creatorUserName,
            Status = "Draft",
            DataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Requests.Add(request);
        await AddParticipantIfMissingAsync(request.Id, creatorUserId, "Creator");
        await db.SaveChangesAsync();
        return request;
    }

    public static bool IsEditable(string status) => status is "Draft" or "Sent Back";

    // Saves field values without changing workflow state -- used both for a plain Draft
    // save and for editing after Send Back (Submit is a separate, explicit step).
    public async Task<(bool ok, string message)> SaveFieldsAsync(long requestId, int userId, string? subject, Dictionary<string, JsonElement> fieldValues)
    {
        var request = await db.Requests.FindAsync(requestId);
        if (request is null) return (false, "Request not found.");
        if (request.CreatorUserId != userId) return (false, "Only the creator can edit this request.");
        if (!IsEditable(request.Status)) return (false, "Only Draft or Sent Back requests can be edited.");

        var fields = await GetFormFieldsAsync(request.ApprovalTypeId);
        foreach (var f in fields.Where(f => f.IsRequired && !f.IsReadOnly))
        {
            if (!fieldValues.TryGetValue(f.FieldKey, out var v) || v.ValueKind == JsonValueKind.Null ||
                (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())))
                return (false, $"{f.FieldLabel} is required.");
        }
        foreach (var f in fields.Where(f => f.DataType == FieldDataType.Dropdown && !string.IsNullOrEmpty(f.LookupType)))
        {
            if (fieldValues.TryGetValue(f.FieldKey, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var val = v.GetString();
                if (!string.IsNullOrEmpty(val))
                {
                    var allowed = await db.PicklistValues.AnyAsync(p => p.LookupType == f.LookupType && p.Value == val && p.Active);
                    if (!allowed) return (false, $"'{val}' is not a valid {f.FieldLabel}.");
                }
            }
        }

        request.Subject = subject;
        request.DataJson = JsonSerializer.Serialize(fieldValues);
        request.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return (true, "Saved.");
    }

    public async Task<(bool ok, string message)> SubmitAsync(long requestId, int userId)
    {
        var request = await db.Requests.FindAsync(requestId);
        if (request is null) return (false, "Request not found.");
        if (request.CreatorUserId != userId) return (false, "Only the creator can submit this request.");
        if (!IsEditable(request.Status)) return (false, "Only Draft or Sent Back requests can be submitted.");

        var isResubmit = request.Status == "Sent Back";
        var fieldValues = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request.DataJson) ?? new();

        if (!isResubmit)
        {
            var route = await routing.ResolveAsync(request.ApprovalTypeId, fieldValues);

            db.RoutingLog.Add(new RoutingLogEntry
            {
                RequestNumber = request.RequestNumber,
                ApprovalTypeId = request.ApprovalTypeId,
                OutcomeCode = route.OutcomeCode,
                Success = route.Ok,
                MatchedRuleName = route.MatchedRuleName,
                Detail = route.Detail,
                RoutingContextJson = request.DataJson,
                CreatedAt = DateTime.UtcNow
            });

            if (!route.Ok || route.RoutingRuleId is null)
            {
                await db.SaveChangesAsync();
                return (false, route.Detail ?? "No matching routing rule was found for the submitted data.");
            }

            request.RoutingRuleId = route.RoutingRuleId;
            request.WorkflowVersionId = route.WorkflowVersionId;
            request.CurrentLevelNo = route.ApproverIds.Keys.Min();
            foreach (var userId2 in route.ApproverIds[request.CurrentLevelNo.Value])
                await AddParticipantIfMissingAsync(request.Id, userId2, "Approver");
        }

        request.Status = "Pending";
        request.UpdatedAt = DateTime.UtcNow;

        db.AuditLogs.Add(new AuditLog { RequestId = request.Id, UserId = userId, ActionCode = isResubmit ? "Resubmit" : "Submit", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        if (!isResubmit) await ppf.RaiseEventAsync(request.Id, "Created");
        else await ppf.RaiseEventAsync(request.Id, "Resubmit");
        // Separate from Created/Resubmit (which are about the request as a whole) -- this is
        // specifically "it's now your turn to decide," aimed at whoever the CURRENT level's
        // approver(s) are, whether that's level 1 on a fresh submit or wherever a resubmit
        // lands back on.
        await ppf.RaiseEventAsync(request.Id, "LevelPending");

        return (true, request.Status);
    }

    // ---------- Decisions ----------

    static readonly string[] DecisionModes = ["ANY_ONE", "ALL", "MAJORITY", "MINIMUM_COUNT"];

    public async Task<(bool ok, string message)> DecideAsync(long requestId, int userId, string decision, string? comments, bool adminOverride = false)
    {
        decision = decision.Trim();
        if (decision is not ("Approve" or "Reject" or "SendBack")) return (false, "Invalid decision.");

        var request = await db.Requests.FindAsync(requestId);
        if (request is null) return (false, "Request not found.");
        if (request.Status != "Pending") return (false, "Request is not currently awaiting a decision.");
        if (request.CurrentLevelNo is null || request.WorkflowVersionId is null || request.RoutingRuleId is null) return (false, "Route is incomplete.");

        var step = await db.WorkflowSteps.SingleOrDefaultAsync(x => x.WorkflowVersionId == request.WorkflowVersionId && x.RoutingRuleId == request.RoutingRuleId && x.LevelNo == request.CurrentLevelNo);
        if (step is null) return (false, "Configured workflow level was not found.");

        var eligibleIds = await db.WorkflowStepApprovers.Where(x => x.WorkflowStepId == step.Id).Select(x => x.UserId).ToListAsync();
        if (!adminOverride && !eligibleIds.Contains(userId)) return (false, "You are not an eligible approver for this level.");
        if (decision is "Reject" or "SendBack" && string.IsNullOrWhiteSpace(comments)) return (false, $"Comments are required when {decision.ToLowerInvariant()}ing.");

        var now = DateTime.UtcNow;
        var effectiveComments = adminOverride ? $"{AdminOverrideMarker} -- decided on behalf of the assigned approver(s)] {comments}".TrimEnd() : comments;

        db.RequestActions.Add(new RequestAction { RequestId = requestId, LevelNo = step.LevelNo, UserId = userId, ActionCode = decision, Comments = effectiveComments, CreatedAt = now });
        db.AuditLogs.Add(new AuditLog { RequestId = requestId, UserId = userId, ActionCode = decision, DetailsJson = effectiveComments, CreatedAt = now });
        await AddParticipantIfMissingAsync(requestId, userId, "Approver");

        string? eventCode = null;
        if (decision == "Reject")
        {
            request.Status = "Rejected";
            request.CurrentLevelNo = null;
            eventCode = "Rejected";
        }
        else if (decision == "SendBack")
        {
            request.Status = "Sent Back";
            eventCode = "SentBack";
        }
        else // Approve
        {
            var decidedCount = await db.RequestActions.CountAsync(a => a.RequestId == requestId && a.LevelNo == step.LevelNo && a.ActionCode == "Approve") + 1;
            var required = step.Mode switch
            {
                "ANY_ONE" => 1,
                "ALL" => eligibleIds.Count,
                "MINIMUM_COUNT" => step.RequiredCount ?? eligibleIds.Count,
                _ => eligibleIds.Count / 2 + 1 // MAJORITY
            };
            if (decidedCount < required)
            {
                // Level not complete yet -- stays Pending at the same level for the
                // remaining approvers.
                await db.SaveChangesAsync();
                return (true, request.Status);
            }

            // ANY_ONE: mark every OTHER eligible approver's copy of this level as no longer
            // needing their decision, so the audit trail explains why they never acted.
            if (step.Mode == "ANY_ONE")
            {
                foreach (var otherUserId in eligibleIds.Where(x => x != userId))
                    db.RequestActions.Add(new RequestAction { RequestId = requestId, LevelNo = step.LevelNo, UserId = otherUserId, ActionCode = "NoLongerRequired", Comments = "Level completed by another eligible approver.", CreatedAt = now });
            }

            var nextLevel = await db.WorkflowSteps
                .Where(s => s.WorkflowVersionId == request.WorkflowVersionId && s.RoutingRuleId == request.RoutingRuleId && s.LevelNo > step.LevelNo)
                .OrderBy(s => s.LevelNo)
                .FirstOrDefaultAsync();

            if (nextLevel is null)
            {
                request.Status = "Approved";
                request.CurrentLevelNo = null;
                eventCode = "Approved";
            }
            else
            {
                request.CurrentLevelNo = nextLevel.LevelNo;
                var nextApprovers = await db.WorkflowStepApprovers.Where(a => a.WorkflowStepId == nextLevel.Id).Select(a => a.UserId).ToListAsync();
                foreach (var nextUserId in nextApprovers)
                    await AddParticipantIfMissingAsync(requestId, nextUserId, "Approver");
                eventCode = "LevelPending";
            }
        }

        request.UpdatedAt = now;
        await db.SaveChangesAsync();

        if (eventCode is not null) await ppf.RaiseEventAsync(requestId, eventCode);
        if (eventCode == "Approved") await ppf.RaiseEventAsync(requestId, "Completed");

        return (true, request.Status);
    }

    public async Task<(bool ok, string message)> WithdrawAsync(long requestId, int creatorUserId, string? reason)
    {
        var request = await db.Requests.FindAsync(requestId);
        if (request is null) return (false, "Request not found.");
        if (request.CreatorUserId != creatorUserId) return (false, "Only the creator can withdraw this request.");
        if (request.Status is not ("Pending" or "Sent Back")) return (false, "Only a Pending or Sent Back request can be withdrawn.");

        request.Status = "Withdrawn";
        request.CurrentLevelNo = null;
        request.UpdatedAt = DateTime.UtcNow;
        db.AuditLogs.Add(new AuditLog { RequestId = requestId, UserId = creatorUserId, ActionCode = "Withdraw", DetailsJson = reason, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return (true, request.Status);
    }

    static readonly TimeSpan NudgeCooldown = TimeSpan.FromMinutes(15);

    public async Task<(bool ok, string message)> NudgeAsync(long requestId, int requesterUserId, bool isAdminOverride = false)
    {
        var request = await db.Requests.FindAsync(requestId);
        if (request is null) return (false, "Request not found.");
        if (!isAdminOverride && request.CreatorUserId != requesterUserId) return (false, "Only the creator can send a reminder.");
        if (request.Status != "Pending") return (false, "This request is not currently awaiting a decision.");

        var last = await db.AuditLogs.Where(a => a.RequestId == requestId && a.ActionCode == "Nudge").OrderByDescending(a => a.CreatedAt).FirstOrDefaultAsync();
        if (last is not null && DateTime.UtcNow - last.CreatedAt < NudgeCooldown)
        {
            var wait = NudgeCooldown - (DateTime.UtcNow - last.CreatedAt);
            return (false, $"A reminder was already sent recently. Try again in about {Math.Ceiling(wait.TotalMinutes)} minute(s).");
        }

        db.AuditLogs.Add(new AuditLog { RequestId = requestId, UserId = requesterUserId, ActionCode = "Nudge", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await ppf.RaiseEventAsync(requestId, "Nudged");
        return (true, "Reminder sent.");
    }

    public async Task<(bool ok, string message)> ReassignApproverAsync(long requestId, int? oldUserId, int newUserId, string reason, int changedByUserId)
    {
        var request = await db.Requests.FindAsync(requestId);
        if (request is null) return (false, "Request not found.");
        if (request.CurrentLevelNo is null || request.WorkflowVersionId is null || request.RoutingRuleId is null) return (false, "Route is incomplete.");

        var step = await db.WorkflowSteps.SingleOrDefaultAsync(x => x.WorkflowVersionId == request.WorkflowVersionId && x.RoutingRuleId == request.RoutingRuleId && x.LevelNo == request.CurrentLevelNo);
        if (step is null) return (false, "Configured workflow level was not found.");

        if (oldUserId is not null)
        {
            var old = await db.WorkflowStepApprovers.SingleOrDefaultAsync(a => a.WorkflowStepId == step.Id && a.UserId == oldUserId);
            if (old is not null) db.WorkflowStepApprovers.Remove(old);
        }
        if (!await db.WorkflowStepApprovers.AnyAsync(a => a.WorkflowStepId == step.Id && a.UserId == newUserId))
            db.WorkflowStepApprovers.Add(new WorkflowStepApprover { WorkflowStepId = step.Id, UserId = newUserId });

        db.ApproverReassignments.Add(new ApproverReassignment { RequestId = requestId, LevelNo = step.LevelNo, OldUserId = oldUserId, NewUserId = newUserId, Reason = reason, ChangedByUserId = changedByUserId, CreatedAt = DateTime.UtcNow });
        db.AuditLogs.Add(new AuditLog { RequestId = requestId, UserId = changedByUserId, ActionCode = "Reassign", DetailsJson = JsonSerializer.Serialize(new { oldUserId, newUserId, reason }), CreatedAt = DateTime.UtcNow });

        await AddParticipantIfMissingAsync(requestId, newUserId, "Approver");
        await db.SaveChangesAsync();
        return (true, "Reassigned.");
    }

    // ---------- Lists ----------

    public async Task<List<Request>> GetMyWorkAsync(int userId)
    {
        var ids = await db.WorkflowParticipants.Where(x => x.UserId == userId).Select(x => x.RequestId).ToListAsync();
        return await db.Requests.Where(x => x.CreatorUserId == userId || ids.Contains(x.Id)).OrderByDescending(x => x.CreatedAt).ToListAsync();
    }

    public async Task<List<long>> GetPendingForUserAsync(IEnumerable<Request> requests, int userId)
    {
        var pendingIds = requests.Where(r => r.Status == "Pending").Select(r => r.Id).ToList();
        if (pendingIds.Count == 0) return [];
        var steps = await db.WorkflowSteps.Where(s => db.Requests.Any(r => pendingIds.Contains(r.Id) && r.WorkflowVersionId == s.WorkflowVersionId && r.RoutingRuleId == s.RoutingRuleId && r.CurrentLevelNo == s.LevelNo)).ToListAsync();
        var result = new List<long>();
        foreach (var r in requests.Where(r => pendingIds.Contains(r.Id)))
        {
            var step = steps.SingleOrDefault(s => s.WorkflowVersionId == r.WorkflowVersionId && s.RoutingRuleId == r.RoutingRuleId && s.LevelNo == r.CurrentLevelNo);
            if (step is null) continue;
            if (await db.WorkflowStepApprovers.AnyAsync(a => a.WorkflowStepId == step.Id && a.UserId == userId))
                result.Add(r.Id);
        }
        return result;
    }

    public async Task<Request?> GetByRequestNumberAsync(string requestNumber) =>
        await db.Requests.FirstOrDefaultAsync(x => x.RequestNumber == requestNumber);

    public Task<List<TimelineLevel>?> GetTimelineAsync(long requestId) => timeline.GetTimelineAsync(requestId);
}
