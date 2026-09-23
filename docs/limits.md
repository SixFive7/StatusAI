# Limits

The three limit rows: where the numbers come from, how the projection is built, and one design
that was shipped and reverted.

## The endpoint

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <claudeAiOauth.accessToken from ~/.claude/.credentials.json>
anthropic-beta: oauth-2025-04-20
User-Agent: claude-code/<version>
```

Undocumented, like everything else this project reads. Three-second timeout, and every failure
path is a bare `catch` that leaves the rows rendering their last cached value.

### None of it comes from stdin

Every figure in these three rows is the OAuth response and the registry cache, and the account line
beside them is `~/.claude.json` and `.credentials.json`. Nothing here reads the status-line payload.

That is what makes them the load-bearing part of a degraded render. A payload malformed enough to
silence cship — and with it the prompt line, the model line and the token rows — leaves these rows
completely intact, which is why the block is now emitted on its own rather than dropped when there
is no host line to insert it into. See [layout.md](layout.md#when-there-is-no-host-line).

## Response shape

Two shapes ship in the same payload. `Fetch()` prefers the `limits` array and falls back to the
legacy top-level `five_hour` / `seven_day` objects, which carry `utilization` but no reset time —
so on the fallback path every row shows a reset of `—` and no projection is possible.

```jsonc
{
  "limits": [
    { "kind": "session",       "group": "session", "percent": 34, "severity": "normal",
      "resets_at": "2026-08-15T07:29:59.821118+02:00", "scope": null,  "is_active": false },
    { "kind": "weekly_all",    "group": "weekly",  "percent": 81, "severity": "warning",
      "resets_at": "2026-08-15T04:59:59.821140+02:00", "scope": null,  "is_active": true  },
    { "kind": "weekly_scoped", "group": "weekly",  "percent": 21, "severity": "normal",
      "resets_at": "2026-08-15T04:59:59.821391+02:00", "is_active": false,
      "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null } }
  ],
  "five_hour":  { "utilization": 34.0, "resets_at": "…", "limit_dollars": null, … },
  "seven_day":  { "utilization": 81.0, "resets_at": "…", … },
  "extra_usage": { "is_enabled": false, "monthly_limit": 1000, "used_credits": 0.0,
                   "currency": "EUR", "disabled_reason": "out_of_credits", … },
  "spend": { "used": { "amount_minor": 0, "currency": "EUR", "exponent": 2 },
             "limit": { "amount_minor": 1000, … }, "percent": 0, "enabled": false, … },
  "member_dashboard_available": false
}
```

**Most sibling keys are null and that is normal, not a fault.** A live Max 20 account returns
`seven_day_oauth_apps`, `seven_day_opus`, `seven_day_sonnet`, `seven_day_cowork`,
`seven_day_omelette`, `tangelo`, `iguana_necktie`, `omelette_promotional`, `cinder_cove` and
`amber_ladder` all as `null`, plus `nimbus_quill` as a zeroed object. They are feature buckets for
plans and surfaces this account does not have. Do not add rows for them speculatively — read
`limits[]`, which only contains what actually applies.

`kind` values seen in the wild: `session`, `weekly_all`, `weekly_scoped`. `Fetch()` hard-requires
the first two, which is why Enterprise (monthly credits instead) loses all three rows. Only one
scoped row is displayed; if several ever appear the highest percentage wins.

`severity` drives the label colour through `SevColor` — `normal` plain, `warning` amber,
`critical` bold red.

### `is_active` is reported and unused

Each entry carries `is_active`, and exactly one is true: the limit that is currently the binding
constraint. In the sample above `weekly_all` is active at 81% while the session sits at 34%.

Nothing in `Program.cs` reads it. It is the cheapest available answer to "which of these three
should I actually be worried about", and it comes free in a payload already being parsed. Worth
picking up — but note it is a *server* judgement about which limit binds, not a statement about
how the windows relate to each other. See the rejected design below for why that distinction
matters.

## The projection

Each row renders as:

```
label  bar(now)  now%  ↻ reset  → eta  ⇢ bar(projected)  projected%
```

- **`hist`** in `HKCU\Software\cshipUsage` keeps `t:s:w:f` samples, pruned to the last hour.
- **Slope** is Theil-Sen — the median of all pairwise slopes — chosen because it shrugs off a
  single stray step that window invalidation missed. **Gated** until at least 4 samples span at
  least 10 minutes; until then the row shows `early` rather than a guess.
- **Rate** is clamped to 40 %/h, and a row only counts as burning above 0,5 %/h.
- **Horizon** is the hours to this row's own reset, uncapped. It used to carry a `min(…, 8h)`
  ceiling, which made `⇢` mean *at reset* on the 5h row and *in 8 hours* on the 7d row — one glyph
  with two meanings a line apart, so a red "hits 100% in 2d19h" could sit beside a calm `⇢ 20%`.
  The consequence of removing it is that a 7d row burning steadily now saturates the 300% cap,
  which is the honest reading: at this rate the week is gone.
- **Projection** is `now + rate × horizon`, capped at 300%. Bars past 100% switch from `●` to `✗`.
- **Bar fill** is `ceil(pct/10)`, not a round. Rounding stood still across the only boundary that
  matters — everything from 95% to 104% drew ten identical `●`, and `✗` needed 105% to appear.
  Ceiling over-reports every bar by design (31% draws 4 of 10) and was chosen deliberately: over-
  project rather than hide a crossing. It also aligns the bar's colour zones with `PctColor`'s
  thresholds, which rounding had offset by five points.
- **ETA** is `(100 − now) / rate`, flagged as an overshoot when it lands before the row's reset.
- **No reset time** is its own state, carried as `Lim.HasReset`. `weekly_scoped` returns
  `resets_at: null` and the legacy `five_hour` / `seven_day` shape has no reset at all; both used
  to arrive as an `hrs` of 0 and render `↻ 0m`, an assertion that the window resets this instant.
  They now render `↻ —`, and while a row is in that state its projection is suppressed and its
  overshoot test is not evaluated — there is no deadline to test against.

### History invalidation

A series is only meaningful within one account and one window. `WindowCheck` discards it when
`resets_at` moves by more than 60 seconds, or when a percentage drops by more than 1 — a drop is
impossible organically inside a window, so it means the window rolled. An account switch clears
`hist` outright.

That last one is visible in practice: switch accounts and all three rows immediately read
`→ early`, because the whole series was foreign and was thrown away. It is correct behaviour and
not a bug report.

## Rejected: clamping the session projection to the weekly reset

**Status: implemented, deployed, reverted. Do not rebuild it. The premise is false.**

Recorded here because the reasoning is seductive and someone will re-derive it.

**The observation.** At 03:49 the bar read: session 34%, own reset 3h41m away, burning ~34,7 %/h,
projecting **161%** with a red overshoot warning at 1h54m. But the weekly window reset in 1h11m —
*before* that projected blowout. If the weekly reset also reset the session counter, the 161% was
an event that could never happen, and the honest projection was 34 + 34,7 × 1,18 = **75%**.

**What was built.** A `Wall` field on each row holding hours-to-the-weekly-reset (`+∞` for the
weekly rows, which reset *with* the wall rather than into it). The session horizon became
`min(own reset, wall)`. Two marks were added: the meaningless "time to 100%" column was replaced
by `⇥ 1h11m` in the weekly row's severity colour, and the projection bar kept the full 161%
trajectory but cut it with `┃` at the wall, ghosting the cancelled tail at `#3E4257`. Net width
cost was zero — bar 16→17 and percent column 3→2 cancel exactly.

**Why it was wrong.** The session window is independent of the weekly window. Nothing in the
payload ever supported the coupling:

- `session.resets_at` is computed on its own schedule and is unaffected by the weekly boundary.
  In the sample it sits at 07:29:59, two and a half hours the far side of the weekly 04:59:59.
- The session window is anchored to first use and lands on a `:30` boundary; the weekly lands on
  `:00`. They are not the same clock.
- `session.is_active: false` against `weekly_all.is_active: true` says only that the weekly limit
  is the current binding constraint. It says nothing about one window resetting the other, and
  reading it that way is the mistake.

The two *weekly* resets, by contrast, genuinely are the same boundary —
`04:59:59.821140` and `04:59:59.821391` differ only in the fraction left over from the server
computing both from one `now`. That coincidence is real; the session one is not.

**What it cost, and what to keep.** The revert was total: `Program.cs` restored and hash-verified
against the pre-change file, binary rebuilt from it, both glyphs confirmed absent from rendered
output. Two things are worth carrying forward anyway:

1. **A real overshoot must outrank an informational mark.** The implementation deliberately kept
   the red `→ 1h54m` countdown whenever 100% would genuinely arrive before *anything* reset the
   window, and only surrendered the column to `⇥` when the reset really did land first. Any future
   design that suppresses a warning in favour of a reassurance needs the same precedence rule.
2. **Keep a cancelled trajectory visible rather than deleting it.** The ghosted tail was chosen
   specifically so that a wrong assumption would still show its consequences on screen. The
   assumption was wrong, and that choice is the only reason it would have stayed diagnosable.

The visual treatment is preserved as a documentation style example in
[templates/weekly-wall.html](templates/weekly-wall.html) — which carries its own correction band,
because the page argues for the design as though it were correct.

## Known gaps

- **No captured usage payload.** `test/probe.json` is a status-line *stdin* payload; there is no
  fixture for the OAuth usage response, so the limit rows cannot be rendered offline or
  regression-tested. Capturing one — with the account identifiers scrubbed — is cheap and would
  have made the rejected design above testable without touching a live account.
- **`is_active`, `group` and `extra_usage` are parsed past and discarded.**
- **Enterprise is unhandled**, not degraded: `Fetch()` requires `kind == "session"` and
  `weekly_all`, and returns false without them, so all three rows vanish with no diagnostic.
