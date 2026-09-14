# TerraInvictaMCP

An MCP server for Terra Invicta. It lets an AI coding agent (Claude Code,
OpenCode, Antigravity, or any other MCP client) start the game, load and
create campaigns, run the clock, resolve battles, take screenshots, and
check an installed mod against what the running game holds. The main use
is mod testing: whether every value a mod sets landed, whether its
references resolve, whether its text localizes, and whether it conflicts
with the other mods enabled.

Two parts: a DLL that runs inside the game and serves a local TCP socket, and
a Python MCP server that exposes it as tools. No compiler is needed; the
release zip ships the DLL.

For work on the mod itself see [Developing this mod](#developing-this-mod).

## What it can do

Requests the agent can carry out once connected:

- Check a mod for problems, or confirm that every change in it reached the
  game.
- Start a new campaign with the mod enabled and run the first month
  unattended.
- Report conflicts between installed mods, or which Workshop mods are out
  of date.
- Trigger a named event, pick an option, and report the effect.
- Force a nation stat (cohesion, democracy, inequality, education) and
  check whether an event fires.
- Read a nation panel tooltip as text.
- Screenshot the game.

## Requirements

- Terra Invicta 1.0.51 or later on the Steam public branch, Windows or
  Linux (Proton).
- **Unity Mod Manager 0.24 or later**, installed with the **Assembly**
  method. Download from
  [Nexus Mods](https://www.nexusmods.com/site/mods/21) (account required),
  unpack, run `UnityModManager.exe`, select Terra Invicta, choose Assembly,
  Install. On Windows the Doorstop method also works; the installer
  recognises both. Under Proton, Doorstop needs a Steam launch option and
  Assembly does not.

  On Linux the UMM installer is a Windows program. Install `mono-complete`
  and run `mono UnityModManager.exe`. Its folder picker does not list
  folders under a dotted path (`~/.steam`, `~/.local`); either symlink an
  undotted route (`ln -s ~/.steam ~/steam`) or use the console installer
  from the unpacked UMM folder:

  ```sh
  printf '97\nn\nI\n' | mono Console.exe
  ```

  The three answers are Terra Invicta's index in UMM's game list (97 at the
  time of writing), `n`, and `I` for install. If the index has changed, run
  `mono Console.exe` and read it from the list.
- Python 3.7 or later. On Windows: python.org or
  `winget install Python.Python.3.12`.
- An MCP client. Claude Code is the tested one; OpenCode and Antigravity are
  registered by the installer as well. Any other client takes the config
  snippet the installer prints.

## Install

1. Download the [latest release](https://github.com/MeatBunny/TerraInvictaMCP/releases/latest)
   and unzip it into the game folder's `Mods/Enabled/` so that
   `ModInfo.json` and `TerraInvictaMCP.dll` are directly under
   `Mods/Enabled/TerraInvictaMCP/`. Windows Extract All adds a wrapper
   folder; move the inner one up a level if it did.

   The game folder: Steam library, right-click Terra Invicta, Manage,
   Browse local files. Typically
   `~/.steam/steam/steamapps/common/Terra Invicta` or
   `~/.local/share/Steam/steamapps/common/Terra Invicta` on Linux, and
   `C:\Program Files (x86)\Steam\steamapps\common\Terra Invicta` (or the
   same path in another Steam library) on Windows.
2. Run the installer from `Mods/Enabled/TerraInvictaMCP/`.

   Linux:

   ```sh
   ./install.sh
   ```

   Windows (PowerShell):

   ```powershell
   powershell -ExecutionPolicy Bypass -File install.ps1
   ```

   If the agent runs it, name the client, since there is no terminal to
   answer prompts: `./install.sh --register=claude`, or
   `powershell -ExecutionPolicy Bypass -File install.ps1 -Register claude`.
3. Start the game, open **Mods** from the main menu, enable TerraInvictaMCP
   and tick **use mods**. The tick is what loads template mods, which is what
   the checks inspect; the DLL itself loads through Unity Mod Manager
   regardless.
4. Start the agent from the game folder. The Claude Code registration is
   scoped to that folder. Restart it if it was already running.

Run the installer again after every game update. It is idempotent.

### What the installer does

It checks the install, builds only when a source is newer than the DLL, checks
the UMM injection, registers clients (see Registration), and self-tests the
server.

## Registration

- **Claude Code**: `claude mcp add`, local scope, run from the game folder.
- **OpenCode**: `opencode mcp add`, global.
- **Antigravity**: an entry in its `mcp_config.json`, global. It has no MCP
  command.

Non-interactive runs register only what `--register=claude,opencode`
(`-Register claude,opencode` on Windows) names; `--yes` (`-Yes`) accepts
every client found. When a client was found and left unregistered, the last
lines of the run say so and give the command to re-run.

Any other MCP client takes the snippet the installer prints with the real
path filled in:

```json
{"mcpServers": {"terra-invicta": {"command": "python3",
    "args": ["/path/to/Mods/Enabled/TerraInvictaMCP/server"]}}}
```

## First use

Ask the agent to observe the game. The reply states whether the game is
running, whether a campaign is loaded, the date, and what to do next. From
there, "start the game", "load my save" and "check my mods" do what they
say. The server hands the agent its operating manual
(`docs/playbook.md`) as an MCP resource.

## What the mod check covers

- **Values.** Every field the mod sets is compared with what the running
  game holds. When another mod overwrote a value, that mod is named.
- **References.** A tech requiring a renamed tech, a ship pointing at a
  missing drive: found without starting a campaign.
- **Text.** Names and descriptions are resolved through the game's
  localization, so a missing entry is found before it appears as a raw key.
- **Conflicts.** Pairwise report across everything enabled.
- **Reachability.** Content that must be registered in a second list to
  appear (orgs, councilor looks) is checked for that registration.
- **Live testing.** New campaign on any scenario, DLC included; unattended
  runs of days or months; spawned fleets, resolved battles, screenshots.

The full guide, with examples and how to read the results:
[docs/testing-your-mod.md](docs/testing-your-mod.md).

## Safety and privacy

- The in-game server listens on `127.0.0.1` only. Nothing about the
  machine or the game leaves it, with one exception: a Workshop freshness
  check queries Steam's public web API about the installed mods.
- **The socket has no authentication.** Any program running under the same
  user can connect and drive the game while it runs. That is the trust
  boundary of any program you run, and the reason the socket is
  loopback-only.
- **Achievements.** Terra Invicta suppresses Steam achievement unlocks while
  the debug console is enabled. This mod reaches the console directly, so
  console-driven changes can unlock achievements on an install without a
  console mod. Test on a throwaway profile if that matters.
- Test on a throwaway save. The tools that change the game are marked so
  the agent asks before anything destructive; saving first, or using a
  scratch campaign, is still the safe habit.

## Troubleshooting

- **"Bridge unreachable"**: the game is not running, or the mod is not
  enabled in the Mods menu.
- **The agent has no terra-invicta tools**: start it from the game folder,
  since the Claude Code registration is scoped to that folder, and restart
  it if it was running while the installer ran. Otherwise run the installer
  again with `--register=claude` (`-Register claude`) and read its report.
  On Windows, run the installer again after upgrading Python: the Claude
  Code registration stores the interpreter's full path and the installer
  rewrites it. OpenCode and Antigravity store it too; register those again
  (see Registration).
- **"does not look like the Terra Invicta folder"**: the zip was unpacked
  one level too deep, the Windows Extract All default. `ModInfo.json` and
  `TerraInvictaMCP.dll` must be directly under
  `Mods/Enabled/TerraInvictaMCP/`.
- **Mods stopped loading after a game update**: run the installer again.
  Updates revert the Assembly injection; the installer reports it, repairs
  it on Linux when `UMM_INSTALLER_DIR` points at the unpacked UMM installer
  and `mono` is present, and otherwise names the fix (run
  `UnityModManager.exe`, Install). A Doorstop install is unaffected by
  updates.
- **The clock does not move**: something needs a decision, a popup, an
  event, or an unresolved battle. "Observe" says which; the agent can read
  the options and pick one.
- Anything else: ask the agent to tail the game log. Errors land there.

## Uninstall

Disable the mod in the Mods menu or delete this folder, then remove the
registration from each client:

- **Claude Code**: `claude mcp remove terra-invicta`, from the game folder.
- **OpenCode**: no remove command. Delete the `terra-invicta` entry under
  `mcp` in `~/.config/opencode/opencode.json` (Windows:
  `%APPDATA%\opencode\opencode.json`), or set its `"enabled": false`. The
  file is `opencode.jsonc` on some installs.
- **Antigravity**: delete the `terra-invicta` entry from
  `~/.gemini/config/mcp_config.json` (Windows:
  `%USERPROFILE%\.gemini\config\mcp_config.json`), or use `/mcp` in the
  prompt panel.

The mod loader's change to `UnityEngine.UIModule.dll` can stay; other DLL
mods need it. UnityModManager.exe's Restore button reverts it.

## Developing this mod

Everything from here down is for changing TerraInvictaMCP itself. Using it
to test your own mods needs none of this.

### Building from source

Only needed if you change the in-game part; a clone has no DLL until you
build one or take it from the release zip. `./build.sh` on Linux, `build.ps1`
on Windows; run either with `--help` (`-Help` on Windows) for its
requirements and the environment variables it reads.

The build compiles against the game's own assemblies. These cannot be
redistributed, so it needs a local install of the game -- and for the
same reason there is no CI build. You need one of:

- **Mono's `mcs`** (Debian/Ubuntu: `sudo apt install mono-devel`), or
- **a modern C# compiler** -- a Roslyn `csc` from Visual Studio Build Tools or
  the .NET SDK.

Windows's built-in `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
will *not* work. It is the C# 5 compiler and this source uses C# 6. The build
script detects it, refuses it, and says so instead of handing you a
confusing compiler error.

### For tool developers

The bridge is an ordinary TCP server speaking newline-delimited JSON (NDJSON:
one JSON object per line) on port 17470. Every MCP tool is built on it, and
you can build your own tooling the same way. The wire contract -- every
verb, argument, and gotcha -- is in
[docs/PROTOCOL.md](docs/PROTOCOL.md). A debug call without any MCP client:

```sh
python3 server --call query.time
```

Patches are welcome, starting with an issue.
[docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) has the rules. It also lists the
things in this codebase that look like defects and are not.

### Verb families

Liveness and discovery, read-only queries, time control, saves and campaign
entry, prompts and alerts, combat, ship designs, fixtures, gated play,
evaluation without execution, the generic action layer, AI control, console
and selection, screenshots, screen and view control. Every verb, its
arguments and its gotchas: [docs/PROTOCOL.md](docs/PROTOCOL.md).
