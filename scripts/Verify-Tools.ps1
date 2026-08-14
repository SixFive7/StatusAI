# Independent implementation of StatusAI's tool-call counting, for verifying the binary.
# Shares no code with it. Counts main vs sub-agent tool calls for one session.
#
#   ./Verify-Tools.ps1 -Sid 66419393-007e-4955-838b-c95b669aeccd
#
# Dedup is on the tool_use block's own toolu_ id -- globally unique, and stable across
# the verbatim copies that child transcripts hold of ancestor records. Note this is the
# opposite key to the token walk: message.id is right for usage and useless for blocks,
# because one API response is written once per content block and repeats its message.id.

param(
    [Parameter(Mandatory)] [string] $Sid,
    [string] $ProjectsRoot = (Join-Path $env:USERPROFILE '.claude\projects')
)

$main = Get-ChildItem -Path $ProjectsRoot -Filter "$Sid.jsonl" -Recurse -File -ErrorAction SilentlyContinue |
        Select-Object -First 1
if (-not $main) { throw "No transcript for session $Sid under $ProjectsRoot" }
$subs = Join-Path $main.DirectoryName "$Sid\subagents"

function Count-Tools($files, $seen) {
    $n = 0
    foreach ($f in $files) {
        foreach ($line in [System.IO.File]::ReadLines($f)) {
            if ($line.IndexOf('"tool_use"') -lt 0) { continue }
            try { $j = $line | ConvertFrom-Json } catch { continue }
            if ($j.type -ne 'assistant' -or -not $j.message) { continue }
            foreach ($b in $j.message.content) {
                if ($b.type -eq 'tool_use' -and $b.id -and $seen.Add($b.id)) { $n++ }
            }
        }
    }
    $n
}

$seen = [System.Collections.Generic.HashSet[string]]::new()
$m = Count-Tools @($main.FullName) $seen        # main first, so shared records credit the trunk
$agentFiles = @()
if (Test-Path $subs) { $agentFiles = Get-ChildItem -Recurse -Filter 'agent-*.jsonl' $subs | % FullName }
$s = Count-Tools $agentFiles $seen

"tool calls  main=$m  sub=$s  total=$($m + $s)   (agent files: $($agentFiles.Count))"
