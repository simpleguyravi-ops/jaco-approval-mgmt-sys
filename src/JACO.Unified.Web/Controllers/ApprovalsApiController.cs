using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// For external systems (SAP, etc.) -- create an approval request and read its status by
// Request Number. Deliberately NOT [Authorize]/cookie-based: authentication (an ApiClient's
// key), the enable/disable master switch, and full request/response logging are all
// handled up front by ApiGatewayMiddleware before a request ever reaches here -- by the
// time an action body runs, HttpContext.Items["ApiClient"] is guaranteed to hold a valid,
// active ApiClient. Every WorkflowField (including IsSensitive ones) is returned here --
// the caller is a trusted system-to-system credential, not an end user browsing the UI,
// so the human-facing "hide from the creator" rule doesn't apply.
[ApiController]
[Route("api/v1/approvals")]
[EnableRateLimiting("api")]
public sealed class ApprovalsApiController(UnifiedDbContext db, RequestService requests) : ControllerBase
{
    ApiClient CurrentClient => (ApiClient)HttpContext.Items["ApiClient"]!;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApprovalApiRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.ApprovalTypeCode))
            return BadRequest(new ApiErrorResponse { Error = "approvalTypeCode is required." });

        var type = await db.ApprovalTypes.SingleOrDefaultAsync(t => t.Code == body.ApprovalTypeCode && t.Active);
        if (type is null)
            return BadRequest(new ApiErrorResponse { Error = $"No active Approval Type with code '{body.ApprovalTypeCode}'." });

        var user = await requests.ResolveApiClientUserAsync(CurrentClient);
        var request = await requests.CreateDraftAsync(type.Id, user.Id, user.DisplayName);

        var fieldValues = new Dictionary<string, JsonElement>(body.Data);
        if (!string.IsNullOrWhiteSpace(body.ExternalReference))
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body.ExternalReference));
            fieldValues["externalReference"] = doc.RootElement.Clone();
        }

        var (saveOk, saveMessage) = await requests.SaveFieldsAsync(request.Id, user.Id, body.Subject, fieldValues);
        if (!saveOk)
        {
            // Create is meant to be atomic from the caller's point of view -- don't leave a
            // half-formed Draft cluttering the Requests list on a rejected call.
            db.Requests.Remove(request);
            await db.SaveChangesAsync();
            return BadRequest(new ApiErrorResponse { Error = saveMessage });
        }

        var (submitOk, submitMessage) = await requests.SubmitAsync(request.Id, user.Id, source: "Api");
        if (!submitOk)
        {
            db.Requests.Remove(request);
            await db.SaveChangesAsync();
            return BadRequest(new ApiErrorResponse { Error = submitMessage });
        }

        var response = await BuildResponseAsync(request.Id);
        return CreatedAtAction(nameof(Get), new { requestNumber = request.RequestNumber }, response);
    }

    [HttpGet("{requestNumber}")]
    public async Task<IActionResult> Get(string requestNumber)
    {
        var request = await requests.GetByRequestNumberAsync(requestNumber);
        if (request is null) return NotFound(new ApiErrorResponse { Error = $"No request found with number '{requestNumber}'." });

        return Ok(await BuildResponseAsync(request.Id));
    }

    [HttpGet("{requestNumber}/timeline")]
    public async Task<IActionResult> Timeline(string requestNumber)
    {
        var request = await requests.GetByRequestNumberAsync(requestNumber);
        if (request is null) return NotFound(new ApiErrorResponse { Error = $"No request found with number '{requestNumber}'." });

        var timeline = await requests.GetTimelineAsync(request.Id) ?? [];
        var result = timeline.Select(l => new ApiTimelineLevel(
            l.LevelNo, l.Mode, l.ApproverNames.ToList(), l.LevelStatus,
            l.Decisions.Select(d => new ApiTimelineDecision(d.ActorName, d.ActionCode, d.Comments, d.AtUtc)).ToList()
        )).ToList();
        return Ok(result);
    }

    // Self-describing discovery: an integrator (or a script SAP runs at deploy time) can
    // find out what approvalTypeCode values even exist without asking a human first.
    [HttpGet("types")]
    public async Task<IActionResult> Types()
    {
        var types = await db.ApprovalTypes.Where(t => t.Active).OrderBy(t => t.Name)
            .Select(t => new { code = t.Code, name = t.Name, description = t.Description }).ToListAsync();
        return Ok(types);
    }

    // The live field shape for one Approval Type -- exactly what Create accepts in `data`
    // and what Get returns in `data` back, generated from the same WorkflowFields catalog
    // that drives the UI form. A field added/renamed/removed under Criteria Fields is
    // reflected here immediately -- no API version, no redeploy, no admin action needed
    // beyond the field edit itself.
    [HttpGet("schema/{approvalTypeCode}")]
    public async Task<IActionResult> Schema(string approvalTypeCode)
    {
        var type = await db.ApprovalTypes.SingleOrDefaultAsync(t => t.Code == approvalTypeCode && t.Active);
        if (type is null) return NotFound(new ApiErrorResponse { Error = $"No active Approval Type with code '{approvalTypeCode}'." });

        var fields = await requests.GetFieldSchemaAsync(type.Id);
        return Ok(new
        {
            approvalTypeCode = type.Code,
            approvalTypeName = type.Name,
            fields = fields.Select(f => new
            {
                order = f.DisplayOrder,
                key = f.FieldKey,
                label = f.Label,
                dataType = f.DataType,
                required = f.Required,
                sensitive = f.Sensitive,
                lookupType = f.LookupType,
                allowedValues = f.AllowedValues.Count > 0 ? f.AllowedValues : null
            })
        });
    }

    async Task<ApprovalApiResponse> BuildResponseAsync(long requestId)
    {
        var request = await db.Requests.SingleAsync(r => r.Id == requestId);
        var type = await db.ApprovalTypes.FindAsync(request.ApprovalTypeId);
        var fields = await requests.GetSubmittedFieldsAsync(request);

        return new ApprovalApiResponse
        {
            RequestNumber = request.RequestNumber,
            ApprovalType = type?.Name ?? "(unknown)",
            Status = request.Status,
            CurrentLevel = request.CurrentLevelNo,
            Subject = request.Subject,
            ExternalReference = RequestService.ExtractField(request.DataJson, "externalReference"),
            CreatedBy = request.CreatorUserName,
            CreatedAtUtc = request.CreatedAt,
            UpdatedAtUtc = request.UpdatedAt,
            Data = fields.ToDictionary(f => f.FieldKey, f => f.Value)
        };
    }
}
