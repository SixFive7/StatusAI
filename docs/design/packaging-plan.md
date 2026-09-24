<sub>[StatusAI](../../README.md) / [Documentation](../README.md)</sub>

# Packaging & distribution plan

**Status:** a plan, written 2026-08-14; none of it is built yet. A few facts it relied on have
changed since, and are corrected where they appear. Until it is built, a release is a zip and a
pasted PowerShell block: see [install.md](../guide/install.md) and
[development.md](../development.md#releasing).

The goal is a `setup.exe` that a friend can run on a clean Windows machine to get Claude Code plus
this status line, with auto-update, no admin rights and no manual configuration.

It is feasible, and a weekend's work for the lite tier. The tools are known from earlier projects,
which shipped Windows installers with Velopack and with Inno Setup, so the time goes into writing
this one rather than into learning packaging.

## Two tiers

**Lite**, to ship first: `statusai.exe`, and `cship.exe` downloaded at install time (see
*Licensing*). No starship, no fonts, no terminal configuration.

The binary uses **no Nerd Font glyphs**, only emoji and `│ ● ○ ✗ ↻ → ⇢ · — … ⚠`. All 45 patched
codepoints in the prompt line come from starship's config; the model line that `cship.toml` draws
has two more, which show as boxes without the font. Dropping starship removes the font install, the
Windows Terminal profile edit, the VS Code setting, and the starship config collision. What gets
installed falls from ~32 MB to ~8 MB. Friends lose the powerline first line and keep everything
else.

**Full**, later: adds starship and JetBrainsMono Nerd Font. See *Starship* below.

## Auto-update: Velopack

Velopack, not the Inno Setup and GitHub Releases route an earlier project took. The usual objection
is that Velopack gives no install-time hooks, so config drops and JSON merging must move inside the
app. That costs nothing here, because the exe should install itself anyway. In exchange:

- A stable `%LocalAppData%\StatusAI\current\` that survives every update, so `statusLine.command`
  is a fixed absolute path and **PATH never enters the picture**.
- `Update.exe` as an external swap coordinator, a cleaner answer to replacing a binary that runs
  every 60 seconds than anything Inno offers.
- Delta packages and stable/beta channels for free.

`cship.exe` is not in the package, for the reasons under *Licensing*: the exe downloads it at
install time, pinned by URL and SHA-256, as the zip's install block does today. Where it then lives
is an open decision, because Velopack replaces `current\` on every update.

`VelopackApp.Build().Run()` must be the first statement that runs, which in `Program.cs` means the
first of its top-level statements.

### Update state lives in the registry

Not in a marker file: everything else that persists across renders already lives in
`HKCU\Software\StatusAI` (19 values today: 13 when this was written, then `bd`, `cr` and `ig`,
then `rows` in place of `val`, and `fail`, `why` and `tryTs`).
Four more values:

| value | holds |
|---|---|
| `updTs` | unix seconds of the last check, the freshness gate |
| `updEtag` | ETag from the release feed, so a no-op check is a 304 |
| `updVer` | newest version seen |
| `updFile` | path to a downloaded, verified package awaiting apply |

The code is the existing `EurPerUsd()` shape: a timestamp gate, a network call behind it, the result
cached. `RegStr` / `RegLong` / `Save` already exist, and `LockName()` already scopes a mutex per user
*and* elevation level, which is exactly what's needed when several Claude Code windows all render
every 60 seconds. Uninstall stays one line: delete the key.

### The status line is its own update host

A CLI exits in milliseconds, so there is nowhere to put the 10-minute timer a tray app would run.
But this CLI runs **every 60 seconds for as long as Claude Code is open**, which makes it a better
update host than a tray app.

- Gate on `updTs`, check once a day.
- Spawn a **detached** `--update-check` child to do the network work, so the render never blocks.
  Claude Code cancels in-flight status-line execution on the next trigger, so an inline download
  would be killed mid-flight.
- Check the signature of what it downloaded before applying it, with the certificate's thumbprint
  pinned.
- Apply on a later render by spawning the installer and exiting immediately. The exe runs ~100 ms
  per 60 s, so the file is unlocked 99,8% of the time.

### The exe installs itself

Give it `--install` / `--uninstall`. Velopack drops files; the exe downloads cship and does the
settings.json merge, config placement, and its own uninstall sweep.

This exists to avoid the nastiest landmine below. The merge keeps to the discipline of a config
editor from an earlier project: parse or refuse, splice the bytes rather than rewrite the file,
re-parse and cross-check before writing, replace atomically with `File.Replace` and a backup, and do
nothing when the file is already right. On .NET 10 the parsing can be `System.Text.Json`.

## Claude Code itself

Install it, don't bundle it. It is a self-contained binary of ~240 MB (2.1.281) that already
updates itself, and shipping a copy means shipping something stale and huge. Detect it, and run the
official installer if it is missing.

One manual step remains. The bar cannot render until the friend has signed in to Claude Code at
least once, because it reads their OAuth token and needs `~/.claude` to exist. So the honest promise
is "run setup, sign in, done", not one click.

## Releasing from CI

Today a release is built on the developer's machine by [Package.ps1](../../scripts/Package.ps1) and
published by hand, unsigned. With an installer it moves to a CI workflow that builds, signs and
publishes: the signing certificate comes from a secret and is checked for expiry before use,
`signtool` signs with a fallback timestamp server, and each release gets a Sigstore attestation.

## Starship

Starship is left out of the first installer, for three reasons in order of weight:

1. It is the sole source of the font problem (45 codepoints, Windows Terminal profile edits, VS Code
   settings, "why is my bar all boxes").
2. It is a config landmine: a friend with an existing setup either loses their prompt to our
   `starship.toml` or silently ignores ours, with no diagnostic either way. `cship` honours a
   pre-existing `STARSHIP_CONFIG`, so ours can be overridden invisibly.
3. It degrades gracefully: without it the line simply doesn't render.

If it is included later, bundle it inside the Velopack package. Then Velopack updates it like any
other file, versioned with our release and delta-updated, and we decide which version friends run.
That matters because a starship release changing module syntax would otherwise break `cship.toml` on
someone else's upgrade schedule. ISC permits the bundling.

Do **not** shell out to winget, scoop or choco instead: those need admin, update on their own
timetable, and hand us a version we never tested.

Two fixes make bundling safe:

- Ship starship *inside* the package so `cship` finds it in its own directory before PATH. A friend's
  own starship keeps running their shell prompt; our bar gets the version we tested.
- Set `STARSHIP_CONFIG` explicitly on the child process, so a friend's global variable can't silently
  redirect our bar to their config.

## Licensing

Every licence below would allow bundling. Where cship comes from was settled by SHA256, not
inference: the local binary is byte-identical to `stephenleo/cship` release v1.8.0.

| component | licence | note |
|---|---|---|
| `cship.exe` 1.8.0 | Apache-2.0 | unmodified upstream, unsigned, no NOTICE file, so the obligations come down to shipping LICENSE and an attribution; not bundled all the same, see below |
| `starship.exe` | ISC | portable zip installs per-user, no admin |
| JetBrainsMono Nerd Font 3.4.0 | OFL-1.1 | bundleable unmodified; per-user font install needs no admin |
| `statusai.exe` | MIT | ours; the .NET runtime compiled into it is MIT as well, and [THIRD-PARTY-NOTICES.txt](../../THIRD-PARTY-NOTICES.txt) carries its notices |
| `cship.toml`, `starship.toml` | Apache-2.0, ISC | based on cship's and starship's own, and kept under their licences |

On disk, everything together is ~31,6 MB and the lite tier ~8 MB.

cship stays out of the package to escape transitive notice paperwork: it statically links ~43
crates, `ring` among them, whose licence requires its notice. The lite package holds only our own
binary and the configs, and the installer **downloads cship at install time from a pinned URL with
SHA256 verification**. The zip release already works that way: the block in
[install.md](../guide/install.md#download) fetches cship 1.8.0 from its own release and checks its
SHA-256.

## Landmines

Ranked by damage.

**1. The PS 5.1 UTF-8 mojibake.** Upstream cship's `install.ps1` does
`Get-Content -Raw | ConvertFrom-Json` on settings.json. Under Windows PowerShell 5.1 that reads
UTF-8-no-BOM as the ANSI codepage, so `"café ☕"` comes back as `"cafÃ© â˜•"`. That is silent,
irreversible corruption of a friend's config, in the exact code path everyone copies, and it is
**why the exe does the merge, not PowerShell**. Neither shell survives comments, duplicate keys or a
BOM either.

**2. Upstream cship will rip the status line out.** `cship uninstall` deletes the whole `statusLine`
key, and upstream's `install.ps1` calls `cship uninstall` before any upgrade. If a friend ever runs
the official cship installer, StatusAI silently vanishes.

**3. `cship` must sit next to `statusai`.** Windows searches the calling exe's directory before
PATH. If it isn't found, `catch { return input; }` **echoes the entire raw session JSON onto the
status line**. The zip's install block puts the two side by side. With cship downloaded rather than
packaged, the installer has to put it where the exe finds it, and keep it there across updates; see
*Where cship lives* under the open decisions.

**4. A setting hardcoded for one machine.** `Nl()` forces nl-NL formatting unconditionally. It must
become a setting the installer writes, or a friend gets Dutch decimals. When this was written the
terminal's width was a second one: `TermWidth()` returned 141 unless `STATUSAI_WIDTH` said
otherwise. It now reads `COLUMNS`, which Claude Code sets to its terminal's width when it runs the
status line, so the width needs no installer; see [layout.md](../reference/layout.md#width-budget).

**5. Unsigned and x64-only.** No ARM64 build of ours; neither `statusai.exe` nor cship is signed,
and the installer would not be either. SmartScreen will object to the installer, and where Windows
11's Smart App Control is on it blocks both programs outright, as
[install.md](../guide/install.md#before-you-start) says. `vpk pack` signs as it packs, through
`--signParams`, once there is a certificate; see *Signing* under the open decisions.

**6. Plan-dependent degradation.** API-key-only friends lose the three limit rows and get
`👤 not signed in`; the token grid still works, since it reads transcripts. Enterprise is untested:
`Fetch()` once hard-required `kind == "session"` and `weekly_all`; since 2026-09-24 any non-empty
`limits[]` counts, what it sends of those two and one model-scoped meter is drawn, and anything else
(monthly credits, say) is named in an amber notice instead. A usage fetch that fails leaves the
rows at their last cached value, or absent, and from the second failure in a row a `⚠` row says
*why* and how old the rows are, as for every other missing source.

## Install surface

1. Preflight: x64, Windows 10+, `~/.claude` exists (so Claude Code has been signed in to once), and
   the Microsoft Visual C++ Redistributable that cship needs to start.
2. Back up `~/.claude/settings.json`.
3. Velopack installs to `%LocalAppData%\StatusAI\current\`.
4. `--install`: download cship 1.8.0 and check its SHA-256, merge the `statusLine` block with an
   **absolute** command path, write `cship.toml` if absent.
5. Restart Claude Code.

Uninstall must sweep: `HKCU\Software\StatusAI`, `%LocalAppData%\StatusAI\tokens\*.bin` (one per
session, never garbage-collected), the `statusLine` key, the `cship` folder cship keeps beside each
session's transcript, and legacy `~/.claude/statusline-usage.json` / `statusline-cache.json`.

## Effort

| piece | what it takes | effort |
|---|---|---|
| Velopack packaging | `vpk pack`, the channels and the feed | half a day |
| `--install` / `--uninstall` | the merge discipline above, on `System.Text.Json`, and the cship download | half a day |
| Updater | registry-gated check, detached child, signature check, Velopack apply | half a day |
| CI | the release workflow above | half a day |
| Portability fixes | locale as a setting | a couple of hours |

A weekend for lite, and a day or two more for starship and fonts. The estimates count on the tools
being familiar from those earlier projects, not on their code.

## Open decisions

- **Rename: decided** on 2026-09-24, before the first release. Everything that belongs to StatusAI
  took its name: the binary is `statusai.exe` and the `statusLine` command `statusai`, the
  registry key `Software\StatusAI`, the token cache `%LocalAppData%\StatusAI\tokens`, the usage
  lock `Global\StatusAI.fetch.*`, and the two variables `STATUSAI_WIDTH` and `STATUSAI_OFFLINE`.
  Before a release it needs no migration, since a new user has no old state, so the program carries
  no code to move any; the developer's machine gets its old key and cache copied across once, by
  hand.
- **Where cship lives:** it is downloaded at install time, and Velopack replaces `current\` on
  every update, so a cship downloaded beside `statusai.exe` is gone after the first update.
  Either the exe downloads it again after each update, or cship lives outside `current\` and the
  exe runs it by its full path.
- **Feed host:** object storage such as Backblaze B2, which Velopack targets natively, or GitHub
  Releases, which is simpler: the repo and its releases are already there.
- **Signing:** self-signed with a client-side thumbprint pin (free, and how an earlier project does
  it) or a real OV certificate (~$150/yr) to satisfy SmartScreen properly. Smart App Control lets an
  unknown program run only when it is signed with a certificate from a CA in Microsoft's
  [Trusted Root Program](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control),
  so the self-signed route leaves it blocked there.
- **ARM64:** cship and starship both publish it; we don't.
