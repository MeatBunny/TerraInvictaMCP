# Contributing

We welcome patches. The first four sections of this file describe code that
looks wrong and is deliberate. Read them before you change that code.

## Empty catches are the design

`catch (Exception) { }` is everywhere in `src/`.
`grep -ro 'catch (Exception) { }'
src/ | wc -l` counts them. One design principle in
`docs/PROTOCOL.md` is "never crash the game". The bridge is the small TCP
server the mod runs inside the game. A verb is one named request it answers.
Every verb runs inside a catch-all. A failure returns an error response. A
broken bridge degrades to an inert mod. It does not take someone's campaign down
mid-session. A swallowed exception in a reflection probe or a UI walk is that
principle at work. If you add a rethrow, a harmless missing member becomes a
crash for every user.

Verbs are the easy case. The catch-all is already around them. Three
surfaces are not verbs. They must not throw either:

- **Harmony patches.** Harmony is the library that patches game methods at
  runtime. Its patches run inside the game's own call stack. Most of our
  patches have no catch, so each one is written so that it cannot throw.
  For example, `AiControl.ActivePlayerPostfix` (`src/AiControl.cs`) is four
  lines of null-guarded field reads. If a patch can throw, put a guard
  around its body, or use a Harmony finalizer that swallows the exception.
- **The socket thread.** `Server.AcceptLoop` (`src/Server.cs`) catches around
  the accept and again around everything it does with the client. If an
  exception escapes there, the listener stops and the game continues to run.
- **The `OnUpdate` drain.** `src/Main.cs` wraps `Server.Drain()` and
  `Verbs.Tick()` in separate catches. If a tick fails, the queue continues to
  drain. When you add work there, keep the catches separate.

If a swallowed failure must be visible, make it reportable. Put it in the
verb's response payload or in a status field. Do not let it propagate.

## The build is `-nostdlib` on purpose

`build.sh` and `build.ps1` compile against the game's own assemblies with
`-nostdlib -noconfig`. Unity's Mono profile places some BCL types in different
assemblies than a system Mono or .NET install. When Unity Mod Manager (UMM,
the mod loader) loads the DLL, a typeref emitted against the wrong assembly
causes a `TypeLoadException`. This is a runtime failure with no compile-time
warning. If you add a system reference to clear a build error, you replace a
visible error with an invisible one. Use what the game ships. Or use a
different type.

## Nothing generated may be a `.json`

Terra Invicta's Mod Manager parses every `.json` under a mod folder,
recursively, as a template array. A file it cannot match to a vanilla template
gives every user an error popup at launch. A malformed file can stop the game
from booting. `ModInfo.json` must be the only `.json` in this mod. Caches,
state and generated data use another extension. The convention is `.dat`.
modcheck writes `server/modcheck_baseline.dat` and `server/modcheck_enums.dat`
at runtime, and git ignores them. A fresh checkout has none to look at.
`release.sh` asserts the rule over the staged tree and refuses to package
otherwise. CI asserts it over the checked-out tree.

## Verbs finish inside one frame

Bridge commands execute on the Unity main thread. They drain from UMM's
`OnUpdate`. Whatever a verb does, the game does not render until it returns.

**The budget is 16 ms, one frame at 60 fps.** A verb that blocks goes over the
budget immediately. A sleep, a wait on a socket, and a loop until something
completes all block. A verb that only computes also goes over it. A walk over
every hab, fleet, councilor or nation in a late campaign takes seconds. The
game freezes for all of those seconds.

You cannot profile this without the game. Meet the budget through the
structure of the code. Never let the size of the campaign decide how much work
one call does. Bound the walk with a cap or a caller-supplied limit.
`query.state` expands a list to at most 200 entries. If you cannot bound the
work, it is not a verb. For long-running work, one verb arms it, a per-frame
tick advances it, and the caller polls to observe it. `time.run_until` and
`combat.autoresolve` are the worked examples. The Python side does the
waiting.

## Every change starts with an issue

- Open an issue that describes the problem or the change. Wait for a
  maintainer to acknowledge it before you write code. This rule applies to
  every contribution, with or without an AI assistant. A maintainer closes a
  PR with no acknowledged issue behind it, however good the patch is.
- Search open and closed issues and PRs first. If someone already reported or
  attempted it, add to that thread. Do not open a competing one.
- Typo and documentation fixes need the issue too. They are the one case that
  needs no reproduction.

## Show the work

- A bug fix includes a reproduction that you ran yourself against this code.
  Show the exact steps, the commands, and the output. Nobody can review a fix
  for a problem that nobody demonstrated.
- For feature work, give a demonstration of the current behavior instead. It
  shows the gap that the change closes.
- Quote the output of the checks you ran. "Tests pass" is not evidence.
- Name every check that you cannot run, and give the reason. It is normal for
  an outside contributor to have no game, no C# compiler or no running bridge.
  It counts against nothing. If you report a check as passed when it did not
  run, that counts against you.
- **If you cannot run the reproduction, do not write one that looks as if you
  ran it.** Instead, give the observation that sent you here: the log line, the
  response payload, or the source you read. Give the reasoning from it to the
  fix, under a heading that says it was not executed. Steps that you worked out
  but cannot execute also go under that same heading. A reconstructed repro that
  you present as output is worse than no repro. It costs a maintainer a session
  to discover it.
- **Never paste output that you did not capture.** Reformatted real output is
  acceptable. Output that you write from what you expect a command to print is
  fabricated evidence. A reviewer cannot see the fabrication until it costs them
  a session. If you ask an assistant for a transcript, it will write one. This
  rule exists for that case. Copy from the terminal, or label the block as
  illustrative.

## Keep the diff to the issue

- Change what the issue requires and leave the rest alone. If a bugfix also
  carries refactors, renames, reformatting, dependency changes or version
  bumps, the whole PR goes back. Those changes cost more to review than the
  fix.
- Fix the cause. Do not fix only the symptom. If the cause reaches past what
  the issue anticipated, say so in the issue. Settle the scope before you
  write the fix.
- If you find an improvement and leave it alone, report it as a separate
  issue.
- The maintainer owns these: the release version numbers (in
  `ModInfo.json`, `Verbs.ModVersion` and `SERVER_INFO`), this file, and two
  files that exist only in the repository and not in the release zip. The
  two files are
  [`AGENTS.md`](https://github.com/MeatBunny/TerraInvictaMCP/blob/main/AGENTS.md)
  and the [CI workflow](https://github.com/MeatBunny/TerraInvictaMCP/blob/main/.github/workflows/ci.yml)
  file. If one of these is wrong or contradicts another, open an issue about
  it. If a PR rewrites these rules to match the code, the maintainer closes
  it, even when the code is correct.

## If you used an AI assistant

You are welcome here. Follow the same rules as any other contributor, plus
three more:

- **You are responsible for every line you submit.** If you cannot say why a
  line is there and why it is correct, remove it from the PR.
- **You submit it. The agent does not.** The assistant's part is to prepare a
  branch on your own fork and draft the PR body. You open the PR, after you read
  the diff. If an agent runs with no human in the loop, it must put its full
  report in the PR body. An agent that does not do this must not run against
  this project at all.
- **One AI-assisted PR at a time.** The maintainer closes a batch without
  reading it.

## Before opening a PR

These are the commands. Run the ones that apply to your change. See "Show the
work" about the ones you cannot run.

- `python3 server/stdio_selfcheck.py` for any Python change. The MCP (Model
  Context Protocol, the standard AI assistants use to call outside tools)
  transport is newline-delimited JSON-RPC on stdout. A single stray `print`
  in any module the server imports drops every client's connection. This
  script runs a real server and checks that the stream stays clean. It
  needs no running game.
- `python3 -m unittest discover -s server/tests` for any change under
  `server/`. It needs no game, no bridge and no game assemblies. A guard fails
  a test module that reaches the bridge port. CI runs it on every PR.
  Some cases pin behavior that is wrong on purpose, and the case says so. If
  you fix one, change its test in the same diff.
- `python3 server/modcheck.py [check] [mod]` for changes to modcheck. It is the
  debug entry. It runs without an MCP client. The checks that read live state
  need a running game.
- `./build.sh` for DLL changes. It writes the DLL where the game loads it from.
  This is the whole loop for a build.
- An edit to `server/*.py` does not change a server that your client already
  started. The client spawns that Python process once and keeps it for the
  session. The process holds the code it read at launch until the client
  reconnects it. A game restart reloads the DLL and reattaches to the same
  stale server. `selftest` reports this as `offline.serverCode` and fails the
  call. `observe` names it too, and only then.
- As MCP tools, `selftest` and `modcheck` are the fuller check. They need a
  running game and an MCP client registered against this server. This is
  what `install.sh` sets up. If you cannot get there, say so under "Show the
  work". `selftest` has no command-line equivalent.
- Keep `docs/PROTOCOL.md` in step with verb changes. A wire-contract change is
  a mod version bump. `docs/PROTOCOL.md` notes the version a verb
  appeared in ("since 0.1.1" on its row). The version lives in three files that
  move together: `ModInfo.json`, `Verbs.ModVersion` in `src/Verbs.cs`, and
  `SERVER_INFO` in `server/__main__.py`. `selftest` reports drift in
  `offline.versionMatch`. CI's version agreement step fails on it.

The [CI
workflow](https://github.com/MeatBunny/TerraInvictaMCP/blob/main/.github/workflows/ci.yml)
runs the offline checks on your PR and is the authority on which ones.

## Security reports

Do not open a public issue. Use GitHub's private vulnerability reporting on this
repository. What a report needs, and the two properties that are known and
accepted, are in
[`SECURITY.md`](https://github.com/MeatBunny/TerraInvictaMCP/blob/main/SECURITY.md).
