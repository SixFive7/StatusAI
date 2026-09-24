<#
.SYNOPSIS
    Builds the release zip of one version, render-tested, with its SHA-256 and its release notes.
    Publishes nothing.

.DESCRIPTION
    1. Refuse unless -Version is x.y.z and CHANGELOG.md has a "## <version> - <date>" heading
       (a hyphen or an em dash), and unless the cship this script tells a friend to download is
       the one the render tests pin (tests/cases.json) and the one the install guide fetches
       (docs/guide/install.md): its version, URL and SHA-256.
    2. dotnet publish src/cship-usage.csproj -c Release -r win-x64 -o <OutDir>/build
       -p:Version=<version>, into a fresh directory, with no build server left running after it.
       The exe records the version, and the commit the SDK reads from git.
    3. Run tests/Test-Renders.ps1 against that exe, in the same PowerShell as this script, with
       its scratch under <OutDir>. Refuse if any render differs, or if the cship it would render
       with is not the pinned download, byte for byte.
    4. Stage cship-usage.exe, cship.toml and starship.toml (from config/) and a plain-text ASCII
       README.txt in <OutDir>/StatusAI-<version>-win-x64/, and zip them in that order at the root
       of StatusAI-<version>-win-x64.zip. The zip is written with System.IO.Compression, so entry
       names use forward slashes (Compress-Archive under Windows PowerShell 5.1 writes
       backslashes), and every entry is dated the day of the CHANGELOG heading. The zip is read
       back and every entry checked against its source by SHA-256.
    5. Write <zip>.sha256, one line "<sha256>  <name>" as sha256sum writes it, and
       notes-v<version>.md: the CHANGELOG section of the version without its heading, every
       relative link pointed at the tag on GitHub, and a footer pointing at the install guide,
       with every paragraph on one line because GitHub shows each line break of release notes. A
       relative link to a path the repository does not have refuses.

    Nothing is tagged, pushed or uploaded; the last lines print the gh command that would publish
    the release. A working tree with uncommitted changes is reported, not refused: the exe records
    the commit it was built at, and a release should be built from the commit it is tagged at. A
    run that refuses or fails removes the version's zip, .sha256, notes and staging directory,
    its own or an earlier run's, so nothing is left behind that this run did not check.

    Runs under Windows PowerShell 5.1 and PowerShell 7. Exit code 0 when packaged, 1 when it
    refused (the version, the CHANGELOG, the cship pin, the render tests or a link in the notes),
    2 when the build or the packaging itself failed.

.PARAMETER Version
    The version to package, x.y.z. CHANGELOG.md must have a section for it.

.PARAMETER OutDir
    Where the build, the test scratch, the zip, its .sha256 and the notes go.
    Default: .work/release in the repository.

.PARAMETER Cship
    The cship.exe the render tests use. It must be byte-identical to the pinned download.
    Default: the one on PATH.

.EXAMPLE
    ./scripts/Package.ps1 -Version 0.1.0
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $OutDir,
    [string] $Cship
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# The cship a friend downloads beside cship-usage.exe: the version the render tests pin, from its
# own release. install.md fetches the same URL and checks the same SHA-256.
$CshipVersion = '1.8.0'
$CshipUrl     = 'https://github.com/stephenleo/cship/releases/download/v1.8.0/cship-x86_64-pc-windows-msvc.exe'
$CshipSha256  = 'fc0b77fb9a43a72ae3040ee956674fb2a94523dacdfb1ae411810fc0d7b562c0'
$GitHubRepo   = 'SixFive7/StatusAI'
$Site         = "https://github.com/$GitHubRepo"
$GuideUrl     = "$Site/blob/main/docs/guide/install.md"

$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = (Resolve-Path -LiteralPath (Join-Path $here '..')).Path
if (-not $OutDir) { $OutDir = Join-Path $root '.work\release' }
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$Utf8   = New-Object System.Text.UTF8Encoding($false)
$Nl     = [System.Globalization.CultureInfo]::GetCultureInfo('nl-NL')
$Outputs = @()   # this version's artifacts: removed before a run, and again when it does not finish

class Refusal : System.Exception { Refusal([string] $m) : base($m) { } }
function Refuse([string] $msg) { throw [Refusal]::new($msg) }

function Hash([string] $p) { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLowerInvariant() }
function StreamHash([System.IO.Stream] $s) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($sha.ComputeHash($s)) -replace '-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
function ReadText([string] $p) { [System.IO.File]::ReadAllText($p, $Utf8).Replace("`r`n", "`n") }
function WriteText([string] $p, [string] $text) { [System.IO.File]::WriteAllText($p, $text, $Utf8) }
function Fill([string] $template, [hashtable] $values) {
    $t = $template.Replace("`r`n", "`n")
    foreach ($k in $values.Keys) { $t = $t.Replace('{' + $k + '}', [string] $values[$k]) }
    $t
}
function Say([string] $label, [string] $value) { Write-Host ('{0,-15}: {1}' -f $label, $value) }

# A path as the release command should name it: relative with forward slashes inside the
# repository, so it works from the repository root in PowerShell and in bash alike.
function CommandPath([string] $p) {
    $prefix = $root.TrimEnd('\') + '\'
    if ($p.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $p.Substring($prefix.Length).Replace('\', '/') }
    return '"' + $p + '"'
}

# ---------------------------------------------------------------------- release notes
# Every relative Markdown link in the CHANGELOG section, pointed at the tag on GitHub: blob for a
# file, tree for a directory, raw for an image. A link to a path that does not exist is collected
# in $missing. CHANGELOG.md sits at the repository root, so its links are relative to the root.
function Convert-Target([string] $target, [bool] $image, [string] $tag, [System.Collections.Generic.List[string]] $missing) {
    if ($target -match '^[A-Za-z][A-Za-z0-9+.-]*:' -or $target.StartsWith('//')) { return $target }
    $t = $target
    if ($t.StartsWith('#')) { $t = 'CHANGELOG.md' + $t }
    $t = $t.TrimStart('/')
    while ($t.StartsWith('./')) { $t = $t.Substring(2) }
    $anchor = ''
    $i = $t.IndexOf('#')
    if ($i -ge 0) { $anchor = $t.Substring($i); $t = $t.Substring(0, $i) }
    if (@($t -split '/' | Where-Object { $_ -eq '..' }).Count -gt 0) { $missing.Add("$target (outside the repository)"); return $target }
    $full = if ($t.Length -gt 0) { Join-Path $root ([Uri]::UnescapeDataString($t).Replace('/', '\')) } else { $root }
    if (Test-Path -LiteralPath $full -PathType Container) { $kind = 'tree' }
    elseif (Test-Path -LiteralPath $full -PathType Leaf) { $kind = if ($image) { 'raw' } else { 'blob' } }
    else { $missing.Add($target); return $target }
    return "$Site/$kind/$tag/$t$anchor"
}

function Convert-Links([string] $md, [string] $tag, [System.Collections.Generic.List[string]] $missing) {
    # [text](target "title") and ![alt](target), with one level of brackets inside the text
    $inline = [regex] '(?<bang>!?)\[(?<text>(?:[^\[\]]|\[[^\[\]]*\])*)\]\((?<target>[^()\s]+)(?<title>\s+"[^"]*")?\)'
    $md = $inline.Replace($md, [System.Text.RegularExpressions.MatchEvaluator] {
        param($m)
        $new = Convert-Target $m.Groups['target'].Value ($m.Groups['bang'].Value -eq '!') $tag $missing
        $m.Groups['bang'].Value + '[' + $m.Groups['text'].Value + '](' + $new + $m.Groups['title'].Value + ')'
    })
    # [label]: target
    $reference = [regex] '(?m)^(?<lead> {0,3}\[[^\]]+\]:[ \t]*)(?<target>\S+)'
    $reference.Replace($md, [System.Text.RegularExpressions.MatchEvaluator] {
        param($m)
        $m.Groups['lead'].Value + (Convert-Target $m.Groups['target'].Value $false $tag $missing)
    })
}

# GitHub shows release notes the way it shows comments: every line break in the source is a line
# break on the page. The CHANGELOG is wrapped at 100 columns, so each paragraph and list item is
# joined back into one line. Blank lines, headings, rules, table rows, list items and fenced code
# keep their own lines.
function Join-SoftWraps([string] $md) {
    $out = New-Object 'System.Collections.Generic.List[string]'
    $fence = $false
    foreach ($line in $md.Replace("`r", '').Split("`n")) {
        $t = $line.TrimStart()
        if ($t.StartsWith('```')) { $fence = -not $fence; $out.Add($line); continue }
        if ($fence -or $out.Count -eq 0) { $out.Add($line); continue }
        $prev = $out[$out.Count - 1].TrimStart()
        $newBlock = $t.Length -eq 0 -or $t -match '^(#|>|\||---|\*\*\*|[-*+] |[0-9]+[.)] )'
        $prevEnds = $prev.Length -eq 0 -or $prev -match '^(#|\||---|\*\*\*|```)'
        if ($newBlock -or $prevEnds) { $out.Add($line) }
        else { $out[$out.Count - 1] = $out[$out.Count - 1].TrimEnd() + ' ' + $t }
    }
    return ($out -join "`n")
}

# ---------------------------------------------------------------------- README.txt and the notes' footer
$ReadmeTemplate = @'
StatusAI {VERSION} for Windows x64
{SITE}

A status line for Claude Code: the tokens of the whole agent tree, the usage
limits, the account, and the session's time and cost. It shows in Claude
Code's terminal interface; the VS Code extension's chat panel shows no status
line.

In this zip
  cship-usage.exe  the status line. x64 only, and unsigned.
  cship.toml       the configuration of cship, which draws the model line and
                   the context bar. It goes in %USERPROFILE%\.config.
  starship.toml    optional: the configuration of starship, for a prompt line
                   above the model line.
  README.txt       this file.

cship-usage.exe runs on top of cship {CSHIP}, which is not in this zip. Download
it from cship's own release:

  {URL}
  SHA-256 {SHA}

and save it as cship.exe in the same folder as cship-usage.exe. cship-usage
runs the cship.exe next to it before any other, and it won't find the download
under its original name. cship needs the Microsoft Visual C++ Redistributable
(x64), which most PCs already have:

  https://aka.ms/vc14/vc_redist.x64.exe

Neither program is signed. Where Windows 11's Smart App Control is on, it
blocks them both; the install guide says what to do.

Installing, in brief
  1. Put cship-usage.exe in %USERPROFILE%\.local\bin. That folder must be on
     PATH; Claude Code's native installer keeps claude.exe there.
  2. Download cship as above, check its SHA-256, and save it in that same
     folder as cship.exe.
  3. Copy cship.toml to %USERPROFILE%\.config\cship.toml, unless you already
     have one.
  4. Add this to %USERPROFILE%\.claude\settings.json, inside its outer braces:
       "statusLine": { "type": "command", "command": "cship-usage", "refreshInterval": 60 }
     There is no width to set: Claude Code 2.1.153 and later pass the terminal's.
  5. Restart Claude Code.

The install guide does steps 1 to 3 with one PowerShell block, checks the hash
and that cship starts, and says what to expect, what to do when something is
missing, and how to uninstall:
  {GUIDE}

Do not run cship's own installer or "cship uninstall": both delete the
statusLine setting.

cship is licensed under Apache-2.0: https://github.com/stephenleo/cship
'@

$NotesFooter = @'

---

**Install:** download `{ZIP}` below and follow the
[install guide]({SITE}/blob/{TAG}/docs/guide/install.md). It puts `cship-usage.exe` in place and
fetches [cship](https://github.com/stephenleo/cship) {CSHIP} beside it from cship's own release,
checked against a pinned SHA-256. `{ZIP}.sha256` holds the SHA-256 of the zip.

**Earlier changes** are in the [changelog]({SITE}/blob/{TAG}/CHANGELOG.md).
'@

function Invoke-Main {
    # ------------------------------------------------------------------ what is being released
    if (-not $Version) { Refuse 'Pass -Version x.y.z.' }
    if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+\z') { Refuse "-Version must be x.y.z, three numbers; got '$Version'." }
    $tag   = "v$Version"
    $name  = "StatusAI-$Version-win-x64"
    $build = Join-Path $OutDir 'build'
    $stage = Join-Path $OutDir $name
    $zip   = Join-Path $OutDir "$name.zip"
    $sum   = "$zip.sha256"
    $notes = Join-Path $OutDir "notes-$tag.md"

    # a refused or failed run leaves nothing of this version behind that could pass for its result
    $script:Outputs = @($stage, $zip, $sum, $notes)
    foreach ($p in @($build) + $Outputs) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force } }

    $lines = (ReadText (Join-Path $root 'CHANGELOG.md')).Split("`n")
    $heading = [regex] ('^## ' + [regex]::Escape($Version) + ' [-' + [char] 0x2014 + '] (?<rest>.*)$')
    $at = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($heading.IsMatch($lines[$i])) { $at = $i; break } }
    if ($at -lt 0) { Refuse "CHANGELOG.md has no '## $Version - <date>' heading (a hyphen or an em dash). Write the release's entry first." }
    $end = $lines.Count
    for ($i = $at + 1; $i -lt $lines.Count; $i++) { if ($lines[$i].StartsWith('## ')) { $end = $i; break } }
    $section = ''
    if ($end - $at -gt 1) { $section = ($lines[($at + 1)..($end - 1)] -join "`n").Trim() }
    if ($section.Length -eq 0) { Refuse "The CHANGELOG section for $Version is empty." }
    $missing = New-Object 'System.Collections.Generic.List[string]'
    $body = Convert-Links $section $tag $missing
    if ($missing.Count -gt 0) { Refuse "The CHANGELOG section for $Version links to paths the repository does not have: $($missing -join ', ')." }

    # the day the release is dated, which every zip entry carries, so that the same files make the same zip
    $stamp = $null
    if ($heading.Match($lines[$at]).Groups['rest'].Value -match '^(?<y>[0-9]{4})-(?<m>[0-9]{2})-(?<d>[0-9]{2})') {
        $stamp = [DateTimeOffset]::new([int] $Matches['y'], [int] $Matches['m'], [int] $Matches['d'], 12, 0, 0, [TimeSpan]::Zero)
    }

    # the cship a friend is told to fetch is the one the renders were recorded with, everywhere
    $cases = Get-Content -LiteralPath (Join-Path $root 'tests\cases.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string] $cases.cship -ne $CshipVersion) {
        Refuse "tests/cases.json pins cship $($cases.cship), and this script tells a friend to download $CshipVersion. Change the constants at the top of this script together with the install guide."
    }
    $guide = ReadText (Join-Path $root 'docs\guide\install.md')
    foreach ($s in $CshipUrl, $CshipSha256) {
        if ($guide.IndexOf($s, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Refuse "docs/guide/install.md does not name $s, so it would fetch a different cship from the one this release pins."
        }
    }

    if (-not $Cship) {
        $onPath = Get-Command cship.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $onPath) { Refuse "No cship.exe on PATH for the render tests. Download $CshipUrl and pass -Cship <path>." }
        $Cship = $onPath.Source
    }
    if (-not (Test-Path -LiteralPath $Cship -PathType Leaf)) { Refuse "No cship.exe at $Cship." }
    $cshipPath = (Resolve-Path -LiteralPath $Cship).Path
    $cshipHash = Hash $cshipPath
    if ($cshipHash -ne $CshipSha256) {
        Refuse "The render tests would run with $cshipPath (sha256 $cshipHash), not the cship $CshipVersion a friend downloads (sha256 $CshipSha256). Download $CshipUrl and pass -Cship <path>."
    }

    Say 'version' "$Version (tag $tag)"
    Say 'cship' "$cshipPath (the pinned $CshipVersion download, by sha256)"

    # git: the commit the exe will record, and whether the working tree is that commit
    $git = Get-Command git.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($git) {
        $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try {
            $head   = ((& $git.Source -C $root rev-parse --short HEAD 2>$null) -join ' ').Trim()
            $dirty  = @(& $git.Source -C $root status --porcelain 2>$null).Count
            $tagged = @(& $git.Source -C $root tag -l $tag 2>$null).Count
        } finally { $ErrorActionPreference = $prev }
        Say 'commit' $head
        if ($dirty -gt 0) {
            Write-Host "  the working tree has $dirty uncommitted change(s): the exe records $head, and the zip may match no commit. Build a release from its commit." -ForegroundColor Yellow
        }
        if ($tagged -gt 0) { Write-Host "  $tag already exists as a tag." -ForegroundColor Yellow }
    }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    # ------------------------------------------------------------------ build
    Say 'build' $build
    # --disable-build-servers: no MSBuild node or compiler server outlives the build
    & dotnet publish (Join-Path $root 'src\cship-usage.csproj') -c Release -r win-x64 -o $build "-p:Version=$Version" --disable-build-servers --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }
    $exe = Join-Path $build 'cship-usage.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "dotnet publish wrote no $exe." }
    $vi = (Get-Item -LiteralPath $exe).VersionInfo
    if ($vi.FileVersion -ne "$Version.0" -or $vi.ProductVersion -notmatch ('^' + [regex]::Escape($Version) + '(\+|$)')) {
        throw "The build is not $Version`: FileVersion $($vi.FileVersion), ProductVersion $($vi.ProductVersion)."
    }
    Say 'cship-usage' "FileVersion $($vi.FileVersion), ProductVersion $($vi.ProductVersion)"

    # ------------------------------------------------------------------ render tests
    $ps = (Get-Process -Id $PID).Path
    if (-not $ps -or @('powershell.exe', 'pwsh.exe') -notcontains (Split-Path -Leaf $ps).ToLowerInvariant()) { $ps = 'powershell.exe' }
    Say 'render tests' $ps
    & $ps -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\Test-Renders.ps1') -Exe $exe -Cship $cshipPath -Scratch (Join-Path $OutDir 'test-renders')
    if ($LASTEXITCODE -ne 0) { Refuse "The build fails the render tests (exit $LASTEXITCODE); nothing packaged." }

    # ------------------------------------------------------------------ stage
    New-Item -ItemType Directory -Path $stage | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $stage 'cship-usage.exe')
    Copy-Item -LiteralPath (Join-Path $root 'config\cship.toml') -Destination (Join-Path $stage 'cship.toml')
    Copy-Item -LiteralPath (Join-Path $root 'config\starship.toml') -Destination (Join-Path $stage 'starship.toml')
    $readme = Fill $ReadmeTemplate @{ VERSION = $Version; SITE = $Site; CSHIP = $CshipVersion; URL = $CshipUrl; SHA = $CshipSha256; GUIDE = $GuideUrl }
    if ($readme -match '[^\x00-\x7F]') { throw 'README.txt is not ASCII.' }
    WriteText (Join-Path $stage 'README.txt') ($readme + "`n")
    $files = @('cship-usage.exe', 'cship.toml', 'starship.toml', 'README.txt')
    if (-not $stamp) { $stamp = [DateTimeOffset]::new((Get-Item -LiteralPath $exe).LastWriteTime) }

    # ------------------------------------------------------------------ zip, then read it back
    # The compression types are named as strings: Windows PowerShell 5.1 loads their assembly only
    # at Add-Type, after the script is parsed.
    Add-Type -AssemblyName System.IO.Compression
    $fs = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
    try {
        $za = New-Object -TypeName 'System.IO.Compression.ZipArchive' -ArgumentList $fs, 'Create'
        try {
            foreach ($f in $files) {
                $entry = $za.CreateEntry($f, 'Optimal')
                $entry.LastWriteTime = $stamp
                $bytes = [System.IO.File]::ReadAllBytes((Join-Path $stage $f))
                $es = $entry.Open()
                try { $es.Write($bytes, 0, $bytes.Length) } finally { $es.Dispose() }
            }
        } finally { $za.Dispose() }
    } finally { $fs.Dispose() }

    $listing = @()
    $fs = [System.IO.File]::OpenRead($zip)
    try {
        $za = New-Object -TypeName 'System.IO.Compression.ZipArchive' -ArgumentList $fs, 'Read'
        try {
            $names = @($za.Entries | ForEach-Object { $_.FullName })
            if (($names -join '|') -cne ($files -join '|')) { throw "The zip holds $($names -join ', '), not $($files -join ', ')." }
            foreach ($e in $za.Entries) {
                $es = $e.Open()
                try { $h = StreamHash $es } finally { $es.Dispose() }
                if ($h -ne (Hash (Join-Path $stage $e.FullName))) { throw "$($e.FullName) in the zip differs from $stage\$($e.FullName)." }
                $listing += '{0} ({1} bytes)' -f $e.FullName, $e.Length.ToString('N0', $Nl)
            }
        } finally { $za.Dispose() }
    } finally { $fs.Dispose() }

    $zipHash = Hash $zip
    WriteText $sum "$zipHash  $name.zip`n"

    # ------------------------------------------------------------------ release notes
    $footer = Fill $NotesFooter @{ ZIP = "$name.zip"; SITE = $Site; TAG = $tag; CSHIP = $CshipVersion }
    WriteText $notes ((Join-SoftWraps ($body + "`n" + $footer)) + "`n")

    # ------------------------------------------------------------------ report
    $size = (Get-Item -LiteralPath $zip).Length
    Say 'packaged' $zip
    Say '  size' ('{0} bytes ({1} MiB)' -f $size.ToString('N0', $Nl), ($size / 1MB).ToString('N2', $Nl))
    Say '  sha256' "$zipHash (in $name.zip.sha256)"
    Say '  entries' ($listing -join ', ')
    Say '  checked' 'every entry read back from the zip matches its source by sha256'
    Say 'notes' $notes
    $publish = 'gh release create {0} {1} {2} --repo {3} --title "StatusAI {4}" --notes-file {5} --target main' -f $tag, (CommandPath $zip), (CommandPath $sum), $GitHubRepo, $Version, (CommandPath $notes)
    Write-Host ''
    Write-Host 'Nothing is published. To publish, from the repository root once the release commit is pushed:'
    Write-Host "  $publish"
}

try {
    Invoke-Main
    $code = 0
} catch [Refusal] {
    Write-Host $_.Exception.Message -ForegroundColor Red
    $code = 1
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    $code = 2
}
if ($code -ne 0) {
    foreach ($p in $Outputs) { if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction SilentlyContinue } }
}
exit $code
