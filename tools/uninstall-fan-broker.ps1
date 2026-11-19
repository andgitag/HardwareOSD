#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Explicitly remove the optional HardwareOSD fan broker service.
.DESCRIPTION
    Stops and deletes the service and removes its Program Files directory. It does not remove the
    HardwareOSD application, its settings or the broker log directory.
#>
[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'HardwareOSD\FanBroker')
)

$ErrorActionPreference = 'Stop'
$serviceName = 'HardwareOSDFanBroker'
$installPath = [System.IO.Path]::GetFullPath($InstallDirectory)
$programFiles = [System.IO.Path]::GetFullPath($env:ProgramFiles)
if (-not $installPath.StartsWith($programFiles + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "UNINSTALL FAILED: install directory must be under $programFiles" -ForegroundColor Red
    exit 1
}

Write-Host ''
Write-Host 'HardwareOSD optional MSI fan broker' -ForegroundColor Cyan
Write-Host 'This stops and deletes the LocalSystem service and removes its Program Files directory.'
Write-Host 'The HardwareOSD application and settings are not touched.'
Write-Host ''
$answer = Read-Host 'Type REMOVE to approve these administrator changes'
if ($answer -ne 'REMOVE') {
    Write-Host 'Cancelled; no changes were made.'
    exit 0
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

if (Test-Path -LiteralPath $installPath) {
    Remove-Item -LiteralPath $installPath -Recurse -Force
}

Write-Host 'The fan broker service and its Program Files directory were removed.' -ForegroundColor Green
Write-Host 'Logs remain under: ' + (Join-Path $env:ProgramData 'HardwareOSD\FanBroker\logs')
