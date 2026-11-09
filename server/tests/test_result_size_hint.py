"""Every JSON tool declares the result size the server enforces.

  python3 -m unittest discover -s server/tests

Claude Code reads `_meta["anthropic/maxResultSizeChars"]` to decide how much
of a result to keep inline; the value is the server's own TEXT_LIMIT, so the
two never disagree. Tools that return raw content blocks (log_tail,
screenshot) bypass that limit and carry no hint. Nothing here needs a
running game.
"""
import os
import sys
import unittest


SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
import _offline                                     # noqa: E402,F401
import tools                                        # noqa: E402


class ResultSizeHintTest(unittest.TestCase):

    def test_every_json_tool_carries_the_hint(self):
        for definition in tools.TOOL_DEFS:
            if definition["name"] in tools.CONTENT_TOOLS:
                self.assertNotIn("_meta", definition, definition["name"])
                continue
            self.assertEqual(
                definition["_meta"]["anthropic/maxResultSizeChars"],
                tools.TEXT_LIMIT, definition["name"])

    def test_content_tools_are_the_two_that_return_blocks(self):
        self.assertEqual(tools.CONTENT_TOOLS, {"log_tail", "screenshot"})


if __name__ == "__main__":
    unittest.main()
