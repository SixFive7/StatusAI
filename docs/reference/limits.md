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

### Who fetches, and when

Every fetch goes through `GetUsage()`, whoever asks for it:

- A render, once the shared copy is 50 seconds old. That is once a minute for as long as a terminal
  session is open, whether Claude is working in it or not, because Claude Code runs the status line
  every 60 seconds. A render that finds another token in `.credentials.json` than the one the copy
  was fetched with fetches at once, however young the copy is; see
  [when the token changes](#when-the-token-changes).
- `statusai --refresh`, which calls `GetUsage()` and exits without drawing anything. The
  [Stream Deck key](architecture.md#the-stream-deck-key) starts it while Claude is working and its
  key is visible: 62 seconds after the last fetch or attempt by anyone, which leaves a status line
  that has fetched two seconds ahead of it for good, and not at all while the shared copy is under
  50 seconds old.

Neither asks while a 429 holds the token off; see
[a 429 that says how long](#a-429-that-says-how-long). And at a switch to another account neither
asks if claude-swap fetched the new account's usage a moment before: its figure stands in for the
fetch; see [claude-swap's figure at a switch](#claude-swaps-figure-at-a-switch).

A session with no terminal, in the VS Code extension for instance, runs no status line and so
fetched nothing before there was a key: on 2026-10-01 the shared copy went 40 minutes without a
fetch while seven such sessions were open and the 5h row went from 10% to 16%. With the key, the
calls per hour come to this:

| | fetches | how far apart, measured |
|---|---|---|
| a terminal session open | 60 an hour, 72 at most, by the status line | 59,8 to 60,1 s |
| Claude working with no terminal open, the key visible | at most 58 an hour, by `--refresh`, and one more a minute after the last write | 61,6 to 62,7 s |
| nothing happening, or no key visible | none | |

The lock, the 50 seconds and the count of failures are shared, so the two never add up: with both
running, whichever comes first fetches and the other finds the copy fresh. That is the status
line once it has fetched, since its 60 seconds come round before the key's 62. A key that was
ahead of an idle terminal stays ahead for half an hour at most, by two seconds less each minute.

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
⚠ usage — timed out after 3 s; no limit rows for the new account yet
```

The first one is quiet because the last good rows stand in for it. Right after a switch to another
account there are none to stand in, since the old account's are not drawn, so the new account's
first failure raises the row at once, with the last of the reasons above; see
[when the token changes](#when-the-token-changes).

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
cache stale tries again. Nor is a token that has not been tried yet: after a switch the new
account's token is tried at once, however the old one's attempts went. The rule is `MayRetry()`,
which the offline renders go through as well.

The count is kept beside the rows in the registry, `fail` for the failures in a row and `why` for
the latest reason, so every session shows the same row, whether or not it was the one that fetched,
and `tryTs` for when the fetch was last tried, which the 50 seconds are measured from. A good fetch
sets `fail` back to 0. Only a fetch that finishes is counted: one cut short by such a cancel counts
as neither, so in a busy session a timeout can go uncounted until a quiet moment. `tryTs`, though,
is written as an attempt starts, so even one cut short holds the next attempt off once the row
shows. Beside it, `tryTok` is the print of the token the attempt was made with (see below). The
count is of failures in a row with one token: the first attempt with a token not tried before
starts it again, so the row says what the new token's own fetches came to. `holdTs` is when a
429's hold lifts, 0 for none; see below.

### A 429 that says how long

A 429 is the endpoint refusing a token for a while, and it says for how long in `Retry-After`. On
13 October, six requests within 7 seconds of a switch of account, four from Claude Code's VS Code
windows, one from claude-swap and one from StatusAI, drew a 429 that held every caller with that
token off for 300 seconds. StatusAI did not read the header then, and asked four more times inside
the block; claude-swap, asking near its end, was told `Retry-After: 4`.

The `Retry-After` of a 429 now sets `holdTs`, when the block lifts: a number of seconds from the
response or an HTTP date, as HttpClient reads the header, rounded up to the second so that an
attempt on the dot is never early. Until then no render and no `--refresh` tries the token that
drew it, whatever the count says (`MayRetry()`), and the key starts no `--refresh` at all. The hold
is the token's, like the count: a token not tried yet, after a switch or a renewal, is tried at
once, and its first attempt drops the hold. A good fetch clears it. Once the time has passed,
fetching goes on as before: the next render to find the copy stale tries again.

The hold is at most an hour, `MaxHold`, whatever the header asks. An hour is what Claude Code caps
the same endpoint's `Retry-After` at (3.600.000 ms in the 2.1.296 bundle, which waits 300.000 ms
when there is no header), and no block longer than that has been seen. A longer ask is more likely
a broken header than a block, and an hour without a fetch is long already for rows that say where
the limits stand; past it, being wrong costs one request an hour. A hold more than an hour ahead of
the clock, as when the clock has been set back, holds nothing off. Nor does a 429 without a
`Retry-After`, or with one that cannot be read: it is retried like any other failure, the first
time at the next render and from the second failure in a row once every 50 seconds.

A held 429 raises the `⚠` row at its first failure, since no retry comes before the time it names,
and the row names that time, in the machine's time zone:

```
⚠ usage — HTTP 429 from api.anthropic.com until 12:49:50; the limit rows are 5m old
⚠ usage — HTTP 429 from api.anthropic.com until 12:49:50; no limit rows for the new account yet
```

Once the time has passed the row loses its `until` and stays, like any other, until a fetch
succeeds. The [key](../guide/stream-deck.md#what-it-shows) says it on its first small line as
`⚠ 429 until 12:50`: to the minute, like every time on the key, and rounded up, so that it never
says the block ends before it does.

The time costs 15 columns, ` until 12:49:50`. The longest row it makes, the second above, is 95
columns where the longest had been 87: it fits whole in a terminal of 101 columns or more without
`statusLine.padding`, and is shortened with `…` in a narrower one, like any other reason.

### When the token changes

The shared copy is one for the machine, not one per account, so it is tied to the credential it
was fetched with. A good fetch stores the print of its access token beside `acct`, in `tok`: the
first 8 hex digits of the token's SHA-256, never the token itself. Every render, every
`--refresh` and every drawing of the key reads the token in `.credentials.json` and compares its
print with `tok`. While they are the same, nothing changes. When they are not, the copy is stale
at once, 50 seconds or not, and the render fetches, through the lock like any other, so a dozen
sessions make one request between them and not a dozen.

A new token is not always another account. Claude Code renews the token about every 8 hours for
the same account, and the claude-swap tool switches between accounts by rewriting
`.credentials.json`, then `~/.claude.json` a moment later. So the account decides what a render
that does not fetch draws while that fetch runs:

| `~/.claude.json`'s `accountUuid` against `acct` | the status line | the Stream Deck key |
|---|---|---|
| the same: the token was renewed | the copy, as before | the copy, as before |
| another, or either cannot be read: a switch | no limit rows, nor what came with them: the breakdown, the meters notice, the on-credit row | both meters grey, `—` and empty cells, `new account` on the first small line |
| the same, after that fetch failed | the copy, and the `⚠` row from the second failure, as always | as always |
| another, after that fetch failed | no limit rows, and the `⚠` row at once: `no limit rows for the new account yet` | `⚠` and the reason on the first small line, `new account` on the second |

Normally the render that notices the new token is the one that fetches, which takes three seconds
at most, and draws the new account's rows itself, so none of this is seen. The account is compared
only once the token has changed: a copy fetched with the token signed in now belongs to that
token's account, whatever `~/.claude.json` says.

It costs no width. Withheld rows take their lines away rather than adding any, and the key says
`new account` on a small line it already had. The new `⚠` reason ends in 37 characters where
`the limit rows are 2m old` has 25: the longest row it makes,
`⚠ usage — offline, api.anthropic.com not reached; no limit rows for the new account yet`, is 87
columns, which fits whole in a terminal of 93 columns or more without `statusLine.padding`, and is
shortened with `…` in a narrower one, like any other reason.

A switch can also overtake a request. If, once the response is in, `.credentials.json` holds
another token than the one the request was made with, the response may be the old account's, so
it is dropped as an attempt that came to nothing, and the next render fetches with the new token
at once. The account is read after the response, so that after a switch which came before the
request, `~/.claude.json` names the new account by then.

`tok` and `acct` are written after the rows and before `ts`, and read before the rows, so a render
that reads them while a fetch is being written can find rows newer than the token and the account
it read, and so withhold them for once, but never draw old rows under a new token.

Neither surface is woken by the switch itself: the status line draws it at its next render, which
Claude Code runs every 60 seconds and at every message, and the key at its next redraw, which is
once a minute while it shows. See [known gaps](#known-gaps).

### claude-swap's figure at a switch

A switch sets off a burst of requests for the new account. claude-swap fetches its usage the moment
it has switched, and every Claude Code session that follows the switch fetches its own, one per VS
Code window. On 13 October six requests came within 7 seconds, one of them the fetch of the render
that noticed the new token, and the sixth drew the 429 [above](#a-429-that-says-how-long).

So at a switch to another account, the render or `--refresh` that would fetch looks first for
claude-swap's figure for that account, in `%USERPROFILE%\.claude-swap-backup\cache\usage.json`, the
table claude-swap keeps of every account's usage, and takes it in place of its own request when
every one of these holds:

- the file has `schemaVersion` 2;
- a row's `email` and `organizationUuid` are those of `oauthAccount` in `~/.claude.json`;
- its `lastGood` has `five_hour` and `seven_day`, each with a `pct` and, while that window is
  open, a `resets_at`, and its `scoped` rows, the model-scoped weekly meters, have a `name` and the
  same;
- its `fetchedAt` is after `.credentials.json` was last written, which is when the token changed,
  and at most 60 seconds ago.

The figure is then saved as the new account's first good fetch, at the time claude-swap made it:
the account and the token's print, the reset times, and the first sample of the new account's
history at that time, so the rows read `→ early` as after any switch. It has the meters'
percentages and reset times and nothing else: no severity, so the labels are in their plain
colour, no product breakdown, no on-credit alarm and no meters notice. StatusAI's next regular
fetch fills those in: the first render 50 seconds or more after claude-swap's fetch, or the key's
refresh 62 seconds after it while Claude is working.

In every other case the render fetches, as it always has: the file missing, as it is wherever
claude-swap is not installed, locked, unreadable or of another schema, no row for the account, or a
figure from before the switch or over a minute old. Nothing in StatusAI needs claude-swap, and a
failure to read its file is silent. The file is opened with sharing that lets claude-swap replace it
meanwhile, which it does by renaming a new file over it, retrying for about 0,75 seconds when that
is refused. Only a switch to another account looks at the file: a token renewed for the same
account fetches as ever.

It costs no width: the rows are the ones a fetch draws, without the breakdown for that minute.

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
happen naturally inside a window, so it means the window rolled. An account switch, seen at the
first good fetch for the new account, clears the whole series outright: `hist`, the times each
series is valid from, `vfS`, `vfW` and `vfF`, and the reset times `WindowCheck` compares with,
`rsS`, `rsW` and `rsF`.

The reset times were kept until 2026-10-13. A meter that has no reset time at the switch, such as a
5h with no window open, then kept the old account's, and once the new account's window opened and
sent one of its own, the two were further apart than 60 seconds and the series restarted a second
time, five minutes after the switch on the day it was found.

The switch is visible in practice. The limit rows go until the new account's first good fetch,
which the render that notices the new token normally makes at once (see
[when the token changes](#when-the-token-changes)), and then all three read `→ early`, because the
whole series was foreign and was thrown away. That is correct behaviour, not a bug.

## Known gaps

- The history cannot be run offline. `STATUSAI_OFFLINE` takes a usage response through the same
  `ParseUsage()` as a live fetch and draws it, and the
  [render tests](../development.md#the-render-tests) pin that parse and the drawing, but the
  forecast inputs come from the fixture, so the window checks and the slope cannot be
  regression-tested. See [development.md](../development.md#offline-beside-live-sessions).
- A failed fetch keeps the last good rows, their countdowns stopped where that fetch measured them;
  from the second failure in a row a `⚠` row gives their age (see
  [when a fetch fails](#when-a-fetch-fails)). After a switch to another account there are no rows
  at all until the new account's first good fetch, or claude-swap's figure in its place. Claude
  Code's payload has since gained `rate_limits.five_hour` and `rate_limits.seven_day`, a used
  percentage and a reset time each, which could stand in for them and which nothing here reads.
- A 429's hold is kept per token, as Claude Code keeps it. Whether the endpoint holds off a token
  or an account is not known, so a token renewed in the middle of a block is tried once, and is
  held again if the block is the account's. A hold given as a date is measured against this
  machine's clock.
- For the minute after a switch that claude-swap's figure stands in for a fetch, the rows have no
  severity, breakdown, credit or meters notice (see
  [claude-swap's figure at a switch](#claude-swaps-figure-at-a-switch)). Nor does the figure say a
  scoped meter's kind or surface: claude-swap lists every meter scoped to a model, so one scoped to
  a model and a surface, which StatusAI flags, would be drawn as the scoped row until that next
  fetch. None has been seen.
- Nothing watches `.credentials.json`, so a switch is not drawn the moment it happens. The status
  line shows it at its next render, within 60 seconds and at the next message; the key at its next
  redraw, within a minute while it shows, and until then each shows what it drew last. The key
  fetches only while Claude is writing, so after a switch with Claude idle and no terminal open
  it reads `new account` until something fetches.
- Only a token that changes is compared. A copy fetched with the token signed in now is drawn as
  that token's account's even if `~/.claude.json` names another, which a switch could only cause
  in the moment between its two writes.
- `is_active` and `group` are parsed past and discarded.
- A new meter is not drawn until its code is reviewed. Only the session, `weekly_all` and one
  model-scoped `weekly_scoped` are; anything else is named in the amber `⚠` row instead (see
  [above](#three-meters-are-drawn-any-other-is-flagged)). None has been seen yet.
- Enterprise is untested. Whatever it sends of the three is drawn, and the rest is flagged.
