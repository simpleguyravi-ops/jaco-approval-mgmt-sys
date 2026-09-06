#Requires -RunAsAdministrator
<#
    Applies Phase 7 of DEPLOYMENT_RUNBOOK.md ("Applying a later code change") to the QA
    server: move the checkout to a named tag, run any new migration scripts, republish, and
    restart the JACO-Unified service. Run from an elevated PowerShell ON THE QA SERVER
    ITSELF -- not from dev. Stop-Service/Start-Service need Administrator rights; without
    them Stop-Service fails with a misleadingly generic "cannot open service" error.

    Usage:
        .\Sync-Qa.ps1 -Tag qa-2026-09-04e -SqlInstance "QA-SQL01\INSTANCE01"

    Today's checkpoint for the Rule Builder fixes/AND-OR feature is tag qa-2026-09-04e
    (commit ac5e2e3) -- see Database\011_AddCriteriaLogicalOperator.sql for the one new
    migration it brings.
#>
param(
    [Parameter(Mandatory)] [string]$Tag,
    [Parameter(Mandatory)] [string]$SqlInstance,
    [string]$RepoPath    = "C:\JACO\JAMS",
    [string]$ServiceName = "JACO-Unified",
    [string]$PublishPath = "C:\JACO\_services\Unified",
    [string]$Database    = "JACO_Unified"
)

$ErrorActionPreference = "Stop"

Write-Host "== 1. Stopping $ServiceName ==" -ForegroundColor Cyan
Stop-Service -Name $ServiceName

Write-Host "== 2. Moving $RepoPath to tag $Tag ==" -ForegroundColor Cyan
Push-Location $RepoPath
try {
    git fetch --all --tags
    git checkout $Tag
} finally {
    Pop-Location
}

Write-Host "== 3. Applying migrations newer than what's already run ==" -ForegroundColor Cyan
Write-Host "Checked manually against this sync: Database\011_AddCriteriaLogicalOperator.sql" -ForegroundColor Yellow
Write-Host "If QA is already past 011 (re-running this script for a later tag), skip it and" -ForegroundColor Yellow
Write-Host "run only whatever is numbered higher -- see DEPLOYMENT_RUNBOOK.md Phase 7 step 3." -ForegroundColor Yellow
$migration = Join-Path $RepoPath "Database\011_AddCriteriaLogicalOperator.sql"
if (Test-Path $migration) {
    sqlcmd -S $SqlInstance -d $Database -E -i $migration
} else {
    Write-Host "  011_AddCriteriaLogicalOperator.sql not found at $migration -- already applied by an earlier sync, or wrong tag/path. Confirm before continuing." -ForegroundColor Red
}

Write-Host "== 4. Republishing over $PublishPath ==" -ForegroundColor Cyan
dotnet publish (Join-Path $RepoPath "src\JACO.Unified.Web\JACO.Unified.Web.csproj") -c Release -o $PublishPath

Write-Host "== 5. Starting $ServiceName ==" -ForegroundColor Cyan
Start-Service -Name $ServiceName

Start-Sleep -Seconds 2
Get-Service -Name $ServiceName | Format-Table Name, Status, StartType -AutoSize

Write-Host "Done. Smoke-test: Rule Builder -> open/edit a rule, confirm the AND/OR selector," -ForegroundColor Green
Write-Host "criteria preview, and simulator appear; open Bulk Rule and confirm an approver" -ForegroundColor Green
Write-Host "picker closes on outside click even after adding several values/levels." -ForegroundColor Green
