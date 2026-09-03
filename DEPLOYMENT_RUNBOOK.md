# JACO-Unified (JAMS) — QA / Production Deployment Runbook

This is the single, versioned procedure for standing up `JACO-Unified` on a new server.
Written for the **first QA rollout** (2026-09-03); the same phases, unchanged, are what you
run again for **Production go-live** — differences are called out inline as "**Prod note:**".

Scope: JAMS is a **standalone application** — it is not integrated with JACO Portal or any
other JACO app, and does not depend on any of them being installed. It has its own login page
and its own local user accounts; there is no SSO to configure and nothing to register with
another app's database.

Repo: `https://github.com/simpleguyravi-ops/jaco-approval-mgmt-sys.git` (branch `master`).
This runbook targets commit `581723c` and later.

---

## Phase 0 — Discover the target server's real values

Don't assume dev's values (`localhost\MSSQLSERVER01`, port 5004, etc.) are correct on the new
box — check what's actually there:

```powershell
Get-Service | Where-Object { $_.Name -like '*SQL*' }
```

Note down:
- The SQL Server instance name (e.g. `.\SQLEXPRESS`, or `<hostname>\MSSQLSERVER01`) and
  whether you'll connect with Windows auth (`Trusted_Connection=True`, matches dev) or a SQL
  login.
- `sqlcmd.exe`'s path — it's often installed (with SSMS or the SQL Server Client SDK) but not
  on `PATH`. If `sqlcmd -?` fails, look under
  `C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\<version>\Tools\Binn\SQLCMD.EXE`.

Also confirm the port you intend to use is free, and isn't in a Windows-reserved exclusion
range (see Phase 3's note on this):

```powershell
Get-NetTCPConnection -LocalPort 5004 -State Listen -ErrorAction SilentlyContinue
netsh interface ipv4 show excludedportrange protocol=tcp
```

If 5004 is taken or reserved, pick another and use it consistently through every phase below.

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
only right before real users start relying on the environment (see Phase 5).

A brand-new database has zero `AppUsers` rows, so nobody can sign in yet — Phase 5 covers
bootstrapping the first admin account with `tools/SeedAdmin`.

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
| `ConnectionStrings:DefaultConnection` | `Server=<SQL_INSTANCE>;Database=JACO_Unified;Trusted_Connection=True;TrustServerCertificate=True;` (or the SQL-login form, per what Phase 0 found) |
| `AppBaseUrl` | `http://<THIS_SERVER_HOSTNAME>:5004` |
| `SharedAuth:KeyRingPath` | any folder this app can read/write, e.g. `C:\JACO\_shared\dpkeys` — this is JAMS's own private key ring (used to persist data-protection keys, e.g. antiforgery tokens, across restarts); since JAMS is standalone it does **not** need to match any other app's key ring |
| `Attachments:RootPath` | `C:\JACO\_shared\unified-attachments` (create the folder if it doesn't exist) |

**`Urls` vs `AppBaseUrl` — don't confuse them:** `AppBaseUrl` is only used for generating
absolute links (emails, API responses); it does **not** control what Kestrel actually listens
on. Without an explicit `Urls` key (or `ASPNETCORE_URLS`), the app falls back to Kestrel's
built-in default, `http://localhost:5000` — loopback-only, so nothing outside the box can
reach it, and on some Windows Server boxes port 5000 is also a Hyper-V/Docker-reserved port
exclusion, which surfaces as a confusing "access forbidden by its access permissions" crash on
startup rather than a normal "port in use" error. Use `+` (all interfaces), not `localhost`,
so other machines can reach it. If the service won't start, check
`netsh interface ipv4 show excludedportrange protocol=tcp` before assuming it's a permissions
problem.

**Prod note:** once a reverse proxy fronts the app at `https://<domain>/JAMS` (SSL handled
there, per the earlier security review — this app has no TLS logic of its own),
`AppBaseUrl` becomes `https://<domain>/JAMS` and `Program.cs` automatically derives the
correct path base and honors `X-Forwarded-Proto` — no code change needed, only this config
value (`Urls` still binds to plain `http://+:<port>`; the proxy handles TLS in front of it).

---

## Phase 4 — Publish and install as a Windows Service

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

## Phase 5 — Smoke test

**How login works:** JAMS is standalone — there's no SSO, only its own local accounts (backed
by `AppUsers`). A brand-new database has none, so bootstrap the first admin with the repo's
`tools/SeedAdmin` project (safe to re-run; resets the password if the account already exists):
```powershell
cd C:\JACO\JACO-Unified\tools\SeedAdmin
dotnet run -- "Server=<SQL_INSTANCE>;Database=JACO_Unified;Trusted_Connection=True;TrustServerCertificate=True;" admin "Administrator" "<a strong temp password>"
```
Sign in with that account — the app forces a password change on first login. Do this once per
environment (QA, then again for Production), and give the real admin the credentials
out-of-band, not by leaving them in this file or in Slack/email history. Once signed in, add
any further accounts through **Admin → User Accounts** rather than re-running `SeedAdmin`.

Checklist:
- [ ] `http://<server>:5004/Account/Login` loads.
- [ ] Sign in with the `SeedAdmin` account above; forced password-change flow works.
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

## Phase 6 — Repeating this for Production

Same phases, verbatim, with these substitutions:

| Item | QA | Production |
|---|---|---|
| SQL instance | Phase 0's discovered QA value | Phase 0's discovered Prod value (repeat the discovery step there — don't assume it matches QA) |
| `AppBaseUrl` | `http://<qa-host>:5004` | `https://<prod-domain>/JAMS` (once the reverse proxy is live) |
| System Mode | Test until UAT passes | **Production**, switched on at go-live per Phase 5's last step |

TLS/SSL termination is explicitly out of scope for this app (handled at the production
reverse proxy, per the security review already done on this codebase) — nothing in Phases
1–5 changes for that; only `AppBaseUrl` needs to reflect the real public URL once it's live.
