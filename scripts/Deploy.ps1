<#
.SYNOPSIS
    Replaces the installed statusai.exe with a new build, or puts the first one in place: tested
    first, backed up, copied with retries, verified by hash, and rolled back if the copy does not
    land.

.DESCRIPTION
    The procedure docs/development.md describes, as one script:

      1. Refuse unless cship.exe sits beside the target: without it the status line prints the
         raw session JSON.
      2. Stop if the target already has the same SHA-256.
      3. Run the render tests against the new build (tests/Test-Renders.ps1), with the cship that
         will run it. A failing build is not deployed.
      4. Back the target up as a sibling statusai.exe.bak.<unix-seconds>, and verify the copy.
      5. Copy the new build over the target, retrying: Claude Code runs the status line every
         60 seconds per session, and Windows holds the image for the ~100 ms it runs.
      6. Verify the target's SHA-256 against the build. If no copy landed, the target still is the
         previous binary and there is nothing to undo. If one landed wrong, restore the backup the
         same way and verify that, so a working binary is always in place.

    A -Target that does not exist yet, in a folder that does, is a first deploy: there is nothing
    to compare or back up, so steps 2 and 4 are skipped, and a copy that lands wrong is removed
    rather than restored.

    The registry cache is not touched. It holds figures rather than drawn rows, so the new binary
    draws them itself at once; until the next fetch, within 50 s, the figures are the ones the old
    binary computed (docs/development.md, trap 1).

.PARAMETER Source
    The new build. Default: the output of `dotnet publish -c Release -r win-x64` in src/.

.PARAMETER Target
    The installed statusai.exe to replace, or where the first one goes. Default: the one Claude
    Code finds on PATH.

.PARAMETER SkipTests
    Deploy without running the render tests first.

.EXAMPLE
    ./scripts/Deploy.ps1 -WhatIf

.EXAMPLE
    ./scripts/Deploy.ps1 -Source .work/publish/statusai.exe

.EXAMPLE
    ./scripts/Deploy.ps1 -Target $env:USERPROFILE\.local\bin\statusai.exe
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
if (-not $Source) { $Source = Join-Path $here '..\src\bin\Release\net10.0-windows\win-x64\publish\statusai.exe' }
if (-not $Target) {
    $found = Get-Command statusai.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { Write-Host 'No statusai.exe on PATH. Pass -Target <path>: the one to replace, or where the first one goes.' -ForegroundColor Red; exit 1 }
    $Target = $found.Source
}

# '' for a file that cannot be read, such as one another process holds open without sharing it.
# Hashed in .NET rather than by Get-FileHash, which under Windows PowerShell 5.1 takes -WhatIf
# for its own and returns nothing.
function Hash([string] $p) {
    try {
        $fs = [IO.File]::Open($p, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite, Delete')
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { [BitConverter]::ToString($sha.ComputeHash($fs)).Replace('-', '').ToLowerInvariant() }
            finally { $sha.Dispose() }
        } finally { $fs.Dispose() }
    } catch { '' }
}
function Copy-WithRetry([string] $from, [string] $to) {
    foreach ($i in 1..12) {
        try { Copy-Item -LiteralPath $from -Destination $to -Force; return $i }
        catch { Write-Host ("  attempt {0} failed: {1}" -f $i, $_.Exception.Message); Start-Sleep -Milliseconds 700 }
    }
    return 0
}
function Remove-WithRetry([string] $p) {
    foreach ($i in 1..12) {
        try { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }; return $i }
        catch { Write-Host ("  attempt {0} failed: {1}" -f $i, $_.Exception.Message); Start-Sleep -Milliseconds 700 }
    }
    return 0
}

# ------------------------------------------------------------------ preflight
if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    Write-Host "No build at $Source. Run dotnet publish first (docs/development.md)." -ForegroundColor Red; exit 1
}
$Source = (Resolve-Path -LiteralPath $Source).Path
$first = -not (Test-Path -LiteralPath $Target)
if ($first) {
    $dir = Split-Path -Parent $Target
    if (-not $dir -or -not (Test-Path -LiteralPath $dir -PathType Container)) {
        Write-Host "No binary at $Target, and no folder to put a first one in. A first install is docs/guide/install.md." -ForegroundColor Red; exit 1
    }
    $Target = Join-Path (Resolve-Path -LiteralPath $dir).Path (Split-Path -Leaf $Target)
} elseif (-not (Test-Path -LiteralPath $Target -PathType Leaf)) {
    Write-Host "$Target is not a file." -ForegroundColor Red; exit 1
} else {
    $Target = (Resolve-Path -LiteralPath $Target).Path
}
$cship = Join-Path (Split-Path -Parent $Target) 'cship.exe'
if (-not (Test-Path -LiteralPath $cship -PathType Leaf)) {
    Write-Host "No cship.exe beside $Target. statusai runs the cship in its own directory, and without one the status line prints the raw session JSON." -ForegroundColor Red
    exit 1
}

$newHash = Hash $Source
if ($newHash -eq '') { Write-Host "Cannot read $Source, which another process may hold open. Nothing deployed." -ForegroundColor Red; exit 1 }
Write-Host "build          : $Source"
Write-Host "  sha256       : $newHash"
if ($first) {
    $oldHash = ''
    Write-Host "installed      : none yet at $Target; a first deploy"
} else {
    $oldHash = Hash $Target
    if ($oldHash -eq '') { Write-Host "Cannot read $Target, which another process may hold open. Nothing deployed." -ForegroundColor Red; exit 1 }
    Write-Host "installed      : $Target"
    Write-Host "  sha256       : $oldHash"
    if ($newHash -eq $oldHash) { Write-Host 'already deployed; nothing to do'; exit 0 }
}

if (-not $SkipTests) {
    Write-Host 'render tests   :'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here '..\tests\Test-Renders.ps1') -Exe $Source -Cship $cship
    if ($LASTEXITCODE -ne 0) { Write-Host "The build fails the render tests (exit $LASTEXITCODE); nothing deployed." -ForegroundColor Red; exit 1 }
}

if (-not $PSCmdlet.ShouldProcess($Target, $(if ($first) { "put $Source in place" } else { "replace with $Source" }))) { exit 0 }

# ------------------------------------------------------------------ a first deploy: copy, verify
if ($first) {
    $n = Copy-WithRetry $Source $Target
    $got = Hash $Target
    if ($n -gt 0 -and $got -eq $newHash) {
        Write-Host ("deployed       : attempt {0}, sha256 matches the build; the first at {1}" -f $n, $Target)
        Write-Host ("deployed at    : {0} (unix {1})" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'), [DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
        exit 0
    }
    if (-not (Test-Path -LiteralPath $Target)) {
        Write-Host "DEPLOY FAILED (copy attempts used: $n); nothing was put in place at $Target" -ForegroundColor Red
        exit 2
    }
    $state = if ($got -eq '') { 'cannot be read' } else { "has sha256 $got" }
    Write-Host "DEPLOY FAILED (copy attempts used: $n; $Target $state); removing it" -ForegroundColor Red
    $r = Remove-WithRetry $Target
    if ($r -gt 0 -and -not (Test-Path -LiteralPath $Target)) { Write-Host ("removed it (attempt {0}); nothing is in place at {1}" -f $r, $Target); exit 2 }
    Write-Host "REMOVE FAILED: $Target $state, and is not the build; delete it by hand" -ForegroundColor Red
    exit 3
}

# ------------------------------------------------------------------ back up, copy, verify
$t = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$bak = "$Target.bak.$t"
try { Copy-Item -LiteralPath $Target -Destination $bak }
catch { Write-Host "Could not back up $Target to ${bak}: $($_.Exception.Message) Nothing deployed." -ForegroundColor Red; exit 1 }
if ((Hash $bak) -ne $oldHash) { Write-Host "The backup $bak does not match the installed binary; nothing deployed." -ForegroundColor Red; exit 1 }
Write-Host "backup         : $bak (verified)"

$n = Copy-WithRetry $Source $Target
$got = Hash $Target
if ($n -gt 0 -and $got -eq $newHash) {
    Write-Host ("deployed       : attempt {0}, sha256 matches the build" -f $n)
    Write-Host ("deployed at    : {0} (unix {1})" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'), [DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
    exit 0
}

# No copy landed, so the previous binary never left: a restore would only wait on the same lock,
# and report a failure when there is nothing wrong.
if ($got -eq $oldHash) {
    Write-Host "DEPLOY FAILED (copy attempts used: $n); the target is unchanged and still the previous binary (sha256 $got)" -ForegroundColor Red
    exit 2
}

$state = if ($got -eq '') { 'cannot be read' } else { "has sha256 $got" }
Write-Host "DEPLOY FAILED (copy attempts used: $n; the target now $state); restoring $bak" -ForegroundColor Red
$r = Copy-WithRetry $bak $Target
$now = Hash $Target
if ($now -eq $oldHash) { Write-Host ("restored the previous binary (attempt {0}, sha256 {1})" -f $r, $now); exit 2 }
$state = if ($now -eq '') { 'cannot be read' } else { "has sha256 $now" }
Write-Host "RESTORE FAILED: $Target $state; the previous binary is intact at $bak" -ForegroundColor Red
exit 3
