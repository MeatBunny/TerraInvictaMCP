"""ai_autopilot forwards victory_bias to ai.control on engage and nowhere else.

  python3 -m unittest discover -s server/tests

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
import _offline                                     # noqa: E402,F401
import compose                                      # noqa: E402
import tools                                        # noqa: E402


class FakeBridge:

    def __init__(self):
        self.calls = []

    def call(self, verb, args=None, timeout=None):
        self.calls.append((verb, args))
        return {}


def run(args):
    fake = FakeBridge()
    with mock.patch.object(compose, "bridge", fake):
        compose.ai_autopilot(args, None)
    return fake.calls


class AiAutopilotTest(unittest.TestCase):

    def test_engage_forwards_the_flag(self):
        self.assertEqual(run({"action": "engage", "victory_bias": True}),
                         [("ai.control", {"action": "engage",
                                          "smart": "brutal",
                                          "victory_bias": True})])

    def test_engage_without_the_flag_sends_no_key(self):
        self.assertEqual(run({"action": "engage", "smart": "campaign"}),
                         [("ai.control", {"action": "engage",
                                          "smart": "campaign"})])

    def test_release_and_status_forward_neither(self):
        for action in ("release", "status"):
            self.assertEqual(run({"action": action, "smart": "campaign",
                                  "victory_bias": True}),
                             [("ai.control", {"action": action})])

    def test_schema_has_the_boolean(self):
        definition = next(d for d in tools.TOOL_DEFS
                          if d["name"] == "ai_autopilot")
        props = definition["inputSchema"]["properties"]
        self.assertEqual(props["victory_bias"]["type"], "boolean")


if __name__ == "__main__":
    unittest.main()
