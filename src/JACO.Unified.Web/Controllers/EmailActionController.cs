using JACO.Unified.Infrastructure;
using JACO.Unified.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JACO.Unified.Web.Controllers;

// Deliberately NOT a UnifiedControllerBase (which is [Authorize] at class level) -- these
// actions are the whole point of the one-click email links: they must work for someone who
// is not logged in at all. The opaque, signed+encrypted token from ApprovalActionLinkService
// is the ONLY credential; every action re-validates it and re-checks eligibility against the
// request's CURRENT state before doing anything, so a stale, forwarded, or tampered link
// can't approve/reject on someone else's behalf or replay an already-decided level.
[AllowAnonymous]
[EnableRateLimiting("emailAction")]
public sealed class EmailActionController(UnifiedDbContext db, RequestService requests, ApprovalActionLinkService linkService) : Controller
{
    // GET is never allowed to change state here -- a corporate mail gateway's Safe
    // Links-style scanner routinely auto-visits every link in an incoming email BEFORE a
    // human opens it, so a GET that decided immediately would let a scanner silently
    // approve a real request. Both Approve and Reject now follow the same
    // render-a-confirm-page-then-POST pattern; Decide only ever routes to one of those two
    // confirm pages, matching the links PpfExecutor already builds (their URLs don't change).
    [HttpGet]
    public async Task<IActionResult> Decide(string token, string decision)
    {
        if (!linkService.TryValidate(token, out _, out _))
            return View("Invalid");

        if (decision == "Reject")
            return RedirectToAction(nameof(RejectForm), new { token });
        if (decision == "Approve")
            return RedirectToAction(nameof(ApproveForm), new { token });

        return View("Invalid");
    }

    [HttpGet]
    public async Task<IActionResult> ApproveForm(string token)
    {
        if (!linkService.TryValidate(token, out var requestId, out var userId))
            return View("Invalid");

        var req = await db.Requests.FindAsync(requestId);
        var eligible = req is not null && await requests.IsEligibleApproverAsync(requestId, userId);
        if (req is null || req.Status != "Pending" || !eligible)
            return View("AlreadyHandled", req?.RequestNumber);

        return View(new EmailApproveViewModel { Token = token, RequestNumber = req.RequestNumber, Subject = req.Subject });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveConfirm(string token)
    {
        if (!linkService.TryValidate(token, out var requestId, out var userId))
            return View("Invalid");

        var req = await db.Requests.FindAsync(requestId);
        var eligible = req is not null && await requests.IsEligibleApproverAsync(requestId, userId);
        if (req is null || req.Status != "Pending" || !eligible)
            return View("AlreadyHandled", req?.RequestNumber);

        var (ok, message) = await requests.DecideAsync(requestId, userId, "Approve", "Approved via one-click email link.");
        return View("Result", new EmailActionResultViewModel { Ok = ok, Message = message, RequestNumber = req.RequestNumber, Decision = "Approve" });
    }

    [HttpGet]
    public async Task<IActionResult> RejectForm(string token)
    {
        if (!linkService.TryValidate(token, out var requestId, out var userId))
            return View("Invalid");

        var req = await db.Requests.FindAsync(requestId);
        var eligible = req is not null && await requests.IsEligibleApproverAsync(requestId, userId);
        if (req is null || req.Status != "Pending" || !eligible)
            return View("AlreadyHandled", req?.RequestNumber);

        return View(new EmailRejectViewModel { Token = token, RequestNumber = req.RequestNumber });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectConfirm(string token, string comments)
    {
        if (!linkService.TryValidate(token, out var requestId, out var userId))
            return View("Invalid");

        var req = await db.Requests.FindAsync(requestId);
        var eligible = req is not null && await requests.IsEligibleApproverAsync(requestId, userId);
        if (req is null || req.Status != "Pending" || !eligible)
            return View("AlreadyHandled", req?.RequestNumber);

        if (string.IsNullOrWhiteSpace(comments))
            return View("RejectForm", new EmailRejectViewModel { Token = token, RequestNumber = req.RequestNumber, Error = "A reason is required to reject." });

        var (ok, message) = await requests.DecideAsync(requestId, userId, "Reject", comments);
        return View("Result", new EmailActionResultViewModel { Ok = ok, Message = message, RequestNumber = req.RequestNumber, Decision = "Reject" });
    }
}
