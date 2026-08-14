$sid  = 'a5c44785-50ec-4ccc-b3c9-cf26bbb7bb08'
$proj = 'C:\Users\you\.claude\projects\C--Users-you-Downloads'
$main = Join-Path $proj "$sid.jsonl"
$subs = Join-Path $proj "$sid\subagents"

function Sum-Files($files, $seen) {
  $i=0;$o=0;$cc=0;$cr=0;$n=0
  foreach ($f in $files) {
    foreach ($line in [System.IO.File]::ReadLines($f)) {
      if ($line.IndexOf('"usage"') -lt 0) { continue }
      try { $j = $line | ConvertFrom-Json } catch { continue }
      if ($j.type -ne 'assistant') { continue }
      $u = $j.message.usage; if (-not $u) { continue }
      $id = $j.message.id; if (-not $id) { continue }
      if ($seen.Contains($id)) { continue }
      [void]$seen.Add($id)
      $i += [int]$u.input_tokens; $o += [int]$u.output_tokens
      $cc += [int]$u.cache_creation_input_tokens; $cr += [int]$u.cache_read_input_tokens
      $n++
    }
  }
  [pscustomobject]@{ msgs=$n; input=$i; output=$o; cache_create=$cc; cache_read=$cr; total=($i+$o+$cc+$cr) }
}

$seen = [System.Collections.Generic.HashSet[string]]::new()
$m = Sum-Files @($main) $seen
$agentFiles = @()
if (Test-Path $subs) { $agentFiles = Get-ChildItem -Recurse -Filter 'agent-*.jsonl' $subs | % FullName }
$s = Sum-Files $agentFiles $seen

'MAIN  : ' + ($m | ConvertTo-Json -Compress)
'SUBS  : ' + ($s | ConvertTo-Json -Compress) + "  (files: $($agentFiles.Count))"
$tot = [pscustomobject]@{ msgs=($m.msgs+$s.msgs); input=($m.input+$s.input); output=($m.output+$s.output); cache_create=($m.cache_create+$s.cache_create); cache_read=($m.cache_read+$s.cache_read); total=($m.total+$s.total) }
'TREE  : ' + ($tot | ConvertTo-Json -Compress)
