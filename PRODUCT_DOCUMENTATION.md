# JAMS — JACO Approval Management System
## Final Product Documentation

**Version:** As of tag `qa-2026-09-04d` (2026-09-04)
**Audience:** Product owners, new developers, IT operations
**Companion documents:** [ADMIN_GUIDE.md](ADMIN_GUIDE.md) · [FEATURES.md](FEATURES.md) · [SECURITY.md](SECURITY.md) · [DEPLOYMENT_RUNBOOK.md](DEPLOYMENT_RUNBOOK.md) · [TEST_CASES.md](../TEST_CASES.md)

---

## 1. What JAMS Is

JAMS (JACO Approval Management System) is a single, standalone web application that runs **every approval workflow in the business** — Change Requests, Sales Discounts, and any future request type — on one shared engine, instead of building a new app per workflow.

Historically, each new approval process (CR, Sales Discount) would have meant a new codebase: its own request screens, its own routing logic, its own approval engine, wired to a separate central Approval service over HTTP. JAMS replaces that pattern. It is **one application** in which:

- An "Approval Type" (e.g. "Change Request", "Sales Discount") is **configuration**, not code — added by an admin in a few screens, not by a developer.
- The fields a request captures, the routing rules that decide who approves it, the emails it sends, and the permissions that govern it are all **data-driven**, read from database tables at run time.
- Adding a brand-new kind of approval to the business — a third, fourth, tenth type — requires **zero new C# code** in the common case: an admin defines the fields, the routing rules, and the email templates, and the same screens, the same API, and the same reporting immediately work for it.

This is the core value proposition: JAMS turns "build a new approval app" into "configure a new approval type."

## 2. Why It Exists

Two earlier standalone apps (JACO-CR for Change Requests, and an initial Sales Discount design) each re-implemented the same shape of problem — a draft/submit/approve/reject/send-back lifecycle, multi-level routing by criteria, email notifications, attachments, audit history — with separate codebases talking to a separate central "Approval" service.

That pattern doesn't scale: every new approval process meant a new app, a new database, a new deployment, and a growing central Approval codebase that became "increasingly tedious to touch safely" as more request types piled into it (the concern that originally motivated keeping CR and Sales Discount as separate apps at all).

JAMS resolves this by inverting the design: instead of one engine per request type, there is **one engine, and each request type is a row of configuration**. Sales Discount was proven out as JAMS's second Approval Type with **zero new engine code** — only field catalog, routing rules, and picklist data were added (see [TEST_CASES.md](../TEST_CASES.md) §8). This is the pattern intended for every future request type.

## 3. Who Uses It

| Role | What they do in JAMS |
|---|---|
| **Requester** | Creates and submits requests for any Approval Type they've been granted `CanCreate` on; tracks status in "My Work"; responds to Send-Backs; withdraws requests they no longer need. |
| **Approver** | Reviews and decides (Approve / Reject / Send Back) requests routed to them, from the app or with a single click from email. |
| **Admin** | Configures Approval Types, fields, routing rules, user accounts and permissions, email templates and triggers, digest schedules, the external API, and system settings; investigates via the Cockpit logs. |
| **Auditor** | Read-only access to Reports; no ability to create, approve, or configure anything. |
| **External system** | Integrates via the versioned REST API (`/api/v1/approvals`) to create requests and read status/timeline programmatically, authenticated with its own API key. |

## 4. High-Level Architecture

**Stack:** ASP.NET Core 8 MVC, Entity Framework Core, SQL Server, server-rendered Razor views with vanilla JavaScript (no SPA framework) — deliberately simple, deployed as a single Windows Service.

**Single application, single database** (`JACO_Unified`). There is no longer a separate "Approval service" that other apps call over HTTP — the request screens, the routing engine, the admin configuration, and the API all live in one codebase and one database.

### 4.1 The routing/configuration hierarchy

This is the heart of the system — the chain of tables that turns "a user submits a form" into "the right people are asked to approve it, in the right order":

```
ApprovalType            (e.g. "Change Request", "Sales Discount")
  └─ WorkflowVersion     (exactly one IsCurrent version drives live routing)
       └─ RoutingRule    (named, prioritized — checked lowest-priority-first, first full match wins)
            ├─ RoutingRuleCriteria   (field/operator/value conditions — ALL must match)
            └─ WorkflowStep         (one approval level: mode + required count)
                 └─ WorkflowStepApprover  (the specific user(s) at that level)
```

Independently, **`WorkflowField`** is the field catalog for a type — it drives the dynamic create/edit form, the read-only details view, the list of criteria fields available in the Rule Builder, and (via a `LookupType`) which `PicklistValue` rows populate a dropdown. A field with `ApprovalTypeId = null` is generic and shared by every Approval Type (e.g. a common "Priority" field). This single table is *why* adding a new request type needs no new code — the UI has nothing hard-coded about what a "Change Request" or a "Sales Discount" looks like.

### 4.2 The request lifecycle

Every request, of every type, is one row in a single `Requests` table with its business data held as JSON (`DataJson`), tracked through one status field:

```
Draft → Pending → Approved
              ├→ Rejected
              ├→ Sent Back → (creator edits) → Pending (resubmit)
              └→ Withdrawn (by creator, while Pending)
```

Multi-level approval within "Pending" is handled by `WorkflowStep`/`WorkflowStepApprover`/`RequestAction`, supporting four quorum modes per level: **ANY_ONE** (first responder decides), **ALL** (every approver must agree), **MAJORITY**, and **MINIMUM_COUNT** (a configurable N of the assigned approvers). An admin can override and record a decision on behalf of the "correct" approver when needed (fully audited).

### 4.3 Notification and reporting layers sit on top, not inside

The routing engine has no knowledge of email. A separate **Post-Processing Framework (PPF)** listens for lifecycle events (Created, Approved, Rejected, Sent Back, Resubmitted, Nudged, Level Pending, Completed) and, per admin-configured rule, renders a `MailTemplate` and sends it — to the creator, a fixed address, the value of a submitted field (e.g. a resolved branch email), or every current-level approver individually with a personalized one-click Approve/Reject link. A PPF failure is logged but **never blocks or alters the underlying request's status** — email is best-effort, not a lifecycle dependency.

Similarly, **Reports** and the **external API** are read-only/write-through layers over the same `Requests`/`RequestActions`/`AuditLogs` data — no separate tracking tables, no risk of the reporting view drifting from the system of record.

### 4.4 Integration model

- **Inbound:** the external API (`api/v1/approvals`) lets another system create a request and poll or read its status/timeline, authenticated by a per-client API key, independent of the cookie-based UI login.
- **Outbound:** JAMS emails people — approvers, creators, and (for Sales Discount) a branch's account team once a request completes — for downstream manual action (e.g. keying an approved discount into SAP). JAMS does not currently write back into any external system directly; the "Field" recipient mode on PPF rules is the generic mechanism any future type can reuse for its own outbound notification target, with no engine changes.
- **Identity:** cookie-based auth with its own local login (`AccountController`) is fully standalone — no external Portal or SSO dependency in the current deployment. The same authentication infrastructure (shared cookie name, shared Data Protection key ring) that would allow JAMS to participate in single sign-on with sibling JACO apps exists in the code, but is **not** how JAMS is deployed today; see §5 of [SECURITY.md](SECURITY.md) for the distinction.

## 5. Data Model Summary

The full entity list (30 tables) is in [FEATURES.md](FEATURES.md) and the Explore inventory; the ones worth understanding conceptually:

- **Configuration:** `ApprovalType`, `WorkflowVersion`, `WorkflowField`, `PicklistValue`, `RoutingRule` + `RoutingRuleCriteria`, `WorkflowStep` + `WorkflowStepApprover`.
- **Transactional:** `Request`, `RequestAttachment`, `RequestAction`, `WorkflowParticipant` (auto-tracks everyone who's ever touched a request, powering "My Work" without a separate grant), `ApproverReassignment`.
- **Identity & access:** `AppUser`, `UserWorkflowPermission` (per-type Create/View-All grants).
- **Notifications:** `EmailSettings`, `MailTemplate`, `PostProcessingRule`, `PostProcessingExecution`, `DigestSchedule`, `DigestRun` + `DigestRunRecipient`.
- **External API:** `ApiClient`, `ApiSettings`, `ApiRequestLog`.
- **Operations & compliance:** `AuditLog`, `RoutingLogEntry`, `SystemSettings` (Production/Test flag), `LogArchive` (pre-delete snapshot of every purged log row).

## 6. What Makes JAMS Different From the Apps It Replaces

| | Old model (CR / Approval / Sales Discount as separate apps) | JAMS |
|---|---|---|
| New request type | New application, new database, new deployment | New config rows: type, fields, rules, templates |
| Routing logic | Re-implemented or shared via a central service each app calls over HTTP | One engine, one database, no network hop |
| Approver selection UI | Native multi-select (`Ctrl`/`Cmd`+click to deselect) | Chip-based picker with search — the same pattern used for both the simple Rule Builder and the Bulk Rule tool, after two rounds of UAT-driven redesign |
| Bulk rule authoring | Not available — every rule hand-built | "Bulk Rule" wizard: split rules by a field's value (with drill-down into a second field, e.g. a numeric range) and generate/save every resulting rule in one action |
| User & permission onboarding | Manual, one account at a time | CSV bulk import for both routing rules and user accounts, with a validated preview step before anything is written |
| Reporting | App-specific or absent | Type-agnostic Volume / Cycle Time / Approver Workload reports across every Approval Type at once |
| External integration | Ad hoc per app | One versioned, API-key-authenticated REST API, with a live-generated reference page per type |

## 7. Current Deployment

- **Environments:** QA is live and has undergone a full UAT round; Production rollout follows the same procedure (see [DEPLOYMENT_RUNBOOK.md](DEPLOYMENT_RUNBOOK.md)).
- **Approval Types live today:** Change Request (CR), Sales Discount.
- **Branding:** the product is presented to users as **"JAMS — Approval System"** throughout the UI, login page, and email templates (the internal codebase/repo name `JACO.Unified`/`JACO-Unified` predates this branding decision and is retained only as the technical project name).
- **Pricing position** (for reference, from prior market analysis in this project): a per-seat SaaS model was recommended over a flat license, positioned against comparable lightweight approval-workflow tools.

## 8. Where to Go Next

- To operate the system day-to-day as an admin: [ADMIN_GUIDE.md](ADMIN_GUIDE.md).
- For a scannable list of every capability: [FEATURES.md](FEATURES.md).
- For the security posture, what was tested, and what remains a known limitation: [SECURITY.md](SECURITY.md).
- To stand up a new environment or push an update: [DEPLOYMENT_RUNBOOK.md](DEPLOYMENT_RUNBOOK.md).
- For the full regression/verification history of every feature: [TEST_CASES.md](../TEST_CASES.md).
