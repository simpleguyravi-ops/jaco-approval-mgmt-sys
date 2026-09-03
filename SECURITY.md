# JAMS — Security Concerns & Risk Assessment

**Audience:** IT/security reviewers, admins preparing a Production go-live.
**Source of truth for verification history:** [TEST_CASES.md](../TEST_CASES.md) §12 (Security review, 2026-09-03) and §13 (Compliance & log-clearing). This document summarizes and organizes that review by risk area; where the two disagree, TEST_CASES.md is authoritative.

---

## 1. Summary

A full-application security pass was carried out on 2026-09-03, covering authentication, session handling, authorization/IDOR, injection classes, file handling, the external API layer, and email-triggered actions. Several real issues were found and fixed during that pass (below). The remaining known limitations are explicitly called out in §5 rather than left implicit, so a Production go-live decision is made with full information rather than a false sense of completeness.

## 2. Issues Found and Fixed

| Issue | Risk | Fix |
|---|---|---|
| **GET-triggered approval action** | An Approve/Reject link could previously be actioned by a GET request alone — a crawler, link scanner, or prefetching browser could trigger a real approval decision with no human intent behind it | `EmailActionController.Decide` (GET) now only *validates* the token and redirects to a confirm page; the actual decision (`ApproveConfirm`/`RejectConfirm`) requires an explicit POST from that confirm page |
| **Missing CSRF protection on email-approval POSTs** | The anonymous, token-based approve/reject confirm actions lacked an antiforgery token, leaving a cross-site request forgery gap even though the requests were otherwise authenticated by the signed link token | `[ValidateAntiForgeryToken]` added to `ApproveConfirm`/`RejectConfirm`; the confirm forms render and submit the token normally |
| **Stored XSS — admin-entered API client description** | An API client's admin-entered description was rendered without encoding in the admin UI | Output-encoded on render |
| **Stored XSS — un-encoded recipient name in digest emails** | A recipient's display name was interpolated into digest email HTML without encoding | Encoded via the existing `MailMergeService` token-encoding path |
| **Dependency version drift** | Several NuGet packages were behind their patched versions | Pinned/updated to current patched versions |
| **Missing security response headers** | `Permissions-Policy`, `Cross-Origin-Opener-Policy`, and `X-Permitted-Cross-Domain-Policies` were not set | Added alongside the existing header set in `Program.cs` (see §3) |
| **No `robots.txt`** | Search engines had no explicit instruction not to index an internal tool | Added |

## 3. Security Controls Already in Place

### Transport & response headers
Set on every response via middleware in `Program.cs`: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: same-origin`, `Content-Security-Policy` (`default-src 'self'`, `frame-ancestors 'none'`, `object-src 'none'`), `Permissions-Policy` (camera, microphone, geolocation, payment, USB, and interest-cohort/FLoC all denied), `Cross-Origin-Opener-Policy: same-origin`, `X-Permitted-Cross-Domain-Policies: none`. HSTS is enabled outside Development. `ForwardedHeaders` (`X-Forwarded-For`/`X-Forwarded-Proto`) are only trusted from an explicitly configured reverse-proxy IP — untrusted by default.

### Authentication & session
- Local login only (`AccountController`) — no dependency on an external Portal/SSO in the current deployment.
- Passwords hashed with **PBKDF2-SHA256, 100,000 iterations, 16-byte salt** — the same parameters used platform-wide for consistency.
- **Account lockout**: 5 failed attempts locks an account for 15 minutes; an admin can unlock manually.
- **Timing-attack-resistant login**: a login against a non-existent username still runs a dummy credential comparison, so response timing doesn't reveal which usernames exist.
- Session cookie: `HttpOnly`, `SameSite=Lax`, 8-hour sliding expiration, backed by a persisted ASP.NET Core Data Protection key ring.
- Forced password change flow for newly created/reset accounts.
- No self-service password reset exists (removes an entire class of reset-token/email-spoofing risk at the cost of requiring admin-assisted resets — an accepted, deliberate trade-off, not an oversight).

### Authorization
- Every admin screen requires the `UnifiedAdmin` policy (role `UNIFIED_ADMIN`, `PORTAL_ADMIN`, or `SYSTEM_ADMIN`); Reports additionally accepts the `UNIFIED_AUDITOR` role, read-only.
- Per-Approval-Type Create/View-All grants are checked on every request action, not just on the list screen (an IDOR-style "guess the URL" sweep was carried out across Requests, Attachments, and admin actions during the review).
- Admin overrides on decisions are permitted but fully audited — logged with both the assigned approver and the actual actor.

### Data handling
- **File uploads**: server-generated GUID filenames only (the user-supplied filename is never used to construct a path — path traversal is not reachable regardless of what a caller names a file), a denylist of executable/script extensions, a 50MB size cap, and forced `Content-Disposition: attachment` on download (a malicious upload can't execute in-browser).
- **SQL injection**: the codebase is EF Core-parameterized throughout; the review specifically identified and checked the one static, parameterless raw-SQL statement in the codebase and confirmed no injectable surface exists.
- **Open redirect**: every `ReturnUrl`-style redirect is checked with `Url.IsLocalUrl` before use.
- **No ReDoS surface**: no user-controlled input is fed into a regular expression with unbounded backtracking risk.
- **CSRF**: `[ValidateAntiForgeryToken]` is applied to essentially every state-changing POST across the app, including the anonymous email-action confirm endpoints (see §2).
- **No native browser popups**: every confirmation dialog uses a shared, safe in-app modal rather than `confirm()`/`alert()`, avoiding the class of issues around browser-native dialogs and any risk of a spoofable/blockable confirmation.

### One-click email actions
- Approve/Reject links embed a **signed, encrypted, time-limited (14-day)** token binding a specific request and user, generated by `ApprovalActionLinkService` using the same Data Protection key ring as the session cookie. A tampered, expired, or already-actioned token simply fails validation — it does not leak information about why.
- Current eligibility (is this person still a valid approver at this level, right now?) is re-checked at decision time, not just at link-generation time — an approver removed or reassigned after the email was sent cannot still act on the stale link.

### External API layer
- Disabled by default (`ApiSettings.Enabled = false` at seed time); must be explicitly turned on by an admin.
- Authentication is fully independent of the UI's cookie session: a required `X-Api-Key` header, verified by **constant-time comparison of a salted SHA-256 hash** (`ApiKeyService`) — only a non-secret 16-character prefix is ever used for fast lookup; the full key is never stored in retrievable form.
- `ApiGatewayMiddleware` runs before any controller code: rejects disabled/missing/invalid/inactive keys before they reach business logic, catches unhandled exceptions to return a generic 500 (no stack traces or internal detail leaked to an external caller), and logs every call — success, failure, or rejection — including method, path, truncated request/response bodies, status, remote IP, and duration (unless request logging is explicitly turned off).
- Per-field API exposure control (`WorkflowField.IncludeInApi`) lets an admin expose a narrower data surface via the API than via the UI, independent of UI visibility.

### Rate limiting
Applied platform-wide via `AddRateLimiter` (HTTP 429 on rejection):

| Policy | Applies to | Limit |
|---|---|---|
| `login` | Login attempts, by client IP | 10 / minute |
| `emailAction` | Anonymous email approve/reject actions, by client IP | 20 / minute |
| `sensitive` | Authenticated state-changing actions, by user (or IP if unauthenticated) | 30 / minute |
| `api` | External API calls, by API key (or IP if unauthenticated) | 60 / minute |

### Compliance — log retention
Every Cockpit "Clear Old Entries" action **archives a full JSON snapshot of every row before deleting it** (`LogArchive`, downloadable from "Archived Clears"), and a **90-day minimum retention floor is enforced server-side** — not merely in the UI — whenever `SystemSettings.IsProduction` is set. This protects against an admin accidentally deleting recent operational history; it is explicitly **not** a replacement for regular SQL Server backups, which remain the real disaster-recovery layer for data loss at the database level. See [TEST_CASES.md](../TEST_CASES.md) §13 for the full design rationale and manual verification checklist.

## 4. An Important Clarification: Standalone vs. Shared-SSO Architecture

The codebase's authentication infrastructure (cookie name, Data Protection key ring path/application name) is built to be **capable of** participating in single sign-on with other JACO applications sharing the same key ring. **This is not how JAMS is deployed today.** Per explicit product direction, JAMS is deployed and operated as a fully standalone application with its own local login and no Portal dependency (see [DEPLOYMENT_RUNBOOK.md](DEPLOYMENT_RUNBOOK.md)). This is noted here specifically so a future reviewer does not mistake "the code could support SSO" for "SSO is in use" — the actual trust boundary today is JAMS's own login screen and its own `AppUsers` table, full stop.

## 5. Known Limitations / Explicitly Out of Scope (Deferred, Not Overlooked)

These were identified during the review and are recorded deliberately rather than silently accepted:

1. **TLS/HTTPS termination is not handled by the application itself** — it is the responsibility of the reverse proxy/load balancer in front of JAMS in each environment. Confirm TLS is correctly configured at that layer before go-live; the app's own `UseHsts()`/`UseHttpsRedirection()` assume it sits behind a terminator that has already done this.
2. **Content-Security-Policy still allows `'unsafe-inline'`** for scripts and styles. This was a deliberate scope decision (removing it would require a broader refactor of the server-rendered Razor views' inline `<script>`/`<style>` blocks) rather than an oversight — it meaningfully reduces, but does not eliminate, the CSP's protection against injected script execution. Tightening this is a reasonable future hardening item, not a blocking one, given the stored-XSS gaps found in this same review were already fixed at the output-encoding layer.
3. **No CAPTCHA or equivalent bot-mitigation on the login page.** Rate limiting (`login` policy, 10/min by IP) and account lockout (5 attempts/15 min) provide meaningful brute-force resistance, but a determined, distributed attacker is not fully mitigated by IP-based rate limiting alone.
4. **Automated dependency vulnerability scanning (`dotnet list package --vulnerable`) could not be completed** during the review due to sandbox network restrictions. **This must be run from an environment with real network access before Production go-live**, and any findings addressed, since the manual version-drift fix in §2 was a point-in-time check, not a substitute for an automated scan.
5. **The external API's default rate limits are not yet tuned to real integration traffic** (per [TEST_CASES.md](../TEST_CASES.md) §10) — the current 60/minute default is a reasonable starting point but should be revisited once a real external caller's traffic pattern is known.
6. **No per-API-client scoping to specific Approval Types** — any active, valid API key can currently call any Approval Type's endpoints. If a future integration should only ever touch one type, this would need to be added; not a risk today given the small number of trusted integrations expected.
7. **A dev-only test routing rule and a test API client exist in the Sales Discount configuration** (per [TEST_CASES.md](../TEST_CASES.md) §8, §10) and are explicitly flagged there to be removed/rotated before Production go-live — this is an environment-hygiene item, not a code defect, but is called out here so it isn't missed at cutover.

## 6. Recommendation Before Production Go-Live

Treat items 4 and 7 in §5 as **blocking** for go-live (a real dependency scan, and removal/rotation of dev-only test data). Treat items 1–3, 5, and 6 as **accepted risk with a documented rationale**, to be revisited opportunistically rather than blocking launch. This mirrors the checklist already present in [DEPLOYMENT_RUNBOOK.md](DEPLOYMENT_RUNBOOK.md) Phase 5 (UAT sign-off) and Phase 6 (Production-specific notes).
