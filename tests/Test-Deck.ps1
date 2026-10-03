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
    network are never touched, a key press opens nothing and a refresh fetches nothing: the
    plugin reports what a press would have run, the --deck-press it starts ends at once, and the
    --refresh it starts has nothing to fetch. No terminal is ever opened by these tests, and
    nothing on the desktop is touched.

    -Live adds the four cases that cannot be had that way, because they are about the desktop.
    The first is the control: with Windows Terminal running, a new window asked of it with
    nothing done for it has to stay behind the window in front, as the plugin's did before it
    was taught otherwise. Then a press with no terminal open, with one open behind another
    window, and with one minimised, each once as a short press and once as a hold, and every
    time the terminal has to end up in front. For these the plugin, and the terminal windows the
    cases open for themselves, are started through WMI, by a Windows service, so that they stand
    where the real ones do: a program that this script starts itself descends from the window in
    front whenever the script was started from it, and Windows lets a program started by the
    program in front take the foreground. The cases open real Windows Terminal windows and take
    the foreground for a few seconds at a time. The tabs they open run `cmd /c ping` in place of
    the default profile's command, carry a title that starts with "statusai test", and close by
    themselves; the foreground goes back to the window that had it. Nothing that was open before
    is closed, moved or resized, and the cases are skipped where they would be in the way: on a
    locked session, while a full-screen program or a presentation is in front, and while a
    Windows Terminal window of your own is open, since a press would put its tab there.

    Runs under Windows PowerShell 5.1 and PowerShell 7. Exit code 0 when every case passes, 1
    when any fails, 2 when the run could not start.

.PARAMETER Exe
    The statusai.exe to test. Default: the output of `dotnet publish -c Release -r win-x64`
    in src/.

.PARAMETER Case
    Only these cases; wildcards allowed, and a comma separates several. Default: every case.

.PARAMETER Live
    Run the cases that open Windows Terminal as well. Without it they are left out, whatever
    -Case says.

.PARAMETER Scratch
    Where the homes the plugins run in go. Default: .work/test-deck in the repository.

.EXAMPLE
    ./tests/Test-Deck.ps1 -Exe .work/publish/statusai.exe

.EXAMPLE
    ./tests/Test-Deck.ps1 -Case 'a ping*', 'the app closes*'

.EXAMPLE
    ./tests/Test-Deck.ps1 -Exe .work/publish/statusai.exe -Live -Case 'live*'
#>
[CmdletBinding()]
param(
    [string]   $Exe,
    [string[]] $Case = @('*'),
    [switch]   $Live,
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
class Skip : System.Exception { Skip([string] $m) : base($m) { } }
function Fail([string] $msg) { throw [Ouch]::new($msg) }
function Need([bool] $ok, [string] $what) { if (-not $ok) { Fail $what } }

# How many of a process's handles are marked for a program it starts to be given as well. Asked
# of Windows for the whole system, since nothing gives the answer for one process from outside.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class HandedDown {
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int what, IntPtr buffer, int size, out int needed);

    public static int Count(int pid) {
        int size = 1 << 24, needed;
        IntPtr buffer;
        while (true) {
            buffer = Marshal.AllocHGlobal(size);
            int status = NtQuerySystemInformation(64, buffer, size, out needed);   // SystemExtendedHandleInformation
            if (status == 0) break;
            Marshal.FreeHGlobal(buffer);
            if (status != unchecked((int)0xC0000004)) return -1;                   // anything but "too small"
            size = Math.Max(size * 2, needed + (1 << 20));
        }
        try {
            // a count, then 40 bytes a handle: the owner's process id at 8, the handle's attributes at 32
            long count = Marshal.ReadIntPtr(buffer).ToInt64();
            int n = 0;
            for (long i = 0; i < count; i++) {
                IntPtr entry = new IntPtr(buffer.ToInt64() + 16 + i * 40);
                if (Marshal.ReadIntPtr(entry, 8).ToInt64() == pid && (Marshal.ReadInt32(entry, 32) & 2) != 0) n++;
            }
            return n;
        } finally { Marshal.FreeHGlobal(buffer); }
    }
}
'@

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

# $more: variables a case sets for its plugin. STATUSAI_DECK_TAB is never inherited, and only the
# -Live cases set it: with it a press opens a real terminal.
function Start-Statusai([string] $arguments, [string] $homeDir, [hashtable] $more = @{}) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $script:ExePath
    $psi.Arguments = $arguments
    $psi.WorkingDirectory = $homeDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($k in 'STATUSAI_OFFLINE', 'STATUSAI_WIDTH', 'STATUSAI_DECK_TAB', 'STATUSAI_DECK_SLOT', 'COLUMNS', 'LINES', 'HOME', 'CLAUDE_HOME') { [void] $psi.Environment.Remove($k) }
    $psi.Environment['STATUSAI_OFFLINE'] = $homeDir
    $psi.Environment['HOME'] = $homeDir
    $psi.Environment['CLAUDE_HOME'] = $homeDir
    foreach ($k in $more.Keys) { $psi.Environment[$k] = [string] $more[$k] }
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
function New-Session([hashtable] $more = @{}, [bool] $apart = $false) {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    $s = [pscustomobject]@{ Listener = $l; Port = ([System.Net.IPEndPoint] $l.LocalEndpoint).Port; Client = $null; Socket = $null
                            Stream = $null; Run = $null; HomeDir = (New-Home); Request = ''; More = $more; Apart = $apart }
    [void] $script:Open.Add($s)
    $s
}

# answer: ok, wrong (a Sec-WebSocket-Accept that does not match), refuse (400), silent (no answer)
function Connect-Plugin($s, [string] $answer = 'ok') {
    $arguments = "-port {0} -pluginUUID {1} -registerEvent registerPlugin -info {{}}" -f $s.Port, $Uuid
    if ($s.Apart) { $s.Run = Start-StatusaiApart $arguments $s.HomeDir $s.More } else { $s.Run = Start-Statusai $arguments $s.HomeDir $s.More }
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

# Everything the plugin says until it has been quiet for a while: for the places where how many
# messages come, and in which order, is not the point.
function Read-Until-Quiet($s, [int] $firstMs, [int] $quietMs) {
    $all = New-Object System.Collections.ArrayList
    $wait = $firstMs
    while ($true) {
        $f = Read-Said $s $wait
        if ($f.kind -eq 'timeout') { break }
        if ($f.kind -ne 'text') { Fail "expected messages, got $(Describe $f)" }
        [void] $all.Add($f)
        $wait = $quietMs
    }
    , $all
}

# a session up to the point where the plugin has registered and said it started
function Open-Plugin([hashtable] $more = @{}, [bool] $apart = $false) {
    $s = New-Session $more $apart
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

$Cases['handles the app hands down are not handed on'] = {
    # The app starts a plugin with handles of its own open to it, its log among them. A file is
    # opened here the same way, so that this plugin is given it together with its three pipes.
    $file = Join-Path $script:RunRoot 'handed-down.txt'
    $share = [System.IO.FileShare] ([int] [System.IO.FileShare]::ReadWrite -bor [int] [System.IO.FileShare]::Inheritable)
    $fs = New-Object System.IO.FileStream($file, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, $share)
    try { $s = Open-Plugin } finally { $fs.Dispose() }
    # what the plugin starts would get every handle still marked: a terminal, for as long as it is open
    $n = [HandedDown]::Count($s.Run.Proc.Id)
    Need ($n -ge 0) 'Windows did not list the handles'
    Need ($n -eq 0) "the plugin holds $n handles that a program it starts would be given as well"
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
    # the press is a process of its own, which opens nothing offline and says so by ending with 0
    [void] (Expect-Log $s '^offline: --deck-press tab ended with 0$' 3000)
    Expect-Nothing $s 700 'after a short press'
    # long: the window at 500 ms, with the key still down, and nothing when it comes up
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Send-Event $s 'keyDown' 'K1'
    [void] (Expect-Log $s "^offline: would run .*$wt -w new$" 1500)
    $ms = $sw.ElapsedMilliseconds
    Need ($ms -ge 450 -and $ms -le 900) "the long press acted after $ms ms, not about 500"
    [void] (Expect-Log $s '^offline: --deck-press window ended with 0$' 3000)
    Send-Event $s 'keyUp' 'K1'
    Expect-Nothing $s 700 'after the release of a long press'
    # a hold just under the half second is still a short press
    Send-Event $s 'keyDown' 'K1'
    Start-Sleep -Milliseconds 350
    Send-Event $s 'keyUp' 'K1'
    [void] (Expect-Log $s "^offline: would run .*$wt -w 0 nt$" 1000)
    [void] (Expect-Log $s '^offline: --deck-press tab ended with 0$' 3000)
}

$Cases['forty presses, and as many handles as before'] = {
    $s = Open-Plugin
    [void] (Show-Key $s 'N1')
    $said = { param([object[]] $all, [string] $pattern) @($all | Where-Object { $_.json.event -eq 'logMessage' -and $_.json.payload.message -match $pattern }).Count }
    # one after the other: each is started, waited for and read before the next
    $press = { param([int] $times)
        for ($i = 0; $i -lt $times; $i++) {
            Send-Event $s 'keyDown' 'N1'
            Send-Event $s 'keyUp' 'N1'
            [void] (Expect-Log $s '^offline: would run ' 2000)
            [void] (Expect-Log $s '^offline: --deck-press tab ended with 0$' 5000)
        }
    }
    # The first program a process starts costs it two handles, which is Windows looking up what
    # it knows about the program. What .NET's own way of asking whether a process has ended costs
    # is 13 more, once, and they stay: that is what this plugin was found with after its first
    # press, and what it must not take on again.
    $s.Run.Proc.Refresh(); $fresh = $s.Run.Proc.HandleCount
    & $press 2
    $s.Run.Proc.Refresh(); $before = $s.Run.Proc.HandleCount
    Need ($before - $fresh -le 6) "the plugin held $fresh handles before its first press and $before after it"
    & $press 30
    # and ten at once, so that the plugin waits for several of them together
    for ($i = 0; $i -lt 10; $i++) { Send-Event $s 'keyDown' 'N1'; Send-Event $s 'keyUp' 'N1' }
    $all = Read-Until-Quiet $s 5000 1000
    $ran = & $said $all '^offline: would run '
    $ended = & $said $all '^offline: --deck-press tab ended with 0$'
    Need ($ran -eq 10 -and $ended -eq 10) "ten presses at once: $ran were started and $ended ended with 0"
    $s.Run.Proc.Refresh(); $after = $s.Run.Proc.HandleCount
    # each press is a process started, with a handle to it that is waited on and has to be closed again
    Need ([Math]::Abs($after - $before) -le 3) "the plugin held $before handles before 40 presses and $after after them"
}

$Cases['no Windows Terminal: an alert on the key, why in the log, and as many handles as before'] = {
    # A %LOCALAPPDATA% with no wt.exe in it, and something for a tab to run, which makes the
    # press a real one: --deck-press tries to start the terminal. It is a hold each time, which
    # asks for a new window, so no window that is open is looked for.
    $local = Join-Path $script:RunRoot 'no-terminal'
    New-Item -ItemType Directory -Path (Join-Path $local 'Microsoft\WindowsApps') -Force | Out-Null
    $s = Open-Plugin @{ STATUSAI_DECK_TAB = 'cmd /c exit'; LOCALAPPDATA = $local }
    [void] (Show-Key $s 'X1')
    $hold = {
        Send-Event $s 'keyDown' 'X1'
        [void] (Expect-Event $s 'showAlert' 3000 'X1')
        [void] (Expect-Log $s '^could not start .*\\no-terminal\\Microsoft\\WindowsApps\\wt\.exe: Windows error 2$' 3000)
        Send-Event $s 'keyUp' 'X1'
        Expect-Nothing $s 300 'after the release'
    }
    # the first costs what the first program a process starts costs, and no more: naming the
    # path in the log by asking the shell for it would load the shell into the plugin, 38 handles
    $s.Run.Proc.Refresh(); $fresh = $s.Run.Proc.HandleCount
    & $hold
    $s.Run.Proc.Refresh(); $before = $s.Run.Proc.HandleCount
    Need ($before - $fresh -le 6) "the plugin held $fresh handles before its first press and $before after it"
    foreach ($i in 1..3) { & $hold }
    $s.Run.Proc.Refresh(); $after = $s.Run.Proc.HandleCount
    Need ([Math]::Abs($after - $before) -le 3) "the plugin held $before handles before three presses that failed and $after after them"
}

$Cases['a refresh when Claude writes, one a slot, and none while no key shows'] = {
    $s = Open-Plugin
    $file = Join-Path $s.HomeDir '.claude\projects\p\session.jsonl'
    # no key on show: the write is noted and nothing is asked for
    [System.IO.File]::AppendAllText($file, "{}`n")
    Expect-Nothing $s 1500 'after a write while no key shows'
    # the key appears: its picture, then the refresh the write was waiting for
    [void] (Show-Key $s 'R1')
    [void] (Expect-Log $s '^offline: ran --refresh$' 3000)
    # more writes inside the same slot: nothing more until the slot is over
    [System.IO.File]::AppendAllText($file, "{}`n")
    Start-Sleep -Milliseconds 300
    [System.IO.File]::AppendAllText($file, "{}`n")
    Expect-Nothing $s 2500 'after writes inside the slot'
}

$Cases['the deck goes and comes back: the key is drawn again, in whichever order the app says so'] = {
    $s = Open-Plugin
    $file = Join-Path $s.HomeDir '.claude\projects\p\session.jsonl'
    $back = '{"event":"deviceDidConnect","device":"dev","deviceInfo":{"name":"Stream Deck XL","size":{"columns":8,"rows":4},"type":2}}'
    $gone = '{"event":"deviceDidDisconnect","device":"dev"}'
    [void] (Show-Key $s 'L1')
    # the deck goes, as when the session is locked: a write is noted, and nothing is drawn or fetched for a key that does not show
    Send-Event $s 'willDisappear' 'L1'
    Send-Text $s $gone
    [System.IO.File]::AppendAllText($file, "{}`n")
    Expect-Nothing $s 1500 'while the deck is away'
    # it comes back with the key named before the deck: the picture, and the refresh that was waiting
    Send-Event $s 'willAppear' 'L1'
    Send-Text $s $back
    $said = Read-Until-Quiet $s 3000 700
    Need (@($said | Where-Object { $_.json.event -eq 'setImage' -and $_.json.context -eq 'L1' }).Count -ge 1) 'the key was not drawn when it came back before its deck'
    Need (@($said | Where-Object { $_.json.event -eq 'logMessage' -and $_.json.payload.message -eq 'offline: ran --refresh' }).Count -eq 1) 'the refresh that was waiting did not come, or came more than once'
    # away again, and back with the deck named before the key
    Send-Event $s 'willDisappear' 'L1'
    Send-Text $s $gone
    Send-Text $s $back
    Expect-Nothing $s 500 'for a deck that is back with no key showing yet'
    Send-Event $s 'willAppear' 'L1'
    [void] (Expect-Event $s 'setImage' 3000 'L1')
    Expect-Nothing $s 500 'after the key came back'
    # a deck that comes back with nothing said about its key, and a computer that wakes up: the
    # picture is sent again though it has not changed, since the deck may have lost it
    Send-Text $s $back
    [void] (Expect-Event $s 'setImage' 3000 'L1')
    Send-Text $s '{"event":"systemDidWakeUp"}'
    [void] (Expect-Event $s 'setImage' 3000 'L1')
    Expect-Nothing $s 500 'after the wake-up'
}

$Cases['a hundred refreshes, and as many handles as before'] = {
    # a slot of no length, which only an offline plugin takes: every write is followed by a refresh
    $s = Open-Plugin @{ STATUSAI_DECK_SLOT = '0' }
    $file = Join-Path $s.HomeDir '.claude\projects\p\session.jsonl'
    [void] (Show-Key $s 'H1')
    $count = { param([int] $writes)
        $n = 0
        for ($i = 0; $i -lt $writes; $i++) {
            [System.IO.File]::AppendAllText($file, "{}`n")
            foreach ($f in (Read-Until-Quiet $s 5000 60)) {
                if ($f.json.event -ne 'logMessage' -or $f.json.payload.message -ne 'offline: ran --refresh') { Fail "expected only refreshes, got $(Describe $f)" }
                $n++
            }
        }
        $n
    }
    # the first refreshes bring in what a process loads once to start another
    [void] (& $count 10)
    $s.Run.Proc.Refresh(); $before = $s.Run.Proc.HandleCount
    $n = & $count 100
    Need ($n -ge 100) "100 writes were followed by $n refreshes"
    $s.Run.Proc.Refresh(); $after = $s.Run.Proc.HandleCount
    # Each refresh is a process started, with a handle to it that has to be closed again: a leak
    # of one a refresh would be $n here. The count has come out the same to the handle whenever
    # this was run; three either way are let through so that it stays a test of leaks only.
    Need ([Math]::Abs($after - $before) -le 3) "the plugin held $before handles before $n refreshes and $after after them"
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

# ------------------------------------------------------------------ the cases on the live desktop
# -Live only. A press here is a real one: the plugin is given STATUSAI_DECK_TAB, so the
# --deck-press it starts does start Windows Terminal, with a tab that runs ping and closes by
# itself.
#
# What these cases are about is the foreground, which Windows keeps for the program the user is
# working in and for what that program starts. A plugin started by this script descends from the
# window in front whenever the script was started from it, and its terminal could come to the
# front with nothing done for it. The app's plugin does not. So the plugin is started through WMI
# here, by the service Windows has for that, and the first case is the control that says whether
# a terminal asked for from there is kept behind.
if ($Live) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class Desk {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int how);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, byte[] inputs, int size);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr OpenInputDesktop(int flags, bool inherit, int access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder sb, int len, out int needed);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    public const string TerminalClass = "CASCADIA_HOSTING_WINDOW_CLASS";

    public static string Class(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }
    public static string Title(IntPtr h) { StringBuilder sb = new StringBuilder(256); GetWindowTextW(h, sb, 256); return sb.ToString(); }

    public static string Describe(IntPtr h) {
        if (h == IntPtr.Zero) return "no window";
        uint pid; GetWindowThreadProcessId(h, out pid);
        string name = "?"; try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }
        string t = Title(h); if (t.Length > 40) t = t.Substring(0, 40) + "...";
        return "'" + t + "' of " + name + " [" + Class(h) + "]" + (IsIconic(h) ? ", minimised" : "");
    }

    // every Windows Terminal window, front to back
    public static IntPtr[] Terminals() {
        List<IntPtr> l = new List<IntPtr>();
        for (IntPtr h = FindWindowExW(IntPtr.Zero, IntPtr.Zero, TerminalClass, null); h != IntPtr.Zero; h = FindWindowExW(IntPtr.Zero, h, TerminalClass, null)) l.Add(h);
        return l.ToArray();
    }

    // "Default" while the keyboard goes to the user's own desktop, and not on a locked session
    public static string InputDesktop() {
        IntPtr d = OpenInputDesktop(0, false, 1);
        if (d == IntPtr.Zero) return "none that can be reached";
        StringBuilder sb = new StringBuilder(256); int needed;
        GetUserObjectInformationW(d, 2, sb, 512, out needed);
        CloseDesktop(d);
        return sb.ToString();
    }

    // why Windows itself would hold a notification back now, or nothing
    public static string Busy() {
        int q; if (SHQueryUserNotificationState(out q) != 0) return "";
        return q == 1 ? "nobody is at the session" : q == 2 ? "a full-screen program is in front" : q == 3 ? "a full-screen game is running" : q == 4 ? "a presentation is being given" : "";
    }

    // put a window back in front, for after a case; true when it is there
    public static bool Front(IntPtr h) {
        if (!IsWindow(h)) return false;
        for (int i = 0; i < 6 && GetForegroundWindow() != h; i++) {
            if (i % 2 == 0) { SendInput(1, new byte[40], 40); SetForegroundWindow(h); }
            else {
                uint pid; uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out pid), me = GetCurrentThreadId();
                AttachThreadInput(me, fg, true); SetForegroundWindow(h); AttachThreadInput(me, fg, false);
            }
            System.Threading.Thread.Sleep(80);
        }
        return GetForegroundWindow() == h;
    }
}
'@

    $TestTitle = 'statusai test'
    $BaseTitle = 'statusai test base'
    $ControlTitle = 'statusai test control'
    $WtExe = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\wt.exe'
    $script:Guarded = $null     # what the control found, once it has run

    # A program started through WMI, and its process id. It gets this script's environment with
    # $set on top, a value of $null taking a variable out: WMI hands on none by itself.
    function Start-Apart([string] $commandLine, [string] $directory, [hashtable] $set, [bool] $hidden) {
        $vars = @{}
        foreach ($e in [System.Environment]::GetEnvironmentVariables().GetEnumerator()) { $vars[[string] $e.Key] = [string] $e.Value }
        foreach ($k in $set.Keys) { if ($null -eq $set[$k]) { $vars.Remove($k) } else { $vars[$k] = [string] $set[$k] } }
        $props = @{ EnvironmentVariables = [string[]] @($vars.Keys | Where-Object { $vars[$_] } | ForEach-Object { '{0}={1}' -f $_, $vars[$_] }) }
        if ($hidden) { $props.ShowWindow = [uint16] 0 }
        $startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property $props
        $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $commandLine; CurrentDirectory = $directory; ProcessStartupInformation = $startup }
        if ($r.ReturnValue -ne 0) { Fail "WMI did not start $commandLine (Win32_Process.Create returned $($r.ReturnValue))" }
        [int] $r.ProcessId
    }

    # Start-Statusai, apart. Nothing is read from its standard output or error, which go nowhere.
    function Start-StatusaiApart([string] $arguments, [string] $homeDir, [hashtable] $more) {
        $set = @{ STATUSAI_WIDTH = $null; STATUSAI_DECK_TAB = $null; STATUSAI_DECK_SLOT = $null; COLUMNS = $null; LINES = $null
                  STATUSAI_OFFLINE = $homeDir; HOME = $homeDir; CLAUDE_HOME = $homeDir }
        foreach ($k in $more.Keys) { $set[$k] = [string] $more[$k] }
        $id = Start-Apart ('"{0}" {1}' -f $script:ExePath, $arguments) $homeDir $set $true
        $nothing = [pscustomobject]@{ Result = '' }
        [pscustomobject]@{ Proc = [System.Diagnostics.Process]::GetProcessById($id); Out = $nothing; Err = $nothing }
    }
    # what a test tab runs: ping, for that many seconds, under a title that stays as it is
    function Get-TabCommand([string] $title, [int] $seconds) {
        '--title "{0}" --suppressApplicationTitle cmd /c "ping -n {1} 127.0.0.1 >nul"' -f $title, ($seconds + 1)
    }

    # Windows Terminal, started apart with nothing done for it, as the plugin's first version
    # started it. WMI cannot start an app execution alias itself (Win32_Process.Create returns
    # 8), so it starts a hidden cmd, which starts the terminal.
    function Start-TerminalApart([string] $arguments) {
        $line = '"{0}" {1}' -f $WtExe, $arguments
        [void] (Start-Apart ('"{0}" /d /c "{1}"' -f (Join-Path $env:SystemRoot 'System32\cmd.exe'), $line) $script:RunRoot @{} $true)
    }

    # The moments at which a terminal that jumps to the front would be in the way. A Windows
    # Terminal window of your own skips the case, unless $others is 'allow': a case that makes its
    # own terminal window and presses for that one, which is then on top of yours, and only needs
    # the window in front not to be a terminal. A tab of a press that went to your window all the
    # same would close by itself, as every test tab does.
    function Assert-Free([string] $others = 'skip') {
        $desk = [Desk]::InputDesktop()
        if ($desk -ne 'Default') { throw [Skip]::new("the session is locked, or its input goes to another desktop ($desk)") }
        $busy = [Desk]::Busy()
        if ($busy) { throw [Skip]::new($busy) }
        foreach ($w in [Desk]::Terminals()) {
            if ([Desk]::Title($w).StartsWith($TestTitle)) { continue }
            if ($others -ne 'allow') { throw [Skip]::new("a Windows Terminal window of your own is open ($([Desk]::Describe($w))), and a press would put its tab there") }
            if ([Desk]::Class([Desk]::GetForegroundWindow()) -eq [Desk]::TerminalClass) { throw [Skip]::new("a Windows Terminal window is in front ($([Desk]::Describe([Desk]::GetForegroundWindow()))), so there is no terminal behind for a press to bring up") }
        }
    }

    function Wait-Until([scriptblock] $done, [int] $ms) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt $ms) { if (& $done) { return $true }; Start-Sleep -Milliseconds 100 }
        $false
    }

    # A terminal window of the test's own, which closes by itself, and behind the window that was
    # in front before it. Skipped when no other window can be put in front of it. It is started
    # apart, so that the Windows Terminal it starts owes nothing to this script's standing, as the
    # one a user opened long before a press owes nothing to the plugin's.
    function Open-Base([int] $seconds, [IntPtr] $was) {
        $before = @([Desk]::Terminals())
        Start-TerminalApart ('-w new ' + (Get-TabCommand $BaseTitle $seconds))
        $script:Base = [IntPtr]::Zero
        $ok = Wait-Until { foreach ($w in [Desk]::Terminals()) { if ($before -notcontains $w -and [Desk]::Title($w) -eq $BaseTitle) { $script:Base = $w; return $true } }; $false } 10000
        if (-not $ok) { Fail 'the test could not open a terminal window of its own' }
        Start-Sleep -Milliseconds 300
        Put-Behind $script:Base $was
        $script:Base
    }
    function Put-Behind([IntPtr] $base, [IntPtr] $was) {
        if ([Desk]::GetForegroundWindow() -ne $base) { return }
        if ($was -eq [IntPtr]::Zero -or $was -eq $base -or -not [Desk]::Front($was)) { throw [Skip]::new('there is no other window to put in front of the test terminal') }
    }

    # One press, played to the plugin as the app plays it, and what has to be true after it: the
    # press was carried out, and the window in front is the terminal with the test tab. $base is
    # the test's own terminal window when there is one. Returns the window in front.
    function Invoke-Press($s, [string] $context, [bool] $hold, [IntPtr] $base) {
        Assert-Free $script:Others
        $before = @([Desk]::Terminals())
        $what = 'tab'; if ($hold) { $what = 'window' }
        Send-Event $s 'keyDown' $context
        if ($hold) { Start-Sleep -Milliseconds 800 }
        Send-Event $s 'keyUp' $context
        $f = Expect-Log $s "^offline: --deck-press $what ended with \d+$" 12000
        $code = [int] ($f.json.payload.message -replace '^.* ', '')
        if ($code -ge 100) { Fail "Windows Terminal could not be started: Windows error $($code - 100)" }
        if ($code -eq 1) { Fail "--deck-press $what gave up with the terminal not in front; in front is $([Desk]::Describe([Desk]::GetForegroundWindow()))" }
        # the tab's title is the window's a moment after the window is in front
        [void] (Wait-Until { $w = [Desk]::GetForegroundWindow(); [Desk]::Class($w) -eq [Desk]::TerminalClass -and [Desk]::Title($w) -eq $TestTitle } 3000)
        $front = [Desk]::GetForegroundWindow()
        Need ([Desk]::Class($front) -eq [Desk]::TerminalClass -and [Desk]::Title($front) -eq $TestTitle) "after the press the window in front is $([Desk]::Describe($front)), not the terminal with the tab"
        Need (-not [Desk]::IsIconic($front)) 'the terminal in front is still minimised'
        if ($hold -or $base -eq [IntPtr]::Zero) { Need ($before -notcontains $front) 'a new window was asked for, and the window in front is one that was there already' }
        else { Need ($front -eq $base) 'the tab did not go to the terminal window that was open, or that window is not the one in front' }
        # 0: Windows let the terminal come up by itself; 10 + n: it had to be asked for n times
        $asked = 'without being asked for'; if ($code -ge 10) { $asked = "after being asked for $($code - 10) time(s)" }
        Write-Host "      a ${what}: in front $asked"
        $front
    }

    # the tab of a press has ended: its window is gone, or shows the tab it had before
    function Wait-TabGone([IntPtr] $window, [IntPtr] $base) {
        $ok = Wait-Until { -not [Desk]::IsWindow($window) -or ($window -eq $base -and [Desk]::Title($window) -eq $BaseTitle) } 12000
        Need $ok 'the tab the test opened did not close by itself'
    }

    function Invoke-BothPresses([string] $context, [bool] $withBase, [bool] $minimised, [string] $others = 'skip') {
        $script:Others = $others
        Assert-Free $others
        $was = [Desk]::GetForegroundWindow()
        $base = [IntPtr]::Zero
        if ($withBase) { $base = Open-Base 22 $was }
        try {
            $s = Open-Plugin @{ STATUSAI_DECK_TAB = (Get-TabCommand $TestTitle 3) } $true
            [void] (Show-Key $s $context)
            $s.Run.Proc.Refresh(); $handles = $s.Run.Proc.HandleCount
            foreach ($hold in $false, $true) {
                if ($minimised) { [void] [Desk]::ShowWindow($base, 6); Start-Sleep -Milliseconds 400 }
                elseif ($withBase) { Put-Behind $base $was }
                $front = Invoke-Press $s $context $hold $base
                Wait-TabGone $front $base
                if ($was -ne [IntPtr]::Zero -and [Desk]::IsWindow($was)) { [void] [Desk]::Front($was) }
            }
            # Starting a Store app loads Windows' app model into the process that does it, 63 handles
            # that never go: the press is a process of its own so that the plugin is not that process.
            # Two handles are what Windows takes for the first program a process starts.
            $s.Run.Proc.Refresh(); $after = $s.Run.Proc.HandleCount
            Write-Host "      the plugin: $handles handles before the two presses, $after after them"
            Need ($after - $handles -le 6) "the plugin held $handles handles before the two presses and $after after them"
            if ($script:Guarded -eq $true) { Write-Host '      the control stayed behind: these presses came from where Windows guards the foreground' }
            elseif ($script:Guarded -eq $false) { Write-Host '      the control came to the front by itself: these presses say nothing about a plugin' }
        } finally {
            # the test's own terminal closes by itself; wait for that, so the next case starts without it
            if ($base -ne [IntPtr]::Zero) { [void] (Wait-Until { -not [Desk]::IsWindow($base) } 30000) }
            if ($was -ne [IntPtr]::Zero -and [Desk]::IsWindow($was)) { [void] [Desk]::Front($was) }
        }
    }

    # Whether Windows keeps a program that stands where the plugin does from the foreground, on
    # this desktop, now: the cases after it rest on that. Windows Terminal is running, with a
    # window behind the one in front, as it is for a user who has a terminal open; a new window
    # is asked of it as the plugin first asked, with nothing done for it, and has to stay behind
    # the window in front. A Windows Terminal that is not running yet is no control: a program
    # that has just been started may bring up its first window, and the terminal's first window
    # comes to the front whoever starts it. Where the control comes to the front, the case is
    # skipped and says why.
    $Cases['live: the control: a terminal started with nothing done for it stays behind'] = {
        Assert-Free 'allow'
        $was = [Desk]::GetForegroundWindow()
        if ($was -eq [IntPtr]::Zero) { throw [Skip]::new('no window is in front for a terminal to stay behind') }
        $base = Open-Base 12 $was
        try {
            $before = @([Desk]::Terminals())
            Start-TerminalApart ('-w new ' + (Get-TabCommand $ControlTitle 3))
            $script:Control = [IntPtr]::Zero
            $ok = Wait-Until { foreach ($w in [Desk]::Terminals()) { if ($before -notcontains $w -and [Desk]::Title($w) -eq $ControlTitle) { $script:Control = $w; return $true } }; $false } 10000
            if (-not $ok) { Fail 'the control terminal did not open' }
            Start-Sleep -Milliseconds 1200            # as long as a press gives a terminal to get to the front
            $front = [Desk]::GetForegroundWindow()
            $script:Guarded = $front -ne $script:Control -and $front -ne $base
            Write-Host "      in front a second after the control terminal opened: $([Desk]::Describe($front))"
            [void] (Wait-Until { -not [Desk]::IsWindow($script:Control) } 12000)
        } finally {
            [void] (Wait-Until { -not [Desk]::IsWindow($base) } 20000)
            if ([Desk]::IsWindow($was) -and [Desk]::GetForegroundWindow() -ne $was) { [void] [Desk]::Front($was) }
        }
        if (-not $script:Guarded) { throw [Skip]::new('Windows let the control terminal come to the front by itself: on this desktop, now, the cases after it say nothing about a plugin') }
    }

    $Cases['live: no terminal open: a press opens one in front, and so does a hold'] = { Invoke-BothPresses 'F1' $false $false }
    $Cases['live: a terminal open behind another window: a press puts its tab there and brings it up, a hold brings up a new one'] = { Invoke-BothPresses 'F2' $true $false 'allow' }
    $Cases['live: the terminal minimised: a press brings it back with its tab, a hold brings up a new one'] = { Invoke-BothPresses 'F3' $true $true }
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
    $pass = 0; $fail = 0; $skip = 0
    foreach ($name in $selected) {
        try {
            & $Cases[$name]
            $pass++
            Write-Host "pass  $name"
        } catch [Skip] {
            # a live case that would have been in the way: not run, and not a failure
            $skip++
            Write-Host "skip  $name" -ForegroundColor Yellow
            Write-Host "      $($_.Exception.Message)"
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
    $skipped = ''; if ($skip) { $skipped = ", $skip skipped" }
    Write-Host "$($pass + $fail + $skip) cases: $pass pass, $fail fail$skipped" -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
    if ($fail) { return 1 }
    Remove-Item -LiteralPath $script:RunRoot -Recurse -Force
    return 0
}

try { $code = Invoke-Main } finally { Close-Sessions }
exit $code
