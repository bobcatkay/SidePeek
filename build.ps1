<#
.SYNOPSIS
    Build and package SidePeek as a single-file executable.

.PARAMETER SelfContained
    Bundle the .NET runtime (no .NET install required on target, ~150MB).
    Default is framework-dependent (requires .NET 10 Desktop Runtime, ~5MB).

.PARAMETER Runtime
    Target runtime identifier. Default: win-x64.

.PARAMETER Configuration
    Build configuration. Default: Release.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SelfContained
#>
param(
    [switch]$SelfContained,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\SidePeek.App\SidePeek.App.csproj"
$publishDir = Join-Path $root "dist\$Runtime"
$distRoot = Join-Path $root "dist"

Write-Host "==> Cleaning previous output" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

if ($SelfContained) { $scValue = "true" } else { $scValue = "false" }
Write-Host "==> Publishing (self-contained=$scValue, runtime=$Runtime, config=$Configuration)" -ForegroundColor Cyan

$publishArgs = @(
    "publish", $project,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", $scValue,
    "-o", $publishDir,
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:DebugType=none",
    "--nologo"
)

# Single-file compression is only supported for self-contained publishes.
if ($SelfContained) {
    $publishArgs += "-p:EnableCompressionInSingleFile=true"
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $publishDir "SidePeek.exe"
if (-not (Test-Path $exe)) { throw "Executable not found: $exe" }

$version = (Get-Item $exe).VersionInfo.ProductVersion
if (-not $version) { $version = "0.0.0" }
$suffix = ""
if ($SelfContained) { $suffix = "-selfcontained" }
$zipName = "SidePeek-$version-$Runtime$suffix.zip"
$zipPath = Join-Path $distRoot $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "==> Creating zip: $zipName" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath

$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Build complete." -ForegroundColor Green
Write-Host "  Executable : $exe ($sizeMb MB)"
Write-Host "  Publish dir: $publishDir"
Write-Host "  Zip        : $zipPath"
