<#
.SYNOPSIS
Bump the Rust package version, update Cargo.lock, and create a GPUI release package.
#>
param(
    [ValidateSet('Major', 'Minor', 'Patch')][string]$Part = 'Patch',
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [Alias('NuGetProxy')][AllowEmptyString()][string]$Proxy = 'http://127.0.0.1:10808',
    [switch]$SelfContained,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\cargo.ps1')
$manifestPath = Join-Path $PSScriptRoot 'Cargo.toml'
$lockPath = Join-Path $PSScriptRoot 'Cargo.lock'
$manifestBefore = [IO.File]::ReadAllText($manifestPath)
$lockBefore = [IO.File]::ReadAllBytes($lockPath)
$pattern = '(?m)^version = "(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)"(?=\r?$)'
$match = [regex]::Match($manifestBefore, $pattern)
if (-not $match.Success) { throw 'Unable to find the package semantic version in Cargo.toml.' }
$major = [int]$match.Groups['major'].Value
$minor = [int]$match.Groups['minor'].Value
$patch = [int]$match.Groups['patch'].Value
switch ($Part) {
    'Major' { $major++; $minor = 0; $patch = 0 }
    'Minor' { $minor++; $patch = 0 }
    'Patch' { $patch++ }
}
$version = "$major.$minor.$patch"
$manifestAfter = $manifestBefore.Remove($match.Index, $match.Length).Insert($match.Index, ('version = "' + $version + '"'))
try {
    [IO.File]::WriteAllText($manifestPath, $manifestAfter, [Text.UTF8Encoding]::new($false))
    Invoke-SidePeekCargo -Arguments @('update', '--workspace', '--offline')
    & (Join-Path $PSScriptRoot 'build.ps1') -Runtime $Runtime -Proxy $Proxy -Offline:$Offline
} catch {
    # Package version and lockfile must roll back together if compilation fails.
    [IO.File]::WriteAllText($manifestPath, $manifestBefore, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes($lockPath, $lockBefore)
    throw
}
