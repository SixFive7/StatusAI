<#
.SYNOPSIS
    Renders every fixture through a statusai.exe, offline, and compares the output byte for
    byte with tests/expected.

.DESCRIPTION
    A case in cases.json is one stdin payload (fixtures/payloads) rendered against one home
    (fixtures/homes) at one or more terminal widths. Each render runs in a fresh copy of its home
    under the scratch directory, with

      STATUSAI_OFFLINE = that copy   the limit rows, the euro rate, the account and the token
                                     cache all come from it; the registry cache, the usage lock
                                     and the network are never touched
      STATUSAI_WIDTH   = the width   unless the case has an "env", which then sets exactly the
                                     variables it names, STATUSAI_WIDTH and COLUMNS only, with
                                     {w} standing for the width; COLUMNS is cleared unless it is
                                     named
      HOME             = that copy   cship reads .config/cship.toml there: tests/cship.toml, which
                                     is the shipped config without the starship line

    and the working directory set to it, so the payloads' relative transcript paths resolve
    inside it. Nothing is written to tests/ unless -Update is given.

    The binary under test is staged beside a cship.exe in <scratch>/bin, because statusai runs
    the cship in its own directory. The expected renders were recorded with the cship version
    cases.json names; another version is reported, and may draw the model line differently.

    Runs under Windows PowerShell 5.1 and PowerShell 7. Exit code 0 when every render matches,
    1 when any differs or has no expected render, 2 when the run could not start.

.PARAMETER Exe
    The statusai.exe to test. Default: the output of `dotnet publish -c Release -r win-x64`
    in src/.

.PARAMETER Cship
    The cship.exe to render with. Default: the one beside -Exe, else the one on PATH.

.PARAMETER Case
    Only these cases; wildcards allowed, and a comma separates several. Default: every case.

.PARAMETER Update
    Write each render to tests/expected instead of comparing, and remove expected files that no
    case produces. For a deliberate change of output: review the diff before committing it.

.PARAMETER Show
    Print each render to the console, colours and all.

.PARAMETER OutDir
    Also write each render to this directory as <case>.w<width>.ansi.

.PARAMETER Scratch
    Where the staged binaries, the per-render homes and any failing output go.
    Default: .work/test-renders in the repository.

.EXAMPLE
    ./tests/Test-Renders.ps1 -Exe (Get-Command statusai).Source

.EXAMPLE
    ./tests/Test-Renders.ps1 -Case showcase -Show
#>
[CmdletBinding()]
param(
    [string]   $Exe,
    [string]   $Cship,
    [string[]] $Case = @('*'),
    [switch]   $Update,
    [switch]   $Show,
    [string]   $OutDir,
    [string]   $Scratch
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 leaves $PSScriptRoot empty in parameter defaults, so they are set here
$Tests    = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Exe)     { $Exe = Join-Path $Tests '..\src\bin\Release\net10.0-windows\win-x64\publish\statusai.exe' }
if (-not $Scratch) { $Scratch = Join-Path $Tests '..\.work\test-renders' }
# -File passes "a,b" as one string, so a comma separates cases as well
$Case     = @($Case | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Homes    = Join-Path $Tests 'fixtures\homes'
$Payloads = Join-Path $Tests 'fixtures\payloads'
$Expected = Join-Path $Tests 'expected'
$Utf8     = New-Object System.Text.UTF8Encoding($false)
$Sgr      = [regex]"$([char]27)\[[0-9;]*m"

class RunError : System.Exception { RunError([string] $m) : base($m) { } }
function Fail([string] $msg) { throw [RunError]::new($msg) }

# ------------------------------------------------------------------ the width, as a case sets it
# STATUSAI_WIDTH is the width unless the case has an "env". Then exactly the variables it names are
# set, with {w} replaced by the width, and a null leaves one unset. Only the two the binary takes
# its width from may be named.
function Get-WidthEnv([string] $name, $c, [int] $width) {
    $vars = @{}
    if (-not ($c.PSObject.Properties.Name -contains 'env')) { $vars['STATUSAI_WIDTH'] = [string] $width; return $vars }
    foreach ($p in $c.env.PSObject.Properties) {
        if ($p.Name -cne 'STATUSAI_WIDTH' -and $p.Name -cne 'COLUMNS') {
            Fail "case ${name}: env may name STATUSAI_WIDTH and COLUMNS, not $($p.Name)"
        }
        if ($null -ne $p.Value) { $vars[$p.Name] = ([string] $p.Value).Replace('{w}', [string] $width) }
    }
    $vars
}

# ------------------------------------------------------------------ one render
function Invoke-Render([string] $exePath, [string] $homeDir, [byte[]] $payload, [hashtable] $widthEnv) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exePath
    $psi.WorkingDirectory = $homeDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    # cleared first, so nothing from the shell running the tests reaches a render; Claude Code
    # sets COLUMNS and LINES for its hooks as well as for the status line
    foreach ($k in 'STATUSAI_OFFLINE', 'STATUSAI_WIDTH', 'COLUMNS', 'LINES', 'HOME', 'CLAUDE_HOME', 'STARSHIP_CONFIG', 'STARSHIP_SHELL') {
        [void] $psi.Environment.Remove($k)
    }
    $psi.Environment['STATUSAI_OFFLINE'] = $homeDir
    foreach ($k in $widthEnv.Keys) { $psi.Environment[$k] = $widthEnv[$k] }
    $psi.Environment['HOME'] = $homeDir
    $psi.Environment['CLAUDE_HOME'] = $homeDir
    $p = [System.Diagnostics.Process]::Start($psi)
    try {
        $p.StandardInput.BaseStream.Write($payload, 0, $payload.Length)
        $p.StandardInput.Close()
        $err = $p.StandardError.ReadToEndAsync()
        $ms = New-Object System.IO.MemoryStream
        $p.StandardOutput.BaseStream.CopyTo($ms)
        if (-not $p.WaitForExit(60000)) { $p.Kill(); Fail 'a render did not finish in 60 s' }
        [pscustomobject]@{ Out = $ms.ToArray(); Err = $err.Result; Code = $p.ExitCode }
    } finally { $p.Dispose() }
}

function Same([byte[]] $a, [byte[]] $b) {
    [Convert]::ToBase64String($a) -ceq [Convert]::ToBase64String($b)
}

function Show-Diff([byte[]] $want, [byte[]] $got) {
    $a = $Utf8.GetString($want) -split "`n"
    $b = $Utf8.GetString($got) -split "`n"
    for ($i = 0; $i -lt [Math]::Max($a.Count, $b.Count); $i++) {
        $x = if ($i -lt $a.Count) { $a[$i] } else { $null }
        $y = if ($i -lt $b.Count) { $b[$i] } else { $null }
        if ($x -ceq $y) { continue }
        $xs = if ($null -ne $x) { $Sgr.Replace($x, '') } else { '(no line)' }
        $ys = if ($null -ne $y) { $Sgr.Replace($y, '') } else { '(no line)' }
        if ($xs -ceq $ys) { Write-Host ("    line {0}: same text, different colours |{1}|" -f ($i + 1), $ys) }
        else {
            Write-Host ("    line {0} expected |{1}|" -f ($i + 1), $xs)
            Write-Host ("    line {0} actual   |{1}|" -f ($i + 1), $ys)
        }
    }
}

function Invoke-Main {
    # ---------------------------------------------------------------- the binaries under test
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
        Fail "No statusai.exe at $Exe. Build it (docs/development.md) or pass -Exe <path>."
    }
    $exePath = (Resolve-Path -LiteralPath $Exe).Path
    $cshipPath = $Cship
    if (-not $cshipPath) {
        $beside = Join-Path (Split-Path -Parent $exePath) 'cship.exe'
        if (Test-Path -LiteralPath $beside -PathType Leaf) { $cshipPath = $beside }
        else {
            $onPath = Get-Command cship.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($onPath) { $cshipPath = $onPath.Source }
        }
    }
    if (-not $cshipPath -or -not (Test-Path -LiteralPath $cshipPath -PathType Leaf)) {
        Fail 'No cship.exe beside -Exe or on PATH. Pass -Cship <path>.'
    }
    $cshipPath = (Resolve-Path -LiteralPath $cshipPath).Path

    New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
    $scratchDir = (Resolve-Path -LiteralPath $Scratch).Path
    $bin = Join-Path $scratchDir 'bin'
    if (Test-Path -LiteralPath $bin) { Remove-Item -LiteralPath $bin -Recurse -Force }
    New-Item -ItemType Directory -Path $bin | Out-Null
    Copy-Item -LiteralPath $exePath   -Destination (Join-Path $bin 'statusai.exe')
    Copy-Item -LiteralPath $cshipPath -Destination (Join-Path $bin 'cship.exe')
    $staged = Join-Path $bin 'statusai.exe'

    $manifest = Get-Content -LiteralPath (Join-Path $Tests 'cases.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $cshipVersion = ((& (Join-Path $bin 'cship.exe') --version) -join ' ').Trim()
    Write-Host "statusai    : $exePath"
    Write-Host "  sha256    : $((Get-FileHash -Algorithm SHA256 -LiteralPath $exePath).Hash.ToLowerInvariant())"
    Write-Host "cship       : $cshipPath ($cshipVersion)"
    if ($cshipVersion -ne "cship $($manifest.cship)") {
        Write-Host "  the expected renders were recorded with cship $($manifest.cship); the model line may differ" -ForegroundColor Yellow
    }

    # ---------------------------------------------------------------- every case
    $runRoot = Join-Path $scratchDir 'run'
    $failDir = Join-Path $scratchDir 'actual'
    foreach ($d in $runRoot, $failDir) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    if ($OutDir) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
    if ($Update) { New-Item -ItemType Directory -Force -Path $Expected | Out-Null }

    $selected = @($manifest.cases.PSObject.Properties | Where-Object {
        $n = $_.Name; @($Case | Where-Object { $n -like $_ }).Count -gt 0 })
    if ($selected.Count -eq 0) { Fail "No case matches $($Case -join ', ')." }

    $produced = @{}
    $pass = 0; $fail = 0; $new = 0; $written = 0
    foreach ($prop in $selected) {
        $name = $prop.Name; $c = $prop.Value
        $src = Join-Path $Homes $c.home
        $payloadPath = Join-Path $Payloads ($c.payload + '.json')
        if (-not (Test-Path -LiteralPath $src -PathType Container)) { Fail "case ${name}: no home $src" }
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) { Fail "case ${name}: no payload $payloadPath" }
        $payload = [System.IO.File]::ReadAllBytes($payloadPath)
        foreach ($width in $c.widths) {
            $key = "$name.w$width"
            $produced["$key.ansi"] = $true
            $homeDir = Join-Path $runRoot $key
            Copy-Item -LiteralPath $src -Destination $homeDir -Recurse
            New-Item -ItemType Directory -Path (Join-Path $homeDir '.config') | Out-Null
            Copy-Item -LiteralPath (Join-Path $Tests 'cship.toml') -Destination (Join-Path $homeDir '.config\cship.toml')

            $r = Invoke-Render $staged $homeDir $payload (Get-WidthEnv $name $c $width)
            if ($Show) { [Console]::Out.Write("`n== $key`n" + $Utf8.GetString($r.Out) + "`n") }
            if ($OutDir) { [System.IO.File]::WriteAllBytes((Join-Path $OutDir "$key.ansi"), $r.Out) }
            $expPath = Join-Path $Expected "$key.ansi"
            if ($Update) {
                if (-not ((Test-Path -LiteralPath $expPath) -and (Same ([System.IO.File]::ReadAllBytes($expPath)) $r.Out))) {
                    [System.IO.File]::WriteAllBytes($expPath, $r.Out); $written++
                }
                $pass++
            } elseif (-not (Test-Path -LiteralPath $expPath)) {
                $new++
                Write-Host "NEW   $key  (no expected render; -Update records it)" -ForegroundColor Yellow
            } else {
                $want = [System.IO.File]::ReadAllBytes($expPath)
                if (Same $want $r.Out) { $pass++ }
                else {
                    $fail++
                    New-Item -ItemType Directory -Force -Path $failDir | Out-Null
                    [System.IO.File]::WriteAllBytes((Join-Path $failDir "$key.ansi"), $r.Out)
                    Write-Host "FAIL  $key  (exit $($r.Code)): $($c.checks)" -ForegroundColor Red
                    Show-Diff $want $r.Out
                    if ($r.Err) { Write-Host "    stderr: $($r.Err.Trim())" }
                    continue      # its home stays under run/ for a look
                }
            }
            Remove-Item -LiteralPath $homeDir -Recurse -Force
        }
    }

    # expected renders that no case produces any more
    $stale = @()
    if ((Test-Path -LiteralPath $Expected) -and $Case.Count -eq 1 -and $Case[0] -eq '*') {
        $stale = @(Get-ChildItem -LiteralPath $Expected -Filter '*.ansi' | Where-Object { -not $produced.ContainsKey($_.Name) })
        foreach ($f in $stale) {
            if ($Update) { Remove-Item -LiteralPath $f.FullName; Write-Host "removed stale $($f.Name)" }
            else { Write-Host "STALE $($f.Name)  (no case renders it; -Update removes it)" -ForegroundColor Yellow }
        }
    }

    $total = $pass + $fail + $new
    if ($Update) {
        Write-Host "$total renders recorded; $written expected files written or changed, $($stale.Count) removed"
        return 0
    }
    $bad = $fail -or $new -or $stale.Count
    Write-Host "$total renders: $pass pass, $fail fail, $new without an expected render" -ForegroundColor $(if ($bad) { 'Red' } else { 'Green' })
    if ($fail) { Write-Host "failing output is in $failDir" }
    if ($bad) { return 1 }
    return 0
}

# The renders are UTF-8 and so are the diffs: print them as such, and put the console back after.
$prevEncoding = $null
try { $prevEncoding = [Console]::OutputEncoding; [Console]::OutputEncoding = $Utf8 } catch { $prevEncoding = $null }
try {
    $code = Invoke-Main
} catch [RunError] {
    Write-Host $_.Exception.Message -ForegroundColor Red
    $code = 2
} finally {
    if ($prevEncoding) { try { [Console]::OutputEncoding = $prevEncoding } catch { } }
}
exit $code
