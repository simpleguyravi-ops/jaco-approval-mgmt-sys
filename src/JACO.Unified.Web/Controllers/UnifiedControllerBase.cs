using System.Security.Claims;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JACO.Unified.Web.Controllers;

[Authorize]
public abstract class UnifiedControllerBase(RequestService requests) : Controller
{
    protected bool IsAdmin => User.IsInRole("UNIFIED_ADMIN") || User.IsInRole("PORTAL_ADMIN") || User.IsInRole("SYSTEM_ADMIN");
    // An Auditor sees across every Approval Type in Reports (aggregate counts/timings only,
    // no field values) -- so their drill-through to All Requests needs the same breadth,
    // same carve-out IsAdmin already gets there, rather than falling back to CanView grants
    // that a report-only account was never meant to need.
    protected bool IsAuditor => IsAdmin || User.IsInRole("UNIFIED_AUDITOR");

    // Cached per-request so a controller action can call this repeatedly without re-querying.
    AppUser? _currentUser;
    protected async Task<AppUser> CurrentUserAsync()
    {
        _currentUser ??= await requests.ResolveCurrentUserAsync(User)
            ?? throw new InvalidOperationException("Unable to resolve the signed-in user -- SSO cookie is missing a Name claim.");
        return _currentUser;
    }
}
