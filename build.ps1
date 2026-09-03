<#
.SYNOPSIS
    Build and package SidePeek as a single-file executable.

.PARAMETER SelfContained
    Bundle the .NET runtime (no .NET install required on target, ~150MB).
    Default is framework-dependent (requires .NET 9 Desktop Runtime, ~5MB).

.PARAMETER Runtime
    Target runtime identifier. Default: win-x64.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER NuGetProxy
    HTTP proxy used only for the dotnet publish process. Pass an empty string to use a direct connection.
    Default: http://127.0.0.1:10808.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -SelfContained
    .\build.ps1 -NuGetProxy ""
#>
param(
    [switch]$SelfContained,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [AllowEmptyString()]
    [string]$NuGetProxy = "http://127.0.0.1:10808"
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

$previousProxyEnvironment = @{
    HTTP_PROXY = $env:HTTP_PROXY
    HTTPS_PROXY = $env:HTTPS_PROXY
    ALL_PROXY = $env:ALL_PROXY
    NO_PROXY = $env:NO_PROXY
}

[Uri]$nugetProxyUri = $null
if (-not [string]::IsNullOrWhiteSpace($NuGetProxy)) {
    $isValidProxy = [Uri]::TryCreate($NuGetProxy, [UriKind]::Absolute, [ref]$nugetProxyUri)
    if (-not $isValidProxy -or $nugetProxyUri.Scheme -notin "http", "https") {
        throw "NuGetProxy must be an absolute HTTP(S) URL or an empty string: $NuGetProxy"
    }
}

$publishExitCode = $null
try {
    if ($null -ne $nugetProxyUri) {
        # Override stale terminal proxy variables only for this publish, then restore them below.
        $env:HTTP_PROXY = $nugetProxyUri.AbsoluteUri
        $env:HTTPS_PROXY = $nugetProxyUri.AbsoluteUri
        Remove-Item Env:ALL_PROXY -ErrorAction SilentlyContinue
        Remove-Item Env:NO_PROXY -ErrorAction SilentlyContinue

        $proxyDisplay = "$($nugetProxyUri.Scheme)://$($nugetProxyUri.Host):$($nugetProxyUri.Port)"
        Write-Host "==> NuGet proxy (process-local): $proxyDisplay" -ForegroundColor Cyan
    }

    dotnet @publishArgs
    $publishExitCode = $LASTEXITCODE
}
finally {
    foreach ($proxyVariableName in $previousProxyEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable(
            $proxyVariableName,
            $previousProxyEnvironment[$proxyVariableName],
            [EnvironmentVariableTarget]::Process)
    }
}

if ($null -eq $publishExitCode -or $publishExitCode -ne 0) {
    throw "dotnet publish failed (exit $publishExitCode)"
}

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
