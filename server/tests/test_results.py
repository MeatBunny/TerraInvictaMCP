"""JSON tool results stay parseable and distinguish omitted data from success."""
import json
import os
import sys
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
    def test_small_results_keep_their_pretty_format_and_values(self):
        for data in (None, [], {"ok": True, "text": "quoted \"text\"\n"}):
            with self.subTest(data=data):
                result = tools.json_result(data)
                self.assertFalse(result["isError"])
                self.assertEqual(result["content"][0]["text"],
                                 json.dumps(data, indent=2))

    def test_large_results_that_fit_remain_complete_and_compact(self):
        data = {"text": "x" * tools.PRETTY_LIMIT}
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

    def test_limit_counts_serialized_characters_including_escapes(self):
        data = {"text": '\"\\\n\t\u2603\U0001f680' * 10000}
        self.assertLess(len(data["text"]), tools.TEXT_LIMIT)
        self.assertGreater(len(compact(data)), tools.TEXT_LIMIT)
        result = tools.json_result(data)
        self.assertTrue(result["isError"])
        report = json.loads(result["content"][0]["text"])
        self.assertEqual(report["textLength"], len(compact(data)))
        self.assertNotIn("text", report)


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


if __name__ == "__main__":
    unittest.main()
