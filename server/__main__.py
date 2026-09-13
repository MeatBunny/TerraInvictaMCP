"""TerraInvictaMCP server -- MCP stdio front-end for the in-game bridge.

Run as the MCP server (registered by install.sh):
  python3 /path/to/Mods/Enabled/TerraInvictaMCP/server

Debug path when the MCP layer itself is in question -- one bridge request,
print the reply, exit:
  python3 server --call <verb> [k=v ...]     (values parsed as JSON when
                                              possible, else strings)

Transport: newline-delimited JSON-RPC 2.0 on stdin/stdout. Each tool call
opens a TCP connection to the bridge (127.0.0.1:17470) inside the running
game; a dead bridge yields a tool error naming the next step, never a crash.
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bridge       # noqa: E402
import codestate    # noqa: E402
import resources    # noqa: E402
import tools        # noqa: E402

# Everything this server runs is loaded by now. Pin the bytes each module came
# from: from here on, an edit to server/*.py is a difference this process can
# see and report, instead of an invisible one that only a reconnect applies.
codestate.note()

SERVER_INFO = {"name": "terra-invicta", "version": "0.1.2"}
PROTOCOL_VERSIONS = ("2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05")

INSTRUCTIONS = """\
Terra Invicta (Steam) with an in-game TCP bridge served by the
TerraInvictaMCP DLL. These tools drive, observe, and test the game with no
GUI and no human in the loop. Agents are the only users.

MATCH THE ASK. Most requests are a quick check, not an audit. Default to the
cheapest thing that answers the actual question, and only build a full gate
run when asked for one.

  Quick check (minutes, the default). Force the state with `grant` and
  `give_resources`, or let `autopilot` run the campaign for a while, then
  assert the ONE thing in question. Skip everything not asked about. Say what
  you did not check. (`autopilot` is a UI macro, not the faction AI, and it
  picks techs at random; never use it to reach a specific tech state or to
  sample how the AI plays. `ai_autopilot` is the real thing: it hands the
  faction to AIDailyFactionPlanner, so use it when the question is how the AI
  plays, or when a late-game state has to look like one play produced.)

  Full gate (hours, only on request). modcheck, every localization key, every
  chain end to end, an AI regression run.

Never construct campaign state by playing to it. `grant`, `give_resources`
and `autopilot` reach in seconds what a playthrough takes hours to reach;
the `spawn_*` fixtures put the object itself in place (`spawn_hab`,
`spawn_module`, `spawn_fleet`, `spawn_army`, `spawn_councilor`,
`spawn_alien_site`); `kill_state` takes one back out (hab, councilor, army,
fleet, facility); and `war`, `control_points` and `prospect` set the
geopolitical and exploration state the same way. If you find yourself
building a hab through the game's own screens, or playing to a war, stop:
each is one call. Fixtures bypass cost, prereqs and build time, so they
prove a mechanism fires and never that content is reachable through play --
reachability is `modcheck` territory.

Reference resources, listed here because they get skipped:
`ti://docs/playbook` -- full operational playbook; read it before scripting
any campaign setup. `ti://docs/protocol` -- bridge wire contract and verb
names for raw/batch. `ti://templates`, `ti://saves`, `ti://logs/game` --
live indexes.

Call `observe` first, every session and whenever unsure. It reports whether
the bridge is up, a campaign is loaded, the date, what (if anything) blocks
the clock, and it names the next call.

The blocked-clock model: pending prompts, open alerts, modal screens, and
unresolved combat FREEZE the strategy clock -- no time passes at any speed
until they are cleared. A "hung" run is a blocked run. `advance` owns the
unattended loop (max speed, run_until, neutral prompt dismissal, combat
autoresolve) and returns early with the alert text and options when a real
decision blocks; decide with `alert_choose`, then call `advance` again. It
answers narrative event popups on its own and lists each one, with the option
taken, under `narrativeEvents` -- read that key rather than assuming no story
event fired.

Launch cycle: the game_start/game_stop tools own it on every platform.
`game_start` launches through Steam and polls the bridge up (20-60s).
`load_game` works from the main menu and tears the session down while
loading -- poll `observe` until campaign=true. `game_stop` kills the game;
do it when the session is done, and never leave the game running idle.

Scratch-save rule: never experiment on a campaign a person is playing.
`save_game name=scratch-<topic>` first and work on that copy.

Template universe: before a campaign starts, `template` returns the
un-resolved UNION of every scenario's data -- vanilla, DLC, and mod entries
coexist, scenario variants distinguished by scenarioTags. Campaign start
runs scenario resolution ONCE, destructively; afterwards only the loaded
scenario's resolved set is visible. Cross-scenario audits (modcheck,
template sweeps) run from the main menu; "what the game actually uses" is
answered inside a campaign.

The engine is ground truth: `template` and `localize` read merged in-engine
data; files on disk are neither complete (mods, DLC, scenario overlays)
nor strict JSON.
"""


def reply(msg_id, result=None, error=None):
    out = {"jsonrpc": "2.0", "id": msg_id}
    if error is not None:
        out["error"] = error
    else:
        out["result"] = result
    sys.stdout.write(json.dumps(out) + "\n")
    sys.stdout.flush()


def notify(method, params):
    sys.stdout.write(json.dumps({"jsonrpc": "2.0", "method": method,
                                 "params": params}) + "\n")
    sys.stdout.flush()


def make_progress(token):
    def progress(current, total=None, message=None):
        params = {"progressToken": token, "progress": current}
        if total is not None:
            params["total"] = total
        if message:
            params["message"] = message
        notify("notifications/progress", params)
    return progress


def handle(msg):
    """Dispatch one client message. Anything that is not an addressable
    request is consumed silently.

    A client may send any JSON at all, and every field read here is read off
    something it chose: an envelope that is not an object, `params` that is a
    scalar, a `_meta` that is a number. Each is checked before it is indexed,
    because an AttributeError here would otherwise reach the read loop and end
    the process.
    """
    if not isinstance(msg, dict):
        # Not a JSON-RPC message: no id to answer to and no method to
        # dispatch, so it gets the same treatment as an unparseable line.
        return
    msg_id = msg.get("id")
    if msg_id is None:
        # A notification. JSON-RPC forbids answering one, `ping` included, and
        # the only notifications a client sends this server are lifecycle
        # ones that need no work done for them.
        return
    method = msg.get("method")
    params = msg.get("params")
    if not isinstance(params, dict):
        params = {}
    if method == "initialize":
        client_ver = params.get("protocolVersion")
        ver = client_ver if client_ver in PROTOCOL_VERSIONS \
            else PROTOCOL_VERSIONS[0]
        reply(msg_id, {"protocolVersion": ver,
                       "capabilities": {"tools": {}, "resources": {}},
                       "serverInfo": SERVER_INFO,
                       "instructions": INSTRUCTIONS})
    elif method == "tools/list":
        reply(msg_id, {"tools": tools.TOOL_DEFS})
    elif method == "tools/call":
        meta = params.get("_meta")
        token = meta.get("progressToken") if isinstance(meta, dict) else None
        progress = make_progress(token) if token is not None else None
        reply(msg_id, tools.handle_call(params.get("name", ""),
                                        params.get("arguments"),
                                        progress))
    elif method == "resources/list":
        reply(msg_id, resources.list_resources())
    elif method == "resources/read":
        uri = params.get("uri", "")
        try:
            reply(msg_id, resources.read_resource(uri))
        except (KeyError, TypeError):
            reply(msg_id, error={"code": -32002,
                                 "message": "resource not found: %s" % (uri,)})
        except OSError as e:
            reply(msg_id, error={"code": -32603,
                                 "message": "cannot read %s: %s" % (uri, e)})
    elif method == "ping":
        reply(msg_id, {})
    else:
        reply(msg_id, error={"code": -32601,
                             "message": "method not found: %s" % (method,)})


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            continue
        try:
            handle(msg)
        except Exception as e:
            # One client message must never end the session. Everything
            # queued behind a bad line is other work, usually from the same
            # agent, and a dead stdio server reads to it as a tool that
            # vanished rather than a request it got wrong. Answer the request
            # if it can be addressed; drop it if it cannot.
            msg_id = msg.get("id") if isinstance(msg, dict) else None
            if msg_id is not None:
                reply(msg_id, error={
                    "code": -32603,
                    "message": "internal error handling %s: %s"
                               % (type(e).__name__, e)})


USAGE = """\
usage: python3 server                          MCP stdio server (the product)
       python3 server --call <verb> [k=v ...]  one bridge request, print the
                                               reply, exit (values parsed as
                                               JSON when possible)
"""


def debug_call(argv):
    if not argv or argv[0] in ("-h", "--help"):
        sys.stderr.write(USAGE)
        return 2
    verb = argv[0]
    args = {}
    for pair in argv[1:]:
        if "=" not in pair:
            sys.stderr.write("bad argument %r (want k=v)\n" % pair)
            return 2
        key, value = pair.split("=", 1)
        try:
            args[key] = json.loads(value)
        except json.JSONDecodeError:
            args[key] = value
    try:
        resp = bridge.send({"id": 1, "cmd": verb, "args": args})
    except bridge.BridgeError as e:
        sys.stderr.write("bridge unreachable: %s\n"
                         "Is the game running with the mod loaded? Use the "
                         "game_start tool, or launch Terra Invicta from "
                         "Steam.\n" % e)
        return 1
    print(json.dumps(resp, indent=2))
    return 0 if resp.get("ok") else 1


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--call":
        sys.exit(debug_call(sys.argv[2:]))
    if len(sys.argv) > 1:
        sys.stderr.write(USAGE)
        sys.exit(0 if sys.argv[1] in ("-h", "--help") else 2)
    main()
