"""compose.console narrows output lines with contains and limit.

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


LINES = ["alpha 1", "beta 2", "alpha 3"]


class FakeBridge:

    def call(self, verb, args=None, timeout=None):
        return {"output": list(LINES), "errors": []}


def run(args):
    with mock.patch.object(compose, "bridge", FakeBridge()):
        return compose.console(dict(args, line="listfactions"), None)


class ConsoleToolTest(unittest.TestCase):

    def test_no_args_keeps_every_line(self):
        self.assertEqual(run({}), {"output": LINES, "errors": [], "count": 3,
                                   "returned": 3, "truncated": False})

    def test_limit_only(self):
        data = run({"limit": 2})
        self.assertEqual(data["output"], LINES[:2])
        self.assertEqual((data["count"], data["returned"], data["truncated"]),
                         (3, 2, True))

    def test_contains_only(self):
        data = run({"contains": "^alpha"})
        self.assertEqual(data["output"], ["alpha 1", "alpha 3"])
        self.assertEqual((data["count"], data["returned"], data["truncated"]),
                         (2, 2, False))

    def test_both(self):
        data = run({"contains": "alpha", "limit": 1})
        self.assertEqual(data["output"], ["alpha 1"])
        self.assertEqual((data["count"], data["returned"], data["truncated"]),
                         (2, 1, True))

    def test_bad_limit_refused(self):
        for bad in (0, -1, "2", 2.5, True):
            with self.assertRaises(compose.ToolError, msg=repr(bad)):
                run({"limit": bad})

    def test_bad_regex_refused(self):
        with self.assertRaises(compose.ToolError):
            run({"contains": "("})


if __name__ == "__main__":
    unittest.main()
