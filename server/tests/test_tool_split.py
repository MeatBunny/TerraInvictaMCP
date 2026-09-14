"""Unit tests for the three tools that read and wrote through one name.

  python3 -m unittest discover -s server/tests

`faction_relations`, `ui_view` and `ui_screen` each served a bare read form and
an argument-driven write from the same tool, so each had to be marked a write
in the tool table. The pause gate reads that column: a write is refused over a
stopped clock and a read runs with the banner. Reading a paused campaign's
relations, or asking which screen is up, was therefore refused -- a guard
against building state over a dead clock, stopping the calls that build none.

Each tool is now one or the other, and what holds the split is here: the reads
refuse the arguments that would write, the writes refuse a call with nothing to
do, and `ui_status` is both UI reads in one answer. A defect in any of those
reads as a tool that quietly does something other than its annotation claims,
which nothing downstream catches.

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
import compose                                      # noqa: E402
import tools                                        # noqa: E402


class FakeBridge:
    """The three verbs these tools call, and a record of what reached them."""

    VERBS = frozenset(("faction.relations", "ui.view", "ui.screen"))

    def __init__(self, replies=None):
        self.replies = replies or {}
        self.calls = []

    def verbs(self, refresh=False):
        return set(self.VERBS)

    def call(self, verb, args=None, timeout=None):
        self.calls.append((verb, dict(args or {})))
        return self.replies.get(verb, {})

    def args_for(self, verb):
        return [a for v, a in self.calls if v == verb]


class SplitTestCase(unittest.TestCase):
    """Every case runs the handler against the fake bridge."""

    def run_tool(self, handler, args, replies=None):
        fake = FakeBridge(replies)
        with mock.patch.object(compose, "bridge", fake):
            return handler(args), fake

    def refuse(self, handler, args):
        """The refusal text, plus the assertion that nothing reached a verb."""
        fake = FakeBridge()
        with mock.patch.object(compose, "bridge", fake):
            with self.assertRaises(compose.ToolError) as caught:
                handler(args)
        self.assertEqual(fake.calls, [], "the refused call still sent a verb")
        return str(caught.exception)


class FactionRelationsReadTest(SplitTestCase):
    """The read passes its two arguments through and writes nothing."""

    def test_the_read_passes_its_arguments_through(self):
        # A bare call reads the active player, a faction reads someone else,
        # a pair narrows the read.
        for args in ({}, {"faction": 3}, {"faction": 3, "other": 5}):
            with self.subTest(args=args):
                _, fake = self.run_tool(compose.faction_relations, dict(args))
                self.assertEqual(fake.args_for("faction.relations"), [args])

    def test_a_hate_of_zero_is_still_a_write(self):
        # The whole point of the split: the read tool is annotated read-only
        # and the gate believes it, so a write reaching this handler would be
        # a state change made over a clock the gate had already stopped. Zero
        # hate is a value like any other, and a falsy test here would let the
        # one write that resets a pair through the read tool.
        text = self.refuse(compose.faction_relations,
                           {"faction": 3, "other": 5, "hate": 0})
        self.assertIn("set_faction_relation", text)


class SetFactionRelationTest(SplitTestCase):
    """The write demands the pair and the value; the verb defaults neither."""

    # hate 0 on purpose: zero is a value, and a falsy test would refuse the
    # one write that resets a pair.
    FULL = {"faction": 3, "other": 5, "hate": 0,
            "cant_conflagrate": True, "cause": "test"}

    def test_a_full_call_reaches_the_verb_unchanged(self):
        _, fake = self.run_tool(compose.set_faction_relation, dict(self.FULL))
        self.assertEqual(fake.args_for("faction.relations"), [self.FULL])

    def test_without_hate_it_is_refused(self):
        # Without this the call reads a table and reports it as a write: the
        # verb takes the write off the presence of `hate`.
        text = self.refuse(compose.set_faction_relation,
                           {"faction": 3, "other": 5})
        self.assertIn("hate", text)
        self.assertIn("faction_relations", text)

    def test_without_the_pair_it_is_refused(self):
        # The verb defaults the subject to the active player. A write aimed at
        # whoever happens to be active is one nobody can read back.
        text = self.refuse(compose.set_faction_relation, {"other": 5,
                                                          "hate": 40})
        self.assertIn("faction", text)
        text = self.refuse(compose.set_faction_relation, {"faction": 3,
                                                          "hate": 40})
        self.assertIn("other", text)

    def test_a_boolean_is_not_a_hate_value(self):
        # bool is an int in Python, so True would ride to the engine as a
        # hate of 1 and be reported as the value that was asked for.
        text = self.refuse(compose.set_faction_relation,
                           {"faction": 3, "other": 5, "hate": True})
        self.assertIn("number", text)


class UiViewTest(SplitTestCase):
    """ui_view moves the view and nothing else."""

    def test_a_view_reaches_the_verb(self):
        _, fake = self.run_tool(compose.ui_view, {"view": "PoliticalMap"})
        self.assertEqual(fake.args_for("ui.view"), [{"view": "PoliticalMap"}])

    def test_an_empty_view_is_refused_and_names_the_read(self):
        # The verb reads an empty string as no argument and answers with the
        # bare read, which is the form that moved to ui_status.
        self.assertIn("ui_status", self.refuse(compose.ui_view, {"view": ""}))


class UiScreenTest(SplitTestCase):
    """ui_screen needs something to open, close or press."""

    def drives(self, args):
        _, fake = self.run_tool(compose.ui_screen, args)
        return fake.args_for("ui.screen")

    def test_each_drive_reaches_the_verb(self):
        # {"hab": 0}: presence, not truth. A falsy-id test would refuse a real
        # state id. And manage rides along with a hab.
        for args in ({"show": "habitats"}, {"hide": True}, {"hab": 12},
                     {"detail": 12}, {"rename": True}, {"hab": 0},
                     {"hab": 12, "manage": True}):
            self.assertEqual(self.drives(dict(args)), [args])

    def test_nothing_to_do_is_refused_and_names_the_read(self):
        text = self.refuse(compose.ui_screen, {})
        self.assertIn("ui_status", text)

    def test_a_false_flag_is_not_a_drive(self):
        text = self.refuse(compose.ui_screen, {"hide": False,
                                               "rename": False})
        self.assertIn("ui_status", text)

    def test_list_is_refused_and_names_the_read(self):
        # The bare listing is the read that moved; leaving it reachable here
        # would keep a read behind a tool the pause gate refuses.
        text = self.refuse(compose.ui_screen, {"list": True})
        self.assertIn("ui_status", text)

    def test_list_beside_a_drive_is_refused_too(self):
        text = self.refuse(compose.ui_screen, {"show": "habitats",
                                               "list": True})
        self.assertIn("ui_status", text)

    def test_manage_needs_a_hab(self):
        text = self.refuse(compose.ui_screen, {"manage": True})
        self.assertIn("hab", text)
        text = self.refuse(compose.ui_screen, {"show": "habitats",
                                               "manage": True})
        self.assertIn("hab", text)


class UiStatusTest(SplitTestCase):
    """ui_status is both UI reads, merged into one answer."""

    VIEW = {"scene": "SolarSystemScene", "currentView": "SolarSystem",
            "settable": ["SolarSystem", "PoliticalMap"], "changed": False}
    SCREEN = {"activeInfoScreen": "HabitatsScreenController",
              "screens": ["HabitatsScreenController",
                          "FleetsScreenController"],
              "action": "list"}

    def status(self, view=None, screen=None):
        replies = {"ui.view": self.VIEW if view is None else view,
                   "ui.screen": self.SCREEN if screen is None else screen}
        return self.run_tool(compose.ui_status, {}, replies)

    def test_it_asks_both_verbs_as_reads_and_carries_both_halves(self):
        out, fake = self.status()
        self.assertEqual(fake.args_for("ui.view"), [{}])
        self.assertEqual(fake.args_for("ui.screen"), [{"list": True}])
        self.assertEqual(out["currentView"], "SolarSystem")
        self.assertEqual(out["scene"], "SolarSystemScene")
        self.assertEqual(out["settable"], ["SolarSystem", "PoliticalMap"])
        self.assertEqual(out["activeInfoScreen"], "HabitatsScreenController")
        self.assertEqual(len(out["screens"]), 2)
        # The drive bookkeeping is left out. Both keys describe a call that
        # asked for something. This one asks for nothing, so reporting
        # changed: false and action: list would be answering a question
        # nobody put.
        self.assertNotIn("changed", out)
        self.assertNotIn("action", out)

    def test_a_key_from_both_verbs_keeps_both_values(self):
        # No key comes back from both today. If a game update makes one, the
        # merge must not drop half the answer in silence.
        out, _ = self.status(screen=dict(self.SCREEN, scene="OtherScene"))
        self.assertEqual(out["scene"], "SolarSystemScene")
        self.assertEqual(out["screen_scene"], "OtherScene")

    def test_a_note_from_the_screen_verb_survives(self):
        # The verb says so when its reflection came up empty, and that note is
        # the difference between an empty campaign and a renamed engine field.
        out, _ = self.status(screen=dict(self.SCREEN, note="field missing"))
        self.assertEqual(out["note"], "field missing")


class ToolTableTest(unittest.TestCase):
    """The annotations are the contract the pause gate and clients read.

    The read-only column itself is asserted in test_pause_limit beside the
    gate decision each value produces.
    """

    def test_the_split_pairs_name_each_other(self):
        # A tool that refuses an argument has to say where that argument went,
        # or the refusal is a dead end.
        text = {t[0]: t[4] for t in tools.TOOLS}
        self.assertIn("set_faction_relation", text["faction_relations"])
        self.assertIn("faction_relations", text["set_faction_relation"])
        self.assertIn("ui_status", text["ui_view"])
        self.assertIn("ui_status", text["ui_screen"])
        self.assertIn("ui_view", text["ui_status"])
        self.assertIn("ui_screen", text["ui_status"])


if __name__ == "__main__":
    unittest.main()
