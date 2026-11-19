#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Explicitly install the optional HardwareOSD read-only fan broker as a LocalSystem service.
.DESCRIPTION
    This is never invoked by HardwareOSD. It verifies the source hash manifest, copies the service
    into an administrator-controlled Program Files directory, applies a restrictive ACL and creates
    the service with Automatic startup. Run uninstall-fan-broker.ps1 to remove it.
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\FanBroker'),
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'HardwareOSD\FanBroker'),
    [string]$Approval = ''
)

$ErrorActionPreference = 'Stop'
$serviceName = 'HardwareOSDFanBroker'
$displayName = 'HardwareOSD Fan Broker'
$manifestName = 'fan-broker-manifest.json'

function Fail([string]$Message) {
    Write-Host "INSTALL FAILED: $Message" -ForegroundColor Red
    exit 1
}

$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$manifestPath = Join-Path $sourcePath $manifestName
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    Fail "hash manifest is missing: $manifestPath. Install from a published HardwareOSD release, not a raw build output."
}

$installPath = [System.IO.Path]::GetFullPath($InstallDirectory)
$programFiles = [System.IO.Path]::GetFullPath($env:ProgramFiles)
if (-not $installPath.StartsWith($programFiles + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    Fail "install directory must be under $programFiles"
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $manifest.files -or $manifest.files.Count -eq 0) {
    Fail "manifest contains no files"
}

$sourceFiles = Get-ChildItem -LiteralPath $sourcePath -Recurse -File |
    Where-Object { $_.Name -ne $manifestName }
$manifestPaths = @{}
foreach ($entry in $manifest.files) {
    if (-not $entry.path -or $entry.path -match '(^[\\/]|\.\.)') {
        Fail "manifest contains an unsafe path: $($entry.path)"
    }
    if ($manifestPaths.ContainsKey($entry.path)) {
        Fail "manifest contains a duplicate path: $($entry.path)"
    }
    $manifestPaths[$entry.path] = [string]$entry.sha256
}

if ($sourceFiles.Count -ne $manifestPaths.Count) {
    Fail "source file count ($($sourceFiles.Count)) does not match the manifest ($($manifestPaths.Count)); do not install a modified folder."
}

foreach ($file in $sourceFiles) {
    $relative = $file.FullName.Substring($sourcePath.Length).TrimStart('\')
    if (-not $manifestPaths.ContainsKey($relative)) {
        Fail "source file is not listed in the manifest: $relative"
    }
    $actual = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $manifestPaths[$relative].ToLowerInvariant()) {
        Fail "hash mismatch for $relative"
    }
}

$binary = Join-Path $installPath 'HardwareOSD.FanBroker.exe'
Write-Host ''
Write-Host 'HardwareOSD optional MSI fan broker' -ForegroundColor Cyan
Write-Host 'This creates a LocalSystem Windows service for read-only MSI_ACPI.Get_Fan calls.'
Write-Host 'It never controls fans, writes EC registers, installs drivers or changes WMI ACLs.'
Write-Host ''
Write-Host "Source:      $sourcePath"
Write-Host "Install to:  $installPath"
Write-Host "Service:     $serviceName (Automatic, LocalSystem)"
Write-Host ''
$answer = if ($Approval) { $Approval } else { Read-Host 'Type INSTALL to approve these administrator changes' }
if ($answer -ne 'INSTALL') {
    Write-Host 'Cancelled; no changes were made.'
    exit 0
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host 'Removing the existing broker service before upgrade...'
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

if (Test-Path -LiteralPath $installPath) {
    Remove-Item -LiteralPath $installPath -Recurse -Force
}
New-Item -ItemType Directory -Path $installPath -Force | Out-Null

foreach ($entry in $manifest.files) {
    $relative = [string]$entry.path
    $destination = Join-Path $installPath $relative
    $parent = Split-Path -Parent $destination
    if ($parent) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $sourcePath $relative) -Destination $destination -Force
    $copiedHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($copiedHash -ne ([string]$entry.sha256).ToLowerInvariant()) {
        Fail "copied hash mismatch for $relative"
    }
}

# Only SYSTEM and Administrators may modify or execute the service files. Standard users only
# need access to the named pipe, which is protected separately by the service.
$aclResult = & icacls.exe $installPath /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'
if ($LASTEXITCODE -ne 0) {
    Fail "could not apply the service directory ACL"
}

if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
    Fail "service binary is missing after copy: $binary"
}

$newService = New-Service -Name $serviceName `
    -BinaryPathName ('"' + $binary + '"') `
    -DisplayName $displayName `
    -Description 'Optional read-only MSI fan RPM broker for HardwareOSD. Calls only MSI_ACPI.Get_Fan.' `
    -StartupType Automatic
if (-not $newService) {
    Fail 'New-Service returned no service object'
}

$config = (& sc.exe qc $serviceName) -join "`n"
if ($config -notmatch 'LocalSystem|LOCAL SYSTEM') {
    & sc.exe delete $serviceName | Out-Null
    Fail 'the service was not created under LocalSystem'
}

# Restart on transient failures, but never make the service install itself or change its config.
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
& sc.exe description $serviceName 'Optional read-only MSI fan RPM broker for HardwareOSD. Calls only MSI_ACPI.Get_Fan.' | Out-Null

Start-Service -Name $serviceName
Start-Sleep -Seconds 2
$service = Get-Service -Name $serviceName
Write-Host ''
Write-Host "Service status: $($service.Status)" -ForegroundColor Green
Write-Host "Service account: LocalSystem"
Write-Host "Logs: $env:ProgramData\HardwareOSD\FanBroker\logs"
Write-Host 'Run tools\fan-broker-probe.ps1 as a standard user to verify the pipe.'
