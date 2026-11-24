#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Removes the NIGHTSHIFT service. Stopping the service ends any active session and restores power settings.
.PARAMETER RemoveData
  Also delete %ProgramData%\Nightshift (session history and recovery data). Refused while a restore is pending.
#>
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'Nightshift'),
    [switch]$RemoveData
)
$ErrorActionPreference = 'Stop'
$name = 'NightshiftService'
$ctl = Join-Path $InstallDir 'nightshiftctl.exe'

$recoveryPending = $false
$svc = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq 'Running' -and (Test-Path $ctl)) {
        try {
            $sys = & $ctl system --json | ConvertFrom-Json
            $recoveryPending = [bool]$sys.service.recoveryPending
        } catch { }
    }
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $name -Force
        (Get-Service $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $name | Out-Host
}

Remove-Item -Recurse -Force -Path $InstallDir -ErrorAction SilentlyContinue

if ($RemoveData) {
    if ($recoveryPending) {
        Write-Warning 'A settings restore was still pending; keeping %ProgramData%\Nightshift so it can be completed. Reinstall to finish it.'
    } else {
        Remove-Item -Recurse -Force -Path (Join-Path $env:ProgramData 'Nightshift') -ErrorAction SilentlyContinue
    }
}
Write-Host 'NIGHTSHIFT service removed.'
