#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Installs (or upgrades) the NIGHTSHIFT background service. Phase 2 developer installer; the MSI/MSIX
  installer is Phase 6.

.PARAMETER Source
  Folder produced by: dotnet publish src/Nightshift.Service -c Release -r win-x64 --self-contained -o out/service

.PARAMETER NoPrivilegeRestriction
  Skip 'sc.exe privs'. By default the service token is reduced to the privileges it needs.
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\out\service'),
    [string]$CtlSource = (Join-Path $PSScriptRoot '..\out\ctl'),
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'Nightshift'),
    [switch]$NoPrivilegeRestriction
)
$ErrorActionPreference = 'Stop'
$name = 'NightshiftService'

function Invoke-Sc([string[]]$scArgs) {
    & sc.exe @scArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe $($scArgs -join ' ') failed with $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $Source 'Nightshift.Service.exe'))) {
    throw "Nightshift.Service.exe not found in '$Source'. Publish the service first (see docs/TESTING.md)."
}

$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host 'Stopping the running service (an active session is ended and its settings restored)...'
    Stop-Service -Name $name -Force
    (Get-Service $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

$serviceDir = Join-Path $InstallDir 'Service'
New-Item -ItemType Directory -Force -Path $serviceDir | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $serviceDir -Recurse -Force
if (Test-Path $CtlSource) {
    Copy-Item -Path (Join-Path $CtlSource '*') -Destination $InstallDir -Recurse -Force
}
$exe = Join-Path $serviceDir 'Nightshift.Service.exe'

if (-not $existing) {
    Invoke-Sc @('create', $name, "binPath=", "`"$exe`"", 'start=', 'auto', 'obj=', 'LocalSystem', 'DisplayName=', 'NIGHTSHIFT Session Service')
} else {
    Invoke-Sc @('config', $name, "binPath=", "`"$exe`"", 'start=', 'auto', 'obj=', 'LocalSystem')
}
Invoke-Sc @('description', $name, 'Keeps selected work running with the laptop lid closed and restores power settings afterwards.')
# Restart after a crash (5 s, 15 s, 60 s); the failure counter resets after one day. Startup recovery restores settings.
Invoke-Sc @('failure', $name, 'reset=', '86400', 'actions=', 'restart/5000/restart/15000/restart/60000')
Invoke-Sc @('failureflag', $name, '1')
Invoke-Sc @('sidtype', $name, 'unrestricted')
if (-not $NoPrivilegeRestriction) {
    # Least privilege: drop every LocalSystem privilege except these.
    Invoke-Sc @('privs', $name, 'SeChangeNotifyPrivilege/SeImpersonatePrivilege/SeCreateGlobalPrivilege')
}

try {
    if (-not [System.Diagnostics.EventLog]::SourceExists($name)) {
        [System.Diagnostics.EventLog]::CreateEventSource($name, 'Application')
    }
} catch {
    Write-Warning "Could not register the event log source: $_"
}

Start-Service -Name $name
(Get-Service $name).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "NIGHTSHIFT service installed and running. Data: $env:ProgramData\Nightshift. Client: $InstallDir\nightshiftctl.exe"
