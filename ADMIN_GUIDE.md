# JAMS — Admin Guide

**Audience:** Administrators configuring and operating JAMS day-to-day.
**Companion documents:** [PRODUCT_DOCUMENTATION.md](PRODUCT_DOCUMENTATION.md) · [FEATURES.md](FEATURES.md) · [SECURITY.md](SECURITY.md)

All admin screens live under **Administration** in the top navigation, and require the Admin role unless noted. Every list screen in JAMS supports column sorting (click a header) and most support CSV export (a "Download CSV" button) and, where relevant, stat-card filtering (click a summary tile to filter the list to it).

---

## 1. Setting Up a New Approval Type

This is the core admin workflow — it's how JAMS gains a new kind of request without any code change.

1. **Approval Types → + New Type.** Give it a name and a unique Code (e.g. `SALES_DISCOUNT`). This automatically creates the type's first Workflow Version.
2. **Workflow Fields.** Add every field the request form should capture: a key (used internally and in the API), a label (shown to users), a data type (text / number / currency / date / dropdown / textarea), and flags:
   - **Required** — form won't submit without it.
   - **Sensitive** — hidden from the request's own creator on the Details view (use for figures like cost price/margin that an approver should see but a requester should not).
   - **Include in API** — whether external systems can read/write this field; can be turned off per field even if it's visible in the UI.
   - **Lookup Type** — for Dropdown fields, which Picklist group of values populates the dropdown.
   - Leave **Approval Type** blank on a field to make it generic — shared by every type instead of just this one.
3. **Picklist Values.** If any field is a dropdown, add its allowed values here under the matching Lookup Type. Values can carry extra data (e.g. a branch's account email) via the Extra Data column.
4. **Rule Builder → + New Rule** (see §2 below) to define who approves requests of this type, and under what conditions.
5. **Users & Roles.** Grant the users/groups who should be able to create or view-all requests of this type (see §5).
6. **Mail Templates / Post-Processing Rules** (optional but expected). Configure what emails go out and to whom as the request moves through its lifecycle (see §6–7).

Once these are in place, the standard Requests screens, Reports, and the external API all work for the new type automatically — nothing else to build.

## 2. Rule Builder (Routing Rules)

**Administration → Rule Builder**, per Approval Type (switch types via the tabs at the top).

Rules are checked **in priority order, lowest first**; the first rule whose criteria **all** match wins. A rule with no criteria matches everything, so it's typically used as the lowest-priority "default" fallback.

### 2.1 Creating one rule at a time

**+ New Rule** opens the rule form:
- **Rule Name**, **Priority** (lower runs first), **Active** toggle.
- **Criteria** — add condition rows (field / operator / value). Leave a row's field blank to skip it. Supported operators include `=`, `!=`, contains, starts with, ends with, "in" a list, and numeric/date comparisons (`>`, `>=`, `<`, `<=`).
- **Levels & Approvers** — set how many approval levels this rule has (up to 5), and for each level:
  - **Mode**: `ANY_ONE` (first decision wins), `ALL` (everyone must approve), `MAJORITY`, or `MINIMUM_COUNT` (set a specific required number).
  - **Approvers** — click into the search box to filter by name or department, click a name to add them as a chip; click a chip's **×** to remove them. (This replaced an earlier native multi-select control that required Ctrl/Cmd+click to deselect and was reported as confusing during UAT.)

### 2.2 Bulk Rule (building many rules at once)

**Rule Builder → Bulk Rule** is for the common case of "the same approval structure, but a different approver per branch/region/category" — instead of hand-building one rule per value:

1. Choose which field to **split by** (e.g. Branch). JAMS shows one card per distinct value that field can take.
2. For each value's card, either:
   - Set its approver levels directly, **or**
   - Turn on **"drill into this value further"** to split that value again by a second field (typically a numeric range, e.g. discount %), giving different approvers for different bands within that value.
3. Click **Save** — JAMS generates and saves one ordinary routing rule per resulting group/band automatically. These behave exactly like any manually created rule afterward (they appear in the normal Rule Builder list and can be edited individually).

This screen went through three rounds of UI redesign based on direct usability feedback before being built — it deliberately avoids "matrix"/"axis" terminology in favor of plain-language "split by" and live preview cards.

### 2.3 Bulk Import (CSV)

**Rule Builder → Bulk Import** lets you upload a spreadsheet of rules instead of using the screens above — useful for a large initial matrix (e.g. "5 branches × 3 companies × 5 approval levels").

1. **Download Template** to get a correctly-headed sample CSV.
2. Fill in one row per rule (criteria columns + up to 5 levels' approver columns; approvers are matched by user/email).
3. Upload the file — JAMS shows a **preview** of every row, flagging any errors (unknown field, unresolved approver, bad range) **before writing anything**.
4. Choose **Replace All** (wipes existing rules for this type and inserts the file's rows) or **Upsert** (matches existing rules by name, updates in place, adds new ones, leaves everything else untouched).
5. Confirm. Nothing is written until this step — the preview step is purely a dry run.

## 3. Mail Templates & Post-Processing Rules

**Mail Templates** are the reusable HTML bodies used for both automatic lifecycle emails and digests.
- Create/Edit a template's Name, Subject, Body (with `{{FieldKey}}`-style merge tokens), and whether it's a single-record or table-style (digest) template.
- **Preview** renders sample merged output right in the browser before you rely on it.
- **Copy** duplicates an existing template as a starting point for a new one.

**Post-Processing Rules** ("PPF rules") decide *when* an email goes out and *to whom*, per Approval Type:
- **Event**: Created, Resubmitted, Approved, Rejected, Sent Back, Nudged, Level Pending, or Completed.
- **Template**: which Mail Template to send.
- **To / Cc**: choose a recipient mode — **Creator**, a **Fixed** address, the value of a submitted **Field** (e.g. a resolved branch email), or **Current Approver(s)** (sends individually to everyone eligible to decide right now, each with their own one-click Approve/Reject link). Cc supports the same modes and accepts multiple addresses.
- **Sequence** and **Active** control ordering and whether the rule fires at all.

A failed or skipped send is logged (see §9 PPF Monitor) but never blocks the underlying request.

## 4. Digest Schedule

**Administration → Digest**, per Approval Type:
- **Schedule** — configure an automatic recurring email to every user who currently has at least one item awaiting their decision: **Every N Days** or specific **Weekdays**, a start time, and which template to use.
- **Run Now** — manually fires the same digest logic immediately, without waiting for the schedule (useful for testing a new schedule or template).
- The **manual digest picker** (Digest → Index) sends a one-off digest to a single chosen recipient — handy for a spot-check or an ad hoc reminder.
- Every send (scheduled or manual) is recorded in the **Digest Log** (Cockpit), including the exact rendered content per recipient.

> The schedule only fires while the JAMS Windows Service is running continuously — it is not a separate always-on scheduler process. If the service restarts near a scheduled time, verify the next run in the Digest Log.

## 5. Users, Accounts & Permissions

Two separate screens, two separate concerns — don't confuse them:

**User Accounts** (Administration → User Accounts) — *who can log in*:
- Create/Edit an account: name, email, department, Admin/Auditor flags, and password (min 8 characters; only replaced if you type a new one).
- **Unlock** clears an account's lockout after too many failed logins.
- **Bulk Import** — upload a CSV to onboard many users at once, including their per-type Create/View grants in the same file. New accounts always get a random, JAMS-generated password (never one from the CSV) — shown once, immediately after import, in a one-time reveal banner. Save these before navigating away; they cannot be viewed again.

**Users & Roles** (Administration → Users & Roles) — *what a logged-in user can do*, per Approval Type:
- A grid of active users against two checkboxes per Approval Type: **Can Create** and **Can View All** ("Display All" — see every request of that type, not just their own). Save writes an audited before/after change record.

## 6. API Access

**Administration → API Clients** (only relevant if an external system needs to create or read requests programmatically):
- **Master switch**: enable/disable the whole external API layer, and toggle whether every call is logged.
- **Create** a new client — the plaintext API key is shown **once**, immediately after creation. Store it securely; it cannot be retrieved again (only rotated).
- **Regenerate** rotates a client's key (invalidates the old one immediately).
- **Toggle Active** to suspend a client without deleting it.
- **Reference** — a live, auto-generated documentation page per Approval Type, showing the exact field schema and an example payload — always in sync with the real `WorkflowField` configuration, so it can't drift out of date.
- **Download API Spec** produces a downloadable Markdown spec file for a type, for handing to an external integration team.

## 7. Email (SMTP) Settings

**Administration → Email Settings** — configure the outgoing mail server (host, port, TLS, from-address, credentials) directly in the app; takes effect immediately, no redeploy needed. The password is encrypted at rest and only replaced if you type a new one. **Send Test** verifies the configuration end-to-end before relying on it.

## 8. System Settings

**Administration → System Settings** — a single **Production / Test** toggle. This exists specifically to gate compliance guardrails: when set to Production, the Cockpit's "Clear Old Entries" actions enforce a hard, server-side 90-day minimum retention floor (see [SECURITY.md](SECURITY.md) §Compliance). Leave this as **Test** until UAT sign-off is complete on a new environment; switch to **Production** only as the final step before go-live (see Phase 5 of [DEPLOYMENT_RUNBOOK.md](DEPLOYMENT_RUNBOOK.md)).

## 9. Cockpit — Operations & Compliance Console

**Administration → Cockpit** is the admin's investigation and housekeeping console, with several logs:

| Log | What it shows |
|---|---|
| **Routing Log** | Every routing attempt at submit time — matched, unmatched, or misconfigured (e.g. a type with no matching rule) |
| **Audit Log** | Every business action: decisions, logins, permission changes, reassignments, etc. Filter to "Admin override only" to see every case where an admin decided on someone else's behalf |
| **API Request Log** | Raw HTTP-level trail of every external API call, successful or rejected, including rejected-auth attempts |
| **Digest Log** | Every digest run (scheduled or manual) with per-recipient outcome and the exact content sent |

The separate **PPF Monitor** (Home → PPF Monitor, admin-only) shows every attempted notification email — sent, failed, or skipped, with the reason — across every request, independent of the Digest/PPF-rule logs above.

**Clearing old entries:** each log has a **Clear Old Entries** action — pick a cutoff date, preview what will be removed, then confirm. Every row is **archived to a full JSON snapshot before deletion** (visible under **Archived Clears**, downloadable), and a server-side 90-day minimum retention floor is enforced whenever System Settings is in Production mode — the UI cannot be used to delete anything more recent than that floor in Production, regardless of what date is picked. This protects against an admin mistake; it is **not** a substitute for regular SQL Server backups, which remain the actual disaster-recovery layer.

## 10. Reports

**Reports** (Admin or Auditor role) — read-only, type-agnostic analytics computed from live request data, no separate tracking needed:
- **Volume** — counts by type/status over a date range.
- **Cycle Time** — average/median/min/max hours to completion by type (measured from the request's latest submission, not its draft creation).
- **Approvers** — per-approver decision counts, average decision time, and current pending workload.

All three support the same date-range filtering, sorting, and CSV export as every other list in the system.

## 11. Handling a Stuck or Misrouted Request

- **No rule matched at submission** → check the **Routing Log** for the specific criteria that failed to match; add or correct a rule in the Rule Builder.
- **Wrong approver assigned** → use **Reassign** (single request) or **Bulk Reassign** (many at once) from the Requests admin actions — both are fully audited.
- **Approver forgot to act** → the requester can **Nudge** them (cooldown-limited) from Request Details; you can also manually trigger a Digest **Run Now** for that type.
- **Someone needs to decide on another's behalf** → use the admin override on the Decide screen; this is recorded in the Audit Log and visible in the "Admin override only" filter for later review.
