# JAMS — Feature List

Every capability built and verified in this project, grouped by area. See [TEST_CASES.md](../TEST_CASES.md) for the verification history behind each item, and [ADMIN_GUIDE.md](ADMIN_GUIDE.md) for how to use them.

---

## Core Engine

- Multi-Approval-Type platform on a single shared routing engine — a new request type is pure configuration (fields, rules, templates), not new code.
- Data-driven field catalog (`WorkflowField`) per type: label, data type, required/sensitive/API-inclusion/lookup/order/active flags; generic fields shared across all types.
- Shared, admin-editable dropdown/lookup value table (`PicklistValue`), with optional extra data per value (e.g. a branch's account email).
- Draft → Pending → Approved / Rejected / Sent Back → Resubmit → Withdrawn request lifecycle.
- Multi-level approval routing with four quorum modes per level: Any One, All, Majority, Minimum Count.
- Priority-ordered routing rules with multi-field criteria (equals, not-equals, contains, starts-with, ends-with, in-list, numeric/date comparisons); first full match wins.
- Sensitive-field visibility control: a field can be hidden from a request's own creator while remaining visible to approvers/admins.
- Automatic participant tracking ("My Work" works without a separate access grant for anyone who's ever created or been eligible to approve a request).
- Admin override on decisions, with full audit trail of who actually decided vs. who was assigned.
- Reassign a single request's, or bulk-reassign many requests', current approver.
- Withdraw (creator, while Pending) and Send-Back → Edit → Resubmit flow.
- Nudge a current approver with a cooldown-limited reminder.

## Request Screens

- "My Work" — requests created by or awaiting/involving the current user, with search, status/type/date filters, and sortable columns.
- "Display All" — oversight view of every request of a type the user has view-all rights on.
- Dynamic Create/Edit forms generated entirely from a type's field catalog (no per-type screens to build).
- Read-only Details view: fields, full approval timeline, attachments, decision eligibility.
- File attachment upload (50MB limit, executable-extension denylist) and secure download.
- CSV export of both "My Work" and "Display All", respecting active filters.

## Rule Builder

- Simple Rule Builder: per-rule criteria + up to 5 approval levels, each with its own quorum mode and required count.
- Chip-based approver picker with live search/filter by name or department (replaced an earlier native multi-select that needed Ctrl/Cmd+click to deselect — redesigned after direct UAT feedback), used consistently in both the simple Rule Builder and Bulk Rule.
- **Bulk Rule**: plain-language "split rules by a field's value" wizard, with optional second-level drill-down (e.g. into a numeric range) per value, generating and saving many real routing rules from one action — went through three full UI redesign iterations based on specific usability feedback before functionality was built.
- **Bulk Import**: CSV upload of an entire rule matrix (criteria + all levels' approvers per row), with a validated preview step (no write until confirmed) and a choice of Replace-All or Upsert.
- Downloadable CSV template and CSV export of the current rule list.
- Sortable, priority-ordered rule list with criteria/level summaries and active/disabled status badges.

## Email & Notifications

- Admin-editable SMTP settings, applied live (no redeploy), with encrypted-at-rest password and a built-in Send Test.
- Reusable HTML Mail Templates with merge tokens, single-record and table (digest) styles, in-browser preview, and one-click duplication (Copy).
- Post-Processing Rules ("PPF"): configurable per Approval Type and lifecycle event (Created/Resubmitted/Approved/Rejected/Sent Back/Nudged/Level Pending/Completed), choosing recipient mode (Creator / Fixed address / a submitted field's value / every current-level approver individually) for both To and Cc (Cc optional, multi-address supported).
- One-click Approve/Reject links embedded directly in approver emails, each a signed, time-limited (14-day), tamper-proof token — no login required to act.
- Inline-embedded email logo (`cid:` attachment) so branding renders correctly for any recipient regardless of network access, instead of a broken external-image link.
- A notification failure never blocks or alters the underlying request — email is best-effort by design.
- Automatic per-Approval-Type Pending Approvals Digest: configurable recurrence (Every N Days or specific Weekdays), precise per-recipient "you personally have something to decide" targeting (not a loose involvement check), plus a manual "Run Now" and a one-off manual digest to a single recipient.
- PPF Monitor: every notification attempt (sent/failed/skipped, with reason) across every request, filterable and exportable.
- Digest Log: every scheduled/manual digest run, with per-recipient outcome and the exact rendered content sent.

## Users & Access

- Local, standalone login (username/password) with account lockout (5 attempts / 15 minutes) and admin-triggered unlock.
- Self-service and forced (first-login) password change.
- Per-Approval-Type permission grants: Can Create, Can View All ("Display All"), fully audited with before/after change history.
- Admin and Auditor roles (Auditor: Reports-only, no create/approve/configure access).
- CSV bulk import of user accounts, including their per-type permission grants in the same file; system-generated random passwords (never taken from the CSV), revealed exactly once immediately after import.

## Reporting

- Volume report — counts by type and status over a date range, with a weekly created-vs-decided trend.
- Cycle Time report — average/median/min/max hours to completion, measured from the request's actual submission (not its draft creation), by type and by level.
- Approver Workload report — per-approver decision counts, average decision time, and current pending load.
- Consistent date-range filtering, column sorting, and CSV export across all three; visible to Admins and to the read-only Auditor role.

## External API

- Versioned REST API (`/api/v1/approvals`), authenticated independently of the UI login via a per-client API key.
- Create + auto-submit a request in one atomic call (rolls back cleanly on any validation/routing failure).
- Read a request's full status/data and its per-level approval timeline.
- Discover active Approval Type codes and a type's live field schema (kept in sync automatically with the admin-configured field catalog — never goes stale).
- Per-field control over API exposure, independent of whether the field is visible in the UI.
- Admin console: master enable/disable switch, per-client key issuance/rotation/deactivation, one-time key reveal, live-generated API reference page and downloadable Markdown spec per Approval Type.
- Full request/response audit trail of every external call (Cockpit → API Request Log), including rejected/unauthorized attempts.

## Operations, Compliance & Admin Tooling

- **Cockpit** console with four independent logs — Routing Log, Audit Log, API Request Log, Digest Log — each sortable, filterable, and CSV-exportable.
- "Admin override only" filter on the Audit Log, surfacing every case where an admin decided on someone else's behalf.
- **Archive-before-purge** log retention: every "Clear Old Entries" action snapshots the full rows being removed to a downloadable archive before deleting them.
- Server-side, non-bypassable 90-day minimum retention floor on log clearing, automatically enforced whenever System Settings is set to Production (relaxed in Test mode for active development/QA).
- Single Production/Test environment flag (System Settings) gating the above.
- Standing UI conventions applied platform-wide: clickable stat-card filters, "back to list" links from every detail/edit screen, sortable headers on every multi-record list.
- Shared in-app confirmation modal for every destructive action (no native browser `confirm()`/`alert()` popups anywhere in the app).

## Branding & UX Polish (from UAT)

- Product-wide rebrand to **"JAMS — Approval System"** across the header, browser tab title, login page, and email templates.
- Cache-busted static assets (`asp-append-version`) so a deployment's CSS/JS changes are never masked by browser heuristic caching.
- Aligned, fixed request header/action-button layout between the request Details and Edit screens.
- Editable-through-the-value-link pattern for Picklist Values (matches the rest of the admin UI's edit-in-place convention).
- Sticky save toolbar on long request edit forms.

## Deployment & Environment

- Standalone Windows Service deployment (`JACO-Unified`), independent of any Portal/SSO application.
- Git-tag-based promotion workflow between dev and each environment (QA, Production), with a documented, repeatable update procedure.
- Full deployment runbook covering fresh environment setup (Phases 0–6) and routine updates (Phase 7).
