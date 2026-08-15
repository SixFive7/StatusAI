# Development

Building, deploying and testing a change to the status line on a live machine. Three of the four
traps below cost real time; the first one is the expensive one.

## Build

.NET 10 SDK (developed against 10.0.302), NativeAOT, `net10.0-windows`, x64.

```bash
cd src && dotnet publish -c Release -r win-x64
# -> src/bin/Release/net10.0-windows/win-x64/publish/cship-usage.exe   ~4,74 MiB
```

## Trap 1 — the cache serves output from the *previous* binary

**This will make a working change look broken, and a broken change look fine.**

`GetUsage()` returns the cached render string whenever `now - ts < 50`. The cache holds a fully
rendered, ANSI-coloured string, not the underlying numbers — so a value written 20 seconds ago by
the *old* binary is returned verbatim by the *new* one. Claude Code re-runs the status line every
60 seconds, so on any machine with a live session there is almost always a fresh entry.

The failure mode is nasty because the numbers are current — only the *rendering* is stale. A test
run of a new build can show freshly fetched percentages laid out by code that is no longer on
disk, which reads as "the fetch works but my change did nothing."

Invalidate before every test:

```powershell
Set-ItemProperty -Path "HKCU:\Software\cshipUsage" -Name "ts" -Value "0"
```

Then run immediately — a live Claude Code window is racing you for the same mutex, and if it wins,
you get its render instead.

## Trap 2 — the binary is locked while the status line runs it

Windows holds the image while the process runs, and it runs every 60 seconds for ~100 ms. A
straight copy fails intermittently. Retry:

```powershell
foreach ($i in 1..12) {
  try { Copy-Item $src $dst -Force -ErrorAction Stop; break }
  catch { Start-Sleep -Milliseconds 700 }
}
```

Back up the outgoing binary first. The convention on the dev machine is a sibling
`cship-usage.exe.bak.<unix-seconds>`, which makes a revert a copy rather than a rebuild.

## Trap 3 — `dotnet publish` output is not what is deployed

The status line runs the binary at its installed location, not the publish directory. Editing
`Program.cs` and rebuilding changes nothing visible until the copy step runs. Conversely, a revert
that only restores the installed binary leaves the publish tree holding the change, ready to be
redeployed by the next person who runs the copy step. **Revert the source, rebuild, and redeploy**
so all three stay consistent.

## Testing a render

The binary reads the status-line payload on stdin. `test/probe.json` holds a captured one; a
minimal payload is enough when only the limit rows matter:

```powershell
$stdin = '{"transcript_path":"","cost":{"total_cost_usd":0,"total_duration_ms":0,"total_lines_added":0,"total_lines_removed":0}}'
$out = $stdin | & "<path>\cship-usage.exe"
```

**Check alignment on the stripped text, not the coloured output.** SGR escapes make every visual
length wrong:

```powershell
($out -replace "`e\[[0-9;]*m", "") -split "`n" |
  ForEach-Object { "{0,3} |{1}|" -f $_.Length, $_ }
```

**Check for a specific glyph by codepoint**, not by pasting the character into a script — encoding
round-trips through the shell are not reliable:

```powershell
$out -match ([char]0x2503)      # ┃
```

## Trap 4 — an account switch looks like a regression

`AccountInfo()` is read fresh from `~/.claude.json` on every render, and a changed `accountUuid`
clears `hist` outright. Immediately after a switch every row reads `→ early` and the percentages
belong to a different account, so they will not match anything observed a minute earlier. That is
`WindowCheck` and the account guard working, not a fault. Check the account line before
investigating a sudden change in the numbers.

## Verifying the accounting

Unchanged by presentation work, but run after any Claude Code upgrade — the transcript layout is
undocumented and fails silently downward:

```powershell
./scripts/Split-MainVsTree.ps1 -Sid <session-id>
./scripts/Verify-Tools.ps1     -Sid <session-id>
./scripts/Decode-TokCache.ps1  -Sid <session-id>
```

See [accounting.md](accounting.md) for the expected figures.
