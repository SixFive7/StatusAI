# Architecture

## The render chain

```
Claude Code  ──stdin JSON──▶  cship-usage.exe  ──stdin──▶  cship.exe  ──▶  starship
  statusLine hook               (this repo)                (Apache-2.0)     (ISC, optional)
  refreshInterval: 60                 │
                                      ├──▶ transcript tree      tokens, tool calls
                                      ├──▶ api.anthropic.com    5h / 7d / scoped limits
                                      ├──▶ ecb.europa.eu        EUR reference rate
                                      └──▶ .credentials.json    OAuth token, plan tier
```

`cship-usage` reads the status-line payload on stdin, pipes it through `cship`, takes its stdout,
appends the meta segment to the last non-empty line, and inserts the token rows and limit rows
beneath it. If `cship` returns nothing — which is what it does with a payload it cannot parse —
there is no line to append to, and the block is emitted on its own with the meta segment on its
first row rather than dropped; see [layout.md](layout.md#when-there-is-no-host-line).

**`cship` must be co-located with `cship-usage`.** Windows searches the calling executable's own
directory before PATH. If `cship` isn't found, the failure path is `catch { return input; }`, which
echoes the entire raw session JSON onto the status line. Treat co-location as an invariant, not a
convenience.

Without **starship** on PATH the `$starship_prompt` line silently vanishes and cship's own modules
still render — a graceful degradation, which is why starship is optional. Note that `cship` honours
a pre-existing `STARSHIP_CONFIG` environment variable, so a user who sets one globally will silently
get their own config rather than the shipped one.

## What each row is

| row | produced by |
|---|---|
| prompt line | starship, via cship — includes RAM (`memory_usage`) and GPU (`custom.gpu`, shells out to `nvidia-smi` **every render**) |
| model line | cship (`$cship.model $cship.effort $cship.context_bar`) + our meta segment (⏱ 📝 💸 💰) |
| two token rows | this repo |
| 5h / 7d / scoped rows, account line and product breakdown, the meters notice and the on-credit ⚠ row | this repo, from the OAuth usage endpoint — see [limits.md](limits.md) |

## Update cadence

Four clocks, stacked:

| layer | interval | trigger |
|---|---|---|
| Claude Code → `cship-usage` | `refreshInterval: 60` | plus every new assistant message, after `/compact`, on permission-mode change; debounced 300 ms |
| OAuth limit bars, product breakdown, on-credit alarm, ignored meters | cached **50 s** | `FreshVal`: `now - t < 50`, else re-fetch behind a named mutex |
| EUR rate | cached **24 h** | ECB daily feed |
| token rows, account line | **every render** | incremental parse of appended bytes only |

A 60 s refresh against a 50 s cache means an idle render almost always finds the cache stale, so the
usage endpoint is hit roughly once a minute while a session is open. The cache only earns its keep
during bursts. Setting the cache to ~70 s, or `refreshInterval` to 45, would make it actually cache.

**The cache holds the fully rendered, ANSI-coloured string — not the underlying numbers.** That
keeps a cache hit free of all formatting work, but it means a cached entry belongs to whichever
build wrote it. Beside it, `bd`, `cr` and `ig` hold the product breakdown, the on-credit alarm and
the meters it did not draw from the same fetch, already reduced to what is shown: where the
breakdown and the notice fit depends on the render, so they are laid out per render, and a cache
hit must still draw all three. On a machine with a live session that is the trap described in
[development.md](../development.md): a freshly built binary will happily serve a render produced by
its predecessor.

## State

| location | contents |
|---|---|
| `HKCU\Software\cshipUsage` | limit-bar cache (`ts`, `val`, `bd`, `cr`, `ig`, `hist`, `acct`, `sn`, `rsS/rsW/rsF`, `vfS/vfW/vfF`), FX rate (`fx`, `fxTs`) |
| `~/.claude/statusline-tokens/<sid>.bin` | `CTK2` token cache — offsets, running totals, two dedup sets |
| named mutex `Global\cshipUsage.fetch.<SID>.{adm\|std}` | single-flight on the usage fetch, scoped per user *and* elevation level |

Legacy, no longer written but possibly still on disk: `~/.claude/statusline-usage.json`,
`statusline-cache.json`.

With `CSHIP_OFFLINE` set — a dev-loop switch, see
[development.md](../development.md#offline-beside-live-sessions) — none of the three is touched: the
rows and the euro rate come from a file in that directory, and the token cache and the account files
move into it.

## Portability

The split is sharper than expected: **the token accounting is fully portable; the surrounding code
is not.**

| Windows-only API | belongs to |
|---|---|
| `Registry.CurrentUser` (7 sites) | limit-bar cache, and the FX rate cache |
| `WindowsIdentity` / `WindowsPrincipal` | mutex naming |
| `Global\` mutex | single-flight on the usage fetch |
| `net10.0-windows` TFM | consequence of the above |

Everything in the walker — `Path`, `FileStream`, `BinaryReader`, `JsonDocument`,
`Environment.SpecialFolder.UserProfile` — is cross-platform. The transcript tree is located from
`transcript_path` in the stdin payload, so no path is hardcoded.

A Linux/macOS port means replacing the registry with a JSON file and the mutex naming with a lock
file. The accounting needs no changes.

| move it to… | what breaks | fix |
|---|---|---|
| Linux / macOS | registry, mutex naming, TFM | ~an hour |
| a different terminal width | `TermWidth()` defaults to 141; `CSHIP_WIDTH` overrides it, set by hand | have the installer set it |
| a terminal rendering emoji single-width | the grid — `iw[]` declares 4 columns per 2-emoji block | one array |
| a machine without a Nerd Font | cship/starship glyphs, **not** the token rows | see below |
| an API-key-only account | the limit rows and the account line | nothing — degrades |
| an Enterprise plan | untested: whatever of the session, `weekly_all` and a model-scoped `weekly_scoped` it sends is drawn; anything else is flagged for review | — |
| a machine without an NVIDIA GPU | starship's `custom.gpu` segment | — |
| a different model | nothing — cost comes from the payload, there is no price table | — |

**The binary itself uses no Nerd Font glyphs** — only emoji plus `│ ● ○ ✗ ↻ → ⇢ · — … ⚠`. All 45
patched codepoints in the prompt line come from starship's config, and cship's model line adds two
more. Dropping starship removes ~90 % of the font requirement.

## Build

.NET 10 SDK, `PublishAot`, `InvariantGlobalization`, `net10.0-windows`, x64.

```
dotnet publish src/cship-usage.csproj -c Release -r win-x64 -o <out>
```

Output is ~4,83 MiB and genuinely self-contained: no `hostfxr`, no `coreclr`, and no VC++
redistributable — NativeAOT statically links the C++ runtime and uses only the in-box UCRT. There is
**no ARM64 build**, though both cship and starship publish one.

`InvariantGlobalization` means no named culture exists at runtime, which is why nl-NL formatting is
done by swapping separators on invariant output rather than by `CultureInfo`.
