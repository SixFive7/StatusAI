<sub>[StatusAI](../../README.md) / [Documentation](../README.md)</sub>

# Limits

The limit rows: where the numbers come from, how the projection is built, what else the response
carries and which of it is drawn. The designs that were rejected are in
[rejected-designs.md](../design/rejected-designs.md).

## The endpoint

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <claudeAiOauth.accessToken from ~/.claude/.credentials.json>
anthropic-beta: oauth-2025-04-20
User-Agent: claude-code/2.1.90
```

Undocumented, like everything else this project reads. Three-second timeout. A fetch that fails
leaves the rows drawing the last good one; see [when a fetch fails](#when-a-fetch-fails).

### When a fetch fails

A failed fetch leaves the cache as it was, stale, so the next render to take the lock tries again:
that render is the retry. No render makes a second attempt of its own: that would double the three
seconds one can already spend waiting, and Claude Code cancels a render still running when a newer
update comes along. Meanwhile the rows keep drawing the last good fetch, with the countdowns it
measured.

The first failure is quiet. The second in a row raises a red `⚠` row, which names the source, the
reason and how old the rows still drawn are, and stays until a fetch succeeds:

```
⚠ usage — timed out after 3 s; the limit rows are 2m old
⚠ usage — HTTP 429 from api.anthropic.com; the limit rows are 5m old
⚠ usage — offline, api.anthropic.com not reached; the limit rows are 3m old
⚠ usage — no OAuth token in .credentials.json; no limit rows yet
```

The timeout is HttpClient's own three seconds. Offline is a name that did not resolve or a network
that is down or out of reach. A response that is not JSON reads `unreadable response`, one without a
meter `the response has no meters`, and any other failure is named by its exception. With nobody
signed in there is no token to fetch with and no rows to miss, and the account line already says
`not signed in`, so the row stays away.

From the second failure in a row the retries are spaced out: a render tries again only once the
last attempt is 50 seconds old, so the open sessions between them make at most one attempt every 50
seconds rather than one at every render, each of which could wait three seconds on a timeout. A
render in between makes no attempt and takes no lock, and draws the saved rows and the `⚠` row
straight away. Before that nothing is held back: after a single failure the next render to find the
cache stale tries again. The rule is `MayRetry()`, which the offline renders go through as well.

The count is kept beside the rows in the registry, `fail` for the failures in a row and `why` for
the latest reason, so every session shows the same row, whether or not it was the one that fetched,
and `tryTs` for when the fetch was last tried, which the 50 seconds are measured from. A good fetch
sets `fail` back to 0. Only a fetch that finishes is counted: one cut short by such a cancel counts
as neither, so in a busy session a timeout can go uncounted until a quiet moment. `tryTs`, though,
is written as an attempt starts, so even one cut short holds the next attempt off once the row
shows.

### None of it comes from stdin

Every figure in these rows is the OAuth response and the registry cache, and so are the product
breakdown and the on-credit alarm; the account line beside them is `~/.claude.json` and
`.credentials.json`. Nothing here reads the status-line payload.

That is what makes them the load-bearing part of a degraded render. A payload malformed enough to
silence cship (and with it the prompt line, the model line and the token rows) leaves these rows
completely intact, which is why the block is now emitted on its own rather than dropped when there
is no host line to insert it into. See [layout.md](layout.md#when-there-is-no-host-line).

## Response shape

Two shapes ship in the same payload. `ParseUsage()` prefers the `limits` array and falls back to
the legacy top-level `five_hour` / `seven_day` objects, which carry `utilization` but no reset
time, so on the fallback path every row shows a reset of `—` and no projection is possible. The
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
  "five_hour":  { "utilization": 34.0, "resets_at": "...", "limit_dollars": null, ... },
  "seven_day":  { "utilization": 81.0, "resets_at": "...", ... },
  "extra_usage": { "is_enabled": false, "monthly_limit": 1000, "used_credits": 0.0,
                   "currency": "EUR", "disabled_reason": "out_of_credits", ... },
  "spend": { "used": { "amount_minor": 0, "currency": "EUR", "exponent": 2 },
             "limit": { "amount_minor": 1000, ... }, "percent": 0, "enabled": false, ... },
  "member_dashboard_available": false,
  // from 2026-09-23 on (the switched-to account):
  "seven_day_breakdown": { "as_of": "...", "window_started_at": "2026-09-21T07:00:00.037784+00:00",
    "rows": [ { "key": "claude_code", "display_name": "Claude Code", "percent": 99 },
              { "key": "chat",        "display_name": "Chats",       "percent": 0 },
              { "key": "cowork",      "display_name": "Cowork",      "percent": 1 },
              { "key": "other",       "display_name": "Other",       "percent": 0 } ] }
}
```

Most sibling keys are null, and that is normal. A live Max 20 account returns
`seven_day_oauth_apps`, `seven_day_opus`, `seven_day_sonnet`, `seven_day_cowork`,
`seven_day_omelette`, `tangelo`, `iguana_necktie`, `omelette_promotional`, `cinder_cove` and
`amber_ladder` all as `null`, plus `nimbus_quill` as a zeroed object. They are feature buckets for
plans and surfaces this account does not have. Don't add rows for them speculatively: read
`limits[]`, which only contains what actually applies.

`kind` values seen in the wild: `session`, `weekly_all`, `weekly_scoped`.

`severity` drives the label colour through `SevColor`: `normal` plain, `warning` amber, `critical`
bold red.

### Three meters are drawn; any other is flagged

**Status:** ignored and flagged pending review, decided on 2026-09-24. In one build, deployed for
two hours that morning, every entry in `limits[]` got a row. That part was reverted: the code should
be reviewed whenever a new meter turns up, so it keeps working for the three known meters and
ignores any other for now. A meter this binary has never been checked against is not drawn on
trust. It is named instead, so that the code gets reviewed before it shows the meter. That is why a
fourth meter is missing from the rows, and why that is a decision, not a regression.

- What is drawn: `session` as `5h`, `weekly_all` as `7d`, and one model-scoped `weekly_scoped` (a
  `scope.model` with a name and no `scope.surface`) under its model's name. The scoped row stays
  with the meter the history has been following (`sn`) for as long as the server still sends it,
  and otherwise takes the highest, as it always has. The three are drawn in that order whatever
  order the server sends them in, and each has its own history series.
- Everything else is flagged: a kind never seen, a surface-scoped meter, a meter scoped to both a
  model and a surface, a second model-scoped one. None of these is drawn. Each is named in an amber
  `⚠` row at the foot of the block, as in `Cowork (weekly_scoped, surface)`: the name it would be
  drawn under (the model, else the surface, else the kind), then its kind and what its scope names.
  See [layout.md](layout.md#the-meters-notice).
- A missing meter no longer costs every row. `Fetch()` once returned nothing without both the
  session and `weekly_all`, so an account lacking either lost all its rows. Any non-empty
  `limits[]` counts now: a missing meter only means one row fewer, a response with none of the
  three draws no rows and flags what it has, and a series whose meter is absent records `-1`, which
  the slope skips.

Names are drawn with every control character replaced, so nothing the server sends can act on the
terminal. The flag is cached beside the rows' figures, registry value `ig`, one meter per line, so
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

- money spent this period, `spend.used.amount_minor` or `extra_usage.used_credits` above 0, which
  reads `⚠ on credit — $12,40 spent beyond the plan this period`;
- a limit at 100% while credits are on (`spend.enabled` or `extra_usage.is_enabled`), since usage
  bills from then on: `⚠ on credit — 5h at 100% with usage credits on; $0,00 spent so far this
  period`. Any meter in the response counts, a flagged one included, because a limit that bills is
  reported whether or not it is drawn.

The amount is `spend.used` when it has one (minor units with their own `exponent` and `currency`),
and otherwise `extra_usage.used_credits`, read as minor units too, with `decimal_places` as the
exponent. That second reading is an inference: in the August response `extra_usage.monthly_limit`
was 1000 where `spend.limit.amount_minor` was 1000 at exponent 2, and no non-zero `used_credits` has
been seen to confirm it. `USD` and `EUR` get their sign, as in the meta segment; any other currency
gets its code.

Switched on and being spent are separate fields. `spend.enabled` and `extra_usage.is_enabled` say
credits are on; `spend.used.amount_minor` and `extra_usage.used_credits` say money has been spent
this period. The response has no field for "billing right now", which is inferred from a limit at
100% while credits are on. The 2026-09-23 response also carries `extra_usage.user_disabled: true`,
`credits_ever_enabled: true`, `spend_limit_reached`, `can_toggle` and `can_purchase_credits`, so a
softer "credits are on" notice could be told apart from the alarm without guessing.

The breakdown and the alarm are cached beside the rows' figures, in registry values `bd` and `cr`,
because a cache hit does not fetch and must still draw them.

### `is_active` is reported and unused

Each entry carries `is_active`, and exactly one is true: the limit that is currently the binding
constraint. In the sample above `weekly_all` is active at 81% while the session sits at 34%.

Nothing in `Program.cs` reads it. It is the cheapest available answer to "which of these three
should I actually be worried about", and it comes free in a payload already being parsed, so it is
worth picking up. Bear in mind that it is a *server* judgement about which limit binds, not a
statement about how the windows relate to each other. See
[the rejected weekly-wall design](../design/rejected-designs.md#rejected-clamping-the-session-projection-to-the-weekly-reset)
for why that distinction matters.

## The projection

Each row renders as:

```
label  bar(now)  now%  ↻ reset  → eta  ⇢ bar(projected)  projected%
```

- `hist` in `HKCU\Software\StatusAI` keeps `t:s:w:f` samples, pruned to the last hour.
- The slope is Theil-Sen, the median of all pairwise slopes, chosen because it shrugs off a single
  stray step that window invalidation missed. It is **gated** until at least 4 samples span at
  least 10 minutes; until then the row shows `early` rather than a guess.
- The rate is clamped to 40%/h, and a row only counts as burning above 0,5%/h.
- The horizon is the hours to this row's own reset, uncapped. It used to carry a `min(..., 8h)`
  ceiling, which made `⇢` mean *at reset* on the 5h row and *in 8 hours* on the 7d row: one glyph
  with two meanings a line apart, so a red "hits 100% in 2d19h" could sit beside a calm `⇢ 20%`.
  Without the ceiling, a 7d row burning steadily now saturates the 300% cap, which is the honest
  reading: at this rate the week is gone.
- The projection is `now + rate * horizon`, capped at 300%. Bars past 100% switch from `●` to `✗`.
  `→` and `⇢` share this one pace; taking `⇢` from the window's average instead was replayed and
  rejected (see [rejected-designs.md](../design/rejected-designs.md#rejected-a-window-average-projection-option-c)).
- The bar fill is `ceil(pct/10)`, not a round. Rounding stood still across the only boundary that
  matters: everything from 95% to 104% drew ten identical `●`, and `✗` needed 105% to appear. The
  ceiling over-reports every bar by design (31% draws 4 of 10) and was chosen deliberately, to
  over-project rather than hide a crossing. It also aligns the bar's colour zones with `PctColor`'s
  thresholds, which rounding had offset by five points.
- The ETA is `(100 - now) / rate`, and its colour says where it lands against the row's own reset:
  **red** when 100% comes first, so the window runs out before it resets, and **forest green**
  `#28A428` when the reset comes first, so at this pace this window never runs out. The time is
  kept either way, because it still says what the pace is. It used to be dim in the second case,
  which made the one reassuring reading on the row look like the others. Forest cost no width and
  no new word, where replacing the time with `never`, a `↻ first` marker or `—` would each have
  changed what the column means. A row with no reset time has nothing to test against and stays
  dim, as do `early`, `maxed` and `never`. The same rule holds on every row: 5h, 7d, scoped.
- No reset time is its own state, carried as `Lim.HasReset`. On 2026-08-18 `weekly_scoped` at 0%
  returned `resets_at: null`, three samples running (the 08-15 and 09-23 responses carry a time
  there), and the legacy `five_hour` / `seven_day` shape has no reset at all. Both used to arrive
  as an `hrs` of 0 and render `↻ 0m`, an assertion that the window resets this instant. They now
  render `↻ —`, and while a row is in that state its projection is suppressed and its `→` is
  neither red nor forest, since there is no deadline to test against.
- A row at 100% is blocked until its window resets, so where it is heading does not apply for now.
  `→` reads `maxed` (or `early` if there is no trend yet), and the `⇢` segment is kept but drawn
  wholly dim `#6E738D`: the glyph, every bar cell including the `✗` marks, and the percentage, which
  would otherwise be red past 100%. It is greyed like a disabled control rather than removed, so the
  row keeps its shape and the pace stays readable. Decided on 2026-09-24; the glyphs, and so the
  width, are exactly those of the coloured segment.

```
5h ●●●●●●●●●● 100% ↻ 3h36m → maxed ⇢ ●●●●●●●●●●✗✗✗✗✗       143%
```

### History invalidation

A series is only meaningful within one account and one window. `WindowCheck` discards it when
`resets_at` moves by more than 60 seconds, or when a percentage drops by more than 1: a drop cannot
happen naturally inside a window, so it means the window rolled. An account switch clears `hist`
outright.

That last one is visible in practice. Switch accounts and all three rows immediately read
`→ early`, because the whole series was foreign and was thrown away. That is correct behaviour, not
a bug.

## Known gaps

- The history cannot be run offline. `STATUSAI_OFFLINE` takes a usage response through the same
  `ParseUsage()` as a live fetch and draws it, and the
  [render tests](../development.md#the-render-tests) pin that parse and the drawing, but the
  forecast inputs come from the fixture, so the window checks and the slope cannot be
  regression-tested. See [development.md](../development.md#offline-beside-live-sessions).
- A failed fetch keeps the last good rows, their countdowns stopped where that fetch measured them;
  from the second failure in a row a `⚠` row gives their age (see
  [when a fetch fails](#when-a-fetch-fails)). Claude Code's payload has since gained
  `rate_limits.five_hour` and `rate_limits.seven_day`, a used percentage and a reset time each,
  which could stand in for them and which nothing here reads.
- `is_active` and `group` are parsed past and discarded.
- A new meter is not drawn until its code is reviewed. Only the session, `weekly_all` and one
  model-scoped `weekly_scoped` are; anything else is named in the amber `⚠` row instead (see
  [above](#three-meters-are-drawn-any-other-is-flagged)). None has been seen yet.
- Enterprise is untested. Whatever it sends of the three is drawn, and the rest is flagged.
