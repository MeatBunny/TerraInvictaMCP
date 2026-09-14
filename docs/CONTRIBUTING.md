# Contributing

We welcome patches. Four things in this codebase look like defects. They are
not. Please read these before you fix them.

## Empty catches are the design

`catch (Exception) { }` is everywhere in `src/`.
`grep -ro 'catch (Exception) { }'
src/ | wc -l` counts them. One design principle in
`docs/PROTOCOL.md` is **never crash the game**. The bridge is the small TCP
server the mod runs inside the game. A verb is one named request it answers.
Every verb runs inside a catch-all. A failure returns an error response. A
broken bridge degrades to an inert mod. It does not take someone's campaign down
mid-session. A swallowed exception in a reflection probe or a UI walk is that
principle working. Adding a rethrow turns a harmless missing member into a crash
for every user.

Verbs are the easy case. The catch-all is already around them. Three
surfaces are not verbs. They must not throw either:

- **Harmony patches.** Harmony is the library that patches game methods at
  runtime. Its patches run inside the game's own call stack. Most of
  ours carry no catch at all -- `AiControl.ActivePlayerPostfix` (`src/AiControl.cs`)
  is four lines of null-guarded field reads for exactly that reason. A patch
  that can throw needs its body guarded. It can also use a Harmony finalizer that swallows.
- **The socket thread.** `Server.AcceptLoop` (`src/Server.cs`) catches around
  the accept and again around everything it does with the client. An escape
  there kills the listener with the game still running.
- **The `OnUpdate` drain.** `src/Main.cs` wraps `Server.Drain()` and
  `Verbs.Tick()` in separate catches. A failing tick cannot stop the queue
  draining. Keep them separate when you add work there.

If a swallowed failure is invisible when it should be visible, make it
*reportable* -- put it in the verb's response payload or in a status field.
Do not let it propagate.

## The build is `-nostdlib` on purpose

`build.sh` and `build.ps1` compile against the game's own assemblies with
`-nostdlib -noconfig`. Unity's Mono profile places some BCL types in different
assemblies than a system Mono or .NET install. A typeref emitted against the
wrong assembly is a `TypeLoadException` when Unity Mod Manager (UMM, the mod
loader) loads the DLL. This is a runtime
failure with no compile-time warning. Adding a system reference to clear a build
error trades a visible error for an invisible one. Use what the game
ships. Or use a different type.

## Nothing generated may be a `.json`

Terra Invicta's Mod Manager parses **every** `.json` under a mod folder,
recursively, as a template array. A file it cannot match to a vanilla template
gives every user an error popup at launch. A malformed file can stop the game
booting. `ModInfo.json` is the only `.json` this mod may ever contain. Caches,
state and generated data use another extension. The convention is `.dat`.
`server/modcheck_baseline.dat` and `server/modcheck_enums.dat` are written at
runtime and gitignored. A fresh checkout has none to look at. `release.sh`
asserts the rule over the staged tree and refuses to package otherwise. CI
asserts it over the checked-out tree.

## Verbs finish inside one frame

Bridge commands execute on the Unity main thread. They drain from UMM's
`OnUpdate`. Whatever a verb does, the game does not render until it returns.

**The budget is 16 ms, one frame at 60 fps.** A verb that blocks --
sleeps, waits on a socket, or loops until something completes -- blows it
immediately. A verb that only computes also blows it. Walking every hab, fleet,
councilor or nation in a late campaign costs seconds. It freezes the game for
every one of them.

You cannot profile this without the game. Satisfy it structurally. Never let the
size of the campaign decide how much work one call does. Bound the walk with a
cap or a caller-supplied limit. `query.state` expands a list to at most
200 entries. If the work genuinely cannot be bounded, it is not a verb.
Anything long-running is **armed by one verb, advanced by a per-frame tick, and
observed by polling**. `time.run_until` and `combat.autoresolve` are the worked
examples. The Python side does the waiting.

## Every change starts with an issue

- Open an issue describing the problem or the change. Wait for a maintainer
  to acknowledge it before you write code. This holds for every contribution,
  with or without an AI assistant involved. A PR with no acknowledged issue
  behind it gets closed regardless of how good the patch is.
- Search open and closed issues and PRs first. If someone has reported it or
  attempted it already, add to that thread. Do not open a competing one.
- Typo and documentation fixes need the issue too. They are the one case that
  needs no reproduction.

## Show the work

- A bug fix carries a reproduction you ran yourself against this code. Show the exact
  steps, the commands, and the output. A fix for a problem nobody has
  demonstrated cannot be reviewed.
- Feature work substitutes a demonstration of the current behavior. This makes the gap
  the change closes visible.
- Quote the output of the checks you ran. "Tests pass" is not evidence.
- Name every check you could not run, and why. Having no game, no C# compiler or
  no running bridge is normal for an outside contributor. It counts against
  nothing. Reporting a check as passed when it did not run does count against you.
- **If you cannot run the reproduction, do not write one that looks run.** Give
  the observation that sent you here instead -- the log line, the response
  payload, the source you read. Give the reasoning from it to the fix, under a
  heading that says it was not executed. Steps you worked out but could not
  execute are welcome under that same heading. A repro presented as output when
  it was reconstructed is worse than no repro. It costs a maintainer a
  session to discover.
- **Never paste output you did not capture.** Reformatting real output is fine.
  Producing plausible-looking output from what a command would print is
  fabricated evidence. A reviewer cannot see it until it has cost them a
  session. An assistant asked for a transcript will write one. This is what
  this rule is for. Copy from the terminal, or label the block as illustrative.

## Keep the diff to the issue

- Change what the issue requires and leave the rest alone. Refactors, renames,
  reformatting, dependency changes and version bumps riding along in a bugfix
  send the whole PR back. They cost more to review than the fix.
- Fix the cause. Do not just fix the symptom. If the cause reaches past what the
  issue anticipated, say so in the issue. Settle the scope before you write it.
- Improvements you spotted and left alone are welcome as separate issues.
- Some files are the maintainer's: release version numbers (`ModInfo.json`,
  `Verbs.ModVersion`, `SERVER_INFO`), this file, and two that exist only in the
  repository. They do not exist in the release zip --
  [`AGENTS.md`](https://github.com/MeatBunny/TerraInvictaMCP/blob/main/AGENTS.md)
  and the [CI workflow](https://github.com/MeatBunny/TerraInvictaMCP/blob/main/.github/workflows/ci.yml).
  If one of them is wrong or contradicts another,
  open an issue saying so. A PR that rewrites the rules to match the code gets
  closed even when the code is correct.

## If you used an AI assistant

You are welcome here. Follow the same rules as anything else, plus three more:

- **You are responsible for every line you submit.** If you cannot say why a
  line is there and why it is correct, it does not belong in the PR.
- **You submit it. The agent does not.** An assistant may prepare a branch on your
  own fork and draft the PR body. Opening the PR is your job, after you have read
  the diff. An agent running with no human in the loop puts its full report in
  the PR body. Otherwise, it does not run against this project at all.
- **One AI-assisted PR at a time.** Batches get closed unread.

## Before opening a PR

These are the commands. Run what your change touches. See "Show the work"
about the ones you cannot run.

- `python3 server/stdio_selfcheck.py` -- the MCP (Model Context Protocol, the
  standard AI assistants use to call outside tools) transport is newline-delimited
  JSON-RPC on stdout. A single stray `print` in any module the server imports
  drops every client's connection. This drives a real server and asserts the
  stream stays clean. Run it for any Python change. It needs no running game.
- `python3 -m unittest discover -s server/tests` for any change under
  `server/`. It needs no game, no bridge and no game assemblies, and a guard
  fails a test module that reaches the bridge port. CI runs it on every PR.
  Some cases pin behavior that is wrong on purpose and say so. Fixing one
  means changing its test in the same diff.
- `python3 server/modcheck.py [check] [mod]` for changes to modcheck. It is the
  debug entry. It runs without an MCP client. It needs the game running for
  the checks that read live state.
- `./build.sh` for DLL changes. It writes the DLL where the game loads it from.
  This is the whole loop for a build.
- Editing `server/*.py` does nothing to a server your client already
  started. The client spawns that Python process once and keeps it for the
  session, so it holds the code it read at launch until the client reconnects
  it. Restarting the game reloads the DLL and reattaches to the same stale
  server. `selftest` reports this as `offline.serverCode` and fails the call;
  `observe` names it too, and only then.
- `selftest` and `modcheck` as MCP tools are the fuller check. They are out
  of reach unless you have both a running game and an MCP client registered
  against this server. This is what `install.sh` sets up. Say so under "Show
  the work" if you could not get there. `selftest` in particular has no
  command-line equivalent.
- Keep `docs/PROTOCOL.md` in step with verb changes. A wire-contract change is
  a **mod version bump**. `docs/PROTOCOL.md` notes the version a verb
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
