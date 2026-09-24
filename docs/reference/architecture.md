<sub>[StatusAI](../../README.md) / [Documentation](../README.md)</sub>

# Architecture

The parts of the status line and how they fit together: the render chain, what draws each row, how
often each source refreshes, the state kept between renders, and what would have to change to move
it elsewhere.

## The render chain

```
Claude Code  --stdin JSON-->  statusai.exe  --stdin-->  cship.exe  -->  starship
  statusLine hook              (this repo)              (Apache-2.0)    (ISC, optional)
  refreshInterval: 60               |
                                    +--> transcript tree      tokens, tool calls
                                    +--> api.anthropic.com    5h / 7d / scoped limits
                                    +--> ecb.europa.eu        EUR reference rate
                                    +--> .credentials.json    OAuth token, plan tier
```

`statusai` reads the status-line payload on stdin, pipes it through `cship`, takes its stdout,
appends the meta segment to the last non-empty line, and inserts the token rows and limit rows
beneath it. If `cship` returns nothing, as it does with a payload it cannot parse, there is no line
to append to, and the block is emitted on its own with the meta segment on its first row rather than
dropped; see [layout.md](layout.md#when-there-is-no-host-line).

`cship` has to be in the same directory as `statusai`. Windows searches the calling executable's
own directory before PATH, and if `cship` isn't found, the failure path is `catch { return input; }`,
which echoes the entire raw session JSON onto the status line. Keeping the two together is a
requirement, not a convenience.

Without starship on PATH the `$starship_prompt` line silently vanishes and cship's own modules still
render. That degrades gracefully, which is why starship is optional. `cship` does honour a
pre-existing `STARSHIP_CONFIG` environment variable, though, so someone who has set one globally
will silently get their own config rather than the shipped one.

## What each row is

| row | produced by |
|---|---|
| prompt line | starship, via cship; includes RAM (`memory_usage`) and the GPU (`custom.gpu`, which shells out to `nvidia-smi` on **every render**) |
| model line | cship (`$cship.model $cship.effort $cship.context_bar`) + our meta segment (⏱ 📝 💸 💰) |
| two token rows | this repo |
| 5h / 7d / scoped rows, account line and product breakdown, the meters notice and the on-credit ⚠ row | this repo, from the OAuth usage endpoint (see [limits.md](limits.md)) |

## Update cadence

Four clocks, stacked:

| layer | interval | trigger |
|---|---|---|
| Claude Code running `statusai` | `refreshInterval: 60` | plus every new assistant message, after `/compact`, on permission-mode change; debounced 300 ms |
| OAuth limit bars, product breakdown, on-credit alarm, ignored meters | cached **50 s** | `FreshVal`: `now - t < 50`, else re-fetch behind a named mutex; a failed fetch leaves it stale, so the next render retries |
| EUR rate | cached **24 h** | ECB daily feed |
| token rows, account line | **every render** | incremental parse of appended bytes only |

A 60 s refresh against a 50 s cache means an idle render almost always finds the cache stale, so the
usage endpoint is hit roughly once a minute while a session is open. The cache only earns its keep
during bursts. Setting the cache to ~70 s, or `refreshInterval` to 45, would make it actually cache.

**The cache holds the rows' figures, not the drawn rows.** `rows` has a line per limit row: its
label, percentage, hours to its reset, whether it has one, pace, whether the pace is still gated,
and severity. Every session draws them at its own width, which comes from its own terminal, so rows
drawn once would be the wrong width in any session of another. Beside them, `bd`, `cr` and `ig`
hold the product breakdown, the on-credit alarm and the meters it did not draw from the same fetch,
already reduced to what is shown, and those are laid out per render as well. So the drawing is
always the running build's own; what a cached entry still carries from the build that fetched it is
the figures, the pace above all. On a machine with a live session that is the trap described in
[development.md](../development.md). Until 2026-09-24 the cache held the drawn rows, in `val`, and
a new build served its predecessor's drawing too. `fail` and `why` count the fetches that have
failed in a row and keep the latest reason, for the `⚠` row every session draws from them; see
[limits.md](limits.md#when-a-fetch-fails).

## State

| location | contents |
|---|---|
| `HKCU\Software\StatusAI` | limit-bar cache (`ts`, `rows`, `bd`, `cr`, `ig`, `fail`, `why`, `hist`, `acct`, `sn`, `rsS/rsW/rsF`, `vfS/vfW/vfF`), FX rate (`fx`, `fxTs`) |
| `%LOCALAPPDATA%\StatusAI\tokens\<sid>.bin` | `CTK2` token cache: offsets, running totals, two dedup sets |
| named mutex `Global\StatusAI.fetch.<SID>.{adm\|std}` | single-flight on the usage fetch, scoped per user *and* elevation level |

Legacy, no longer written but possibly still on disk: `~/.claude/statusline-usage.json` and
`statusline-cache.json`, and `HKCU\Software\cshipUsage` and `~/.claude/statusline-tokens`, which
held the registry cache and the token cache before the rename to StatusAI. In that old key, `val`
held the drawn rows until 2026-09-24, and the first good fetch after that switch removed it.

With `STATUSAI_OFFLINE` set, a switch for development (see
[development.md](../development.md#offline-beside-live-sessions)), none of the three is touched: the
rows and the euro rate come from a file in that directory, and the token cache and the account files
move into it.

## Portability

The token accounting is fully portable, but the code around it uses a few Windows-only APIs:

| Windows-only API | belongs to |
|---|---|
| `Registry.CurrentUser` (8 sites) | limit-bar cache, and the FX rate cache |
| `WindowsIdentity` / `WindowsPrincipal` | mutex naming |
| `Global\` mutex | single-flight on the usage fetch |
| `net10.0-windows` TFM | consequence of the above |

Everything in the walker is cross-platform: `Path`, `FileStream`, `BinaryReader`, `JsonDocument`,
`Environment.SpecialFolder.UserProfile` and `LocalApplicationData`. The transcript tree is located
from `transcript_path` in the stdin payload, so no path is hardcoded.

A Linux/macOS port means replacing the registry with a JSON file and the mutex naming with a lock
file. The accounting needs no changes.

| move it to | what breaks | fix |
|---|---|---|
| Linux / macOS | registry, mutex naming, TFM | about an hour |
| a different terminal width | nothing from Claude Code 2.1.153 on, which sets `COLUMNS` to the terminal's width; before that the width is 141 unless `STATUSAI_WIDTH` says otherwise | none |
| a terminal rendering emoji single-width | the grid: `iw[]` declares 4 columns per 2-emoji block | one array |
| a machine without a Nerd Font | cship/starship glyphs, **not** the token rows | see below |
| an API-key-only account | the limit rows and the account line | nothing; it degrades |
| an Enterprise plan | untested: whatever of the session, `weekly_all` and a model-scoped `weekly_scoped` it sends is drawn; anything else is flagged for review | none |
| a machine without an NVIDIA GPU | starship's `custom.gpu` segment | none |
| a different model | nothing: the cost comes from the payload, and there is no price table | none |

The binary itself uses no Nerd Font glyphs, only emoji and `│ ● ○ ✗ ↻ → ⇢ · — … ⚠`. All 45 patched
codepoints in the prompt line come from starship's config, and cship's model line adds two more.
Dropping starship removes ~90% of the font requirement.

## Build

.NET 10 SDK, `PublishAot`, `InvariantGlobalization`, `net10.0-windows`, x64.

```
dotnet publish src/StatusAI.csproj -c Release -r win-x64 -o <out>
```

Output is ~4,83 MiB and really self-contained: no `hostfxr`, no `coreclr` and no VC++
redistributable, because NativeAOT statically links the C++ runtime and uses only the in-box UCRT.
There is **no ARM64 build**, though cship and starship both publish one.

`InvariantGlobalization` means no named culture exists at runtime, which is why nl-NL formatting is
done by swapping separators on invariant output rather than by `CultureInfo`.
