"""JSON tool results stay parseable and distinguish omitted data from success."""
import json
import os
import sys
import tempfile
import unittest
from unittest import mock

SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
import _offline                                     # noqa: E402,F401
import tools                                        # noqa: E402


def compact(data):
    return json.dumps(data, separators=(",", ":"))


class JsonResultTest(unittest.TestCase):
    def test_results_that_fit_are_compact_and_keep_their_values(self):
        for data in (None, [], {"ok": True, "text": "quoted \"text\"\n"},
                     {"text": "x" * 8192}):
            with self.subTest(data=data):
                result = tools.json_result(data)
                self.assertFalse(result["isError"])
                self.assertEqual(result["content"][0]["text"], compact(data))

    def test_text_limit_boundary(self):
        overhead = len(compact({"text": ""}))
        for offset in (-1, 0, 1):
            with self.subTest(offset=offset):
                data = {"text": "x" * (tools.TEXT_LIMIT - overhead + offset)}
                result = tools.json_result(data)
                text = result["content"][0]["text"]
                decoded = json.loads(text)
                self.assertLessEqual(len(text), tools.TEXT_LIMIT)
                self.assertEqual(result["isError"], offset > 0)
                if offset <= 0:
                    self.assertEqual(decoded, data)
                else:
                    self.assertEqual(decoded["error"], "response_too_large")
                    self.assertEqual(decoded["textLength"], tools.TEXT_LIMIT + 1)
                    self.assertEqual(decoded["textLimit"], tools.TEXT_LIMIT)
                    self.assertIn("may already have completed", decoded["message"])
                    self.assertIn("inspect current state", decoded["message"])
                    self.assertEqual(decoded["head"],
                                     compact(data)[:tools.HEAD_CHARS])

    def test_limit_counts_serialized_characters_including_escapes(self):
        data = {"text": '\"\\\n\t\u2603\U0001f680' * 10000}
        self.assertLess(len(data["text"]), tools.TEXT_LIMIT)
        self.assertGreater(len(compact(data)), tools.TEXT_LIMIT)
        result = tools.json_result(data)
        self.assertTrue(result["isError"])
        report = json.loads(result["content"][0]["text"])
        self.assertEqual(report["textLength"], len(compact(data)))
        self.assertNotIn("text", report)
        self.assertEqual(len(report["head"]), tools.HEAD_CHARS)


class DispatchResultTest(unittest.TestCase):
    def setUp(self):
        patch = mock.patch.object(tools.compose, "bridge",
                                  _offline.DispatchBridge())
        patch.start()
        self.addCleanup(patch.stop)

    def test_oversized_console_output_is_an_error_without_repeating_handler(self):
        data = {"output": ["TI_DEV_RESULT:" + json.dumps(
            {"ok": True, "detail": "x" * tools.TEXT_LIMIT})]}
        handler = mock.Mock(return_value=data)
        with mock.patch.dict(tools.BY_NAME, {"fake_tool": handler}):
            result = tools.handle_call("fake_tool", {})
        handler.assert_called_once_with({}, None)
        self.assertTrue(result["isError"])
        report = json.loads(result["content"][0]["text"])
        self.assertEqual(report["error"], "response_too_large")
        self.assertNotIn("output", report)
        self.assertIn("TI_DEV_RESULT:", report["head"])

    def test_handler_failed_flag_cannot_clear_formatter_failure(self):
        for failed in (False, True):
            with self.subTest(failed=failed):
                handler = mock.Mock(return_value={
                    "text": "x" * tools.TEXT_LIMIT, "_failed": failed})
                with mock.patch.dict(tools.BY_NAME, {"fake_tool": handler}):
                    result = tools.handle_call("fake_tool", {})
                self.assertTrue(result["isError"])
                report = json.loads(result["content"][0]["text"])
                self.assertEqual(report["error"], "response_too_large")
                self.assertNotIn("_failed", report)

    def test_pause_banner_keeps_the_error_payload_parseable(self):
        handler = mock.Mock(return_value={"text": "x" * tools.TEXT_LIMIT})
        with mock.patch.dict(tools.BY_NAME, {"fake_tool": handler}), \
                mock.patch.object(tools.compose, "pause_gate",
                                  return_value=(None, "PAUSE LIMIT EXCEEDED")):
            result = tools.handle_call("fake_tool", {})
        self.assertTrue(result["isError"])
        self.assertEqual(result["content"][0]["text"], "PAUSE LIMIT EXCEEDED")
        report = json.loads(result["content"][1]["text"])
        self.assertEqual(report["error"], "response_too_large")


class CallLogTest(unittest.TestCase):
    """Every dispatch appends one line: outcome, stall, args cut at 200."""

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.log = os.path.join(tmp.name, "calls.log")
        open(self.log, "w").close()
        for patch in (
                mock.patch.object(tools, "CALL_LOG", self.log),
                mock.patch.object(tools.compose, "bridge",
                                  _offline.DispatchBridge()),
                mock.patch.object(tools.bridge, "last_stall",
                                  return_value=(12.3, 0.0))):
            patch.start()
            self.addCleanup(patch.stop)

    def test_one_line_per_call_with_its_outcome(self):
        handlers = (mock.Mock(return_value={"ok": True}),
                    mock.Mock(side_effect=tools.ToolError("no")),
                    mock.Mock(side_effect=tools.bridge.BridgeError("down")),
                    mock.Mock(return_value={"_failed": True}))
        long_args = {"x": "y" * 300}
        for handler in handlers:
            with mock.patch.dict(tools.BY_NAME, {"fake_tool": handler}):
                tools.handle_call("fake_tool", long_args)
        with open(self.log) as f:
            lines = f.read().splitlines()
        self.assertEqual([l.split()[3] for l in lines],
                         ["outcome=ok", "outcome=refused", "outcome=error",
                          "outcome=error"])
        for line in lines:
            self.assertEqual(line.split()[1], "tool=fake_tool")
            self.assertEqual(line.split()[4], "stall=12.3")
            shown = line.split(" args=", 1)[1]
            self.assertEqual(len(shown), 203)
            self.assertTrue(shown.endswith("..."))
            self.assertEqual(shown[:200], compact(long_args)[:200])

    def test_a_call_refused_before_dispatch_is_logged_too(self):
        tools.handle_call("no_such_tool", {"a": 1})
        tools.handle_call("observe", ["not", "an", "object"])
        with open(self.log) as f:
            lines = f.read().splitlines()
        self.assertEqual([l.split()[1:4:2] for l in lines],
                         [["tool=no_such_tool", "outcome=refused"],
                          ["tool=observe", "outcome=refused"]])
        self.assertTrue(lines[1].endswith('args=["not","an","object"]'))


if __name__ == "__main__":
    unittest.main()
