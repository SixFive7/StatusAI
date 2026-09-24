# Limits

The limit rows: where the numbers come from, how the projection is built, what else the response
carries and which of it is drawn, and the designs that were rejected.

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

Every figure in these rows is the OAuth response and the registry cache, and so are the product
breakdown and the on-credit alarm; the account line beside them is `~/.claude.json` and
`.credentials.json`. Nothing here reads the status-line payload.

That is what makes them the load-bearing part of a degraded render. A payload malformed enough to
silence cship — and with it the prompt line, the model line and the token rows — leaves these rows
completely intact, which is why the block is now emitted on its own rather than dropped when there
is no host line to insert it into. See [layout.md](layout.md#when-there-is-no-host-line).

## Response shape

Two shapes ship in the same payload. `ParseUsage()` prefers the `limits` array and falls back to
the legacy top-level `five_hour` / `seven_day` objects, which carry `utilization` but no reset
time — so on the fallback path every row shows a reset of `—` and no projection is possible. The
live fetch and the offline render both parse through it, so a fixture exercises the same code.

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
  "member_dashboard_available": false,
  // from 2026-09-23 on (the switched-to account):
  "seven_day_breakdown": { "as_of": "…", "window_started_at": "2026-09-21T07:00:00.037784+00:00",
    "rows": [ { "key": "claude_code", "display_name": "Claude Code", "percent": 99 },
              { "key": "chat",        "display_name": "Chats",       "percent": 0 },
              { "key": "cowork",      "display_name": "Cowork",      "percent": 1 },
              { "key": "other",       "display_name": "Other",       "percent": 0 } ] }
}
```

**Most sibling keys are null and that is normal, not a fault.** A live Max 20 account returns
`seven_day_oauth_apps`, `seven_day_opus`, `seven_day_sonnet`, `seven_day_cowork`,
`seven_day_omelette`, `tangelo`, `iguana_necktie`, `omelette_promotional`, `cinder_cove` and
`amber_ladder` all as `null`, plus `nimbus_quill` as a zeroed object. They are feature buckets for
plans and surfaces this account does not have. Do not add rows for them speculatively — read
`limits[]`, which only contains what actually applies.

`kind` values seen in the wild: `session`, `weekly_all`, `weekly_scoped`.

`severity` drives the label colour through `SevColor` — `normal` plain, `warning` amber,
`critical` bold red.

### Three meters are drawn; any other is flagged

**Status: decided on 2026-09-24 — ignored and flagged, pending review.** For one commit
(`a84bd3a`) every entry in `limits[]` got a row. The user reverted that part: "I want to review our
code if there is ever a new meter. Keep the code working for our three meters and ignore any other
meters for now." A meter this binary has never been checked against is not drawn on trust. It is
named instead, so that the code gets reviewed before it shows the meter — which is why a fourth
meter is missing from the rows, and why that is a decision, not a regression.

- **Drawn:** `session` as `5h`, `weekly_all` as `7d`, and one model-scoped `weekly_scoped` — a
  `scope.model` with a name and no `scope.surface` — under its model's name. The scoped row stays
  with the meter the history has been following (`sn`) for as long as the server still sends it,
  and otherwise takes the highest, as it always has. The three are drawn in that order whatever
  order the server sends them in, and each has its own history series.
- **Flagged:** everything else — a kind never seen, a surface-scoped meter, a meter scoped to both a
  model and a surface, a second model-scoped one. None is drawn; each is named in an amber `⚠`
  row at the foot of the block, `Cowork (weekly_scoped, surface)`: the name it would be drawn
  under — model, else surface, else the kind — then its kind and what its scope names. See
  [layout.md](layout.md#the-meters-notice).
- **Missing meters:** `Fetch()` once returned nothing without both the session and `weekly_all`, so
  an account lacking either lost every row. Any non-empty `limits[]` counts now: a missing meter
  only means one row fewer, a response with none of the three draws no rows and flags what it
  has, and a series whose meter is absent records `-1`, which the slope skips.

Names are drawn with every control character replaced, so nothing the server sends can act on the
terminal. The flag is cached beside the rendered rows, registry value `ig`, one meter per line, so
a cache hit still shows it.

### The product breakdown

`seven_day_breakdown.rows[]` is each product's share of this week's usage: whole percents that sum
to 100, over a window whose `window_started_at` is exactly seven days before `weekly_all`'s
`resets_at` (21 Sep 07:00 against 28 Sep 07:00 in the first capture). It is drawn after the account
as `CC 99% · Chat 0% · Cowork 1%`: Claude Code is `CC` and Chats is `Chat`, those two and Cowork
always, and `Other` or a product this binary does not know only while it is above 0%. How it fits
the width is in [layout.md](layout.md#the-account-line-and-the-breakdown).

### On credit

Being billed beyond the plan should never happen, so it raises a red `⚠` row of its own, last in the
block, with the amount spent. It fires on either of two conditions:

- **Money spent this period:** `spend.used.amount_minor` or `extra_usage.used_credits` above 0 —
  `⚠ on credit — $12,40 spent beyond the plan this period`.
- **A limit at 100% while credits are on** (`spend.enabled` or `extra_usage.is_enabled`): usage
  from then on bills — `⚠ on credit — 5h at 100% with usage credits on; $0,00 spent so far this
  period`. Any meter in the response counts, a flagged one included: a limit that bills is
  reported whether or not it is drawn.

The amount is `spend.used` when it has one — minor units with their own `exponent` and `currency`
— and otherwise `extra_usage.used_credits`, read as minor units too with `decimal_places` as the
exponent. That second reading is an inference: in the August response `extra_usage.monthly_limit`
was 1000 where `spend.limit.amount_minor` was 1000 at exponent 2, and no non-zero `used_credits` has
been seen to confirm it. `USD` and `EUR` get their sign, as in the meta segment; any other currency
its code.

**Switched on and being spent are separate fields.** `spend.enabled` and `extra_usage.is_enabled` say
credits are on; `spend.used.amount_minor` and `extra_usage.used_credits` say money has been spent
this period. The response has no field for "billing right now" — that is inferred from a limit at
100% while credits are on. The 2026-09-23 response also carries `extra_usage.user_disabled: true`,
`credits_ever_enabled: true`, `spend_limit_reached`, `can_toggle` and `can_purchase_credits`, so a
softer "credits are on" notice could be told apart from the alarm without guessing.

The breakdown and the alarm are cached beside the rendered rows — registry values `bd` and `cr` —
because a cache hit does not fetch and must still draw them.

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
  `→` and `⇢` share this one pace; taking `⇢` from the window's average instead was replayed and
  rejected — see [below](#rejected-a-window-average-projection-option-c).
- **Bar fill** is `ceil(pct/10)`, not a round. Rounding stood still across the only boundary that
  matters — everything from 95% to 104% drew ten identical `●`, and `✗` needed 105% to appear.
  Ceiling over-reports every bar by design (31% draws 4 of 10) and was chosen deliberately: over-
  project rather than hide a crossing. It also aligns the bar's colour zones with `PctColor`'s
  thresholds, which rounding had offset by five points.
- **ETA** is `(100 − now) / rate`, and its colour says where it lands against the row's own reset:
  **red** when 100% comes first — the window runs out before it resets — and **forest green**
  `#28A428` when the reset comes first, so at this pace this window never runs out. The time is
  kept either way, because it still says what the pace is. It used to be dim in the second case,
  which made the one reassuring reading on the row look like the others; forest cost no width and
  no new word, where replacing the time with `never`, a `↻ first` marker or `—` would each have
  changed what the column means. A row with no reset time has nothing to test against and stays
  dim, as do `early`, `maxed` and `never`. The same rule holds on every row — 5h, 7d, scoped.
- **No reset time** is its own state, carried as `Lim.HasReset`. On 2026-08-18 `weekly_scoped` at
  0% returned `resets_at: null`, three samples running — the 08-15 and 09-23 responses carry a time
  there — and the legacy `five_hour` / `seven_day` shape has no reset at all; both used to arrive
  as an `hrs` of 0 and render `↻ 0m`, an assertion that the window resets this instant.
  They now render `↻ —`, and while a row is in that state its projection is suppressed and its
  `→` is neither red nor forest — there is no deadline to test against.
- **A row at 100%** is blocked until its window resets, so where it is heading does not apply for
  now. `→` reads `maxed` — `early` if there is no trend yet — and the `⇢` segment is kept but drawn
  wholly dim `#6E738D`: the glyph, every bar cell including the `✗` marks, and the percentage, which
  would otherwise be red past 100%. Greyed like a disabled control rather than removed, so the row
  keeps its shape and the pace stays readable. Decided on 2026-09-24; the glyphs, and so the width,
  are exactly those of the coloured segment.

```
5h ●●●●●●●●●● 100% ↻ 3h36m → maxed ⇢ ●●●●●●●●●●✗✗✗✗✗       143%
```

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

## Rejected: a window-average projection (option c)

**Status: proposed, replayed against a week of use, rejected on 2026-09-23. Never implemented.**

Recorded here because the reading that prompted it will recur, and the fix looks obvious.

**The observation.** At 19:57 on 2026-09-23 the 7d row read 89%, a red `→ 4h35m` and `⇢ 226%`.
Both came from one number: the 60-minute pace, 2,40 %/h, stretched over the 57 hours to the
Saturday 05:00 reset, nights included. The hour behind it ran at three times the week's average and
was the ninth busiest of 111.

**What was proposed.** Keep `→` on the 60-minute pace, and take `⇢` on the weekly rows — 7d and
scoped — from the window's own average: `now + now ÷ elapsed × hours to reset`, with
`elapsed = 168 h − hours to reset`, because the API sends only `resets_at`. It would project only
when that adds at least one point by the reset — the 0,5 %/h test switches the projection on all at
once, 27% to 85% within an hour in the replay — and show the current value for a window's first 12
hours. The 5h row would not change. At 19:57 that is `⇢ 135%` on 7d instead of 226%, and `⇢ 36%` on
Fable instead of 24%. The worst-case width is unchanged: same solver, same 300% cap, same bar cap.

**What the replay showed.** The week from 2026-09-19 05:00 to 2026-09-23 20:03 was replayed minute
by minute: the meter reconstructed from this machine's usage and the registry's real samples, read
as whole percents every 60 seconds, through the binary's own slope, gate, clamp, rounding and cap.

- **Steadier.** Today's `⇢` stood 50 points or more from where it was an hour earlier in 22% of
  minutes and sat pinned at 300% for 9% of the week; c's never moved more than 16 points in an hour.
- **More accurate.** Extended 6, 12 and 24 hours ahead, c missed by 3,8 / 6,9 / 14,2 points on
  average against today's 4,8 / 10,5 / 23,0 — today did worse than assuming no further use at 12
  and 24 hours — and c never forecast a 100% that did not come, where today forecast ten.
- **Slower to react.** An hour of heavy use moved c's `⇢` by a median of 6 points, and after use
  stopped for good it would have kept forecasting an overshoot for about 40 hours.
- **Biased by the shape of the week.** The week started quiet and ended busy, so the average lagged
  it and c under-forecast at every horizon. A week with its busy days first flips the sign.
- **Two answers on one row.** With `→` on one pace and `⇢` on another, the two disagreed 39% of the
  time: a red countdown beside a `⇢` under 100% (`→ 15h13m ⇢ 76%`, Monday 17:00), or `→ never`
  beside a `⇢` over it (`→ never ⇢ 128%`, all Tuesday night).

**The decision.** The user kept the current behaviour: one pace, the last 60 minutes, for both `→`
and `⇢`.

**What to keep.** Judge any future forecast change the same way: replay whole-percent samples at the
binary's own cadence, and backtest against "no change" as well as against the current method — a
forecast that loses to "no change" is not earning its place. The replay, the backtest and every
option as it was drawn are in [limits-decisions.html](limits-decisions.html), under its own
correction band.

## Known gaps

- **The history cannot be run offline.** `CSHIP_OFFLINE` takes a usage response through the same
  `ParseUsage()` as a live fetch and draws it, and `test/probe.json` is a status-line *stdin* payload
  — but the forecast inputs come from the fixture, so the window checks and the slope cannot be
  regression-tested. See [development.md](development.md#offline-beside-live-sessions).
- **`is_active` and `group` are parsed past and discarded.**
- **A new meter is not drawn until its code is reviewed.** Only the session, `weekly_all` and one
  model-scoped `weekly_scoped` are; anything else is named in the amber `⚠` row instead — see
  [above](#three-meters-are-drawn-any-other-is-flagged). None has been seen yet.
- **Enterprise is untested.** Whatever it sends of the three is drawn, and the rest is flagged.
