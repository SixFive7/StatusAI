param([string]$Sid)
$p = Join-Path $env:USERPROFILE ".claude\statusline-tokens\$Sid.bin"
$fs=[System.IO.File]::OpenRead($p); $br=New-Object System.IO.BinaryReader($fs)
$magic=$br.ReadInt32()
$main = 1..5 | % { $br.ReadInt64() }; $sub = 1..5 | % { $br.ReadInt64() }
$names='in','out','cache_write','cache_read','TOOL CALLS'
'magic: 0x{0:X}' -f $magic
'{0,-12} {1,14} {2,14} {3,14}' -f 'counter','main','sub','total'
for($i=0;$i -lt 5;$i++){'{0,-12} {1,14:N0} {2,14:N0} {3,14:N0}' -f $names[$i],$main[$i],$sub[$i],($main[$i]+$sub[$i])}
$mt=($main[0..3]|Measure-Object -Sum).Sum; $st=($sub[0..3]|Measure-Object -Sum).Sum
'{0,-12} {1,14:N0} {2,14:N0} {3,14:N0}' -f 'TOKENS',$mt,$st,($mt+$st)
$br.Close();$fs.Close()
