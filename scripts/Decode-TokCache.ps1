# Dumps the contents of StatusAI's incremental token cache for one session, so the
# rendered numbers (four significant figures) can be checked against the raw totals.
#
#   ./Decode-TokCache.ps1 -Sid 66419393-007e-4955-838b-c95b669aeccd
#
# Format CTK2: magic, 5 main counters, 5 sub counters, the file/offset table, then the
# message.id and toolu_ dedup sets. A CTK1 magic means a pre-tool-call cache, which the
# binary discards and rebuilds on its next render.

param(
    [Parameter(Mandatory)] [string] $Sid,
    [string] $CacheDir = (Join-Path $env:LOCALAPPDATA 'StatusAI\tokens')
)

$p = Join-Path $CacheDir "$Sid.bin"
if (-not (Test-Path $p)) { throw "No cache at $p. Render the status line for that session first." }

$fs = [System.IO.File]::OpenRead($p)
$br = New-Object System.IO.BinaryReader($fs)
try {
    $magic = $br.ReadInt32()
    $tag = -join ([BitConverter]::GetBytes($magic) | % { [char]$_ })
    "cache : $p"
    "magic : 0x{0:X} ($tag)" -f $magic
    if ($tag -ne 'CTK2') { "  (not CTK2, so the binary will discard and rebuild this)" }

    $main = 1..5 | % { $br.ReadInt64() }
    $sub  = 1..5 | % { $br.ReadInt64() }
    $names = 'in', 'out', 'cache_write', 'cache_read', 'TOOL CALLS'
    ''
    '{0,-12} {1,14} {2,14} {3,14}' -f 'counter', 'main', 'sub', 'total'
    for ($i = 0; $i -lt 5; $i++) {
        '{0,-12} {1,14:N0} {2,14:N0} {3,14:N0}' -f $names[$i], $main[$i], $sub[$i], ($main[$i] + $sub[$i])
    }
    $mt = ($main[0..3] | Measure-Object -Sum).Sum
    $st = ($sub[0..3]  | Measure-Object -Sum).Sum
    '{0,-12} {1,14:N0} {2,14:N0} {3,14:N0}' -f 'TOKENS', $mt, $st, ($mt + $st)

    $nf = $br.ReadInt32()
    for ($i = 0; $i -lt $nf; $i++) { $null = $br.ReadString(); $null = $br.ReadInt64() }
    $nh = $br.ReadInt32(); for ($i = 0; $i -lt $nh; $i++) { $null = $br.ReadInt64() }
    $nt = $br.ReadInt32()
    ''
    "files tracked: $nf   message ids: $nh   tool ids: $nt"
}
finally { $br.Close(); $fs.Close() }
