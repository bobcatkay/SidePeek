<#
.SYNOPSIS
Build and package the Rust/GPUI implementation of SidePeek.
.PARAMETER Proxy
Process-local Cargo HTTP proxy. Use an empty string for a direct connection.
.PARAMETER SelfContained
Compatibility parameter: GPUI builds already run without .NET.
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [Alias('NuGetProxy')][AllowEmptyString()][string]$Proxy = 'http://127.0.0.1:10808',
    [switch]$SelfContained,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\cargo.ps1')
$cargoArguments = @('build', '--locked')
if ($Configuration -eq 'Release') { $cargoArguments += '--release' }
if ($Offline) { $cargoArguments += '--offline' }
Invoke-SidePeekCargo -Arguments $cargoArguments -Proxy $Proxy
$metadata = Get-SidePeekMetadata
$version = $metadata.packages[0].version
$profile = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
$executable = Join-Path $metadata.target_directory "$profile\sidepeek.exe"
if (-not (Test-Path -LiteralPath $executable)) { throw "Executable not found: $executable" }
# Versioned output avoids removing a running app or older WPF release packages.
$destination = Join-Path $PSScriptRoot "dist\SidePeek-$version-gpui-$Runtime"
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath $executable -Destination (Join-Path $destination 'SidePeek.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $destination -Force
$packageDocs = Join-Path $destination 'docs'
New-Item -ItemType Directory -Force -Path $packageDocs | Out-Null
foreach ($document in @('SETUP.md', 'ARCHITECTURE.md', 'TASKS.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\$document") -Destination $packageDocs -Force
}
$archive = "$destination.zip"
Compress-Archive -LiteralPath (Join-Path $destination 'SidePeek.exe'), (Join-Path $destination 'README.md'), $packageDocs -DestinationPath $archive -Force
Write-Host "Executable: $(Join-Path $destination 'SidePeek.exe')"
Write-Host "Archive: $archive"
