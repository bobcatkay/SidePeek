<#
.SYNOPSIS
    Bump SidePeek version and create a release package.

.PARAMETER Part
    Which semantic version part to increment. Default: Patch.

.PARAMETER SelfContained
    Bundle the .NET runtime. Passed through to build.ps1.

.PARAMETER Runtime
    Target runtime identifier. Default: win-x64.

.PARAMETER Configuration
    Build configuration. Default: Release.

.EXAMPLE
    .\build-release.ps1
    .\build-release.ps1 -Part Minor -SelfContained
#>
param(
    [ValidateSet("Major", "Minor", "Patch")]
    [string]$Part = "Patch",
    [switch]$SelfContained,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\SidePeek.App\SidePeek.App.csproj"
$buildScript = Join-Path $root "build.ps1"

if (-not (Test-Path $project)) { throw "Project not found: $project" }
if (-not (Test-Path $buildScript)) { throw "Build script not found: $buildScript" }

$projectText = Get-Content $project -Raw -Encoding UTF8
$versionPattern = "<Version>(?<version>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?<suffix>[-+][^<]+)?</Version>"
$match = [regex]::Match($projectText, $versionPattern)
if (-not $match.Success) {
    throw "Unable to find a semantic <Version>x.y.z</Version> value in $project"
}

$major = [int]$match.Groups["version"].Value
$minor = [int]$match.Groups["minor"].Value
$patch = [int]$match.Groups["patch"].Value

switch ($Part) {
    "Major" {
        $major++
        $minor = 0
        $patch = 0
    }
    "Minor" {
        $minor++
        $patch = 0
    }
    default {
        $patch++
    }
}

$oldVersion = $match.Groups[0].Value -replace "<Version>|</Version>", ""
$newVersion = "$major.$minor.$patch"
$updatedText = [regex]::Replace(
    $projectText,
    $versionPattern,
    "<Version>$newVersion</Version>",
    1)

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($project, $updatedText, $utf8NoBom)

Write-Host "==> Version bumped: $oldVersion -> $newVersion" -ForegroundColor Cyan

$buildArgs = @{
    Runtime = $Runtime
    Configuration = $Configuration
}
if ($SelfContained) {
    $buildArgs.SelfContained = $true
}

try {
    & $buildScript @buildArgs
}
catch {
    [System.IO.File]::WriteAllText($project, $projectText, $utf8NoBom)
    Write-Host "==> Build failed; version rolled back to $oldVersion" -ForegroundColor Yellow
    throw
}
