"""Tool table: schemas, annotations, dispatch.

Handlers come in two kinds: a bridge verb name (string; args pass through and
the verb is feature-checked against the DLL's own registry) or a function
taking (args, progress). Functions return plain data (JSON-encoded here), a
{"_content": [...]} dict for non-text blocks, or raise compose.ToolError. A
handler whose payload is worth returning even though the call failed sets
"_failed": True in the dict; dispatch strips the key and marks the result an
error, so the caller gets the report and the failure both.
"""
import json
import re

import bridge
import codestate
import compose
from compose import ToolError

try:
    import modcheck
except ImportError:
    modcheck = None

PRETTY_LIMIT = 4096     # responses above this stay compact to save tokens
TEXT_LIMIT = 160000     # JSON above this is replaced by an explicit tool error

GAME_DOWN = ("game is not running or the bridge is down (%s) -- call "
             "game_start, then observe. A freshly launched game takes 20-60s "
             "before the bridge answers.")

# A timeout is not the game being down, and saying it is sends the caller to
# relaunch a running game. The bridge answers on the main thread, so a long
# stall there -- a large save, an asset load, a scene change -- outlives the
# call budget with the process perfectly alive.
GAME_BUSY = ("the bridge accepted the call and did not answer in time (%s). "
             "The game is UP: verbs are served on its main thread, so a large "
             "save, an asset load or a scene change stalls them past the "
             "budget. Do NOT call game_start. Wait and retry; observe reports "
             "whether the process is there.")

MODCHECK_MISSING = ("the %s tool is not available: server/modcheck.py is "
                    "missing from this install")

# Every console wrapper carries this: most of these commands print nothing at
# all, so a call that returns empty output is not evidence it did anything.
CONSOLE_SILENT = ("Empty console output does NOT mean the command took effect; "
                  "most of these print nothing either way, so verify with "
                  "query or inspect.")

# Every bridge verb some tool or composite calls; selftest diffs this against
# the DLL's own registry.
BRIDGE_VERBS_USED = {
    "ping", "version", "verbs", "console", "select",
    "query.time", "query.factions", "query.habs", "query.fleets",
    "query.state", "query.template", "query.localize", "query.scenarios", "query.scenario",
    "query.designs", "query.councilors", "query.nations",
    "time.pause", "time.play", "time.speed", "time.run_until",
    "saves.list", "saves.save", "saves.load", "campaign.new",
    "game.main_menu",
    "prompts.list", "prompts.dismiss", "alert.choose",
    "spawn.fleet", "combat.start", "combat.status", "combat.autoresolve",
    "combat.precombat", "combat.stance", "query.autopilot",
    "spawn.hab", "spawn.module", "spawn.army", "spawn.councilor",
    "spawn.alien_site",
    "design.create", "design.delete", "design.auto",
    "kill.module", "faction.relations",
    "module.power", "hab.build_module",
    "mods.list", "assets.bundles", "assets.resolve",
    "ai.control",
    # the screenshot tool prefers this over the desktop capture tools.
    "ui.screenshot",
    # the view and screen drives that make a gated panel photographable.
    "ui.view", "ui.screen",
    # modcheck's session calls these directly.
    "harmony.patches", "query.templateDupes", "query.enums",
    # the crash_the_game test fixture.
    "test.crash_the_game",
}

# Verbs no tool calls, reached through `raw`, but still expected in the DLL.
# selftest folds these into the drift comparison so a build that lost one shows
# up as missing instead of hiding in the unmapped-verb list.
BRIDGE_VERBS_RAW_ONLY = {
    "fleet.bombard", "fleet.land", "fleet.transfer",
    "nation.policies", "nation.set_policy", "nation.set_stat",
    "faction.diplomacy",
    "ui.tooltip", "ui.describe", "ui.options",
    "mission.evaluate",
    "action.list", "action.invoke",
}


def tool_result(text, is_error=False):
    return {"content": [{"type": "text", "text": text}], "isError": is_error}


def json_result(data):
    text = json.dumps(data, separators=(",", ":"), default=str)
    if len(text) <= PRETTY_LIMIT:
        text = json.dumps(data, indent=2, default=str)
    if len(text) > TEXT_LIMIT:
        # The handler has already run. A clipped JSON document is neither a
        # usable result nor evidence that a state-changing command failed.
        return tool_result(json.dumps({
            "error": "response_too_large",
            "message": (
                "Serialized tool response exceeds the text limit; the payload "
                "was omitted. The operation may already have completed. Do not "
                "retry a state-changing command blindly; inspect current state "
                "first. Request less output using limit/fields/contains where "
                "supported, or make the producer return a smaller result."),
            "textLength": len(text),
            "textLimit": TEXT_LIMIT,
        }, indent=2), True)
    return tool_result(text)


# ---------------------------------------------------------------- schema DSL

def s(desc, req=False):
    return {"type": "string", "description": desc, "_req": req}


def i(desc, req=False):
    return {"type": "integer", "description": desc, "_req": req}


# Fractional args exist (army strength, xenoforming level): declaring them integer
# would have a schema-validating client reject the fraction.
def n(desc, req=False):
    return {"type": "number", "description": desc, "_req": req}


def b(desc, req=False):
    return {"type": "boolean", "description": desc, "_req": req}


def o(desc, req=False):
    return {"type": "object", "description": desc, "_req": req}


def arr(desc, req=False, items=None):
    schema = {"type": "array", "description": desc, "_req": req}
    if items is not None:
        schema["items"] = items
    return schema


# A design entry is {slot, name}, a fire mode is {slot, mode}, and an armor
# facing is {material, value}. The bridge takes the integer token and nothing
# else: it will not round 2.5 to 2 or read "3" as three, so a bare string in
# one of these lists and a fractional slot are both errors with or without
# these schemas. Stating the item shape has a validating client catch them
# before the call is made, in a message that names the field instead of the
# verb.
DESIGN_ENTRY = {
    "type": "object",
    "properties": {
        "slot": {"type": "integer",
                 "description": "index into the hull's shipModuleSlots list"},
        "name": {"type": "string", "description": "part template data name"}},
    "required": ["slot", "name"],
    "additionalProperties": False}

FIRE_MODE_ENTRY = {
    "type": "object",
    "properties": {
        "slot": {"type": "integer",
                 "description": "index into the hull's shipModuleSlots list"},
        "mode": {"type": "string", "description": "FireMode name"}},
    "required": ["slot", "mode"],
    "additionalProperties": False}

ARMOR_FACING = {
    "type": "object",
    "properties": {
        "material": {"type": "string",
                     "description": "TIShipArmorTemplate data name"},
        "value": {"type": "integer",
                  "description": "armor points; clamped to what the hull holds"}},
    "required": ["material", "value"],
    "additionalProperties": False}


# ---------------------------------------------------------------- local handlers

def log_tail_tool(args, progress=None):
    which = args.get("which", "player")
    paths = {"player": bridge.PLAYER_LOG, "game": bridge.GAME_LOG}
    if which not in paths:
        raise ToolError("which must be 'player' or 'game'")
    try:
        lines = bridge.tail_log(args.get("lines", 50), args.get("pattern"),
                                paths[which])
    except (OSError, re.error) as e:
        raise ToolError("log_tail failed: %s" % e)
    text = "\n".join(lines) or "(no matching lines)"
    if len(text) > TEXT_LIMIT:
        text = (text[-TEXT_LIMIT:]
                + "\n...[truncated to the last %d chars -- narrow with "
                  "lines/pattern]" % TEXT_LIMIT)
    return {"_content": [{"type": "text", "text": text}]}


def raw_tool(args, progress=None):
    if not args.get("cmd"):
        raise ToolError("raw needs cmd=<bridge verb>")
    return bridge.call(args["cmd"], args.get("args") or {})


def modcheck_tool(args, progress=None):
    if modcheck is None:
        raise ToolError(MODCHECK_MISSING % "modcheck")
    return modcheck.run(bridge, mod=args.get("mod"),
                        check=args.get("check", "all"),
                        scenario=args.get("scenario"),
                        verbose=bool(args.get("verbose")))


def save_check_tool(args, progress=None):
    if modcheck is None or not hasattr(modcheck, "save_check"):
        raise ToolError(MODCHECK_MISSING % "save_check")
    return modcheck.save_check(name=args.get("name"))


def workshop_status_tool(args, progress=None):
    if modcheck is None or not hasattr(modcheck, "workshop_status"):
        raise ToolError(MODCHECK_MISSING % "workshop_status")
    return modcheck.workshop_status(mod=args.get("mod"))


# ---------------------------------------------------------------- tool table
# (name, handler, readOnly, destructive, description, properties)
#
# destructive is what a client's confirmation policy reads, so it is set from
# what the call costs to get wrong, not from how it is implemented: ending the
# game process, overwriting a save, replacing the loaded campaign, answering a
# decision on the player's behalf, and reaching an arbitrary verb are all
# destructive. Every write is, in fact, except the three that only move the
# clock or the camera (time, ui_view, ui_screen), and a read never is.
# test_tool_annotations.py holds the rule.

TOOLS = [
    ("observe", compose.observe, True, False,
     "One-call situation report: bridge and mod version, game process, "
     "campaign loaded, date/speed/paused, blocked flag AND cause, pending "
     "prompt names, combat, player faction. Call it first, and again whenever "
     "unsure; every answer names the next step, including when the game is "
     "down. Also reports the pause limit: a game clock that has not gained "
     "time for %d s (set_pause_limit changes it) is a test failure, and past "
     "it "
     "every state-changing tool is refused. observe warns past half the limit, "
     "leads with PAUSE LIMIT EXCEEDED and the violation record past it, and "
     "always carries stallSession -- the current limit, whether it is the "
     "shipped default, when it was last changed, stallViolations (stall "
     "episodes), refusedCalls, longestStallSeconds and the last violation "
     "record -- so a stall the run has since cleared is still readable "
     "afterwards." % compose.PAUSE_LIMIT_SECONDS,
     {}),
    ("game_start", compose.game_start, False, True,
     "Launch Terra Invicta via Steam and poll until the in-game bridge "
     "answers (20-60s). load=<save name> also loads that save and waits for "
     "the campaign; wait_campaign=<seconds> waits without loading. No-op when "
     "the bridge is already up (still honors load).",
     {"load": s("save name to load once the bridge is up"),
      "extension": s("which file, when both a .gz and a .json of that name "
                     "exist; omit and the profile's own format wins"),
      "wait_campaign": i("seconds to wait for a campaign (default 300 when "
                         "load is given)")}),
    ("game_stop", compose.game_stop, False, True,
     "Kill the game process. Unsaved progress is lost -- save_game first if "
     "it matters. Kill the game when a session is done; never leave it "
     "running idle.",
     {}),
    ("advance", compose.advance, False, True,
     "Unattended run to a target date: max speed, run_until, prompt "
     "dismissal, combat autoresolve. Answers only neutrally-answerable "
     "prompts by default; answer_policy=all force-drops the rest, leaving "
     "those decisions unmade. Narrative event popups ARE answered by default, "
     "by the engine's own AI option strategy, and every one is reported in "
     "narrativeEvents with the option taken; narrativeNotes carries the "
     "exceptions -- an event left standing, an option pressed with no readable "
     "event behind it, an event whose target was already gone so the engine "
     "applied nothing, a prompt dropped with no option applied; "
     "narrativeBoxWaits counts polls spent waiting for a "
     "queued event box, which is not a stop. Set narrative_events=false to "
     "have the run stop on them instead. Returns EARLY with the alert text "
     "and options when a real decision blocks -- answer via alert_choose, "
     "then call advance again. Chunks to its max_seconds budget and says how "
     "to continue. Returns a digest: date span, prompts answered/dropped, "
     "narrative events answered, combats resolved, stop reason. "
     "A call that ran out its budget having moved ZERO game days and named no "
     "cause says stopReason no_progress rather than max_seconds, counts "
     "consecutive such calls across calls, and REFUSES the third in a row "
     "instead of re-arming (force=true runs it anyway, once the cause is "
     "understood). crash_recovered, combat, autopilot_off, "
     "prompts_unreadable, decision and narrative_event hand you something to "
     "do and clear the counter instead; mission_phase, pause_limit, "
     "active_player_moved, prompts_refused, bridge_lost and a terminal "
     "crashed count toward it, because calling again over any of them changes "
     "nothing. The reasons themselves: crashed and "
     "crash_recovered (the game hit its crash handler; the run confirms the "
     "process died, restarts it and reloads the newest save once, then stops "
     "rather than looping, with a next that names game_start), combat (a "
     "fight the autoresolver will not retry "
     "-- read combat_precombat action=status), autopilot_off (the autopilot "
     "macro switched itself off), prompts_unreadable (the queue could not be "
     "read, which is NOT the same as empty), pause_limit (the clock went past "
     "the pause limit without gaining time, so the call stopped instead of "
     "spending its budget on it -- the digest carries the state, the blocking "
     "prompt and how long since any verb ran), mission_phase (a councilor "
     "mission phase stayed open: the call holds the clock PAUSED while one is, "
     "because a semimonthly tick landing inside an open phase is what corrupts "
     "it -- missionPhaseWaits counts the polls spent held, and force=true runs "
     "anyway), narrative_event (narrative_events=llm only: a story event box "
     "is open and the decision is yours -- the options are in `alert`, answer "
     "with alert_choose and call advance again), "
     "active_player_moved (console setfaction moved the active-player seat "
     "off the faction the campaign came up with, so the prompt pass refused "
     "to answer through the human UI's handlers -- the digest names the "
     "faction to put back with setfaction, then call again), "
     "bridge_busy and bridge_lost (a "
     "stalled main thread and a bridge that stopped answering; both name "
     "observe as the next step). A bridge timeout on the per-poll clock read "
     "is one lost poll, counted in lostPolls, not the end of the call -- but "
     "%d unanswered in a row end it as bridge_lost, since each waited the "
     "full verb timeout. Needs a loaded campaign."
     % compose.LOST_POLLS_MAX,
     {"until": s("target date YYYY-MM-DD (or use days)"),
      "days": i("advance this many game days from now"),
      "answer_policy": s("'neutral' (default: skip real decisions) or 'all' "
                         "(force-drop undecided prompts)"),
      # Declared by hand rather than through b(): the argument takes three
      # answers and a boolean schema can carry two. build_defs reads _req off
      # every property, so it is stated here as it is everywhere else.
      "narrative_events": {
          "type": ["boolean", "string"],
          "enum": [True, False, "ai", "llm"],
          "description":
              "how story events are answered. true or \"ai\" (default): the "
              "engine's own AI option strategy presses a button and the run "
              "keeps going. \"llm\": nobody presses it -- the call stops on "
              "the first open box with stopReason narrative_event and the "
              "options in `alert`, you answer with alert_choose and call "
              "advance again. false: ATTENDED ONLY -- nothing here will ever "
              "answer one, so an unattended run stalls on the first story "
              "event. A one-option event is pressed in every mode, since "
              "there is no decision in it, and reported with single: true.",
          "_req": False},
      "autoresolve": b("autoresolve combats (default true); false leaves "
                       "fights pending for a human"),
      "force": b("run even after consecutive zero-progress calls have "
                 "tripped the stall refusal; also runs the clock with a "
                 "mission phase open and does not stop on the pause limit "
                 "(default false)"),
      "max_seconds": i("wall-clock budget per call, default 90")}),
    ("query", compose.query, True, False,
     "List game state. faction narrows habs/fleets/designs/councilors; "
     "contains/limit narrow nations. kind=scenarios enumerates the campaign "
     "picker (category, list order, requiredDLC) -- needed before "
     "campaign_new or smoke_test.",
     {"kind": s("factions, habs, fleets, designs, scenarios, councilors, or "
                "nations", req=True),
      "faction": i("restrict to one faction id"),
      "contains": s("substring filter (nations)"),
      "limit": i("max entries returned")}),
    ("inspect", "query.state", True, False,
     "Reflection dump at depth 1 of any live value, not just registered "
     "states. Start from id=<game state> OR root=<static entry point, e.g. "
     "'GameControl.control'> -- exactly one, never both -- then optionally "
     "path=<dot-path> to walk from there ('currentSpeeds[2].displayName'; "
     "one [n] index per segment, lists and arrays only, no dictionary keys, "
     "16 segments max). A bad segment errors, naming it and the type it "
     "failed on, never a silent null. "
     "include_private=true adds non-public members to both the walk and the "
     "dump. Template lists come back as dataNames, which is how you verify a "
     "grant against finishedProjectNames/completedProjects; ti://docs/playbook "
     "has the rest of what expands. Caveat: path reaches property getters, "
     "and a getter that mutates is on you. Needs a loaded campaign.",
     {"id": i("game state id (ids come from query); XOR root"),
      "root": s("static entry point 'TypeName.Member[.Member]'; XOR id"),
      "path": s("dot-path walked from the start object"),
      "include_private": b("include non-public members (default false)")}),
    ("template", "query.template", True, False,
     "Merged post-mod template data -- ground truth for what the engine "
     "holds, unlike files on disk. Without dataName: the class's data names "
     "(contains/limit filter). With dataName: that one entry as JSON. "
     "fields=[...] projects named members across all matching entries in one "
     "call (bulk mode, offset/limit paging) -- use it for reference checks "
     "instead of one call per entry. Before a campaign starts this is the "
     "un-resolved union of every scenario's data. Class index: ti://templates.",
     {"type": s("template class, e.g. TITraitTemplate", req=True),
      "dataName": s("one entry's dataName"),
      "contains": s("substring filter for the name listing"),
      "fields": arr("member names to project across matching entries (bulk "
                    "mode)"),
      "limit": i("max entries returned"),
      "offset": i("skip this many entries (bulk paging)")}),
    ("localize", "query.localize", True, False,
     "Resolve display text through the engine's LocalizationManager -- the "
     "only truthful source, since mod and scenario text never merges to disk. "
     "keys=[...] for raw keys, or type+dataName to resolve "
     "displayName/summary/description. Each result carries fellBack (true "
     "when the key resolved from a fallback).",
     {"keys": arr("localization keys to resolve"),
      "type": s("template class (with dataName, instead of keys)"),
      "dataName": s("entry whose display strings to resolve"),
      "language": s("language code, e.g. en, deu; default the game's")}),
    ("console", compose.console, False, True,
     "Run a debug-console command with output capture. Terminal arguments "
     "are comma-separated; command matching is case-insensitive SUBSTRING in "
     "dictionary order, so always use exact command names. Some commands "
     "print nothing on success. select=<state id> sets the UI selection "
     "first so selection-dependent commands (addtrait, killstate, ...) have "
     "a target.",
     {"line": s("console command line, e.g. 'triggerevent event_DryHole'",
                req=True),
      "select": i("state id to select before running")}),
    ("autopilot", compose.autopilot, False, True,
     "Play the player faction with the game's UI macro, or stop. NOT the "
     "faction AI, and it picks a RANDOM tech on a tech prompt, so never use "
     "it to sample AI play or to reach a tech state (ai_autopilot for both). "
     "Still the cheap way to buy campaign time: action=on, advance years, "
     "action=off, then test -- a clean toggle you can drop in and out of "
     "mid-campaign. ignore_exceptions=true keeps it running through errors, "
     "and an unattended run wants it: WITHOUT IT THE MACRO SWITCHES ITSELF "
     "OFF AND PAUSES on the first exception in game code, and the run then "
     "keeps resuming a clock nobody is driving. action=status reads the "
     "macro's own engaged state back, so a run can tell that it flipped; "
     "advance polls the same reading while the macro is on and stops with "
     "stopReason autopilot_off when it has. Never run it alongside "
     "ai_autopilot: that refuses to engage while this is on.",
     {"action": s("on, off, or status", req=True),
      "save_cycles": i("autosave every N cycles"),
      "ignore_exceptions": b("keep running through exceptions")}),
    ("ai_autopilot", compose.ai_autopilot, False, True,
     "Hand the player faction to the game's REAL faction AI (the planner "
     "itself, not the autopilot macro), or take it back. The AI plans and "
     "answers its own prompts, so prompts stop freezing the clock. "
     "UNATTENDED RUNS MUST ARM AUTORESOLVE: a combat your engaged faction is "
     "in still takes the human path, so it posts a begin-combat prompt "
     "nothing clicks and the planner starves behind it -- drive engaged "
     "stretches through advance, which autoresolves, not bare time. "
     "smart=brutal (the DEFAULT) plays each planning call at Brutal and holds "
     "game speed at maximum; smart=campaign uses the campaign's own "
     "difficulty. engage smart=brutal is REFUSED when a game update broke the "
     "difficulty machinery (status reports difficultySetter and "
     "difficultyWindows); engage smart=campaign then. Refuses to engage while "
     "the autopilot macro is on. Answer any alert already on screen with "
     "alert_choose BEFORE engaging: an engaged faction is notification-silent "
     "and will not drain it. Everything reverts on release, saves stay "
     "vanilla-shaped (one taken while engaged loads with the faction yours "
     "again), and it releases itself if the campaign changes. Scope of "
     "brutal, side effects and the clock counters: ti://docs/playbook.",
     {"action": s("engage, release, or status", req=True),
      "smart": s("brutal (default) or campaign")}),
    ("grant", compose.grant, False, True,
     "Force one thing complete: kind=project|tech|objective|milestone, "
     "name=<dataName>, optional faction=<dataName> (defaults to the player; "
     "tech is global and takes none). dataNames are case-sensitive; "
     "project names retry with a 'Project_' prefix, so either form works. To "
     "grant EVERYTHING use console line='givealltechs <faction>', but beware: "
     "it also completes every project whose faction prereqs are met, which "
     "will silently pre-satisfy whatever you meant to test.",
     {"kind": s("project, tech, objective, or milestone", req=True),
      "name": s("dataName, case-sensitive", req=True),
      "faction": s("faction dataName; omit for the player faction")}),
    ("give_resources", compose.give_resources, False, True,
     "Resources for the player faction. Bare call gives absurd amounts of "
     "all 14, which is what you want for most tests. resource=<name> with "
     "amount=<n> adds one. amount alone sets the give-everything figure.",
     {"resource": s("FactionResource name, case-sensitive, one of: Money, "
                    "Influence, Operations, Research, Projects, Boost, "
                    "MissionControl, Water, Volatiles, Metals, NobleMetals, "
                    "Fissiles, Antimatter, Exotics"),
      "amount": i("amount to add")}),
    ("kill_state", compose.kill_state, False, True,
     "Destroy or kill one thing by state id: a hab (with the game's 25% ruin "
     "roll), a councilor, a fleet (every ship), an army, or a region space "
     "facility. Any other state type is refused. TRAP: destroying a hab "
     "belonging to your own faction credits the ALIENS as the attacker, which "
     "moves alien-related state you may be measuring. "
     + CONSOLE_SILENT + " Needs a loaded campaign.",
     {"id": i("state id of the hab, councilor, army, fleet, or facility",
              req=True)}),
    ("kill_module", "kill.module", False, True,
     "Destroy ONE hab module, which kill_state cannot: the engine's "
     "DestroyModule is reached through the owning faction's Habitats screen "
     "and no console command touches it. module=<module STATE id>, not a "
     "template name -- a hab can hold several of one template. hab=<hab id> "
     "alone is REFUSED, and the refusal lists that hab's modules and their "
     "ids, which is how you find one. An already-dead module is refused too: "
     "the engine would skip the destruction and still run the last-module "
     "check, so it could take the HAB down and report it as this call's "
     "doing. hate is a SWITCH, not an amount -- the engine applies the "
     "module's tier times a global multiplier and never reads the number -- "
     "and it needs a destroyer. Destroying the hab's last okay module "
     "destroys the hab, and that ALSO needs a destroyer: the engine reports a "
     "hab loss through the destroying faction with no null check, so without "
     "one it throws after the module is already gone. It is refused instead. "
     "A throw out of the engine is caught either way and reported as "
     "engineThrew beside the state read back, never as a bare error. "
     "override_protection is the escape hatch for ONE "
     "engine refusal: a core module, or one carrying the AlienWormhole "
     "special rule, on the ALIEN faction's primary hab. Without the flag that "
     "refusal stands exactly as it does in play and the reply says so. With "
     "it the verb points the alien faction's primaryHab elsewhere for the "
     "single DestroyModule call and restores it afterwards, and the reply "
     "reports primaryHab read back, overrideApplied, and a warning: no play "
     "path produces that state, so anything measured downstream of it says "
     "nothing about play. The flag is REFUSED on that hab's last okay module: "
     "taking the hab down would bypass a second guard, the one in DestroyHab, "
     "and leave primaryHab on an archived hab, so destroy a different module "
     "if module loss is what you are testing. Needs a loaded campaign.",
     {"module": i("module state id (TIHabModuleState)"),
      "hab": i("hab state id: alone it is refused with the hab's module "
               "listing; with module it asserts the module belongs to that "
               "hab"),
      "destroyer": i("faction id credited with the destruction"),
      "hate": n("any value above 0 switches hate and atrocities on; needs "
                "destroyer"),
      "override_protection": b("destroy a protected module on the alien "
                               "primary hab anyway (default false)")}),
    ("faction_relations", compose.faction_relations, True, False,
     "Read faction hate: what one faction feels about another, the diplomacy "
     "state behind a war declaration, a refused trade or the alien response. "
     "Bare call reads the ACTIVE PLAYER's whole table: per faction, hate in "
     "both directions (it is not symmetric -- each faction keeps its own), "
     "the engine's own mood word (Tolerance, Conflicted, War), the pair's "
     "min/max, and whether they are permanent allies. faction=<id> reads "
     "someone else's; other=<id> narrows to one pair. This reads and changes "
     "nothing, so it answers over a stopped clock; hate=<n> here is refused "
     "and names set_faction_relation, which is the write. Needs a loaded "
     "campaign.",
     {"faction": i("faction whose hate is read; defaults to the active "
                   "player"),
      "other": i("the faction it is felt toward; omit to list every "
                 "faction")}),
    ("set_faction_relation", compose.set_faction_relation, False, True,
     "Set one pair's faction hate, the last piece of campaign state with no "
     "other headless path: no console command sets one, and war, "
     "control_points and the spawn fixtures all write around it. Set it to "
     "stage an AI reaction that keys off hate -- a war declaration, a refused "
     "trade, the alien response -- instead of playing until the factions have "
     "reasons. faction=<id> is who feels it, other=<id> is who it is felt "
     "toward, hate=<n> is the value; read the pair and its mood first with "
     "faction_relations, which is also where the relations table comes from. "
     "Every number comes back READ BACK, because the engine reshapes the "
     "write: it clamps to the pair's min/max (max is infinite for a human "
     "subject, finite for the aliens), scales an alien faction's increase "
     "by 0.6 unless the aliens have gone loud, and on an INCREASE spreads "
     "the change to the alien proxy and appeaser by default -- pass "
     "cant_conflagrate=true to keep it to the one pair. A permanent-ally pair "
     "is REFUSED rather than reported as set, since the engine writes nothing "
     "for one. Needs a loaded campaign.",
     {"faction": i("faction whose hate is written", req=True),
      "other": i("the faction it is felt toward", req=True),
      "hate": n("new hate value for the pair", req=True),
      "cant_conflagrate": b("keep the write to this pair instead of letting "
                            "it spread to the alien proxy and appeaser "
                            "(default false, the engine's own default)"),
      "cause": s("label recorded with the change (default 'Bridge Set')")}),
    ("module_power", compose.module_power, False, True,
     "Turn one hab module on or off, the way the Habitats screen's toggle "
     "does. module=<module STATE id> (not a template name, unlike "
     "spawn_module: a hab can hold several modules of one template) and "
     "on=true|false. hab=<hab id> alone is REFUSED, and the refusal carries "
     "that hab's modules and their ids, which is how you find one. Refused "
     "with the failing condition when "
     "the screen would refuse it too: a core module (never toggleable), one under "
     "construction or decommissioning, or CanPower/CanDepower false -- pulling "
     "a generator the hab needs, a shipyard with a queued build, a fleet "
     "repairing or resupplying, or a PowerFirst module (24 vanilla templates "
     "carry it, Shipyard and Farm and the barracks among them) while the hab "
     "is NOT already running a power deficit, or while it still has an "
     "unpowered generator to switch on instead. The engine's setter reports "
     "nothing and "
     "coerces in silence, so the powered flag is READ BACK and a call it did "
     "not take comes back as an error, not a success. Power management may "
     "re-power a module later on its own. Needs a loaded campaign.",
     {"module": i("module state id (TIHabModuleState)"),
      "on": b("true powers it up, false shuts it down"),
      "hab": i("hab state id: alone it is refused with the hab's module "
               "listing; with module it asserts the module belongs to that "
               "hab")}),
    ("war", compose.war, False, True,
     "Nation war, peace, occupation and diplomatic relations. "
     "action=declare (id=<nation>, target=<nation>): full war. "
     "action=peace (id=<nation>): white peace ends EVERY war that nation is "
     "in, not one -- and an exiting ally can inherit one as the new attacker, "
     "so read atWar on BOTH sides. action=occupy (id=<nation>): each army "
     "abroad sets the region it stands in to 100% occupied. "
     "action=set_occupied (id=<region>): that "
     "region becomes 100% occupied by a war enemy, and its nation must "
     "already be at war. action=clear_cooldowns: global, no id, clears every "
     "nation's improve-relations cooldown. target is a case-sensitive "
     "dataName (display name tried second). "
     + CONSOLE_SILENT + " Needs a loaded campaign.",
     {"action": s("declare, peace, occupy, set_occupied, or clear_cooldowns",
                  req=True),
      "id": i("nation state id (declare/peace/occupy) or region state id "
              "(set_occupied)"),
      "target": s("target nation dataName or display name (declare only), "
                  "case-sensitive")}),
    ("control_points", compose.control_points, False, True,
     "Take or scramble national control points. action=give_one: one CP of "
     "one nation to a faction -- the game picks which, you do not. "
     "action=give_all: every CP in one nation. action=give_all_everywhere: "
     "every CP of every nation to you. action=randomize: every CP to a random "
     "human faction. nation and faction are case-sensitive NAMES (dataName or "
     "display name), not state ids; give_one needs both, give_all needs "
     "nation (the bare command throws). TRAP: a nation name that matches "
     "nothing makes give_all fall back to the current map selection, which "
     "kill_state or war may have left behind, so a typo can silently hit "
     "another nation -- check with query kind=nations. " + CONSOLE_SILENT + " Needs a loaded campaign.",
     {"action": s("give_one, give_all, give_all_everywhere, or randomize",
                  req=True),
      "nation": s("nation dataName or display name (give_one, give_all), "
                  "case-sensitive"),
      "faction": s("faction dataName, case-sensitive; give_one requires it, "
                   "give_all defaults to you")}),
    ("prospect", compose.prospect, False, True,
     "Prospect space bodies and reveal their resource sites, for your faction "
     "only. body=<name> prospects that one body and fires a probe-arrived "
     "notification; with no body it prospects EVERY body silently. Body names "
     "are case-sensitive (display name or template name). "
     + CONSOLE_SILENT + " Needs a loaded campaign.",
     {"body": s("space body display name or template name; omit to reveal "
                "every body")}),
    ("time", compose.time_control, False, False,
     "Fine-grained clock control for what advance doesn't cover. Speed 5 is "
     "fastest -- set it before waiting on game days; run_until arms an "
     "auto-pause, polled with action=status. Prompts still freeze the clock; "
     "that loop is what advance automates. Status also reports missionPhase "
     "{active, prepping, planning, collisions}: arming a NEW run_until target "
     "while active is refused, since handing the clock back into an open "
     "councilor mission phase is what corrupts it -- re-arming the target "
     "already armed is a no-op and always allowed, and force=true arms "
     "anyway and accepts the collision. "
     "No args: same as action=status.",
     {"action": s("pause, play, or status"),
      "speed": i("time-speed level 0-5"),
      "run_until": s("arm an auto-pause at this date"),
      "force": b("arm run_until even with a mission phase open, accepting "
                 "the collision (default false)")}),
    ("pause_limit", compose.pause_limit_tool, True, False,
     "Read the pause limit and this session's stall counters. The game clock "
     "not gaining time for this many real seconds is a test failure: past it "
     "every state-changing tool is refused with PAUSE LIMIT EXCEEDED, and "
     "read-only tools answer with that banner in front. A clock crawling at "
     "speed 1 or 2 with no run_until armed counts as stopped. The default is "
     "%d and TIBRIDGE_PAUSE_LIMIT sets the starting value; set_pause_limit "
     "changes it for the session. This read answers over a stalled clock "
     "without the banner, since it is the reading an agent takes to find out "
     "why everything else is being refused."
     % compose.PAUSE_LIMIT_SECONDS,
     {}),
    ("set_pause_limit", compose.set_pause_limit_tool, False, False,
     "Set the pause limit for the rest of this server session. seconds=0 "
     "disables it entirely. Raise it for a setup that genuinely has to run "
     "with the clock stopped, and say in the report that it was raised: every "
     "observe carries the limit, whether it is the shipped default and when "
     "it "
     "was last changed, so a run that raised it cannot hide that it did. The "
     "value is session state -- it lasts until this server process exits and "
     "is written nowhere. Never refused by the limit itself, since it is the "
     "way out of one.",
     {"seconds": n("new limit in real seconds; 0 disables the limit",
                   req=True)}),
    ("save_game", compose.save_game, False, True,
     "Write a named save. Save a scratch name (scratch-<topic>) before "
     "experimenting on a campaign a person is playing. The run remembers the "
     "file, so a crash recovery reloads it rather than refusing for lack of "
     "an autosave. Needs a loaded campaign.",
     {"name": s("save name", req=True)}),
    ("load_game", compose.load_game, False, True,
     "Load a save by name (works from the main menu -- the hands-off entry "
     "point). Tears the running session down; poll observe until "
     "campaign=true (20-60s). A bad name returns the save list.",
     {"name": s("save name (ti://saves lists them)", req=True),
      "extension": s("which file, when both a .gz and a .json of that name "
                     "exist; omit and the profile's own format wins")}),
    ("campaign_new", compose.campaign_new, False, True,
     "Start a campaign from the main menu; refused while a campaign is loaded "
     "or a load is in flight. Returns loading:true -- poll observe until the "
     "campaign is up. The tutorial is forced off. options sets the start "
     "screen's other dropdowns (map size, council count), at most one per "
     "category; query kind=scenarios lists both the scenario dataNames and "
     "those options, by category. Returns the category->dataName map the "
     "launch used.",
     {"scenario": s("scenario data name, e.g. 2070Scenario"),
      "faction": s("faction, e.g. ResistCouncil"),
      "difficulty": i("difficulty 1-4"),
      "options": arr("non-scenario start options by meta template dataName, "
                     "e.g. [\"VeryLightSolarSystem\"]")}),
    ("main_menu", compose.main_menu, False, True,
     "Return a loaded campaign to the start screen, in the same process -- "
     "the way back that campaign_new needs, since it is refused while a "
     "campaign is loaded. This is the engine's own exit-to-menu path (the "
     "one behind the options screen's button), so the campaign is UNLOADED "
     "and anything unsaved is gone: save_game first if it matters. Waits "
     "until the bridge reports no campaign, then campaign_new or load_game "
     "answers. save=true also writes the exit save, which the button always "
     "does and this does not, because it always writes to the same path and "
     "would overwrite a player's continue-save on every lap of a scenario "
     "sweep. Any cinematic still on screen is closed first and reported in "
     "cinematicsClosed: the unload nulls the render texture every video "
     "player points at, and a cinematic left running dereferences it a "
     "frame later and crashes the game. Answers harmlessly with no "
     "campaign loaded.",
     {"save": b("also write the exit save (default false)"),
      "wait": i("seconds to wait for the unload, default 120")}),
    ("prompts", compose.prompts, False, True,
     "Manual prompt control when advance's policy isn't wanted. mode=list: "
     "pending prompts and whether a neutral answer exists (every one freezes "
     "the clock). mode=answer: close modal screens and answer the answerable "
     "ones (type= restricts to one prompt name). A narrative event is answered "
     "only by its own alert box, so a type= filter can never answer one and a "
     "narrative prompt seen before its box is up comes back skipped. mode=answer "
     "ALSO CHANGES STATE INSIDE A FIGHT: PromptSelectSpaceCombatStance is answered "
     "with Defend, submitted through the live precombat screen, so use type= to "
     "keep it out of a pass meant to leave the fight alone. Once every side has a "
     "stance AND the combat requires bidding, the engine queues "
     "PromptSelectSpaceCombatBid on the combat's factions, the answered one "
     "included -- SpaceCombatManager.StanceSubmitted checks "
     "HaveStancesBeenSelected first and returns without queueing anything when "
     "requiresBidding is false. So remaining being non-zero right after a stance "
     "answer is the bid rather than a failure, and remaining dropping to zero is "
     "the ordinary answer for a combat with no bidding. A following mode=drop "
     "force-drops the bid. mode=drop: force-drop what remains -- "
     "the decision goes unmade, the run continues. A narrative event is never "
     "force-dropped: the alert box and the prompt are independent queue "
     "entries and only the box is ever alerted, so removing the prompt leaves "
     "the box to arrive on schedule and apply the option anyway, costs and "
     "grants included. Dropping one hides a pending decision instead of "
     "forfeiting it. Take that decision with narrative_events=false and "
     "alert_choose. "
     "Answer and drop both REFUSE once console setfaction has moved the "
     "active-player seat off the faction the campaign came up with "
     "(ok:false, reason activePlayerMoved, nothing touched): those answers "
     "go through the human UI's handlers, which read the seated faction's "
     "councilors and crash the game on a faction the human is not playing. "
     "The reply names the faction to put back in seatedFaction; setfaction "
     "back to it and call again. mode=list still reads the queue. "
     "The answer pass does drop the narrative prompt nothing "
     "can ever answer -- no event template, or a target already killed, the "
     "engine's own condition -- because that one blocks the clock forever. "
     "Needs a loaded campaign.",
     {"mode": s("list, answer, or drop", req=True),
      "type": s("restrict answer/drop to one prompt name"),
      "narrative_events": b("answer narrative event popups (default true); "
                            "false leaves them standing for alert_choose")}),
    ("alert_choose", "alert.choose", False, True,
     "The open alert box: without option, report whether one is up, its "
     "text, and its live option buttons. With option=N, press that button. "
     "This is how to take a specific in-game decision; advance returns early "
     "and points here. A press reports answered: true when the engine applied "
     "the option, false when the narrative event's target was already gone so "
     "it applied nothing (note says so, and the prompt left behind is cleared "
     "by prompts), null when the event record could not be read. A press the "
     "controller would discard, because it is not taking narrative input yet, "
     "is refused instead of made. detail=true adds what each option would do, "
     "read off the event template rather than the popup: its outcomes with "
     "their chances, effects, costs and grants. That reading crosses the "
     "popup's own ideology gate on purpose -- the game hides an option's "
     "information from a faction whose ideology fails the option's condition, "
     "and infoHiddenFromFaction says when that happened. Under detail the "
     "list is every option the EVENT declares, since the popup leaves a gated "
     "option's button dead and a walk over live buttons alone would drop the "
     "very options that gate hides; such a row says hidden: true and cannot "
     "be pressed. Without detail the list is the live buttons only. Needs a "
     "loaded campaign.",
     {"option": i("0-based option index to press"),
      "detail": b("report each option's outcomes, costs and grants "
                  "(default false)")}),
    ("spawn_fleet", "spawn.fleet", False, True,
     "Test fixture: 1-20 ships of a faction design as a fleet. location MUST "
     "be an orbit or a hab id; a hab site or a fleet is refused, and the "
     "refusal names the way in (spawn into an orbit, then raw "
     "cmd=fleet.land). Without design: the faction design with the most weapon "
     "mounts. The ships always form their own fleet: spawning where the "
     "faction already has one puts a second fleet beside it. shipsTotal is "
     "the new fleet's ship count and equals the ships asked for; a "
     "merged:true beside it is the canary for that stopping being true, "
     "and nothing on the current game build makes it appear. Needs a "
     "loaded campaign.",
     {"faction": i("owning faction id", req=True),
      "ships": i("ship count, 1-20", req=True),
      "location": i("orbit or hab state id to place the fleet at", req=True),
      "design": s("design data name or display name")}),
    ("design_create", "design.create", False, True,
     "Build a ship design from named parts and validate it: hull, drive, power "
     "plant, radiator, three armor facings, propellant tanks, utility modules "
     "and both weapon groups, saved to the faction's design list so "
     "spawn_fleet can build ships from it. The ship designer is a mouse-driven "
     "screen, so this is the only headless way to ask whether a part "
     "combination is buildable -- a modded weapon on a vanilla hull, an "
     "unresearched drive, a refit of a shipped design with one part swapped. "
     "Every part is a template dataName. modules, nose_weapons and "
     "hull_weapons are lists of {slot, name}, where slot is the index into the "
     "hull's shipModuleSlots list; there is no default slot, a bare string is "
     "refused, and a negative slot is an argument error. armor is {nose, "
     "lateral, tail}, each {material, value}; the "
     "value is CLAMPED to what the hull holds and the reply reports what "
     "landed. It VALIDATES rather than trusting: checks runs the engine's ten "
     "ValidTemplate predicates one at a time (hull, drive, radiator, power "
     "plant, tanks>0, three armor materials, role not NoRole, AllowedRole) and "
     "reason names the FIRST failure, which the engine's own bare bool never "
     "does. droppedModules, droppedNoseWeapons and droppedHullWeapons list "
     "entries the engine would silently discard, with why -- an unknown name "
     "or a slot the hull will not take. WATCH THE WEAPON LISTS: "
     "ValidTemplate never reads them, so a weapon in a slot the hull refuses "
     "would otherwise come back valid and save as a warship with no weapon. "
     "Any dropped entry sets reason and refuses the save, so valid means the "
     "ten predicates AND an intact parts list. With "
     "legal (default true) every part is checked with FactionCanBuild and an "
     "illegal one refuses the save, listed under illegalParts; legal=false "
     "saves it anyway, which is a fixture that makes a part buildable without "
     "its research. FactionCanBuild sends an ALIEN faction past its own "
     "no-project shortcut, so an alien design lists nearly every part as "
     "illegal and the reply says so in note; use legal=false for one. "
     "An invalid design registers NOTHING. Needs a loaded "
     "campaign.",
     {"faction": i("designing faction id", req=True),
      "name": s("display name for the class", req=True),
      "hull": s("TIShipHullTemplate data name", req=True),
      "drive": s("TIDriveTemplate data name", req=True),
      "power_plant": s("TIPowerPlantTemplate data name", req=True),
      "radiator": s("TIRadiatorTemplate data name", req=True),
      "tanks": i("propellant tank count, must be > 0 to validate", req=True),
      "role": s("ShipRole name, e.g. MS_Strike, Explorer, ArmyCarrier",
                req=True),
      "armor": dict(o("{nose, lateral, tail}, each {material: "
                      "TIShipArmorTemplate data name, value: armor points}",
                      req=True),
                    properties={"nose": ARMOR_FACING,
                                "lateral": ARMOR_FACING,
                                "tail": ARMOR_FACING},
                    # All three, because ValidTemplate tests all three and a
                    # missing facing fails it.
                    required=["nose", "lateral", "tail"]),
      "modules": arr("utility modules as [{slot, name}]", items=DESIGN_ENTRY),
      "nose_weapons": arr("nose hardpoint weapons as [{slot, name}]",
                          items=DESIGN_ENTRY),
      "hull_weapons": arr("hull hardpoint weapons as [{slot, name}]",
                          items=DESIGN_ENTRY),
      "fire_modes": arr("optional fire modes as [{slot, mode}], mode being a "
                        "FireMode name", items=FIRE_MODE_ENTRY),
      "save": b("register the design with the faction (default true)"),
      "legal": b("refuse the save when a part needs research the faction has "
                 "not done (default true)")}),
    ("design_delete", "design.delete", False, True,
     "Remove a ship design from a faction, by data name or display name (the "
     "same spelling spawn_fleet takes). The engine gates this, and the gate "
     "has THREE false arms, only one of which is a queue: the designing "
     "faction's AI is saving up to buy this design; a live ship of the class "
     "still exists anywhere in the campaign; or a shipyard queue holds it as "
     "a build or as the original of a refit. DeleteShipDesign then does "
     "nothing at all. The live-ship arm is the one a test hits, because "
     "spawn_fleet builds ships of a design in one call, so kill those ships "
     "before deleting. The gate's answer is reported before the call, with "
     "the arm that fired named and the offending ship ids listed, and the "
     "faction's list is read back after, so a refused delete is never "
     "reported as a delete. Needs a loaded campaign.",
     {"faction": i("owning faction id", req=True),
      "design": s("design data name or display name", req=True)}),
    ("design_auto", "design.auto", False, True,
     "Let the engine design the ship: the same DesignShip search the ship "
     "designer's autodesign button runs, which picks the hull, drive, power "
     "plant, radiator, armor, weapons and tank count itself from what the "
     "faction can build. Use it when the question is whether the faction CAN "
     "field a role at all, or to get a working baseline design to refit by "
     "hand with design_create. outcome is the engine's own "
     "ShipDesignerOutcome by name -- Success, NoAvailableHulls, NoHullsForRole, "
     "NoDrives, NoPowerPlants, NoWeapons, ExoticsRequired and the rest -- so a "
     "failure says which part of the search ran out. Saved on success unless "
     "save=false. Needs a loaded campaign.",
     {"faction": i("designing faction id", req=True),
      "role": s("ShipRole name to design for", req=True),
      "range_au": n("desired strategic range in AU, default 1.0"),
      "exotics": b("allow parts that cost exotics (default false)"),
      "antimatter": b("allow parts that cost antimatter (default false)"),
      "save": b("register the design on success (default true)")}),
    ("spawn_hab", "spawn.hab", False, True,
     "Test fixture: found a completed hab (station at an orbit, base at a hab "
     "site or body) for a faction, tier 1-3, optionally with named "
     "TIHabModuleTemplate modules installed and completed. Bypasses cost, "
     "prereqs, build time and placement validity. Modules beyond what the "
     "tier's sectors hold are NOT installed and come "
     "back in modulesNotInstalled with a warning; complete=false leaves every "
     "module on a normal-length build. Needs a loaded campaign.",
     {"faction": i("owning faction id", req=True),
      "location": i("hab site, orbit, Lagrange point, or planet/moon state id",
                    req=True),
      "tier": i("hab tier, 1-3", req=True),
      "modules": arr("TIHabModuleTemplate data names to install"),
      "complete": b("finish construction immediately (default true)")}),
    ("spawn_module", "spawn.module", False, True,
     "Test fixture: install one TIHabModuleTemplate module in a hab's first "
     "free slot and finish it. BYPASSES cost, prereqs, build time, tech "
     "gating and slot validity, and makes no upgrade decision -- "
     "hab_build_module is the one that pays and decides. Never displaces a "
     "standing module; refused when the hab has no free slot. "
     "complete=false leaves it on a normal-length build. Needs a loaded "
     "campaign.",
     {"hab": i("hab state id", req=True),
      "module": s("TIHabModuleTemplate data name", req=True),
      "complete": b("finish construction immediately (default true)")}),
    ("hab_build_module", "hab.build_module", False, True,
     "Queue a hab module build the way a player does: the same "
     "BuildHabModuleAction the Habitats screen's confirm popup submits, "
     "which charges the owner, records the expenditure and starts a "
     "normal-length build. UNLIKE spawn_module it does NOT bypass cost or "
     "prereqs -- it is refused when the hab does not allow the module, when "
     "no slot will take it, and when the owner cannot afford it (the "
     "refusal carries the cost and the treasury). Use spawn_module when you "
     "want the module in place regardless; use this when the decision is "
     "the thing under test. The engine's own upgrade-versus-new-build "
     "choice is made and reported: with no target it prefers a slot holding "
     "a module this one upgrades from over an empty slot, and `decision` "
     "names which it took, what it replaces and whether the cost was the "
     "upgrade price. Needs a loaded campaign.",
     {"hab": i("hab state id", req=True),
      "module": s("TIHabModuleTemplate data name to build", req=True),
      "target": i("build in THIS slot: a TIHabModuleState id (what "
                  "kill_module and module_power call `module`). Omit to let "
                  "the verb pick, upgrades first")}),
    ("spawn_army", "spawn.army", False, True,
     "Test fixture: place an army in a region -- type=human (needs a nation "
     "that owns the region, optional strength), megafauna (alien xenofauna, "
     "needs the region to have a nation), or invader (alien ground invasion). "
     "Bypasses build cost, build time and army economics. Needs a loaded "
     "campaign.",
     {"type": s("human (default), megafauna, or invader"),
      "region": i("region state id", req=True),
      "nation": i("owning nation id (human armies; must own the region)"),
      "strength": n("starting strength, default 1.0 (human armies)")}),
    ("spawn_councilor", "spawn.councilor", False, True,
     "Test fixture: generate a councilor and seat it on a faction's council, "
     "optionally forcing a TICouncilorTypeTemplate job, a home region, or "
     "maxed stats. Skips the hire cost and the recruit pool. Refused when the "
     "council is full. Needs a loaded campaign.",
     {"faction": i("faction id", req=True),
      "job": s("TICouncilorTypeTemplate data name"),
      "region": i("home region state id"),
      "max_stats": b("generate with maximum attributes")}),
    ("spawn_alien_site", "spawn.alien_site", False, True,
     "Test fixture: activate the region's OWN alien site holder. Bypasses the "
     "alien AI's own timing and prerequisites. facility and landing are "
     "refused when the region already has one. The region is a state id, or "
     "region_name as its dataName or display name, since nothing hands you a "
     "region id. kind=xenoforming: level=0 is the ENGINE'S own removal, not "
     "something this verb invents, and the reply reports the level read back "
     "plus extant (the engine's level>0 test). The level is passed through as "
     "given and the engine floors it at zero, so a negative reads back as 0. "
     "Removal only clears the xenoforming: armies and effects it already "
     "spawned stand, and have to be taken out separately. Needs a loaded "
     "campaign.",
     {"region": i("region state id; or pass region_name"),
      "region_name": s("region dataName or display name, used when region is "
                       "omitted; refused when the name matches more than one"),
      "kind": s("facility, crashdown, landing, or xenoforming", req=True),
      "level": n("xenoforming level (kind=xenoforming); 0 is the engine's own "
                 "removal"),
      "first": b("mark as the first crashdown (kind=crashdown)"),
      "days": n("landing duration in days (kind=landing)")}),
    ("combat_start", "combat.start", False, True,
     "Start combat between two fleets. One combat at a time globally: while "
     "another is unresolved this returns started:false. Same-faction pairs "
     "are refused (one faction on both sides wedges the simulation "
     "permanently). Unresolved combat freezes the clock -- follow with "
     "combat_autoresolve or leave it for a human. Needs a loaded campaign.",
     {"attacker": i("attacking fleet id", req=True),
      "defender": i("defending fleet id", req=True),
      "hab": i("hab id if assaulting one")}),
    ("combat_status", "combat.status", True, False,
     "Active/pending combats with participants, stances, and flags; whether "
     "the clock is blocked; the autoresolve machine's state (including its "
     "error when a resolution disarmed). Needs a loaded campaign.",
     {}),
    ("combat_autoresolve", compose.combat_autoresolve, False, True,
     "Resolve a combat headlessly: arms the machine, WAITS for it, and "
     "reports what it did. Closes the post-combat report itself, including "
     "the evade escape report. A stance is checked against what the combat "
     "allows (default prefers Defend). AI-vs-AI combats resolve themselves "
     "and are not armed at all. On a stall it stops waiting and diagnoses "
     "instead of hanging: the phase reached, which one-shots fired, the "
     "precombat canvas and its live buttons. It also clears an orphaned "
     "PromptBeginCombat -- a combat can end with no simulation to accept, "
     "and then no button ever runs and the prompt that screen queued would "
     "hold the clock and block saving for the rest of the campaign. It is "
     "dropped ONLY with the canvas down; while the canvas is up it is the "
     "canvas that freezes the clock and the prompt is not the problem. A "
     "combat that already failed is NOT armed again once a one-shot has "
     "fired on it (a second accept would apply the same simulated damage "
     "twice), once the failure is one no retry changes, or after two "
     "attempts that recorded an error. A wait whose status polls stop being "
     "answered ends with resolved false and touches no prompt: with nothing "
     "read, a standing begin-combat prompt cannot be told from one a live "
     "fight is waiting on. The stop reason is bridge_busy when every "
     "unanswered poll timed out (the main thread is slow, the game is up; "
     "call combat_autoresolve again on the same combat to go back to "
     "waiting, which does not arm a second time) and bridge_lost otherwise. "
     "Needs a loaded campaign.",
     {"combat": i("combat id; defaults to the active one"),
      "stance": s("player-side stance: Pursue, Defend, or Evade"),
      "wait": b("wait for the resolution (default true); false arms and "
                "returns, and you poll combat_status yourself"),
      "max_seconds": i("seconds to wait before diagnosing a stall, "
                       "default 120")}),
    ("combat_precombat", "combat.precombat", False, True,
     "Press the precombat screen's own buttons, for a combat the "
     "autoresolver cannot finish. Use this rather than dropping the "
     "begin-combat prompt: it is the precombat CANVAS that freezes the clock, "
     "and only a button takes it down. START WITH action=status (the "
     "default), which reads the canvas and reports WHICH buttons are live and "
     "presses nothing -- the screen's close and cancel buttons are gone once "
     "the precombat interaction has ended, so a dead end reached after that "
     "point may have no button left here and needs a person. action=close "
     "closes the report, action=cancel calls the attack off entirely "
     "(CancelCombat, no battle), action=reject and action=live hand the fight "
     "to the tactical layer, WHICH NOTHING HEADLESS DRIVES -- prefer cancel "
     "or close in an unattended run. There is no accept: applying an "
     "autoresolve writes damage into the real states and combat_autoresolve "
     "owns that call so it happens exactly once. Refused while "
     "combat_autoresolve is armed, since that drives this same screen. Needs "
     "a loaded campaign.",
     {"action": s("status (default), close, cancel, reject, or live")}),
    ("combat_stance", "combat.stance", False, True,
     "Submit the player's combat stance by hand, for a fight nobody is "
     "autoresolving. combat_autoresolve submits one on its way past, so this "
     "is for the other case: a fight meant to be taken by a person, where "
     "the stance prompt is what freezes the clock and no other tool answers "
     "it. It acts on the live precombat screen and on ITS active player -- "
     "there is no faction or combat argument, because the engine's own "
     "submit takes both from the screen and only the stance from its caller, "
     "so anything else would report a success it never made. The stance is "
     "checked against what the combat allows for that faction and refused by "
     "name if it is not among them. Refused with the precombat canvas down, "
     "since the button being pressed is on it and the screen behind it may "
     "still hold a finished fight, and refused while combat_autoresolve is "
     "armed, which submits the stance itself a step per frame. The reply "
     "reads the stance back off the combat and reports whether the stance "
     "prompt left the queue. The OTHER precombat prompt, PromptBeginCombat, "
     "is not answered here: every button that clears it commits an outcome, "
     "so combat_precombat owns it. Needs a loaded campaign.",
     {"stance": s("Pursue, Defend, or Evade", req=True)}),
    ("modcheck", modcheck_tool, True, False,
     "Acceptance engine for installed mods, returned as structured verdicts "
     "(OK/SHADOWED/MISSING/MISMATCH/DISABLED) with a nextStep per failure. "
     "Summary by default. Run cross-scenario checks from the main menu. UI "
     "rendering and balance are out of scope -- screenshot and assets for "
     "those.",
     {"mod": s("drill into one mod by folder name"),
      "check": s("merge, refs, locale, conflicts, reach, or all (default)"),
      "scenario": s("scope verdicts to one scenario's resolved set"),
      "verbose": b("full per-entry detail")}),
    ("save_check", save_check_tool, True, False,
     "Offline save compatibility: diff a save's template references and "
     "requiredDLC against the current merged universe; names what dangles if "
     "a mod is removed. Works with the game down. Without name: the newest "
     "save.",
     {"name": s("save name; default the newest")}),
    ("workshop_status", workshop_status_tool, True, False,
     "Read each subscribed mod's WorkshopItemInfo.xml and query Steam for "
     "upstream update times; reports stale/outdated/abandoned signals before "
     "any test run. Works with the game down.",
     {"mod": s("restrict to one mod")}),
    ("assets", compose.assets, True, False,
     "action=list: loaded mod/DLC asset bundles and their asset names "
     "(path= narrows to one bundle). action=resolve: load one "
     "'bundle/asset' path and report type and dimensions -- the only check "
     "that catches a typo'd portrait path before a councilor spawns blank.",
     {"action": s("list or resolve", req=True),
      "path": s("bundle name (list) or 'bundle/asset' (resolve)")}),
    ("smoke_test", compose.smoke_test, False, True,
     "Per-scenario acceptance composite: start the campaign, wait, advance "
     "N game days (default 3), scan both logs for new exceptions. Without "
     "scenario: every picker entry in turn, each one returned to the start "
     "screen with main_menu before the next begins, in the same game process. "
     "A menu return that will not complete, or a bridge that has gone, falls "
     "back to a relaunch and the row says so in relaunchedBecause. A "
     "scenario that loses the bridge is that scenario's row -- stopReason "
     "bridge_lost, or bridge_busy when the calls timed out, with reached "
     "naming the step -- and the sweep carries on, so one dead game does not "
     "cost the scenarios after it. Every row carries newExceptions and "
     "logNoiseIgnored, the failed rows included. Runs with a campaign "
     "loaded: campaign_new is refused there, so a loaded campaign goes back "
     "to the start screen first, for a named scenario as well as a sweep, "
     "and the row that paid for it says returnedToMenu.",
     {"scenario": s("one scenario data name; default all picker entries"),
      "days": i("game days to advance per scenario, default 3")}),
    ("selftest", compose.selftest, True, False,
     "Is the running code the code on disk? Reports whether this server "
     "process still holds the server/*.py it was started with (an edit "
     "lands only when the CLIENT reconnects the server -- restarting the "
     "game does not), plus UMM injection state (a game update silently "
     "reverts it), bridge verb registry vs tool table drift, mod version "
     "agreement across ModInfo.json/DLL/server, and the use-mods setting. "
     "Stale server code fails the call: every other line, here and "
     "everywhere else, came out of the old code. Offline checks still run "
     "with the game down.",
     {}),
    ("log_tail", log_tail_tool, True, False,
     "Tail a log locally; works with the game down. which=player (Unity "
     "Player.log: mod loader [Manager] lines, exceptions, crashes) or "
     "which=game (Logs/TerraInvicta.log: template registration, merge and "
     "save messages -- first stop when a mod misbehaves). pattern= regex "
     "filter.",
     {"lines": i("line count, default 50"),
      "pattern": s("regex filter"),
      "which": s("player (default) or game")}),
    ("ui_view", compose.ui_view, False, False,
     "Switch the game's top-level view between the two that are safe to "
     "switch: solar system and political map. view=SolarSystem or "
     "view=PoliticalMap is REQUIRED -- ui_status is what reports the current "
     "view, the active scene and the screens, and it changes nothing. The "
     "switch closes any open info screen first (the vanilla button order, so "
     "the screen does not end up covering the map in a screenshot) and then "
     "moves the view. Pair it with screenshot: the map is a visual surface "
     "and this is the only way to choose which one gets photographed. "
     "view=MainMenu is refused -- main_menu owns unloading a campaign -- and "
     "so is view=SpaceCombat, which would start a fight the combat tools did "
     "not arm. Switching AWAY from the space combat view is refused too, "
     "since it disables the combat manager with the fight still standing. "
     "SolarSystem is refused when the active scene is not SolarSystemScene, "
     "because the engine would load that scene instead and the load does not "
     "finish inside the call. Changes UI state only, never game state. Needs "
     "a loaded campaign.",
     {"view": s("SolarSystem or PoliticalMap", req=True)}),
    ("ui_screen", compose.ui_screen, False, False,
     "Open the info screens -- habitats, fleets, research, nations, intel, "
     "objectives, council -- plus the space object detail panel and the two "
     "rename panels, none of which any other tool reaches. This is what makes "
     "a screen-gated surface photographable: open it here, then screenshot. "
     "One of show, hide, hab, detail or rename is REQUIRED; a call with none "
     "of them is refused, and so is list -- ui_status is what reports the "
     "registered screens and the active one, and it changes nothing. "
     "show=<screen> takes a short name like habitats or fleets as well as a "
     "full controller type name, and an unknown one is refused with the valid "
     "list rather than throwing. hide=true closes the active info screen. "
     "hab=<id> raises the game's own HabDetailRequested for that hab, which "
     "opens the habitats screen with it selected -- the engine defers that "
     "listener to a later frame, so the reply says deferred and you read it "
     "back with ui_status. manage=true rides on hab and is refused without "
     "one. detail=<id> opens the space object detail panel on a hab, fleet or "
     "body and lands inside the call. rename=true presses the rename button "
     "on the panel that is up and reports whether the rename panel went "
     "active; it is refused up front when the hab is not the active player's, "
     "because the engine's handler returns in silence there. rename cannot be "
     "combined with hab in one call (the deferred listener would close the "
     "panel again) and needs a second call after it; with detail it works in "
     "the same call. Renaming itself is not the point -- action.invoke "
     "ChangeHabBio already does that; this puts the panel on screen. Changes "
     "UI state only, never game state. Needs a loaded campaign.",
     {"show": s("screen to open: habitats, fleets, research, nations, intel, "
                "objectives, council, or a controller type name"),
      "hide": b("close the active info screen"),
      "hab": i("hab state id to select on the habitats screen"),
      "manage": b("open the hab straight into management; needs hab "
                  "(default false)"),
      "detail": i("hab, fleet or body state id for the space object detail "
                  "panel"),
      "rename": b("press the rename button on the panel that is up")}),
    ("ui_status", compose.ui_status, True, False,
     "What is on the screen right now, and what else could be: the current "
     "top-level view (solar system or political map), the active Unity scene, "
     "the views that are settable, the info screens this campaign registered "
     "-- habitats, fleets, research, nations, intel, objectives, council -- "
     "and which of them is up. Both UI reads in one call, and it changes "
     "nothing, so it answers over a stopped clock. Call it before a "
     "screenshot to know what will be in the picture, and after a ui_screen "
     "hab=<id> to read back the screen the engine opened a frame later. "
     "ui_view moves the view and ui_screen opens a screen, a detail panel or "
     "a rename panel. Needs a loaded campaign.",
     {}),
    ("screenshot", compose.screenshot, True, False,
     "Capture the game to an image block -- the only check for visual "
     "surfaces (UI, portraits, map state). With the bridge up it captures "
     "INSIDE the process, so an occluded window still yields a true picture. "
     "With the game or that verb down it falls back to desktop capture, which "
     "photographs the screen and therefore returns whatever window is on top. "
     "A failure lists every path it tried.",
     {}),
    ("batch", compose.batch, False, True,
     "Ordered bridge verbs in one call, fail-fast, per-step results -- "
     "fixture setup without N round trips. steps=[{cmd, args}, ...] using "
     "raw verb names (ti://docs/protocol). Past the pause limit it is judged "
     "by the verbs it carries, not by its name: every step a read (query.*, "
     "assets.*, and the named readers listed under raw) runs with the banner, "
     "and anything else is refused like the tool it stands in for.",
     {"steps": arr("[{cmd: verb, args: {...}}, ...]", req=True)}),
    ("raw", raw_tool, False, True,
     "Escape hatch: send any bridge verb directly. Usable for new DLL verbs "
     "before the tool table learns them. Verb reference: ti://docs/protocol. "
     "Past the pause limit it is judged by the verb it carries: query.*, "
     "assets.*, mods.list, harmony.patches, ui.screenshot, ui.tooltip, "
     "ui.describe, combat.status, prompts.list, saves.list and version run "
     "with the banner, and every other verb is refused, since a write does "
     "not become safe over a stopped clock by being sent through here.",
     {"cmd": s("bridge verb name, e.g. query.state", req=True),
      "args": o("verb arguments")}),
    ("crash_the_game", compose.crash_the_game, False, True,
     "TEST FIXTURE. CRASHES THE RUNNING GAME ON PURPOSE AND ENDS THE SESSION. "
     "It raises a real unhandled exception inside the game so the game's own "
     "crash handler runs: the crash panel comes up, the clock is paused and "
     "blocked, every event listener is cleared and input is switched off. The "
     "bridge keeps answering over all of it, which is the point -- this is how "
     "you check that a client detects the crash flag and recovers. Nothing "
     "inside the process undoes it: recovery is game_stop, game_start and "
     "load_game, and anything unsaved is lost. Refused unless "
     "confirm=\"" + compose.CRASH_CONFIRM + "\" is passed. To stop the game "
     "without crashing it, call game_stop instead. Needs a loaded campaign.",
     {"confirm": s("must be exactly \"" + compose.CRASH_CONFIRM
                   + "\"; without it the call is refused", req=False)}),
]


def build_defs():
    defs = []
    for name, _handler, ro, destr, desc, props in TOOLS:
        schema = {"type": "object",
                  "properties": {k: {p: v for p, v in prop.items()
                                     if p != "_req"}
                                 for k, prop in props.items()}}
        required = [k for k, prop in props.items() if prop["_req"]]
        if required:
            schema["required"] = required
        defs.append({"name": name, "description": desc,
                     "inputSchema": schema,
                     "annotations": {"readOnlyHint": ro,
                                     "destructiveHint": destr,
                                     "openWorldHint": False}})
    return defs


TOOL_DEFS = build_defs()
BY_NAME = {t[0]: t[1] for t in TOOLS}
# The readOnly column, by tool name. The pause limit reads it: a tool that
# changes nothing is never refused over a stopped clock, it is only warned.
READ_ONLY = {t[0]: t[2] for t in TOOLS}


def call_verb(verb, args):
    served = bridge.verbs()
    if verb not in served:
        raise ToolError(
            "the running DLL does not serve verb '%s' yet -- rebuild/update "
            "the mod DLL (run install.sh, restart the game)" % verb)
    return bridge.call(verb, args)


def with_banner(result, banner):
    """Put the pause-limit banner in front of a result the limit let through.

    Its own content block rather than a prefix inside the payload: the payload
    is JSON that a client may parse, and the banner is not part of it.
    """
    if not banner:
        return result
    content = result.get("content") or []
    result["content"] = [{"type": "text", "text": banner}] + list(content)
    return result


def handle_call(name, arguments, progress=None):
    # Pin any server module imported since the last call, so the freshness
    # check compares against the moment the code was loaded rather than the
    # moment it is asked about.
    codestate.note()
    # `name` and `arguments` come straight off the wire, so neither is known
    # to be the type it should be: a list name is an unhashable dict key and a
    # list of arguments has no .items(). Both are client errors and are
    # answered as tool errors, ahead of anything that would raise.
    handler = BY_NAME.get(name) if isinstance(name, str) else None
    if handler is None:
        return tool_result("unknown tool: %s" % (name,), True)
    if arguments is None:
        arguments = {}
    if not isinstance(arguments, dict):
        return tool_result("arguments must be a JSON object, got %s"
                           % type(arguments).__name__, True)
    args = {k: v for k, v in arguments.items() if v is not None}
    # The pause limit, ahead of the handler: a game clock that is not gaining
    # time is a test that is not running, and the tools that would build more
    # state over it are refused until it moves. Read-only tools run and carry
    # the banner instead, because reading a stopped campaign is legitimate work
    # for anyone who is not the one driving it. Anything the gate itself cannot
    # decide lets the call through: a watchdog that breaks dispatch is worse
    # than the stall it reports.
    try:
        refusal, banner = compose.pause_gate(name, args, READ_ONLY.get(name))
    except Exception:
        refusal, banner = None, None
    if refusal:
        return tool_result(refusal, True)
    # A campaign the server has not seen before invalidates the counters that
    # describe the last one, and the transition need not have gone through a
    # tool: the in-game exit to the menu and a campaign started from the start
    # screen both reach one with no verb of ours involved. Guarded like the
    # gate above -- a watchdog that breaks dispatch is worse than what it
    # watches for.
    try:
        compose.note_campaign()
    except Exception:
        pass
    try:
        if isinstance(handler, str):
            data = call_verb(handler, args)
        else:
            data = handler(args, progress)
    except ToolError as e:
        return with_banner(tool_result(str(e), True), banner)
    except bridge.VerbError as e:
        return with_banner(tool_result("bridge error: %s" % e, True), banner)
    except bridge.BridgeTimeout as e:
        return with_banner(tool_result(GAME_BUSY % e, True), banner)
    except bridge.BridgeError as e:
        return with_banner(tool_result(GAME_DOWN % e, True), banner)
    except Exception as e:      # never wedge the agent on a handler bug
        return with_banner(
            tool_result("%s failed: %s: %s" % (name, type(e).__name__, e),
                        True), banner)
    if isinstance(data, dict) and "_content" in data:
        return with_banner({"content": data["_content"], "isError": False},
                           banner)
    failed = isinstance(data, dict) and data.pop("_failed", False)
    result = json_result(data)
    result["isError"] = result["isError"] or bool(failed)
    return with_banner(result, banner)
