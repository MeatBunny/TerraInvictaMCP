# TerraInvictaMCP

This is an MCP server for Terra Invicta. An AI coding agent (Claude Code,
OpenCode, Antigravity, or any other MCP client) uses it to control the game.
The agent can start the game, load and create campaigns, run the clock,
resolve battles, and take screenshots. It can also compare an installed mod
with the data that the running game holds. The main use is mod testing. A
test shows whether every value that a mod sets reached the game, and whether
its references resolve. It also shows whether its text localizes, and whether
it conflicts with the other enabled mods.

It has two parts. A DLL runs inside the game and serves a local TCP socket.
A Python MCP server makes that socket available to the agent as tools. You
do not need a compiler, because the release zip includes the DLL.

For work on the mod itself, see [Developing this mod](#developing-this-mod).

## What it can do

When the agent is connected, it can carry out these requests:

- Find the problems in a mod, or check that every change in it reached the
  game.
- Start a new campaign with the mod enabled and run the first month
  unattended.
- Report conflicts between installed mods, or which Workshop mods are out
  of date.
- Trigger a named event, pick an option, and report the effect.
- Force a nation stat (cohesion, democracy, inequality, education) and
  find out whether an event fires.
- Read a nation panel tooltip as text.
- Screenshot the game.

## Requirements

- Terra Invicta 1.0.51 or later on the Steam public branch, Windows or
  Linux (Proton).
- **Unity Mod Manager 0.24 or later**, installed with the Assembly method.
  Download it from [Nexus Mods](https://www.nexusmods.com/site/mods/21)
  (account required) and unpack it. Run `UnityModManager.exe`, select Terra
  Invicta, choose Assembly, and click Install. On Windows, the Doorstop
  method also works. The installer recognizes both methods. Under Proton,
  Doorstop needs a Steam launch option, but Assembly does not.

  On Linux, the UMM installer is a Windows program. Install `mono-complete`
  and run `mono UnityModManager.exe`. Its folder picker does not list folders
  under a dotted path (`~/.steam`, `~/.local`). You can make a symlink to an
  undotted path (`ln -s ~/.steam ~/steam`). Or you can run the console
  installer from the unpacked UMM folder:

  ```sh
  printf '97\nn\nI\n' | mono Console.exe
  ```

  The three answers are Terra Invicta's index in UMM's game list (97 at the
  time of writing), `n`, and `I` for install. If the index is not 97, run
  `mono Console.exe` and read the index from the list.
- Python 3.7 or later. On Windows, get it from python.org or
  `winget install Python.Python.3.12`.
- An MCP client. Claude Code is the tested client. The installer also
  registers OpenCode and Antigravity. Any other client uses the
  configuration snippet that the installer prints.

## Install

1. Download the [latest release](https://github.com/MeatBunny/TerraInvictaMCP/releases/latest)
   and unzip it into `Mods/Enabled/` in the game folder. `ModInfo.json` and
   `TerraInvictaMCP.dll` must be directly under
   `Mods/Enabled/TerraInvictaMCP/`. Windows Extract All adds a wrapper
   folder. If it did, move the inner folder up one level.

   To find the game folder, open your Steam library, right-click Terra
   Invicta, select Manage, and click Browse local files. On Linux, it is
   usually `~/.steam/steam/steamapps/common/Terra Invicta` or
   `~/.local/share/Steam/steamapps/common/Terra Invicta`. On Windows, it is
   usually `C:\Program Files (x86)\Steam\steamapps\common\Terra Invicta`, or
   the same path in another Steam library.
2. Run the installer from `Mods/Enabled/TerraInvictaMCP/`.

   Linux:

   ```sh
   ./install.sh
   ```

   Windows (PowerShell):

   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```

   If the agent runs the installer, name the client in the command. The
   agent has no terminal to answer prompts. Use
   `./install.sh --register=claude`, or
   `powershell -ExecutionPolicy Bypass -File install.ps1 -Register claude`.
3. Start the game, open "Mods" from the main menu, enable TerraInvictaMCP,
   and tick "use mods". The game loads template mods only when this box is
   ticked, and the checks need those mods. Unity Mod Manager loads the DLL
   itself whether the box is ticked or not.
4. Start the agent from the game folder. The Claude Code registration
   applies only to that folder. If the agent was already running, restart
   it.

Run the installer again after every game update. It is safe to run multiple
times.

### What the installer does

The installer checks the install. If a source file is newer than the DLL,
it builds the DLL. It checks the UMM injection and reports its state. It
repairs the injection only in the case that Troubleshooting describes. It
also registers clients (see Registration) and runs a self-test of the
server.

## Registration

- **Claude Code**: `claude mcp add`, local scope, run from the game folder.
- **OpenCode**: `opencode mcp add`, global.
- **Antigravity**: an entry in its `mcp_config.json`, global. It has no MCP
  command.

A non-interactive run registers only the clients that
`--register=claude,opencode` (`-Register claude,opencode` on Windows) names.
`--yes` (`-Yes`) accepts every client that the installer finds. If the
installer finds a client but does not register it, the last lines of the run
say so. They also give the command to run again.

For any other MCP client, use the snippet that the installer prints. The
installer fills in the real path:

```json
{"mcpServers": {"terra-invicta": {"command": "python3",
    "args": ["/path/to/Mods/Enabled/TerraInvictaMCP/server"]}}}
```

## First use

Ask the agent to observe the game. The reply states whether the game is
running, whether a campaign is loaded, the date, and what to do next. From
there, commands like "start the game", "load my save", and "check my mods"
do exactly what they say. The server hands the agent its operating manual
(`docs/playbook.md`) as an MCP resource.

## What the mod check covers

- **Values.** The check compares every field that the mod sets with the value
  in the running game. If another mod overwrote a value, the report names
  that mod.
- **References.** The check finds missing references without starting a
  campaign. Examples are a tech that requires a renamed tech, or a ship that
  points at a missing drive.
- **Text.** The check resolves names and descriptions through the game's
  localization. It finds a missing entry before the entry appears in the
  game as a raw key.
- **Conflicts.** It creates a pairwise report across everything enabled.
- **Reachability.** Some content (orgs, councilor looks) needs a registration
  in a second list. Without it, the content does not appear. The check
  checks that this registration exists.
- **Live testing.** It can start a new campaign on any scenario, including
  DLC, run unattended for days or months, spawn fleets, resolve battles,
  and take screenshots.

The full guide is in [docs/testing-your-mod.md](docs/testing-your-mod.md).
It has examples and tells you how to read the results.

## Safety and privacy

- The in-game server listens on `127.0.0.1` only. Nothing about the
  machine or the game leaves it, with one exception. A Workshop freshness
  check queries Steam's public web API about the installed mods.
- **The socket has no authentication.** Any program that runs under the
  same user can connect and drive the game while it runs. This is the same
  trust that you give to every program that you run. The socket is
  loopback-only for this reason.
- **Achievements.** Terra Invicta suppresses Steam achievement unlocks while
  the debug console is enabled. This mod sends console commands while the
  console is disabled. For that reason, the game does not suppress
  achievements. Changes that the mod makes through the console can unlock
  achievements on an install that has no console mod. If that matters to
  you, test on a throwaway profile.
- Test on a throwaway save. Each tool that changes the game carries the
  MCP hint for a destructive tool. A client that has a confirmation policy
  asks you before the agent uses such a tool. Still, save first or use a
  scratch campaign.

## Troubleshooting

- **"Bridge unreachable"**: the game is not running, or the mod is not
  enabled in the Mods menu.
- **The agent has no terra-invicta tools**: start the agent from the
  game folder. The Claude Code registration applies only to that folder. If the
  agent was running while the installer ran, restart it. If the tools are
  still missing, run the installer again with `--register=claude`
  (`-Register claude`) and read its report. On Windows, run the installer
  again after you upgrade Python. The Claude Code registration stores the
  full path of the interpreter, and the installer rewrites that path.
  OpenCode and Antigravity also store it, so register those clients again
  (see Registration).
- **"does not look like the Terra Invicta folder"**: the zip was unpacked
  one level too deep. Windows Extract All does this by default.
  `ModInfo.json` and `TerraInvictaMCP.dll` must be directly under
  `Mods/Enabled/TerraInvictaMCP/`.
- **Mods stopped loading after a game update**: run the installer again.
  Updates revert the Assembly injection. The installer reports this. If
  `UMM_INSTALLER_DIR` points at the unpacked UMM installer and `mono` is
  present, the installer repairs the injection on Linux. Otherwise, it
  names the fix (run `UnityModManager.exe`, Install). Updates do not affect
  a Doorstop install.
- **The clock does not move**: a popup, an event, or an unresolved
  battle needs a decision. "Observe" says which one. The agent can read the
  options and pick one.
- Anything else: ask the agent to tail the game log, because errors appear
  there.

## Uninstall

Disable the mod in the Mods menu or delete this folder, and then remove the
registration from each client:

- **Claude Code**: `claude mcp remove terra-invicta`, from the game folder.
- **OpenCode**: it has no remove command. Delete the `terra-invicta` entry
  under `mcp` in `~/.config/opencode/opencode.json` (Windows:
  `%APPDATA%\opencode\opencode.json`), or set its `"enabled": false`. On
  some installs, the file is `opencode.jsonc`.
- **Antigravity**: delete the `terra-invicta` entry from
  `~/.gemini/config/mcp_config.json` (Windows:
  `%USERPROFILE%\.gemini\config\mcp_config.json`), or use `/mcp` in the
  prompt panel.

The mod loader's change to `UnityEngine.UIModule.dll` is safe to keep,
because other DLL mods need it. The Restore button in UnityModManager.exe
reverts it.

## Developing this mod

The sections below are for changes to TerraInvictaMCP itself. You do not
need them to test your own mods.

### Building from source

You need to build the DLL only if you change the in-game part. A clone has
no DLL, so build one or take it from the release zip. Use `./build.sh` on
Linux, and `build.ps1` on Windows. Run either script with `--help` (`-Help`
on Windows) to see its requirements and the environment variables that it
reads.

The build compiles against the game's own assemblies. These assemblies are
not redistributable, so the build needs a local install of the game. For the
same reason, there is no CI build. You need one of these compilers:

- **Mono's `mcs`** (Debian/Ubuntu: `sudo apt install mono-devel`), or
- **a modern C# compiler**: a Roslyn `csc` from Visual Studio Build Tools or
  the .NET SDK.

Windows's built-in `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
does not work. It is the C# 5 compiler, and this source uses C# 6. The build
script detects this compiler and refuses it. It tells you the reason, so you
do not get a confusing compiler error.

### For tool developers

The bridge is an ordinary TCP server that speaks newline-delimited JSON
(NDJSON: one JSON object per line) on port 17470. Every MCP tool uses the
bridge, and you can build your own tooling on it in the same way. The wire
contract is in [docs/PROTOCOL.md](docs/PROTOCOL.md). It gives every verb,
argument, and gotcha. This debug call needs no MCP client:

```sh
python3 server --call query.time
```

Patches are welcome. Open an issue first.
[docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) has the rules. It also lists
the things in this codebase that look like defects and are not.

### Verb families

The verb families include:

- liveness and discovery
- read-only queries
- time control
- saves and campaign entry
- prompts and alerts
- combat
- ship designs
- fixtures
- gated play
- evaluation without execution
- the generic action layer
- AI control
- console and selection
- screenshots
- screen and view control

You can find every verb, its arguments, and its gotchas in
[docs/PROTOCOL.md](docs/PROTOCOL.md).
