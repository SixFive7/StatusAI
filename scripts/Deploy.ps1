<#
.SYNOPSIS
    Replaces the installed cship-usage.exe with a new build: tested first, backed up, copied with
    retries, verified by hash, and rolled back if the copy does not land.

.DESCRIPTION
    The procedure docs/development.md describes, as one script:

      1. Refuse unless cship.exe sits beside the target: without it the status line prints the
         raw session JSON.
      2. Stop if the target already has the same SHA-256.
      3. Run the render tests against the new build (tests/Test-Renders.ps1), with the cship that
         will run it. A failing build is not deployed.
      4. Back the target up as a sibling cship-usage.exe.bak.<unix-seconds>, and verify the copy.
      5. Copy the new build over the target, retrying: Claude Code runs the status line every
         60 seconds per session, and Windows holds the image for the ~100 ms it runs.
      6. Verify the target's SHA-256 against the build. If the copy failed or landed wrong, restore
         the backup the same way and verify that, so a working binary is always in place.

    The registry cache is not touched: the next fetch, within 50 s, writes a render of the new
    binary, and until then the old binary's render may still be served (docs/development.md,
    trap 1).

.PARAMETER Source
    The new build. Default: the output of `dotnet publish -c Release -r win-x64` in src/.

.PARAMETER Target
    The installed cship-usage.exe to replace. Default: the one Claude Code finds on PATH.

.PARAMETER SkipTests
    Deploy without running the render tests first.

.EXAMPLE
    ./scripts/Deploy.ps1 -WhatIf

.EXAMPLE
    ./scripts/Deploy.ps1 -Source .work/publish/cship-usage.exe
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Source,
    [string] $Target,
    [switch] $SkipTests
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Source) { $Source = Join-Path $here '..\src\bin\Release\net10.0-windows\win-x64\publish\cship-usage.exe' }
if (-not $Target) {
    $found = Get-Command cship-usage.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { Write-Host 'No cship-usage.exe on PATH. Pass -Target <path>.' -ForegroundColor Red; exit 1 }
    $Target = $found.Source
}

function Hash([string] $p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLowerInvariant() }
function Copy-WithRetry([string] $from, [string] $to) {
    foreach ($i in 1..12) {
        try { Copy-Item -LiteralPath $from -Destination $to -Force; return $i }
        catch { Write-Host ("  attempt {0} failed: {1}" -f $i, $_.Exception.Message); Start-Sleep -Milliseconds 700 }
    }
    return 0
}

# ------------------------------------------------------------------ preflight
if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    Write-Host "No build at $Source. Run dotnet publish first (docs/development.md)." -ForegroundColor Red; exit 1
}
if (-not (Test-Path -LiteralPath $Target -PathType Leaf)) {
    Write-Host "No installed binary at $Target to replace. A first install is manual: docs/guide/install.md." -ForegroundColor Red; exit 1
}
$Source = (Resolve-Path -LiteralPath $Source).Path
$Target = (Resolve-Path -LiteralPath $Target).Path
$cship = Join-Path (Split-Path -Parent $Target) 'cship.exe'
if (-not (Test-Path -LiteralPath $cship -PathType Leaf)) {
    Write-Host "No cship.exe beside $Target. cship-usage runs the cship in its own directory, and without one the status line prints the raw session JSON." -ForegroundColor Red
    exit 1
}

$newHash = Hash $Source
$oldHash = Hash $Target
Write-Host "build          : $Source"
Write-Host "  sha256       : $newHash"
Write-Host "installed      : $Target"
Write-Host "  sha256       : $oldHash"
if ($newHash -eq $oldHash) { Write-Host 'already deployed; nothing to do'; exit 0 }

if (-not $SkipTests) {
    Write-Host 'render tests   :'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here '..\tests\Test-Renders.ps1') -Exe $Source -Cship $cship
    if ($LASTEXITCODE -ne 0) { Write-Host "The build fails the render tests (exit $LASTEXITCODE); nothing deployed." -ForegroundColor Red; exit 1 }
}

if (-not $PSCmdlet.ShouldProcess($Target, "replace with $Source")) { exit 0 }

# ------------------------------------------------------------------ back up, copy, verify
$t = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$bak = "$Target.bak.$t"
Copy-Item -LiteralPath $Target -Destination $bak
if ((Hash $bak) -ne $oldHash) { Write-Host "The backup $bak does not match the installed binary; nothing deployed." -ForegroundColor Red; exit 1 }
Write-Host "backup         : $bak (verified)"

$n = Copy-WithRetry $Source $Target
$got = Hash $Target
if ($n -gt 0 -and $got -eq $newHash) {
    Write-Host ("deployed       : attempt {0}, sha256 matches the build" -f $n)
    Write-Host ("deployed at    : {0} (unix {1})" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'), [DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
    exit 0
}

Write-Host "DEPLOY FAILED (copy attempts used: $n; the target's sha256 is now $got); restoring $bak" -ForegroundColor Red
$r = Copy-WithRetry $bak $Target
$now = Hash $Target
if ($r -gt 0 -and $now -eq $oldHash) { Write-Host "restored the previous binary (sha256 $now)"; exit 2 }
Write-Host "RESTORE FAILED: $Target has sha256 $now; the previous binary is intact at $bak" -ForegroundColor Red
exit 3
