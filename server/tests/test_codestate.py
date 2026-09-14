"""Unit tests for the running-code-vs-disk check.

  python3 -m unittest discover -s server/tests

The defect these guard against is a check that always passes. Hashing a file
at check time and comparing it against that same file agrees with itself
whatever happened in between, and a freshness check that can only ever say
"current" is worse than none: it is the reassurance that ends the
investigation. So every case here that asserts a clean verdict has a twin
that changes a byte and demands the failure.

The real server directory is never touched. Each case points codestate at a
temporary tree and hands it fake modules, which is also the only way to test
a file being deleted underneath a loaded process.
"""
import json
import os
import shutil
import sys
import tempfile
import unittest
from unittest import mock

SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
# Arms the guard that fails any case which would dial a running game.
import _offline                                     # noqa: E402,F401
import bridge                                       # noqa: E402
import codestate                                    # noqa: E402
import compose                                      # noqa: E402
import tools                                        # noqa: E402


class _FakeModule(object):
    """A module object as _loaded_files sees one: a __file__ and nothing."""

    def __init__(self, path):
        self.__file__ = path


class Sandbox(unittest.TestCase):
    """A throwaway directory standing in for server/, and an empty record.

    setUp order matters: _RECORDED is replaced before any test can call
    note(), so no case can write into the record the running process keeps
    for its own modules.
    """

    def setUp(self):
        self.dir = tempfile.mkdtemp(prefix="ti-codestate-")
        self.addCleanup(shutil.rmtree, self.dir, True)
        for target, value in (("SERVER_DIR", self.dir), ("_RECORDED", {})):
            patch = mock.patch.object(codestate, target, value)
            patch.start()
            self.addCleanup(patch.stop)
        self.modules = {}
        patch = mock.patch.dict(sys.modules, self.modules)
        patch.start()
        self.addCleanup(patch.stop)

    def load(self, name, text="original\n"):
        """Write a file into the fake server dir and pretend it is imported."""
        path = os.path.join(self.dir, name)
        with open(path, "w") as f:
            f.write(text)
        sys.modules["ti_fake_" + name] = _FakeModule(path)
        return path

    def rewrite(self, path, text):
        with open(path, "w") as f:
            f.write(text)


class LoadedFilesTest(Sandbox):
    """Which files count as this server's own code."""

    def test_picks_up_a_py_file_in_the_server_directory(self):
        path = self.load("alpha.py")
        self.assertEqual(codestate._loaded_files(), [path])

    def test_ignores_a_module_in_a_subdirectory(self):
        # server/tests/ is where this file lives; the server never loads one.
        sub = os.path.join(self.dir, "tests")
        os.mkdir(sub)
        sys.modules["ti_fake_test"] = _FakeModule(os.path.join(sub, "t.py"))
        self.assertEqual(codestate._loaded_files(), [])

    def test_ignores_non_python_files(self):
        sys.modules["ti_fake_ext"] = _FakeModule(
            os.path.join(self.dir, "accel.so"))
        self.assertEqual(codestate._loaded_files(), [])

    def test_ignores_modules_with_no_file(self):
        sys.modules["ti_fake_builtin"] = _FakeModule(None)
        self.assertEqual(codestate._loaded_files(), [])


class NoteTest(Sandbox):
    """The record is written once, at load time, and never refreshed."""

    def test_note_records_the_bytes_on_disk(self):
        path = self.load("alpha.py")
        self.assertEqual(codestate.note(), [path])
        self.assertTrue(codestate._RECORDED[path])

    def test_a_second_note_does_not_refresh_an_existing_record(self):
        # The whole check rests on this: refresh the record at check time and
        # the comparison is the file against itself.
        path = self.load("alpha.py")
        codestate.note()
        first = codestate._RECORDED[path]
        self.rewrite(path, "edited\n")
        self.assertEqual(codestate.note(), [])
        self.assertEqual(codestate._RECORDED[path], first)

    def test_note_records_a_module_imported_later(self):
        self.load("alpha.py")
        codestate.note()
        beta = self.load("beta.py")
        self.assertEqual(codestate.note(), [beta])


class DriftTest(Sandbox):
    """What the comparison says once the disk moves."""

    def test_clean_when_nothing_changed(self):
        self.load("alpha.py")
        self.load("beta.py")
        codestate.note()
        self.assertEqual(codestate.drift(), [])
        self.assertIsNone(codestate.stale())

    def test_a_changed_file_is_named(self):
        path = self.load("alpha.py")
        self.load("beta.py")
        codestate.note()
        self.rewrite(path, "edited\n")
        self.assertEqual(codestate.drift(),
                         [(os.path.join("server", "alpha.py"),
                           "changed on disk")])

    def test_a_same_length_edit_is_caught(self):
        # Content hash, not size: an edit that keeps the byte count is the
        # ordinary case when a constant or a comparison operator moves.
        path = self.load("alpha.py", "x = 1\n")
        codestate.note()
        self.rewrite(path, "x = 2\n")
        self.assertTrue(codestate.drift())

    def test_an_edit_reverted_is_clean_again(self):
        # Timestamps would call this stale; the bytes the process holds are
        # the bytes on disk, so a reconnect would change nothing.
        path = self.load("alpha.py", "x = 1\n")
        codestate.note()
        self.rewrite(path, "x = 2\n")
        self.rewrite(path, "x = 1\n")
        self.assertEqual(codestate.drift(), [])

    def test_every_changed_file_is_named_not_just_the_first(self):
        one = self.load("alpha.py")
        two = self.load("beta.py")
        codestate.note()
        self.rewrite(one, "edited\n")
        self.rewrite(two, "edited\n")
        self.assertEqual([name for name, _why in codestate.drift()],
                         [os.path.join("server", "alpha.py"),
                          os.path.join("server", "beta.py")])

    def test_a_deleted_file_is_reported(self):
        path = self.load("alpha.py")
        codestate.note()
        os.remove(path)
        self.assertIn("gone", codestate.drift()[0][1])


class VerdictTest(Sandbox):
    """The sentences a caller reads."""

    def test_clean_verdict_says_current(self):
        self.load("alpha.py")
        codestate.note()
        line, failed = codestate.check()
        self.assertTrue(line.startswith("current -- 1 "))
        self.assertFalse(failed)

    def test_stale_verdict_leads_with_the_failure(self):
        path = self.load("alpha.py")
        codestate.note()
        self.rewrite(path, "edited\n")
        line, failed = codestate.check()
        self.assertTrue(line.startswith("STALE -- "))
        self.assertTrue(failed)
        self.assertEqual(line, codestate.stale())

    def test_stale_verdict_names_the_file(self):
        path = self.load("alpha.py")
        codestate.note()
        self.rewrite(path, "edited\n")
        self.assertIn("alpha.py", codestate.check()[0])

    def test_stale_verdict_says_reconnect_and_not_restart_the_game(self):
        # The instruction is the point: the two failures look identical from
        # the client, and relaunching the game fixes only the other one.
        path = self.load("alpha.py")
        codestate.note()
        self.rewrite(path, "edited\n")
        message = codestate.check()[0]
        self.assertIn("Reconnect this MCP server", message)
        self.assertIn("Relaunching the game does NOT do it", message)


class SelftestTest(Sandbox):
    """selftest reports the freshness verdict and fails on drift."""

    def setUp(self):
        Sandbox.setUp(self)
        # Never reach for the bridge from a unit test: the developer's game
        # may well be running, and selftest would drive it.
        patch = mock.patch.object(
            compose.bridge, "call",
            side_effect=bridge.BridgeError("no game in a unit test"))
        patch.start()
        self.addCleanup(patch.stop)

    def test_clean_run_reports_current_first_and_does_not_fail(self):
        self.load("alpha.py")
        codestate.note()
        report = compose.selftest({})
        self.assertEqual(list(report["offline"])[0], "serverCode")
        self.assertTrue(report["offline"]["serverCode"].startswith("current"))
        self.assertNotIn("_failed", report)

    def test_drift_is_reported_and_fails_the_call_with_the_game_down(self):
        # The offline half is what an agent runs after editing the server,
        # which is exactly when the game is most likely not up.
        path = self.load("alpha.py")
        codestate.note()
        self.rewrite(path, "edited\n")
        report = compose.selftest({})
        self.assertTrue(report["offline"]["serverCode"].startswith("STALE"))
        self.assertTrue(report["bridge"].startswith("down"))
        self.assertTrue(report["_failed"])


class ObserveTest(Sandbox):
    """observe carries the failure only when there is one."""

    def setUp(self):
        Sandbox.setUp(self)
        patch = mock.patch.object(compose, "_observe",
                                  return_value={"bridge": "up"})
        patch.start()
        self.addCleanup(patch.stop)

    def test_clean_observe_is_unchanged(self):
        # Checked key by key rather than as a whole dict: observe also carries
        # the pause-limit counters on every reply, and this case is about the
        # code-drift key alone.
        self.load("alpha.py")
        codestate.note()
        report = compose.observe({})
        self.assertNotIn("serverCode", report)
        self.assertEqual(report["bridge"], "up")

    def test_stale_observe_leads_with_the_failure(self):
        path = self.load("alpha.py")
        codestate.note()
        self.rewrite(path, "edited\n")
        report = compose.observe({})
        self.assertEqual(list(report)[0], "serverCode")
        self.assertTrue(report["serverCode"].startswith("STALE"))
        self.assertEqual(report["bridge"], "up")


class DispatchTest(Sandbox):
    """handle_call pins late imports and turns _failed into a tool error."""

    def setUp(self):
        Sandbox.setUp(self)
        # handle_call takes a stall reading and a campaign token before it
        # dispatches, both through compose.bridge. Left real, they read
        # whatever game is running on this machine: a campaign parked past the
        # pause limit answers every case below with the PAUSE LIMIT banner in
        # place of the payload the case built. The stand-in reports nothing to
        # measure, which is what the gate needs to let the call through.
        patch = mock.patch.object(compose, "bridge", _offline.DispatchBridge())
        patch.start()
        self.addCleanup(patch.stop)

    def call(self, payload):
        with mock.patch.dict(tools.BY_NAME,
                             {"fake": lambda args, progress=None: payload}):
            return tools.handle_call("fake", {})

    def test_dispatch_pins_a_module_imported_since_the_last_call(self):
        path = self.load("alpha.py")
        self.call({"ok": True})
        self.assertIn(path, codestate._RECORDED)

    def test_failed_payload_is_returned_marked_an_error_and_stripped(self):
        result = self.call({"offline": {"serverCode": "STALE -- ..."},
                            "_failed": True})
        self.assertTrue(result["isError"])
        self.assertEqual(json.loads(result["content"][0]["text"]),
                         {"offline": {"serverCode": "STALE -- ..."}})


if __name__ == "__main__":
    unittest.main()
