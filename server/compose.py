"""Composite workflows: observe, game lifecycle, the unattended-run loop,
batch, smoke test, and the thin verb wrappers that need arg mapping.

Handlers take (args, progress) and return plain data (JSON-encoded by
tools.py) or a {"_content": [...]} dict for non-text blocks. They raise
ToolError for agent-correctable failures; bridge.BridgeError propagates and
becomes the game-down guidance in tools.py.
"""
import base64
import datetime
import filecmp
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time as _time

import bridge
import codestate
from bridge import IS_WINDOWS, BridgeError, BridgeTimeout, VerbError

APPID = "1176470"
EXE_NAME = "TerraInvicta.exe"

# How the game is recognized on POSIX. Wine (and therefore Proton) renames the
# process to the exe's basename, which the kernel truncates to 15 characters,
# so the game runs under the process NAME "TerraInvicta.ex" and pgrep/pkill
# match it without -f. That matters: -f matches whole command lines, and the
# Steam launch chain carries the exe path in the argv of the launch wrapper,
# the reaper, the runtime entry point and the Proton script -- as does any
# unrelated command that merely mentions the file, so an agent running
# `ls '<game>/TerraInvicta.exe'` was a valid kill target. Name matching cannot
# be spoofed that way.
GAME_COMM = r"^TerraInvicta\.ex"
LAUNCH_WAIT = 180       # seconds to wait for the bridge after a Steam launch
CAMPAIGN_WAIT = 300     # seconds to wait for a campaign after a load/new


class ToolError(Exception):
    """A tool-level failure whose message tells the agent the next step."""


# ---------------------------------------------------------------- run state
#
# The MCP server process outlives every tool call, so what an unattended run
# has to remember BETWEEN calls lives here. Nothing else in this module keeps
# state; these three counters exist because the failure they catch is the one
# a per-call view cannot see. `advance` is called in a loop by an agent that
# each digest tells to call again, so without a memory the thirtieth fruitless
# call is indistinguishable from the first and the loop never ends.
_RUN = {
    # Consecutive advance calls that finished having moved zero game days,
    # with the stop reason each of them gave.
    "zeroCalls": 0,
    "zeroReasons": [],
    # Automatic crash restarts since the last call that made progress. The
    # guard rail is one: a crash that recurs on reload must not become a
    # restart loop.
    "crashRecoveries": 0,
    # The autopilot macro was switched on through this server and has not been
    # switched off. Only then is its engaged state worth polling.
    "macroOn": False,
    # The campaign token last seen on an envelope. A different one means the
    # campaign every other counter here describes is gone, whether or not a
    # tool of this server's ordered the change.
    "campaignToken": None,
    # When the run entered the campaign it is playing, in the format the DLL
    # reports save mtimes in. What makes "a save of this run" answerable.
    "campaignAt": None,
    # The save this run is playing, as {name, extension}, and the campaign
    # token it belongs to. The token is None until the campaign the save was
    # loaded into comes up, and is bound on the next change; a change after
    # that is a campaign this save is no evidence about, and it is dropped.
    "save": None,
    "saveToken": None,
    # A campaign entry this server ordered and has not seen arrive yet, so the
    # next token change is that one rather than a campaign someone reached
    # through the game's own screens, and the game process it was ordered in.
    "campaignPending": False,
    "entryProcess": None,
}

# Consecutive fruitless advance calls before the escalation changes. The
# second says the run appears stalled and names what the harness can see; the
# third refuses to re-arm at all.
ZERO_PROGRESS_STALLED = 2
ZERO_PROGRESS_REFUSE = 3

# Automatic crash restarts allowed without intervening progress.
MAX_CRASH_RECOVERIES = 1

# Seconds to wait for the bridge to go quiet after a stop, before a restart is
# called failed.
BRIDGE_DOWN_WAIT = 20

# Consecutive polls that go unanswered before a waiting loop gives up and
# reports the bridge as lost. A single failed poll is a busy main thread and
# is retried; five in a row is a socket nothing is going to answer. The loops
# that count them never take an unanswered poll for a reading: silence says
# nothing about what the game is doing, and acting on it as though it did is
# how a live fight got its prompt dropped.
LOST_POLLS_MAX = 5

# Polls with an unreadable remaining-prompt count before the call stops. The
# count is unreadable when the dismiss call raised, when the reply was not a
# dictionary, or when the queue refused; none of those is a clear queue.
UNKNOWN_REMAINING_POLLS = 5

# Polls a call will spend with the clock held down for an open mission phase
# before it stops. At two seconds a poll that is half a minute, which is far
# longer than the confirmation the dismiss pass presses needs and far shorter
# than the budget, so a phase nothing here can close is named as itself
# instead of arriving as an unexplained max_seconds.
MISSION_PHASE_POLLS = 15


# ------------------------------------------------------------- pause limit
#
# How long the game may go without gaining time before the tools stop taking
# state-changing calls. The failure this catches is a run that spends its hour
# with the clock stopped -- blocked on a prompt nobody answered, parked at a
# target nobody re-armed, or building fixtures one call at a time -- and
# reports nothing wrong, because every individual call succeeded. From outside
# it is unknown whether the agent is thinking or waiting on something that will
# never arrive. Activity is not an excuse: a stopped clock is a test that is
# not running.
#
# The DLL measures the stall against real time and puts the seconds on every
# response envelope (see bridge._note_stall) and the diagnosis on query.time.
# Everything here decides what to do with it.
PAUSE_LIMIT_SECONDS = 300

# How old a stall reading may be before it is refreshed with one query.time.
# Short enough that a refusal describes now rather than the last call, long
# enough that a burst of tool calls costs one extra verb rather than one each.
STALL_CACHE_SECONDS = 5

# Tools that run whatever the clock is doing, because they move it, clear what
# blocks it, end or recover the run, or are the escape hatch. A limit that
# refused the way out would be a trap rather than a guard.
PAUSE_EXEMPT = frozenset((
    "advance", "time", "prompts", "alert_choose",
    "combat_start", "combat_status", "combat_autoresolve", "combat_precombat",
    "combat_stance",
    "autopilot", "ai_autopilot", "game_stop", "game_start", "main_menu",
    "load_game", "campaign_new", "save_game", "crash_the_game",
    # A write, but to this server's own session state rather than the game's,
    # and it is how a run raises the limit it is being refused by.
    "set_pause_limit"))

# The two general-purpose passes through to any bridge verb. Over the limit
# they are decided by what they carry rather than by their own names: a read
# gets the banner, anything else is refused. Listing them as plain exempt was
# a hole the width of the whole verb table -- every fixture, every console
# line and every write the limit refuses under its own tool name goes through
# either of these unchanged.
PAUSE_BANNER_ANYWAY = frozenset(("raw", "batch"))

# Bridge verbs that change nothing, for deciding a raw or batch call. Named
# one by one rather than guessed from the verb name: `query.` and `assets.`
# are whole read-only namespaces, and the rest are the individual readers.
# Anything not here -- a verb this list has not learned included -- is treated
# as a write, which is the safe direction for a guard whose job is to stop
# state being built over a dead clock.
READ_VERB_PREFIXES = ("query.", "assets.")
READ_VERBS = frozenset((
    "mods.list", "harmony.patches", "ui.screenshot", "ui.tooltip",
    "ui.describe", "combat.status", "prompts.list", "saves.list", "version",
    # `action.list` is the reflection catalog of invokable action classes and
    # has no tool of its own, so `raw` is the only way to read it and the
    # allowlist is the only thing that decides. `ping` and `verbs` are the two
    # verbs that answer with no campaign loaded and change nothing at all.
    "action.list", "ping", "verbs"))

# Read-only tools that report the limit themselves. Prefixing the banner as
# well would say the same thing twice on the one call an agent reads closely.
PAUSE_NO_BANNER = frozenset(("observe", "pause_limit"))

PAUSE_MESSAGE = (
    "PAUSE LIMIT EXCEEDED: the game clock has not moved for %d s, over the "
    "limit of %d s (state=%s, prompt=%s, last verb %s s ago). This is a test "
    "failure. Move the clock with advance (force=true if advance has refused "
    "for no progress), clear what blocks it with prompts, alert_choose or "
    "combat_autoresolve, or stop the game and report the failure. "
    "State-changing calls are refused until the clock moves.")


def _initial_limit():
    """The limit this process starts with. TIBRIDGE_PAUSE_LIMIT overrides."""
    raw = os.environ.get("TIBRIDGE_PAUSE_LIMIT")
    if raw is None or not str(raw).strip():
        return float(PAUSE_LIMIT_SECONDS)
    try:
        return max(0.0, float(raw))
    except ValueError:
        # A typo must not silently disable the guard.
        return float(PAUSE_LIMIT_SECONDS)


# Session state, not a constant: the set_pause_limit tool writes it, so a QA
# run can tune or disable the limit without restarting the server. Every
# observe says what it is and when it was last changed, so a run that raised it
# cannot hide that it did.
_LIMIT = {"seconds": _initial_limit(), "changedAt": None}

# The record of what the stall did to this session. `stallViolations` counts
# episodes -- one per continuous stall that crossed the limit, however many
# calls it refused -- and `refusedCalls` counts the refusals themselves, so a
# single long stall reads as one failure rather than as forty.
_STALL = {"stallViolations": 0,
          "refusedCalls": 0,
          "longestStallSeconds": 0,
          "lastViolation": None}

# Whether a crossing is currently open. Closed again by any reading at or
# under the limit, which is what makes the next crossing a new episode.
_EPISODE = {"open": False}


def pause_limit():
    return _LIMIT["seconds"]


def set_pause_limit(seconds, record=False):
    """Set the limit for this session. 0 disables it.

    `record` stamps the change, which is what observe reports. Only the tool
    passes it: a test or a fixture restoring the limit it found is not a run
    deciding to run with a different one.
    """
    try:
        value = float(seconds)
    except (TypeError, ValueError):
        raise ToolError("seconds must be a number (0 disables the limit)")
    if value < 0:
        raise ToolError("seconds must be 0 or more (0 disables the limit)")
    _LIMIT["seconds"] = value
    if record:
        _LIMIT["changedAt"] = _time.strftime("%Y-%m-%dT%H:%M:%S")
    return value


def limit_state():
    """The limit, whether it is the shipped default, and when it last moved.

    All three on every observe. A run that raised the limit and then reported
    a clean sweep has to be readable as what it was, and the seconds alone do
    not say whether the value was chosen or inherited from the environment.
    """
    limit = pause_limit()
    return {"limitSeconds": limit,
            "limitIsDefault": limit == float(PAUSE_LIMIT_SECONDS),
            "defaultSeconds": PAUSE_LIMIT_SECONDS,
            "limitChangedAt": _LIMIT["changedAt"]}


def _speed_level(value):
    """The speed level an argument names, or None when it names none.

    Coerced, because the wire is JSON and a client that types its arguments
    loosely sends "0" for 0, which the DLL accepts as a pause. A boolean is
    not a level: bool is an int in Python and False would read as speed 0.
    """
    if value is None or isinstance(value, bool):
        return None
    try:
        return float(str(value).strip())
    except (TypeError, ValueError):
        return None


def _stops_the_clock(args):
    """A `time` call that pauses. Speed 0 is a pause by its other name."""
    args = args or {}
    if args.get("action") == "pause":
        return True
    return _speed_level(args.get("speed")) == 0


def _speed_arg(value):
    """The `level` a `time.speed` call carries for this `speed` argument.

    The DLL takes the JSON integer token and nothing else, and the pause gate
    reads the same argument through `_speed_level`, which coerces. Without the
    same coercion here the two disagree over a loosely typed client: the gate
    reads `"0"` as the pause it is and refuses it over a stalled clock, and
    under the limit the DLL refuses the same string for its type -- so the one
    call that would clear the stall never lands whichever side of the limit it
    is made on.

    Only a whole number is coerced. Anything else goes through untouched, so
    the refusal comes from the DLL, which is the side that knows the levels.
    """
    level = _speed_level(value)
    try:
        whole = int(level)
    except (TypeError, ValueError, OverflowError):
        return value
    return whole if level == whole else value


def _read_verb(cmd):
    """Whether this bridge verb only reads."""
    return isinstance(cmd, str) and (
        cmd in READ_VERBS or cmd.startswith(READ_VERB_PREFIXES))


def _passthrough_verbs(name, args):
    """Every bridge verb a raw or batch call would send, or None when the call
    does not say. A step list this cannot read is not an empty one."""
    args = args or {}
    if name == "raw":
        cmd = args.get("cmd")
        return [cmd] if cmd else None
    steps = args.get("steps")
    if not isinstance(steps, list) or not steps:
        return None
    return [s.get("cmd") if isinstance(s, dict) else None for s in steps]


def pause_decision(name, args, read_only, stall, limit):
    """What to do with this call: "run", "banner" or "refuse".

    Pure, so the decision is testable without a game. `stall` is the seconds
    the DLL reported and is None whenever there is nothing to enforce: no
    campaign, a DLL older than the key, or a refresh that failed. Unknown never
    refuses -- a harness that stopped working over a reading it could not take
    would be a worse failure than the stall it guards against.

    Read-only tools are never refused. A stalled campaign is still a legitimate
    thing to read: a researcher, a reviewer or the user looking at a paused game
    is not the failure this exists for. They get the banner instead, which puts
    the stall in front of whoever is driving without blocking whoever is not.

    `raw` and `batch` are decided by the verbs they carry, for the same reason:
    the name says nothing about whether the call reads or writes, and taking
    them at their name let every refused write through under another spelling.
    """
    if not limit or stall is None or stall <= limit:
        return "run"
    if name == "time" and _stops_the_clock(args):
        return "refuse"
    if name in PAUSE_BANNER_ANYWAY:
        verbs = _passthrough_verbs(name, args)
        if verbs and all(_read_verb(v) for v in verbs):
            return "banner"
        # Includes a call whose verbs could not be read: over the limit, a
        # passthrough that will not say what it sends is refused rather than
        # guessed at.
        return "refuse"
    if name in PAUSE_EXEMPT:
        return "run"
    if read_only:
        return "banner"
    return "refuse"


def pause_banner(stall, block, limit, prompt):
    """The PAUSE LIMIT EXCEEDED text, for a refusal and for a banner alike."""
    block = block if isinstance(block, dict) else {}
    since = block.get("sinceLastVerbSeconds")
    return PAUSE_MESSAGE % (
        int(stall), int(limit),
        block.get("state") or "unknown",
        prompt or "none",
        "?" if since is None else int(since))


def _stall_block(t):
    """The stall block off a query.time reply, or None."""
    block = t.get("stall") if isinstance(t, dict) else None
    return block if isinstance(block, dict) else None


def _blocking_prompt():
    """The name of the prompt holding the clock, or None.

    Read once per episode, at the crossing: it is the difference between "a
    decision nobody is taking" and "nobody is driving at all", and the two need
    different fixing.
    """
    data = _try("prompts.list")
    items = data.get("prompts") if isinstance(data, dict) else None
    if not isinstance(items, list):
        return None
    for item in items:
        if isinstance(item, dict) and item.get("name"):
            return item["name"]
    return None


def _violation_record(stall, block, limit):
    block = block if isinstance(block, dict) else {}
    return {"at": _time.strftime("%Y-%m-%dT%H:%M:%S"),
            "stallSeconds": int(stall),
            "limitSeconds": int(limit),
            "state": block.get("state"),
            "crawl": block.get("crawl"),
            "sinceLastVerbSeconds": block.get("sinceLastVerbSeconds"),
            "prompt": _blocking_prompt(),
            # Filled by the first refusal of this episode. An episode opened by
            # a reading that refused nothing keeps it null, which is the truth.
            "tool": None}


def note_reading(stall, block, limit=None):
    """Record a stall reading. Returns the open episode's record, or None.

    This is where an episode begins and ends: it opens the first time a reading
    crosses the limit and closes on the first reading AT OR UNDER it, so one
    long stall is one violation whatever it refused along the way.

    An unknown reading closes nothing. Unknown is not "the clock moved": a
    bridge timeout or a dropped connection clears the cached reading, and every
    such hiccup mid-stall would otherwise end the episode and start a second
    one on the next call -- counting one stall as two and rewriting the record
    with a later, less useful one. Unknown returns None all the same, so an
    unknown reading never stops or refuses anything either.
    """
    limit = pause_limit() if limit is None else limit
    if stall is not None and stall > _STALL["longestStallSeconds"]:
        _STALL["longestStallSeconds"] = int(stall)
    if not limit:
        _EPISODE["open"] = False
        return None
    if stall is None:
        return None
    if stall <= limit:
        _EPISODE["open"] = False
        return None
    if not _EPISODE["open"]:
        _EPISODE["open"] = True
        _STALL["stallViolations"] += 1
        _STALL["lastViolation"] = _violation_record(stall, block, limit)
    return _STALL["lastViolation"]


def note_refusal(name):
    _STALL["refusedCalls"] += 1
    record = _STALL["lastViolation"]
    if isinstance(record, dict) and record.get("tool") is None:
        record["tool"] = name


def stall_counters():
    out = dict(_STALL)
    out.update(limit_state())
    return out


# What _refresh_stall answers when the query.time itself failed, which is not
# the same as a reply that carried no stall block. The first says nothing was
# measured and the cached seconds are now stale by however long the call took;
# the second is a DLL older than the key, whose cached seconds are current.
# Told apart, or the limit refuses calls over a reading taken before the
# bridge died.
UNREADABLE = object()


def _refresh_stall():
    """One query.time, for the reading and its diagnosis both.

    Returns the stall block, None when the reply carried none, or UNREADABLE
    when the call failed, timed out or found no campaign. Unknown never
    refuses, but the caller has to know which unknown it has: a failed call
    invalidates the cached seconds it was refreshing.
    """
    try:
        return _stall_block(bridge.call("query.time"))
    except (BridgeError, VerbError):
        return UNREADABLE


def current_reading():
    """(stall seconds, diagnosis block). Either may be None for unknown.

    The seconds are cached off the last response envelope of any verb, so an
    ordinary session keeps them fresh for free; the diagnosis only comes from
    query.time, so it is None until something refreshes.

    A refresh that could not be taken answers unknown for both. The cached
    seconds it was sent to replace are no longer a reading of anything: the
    call that failed cleared them (bridge.clear_stall), and the game they
    described may not be running at all.
    """
    seconds, age = bridge.last_stall()
    block = None
    if age is None or age > STALL_CACHE_SECONDS:
        block = _refresh_stall()
        if block is UNREADABLE:
            return None, None
        seconds, _age = bridge.last_stall()
        if block is not None and block.get("seconds") is not None:
            seconds = block["seconds"]
    return seconds, block


def _pause_stop(t, polls, suppress=False, first_poll=1):
    """The digest entry advance stops on, or None to keep going.

    advance reads the stall off the query.time it takes every poll, so this
    costs nothing until it stops something. It never fires on the first poll:
    advance is the way out of a stall, and a call that gave up before running
    its loop body once would leave an agent with a refusal and no tool that
    could clear it. `suppress` is the caller's own reason to keep going -- a
    hold it is working, or force -- and it still records the reading, because
    the session counters describe the game rather than this loop's patience.

    The entry is built from THIS poll's reading rather than handed back from
    the episode. The episode's record was taken when the stall first crossed
    the limit, which can be minutes and another tool ago; a digest built from
    it would report those seconds and that state beside a `next` line
    describing now.
    """
    block = _stall_block(t)
    stall = block.get("seconds") if isinstance(block, dict) else None
    if stall is None:
        stall, _age = bridge.last_stall()
    over = note_reading(stall, block) is not None
    if not over or suppress or polls <= first_poll:
        return None
    entry = _violation_record(stall, block, pause_limit())
    # Named, because a digest that said nothing here would read as a refusal
    # by some other tool. advance stopped itself.
    entry["tool"] = "advance"
    return entry


def _needs_reading(name, args):
    """Whether this call is worth taking a stall reading before it runs.

    A reading can cost a whole query.time, and a query.time against a wedged
    main thread costs the full bridge timeout. game_stop is the recovery for
    exactly that game, and game_start, load_game and advance are the rest of
    the way back -- so the tools that can neither be refused nor bannered skip
    the reading rather than paying half a minute for an answer nothing would
    have done anything with.
    """
    if name == "time" and _stops_the_clock(args):
        return True
    if name in PAUSE_BANNER_ANYWAY:
        return True
    return name not in PAUSE_EXEMPT


def pause_gate(name, args, read_only=False):
    """(refusal, banner) for this tool call; both None when nothing is wrong.

    Called by tools.handle_call for every tool. A refusal means the handler
    must not run at all; a banner means it runs and its answer carries the
    warning.
    """
    limit = pause_limit()
    if not limit or not _needs_reading(name, args):
        return None, None
    stall, block = current_reading()
    # The diagnosis before the record, not after it. current_reading skips the
    # query.time while the cached seconds are fresh, and the violation record
    # is written once per episode, so a crossing detected off a fresh cache
    # needs the block read now or the whole episode records a null state.
    # One extra verb, only on the readings that are about to refuse something.
    if block is None and stall is not None and stall > limit:
        block = _refresh_stall()
        if block is UNREADABLE:
            # The bridge did not answer, so there is no reading here at all --
            # not even the cached one, which the failed call has just cleared
            # and which describes a game that may have died since. Refusing
            # the call on it would answer a dead bridge with a stall report;
            # unknown lets the tool run and fail with its own error, which is
            # the one that says what actually happened.
            stall = bridge.last_stall()[0]
            block = None
        elif isinstance(block, dict) and block.get("seconds") is not None:
            # The refresh is a newer reading than the cache it replaced, and
            # the clock may have moved in between.
            stall = block["seconds"]
    record = note_reading(stall, block, limit)
    decision = pause_decision(name, args, read_only, stall, limit)
    if decision == "run":
        return None, None
    if decision == "banner" and name in PAUSE_NO_BANNER:
        return None, None
    prompt = record.get("prompt") if isinstance(record, dict) else None
    text = pause_banner(stall, block, limit, prompt)
    if decision == "banner":
        return None, text
    note_refusal(name)
    return text, None


# Stop reasons that mean the call did nothing and named no cause. Only these
# count toward the stall refusal.
#
# Every other zero-day stop hands the caller something concrete: a decision to
# answer, a combat to dispose of, a macro to restart, a queue to inspect, a
# crash with its own separate budget. Two prompts and a fight inside one
# in-game day is an ordinary campaign, and counting those calls would refuse a
# run that is working -- a new way for an unattended run to stop, in a change
# whose whole purpose is removing them. So an actionable stop CLEARS the
# counter: the caller acted, and the run is not spinning in silence.
#
# `mission_phase` is in the set even though it names a cause: what it names is
# a phase that would not close over half a minute of dismiss passes, which is
# a game nothing here can drive rather than a decision the caller can take.
# Three of those in a row is the stall the refusal exists for.
#
# `pause_limit` is in it for the same reason and more plainly: the call stopped
# because the clock has not moved, and calling again over the same stopped
# clock is the loop the refusal exists to break. Left out, a run could take
# zero-day pause_limit stops forever, each one clearing the counter that was
# supposed to notice.
# A prompts.dismiss refusal is in it for the third form of the same reason:
# the pass that answers prompts declined to run, and nothing this loop does
# between calls changes its mind. Only the caller can, by fixing what the
# refusal names.
# `bridge_lost` and a terminal `crashed` are in it for a fourth form of the
# same reason: both hand back a digest, so both leave the caller able to call
# again, and neither is a state any number of further calls changes. A bridge
# that has stopped answering and a game that crashed a second time after its
# automatic restart are exactly the runs that must escalate to the refusal
# rather than loop. `crash_recovered` is the one that clears the counter: the
# game came back, the save reloaded, and the next call has a live game to run.
INERT_STOPS = frozenset(
    ("no_progress", "unknown", "mission_phase", "pause_limit",
     "active_player_moved", "prompts_refused", "bridge_lost", "crashed"))


def _note_progress(days, reason):
    """Fold one finished advance call into the run state."""
    if days > 0 or reason not in INERT_STOPS:
        _RUN["zeroCalls"] = 0
        _RUN["zeroReasons"] = []
    else:
        _RUN["zeroCalls"] += 1
        _RUN["zeroReasons"] = (_RUN["zeroReasons"] + [reason])[-6:]
    # Days moved before the crash landed are not evidence the crash is behind
    # the run: a save that reloads, runs a week and crashes again is the
    # recurrence the recovery budget exists to stop, and clearing here would
    # restart it forever. Only a call that moved the clock and ended some other
    # way clears the budget.
    if days > 0 and reason not in ("crashed", "crash_recovered"):
        _RUN["crashRecoveries"] = 0


def _reset_run(keep_macro=False):
    """Forget what belonged to the process or campaign just replaced.

    Called from game_start, campaign_new and load_game. `zeroCalls` describes
    a campaign that is gone, and a stale two has the first advance of the next
    campaign refused for a stall that is not there. `macroOn` describes a
    component in a process that is gone, and a stale true has the next advance
    poll a macro that does not exist, read `activated: false` and stop the run
    pointing at an exception that never happened.

    `keep_macro` is for a save loaded into the SAME process, where the macro
    is a live MonoBehaviour that outlives the load and is still worth watching.

    `crashRecoveries` deliberately survives all of these. It is the one counter
    whose whole job is to outlive a restart: clearing it here would let the
    recovery's own game_start reset the budget it had just spent, and the
    second crash would restart again, forever. Progress clears it instead.
    """
    _RUN["zeroCalls"] = 0
    _RUN["zeroReasons"] = []
    if not keep_macro:
        _RUN["macroOn"] = False


def _forget_entry():
    """Forget the campaign this run was entering, and the save behind it.

    For the two calls that end a process: a launch and a stop. Everything
    `_entered_campaign` records describes a campaign in the process being
    replaced, and `campaignPending` is a promise that the NEXT campaign token
    is the one this run ordered. Left set across a launch, that promise binds
    a save from the dead process to whatever campaign comes up next -- a
    campaign started by hand from the start screen would be recorded as
    playing it, and a crash recovery would reload it, resuming a game nobody
    asked for while reporting a recovery.

    Not folded into `_reset_run`: `note_campaign` calls that with the entry it
    has just bound, and clearing there would drop the binding it exists to
    make.
    """
    _RUN["campaignAt"] = None
    _RUN["campaignPending"] = False
    _RUN["entryProcess"] = None
    _RUN["save"] = None
    _RUN["saveToken"] = None


def _stamp():
    """Now, in the format the DLL reports save mtimes in.

    Both sides read the same clock: the verb answers
    File.GetLastWriteTime formatted as local time, and the bridge is a
    loopback listener, so the game and this process are on one machine. The
    format sorts lexically, which is what the save picker compares on.
    """
    return datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")


def _entered_campaign(save=None, extension=None):
    """Record that this run is entering a campaign, and on what.

    `save` is the save being loaded, None for a campaign started fresh. The
    token is left unbound: the campaign this save is being loaded into has not
    come up yet, and the next token change is the one it belongs to.

    The moment is stamped here rather than when the new campaign is first seen,
    which is up to a poll later. Earlier is the safe direction: everything
    written from now on belongs to this run, and the load itself writes
    nothing.

    The game process is recorded with it. An entry is a promise about the next
    campaign IN THIS GAME, and it is worth nothing in the next one.
    """
    _RUN["campaignAt"] = _stamp()
    _RUN["campaignPending"] = True
    _RUN["entryProcess"] = bridge.last_campaign_process()
    _RUN["save"] = ({"name": save, "extension": extension} if save else None)
    _RUN["saveToken"] = None


def _note_process():
    """Forget the campaign entry when the game it was ordered in is gone.

    `game_start` and `game_stop` forget it for the processes this server
    replaces, and until this ran that was every process it knew about. It is
    not every process there is: a person can close the game at the console and
    start it again, and on the Windows test machine that is the ordinary way it
    gets launched. What that leaves behind is an entry promising that the next
    campaign token is the one this run ordered, plus a save from a game that no
    longer exists -- so a campaign started by hand in the new process is
    recorded as the one the run ordered, and a crash recovery reloads the dead
    run's save into it.

    The token cannot catch this on its own. Its counter restarts at 1 in every
    process, so the first campaign of the replacement game answers a token this
    session may already have seen. The process key is what differs, and it
    differs on the FIRST envelope of the new game, before any campaign is up.

    Either key being None is no information rather than a change: a DLL older
    than the key never sends one, and an entry ordered before this server ever
    read one cannot be dated by it.
    """
    if _RUN["entryProcess"] is None:
        return False
    process = bridge.last_campaign_process()
    if process is None or process == _RUN["entryProcess"]:
        return False
    _forget_entry()
    return True


def note_campaign():
    """React to a campaign this server has not seen before.

    Called before every tool call, because the transition need not have gone
    through a tool: the in-game Options > Exit and a campaign started from the
    start screen both reach a new campaign with no verb of ours involved, and
    every counter kept here would otherwise go on describing a campaign that
    no longer exists.

    `macroOn` is deliberately untouched. The autopilot macro is a live
    MonoBehaviour that survives an in-process load, which is the same reason
    `load_game` resets with `keep_macro=True`; clearing it here would stop the
    advance loop watching a macro that is still running.
    """
    _note_process()
    token = bridge.last_campaign_token()
    # None is no information rather than a campaign of its own: an old DLL
    # sends no token, and the gap between two campaigns is not a third one.
    if token is None or token == _RUN["campaignToken"]:
        return False
    _RUN["campaignToken"] = token
    ordered = _RUN["campaignPending"]
    _RUN["campaignPending"] = False
    if ordered and _RUN["save"] is not None:
        if _RUN["saveToken"] not in (None, token):
            # The save is already bound, and to a campaign that is not this
            # one -- it was written in the campaign the ordered entry replaced,
            # so it says nothing about this one. Binding it here would have a
            # crash recovery reload another campaign's save.
            _RUN["save"] = None
            _RUN["saveToken"] = None
        else:
            # Unbound, so it is the save this entry was ordered with: the
            # campaign it was loaded into is this one.
            _RUN["saveToken"] = token
    elif not ordered:
        # A campaign nothing here ordered, so there is no earlier moment to
        # date it from and no save this run is known to be playing. A save the
        # last campaign was loaded from is no evidence about this one, and
        # reloading it after a crash would silently resume a different game.
        _RUN["campaignAt"] = _stamp()
        _RUN["save"] = None
        _RUN["saveToken"] = None
    # The same reset the lifecycle tools take, rather than a second copy of
    # the fields it clears: keeping the macro is the whole difference, and it
    # is the argument that says so.
    _reset_run(keep_macro=True)
    return True


def _bridge_down(seconds=BRIDGE_DOWN_WAIT):
    """Poll until the bridge stops answering. True when it did.

    A stop is a signal, not a synchronous death, so a process on its way out
    can still answer for a moment.
    """
    deadline = _time.monotonic() + seconds
    while True:
        if not _bridge_up():
            return True
        if _time.monotonic() >= deadline:
            return False
        _time.sleep(1)


def _zero_progress_next(reason):
    """The `next` line for a call that advanced no days, escalating with the
    number of fruitless calls before it. Called after _note_progress, so
    zeroCalls already counts this one."""
    if _RUN["zeroCalls"] < ZERO_PROGRESS_STALLED:
        return ("no game days passed in this call (stopped on %s). One call "
                "moving nothing is ordinary when a decision came up "
                "immediately: deal with the stop reason and call advance "
                "again." % reason)
    return ("no game days passed in this call, and none in the %d before it "
            "(stop reasons: %s): the run appears stalled rather than slow. "
            "Read observe for the blocked flag and its cause, prompts for "
            "what is queued, combat_status for an unresolved fight, and "
            "log_tail which=player for an exception. The %dth fruitless call "
            "in a row is refused rather than re-armed."
            % (_RUN["zeroCalls"] - 1, ", ".join(_RUN["zeroReasons"]),
               ZERO_PROGRESS_REFUSE))


# ---------------------------------------------------------------- helpers

def _bridge_up():
    try:
        bridge.call("ping", timeout=3.0)
        return True
    except (BridgeError, VerbError):
        return False


def _game_pids():
    """Pids whose process NAME is the game's exe, or None when pgrep is not
    usable. Never matches on the command line -- see GAME_COMM."""
    try:
        proc = subprocess.run(["pgrep", GAME_COMM],
                              capture_output=True, text=True)
    except OSError:
        return None
    return [int(p) for p in proc.stdout.split() if p.isdigit()]


def _process_running():
    try:
        if IS_WINDOWS:
            out = subprocess.run(
                ["tasklist", "/FI", "IMAGENAME eq %s" % EXE_NAME, "/NH"],
                capture_output=True, text=True).stdout or ""
            return EXE_NAME.lower() in out.lower()
    except OSError:
        return None
    pids = _game_pids()
    return None if pids is None else bool(pids)


def _try(verb, args=None):
    try:
        return bridge.call(verb, args)
    except (BridgeError, VerbError):
        return None


def _parse_date(value):
    try:
        return datetime.date.fromisoformat(str(value)[:10])
    except ValueError:
        raise ToolError("cannot parse date %r; use YYYY-MM-DD" % (value,))


def _date_of(t):
    if isinstance(t, dict) and t.get("date"):
        return _parse_date(t["date"])
    raise ToolError("query.time returned no date field: %s"
                    % json.dumps(t, default=str)[:200])


# A flag argument that means what it says. The schema declares a boolean and a
# well-behaved client sends one, but the string "false" is TRUE to Python, and
# a switch whose whole job is to turn something off must not silently stay on.
def _flag(args, key, default):
    v = args.get(key, default)
    if isinstance(v, bool):
        return v
    if isinstance(v, str):
        low = v.strip().lower()
        if low in ("true", "1", "yes"):
            return True
        if low in ("false", "0", "no", ""):
            return False
        raise ToolError("%s must be true or false, got %r" % (key, v))
    if v is None:
        return default
    return bool(v)


def _narrative_mode(args, key="narrative_events"):
    """Which of the three modes the caller asked for.

    The boolean vocabulary is the one _flag accepts, because `raw` and older
    clients reach this with strings, and true/false must keep meaning what
    they have always meant. Anything outside both vocabularies is refused
    rather than defaulted: a misspelt "LLM" that silently answered every story
    event with the engine's AI would be undetectable from the digest.
    """
    v = args.get(key, True)
    if v is None:
        return "ai"
    if isinstance(v, bool):
        return "ai" if v else "false"
    if isinstance(v, str):
        low = v.strip().lower()
        if low in ("ai", "llm"):
            return low
        if low in ("true", "1", "yes"):
            return "ai"
        if low in ("false", "0", "no", ""):
            return "false"
    raise ToolError(
        "%s must be true or \"ai\" (the engine's AI answers story events), "
        "\"llm\" (advance stops on one and hands it to you) or false "
        "(attended: nothing answers it), got %r" % (key, v))


# Polls an unanswered narrative prompt gets while its box has NOT rendered.
# Its box is queued behind the notifications in front of it, so the wait is a
# queue draining rather than a decision. This is the bound on a box that never
# arrives at all; at a 2s poll it is about a minute, and it has to stay well
# inside advance's own default 90s budget or the run reports a time-out
# instead of the hold. Ten polls was too short: an ordinary campaign start
# hit it on a healthy tree.
NARRATIVE_BOX_POLLS = 30

# Polls the box gets once it HAS rendered. The wait no longer ends on a
# counter: `alert.choose` is read each poll of a box wait, and a box that is
# open with live option buttons is one the screens pass takes on its next
# pass. So the long leash above stops being spent the moment the box appears,
# and this much shorter one takes over -- a box standing open across several
# polls with its prompt still queued is the controller refusing the press
# (NarrativePressLands false), which is a real hold and has to be reported as
# one rather than waiting out the whole budget.
NARRATIVE_BOX_PRESS_POLLS = 5

# True when everything the dismiss pass left standing is a narrative event.
# Anything else in the skipped list is a real hold and must not borrow the
# box-wait patience.
#
# `require_flag` asks for the bridge's `waitingForBox` as well as the name,
# and it is right only under the mode that presses. There the dismiss pass
# TRIED to answer the prompt and the flag is its report of why it could not,
# so the name alone is not enough: a narrative prompt held back on purpose
# carries the same name and is a decision the caller asked for, and giving
# that one the queue-drain leash spends the patience and then reports that a
# box never came up while the box is standing there answerable.
#
# Under llm and false nothing presses, so the dismiss pass never tries and the
# flag says nothing about the box. See _box_wait.
def _only_narrative(dismissed, require_flag=True):
    if not isinstance(dismissed, dict):
        return False
    skipped = dismissed.get("skipped")
    if not skipped:
        return False
    for entry in skipped:
        if not isinstance(entry, dict):
            return False
        if entry.get("name") != "PromptAddressNarrativeEvent":
            return False
        if require_flag and entry.get("waitingForBox") is not True:
            return False
    return True


# True when the alert box is standing open with at least one live option
# button -- the state in which pressing one answers the event. `open` alone is
# not it: the box comes up before its buttons do, and a box with no live
# option is a plain notification the screens pass closes rather than answers.
def _box_open(alert):
    return (isinstance(alert, dict) and bool(alert.get("open"))
            and bool(alert.get("options")))


# The box is open, has a live option button, AND a press on it would land.
# One place, because the state that ends a box wait is exactly the state the
# llm handover begins on, and the two reading it differently is how a call
# ends on a decision with the box it was waiting for standing open.
#
# `pressLands` absent is a DLL older than the key and is taken at its word.
def _box_answerable(alert):
    return (_box_open(alert)
            and alert.get("pressLands") is not False)


def _box_wait(dismissed, mode, alert):
    """Whether this poll's hold is a narrative box that has not come up yet.

    Under `ai` the dismiss pass tried to answer the prompt and could not, and
    `waitingForBox` is its report of why, so the flag decides.

    Under `llm` and `false` nothing in this server presses an option, so the
    dismiss pass never tries: it marks every standing narrative prompt as
    deliberately left, from the switch alone. A build before this one stamped
    `waitingForBox: false` there whether or not the box was up, and reading
    that as "the decision is standing ready" ended the call on poll two with
    an alert that was not open yet -- a box queued behind two notifications
    was enough. So under those modes the box itself decides, off the reading
    this poll already took.
    """
    if not _only_narrative(dismissed, require_flag=mode == "ai"):
        return False
    if mode == "ai":
        return True
    return not _box_answerable(alert)


def _hold_polls(stuck, waiting_for_box, was_waiting_for_box):
    """How many polls the CURRENT hold has lasted, given the previous count.

    A hold that turns into a box wait, or a box wait that turns into
    something else, is a new hold. Carrying the old count over would shorten
    the box's leash by however many polls something else was blocking, and
    would report those polls as box waits.
    """
    if waiting_for_box != was_waiting_for_box:
        stuck = 0
    return stuck + 1


def _phase_block(t):
    """The mission-phase block off a query.time reply, or None.

    A DLL older than that key returns None here, and an absent block reads
    everywhere below as a closed phase. A server that refused to run against
    an old DLL would be a worse failure than the collision it is guarding.
    """
    block = t.get("missionPhase") if isinstance(t, dict) else None
    return block if isinstance(block, dict) else None


def _phase_decision(t, waits, force=False, engaged=False,
                    polls=MISSION_PHASE_POLLS):
    """Whether advance may hand the clock back on this reading of query.time.

    The collision that corrupts a mission phase is a semimonthly tick landing
    while `phaseActive` is already true, and what produces one is a running
    game clock, not an armed run_until. So while a phase is open this call
    hands the clock back in no form at all: no time.speed, no time.run_until,
    no time.play, and the clock pauses if it is running.

    The hold cannot watch the clock alone, because nothing in the game closes a
    player's mission phase on its own: the dismiss pass is what presses the
    assignment confirmation, and the AI factions finish their planning off the
    game clock. So the caller runs its ordinary loop body on a "hold" and asks
    again next poll.

    `engaged` skips the hold entirely: with ai.control engaged, AiControl's own
    StartNewMissionPhase prefix defers a colliding tick, so the phase is
    already guarded and holding the clock here would only stall the run that
    engagement is driving.

    `waits` is how many polls of this call have already found the phase open.
    Returns (action, waits, phase) with action one of "run", "hold", "stop".
    """
    phase = _phase_block(t)
    if force or engaged or not (phase and phase.get("active")):
        return "run", 0, phase
    waits += 1
    if waits >= polls:
        return "stop", waits, phase
    return "hold", waits, phase


def _note_phase(digest, phase):
    """Put an open mission phase in the digest, whether or not it is held for.

    Two readings return "run" over an open phase: force=true, and an
    ai.control engagement. Both hand the clock back into the phase on purpose,
    so a forced run that causes a collision has the phase in its own report.
    The block recorded is the last reading of this call that found the phase
    open, which makes the key mean "a phase was open during this call" rather
    than "one is open now".
    """
    if isinstance(phase, dict) and phase.get("active"):
        digest["missionPhase"] = phase


def _phase_step(t, digest, phase_waits, armed, force, engaged, target):
    """The phase reading and the clock action both the door and the loop take.

    Returns (holding_phase, phase_waits, armed, action, phase). A hold takes
    the clock down and disarms: a target armed before the phase opened is
    still a clock heading into it, and it is re-armed when the phase closes
    (the DLL answers alreadyArmed and does not touch the park). An arm the
    DLL refused for a phase the reading did not show is a hold too: nothing
    was played, so the poll stays off the clock and the next reading counts
    the waits.
    """
    action, phase_waits, phase = _phase_decision(t, phase_waits, force, engaged)
    _note_phase(digest, phase)
    if action != "run":
        digest["missionPhaseWaits"] = phase_waits
        if action == "stop":
            return True, phase_waits, armed, action, phase
        _hold_clock(t)
        return True, phase_waits, False, action, phase
    if not armed:
        armed, refusal = _start_clock(target, force)
        if refusal == "mission_phase":
            _hold_clock(t)
            return True, phase_waits, armed, action, phase
    return False, phase_waits, armed, action, phase


def _ai_engaged():
    """Whether ai.control holds the player faction. Unknown reads as not.

    Read once at the door of a call. Failing closed is the safe way round: a
    reading nobody could take leaves the phase hold in place, which costs a
    slower call, while failing open would hand the clock back into an open
    phase, which costs the phase.
    """
    reply = _try("ai.control", {"action": "status"})
    return bool(reply.get("engaged")) if isinstance(reply, dict) else False


def _start_clock(target, force):
    """Full speed, the target armed, the clock running. Returns
    (armed, refusal).

    The three calls belong together: each of them alone is a half-resumed
    clock, and every place that resumes after a mission-phase hold has to do
    all three or the run sits at whatever speed the hold left behind.

    The one exception is the refusal this loop can lose a race to. A phase
    that opened in the frames between the reading and the arm makes the DLL
    refuse the target, and playing the clock afterwards would hand the game
    exactly what the refusal withheld -- a running clock heading for the next
    semimonthly tick with the phase open. So a mission-phase refusal stops
    before the play and is reported to the caller, which turns the rest of the
    poll into a hold. Every other failed arm still plays: those are transport
    failures with no phase behind them, and a clock left down for one would
    stall the run for nothing.

    The arm goes FIRST for the same reason. time.speed 5 is handing the clock
    back on its own -- the game runs at full speed with or without a target --
    so sending it before the arm meant a refused arm had already done the
    thing the refusal exists to prevent, and the hold that followed was a hold
    over a clock this call had just started.
    """
    armed, refusal = _arm_run_until(target.isoformat(), force)
    if refusal == "mission_phase":
        return False, refusal
    _try("time.speed", {"level": 5})
    _try("time.play")
    return armed, None


def _hold_clock(t):
    """Take the clock down for a mission phase. True when it was stopped here.

    Not merely declining to resume. A phase can open on a clock that is already
    running -- the previous advance call armed and left it running, or the
    caller did -- and that clock is the collision itself. Reading `paused` off
    the poll that is already in hand keeps the ordinary held poll to no extra
    verb at all.
    """
    if isinstance(t, dict) and t.get("paused"):
        return False
    return _try("time.pause") is not None


def _arm_run_until(target, force):
    """Arm run_until at `target`. Returns (armed, refusal).

    Tolerant on purpose. The DLL refuses a new target while a mission phase is
    open, and that is a race this loop can lose: the phase can open in the
    frames between the reading and the call. A refusal leaves the run unarmed
    and the next poll asks again, which is what the wait does anyway, while
    raising here would unwind the whole call and take the digest with it.

    `refusal` is "mission_phase" for that one, and None for everything else,
    because the caller has to treat them differently: a phase refusal means
    the clock must stay down, and a bridge failure means nothing about the
    clock at all. The DLL states the reason as the first word of its error
    message (Verbs.TimeRunUntil).
    """
    try:
        reply = bridge.call("time.run_until", {"date": target, "force": force})
    except VerbError as e:
        return False, ("mission_phase"
                       if str(e).startswith("mission_phase") else None)
    except BridgeError:
        return False, None
    return isinstance(reply, dict) and bool(reply.get("armed")), None


def _armed_note(armed):
    """What the budget messages may claim about the target.

    A call that spent its budget waiting on a mission phase never armed
    anything, and must not tell the caller a stop is waiting at a date the
    game will run straight past.
    """
    if armed:
        return "run_until stays armed"
    return ("run_until is NOT armed and the clock is HELD PAUSED -- a mission "
            "phase was open, and this call never saw it close")


def _phase_next(target, phase):
    """The `next` line for a run that gave up holding for a mission phase."""
    collisions = phase.get("collisions") if isinstance(phase, dict) else None
    return (
        "a mission phase has been open for %d polls and nothing here could "
        "close it, so the clock was never handed back and is left paused: a "
        "semimonthly tick landing inside an open phase is what corrupts it. "
        "The phase ends "
        "when every faction has signalled its assignments, so read prompts "
        "for a mission-assignment confirmation still queued and "
        "log_tail which=game for a planning exception. advance force=true "
        "with until=%s runs anyway and accepts the collision.%s"
        % (MISSION_PHASE_POLLS, target,
           "" if not collisions
           else (" The engine has already logged %d such collision(s) in "
                 "this campaign." % collisions)))


def _stop_next(waiting_for_box, polls, rendered=False, attended=False):
    """The `next` line for a run that stopped on a decision.

    `polls` is the length of this hold, which is what the sentence is about.
    The run-total box-wait counter is a different number and spans regimes,
    so printing it here would report waits that belong to an earlier hold.

    `rendered` splits the box wait in two, because the two are different
    failures with different next steps: a box that never came up is a queue
    that never drained, and a box standing open whose press does not land is
    a controller not taking narrative input.
    """
    if waiting_for_box and rendered:
        return ("a narrative event's alert box is OPEN with live option "
                "buttons and its prompt is still queued after %d polls, so "
                "the automatic press is not landing -- the controller "
                "refuses one until it is taking narrative input. Read "
                "alert_choose for the text and options and press one "
                "yourself, then call advance again" % polls)
    if waiting_for_box:
        return ("a narrative event's alert box never came up over %d polls, "
                "so nothing could answer it -- read alert_choose and press "
                "an option, then call advance again. It cannot be dropped: "
                "removing the prompt leaves the box live, so the option is "
                "applied anyway a poll or two later, with its costs and "
                "grants" % polls)
    text = ("a decision blocks the clock -- read the alert text and options "
            "and answer with alert_choose option=N, then call advance again. "
            "prompts mode=drop type=<name> forfeits an ordinary prompt and "
            "does nothing to a narrative one: an answerable narrative event "
            "is answered through its own alert box, and an unanswerable one "
            "(its event template gone, or its target killed) is dropped by "
            "the prompt pass on its own")
    if attended:
        # narrative_events=false is the attended mode: nothing in this server
        # will ever press an option, however many times it is called. Said
        # here because the sentence above reads like advice a later call could
        # act on, and under this mode no later call can.
        text += (". This run was started with narrative_events=false, so no "
                 "call will ever answer a story event for you -- narrative "
                 "events=llm is the unattended handover, where advance stops "
                 "on the box and hands you the options")
    return text


def _narrative_next(alert, target):
    """The `next` line for an llm handover.

    Names the options, because the whole mode exists so the caller can pick
    one, and a stop that made it read alert_choose to learn what it stopped on
    would cost a round trip for something already in hand.
    """
    options = alert.get("options") if isinstance(alert, dict) else None
    listed = ", ".join(
        "%s=%s" % (o.get("option"), o.get("label") or "?")
        for o in options if isinstance(o, dict)) if options else ""
    return ("a story event's alert box is open and narrative_events=llm "
            "hands it to you: nothing here pressed anything. The options are "
            "in `alert`%s. Answer with alert_choose option=N -- "
            "alert_choose detail=true first if the outcomes matter -- then "
            "call advance again with until=%s. The event cannot be dropped: "
            "removing the prompt leaves the box live, so the option is "
            "applied anyway a poll or two later, with its costs and grants"
            % ((" (%s)" % listed) if listed else "", target))


def _drop_note(answered):
    """The narrativeNotes line for one dropped narrative prompt.

    Named by its event. `via` is a constant sentence, so a note built from it
    first read identically for every drop in a run, and the dedupe collapsed
    them all to one line while promptsAnswered counted each. The dataName is
    the only field that tells two drops apart.
    """
    event = answered.get("event")
    reason = answered.get("deadEnd") or answered.get("via")
    note = ("narrative prompt dropped with no option applied: %s"
            % (event if event else "event unnamed"))
    if reason:
        note += " (%s)" % reason
    return note

def _campaign_loaded():
    try:
        bridge.call("query.time")
        return True
    except VerbError:
        return False


def _wait_secs(value, default=CAMPAIGN_WAIT):
    """Seconds to wait, from an argument that may be absent or a flag.

    Only None is absent. A boolean is a client saying "wait" rather than a
    number of seconds, so it takes the default too -- int(True) is a
    one-second wait, which is a wait that always fails. Everything else is the
    caller's own number, including 0. Tested with isinstance: bool is an int
    in Python, so `value in (None, True, False)` would swallow 0 and 1.
    """
    if value is None or isinstance(value, bool):
        return default
    return int(value)


def _wait_campaign(seconds, progress=None):
    """Poll until query.time answers. BridgeError is expected mid-load (the
    session tears down); keep polling through it."""
    t0 = _time.monotonic()
    while _time.monotonic() - t0 < seconds:
        _time.sleep(5)
        if progress:
            progress(int(_time.monotonic() - t0), seconds,
                     "waiting for campaign")
        try:
            return bridge.call("query.time")
        except (BridgeError, VerbError):
            continue
    raise ToolError("no campaign after %ds -- observe for state, "
                    "log_tail which=player for errors" % seconds)


def _saves_hint():
    saves = _try("saves.list")
    if saves is None:
        return ""
    text = json.dumps(saves, separators=(",", ":"), default=str)
    return "; saves: " + (text[:2000] + "..." if len(text) > 2000 else text)


# ---------------------------------------------------------------- observe

def observe(args, progress=None):
    """Session status, plus the one condition that invalidates the rest of it.

    A server running replaced code answers every later question from the old
    code, and observe is the call whose reading everything downstream is
    judged against, so the drift is reported here as well as in selftest. The
    key exists only when the code has drifted, which leaves the ordinary
    reply exactly the size it was.
    """
    report = _observe(args, progress)
    out = {}
    # First key, so a stalled clock is the first thing read. _observe has just
    # called query.time, so its stall block is this second's.
    line, record = _pause_line(report)
    if line:
        out["pauseLimit"] = line
        if record:
            out["pauseLimitRecord"] = record
    bad = codestate.stale()
    if bad:
        out["serverCode"] = bad
    out.update(report)
    # Always carried, counters and limit both: a run that sat stopped for
    # twenty minutes and then moved on leaves nothing else behind, and this is
    # the reading an orchestrator takes after the run rather than during it. A
    # run that raised or disabled the limit cannot hide that either.
    out["stallSession"] = stall_counters()
    return out


def _pause_line(report):
    """(line, record) for observe. The line is None when nothing is wrong.

    Silent while the clock is healthy. Past half the limit it warns, because
    that is when a run can still fix itself; past the limit it opens with the
    same words a refusal does, so the two read as one condition.
    """
    if not (isinstance(report, dict) and report.get("campaign")):
        # No campaign, or the bridge is down. There is no clock to be stopped,
        # and a reading left over from the campaign before this one would
        # report a stall against a game that is not running.
        return None, None
    t = report.get("time")
    block = _stall_block(t)
    stall = block.get("seconds") if isinstance(block, dict) else None
    if stall is None:
        stall, _age = bridge.last_stall()
    limit = pause_limit()
    record = note_reading(stall, block, limit)
    if not limit or stall is None:
        return None, None
    if stall > limit:
        prompt = record.get("prompt") if isinstance(record, dict) else None
        return pause_banner(stall, block, limit, prompt), record
    if stall > limit / 2.0:
        return ("the game clock has not moved for %d s of a %d s limit. Past "
                "the limit every state-changing tool is refused. Move it with "
                "advance, or clear what blocks it with prompts, alert_choose "
                "or combat_autoresolve."
                % (int(stall), int(limit))), None
    return None, None


def pause_limit_tool(args, progress=None):
    """Read the pause limit and this session's stall counters.

    Read-only, and the write lives in set_pause_limit below. One tool that did
    both had to be marked a write, so reading the limit was itself refused
    over a stalled clock -- the one reading an agent takes to find out why
    everything else is being refused.
    """
    out = stall_counters()
    out["note"] = ("set_pause_limit seconds=N changes it, 0 disables it; "
                   + SESSION_STATE_NOTE)
    return out


SESSION_STATE_NOTE = ("the limit is server session state: it lasts until "
                      "this server process exits and is written nowhere.")


def set_pause_limit_tool(args, progress=None):
    """Set the pause limit for the rest of this server session."""
    seconds = args.get("seconds")
    if seconds is None:
        raise ToolError("set_pause_limit needs seconds=N (0 disables the "
                        "limit); pause_limit reads the current one")
    set_pause_limit(seconds, record=True)
    out = stall_counters()
    out["note"] = (SESSION_STATE_NOTE + " Every observe reports it, so a run "
                   "that changed it cannot hide that it did.")
    return out


def _observe(args, progress=None):
    if not _bridge_up():
        running = _process_running()
        if running:
            nxt = ("the game process is up but the bridge is not answering -- "
                   "launch usually takes 20-60s, and a cold start can take up "
                   "to %ds; retry observe, then log_tail which=player "
                   "pattern='\\[Manager\\]' if it never comes up"
                   % LAUNCH_WAIT)
        else:
            nxt = "game is not running -- call game_start"
        return {"bridge": "down", "processRunning": running, "next": nxt}

    report = {"bridge": "up"}
    report["version"] = _try("version")
    try:
        t = bridge.call("query.time")
    except VerbError:
        report["campaign"] = False
        report["next"] = (
            "no campaign loaded (main menu) -- load_game a save or "
            "campaign_new to play; template/localize/modcheck work from here "
            "and see the un-resolved all-scenario union")
        return report
    except BridgeError as e:
        report["bridge"] = ("lost mid-call (%s) -- the game may be loading; "
                            "retry observe" % e)
        return report

    report["campaign"] = True
    report["time"] = t
    if _crashed(t):
        report["crashed"] = True
        report["next"] = (
            "the game has hit its crash handler: an unhandled exception in "
            "game code set GameControl.handlingException, which froze the "
            "clock, cleared every event listener and turned input off. The "
            "bridge still answers, so everything below is a reading off a "
            "dead game. Loading a save in this process does NOT clear the "
            "flag and comes back permanently degraded -- game_stop, "
            "game_start load=<save>. advance does that on its own, once. "
            "log_tail which=player has the exception.")
        return report

    scenario = _try("query.scenario")
    if isinstance(scenario, dict):
        report["scenario"] = {k: scenario.get(k)
                              for k in ("dataName", "resolved")
                              if scenario.get(k) is not None}

    prompts_data = _try("prompts.list")
    items = prompts_data.get("prompts") if isinstance(prompts_data, dict) \
        else None
    if isinstance(items, list):
        report["pendingPrompts"] = [
            p.get("name") or p.get("type") or str(p)[:40]
            for p in items if isinstance(p, dict)][:15]

    combat = _try("combat.status")
    if isinstance(combat, dict):
        summary = {k: combat.get(k)
                   for k in ("active", "pending", "blocked")
                   if combat.get(k)}
        # The autoresolve machine's status dict always exists; it is only
        # worth reporting when armed or holding an error.
        ar = combat.get("autoresolve")
        if isinstance(ar, dict) and (ar.get("armed") or ar.get("error")):
            summary["autoresolve"] = ar
        if summary:
            report["combat"] = summary

    factions = _try("query.factions")
    if isinstance(factions, list):
        for f in factions:
            if isinstance(f, dict) and f.get("activePlayer"):
                report["playerFaction"] = f
                break

    if isinstance(t, dict) and t.get("blocked"):
        if report.get("pendingPrompts"):
            cause = ("pending prompts freeze the clock -- advance handles "
                     "them, or prompts/alert_choose manually")
        elif report.get("combat"):
            cause = ("unresolved combat freezes the clock -- "
                     "combat_autoresolve, or leave it for a human")
        else:
            cause = ("a modal screen or alert -- alert_choose reports the "
                     "open alert")
        report["blockedCause"] = cause
    # Reported even when the clock is running, because the flag can be read
    # before the pause lands and because a null means the read itself failed.
    if isinstance(t, dict) and "crashed" in t:
        report.setdefault("crashed", t["crashed"])
    return report


# ---------------------------------------------------------------- lifecycle

def game_start(args, progress=None):
    load = args.get("load")
    # A launch or a load replaces the process or the campaign the run counters
    # describe, so they are dropped up front rather than at whichever exit is
    # taken.
    #
    # The macro is the exception, and it is decided by whether a process is
    # about to be replaced. A launch kills the MonoBehaviour with the process.
    # A call that launches nothing does not: the no-op branch is a caller
    # re-confirming the game is there, and a load= into a running game is the
    # same in-process load `load_game` does, after which the macro is still
    # running. Clearing the flag on those stopped `advance` polling
    # query.autopilot, and a macro that had switched itself off then went
    # unnoticed for the rest of the run.
    already_up = _bridge_up()
    _reset_run(keep_macro=already_up)
    out = {"launched": False}
    if already_up:
        out["bridge"] = "up (already running)"
    else:
        # A new process. Whatever campaign the last one was entering never
        # arrived, and the save behind it belongs to a game that is gone.
        _forget_entry()
        try:
            if IS_WINDOWS:
                # The Steam URL protocol; the shell association reaches Steam
                # without needing steam.exe on PATH.
                os.startfile("steam://rungameid/" + APPID)
            else:
                # start_new_session detaches the game from this server's
                # session and process group, so it survives the server exiting
                # -- what setsid was here for, without the extra binary.
                subprocess.Popen(["steam", "-applaunch", APPID],
                                 stdout=subprocess.DEVNULL,
                                 stderr=subprocess.DEVNULL,
                                 start_new_session=True)
        except OSError as e:
            raise ToolError("could not launch: %s (%s)" % (
                e, "is Steam installed?" if IS_WINDOWS
                else "is steam on PATH?"))
        out["launched"] = True
        t0 = _time.monotonic()
        up = False
        while _time.monotonic() - t0 < LAUNCH_WAIT:
            _time.sleep(5)
            if progress:
                progress(int(_time.monotonic() - t0), LAUNCH_WAIT,
                         "waiting for bridge (usually up in 20-60s; waiting "
                         "up to %ds before giving up)" % LAUNCH_WAIT)
            if _bridge_up():
                up = True
                break
        if not up:
            raise ToolError(
                "bridge not up after %ds -- launch usually answers in 20-60s "
                "and this is the full budget, so something is wrong rather "
                "than slow: retry observe, and check log_tail which=player "
                "pattern='\\[Manager\\]' for a failed injection" % LAUNCH_WAIT)
        out["bridge"] = "up"
        out["bridgeUpAfterSeconds"] = round(_time.monotonic() - t0)
    if load:
        load_game({"name": load, "extension": args.get("extension")})
        out["load"] = load
    if load or args.get("wait_campaign"):
        out["campaign"] = _wait_campaign(_wait_secs(args.get("wait_campaign")),
                                         progress)
    return out


def game_stop(args, progress=None):
    # The campaign goes with the process, whether or not the kill below finds
    # anything to kill: a run that ordered a load and then stopped the game
    # must not have that save bound to the next campaign anyone starts.
    _forget_entry()
    if IS_WINDOWS:
        try:
            proc = subprocess.run(["taskkill", "/IM", EXE_NAME, "/F"],
                                  capture_output=True, text=True)
        except OSError as e:
            raise ToolError("taskkill failed: %s" % e)
        return {"stopped": proc.returncode == 0,
                "taskkillExit": proc.returncode,
                "note": "exit 128 means no such process was running"}
    pids = _game_pids()
    if pids is None:
        raise ToolError("pgrep is not available, so the game process cannot "
                        "be found; kill it by hand")
    if pids:
        killed, failed = [], []
        for pid in pids:
            try:
                os.kill(pid, signal.SIGTERM)
                killed.append(pid)
            except OSError as e:
                failed.append("%d (%s)" % (pid, e))
        # Only the game is signalled, so a survivor of SIGTERM is escalated
        # here.
        deadline = _time.monotonic() + 10
        while _time.monotonic() < deadline and _game_pids():
            _time.sleep(0.5)
        forced = []
        for pid in _game_pids() or []:
            try:
                os.kill(pid, signal.SIGKILL)
                forced.append(pid)
            except OSError as e:
                failed.append("%d (%s)" % (pid, e))
        if forced:
            _time.sleep(1)      # let the parent reap before the last look
        remaining = _game_pids() or []
        out = {"stopped": bool(killed) and not remaining, "killed": killed,
               "matchedBy": "process name"}
        if forced:
            out["forceKilled"] = forced
        if remaining:
            out["remaining"] = remaining
        if failed:
            out["failed"] = failed
        return out
    return {"stopped": False, "matchedBy": "process name",
            "note": "no process named %s -- the game is not running"
                    % EXE_NAME}


# --------------------------------------------------- crash fixture (testing)

# The literal crash_the_game demands, matching the DLL's own. Spelled out
# rather than a boolean so no client's default-filling can supply it by
# accident.
CRASH_CONFIRM = "crash-the-game"


def crash_the_game(args, progress=None):
    """Put the game into its own crash state deliberately. The DLL checks
    `confirm` and refuses before touching anything."""
    return bridge.call("test.crash_the_game", args)


# ---------------------------------------------------------------- advance

def _crashed(t):
    """True when the game has hit its crash handler.

    GlobalInstaller.HandleException sets GameControl.handlingException before
    anything else, shows the crash panel, calls PauseAndBlock, clears every
    event listener and stops accepting input. The bridge survives all of it,
    so every verb keeps answering over a dead game and the frozen clock reads
    exactly like a modal alert. The flag is the only thing that tells them
    apart, so it is never inferred from a blocked clock with no prompts.
    """
    return isinstance(t, dict) and t.get("crashed") is True


def _save_rows():
    """Every save the DLL lists, as {name, extension, mtime} rows."""
    saves = _try("saves.list")
    if not isinstance(saves, list):
        return []
    return [s for s in saves if isinstance(s, dict) and s.get("name")]


def _engine_written(name):
    """Whether the game writes this save on its own.

    The names it uses are Autosave, Autosave2, Autosave3, CombatAutosave and
    ExitSave, so the test is a substring rather than a list that a numbered
    slot would fall out of. A player save that happens to match costs nothing:
    the picker also requires an mtime inside this run's campaign, and a file
    written during the run IS of the run whatever it is called.
    """
    low = str(name).lower()
    return "autosave" in low or low.startswith("exitsave")


def _run_save():
    """The newest save of THIS run, or None.

    Never simply the newest save on disk. The folder holds other people's
    campaigns and other runs of this harness, and reloading one of those after
    a crash resumes a game nobody asked for while reporting a recovery.

    Two things qualify. Anything the game wrote by itself after this run
    entered its campaign is of that campaign by construction. So is the save
    the run loaded, which is the fallback when the crash came before the first
    autosave. The newest of the two wins, on the mtime the DLL reports.
    """
    rows = _save_rows()
    if not rows:
        return None
    candidates = []
    entered = _RUN["campaignAt"]
    if entered:
        candidates = [r for r in rows if _engine_written(r["name"])
                      and str(r.get("mtime") or "") >= entered]
    loaded = _RUN["save"]
    if loaded:
        # By name AND extension when the extension is known, which is the only
        # way to name one of two twins. With it unknown, every twin of the stem
        # is a candidate and the newest of them wins below -- the same answer
        # the DLL's own bare-name search gives.
        candidates += [r for r in rows if r["name"] == loaded["name"]
                       and (loaded.get("extension") is None
                            or r.get("extension") == loaded["extension"])]
    if not candidates:
        return None
    # Ties fall back to name order, so the pick is deterministic.
    candidates.sort(key=lambda s: (str(s.get("mtime") or ""), str(s["name"])))
    return candidates[-1]


def _recover_from_crash(digest, at, progress=None):
    """Restart the game and reload the newest save. Returns the stop reason.

    Recovery has to be a process restart. Loading a save inside the same
    process restores the clock, the input manager and the event graph, but it
    does not clear handlingException: that flag has one writer in the whole
    assembly, no clearer, and it survives a scene load, so a reloaded game in
    the same process is permanently degraded. The crash dialog itself offers
    no way back either -- quit, open the save folder, open the log folder, two
    support links.
    """
    digest["crash"] = {"detectedAt": at.isoformat()}
    # The save is looked up before the budget so the terminal text can name
    # the blocker that is actually in the way. The budget survives campaign
    # changes and is spent by an attempt rather than by a completed restart,
    # so "crashed again after an automatic restart" is not true of every run
    # that reaches it, and a run with nothing to reload is told the one thing
    # it can act on.
    save = _run_save()
    if save is None:
        digest["crash"]["recovered"] = False
        digest["next"] = (
            "the game crashed and there is no save OF THIS RUN to reload, so "
            "nothing was restarted: no autosave has been written since the "
            "campaign was entered, and the run did not come from a save. The "
            "newest save on disk is not a substitute -- it can belong to "
            "another campaign entirely, and reloading it would resume a game "
            "nobody asked for while reporting a recovery. A fresh campaign is "
            "never started automatically for the same reason. Read log_tail "
            "which=player for the exception, then load_game name=<save> to "
            "choose one by hand.")
        return "crashed"

    if _RUN["crashRecoveries"] >= MAX_CRASH_RECOVERIES:
        digest["crash"]["recovered"] = False
        digest["next"] = (
            "the game crashed again with this run's one automatic restart "
            "already spent, so this one was not retried: a crash that recurs "
            "on reload would otherwise become a restart loop. Read log_tail "
            "which=player for the exception, then decide by hand. game_start "
            "load=<save> resumes once the cause is understood.")
        return "crashed"

    _RUN["crashRecoveries"] += 1
    digest["crash"]["save"] = save.get("name")
    digest["crash"]["saveWritten"] = save.get("mtime")
    # What the restart costs: everything between the save and where the run
    # had got to. Named in the digest because nothing else records it.
    digest["crash"]["gameTimeLost"] = (
        "the campaign had reached %s; the reloaded save is whatever state it "
        "was written in" % at.isoformat())
    if progress:
        progress(0, 1, "crash detected -- restarting and reloading %s"
                 % save.get("name"))
    try:
        digest["crash"]["stop"] = game_stop({}, None)
        # The stop has to be CONFIRMED, not assumed. game_stop reports
        # stopped: False on a leftover pid or a kill that failed, and the old
        # code discarded that: game_start would then find the bridge already
        # up, report "already running", never launch, and load the save into
        # the crashed process -- which comes back permanently degraded, while
        # this function reported a successful recovery and told the caller to
        # carry on.
        if not _bridge_down():
            digest["crash"]["recovered"] = False
            digest["next"] = (
                "the game crashed and the process would not die: the bridge "
                "was still answering %ds after game_stop, so nothing was "
                "restarted. Loading a save into the crashed process is not a "
                "recovery -- handlingException has no clearer and survives a "
                "scene load, so the reloaded game comes back degraded. Kill "
                "it by hand, then game_start load=<save>." % BRIDGE_DOWN_WAIT)
            return "crashed"
        # The extension goes with the name: with both twins of a stem on
        # disk, the profile setting decides which one a bare name resolves
        # to, and that need not be the file whose mtime was just read.
        game_start({"load": save.get("name"),
                    "extension": save.get("extension")}, progress)
    except (ToolError, BridgeError, VerbError) as e:
        # Widened past ToolError: the save load inside game_start reaches the
        # bridge, and a transport failure there would otherwise unwind the
        # whole advance call and take the digest and this crash record with it.
        digest["crash"]["recovered"] = False
        digest["next"] = ("the game crashed and the restart failed: %s. Read "
                          "log_tail which=player, then game_start "
                          "load=<save> by hand." % e)
        return "crashed"
    digest["crash"]["recovered"] = True
    # A new process carries no macro. Leaving the flag set would have the next
    # call poll a switch nobody threw and stop on it.
    _RUN["macroOn"] = False
    digest["next"] = (
        "the game crashed, was restarted, and %s was reloaded. run_until did "
        "not survive the restart, so call advance again with until=%s to "
        "continue." % (save.get("name"), digest["targetDate"]))
    return "crash_recovered"


def _dismiss_refusal(dismissed):
    """(stopReason, next) when prompts.dismiss refused the pass, else None.

    The reply is ok:false with a `reason` and a `text` written for the caller.
    The text is used as `next` verbatim rather than reworded here: the DLL
    knows what it refused and what the way out is, and a second wording of it
    in this file is one more thing to keep in step.
    """
    if not isinstance(dismissed, dict) or dismissed.get("ok") is not False:
        return None
    reason = dismissed.get("reason")
    # A reason this server does not know still stops, under the generic name.
    stop = "active_player_moved" if reason == "activePlayerMoved" \
        else "prompts_refused"
    return stop, dismissed.get("text")


def _remaining(dismissed):
    """(count, known). A count that could not be read is not a count of zero.

    Three paths leave it unreadable: the dismiss call raised, the reply was
    not a dictionary, or the queue refused. None of them is a clear queue.
    """
    if not isinstance(dismissed, dict) or "remaining" not in dismissed:
        return None, False
    return dismissed.get("remaining"), True


def _combat_id(status, autoresolve):
    """The id of the combat this poll is about, or None."""
    for key in ("active", "pending"):
        value = status.get(key) if isinstance(status, dict) else None
        if isinstance(value, list):
            value = value[0] if value else None
        if isinstance(value, dict) and value.get("id") is not None:
            return value["id"]
    for key in ("combat", "attemptedCombat"):
        if isinstance(autoresolve, dict) and autoresolve.get(key) is not None:
            return autoresolve[key]
    return None


def advance(args, progress=None):
    policy = args.get("answer_policy", "neutral")
    if policy not in ("neutral", "all"):
        raise ToolError("answer_policy must be 'neutral' (default) or 'all' "
                        "(force-drop undecided prompts)")
    autoresolve = _flag(args, "autoresolve", True)
    # Defaults to answering. `llm` ends the call on the open box so the caller
    # answers it; `false` is the attended case, where nobody here is going to.
    narrative_mode = _narrative_mode(args)
    force = _flag(args, "force", False)
    max_seconds = float(args.get("max_seconds", 90))

    # The refusal problem 2 exists for. A run whose calls stop moving days is
    # told to call again by every digest it gets, so the stop has to live
    # here, at the door, rather than in the message.
    if force:
        _RUN["zeroCalls"] = 0
        _RUN["zeroReasons"] = []
    elif _RUN["zeroCalls"] >= ZERO_PROGRESS_REFUSE - 1:
        raise ToolError(
            "the last %d advance calls each moved zero game days (stop "
            "reasons: %s), so this one is refused rather than re-armed: "
            "calling again is what the digests have been asking for and it "
            "has not worked. Diagnose first -- observe for the blocked flag "
            "and its cause, prompts for what is queued, combat_status for an "
            "unresolved fight, log_tail which=player for an exception. "
            "advance force=true runs anyway once the cause is understood."
            % (_RUN["zeroCalls"], ", ".join(_RUN["zeroReasons"])))

    t = bridge.call("query.time")
    start = _date_of(t)
    if args.get("until"):
        target = _parse_date(args["until"])
    elif args.get("days"):
        target = start + datetime.timedelta(days=int(args["days"]))
    else:
        raise ToolError("advance needs until=YYYY-MM-DD or days=N")
    if target <= start:
        raise ToolError("target %s is not after the current date %s"
                        % (target, start))

    digest = {"startDate": start.isoformat(),
              "targetDate": target.isoformat(),
              "promptsAnswered": 0, "screensClosed": 0,
              "combatsAutoresolved": 0, "dismissErrors": [],
              # Every story event this run answered on the caller's behalf,
              # with the option it took. Present even when empty: an absent key
              # and "none fired" would otherwise read the same.
              "narrativeEvents": [],
              # Everything about a narrative event that is not a clean answer.
              # Four lines reach it. Three come from the bridge's screens
              # pass: an event left standing under narrative_events=false, an
              # option pressed with no readable event behind it, and an event
              # whose target was already gone when the box was pressed, so the
              # engine applied nothing. The fourth is composed here, from a
              # prompt the bridge dropped because nothing could ever answer
              # it. screensClosed is a count and can carry none of them.
              "narrativeNotes": []}
    total_days = (target - start).days

    if _crashed(t):
        digest["stopReason"] = _recover_from_crash(digest, start, progress)
        return _finish(digest, start, start)

    # Read once, at the door: an engagement does not come and go inside one
    # call, and asking every poll would cost a verb for an answer that cannot
    # change. With ai.control engaged the DLL's own prefix guards the phase,
    # so the hold below is skipped entirely.
    engaged = _ai_engaged()
    # The clock is conditional: a mission phase open at the door is one this
    # call holds for rather than runs into. `armed` stays false, and nothing
    # hands the clock back, until the phase reads closed.
    holding_phase, phase_waits, armed, _action, _phase = _phase_step(
        t, digest, 0, False, force, engaged, target)

    t0 = _time.monotonic()
    stuck = 0
    unknown_polls = 0
    # Consecutive clock reads that went unanswered. One is a busy main thread
    # and is retried; a run of them is a bridge nothing is going to answer,
    # and polling on to the budget over it reports a dead game as a slow one.
    lost_polls = 0
    # Polls of this call that got a clock reading, for the pause limit.
    polls = 0
    # Polls of THIS hold on which the alert box was open with live options.
    box_polls = 0
    was_waiting_for_box = False
    combat_arms = set()
    # Whether the previous poll found a combat. The pause limit reads it: a
    # combat this call did not arm is still a combat holding the clock.
    in_combat = False
    cur = start
    # Every exit that has a digest to report goes through _finish, a
    # transport failure included: the digest and the call count survive it.
    # BridgeTimeout is handled inside the loop as one lost poll and never
    # reaches here.
    try:
        while True:
            _time.sleep(2)
            # A bridge call times out when the game's MAIN THREAD stalls past
            # the 30s budget -- a large save, an asset load, a scene change.
            # The game is up; this poll is lost and the next one is taken,
            # exactly as the campaign wait already does. Treating it as the
            # call's death reported a running game as down and sent the caller
            # to relaunch it.
            try:
                t = bridge.call("query.time")
            except BridgeTimeout as e:
                digest["lostPolls"] = digest.get("lostPolls", 0) + 1
                digest["lastLostPoll"] = str(e)
                lost_polls += 1
                if lost_polls >= LOST_POLLS_MAX:
                    digest["stopReason"] = "bridge_lost"
                    digest["bridgeError"] = str(e)
                    digest["next"] = (
                        "%d clock reads in a row went unanswered (%s), so "
                        "this call stopped rather than spend its budget on a "
                        "bridge that is not answering. Each of them waited "
                        "the full verb timeout, so the game has been silent "
                        "for minutes rather than seconds and nothing here "
                        "knows what it is doing. observe reports whether the "
                        "process is still there: with it up the main thread "
                        "is busy and advance can be called again, and with it "
                        "gone game_start relaunches."
                        % (lost_polls, e))
                    break
                if _time.monotonic() - t0 > max_seconds:
                    digest["stopReason"] = "max_seconds"
                    digest["next"] = (
                        "time budget hit at %s with the last %d poll(s) "
                        "lost to a busy main thread; the game is up and %s "
                        "-- call advance again with until=%s"
                        % (cur.isoformat(), digest["lostPolls"],
                           _armed_note(armed), target.isoformat()))
                    break
                continue
            lost_polls = 0
            previous = cur
            cur = _date_of(t)
            if cur > previous:
                # The clock is gaining time, so whatever the phase block says,
                # this call is not stuck behind a phase. The count is polls
                # spent with the clock held down, and a poll that moved the
                # date is not one of those -- counting them stopped runs that
                # were working, on a bound meant for runs that were not.
                phase_waits = 0
            if progress:
                progress((cur - start).days, total_days,
                         "at %s" % cur.isoformat())
            # Ahead of every other reading: after a crash the clock is blocked,
            # the prompt queue is empty and input is off, which is the exact
            # shape of a modal alert nothing can answer.
            if _crashed(t):
                digest["stopReason"] = _recover_from_crash(
                    digest, cur, progress)
                break
            if cur >= target:
                digest["stopReason"] = "reached"
                break
            # Every poll, not only the polls with nothing armed: a phase can
            # open on a call that armed and started the clock a minute ago, and
            # the resume at the bottom of this loop would then hand the clock
            # straight back into it. The reading is the one already in hand, so
            # asking costs no verb.
            #
            # Ahead of the blocked/paused gate too, because a call holding for
            # a phase has stopped the clock on purpose and would read below as
            # an ordinary pause to resume out of. The loop body still runs on a
            # hold: the dismiss pass presses the mission-assignment
            # confirmation, which is what closes a player's phase, and the AI
            # factions finish their planning off the game clock -- so a hold
            # that only watched would never end.
            #
            # Ahead of the pause limit as well, and that is the order that
            # matters most. `holding_phase` is one of the flags the limit
            # treats as a hold it must not end, so a phase that first opens on
            # a poll past the limit has to be seen here BEFORE the limit is
            # tested -- read a poll late, the call stops on `pause_limit` with
            # a `next` telling the caller to force=true through the collision
            # the hold exists to prevent.
            holding_phase, phase_waits, armed, action, phase = _phase_step(
                t, digest, phase_waits, armed, force, engaged, target)
            if action == "stop":
                digest["stopReason"] = "mission_phase"
                digest["next"] = _phase_next(target.isoformat(), phase)
                break
            # The pause limit, off the reading this poll already took. Not at
            # the door and not on the first poll: advance is the tool that
            # clears a stall, so it gets a full pass of the loop body -- the
            # dismiss pass, the autoresolve, the resume -- before the limit is
            # allowed to end the call. Past that, spending the rest of the
            # budget on a clock that is not moving is the failure itself.
            #
            # A hold this call is working is the exception, and every one of
            # them counts: a mission phase held down, a narrative box before
            # and after it renders, a combat autoresolve grinding, and a
            # prompt queue that has not read back yet. Each takes several
            # polls by design and each ends at a stop of its own that says
            # what to do about it, so a stall already past the limit when the
            # call started would otherwise end all of them on poll two -- and
            # the mission-phase one worst of all, since its `next` would tell
            # the caller to force=true through the collision the hold exists
            # to prevent. force suppresses the stop for the same reason it
            # suppresses the no-progress refusal: the caller has read the
            # diagnosis and asked anyway. The reading is still recorded in
            # both cases, so the session counters see the stall.
            #
            # `holding_phase` is this poll's, decided just above. The rest are
            # last poll's, which is what "this call is in the middle of a
            # hold" means at the top of a loop body -- the readings they come
            # from are taken further down.
            #
            # `in_combat` is separate from `combat_arms` because the two are
            # different facts: `combat_arms` is what THIS call armed, and a
            # combat already grinding when the call started -- armed by an
            # earlier advance, or by combat_autoresolve by hand -- leaves it
            # empty. That combat freezes the clock exactly the same way, and
            # ending the call on it reports the fight as a stalled test.
            polls += 1
            working = bool(box_polls or combat_arms or in_combat
                           or holding_phase or was_waiting_for_box
                           or unknown_polls)
            record = _pause_stop(t, polls, suppress=force or working)
            if record:
                digest["stopReason"] = "pause_limit"
                digest["pauseLimit"] = record
                digest["next"] = pause_banner(
                    record["stallSeconds"], _stall_block(t), pause_limit(),
                    record.get("prompt"))
                break
            # A speed set that lands while the clock is momentarily blocked is
            # refused; re-assert instead of trusting the entry-time call. Never
            # during a mission-phase hold, where the whole point is that this
            # call is not asking the game to run.
            if (not holding_phase and not t.get("blocked")
                    and t.get("speed", 5) < 5):
                _try("time.speed", {"level": 5})
            # The macro turns itself off and pauses on its first exception
            # unless it was started with IgnoreExceptions, and nothing
            # announces that. A run that keeps resuming the clock over a game
            # nobody is driving is the shape problem 2 hides, so the switch is
            # read rather than assumed.
            if _RUN["macroOn"]:
                macro = _try("query.autopilot")
                if isinstance(macro, dict) and macro.get("activated") is False:
                    _RUN["macroOn"] = False
                    digest["stopReason"] = "autopilot_off"
                    digest["autopilot"] = macro
                    digest["next"] = (
                        "the autopilot macro is no longer engaged: it "
                        "switches itself off and pauses on the first "
                        "exception in game code unless it was started with "
                        "ignore_exceptions=true. Nothing has been driving the "
                        "faction since. Read log_tail which=player for the "
                        "exception, then autopilot action=on to restart it.")
                    break
            if _time.monotonic() - t0 > max_seconds:
                digest["stopReason"] = "max_seconds"
                digest["next"] = (
                    "time budget hit at %s; %s -- call advance again with "
                    "until=%s to continue"
                    % (cur.isoformat(), _armed_note(armed),
                       target.isoformat()))
                break
            if not holding_phase and not (t.get("blocked") or t.get("paused")):
                continue

            # Combat first: an unresolved combat freezes the clock until it is
            # disposed of, and prompts.dismiss deliberately leaves it alone.
            cs = _try("combat.status") or {}
            has_combat = bool(cs.get("active")) or bool(cs.get("pending"))
            in_combat = has_combat
            ar = cs.get("autoresolve") or {}
            ar_busy = bool(ar.get("armed") or ar.get("active")
                           or ar.get("autoresolving"))
            if has_combat and autoresolve and not ar_busy:
                # The recorded error, read instead of ignored. A failed tick
                # disarms, so without this a failed combat looks like a first
                # arm; re-arming it runs AutoresolveSelected and
                # OnAcceptAutoresolveSelected twice and applies the simulated
                # damage to the real states twice. The DLL refuses those
                # re-arms; this stops before making the call, so the reason
                # names the combat and the error rather than the time budget.
                cid = _combat_id(cs, ar)
                # The machine's recorded error belongs to whichever combat it
                # last touched, and that is not always the one this poll is
                # about: after A fails and B appears while A's canvas still
                # holds the clock, the status reports B with A's error
                # attached. `attemptedCombat` names the owner, so the gate
                # applies only when it matches.
                attempted = ar.get("attemptedCombat")
                mine = attempted is None or cid is None or attempted == cid
                error = ar.get("error") if mine else None
                blocked_by = None
                if error and ar.get("retryable") is False:
                    blocked_by = "the mod will not arm on it again"
                if blocked_by:
                    digest["stopReason"] = "combat"
                    digest["combat"] = cs
                    digest["next"] = (
                        "combat %s failed to autoresolve (%s) and %s. "
                        "combat_status to inspect, then combat_precombat "
                        "action=status, which reports whether the precombat "
                        "canvas is up and WHICH of its buttons are live: once "
                        "the precombat interaction has ended, the screen's "
                        "own close and cancel buttons may already be gone, "
                        "and then nothing here can clear it. Dropping the "
                        "prompt never helps -- it is the canvas and not the "
                        "prompt that freezes the clock."
                        % (cid, error, blocked_by))
                    break
                try:
                    # Named, not defaulted. With no argument the mod targets
                    # the active combat, else the first unresolved one, which
                    # is not necessarily the one this poll counted arms
                    # against.
                    bridge.call("combat.autoresolve",
                                {"combat": cid} if cid is not None else {})
                    # Distinct combats armed, not arms made: a combat that
                    # failed and was re-armed is still one fight.
                    if cid not in combat_arms:
                        digest["combatsAutoresolved"] += 1
                    combat_arms.add(cid)
                except VerbError as e:
                    digest["stopReason"] = "combat"
                    digest["combat"] = cs
                    digest["next"] = (
                        "combat needs a decision the autoresolver refused "
                        "(%s) -- combat_status to inspect, combat_autoresolve "
                        "with a stance, combat_precombat action=status for "
                        "what the screen still offers, or leave it for a "
                        "human" % e)
                    break
            if has_combat:
                # Not while the clock is being held for a mission phase: the
                # autoresolve machine needs frames rather than game time, and
                # a running clock is the collision the hold exists to avoid.
                if not holding_phase:
                    _try("time.play")
                continue    # let the autoresolver grind, keep polling

            dismissed = {}
            dismiss_args = {}
            if policy == "all":
                dismiss_args["all"] = True
            # The switch and the label. `narrative` is what the DLL acts on and
            # keeps its old meaning exactly; `narrativeMode` says which of the
            # two false-ish modes this is, so the reply can name who the event
            # was left for. A DLL older than the key ignores it and behaves as
            # it always did.
            dismiss_args["narrativeMode"] = narrative_mode
            if narrative_mode != "ai":
                dismiss_args["narrative"] = False
            try:
                dismissed = bridge.call("prompts.dismiss", dismiss_args)
            except VerbError as e:
                digest["dismissErrors"].append(str(e))
            # The pass can refuse itself. That comes back as a normal reply
            # carrying ok:false rather than an error, because the refusal has
            # state in it the caller has to act on, and nothing in this loop
            # can clear one: the pass that just declined to run is the only
            # thing here that answers a prompt. Polling on would re-read the
            # same refusal until the budget ran out and then report a budget.
            refusal = _dismiss_refusal(dismissed)
            if refusal:
                digest["stopReason"], digest["next"] = refusal
                digest["promptsRefusal"] = dismissed
                break
            if isinstance(dismissed, dict):
                for key, into in (("dismissed", "promptsAnswered"),
                                  ("forced", "promptsAnswered"),
                                  ("screens", "screensClosed")):
                    value = dismissed.get(key)
                    digest[into] += len(value) if isinstance(value, list) else 0
                # Read off the bridge's own key rather than sniffed out of the
                # screens log. The log is prose, and matching "narrative" in it
                # captured every ordinary answer as an anomaly while missing
                # the one press that reports itself as a plain option.
                for line in dismissed.get("narrativeNotes") or []:
                    text = line if isinstance(line, str) else str(line)
                    if text not in digest["narrativeNotes"]:
                        digest["narrativeNotes"].append(text)
                for answered in dismissed.get("dismissed") or []:
                    if (not isinstance(answered, dict)
                            or "event" not in answered):
                        continue
                    # A dropped prompt applied no option, so it is not one of
                    # the decisions this run took on the caller's behalf. It
                    # still has to be visible, in the channel for things that
                    # went sideways. Appended without the dedupe above: two
                    # drops are two events, and collapsing them would hide one
                    # while promptsAnswered counted both.
                    if answered.get("dropped"):
                        digest["narrativeNotes"].append(_drop_note(answered))
                        continue
                    digest["narrativeEvents"].append(answered)
                if dismissed.get("skipped"):
                    digest["promptsSkipped"] = dismissed["skipped"]
            remaining, remaining_known = _remaining(dismissed)
            if not remaining_known:
                # Said, not assumed away. The clock is frozen and the one
                # reading that would explain it did not come back.
                unknown_polls += 1
                digest["unreadablePromptPolls"] = unknown_polls
                if unknown_polls >= UNKNOWN_REMAINING_POLLS:
                    digest["stopReason"] = "prompts_unreadable"
                    digest["next"] = (
                        "the clock is frozen and prompts.dismiss did not "
                        "report a remaining count over %d polls, so nothing "
                        "here knows whether anything is queued -- it is NOT "
                        "zero. %s Read prompts for the queue and log_tail "
                        "which=player for an exception in the dismissal "
                        "path."
                        % (unknown_polls,
                           ("The last dismiss error was: %s."
                            % digest["dismissErrors"][-1])
                           if digest["dismissErrors"]
                           else "The call did not raise; the reply had no "
                                "`remaining` key, which means an older DLL "
                                "or a reply shape this server does not "
                                "know."))
                    break
                if not holding_phase:
                    _try("time.play")
                continue
            unknown_polls = 0
            # The box, read once for the two things that need it under a mode
            # which presses nothing: the llm handover below, and the box wait
            # after it, which under these modes cannot use the bridge's flag
            # because the dismiss pass never tried to answer the prompt.
            #
            # Gated on a standing prompt so a run that never meets a story
            # event pays nothing for the mode: an event's prompt is queued by
            # the same call that queues its box, and the prompt is what blocks
            # the clock, so the box cannot be up with the queue clear.
            alert = None
            if remaining and narrative_mode != "ai":
                alert = _try("alert.choose")
            # The llm handover. Nothing here is going to press a button, so the
            # only thing worth waiting for is the box itself: the moment it is
            # open with live options and a press would land, the call ends and
            # the caller answers it. No hold count applies -- the handover IS
            # the result, not a failure to make progress -- and the stop stays
            # out of INERT_STOPS for the same reason.
            if narrative_mode == "llm" and _box_answerable(alert):
                digest["stopReason"] = "narrative_event"
                digest["alert"] = alert
                digest["remainingPrompts"] = remaining
                digest["next"] = _narrative_next(alert, target.isoformat())
                break
            # A narrative prompt whose alert box has not come up yet is not a
            # decision waiting on the caller. Only that box may answer it, and
            # the box is queued behind whatever notification is on screen, so
            # it can trail its own prompt by a poll or more -- an objective
            # alert in front of it is enough. Stopping on that reports a
            # decision where there is none, and the caller clears it by doing
            # nothing but calling again.
            #
            # So the wait is driven by the BOX, not by a counter: alert.choose
            # is read every poll of a box wait, and the two states it tells
            # apart get different patience. Not rendered yet is a queue
            # draining and gets the long leash; rendered and still queued is
            # the controller refusing the press, which is a real hold and gets
            # a short one. Both are bounded, so a box that never arrives
            # surfaces as a hold instead of spinning to the time budget.
            waiting_for_box = bool(remaining) and _box_wait(
                dismissed, narrative_mode, alert)
            if remaining:
                # `stuck` is the length of THIS hold, reset on a regime change.
                stuck = _hold_polls(stuck, waiting_for_box,
                                    was_waiting_for_box)
                if waiting_for_box != was_waiting_for_box:
                    box_polls = 0
                was_waiting_for_box = waiting_for_box
                if waiting_for_box:
                    # Run total, spanning every hold. Reported for the shape of
                    # the whole call; the stop message uses the per-hold count
                    # instead, because it describes one hold.
                    digest["narrativeBoxWaits"] = \
                        digest.get("narrativeBoxWaits", 0) + 1
                    if alert is None:
                        alert = _try("alert.choose")
                    if _box_open(alert):
                        box_polls += 1
                        digest["narrativeBoxPolls"] = box_polls
                if waiting_for_box and box_polls:
                    # Counted from the poll the box appeared on, not from the
                    # start of the hold: a box that shows up on poll 40 of a
                    # long queue drain has not been refusing a press for 40
                    # polls, and comparing the whole hold against the short
                    # leash would report it as one immediately.
                    held = box_polls
                    over = box_polls >= NARRATIVE_BOX_PRESS_POLLS
                elif waiting_for_box:
                    held = stuck
                    over = stuck >= NARRATIVE_BOX_POLLS
                else:
                    held = stuck
                    over = stuck >= 2
                if over:
                    if alert is None:
                        alert = _try("alert.choose")
                    digest["stopReason"] = "decision"
                    digest["remainingPrompts"] = remaining
                    if alert:
                        digest["alert"] = alert
                    digest["next"] = _stop_next(
                        waiting_for_box, held, bool(box_polls),
                        attended=narrative_mode == "false")
                    break
            else:
                stuck = 0
                box_polls = 0
                was_waiting_for_box = False
            # Skipped entirely while a mission phase is being held for -- this
            # call stopped the clock on purpose and must not ask for it back.
            if not holding_phase:
                _try("time.play")
    except BridgeTimeout as e:
        # A timeout on one of the loop's OTHER calls. The per-poll query.time
        # absorbs its own and keeps going; this one ends the call, but it is
        # still a busy main thread and not a dead game, so it must not be
        # reported as one.
        digest["lostPolls"] = digest.get("lostPolls", 0) + 1
        digest["stopReason"] = "bridge_busy"
        digest["bridgeError"] = str(e)
        digest["next"] = (
            "a call inside the run went unanswered for the full timeout "
            "(%s). The game is UP -- verbs are served on its main thread, so "
            "a large save, an asset load or a scene change stalls them past "
            "the budget. Do NOT call game_start; call advance again with "
            "until=%s." % (e, digest["targetDate"]))
    except BridgeError as e:
        digest["stopReason"] = "bridge_lost"
        digest["bridgeError"] = str(e)
        digest["next"] = (
            "the bridge stopped answering mid-run (%s). A closed socket, not "
            "a slow one: a busy main thread times out and is counted as a "
            "lost poll instead. observe reports whether the process is still "
            "there, and game_start relaunches it." % e)

    return _finish(digest, start, cur)


def _finish(digest, start, cur):
    """Close a digest out and fold the call into the run state.

    Every exit that has a digest to report comes through here: the loop's
    breaks, the crash exit that never entered the loop, and the transport
    failures the loop is wrapped against. So a run cannot lose count of a
    fruitless call by dying rather than stopping.

    What still raises past it is the entry sequence before the loop -- the
    first query.time, the date parse, the speed and run_until arming. Those
    happen before the digest has anything in it, and a game that cannot answer
    them has nothing to report anyway.
    """
    digest["endDate"] = cur.isoformat()
    days = (cur - start).days
    digest["daysAdvanced"] = days
    reason = digest.get("stopReason", "unknown")
    # A call that moved nothing is its own event, and it is not the same event
    # as running out of budget: the budget reason reads as "it was going fine,
    # ask for more time", which is what kept the caller asking.
    if days == 0 and reason == "max_seconds":
        digest["stopReason"] = "no_progress"
        reason = "no_progress"
    _note_progress(days, reason)
    if days == 0:
        digest["next"] = (_zero_progress_next(reason) + " "
                          + digest.get("next", "")).strip()
        digest["consecutiveZeroProgressCalls"] = _RUN["zeroCalls"]
    return digest


# ---------------------------------------------------------------- batch

def batch(args, progress=None):
    steps = args.get("steps")
    if not isinstance(steps, list) or not steps:
        raise ToolError('batch needs steps=[{"cmd": ..., "args": {...}}, ...]')
    results = []
    for idx, step in enumerate(steps):
        if not isinstance(step, dict) or not step.get("cmd"):
            results.append({"step": idx, "ok": False,
                            "error": "step needs a cmd"})
            break
        cmd = step["cmd"]
        if progress:
            progress(idx, len(steps), cmd)
        try:
            data = bridge.call(cmd, step.get("args") or {})
            results.append({"step": idx, "cmd": cmd, "ok": True,
                            "data": data})
        except VerbError as e:
            results.append({"step": idx, "cmd": cmd, "ok": False,
                            "error": str(e)})
            break   # fail-fast
    return {"results": results,
            "completed": all(r.get("ok") for r in results),
            "ran": len(results), "of": len(steps)}


# ------------------------------------------------------------------- combat

# Seconds a headless resolution gets before this tool stops waiting and
# diagnoses instead. The machine's own budget is 3600 frames (a minute at
# 60fps) and it only ends the arm; the wait has to outlast that to see what it
# left behind.
COMBAT_RESOLVE_WAIT = 120

# The prompt the precombat screen queues. Every engine path that removes it is
# one of that screen's own button handlers.
BEGIN_COMBAT = "PromptBeginCombat"


def _begin_combat_prompts(listing):
    """The begin-combat rows of a prompts.list reply. The reply is
    {activePlayer, blocked, prompts: [{name, scope, ...}]}, faction and nation
    prompts in one list with a `scope` field."""
    if not isinstance(listing, dict):
        return []
    rows = listing.get("prompts")
    if not isinstance(rows, list):
        return []
    return [r for r in rows
            if isinstance(r, dict) and r.get("name") == BEGIN_COMBAT]


def _combat_orphan(report, status):
    """After a resolution ends: is a begin-combat prompt holding the clock with
    nothing left that could clear it, and if so, take it off the queue.

    The DLL clears this itself on the one path that leaves it (a combat that
    ends with no simulation to accept, where no button ever runs). This is
    the backstop: an older DLL, or any other route to the same state.

    Two conditions, and both are about not touching a live fight.

    The canvas: while the precombat screen is up it is the CANVAS that freezes
    the clock, not the prompt, so dropping the prompt would change nothing and
    would remove the record of a fight still waiting for a button.

    The combat: the canvas is ALSO down through the whole simulation, because
    AutoresolveSelected ends the precombat interaction, and the prompt is not
    removed until the accept. A slow resolution therefore looks exactly like an
    orphan from outside, minus the one thing that tells them apart -- whether
    an unresolved combat is still there.

    Both conditions are read, never assumed. A screen read that fails says
    nothing about the canvas, and reading it as a canvas that is down turns
    silence into the one answer that allows the drop.
    """
    try:
        screen = bridge.call("combat.precombat", {"action": "status"})
        unread = None if isinstance(screen, dict) else (
            "combat.precombat answered with no screen object")
    except (BridgeError, VerbError) as e:
        screen = None
        unread = str(e)
    if unread is not None:
        report["precombat"] = None
        report["orphanPrompt"] = None
        report["orphanPromptDropped"] = False
        report["orphanPromptNote"] = (
            "the precombat screen could not be read (%s), so whether the "
            "canvas is up is unknown and any standing %s was left alone. "
            "combat_precombat action=status reads the screen once the bridge "
            "answers again." % (unread, BEGIN_COMBAT))
        return
    report["precombat"] = screen
    if screen.get("canvasUp"):
        report["orphanPrompt"] = None
        return
    if status.get("active") or status.get("pending"):
        report["orphanPrompt"] = None
        report["orphanPromptNote"] = (
            "a combat is still unresolved, so any standing begin-combat "
            "prompt belongs to it and was left alone")
        return
    # Read with the error kept, not through _try. A queue nobody could read
    # answers `standing` with an empty list, which is the same shape as a
    # queue with nothing in it -- and the tool would then report "nothing was
    # standing" about a prompt it never looked for, which is the reading that
    # stops anyone looking again.
    try:
        listing = bridge.call("prompts.list")
        listing_error = None
    except (BridgeError, VerbError) as e:
        listing = None
        listing_error = str(e)
    if listing_error is not None:
        report["orphanPrompt"] = None
        report["orphanPromptDropped"] = False
        report["orphanPromptNote"] = (
            "the prompt queue could not be read (%s), so whether a %s is "
            "standing is unknown and nothing was dropped. prompts reads the "
            "queue once the bridge answers again."
            % (listing_error, BEGIN_COMBAT))
        return
    standing = _begin_combat_prompts(listing)
    if not standing:
        report["orphanPrompt"] = None
        return
    report["orphanPrompt"] = standing
    dropped = _try("prompts.dismiss", {"type": BEGIN_COMBAT, "all": True})
    # A refusal is a reply, so "it came back" is not "it ran". Reported as the
    # refusal it was: claiming a drop that did not happen is how a standing
    # prompt stops being looked for.
    refusal = _dismiss_refusal(dropped)
    report["orphanPromptDropped"] = dropped is not None and refusal is None
    if refusal:
        report["orphanPromptNote"] = (
            "a %s was standing with the precombat canvas already down, and "
            "prompts.dismiss refused to run: %s" % (BEGIN_COMBAT, refusal[1]))
        return
    report["orphanPromptNote"] = (
        "a %s was standing with the precombat canvas already down. Nothing in "
        "the engine clears one but that screen's own buttons, and there was "
        "no screen left, so it would have blocked saving and held the clock "
        "for the rest of the campaign. It was dropped here." % BEGIN_COMBAT)


def combat_autoresolve(args, progress=None):
    """Arm a headless resolution AND wait it out, with a diagnosis on a stall.

    The verb arms and returns inside one frame, which is the execution model.
    The waiting belongs on this side: a resolution that never finishes has to
    be reported with the state it left behind (a combat gone, a begin-combat
    prompt standing, the clock frozen) rather than as a tool that hung.

    Every refusal the verb makes is still a refusal here (a one-shot that has
    already fired, a failure no retry changes, the attempt budget). Those are
    the contract and they raise.
    """
    a = {}
    if args.get("combat") is not None:
        a["combat"] = args["combat"]
    if args.get("stance"):
        a["stance"] = args["stance"]
    armed = bridge.call("combat.autoresolve", a)
    report = {"armed": armed}
    if not _flag(args, "wait", True):
        report["waited"] = False
        report["next"] = ("armed and not waited on, as asked. Poll "
                          "combat_status until its autoresolve disarms")
        return report
    # An AI-vs-AI combat is not armed at all: the engine's own listeners
    # resolve it. There is nothing to wait for.
    if isinstance(armed, dict) and armed.get("armed") is False:
        report["waited"] = False
        report["next"] = ("nothing was armed (%s); combat_status reports what "
                          "the engine is doing with it"
                          % (armed.get("resolving") or "see `armed`"))
        return report

    seconds = float(args.get("max_seconds", COMBAT_RESOLVE_WAIT))
    t0 = _time.monotonic()
    status = {}
    ar = {}
    # Consecutive polls that came back with no status. A poll that raises says
    # NOTHING about the resolution, so it is counted here and never read as a
    # status: an empty status is the shape of a clean disarm, and taking a
    # dead bridge for one would report `resolved: true` and let the orphan
    # pass drop the begin-combat prompt of a fight still on screen.
    lost = 0
    last_lost = None
    # How many of that consecutive run timed out rather than failed. A timeout
    # is a main thread too busy to answer, with the socket open and the game
    # up; anything else is the socket. The two end the call under different
    # stop reasons because the way out differs.
    lost_timeouts = 0
    while True:
        _time.sleep(2)
        if progress:
            progress(int(_time.monotonic() - t0), int(seconds),
                     "autoresolving (%s)" % (ar.get("phase") or "arming"))
        polled = None
        why = None
        timed_out = False
        try:
            # BridgeTimeout is a BridgeError: the main thread being busy is a
            # state a resolution itself produces, so one of those is a poll to
            # take again rather than a reason to stop.
            polled = bridge.call("combat.status")
        except (BridgeError, VerbError) as e:
            why = str(e)
            timed_out = isinstance(e, BridgeTimeout)
        if why is None and not isinstance(polled, dict):
            why = "combat.status answered with no status object"
        if why is not None:
            lost += 1
            if timed_out:
                lost_timeouts += 1
            last_lost = why
            if lost >= LOST_POLLS_MAX or _time.monotonic() - t0 > seconds:
                break
            continue
        lost = 0
        lost_timeouts = 0
        status = polled
        ar = status.get("autoresolve") or {}
        if not ar.get("armed"):
            break
        if _time.monotonic() - t0 > seconds:
            report["stalled"] = True
            break
    report["seconds"] = round(_time.monotonic() - t0, 1)
    report["status"] = status
    report["phase"] = ar.get("phase")
    report["reached"] = ar.get("reached")
    report["firedAutoresolveSelected"] = ar.get("firedAutoresolveSelected")
    report["firedAcceptAutoresolve"] = ar.get("firedAcceptAutoresolve")
    report["error"] = ar.get("error")
    report["note"] = ar.get("note")
    # As of the last poll, which is BEFORE any orphan drop below.
    report["blocked"] = status.get("blocked")

    if lost:
        # The wait ended on unanswered polls, so every reading above belongs
        # to the last poll that did answer, and there may have been none. The
        # resolution is not reported either way, and the orphan pass is not
        # run at all: it decides from a status, and a status nobody read looks
        # identical to a combat that is over.
        #
        # Which of the two transport stops it was follows from what the polls
        # did. Every one of them timing out is a main thread that never came
        # back inside the verb timeout, with the socket open and the game up,
        # and a resolution can produce exactly that: the wait is longer than
        # the timeout by design. Anything else -- a refused verb, a closed
        # socket, a reply that was not a status -- is the bridge itself.
        busy = lost_timeouts == lost
        report["resolved"] = False
        report["stopReason"] = "bridge_busy" if busy else "bridge_lost"
        report["lostPolls"] = lost
        report["bridgeError"] = last_lost
        report["_failed"] = True
        if busy:
            report["next"] = (
                "the last %d combat.status poll(s) timed out (%s), so the "
                "main thread is busy rather than gone and what the resolution "
                "did is unknown; nothing was done about the begin-combat "
                "prompt, since dropping one on a fight still waiting for a "
                "button is worse than leaving it. Read combat_status first: "
                "the resolution may well have finished while the polls were "
                "going unanswered, and calling combat_autoresolve on a "
                "combat that is over is refused as an unsafe re-arm. Go back "
                "to waiting with combat_autoresolve on the same combat only "
                "if combat_status still reports it armed -- that call reports "
                "the status and does not arm a second time, so no one-shot "
                "fires twice." % (lost, last_lost))
        else:
            report["next"] = (
                "the last %d combat.status poll(s) went unanswered (%s), so "
                "what the resolution did is unknown and nothing was done "
                "about the begin-combat prompt -- dropping one on a fight "
                "still waiting for a button is worse than leaving it. observe "
                "reports whether the process is still there; with the game "
                "up, combat_status reads the machine and combat_precombat "
                "action=status the screen." % (lost, last_lost))
        return report

    if report.get("stalled"):
        _combat_orphan(report, status)
        report["_failed"] = True
        report["next"] = (
            "the resolution was still armed after %ds, in phase %s. Read "
            "`status` for what the machine holds and combat_precombat "
            "action=status for whether the screen still offers a button. "
            "Arming again is refused once a one-shot has fired on this combat."
            % (seconds, report["phase"] or "unknown"))
        return report

    if ar.get("error"):
        _combat_orphan(report, status)
        report["_failed"] = True
        report["next"] = (
            "the resolution disarmed with an error after reaching phase %s. "
            "combat_precombat action=status reports which of the screen's own "
            "buttons are live -- that depends on how far it got, and once the "
            "precombat interaction has ended there may be none left. Dropping "
            "the begin-combat prompt does not help while the canvas is up: it "
            "is the canvas that freezes the clock."
            % (ar.get("reached") or "unknown"))
        return report

    # A clean disarm still has to be checked for the state the engine's own
    # end-combat branch leaves: the combat gone, no button ever pressed, and
    # the prompt it queued still standing.
    _combat_orphan(report, status)
    report["resolved"] = True
    if status.get("blocked") and not report.get("orphanPromptDropped"):
        # Reported, not failed. An ordinary campaign has prompts queued most
        # of the time, and a research prompt blocking the clock is not this
        # tool's business; advance answers those. Only a begin-combat prompt
        # with nothing left to clear it was ever this tool's to fix, and that
        # one is handled above.
        report["next"] = (
            "the combat is resolved and the clock is still blocked by "
            "something else -- advance answers ordinary prompts, prompts "
            "lists the queue, and combat_precombat action=status reports a "
            "canvas still holding it")
    return report


# ---------------------------------------------------------------- smoke test

def _log_marks():
    marks = {}
    for which, path in (("player", bridge.PLAYER_LOG),
                        ("game", bridge.GAME_LOG)):
        try:
            marks[which] = (path, os.path.getsize(path))
        except OSError:
            marks[which] = (path, 0)
    return marks


# Engine log lines that match the exception/ERROR scan and mean nothing about
# the tree under test. Each entry is (substring, why it is not a failure), and
# the whole list is reported by every run that applies it -- an allowlist that
# hides what it swallowed is how a real failure gets classified away.
#
# Both current entries are Log::Error calls inside
# TISpaceShipTemplate.UnnormalizedTemplateSpaceCombatValue, which substitutes a
# fallback (1.0 and 0.0 respectively) and carries on computing. They fire on
# ordinary vanilla designs, they repeat once per evaluation, and nothing
# downstream of them fails.
#
# The bar for adding one: the line is written by the ENGINE about its own
# arithmetic, the engine recovers from it in the same method, and it appears on
# a tree with no mods that touch the subject. A line naming a mod's data
# ("Bad upgradesFromName X in Y") is the opposite of that and must stay a
# failure -- it is exactly what a smoke test exists to catch.
LOG_NOISE = (
    ("expectedLifetime_s was NaN!",
     "TISpaceShipTemplate.UnnormalizedTemplateSpaceCombatValue substitutes "
     "1.0 for the NaN and continues"),
    ("_unnormalizedCombatValue was invalid!",
     "the same method substitutes 0 for a NaN or infinite combat value and "
     "continues"),
)


def _log_noise(line):
    """The allowlist reason this line is engine noise, or None."""
    for needle, why in LOG_NOISE:
        if needle in line:
            return why
    return None


def _scan_logs(marks, cap=40):
    """Split the log lines written since `marks` into failures and known
    engine noise, and count what was set aside.

    The noise is COUNTED and named rather than dropped: a run that says
    "ok, and 118 allowlisted lines from two patterns" can be argued with. One
    that silently filtered them cannot.
    """
    failures = []
    noise = {}
    capped = False
    for which, (path, offset) in sorted(marks.items()):
        try:
            with open(path, "rb") as f:
                f.seek(offset)
                blob = f.read(4 * 1024 * 1024)
        except OSError:
            continue
        for line in blob.decode("utf-8", "replace").splitlines():
            if "Exception" not in line and "ERROR" not in line:
                continue
            why = _log_noise(line)
            if why is not None:
                noise[why] = noise.get(why, 0) + 1
                continue
            if len(failures) >= cap:
                capped = True
                continue
            failures.append("%s: %s" % (which, line.strip()))
    return {"failures": failures, "noise": noise, "capped": capped}


def _bridge_stop(e):
    """The stop reason a transport failure earns.

    The same split `advance` and the autoresolve wait make. A timeout is a
    main thread too busy to answer inside the verb budget, with the socket
    open and the game up; anything else is the socket. Calling both of them
    `bridge_lost` would send a reader to relaunch a game that is still
    running and only slow.
    """
    return "bridge_busy" if isinstance(e, BridgeTimeout) else "bridge_lost"


def smoke_test(args, progress=None):
    days = int(args.get("days", 3))
    if args.get("scenario"):
        names = [args["scenario"]]
    else:
        data = bridge.call("query.scenarios") or {}
        # Only the Scenario category names a campaign; the other categories are
        # start options (map size, council count) campaign.new refuses as a
        # scenario name.
        names = [o.get("dataName")
                 for c in data.get("categories", [])
                 if c.get("name") == "Scenario"
                 for o in c.get("options", [])]
        names = [n for n in names if n]
        if not names:
            raise ToolError("query.scenarios returned no options")
    results = []
    for idx, name in enumerate(names):
        if progress:
            progress(idx, len(names), "smoke %s" % name)
        # campaign.new refuses while a campaign is loaded, so the loaded one
        # goes back to the start screen first through main_menu, the engine's
        # own exit-to-menu path in one process. A relaunch is the fallback,
        # for a DLL that has no such verb and for a return that does not
        # complete.
        relaunched = None
        relaunch_error = None
        # Whether this row paid for a menu return. The step runs for a named
        # scenario exactly as it does for a sweep -- a client that asks for one
        # scenario with a campaign up is the ordinary case, not a misuse -- and
        # the row says so, because a campaign.new refused for a start screen
        # that is not there reads the same whether the return never ran or ran
        # and did not finish landing.
        returned_to_menu = False
        try:
            if _campaign_loaded():
                main_menu({}, progress)
                returned_to_menu = True
        except (ToolError, VerbError, BridgeError) as e:
            # A transport failure joins the other two here because the way out
            # is the same one: the scenario before this one may have taken the
            # game down with it, and then every call on this path fails the
            # way a menu return that will not complete does. Relaunching is
            # what lets the scenarios after a lost bridge be tested at all.
            relaunched = str(e)
            try:
                game_stop({})
                _time.sleep(5)
                game_start({})
            except (ToolError, VerbError, BridgeError) as relaunch:
                # A recovery that cannot run is reported, and the sweep carries
                # on to spend this scenario's steps against whatever is left.
                # They fail and the row says so, which is a report; an
                # exception raised here would end the sweep instead, leaving
                # every scenario after it untested and unmentioned.
                relaunch_error = str(relaunch)
        marks = _log_marks()
        row = {"scenario": name}
        if returned_to_menu:
            row["returnedToMenu"] = True
        if relaunched:
            # Said out loud. A silent fallback to a relaunch is the same cost
            # the menu return exists to remove, and a run that pays it twice
            # should show why.
            row["relaunchedBecause"] = relaunched
        if relaunch_error:
            row["relaunchFailed"] = relaunch_error
        # The step about to run, so a row that fails names what it reached
        # rather than leaving the reader to guess how far the scenario got.
        # The menu return above is not one of the values: it reports itself,
        # through `relaunchedBecause` and `relaunchFailed`, and the scenario
        # is tried from wherever it left the game either way.
        reached = "campaign.new"
        # Whether the scenario ran its steps to the end. The verdict needs both
        # this and the log scan below, and the scan is taken after the except
        # arms, so it cannot be written here.
        ran = False
        try:
            # The tool rather than the verb. Each scenario is a campaign of
            # its own, and going straight to the verb left the counters, the
            # run's recorded save and the zero-progress history describing the
            # scenario before it -- which is how a sweep gets its second row
            # refused for a stall that belongs to its first.
            campaign_new({"scenario": name}, progress)
            reached = "campaign load"
            _wait_campaign(CAMPAIGN_WAIT, progress)
            reached = "advance"
            row["advance"] = advance({"days": days, "max_seconds": 120},
                                     progress)
            ran = True
        except (ToolError, VerbError) as e:
            row["error"] = str(e)
            row["reached"] = reached
        except BridgeError as e:
            # Not a verdict on the scenario. Nothing here says the campaign is
            # bad, only that the run could not be watched to the end, so the
            # row records it the way advance records the same two failures and
            # the sweep moves on -- the next scenario relaunches before it
            # starts. Left uncaught, one dead bridge ended the whole sweep
            # with an error naming a single scenario.
            row["stopReason"] = _bridge_stop(e)
            row["bridgeError"] = str(e)
            row["reached"] = reached
            row["next"] = (
                "the bridge stopped answering during this scenario (%s). The "
                "sweep relaunches the game before the next one; after the "
                "last scenario it is left where it ended, so observe reports "
                "whether the process is still there and game_start brings it "
                "back." % e)
        # After the try, so the rows that failed carry the log evidence too.
        # A scenario that raised or lost the bridge is exactly the row a
        # reader goes looking for an exception on.
        scan = _scan_logs(marks)
        row["newExceptions"] = scan["failures"]
        # Reported on every row, including the clean ones. An allowlist that
        # only shows itself when it fired cannot be reviewed, and this one
        # decides whether a run passes.
        row["logNoiseIgnored"] = scan["noise"]
        if scan["capped"]:
            row["newExceptionsTruncated"] = True
        row["ok"] = (ran and not row["newExceptions"]
                     and row["advance"].get("stopReason")
                     in ("reached", "max_seconds"))
        results.append(row)
    return {"days": days, "ok": all(r.get("ok") for r in results),
            "results": results,
            # The allowlist itself, verbatim, so the reader of a pass can see
            # exactly what was not counted against it and argue with the list.
            "logNoiseAllowlist": [{"match": needle, "why": why}
                                  for needle, why in LOG_NOISE]}


# ---------------------------------------------------------------- selftest

def _mod_info_version():
    """(version, None) from the mod's ModInfo.json, or (None, why-not)."""
    path = os.path.join(bridge.MOD_DIR, "ModInfo.json")
    try:
        with open(path, "r") as f:
            data = json.load(f)
    except OSError as e:
        return None, "unreadable (%s)" % e
    except ValueError as e:
        return None, "not valid JSON (%s)" % e
    version = data.get("Version") if isinstance(data, dict) else None
    if not version:
        return None, "no Version field in %s" % path
    return str(version), None


def _server_version():
    """(version, None) from SERVER_INFO, or (None, why-not).

    SERVER_INFO lives in the entry module, which is `__main__` whenever this
    server is the running program -- the only way selftest is normally
    reached. Imported instead (a harness, a test), the constant is read out
    of the file without executing it, so the check still has three sources.
    """
    info = getattr(sys.modules.get("__main__"), "SERVER_INFO", None)
    if isinstance(info, dict) and info.get("version"):
        return str(info["version"]), None
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                        "__main__.py")
    try:
        with open(path, "r") as f:
            m = re.search(r'SERVER_INFO\s*=\s*\{[^}]*"version":\s*"([^"]+)"',
                          f.read())
    except OSError as e:
        return None, "cannot read SERVER_INFO (%s)" % e
    return (m.group(1), None) if m else \
        (None, "no SERVER_INFO version in %s" % path)


def _version_agreement(dll=None):
    """True when every place this install states the mod version agrees,
    else a string naming each source and its value.

    Three files carry the version independently -- ModInfo.json (what Unity
    Mod Manager reports), src/Verbs.cs ModVersion (what the version verb
    answers as `mod`), and SERVER_INFO in server/__main__.py (what an MCP
    client sees at initialize). Nothing else keeps them equal, and they have
    silently drifted apart before. `dll` is the version verb's answer as a
    (value, why-not) pair, or None with the game down.
    """
    sources = [("ModInfo.json", _mod_info_version()),
               ("server", _server_version())]
    if dll is not None:
        sources.append(("DLL", dll))
    parts = ["%s %s" % (name, value if value is not None
                        else "unknown (%s)" % why)
             for name, (value, why) in sources]
    seen = {value for _name, (value, _why) in sources if value is not None}
    if len(seen) > 1:
        return ("MISMATCH: " + ", ".join(parts) + " -- set all three "
                "together: ModInfo.json Version, src/Verbs.cs ModVersion, "
                "server/__main__.py SERVER_INFO")
    if any(value is None for _name, (value, _why) in sources):
        return "unverified: " + ", ".join(parts)
    return True


def selftest(args, progress=None):
    report = {"offline": {}}
    # First, because it decides whether the rest of the report is worth
    # reading: the checks below are computed by whatever code this process
    # loaded, which is what this line is about.
    line, failed = codestate.check()
    report["offline"]["serverCode"] = line
    if failed:
        report["_failed"] = True
    report["offline"]["versionMatch"] = _version_agreement()
    managed = os.path.join(bridge.GAME_ROOT, "TerraInvicta_Data", "Managed")
    ui = os.path.join(managed, "UnityEngine.UIModule.dll")
    orig = ui + ".original_"
    # UMM's two install methods, in the order both installers check them.
    # The Assembly method patches UIModule.dll and leaves the .original_
    # backup; DoorstopProxy touches nothing in Managed/ and drops winhttp.dll
    # and doorstop_config.ini in the game root instead. Doorstop is read only
    # where the Assembly method left no backup, because a leftover winhttp.dll
    # from an abandoned Doorstop install would otherwise mask a reverted
    # Assembly install and report a mod loader that does not load.
    doorstop = (os.path.exists(os.path.join(bridge.GAME_ROOT, "winhttp.dll"))
                and os.path.exists(os.path.join(bridge.GAME_ROOT,
                                                "doorstop_config.ini")))
    if not os.path.exists(orig):
        report["offline"]["ummInjection"] = (
            "patched (Doorstop)" if doorstop else
            "no .original_ backup next to UnityEngine.UIModule.dll and no "
            "Doorstop pair in the game root -- no UMM install found here")
    elif filecmp.cmp(ui, orig, shallow=False):
        report["offline"]["ummInjection"] = (
            "REVERTED -- UIModule.dll matches its backup; a game update "
            "undid the injection. Repair it (Linux: run install.sh; "
            "Windows: re-run UnityModManager.exe and Install), then "
            "restart the game.")
    else:
        report["offline"]["ummInjection"] = "patched"

    try:
        ver = bridge.call("version")
    except BridgeError as e:
        report["bridge"] = ("down (%s) -- only offline checks ran; "
                            "game_start to run the rest" % e)
        return report
    report["bridge"] = "up"
    report["version"] = ver
    # The DLL's own version closes the three-way comparison started above.
    if isinstance(ver, dict) and ver.get("mod"):
        report["offline"]["versionMatch"] = _version_agreement(
            (str(ver["mod"]), None))
    else:
        report["offline"]["versionMatch"] = _version_agreement(
            (None, "version reply had no mod field"))

    served = bridge.verbs(refresh=True)
    import tools
    used = tools.BRIDGE_VERBS_USED | tools.BRIDGE_VERBS_RAW_ONLY
    report["verbDrift"] = {
        "toolTableCallsMissingFromDll": sorted(used - served),
        "dllVerbsNoToolMaps": sorted(served - used),
        "note": "unmapped DLL verbs stay reachable through raw"}

    mods = _try("mods.list")
    use = mods.get("useMods") if isinstance(mods, dict) else None
    report["useMods"] = use if use is not None else \
        "could not read from mods.list"
    return report


# ---------------------------------------------------------------- wrappers

QUERY_KINDS = {
    "factions": ("query.factions", ()),
    "habs": ("query.habs", ("faction",)),
    "fleets": ("query.fleets", ("faction",)),
    "designs": ("query.designs", ("faction", "limit")),
    "scenarios": ("query.scenarios", ()),
    "councilors": ("query.councilors", ("faction", "limit")),
    "nations": ("query.nations", ("contains", "limit")),
}


def query(args, progress=None):
    kind = args.get("kind")
    if kind not in QUERY_KINDS:
        raise ToolError("kind must be one of: %s"
                        % ", ".join(sorted(QUERY_KINDS)))
    verb, allowed = QUERY_KINDS[kind]
    return bridge.call(verb, {k: args[k] for k in allowed if k in args})


def console(args, progress=None):
    if not args.get("line"):
        raise ToolError("console needs line=<command>")
    if args.get("select") is not None:
        bridge.call("select", {"id": args["select"]})
    return bridge.call("console", {"line": args["line"]})


def autopilot(args, progress=None):
    """Hand the player faction to the AI, or take it back.

    Bare `autopilot` TOGGLES, which is useless when you do not know the
    current state, so this always sends an explicit on/off token.
    """
    action = args.get("action", "status")
    if action not in ("on", "off", "status"):
        raise ToolError("action must be on, off, or status")
    if action == "status":
        # Read from the macro itself. The console command is write-only, but
        # Autopilot.Activated is not, and the difference matters: the macro
        # switches itself off on the first exception in game code unless it
        # was started with ignore_exceptions, so "I sent action=on" is not
        # evidence that it is still on.
        macro = bridge.call("query.autopilot")
        if isinstance(macro, dict) and macro.get("activated") is False:
            _RUN["macroOn"] = False
        return macro

    tokens = [action]
    if args.get("save_cycles") is not None:
        cycles = args["save_cycles"]
        if cycles < 0:
            raise ToolError("save_cycles must be >= 0")
        tokens.append(str(cycles))
    if args.get("ignore_exceptions") is not None:
        tokens.append("IgnoreExceptions" if _flag(args, "ignore_exceptions", False)
                      else "DontIgnoreExceptions")

    line = "autopilot " + ",".join(tokens)
    out = bridge.call("console", {"line": line})
    # Remembered so the advance loop knows whether the macro's engaged state
    # is worth a poll. A macro nobody turned on here is not this server's to
    # watch, and one that was turned on and switched itself off is the whole
    # point of watching.
    _RUN["macroOn"] = action == "on"
    result = {"sent": line, "action": action, "output": out}
    result["macro"] = _try("query.autopilot")
    return result


def ai_autopilot(args, progress=None):
    """Engage or release the real faction AI on the player's faction.

    Thin over the bridge verb: the DLL owns the engagement, since it is the
    side that can flip isAI, re-seed the planner and keep saves clean.
    """
    action = args.get("action", "status")
    if action not in ("engage", "release", "status"):
        raise ToolError("action must be engage, release, or status")
    smart = args.get("smart", "brutal")
    if smart not in ("campaign", "brutal"):
        raise ToolError("smart must be campaign or brutal")
    call = {"action": action}
    if action == "engage":
        call["smart"] = smart
    return bridge.call("ai.control", call)


# Typed wrappers over the debug console. The console parser matches command
# names as a case-insensitive SUBSTRING of the line and splits args on commas
# only, so hand-built lines are easy to get wrong; these build them correctly.
GRANT_KINDS = {
    # kind: (command, takes a faction argument)
    "project": ("giveproject", True),
    "tech": ("givetech", False),      # global: finishes it for everyone
    "objective": ("completeobjective", True),
    "milestone": ("completemilestone", True),
}


def grant(args, progress=None):
    """Force one thing to be true. The fast path for setting up a test."""
    kind = args.get("kind")
    if kind not in GRANT_KINDS:
        raise ToolError("kind must be one of: %s"
                        % ", ".join(sorted(GRANT_KINDS)))
    name = args.get("name")
    if not name:
        raise ToolError("grant needs name=<dataName>")
    command, takes_faction = GRANT_KINDS[kind]

    faction = args.get("faction")
    if faction and not takes_faction:
        raise ToolError("%s is global; it takes no faction" % kind)
    # Parameters are case-sensitive dataNames; arguments split on commas only.
    line = "%s %s" % (command, name)
    if faction:
        line += "," + faction
    out = bridge.call("console", {"line": line})
    return {"sent": line, "output": out}


def give_resources(args, progress=None):
    """Resources for the player faction. Default: everything, absurd amounts."""
    resource = args.get("resource")
    amount = args.get("amount")
    if resource:
        if amount is None:
            raise ToolError("give_resources needs amount with resource")
        # addresource REQUIRES the second comma even with no faction.
        line = "addresource %s,%s," % (resource, amount)
    elif amount is not None:
        line = "givemetonsofresources %s" % amount
    else:
        line = "givemetonsofresources"
    out = bridge.call("console", {"line": line})
    return {"sent": line, "output": out}


# Concrete state class names as `select` reports them: the bridge describes a
# state by its runtime type, so every subclass has to be listed. Taken from the
# 1.0.49 hierarchy. An unlisted type is refused with its name in the message,
# which is the signal to extend the list rather than a silent wrong answer.
NATION_STATES = ("TINationState",)
REGION_STATES = ("TIRegionState",)
KILLABLE_STATES = ("TIArmyState", "TIAlienArmyState", "TIMegafaunaArmyState",
                   "TICouncilorState", "TISpaceFleetState", "TIHabState",
                   "TILaunchFacilityState", "TIMissionControlFacilityState",
                   "TISpaceDefensesFacilityState")


def _run_console(line, select=None, expect=None, needs=None):
    """One exact console line, optionally against the map selection.

    The console reads two selection slots. `select` sets the map slot
    (UIOtherSelectedState), which is the one the bridge can reach; the asset
    slot (UISelectedAssetState) has no setter, so asset-only commands such as
    killasset are not wrappable and are left to the console tool.

    The select verb already answers {id, type, name}, so the type check costs
    no extra round trip. It runs before the command: these commands are silent
    on a selection they cannot use, and an empty output frame is otherwise
    indistinguishable from success.
    """
    result = {"sent": line}
    if select is not None:
        chosen = bridge.call("select", {"id": select}) or {}
        result["selected"] = chosen
        kind = chosen.get("type")
        if expect and kind not in expect:
            raise ToolError("this command acts on %s, but state %s is a %s"
                            % (needs, select, kind or "state of unknown type"))
    result["output"] = bridge.call("console", {"line": line})
    return result


def kill_state(args, progress=None):
    """Destroy the selected hab, councilor, army, fleet, or space facility.

    One command covers all five: killstate branches on what is selected, so
    splitting this per target kind would send an identical line each time.
    """
    state = args.get("id")
    if state is None:
        raise ToolError("kill_state needs id=<game state id>")
    return _run_console("killstate", select=state, expect=KILLABLE_STATES,
                        needs="a hab, councilor, army, fleet, or region space "
                              "facility")


def module_power(args, progress=None):
    """One hab module's power switch, through the verb the Habitats screen's
    own toggle uses.

    The DLL owns the gates and the read-back; this checks the two arguments it
    cannot express in a schema (a missing `on` would otherwise reach the verb
    as a depower request from a client that drops false-y values) and turns a
    coerced write into an error, because `applied: false` inside a payload that
    otherwise looks like every other success reads as one.
    """
    module = args.get("module")
    hab = args.get("hab")
    if module is None:
        if hab is None:
            raise ToolError("module_power needs module=<module state id> and "
                            "on=<true|false>; pass hab=<hab id> alone to be "
                            "refused with that hab's modules and their ids")
        # The DLL refuses a bare hab and puts the module listing in the
        # refusal, which is the discovery path, the same shape kill.module
        # uses. That error propagates to the caller as isError; let it, rather
        # than duplicating the listing here or dressing a refusal up as a
        # success this one verb alone would return.
        return bridge.call("module.power", {"hab": hab})
    if not isinstance(args.get("on"), bool):
        raise ToolError("module_power needs on=true (power it up) or on=false "
                        "(shut it down)")

    data = bridge.call("module.power", args)
    if isinstance(data, dict) and data.get("applied") is False:
        raise ToolError(
            "the engine did not take the setting: asked for powered=%s, the "
            "module reads powered=%s afterwards. SetPowerStatus coerces in "
            "silence, so read canPower/canDepower on the module and netPower "
            "on the hab. Full response: %s"
            % (data.get("requested"), data.get("powered"),
               json.dumps(data, separators=(",", ":"), default=str)))
    return data


WAR_ACTIONS = {
    # action: (command, what the map selection must be, takes a target name)
    "declare": ("DeclareWar", "nation", True),
    "peace": ("PeaceOut", "nation", False),
    "occupy": ("Occupy", "nation", False),
    "set_occupied": ("SetOccupied", "region", False),
    "clear_cooldowns": ("ClearRelationsCooldowns", None, False),
}


def war(args, progress=None):
    """War and relations state. Every action but clear_cooldowns is selection-driven."""
    action = args.get("action")
    if action not in WAR_ACTIONS:
        raise ToolError("action must be one of: %s"
                        % ", ".join(sorted(WAR_ACTIONS)))
    command, needs, takes_target = WAR_ACTIONS[action]

    state = args.get("id")
    if needs and state is None:
        raise ToolError("action=%s acts on the map selection; pass id=<%s "
                        "state id>" % (action, needs))
    if state is not None and not needs:
        raise ToolError("action=%s is global and takes no id" % action)

    target = args.get("target")
    if takes_target and not target:
        raise ToolError("action=declare needs target=<nation dataName or "
                        "display name>")
    if target and not takes_target:
        raise ToolError("action=%s takes no target" % action)

    line = command + (" " + target if target else "")
    expect = NATION_STATES if needs == "nation" else REGION_STATES
    return _run_console(line, select=state,
                        expect=expect if needs else None,
                        needs="a nation" if needs == "nation" else "a region")


CP_ACTIONS = {
    # action: (command, needs a nation name)
    "give_one": ("GiveCP", True),
    "give_all": ("GiveAllCPs", True),
    "give_all_everywhere": ("GiveMeAllCPs", False),
    "randomize": ("RandomizeAllCPs", False),
}


def control_points(args, progress=None):
    """Control points by nation NAME, not state id: these commands match names."""
    action = args.get("action")
    if action not in CP_ACTIONS:
        raise ToolError("action must be one of: %s"
                        % ", ".join(sorted(CP_ACTIONS)))
    command, needs_nation = CP_ACTIONS[action]
    nation = args.get("nation")
    faction = args.get("faction")

    if needs_nation and not nation:
        # GiveAllCPs with nothing after it throws inside the handler.
        raise ToolError("action=%s needs nation=<dataName or display name>"
                        % action)
    if not needs_nation and (nation or faction):
        raise ToolError("action=%s takes no nation or faction" % action)
    if action == "give_one" and not faction:
        raise ToolError("action=give_one needs faction=<dataName>")

    line = command
    if nation:
        # Arguments split on commas only.
        line += " " + nation
        if faction:
            line += "," + faction
    return _run_console(line)


def prospect(args, progress=None):
    """One body, or every body. Body names are case-sensitive."""
    body = args.get("body")
    return _run_console("prospect " + body if body else "revealsites")


# Faction hate: one verb, two tools, so the read is not a state-changing call
# as far as the pause gate is concerned and a paused campaign can be read.


def faction_relations(args, progress=None):
    """Read faction hate. The write is set_faction_relation."""
    if "hate" in args:
        raise ToolError(
            "faction_relations only reads. Set a pair with "
            "set_faction_relation faction=<id> other=<id> hate=<number>")
    return bridge.call("faction.relations", args)


def set_faction_relation(args, progress=None):
    """Set one pair's faction hate. The read is faction_relations.

    The verb defaults the subject to the active player and takes the write off
    the presence of `hate`; both ids are demanded here instead, because a write
    aimed at whoever happens to be active is a write nobody can read back.
    """
    if args.get("faction") is None or args.get("other") is None:
        raise ToolError(
            "set_faction_relation needs faction=<id> (who feels it) and "
            "other=<id> (who it is felt toward); faction_relations reads the "
            "table those ids come from")
    hate = args.get("hate")
    if hate is None or isinstance(hate, bool):
        raise ToolError(
            "set_faction_relation needs hate=<number>, the value the pair is "
            "set to; faction_relations reads what it is now")
    return bridge.call("faction.relations", args)


def time_control(args, progress=None):
    action = args.get("action")
    if action and action not in ("pause", "play", "status"):
        raise ToolError("action must be pause, play, or status")
    force = _flag(args, "force", False)
    out = {}
    if args.get("speed") is not None:
        out["speed"] = bridge.call("time.speed",
                                   {"level": _speed_arg(args["speed"])})
    if args.get("run_until"):
        # `force` passed through rather than dropped: the DLL refuses a new
        # target while a mission phase is open, and without this argument the
        # only override was raw time.run_until -- an escape hatch that the
        # refusal message names and the tool did not offer.
        out["run_until"] = bridge.call("time.run_until",
                                       {"date": args["run_until"],
                                        "force": force})
    if action in ("pause", "play"):
        out[action] = bridge.call("time." + action)
    elif action == "status" or not out:
        return bridge.call("query.time")
    return out


def campaign_new(args, progress=None):
    """Start a campaign, and forget the counters that described the last one.

    Thin over the verb; it exists for the reset. A session that ended with two
    fruitless calls would otherwise have the first advance of the NEW campaign
    refused for a stall that belongs to a campaign no longer loaded.
    """
    _reset_run()
    # A campaign with no save behind it. Recorded as such rather than left
    # alone: whatever the run was playing before is not what a crash here
    # should reload.
    _entered_campaign()
    return bridge.call("campaign.new", args)


# Seconds to wait for the campaign to finish unloading after main_menu. The
# teardown is a coroutine plus an async scene load, so it takes frames rather
# than the tens of seconds a load takes; this is generous and bounded.
MENU_WAIT = 120


def main_menu(args, progress=None):
    """Return a loaded campaign to the start screen, in the same process.

    campaign_new is refused while a campaign is loaded; this is the way back
    without a full relaunch.

    The verb answers as soon as it has asked the engine to go; the campaign
    unloads over the following frames. So the waiting is here, and what it
    waits for is the bridge reporting no campaign, which is the same condition
    campaign_new tests.
    """
    seconds = _wait_secs(args.get("wait"), MENU_WAIT)
    if not _campaign_loaded():
        return {"campaign": False,
                "note": "no campaign was loaded; the start screen is already "
                        "up and campaign_new answers from here"}
    out = bridge.call("game.main_menu",
                      {"save": _flag(args, "save", False)})
    # Every counter here described the campaign being unloaded.
    _reset_run()
    t0 = _time.monotonic()
    while _time.monotonic() - t0 < seconds:
        _time.sleep(2)
        if progress:
            progress(int(_time.monotonic() - t0), seconds,
                     "returning to the main menu")
        try:
            if not _campaign_loaded():
                return {"campaign": False, "returned": out,
                        "seconds": round(_time.monotonic() - t0, 1),
                        "next": "the start screen is up; campaign_new or "
                                "load_game from here"}
        except BridgeError:
            # The scene swap can drop the session the way a save load does.
            continue
    raise ToolError(
        "the campaign was still loaded %ds after game.main_menu was accepted. "
        "The engine's teardown is ClearGameData plus an async scene load, so "
        "a stall here is the game's, not the bridge's: read log_tail "
        "which=player for an exception in it, and observe for whether the "
        "process is still there. game_stop then game_start is the way out."
        % seconds)


def save_game(args, progress=None):
    """Write a named save, and remember that this run is now playing it.

    Thin over the verb; it exists for the record. A save written during a run
    is the best thing a crash recovery can come back to, and it is the only
    one the harness itself made -- so a run that saves its own scratch file
    and then crashes reloads that rather than refusing for lack of an
    autosave.
    """
    name = args.get("name")
    if not name:
        raise ToolError("save_game needs name=<save>")
    out = bridge.call("saves.save", {"name": name})
    _RUN["save"] = {"name": name,
                    "extension": _extension_of(out)}
    # The campaign is up and its token is the one this save belongs to. Bound
    # now rather than left for the next change, which would bind it to the
    # campaign AFTER this one.
    _RUN["saveToken"] = _RUN["campaignToken"]
    return out


def _extension_of(saved):
    """The extension of the file saves.save reports writing, or None.

    The reply carries the full path, and which extension the game appends is
    a profile setting, so it is read rather than assumed.
    """
    path = saved.get("path") if isinstance(saved, dict) else None
    if not path:
        return None
    ext = os.path.splitext(str(path))[1]
    return ext or None


def load_game(args, progress=None):
    name = args.get("name")
    if not name:
        raise ToolError("load_game needs name=<save>%s" % _saves_hint())
    a = {"name": name}
    if args.get("extension"):
        a["extension"] = args["extension"]
    try:
        data = bridge.call("saves.load", a)
    except VerbError as e:
        raise ToolError("load failed: %s%s" % (e, _saves_hint()))
    # A different campaign, in the same process: the stall count belonged to
    # the campaign being replaced, and the macro did not.
    _reset_run(keep_macro=True)
    # What the run is playing, so a crash before the first autosave has
    # something of this campaign to come back to.
    _entered_campaign(name, args.get("extension"))
    return {"loading": data, "load": name,
            "next": "the session tears down and reloads (20-60s); poll "
                    "observe until campaign=true"}


def prompts(args, progress=None):
    mode = args.get("mode", "list")
    if mode == "list":
        return bridge.call("prompts.list")
    if mode in ("answer", "drop"):
        a = {}
        if mode == "drop":
            a["all"] = True
        if args.get("type"):
            a["type"] = args["type"]
        # Same default and same meaning as advance's: answered unless asked
        # otherwise, and false leaves an answerable one untouched by drop as
        # well. The narrative prompt nothing can answer is dropped either
        # way; that is not an answer, and leaving it would hold the clock for
        # the rest of the campaign.
        if not _flag(args, "narrative_events", True):
            a["narrative"] = False
        return bridge.call("prompts.dismiss", a)
    raise ToolError("mode must be list, answer, or drop")


def assets(args, progress=None):
    action = args.get("action")
    if action == "list":
        a = {"bundle": args["path"]} if args.get("path") else {}
        return bridge.call("assets.bundles", a)
    if action == "resolve":
        if not args.get("path"):
            raise ToolError("resolve needs path='bundle/asset'")
        return bridge.call("assets.resolve", {"path": args["path"]})
    raise ToolError("action must be 'list' or 'resolve'")


# PowerShell fallback for Windows: System.Drawing CopyFromScreen over the
# whole virtual desktop, saved as PNG to the path substituted in.
_PS_SHOT = (
    "Add-Type -AssemblyName System.Windows.Forms,System.Drawing; "
    "$b = [System.Windows.Forms.SystemInformation]::VirtualScreen; "
    "$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height; "
    "$g = [System.Drawing.Graphics]::FromImage($bmp); "
    "$g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size); "
    "$bmp.Save('%s', [System.Drawing.Imaging.ImageFormat]::Png); "
    "$g.Dispose(); $bmp.Dispose()")


def _shot_attempts(outdir, path):
    """Ordered (name, argv, install-hint) capture attempts; the first whose
    binary exists and exits 0 wins."""
    if IS_WINDOWS:
        return [("powershell",
                 ["powershell", "-NoProfile", "-Command",
                  _PS_SHOT % path.replace("'", "''")],
                 "PowerShell ships with Windows; is it disabled by policy?")]
    return [
        ("cosmic-screenshot",
         ["cosmic-screenshot", "--interactive=false", "--save-dir", outdir],
         "COSMIC desktop"),
        ("gnome-screenshot", ["gnome-screenshot", "-f", path],
         "apt/dnf install gnome-screenshot"),
        ("spectacle", ["spectacle", "-bn", "-o", path],
         "KDE; apt/dnf install kde-spectacle"),
        ("scrot", ["scrot", path], "apt/dnf install scrot"),
        ("import", ["import", "-window", "root", path],
         "ImageMagick; apt/dnf install imagemagick"),
    ]


# Unity finishes a capture at the end of the frame and writes the PNG from
# native code, so the file appears after the verb has already answered.
SHOT_WAIT = 8.0         # seconds per supersize step to appear and settle
SHOT_POLL = 0.15

def _bridge_screenshot():
    """In-engine capture through ui.screenshot: (PNG bytes, None), or
    (None, why-not) to fall back to the desktop tools.

    This is the only capture that sees the game itself. The desktop tools
    photograph the screen, so they return whatever window is on top -- an
    agent's own terminal, twice observed -- and with synthetic input banned
    nothing here can raise the game window.
    """
    if "ui.screenshot" not in bridge.verbs():
        return None, "ui.screenshot (not served by this DLL build)"
    data = bridge.call("ui.screenshot") or {}
    rel = data.get("relativePath")
    if not rel:
        return None, "ui.screenshot (reply carried no relativePath)"
    path = os.path.join(bridge.PERSISTENT_DIR, *rel.split("/"))
    deadline = _time.monotonic() + SHOT_WAIT * max(1, int(data.get("supersize") or 1))
    size = -1
    while _time.monotonic() < deadline:
        _time.sleep(SHOT_POLL)
        try:
            now = os.path.getsize(path)
        except OSError:
            now = -1
        # A size that stopped growing is the only end-of-write signal there
        # is; reading a file mid-write returns a truncated PNG.
        if now > 0 and now == size:
            with open(path, "rb") as f:
                blob = f.read()
            try:
                os.remove(path)
            except OSError:
                pass
            return blob, None
        size = now
    return None, ("ui.screenshot (armed, but no finished file at %s within "
                  "%gs)" % (path, SHOT_WAIT))


def screenshot(args, progress=None):
    # The bridge first when the game is up, the desktop tools when it is not.
    try:
        blob, in_engine = _bridge_screenshot()
    except (BridgeError, VerbError, OSError) as e:
        blob, in_engine = None, "ui.screenshot (%s)" % e
    if blob:
        return {"_content": [{"type": "image",
                              "data": base64.b64encode(blob).decode("ascii"),
                              "mimeType": "image/png"}]}

    outdir = tempfile.mkdtemp(prefix="ti-shot-")
    try:
        path = os.path.join(outdir, "shot.png")
        tried = []
        if in_engine:
            tried.append(in_engine)
        got = None
        for name, argv, hint in _shot_attempts(outdir, path):
            if not shutil.which(argv[0]):
                tried.append("%s (not installed: %s)" % (name, hint))
                continue
            try:
                proc = subprocess.run(argv, capture_output=True, text=True,
                                      timeout=30)
            except subprocess.TimeoutExpired:
                tried.append("%s (timed out after 30s)" % name)
                continue
            except OSError as e:
                tried.append("%s (%s)" % (name, e))
                continue
            if proc.returncode != 0:
                tried.append("%s (exit %d: %s)"
                             % (name, proc.returncode,
                                (proc.stderr or proc.stdout).strip()[:200]))
                continue
            # cosmic-screenshot prints the file it wrote; the others write
            # `path`.
            candidate = proc.stdout.strip()
            if not os.path.isfile(candidate):
                candidate = path
            if not os.path.isfile(candidate):
                tried.append("%s (exit 0 but wrote no file)" % name)
                continue
            got = candidate
            break
        if not got:
            raise ToolError("screenshot failed; tried: %s" % "; ".join(tried))
        with open(got, "rb") as f:
            data = base64.b64encode(f.read()).decode("ascii")
        # Files inside outdir go with the rmtree below; a capture tool that
        # chose its own destination has to be cleaned up by hand.
        if not os.path.abspath(got).startswith(os.path.abspath(outdir)
                                               + os.sep):
            try:
                os.remove(got)
            except OSError:
                pass
        return {"_content": [{"type": "image", "data": data,
                              "mimeType": "image/png"}]}
    finally:
        shutil.rmtree(outdir, ignore_errors=True)


# ------------------------------------------------------ UI view and screen
#
# Two verbs, three tools. Each verb reads with no arguments and drives with
# them, and folding both forms into one tool made the read a state-changing
# call: the pause gate refuses those, so asking which screen is up was refused
# over a stopped clock along with the drives that change it. The drives are
# ui_view and ui_screen; the reads are ui_status, which is both of them.

# The ui_screen arguments that make the verb do something. `manage` is not one:
# it rides on `hab` and means nothing alone. `list` is not one either, and is
# refused outright -- it is the read that moved to ui_status.
UI_SCREEN_DRIVES = ("show", "hide", "hab", "detail", "rename")


def _screen_drives(args):
    """Which of the drive arguments this call actually carries.

    An id counts when it is present at all, because 0 is a state id like any
    other; a flag counts only when it is true, so hide=false is not a drive.
    """
    drives = []
    for key in UI_SCREEN_DRIVES:
        if key in ("hab", "detail"):
            carried = args.get(key) is not None
        elif key == "show":
            carried = bool(args.get(key))
        else:
            carried = _flag(args, key, False)
        if carried:
            drives.append(key)
    return drives


def ui_view(args, progress=None):
    """Move the top-level view. The read is ui_status."""
    if not args.get("view"):
        raise ToolError(
            "ui_view needs view=SolarSystem or view=PoliticalMap. To read the "
            "current view and the active scene without moving anything, call "
            "ui_status")
    return bridge.call("ui.view", args)


def ui_screen(args, progress=None):
    """Open a screen or panel. The read is ui_status."""
    if _flag(args, "list", False):
        raise ToolError(
            "ui_screen does not list. ui_status carries the registered "
            "screens and the active info screen, and changes nothing")
    drives = _screen_drives(args)
    if not drives:
        raise ToolError(
            "ui_screen needs one of show=<screen>, hide=true, hab=<id>, "
            "detail=<id> or rename=true. To read the registered screens and "
            "the active one, call ui_status")
    if _flag(args, "manage", False) and "hab" not in drives:
        raise ToolError(
            "manage is the second argument of HabDetailRequested and means "
            "nothing without one: pass it beside hab=<id>, or leave it out")
    return bridge.call("ui.screen", args)


def ui_status(args, progress=None):
    """What is on the screen, from both UI verbs in one call.

    Neither reply is the whole answer. ui.view has the current view and the
    active Unity scene; ui.screen has the screens this campaign registered and
    which of them is up.
    """
    view = bridge.call("ui.view", {})
    screen = bridge.call("ui.screen", {"list": True})
    out = {}
    if isinstance(view, dict):
        # `changed` is the drive's own read-back and is false on every read.
        out.update((k, v) for k, v in view.items() if k != "changed")
    else:
        out["view"] = view
    if isinstance(screen, dict):
        for key, value in screen.items():
            # `action` is "list" on every read and says nothing.
            if key == "action":
                continue
            # No key comes back from both verbs today. If one ever does, the
            # screen reply keeps its value under a name of its own rather than
            # overwriting a field that means something else.
            out["screen_" + key if key in out else key] = value
    else:
        out["screen"] = screen
    return out
