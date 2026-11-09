"""Unit tests for the combat_autoresolve wait.

  python3 -m unittest discover -s server/tests

The wait polls combat.status until the machine disarms, and then decides
whether a standing begin-combat prompt is an orphan to drop. Both halves read
the same status, so a poll that never answered is the dangerous input: taken
as a reading it says "nothing armed, no combat left", which is a clean resolve
followed by a prompt drop on a fight that is still on screen waiting for a
button.

No running game is needed. The fake answers the four verbs the wait calls,
and the clock is virtual so a poll costs nothing.
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
from test_advance import _no_sleep                  # noqa: E402


BEGIN_COMBAT = "PromptBeginCombat"

# A status with the machine disarmed, the combat gone and the clock still
# blocked: the shape that ends the wait cleanly and sends it to the orphan
# pass.
DONE = {"active": None, "pending": [], "blocked": True,
        "autoresolve": {"armed": False, "phase": "idle", "combat": None,
                        "frames": 12, "error": None, "note": None,
                        "attemptedCombat": 7, "attempts": 1,
                        "reached": "settling", "errorKind": "unknown",
                        "retryable": True,
                        "firedAutoresolveSelected": True,
                        "firedAcceptAutoresolve": True}}


class CombatBridge:
    """A bridge that arms a resolution and answers combat.status from a
    script, one entry per poll, the last entry repeating.

    An entry is either a status dict or an exception instance to raise. The
    other three verbs answer the state an orphaned prompt lives in: the canvas
    down and a begin-combat prompt standing. That is deliberate, and it is
    what makes "no dismiss was called" mean something -- with this fixture the
    clean path does call it.

    `precombat` is that screen's answer, and it takes an exception too: the
    orphan pass reads the screen as well as the status, and a read that fails
    is the second way to arrive at "the canvas is down" without having looked.
    """

    def __init__(self, script, precombat=None, prompts=None):
        self.script = list(script)
        self.precombat = precombat if precombat is not None else {
            "canvasUp": False, "buttons": {}, "combat": None}
        # The queue read, which takes an exception for the same reason the
        # screen does: an unread queue is not an empty one.
        self.prompts = prompts
        self.calls = []
        self.polls = 0

    def verbs(self, refresh=False):
        return {"combat.autoresolve", "combat.status", "combat.precombat",
                "prompts.list", "prompts.dismiss"}

    def call(self, verb, args=None, timeout=None):
        self.calls.append((verb, args))
        if verb == "combat.autoresolve":
            return {"armed": True, "combat": 7, "phase": "settling"}
        if verb == "combat.status":
            self.polls += 1
            entry = self.script[min(self.polls, len(self.script)) - 1]
            if isinstance(entry, Exception):
                raise entry
            return entry
        if verb == "combat.precombat":
            if isinstance(self.precombat, Exception):
                raise self.precombat
            return self.precombat
        if verb == "prompts.list":
            if isinstance(self.prompts, Exception):
                raise self.prompts
            if self.prompts is not None:
                return self.prompts
            return {"activePlayer": "Resistance", "blocked": True,
                    "prompts": [{"name": BEGIN_COMBAT, "scope": "faction"}]}
        if verb == "prompts.dismiss":
            return {"dismissed": [BEGIN_COMBAT], "remaining": 0}
        return {}

    def count(self, verb):
        return sum(1 for v, _ in self.calls if v == verb)

    def last_stall(self):
        return None, 0.0

    def last_campaign_token(self):
        return "fake-1"

    def last_campaign_process(self):
        return "fake"


class LostStatusPollTest(unittest.TestCase):
    """A status poll that fails is a lost poll, never a resolution.

    Every poll used to go through the helper that turns a transport failure
    into None, so a bridge that had stopped answering produced an empty status
    -- no autoresolve block, so "not armed", so a clean disarm. The wait then
    reported resolved: true, and the orphan pass read the same empty status as
    "no combat left" and dropped the begin-combat prompt of a fight nobody had
    answered yet.
    """

    def setUp(self):
        _no_sleep(self)

    def _run(self, script, args=None, precombat=None):
        fake = CombatBridge(script, precombat)
        with mock.patch.object(compose, "bridge", fake):
            report = compose.combat_autoresolve(dict(args or {"combat": 7}))
        return report, fake

    def test_a_dead_bridge_resolves_nothing_and_drops_nothing(self):
        report, fake = self._run([bridge.BridgeError("connection refused")])
        self.assertIs(report["resolved"], False)
        self.assertEqual(report["stopReason"], "bridge_lost")
        self.assertEqual(report["lostPolls"], compose.LOST_POLLS_MAX)
        self.assertIn("connection refused", report["bridgeError"])
        # The orphan pass never ran: no screen was read and no prompt touched.
        self.assertEqual(fake.count("combat.precombat"), 0)
        self.assertEqual(fake.count("prompts.list"), 0)
        self.assertEqual(fake.count("prompts.dismiss"), 0)
        self.assertNotIn("orphanPromptDropped", report)

    def test_the_clean_path_does_drop_it(self):
        # The control for the case above. Same fixture, same standing prompt,
        # a status that answers: here the drop is correct and happens, so the
        # assertions above are about the lost polls and not about a fixture
        # that could never have dismissed anything.
        report, fake = self._run([DONE])
        self.assertIs(report["resolved"], True)
        self.assertNotIn("stopReason", report)
        self.assertIs(report["orphanPromptDropped"], True)
        self.assertEqual(fake.count("prompts.dismiss"), 1)

    def test_a_verb_error_counts_the_same_way(self):
        # A refusal is not a reading either: combat.status answers with no
        # campaign, and a load that lands mid-wait refuses it.
        report, _ = self._run([bridge.VerbError("no campaign")])
        self.assertEqual(report["stopReason"], "bridge_lost")
        self.assertIs(report["resolved"], False)

    def test_a_timeout_is_one_lost_poll_and_the_wait_goes_on(self):
        # A busy main thread is the state a resolution itself produces, so a
        # timeout must not end the wait. Three of them, then the real answer.
        report, fake = self._run(
            [bridge.BridgeTimeout("no reply within 30s")] * 3 + [DONE])
        self.assertIs(report["resolved"], True)
        self.assertEqual(fake.count("combat.status"), 4)
        self.assertNotIn("lostPolls", report)

    def test_a_reply_that_is_not_a_status_is_lost_too(self):
        # A null result is not an empty status. Reading one as a disarm is the
        # same mistake with a different cause.
        report, _ = self._run([None])
        self.assertEqual(report["stopReason"], "bridge_lost")
        self.assertIs(report["resolved"], False)
        self.assertIn("no status object", report["bridgeError"])

    def test_the_budget_ends_a_wait_that_is_all_lost_polls(self):
        # Fewer polls than the cap, because max_seconds runs out first. The
        # ending is still bridge_lost: nothing was read, so nothing is known.
        report, fake = self._run(
            [bridge.BridgeError("connection refused")],
            {"combat": 7, "max_seconds": 4})
        self.assertEqual(report["stopReason"], "bridge_lost")
        self.assertLess(fake.count("combat.status"), compose.LOST_POLLS_MAX)
        self.assertNotIn("stalled", report)
        self.assertEqual(fake.count("prompts.dismiss"), 0)

    def test_polls_that_all_timed_out_end_the_wait_as_busy(self):
        # A timeout is a main thread that did not come back inside the verb
        # timeout, which a long resolution produces on its own: the game is
        # up, and the way back is another call on the same combat.
        report, fake = self._run(
            [bridge.BridgeTimeout("no reply within 30s")] * 6)
        self.assertEqual(report["stopReason"], "bridge_busy")
        self.assertIs(report["resolved"], False)
        self.assertEqual(report["lostPolls"], compose.LOST_POLLS_MAX)
        self.assertEqual(fake.count("prompts.dismiss"), 0)
        # The next line sends the caller to the status first. The resolution
        # may well have finished while the polls were going unanswered, and a
        # second arm on a combat whose one-shots have fired is refused.
        # Telling the caller to re-arm without looking first produced that
        # refusal as the answer to a tool that had just said to call it.
        text = report["next"]
        self.assertIn("combat_status", text)
        self.assertLess(text.index("combat_status"),
                        text.index("combat_autoresolve on the same combat"))
        self.assertIn("still reports it armed", text)

    def test_one_poll_that_did_not_time_out_makes_it_lost(self):
        # Timeouts up to the last one, and then the socket. Busy is claimed
        # only when every unanswered poll was a timeout.
        report, _ = self._run(
            [bridge.BridgeTimeout("no reply within 30s")] * 4
            + [bridge.BridgeError("connection refused")])
        self.assertEqual(report["stopReason"], "bridge_lost")
        self.assertIn("observe", report["next"])


class UnreadPrecombatScreenTest(unittest.TestCase):
    """A precombat screen that could not be read is not a canvas that is down.

    The orphan pass used to read the screen through the helper that answers
    None on a transport failure, so an unread screen produced `canvasUp`
    missing -- the one reading that lets the drop go ahead, on a fight that
    may still be on screen waiting for a button.
    """

    def setUp(self):
        _no_sleep(self)

    def _run(self, precombat):
        fake = CombatBridge([DONE], precombat)
        with mock.patch.object(compose, "bridge", fake):
            report = compose.combat_autoresolve({"combat": 7})
        return report, fake

    def test_a_screen_read_that_raises_drops_nothing(self):
        report, fake = self._run(bridge.BridgeError("connection refused"))
        self.assertEqual(fake.count("prompts.dismiss"), 0)
        self.assertIs(report["orphanPromptDropped"], False)
        self.assertIsNone(report["precombat"])
        self.assertIn("could not be read", report["orphanPromptNote"])
        self.assertIn("connection refused", report["orphanPromptNote"])

    def test_a_screen_reply_that_is_not_an_object_drops_nothing(self):
        report, fake = self._run("no")
        self.assertEqual(fake.count("prompts.dismiss"), 0)
        self.assertIs(report["orphanPromptDropped"], False)
        self.assertIn("no screen object", report["orphanPromptNote"])

    def test_a_canvas_that_is_really_down_still_drops_it(self):
        # The control: the same fixture with a screen that answers.
        report, fake = self._run({"canvasUp": False, "buttons": {}})
        self.assertEqual(fake.count("prompts.dismiss"), 1)
        self.assertIs(report["orphanPromptDropped"], True)


class UnreadPromptQueueTest(unittest.TestCase):
    """A prompt queue that could not be read is not an empty queue.

    The last read of the orphan pass had the same defect the two above had:
    it went through the helper that answers None on a failure, so an unread
    queue produced no standing prompts and the report said nothing was
    standing -- which is the reading that stops anyone looking again, about
    the one prompt that holds the clock for the rest of the campaign.
    """

    def setUp(self):
        _no_sleep(self)

    def _run(self, prompts):
        fake = CombatBridge([DONE], prompts=prompts)
        with mock.patch.object(compose, "bridge", fake):
            report = compose.combat_autoresolve({"combat": 7})
        return report, fake

    def test_a_queue_read_that_raises_says_so_and_drops_nothing(self):
        report, fake = self._run(bridge.BridgeError("connection refused"))
        self.assertEqual(fake.count("prompts.dismiss"), 0)
        self.assertIs(report["orphanPromptDropped"], False)
        self.assertIn("could not be read", report["orphanPromptNote"])
        self.assertIn("connection refused", report["orphanPromptNote"])

    def test_a_queue_that_really_is_empty_reports_nothing_standing(self):
        # The control: a queue that answered, with nothing in it. No note,
        # because there is nothing to say.
        report, fake = self._run({"prompts": []})
        self.assertEqual(fake.count("prompts.dismiss"), 0)
        self.assertIsNone(report["orphanPrompt"])
        self.assertNotIn("orphanPromptNote", report)


class AutoresolveFaultFixtureTest(unittest.TestCase):
    def test_the_verb_is_declared_so_selftest_covers_it(self):
        # selftest diffs this set against the DLL's registry; a verb missing
        # from it hides in the unmapped list instead of failing.
        self.assertIn("test.autoresolve_fault", tools.BRIDGE_VERBS_RAW_ONLY)


if __name__ == "__main__":
    unittest.main()
