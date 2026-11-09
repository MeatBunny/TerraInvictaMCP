# Working in this repository

`docs/CONTRIBUTING.md` controls contributions. Read it before you write anything
that you want to submit.

## Layout

- `src/` is the in-game C# 6 DLL that runs the bridge. `server/` is the MCP
  (Model Context Protocol, the standard that AI assistants use to call outside
  tools) stdio server. It uses only the Python 3 standard library.
- `docs/PROTOCOL.md` is the wire contract between them. It defines the
  verb names and payloads.

## Constraints, and where each is written down

- Build `-nostdlib` against the game's own assemblies. The header comment of
  `build.sh` and `build.ps1` ("-nostdlib against the game's own assemblies is
  deliberate") explains why. The compiler argument lists (`ARGS=` in
  `build.sh`, `$CArgs` in `build.ps1`) show the flags. There is no automated
  check. A typeref against the wrong assembly compiles cleanly and throws an
  error at load.
- `ModInfo.json` must be the only `.json` in this mod. Read the comment in
  `release.sh` beginning "The game's Mod Manager parses EVERY .json". The
  `STRAY=` assertion under that comment enforces this rule. The "no stray
  .json" step in `.github/workflows/ci.yml` also enforces it.
- Verbs finish inside one frame. Read "Execution model" in `docs/PROTOCOL.md`.
  `docs/CONTRIBUTING.md` works through this rule under "Verbs finish inside one
  frame". No automated check exists. Code review is the only check.
- Stdout carries JSON-RPC and nothing else. Read the docstring of
  `server/stdio_selfcheck.py`. That script enforces the rule.
  `.github/workflows/ci.yml` runs it on every PR.
- Never crash the game. Read "Design principles" in `docs/PROTOCOL.md`. The
  catch-alls in `Verbs.Execute` (`src/Verbs.cs`) and `OnUpdate` (`src/Main.cs`)
  enforce this rule.

## Commands

For the commands and checks, read "Before opening a PR" in
`docs/CONTRIBUTING.md`.
