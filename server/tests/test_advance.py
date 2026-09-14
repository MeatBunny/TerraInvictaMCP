"""Unit tests for the advance loop.

  python3 -m unittest discover -s server/tests

Two layers. The decision helpers decide how long a hold is tolerated, what the
caller is told when it is not, and how a dropped narrative event is named;
each used to be an expression buried in the loop, where a wrong answer looks
like a patient run or a stale sentence and nothing downstream catches it. The
loop itself runs against a fake bridge, which is the only way to test what a
call REPORTS: a run that dies quietly does so by reporting a stall as a budget,
an unreadable queue as an empty one, or a busy game as a dead one.

Neither layer needs a running game.
"""
import datetime
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


NARRATIVE = "PromptAddressNarrativeEvent"


def _skipped(*entries):
    return {"skipped": list(entries)}


class OnlyNarrativeTest(unittest.TestCase):
    """_only_narrative decides which holds get the long box-wait leash.

    The flag is the whole test, not the prompt name: a narrative prompt held
    back on purpose by narrative_events=false carries the same name and is a
    decision the caller asked for. Giving that one the leash spends the
    patience and then reports that a box never came up while the box is
    standing there answerable.
    """

    def test_waiting_for_box_is_a_box_wait(self):
        self.assertTrue(compose._only_narrative(
            _skipped({"name": NARRATIVE, "waitingForBox": True})))

    def test_deliberately_left_standing_is_not(self):
        # narrative_events=false marks the entry this way. It is a real
        # decision and must fall back to the short patience.
        self.assertFalse(compose._only_narrative(
            _skipped({"name": NARRATIVE, "waitingForBox": False})))

    def test_absent_flag_is_not_a_box_wait(self):
        # An older DLL, or a narrative prompt that got here some other way.
        # The leash is granted only on a positive statement.
        self.assertFalse(compose._only_narrative(
            _skipped({"name": NARRATIVE})))

    def test_mixed_list_is_not_a_box_wait(self):
        # One box wait beside anything else is not a queue draining, and the
        # other entry must not borrow the leash.
        self.assertFalse(compose._only_narrative(_skipped(
            {"name": NARRATIVE, "waitingForBox": True},
            {"name": "PromptSelectTech", "reason": "no tech to select"})))

    def test_empty_and_missing_are_not_box_waits(self):
        self.assertFalse(compose._only_narrative(_skipped()))
        self.assertFalse(compose._only_narrative({}))
        self.assertFalse(compose._only_narrative(None))


class HoldPollsTest(unittest.TestCase):
    """_hold_polls counts the CURRENT hold, and the stop message quotes it.

    A run total would report polls that belong to an earlier hold as though
    they were spent waiting on this box.
    """

    def _run(self, regimes):
        """Drive the loop's bookkeeping over a list of waiting_for_box values
        and return the poll count at the last one."""
        stuck = 0
        was = False
        for waiting in regimes:
            stuck = compose._hold_polls(stuck, waiting, was)
            was = waiting
        return stuck

    def test_regime_change_restarts_the_count(self):
        # Three polls of an ordinary hold, then two of a box wait. The box
        # wait is two polls old, not five.
        self.assertEqual(self._run([False, False, False, True, True]), 2)

    def test_change_back_restarts_it_again(self):
        self.assertEqual(self._run([True, True, True, False]), 1)


class StopNextTest(unittest.TestCase):
    """_stop_next has to name a path that exists.

    The non-box branch is reachable under narrative_events=false, where the
    standing prompt is a narrative one, so it must not send the caller to a
    drop that will not happen to it.
    """

    def test_box_branch_names_alert_choose_and_the_current_hold(self):
        # The poll count is the CURRENT hold's, so a box wait two polls old
        # after three polls of an ordinary hold says two, never five.
        text = compose._stop_next(True, 2)
        self.assertIn("over 2 polls", text)
        self.assertNotIn("over 5 polls", text)
        self.assertIn("alert_choose", text)

    def test_non_box_branch_does_not_offer_a_narrative_drop(self):
        text = compose._stop_next(False, 2)
        self.assertIn("alert_choose", text)
        self.assertIn("does nothing to a narrative one", text)


def _phase(active, collisions=0):
    return {"missionPhase": {"active": active, "prepping": False,
                             "planning": False, "collisions": collisions}}


class PhaseDecisionTest(unittest.TestCase):
    """_phase_decision says whether advance may hand the clock back.

    What corrupts a councilor mission phase is game time reaching the next
    semimonthly tick while the phase is open, so what has to be withheld is the
    clock itself: a run at speed 5 with nothing armed collides exactly as one
    with a target does. The hold is bounded because nothing here can close a
    phase the game will not close itself.
    """

    def test_a_phase_that_closes_within_the_bound_runs_and_forgets_the_hold(
            self):
        # The ordinary case: the dismiss pass presses the assignment
        # confirmation and the phase closes a poll or two later.
        action, waits, _ = compose._phase_decision(_phase(True), 0)
        self.assertEqual(action, "hold")
        self.assertEqual(waits, 1)
        action, waits, _ = compose._phase_decision(_phase(False), waits)
        self.assertEqual(action, "run")
        self.assertEqual(waits, 0)

    def test_a_reply_that_is_not_a_dict_runs(self):
        self.assertEqual(compose._phase_decision(None, 0)[0], "run")
        self.assertEqual(compose._phase_decision(
            {"missionPhase": "open"}, 0)[0], "run")

    def test_the_stop_line_names_the_override_and_the_collision_count(self):
        text = compose._phase_next("2022-04-01", _phase(True, 3)["missionPhase"])
        self.assertIn("force=true", text)
        self.assertIn("until=2022-04-01", text)
        self.assertIn("3 such collision", text)

    def test_the_stop_line_omits_a_collision_count_of_zero(self):
        text = compose._phase_next("2022-04-01", _phase(True)["missionPhase"])
        self.assertNotIn("collision(s)", text)


class DropNoteTest(unittest.TestCase):
    """_drop_note names the event.

    `via` is a constant sentence, so a note built from it read identically for
    every drop in a run and the digest's dedupe collapsed them to one line
    while promptsAnswered counted each.
    """

    VIA = ("dropped: no narrative event template behind the prompt, so nothing "
           "could ever answer it; no option was applied")

    def test_the_reason_travels_with_the_name(self):
        note = compose._drop_note(
            {"event": "event_Hurricane", "dropped": True,
             "deadEnd": "the narrative event's target is null, so its alert "
                        "box logs the target away and applies no option"})
        self.assertIn("event_Hurricane", note)
        self.assertIn("target is null", note)

    def test_an_unnamed_event_still_produces_a_note(self):
        note = compose._drop_note({"event": None, "dropped": True,
                                   "via": self.VIA})
        self.assertIn("event unnamed", note)
        self.assertIn(self.VIA, note)


class FakeBridge:
    """A bridge stand-in that answers the verbs the advance loop calls.

    The replies are the shapes the DLL actually returns, so a case here
    exercises the loop's own wiring -- the dismiss call, `_only_narrative`,
    the hold count and the stop message together -- rather than a hand-built
    `skipped` list handed straight to one helper.

    Defaults reproduce a blocking prompt: the clock never moves. The knobs
    below turn on the other four ways a run dies. Every one of them is a
    reading the loop takes each poll, so each is a poll-indexed switch rather
    than a fixed reply: a crash that is true from the first poll, or a queue
    that was never readable, would not exercise the transition that matters.
    """

    def __init__(self, dismiss_reply, date="2022-01-01", days_per_poll=0,
                 blocked=True, crashed_from=None, timeout_polls=(),
                 dismiss_error=None, combat=None, autopilot=None, saves=None,
                 alert=None, alert_from=None, phase_polls=0, phase_from=None,
                 phase_key=True, engaged=False, arm_error=None):
        self.dismiss_reply = dismiss_reply
        self.date = datetime.date.fromisoformat(date)
        self.days_per_poll = days_per_poll
        self.blocked = blocked
        self.crashed_from = crashed_from
        self.timeout_polls = set(timeout_polls)
        self.dismiss_error = dismiss_error
        self.combat = combat
        self.autopilot = autopilot
        self.saves = saves
        # The alert box the loop reads on every poll of a narrative hold. The
        # default is a box that HAS come up, which is what ends the long wait;
        # pass {"open": False} for the box that never arrives.
        self.alert = alert
        # The poll the box renders on. Before it, alert.choose answers with a
        # box that is not up: the event's prompt is queued and its box is
        # still behind the notifications in front of it, which is a queue
        # draining rather than a decision anybody can take.
        self.alert_from = alert_from
        # A councilor mission phase open for the first N polls, the entry read
        # included. phase_key false is a DLL from before the key existed, whose
        # query.time says nothing about the phase at all.
        self.phase_polls = phase_polls
        # A phase that OPENS on poll N and stays open, which is the case a
        # call that already armed and started the clock has to notice.
        self.phase_from = phase_from
        self.phase_key = phase_key
        # ai.control's engagement. advance reads it once at the door: engaged,
        # the DLL's own prefix guards the phase and the hold is skipped.
        self.engaged = engaged
        # An exception time.run_until raises instead of arming. The DLL
        # refuses a new target while a mission phase is open, and that
        # refusal is a VerbError whose message starts with the reason.
        self.arm_error = arm_error
        self.run_until_args = []
        # A dead process answers nothing. Set by a fake game_stop, cleared by a
        # fake game_start, so the crash tests exercise the confirmation the
        # recovery makes rather than assuming the kill worked.
        self.down = False
        self.dismiss_args = []
        self.calls = []
        self.polls = 0
        self.combat_args = []

    def verbs(self, refresh=False):
        return {"ping", "query.time", "prompts.dismiss", "combat.status",
                "combat.autoresolve", "query.autopilot", "saves.list",
                "saves.load", "saves.save", "alert.choose", "campaign.new",
                "time.play",
                "time.speed", "time.run_until", "time.pause", "ai.control"}

    def call(self, verb, args=None, timeout=None):
        if self.down:
            raise bridge.BridgeError("connection refused")
        self.calls.append(verb)
        if verb == "combat.autoresolve":
            self.combat_args.append(args)
        if verb == "query.time":
            self.polls += 1
            if self.polls in self.timeout_polls:
                raise bridge.BridgeTimeout("no reply within 30s")
            if self.polls > 1:
                self.date += datetime.timedelta(days=self.days_per_poll)
            crashed = (self.crashed_from is not None
                       and self.polls >= self.crashed_from)
            reply = {"date": self.date.isoformat(), "blocked": self.blocked,
                     "paused": False, "speed": 5, "crashed": crashed}
            if self.phase_key:
                reply["missionPhase"] = {
                    "active": self._phase_open(),
                    "prepping": False, "planning": False, "collisions": 0}
            return reply
        if verb == "time.run_until":
            self.run_until_args.append(args)
            if self.arm_error is not None:
                raise self.arm_error
            if self._refuses_the_arm(args):
                raise bridge.VerbError(
                    "mission_phase: a mission phase is open (missionPhase "
                    '{"active":true}), and arming a new run_until target '
                    "hands the clock back into it")
            return {"armed": True, "alreadyArmed": False,
                    "target": (args or {}).get("date")}
        if verb == "ai.control":
            return {"engaged": self.engaged}
        if verb == "prompts.dismiss":
            self.dismiss_args.append(args)
            if self.dismiss_error is not None:
                raise bridge.VerbError(self.dismiss_error)
            return self.dismiss_reply
        if verb == "combat.status":
            if self.combat is not None:
                return self.combat
            return {"active": False, "pending": False}
        if verb == "query.autopilot":
            return self.autopilot if self.autopilot is not None else {}
        if verb == "saves.list":
            return self.saves if self.saves is not None else []
        if verb == "saves.save":
            # The reply names the file written; the extension the game
            # appends is a profile setting, so the server reads it off this
            # path rather than assuming one.
            return {"path": "/saves/%s.gz" % (args or {}).get("name")}
        if verb == "alert.choose":
            if self.alert_from is not None and self.polls < self.alert_from:
                return {"open": False}
            if self.alert is not None:
                return self.alert
            return {"open": True, "text": ["a story event"],
                    "options": [{"option": 0, "label": "Acknowledge"}]}
        return {}

    def _phase_open(self):
        """The phase state this poll's query.time reported."""
        if not self.phase_key:
            return False
        return (self.polls <= self.phase_polls
                or (self.phase_from is not None
                    and self.polls >= self.phase_from))

    def _refuses_the_arm(self, args):
        """What the DLL does with a new target over an open mission phase.

        Refused, unless the caller forced it or an ai.control engagement is
        deferring the colliding tick. Modelled here rather than left out: the
        server exempts an engaged run from its own hold, so an arm the DLL
        refused anyway would leave that run with the clock down and nothing
        that ever re-arms it -- and a fake that always armed could not tell
        the two apart. `phase_key` false is a DLL from before any of this,
        which refuses nothing.
        """
        if not self._phase_open():
            return False
        if (args or {}).get("force"):
            return False
        return not self.engaged

    def count(self, verb):
        return self.calls.count(verb)

    def last_stall(self):
        # No clock stall to report, which is what a DLL older than the pause
        # limit answers. These cases are about the loop, not about the limit;
        # test_pause_limit subclasses this with a stall.
        return None, 0.0

    def last_campaign_token(self):
        # One campaign for the whole of a case. Answered rather than left off,
        # so a case driven through tools.handle_call reaches the campaign
        # check instead of having it swallowed as a missing attribute.
        return "fake-1"

    def last_campaign_process(self):
        # One game process for the whole of a case, for the same reason.
        return "fake"


def _no_sleep(case):
    """One virtual clock for both the sleep and the budget.

    The loop sleeps two seconds a poll and measures max_seconds against a real
    monotonic clock. Replacing only the sleep leaves the budget racing the test
    machine, so a case that means "stop after three polls" either passes or
    spins for its whole budget depending on how fast the box is. Advancing a
    fake clock from the sleep makes max_seconds a poll count.
    """
    clock = {"now": 0.0}
    saved_sleep = compose._time.sleep
    saved_monotonic = compose._time.monotonic
    compose._time.sleep = lambda s: clock.__setitem__("now", clock["now"] + s)
    compose._time.monotonic = lambda: clock["now"]
    case.addCleanup(setattr, compose._time, "monotonic", saved_monotonic)
    case.addCleanup(setattr, compose._time, "sleep", saved_sleep)


def _fresh_run_state(case):
    """compose._RUN spans tool calls on purpose, which means it spans TESTS.

    Every case here advances zero days, so without this the third one in a
    process would be refused by the stall guard the fourth problem added, and
    the failure would land on whichever test happened to run third.
    """
    saved = dict(compose._RUN)
    compose._RUN.update({"zeroCalls": 0, "zeroReasons": [],
                         "crashRecoveries": 0, "crashRestarted": False,
                         "macroOn": False,
                         "campaignToken": None, "campaignAt": None,
                         "save": None, "saveToken": None,
                         "campaignPending": False, "entryProcess": None})
    case.addCleanup(compose._RUN.update, saved)


class AdvanceLoopTest(unittest.TestCase):
    """The loop end to end against a fake bridge.

    This is the case the crafted-list tests above cannot make: it starts from
    the reply the DLL sends and ends at the digest the caller reads, so a
    wrong patience, a miscounted hold or a stale stop message all surface
    here. A regression in any one of them shows up as a poll count.
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)

    def _run(self, reply, alert=None, **kw):
        fake = FakeBridge(reply, alert=alert)
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance(dict({"days": 1}, **kw))
        return digest, fake

    # The DLL's reply under narrative_events=false: the prompt is left
    # standing on purpose and says so with waitingForBox false.
    LEFT_STANDING = {
        "screens": [], "narrativeNotes": [], "dismissed": [], "forced": [],
        "skipped": [{"name": NARRATIVE, "waitingForBox": False,
                     "reason": "narrative=false, so this was left standing "
                               "for a deliberate answer through alert.choose; "
                               "it still blocks the clock"}],
        "remaining": 1, "blocked": True}

    # The same prompt before its box has come up, which is a queue draining.
    WAITING_FOR_BOX = {
        "screens": [], "narrativeNotes": [], "dismissed": [], "forced": [],
        "skipped": [{"name": NARRATIVE, "waitingForBox": True,
                     "reason": "its alert box is the only path that may "
                               "answer it, and that box has not come up yet; "
                               "the screens pass takes it as soon as it does"}],
        "remaining": 1, "blocked": True}

    def test_deliberate_hold_stops_after_two_polls(self):
        # The regression this case exists for: matching the prompt by name
        # alone gave this reply the box-wait leash, so the run spent ten polls
        # and then reported that a box never came up while the box was open.
        # The fake's default alert is open, so this is the two-poll hold.
        digest, fake = self._run(self.LEFT_STANDING, narrative_events=False)
        self.assertEqual(digest["stopReason"], "decision")
        self.assertEqual(fake.dismiss_args[0],
                         {"narrative": False, "narrativeMode": "false"})
        self.assertNotIn("narrativeBoxWaits", digest)
        self.assertEqual(len(fake.dismiss_args), 2)
        # Not told to drop the prompt: the stop names the path that exists.
        self.assertNotIn("mode=drop type=PromptAddressNarrativeEvent",
                         digest["next"])
        self.assertNotIn("never came up", digest["next"])
        self.assertIn("alert_choose", digest["next"])
        # And it says no later call will answer it; llm is the mode that can.
        self.assertIn("narrative_events=false", digest["next"])
        self.assertIn("llm", digest["next"])

    def test_an_ordinary_decision_under_ai_says_none_of_that(self):
        digest, _ = self._run(self.LEFT_STANDING)
        self.assertNotIn("narrative_events=false", digest["next"])

    def test_a_box_that_has_not_come_up_is_waited_for_under_false_as_well(
            self):
        # The attended mode sends the same `narrative: false` and gets the
        # same reply, so it had the same defect: a box still queued read as a
        # decision standing ready and the call reported one two polls in, with
        # nothing on screen for the person to answer.
        digest, _ = self._run(self.LEFT_STANDING, alert={"open": False},
                              narrative_events=False)
        self.assertEqual(digest["stopReason"], "decision")
        self.assertEqual(digest["narrativeBoxWaits"],
                         compose.NARRATIVE_BOX_POLLS)
        self.assertIn("never came up", digest["next"])

    def test_ai_runs_and_sends_the_mode_beside_the_switch(self):
        # A DLL older than the mode key ignores it and presses on `narrative`
        # alone. `narrative` is absent under ai, as it always was: the DLL
        # defaults it to true and an old one must not be told otherwise.
        _, fake = self._run(CLEAR, max_seconds=20)
        self.assertEqual(fake.dismiss_args[0], {"narrativeMode": "ai"})

    # A box that never renders. The long leash is the bound on THIS, and it
    # is the only case that should spend it.
    NO_BOX = {"open": False}

    def test_a_box_that_never_comes_gets_the_long_leash(self):
        digest, fake = self._run(self.WAITING_FOR_BOX, alert=self.NO_BOX)
        self.assertEqual(digest["stopReason"], "decision")
        self.assertEqual(digest["narrativeBoxWaits"],
                         compose.NARRATIVE_BOX_POLLS)
        self.assertEqual(len(fake.dismiss_args), compose.NARRATIVE_BOX_POLLS)
        self.assertIn("never came up over %d polls"
                      % compose.NARRATIVE_BOX_POLLS, digest["next"])
        self.assertNotIn("narrativeBoxPolls", digest)

    def test_a_rendered_box_ends_the_wait_early(self):
        # The regression the smoke test found the other way round: the wait
        # used to be a fixed count with no reading of the box at all, so a box
        # that came up late was reported as one that never came, and a box
        # standing open was waited on for the whole leash. The default fake
        # alert IS open, so this hold is the short one.
        digest, fake = self._run(self.WAITING_FOR_BOX)
        self.assertEqual(digest["stopReason"], "decision")
        self.assertEqual(digest["narrativeBoxPolls"],
                         compose.NARRATIVE_BOX_PRESS_POLLS)
        self.assertEqual(len(fake.dismiss_args),
                         compose.NARRATIVE_BOX_PRESS_POLLS)
        self.assertIn("is OPEN with live option buttons", digest["next"])

    def test_an_open_box_with_no_live_option_is_not_rendered(self):
        # The box comes up before its buttons do. Counting that as rendered
        # would spend the short leash on a box nothing could press yet.
        digest, _ = self._run(self.WAITING_FOR_BOX,
                              alert={"open": True, "options": []})
        self.assertEqual(digest["narrativeBoxWaits"],
                         compose.NARRATIVE_BOX_POLLS)
        self.assertIn("never came up", digest["next"])


class DismissRefusalTest(unittest.TestCase):
    """prompts.dismiss can decline to run its pass, and the loop must stop.

    The refusal arrives as an ordinary reply carrying ok:false, not as an
    error, because it has state in it the caller acts on. Read as a normal
    reply it has no `remaining`, which would put the run on the unreadable
    -queue path and report that the queue could not be read -- true, but not
    why, and three polls later than necessary.
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)

    MOVED_SEAT = {"ok": False, "reason": "activePlayerMoved",
                  "text": "the active-player seat has been moved off the "
                          "faction this campaign came up with ... console "
                          "setfaction <faction> and call again",
                  "seatedFaction": {"dataName": "Resistance"}}

    def _run(self, reply):
        fake = FakeBridge(reply)
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 1})
        return digest, fake

    def test_a_known_refusal_stops_on_its_own_reason(self):
        digest, fake = self._run(self.MOVED_SEAT)
        self.assertEqual(digest["stopReason"], "active_player_moved")
        self.assertEqual(len(fake.dismiss_args), 1)
        # The zero-days preamble every stop carries is prefixed to it; the
        # refusal's own words are the rest of the line. The digest carries the
        # DLL reply intact rather than a summary, so the seat to put back
        # (what setfaction is called with) reaches the caller, and a key added
        # on that side does too without a change here.
        self.assertIn(self.MOVED_SEAT["text"], digest["next"])
        self.assertEqual(digest["promptsRefusal"], self.MOVED_SEAT)

    def test_an_engagement_is_not_a_moved_seat(self):
        # ai_autopilot leaves the seat where it is and flips the AI flag on
        # the faction already in it, so the pass runs and the loop advances.
        # The DLL is what makes that call (AiControl.Engaged short-circuits
        # ActivePlayerSeatMoved); this asserts the server does not invent a
        # refusal of its own from an ordinary reply taken during engagement.
        digest, _ = self._run({"dismissed": [], "skipped": [], "remaining": 0})
        self.assertNotEqual(digest["stopReason"], "active_player_moved")
        self.assertNotEqual(digest["stopReason"], "prompts_refused")
        self.assertNotIn("promptsRefusal", digest)

    def test_an_unknown_refusal_still_stops(self):
        # A reason this server has not heard of is still a pass that did not
        # run. Stopping under the generic name beats polling on.
        digest, _ = self._run({"ok": False, "reason": "somethingNew",
                               "text": "no"})
        self.assertEqual(digest["stopReason"], "prompts_refused")

    def test_a_refusal_with_no_text_gets_a_next_anyway(self):
        stop, text = compose._dismiss_refusal({"ok": False,
                                               "reason": "somethingNew"})
        self.assertEqual(stop, "prompts_refused")
        self.assertIn("somethingNew", text)


class NarrativeModeTest(unittest.TestCase):
    """The three answers `narrative_events` takes, and what each one does.

    The argument was a boolean, and the two false-ish behaviours had to share
    it: a caller that meant "stop and hand me the decision" got the wording,
    the patience and the stop reason of a run waiting for a person at the
    keyboard. The parse is its own function because a mode that silently fell
    back to `ai` would answer every story event with the engine's strategy and
    say nothing about it.
    """

    def test_the_boolean_vocabulary_still_means_what_it_meant(self):
        self.assertEqual(compose._narrative_mode({}), "ai")
        self.assertEqual(compose._narrative_mode({"narrative_events": True}),
                         "ai")
        self.assertEqual(compose._narrative_mode({"narrative_events": False}),
                         "false")
        # raw and older clients send strings; _flag has always taken these.
        self.assertEqual(compose._narrative_mode({"narrative_events": "true"}),
                         "ai")
        self.assertEqual(compose._narrative_mode({"narrative_events": "no"}),
                         "false")

    def test_the_two_named_modes(self):
        self.assertEqual(compose._narrative_mode({"narrative_events": "ai"}),
                         "ai")
        self.assertEqual(compose._narrative_mode({"narrative_events": "llm"}),
                         "llm")
        self.assertEqual(compose._narrative_mode({"narrative_events": "LLM"}),
                         "llm")

    def test_a_mode_nobody_defined_is_refused(self):
        # Defaulting would run the engine's AI over every story event of the
        # call while the caller believed it was being handed them.
        with self.assertRaises(tools.ToolError) as e:
            compose._narrative_mode({"narrative_events": "handover"})
        # The error names the modes that exist.
        self.assertIn("llm", str(e.exception))
        self.assertIn("ai", str(e.exception))
        with self.assertRaises(tools.ToolError):
            compose._narrative_mode({"narrative_events": 3})


class NarrativeLlmTest(unittest.TestCase):
    """llm stops on the box instead of pressing it."""

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)

    def _run(self, reply, alert=None, alert_from=None, **kw):
        fake = FakeBridge(reply, alert=alert, alert_from=alert_from)
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance(dict({"days": 1}, **kw))
        return digest, fake

    def test_the_first_open_box_ends_the_call_with_its_options(self):
        digest, fake = self._run(AdvanceLoopTest.LEFT_STANDING,
                                 narrative_events="llm")
        self.assertEqual(digest["stopReason"], "narrative_event")
        self.assertEqual(digest["alert"]["options"][0]["option"], 0)
        self.assertIn("alert_choose", digest["next"])
        # No hold: the handover is the result, not a failure to progress.
        self.assertEqual(len(fake.dismiss_args), 1)
        # Nothing was pressed. The DLL is told not to, by the switch it has
        # always read, and the mode rides beside it.
        self.assertEqual(fake.dismiss_args[0],
                         {"narrative": False, "narrativeMode": "llm"})
        self.assertEqual(digest["narrativeEvents"], [])
        # The next line lists the options it is handing over.
        self.assertIn("0=Acknowledge", digest["next"])
        self.assertIn("2022-01-02", digest["next"])

    def test_a_box_that_has_not_rendered_is_not_a_handover(self):
        # The prompt is queued and the box is still behind the notifications
        # in front of it. Stopping there would hand the caller a decision with
        # no options in it, which it would have to poll for itself.
        #
        # The fixture is LEFT_STANDING, which is what the DLL sends under llm:
        # the mode sets `narrative: false`, so the dismiss pass never tries to
        # answer the prompt and marks it as deliberately left. Run against
        # WAITING_FOR_BOX -- a reply llm never produces -- this case passed
        # while the real path ended on poll two.
        digest, _ = self._run(AdvanceLoopTest.LEFT_STANDING,
                              alert={"open": False}, narrative_events="llm")
        self.assertEqual(digest["stopReason"], "decision")
        # And it got the queue-drain leash rather than the two-poll hold: the
        # box is what the call is waiting for, whatever the flag says.
        self.assertEqual(digest["narrativeBoxWaits"],
                         compose.NARRATIVE_BOX_POLLS)

    def test_a_box_queued_behind_notifications_is_handed_over_when_it_comes(
            self):
        # The defect this pair exists for. The box arrives on poll 6, well
        # past the two polls an ordinary hold gets, and the call has to still
        # be there to hand it over rather than having stopped on `decision`
        # with an alert that was not open.
        # alert_from counts the fake's clock readings, and the first of those
        # is the one advance takes at the door before the loop starts, so the
        # box renders on the sixth poll of the loop.
        digest, fake = self._run(AdvanceLoopTest.LEFT_STANDING,
                                 alert_from=7, narrative_events="llm")
        self.assertEqual(digest["stopReason"], "narrative_event")
        self.assertEqual(digest["alert"]["options"][0]["option"], 0)
        self.assertEqual(len(fake.dismiss_args), 6)

    def test_a_box_with_no_live_options_is_not_a_handover_either(self):
        # An ordinary notification's box is open too; only option buttons make
        # it a story event.
        digest, _ = self._run(AdvanceLoopTest.WAITING_FOR_BOX,
                              alert={"open": True, "options": []},
                              narrative_events="llm")
        self.assertEqual(digest["stopReason"], "decision")

    def test_a_box_whose_press_would_not_land_is_not_handed_over_yet(self):
        # The controller arms narrative input from a delayed coroutine, so a
        # box can be open with live buttons a poll before a press does
        # anything. Handing over there gives the caller options whose press is
        # refused.
        digest, _ = self._run(
            AdvanceLoopTest.WAITING_FOR_BOX,
            alert={"open": True, "pressLands": False,
                   "options": [{"option": 0, "label": "Acknowledge"}]},
            narrative_events="llm")
        self.assertEqual(digest["stopReason"], "decision")

    def test_a_clear_queue_costs_no_alert_read(self):
        # The mode must not add a verb per poll to a run that never meets a
        # story event: the prompt is queued by the same call that queues the
        # box, so an empty queue means no box.
        _, fake = self._run(CLEAR, narrative_events="llm", max_seconds=20)
        self.assertEqual(fake.count("alert.choose"), 0)


# A dismiss reply with nothing queued: the clock is free as far as prompts go.
CLEAR = {"screens": [], "narrativeNotes": [], "dismissed": [], "forced": [],
         "skipped": [], "remaining": 0, "blocked": False}


class MissionPhaseTest(unittest.TestCase):
    """The loop must not hand the clock back while a mission phase is open.

    Every advance call used to set speed 5, arm run_until and play the clock
    unconditionally. Landing a semimonthly tick inside an open phase is the
    collision the engine logs as "StartNewMissionPhase fired when mission phase
    was already active", and it leaves factionsSignallingComplete populated so
    the next phase ends early. Game time reaching that tick is what causes it,
    so the whole clock is withheld, not just the target.
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)

    def _run(self, **kw):
        run_args = {"days": kw.pop("days", 1)}
        for key in ("force", "max_seconds"):
            if key in kw:
                run_args[key] = kw.pop(key)
        fake = FakeBridge(CLEAR, **kw)
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance(run_args)
        return digest, fake

    def test_a_closed_phase_arms_once_and_is_never_re_armed(self):
        # The idempotence half: one target, one arm, however many polls the
        # call takes. Re-stating the armed target is what used to clear the
        # park on every poll.
        digest, fake = self._run(max_seconds=20)
        self.assertEqual(fake.count("time.run_until"), 1)
        self.assertEqual(fake.count("time.pause"), 0)
        self.assertNotIn("missionPhaseWaits", digest)
        self.assertNotIn("missionPhase", digest)

    def test_an_open_phase_is_held_out_and_then_run(self):
        # Open on the entry read only. The clock comes back on the first loop
        # poll, and the hold is recorded so a run can say it happened.
        digest, fake = self._run(phase_polls=1, max_seconds=20)
        self.assertEqual(fake.count("time.run_until"), 1)
        self.assertEqual(digest["missionPhaseWaits"], 1)
        self.assertNotEqual(digest["stopReason"], "mission_phase")

    def test_the_held_clock_is_paused_and_never_played(self):
        # The whole rule: no time.speed, no time.run_until, no time.play, and
        # a clock that is running is stopped. The fake reports paused false on
        # every poll, so the pause is re-asserted each time.
        digest, fake = self._run(phase_polls=99)
        self.assertGreaterEqual(fake.count("time.pause"), 2)
        self.assertEqual(fake.count("time.play"), 0)
        self.assertEqual(fake.count("time.speed"), 0)
        self.assertEqual(fake.count("time.run_until"), 0)
        # A phase that never closes stops with its own reason.
        self.assertEqual(digest["stopReason"], "mission_phase")
        self.assertEqual(digest["missionPhaseWaits"],
                         compose.MISSION_PHASE_POLLS)
        self.assertEqual(digest["missionPhase"]["active"], True)
        self.assertIn("force=true", digest["next"])

    def test_a_phase_that_opens_mid_call_takes_the_clock_back_down(self):
        # The armed half of the rule. The phase was consulted only when the
        # call had nothing armed, so a phase that opened after the arm met the
        # resume at the bottom of this loop instead -- the clock handed
        # straight back into an open phase, which is the whole hazard.
        digest, fake = self._run(phase_from=3)
        self.assertEqual(digest["stopReason"], "mission_phase")
        self.assertGreaterEqual(fake.count("time.pause"), 1)
        # Nothing asks for the clock after the phase opens.
        first_pause = fake.calls.index("time.pause")
        last_play = (len(fake.calls) - 1
                     - fake.calls[::-1].index("time.play"))
        self.assertGreater(first_pause, last_play)

    def test_the_loop_body_still_runs_while_the_phase_is_open(self):
        # The hold cannot watch the clock alone: the dismiss pass is what
        # presses the mission-assignment confirmation, so a hold that skipped
        # it would never end.
        _, fake = self._run(phase_polls=3, max_seconds=20)
        self.assertGreaterEqual(len(fake.dismiss_args), 2)

    def test_an_engagement_skips_the_hold_entirely(self):
        # AiControl's own prefix defers a colliding tick while engaged, so the
        # phase is guarded there and a hold would only stall the run. The
        # fake refuses an arm over an open phase exactly as the DLL does, so
        # this passes only because the DLL exempts an engagement too: with
        # the refusal in place on both sides, an engaged run would arm
        # nothing and hold a clock the engagement is trying to spend.
        digest, fake = self._run(phase_polls=99, engaged=True, max_seconds=20)
        self.assertEqual(fake.count("time.run_until"), 1)
        self.assertEqual(fake.count("time.pause"), 0)
        self.assertNotIn("missionPhaseWaits", digest)
        self.assertNotEqual(digest["stopReason"], "mission_phase")
        # Recorded, though not held for: the run may have caused a collision.
        self.assertEqual(digest["missionPhase"]["active"], True)

    def test_a_clock_that_is_gaining_time_never_stops_on_the_phase(self):
        # The bound counts polls spent with the clock held down. A poll that
        # moved the date is not one: the game is running, whatever the phase
        # block says, and stopping a working run on it would be the bound
        # firing against the case it was written to exclude. Long enough a run
        # to pass MISSION_PHASE_POLLS, which is what makes the point.
        digest, _ = self._run(days=compose.MISSION_PHASE_POLLS + 5,
                              phase_polls=99, days_per_poll=1, blocked=False,
                              max_seconds=200)
        self.assertEqual(digest["stopReason"], "reached")

    def test_a_budget_stop_does_not_claim_a_target_it_never_armed(self):
        # Under a short budget the hold runs out of time before it runs out of
        # polls. "run_until stays armed" was unconditional there, which
        # promised a stop at a date the game would have run straight past.
        digest, _ = self._run(phase_polls=99, max_seconds=20)
        self.assertIn("NOT armed", digest["next"])

    def test_force_runs_through_an_open_phase(self):
        digest, fake = self._run(phase_polls=99, force=True, max_seconds=20)
        self.assertEqual(fake.count("time.run_until"), 1)
        self.assertIs(fake.run_until_args[0]["force"], True)
        self.assertEqual(fake.count("time.pause"), 0)
        self.assertNotEqual(digest["stopReason"], "mission_phase")
        # force and an ai.control engagement both hand the clock back into an
        # open phase on purpose. The choice is defensible; a digest that said
        # nothing about it was not, because a run that may have caused a
        # collision then looked exactly like one that never met a phase.
        self.assertEqual(digest["missionPhase"]["active"], True)
        # Recorded, not held for: no waits, because nothing waited.
        self.assertNotIn("missionPhaseWaits", digest)

    def test_an_arm_refused_for_the_phase_never_plays_the_clock(self):
        # The race: the phase opens in the frames between this call's reading
        # and its arm, so the reading says "run" and the DLL says no. Playing
        # afterwards would hand the game exactly what the refusal withheld.
        _, fake = self._run(
            arm_error=bridge.VerbError(
                "mission_phase: a mission phase is open (missionPhase "
                '{"active":true}), and arming a new run_until target hands '
                "the clock back into it"),
            max_seconds=20)
        self.assertEqual(fake.count("time.play"), 0)
        # Nor the speed. time.speed 5 hands the clock back on its own -- the
        # game runs at full speed with or without a target -- so sending it
        # before the arm did the very thing the refusal exists to prevent,
        # and the hold that followed was a hold over a running clock.
        self.assertEqual(fake.count("time.speed"), 0)
        # The rest of the poll is a hold too: the clock goes down and the
        # dismiss pass still runs, since that is what closes a phase.
        self.assertGreaterEqual(fake.count("time.pause"), 1)
        self.assertGreaterEqual(len(fake.dismiss_args), 1)

    def test_an_arm_lost_to_the_transport_still_plays_the_clock(self):
        # No phase behind this one, so leaving the clock down would stall the
        # run for a failure that says nothing about the game state.
        _, fake = self._run(arm_error=bridge.BridgeError("connection reset"),
                            max_seconds=20)
        self.assertGreaterEqual(fake.count("time.play"), 1)
        self.assertEqual(fake.count("time.pause"), 0)

    def test_a_refusal_that_is_not_the_phase_still_plays_the_clock(self):
        _, fake = self._run(arm_error=bridge.VerbError("arg 'date' must be "
                                                       "yyyy-MM-dd"),
                            max_seconds=20)
        self.assertGreaterEqual(fake.count("time.play"), 1)
        self.assertEqual(fake.count("time.pause"), 0)

    def test_a_dll_without_the_key_behaves_as_it_always_did(self):
        digest, fake = self._run(phase_key=False, phase_polls=99,
                                 max_seconds=20)
        self.assertEqual(fake.count("time.run_until"), 1)
        self.assertEqual(fake.count("time.pause"), 0)
        self.assertNotIn("missionPhaseWaits", digest)


class ProgressTest(unittest.TestCase):
    """A call that moved nothing has to say so.

    The budget reason reads as "it was going fine, ask for more time", and
    that sentence is what an agent obeys. Thirty days and zero days used to
    produce it identically, so a run that had stopped dead kept being told to
    call again, forever.
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)

    def _run(self, **kw):
        fake = FakeBridge(CLEAR, blocked=False, days_per_poll=0)
        with mock.patch.object(compose, "bridge", fake):
            return compose.advance(dict({"days": 30, "max_seconds": 0}, **kw))

    def test_zero_days_is_not_the_budget_reason(self):
        digest = self._run()
        self.assertEqual(digest["daysAdvanced"], 0)
        self.assertEqual(digest["stopReason"], "no_progress")

    def test_the_inert_stops_are_exactly_these(self):
        # Every member is a zero-day stop nothing between two calls can clear:
        # the bound on a phase the game will not close, a refused pass over an
        # unchanged seat, a bridge that stopped answering, a game that crashed
        # again after its restart. Every non-member hands the caller something
        # to do; crash_recovered is the one that clears the count, since the
        # game came back and the next call has a live one to run.
        self.assertEqual(compose.INERT_STOPS, frozenset({
            "no_progress", "unknown", "mission_phase", "pause_limit",
            "active_player_moved", "prompts_refused", "bridge_lost",
            "crashed"}))

    def test_days_advanced_keeps_the_budget_reason(self):
        # Three polls of the virtual clock, two of which move the date.
        fake = FakeBridge(CLEAR, blocked=False, days_per_poll=3)
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 300, "max_seconds": 4})
        self.assertGreater(digest["daysAdvanced"], 0)
        self.assertEqual(digest["stopReason"], "max_seconds")
        self.assertEqual(compose._RUN["zeroCalls"], 0)

    def test_consecutive_fruitless_calls_escalate_then_refuse(self):
        first = self._run()
        self.assertIn("One call moving nothing", first["next"])
        self.assertEqual(first["consecutiveZeroProgressCalls"], 1)

        second = self._run()
        self.assertIn("appears stalled", second["next"])
        self.assertIn("advance again", second["next"])
        self.assertEqual(second["consecutiveZeroProgressCalls"], 2)

        # The third is refused rather than re-armed: calling again is what the
        # two digests above asked for, and it did not work.
        with self.assertRaises(compose.ToolError) as caught:
            self._run()
        self.assertIn("refused", str(caught.exception))
        self.assertIn("force=true", str(caught.exception))

    def test_force_runs_past_the_refusal(self):
        self._run()
        self._run()
        digest = self._run(force=True)
        self.assertEqual(digest["stopReason"], "no_progress")
        self.assertEqual(digest["consecutiveZeroProgressCalls"], 1)

    def test_progress_clears_the_counter(self):
        self._run()
        fake = FakeBridge(CLEAR, blocked=False, days_per_poll=3)
        with mock.patch.object(compose, "bridge", fake):
            compose.advance({"days": 300, "max_seconds": 4})
        self.assertEqual(compose._RUN["zeroCalls"], 0)


class UnreadablePromptsTest(unittest.TestCase):
    """An unreadable remaining count is not a count of zero.

    Three paths leave it unknown -- the dismiss call raised, the reply was not
    a dictionary, the queue refused -- and all three used to take the same
    branch as an empty queue, so the hold counter never rose and the loop
    resumed a frozen clock every two seconds until the budget ran out.
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)

    def test_a_raising_dismiss_stops_and_says_it_is_unknown(self):
        fake = FakeBridge(None, dismiss_error="no prompt queue")
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 1, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "prompts_unreadable")
        self.assertEqual(digest["unreadablePromptPolls"],
                         compose.UNKNOWN_REMAINING_POLLS)
        self.assertIn("NOT zero", digest["next"])
        self.assertIn("no prompt queue", digest["next"])

    def test_a_reply_that_is_not_a_dict_is_also_unknown(self):
        fake = FakeBridge("something else entirely")
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 1, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "prompts_unreadable")
        self.assertIn("NOT zero", digest["next"])


class LostPollTest(unittest.TestCase):
    """A bridge timeout mid-loop is one lost poll, not the end of the call.

    Every verb is answered on the game's main thread, so one long stall there
    -- a large save, an asset load, a scene change -- outlives the 30s call
    budget with the process perfectly alive. The loop's own query.time used to
    sit outside any handler, so that stall aborted the whole call and the tool
    layer reported "the game is not running, call game_start".
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)

    def test_the_call_survives_and_counts_the_lost_polls(self):
        # Polls 2 and 3: poll 1 is the entry read that fixes the start date,
        # which is outside the loop and is the tool layer's to report.
        fake = FakeBridge(CLEAR, blocked=False, days_per_poll=1,
                          timeout_polls=(2, 3))
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 2, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "reached")
        self.assertEqual(digest["lostPolls"], 2)
        self.assertGreater(digest["daysAdvanced"], 0)

    def test_a_run_of_lost_polls_ends_the_call(self):
        # Each of them waited the full verb timeout, so five in a row is
        # minutes of silence. Spending the rest of the budget on it reports a
        # bridge that has stopped answering as a run that was going fine.
        polls = tuple(range(2, 2 + compose.LOST_POLLS_MAX))
        fake = FakeBridge(CLEAR, blocked=False, days_per_poll=1,
                          timeout_polls=polls)
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 5, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "bridge_lost")
        self.assertEqual(digest["lostPolls"], compose.LOST_POLLS_MAX)
        self.assertIn("observe", digest["next"])

    def test_one_answered_poll_starts_the_count_again(self):
        # The cap counts CONSECUTIVE silence. A busy main thread that answers
        # in between is a game that is working, however many polls it lost.
        polls = tuple(range(2, 2 + compose.LOST_POLLS_MAX - 1)) \
            + tuple(range(3 + compose.LOST_POLLS_MAX,
                          2 + 2 * compose.LOST_POLLS_MAX))
        fake = FakeBridge(CLEAR, blocked=False, days_per_poll=1,
                          timeout_polls=polls)
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 2, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "reached")

    def test_a_timeout_is_not_reported_as_the_game_being_down(self):
        def boom(args, progress=None):
            raise bridge.BridgeTimeout("no reply within 30s")

        with mock.patch.dict(tools.BY_NAME, {"advance": boom}):
            result = tools.handle_call("advance", {})
        text = result["content"][0]["text"]
        self.assertTrue(result["isError"])
        self.assertIn("The game is UP", text)
        self.assertIn("Do NOT call game_start", text)
        self.assertNotIn("game is not running", text)


def _combat(error=None, retryable=None, pending_id=7, attempted=None):
    """A combat.status reply with one pending combat and nothing armed.

    `attempted` is the combat the autoresolve machine's recorded error belongs
    to, which is not always the one `pending_id` names.
    """
    if attempted is None and error:
        attempted = pending_id
    autoresolve = {"armed": False, "phase": "idle", "combat": None,
                   "frames": 0, "error": error, "note": None,
                   "attemptedCombat": attempted, "attempts": 1,
                   "reached": "firing", "errorKind": "unknown"}
    if retryable is not None:
        autoresolve["retryable"] = retryable
    return {"active": None,
            "pending": [{"id": pending_id, "name": "Combat %d" % pending_id}],
            "blocked": True, "autoresolve": autoresolve}


class CombatRetryTest(unittest.TestCase):
    """Re-arming a failed combat is the one failure here that corrupts.

    A failed tick disarms, so the next poll saw a combat present and nothing
    armed and called combat.autoresolve again, which re-entered the machine
    from the start. That defeats the mod's two one-shot guards: a second
    AutoresolveSelected raises a second PrecombatComplete, and a second
    OnAcceptAutoresolveSelected reaches ApplySimulatedCombat, which writes the
    same battle's damage into the real states twice.
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)

    def test_a_stable_failure_is_not_retried_at_all(self):
        fake = FakeBridge(CLEAR, combat=_combat(
            error="combat needs a stance from non-AI faction Resistance",
            retryable=False))
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 1, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "combat")
        self.assertEqual(fake.count("combat.autoresolve"), 0)
        self.assertIn("non-AI faction Resistance", digest["next"])
        self.assertIn("7", digest["next"])

    def test_a_repeated_failure_stops_after_the_second_attempt(self):
        # No retryable flag: an older DLL, or one that classified the failure
        # as worth a retry. The server holds the same limit either way.
        fake = FakeBridge(CLEAR, combat=_combat(
            error="no precombat controller"))
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 1, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "combat")
        self.assertEqual(fake.count("combat.autoresolve"),
                         compose.COMBAT_ARM_LIMIT)
        self.assertIn("no precombat controller", digest["next"])
        self.assertIn("combat_precombat", digest["next"])

    def test_an_error_belonging_to_another_combat_does_not_stop_this_one(self):
        # A fails, B appears while A's canvas still holds the clock, and the
        # status reports B with A's error attached. Stopping there would name
        # B and quote A's failure. attemptedCombat says who the error is for.
        fake = FakeBridge(CLEAR, combat=_combat(
            error="precombat controller has a stale active player",
            retryable=False, pending_id=9, attempted=7))
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 1, "max_seconds": 6})
        self.assertNotEqual(digest.get("stopReason"), "combat")
        self.assertGreaterEqual(fake.count("combat.autoresolve"), 1)

    def test_the_arm_names_the_combat_it_counted(self):
        # With no argument the mod targets the active combat, else the first
        # unresolved one, which is not necessarily the one being counted.
        fake = FakeBridge(CLEAR, combat=_combat(pending_id=9))
        with mock.patch.object(compose, "bridge", fake):
            compose.advance({"days": 1, "max_seconds": 6})
        self.assertTrue(fake.combat_args)
        for args in fake.combat_args:
            self.assertEqual(args, {"combat": 9})

    def test_a_first_arm_with_no_recorded_error_is_allowed(self):
        fake = FakeBridge(CLEAR, combat=_combat())
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 1, "max_seconds": 6})
        self.assertGreaterEqual(fake.count("combat.autoresolve"), 1)
        # One combat, however many arms it took: the counter used to tick per
        # arm, so a fight re-armed every poll read as a run that resolved
        # dozens of them.
        self.assertEqual(digest["combatsAutoresolved"], 1)


class CrashTest(unittest.TestCase):
    """A crashed game answers every verb and is dead.

    GlobalInstaller.HandleException freezes the clock, clears every event
    listener and turns input off, and the bridge outlives all of it, so the
    frozen clock reads exactly like a modal alert nothing can answer. Recovery
    is a process restart: the flag has one writer, no clearer, and survives a
    scene load, so a save loaded into the same process comes back degraded.
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)
        # The recovery is a process restart, which a unit test records rather
        # than performs. The stop takes the fake bridge down with it, because
        # the recovery confirms the bridge went quiet before it restarts, and
        # a fake that keeps answering is a fake that never died.
        self.stops = []
        self.starts = []
        self.stop_works = True
        # The run is playing a save it loaded, in a campaign it entered at a
        # known moment. Both are what makes "a save of this run" answerable;
        # without them the recovery has nothing it is entitled to reload.
        compose._RUN["campaignAt"] = self.ENTERED
        compose._RUN["save"] = {"name": "scratch-run", "extension": ".gz"}
        compose._RUN["saveToken"] = "fake-1"
        compose._RUN["campaignToken"] = "fake-1"
        self.fake = self._fake(crashed_from=3)
        self._patch("game_stop", self._stop)
        self._patch("game_start", self._start)

    def _patch(self, name, fn):
        patcher = mock.patch.object(compose, name, fn)
        patcher.start()
        self.addCleanup(patcher.stop)

    def _stop(self, args, progress=None):
        self.stops.append(args)
        if self.stop_works:
            self.fake.down = True
        return {"stopped": self.stop_works}

    def _start(self, args, progress=None):
        self.starts.append(args)
        # A new process answers again. The crash flag is deliberately NOT
        # cleared: the save that reloads and crashes again is the case the
        # recovery budget exists for.
        self.fake.down = False
        return {"launched": True}

    # The folder as a real one looks: an autosave of the campaign under test,
    # a save this run loaded, and an autosave from BEFORE the run entered its
    # campaign, which is another campaign's and must never be reloaded here.
    ENTERED = "2026-08-30 08:00:00"
    SAVES = [{"name": "Autosave1", "extension": ".gz",
              "mtime": "2026-08-30 10:00:00"},
             {"name": "scratch-run", "extension": ".gz",
              "mtime": "2026-08-31 09:00:00"},
             {"name": "Autosave2", "extension": ".gz",
              "mtime": "2026-08-29 12:00:00"}]

    def _fake(self, **kw):
        return FakeBridge(CLEAR, blocked=False, days_per_poll=1,
                          saves=self.SAVES, **kw)

    def _run(self, **kw):
        with mock.patch.object(compose, "bridge", self.fake):
            return compose.advance(dict({"days": 60, "max_seconds": 600},
                                        **kw))

    def test_a_crash_restarts_and_reloads_the_newest_save_of_this_run(self):
        digest = self._run()
        self.assertEqual(digest["stopReason"], "crash_recovered")
        self.assertTrue(digest["crash"]["recovered"])
        self.assertEqual(digest["crash"]["save"], "scratch-run")
        self.assertEqual(digest["crash"]["saveWritten"], "2026-08-31 09:00:00")
        # What the restart cost, named: the in-game date the run had reached.
        self.assertIn(digest["crash"]["detectedAt"],
                      digest["crash"]["gameTimeLost"])
        self.assertEqual(len(self.stops), 1)
        # The extension travels with the name. With both twins of a stem on
        # disk a bare name resolves through the profile setting, which need
        # not be the file whose mtime was just read.
        self.assertEqual(self.starts,
                         [{"load": "scratch-run", "extension": ".gz"}])

    def test_a_process_that_survived_the_stop_is_not_a_recovery(self):
        # game_stop can report stopped:False on a leftover pid or a failed
        # kill. game_start would then find the bridge up, report "already
        # running", never launch, and load the save into the CRASHED process,
        # which comes back permanently degraded -- while the digest claimed a
        # successful recovery and told the caller to carry on.
        self.stop_works = False
        digest = self._run()
        self.assertEqual(digest["stopReason"], "crashed")
        self.assertFalse(digest["crash"]["recovered"])
        self.assertEqual(self.starts, [])
        self.assertIn("would not die", digest["next"])
        self.assertIn("degraded", digest["next"])

    def test_a_second_crash_stops_rather_than_looping(self):
        self._run()
        digest = self._run()
        self.assertEqual(digest["stopReason"], "crashed")
        self.assertFalse(digest["crash"]["recovered"])
        self.assertIn("restart loop", digest["next"])
        self.assertEqual(len(self.starts), 1)

    def test_a_spent_budget_never_claims_a_restart_that_did_not_run(self):
        # The budget is spent when the attempt starts, so a recovery that
        # died at the stop leaves it at the limit with nothing restarted.
        # Saying "crashed again after an automatic restart" there describes
        # a restart the run never had.
        self.stop_works = False
        self._run()
        self.assertEqual(compose._RUN["crashRecoveries"], 1)
        self.stop_works = True
        digest = self._run()
        self.assertEqual(digest["stopReason"], "crashed")
        self.assertFalse(digest["crash"]["recovered"])
        self.assertIn("restart loop", digest["next"])
        self.assertNotIn("after an automatic restart", digest["next"])
        self.assertEqual(self.starts, [])

    def test_a_spent_budget_with_no_save_says_so_instead(self):
        # A budget carried in from an earlier campaign is not the blocker
        # here: there is nothing this run may reload, which is the fact the
        # caller acts on.
        self._run()
        self.fake.saves = []
        compose._RUN["save"] = None
        digest = self._run()
        self.assertEqual(digest["stopReason"], "crashed")
        self.assertIn("no save OF THIS RUN", digest["next"])
        self.assertNotIn("after an automatic restart", digest["next"])

    def test_no_save_means_stop_and_never_a_new_campaign(self):
        self.fake.saves = []
        digest = self._run()
        self.assertEqual(digest["stopReason"], "crashed")
        self.assertFalse(digest["crash"]["recovered"])
        self.assertIn("no save OF THIS RUN", digest["next"])
        self.assertIn("load_game", digest["next"])
        self.assertEqual(self.starts, [])
        self.assertEqual(self.stops, [])

    def test_a_crash_already_up_at_entry_is_caught_before_arming(self):
        # One fruitless call behind it, which is under the refusal, and a
        # crash at the door so this call moves no days either. The recovery
        # is what clears the count, not the clock.
        compose._RUN.update({"zeroCalls": 1, "zeroReasons": ["crashed"]})
        self.fake.crashed_from = 1
        digest = self._run()
        self.assertEqual(digest["stopReason"], "crash_recovered")
        # run_until is never armed over a dead game.
        self.assertEqual(self.fake.count("time.run_until"), 0)
        self.assertEqual(digest["daysAdvanced"], 0)
        self.assertEqual(compose._RUN["zeroCalls"], 0)

    def test_a_restart_forgets_the_macro_it_can_no_longer_watch(self):
        compose._RUN["macroOn"] = True
        self._run()
        self.assertFalse(compose._RUN["macroOn"])


class AutopilotMacroTest(unittest.TestCase):
    """The macro switches itself off on the first exception and says nothing.

    Autopilot.LogCallback clears its own `activated` and pauses unless it was
    started with IgnoreExceptions, and the harness left that switch unset. The
    run then keeps resuming the clock over a game nobody is driving.
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)

    def test_a_macro_that_switched_itself_off_stops_the_run(self):
        compose._RUN["macroOn"] = True
        fake = FakeBridge(CLEAR, blocked=False, days_per_poll=1,
                          autopilot={"present": True, "activated": False,
                                     "ignoreExceptions": False})
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 60, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "autopilot_off")
        self.assertIn("ignore_exceptions", digest["next"])
        self.assertFalse(compose._RUN["macroOn"])

    def test_an_engaged_macro_does_not_stop_the_run(self):
        compose._RUN["macroOn"] = True
        fake = FakeBridge(CLEAR, blocked=False, days_per_poll=1,
                          autopilot={"present": True, "activated": True,
                                     "ignoreExceptions": True})
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 2, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "reached")

    def test_the_macro_is_not_polled_when_this_server_did_not_start_it(self):
        fake = FakeBridge(CLEAR, blocked=False, days_per_poll=1,
                          autopilot={"present": True, "activated": False})
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 2, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "reached")
        self.assertEqual(fake.count("query.autopilot"), 0)


class ActionableStopTest(unittest.TestCase):
    """The stall refusal must not fire on a run that is working.

    Only a call that ended having done nothing AND named no cause counts
    toward it. Every other zero-day stop hands the caller something concrete:
    a decision to answer, a combat to dispose of, a macro to restart, a queue
    to inspect. Two prompts and a fight inside one in-game day is an ordinary
    campaign, and refusing that run would be a new way for an unattended run
    to stop, added by a change whose purpose is removing them.
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)

    HELD = {"screens": [], "narrativeNotes": [], "dismissed": [],
            "forced": [], "skipped": [{"name": "PromptSelectTech",
                                       "reason": "no tech to select"}],
            "remaining": 1, "blocked": True}

    def _decision(self):
        fake = FakeBridge(self.HELD)
        with mock.patch.object(compose, "bridge", fake):
            return compose.advance({"days": 1, "max_seconds": 600})

    def _inert(self):
        fake = FakeBridge(CLEAR, blocked=False)
        with mock.patch.object(compose, "bridge", fake):
            return compose.advance({"days": 30, "max_seconds": 0})

    def test_an_actionable_stop_clears_a_pending_count(self):
        self._inert()
        self.assertEqual(compose._RUN["zeroCalls"], 1)
        # The caller was handed something to do, so the run is not spinning in
        # silence and the count starts over.
        self._decision()
        self.assertEqual(compose._RUN["zeroCalls"], 0)
        # The count restarted, so the two inert calls that follow are the
        # first two rather than the ones that trip the refusal.
        self._inert()
        digest = self._inert()
        self.assertEqual(digest["stopReason"], "no_progress")
        self.assertEqual(compose._RUN["zeroCalls"], 2)

    def test_the_refusal_holds_until_the_caller_forces_it(self):
        # Deliberate, and the one stop this change adds: the guard is a door
        # check, so once two inert calls have tripped it nothing gets through
        # to discover a decision that may have appeared since. The message
        # sends the caller to observe and prompts, which is where they would
        # find it, and force=true is the way back in.
        self._inert()
        self._inert()
        with self.assertRaises(compose.ToolError):
            self._decision()
        fake = FakeBridge(self.HELD)
        with mock.patch.object(compose, "bridge", fake):
            digest = compose.advance({"days": 1, "max_seconds": 600,
                                      "force": True})
        self.assertEqual(digest["stopReason"], "decision")
        self.assertEqual(compose._RUN["zeroCalls"], 0)


class RunStateResetTest(unittest.TestCase):
    """_RUN spans tool calls, so it also spans processes and campaigns.

    A stale macroOn has the first advance in a NEW process poll a component
    that does not exist and stop the run pointing at an exception that never
    happened. A stale zeroCalls has the first advance of a new campaign
    refused for a stall belonging to a campaign no longer loaded.
    """

    def setUp(self):
        _no_sleep(self)
        _fresh_run_state(self)
        compose._RUN.update({"zeroCalls": 2, "zeroReasons": ["no_progress",
                                                            "no_progress"],
                             "crashRecoveries": 1, "macroOn": True})

    def test_game_start_that_launches_nothing_keeps_the_macro(self):
        # The bridge answers, so this takes the no-launch branch and touches
        # no process. The stall count is still dropped, but the macro is a
        # live component in a process nothing replaced: clearing it here
        # stopped advance polling query.autopilot, so a macro that had
        # switched itself off went unnoticed for the rest of the run.
        fake = FakeBridge(CLEAR)
        with mock.patch.object(compose, "bridge", fake):
            out = compose.game_start({})
        self.assertFalse(out["launched"])
        self.assertEqual(compose._RUN["zeroCalls"], 0)
        self.assertTrue(compose._RUN["macroOn"])
        # The one counter whose job is to outlive a restart. Clearing it here
        # would let the recovery's own game_start refund the budget it had
        # just spent, and the second crash would restart again, forever.
        self.assertEqual(compose._RUN["crashRecoveries"], 1)

    def test_campaign_new_resets(self):
        fake = FakeBridge(CLEAR)
        with mock.patch.object(compose, "bridge", fake):
            compose.campaign_new({"scenario": "2070Scenario"})
        self.assertEqual(compose._RUN["zeroCalls"], 0)
        self.assertEqual(compose._RUN["zeroReasons"], [])
        self.assertFalse(compose._RUN["macroOn"])
        self.assertIn("campaign.new", fake.calls)

    def test_load_game_resets_the_stall_count_only(self):
        fake = FakeBridge(CLEAR)
        with mock.patch.object(compose, "bridge", fake):
            compose.load_game({"name": "scratch-run"})
        self.assertEqual(compose._RUN["zeroCalls"], 0)
        # Same process: the macro is a live component that outlives the load.
        self.assertTrue(compose._RUN["macroOn"])

    def test_a_new_campaign_is_not_refused_for_the_last_one(self):
        # The whole point, end to end: two fruitless calls, a new campaign,
        # and the first advance of it runs instead of raising.
        fake = FakeBridge(CLEAR, blocked=False, days_per_poll=1)
        with mock.patch.object(compose, "bridge", fake):
            compose.campaign_new({"scenario": "2070Scenario"})
            digest = compose.advance({"days": 2, "max_seconds": 600})
        self.assertEqual(digest["stopReason"], "reached")


class CampaignTokenTest(unittest.TestCase):
    """The DLL names each campaign, and a name this server has not seen means
    the campaign every counter here describes is gone.

    The counters cannot notice that on their own. A campaign can be left and
    started again entirely through the game's own screens -- the options
    screen's exit, then the start screen -- with no verb of this server's
    involved, and everything it remembers would go on describing a campaign
    that no longer exists.
    """

    def setUp(self):
        # The launch case below polls for the bridge every five seconds, and
        # that wait is virtual here like every other in this file.
        _no_sleep(self)
        _fresh_run_state(self)
        saved = dict(bridge._campaign)
        self.addCleanup(bridge._campaign.update, saved)
        bridge._campaign["token"] = None
        bridge._campaign["process"] = None

    def _seen(self, token):
        """The token as it arrives: on an envelope, from any call."""
        bridge._note_stall({"ok": True, "campaignToken": token})

    def test_a_new_token_clears_the_zero_progress_count(self):
        self._seen("a-1")
        compose.note_campaign()
        compose._RUN.update({"zeroCalls": 2,
                             "zeroReasons": ["no_progress", "no_progress"],
                             "macroOn": True, "crashRecoveries": 1})
        self._seen("a-2")
        self.assertTrue(compose.note_campaign())
        self.assertEqual(compose._RUN["zeroCalls"], 0)
        self.assertEqual(compose._RUN["zeroReasons"], [])
        # The macro is a MonoBehaviour in a process nothing replaced, which is
        # why load_game resets with keep_macro. Clearing it here would stop
        # advance watching a macro that is still running. The crash budget
        # outlives the change too.
        self.assertTrue(compose._RUN["macroOn"])
        self.assertEqual(compose._RUN["crashRecoveries"], 1)
        # A campaign nobody ordered dates itself from now: nothing earlier is
        # known about it, so only saves written from here on are this run's.
        self.assertIsNotNone(compose._RUN["campaignAt"])

    def test_the_same_token_changes_nothing(self):
        self._seen("a-1")
        compose.note_campaign()
        compose._RUN["zeroCalls"] = 2
        self._seen("a-1")
        self.assertFalse(compose.note_campaign())
        self.assertEqual(compose._RUN["zeroCalls"], 2)

    def test_an_old_dll_sends_no_token_and_nothing_happens(self):
        compose._RUN["zeroCalls"] = 2
        bridge._note_stall({"ok": True, "clockStall": 1.0})
        self.assertFalse(compose.note_campaign())
        self.assertEqual(compose._RUN["zeroCalls"], 2)

    def test_a_load_binds_its_save_to_the_campaign_that_arrives(self):
        fake = FakeBridge(CLEAR)
        with mock.patch.object(compose, "bridge", fake):
            compose.load_game({"name": "scratch-run"})
        self.assertIsNone(compose._RUN["saveToken"])
        self._seen("a-1")
        compose.note_campaign()
        self.assertEqual(compose._RUN["save"]["name"], "scratch-run")
        self.assertEqual(compose._RUN["saveToken"], "a-1")

    def test_a_campaign_nobody_ordered_drops_the_run_save(self):
        # Options > Exit in game, then a campaign from the start screen. The
        # save the run was playing belongs to the campaign that is gone, and
        # a crash recovery reloading it would resume a different game.
        fake = FakeBridge(CLEAR)
        with mock.patch.object(compose, "bridge", fake):
            compose.load_game({"name": "scratch-run"})
        self._seen("a-1")
        compose.note_campaign()
        self._seen("a-2")
        compose.note_campaign()
        self.assertIsNone(compose._RUN["save"])
        self.assertIsNone(compose._RUN["saveToken"])

    def test_a_save_bound_to_another_campaign_is_not_re_bound(self):
        # A save written in the campaign a load is replacing. The entry is
        # ordered, so the campaign that arrives is the ordered one -- but the
        # save is already bound to the campaign that is gone, and binding it
        # here would have a crash recovery reload that other game.
        self._seen("a-1")
        compose.note_campaign()
        compose._RUN["save"] = {"name": "old-campaign", "extension": ".gz"}
        compose._RUN["saveToken"] = "a-1"
        compose._RUN["campaignPending"] = True
        self._seen("a-2")
        compose.note_campaign()
        self.assertIsNone(compose._RUN["save"])
        self.assertIsNone(compose._RUN["saveToken"])

    def test_a_launch_forgets_a_campaign_entry_that_never_arrived(self):
        # The load was ordered and the game died during it, so the campaign
        # never came up. Left standing, `campaignPending` promises that the
        # NEXT campaign is the ordered one, and the save from the dead
        # process would be bound to a campaign started by hand in the new one
        # -- which a crash recovery would then reload.
        compose._entered_campaign("scratch-run", ".gz")
        ups = iter([False, True])
        with mock.patch.object(compose, "_bridge_up",
                               lambda: next(ups, True)), \
                mock.patch.object(compose, "IS_WINDOWS", False), \
                mock.patch.object(compose.subprocess, "Popen",
                                  lambda *a, **kw: None):
            out = compose.game_start({})
        self.assertTrue(out["launched"])
        self.assertIsNone(compose._RUN["save"])
        self.assertFalse(compose._RUN["campaignPending"])
        self.assertIsNone(compose._RUN["campaignAt"])
        # And the campaign that does come up is nobody's ordered one.
        self._seen("b-1")
        compose.note_campaign()
        self.assertIsNone(compose._RUN["save"])

    def test_a_stop_forgets_it_too(self):
        compose._entered_campaign("scratch-run", ".gz")
        with mock.patch.object(compose, "IS_WINDOWS", False), \
                mock.patch.object(compose, "_game_pids", lambda: []), \
                mock.patch.object(compose, "_bridge_up", lambda: False):
            compose.game_stop({})
        self.assertIsNone(compose._RUN["save"])
        self.assertFalse(compose._RUN["campaignPending"])
        self._seen("b-1")
        compose.note_campaign()
        self.assertIsNone(compose._RUN["save"])


class CampaignProcessTest(unittest.TestCase):
    """A game process nobody here replaced is still a game that was replaced.

    game_start and game_stop forget the campaign entry for the processes this
    server ends, and for a long time that was every process it knew of. It is
    not: a person can close the game at the console and launch it again, which
    is the ordinary way the Windows test machine gets a game at all. The entry
    left behind promises that the next campaign token is the one this run
    ordered, and carries a save file from a game that no longer exists.

    The token cannot see it. Its counter restarts at 1 in every process, so
    the first campaign of the replacement game answers a token this session
    may already have handed out. The process key is what differs, and it
    arrives on the first envelope of the new game, before any campaign is up.
    """

    def setUp(self):
        _fresh_run_state(self)
        saved = dict(bridge._campaign)
        self.addCleanup(bridge._campaign.update, saved)
        bridge._campaign["token"] = None
        bridge._campaign["process"] = None

    def _process(self, process):
        """An envelope from a game with no campaign loaded."""
        bridge._note_stall({"ok": True, "campaignProcess": process})

    def _seen(self, token, process):
        bridge._note_stall({"ok": True, "campaignToken": token,
                            "campaignProcess": process})

    def test_a_campaign_in_a_new_process_is_not_the_ordered_one(self):
        self._process("aaa")
        compose._entered_campaign("scratch-run", ".gz")
        # A person relaunched the game and started a campaign from the start
        # screen. Its token is the first of the new process, which says
        # nothing about the entry ordered in the old one.
        self._seen("bbb-1", "bbb")
        compose.note_campaign()
        self.assertIsNone(compose._RUN["save"])
        self.assertIsNone(compose._RUN["saveToken"])
        self.assertFalse(compose._RUN["campaignPending"])

    def test_the_entry_is_forgotten_before_any_campaign_comes_up(self):
        # The process key arrives with no campaign loaded, so the entry is
        # gone before the start screen has been touched.
        self._process("aaa")
        compose._entered_campaign("scratch-run", ".gz")
        self._process("bbb")
        self.assertFalse(compose.note_campaign())
        self.assertIsNone(compose._RUN["save"])
        self.assertFalse(compose._RUN["campaignPending"])
        self.assertIsNone(compose._RUN["campaignAt"])

    def test_a_launch_of_our_own_keeps_the_entry_it_ordered(self):
        # The trap in keying this on "the process changed since the last tool
        # call": game_start replaces the process AND orders the entry, in that
        # order and inside one call, so the next call would find a process it
        # had not seen and throw away the entry the launch had just made --
        # taking the save a crash recovery reloads with it. The entry is
        # keyed on the process it was ordered IN, which is the new one.
        self._process("aaa")
        self._process("bbb")
        compose._entered_campaign("scratch-run", ".gz")
        self._seen("bbb-1", "bbb")
        compose.note_campaign()
        self.assertEqual(compose._RUN["save"]["name"], "scratch-run")
        self.assertEqual(compose._RUN["saveToken"], "bbb-1")

    def test_an_old_dll_sends_no_process_and_nothing_changes(self):
        # No key on the envelope is no information, not a process that
        # changed: the entry survives exactly as it did before this existed.
        compose._entered_campaign("scratch-run", ".gz")
        self._seen("aaa-1", None)
        compose.note_campaign()
        self.assertEqual(compose._RUN["save"]["name"], "scratch-run")
        self.assertEqual(compose._RUN["saveToken"], "aaa-1")


class WaitSecondsTest(unittest.TestCase):
    """_wait_secs reads a wait argument that may be absent, a flag or a number.

    The test it replaces was `value in (None, True, False)`, and bool is an
    int in Python: 0 equals False and 1 equals True. So a caller asking for no
    wait at all, or for a one-second one, was given the full default instead --
    up to five minutes spent waiting by a call that asked not to.
    """

    def test_absent_and_flags_take_the_default_and_a_number_is_the_callers(
            self):
        self.assertEqual(compose._wait_secs(None, 300), 300)
        # A boolean says "wait", not how long. int(True) is a one-second wait,
        # which is a wait that always fails.
        self.assertEqual(compose._wait_secs(True, 300), 300)
        self.assertEqual(compose._wait_secs(False, 300), 300)
        self.assertEqual(compose._wait_secs(0, 300), 0)
        self.assertEqual(compose._wait_secs(1, 300), 1)
        self.assertEqual(compose._wait_secs(45, 300), 45)


class RunSaveTest(unittest.TestCase):
    """_run_save picks what a crash recovery may reload.

    Never simply the newest save on disk: the folder holds other people's
    campaigns and other runs of this harness, and restarting into one of those
    resumes a game nobody asked for while reporting a recovery.
    """

    ENTERED = "2026-08-30 08:00:00"
    OLD_AUTO = {"name": "Autosave", "extension": ".gz",
                "mtime": "2026-08-29 12:00:00"}
    NEW_AUTO = {"name": "Autosave", "extension": ".gz",
                "mtime": "2026-08-30 10:00:00"}
    COMBAT_AUTO = {"name": "CombatAutosave", "extension": ".gz",
                   "mtime": "2026-08-30 11:00:00"}
    EXIT = {"name": "ExitSave", "extension": ".gz",
            "mtime": "2026-08-30 12:00:00"}
    STRANGER = {"name": "Cooperatesave00053", "extension": ".gz",
                "mtime": "2026-09-01 12:00:00"}
    LOADED = {"name": "scratch-run", "extension": ".gz",
              "mtime": "2026-08-30 07:00:00"}

    def setUp(self):
        _fresh_run_state(self)
        compose._RUN["campaignAt"] = self.ENTERED

    def _pick(self, rows):
        fake = FakeBridge(CLEAR, saves=list(rows))
        with mock.patch.object(compose, "bridge", fake):
            return compose._run_save()

    def test_an_autosave_from_before_the_campaign_does_not_qualify(self):
        self.assertIsNone(self._pick([self.OLD_AUTO]))

    def test_the_combat_autosave_and_the_exit_save_count(self):
        self.assertEqual(self._pick([self.COMBAT_AUTO])["name"],
                         "CombatAutosave")
        self.assertEqual(self._pick([self.EXIT])["name"], "ExitSave")

    def test_the_loaded_save_qualifies_though_it_predates_the_entry(self):
        # It has to: it was written before the load that entered the campaign.
        compose._RUN["save"] = {"name": "scratch-run", "extension": ".gz"}
        self.assertEqual(self._pick([self.LOADED, self.OLD_AUTO])["name"],
                         "scratch-run")

    def test_the_newest_qualifying_save_wins(self):
        # STRANGER is the newest file in the folder by a day, written while
        # this run was going, and still not this run's: nothing here wrote it.
        compose._RUN["save"] = {"name": "scratch-run", "extension": ".gz"}
        rows = [self.LOADED, self.OLD_AUTO, self.NEW_AUTO, self.STRANGER]
        self.assertEqual(self._pick(rows)["name"], "Autosave")

    def test_the_twin_the_run_loaded_is_the_one_matched(self):
        # Same stem, both formats on disk, and the older file is the .json.
        # The recorded extension decides, not the mtime.
        compose._RUN["save"] = {"name": "scratch-run", "extension": ".json"}
        rows = [dict(self.LOADED, mtime="2026-08-30 07:30:00"),
                dict(self.LOADED, extension=".json")]
        self.assertEqual(self._pick(rows)["extension"], ".json")

    def test_an_unknown_extension_falls_back_to_the_newer_twin(self):
        # load_game without one. Which file the game read went through the
        # profile setting and is not knowable from here, so the pick is the
        # newer of the two, and the extension travels with it.
        compose._RUN["save"] = {"name": "scratch-run", "extension": None}
        rows = [self.LOADED, dict(self.LOADED, extension=".json",
                                  mtime="2026-08-30 07:30:00")]
        self.assertEqual(self._pick(rows)["extension"], ".json")

    def test_a_run_that_never_entered_a_campaign_picks_nothing(self):
        compose._RUN["campaignAt"] = None
        self.assertIsNone(self._pick([self.NEW_AUTO, self.STRANGER]))


class SaveGameRecordTest(unittest.TestCase):
    """A save this run wrote is a save this run may come back to."""

    def setUp(self):
        _fresh_run_state(self)
        compose._RUN["campaignToken"] = "a-1"

    def test_save_game_records_the_file_it_wrote(self):
        fake = FakeBridge(CLEAR)
        with mock.patch.object(compose, "bridge", fake):
            compose.save_game({"name": "scratch-topic"})
        self.assertEqual(compose._RUN["save"],
                         {"name": "scratch-topic", "extension": ".gz"})
        # Bound to the campaign it was written in, not to the next one.
        self.assertEqual(compose._RUN["saveToken"], "a-1")


if __name__ == "__main__":
    unittest.main()
