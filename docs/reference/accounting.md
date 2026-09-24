<sub>[StatusAI](../../README.md) / [Documentation](../README.md)</sub>

# Accounting

How tokens and tool calls are counted, why the obvious approaches are wrong, and how the result is
verified.

## Where the data is

Sub-agent turns are **not** in the main transcript. Every child, at any depth, writes its own file:

```
~/.claude/projects/<project-slug>/
    <session-id>.jsonl                                  # main thread only
    <session-id>/subagents/agent-<agentId>.jsonl        # one per agent, every depth
    <session-id>/subagents/agent-<agentId>.meta.json    # agentType, description, spawnDepth, parentAgentId
    <session-id>/subagents/workflows/<runId>/agent-*.jsonl
```

The walk is recursive, so nested workflow agents are included. `isSidechain: true` appears **only**
inside child transcripts. It is a marker within them, not a way to find child turns in the parent.

None of this is documented by Anthropic. It was established empirically and it can change without
notice; see *Fragility* below.

## What the sub-agents spend

The three sessions of the table in the next section, split with `Split-MainVsTree.ps1` on their
transcripts, which were still on disk on 2026-09-24. These are the 28%, 78% and 98% the README and
the guide quote:

| session | main | sub-agents | tree | sub-agents' share |
|---|---|---|---|---|
| 66419393 | 38.727.133 | 15.222.671 | 53.949.804 | 28,2% |
| 6bef827e | 63.097.290 | 225.881.243 | 288.978.533 | 78,2% |
| 005d13b6 | 4.246.927 | 204.686.388 | 208.933.315 | 98,0% |

## The two duplication traps

Both were measured, not theorised.

**Block-split duplication, about 2,2x.** One API response is written as N records, one per content
block (thinking, text, each tool_use), and every record repeats the *identical* usage object:

```
msg_011CdoRY8BrryXLiP4us1XnW  line  9  [thinking]  out=5785
                              line 10  [text]      out=5785
                              line 11  [tool_use]  out=5785
                              line 13  [tool_use]  out=5785
                              line 15  [tool_use]  out=5785
```

One session: 407 usage records, 193 unique `message.id`. Naive sum 84.360.179 against a correct
38.727.133.

**Cross-file history inheritance, up to 26x.** Child transcripts contain verbatim copies of
ancestor records, with the same `message.id` *and* the same `uuid`. One message appeared in
**89 of 236** agent files in a single session. Per-file dedup yields 7.535.439.800 tokens; one
global dedup set yields 288.978.533.

And `uuid` is the wrong key for usage. Every content block carries its own, so deduplicating on
`uuid` fails to collapse the first trap:

| session | per-file | by `message.id` | by `uuid` |
|---|---|---|---|
| 6bef827e | 7.535.439.800 | **288.978.533** | 473.886.179 |
| 005d13b6 | 209.087.000 | **208.933.315** | 726.512.971 |
| 66419393 | 53.949.804 | **53.949.804** | 116.856.499 |

## The rules

For tokens, walk the main transcript and every `subagents/**/agent-*.jsonl`. Keep records where
`type == "assistant"` and `message.usage` exists, dedup on `message.id` against **one global set
spanning the whole tree**, and sum four counters: `input_tokens`, `output_tokens`,
`cache_creation_input_tokens`, `cache_read_input_tokens`.

For tool calls, count `tool_use` content blocks and dedup on the block's own `toolu_...` id, which
is globally unique and stable across the copies described above.

The order matters, and this one is a real trap: tool counting must happen *before* the `message.id`
dedup. The sibling records of one API response all repeat its `message.id`, so the dedup returns
early on every record after the first, which is exactly where the `tool_use` blocks live. Count the
tools after the dedup and the result silently comes out near zero.

It mirrors the `uuid` finding: `uuid` is wrong for usage and right for blocks, while `message.id` is
right for usage and useless for blocks.

Main is parsed first, then the children in a stable order, so a record shared into several files is
credited where it is first seen. That attribution is correct for a main/sub split. A *per-agent*
breakdown would be noisier, since one ancestor message can appear in dozens of sibling files and
only one of them gets the credit.

Two kinds of record add nothing. `isApiErrorMessage: true` records carry a usage object whose every
field is zero. `isCompactSummary: true` is a `type: "user"` record with no usage, so it never
affects a sum, but it marks a context reset, so any "context remaining" gauge must restart there.

## Incremental parsing

A full re-parse is not viable. The corpus tail runs to 313 MB across 237 files, which takes ~18,6 s
in PowerShell. Profiling shows I/O is not the bottleneck: reading all 152 MB takes 66 ms, while the
per-line iteration takes 4,4 s.

State is cached in `%LOCALAPPDATA%\StatusAI\tokens\<session-id>.bin`, format magic `CTK2`:

- per-file byte offset parsed so far
- running totals, main and sub, five counters each
- the `message.id` dedup set (64-bit FNV-1a hashes)
- the `toolu_` dedup set

Each render stats the directory, skips files whose length is unchanged, and parses only the
appended bytes, always stopping on a record boundary and never mid-line. This has been checked to
converge: parsing 40 agent files and then adding the remaining 46 in a second invocation lands on
the same total as a cold full parse.

Guards:

- A byte budget of 64 MB of new data per render. A larger backlog is absorbed across several
  renders, and the grand total renders with a trailing `+` until it catches up.
- Shrink detection. A file shorter than its stored offset was rewritten, not appended to. Its ids
  are already in the dedup set, so re-reading it would count nothing, and the only sound recovery is
  to rebuild the session from zero. No truncation has been observed, but append-only is not proven.
- Pruning: offsets for files no longer on disk are dropped on each render. They cannot affect the
  totals, which key off ids, but the table would otherwise grow forever.
- An atomic save, to a temp file and then `File.Move(..., overwrite: true)`, so a concurrent render
  never reads a torn file.

Cost: ~5 ms warm on a live session, 294 ms cold on an 87-file tree including the `cship` spawn and
two network calls.

## Verification

`scripts/` holds independent PowerShell implementations sharing no code with the binary. On a frozen
86-agent, depth-6 session:

| | main | sub | total |
|---|---|---|---|
| in | 365 | 1.159 | 1.524 |
| out | 201.311 | 42.238 | 243.549 |
| cache write | 2.819.831 | 1.891.700 | 4.711.531 |
| cache read | 35.705.626 | 13.287.574 | 48.993.200 |
| **tokens** | **38.727.133** | **15.222.671** | **53.949.804** |
| **tool calls** | **153** | **385** | **538** |

That figure has held across every rebuild since the accounting was written, because all the work
since has been presentation.

The [render tests](../development.md#the-render-tests) pin the same rules on every build. Their
showcase fixture is a main transcript and two sub-agents, one of them a nested workflow agent,
carrying both traps above (block-split records and verbatim copies of ancestor records), and the
scripts agree with the token rows it draws.

## Fragility

Ranked by risk, and all three fail the same way: **silently and downward**.

1. The `subagents/` layout is undocumented. If it moves, the sub-agent half reads zero rather than
   raising an error.
2. The record shape is undocumented as well: `message.id`, `message.usage.*`, the `tool_use` blocks.
3. That `cost.total_cost_usd` covers the whole tree was established by measurement, not by
   documentation. If it went back to covering the parent only, the money figures would quietly halve
   while the token rows stayed correct.

That is what the scripts are for. Run them after any Claude Code upgrade.

## Known undercount

Upstream issue [#84223](https://github.com/anthropics/claude-code/issues/84223): sub-agent
transcripts often never receive their final cumulative `usage` record, missing on roughly 20% of
requests and leaving only an early snapshot with `output_tokens: 1`. Any transcript-derived total is
therefore a **floor**, undercounting sub-agent output by up to about two thirds.

Related, [#76484](https://github.com/anthropics/claude-code/issues/76484): background sub-agent
launches never write usage into the parent transcript at all, which is exactly why the child files
have to be read directly.

A cheap self-check: price the counted tokens and compare against the tree-aware
`cost.total_cost_usd`. That ratio was running at about 93% when last measured.
