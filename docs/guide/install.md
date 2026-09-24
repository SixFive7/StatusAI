# Install

StatusAI is a single-machine setup today, not a product: there is no installer yet, so this page
builds it from source and puts it in place by hand. The plan for a one-click install with
auto-update is [packaging-plan.md](../design/packaging-plan.md).

## Before you start

- **Windows 10 or 11, x64.** There is no ARM64 build.
- **Claude Code, signed in with a Claude account.** The limit rows and the account line read the
  sign-in Claude Code keeps in `%USERPROFILE%\.claude`. With an API key the token rows still work,
  and the account line reads `👤 not signed in`.
- **Numbers are in Dutch notation** — `1.234,56`, `$9,32` — and there is no setting for it yet.
- **The width is 141 columns** unless you set `CSHIP_WIDTH`, below. Claude Code runs the status line
  detached from the terminal, so it cannot ask the terminal how wide it is.
- **A terminal that draws emoji two columns wide**, as Windows Terminal does; the token grid is laid
  out on that assumption.

## Build

The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then:

```powershell
dotnet publish src/cship-usage.csproj -c Release -r win-x64 -o <dir>
```

`<dir>\cship-usage.exe` is the whole program: NativeAOT, self-contained, about 5 MB, with no .NET
runtime to install. Check it with the [render tests](../development.md#the-render-tests) before
putting it anywhere.

## Put it in place

1. **Download [cship](https://github.com/stephenleo/cship) 1.8.0** from its releases and put
   `cship.exe` **in the same directory as `cship-usage.exe`**, on your `PATH` —
   `%USERPROFILE%\.local\bin` for instance. cship-usage runs the cship beside it; if there is none,
   the status line prints the raw session JSON instead. Do not run cship's own installer or
   `cship uninstall`: both delete the `statusLine` setting that points at cship-usage.
2. **Copy [config/cship.toml](../../config/cship.toml)** to `%USERPROFILE%\.config\cship.toml`. It
   draws the model line and the context bar.
3. **Tell Claude Code** in `%USERPROFILE%\.claude\settings.json`:

   ```json
   "statusLine": { "type": "command", "command": "cship-usage", "refreshInterval": 60 }
   ```

   and, if your terminal is not 141 columns wide, its width:

   ```json
   "env": { "CSHIP_WIDTH": "120" }
   ```

4. **Restart Claude Code.**

## The prompt line (optional)

[starship](https://starship.rs) adds a prompt line above the model line: the directory, git, the
language versions, RAM, the GPU and the time. Install starship, copy
[config/starship.toml](../../config/starship.toml) to `%USERPROFILE%\.config\starship.toml`, and use
a [Nerd Font](https://www.nerdfonts.com) such as JetBrainsMono Nerd Font in the terminal. The GPU
figure comes from `nvidia-smi`, so it needs an NVIDIA GPU.

Without starship the prompt line is simply not drawn and everything else is unchanged. Without a
Nerd Font the few icons in cship's model line show as boxes; nothing StatusAI draws needs one.

## What to expect at first

- **A new session says nothing is wrong.** Until its first reply there are no token rows and no
  `⚠` row.
- **The limit rows read `→ early` for about ten minutes.** A trend needs four samples across ten
  minutes, and there is one a minute while a session is open.
- **Anything missing is named.** A red `⚠` row at the foot says which source failed; see
  [reading the status line](reading-the-status-line.md#when-a-row-says-).

## Uninstall

Remove `statusLine` from `settings.json`, delete `cship-usage.exe` and `cship.exe`, and delete what
the status line keeps between renders:

```powershell
Remove-Item -Recurse "HKCU:\Software\cshipUsage"
Remove-Item -Recurse "$env:USERPROFILE\.claude\statusline-tokens"
```

## Credits

StatusAI renders on top of [cship](https://github.com/stephenleo/cship) (Apache-2.0) and,
optionally, [starship](https://starship.rs) (ISC).
