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

Using the SQL instance name found in Phase 0 (shown here as `<SQL_INSTANCE>`):

```powershell
sqlcmd -S "<SQL_INSTANCE>" -E -Q "CREATE DATABASE JACO_Unified;"
```

Run every migration **in order** — each one depends on the schema/data the previous ones
created:

```powershell
cd C:\JACO\JACO-Unified\Database
foreach ($f in Get-ChildItem *.sql | Sort-Object Name) {
    Write-Host "Applying $($f.Name)..." -ForegroundColor Cyan
    sqlcmd -S "<SQL_INSTANCE>" -d JACO_Unified -E -i $f.FullName
}
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

No admin user needs seeding here — see Phase 6, "How login works."

---

## Phase 3 — Configure `appsettings.json` for this server

Publish first (next phase creates the file), then edit the **published copy** —
`C:\JACO\_services\Unified\appsettings.json` — not the source-controlled one in the repo
(this file is intentionally environment-specific and not meant to be committed with real
server values in it).

Fields to set, using the values from Phase 0/2:

| Key | Value |
|---|---|
| `ConnectionStrings:DefaultConnection` | `Server=<SQL_INSTANCE>;Database=JACO_Unified;Trusted_Connection=True;TrustServerCertificate=True;` (or the SQL-login form if that's what Portal uses) |
| `AppBaseUrl` | `http://<THIS_SERVER_HOSTNAME>:5004` |
| `SharedAuth:KeyRingPath` | **exactly** the same path found in Phase 0 (do not leave as `C:\JACO\_shared\dpkeys` unless that's genuinely what the other apps use on this server) |
| `Attachments:RootPath` | `C:\JACO\_shared\unified-attachments` (fine as-is if this convention already exists on the server; create the folder if not) |

**Prod note:** once a reverse proxy fronts the apps at `https://<domain>/JAMS` (SSL handled
there, per the earlier security review — this app has no TLS logic of its own),
`AppBaseUrl` becomes `https://<domain>/JAMS` and `Program.cs` automatically derives the
correct path base and honors `X-Forwarded-Proto` — no code change needed, only this config
value.

---

## Phase 4 — Register the app with Portal (so it shows up on My Apps)

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
which only a real Windows Service gives you. The repo already has a script for exactly this
(`Install-JacoUnifiedService.ps1`, written for dev but never actually used there since dev
reverted to the plain-process pattern — this is its first real use).

1. **Publish** (Release, not Debug — Debug is a dev-only convention):
   ```powershell
   dotnet publish C:\JACO\JACO-Unified\src\JACO.Unified.Web\JACO.Unified.Web.csproj -c Release -o C:\JACO\_services\Unified
   ```
2. **Edit `appsettings.json`** in that output folder per Phase 3.
3. **Install the service** (run as Administrator):
   ```powershell
   C:\JACO\_services\Install-JacoUnifiedService.ps1
   ```
   This script (unchanged from what's already in the repo):
   - Grants the service account (`NT AUTHORITY\SYSTEM`) `db_owner` on `JACO_Unified`.
   - Stops anything already listening on port 5004.
   - Creates the `JACO Unified` Event Log source.
   - Registers/starts a Windows Service named `JACO-Unified`, set to auto-restart on
     failure (3 retries, 5s apart) and start automatically on boot.

   If the port differs from 5004 (Phase 0), edit the `$Port` variable at the top of the
   script before running it.

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

Checklist:
- [ ] `http://<server>:5004/Account/Login` loads.
- [ ] Signing into Portal first, then opening Unified, lands you in already authenticated.
- [ ] "Unified Requests" tile appears on this server's Portal → My Apps.
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
