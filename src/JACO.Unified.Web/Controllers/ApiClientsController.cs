using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Controllers;

// Admin management of the external API layer (see ApprovalsApiController /
// ApiGatewayMiddleware): the master enable switch, whether every call gets logged, and the
// API keys ("clients") external systems authenticate with. A key's plaintext is shown
// exactly once, right after Create/Regenerate -- TempData survives exactly one redirect,
// which is the right lifetime for a one-time reveal (a page refresh loses it, same as any
// "copy this now" credential screen).
[Authorize(Policy = "UnifiedAdmin")]
public sealed class ApiClientsController(UnifiedDbContext db, RequestService requests) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var settings = await db.ApiSettings.SingleOrDefaultAsync(s => s.Id == 1) ?? new ApiSettings { Id = 1 };
        ViewBag.Settings = settings;
        ViewBag.NewApiKey = TempData["NewApiKey"];
        ViewBag.NewApiKeyClientName = TempData["NewApiKeyClientName"];
        return View(await db.ApiClients.OrderByDescending(c => c.CreatedAt).ToListAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(bool enabled, bool logRequests)
    {
        var settings = await db.ApiSettings.SingleOrDefaultAsync(s => s.Id == 1);
        if (settings is null)
        {
            settings = new ApiSettings { Id = 1 };
            db.ApiSettings.Add(settings);
        }
        settings.Enabled = enabled;
        settings.LogRequests = logRequests;
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedByUserName = User.Identity?.Name;
        await db.SaveChangesAsync();

        TempData["Success"] = enabled ? "External API enabled." : "External API disabled -- every call will now be rejected regardless of key validity.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "A name is required (e.g. \"SAP Production\").";
            return RedirectToAction(nameof(Index));
        }

        var (plaintextKey, hash, prefix) = ApiKeyService.GenerateKey();
        db.ApiClients.Add(new ApiClient
        {
            Name = name.Trim(),
            Description = description,
            KeyHash = hash,
            KeyPrefix = prefix,
            Active = true,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserName = User.Identity?.Name
        });
        await db.SaveChangesAsync();

        TempData["NewApiKey"] = plaintextKey;
        TempData["NewApiKeyClientName"] = name.Trim();
        TempData["Success"] = $"'{name}' created.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Regenerate(int id)
    {
        var client = await db.ApiClients.FindAsync(id);
        if (client is null) return NotFound();

        var (plaintextKey, hash, prefix) = ApiKeyService.GenerateKey();
        client.KeyHash = hash;
        client.KeyPrefix = prefix;
        await db.SaveChangesAsync();

        TempData["NewApiKey"] = plaintextKey;
        TempData["NewApiKeyClientName"] = client.Name;
        TempData["Success"] = $"New key generated for '{client.Name}' -- the old key stopped working immediately.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ToggleActive(int id)
    {
        var client = await db.ApiClients.FindAsync(id);
        if (client is null) return NotFound();

        client.Active = !client.Active;
        await db.SaveChangesAsync();
        TempData["Success"] = $"'{client.Name}' {(client.Active ? "activated" : "deactivated")}.";
        return RedirectToAction(nameof(Index));
    }

    // Live documentation, not a hand-maintained page -- the field list comes straight from
    // GetFieldSchemaAsync (WorkflowFields), the same source the API itself reads. Add,
    // rename, or remove a field under Criteria Fields and this page (and the API) reflect
    // it immediately, with nothing else to update.
    [HttpGet]
    public async Task<IActionResult> Reference(int? approvalTypeId)
    {
        var types = await db.ApprovalTypes.Where(t => t.Active).OrderBy(t => t.Name).ToListAsync();
        var selectedTypeId = approvalTypeId ?? types.FirstOrDefault()?.Id ?? 0;
        ViewBag.Types = types;
        ViewBag.SelectedTypeId = selectedTypeId;
        if (selectedTypeId == 0) return View(new List<ApprovalFieldSchema>());

        var fields = await requests.GetFieldSchemaAsync(selectedTypeId);
        var selectedType = types.First(t => t.Id == selectedTypeId);

        var example = new Dictionary<string, object?>
        {
            ["approvalTypeCode"] = selectedType.Code,
            ["subject"] = "Short summary of the request",
            ["externalReference"] = "Your own reference, e.g. an SAP document number",
            ["data"] = fields.ToDictionary(f => f.FieldKey, f => (object?)ExampleValue(f))
        };
        ViewBag.ExampleJson = JsonSerializer.Serialize(example, new JsonSerializerOptions { WriteIndented = true });

        return View(fields);
    }

    static object ExampleValue(ApprovalFieldSchema f)
    {
        if (f.AllowedValues.Count > 0) return f.AllowedValues[0];
        return f.DataType switch
        {
            FieldDataType.Number => 1,
            FieldDataType.Currency => 100.00,
            FieldDataType.Date => DateTime.UtcNow.ToString("yyyy-MM-dd"),
            FieldDataType.TextArea => $"{f.Label} text",
            _ => f.Label
        };
    }
}
