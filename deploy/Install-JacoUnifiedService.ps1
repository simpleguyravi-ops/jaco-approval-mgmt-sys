#Requires -RunAsAdministrator
<#
    Installs JACO-Unified as a Windows Service. Run from an elevated PowerShell prompt
    after Phase 3's dotnet publish + appsettings.json edit (DEPLOYMENT_RUNBOOK.md Phase 4).
    Safe to re-run: an existing "JACO-Unified" service is stopped and replaced.
#>

$ServiceName = "JACO-Unified"
$DisplayName = "JACO Unified"
$BinPath     = "C:\JACO\_services\Unified\JACO.Unified.Web.exe"
$SqlInstance = "<SQL_INSTANCE>"   # from Phase 0 — edit before running
$Database    = "JACO_Unified"
$Port        = 5004               # from Phase 0 — edit if 5004 is taken
$EventSource = "JACO Unified"

# Locate sqlcmd.exe: it's usually not on PATH even when SSMS/ODBC tools are installed.
$sqlcmd = (Get-Command sqlcmd.exe -ErrorAction SilentlyContinue).Source
if (-not $sqlcmd) {
    $sqlcmd = Get-ChildItem "C:\Program Files\Microsoft SQL Server" -Recurse -Filter "sqlcmd.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $sqlcmd) { throw "sqlcmd.exe not found. Install the SQL Server command line tools / ODBC Client SDK." }

Write-Host "Granting NT AUTHORITY\SYSTEM db_owner on $Database..." -ForegroundColor Cyan
& $sqlcmd -S $SqlInstance -d $Database -E -Q @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'NT AUTHORITY\SYSTEM')
BEGIN
    CREATE USER [NT AUTHORITY\SYSTEM] FOR LOGIN [NT AUTHORITY\SYSTEM];
END
ALTER ROLE db_owner ADD MEMBER [NT AUTHORITY\SYSTEM];
"@

Write-Host "Stopping anything listening on port $Port..." -ForegroundColor Cyan
$conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
foreach ($c in $conns) {
    Write-Host "  Stopping process $($c.OwningProcess)"
    Stop-Process -Id $c.OwningProcess -Force -ErrorAction SilentlyContinue
}

Write-Host "Creating '$EventSource' Event Log source..." -ForegroundColor Cyan
if (-not [System.Diagnostics.EventLog]::SourceExists($EventSource)) {
    New-EventLog -LogName Application -Source $EventSource
} else {
    Write-Host "  Source already exists, skipping."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists -- stopping and removing before reinstall..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Write-Host "Registering service '$ServiceName'..." -ForegroundColor Cyan
New-Service -Name $ServiceName `
    -BinaryPathName $BinPath `
    -DisplayName $DisplayName `
    -Description "JACO Unified Requests application (JAMS)" `
    -StartupType Automatic | Out-Null

# 3 restarts, 5s apart, reset failure counter after 1 day
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host "Starting service '$ServiceName'..." -ForegroundColor Cyan
Start-Service -Name $ServiceName

Start-Sleep -Seconds 2
Get-Service -Name $ServiceName | Format-Table Name, Status, StartType -AutoSize
