"""Unit tests for the JSON-RPC read loop and the tool-call entry point.

  python3 -m unittest discover -s server/tests

Everything the loop reads comes from a client that may send any JSON at all,
and the loop is a single process serving one session: a message that raises
takes the session down with it, and every request queued behind it dies
unanswered. To the agent on the other end that reads as a tool set that
vanished mid-run, not as one request it got wrong, so the failure is both
total and misattributed.

The other half is what must NOT be written. A notification carries no id,
JSON-RPC forbids answering one, and a reply with a null id is a response no
request is waiting for -- some clients drop the connection over it.

Nothing here needs a running game. Every case below drives the real handler,
not a copy of its logic.
"""
import importlib.util
import io
import json
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


class OfflineBridge:
    """A bridge stand-in for the cases that reach a handler.

    handle_call takes a stall reading and a campaign token before it
    dispatches, both through the bridge module. Without this the case below
    dials the real port -- and on a machine with the game running it reads
    that session's clock and can be refused by its pause limit, which is a
    test that passes or fails on what is on screen somewhere else.
    """

    def last_stall(self):
        return None, 0.0

    def last_campaign_token(self):
        return None

    def last_campaign_process(self):
        return None

    def call(self, verb, args=None, **kw):
        raise AssertionError("no bridge call belongs in this test: %s" % verb)


def _load_server_main():
    """server/__main__.py under a name of its own.

    Importing it as `__main__` would collide with the module running the test
    suite, and the file has to be loaded rather than reimplemented: the read
    loop's exception handling is the thing under test.
    """
    path = os.path.join(SERVER_DIR, "__main__.py")
    spec = importlib.util.spec_from_file_location("ti_server_main", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


srv = _load_server_main()


class StdioTestCase(unittest.TestCase):
    """Captures what the server writes to stdout, as a client would read it."""

    def setUp(self):
        self.out = io.StringIO()
        patcher = mock.patch.object(sys, "stdout", self.out)
        patcher.start()
        self.addCleanup(patcher.stop)

    def messages(self):
        """Every line written, parsed. A line that is not JSON fails here,
        which is the same failure stdio_selfcheck.py exists to catch."""
        return [json.loads(l) for l in self.out.getvalue().splitlines()
                if l.strip()]

    def feed(self, *msgs):
        """Run the read loop over these messages, serialized as a client
        would send them."""
        stdin = io.StringIO("".join(json.dumps(m) + "\n" for m in msgs))
        with mock.patch.object(sys, "stdin", stdin):
            srv.main()


class MalformedEnvelopeTest(StdioTestCase):
    """A client message that is not the shape the server expects."""

    MALFORMED = [
        ("array envelope", []),
        ("string envelope", "ping"),
        ("null envelope", None),
        ("scalar params", {"jsonrpc": "2.0", "id": 1, "method": "initialize",
                           "params": 1}),
        ("array params", {"jsonrpc": "2.0", "id": 1, "method": "tools/list",
                          "params": []}),
        ("scalar _meta", {"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                          "params": {"name": "no_such_tool", "_meta": 1}}),
        ("array tool name", {"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                             "params": {"name": [], "arguments": {}}}),
        ("array tool arguments",
         {"jsonrpc": "2.0", "id": 1, "method": "tools/call",
          "params": {"name": "no_such_tool", "arguments": [1]}}),
        ("array resource uri",
         {"jsonrpc": "2.0", "id": 1, "method": "resources/read",
          "params": {"uri": []}}),
        ("missing method", {"jsonrpc": "2.0", "id": 1}),
    ]

    def test_a_malformed_message_never_ends_the_session(self):
        ping = {"jsonrpc": "2.0", "id": 99, "method": "ping"}
        for label, msg in self.MALFORMED:
            with self.subTest(label):
                self.out.seek(0)
                self.out.truncate()
                self.feed(msg, ping)
                ids = [m.get("id") for m in self.messages()]
                self.assertIn(99, ids,
                              "%s stopped the request behind it" % label)

    def test_an_addressable_malformed_request_is_answered(self):
        """A request carrying an id gets an answer, error or not. Silence
        would leave the client waiting for a response that never comes."""
        for label, msg in self.MALFORMED:
            if not isinstance(msg, dict) or msg.get("id") is None:
                continue
            with self.subTest(label):
                self.out.seek(0)
                self.out.truncate()
                self.feed(msg)
                answered = [m for m in self.messages() if m.get("id") == 1]
                self.assertEqual(len(answered), 1,
                                 "%s went unanswered" % label)

    def test_an_unparseable_line_is_skipped(self):
        stdin = io.StringIO('{bad\n{"jsonrpc": "2.0", "id": 5, '
                            '"method": "ping"}\n')
        with mock.patch.object(sys, "stdin", stdin):
            srv.main()
        self.assertEqual([m["id"] for m in self.messages()], [5])

    def test_a_handler_that_raises_answers_the_request_and_reads_on(self):
        """The loop's own guard, with the failure forced at the one place no
        type check can rule out: a bug inside a handler."""
        ping = {"jsonrpc": "2.0", "id": 7, "method": "ping"}
        with mock.patch.object(srv, "handle",
                               side_effect=[RuntimeError("boom"), None]):
            self.feed({"jsonrpc": "2.0", "id": 6, "method": "tools/list"},
                      ping)
        first = self.messages()[0]
        self.assertEqual(first["id"], 6)
        self.assertIn("RuntimeError", first["error"]["message"])


class OversizedResultTest(StdioTestCase):
    def test_oversized_result_answers_once_and_the_next_request_survives(self):
        handler = mock.Mock(return_value={"text": "x" * tools.TEXT_LIMIT})
        with mock.patch.dict(tools.BY_NAME, {"fake_tool": handler}), \
                mock.patch.object(compose, "bridge", OfflineBridge()):
            self.feed({"jsonrpc": "2.0", "id": 1, "method": "tools/call",
                       "params": {"name": "fake_tool", "arguments": {}}},
                      {"jsonrpc": "2.0", "id": 2, "method": "ping"})
        handler.assert_called_once()
        messages = self.messages()
        self.assertEqual([message["id"] for message in messages], [1, 2])
        result = messages[0]["result"]
        self.assertTrue(result["isError"])
        report = json.loads(result["content"][0]["text"])
        self.assertEqual(report["error"], "response_too_large")
        self.assertIn("result", messages[1])


class NotificationTest(StdioTestCase):
    """A message with no id is a notification and is never answered."""

    NOTIFICATIONS = [
        {"jsonrpc": "2.0", "method": "ping"},
        {"jsonrpc": "2.0", "method": "notifications/initialized"},
        {"jsonrpc": "2.0", "method": "notifications/cancelled",
         "params": {"requestId": 1}},
        {"jsonrpc": "2.0", "method": "tools/list"},
    ]

    def test_no_notification_gets_a_reply(self):
        for msg in self.NOTIFICATIONS:
            with self.subTest(msg["method"]):
                self.out.seek(0)
                self.out.truncate()
                self.feed(msg)
                self.assertEqual(self.messages(), [])

    def test_the_same_methods_as_requests_are_answered(self):
        """The control: the id is what decides, not the method name."""
        for msg in self.NOTIFICATIONS:
            with self.subTest(msg["method"]):
                self.out.seek(0)
                self.out.truncate()
                request = dict(msg, id=3)
                self.feed(request)
                self.assertEqual([m["id"] for m in self.messages()], [3])

    def test_a_null_id_is_never_written(self):
        self.feed(*self.NOTIFICATIONS)
        self.assertNotIn("null", self.out.getvalue())


class HandleCallTest(unittest.TestCase):
    """tools.handle_call takes its two arguments straight off the wire."""

    def test_a_non_string_tool_name_is_a_tool_error(self):
        for name in ([], {}, 1, None):
            with self.subTest(repr(name)):
                result = tools.handle_call(name, {})
                self.assertTrue(result["isError"])
                self.assertIn("unknown tool",
                              result["content"][0]["text"])

    def test_non_object_arguments_are_a_tool_error(self):
        for arguments in ([1], "x", 3):
            with self.subTest(repr(arguments)):
                result = tools.handle_call("observe", arguments)
                self.assertTrue(result["isError"])
                self.assertIn("must be a JSON object",
                              result["content"][0]["text"])

    def test_omitted_arguments_still_reach_the_handler(self):
        seen = {}

        def handler(args, progress=None):
            seen["args"] = args
            return {"ok": True}

        with mock.patch.dict(tools.BY_NAME, {"fake_tool": handler}), \
                mock.patch.dict(tools.READ_ONLY, {"fake_tool": True}), \
                mock.patch.object(compose, "bridge", OfflineBridge()):
            tools.handle_call("fake_tool", None)
        self.assertEqual(seen["args"], {})


if __name__ == "__main__":
    unittest.main()
