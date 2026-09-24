<sub>[StatusAI](../../README.md) / [Documentation](../README.md)</sub>

# Rejected designs

Designs for the limit rows that were built or proposed and then rejected, each with the reasoning
that made it look right and the evidence that decided against it. What the rows do now is in
[limits.md](../reference/limits.md).

## Rejected: clamping the session projection to the weekly reset

**Status:** implemented, deployed, reverted. Do not rebuild it: the premise is false.

It is recorded here because the reasoning is convincing, and someone will arrive at it again.

### The observation

At 03:49 the bar read: session 34%, own reset 3h41m away, burning ~34,7 %/h, projecting **161%**
with a red overshoot warning at 1h54m. But the weekly window reset in 1h11m, *before* that
projected blowout. If the weekly reset also reset the session counter, the 161% was an event that
could never happen, and the honest projection was 34 + 34,7 x 1,18 = **75%**.

### What was built

A `Wall` field on each row holding the hours to the weekly reset (infinite for the weekly rows,
which reset *with* the wall rather than into it). The session horizon became `min(own reset, wall)`.
Two marks were added: the meaningless "time to 100%" column was replaced by `⇥ 1h11m` in the weekly
row's severity colour, and the projection bar kept the full 161% trajectory but cut it with `┃` at
the wall, ghosting the cancelled tail at `#3E4257`. The net width cost was zero: the bar went from 16
to 17 cells and the percent column from 3 to 2, which cancel exactly.

### Why it was wrong

The session window is independent of the weekly window. Nothing in the payload ever supported the
coupling:

- `session.resets_at` is computed on its own schedule and is unaffected by the weekly boundary.
  In the [response sample](../reference/limits.md#response-shape) it sits at 07:29:59, two and a
  half hours the far side of the weekly 04:59:59.
- The session window is anchored to first use and lands on a `:30` boundary; the weekly lands on
  `:00`. They are not the same clock.
- `session.is_active: false` against `weekly_all.is_active: true` says only that the weekly limit
  is the current binding constraint. It says nothing about one window resetting the other, and
  reading it that way is the mistake.

The two *weekly* resets, on the other hand, really are the same boundary: `04:59:59.821140` and
`04:59:59.821391` differ only in the fraction left over from the server computing both from one
`now`. That coincidence is real, and the session one is not.

### What it cost, and what to keep

The revert was total: `Program.cs` restored and hash-verified against the pre-change file, the binary
rebuilt from it, and both glyphs confirmed absent from rendered output. Two things are worth carrying
forward anyway:

1. A real overshoot must outrank an informational mark. The implementation deliberately kept the
   red `→ 1h54m` countdown whenever 100% would really arrive before *anything* reset the window,
   and only gave the column up to `⇥` when the reset really did land first. Any future design that
   suppresses a warning in favour of a reassurance needs the same precedence rule.
2. Keep a cancelled trajectory visible rather than deleting it. The ghosted tail was chosen so that
   a wrong assumption would still show its consequences on screen. The assumption was wrong, and
   that choice is the only reason it would have stayed diagnosable.

The visual treatment is kept as a documentation style example in
[house-style/weekly-wall.html](house-style/weekly-wall.html), which carries its own correction band,
because the page argues for the design as though it were correct.

## Rejected: a window-average projection (option c)

**Status:** proposed, replayed against a week of use, rejected on 2026-09-23. Never implemented.

It is recorded here because the reading that prompted it will come up again, and the fix looks
obvious.

### The observation

At 19:57 on 2026-09-23 the 7d row read 89%, a red `→ 4h35m` and `⇢ 226%`. Both came from one
number: the 60-minute pace, 2,40 %/h, stretched over the 57 hours to the Saturday 05:00 reset,
nights included. The hour behind it ran at three times the week's average and was the ninth busiest
of 111.

### What was proposed

Keep `→` on the 60-minute pace, and take `⇢` on the weekly rows (7d and scoped) from the window's
own average: `now + now / elapsed * hours to reset`, with `elapsed = 168 h - hours to reset`,
because the API sends only `resets_at`. It would project only when that adds at least one point by
the reset, since the 0,5 %/h test switches the projection on all at once (27% to 85% within an hour
in the replay), and show the current value for a window's first 12 hours. The 5h row would not
change. At 19:57 that is `⇢ 135%` on 7d instead of 226%, and `⇢ 36%` on Fable instead of 24%. The
worst-case width is unchanged: same solver, same 300% cap, same bar cap.

### What the replay showed

The week from 2026-09-19 05:00 to 2026-09-23 20:03 was replayed minute by minute: the meter
reconstructed from this machine's usage and the registry's real samples, read as whole percents
every 60 seconds, through the binary's own slope, gate, clamp, rounding and cap.

- c was steadier. Today's `⇢` stood 50 points or more from where it was an hour earlier in 22% of
  minutes and sat pinned at 300% for 9% of the week; c's never moved more than 16 points in an hour.
- It was also more accurate. Extended 6, 12 and 24 hours ahead, c missed by 3,8 / 6,9 / 14,2 points
  on average against today's 4,8 / 10,5 / 23,0 (today did worse than assuming no further use at 12
  and 24 hours), and c never forecast a 100% that did not come, where today forecast ten.
- But it was slower to react. An hour of heavy use moved c's `⇢` by a median of 6 points, and after
  use stopped for good it would have kept forecasting an overshoot for about 40 hours.
- The shape of the week biased it. The week started quiet and ended busy, so the average lagged it
  and c under-forecast at every horizon. A week with its busy days first flips the sign.
- And it gave two answers on one row. With `→` on one pace and `⇢` on another, the two disagreed 39%
  of the time: a red countdown beside a `⇢` under 100% (`→ 15h13m ⇢ 76%`, Monday 17:00), or
  `→ never` beside a `⇢` over it (`→ never ⇢ 128%`, all Tuesday night).

### The decision

The current behaviour stayed: one pace, the last 60 minutes, for both `→` and `⇢`.

### What to keep

Judge any future forecast change the same way: replay whole-percent samples at the binary's own
cadence, and backtest against "no change" as well as against the current method, because a forecast
that loses to "no change" is not earning its place. The replay, the backtest and every option as it
was drawn are in [limits-decisions.html](limits-decisions.html), under its own correction band.
