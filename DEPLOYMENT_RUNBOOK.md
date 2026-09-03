# JACO-Unified (JAMS) — QA / Production Deployment Runbook

This is the single, versioned procedure for standing up `JACO-Unified` on a new server.
Written for the **first QA rollout** (2026-09-03); the same phases, unchanged, are what you
run again for **Production go-live** — differences are called out inline as "**Prod note:**".

Scope: this app only. Portal / CR / Approval / SalesDiscount are assumed already installed
and running on the target server (true for QA as of this writing) — this runbook only adds
Unified alongside them and registers it with the existing Portal for SSO.

Repo: `https://github.com/simpleguyravi-ops/jaco-approval-mgmt-sys.git` (branch `master`).
This runbook targets commit `581723c` and later.

---

## Phase 0 — Discover the target server's real values

Don't assume dev's values (`localhost\MSSQLSERVER01`, port 5004, etc.) are correct on the
new box. The other JACO apps are already running there — the fastest, most reliable way to
learn the server's real SQL instance name, shared key-ring path, and auth mode is to read
**their** already-working config rather than guess:

```powershell
Get-Content "C:\JACO\_services\Portal\appsettings.json"
```

Note down:
- The `ConnectionStrings:DefaultConnection` → `Server=` value (SQL instance name) and whether
  it uses `Trusted_Connection=True` (Windows auth, matches dev) or a SQL login.
- The `SharedAuth:KeyRingPath` value — Unified **must** point at the exact same folder, or SSO
  breaks (a different key ring can't decrypt the other apps' `.JACO.Auth` cookie).

Also confirm the port you intend to use is free:

```powershell
Get-NetTCPConnection -LocalPort 5004 -State Listen -ErrorAction SilentlyContinue
```

If 5004 is taken, pick another and use it consistently through every phase below.

---

## Phase 1 — Get the code onto the server

**Option A — GitHub Desktop** (already installed and authenticated as `simpleguyravi-ops` on
QA):
1. File → Clone Repository → select `simpleguyravi-ops/jaco-approval-mgmt-sys`.
2. Clone to `C:\JACO\JACO-Unified` (matches dev's path — keeps every later command in this
   runbook copy-pasteable without adjustment).

**Option B — git CLI** (e.g. from a Claude Code session running on the target server):
```powershell
git clone https://github.com/simpleguyravi-ops/jaco-approval-mgmt-sys.git C:\JACO\JACO-Unified
```

Verify you're on the expected commit:
```powershell
cd C:\JACO\JACO-Unified
git log --oneline -1
```

---

## Phase 2 — Create and migrate the database

Using the SQL instance name found in Phase 0 (shown here as `<SQL_INSTANCE>`), run every
migration **in order** — each one depends on the schema/data the previous ones created —
**except `002_SeedCR.sql`**, which is dev-only. `001_CreateJACO_Unified.sql` creates the
database itself (`CREATE DATABASE` + `USE`), so it must connect to `master`; everything after
it connects to `JACO_Unified` directly:

```powershell
cd C:\JACO\JACO-Unified\Database
foreach ($f in Get-ChildItem *.sql | Sort-Object Name) {
    if ($f.Name -eq "002_SeedCR.sql") {
        Write-Host "Skipping $($f.Name) (dev-only seed)" -ForegroundColor Yellow
        continue
    }
    Write-Host "Applying $($f.Name)..." -ForegroundColor Cyan
    if ($f.Name -eq "001_CreateJACO_Unified.sql") {
        sqlcmd -S "<SQL_INSTANCE>" -d master -E -i $f.FullName
    } else {
        sqlcmd -S "<SQL_INSTANCE>" -d JACO_Unified -E -i $f.FullName
    }
}
```

**Why skip `002_SeedCR.sql`:** its own header says it's dev-only — it wires a default CR
routing chain to fake accounts ("Dominic"/"Wayne"/etc.) that don't exist in a real environment.
`003_SeedApprovalCatalog.sql` is the correct seed for Test/Production: the same CR catalog
(fields, picklists) but no fake users or routing. Running both back-to-back fails with a
duplicate-key error on `ApprovalTypes` (protected by a unique index) but still leaves **duplicate**
`WorkflowFields`/`WorkflowVersions`/`PicklistValues` rows behind, because those inserts sit in
later `GO` batches that run independently and aren't protected by a unique index. If this
happens, don't try to patch out the duplicates by hand — drop and recreate the database and
re-run the loop above correctly:
```powershell
sqlcmd -S "<SQL_INSTANCE>" -E -Q "ALTER DATABASE JACO_Unified SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE JACO_Unified;"
```

Verify the full table set landed (should be 25+ tables, including `LogArchives` and
`SystemSettings`):
```powershell
sqlcmd -S "<SQL_INSTANCE>" -d JACO_Unified -E -Q "SELECT COUNT(*) AS TableCount FROM sys.tables;"
```

`010_AddSystemSettings.sql` seeds `SystemSettings` with `IsProduction = 0` (Test mode) —
correct default for UAT. **Leave it in Test through the whole QA/UAT cycle** so the Clear-Log
retention floor doesn't block testers clearing their own test data; switch it to Production
only right before real users start relying on the environment (see Phase 6).

No admin user needs seeding here **if Portal SSO is already live on this server** — see
Phase 6, "How login works." If Portal isn't installed here yet (see Phase 4), you'll need
`tools/SeedAdmin` before you can sign in at all — also covered in Phase 6.

---

## Phase 3 — Configure `appsettings.json` for this server

Publish first (next phase creates the file), then edit the **published copy** —
`C:\JACO\_services\Unified\appsettings.json` — not the source-controlled one in the repo
(this file is intentionally environment-specific and not meant to be committed with real
server values in it).

Fields to set, using the values from Phase 0/2:

| Key | Value |
|---|---|
| `Urls` | `http://+:5004` (or whatever port Phase 0 confirmed is free) — **required**, see note below |
| `ConnectionStrings:DefaultConnection` | `Server=<SQL_INSTANCE>;Database=JACO_Unified;Trusted_Connection=True;TrustServerCertificate=True;` (or the SQL-login form if that's what Portal uses) |
| `AppBaseUrl` | `http://<THIS_SERVER_HOSTNAME>:5004` |
| `SharedAuth:KeyRingPath` | **exactly** the same path found in Phase 0 (do not leave as `C:\JACO\_shared\dpkeys` unless that's genuinely what the other apps use on this server) |
| `Attachments:RootPath` | `C:\JACO\_shared\unified-attachments` (fine as-is if this convention already exists on the server; create the folder if not) |

**`Urls` vs `AppBaseUrl` — don't confuse them:** `AppBaseUrl` is only used for generating
absolute links (emails, API responses); it does **not** control what Kestrel actually listens
on. Without an explicit `Urls` key (or `ASPNETCORE_URLS`), the app falls back to Kestrel's
built-in default, `http://localhost:5000` — loopback-only, so nothing outside the box (not
even Portal) can reach it, and on some Windows Server boxes port 5000 is also a
Hyper-V/Docker-reserved port exclusion, which surfaces as a confusing "access forbidden by
its access permissions" crash on startup rather than a normal "port in use" error. Use `+`
(all interfaces), not `localhost`, so other servers can reach it. If the service won't start,
check `netsh interface ipv4 show excludedportrange protocol=tcp` before assuming it's a
permissions problem.

**Prod note:** once a reverse proxy fronts the apps at `https://<domain>/JAMS` (SSL handled
there, per the earlier security review — this app has no TLS logic of its own),
`AppBaseUrl` becomes `https://<domain>/JAMS` and `Program.cs` automatically derives the
correct path base and honors `X-Forwarded-Proto` — no code change needed, only this config
value (`Urls` still binds to plain `http://+:<port>`; the proxy handles TLS in front of it).

---

## Phase 4 — Register the app with Portal (so it shows up on My Apps)

**Skip this phase if `JACO_Portal` doesn't exist on this server yet** (i.e. Portal itself
hasn't been deployed here — this runbook's "Scope" assumption doesn't hold). There's nothing
for the `INSERT` below to write into, and Portal's own schema is out of scope for this repo.
Unified runs standalone in the meantime (local login only — see Phase 6's `tools/SeedAdmin`
note); come back and do this phase once Portal is actually installed on this box.

Check first — don't insert a duplicate if a previous attempt already added it:
```powershell
sqlcmd -S "<SQL_INSTANCE>" -d JACO_Portal -E -Q "SELECT * FROM dbo.Applications WHERE Code = 'UNIFIED';"
```

If nothing comes back:
```powershell
sqlcmd -S "<SQL_INSTANCE>" -d JACO_Portal -E -Q "INSERT INTO dbo.Applications (Code, Name, Description, BaseUrl, IconKey, IsActive, SortOrder) VALUES ('UNIFIED', 'Unified Requests', 'Single application for every request/approval type.', 'http://<THIS_SERVER_HOSTNAME>:5004/', 'layers', 1, 4);"
```

(This is the exact row already live on dev's Portal — `BaseUrl` is the only value that
changes per environment.)

---

## Phase 5 — Publish and install as a Windows Service

Dev intentionally runs Unified as a plain background process (fast iterate/rebuild without
admin elevation on every change — see `jaco-dev-environment` notes). QA and Production
should **not** use that pattern — they need auto-start on reboot and auto-restart on crash,
which only a real Windows Service gives you. The repo has a script for exactly this at
[`deploy/Install-JacoUnifiedService.ps1`](deploy/Install-JacoUnifiedService.ps1).

1. **Publish** (Release, not Debug — Debug is a dev-only convention):
   ```powershell
   dotnet publish C:\JACO\JACO-Unified\src\JACO.Unified.Web\JACO.Unified.Web.csproj -c Release -o C:\JACO\_services\Unified
   ```
   **Before running the published exe or installing the service, verify the target framework
   the publish just produced is actually installed on this box** — `dotnet publish` will
   happily build against a framework version (e.g. `Microsoft.NETCore.App 8.0.30`) that isn't
   present, and the failure only shows up later, at service-start time, as a confusing crash:
   ```powershell
   dotnet --list-runtimes
   ```
   If the needed `Microsoft.NETCore.App` version is missing (this can happen even when the
   matching `Microsoft.AspNetCore.App` version *is* present — they can drift out of sync on a
   box that's had partial updates), install it before continuing:
   ```powershell
   Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile "$env:TEMP\dotnet-install.ps1"
   & "$env:TEMP\dotnet-install.ps1" -Channel 8.0 -Runtime dotnet -InstallDir "C:\Program Files\dotnet"
   ```
   (Run this as Administrator. If it fails with a file-lock error on `dotnet.exe`, a leftover
   MSBuild build-server process from the `dotnet publish` above is holding it open — run
   `dotnet build-server shutdown` first, then retry.)
2. **Edit `appsettings.json`** in that output folder per Phase 3.
3. **Install the service** (run as Administrator):
   ```powershell
   C:\JACO\JACO-Unified\deploy\Install-JacoUnifiedService.ps1
   ```
   Before running it, edit the `$SqlInstance` and `$Port` variables at the top of the script
   to match Phase 0's discovered values (defaults to `5004`). The script:
   - Grants the service account (`NT AUTHORITY\SYSTEM`) `db_owner` on `JACO_Unified`.
   - Stops anything already listening on the configured port.
   - Creates the `JACO Unified` Event Log source.
   - Registers/starts a Windows Service named `JACO-Unified`, set to auto-restart on
     failure (3 retries, 5s apart) and start automatically on boot.
   - Is safe to re-run — it stops and replaces an existing `JACO-Unified` service rather than
     failing on one.

   This script itself needs an elevated shell; if you're driving this from a non-elevated
   session (e.g. Claude Code running as a normal user), launch it via
   `Start-Process powershell -Verb RunAs -Wait` rather than calling it directly.

**Re-deploying a later code change** (once QA/Prod is live) is just: stop the service, publish
over the same folder, start the service again — no need to re-run the installer:
```powershell
Stop-Service JACO-Unified
dotnet publish C:\JACO\JACO-Unified\src\JACO.Unified.Web\JACO.Unified.Web.csproj -c Release -o C:\JACO\_services\Unified
Start-Service JACO-Unified
```

---

## Phase 6 — Smoke test

**How login works:** Unified has its own login page but recognizes the same `.JACO.Auth`
SSO cookie the other apps issue (same key ring, Phase 0/3) — anyone already signed into
Portal/CR/Approval/SalesDiscount on this server lands in Unified already authenticated, no
second login. A user's first visit auto-provisions their local `AppUser` row; admin access
follows the same `PORTAL_ADMIN`/`SYSTEM_ADMIN`/`UNIFIED_ADMIN` role claims already governing
the other apps — nothing to seed manually.

**If Phase 4 was skipped (no Portal on this box yet), none of that applies** — there's no SSO
cookie and no existing account to log in with. Bootstrap a local admin instead using the
repo's `tools/SeedAdmin` project (safe to re-run; resets the password if the account already
exists):
```powershell
cd C:\JACO\JACO-Unified\tools\SeedAdmin
dotnet run -- "Server=<SQL_INSTANCE>;Database=JACO_Unified;Trusted_Connection=True;TrustServerCertificate=True;" admin "Administrator" "<a strong temp password>"
```
Sign in with that account — the app forces a password change on first login. Do this once per
environment (QA, then again for Production), and give the real admin the credentials
out-of-band, not by leaving them in this file or in Slack/email history.

Checklist:
- [ ] `http://<server>:5004/Account/Login` loads.
- [ ] **If Portal is registered (Phase 4 done):** signing into Portal first, then opening
  Unified, lands you in already authenticated. **Otherwise:** sign in with the `SeedAdmin`
  account above.
- [ ] **If Portal is registered:** "Unified Requests" tile appears on this server's
  Portal → My Apps.
- [ ] **Admin → System Mode** shows **Test** (confirms Phase 2's seed took effect correctly).
- [ ] Raise a test Change Request end-to-end: Create → Submit → routes to an approver →
  Approve → Completed.
- [ ] **Admin → API Access → API Reference** → "Download API Specification (.md)" produces a
  file with real field data.
- [ ] **Cockpit → Clear Routing Log**: clear a small batch, confirm it appears under
  **Archived Clears** with a working Download link.
- [ ] Run the relevant sections of [TEST_CASES.md](TEST_CASES.md) — this is the project's
  standing regression checklist; a new environment deserves the same discipline as a code
  change.

**Only after UAT sign-off**, before real users depend on the data: **Admin → System Mode →
Production**. This is what actually turns on the Clear-Log 90-day retention floor — it does
not move or change any existing data.

---

## Phase 7 — Repeating this for Production

Same seven phases, verbatim, with these substitutions:

| Item | QA | Production |
|---|---|---|
| SQL instance | Phase 0's discovered QA value | Phase 0's discovered Prod value (repeat the discovery step there — don't assume it matches QA) |
| `AppBaseUrl` | `http://<qa-host>:5004` | `https://<prod-domain>/JAMS` (once the reverse proxy is live) |
| Portal `Applications.BaseUrl` | QA host | Prod domain, same path |
| System Mode | Test until UAT passes | **Production**, switched on at go-live per Phase 6's last step |

TLS/SSL termination is explicitly out of scope for this app (handled at the production
reverse proxy, per the security review already done on this codebase) — nothing in Phases
1–6 changes for that; only `AppBaseUrl` needs to reflect the real public URL once it's live.
