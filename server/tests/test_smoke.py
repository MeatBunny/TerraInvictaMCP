"""Unit tests for what decides a smoke test's verdict, for the sweep that
collects those verdicts, and for the combat tool's orphan check.

  python3 -m unittest discover -s server/tests

The classifiers share a failure mode: they decide, silently, that something is
not worth reporting. A log line wrongly allowlisted turns a real defect into a
pass, and a begin-combat prompt wrongly identified gets a live one dropped off
the queue. Neither shows up anywhere downstream.

The sweep has one of its own. It is the longest-running tool here, minutes per
scenario, and the game can die inside any of them; a sweep that let that end
the call threw away every scenario it had already tested and never reached the
ones after.

None of it needs a game, a bridge or game assemblies.
"""
import os
import sys
import tempfile
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


# Real lines. The two noise ones are Log::Error calls inside
# TISpaceShipTemplate.UnnormalizedTemplateSpaceCombatValue, which substitutes a
# fallback and carries on; the third names a mod's own data and is exactly what
# a smoke test exists to catch.
NAN_LIFETIME = "[ERROR] expectedLifetime_s was NaN!"
NAN_VALUE = "[ERROR] _unnormalizedCombatValue was invalid! NaN"
MOD_DEFECT = "[ERROR] Bad upgradesFromName DebrisClearingArray in PointDefenseArray"
REAL_EXCEPTION = "NullReferenceException: Object reference not set to an instance"


class LogNoiseTest(unittest.TestCase):
    """The allowlist takes engine arithmetic and nothing else."""

    def test_an_exception_is_not_noise(self):
        self.assertIsNone(compose._log_noise(REAL_EXCEPTION))


class ScanLogsTest(unittest.TestCase):
    """_scan_logs splits what was written since the mark, and counts what it
    set aside rather than dropping it."""

    def _scan(self, before, after, **kw):
        with tempfile.TemporaryDirectory() as d:
            path = os.path.join(d, "game.log")
            with open(path, "w") as f:
                f.write(before)
            marks = {"game": (path, os.path.getsize(path))}
            with open(path, "a") as f:
                f.write(after)
            return compose._scan_logs(marks, **kw)

    def test_only_lines_after_the_mark_are_read(self):
        found = self._scan(REAL_EXCEPTION + "\n", "quiet\n")
        self.assertEqual(found["failures"], [])
        self.assertEqual(found["noise"], {})
        self.assertFalse(found["capped"])

    def test_noise_is_counted_not_dropped(self):
        found = self._scan("", (NAN_LIFETIME + "\n") * 3 + NAN_VALUE + "\n")
        self.assertEqual(found["failures"], [])
        self.assertEqual(sorted(found["noise"].values()), [1, 3])

    def test_a_real_failure_survives_beside_the_noise(self):
        found = self._scan("", NAN_LIFETIME + "\n" + MOD_DEFECT + "\n")
        self.assertEqual(len(found["failures"]), 1)
        self.assertIn("Bad upgradesFromName", found["failures"][0])
        self.assertEqual(sum(found["noise"].values()), 1)

    def test_the_cap_is_reported_rather_than_silent(self):
        found = self._scan("", (MOD_DEFECT + "\n") * 5, cap=2)
        self.assertEqual(len(found["failures"]), 2)
        self.assertTrue(found["capped"])


class BoxOpenTest(unittest.TestCase):
    """_box_open decides which narrative leash a hold gets.

    The open, closed and no-live-option boxes are driven through the advance
    loop in test_advance; None and {} never reach the loop.
    """

    def test_nothing_is_not(self):
        self.assertFalse(compose._box_open(None))
        self.assertFalse(compose._box_open({}))


class BeginCombatPromptsTest(unittest.TestCase):
    """_begin_combat_prompts picks the rows the combat tool may drop.

    prompts.list answers {activePlayer, blocked, prompts:[...]} with faction
    and nation prompts in ONE list under a `scope` field, so a reader that
    expected a per-scope key would find nothing and never clear an orphan.
    """

    LISTING = {"activePlayer": {"id": 1}, "blocked": True, "prompts": [
        {"name": "PromptSelectTech", "scope": "faction"},
        {"name": "PromptBeginCombat", "scope": "faction"},
        {"name": "PromptBeginCombat", "scope": "nation"},
    ]}

    def test_it_finds_them_in_the_flat_list_and_leaves_the_rest(self):
        self.assertEqual(
            [r["scope"] for r in compose._begin_combat_prompts(self.LISTING)],
            ["faction", "nation"])

    def test_an_unreadable_reply_finds_none(self):
        self.assertEqual(compose._begin_combat_prompts(None), [])
        self.assertEqual(compose._begin_combat_prompts({}), [])
        self.assertEqual(compose._begin_combat_prompts({"prompts": "no"}), [])


class SweepBridge:
    """A bridge stand-in with just enough campaign lifecycle for the scenario
    sweep to walk it, and a way to die partway through.

    The sweep's own steps are what is under test, so each reply is the shape
    the step that reads it expects and nothing more. `dies_on` names the
    scenario whose `campaign.new` takes the bridge down, which is the failure
    this fake exists for: a game that goes down in the middle of a run that
    has other scenarios still to test. Once down, every `call` raises.
    """

    def __init__(self, scenarios, logs, dies_on=None, error=None,
                 refuse_first=0, refuse_with="start menu is not initialized"):
        self.scenarios = list(scenarios)
        self.logs = logs
        self.dies_on = dies_on
        # How many campaign.new calls are refused, and with what text, the
        # way a start scene still loading refuses before it changes anything.
        self.refuse_first = refuse_first
        self.refuse_with = refuse_with
        self.error = error or bridge.BridgeError("connection refused")
        self.down = False
        self.campaign = False
        self.calls = []

    # _log_marks reads both log paths off the bridge module it is patched
    # over. Fixture files, so a case reads no line of the developer's own
    # install and every row's scan is empty by construction.
    @property
    def PLAYER_LOG(self):
        return self.logs["player"]

    @property
    def GAME_LOG(self):
        return self.logs["game"]

    def call(self, verb, args=None, timeout=None):
        if self.down:
            raise self.error
        self.calls.append(verb)
        if verb == "query.scenarios":
            # A second category beside the scenarios, the way the picker
            # answers: map size and council count are start options, and
            # campaign.new refuses either as a scenario name.
            return {"categories": [
                {"name": "Scenario",
                 "options": [{"dataName": n} for n in self.scenarios]},
                {"name": "Map Size",
                 "options": [{"dataName": "SmallMap"}]}]}
        if verb == "query.time":
            if not self.campaign:
                raise bridge.VerbError("no campaign is loaded")
            return {"date": "2022-01-01", "blocked": False, "paused": False,
                    "speed": 5}
        if verb == "game.main_menu":
            self.campaign = False
            return {"left": True}
        if verb == "campaign.new":
            if (args or {}).get("scenario") == self.dies_on:
                self.down = True
                raise self.error
            if self.refuse_first:
                self.refuse_first -= 1
                raise bridge.VerbError(self.refuse_with)
            self.campaign = True
            return {"started": (args or {}).get("scenario")}
        return {}

    def count(self, verb):
        return self.calls.count(verb)

    def last_stall(self):
        return None, 0.0

    def last_campaign_token(self):
        return "sweep-1"

    def last_campaign_process(self):
        return "sweep"


class SweepResilienceTest(unittest.TestCase):
    """A transport failure inside one scenario is that scenario's row.

    The sweep is the longest-running tool there is, and the thing most likely
    to kill the game is the thing it is testing. Until this held, a bridge
    that died in the first of six scenarios raised out of the loop: the rows
    already collected went with it, and the five scenarios after it were
    never started, never reported, and indistinguishable from five that
    passed.
    """

    SCENARIOS = ["Scenario_A", "Scenario_B", "Scenario_C"]

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.logs = {}
        for which in ("player", "game"):
            path = os.path.join(tmp.name, which + ".log")
            with open(path, "w"):
                pass
            self.logs[which] = path
        self.fake = None
        self.advances = []
        self.advance_error = None
        self.starts = []
        self.stops = []
        self.start_error = None
        self._patch("advance", self._advance)
        self._patch("game_stop", self._stop)
        self._patch("game_start", self._start)
        # The sweep sleeps through the menu return, the relaunch and the
        # campaign wait. Nothing here measures time, so the sleeps are the
        # whole cost of a case.
        saved_sleep = compose._time.sleep
        compose._time.sleep = lambda s: None
        self.addCleanup(setattr, compose._time, "sleep", saved_sleep)
        # compose._RUN spans tool calls on purpose, which means it spans
        # tests: campaign_new resets it and records an entry, and leaving
        # that behind lands on whichever case runs next.
        saved_run = dict(compose._RUN)
        self.addCleanup(compose._RUN.update, saved_run)

    def _patch(self, name, fn):
        patcher = mock.patch.object(compose, name, fn)
        patcher.start()
        self.addCleanup(patcher.stop)

    def _advance(self, args, progress=None):
        """The advance chunk, stubbed. What it does with the clock is
        test_advance's subject; what the sweep does when it fails is this
        one's."""
        self.advances.append(args)
        if self.advance_error is not None:
            error, self.advance_error = self.advance_error, None
            raise error
        return {"stopReason": "reached", "daysAdvanced": args.get("days")}

    def _stop(self, args, progress=None):
        self.stops.append(args)
        return {"stopped": True}

    def _start(self, args, progress=None):
        self.starts.append(args)
        if self.start_error is not None:
            raise self.start_error
        # A new process answers again, at the start screen.
        self.fake.down = False
        self.fake.campaign = False
        return {"launched": True}

    def _run(self, args=None, campaign=False, **kw):
        self.fake = SweepBridge(self.SCENARIOS, self.logs, **kw)
        # A campaign already up at entry, which is what a client asking for
        # one scenario in the middle of a session has.
        self.fake.campaign = campaign
        with mock.patch.object(compose, "bridge", self.fake):
            return compose.smoke_test(dict(args or {}, days=1))

    # ------------------------------------------------------------ the sweep

    def test_a_start_scene_still_loading_is_waited_out(self):
        out = self._run(refuse_first=2)
        self.assertTrue(all(r["ok"] for r in out["results"]))
        self.assertEqual(self.fake.count("campaign.new"),
                         len(self.SCENARIOS) + 2)

    def test_a_start_scene_that_never_comes_up_is_the_row_error(self):
        out = self._run(refuse_first=compose.HANDOFF_TRIES)
        row = out["results"][0]
        self.assertIn("start menu is not initialized", row["error"])
        self.assertEqual(row["reached"], "campaign.new")

    def test_other_refusals_are_not_retried(self):
        self.fake = SweepBridge(self.SCENARIOS, self.logs, refuse_first=1,
                                refuse_with="a campaign is loaded already")
        with mock.patch.object(compose, "bridge", self.fake), \
                self.assertRaises(bridge.VerbError):
            compose.campaign_new({"scenario": "Scenario_A"})
        self.assertEqual(self.fake.count("campaign.new"), 1)

    def test_a_retried_handoff_says_so(self):
        self.fake = SweepBridge(self.SCENARIOS, self.logs, refuse_first=1)
        with mock.patch.object(compose, "bridge", self.fake):
            reply = compose.campaign_new({"scenario": "Scenario_A"})
        self.assertEqual(reply["handoffRetries"], 1)

    def test_a_clean_sweep_is_one_row_per_scenario_and_passes(self):
        # The guard on everything below: the resilience wrapper must not
        # change what a sweep with nothing wrong reports.
        out = self._run()
        self.assertEqual([r["scenario"] for r in out["results"]],
                         self.SCENARIOS)
        self.assertTrue(out["ok"])
        self.assertEqual(self.starts, [])
        for row in out["results"]:
            self.assertNotIn("stopReason", row)
            self.assertNotIn("bridgeError", row)
            self.assertEqual(row["newExceptions"], [])

    def test_a_bridge_that_dies_in_the_first_scenario_rows_the_rest(self):
        out = self._run(dies_on="Scenario_A")
        self.assertEqual([r["scenario"] for r in out["results"]],
                         self.SCENARIOS)
        self.assertFalse(out["ok"])
        self.assertFalse(out["results"][0]["ok"])
        # The other two ran, which is the whole point: a dead bridge in one
        # scenario says nothing about the scenarios after the relaunch.
        self.assertTrue(all(r["ok"] for r in out["results"][1:]))
        # The lost scenario says what it reached.
        row = out["results"][0]
        self.assertEqual(row["stopReason"], "bridge_lost")
        self.assertEqual(row["reached"], "campaign.new")
        self.assertIn("connection refused", row["bridgeError"])
        # And it carries the log scan, like every other row. The row a
        # scenario died in is the first one a reader looks for an exception
        # on, and the scan used to be taken inside the try: a row that failed
        # was the one row with no log evidence anywhere on it.
        self.assertEqual(row["newExceptions"], [])
        self.assertEqual(row["logNoiseIgnored"], {})
        # The sweep relaunches once before the next scenario, and says so in
        # the row that paid for it, and only there.
        self.assertEqual(len(self.stops), 1)
        self.assertEqual(len(self.starts), 1)
        self.assertIn("relaunchedBecause", out["results"][1])
        self.assertNotIn("relaunchedBecause", out["results"][2])

    def test_a_timeout_is_a_busy_bridge_rather_than_a_lost_one(self):
        # The split advance makes, for the same reason: a busy main thread is
        # a game that is up, and telling the reader to relaunch it is wrong.
        out = self._run(dies_on="Scenario_A",
                        error=bridge.BridgeTimeout("no reply within 30s"))
        self.assertEqual(out["results"][0]["stopReason"], "bridge_busy")

    def test_a_bridge_lost_in_the_advance_chunk_is_a_row_too(self):
        # No relaunch here: the bridge that failed the advance call is still
        # answering, so the menu return between scenarios works as it always
        # does and the sweep's lifecycle is unchanged.
        self.advance_error = bridge.BridgeError("connection refused")
        out = self._run()
        row = out["results"][0]
        self.assertEqual(row["stopReason"], "bridge_lost")
        self.assertEqual(row["reached"], "advance")
        self.assertEqual(self.starts, [])
        self.assertTrue(all(r["ok"] for r in out["results"][1:]))

    def test_a_relaunch_that_cannot_run_still_rows_every_scenario(self):
        # The recovery itself failing is the case that used to take the sweep
        # down by another door. Every scenario is still reported, and the row
        # says the relaunch is why the rest of it could not run.
        self.start_error = compose.ToolError("the game did not come back up")
        out = self._run(dies_on="Scenario_A")
        self.assertEqual(len(out["results"]), 3)
        self.assertEqual([r["stopReason"] for r in out["results"]],
                         ["bridge_lost"] * 3)
        # Each of the two after it died where the first did: the relaunch
        # left nothing answering, so the campaign start is the step that
        # failed rather than the menu return, which reports itself.
        self.assertEqual([r["reached"] for r in out["results"]],
                         ["campaign.new"] * 3)
        self.assertIn("did not come back up",
                      out["results"][1]["relaunchFailed"])

    # -------------------------------------------------- one named scenario

    def test_a_named_scenario_returns_a_loaded_campaign_to_the_menu(self):
        # The named mode is the one a client uses to re-test a single
        # scenario, and it is used from wherever the session already is --
        # usually inside a campaign. campaign.new is refused there and the
        # start screen scene is not even loaded, so the call is worth nothing
        # unless the menu return runs for one scenario the way it runs
        # between scenarios in a sweep.
        out = self._run({"scenario": "Scenario_B"}, campaign=True)
        self.assertEqual(self.fake.count("game.main_menu"), 1)
        self.assertEqual([r["scenario"] for r in out["results"]],
                         ["Scenario_B"])
        self.assertTrue(out["ok"])
        # Reported, so a row that fails at campaign.new anyway says whether
        # the return was the step that did not take.
        self.assertTrue(out["results"][0]["returnedToMenu"])
        # And no relaunch was paid for: the menu return is the cheap way back
        # and the fallback stays a fallback.
        self.assertEqual(self.starts, [])
        self.assertEqual(self.stops, [])

    def test_a_named_scenario_at_the_menu_returns_to_nothing(self):
        # The complement: with the start screen already up there is no
        # campaign to unload, and a return call would be a wasted scene load
        # on a state that is already right.
        out = self._run({"scenario": "Scenario_B"})
        self.assertEqual(self.fake.count("game.main_menu"), 0)
        self.assertNotIn("returnedToMenu", out["results"][0])
        self.assertTrue(out["ok"])

    def test_nothing_escapes_to_the_tool_dispatcher(self):
        # Through handle_call, because that is where the difference shows:
        # an escaped BridgeError is answered as "the game is not running" and
        # every row the sweep had already collected is gone from the reply.
        self.fake = SweepBridge(self.SCENARIOS, self.logs,
                                dies_on="Scenario_A")
        with mock.patch.object(compose, "bridge", self.fake):
            result = tools.handle_call("smoke_test", {"days": 1})
        text = result["content"][0]["text"]
        self.assertFalse(result["isError"])
        for name in self.SCENARIOS:
            self.assertIn(name, text)
        self.assertIn("bridge_lost", text)


if __name__ == "__main__":
    unittest.main()
