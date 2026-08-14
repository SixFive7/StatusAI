param([string]$Sid)
$proj = 'C:\Users\you\.claude\projects\C--Users-you-Downloads'
$main = Join-Path $proj "$Sid.jsonl"
$subs = Join-Path $proj "$Sid\subagents"
$seen = [System.Collections.Generic.HashSet[string]]::new()
function Tools($files, $seen) {
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
$m = Tools @($main) $seen
$agents = @(); if (Test-Path $subs) { $agents = Get-ChildItem -Recurse -Filter 'agent-*.jsonl' $subs | % FullName }
$s = Tools $agents $seen
"tool calls  main=$m  sub=$s  total=$($m+$s)   (agent files: $($agents.Count))"
