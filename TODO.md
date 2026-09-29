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
