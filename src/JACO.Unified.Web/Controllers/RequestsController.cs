using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using JACO.Unified.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// The one screen every Approval Type shares: create/edit/submit/decide/withdraw, all
// rendered from WorkflowField metadata instead of a hand-written view per type. "My Work"
// here is created-by-me + everywhere I'm a participant (automatic, no grant needed); the
// separate "All Requests" oversight view is gated by UserWorkflowPermission.CanView.
[EnableRateLimiting("sensitive")]
public sealed class RequestsController(RequestService requests, UnifiedDbContext db, RequestAttachmentStorage attachments) : UnifiedControllerBase(requests)
{
    // Extensions that could execute if ever served/opened directly, rather than
    // downloaded -- a denylist (not allowlist) since this is a general-purpose business
    // app that legitimately needs to accept most document/image/archive types.
    static readonly string[] BlockedExtensions =
        [".exe", ".dll", ".msi", ".bat", ".cmd", ".com", ".scr", ".ps1", ".psm1", ".vbs", ".vbe",
         ".js", ".jse", ".wsf", ".wsh", ".jar", ".sh", ".app", ".hta", ".cpl", ".reg"];


    [HttpGet]
    public async Task<IActionResult> Index(string? focus, string? search, string? status, int? approvalTypeId, DateTime? dateFrom, DateTime? dateTo, string? sort, string dir = "desc")
    {
        var user = await CurrentUserAsync();
        var mine = await requests.GetMyWorkAsync(user.Id);
        var pendingMineIds = await requests.GetPendingForUserAsync(mine, user.Id);
        var effectiveFocus = focus == "all" ? "all" : "action";
        var source = effectiveFocus == "action" ? mine.Where(x => pendingMineIds.Contains(x.Id)).ToList() : mine;
        var model = await BuildListAsync(source, search, effectiveFocus == "action" ? null : status, approvalTypeId, sort, dir, isAllView: false, user, dateFrom: dateFrom, dateTo: dateTo);
        model.Focus = effectiveFocus;
        model.PendingMineCount = pendingMineIds.Count;
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Export(string? focus, string? search, string? status, int? approvalTypeId, DateTime? dateFrom, DateTime? dateTo, string? sort, string dir = "desc")
    {
        var user = await CurrentUserAsync();
        var mine = await requests.GetMyWorkAsync(user.Id);
        var effectiveFocus = focus == "all" ? "all" : "action";
        var source = mine;
        if (effectiveFocus == "action")
        {
            var pendingMineIds = await requests.GetPendingForUserAsync(mine, user.Id);
            source = mine.Where(x => pendingMineIds.Contains(x.Id)).ToList();
        }
        var model = await BuildListAsync(source, search, effectiveFocus == "action" ? null : status, approvalTypeId, sort, dir, isAllView: false, user, dateFrom: dateFrom, dateTo: dateTo);
        return ExportRows(model.Rows, "my-work");
    }

    [HttpGet]
    public async Task<IActionResult> ExportAll(string? search, string? status, int? approvalTypeId, int? pendingWithUserId, List<string>? columns, DateTime? dateFrom, DateTime? dateTo, string? sort, string dir = "desc")
    {
        var user = await CurrentUserAsync();
        List<int> viewableTypeIds = IsAdmin
            ? await db.ApprovalTypes.Select(t => t.Id).ToListAsync()
            : await db.UserWorkflowPermissions.Where(p => p.UserId == user.Id && p.CanView).Select(p => p.ApprovalTypeId).ToListAsync();
        var all = viewableTypeIds.Count == 0 ? new List<Request>() : await db.Requests.Where(r => viewableTypeIds.Contains(r.ApprovalTypeId)).ToListAsync();
        var model = await BuildListAsync(all, search, status, approvalTypeId, sort, dir, isAllView: true, user, pendingWithUserId, columns ?? [], dateFrom, dateTo);

        var header = new[] { "Request No.", "Type", "Subject", "Status", "Pending With", "Created By", "Created On" }
            .Concat(model.SelectedColumns.Select(k => model.AvailableColumns.First(c => c.FieldKey == k).FieldLabel)).ToArray();
        var bytes = CsvHelper.ToCsvBytes(model.Rows, header, r => new[]
        {
            r.Request.RequestNumber, r.ApprovalTypeName, r.Request.Subject ?? "", r.Request.Status,
            model.PendingWithNames.GetValueOrDefault(r.Request.Id, ""), r.Request.CreatorUserName, r.Request.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
        }.Concat(model.SelectedColumns.Select(k => model.ExtraColumns.GetValueOrDefault(r.Request.Id)?.GetValueOrDefault(k, "") ?? "")).ToArray());
        return new FileContentResult(bytes, "text/csv") { FileDownloadName = $"all-requests-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv" };
    }

    static FileContentResult ExportRows(List<RequestListRow> rows, string fileNamePrefix)
    {
        var bytes = CsvHelper.ToCsvBytes(rows,
            ["Request No.", "Type", "Subject", "Status", "Created By", "Created On"],
            r => [r.Request.RequestNumber, r.ApprovalTypeName, r.Request.Subject ?? "", r.Request.Status, r.Request.CreatorUserName, r.Request.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")]);
        return new FileContentResult(bytes, "text/csv") { FileDownloadName = $"{fileNamePrefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv" };
    }

    async Task<List<ApprovalType>> GetCreatableTypesAsync(AppUser user)
    {
        var active = await db.ApprovalTypes.Where(t => t.Active).OrderBy(t => t.Name).ToListAsync();
        if (IsAdmin) return active;
        var allowedIds = await db.UserWorkflowPermissions.Where(p => p.UserId == user.Id && p.CanCreate).Select(p => p.ApprovalTypeId).ToListAsync();
        return active.Where(t => allowedIds.Contains(t.Id)).ToList();
    }

    // "Display All" -- every request of any type the current user has CanView on, plus
    // everything if they're a platform Administrator.
    [HttpGet]
    public async Task<IActionResult> All(string? search, string? status, int? approvalTypeId, int? pendingWithUserId, List<string>? columns, DateTime? dateFrom, DateTime? dateTo, string? sort, string dir = "desc")
    {
        var user = await CurrentUserAsync();
        List<int> viewableTypeIds;
        if (IsAdmin || IsAuditor)
        {
            viewableTypeIds = await db.ApprovalTypes.Select(t => t.Id).ToListAsync();
        }
        else
        {
            viewableTypeIds = await db.UserWorkflowPermissions.Where(p => p.UserId == user.Id && p.CanView).Select(p => p.ApprovalTypeId).ToListAsync();
        }

        var all = viewableTypeIds.Count == 0
            ? new List<Request>()
            : await db.Requests.Where(r => viewableTypeIds.Contains(r.ApprovalTypeId)).OrderByDescending(r => r.CreatedAt).ToListAsync();

        var model = await BuildListAsync(all, search, status, approvalTypeId, sort, dir, isAllView: true, user, pendingWithUserId, columns ?? [], dateFrom, dateTo);
        return View("Index", model);
    }

    async Task<RequestListViewModel> BuildListAsync(List<Request> source, string? search, string? status, int? approvalTypeId, string? sort, string dir, bool isAllView, AppUser user, int? pendingWithUserId = null, List<string>? columns = null, DateTime? dateFrom = null, DateTime? dateTo = null)
    {
        var types = await db.ApprovalTypes.Where(t => t.Active).OrderBy(t => t.Name).ToListAsync();
        var typeNames = types.ToDictionary(t => t.Id, t => t.Name);
        columns ??= [];

        var availableColumns = approvalTypeId is null
            ? []
            : await db.WorkflowFields.Where(f => f.ApprovalTypeId == approvalTypeId || f.ApprovalTypeId == null).OrderBy(f => f.DisplayOrder)
                .Select(f => new { f.FieldKey, f.FieldLabel }).ToListAsync();
        var validColumnKeys = availableColumns.Select(c => c.FieldKey).ToHashSet();
        columns = columns.Where(validColumnKeys.Contains).ToList();

        var query = source.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status);
        if (approvalTypeId is not null) query = query.Where(x => x.ApprovalTypeId == approvalTypeId);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(x => x.RequestNumber.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                      (x.Subject ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
        if (dateFrom is not null) query = query.Where(x => x.CreatedAt >= dateFrom.Value);
        if (dateTo is not null) query = query.Where(x => x.CreatedAt < dateTo.Value.AddDays(1));

        var afterStatusAndType = query.ToList();
        var pendingWithNames = isAllView ? await GetPendingWithNamesAsync(afterStatusAndType) : [];

        if (pendingWithUserId is not null)
        {
            var pendingWithUserName = (await db.AppUsers.FindAsync(pendingWithUserId.Value))?.DisplayName;
            afterStatusAndType = afterStatusAndType.Where(x => x.Status == "Pending" && pendingWithNames.ContainsKey(x.Id) &&
                pendingWithNames[x.Id].Split(", ").Contains(pendingWithUserName)).ToList();
        }

        var desc = dir == "desc";
        IEnumerable<Request> sorted = sort switch
        {
            "RequestNumber" => desc ? afterStatusAndType.OrderByDescending(x => x.RequestNumber) : afterStatusAndType.OrderBy(x => x.RequestNumber),
            "ApprovalType" => desc ? afterStatusAndType.OrderByDescending(x => typeNames.GetValueOrDefault(x.ApprovalTypeId)) : afterStatusAndType.OrderBy(x => typeNames.GetValueOrDefault(x.ApprovalTypeId)),
            "Subject" => desc ? afterStatusAndType.OrderByDescending(x => x.Subject) : afterStatusAndType.OrderBy(x => x.Subject),
            "Status" => desc ? afterStatusAndType.OrderByDescending(x => x.Status) : afterStatusAndType.OrderBy(x => x.Status),
            "CreatedAt" => desc ? afterStatusAndType.OrderByDescending(x => x.CreatedAt) : afterStatusAndType.OrderBy(x => x.CreatedAt),
            _ => afterStatusAndType.OrderByDescending(x => x.CreatedAt)
        };

        var rows = sorted.Select(r => new RequestListRow { Request = r, ApprovalTypeName = typeNames.GetValueOrDefault(r.ApprovalTypeId, "?") }).ToList();
        var extraColumns = columns.Count == 0 ? [] : rows.ToDictionary(r => r.Request.Id, r => columns.ToDictionary(k => k, k => RequestService.ExtractField(r.Request.DataJson, k) ?? ""));

        return new RequestListViewModel
        {
            Rows = rows,
            Types = types,
            CreatableTypes = await GetCreatableTypesAsync(user),
            DraftCount = source.Count(x => x.Status == "Draft"),
            PendingCount = source.Count(x => x.Status is "Pending" or "Sent Back"),
            ApprovedCount = source.Count(x => x.Status == "Approved"),
            RejectedCount = source.Count(x => x.Status == "Rejected"),
            Search = search,
            Status = status,
            ApprovalTypeId = approvalTypeId,
            Sort = sort,
            Dir = dir,
            IsAllView = isAllView,
            AvailableColumns = availableColumns.Select(c => (c.FieldKey, c.FieldLabel)).ToList(),
            SelectedColumns = columns,
            Users = isAllView ? (await db.AppUsers.Where(u => u.IsActive).OrderBy(u => u.DisplayName).ToListAsync()).Select(u => (u.Id, u.DisplayName)).ToList() : [],
            PendingWithUserId = pendingWithUserId,
            PendingWithNames = pendingWithNames,
            ExtraColumns = extraColumns,
            DateFrom = dateFrom,
            DateTo = dateTo
        };
    }

    // Who each Pending row's current level is actually waiting on -- batched rather than
    // one query per row.
    async Task<Dictionary<long, string>> GetPendingWithNamesAsync(List<Request> source)
    {
        var pending = source.Where(r => r.Status == "Pending" && r.CurrentLevelNo != null && r.WorkflowVersionId != null && r.RoutingRuleId != null).ToList();
        if (pending.Count == 0) return [];

        var versionIds = pending.Select(r => r.WorkflowVersionId!.Value).Distinct().ToList();
        var ruleIds = pending.Select(r => r.RoutingRuleId!.Value).Distinct().ToList();
        var steps = await db.WorkflowSteps.Where(s => versionIds.Contains(s.WorkflowVersionId) && ruleIds.Contains(s.RoutingRuleId)).ToListAsync();
        var stepIds = steps.Select(s => s.Id).ToList();
        var approvers = await db.WorkflowStepApprovers.Where(a => stepIds.Contains(a.WorkflowStepId)).ToListAsync();
        var userIds = approvers.Select(a => a.UserId).Distinct().ToList();
        var userNames = await db.AppUsers.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName);

        var result = new Dictionary<long, string>();
        foreach (var r in pending)
        {
            var step = steps.FirstOrDefault(s => s.WorkflowVersionId == r.WorkflowVersionId && s.RoutingRuleId == r.RoutingRuleId && s.LevelNo == r.CurrentLevelNo);
            if (step is null) continue;
            var names = approvers.Where(a => a.WorkflowStepId == step.Id).Select(a => userNames.GetValueOrDefault(a.UserId, $"User #{a.UserId}"));
            result[r.Id] = string.Join(", ", names);
        }
        return result;
    }

    [HttpGet]
    public async Task<IActionResult> Create(int approvalTypeId)
    {
        var user = await CurrentUserAsync();
        if (!IsAdmin && !await requests.CanCreateAsync(user.Id, approvalTypeId))
            return Forbid();

        var type = await db.ApprovalTypes.FindAsync(approvalTypeId);
        if (type is null) return NotFound();

        var fields = await requests.GetFormFieldsAsync(approvalTypeId);
        var model = new RequestFormViewModel
        {
            Request = new Request { ApprovalTypeId = approvalTypeId, Status = "Draft", CreatorUserId = user.Id, CreatorUserName = user.UserName },
            ApprovalType = type,
            Fields = fields,
            Values = fields.ToDictionary(f => f.FieldKey, f => (string?)null),
            Picklists = await LoadPicklistsAsync(fields)
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(52_428_800)] // 50 MB, matching UploadAttachment -- files can now be picked at Create time too
    public async Task<IActionResult> Create(int approvalTypeId, string? subject, bool submitNow, List<IFormFile>? attachmentFiles)
    {
        var user = await CurrentUserAsync();
        if (!IsAdmin && !await requests.CanCreateAsync(user.Id, approvalTypeId))
            return Forbid();

        var type = await db.ApprovalTypes.FindAsync(approvalTypeId);
        if (type is null) return NotFound();

        var draft = await requests.CreateDraftAsync(approvalTypeId, user.Id, user.UserName);
        var fields = await requests.GetFormFieldsAsync(approvalTypeId);
        var values = ReadFieldValues(Request.Form, fields);

        // Attached here (the draft row now exists) rather than requiring a separate trip to
        // Edit first -- RequestAttachment.RequestId is a required FK, so this couldn't happen
        // any earlier in the flow.
        var attachmentErrors = new List<string>();
        foreach (var file in attachmentFiles ?? [])
        {
            if (file.Length == 0) continue;
            var error = await SaveAttachmentAsync(draft.Id, file, user);
            if (error is not null) attachmentErrors.Add(error);
        }

        var (ok, message) = await requests.SaveFieldsAsync(draft.Id, user.Id, subject, values);
        var attachmentSuffix = attachmentErrors.Count > 0 ? " " + string.Join(" ", attachmentErrors) : "";
        if (ok && submitNow)
        {
            var submitResult = await requests.SubmitAsync(draft.Id, user.Id);
            if (!submitResult.ok)
            {
                TempData["Error"] = submitResult.message;
                return RedirectToAction(nameof(Edit), new { id = draft.Id });
            }
            TempData["Success"] = "Request submitted." + attachmentSuffix;
            return RedirectToAction(nameof(Details), new { id = draft.Id });
        }

        if (!ok)
        {
            TempData["Error"] = message;
            return RedirectToAction(nameof(Edit), new { id = draft.Id });
        }

        TempData["Success"] = "Saved as draft." + attachmentSuffix;
        return RedirectToAction(nameof(Edit), new { id = draft.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(long id)
    {
        var user = await CurrentUserAsync();
        var reqRow = await db.Requests.FindAsync(id);
        if (reqRow is null) return NotFound();
        if (reqRow.CreatorUserId != user.Id && !IsAdmin) return Forbid();
        if (!RequestService.IsEditable(reqRow.Status)) return RedirectToAction(nameof(Details), new { id });

        var type = await db.ApprovalTypes.FindAsync(reqRow.ApprovalTypeId);
        var fields = await requests.GetFormFieldsAsync(reqRow.ApprovalTypeId);
        var currentValues = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reqRow.DataJson) ?? new();

        var model = new RequestFormViewModel
        {
            Request = reqRow,
            ApprovalType = type!,
            Fields = fields,
            Values = fields.ToDictionary(f => f.FieldKey, f => currentValues.TryGetValue(f.FieldKey, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null),
            Picklists = await LoadPicklistsAsync(fields),
            Attachments = await db.RequestAttachments.Where(a => a.RequestId == id).OrderByDescending(a => a.UploadedAt).ToListAsync()
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(long id, string? subject, bool submitNow)
    {
        var user = await CurrentUserAsync();
        var reqRow = await db.Requests.FindAsync(id);
        if (reqRow is null) return NotFound();

        var fields = await requests.GetFormFieldsAsync(reqRow.ApprovalTypeId);
        var values = ReadFieldValues(Request.Form, fields);

        var (ok, message) = await requests.SaveFieldsAsync(id, user.Id, subject, values);
        if (!ok)
        {
            TempData["Error"] = message;
            return RedirectToAction(nameof(Edit), new { id });
        }

        if (submitNow)
        {
            var submitResult = await requests.SubmitAsync(id, user.Id);
            TempData[submitResult.ok ? "Success" : "Error"] = submitResult.ok ? "Request submitted." : submitResult.message;
            return RedirectToAction(nameof(Details), new { id });
        }

        TempData["Success"] = "Saved.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Details(long id)
    {
        var user = await CurrentUserAsync();
        var reqRow = await db.Requests.FindAsync(id);
        if (reqRow is null) return NotFound();

        var isCreator = reqRow.CreatorUserId == user.Id;
        var isParticipant = await requests.IsParticipantAsync(id, user.Id);
        var hasViewAll = await requests.HasViewPermissionAsync(user.Id, reqRow.ApprovalTypeId);
        if (!isCreator && !isParticipant && !hasViewAll && !IsAdmin && !IsAuditor) return Forbid();

        var type = await db.ApprovalTypes.FindAsync(reqRow.ApprovalTypeId);
        var fields = await requests.GetSubmittedFieldsAsync(reqRow);
        if (isCreator && !IsAdmin) fields = fields.Where(f => !f.IsSensitive).ToList();

        var timeline = await requests.GetTimelineAsync(id) ?? [];
        var isEligible = await requests.IsEligibleApproverAsync(id, user.Id);
        var requestAttachments = await db.RequestAttachments.Where(a => a.RequestId == id).OrderByDescending(a => a.UploadedAt).ToListAsync();

        var model = new RequestDetailsViewModel
        {
            Request = reqRow,
            ApprovalType = type!,
            Fields = fields,
            Timeline = timeline,
            Attachments = requestAttachments,
            IsCreator = isCreator,
            IsEligibleApprover = isEligible || (IsAdmin && reqRow.Status == "Pending"),
            IsAdminOverride = !isEligible && IsAdmin && reqRow.Status == "Pending",
            CanWithdraw = isCreator && reqRow.Status is "Pending" or "Sent Back",
            CanEdit = isCreator && RequestService.IsEditable(reqRow.Status),
            IsAdmin = IsAdmin
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(long id, string decision, string? comments)
    {
        var user = await CurrentUserAsync();
        var reqRow = await db.Requests.FindAsync(id);
        if (reqRow is null) return NotFound();

        var isEligible = await requests.IsEligibleApproverAsync(id, user.Id);
        var adminOverride = !isEligible && IsAdmin;

        var (ok, message) = await requests.DecideAsync(id, user.Id, decision, comments, adminOverride);
        TempData[ok ? "Success" : "Error"] = ok ? $"Decision recorded: {decision}." : message;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(long id, string? reason)
    {
        var user = await CurrentUserAsync();
        var (ok, message) = await requests.WithdrawAsync(id, user.Id, reason);
        TempData[ok ? "Success" : "Error"] = ok ? "Request withdrawn." : message;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Nudge(long id)
    {
        var user = await CurrentUserAsync();
        var (ok, message) = await requests.NudgeAsync(id, user.Id, IsAdmin);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(52_428_800)] // 50 MB, matching the original Approval engine's attachment limit
    public async Task<IActionResult> UploadAttachment(long id, IFormFile file, string? returnTo)
    {
        var backTo = returnTo == "Edit" ? nameof(Edit) : nameof(Details);
        var user = await CurrentUserAsync();
        var reqRow = await db.Requests.FindAsync(id);
        if (reqRow is null) return NotFound();
        if (reqRow.CreatorUserId != user.Id && !IsAdmin) return Forbid();
        if (file.Length == 0) { TempData["Error"] = "Choose a file first."; return RedirectToAction(backTo, new { id }); }

        var error = await SaveAttachmentAsync(id, file, user);
        TempData[error is null ? "Success" : "Error"] = error ?? "Attachment uploaded.";
        return RedirectToAction(backTo, new { id });
    }

    // Shared by UploadAttachment (an existing request) and Create (attaching files picked
    // before the request had an Id, saved right after the draft row is created). Returns an
    // error message, or null on success.
    async Task<string?> SaveAttachmentAsync(long requestId, IFormFile file, AppUser user)
    {
        var ext = Path.GetExtension(file.FileName);
        if (BlockedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            return $"'{ext}' files aren't allowed as attachments.";

        await using var stream = file.OpenReadStream();
        var stored = await attachments.SaveAsync(requestId, file.FileName, stream);
        db.RequestAttachments.Add(new RequestAttachment
        {
            RequestId = requestId,
            OriginalFileName = file.FileName,
            StoredFileName = stored.StoredFileName,
            ContentType = file.ContentType,
            FileSize = file.Length,
            UploadedByUserId = user.Id,
            UploadedByUserName = user.UserName,
            UploadedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> DownloadAttachment(long attachmentId)
    {
        var user = await CurrentUserAsync();
        var att = await db.RequestAttachments.FindAsync(attachmentId);
        if (att is null) return NotFound();

        var reqRow = await db.Requests.FindAsync(att.RequestId);
        if (reqRow is null) return NotFound();
        var isCreator = reqRow.CreatorUserId == user.Id;
        var isParticipant = await requests.IsParticipantAsync(att.RequestId, user.Id);
        var hasViewAll = await requests.HasViewPermissionAsync(user.Id, reqRow.ApprovalTypeId);
        if (!isCreator && !isParticipant && !hasViewAll && !IsAdmin && !IsAuditor) return Forbid();

        var path = attachments.GetPath(att.RequestId, att.StoredFileName);
        if (!System.IO.File.Exists(path)) return NotFound();

        db.AuditLogs.Add(new AuditLog { RequestId = att.RequestId, UserId = user.Id, ActionCode = "AttachmentDownload", DetailsJson = JsonSerializer.Serialize(new { attachmentId, fileName = att.OriginalFileName }), CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        return PhysicalFile(path, string.IsNullOrEmpty(att.ContentType) ? "application/octet-stream" : att.ContentType, att.OriginalFileName);
    }

    // ---------- Admin: reassign an approver (employee left/replaced) ----------

    [HttpGet]
    [Authorize(Policy = "UnifiedAdmin")]
    public async Task<IActionResult> Reassign(long id)
    {
        var reqRow = await db.Requests.FindAsync(id);
        if (reqRow is null) return NotFound();
        if (reqRow.Status != "Pending" || reqRow.CurrentLevelNo is null || reqRow.WorkflowVersionId is null || reqRow.RoutingRuleId is null)
            return BadRequest("This item is not currently awaiting a decision at a specific level.");

        var step = await db.WorkflowSteps.SingleOrDefaultAsync(s => s.WorkflowVersionId == reqRow.WorkflowVersionId && s.RoutingRuleId == reqRow.RoutingRuleId && s.LevelNo == reqRow.CurrentLevelNo);
        if (step is null) return NotFound();

        var currentApproverIds = await db.WorkflowStepApprovers.Where(a => a.WorkflowStepId == step.Id).Select(a => a.UserId).ToListAsync();
        var users = await db.AppUsers.Where(u => u.IsActive).OrderBy(u => u.DisplayName).ToListAsync();

        var model = new ReassignEditViewModel
        {
            RequestId = reqRow.Id,
            RequestNumber = reqRow.RequestNumber,
            LevelNo = step.LevelNo,
            CurrentApprovers = users.Where(u => currentApproverIds.Contains(u.Id)).Select(u => (u.Id, u.DisplayName)).ToList(),
            Users = users.Select(u => (u.Id, u.DisplayName, u.Department)).ToList()
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = "UnifiedAdmin")]
    public async Task<IActionResult> Reassign(ReassignEditViewModel model)
    {
        var admin = await CurrentUserAsync();
        var (ok, message) = await requests.ReassignApproverAsync(model.RequestId, model.OldUserId, model.NewUserId, model.Reason, admin.Id);
        TempData[ok ? "Success" : "Error"] = message;
        return RedirectToAction(nameof(Details), new { id = model.RequestId });
    }

    [HttpGet]
    [Authorize(Policy = "UnifiedAdmin")]
    public async Task<IActionResult> BulkReassign(long[] ids, int? oldUserId = null)
    {
        if (ids.Length == 0)
        {
            TempData["Error"] = "Select at least one item to reassign.";
            return RedirectToAction(nameof(All));
        }

        var selected = await db.Requests.Where(r => ids.Contains(r.Id) && r.Status == "Pending").ToListAsync();
        if (selected.Count == 0)
        {
            TempData["Error"] = "None of the selected items are currently awaiting a decision.";
            return RedirectToAction(nameof(All));
        }

        var users = await db.AppUsers.Where(u => u.IsActive).OrderBy(u => u.DisplayName).ToListAsync();

        var approverIds = new HashSet<int>();
        foreach (var r in selected)
        {
            if (r.WorkflowVersionId is null || r.RoutingRuleId is null || r.CurrentLevelNo is null) continue;
            var step = await db.WorkflowSteps.SingleOrDefaultAsync(s => s.WorkflowVersionId == r.WorkflowVersionId && s.RoutingRuleId == r.RoutingRuleId && s.LevelNo == r.CurrentLevelNo);
            if (step is null) continue;
            var stepApproverIds = await db.WorkflowStepApprovers.Where(a => a.WorkflowStepId == step.Id).Select(a => a.UserId).ToListAsync();
            foreach (var uid in stepApproverIds) approverIds.Add(uid);
        }

        var model = new BulkReassignViewModel
        {
            RequestIds = selected.Select(r => r.Id).ToList(),
            RequestNumbers = selected.Select(r => r.RequestNumber).ToList(),
            CurrentApprovers = users.Where(u => approverIds.Contains(u.Id)).Select(u => (u.Id, u.DisplayName)).ToList(),
            Users = users.Select(u => (u.Id, u.DisplayName, u.Department)).ToList(),
            OldUserId = oldUserId is not null && approverIds.Contains(oldUserId.Value) ? oldUserId : null
        };
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = "UnifiedAdmin")]
    public async Task<IActionResult> BulkReassign(BulkReassignViewModel model)
    {
        var admin = await CurrentUserAsync();
        var succeeded = 0;
        var failed = 0;
        foreach (var requestId in model.RequestIds)
        {
            var (ok, _) = await requests.ReassignApproverAsync(requestId, model.OldUserId, model.NewUserId, model.Reason, admin.Id);
            if (ok) succeeded++; else failed++;
        }

        var summary = $"Reassigned {succeeded} of {model.RequestIds.Count} item(s).";
        if (failed > 0) summary += $" {failed} could not be reassigned (already closed or route incomplete).";
        TempData[failed == 0 ? "Success" : "Error"] = summary;
        return RedirectToAction(nameof(All));
    }

    async Task<Dictionary<string, List<PicklistValue>>> LoadPicklistsAsync(List<WorkflowField> fields)
    {
        var result = new Dictionary<string, List<PicklistValue>>();
        foreach (var lookupType in fields.Where(f => f.DataType == FieldDataType.Dropdown && !string.IsNullOrEmpty(f.LookupType)).Select(f => f.LookupType!).Distinct())
            result[lookupType] = await requests.GetPicklistAsync(lookupType);
        return result;
    }

    static Dictionary<string, JsonElement> ReadFieldValues(IFormCollection form, List<WorkflowField> fields)
    {
        var dict = new Dictionary<string, JsonElement>();
        foreach (var f in fields)
        {
            var raw = form[$"Fields[{f.FieldKey}]"].ToString();
            dict[f.FieldKey] = JsonSerializer.SerializeToElement(raw);
        }
        return dict;
    }
}
