using System.Text.Json;

namespace JACO.Unified.Web.Models;

public sealed class CreateApprovalApiRequest
{
    public string ApprovalTypeCode { get; set; } = "";
    public string? Subject { get; set; }
    // Round-tripped back on GET as its own field (see ApprovalApiResponse) -- the external
    // system's own key for this record (e.g. an SAP Sales Order number), kept separate from
    // Data since it's an integration concern, not a business field a human fills in.
    public string? ExternalReference { get; set; }
    public Dictionary<string, JsonElement> Data { get; set; } = new();
}

public sealed class ApprovalApiResponse
{
    public required string RequestNumber { get; init; }
    public required string ApprovalType { get; init; }
    public required string Status { get; init; }
    public int? CurrentLevel { get; init; }
    public string? Subject { get; init; }
    public string? ExternalReference { get; init; }
    public required string CreatedBy { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public required Dictionary<string, string?> Data { get; init; }
}

public sealed record ApiTimelineDecision(string ActorName, string ActionCode, string? Comments, DateTime AtUtc);
public sealed record ApiTimelineLevel(int LevelNo, string Mode, List<string> Approvers, string LevelStatus, List<ApiTimelineDecision> Decisions);

public sealed class ApiErrorResponse
{
    public required string Error { get; init; }
}
