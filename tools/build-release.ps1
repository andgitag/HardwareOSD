# Builds the official self-contained and framework-dependent release archives.
# Existing release folders/archives are never overwritten.
#   powershell -ExecutionPolicy Bypass -File tools\build-release.ps1 -Version 1.0.0.0
param(
    [string]$Version = '1.0.0.0'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root

$releaseDirectories = @(
    "dist\HardwareOSD-$Version-win-x64",
    "dist\HardwareOSD-$Version-win-x64-fdd"
)
$releaseArchives = @($releaseDirectories | ForEach-Object { $_ + '.zip' })
foreach ($path in @($releaseDirectories + $releaseArchives)) {
    if (Test-Path -LiteralPath $path) {
        throw "Refusing to overwrite existing release path: $path"
    }
}

function Publish-App([string]$project, [string]$output, [bool]$selfContained) {
    $self = if ($selfContained) { 'true' } else { 'false' }
    & dotnet publish $project -c Release -r win-x64 -p:Platform=x64 `
        --self-contained $self -p:PublishSingleFile=false -p:DebugType=none -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $project -> $output" }
}

function Publish-Service([string]$output, [bool]$selfContained) {
    $self = if ($selfContained) { 'true' } else { 'false' }
    & dotnet publish 'src\HardwareOSD.FanBroker\HardwareOSD.FanBroker.csproj' -c Release -r win-x64 `
        -p:Platform=x64 --self-contained $self -p:PublishSingleFile=false -p:DebugType=none -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed: FanBroker -> $output" }
}

function Copy-ReleaseCommon([string]$output) {
    # Public archive only: user-facing docs, licenses and the explicit FanBroker installer.
    Copy-Item -LiteralPath README.md,RELEASE-NOTES.md,THIRD-PARTY-NOTICES.md,LICENSE -Destination $output -Force
    Copy-Item -LiteralPath licenses -Destination $output -Recurse -Force
    $tools = Join-Path $output 'tools'
    New-Item -ItemType Directory -Path $tools -Force | Out-Null
    Copy-Item -LiteralPath tools\install-fan-broker.ps1,tools\uninstall-fan-broker.ps1 `
        -Destination $tools -Force
}

function New-ReleaseArchive([string]$directory, [string]$archive) {
    if (Test-Path -LiteralPath $archive) {
        throw "Refusing to overwrite existing release archive: $archive"
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        (Resolve-Path -LiteralPath $directory).Path,
        $archive,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $true)
}

$self = $releaseDirectories[0]
Publish-App 'src\HardwareOSD.App\HardwareOSD.App.csproj' $self $true
Publish-App 'src\HardwareOSD.Cli\HardwareOSD.Cli.csproj' $self $true
Publish-Service (Join-Path $self 'FanBroker') $true
Copy-ReleaseCommon $self
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\write-fan-broker-manifest.ps1 `
    -Directory (Join-Path $self 'FanBroker')
if ($LASTEXITCODE -ne 0) { throw 'self-contained service manifest failed' }

$fdd = $releaseDirectories[1]
Publish-App 'src\HardwareOSD.App\HardwareOSD.App.csproj' $fdd $false
Publish-App 'src\HardwareOSD.Cli\HardwareOSD.Cli.csproj' $fdd $false
Publish-Service (Join-Path $fdd 'FanBroker') $false
Copy-ReleaseCommon $fdd
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\write-fan-broker-manifest.ps1 `
    -Directory (Join-Path $fdd 'FanBroker')
if ($LASTEXITCODE -ne 0) { throw 'framework-dependent service manifest failed' }

$checksums = New-Object System.Collections.Generic.List[string]
for ($index = 0; $index -lt $releaseDirectories.Count; $index++) {
    $directory = $releaseDirectories[$index]
    $archive = $releaseArchives[$index]
    $size = (Get-ChildItem -LiteralPath $directory -Recurse -File | Measure-Object Length -Sum).Sum
    $exe = Join-Path $directory 'HardwareOSD.exe'
    $exeHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash

    New-ReleaseArchive $directory $archive
    $zipHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    $checksums.Add("$zipHash  $([IO.Path]::GetFileName($archive))")
    $checksums.Add("$exeHash  $([IO.Path]::GetFileName($directory))\HardwareOSD.exe")

    Write-Host ("{0}: {1:N1} MB, HardwareOSD.exe SHA256 {2}, ZIP SHA256 {3}" -f `
        $directory, ($size / 1MB), $exeHash, $zipHash)
}

$checksumPath = "dist\SHA256SUMS-$Version.txt"
$checksums | Set-Content -LiteralPath $checksumPath -Encoding ASCII
Write-Host "Checksums: $checksumPath"
