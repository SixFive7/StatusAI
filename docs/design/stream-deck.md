<sub>[StatusAI](../../README.md) / [Documentation](../README.md)</sub>

# The Stream Deck key: how it was designed

The record of the design of 1 October 2026: what was asked for, what the Stream Deck app can and
cannot do, the four ways of building the key with what each was measured to cost, and how the key
decides when to fetch. And of the day after, when it had been on the device for a night: how a
press gets its terminal to the front, what a press left in the plugin, and what a locked session
did to the key. What the key does now is in [stream-deck.md](../guide/stream-deck.md) and
[architecture.md](../reference/architecture.md#the-stream-deck-key). Like the other pages in this
folder it is a record: add to it, and leave what was decided as it stands.

## What was asked for

One key on a Stream Deck XL that opens a Claude Code terminal when pressed and always shows the 5h
and 7d rows, at as little processor time, memory and use of Anthropic's API as it can be done
with. Then, in the order they were decided:

- make it part of StatusAI and not another project, if that can be done cleanly;
- no updates while Claude is not being used, and updates as fast as the status line's while it is;
- a short press for a new tab in the Windows Terminal window used last, a long press for a new
  window;
- no code to bring the terminal to the front: see what Windows does first.

A day later, with the key pressed for the first time: the terminal does have to come to the
front. That part is [further down](#the-terminal-in-front).

## What the app can do

Measured or read on Stream Deck 7.6.0.23012, Windows.

**The app cannot run a program and draw what it prints.** Its 34 built-in actions include *Open*,
which starts a program and reads nothing back, and none that runs on a timer. A key's picture
changes in one way only: a plugin sends `setImage` over the websocket the app opened for it. The
app's binary has three plugin runtimes, a native process, Node and a web page, all started by the
app and kept. A connection it did not start is turned away (`Attempt to register unexpected
connection`), and a plugin that draws and exits is not a timer: it is restarted after a delay
(`Schedule to be restarted in {} seconds`) and then given up on (`Plugin is unstable an was
disabled.`). So "let the Stream Deck call `statusai` once a minute" has no form in which the app
does the calling. What it can do is start `statusai.exe` once and keep it, which is what was built.

**A native exe works as a plugin**, with a manifest that is plain JSON and `SDKVersion` 2: another
plugin on the same machine is one. The app starts it with `-port`, `-pluginUUID`,
`-registerEvent` and `-info`.

**The app answered this exe's handshake.** Before anything was installed, the opening request of
the client in `Deck.cs` was sent to the running app's port by hand, with no registration after it:
`HTTP/1.1 101 Switching Protocols`, with the right `Sec-WebSocket-Accept`.

**A plugin's files can be replaced while it runs, by moving them.** On a running copy of the exe,
a copy over it and a delete were both refused, and moving it aside and putting a new file under
its name both worked. `streamdeck://plugins/restart/<uuid>` and `.../stop/<uuid>` are in the app's
binary and are what Elgato's own command-line tool uses to restart and stop a plugin.

**A `.streamDeckPlugin` is a zip** with the `<uuid>.sdPlugin` folder at its root; the two the app
ships with are.

## Four ways of building it

| | 1. a plugin mode in `statusai.exe` | 2. a second project sharing the source | 3. a thin host exe that runs `statusai` | 4. a Node host that runs `statusai` |
|---|---|---|---|---|
| the status line's binary | 58.880 bytes larger in the prototype, 75.776 as built | unchanged, but some 700 of `Program.cs`'s 1.635 lines move into shared files | a few lines for the flags | as 3 |
| a render | unchanged: 167 of 167 renders identical, 67,9 ms against 68,1 ms at the median of 40 | to be shown again after the move | unchanged | unchanged |
| the process that stays up | 13,0 to 13,2 MB in use, 3,84 to 3,93 MB private | 18,2 to 18,4 MB and 5,04 to 5,24 MB, with the fetch inside it | 9,7 MB and 3,21 MB | 41 to 45 MB and 13 to 28 MB before it does anything |
| another binary | none: a copy of the same file | about 5 MB, not built | 1.992.192 bytes | a `.js` file, and the app's own Node |
| a locked exe | the plugin runs a copy in its own folder | as 1, for its own exe | the host is locked and seldom changes | nothing of ours is locked |
| two versions | two copies of one build, kept in step by `Deploy.ps1` | two binaries that have to agree on the cache | none: the host knows nothing of the cache | as 3 |
| projects in the repository | one | two | two | one, and JavaScript |

**Decided: 1.** It was the first preference, and the measurements gave no reason against it: the
status line's path did not move, and the process that stays up is within 3,5 MB of the smallest
thing that could do the job. The second binary of 2 and 3, and the second language and thirty
megabytes of 4, would have bought nothing the key needs.

Three choices inside it were made on measurements too.

- **The fetch runs in a child, `statusai --refresh`.** With the fetch in the process that stays
  up, the HTTP client stays loaded for good: 18,2 to 18,4 MB and ten threads, against 13,0 to
  13,2 MB and three. A fetch can also take its three seconds, and a key press should not wait for
  it. The child goes through `GetUsage()` like a render, which is what keeps the key and the
  terminals from ever fetching twice. One refresh took 117 ms and 39 ms of processor time in the
  prototype, against a stand-in endpoint.
- **The websocket client is written by hand.** The framework's `ClientWebSocket` made the exe
  282.112 bytes larger where the whole prototype with a client of its own added 58.880, and it
  brought the thread pool with it. The price is a hundred lines of protocol that have to be right,
  which is what [Test-Deck.ps1](../development.md#the-stream-deck-tests) is for.
- **The console host is let go.** A console program started by the app gets a `conhost.exe` of its
  own, 7,5 MB in use and 1,2 MB private. `FreeConsole()` as the mode starts, and there is none.

The version in the line the plugin logs as it starts is the assembly's, with the time the file
was written. The product version with its commit would have been the better name for a build, and
reading it costs 12 kB in the exe, by either of the two ways tried.

## When to fetch

The requirement: nothing extra while Claude is not being used, and the status line's own pace
while it is. A terminal session answers it by itself, since its status line fetches once a minute.
What was missing is every other kind of session: on the day of the design the shared copy went 40
minutes without a fetch while seven sessions in the VS Code extension were open and the 5h row
went from 10% to 16%. So the key needs to know that Claude is working. The signals that were
looked at:

| signal | what was found | |
|---|---|---|
| a write under `~/.claude/projects` | Every session writes its transcripts there, whatever started it: all seven VS Code sessions had theirs, a sub-agent's was written two seconds before it was looked at while its parent's had stood still for nine minutes, and sessions idle for five hours had written nothing. 374 events in 20 minutes of work, 0 to 37 a minute. One change notification on the folder covers all of it and stays signalled until it is armed again: 65.900 appends in 30 seconds were two wake-ups and no processor time that could be measured. | **used** |
| `claude.exe` running | The app can report it for nothing. But seven sessions sat open and idle for hours on the day, and this would have fetched 60 times an hour for all of them. | rejected |
| a hook that runs `statusai` | Reaches every kind of session. Needs an entry in `settings.json`, starts a process for every tool call, and holds each one up while it runs. | rejected |
| the files in `~/.claude/sessions` | They say `busy` or `idle`, and are written only when that changes: a session stayed `busy` for a quarter of an hour without a write while its sub-agents worked. It would have to be polled. | rejected |
| backing off while the figures stand still | The one thing that would see usage from another device. It fetches while nothing is happening here, which is what was asked not to be done. | rejected |

**Decided:** refresh when a write has been seen and the last fetch or attempt by anyone is 62
seconds old; at once if it is older. 62 and not 60, so that a terminal's status line, once it has
fetched, stays two seconds ahead of the key, which then adds nothing while that terminal is open.
The two go through the same `GetUsage()`, so they share one fetch a minute whichever of them is
ahead. Arming the notification again reports the writes made since it was signalled, which gives
the last write of a burst one more refresh a minute later. That one was kept on purpose: it is
the fetch that sees what the last reply cost. In the prototype, a burst of writes gave three
refreshes, 62,0 seconds apart, and then silence.

Three things follow from never fetching without a write, and were accepted:

- Usage from another device, or from a chat in the browser, is not seen until Claude next writes
  something here, a terminal opens, or the key is pressed.
- When a limit's window ends while nothing is happening, the key works it out for itself: that
  row reads 0%, grey, with no reset time, until the next fetch says what the new window holds.
- An access token that has expired with no session open is never noticed, because nothing is
  fetched with it.

## The key itself

The face is the third of three that were drawn first: the label and the percentage, the ten cells,
and one small line with the time to the reset on the left and the time to 100% on the right. At
96 by 96, the key of a Stream Deck XL, that line is 9 px high, which is as small as it could go;
the first face had the reset alone on it, at 11 px, and the second left the line out. It is drawn
as SVG on a canvas of 144 by 144, 2,5 kB, which the app scales.

A press is told from a hold by the half second: a key released before it is a short press and acts
on the release, and one still down after it is a long press and acts there and then. Against the
stand-in for the app, a key held for 120 ms acted 125 ms after it went down, and one held for
900 ms acted at 500 ms.

## On the device

Installed on 1 October 2026, on a Stream Deck XL with the app at 7.6.0.23012, with the status
line's own binary left as it was. Claude was working in the VS Code extension throughout, and no
terminal was open.

- The app started the plugin's copy and logged `Plugin connected`. The process had no
  `conhost.exe` and three threads, with a fourth or a fifth for a minute now and then.
- Over twenty minutes it held 13,2 to 13,5 MB in use, 3,8 to 4,0 MB of it private, and 203
  handles. In 33 minutes it used 0,22 s of processor time.
- The shared copy, which had been 80 minutes old, was fetched five seconds after the plugin
  started and once every 62 seconds from then on: 35 attempts in the first 35 minutes. One of
  them failed and the next one succeeded. A single failure is quiet, so the key showed nothing,
  and what it failed on is not known: the next good fetch clears the reason.
- Nine refreshes were watched as they ran. Each was `statusai.exe --refresh`, started by the
  plugin's process 61,6 to 62,7 s after the one before, and each ended with exit code 0: 0,31 to
  0,59 s long, with 0,03 to 0,09 s of processor time. The seven that were measured for memory
  had 19,3 MB in use and 5,2 MB committed at their peak. Against the prototype's stand-in for
  the endpoint a refresh had been 0,12 s long, with 0,04 s and 16,5 MB.
- With `--refresh` run by hand 60 seconds after each fetch, five times, which is how a status
  line that is ahead fetches, the plugin started no refresh of its own: none in those five
  minutes, and 0,02 s of processor time in the eight around them. Its next one came 62 seconds
  after the last of the five.
- The 5h window ended at 20:40 while it ran. Drawn from the shared copy with `--deck-face`, the
  key read `22%` and `↻ 0m` half a minute before; `0%` in grey with `↻ —` a second after, with
  nothing fetched yet; `0%` and `→early` once the fetch had come, three seconds after the end;
  and `↻ 4h58m` a minute later, when the next window had opened.
- Before anyone had looked at the device, the thirteen recorded faces and the live one were
  drawn with QtSvg 6.11.1, the version in the app's folder (`QSvgRenderer`, from PySide6). All
  of them came out as the figures show them, `↻`, `→`, `⚠` and `—` included.

What the key looks like on the device itself, what a press does there, and whether it brings
Windows Terminal to the front, had not been looked at when this was written.

## The terminal, in front

2 October 2026. The key was pressed on the device for the first time, and the terminal it opened
stayed behind the window in front. That was Windows' answer to "see what Windows does first", and
what was asked for next was plain: both presses have to end with the terminal in front.

**Why it stayed behind.** Windows keeps the foreground for the program the user is working in.
A program may put a window in front if it is the one in front itself, was started by it, or was
the last to be given input; any other gets a flashing taskbar button in place of it. The plugin
is none of those: the app started it, in the background, hours earlier, and the deck's keys are
not input as Windows counts it. The terminal a press starts inherits that standing.

**What gets past it**, and what was looked at:

| way | what it does | |
|---|---|---|
| provide input, then ask | Windows lets through the program that provided the last input. An input event that moves nothing and presses nothing counts, and is seen by no program | **used**, first |
| attach to the thread in front | sharing its input queue makes the caller part of the program in front for the moment | **used**, second |
| a press of Alt, then ask | Windows lifts the guard for everyone when Alt goes down. The program in front sees the key, and a lone Alt opens its menu, so it is sent twice, the second to close what the first opened | **used**, last and twice at most |
| allow it beforehand | `AllowSetForegroundWindow` for everyone, before the terminal is started, so that it can come up by itself. It lasts until the next input from the user, which may come first | used for a new window, and not relied on |
| minimise and restore | restoring a window activates it, at the price of a window that visibly drops and comes back | rejected |

**Which window.** For a new window there is no question: the one that was not there before the
press. For a tab there is, when more than one terminal window is open, because `wt.exe -w 0`
leaves the choice to Windows Terminal, which takes the one used last, and nothing outside it
can ask which that will be. So the choice is made before the tab is asked for: the terminal
window on top of the others on this desktop, a minimised one only when there is no other, is
brought to the front first, which makes it the one used last. The tab then opens in the window
that is already in front, and nothing has to be found afterwards.

**Where it runs.** In the process the press is given anyway (see below). It can take up to eight
seconds, when Windows Terminal has to start first, and the plugin's loop does not wait for it.

## What a press left in the plugin

A day after its first press the plugin's process held 266 handles where it had held 203, and
16,6 MB of memory in use where it had held 13,3. It was not a leak. The count stood at 266
through 70 refreshes, as it had stood at 203 through the first 50, and a list of the 266 by kind
had no handle to a process in it. It had registry keys under `AppModel\StateRepository`, COM's
catalogue and its port, the shell's caches, a window station and a desktop, and 44 libraries
where a plugin that has just started has 27, `apisethost.appexecutionalias.dll` and
`daxexec.dll` among the 17: what Windows loads into a process that starts a Store app by its
alias, which is what `wt.exe` is.

A program that does nothing else showed the same. Before its first start of an alias
(`winget.exe --version`, which opens no window) it held 195 handles and 13,3 MB; after it 258
and 17,7 MB, with 15 libraries more; after the second and the third start, 258 still. An
ordinary program started the same way (`cmd.exe /c exit`) cost it 2 handles. Its 63 handles are
the plugin's 63.

**Decided:** a press runs in a process of its own, `statusai --deck-press`, for the reason the
fetch runs in `--refresh`: what it loads goes when it does, and the process that stays up
stays as small as it was.

The same list showed twelve handles the plugin had not opened but been given. The app starts
its plugins with its own inheritable handles open to them, and its two log files and its crash
reporter's lock were among those. .NET starts a program with such handles handed on, so each
`--refresh` held them for its third of a second, and Windows Terminal, when a press was what
started it, for as long as it stayed open. **Decided:** the plugin marks every handle it has as
its own before it starts anything. In the test for it, a plugin started with a file open to it
the way the app's log is held 22 inheritable handles before the change and none after.

## A night with the session locked

The session was locked from 01:03 to 21:00 on 2 October. The app lets go of the deck for as long
as that lasts (`Session -> suspend` in its log, and the device `disconnected`) and takes it up
again afterwards. Another plugin on the same deck had lost its keys by then: it went on being
sent its data and drew nothing. So the question was whether this one had.

It had not. What can be said from what was left behind:

- The key did not show while the deck was away. The plugin's count of I/O operations over its 27
  hours, 4.818, is what its refreshes alone come to at the 35 a refresh that were measured; a
  redraw a minute through those twenty hours would have added 1.200. So the app sent
  `willDisappear` for the key when it let go of the deck.
- It showed again afterwards. The first fetch after the lock came at 21:15:04, when Claude was
  next put to work, and one every 62 seconds from then on, which only happens while a key shows.
  The key was pressed that evening and opened its terminal.
- The refresh rule itself was not put to the test: no session wrote a transcript while the
  session was locked.

The plugin cannot lose its keys the way the other one did. It keeps no list of decks: a key
shows from its `willAppear` to its `willDisappear`, whatever the app says about decks before,
between or after. What was added is the other half. When the app reports a deck as connected, or
the system as awake, the keys that show are drawn again whether or not their picture changed, in
case the deck came back without it.
[Test-Deck.ps1](../development.md#the-stream-deck-tests) plays a deck going and coming back in
both orders.
