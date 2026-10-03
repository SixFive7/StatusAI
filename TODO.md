# TODO

- **Rename the private `CLAUDE.md` to `AGENTS.md`, after adding `AGENTS.md` to `.git/info/exclude`.**
  Claude Code reads `AGENTS.md` itself since 2.1.277, other agents read the same file, and the other
  repositories have moved to it. The order matters: `.git/info/exclude` hides only `CLAUDE.md`, so a
  renamed file would show as untracked in this public repository and could be committed by accident.
  Keep the `CLAUDE.md` line in the exclude file too, so a file that `/init` or `/memory` recreates
  stays private.
  - Added in 2.1.277: https://code.claude.com/docs/en/changelog#2-1-277
  - Extended in 2.1.281: https://code.claude.com/docs/en/changelog#2-1-281
  - Remaining differences: https://github.com/anthropics/claude-code/tree/main/mods/agents-md#where-it-still-differs-from-claudemd
- **Clean up old `.work` folders (`.work/mit-licence`): their copies and clones carry instruction files
  that Claude Code may load; delete what is no longer needed.**
  - Added in 2.1.277: https://code.claude.com/docs/en/changelog#2-1-277
  - Extended in 2.1.281: https://code.claude.com/docs/en/changelog#2-1-281
  - Remaining differences: https://github.com/anthropics/claude-code/tree/main/mods/agents-md#where-it-still-differs-from-claudemd
- **Question whether a Velopack update that ends every running `statusai.exe` before it swaps the
  binary would let the status line and the Stream Deck plugin share one installed copy.**
  Today one build is installed twice: the status line runs `~/.local/bin/statusai.exe`, and the
  plugin runs its own copy inside its plugin folder, because Windows does not overwrite a running exe
  and the plugin runs for as long as the Stream Deck app does. `Deploy.ps1` keeps the two in step and
  checks their hashes. An updater that ends the plugin's process (the app starts it again by itself)
  and waits out a status-line render could replace a single copy instead. Open: whether the plugin
  manifest's `CodePath` may point outside the plugin folder, and how an update hook would end a
  process that Velopack did not start.
  - Why there are two copies: [the Stream Deck key's design record](docs/design/stream-deck.md)
- **Draw the 5h and 7d rows from the `rate_limits` Claude Code now passes to the status line, and fetch
  only what it does not carry.**
  The payload has `rate_limits.five_hour` and `rate_limits.seven_day`, a used percentage and a reset
  time each. Drawing those rows from it would take the once-a-minute usage fetch away while a
  terminal is open. It does not carry the model-scoped weekly row or the server's severity, and the
  VS Code extension runs no status line, so the Stream Deck key still has to fetch for sessions
  there. Check what a real session's payload holds before choosing.
  - Noted under [known gaps](docs/reference/limits.md#known-gaps)
