# Writes the hash manifest consumed by install-fan-broker.ps1 for a published FanBroker folder.
param(
    [string]$Directory = (Join-Path $PSScriptRoot '..\dist\HardwareOSD-0.25.0-win-x64\FanBroker')
)

$ErrorActionPreference = 'Stop'
$directoryPath = (Resolve-Path -LiteralPath $Directory).Path
$manifestPath = Join-Path $directoryPath 'fan-broker-manifest.json'
$exe = Join-Path $directoryPath 'HardwareOSD.FanBroker.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Service executable not found: $exe"
}

$version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
$files = Get-ChildItem -LiteralPath $directoryPath -Recurse -File |
    Where-Object { $_.FullName -ne $manifestPath } |
    Sort-Object FullName |
    ForEach-Object {
        [pscustomobject]@{
            path = $_.FullName.Substring($directoryPath.Length).TrimStart('\')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }

$manifest = [pscustomobject]@{
    product = 'HardwareOSD'
    component = 'FanBroker'
    version = $version
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    files = @($files)
}

$json = $manifest | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $json, [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $manifestPath ($($files.Count) files, version $version)"
