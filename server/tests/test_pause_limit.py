"""Unit tests for the pause limit.

  python3 -m unittest discover -s server/tests

The limit exists because a stopped game clock is invisible from inside the
harness: every call succeeds, nothing throws, and a whole test hour goes by
with the game paused behind a prompt or parked at a target nobody re-armed.
From outside, an agent thinking and an agent waiting on something that will
never arrive look identical. The DLL measures the stall and puts it on every
response envelope; these cases cover what the server does with it.

Three layers, and the middle one is where the decision lives. `pause_decision`
is pure: a reading, a limit, a tool name, an answer of run, banner or refuse.
`pause_gate` adds the impure parts, the cached reading and its refresh and the
episode bookkeeping. `handle_call` and `advance` are the two boundaries where
the answer has to change what actually happens.

Nothing here needs a running game.
"""
import os
import sys
import unittest
from unittest import mock


SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
# Arms the guard that fails any case which would dial a running game.
import _offline                                     # noqa: E402,F401
import bridge                                       # noqa: E402
import compose                                      # noqa: E402
import tools                                        # noqa: E402
import test_advance                                 # noqa: E402


LIMIT = 300


def _block(seconds, state="paused", since=None, crawl=False):
    """One query.time stall block.

    `since` defaults to unknown rather than to a number. The DLL measures it
    from the verb BEFORE the one being answered, so it is null until a second
    verb has run and is never a value this file can predict; a case that is
    about the field passes its own.
    """
    return {"seconds": seconds, "state": state,
            "sinceLastVerbSeconds": since, "crawl": crawl}


def _fresh_session(case, limit=LIMIT):
    """compose's session state spans tool calls on purpose, so it spans TESTS."""
    saved = dict(compose._STALL)
    episode = dict(compose._EPISODE)
    was = compose.pause_limit()
    compose._STALL.update({"stallViolations": 0, "refusedCalls": 0,
                           "longestStallSeconds": 0, "lastViolation": None})
    compose._EPISODE["open"] = False
    compose.set_pause_limit(limit)
    case.addCleanup(compose.set_pause_limit, was)
    case.addCleanup(compose._EPISODE.update, episode)
    case.addCleanup(compose._STALL.update, saved)


class FakeBridge:
    """The stall cache, the verb the gate refreshes it with, and the queue."""

    def __init__(self, seconds, age=0.0, refresh_to=None, raises=None,
                 state="paused", prompt=None, no_stall_block=False,
                 since=None):
        self.seconds = seconds
        self.age = age
        # What the DLL reports as the silence before this call. None is what
        # it answers until a second verb has run.
        self.since = since
        # What the refresh reads, when one happens. None means "unchanged".
        self.refresh_to = refresh_to
        self.raises = raises
        self.state = state
        self.prompt = prompt
        self.no_stall_block = no_stall_block
        self.calls = []

    def last_stall(self):
        return self.seconds, self.age

    def last_campaign_token(self):
        return "fake-1"

    def last_campaign_process(self):
        return "fake"

    def call(self, verb, args=None, **kw):
        self.calls.append(verb)
        if verb == "prompts.list":
            if self.prompt is None:
                return {"prompts": []}
            return {"prompts": [{"name": self.prompt}]}
        if self.raises is not None:
            raise self.raises
        if verb == "query.time":
            if self.refresh_to is not None:
                self.seconds = self.refresh_to
            self.age = 0.0
            reply = {"date": "2022-01-01", "paused": True}
            if not self.no_stall_block:
                reply["stall"] = _block(self.seconds, self.state, self.since)
            return reply
        return {}

    def count(self, verb):
        return self.calls.count(verb)


class PauseDecisionTest(unittest.TestCase):
    """pause_decision is the whole decision, and it is pure.

    A wrong answer here is either a run that keeps building state over a dead
    clock, or a harness that refuses every call over a reading it could not
    take. Both look like the tools being broken rather than the game being
    stopped, which is why the decision is one testable function.
    """

    def decide(self, name, read_only=False, stall=412.0, args=None,
               limit=LIMIT):
        return compose.pause_decision(name, args or {}, read_only, stall,
                                      limit)

    def test_a_clock_inside_the_limit_or_unmeasured_runs_everything(self):
        self.assertEqual(self.decide("spawn_hab", stall=3.0), "run")
        # The limit itself is not over it.
        self.assertEqual(self.decide("spawn_hab", stall=300.0), "run")
        # A limit of zero disables the guard.
        self.assertEqual(self.decide("spawn_hab", stall=9999.0, limit=0),
                         "run")
        # None is no campaign, a DLL older than the key, or a refresh that
        # failed. Refusing on it would break the harness over a reading it
        # could not take, which is worse than the stall being guarded against.
        self.assertEqual(self.decide("spawn_hab", stall=None), "run")

    def test_over_the_limit_a_fixture_is_refused(self):
        self.assertEqual(self.decide("spawn_hab"), "refuse")
        self.assertEqual(self.decide("grant"), "refuse")
        self.assertEqual(self.decide("console"), "refuse")

    def test_a_read_only_tool_runs_with_the_banner_and_a_write_is_refused(
            self):
        # Reading a stopped campaign is legitimate work for anyone who is not
        # the one driving it: a researcher, a reviewer, the user. Refusing
        # those would make the guard a nuisance to people it is not about.
        # Three tools used to read and write through one name, so each was
        # marked a write and each had its READ refused here: the guard against
        # building state over a dead clock, refusing the calls that build none.
        # The column is read off the table rather than passed by hand, so a
        # tool re-marked in tools.py fails this case.
        for name in ("query", "template", "inspect", "log_tail", "modcheck",
                     "faction_relations", "ui_status"):
            self.assertIs(tools.READ_ONLY[name], True, name)
            self.assertEqual(
                self.decide(name, read_only=tools.READ_ONLY[name]),
                "banner", name)
        for name in ("set_faction_relation", "ui_view", "ui_screen"):
            self.assertIs(tools.READ_ONLY[name], False, name)
            self.assertEqual(
                self.decide(name, read_only=tools.READ_ONLY[name]),
                "refuse", name)

    def test_the_tools_that_move_the_clock_run_clean(self):
        for name in ("advance", "prompts", "alert_choose",
                     "combat_autoresolve", "game_stop", "load_game",
                     "campaign_new", "save_game"):
            self.assertEqual(self.decide(name), "run", name)

    def test_the_limit_pair_reads_and_writes_over_a_stopped_clock(self):
        # The read is the call an agent makes to find out why everything else
        # is being refused, and the write is the way out of the refusal. Both
        # have to answer, and the read has to be marked a read or the gate
        # refuses it.
        self.assertIs(tools.READ_ONLY["pause_limit"], True)
        self.assertEqual(
            self.decide("pause_limit", read_only=True), "banner")
        self.assertIn("pause_limit", compose.PAUSE_NO_BANNER)
        self.assertIs(tools.READ_ONLY["set_pause_limit"], False)
        self.assertEqual(self.decide("set_pause_limit"), "run")

    def test_a_raw_read_runs_with_the_banner(self):
        # Both are general-purpose passes through to anything, so a caller
        # using one has to see what a caller of the tool it stands in for
        # would see.
        for cmd in ("query.time", "assets.list", "prompts.list", "version",
                    "saves.list", "combat.status", "mods.list",
                    "harmony.patches", "ui.screenshot", "ui.tooltip",
                    "ui.describe",
                    # No tool of its own, so raw is the only way to read the
                    # action catalog and this list is the only thing deciding.
                    "action.list",
                    # The two that answer with no campaign loaded.
                    "ping", "verbs"):
            self.assertEqual(self.decide("raw", args={"cmd": cmd}), "banner",
                             cmd)

    def test_a_raw_write_is_refused_like_the_tool_it_stands_in_for(self):
        # The hole this closes is the width of the verb table: every fixture,
        # every console line and every write refused under its own tool name
        # went through raw unchanged.
        for cmd in ("spawn.hab", "console.run", "prompts.dismiss",
                    "saves.save", "time.pause", "campaign.new"):
            self.assertEqual(self.decide("raw", args={"cmd": cmd}), "refuse",
                             cmd)

    def test_a_verb_the_allowlist_does_not_know_is_a_write(self):
        # A verb added to the DLL after this list was written. Treating an
        # unknown verb as a read would reopen the hole on every release.
        self.assertEqual(self.decide("raw", args={"cmd": "future.verb"}),
                         "refuse")

    def test_a_raw_call_that_names_no_verb_is_refused(self):
        # It cannot be judged, and over the limit an unjudgeable passthrough
        # is not given the benefit of the doubt.
        self.assertEqual(self.decide("raw"), "refuse")

    def test_a_batch_is_read_only_when_every_step_is(self):
        reads = [{"cmd": "query.time"}, {"cmd": "prompts.list"}]
        self.assertEqual(self.decide("batch", args={"steps": reads}),
                         "banner")
        mixed = reads + [{"cmd": "spawn.hab"}]
        self.assertEqual(self.decide("batch", args={"steps": mixed}),
                         "refuse")
        self.assertEqual(self.decide("batch", args={"steps": []}), "refuse")
        self.assertEqual(self.decide("batch"), "refuse")

    def test_time_runs_except_for_the_two_ways_it_stops_the_clock(self):
        self.assertEqual(self.decide("time", args={"action": "play"}), "run")
        self.assertEqual(self.decide("time", args={"speed": 5}), "run")
        self.assertEqual(self.decide("time", args={"action": "pause"}),
                         "refuse")
        # Speed 0 is a pause by its other name; the DLL treats it as one too.
        self.assertEqual(self.decide("time", args={"speed": 0}), "refuse")
        # And the wire is JSON: a client that types its arguments loosely
        # sends the level as a string, which the DLL pauses on all the same.
        self.assertEqual(self.decide("time", args={"speed": "0"}), "refuse")
        self.assertEqual(self.decide("time", args={"speed": " 0 "}), "refuse")

    def test_the_forwarded_speed_is_coerced_the_way_the_gate_reads_it(self):
        # Both sides of the same argument. The gate reads "0" as the pause it
        # is; without the same coercion on the way out, the call that gets
        # past the gate is refused by the DLL for its type instead, and the
        # one call that stops the clock lands on neither side of the limit.
        self.assertEqual(compose._speed_arg("0"), 0)
        self.assertEqual(compose._speed_arg(" 5 "), 5)
        self.assertEqual(compose._speed_arg(5), 5)
        self.assertEqual(compose._speed_arg(5.0), 5)
        # Not a whole speed level: left exactly as it came, so the refusal is
        # the DLL's, which is the side that knows what the levels are.
        for value in ("fast", 2.5, True, None, [0]):
            self.assertEqual(compose._speed_arg(value), value)
        self.assertEqual(self.decide("time", args={"speed": "5"}), "run")
        # bool is an int in Python, and False would read as speed 0.
        self.assertEqual(self.decide("time", args={"speed": False}), "run")
        self.assertEqual(self.decide("time", args={"speed": "fast"}), "run")


class BannerTest(unittest.TestCase):
    """The message carries the diagnosis, not just the number."""

    def test_it_names_the_state_the_prompt_and_the_silence(self):
        text = compose.pause_banner(
            412.0, _block(412.0, "blocked", since=900), LIMIT,
            "PromptConfirmMissionAssignments")
        self.assertTrue(text.startswith("PAUSE LIMIT EXCEEDED"))
        self.assertIn("412 s", text)
        self.assertIn("limit of 300 s", text)
        self.assertIn("state=blocked", text)
        self.assertIn("prompt=PromptConfirmMissionAssignments", text)
        self.assertIn("last verb 900 s ago", text)
        # The way out, including advance's own override.
        self.assertIn("advance (force=true", text)

    def test_it_says_so_when_the_diagnosis_is_missing(self):
        text = compose.pause_banner(412.0, None, LIMIT, None)
        self.assertIn("state=unknown", text)
        self.assertIn("prompt=none", text)
        self.assertIn("last verb ? s ago", text)


class LimitSettingTest(unittest.TestCase):
    """The limit starts from the environment and is set for the session."""

    def env_limit(self, value):
        env = dict(os.environ)
        if value is None:
            env.pop("TIBRIDGE_PAUSE_LIMIT", None)
        else:
            env["TIBRIDGE_PAUSE_LIMIT"] = value
        with mock.patch.dict(os.environ, env, clear=True):
            return compose._initial_limit()

    def test_the_default(self):
        self.assertEqual(self.env_limit(None), compose.PAUSE_LIMIT_SECONDS)

    def test_the_environment_override(self):
        self.assertEqual(self.env_limit("30"), 30)

    def test_zero_disables(self):
        self.assertEqual(self.env_limit("0"), 0)

    def test_a_typo_does_not_silently_disable_the_guard(self):
        self.assertEqual(self.env_limit("five minutes"),
                         compose.PAUSE_LIMIT_SECONDS)

    def test_the_read_tool_reads_and_writes_nothing(self):
        _fresh_session(self)
        report = compose.pause_limit_tool({})
        self.assertEqual(report["limitSeconds"], LIMIT)
        # An argument meant for the write reaches a tool that has none, and
        # the limit is unchanged by it.
        compose.pause_limit_tool({"seconds": 30})
        self.assertEqual(compose.pause_limit(), LIMIT)

    def test_the_write_tool_sets_and_records(self):
        _fresh_session(self)
        saved = compose._LIMIT["changedAt"]
        self.addCleanup(compose._LIMIT.__setitem__, "changedAt", saved)
        report = compose.set_pause_limit_tool({"seconds": 30})
        self.assertEqual(report["limitSeconds"], 30)
        self.assertFalse(report["limitIsDefault"])
        self.assertEqual(compose.pause_limit(), 30)
        # Recorded, so a run that raised the limit cannot report a clean
        # sweep without the reading that says it did.
        self.assertIsNotNone(report["limitChangedAt"])

    def test_the_write_tool_needs_a_number(self):
        _fresh_session(self)
        with self.assertRaises(compose.ToolError):
            compose.set_pause_limit_tool({})
        with self.assertRaises(compose.ToolError):
            compose.set_pause_limit_tool({"seconds": "soon"})
        with self.assertRaises(compose.ToolError):
            compose.set_pause_limit_tool({"seconds": -5})
        self.assertEqual(compose.pause_limit(), LIMIT)


class PauseGateTest(unittest.TestCase):
    """pause_gate adds the reading: cached, refreshed when it has gone stale."""

    def setUp(self):
        _fresh_session(self)

    def gate(self, name, args=None, read_only=False, **kw):
        fake = FakeBridge(**kw)
        with mock.patch.object(compose, "bridge", fake):
            return compose.pause_gate(name, args or {}, read_only), fake

    def test_a_stale_reading_costs_one_query_time(self):
        # The refresh IS the reading: query.time answers with the seconds and
        # the diagnosis both.
        (refusal, _), fake = self.gate(
            "spawn_hab", seconds=412.0,
            age=compose.STALL_CACHE_SECONDS + 1, refresh_to=2.0)
        self.assertIsNone(refusal)
        self.assertEqual(fake.count("query.time"), 1)

    def test_a_reading_never_taken_is_refreshed_and_can_refuse(self):
        (refusal, _), fake = self.gate("spawn_hab", seconds=None, age=None,
                                       refresh_to=412.0)
        self.assertIn("PAUSE LIMIT EXCEEDED", refusal)
        self.assertEqual(fake.count("query.time"), 1)

    def test_a_refresh_that_fails_reads_as_unknown(self):
        (refusal, banner), _ = self.gate(
            "spawn_hab", seconds=None, age=None,
            raises=bridge.BridgeError("connection refused"))
        self.assertIsNone(refusal)
        self.assertIsNone(banner)

    def test_a_refresh_that_finds_no_campaign_reads_as_unknown(self):
        (refusal, _), _ = self.gate("spawn_hab", seconds=None, age=None,
                                    raises=bridge.VerbError("no campaign"))
        self.assertIsNone(refusal)

    def test_a_dll_without_the_key_never_refuses(self):
        # Every DLL before this change answers with no clockStall and no stall
        # block, which reads as unknown however often it is refreshed.
        (refusal, _), _ = self.gate("spawn_hab", seconds=None, age=None,
                                    no_stall_block=True)
        self.assertIsNone(refusal)

    def test_a_read_only_tool_gets_the_banner_and_runs(self):
        (refusal, banner), _ = self.gate("query", read_only=True,
                                         seconds=412.0, age=1.0)
        self.assertIsNone(refusal)
        self.assertIn("PAUSE LIMIT EXCEEDED", banner)

    def test_observe_is_not_bannered_because_it_says_it_itself(self):
        (refusal, banner), _ = self.gate("observe", read_only=True,
                                         seconds=412.0, age=1.0)
        self.assertIsNone(refusal)
        self.assertIsNone(banner)

    def test_the_prompt_name_is_read_once_per_episode(self):
        fake = FakeBridge(412.0, age=1.0, prompt="PromptAddressNarrativeEvent")
        with mock.patch.object(compose, "bridge", fake):
            refusal, _ = compose.pause_gate("spawn_hab", {}, False)
            compose.pause_gate("spawn_hab", {}, False)
            compose.pause_gate("grant", {}, False)
        self.assertIn("prompt=PromptAddressNarrativeEvent", refusal)
        self.assertEqual(fake.count("prompts.list"), 1)

    def test_one_episode_is_counted_once_however_many_calls_it_refuses(self):
        fake = FakeBridge(412.0, age=1.0)
        with mock.patch.object(compose, "bridge", fake):
            for _ in range(4):
                compose.pause_gate("spawn_hab", {}, False)
        self.assertEqual(compose._STALL["stallViolations"], 1)
        self.assertEqual(compose._STALL["refusedCalls"], 4)
        self.assertEqual(compose._STALL["longestStallSeconds"], 412)
        # The record names what it first refused and when.
        record = compose._STALL["lastViolation"]
        self.assertEqual(record["tool"], "spawn_hab")
        self.assertEqual(record["stallSeconds"], 412)
        self.assertIsNotNone(record["at"])

    def test_the_clock_moving_ends_the_episode_and_the_next_stall_is_a_new_one(
            self):
        with mock.patch.object(compose, "bridge",
                               FakeBridge(412.0, age=1.0)):
            compose.pause_gate("spawn_hab", {}, False)
        with mock.patch.object(compose, "bridge", FakeBridge(3.0, age=1.0)):
            compose.pause_gate("spawn_hab", {}, False)
        with mock.patch.object(compose, "bridge",
                               FakeBridge(500.0, age=1.0)):
            compose.pause_gate("spawn_hab", {}, False)
        self.assertEqual(compose._STALL["stallViolations"], 2)
        self.assertEqual(compose._STALL["longestStallSeconds"], 500)

    def test_a_disabled_limit_reads_nothing_at_all(self):
        compose.set_pause_limit(0)
        (refusal, banner), fake = self.gate("spawn_hab", seconds=9999.0,
                                            age=1.0)
        self.assertIsNone(refusal)
        self.assertIsNone(banner)
        self.assertEqual(fake.calls, [])

    def test_a_crossing_off_a_fresh_cache_still_records_the_diagnosis(self):
        # The cached seconds come off the last envelope and need no verb, so a
        # crossing detected from them used to write a record with a null state,
        # a null crawl and a null sinceLastVerbSeconds -- and the record is
        # written once per episode, so that was the diagnosis for the whole
        # stall. The refresh happens before the record, not after it.
        (refusal, _), fake = self.gate("spawn_hab", seconds=412.0, age=1.0,
                                       state="blocked", since=12)
        record = compose._STALL["lastViolation"]
        self.assertEqual(record["state"], "blocked")
        self.assertEqual(record["sinceLastVerbSeconds"], 12)
        self.assertIs(record["crawl"], False)
        self.assertEqual(fake.count("query.time"), 1)
        self.assertIn("state=blocked", refusal)

    def test_a_healthy_fresh_cache_costs_no_verb(self):
        # The refresh above is only for readings that are about to refuse.
        (refusal, banner), fake = self.gate("spawn_hab", seconds=12.0, age=1.0)
        self.assertIsNone(refusal)
        self.assertIsNone(banner)
        self.assertEqual(fake.calls, [])

    def test_an_exempt_tool_takes_no_reading_at_all(self):
        # game_stop is the recovery for a wedged main thread, and a query.time
        # against that game costs the whole bridge timeout -- spent before the
        # tool that fixes it ran, for an answer nothing could have used.
        for name in ("game_stop", "game_start", "load_game", "advance",
                     "campaign_new", "alert_choose"):
            (refusal, banner), fake = self.gate(name, seconds=None, age=None)
            self.assertIsNone(refusal, name)
            self.assertIsNone(banner, name)
            self.assertEqual(fake.calls, [], name)

    def test_a_clock_stopping_time_call_still_takes_one(self):
        # It can be refused, so it has to be measured.
        (refusal, _), fake = self.gate("time", args={"action": "pause"},
                                       seconds=None, age=None,
                                       refresh_to=412.0)
        self.assertIn("PAUSE LIMIT EXCEEDED", refusal)
        self.assertEqual(fake.count("query.time"), 1)

    def test_raw_takes_one_because_it_carries_the_banner(self):
        (refusal, banner), fake = self.gate("raw", args={"cmd": "query.time"},
                                            seconds=412.0, age=1.0)
        self.assertIsNone(refusal)
        self.assertIn("PAUSE LIMIT EXCEEDED", banner)
        self.assertEqual(fake.count("query.time"), 1)

    def test_a_raw_write_is_refused_through_the_gate_too(self):
        (refusal, _), _ = self.gate("raw", args={"cmd": "spawn.hab"},
                                    seconds=412.0, age=1.0)
        self.assertIn("PAUSE LIMIT EXCEEDED", refusal)


class EpisodeTest(unittest.TestCase):
    """One continuous stall is one violation, and only a moving clock ends it.

    The reading goes unknown more often than it looks: bridge.clear_stall sets
    it to None on every BridgeError and BridgeTimeout, both of which happen to
    a game whose main thread is busy -- which is a game with a stopped clock.
    """

    def setUp(self):
        _fresh_session(self)

    def note(self, stall):
        block = None if stall is None else _block(stall)
        with mock.patch.object(compose, "bridge", FakeBridge(stall, 0.0)):
            return compose.note_reading(stall, block)

    def test_an_unknown_reading_does_not_close_the_episode(self):
        self.note(412.0)
        first = compose._STALL["lastViolation"]
        self.assertEqual(compose._STALL["stallViolations"], 1)
        # A transport hiccup mid-stall. Closing on it counted the same stall
        # twice and rewrote the record with a later, less useful one.
        self.assertIsNone(self.note(None))
        self.assertTrue(compose._EPISODE["open"])
        self.note(500.0)
        self.assertEqual(compose._STALL["stallViolations"], 1)
        self.assertIs(compose._STALL["lastViolation"], first)

    def test_a_reading_at_or_under_the_limit_closes_it(self):
        self.note(412.0)
        self.note(float(LIMIT))
        self.assertFalse(compose._EPISODE["open"])
        self.note(400.0)
        self.assertEqual(compose._STALL["stallViolations"], 2)


class ObservePauseTest(unittest.TestCase):
    """observe is where an orchestrator reads the stall, during and after."""

    def setUp(self):
        _fresh_session(self)
        # This case is about the stall keys; code drift has its own tests.
        clean = mock.patch.object(compose.codestate, "stale", return_value="")
        clean.start()
        self.addCleanup(clean.stop)

    def observe(self, seconds, state="paused"):
        report = {"bridge": "up", "campaign": True,
                  "time": {"date": "2022-01-01", "paused": True,
                           "stall": _block(seconds, state)}}
        fake = FakeBridge(seconds, 0.0, prompt="PromptBeginCombat")
        with mock.patch.object(compose, "bridge", fake), \
                mock.patch.object(compose, "_observe", return_value=report):
            return compose.observe({})

    def test_a_healthy_clock_says_nothing_about_the_limit(self):
        report = self.observe(4.0)
        self.assertNotIn("pauseLimit", report)

    def test_past_half_the_limit_it_warns(self):
        report = self.observe(200.0)
        self.assertEqual(list(report)[0], "pauseLimit")
        self.assertIn("200 s of a 300 s limit", report["pauseLimit"])
        self.assertNotIn("EXCEEDED", report["pauseLimit"])

    def test_past_the_limit_it_leads_with_the_refusal_and_the_record(self):
        report = self.observe(400.0, state="blocked")
        self.assertEqual(list(report)[0], "pauseLimit")
        self.assertTrue(report["pauseLimit"].startswith(
            "PAUSE LIMIT EXCEEDED"))
        self.assertEqual(report["pauseLimitRecord"]["state"], "blocked")
        self.assertEqual(report["pauseLimitRecord"]["prompt"],
                         "PromptBeginCombat")

    def test_the_session_record_is_carried_whatever_the_clock_is_doing(self):
        report = self.observe(4.0)
        self.assertEqual(report["stallSession"]["stallViolations"], 0)
        self.assertEqual(report["stallSession"]["limitSeconds"], LIMIT)
        self.assertTrue(report["stallSession"]["limitIsDefault"])
        self.observe(900.0)
        report = self.observe(4.0)
        # The point of the record: the clock has moved and every call succeeds
        # again, and the run still has to answer for the stall it had.
        self.assertEqual(report["stallSession"]["stallViolations"], 1)
        self.assertEqual(report["stallSession"]["longestStallSeconds"], 900)
        self.assertEqual(report["stallSession"]["lastViolation"]["state"],
                         "paused")

    def test_a_game_that_is_not_up_is_not_a_stalled_clock(self):
        # The cache outlives the game it described. Without the campaign
        # check, observe would report a 400 s stall against a process that is
        # no longer running.
        fake = FakeBridge(400.0, 0.0)
        with mock.patch.object(compose, "bridge", fake), \
                mock.patch.object(
                    compose, "_observe",
                    return_value={"bridge": "down", "processRunning": False}):
            report = compose.observe({})
        self.assertNotIn("pauseLimit", report)
        self.assertEqual(compose._STALL["stallViolations"], 0)

    def test_a_changed_limit_cannot_hide(self):
        compose.set_pause_limit(30)
        report = self.observe(4.0)
        self.assertEqual(report["stallSession"]["limitSeconds"], 30)
        self.assertFalse(report["stallSession"]["limitIsDefault"])


class DispatchTest(unittest.TestCase):
    """The refusal has to stop the handler; the banner has to reach the text."""

    def setUp(self):
        _fresh_session(self)

    def call(self, name, gate):
        ran = []

        def handler(args, progress=None):
            ran.append(args)
            return {"ok": True}

        with mock.patch.dict(tools.BY_NAME, {name: handler}), \
                mock.patch.object(compose, "pause_gate", gate):
            result = tools.handle_call(name, {})
        return result, ran

    def test_a_refused_tool_never_reaches_its_handler(self):
        result, ran = self.call(
            "spawn_hab", lambda n, a, ro: ("PAUSE LIMIT EXCEEDED: ...", None))
        self.assertTrue(result["isError"])
        self.assertIn("PAUSE LIMIT EXCEEDED", result["content"][0]["text"])
        self.assertEqual(ran, [])

    def test_a_bannered_tool_runs_and_leads_with_the_banner(self):
        result, ran = self.call(
            "query", lambda n, a, ro: (None, "PAUSE LIMIT EXCEEDED: ..."))
        self.assertFalse(result["isError"])
        self.assertEqual(len(ran), 1)
        self.assertIn("PAUSE LIMIT EXCEEDED", result["content"][0]["text"])
        # The payload is still its own block, so a client can still parse it.
        self.assertIn("ok", result["content"][1]["text"])

    def test_a_failing_bannered_tool_still_carries_the_banner(self):
        def boom(args, progress=None):
            raise compose.ToolError("no such template")

        with mock.patch.dict(tools.BY_NAME, {"query": boom}), \
                mock.patch.object(
                    compose, "pause_gate",
                    lambda n, a, ro: (None, "PAUSE LIMIT EXCEEDED: ...")):
            result = tools.handle_call("query", {})
        self.assertTrue(result["isError"])
        self.assertIn("PAUSE LIMIT EXCEEDED", result["content"][0]["text"])

    def test_an_allowed_tool_runs_untouched(self):
        result, ran = self.call("spawn_hab", lambda n, a, ro: (None, None))
        self.assertFalse(result["isError"])
        self.assertEqual(len(ran), 1)
        self.assertEqual(len(result["content"]), 1)

    def test_a_gate_that_raises_does_not_wedge_the_tool(self):
        def gate(name, args, read_only):
            raise RuntimeError("the watchdog itself broke")

        result, ran = self.call("spawn_hab", gate)
        self.assertFalse(result["isError"])
        self.assertEqual(len(ran), 1)

    def test_the_read_only_column_is_what_the_gate_is_told(self):
        seen = []

        def gate(name, args, read_only):
            seen.append((name, read_only))
            return None, None

        def handler(args, progress=None):
            return {"ok": True}

        # Both real handlers are stubbed: what handle_call tells the gate is
        # the whole case, and observe's own answer costs a ping to the bridge
        # and a full report out of whatever game happens to be running.
        with mock.patch.object(compose, "pause_gate", gate), \
                mock.patch.dict(tools.BY_NAME, {"pause_limit": handler,
                                                "observe": handler}):
            tools.handle_call("pause_limit", {})
            tools.handle_call("observe", {})
        self.assertEqual(seen[0], ("pause_limit", True))
        self.assertEqual(seen[1], ("observe", True))


class DeadBridge:
    """A bridge with a stall cached and nothing behind it any more.

    The reading is fresh and over the limit, which is the state the gate takes
    a second query.time in -- and that call is the one the game is no longer
    there to answer. Every verb raises, and the cache clears itself the way
    bridge.clear_stall does on a failed call.
    """

    def __init__(self, seconds, age):
        self.seconds = seconds
        self.age = age
        self.calls = []

    def last_stall(self):
        return self.seconds, self.age

    def last_campaign_token(self):
        return None

    def last_campaign_process(self):
        return None

    def verbs(self, refresh=False):
        raise bridge.BridgeError("connection refused")

    def call(self, verb, args=None, **kw):
        self.calls.append(verb)
        # What a real failed call leaves behind: no reading at all.
        self.seconds, self.age = None, 0.0
        raise bridge.BridgeError("connection refused")


class DeadBridgeTest(unittest.TestCase):
    """A reading that could not be taken must not refuse or record anything.

    The cached seconds are the last thing the game said before it stopped
    answering, and the gate used to keep them when the refresh that was meant
    to replace them failed. So a game that had died mid-stall answered every
    tool with a stall report -- a violation counted, an episode opened, and a
    refusal in place of the error that said the bridge was gone.
    """

    def setUp(self):
        _fresh_session(self)
        clean = mock.patch.object(compose.codestate, "stale", return_value="")
        clean.start()
        self.addCleanup(clean.stop)
        # observe asks the OS whether the process is there; the answer is not
        # what this case is about, and asking costs a pgrep.
        gone = mock.patch.object(compose, "_process_running",
                                 return_value=False)
        gone.start()
        self.addCleanup(gone.stop)

    def call(self, name, args=None):
        fake = DeadBridge(412.0, 1.0)
        # Two patches, because a verb-backed tool reaches the bridge by a
        # second route: the gate and the campaign note go through
        # compose.bridge, and tools.call_verb goes through the bridge module
        # itself, for the verb set and then for the verb. Patching only the
        # first leaves the write below dialing the real port, where a running
        # game answers spawn.hab with its own argument error and the case
        # never sees the dead bridge it is about.
        dead = bridge.BridgeError("connection refused")
        with mock.patch.object(compose, "bridge", fake), \
                mock.patch.object(bridge, "send", side_effect=dead):
            return tools.handle_call(name, args or {}), fake

    def test_observe_over_a_dead_bridge_opens_no_episode(self):
        result, fake = self.call("observe")
        text = result["content"][0]["text"]
        self.assertNotIn("PAUSE LIMIT EXCEEDED", text)
        # observe's own answer, which is the one that says what to do next.
        self.assertIn("game_start", text)
        self.assertEqual(compose._STALL["stallViolations"], 0)
        self.assertEqual(compose._STALL["refusedCalls"], 0)
        self.assertFalse(compose._EPISODE["open"])

    def test_a_write_over_a_dead_bridge_answers_the_bridge(self):
        # Not a pause refusal: the clock is not what is wrong, and a refusal
        # naming the stall would send the caller to move a clock in a game
        # that is not running.
        result, _ = self.call("spawn_hab", {"body": "Earth"})
        text = result["content"][0]["text"]
        self.assertTrue(result["isError"])
        self.assertNotIn("PAUSE LIMIT EXCEEDED", text)
        self.assertIn("game is not running or the bridge is down", text)
        self.assertEqual(compose._STALL["stallViolations"], 0)

    def test_a_bridge_that_is_up_still_refuses_the_write(self):
        # The control. The same stale cache with a live bridge behind it is a
        # refusal, so the case above is about the failed refresh and not about
        # the limit having been switched off.
        fake = FakeBridge(412.0, age=compose.STALL_CACHE_SECONDS + 1,
                          refresh_to=412.0)
        with mock.patch.object(compose, "bridge", fake):
            result = tools.handle_call("spawn_hab", {"body": "Earth"})
        self.assertIn("PAUSE LIMIT EXCEEDED", result["content"][0]["text"])
        self.assertEqual(compose._STALL["stallViolations"], 1)


class StalledBridge(test_advance.FakeBridge):
    """The advance loop's fake bridge, with a stall on every clock reading."""

    def __init__(self, stall, state="blocked", stall_from=None, **kw):
        kw.setdefault("dismiss_reply", test_advance.CLEAR)
        test_advance.FakeBridge.__init__(self, **kw)
        self.stall = stall
        self.state = state
        # The clock reading the stall first crosses the limit on. Before it
        # the clock reads healthy, which is what lets a case put the crossing
        # on a chosen poll instead of at the door: a stall that is already
        # over the limit on poll one ends the call on poll two and no later
        # poll is reached at all.
        self.stall_from = stall_from

    def _stall_now(self):
        if self.stall_from is not None and self.polls < self.stall_from:
            return 12.0
        return self.stall

    def last_stall(self):
        return self._stall_now(), 0.0

    def call(self, verb, args=None, timeout=None):
        reply = test_advance.FakeBridge.call(self, verb, args, timeout)
        if verb == "query.time":
            reply["stall"] = _block(self._stall_now(), self.state)
        return reply


class AdvanceStopTest(unittest.TestCase):
    """advance stops on the limit itself, rather than spending its budget."""

    def setUp(self):
        test_advance._no_sleep(self)
        test_advance._fresh_run_state(self)
        _fresh_session(self)

    def run_advance(self, stall, **kw):
        run_args = {"days": 1, "max_seconds": kw.pop("max_seconds", 600)}
        for passed in ("force", "autoresolve"):
            if passed in kw:
                run_args[passed] = kw.pop(passed)
        fake = StalledBridge(stall, **kw)
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance(run_args)
        return digest, fake

    def test_a_stall_past_the_limit_ends_the_call_with_the_record(self):
        digest, fake = self.run_advance(412.0)
        self.assertEqual(digest["stopReason"], "pause_limit")
        self.assertEqual(digest["pauseLimit"]["stallSeconds"], 412)
        self.assertEqual(digest["pauseLimit"]["state"], "blocked")
        self.assertIn("PAUSE LIMIT EXCEEDED", digest["next"])
        self.assertEqual(compose._STALL["stallViolations"], 1)
        # The first poll still runs the loop body. advance is the way out of
        # a stall: a call that gave up before pressing anything would leave an
        # agent with a refusal from every other tool and nothing left that
        # could clear it.
        self.assertGreaterEqual(len(fake.dismiss_args), 1)

    def test_a_clock_inside_the_limit_runs_the_call_out_as_before(self):
        digest, _ = self.run_advance(12.0)
        self.assertNotEqual(digest["stopReason"], "pause_limit")

    def test_a_disabled_limit_never_stops_the_call(self):
        compose.set_pause_limit(0)
        digest, _ = self.run_advance(9999.0)
        self.assertNotEqual(digest["stopReason"], "pause_limit")

    def test_the_digest_reports_this_poll_and_not_the_episode(self):
        # The episode's record is taken when the stall first crosses the
        # limit, which can be minutes and another tool ago. Handed straight to
        # the digest, it reported those seconds and that state beside a `next`
        # line describing now.
        with mock.patch.object(compose, "bridge",
                               FakeBridge(310.0, age=1.0, state="paused")):
            compose.pause_gate("spawn_hab", {}, False)
        self.assertEqual(compose._STALL["lastViolation"]["stallSeconds"], 310)
        digest, _ = self.run_advance(900.0, state="combat")
        self.assertEqual(digest["stopReason"], "pause_limit")
        self.assertEqual(digest["pauseLimit"]["stallSeconds"], 900)
        self.assertEqual(digest["pauseLimit"]["state"], "combat")
        # Named, or the digest reads as somebody else's refusal.
        self.assertEqual(digest["pauseLimit"]["tool"], "advance")
        self.assertIn("state=combat", digest["next"])
        # Still one stall: the same episode, reported at its current reading.
        self.assertEqual(compose._STALL["stallViolations"], 1)

    def test_a_narrative_box_hold_is_not_ended_by_the_limit(self):
        # A hold takes several polls by design. Ending it on poll two means the
        # run never reaches the `decision` payload that says what to answer --
        # so a stall already past the limit at the door would make every
        # narrative event unanswerable.
        digest, _ = self.run_advance(
            412.0, dismiss_reply=test_advance.AdvanceLoopTest
            .WAITING_FOR_BOX)
        self.assertEqual(digest["stopReason"], "decision")
        # Suppressed, not unseen: the session still counts the stall.
        self.assertEqual(compose._STALL["stallViolations"], 1)

    def test_a_box_that_has_not_rendered_yet_is_not_ended_by_the_limit(self):
        # The half of the box wait with no counter behind it. Before the box
        # renders there is nothing to key a hold off but the wait itself, and
        # the limit used to end the call on poll two -- reporting a stall for
        # a queue that was draining, and never reaching the `decision` payload
        # that names the event.
        digest, _ = self.run_advance(
            412.0, alert={"open": False},
            dismiss_reply=test_advance.AdvanceLoopTest.WAITING_FOR_BOX)
        self.assertEqual(digest["stopReason"], "decision")
        self.assertEqual(compose._STALL["stallViolations"], 1)

    def test_a_mission_phase_hold_is_not_ended_by_the_limit(self):
        # The worst of the collisions this fixes: a phase hold ended by the
        # limit answers with a `next` telling the caller to advance
        # force=true, which is the one thing that runs the clock into the open
        # phase the hold exists to keep it out of.
        digest, _ = self.run_advance(412.0, phase_polls=99)
        self.assertEqual(digest["stopReason"], "mission_phase")
        self.assertEqual(digest["missionPhaseWaits"],
                         compose.MISSION_PHASE_POLLS)
        self.assertEqual(compose._STALL["stallViolations"], 1)

    def test_an_unreadable_prompt_queue_is_not_ended_by_the_limit(self):
        # Same shape: the unknown-queue hold takes five polls to say that
        # nothing here knows what is blocking the clock, and that sentence is
        # the whole value of the stop.
        digest, _ = self.run_advance(412.0, dismiss_error="the queue refused")
        self.assertEqual(digest["stopReason"], "prompts_unreadable")
        self.assertEqual(digest["unreadablePromptPolls"],
                         compose.UNKNOWN_REMAINING_POLLS)

    def test_a_combat_hold_is_not_ended_by_the_limit(self):
        digest, _ = self.run_advance(
            412.0, max_seconds=40,
            combat={"active": {"id": 7}, "pending": False, "autoresolve": {}})
        self.assertNotEqual(digest["stopReason"], "pause_limit")
        self.assertEqual(compose._STALL["stallViolations"], 1)

    def test_a_combat_armed_before_this_call_is_a_hold_too(self):
        # `combat_arms` records what THIS call armed, and a combat already
        # grinding when it started -- armed by an earlier advance, or by
        # combat_autoresolve by hand -- leaves it empty. The machine reports
        # itself armed, so this call arms nothing, and the limit used to end
        # the run on poll two: a fight the harness was already resolving
        # reported as a stalled test.
        digest, fake = self.run_advance(
            412.0, max_seconds=40,
            combat={"active": {"id": 7}, "pending": False,
                    "autoresolve": {"armed": True, "combat": 7}})
        self.assertNotEqual(digest["stopReason"], "pause_limit")
        # Nothing was armed here: the machine already held it.
        self.assertEqual(fake.combat_args, [])
        self.assertEqual(compose._STALL["stallViolations"], 1)

    def test_a_live_combat_the_call_will_not_arm_is_a_hold_too(self):
        # autoresolve=false: the caller means to fly the fight itself, and the
        # loop polls the combat without ever arming anything. Same empty
        # `combat_arms`, same frozen clock, and the same wrong stop.
        digest, fake = self.run_advance(
            412.0, max_seconds=40, autoresolve=False,
            combat={"active": {"id": 7}, "pending": False, "autoresolve": {}})
        self.assertNotEqual(digest["stopReason"], "pause_limit")
        self.assertEqual(fake.combat_args, [])
        self.assertEqual(compose._STALL["stallViolations"], 1)

    def test_a_phase_that_opens_as_the_stall_crosses_is_held_not_stopped(self):
        # The order of the two checks, which is the whole case. The phase
        # opens on the same poll the stall passes the limit. Tested before the
        # phase decision is taken, `holding_phase` is still last poll's false,
        # nothing reads as a hold, and the call stops on pause_limit with a
        # `next` telling the caller to advance force=true -- straight into the
        # collision the hold exists to prevent.
        digest, _ = self.run_advance(412.0, phase_from=5, stall_from=5)
        self.assertEqual(digest["stopReason"], "mission_phase")
        self.assertEqual(digest["missionPhaseWaits"],
                         compose.MISSION_PHASE_POLLS)
        self.assertEqual(compose._STALL["stallViolations"], 1)

    def test_force_suppresses_the_stop(self):
        digest, _ = self.run_advance(412.0, force=True, max_seconds=40)
        self.assertNotEqual(digest["stopReason"], "pause_limit")
        self.assertEqual(compose._STALL["stallViolations"], 1)


class StallCacheTest(unittest.TestCase):
    """bridge caches the stall off every envelope, ok or error."""

    def setUp(self):
        saved = dict(bridge._stall)
        bridge._stall.update({"seconds": None, "at": None})
        self.addCleanup(bridge._stall.update, saved)

    def test_every_envelope_is_a_reading(self):
        # Success and error envelopes both: the calls that fail are exactly
        # where a client would otherwise learn nothing about the clock. A null
        # stall is no campaign loaded, unknown but still a reading, so the age
        # moves and the gate does not ask again on every call. bool is an int
        # in Python, and True would read as a one-second stall.
        for envelope, want in (
                ({"id": 1, "ok": True, "clockStall": 12.5, "data": {}}, 12.5),
                ({"id": 1, "ok": False, "clockStall": 61.0,
                  "error": "arg 'id' is required"}, 61.0),
                ({"id": 1, "ok": True, "clockStall": None}, None),
                ({"id": 1, "ok": True, "clockStall": True}, None)):
            with self.subTest(envelope=envelope):
                bridge._stall.update({"seconds": None, "at": None})
                bridge._note_stall(envelope)
                seconds, age = bridge.last_stall()
                self.assertEqual(seconds, want)
                self.assertIsNotNone(age)
                self.assertLess(age, 5)

    def test_no_campaign_clears_the_cache(self):
        # A stall belongs to a campaign and ends with it: a reading taken
        # before a return to the main menu must not survive it.
        bridge._note_stall({"id": 1, "ok": True, "clockStall": 400.0})
        with mock.patch.object(
                bridge, "send",
                return_value={"id": 2, "ok": False, "error": "no campaign"}):
            with self.assertRaises(bridge.VerbError):
                bridge.call("query.time")
        seconds, age = bridge.last_stall()
        self.assertIsNone(seconds)
        self.assertIsNotNone(age)


if __name__ == "__main__":
    unittest.main()
