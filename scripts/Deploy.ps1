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

    Where the Stream Deck plugin is installed, its folder holds a copy of statusai.exe of its
    own, which the app runs for as long as it is open. Once the status line's binary is in
    place, or was already, that copy is brought in step with the same build, so the key and the
    status line are never two versions:

      7. Compare the plugin's manifest, pictures and statusai.exe with the repository's and the
         build. If nothing differs, say so and stop.
      8. Copy the manifest and the pictures. Move the plugin's statusai.exe aside as
         statusai.exe.old.<unix-seconds>, which Windows allows while it runs, copy the build in
         under its name, and verify its SHA-256. A copy that lands wrong is removed and the old
         one moved back.
      9. End the plugin's process, which the app starts again by itself, from the new copy; wait
         for that process, and remove the old file once nothing runs it. A copy that an earlier
         run moved aside and that is still running is taken as the plugin not having been
         started again yet, and is dealt with the same way.

    Without a plugin folder nothing of this happens, unless -Deck asks for a first install: the
    folder is then created and filled, and the app has to be restarted to see it.

.PARAMETER Source
    The new build. Default: the output of `dotnet publish -c Release -r win-x64` in src/.

.PARAMETER Target
    The installed statusai.exe to replace, or where the first one goes. Default: the one Claude
    Code finds on PATH.

.PARAMETER SkipTests
    Deploy without running the render tests first.

.PARAMETER Deck
    Install the Stream Deck plugin where it is not installed yet. A plugin that is installed is
    brought in step without it.

.PARAMETER DeckDir
    The folder the app keeps its plugins in. Default: %APPDATA%\Elgato\StreamDeck\Plugins. With
    another folder nothing is asked of the app, so the plugin copy can be tried on a scratch
    folder as the target can.

.EXAMPLE
    ./scripts/Deploy.ps1 -WhatIf

.EXAMPLE
    ./scripts/Deploy.ps1 -Source .work/publish/statusai.exe

.EXAMPLE
    ./scripts/Deploy.ps1 -Target $env:USERPROFILE\.local\bin\statusai.exe

.EXAMPLE
    ./scripts/Deploy.ps1 -Deck
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Source,
    [string] $Target,
    [switch] $SkipTests,
    [switch] $Deck,
    [string] $DeckDir
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

# ------------------------------------------------------------------ the Stream Deck plugin's copy
# The plugin is this same statusai.exe, which the app runs from the plugin's own folder for as
# long as the app is open. That copy is therefore locked the whole time, where the status
# line's is free all but 100 ms of each minute, and it would stay the old build if nothing
# brought it along. Returns 0 when the plugin is not installed, is in step or was brought in
# step, and 4 when it is not in step and could not be.
function Sync-Deck {
    $uuid = 'com.sixfive7.statusai'
    $own = Join-Path $env:APPDATA 'Elgato\StreamDeck\Plugins'
    $plugins = if ($DeckDir) { $DeckDir } else { $own }
    $dest = Join-Path $plugins "$uuid.sdPlugin"
    $exe = Join-Path $dest 'statusai.exe'
    $first = -not (Test-Path -LiteralPath $dest -PathType Container)
    if ($first -and -not $Deck) { return 0 }
    if (-not (Test-Path -LiteralPath $plugins -PathType Container)) {
        Write-Host "stream deck    : no plugins folder at $plugins, so the plugin was not installed" -ForegroundColor Red; return 4
    }
    $assets = Join-Path $here "..\streamdeck\$uuid.sdPlugin"
    if (-not (Test-Path -LiteralPath (Join-Path $assets 'manifest.json') -PathType Leaf)) {
        Write-Host "stream deck    : no plugin folder to copy at $assets" -ForegroundColor Red; return 4
    }
    $assets = (Resolve-Path -LiteralPath $assets).Path

    # A copy an earlier run moved aside, if nothing runs it any more. One that cannot be removed
    # is still running: the plugin was not started again from the copy that replaced it.
    $behind = $false
    if (-not $first) {
        foreach ($old in @(Get-ChildItem -LiteralPath $dest -Filter 'statusai.exe.old.*')) {
            # with -WhatIf nothing is removed, so whether it still runs cannot be told: say it may
            if ($WhatIfPreference) { $behind = $true; continue }
            try { Remove-Item -LiteralPath $old.FullName -Force -ErrorAction Stop } catch { $behind = $true }
        }
    }
    # what differs: the manifest and the pictures from the repository's, the exe from the build
    $files = @(Get-ChildItem -LiteralPath $assets -Recurse -File | ForEach-Object { $_.FullName.Substring($assets.Length + 1) })
    $stale = @($files | Where-Object { (Hash (Join-Path $dest $_)) -ne (Hash (Join-Path $assets $_)) })
    $exeStale = (Hash $exe) -ne $newHash
    if (-not $exeStale -and $stale.Count -eq 0 -and -not $behind) { Write-Host "stream deck    : $dest is in step with the build"; return 0 }
    $what = if ($first) { 'install the Stream Deck plugin' }
            elseif ($exeStale -or $stale.Count -gt 0) { 'bring the Stream Deck plugin in step with the build' }
            else { 'start the Stream Deck plugin again, from the copy that is in step' }
    if (-not $PSCmdlet.ShouldProcess($dest, $what)) { return 0 }

    try {
        foreach ($f in $stale) {
            $to = Join-Path $dest $f
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
            Copy-Item -LiteralPath (Join-Path $assets $f) -Destination $to -Force
        }
    } catch { Write-Host "stream deck    : could not copy the manifest and the pictures to ${dest}: $($_.Exception.Message)" -ForegroundColor Red; return 4 }

    $aside = ''
    if ($exeStale) {
        # Windows will not overwrite or delete a program that is running, but it lets it be moved aside
        if (Test-Path -LiteralPath $exe -PathType Leaf) {
            $aside = "$exe.old.$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())"
            try { Move-Item -LiteralPath $exe -Destination $aside }
            catch { Write-Host "stream deck    : could not move $exe aside: $($_.Exception.Message)" -ForegroundColor Red; return 4 }
        }
        $n = Copy-WithRetry $Source $exe
        if ($n -eq 0 -or (Hash $exe) -ne $newHash) {
            Write-Host "stream deck    : the build did not land at $exe; putting the previous copy back" -ForegroundColor Red
            [void] (Remove-WithRetry $exe)
            if ($aside) { try { Move-Item -LiteralPath $aside -Destination $exe } catch { Write-Host "  the previous copy is at $aside" -ForegroundColor Red } }
            return 4
        }
    }
    if ($first -or $exeStale -or $stale.Count -gt 0) {
        Write-Host ("stream deck    : {0} {1}, sha256 matches the build" -f $dest, $(if ($first) { 'installed' } else { 'brought in step' }))
    } else {
        Write-Host "stream deck    : $dest is in step with the build, and a copy from before it still runs"
    }

    # ------------------------------------------------------------------ the running plugin
    $isOwn = [string]::Equals((Resolve-Path -LiteralPath $plugins).Path.TrimEnd('\'), $own.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
    $running = @(Get-Process -Name StreamDeck -ErrorAction SilentlyContinue).Count -gt 0
    if (-not $isOwn) {
        Write-Host '  not the folder of the app itself, so nothing was asked of the app'
    } elseif ($first) {
        Write-Host '  Stream Deck finds a new plugin when it starts: quit it and start it again, then put StatusAI > Claude Code on a key'
    } elseif (-not $running) {
        Write-Host '  Stream Deck is not running; it starts the new copy when it does'
    } elseif ($exeStale -or $behind) {
        # The app starts a plugin again by itself when its process ends, and that needs nothing of
        # the app's developer mode. Its own ways of restarting a plugin do: the link
        # streamdeck://plugins/restart/<uuid>, and StreamDeck.exe --restart <uuid>, which hands
        # the app the same link, are turned away without it ("Feature only enabled in developer
        # mode"). So the plugin's process, which runs the previous copy, is ended, and the app
        # starts the new one in its place a few seconds later.
        $since = Get-Date
        $prefix = $dest.TrimEnd('\') + '\'
        $old = @(Get-CimInstance Win32_Process -Filter "Name = 'statusai.exe'" -ErrorAction SilentlyContinue |
                 Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and $_.CommandLine -match '-pluginUUID' })
        if ($old.Count -eq 0) {
            Write-Host '  the plugin was not running; Stream Deck starts the new copy when it next starts the plugin'
        } else {
            foreach ($p in $old) {
                try { Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop; Write-Host ("  ended the plugin's process {0}, which had run since {1:yyyy-MM-dd HH:mm:ss}" -f $p.ProcessId, $p.CreationDate) }
                catch { Write-Host "  could not end the plugin's process $($p.ProcessId): $($_.Exception.Message)" -ForegroundColor Red }
            }
            $new = @()
            foreach ($i in 1..60) {
                Start-Sleep -Milliseconds 500
                $new = @(Get-CimInstance Win32_Process -Filter "Name = 'statusai.exe'" -ErrorAction SilentlyContinue |
                         Where-Object { $_.ExecutablePath -eq $exe -and $_.CreationDate -gt $since -and $_.CommandLine -match '-pluginUUID' })
                if ($new.Count -gt 0) { break }
            }
            if ($new.Count -eq 0) {
                Write-Host '  no process running the new copy came within 30 s: Stream Deck starts it when it is next started' -ForegroundColor Red
                return 4
            }
            Write-Host ("  the app started the plugin again, from the new copy: process {0}, {1:N1} s after the old one was ended" -f $new[0].ProcessId, ($new[0].CreationDate - $since).TotalSeconds)
        }
    }
    if ($isOwn -and $running -and -not $first -and $stale.Count -gt 0) {
        Write-Host '  the manifest or a picture changed, and Stream Deck reads those when it starts'
    }
    foreach ($old in @(Get-ChildItem -LiteralPath $dest -Filter 'statusai.exe.old.*')) {
        $gone = $false
        foreach ($i in 1..10) { try { Remove-Item -LiteralPath $old.FullName -Force -ErrorAction Stop; $gone = $true; break } catch { Start-Sleep -Milliseconds 300 } }
        if (-not $gone) { Write-Host "  $($old.Name) is still running and is removed by the next deploy" }
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
    if ($newHash -eq $oldHash) { Write-Host 'already deployed; nothing to do'; exit (Sync-Deck) }
}

if (-not $SkipTests) {
    Write-Host 'render tests   :'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $here '..\tests\Test-Renders.ps1') -Exe $Source -Cship $cship
    if ($LASTEXITCODE -ne 0) { Write-Host "The build fails the render tests (exit $LASTEXITCODE); nothing deployed." -ForegroundColor Red; exit 1 }
}

# with -WhatIf: what would happen to the status line's binary, then to the plugin's copy
if (-not $PSCmdlet.ShouldProcess($Target, $(if ($first) { "put $Source in place" } else { "replace with $Source" }))) { exit (Sync-Deck) }

# ------------------------------------------------------------------ a first deploy: copy, verify
if ($first) {
    $n = Copy-WithRetry $Source $Target
    $got = Hash $Target
    if ($n -gt 0 -and $got -eq $newHash) {
        Write-Host ("deployed       : attempt {0}, sha256 matches the build; the first at {1}" -f $n, $Target)
        Write-Host ("deployed at    : {0} (unix {1})" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz'), [DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
        exit (Sync-Deck)
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
    exit (Sync-Deck)
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
