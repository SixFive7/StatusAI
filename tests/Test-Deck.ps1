<#
.SYNOPSIS
    Runs statusai.exe as a Stream Deck plugin against a stand-in for the app, offline, and checks
    what it sends, what a key press does, and what it does when the connection ends.

.DESCRIPTION
    The Stream Deck app talks to a plugin over a websocket on a loopback port. This script is the
    app's side of that: it listens on a port the system picks, starts the binary under test the
    way the app starts a plugin (-port, -pluginUUID, -registerEvent, -info), answers the opening
    handshake and then plays one scenario per case. The websocket client in src/Deck.cs is
    written by hand, so the cases go through the parts of the protocol one by one: frames with
    7, 16 and 64 bit lengths in both directions, a message in fragments, ping and pong, a close
    from either side, a connection that drops, and an app that is not there or answers wrongly.

    Everything runs offline. Each plugin gets a fresh copy of the `shot` home as STATUSAI_OFFLINE,
    so the key it draws is tests/expected/key.svg, the registry cache, the usage lock and the
    network are never touched, a key press starts nothing and a refresh fetches nothing: the
    plugin reports what it would have run instead. No terminal is ever opened by these tests.

    Runs under Windows PowerShell 5.1 and PowerShell 7. Exit code 0 when every case passes, 1
    when any fails, 2 when the run could not start.

.PARAMETER Exe
    The statusai.exe to test. Default: the output of `dotnet publish -c Release -r win-x64`
    in src/.

.PARAMETER Case
    Only these cases; wildcards allowed, and a comma separates several. Default: every case.

.PARAMETER Scratch
    Where the homes the plugins run in go. Default: .work/test-deck in the repository.

.EXAMPLE
    ./tests/Test-Deck.ps1 -Exe .work/publish/statusai.exe

.EXAMPLE
    ./tests/Test-Deck.ps1 -Case 'a ping*', 'the app closes*'
#>
[CmdletBinding()]
param(
    [string]   $Exe,
    [string[]] $Case = @('*'),
    [string]   $Scratch
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

$Tests = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Exe)     { $Exe = Join-Path $Tests '..\src\bin\Release\net10.0-windows\win-x64\publish\statusai.exe' }
if (-not $Scratch) { $Scratch = Join-Path $Tests '..\.work\test-deck' }
$Case = @($Case | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Utf8 = New-Object System.Text.UTF8Encoding($false)
$Uuid = 'TESTTESTTESTTESTTESTTESTTESTTEST'
$Action = 'com.sixfive7.statusai.key'
$NoBytes = New-Object byte[] 0

class Ouch : System.Exception { Ouch([string] $m) : base($m) { } }
function Fail([string] $msg) { throw [Ouch]::new($msg) }
function Need([bool] $ok, [string] $what) { if (-not $ok) { Fail $what } }

# ------------------------------------------------------------------ the plugin under test
$script:Open = New-Object System.Collections.ArrayList   # every session a case opened, for the clean-up
$script:RunRoot = $null
$script:ExePath = $null
$script:HomeNo = 0

function New-Home {
    $script:HomeNo++
    $h = Join-Path $script:RunRoot ('home{0}' -f $script:HomeNo)
    Copy-Item -LiteralPath (Join-Path $Tests 'fixtures\homes\shot') -Destination $h -Recurse
    New-Item -ItemType Directory -Path (Join-Path $h '.claude\projects\p') -Force | Out-Null
    $h
}

function Start-Statusai([string] $arguments, [string] $homeDir) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:ExePath
    $psi.Arguments = $arguments
    $psi.WorkingDirectory = $homeDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($k in 'STATUSAI_OFFLINE', 'STATUSAI_WIDTH', 'COLUMNS', 'LINES', 'HOME', 'CLAUDE_HOME') { [void] $psi.Environment.Remove($k) }
    $psi.Environment['STATUSAI_OFFLINE'] = $homeDir
    $psi.Environment['HOME'] = $homeDir
    $psi.Environment['CLAUDE_HOME'] = $homeDir
    $p = [System.Diagnostics.Process]::Start($psi)
    [pscustomobject]@{ Proc = $p; Out = $p.StandardOutput.ReadToEndAsync(); Err = $p.StandardError.ReadToEndAsync() }
}

# the exit code once the process has gone, or a failure if it is still there after the wait
function Wait-Exit($run, [int] $ms, [string] $when) {
    if (-not $run.Proc.WaitForExit($ms)) { Fail "the plugin was still running $ms ms after $when" }
    $run.Proc.WaitForExit()
    $text = ($run.Out.Result + $run.Err.Result).Trim()
    Need ($text.Length -eq 0) "the plugin wrote to its standard output or error: $text"
    $run.Proc.ExitCode
}

# ------------------------------------------------------------------ the app's side of the websocket
function New-Session {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    $s = [pscustomobject]@{ Listener = $l; Port = ([System.Net.IPEndPoint] $l.LocalEndpoint).Port; Client = $null; Socket = $null
                            Stream = $null; Run = $null; HomeDir = (New-Home); Request = '' }
    [void] $script:Open.Add($s)
    $s
}

# answer: ok, wrong (a Sec-WebSocket-Accept that does not match), refuse (400), silent (no answer)
function Connect-Plugin($s, [string] $answer = 'ok') {
    $s.Run = Start-Statusai ("-port {0} -pluginUUID {1} -registerEvent registerPlugin -info {{}}" -f $s.Port, $Uuid) $s.HomeDir
    $t = $s.Listener.AcceptTcpClientAsync()
    if (-not $t.Wait(5000)) { Fail 'the plugin did not connect within 5 s' }
    $s.Client = $t.Result; $s.Client.NoDelay = $true
    $s.Socket = $s.Client.Client; $s.Stream = $s.Client.GetStream()
    $sb = New-Object System.Text.StringBuilder
    while ($sb.Length -lt 4 -or $sb.ToString($sb.Length - 4, 4) -ne "`r`n`r`n") {
        $b = $s.Stream.ReadByte()
        if ($b -lt 0) { Fail 'the plugin closed the connection during the handshake' }
        [void] $sb.Append([char] $b)
        if ($sb.Length -gt 8192) { Fail 'the handshake request is over 8 kB' }
    }
    $s.Request = $sb.ToString()
    if ($answer -eq 'silent') { return }
    $key = ''
    if ($s.Request -match '(?im)^Sec-WebSocket-Key:\s*(\S+)\s*$') { $key = $Matches[1] }
    $sha = [System.Security.Cryptography.SHA1]::Create()
    try { $accept = [Convert]::ToBase64String($sha.ComputeHash([System.Text.Encoding]::ASCII.GetBytes($key + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11'))) }
    finally { $sha.Dispose() }
    if ($answer -eq 'wrong') { $accept = [Convert]::ToBase64String((New-Object byte[] 20)) }
    # the headers the app itself sends (Stream Deck 7.6), which are more than the protocol asks for
    $resp = "HTTP/1.1 101 Switching Protocols`r`nUpgrade: websocket`r`nConnection: Upgrade`r`nSec-WebSocket-Accept: $accept`r`n" +
            "Server: StreamDeck`r`nAccess-Control-Allow-Credentials: false`r`nAccess-Control-Allow-Methods: GET`r`n" +
            "Access-Control-Allow-Headers: content-type`r`nAccess-Control-Allow-Origin: *`r`n`r`n"
    if ($answer -eq 'refuse') { $resp = "HTTP/1.1 400 Bad Request`r`nContent-Length: 0`r`n`r`n" }
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($resp)
    $s.Stream.Write($bytes, 0, $bytes.Length); $s.Stream.Flush()
}

# One frame as a server sends it: not masked, with the shortest length field that holds the
# payload. The switches are there to send what a server must not.
function Send-Frame($s, [int] $opcode, [byte[]] $payload, [bool] $fin = $true, [int] $rsv = 0, [bool] $masked = $false) {
    if ($null -eq $payload) { $payload = $NoBytes }
    $ms = New-Object System.IO.MemoryStream
    $b0 = $opcode -bor $rsv; if ($fin) { $b0 = $b0 -bor 0x80 }
    $ms.WriteByte([byte] $b0)
    $n = $payload.Length; $m = 0; if ($masked) { $m = 0x80 }
    if ($n -lt 126) { $ms.WriteByte([byte] ($m -bor $n)) }
    elseif ($n -le 0xFFFF) { $ms.WriteByte([byte] ($m -bor 126)); $ms.WriteByte([byte] ($n -shr 8)); $ms.WriteByte([byte] ($n -band 0xFF)) }
    else { $ms.WriteByte([byte] ($m -bor 127)); for ($k = 7; $k -ge 0; $k--) { $ms.WriteByte([byte] (([long] $n -shr (8 * $k)) -band 0xFF)) } }
    if ($masked) { $ms.Write((New-Object byte[] 4), 0, 4) }   # a mask of zeros leaves the payload as it is
    $ms.Write($payload, 0, $n)
    $bytes = $ms.ToArray()
    $s.Stream.Write($bytes, 0, $bytes.Length); $s.Stream.Flush()
}
function Send-Text($s, [string] $json) { Send-Frame $s 1 ($Utf8.GetBytes($json)) }
function Send-Event($s, [string] $event, [string] $context, [string] $payload = '{}') {
    Send-Text $s ('{{"event":"{0}","action":"{1}","context":"{2}","device":"dev","payload":{3}}}' -f $event, $Action, $context, $payload)
}

function Read-Exact($s, [int] $n) {
    $buf = New-Object byte[] $n; $got = 0
    while ($got -lt $n) {
        $r = $s.Stream.Read($buf, $got, $n - $got)
        if ($r -le 0) { return $null }
        $got += $r
    }
    , $buf
}

# The next frame from the plugin: kind frame, or timeout when nothing came within the wait, eof
# when it closed its side, reset when the connection broke.
function Read-Frame($s, [int] $timeoutMs) {
    try {
        if (-not $s.Socket.Poll([Math]::Max(0, $timeoutMs) * 1000, [System.Net.Sockets.SelectMode]::SelectRead)) { return @{ kind = 'timeout' } }
        $h = Read-Exact $s 2
        if ($null -eq $h) { return @{ kind = 'eof' } }
        $f = @{ kind = 'frame'; fin = ($h[0] -band 0x80) -ne 0; rsv = $h[0] -band 0x70; opcode = $h[0] -band 0x0F
                masked = ($h[1] -band 0x80) -ne 0; bits = 7; payload = $NoBytes }
        [long] $len = $h[1] -band 0x7F
        if ($len -eq 126) { $e = Read-Exact $s 2; if ($null -eq $e) { return @{ kind = 'eof' } }; $len = ([long] $e[0] -shl 8) -bor $e[1]; $f.bits = 16 }
        elseif ($len -eq 127) {
            $e = Read-Exact $s 8; if ($null -eq $e) { return @{ kind = 'eof' } }
            $len = 0; foreach ($x in $e) { $len = ($len -shl 8) -bor $x }; $f.bits = 64
        }
        $mask = $null
        if ($f.masked) { $mask = Read-Exact $s 4; if ($null -eq $mask) { return @{ kind = 'eof' } } }
        if ($len -gt 0) {
            $p = Read-Exact $s ([int] $len)
            if ($null -eq $p) { return @{ kind = 'eof' } }
            if ($mask) { for ($i = 0; $i -lt $p.Length; $i++) { $p[$i] = $p[$i] -bxor $mask[$i -band 3] } }
            $f.payload = $p
        }
        $f.length = $len
        return $f
    } catch [System.IO.IOException] { return @{ kind = 'reset' } }
      catch [System.Net.Sockets.SocketException] { return @{ kind = 'reset' } }
      catch [System.ObjectDisposedException] { return @{ kind = 'reset' } }
}

# The next thing the plugin says. Every frame of a client is masked, whole and without reserved
# bits, and this plugin never sends a fragment or a ping, so anything else fails the case here.
function Read-Said($s, [int] $timeoutMs) {
    $f = Read-Frame $s $timeoutMs
    if ($f.kind -ne 'frame') { return $f }
    Need $f.masked 'the plugin sent a frame without a mask'
    Need ($f.rsv -eq 0) 'the plugin sent a frame with a reserved bit set'
    Need $f.fin 'the plugin sent a fragment'
    switch ($f.opcode) {
        1  { $f.kind = 'text'; $f.text = $Utf8.GetString($f.payload); $f.json = $f.text | ConvertFrom-Json }
        8  { $f.kind = 'close'; $f.status = 0; if ($f.payload.Length -ge 2) { $f.status = ([int] $f.payload[0] -shl 8) -bor $f.payload[1] } }
        10 { $f.kind = 'pong' }
        default { Fail "the plugin sent a frame with opcode $($f.opcode)" }
    }
    $f
}

function Describe($f) {
    if ($f.kind -eq 'text') { $t = $f.text; if ($t.Length -gt 160) { $t = $t.Substring(0, 160) + '...' }; return "a message: $t" }
    if ($f.kind -eq 'close') { return "a close with status $($f.status)" }
    "$($f.kind)"
}

# the next message, which has to be this event (and for this key, when one is named)
function Expect-Event($s, [string] $event, [int] $timeoutMs = 3000, [string] $context = '') {
    $f = Read-Said $s $timeoutMs
    if ($f.kind -ne 'text') { Fail "expected $event within $timeoutMs ms, got $(Describe $f)" }
    if ($f.json.event -ne $event) { Fail "expected $event, got $(Describe $f)" }
    if ($context -and $f.json.context -ne $context) { Fail "expected $event for $context, got it for $($f.json.context)" }
    $f
}
function Expect-Log($s, [string] $pattern, [int] $timeoutMs = 3000) {
    $f = Expect-Event $s 'logMessage' $timeoutMs
    if ($f.json.payload.message -notmatch $pattern) { Fail "expected a log line matching $pattern, got: $($f.json.payload.message)" }
    $f
}
function Expect-Nothing($s, [int] $ms, [string] $when) {
    $f = Read-Said $s $ms
    if ($f.kind -ne 'timeout') { Fail "expected nothing for $ms ms $when, got $(Describe $f)" }
}

# a session up to the point where the plugin has registered and said it started
function Open-Plugin {
    $s = New-Session
    Connect-Plugin $s
    $f = Expect-Event $s 'registerPlugin' 5000
    Need ($f.json.uuid -eq $Uuid) "it registered as $($f.json.uuid), not with the uuid it was started with"
    $f = Expect-Log $s '^statusai \d+\.\d+\.\d+ of \d{4}-\d\d-\d\d \d\d:\d\d started as the Stream Deck plugin, offline$'
    $s
}

# the key appears, and the picture the plugin answers with
function Show-Key($s, [string] $context, [string] $payload = '{"settings":{},"coordinates":{"column":7,"row":2},"isInMultiAction":false,"controller":"Keypad"}') {
    Send-Event $s 'willAppear' $context $payload
    Expect-Event $s 'setImage' 3000 $context
}

function Pad([int] $n) { New-Object string ('x', $n) }

function Close-Sessions {
    foreach ($s in @($script:Open)) {
        try { if ($s.Run -and -not $s.Run.Proc.HasExited) { $s.Run.Proc.Kill(); $s.Run.Proc.WaitForExit(3000) | Out-Null } } catch { }
        try { if ($s.Client) { $s.Client.Close() } } catch { }
        try { $s.Listener.Stop() } catch { }
    }
    $script:Open.Clear()
}

# ------------------------------------------------------------------ the cases
$Cases = [ordered]@{}

$Cases['registers, says so, and draws the key'] = {
    $s = New-Session
    Connect-Plugin $s
    Need ($s.Request -match '^GET / HTTP/1\.1\r\n') 'the handshake does not open with GET / HTTP/1.1'
    foreach ($h in "Host: 127\.0\.0\.1:$($s.Port)", 'Upgrade: websocket', 'Connection: Upgrade', 'Sec-WebSocket-Version: 13', 'Sec-WebSocket-Key: [A-Za-z0-9+/]{22}==') {
        Need ($s.Request -match "(?im)^$h\r?$") "the handshake has no header '$h'"
    }
    $f = Expect-Event $s 'registerPlugin' 5000
    Need ($f.text -eq ('{{"event":"registerPlugin","uuid":"{0}"}}' -f $Uuid)) "the registration is not the event and uuid it was started with: $($f.text)"
    [void] (Expect-Log $s '^statusai \d+\.\d+\.\d+ of \d{4}-\d\d-\d\d \d\d:\d\d started as the Stream Deck plugin, offline$')
    Expect-Nothing $s 400 'before a key shows'

    $f = Show-Key $s 'A1'
    Need ($f.bits -eq 16) "a picture of $($f.length) bytes should have a 16 bit length, not $($f.bits)"
    Need ($f.json.payload.target -eq 0 -and $f.json.payload.state -eq 0) 'setImage is not for both the device and the app, state 0'
    $prefix = 'data:image/svg+xml,'
    Need ($f.json.payload.image.StartsWith($prefix)) 'the picture is not an SVG data URI'
    $svg = [Uri]::UnescapeDataString($f.json.payload.image.Substring($prefix.Length))
    $want = [System.IO.File]::ReadAllText((Join-Path $Tests 'expected\key.svg'), $Utf8)
    Need ($svg -ceq $want) 'the picture on the wire is not tests/expected/key.svg'
    Expect-Nothing $s 400 'after the picture'
}

$Cases['frames with 16 and 64 bit lengths, in and out'] = {
    $s = Open-Plugin
    # in: a willAppear whose settings make the frame longer than 125 bytes, then longer than 65535
    [void] (Show-Key $s 'B16' ('{{"settings":{{"pad":"{0}"}}}}' -f (Pad 2000)))
    [void] (Show-Key $s 'B64' ('{{"settings":{{"pad":"{0}"}}}}' -f (Pad 70000)))
    # out: offline, the plugin sends a sendToPlugin's payload straight back
    foreach ($size in 20, 300, 70000) {
        $payload = '{{"pad":"{0}"}}' -f (Pad $size)
        Send-Event $s 'sendToPlugin' 'B16' $payload
        $f = Expect-Event $s 'sendToPropertyInspector' 5000 'B16'
        $bits = 7; if ($f.length -gt 65535) { $bits = 64 } elseif ($f.length -gt 125) { $bits = 16 }
        Need ($f.bits -eq $bits) "a frame of $($f.length) bytes came with a $($f.bits) bit length"
        Need ($f.json.payload.pad.Length -eq $size) "the payload of $size came back as $($f.json.payload.pad.Length)"
    }
}

$Cases['a message in fragments, with a ping between them'] = {
    $s = Open-Plugin
    $json = $Utf8.GetBytes(('{{"event":"willAppear","action":"{0}","context":"FRAG","device":"dev","payload":{{"settings":{{}}}}}}' -f $Action))
    $a = [int] ($json.Length / 3); $b = 2 * $a
    Send-Frame $s 1 ([byte[]] $json[0..($a - 1)]) $false
    Send-Frame $s 0 ([byte[]] $json[$a..($b - 1)]) $false
    Send-Frame $s 9 ($Utf8.GetBytes('mid'))
    $f = Read-Said $s 3000
    Need ($f.kind -eq 'pong' -and $Utf8.GetString($f.payload) -eq 'mid') "expected the pong for the ping sent between two fragments, got $(Describe $f)"
    Expect-Nothing $s 300 'before the last fragment'
    Send-Frame $s 0 ([byte[]] $json[$b..($json.Length - 1)])
    [void] (Expect-Event $s 'setImage' 3000 'FRAG')
}

$Cases['a ping is answered, a pong is ignored'] = {
    $s = Open-Plugin
    foreach ($text in 'abc', '', (Pad 125)) {
        Send-Frame $s 9 ($Utf8.GetBytes($text))
        $f = Read-Said $s 3000
        Need ($f.kind -eq 'pong') "expected a pong, got $(Describe $f)"
        Need ($Utf8.GetString($f.payload) -ceq $text) 'the pong does not carry what the ping did'
    }
    Send-Frame $s 10 ($Utf8.GetBytes('nobody asked'))
    Expect-Nothing $s 400 'after a pong nobody asked for'
    [void] (Show-Key $s 'P1')
}

$Cases['a short press on release, a long press after 500 ms'] = {
    $s = Open-Plugin
    [void] (Show-Key $s 'K1')
    $wt = '\\Microsoft\\WindowsApps\\wt\.exe'
    # short: nothing while the key is down, the tab when it comes up
    Send-Event $s 'keyDown' 'K1'
    Expect-Nothing $s 150 'while the key is down'
    Send-Event $s 'keyUp' 'K1'
    [void] (Expect-Log $s "^offline: would run .*$wt -w 0 nt$" 1000)
    Expect-Nothing $s 700 'after a short press'
    # long: the window at 500 ms, with the key still down, and nothing when it comes up
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Send-Event $s 'keyDown' 'K1'
    [void] (Expect-Log $s "^offline: would run .*$wt -w new$" 1500)
    $ms = $sw.ElapsedMilliseconds
    Need ($ms -ge 450 -and $ms -le 900) "the long press acted after $ms ms, not about 500"
    Send-Event $s 'keyUp' 'K1'
    Expect-Nothing $s 700 'after the release of a long press'
    # a hold just under the half second is still a short press
    Send-Event $s 'keyDown' 'K1'
    Start-Sleep -Milliseconds 350
    Send-Event $s 'keyUp' 'K1'
    [void] (Expect-Log $s "^offline: would run .*$wt -w 0 nt$" 1000)
}

$Cases['a refresh when Claude writes, one a slot, and none while no key shows'] = {
    $s = Open-Plugin
    $file = Join-Path $s.HomeDir '.claude\projects\p\session.jsonl'
    # no key on show: the write is noted and nothing is asked for
    [System.IO.File]::AppendAllText($file, "{}`n")
    Expect-Nothing $s 1500 'after a write while no key shows'
    # the key appears: its picture, then the refresh the write was waiting for
    [void] (Show-Key $s 'R1')
    [void] (Expect-Log $s '^offline: would run --refresh$' 3000)
    # more writes inside the same slot: nothing more until the slot is over
    [System.IO.File]::AppendAllText($file, "{}`n")
    Start-Sleep -Milliseconds 300
    [System.IO.File]::AppendAllText($file, "{}`n")
    Expect-Nothing $s 2500 'after writes inside the slot'
}

$Cases['the app closes: a close in answer, and exit code 0'] = {
    $s = Open-Plugin
    [void] (Show-Key $s 'C1')
    Send-Frame $s 8 ([byte[]] (0x03, 0xE8))          # 1000, normal closure
    $f = Read-Said $s 3000
    Need ($f.kind -eq 'close' -and $f.status -eq 1000) "expected a close with status 1000 in answer, got $(Describe $f)"
    $code = Wait-Exit $s.Run 3000 'the app closed the connection'
    Need ($code -eq 0) "exit code $code after the app closed the connection, not 0"
}

$Cases['a frame the protocol forbids: the plugin closes with 1002, and exit code 1'] = {
    $bad = [ordered]@{
        'a masked frame'               = { param($s) Send-Frame $s 1 ($Utf8.GetBytes('{"event":"keyDown"}')) $true 0 $true }
        'a reserved bit'               = { param($s) Send-Frame $s 1 ($Utf8.GetBytes('{"event":"keyDown"}')) $true 0x40 }
        'a ping in fragments'          = { param($s) Send-Frame $s 9 ($Utf8.GetBytes('x')) $false }
        'a continuation of nothing'    = { param($s) Send-Frame $s 0 ($Utf8.GetBytes('x')) }
        'a new message inside another' = { param($s) Send-Frame $s 1 ($Utf8.GetBytes('{')) $false; Send-Frame $s 1 ($Utf8.GetBytes('}')) }
        'an opcode that does not exist' = { param($s) Send-Frame $s 3 ($Utf8.GetBytes('x')) }
    }
    foreach ($what in $bad.Keys) {
        $s = Open-Plugin
        & $bad[$what] $s
        $f = Read-Said $s 3000
        Need ($f.kind -eq 'close' -and $f.status -eq 1002) "after ${what}: expected a close with status 1002, got $(Describe $f)"
        $code = Wait-Exit $s.Run 3000 $what
        Need ($code -eq 1) "after ${what}: exit code $code, not 1"
    }
    # a frame that announces more than the plugin will hold: refused with 1009 before any of it is read
    $s = Open-Plugin
    $head = [byte[]] (0x81, 127, 0, 0, 0, 0, 0x01, 0x10, 0, 0)   # a text frame of 17 MiB
    $s.Stream.Write($head, 0, $head.Length); $s.Stream.Flush()
    $f = Read-Said $s 3000
    Need ($f.kind -eq 'close' -and $f.status -eq 1009) "after a frame of 17 MiB was announced: expected a close with status 1009, got $(Describe $f)"
    $code = Wait-Exit $s.Run 3000 'a frame too big'
    Need ($code -eq 1) "after a frame too big: exit code $code, not 1"
}

$Cases['the connection drops: exit code 1'] = {
    # reset: the socket is torn down with nothing sent
    $s = Open-Plugin
    [void] (Show-Key $s 'D1')
    $s.Socket.LingerState = New-Object System.Net.Sockets.LingerOption($true, 0)
    $s.Client.Close()
    $code = Wait-Exit $s.Run 3000 'the connection was reset'
    Need ($code -eq 1) "exit code $code after a reset, not 1"
    # the app's side just ends, in the middle of a frame, without a close
    $s = Open-Plugin
    $half = [byte[]] (0x81, 20, 0x7B)
    $s.Stream.Write($half, 0, $half.Length); $s.Stream.Flush()
    $s.Socket.Shutdown([System.Net.Sockets.SocketShutdown]::Send)
    $code = Wait-Exit $s.Run 3000 'the connection ended inside a frame'
    Need ($code -eq 1) "exit code $code after the connection ended inside a frame, not 1"
}

$Cases['no app, or one that answers wrongly: exit code 1'] = {
    # nothing listens on the port
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $port = ([System.Net.IPEndPoint] $l.LocalEndpoint).Port; $l.Stop()
    $run = Start-Statusai ("-port {0} -pluginUUID {1} -registerEvent registerPlugin -info {{}}" -f $port, $Uuid) (New-Home)
    $code = Wait-Exit $run 8000 'it was started with a port nothing listens on'
    Need ($code -eq 1) "exit code $code with no app, not 1"
    # an answer whose Sec-WebSocket-Accept does not match the key, and one that is not an upgrade at all
    foreach ($answer in 'wrong', 'refuse') {
        $s = New-Session
        Connect-Plugin $s $answer
        $f = Read-Frame $s 3000
        Need ($f.kind -ne 'frame') "the plugin went on after a handshake answered with '$answer'"
        $code = Wait-Exit $s.Run 3000 "a handshake answered with '$answer'"
        Need ($code -eq 1) "exit code $code after a handshake answered with '$answer', not 1"
    }
}

$Cases['an app that never answers the handshake: gone after 10 s'] = {
    $s = New-Session
    Connect-Plugin $s 'silent'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $code = Wait-Exit $s.Run 14000 'the handshake went unanswered'
    Need ($code -eq 1) "exit code $code, not 1"
    Need ($sw.ElapsedMilliseconds -ge 8000) "it gave up after $($sw.ElapsedMilliseconds) ms, well before its 10 s"
}

$Cases['--refresh and --deck-face do not wait for stdin'] = {
    # stdin is a pipe that is never written to and never closed, as it is for a plugin
    $run = Start-Statusai '--refresh' (New-Home)
    if (-not $run.Proc.WaitForExit(5000)) { Fail '--refresh was still running after 5 s' }
    $run.Proc.WaitForExit()
    Need ($run.Proc.ExitCode -eq 0) "--refresh ended with exit code $($run.Proc.ExitCode)"
    Need (($run.Out.Result + $run.Err.Result).Length -eq 0) '--refresh printed something'
    $run = Start-Statusai '--deck-face 0' (New-Home)
    if (-not $run.Proc.WaitForExit(5000)) { Fail '--deck-face was still running after 5 s' }
    $run.Proc.WaitForExit()
    Need ($run.Proc.ExitCode -eq 0) "--deck-face ended with exit code $($run.Proc.ExitCode)"
    Need ($run.Out.Result.StartsWith('<svg ')) '--deck-face did not print an SVG'
}

# ------------------------------------------------------------------ run
function Invoke-Main {
    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
        Write-Host "No statusai.exe at $Exe. Build it (docs/development.md) or pass -Exe <path>." -ForegroundColor Red
        return 2
    }
    $script:ExePath = (Resolve-Path -LiteralPath $Exe).Path
    New-Item -ItemType Directory -Force -Path $Scratch | Out-Null
    $script:RunRoot = Join-Path (Resolve-Path -LiteralPath $Scratch).Path 'run'
    if (Test-Path -LiteralPath $script:RunRoot) { Remove-Item -LiteralPath $script:RunRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $script:RunRoot | Out-Null
    Write-Host "statusai    : $script:ExePath"

    $selected = @($Cases.Keys | Where-Object { $n = $_; @($Case | Where-Object { $n -like $_ }).Count -gt 0 })
    if ($selected.Count -eq 0) { Write-Host "No case matches $($Case -join ', ')." -ForegroundColor Red; return 2 }
    $pass = 0; $fail = 0
    foreach ($name in $selected) {
        try {
            & $Cases[$name]
            $pass++
            Write-Host "pass  $name"
        } catch [Ouch] {
            $fail++
            Write-Host "FAIL  $name" -ForegroundColor Red
            Write-Host "      $($_.Exception.Message)"
        } catch {
            # not a check that failed but the stand-in itself: say where
            $fail++
            Write-Host "FAIL  $name" -ForegroundColor Red
            Write-Host "      the test itself stopped: $($_.Exception.Message) ($($_.InvocationInfo.PositionMessage -replace '\s+', ' '))"
        } finally { Close-Sessions }
    }
    Write-Host "$($pass + $fail) cases: $pass pass, $fail fail" -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
    if ($fail) { return 1 }
    Remove-Item -LiteralPath $script:RunRoot -Recurse -Force
    return 0
}

try { $code = Invoke-Main } finally { Close-Sessions }
exit $code
