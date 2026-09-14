"""Unit tests for the crash_the_game test fixture's tool side.

  python3 -m unittest discover -s server/tests

The tool exists to end the session on purpose, and the DLL is what refuses a
call without the confirmation literal. What is testable offline is the tool
side: the literal reaches the verb unchanged, an unknown-verb refusal from an
older DLL names the rebuild, and the tool table marks the fixture destructive
and warns before it explains.
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


CONFIRM = compose.CRASH_CONFIRM


class FakeBridge:
    """Records every verb call."""

    def __init__(self, reply=None):
        self.reply = reply if reply is not None else {
            "triggered": True, "crashed": True}
        self.calls = []

    def call(self, verb, args=None, timeout=None):
        self.calls.append((verb, args))
        return self.reply


class ConfirmedTest(unittest.TestCase):

    def test_the_exact_literal_fires_the_verb(self):
        fake = FakeBridge()
        with mock.patch.object(compose, "bridge", fake):
            data = compose.crash_the_game({"confirm": CONFIRM}, None)
        self.assertEqual(fake.calls,
                         [("test.crash_the_game", {"confirm": CONFIRM})])
        self.assertEqual(data, {"triggered": True, "crashed": True})

    def test_an_older_dll_names_the_rebuild(self):
        # The DLL refuses a verb it does not serve by name; handle_call adds
        # the fix to that refusal.
        older = mock.Mock(side_effect=tools.bridge.VerbError(
            "unknown command 'test.crash_the_game'"))
        with mock.patch.object(compose.bridge, "call", older), \
                mock.patch.object(compose, "pause_gate",
                                  return_value=(None, None)), \
                mock.patch.object(compose, "note_campaign"):
            result = tools.handle_call("crash_the_game",
                                       {"confirm": CONFIRM}, None)
        self.assertTrue(result["isError"])
        text = result["content"][0]["text"]
        self.assertIn("test.crash_the_game", text)
        self.assertIn("install.sh", text)


class RegistrationTest(unittest.TestCase):

    def _entry(self):
        for entry in tools.TOOLS:
            if entry[0] == "crash_the_game":
                return entry
        self.fail("crash_the_game is not in the tool table")

    def test_annotated_destructive_and_not_read_only(self):
        _name, _handler, read_only, destructive, _desc, _props = self._entry()
        self.assertFalse(read_only)
        self.assertTrue(destructive)

    def test_confirm_is_optional_in_the_schema(self):
        # Deliberate: a schema-required argument turns an accidental call into
        # a client-side validation error, and the refusal text is the whole
        # point of the argument.
        defs = [d for d in tools.TOOL_DEFS if d["name"] == "crash_the_game"]
        self.assertEqual(len(defs), 1)
        self.assertNotIn("required", defs[0]["inputSchema"])
        self.assertIn(CONFIRM,
                      defs[0]["inputSchema"]["properties"]["confirm"]
                      ["description"])

    def test_the_description_warns_before_it_explains(self):
        desc = self._entry()[4]
        self.assertTrue(desc.startswith("TEST FIXTURE."), desc[:40])
        self.assertIn("ENDS THE SESSION", desc)
        self.assertIn(CONFIRM, desc)

    def test_the_verb_is_declared_so_selftest_covers_it(self):
        # selftest diffs this set against the DLL's registry; a verb missing
        # from it hides in the unmapped list instead of failing.
        self.assertIn("test.crash_the_game", tools.BRIDGE_VERBS_USED)


if __name__ == "__main__":
    unittest.main()
