# Independent implementation of StatusAI's token accounting, for verifying the binary.
# Shares no code with it. Sums main vs sub-agent tokens for one session.
#
#   ./Split-MainVsTree.ps1 -Sid 66419393-007e-4955-838b-c95b669aeccd
#
# The one correct rule: walk main + every subagents/**/agent-*.jsonl, keep assistant
# records carrying message.usage, and dedup on message.id with a single global set.
# Per-file dedup inflates by up to 26x (children hold verbatim copies of ancestor
# records); no dedup inflates ~2.2x (one API response is written once per content block).

param(
    [Parameter(Mandatory)] [string] $Sid,
    [string] $ProjectsRoot = (Join-Path $env:USERPROFILE '.claude\projects')
)

$main = Get-ChildItem -Path $ProjectsRoot -Filter "$Sid.jsonl" -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
if (-not $main) { throw "No transcript for session $Sid under $ProjectsRoot" }
$subs = Join-Path $main.DirectoryName "$Sid\subagents"

function Sum-Files($files, $seen) {
    $i = 0; $o = 0; $cc = 0; $cr = 0; $n = 0
    foreach ($f in $files) {
        foreach ($line in [System.IO.File]::ReadLines($f)) {
            if ($line.IndexOf('"usage"') -lt 0) { continue }
            try { $j = $line | ConvertFrom-Json } catch { continue }
            if ($j.type -ne 'assistant') { continue }
            $u = $j.message.usage; if (-not $u) { continue }
            $id = $j.message.id;   if (-not $id) { continue }
            if ($seen.Contains($id)) { continue }      # global dedup, spans the whole tree
            [void]$seen.Add($id)
            $i  += [int]$u.input_tokens;                $o  += [int]$u.output_tokens
            $cc += [int]$u.cache_creation_input_tokens; $cr += [int]$u.cache_read_input_tokens
            $n++
        }
    }
    [pscustomobject]@{ msgs = $n; input = $i; output = $o
                       cache_create = $cc; cache_read = $cr; total = ($i + $o + $cc + $cr) }
}

$seen = [System.Collections.Generic.HashSet[string]]::new()
$m = Sum-Files @($main.FullName) $seen          # main first, so shared records credit the trunk
$agentFiles = @()
if (Test-Path $subs) { $agentFiles = Get-ChildItem -Recurse -Filter 'agent-*.jsonl' $subs | % FullName }
$s = Sum-Files $agentFiles $seen

'MAIN  : ' + ($m | ConvertTo-Json -Compress)
'SUBS  : ' + ($s | ConvertTo-Json -Compress) + "  (agent files: $($agentFiles.Count))"
$tot = [pscustomobject]@{
    msgs = $m.msgs + $s.msgs; input = $m.input + $s.input; output = $m.output + $s.output
    cache_create = $m.cache_create + $s.cache_create; cache_read = $m.cache_read + $s.cache_read
    total = $m.total + $s.total }
'TREE  : ' + ($tot | ConvertTo-Json -Compress)
