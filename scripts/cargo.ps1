# Shared Cargo launcher. Environment changes apply only to the child process.
$script:SidePeekRoot = Split-Path $PSScriptRoot -Parent

function Get-SidePeekCargoStartInfo {
    param([string[]]$Arguments, [AllowEmptyString()][string]$Proxy)
    $localCargo = Join-Path $script:SidePeekRoot '.tools\cargo\bin\cargo.exe'
    $cargoCommand = Get-Command cargo -ErrorAction SilentlyContinue
    $executable = if (Test-Path -LiteralPath $localCargo) { $localCargo } elseif ($cargoCommand) { $cargoCommand.Source } else {
        throw 'Rust is unavailable. Install Rust with the MSVC toolchain; see docs/SETUP.md.'
    }
    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $executable
    $info.WorkingDirectory = $script:SidePeekRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    if ($executable -eq $localCargo) {
        $info.Environment['CARGO_HOME'] = Join-Path $script:SidePeekRoot '.tools\cargo'
        $info.Environment['RUSTUP_HOME'] = Join-Path $script:SidePeekRoot '.tools\rustup'
        $info.Environment['PATH'] = "$(Split-Path $localCargo -Parent);$($info.Environment['PATH'])"
    }
    foreach ($name in @('ALL_PROXY', 'NO_PROXY', 'HTTP_PROXY', 'HTTPS_PROXY')) { $info.Environment.Remove($name) | Out-Null }
    if (-not [string]::IsNullOrWhiteSpace($Proxy)) {
        [Uri]$proxyUri = $null
        if (-not [Uri]::TryCreate($Proxy, [UriKind]::Absolute, [ref]$proxyUri) -or $proxyUri.Scheme -notin @('http', 'https')) {
            throw 'Proxy must be an absolute HTTP(S) URL or an empty string.'
        }
        $info.Environment['HTTP_PROXY'] = $Proxy
        $info.Environment['HTTPS_PROXY'] = $Proxy
    }
    return $info
}

function Invoke-SidePeekCargo {
    param([Parameter(Mandatory)][string[]]$Arguments, [AllowEmptyString()][string]$Proxy = '')
    $info = Get-SidePeekCargoStartInfo -Arguments $Arguments -Proxy $Proxy
    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "cargo $($Arguments[0]) failed (exit $($process.ExitCode))" }
    } finally { $process.Dispose() }
}

function Get-SidePeekMetadata {
    $info = Get-SidePeekCargoStartInfo -Arguments @('metadata', '--offline', '--no-deps', '--format-version', '1') -Proxy ''
    $info.RedirectStandardOutput = $true
    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw 'cargo metadata failed' }
        return $output | ConvertFrom-Json
    } finally { $process.Dispose() }
}
