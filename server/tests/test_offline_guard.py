"""The guard that keeps this suite away from a running game.

  python3 -m unittest discover -s server/tests

The defect it exists for is a case that passes for the wrong reason. A case
reaching tools.handle_call or compose.pause_gate without a fake bridge dials
127.0.0.1:17470, and with the game down that connection is refused instantly:
the call raises, the code takes the failure path, and the assertions hold. Run
the same case next to a running game and it reads that session instead -- a
campaign parked past the pause limit answers with the PAUSE LIMIT banner, a
live DLL answers a spawn verb with its own argument error -- so the suite
passes or fails on what is on a screen somewhere else. Four cases did exactly
that.

The guard turns the silent case into a loud one. These cases are what keeps it
armed: an unimported guard is no guard, and an ordinary exception would be
swallowed by the three `except Exception` blocks handle_call wraps around
dispatch.
"""
import os
import socket
import unittest
from unittest import mock

# _offline arms the guard and puts server/ on sys.path, so it comes first.
import _offline
from _offline import BridgeDialed
import bridge
import compose
import tools


TESTS_DIR = os.path.dirname(os.path.abspath(__file__))


class GuardTest(unittest.TestCase):

    def test_every_module_in_this_directory_arms_it(self):
        # Importing _offline is what arms the guard for a module run on its
        # own, where discovery never loads the rest of the suite. A new module
        # that omits it is a new hole, and this is the only thing that notices.
        missing = []
        for name in sorted(os.listdir(TESTS_DIR)):
            if not name.endswith(".py") or name == "_offline.py":
                continue
            with open(os.path.join(TESTS_DIR, name), encoding="utf-8") as f:
                if "import _offline" not in f.read():
                    missing.append(name)
        self.assertEqual(missing, [])

    def test_dialing_the_bridge_port_raises(self):
        with self.assertRaises(BridgeDialed):
            bridge.call("ping", timeout=1.0)

    def test_the_raise_survives_the_dispatcher(self):
        # BaseException is the whole point. handle_call answers any Exception
        # from a handler as a tool result, so a guard raising one would be
        # reported as a failed tool call and the case that dialed would go on
        # to pass or to fail somewhere unrelated.
        def dialer(args, progress=None):
            return bridge.call("query.time", timeout=1.0)

        with mock.patch.object(compose, "bridge", _offline.DispatchBridge()), \
                mock.patch.dict(tools.BY_NAME, {"query": dialer}):
            with self.assertRaises(BridgeDialed):
                tools.handle_call("query", {})

    def test_the_default_port_is_refused_under_a_port_override(self):
        # A case that hardcodes 17470 is still dialing a game on a machine
        # where TIBRIDGE_PORT is not set, so the compiled-in default is out of
        # bounds whatever the environment says.
        with mock.patch.dict(os.environ, {"TIBRIDGE_PORT": "17999"}):
            with self.assertRaises(BridgeDialed):
                socket.create_connection(("127.0.0.1", bridge.DEFAULT_PORT),
                                         timeout=1.0)
            with self.assertRaises(BridgeDialed):
                socket.create_connection(("127.0.0.1", 17999), timeout=1.0)


if __name__ == "__main__":
    unittest.main()
