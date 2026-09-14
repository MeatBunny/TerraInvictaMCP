"""Unit tests for selftest's offline UMM-injection report.

  python3 -m unittest discover -s server/tests

Unity Mod Manager installs itself two ways. The Assembly method patches
UnityEngine.UIModule.dll and leaves a .original_ backup beside it; the
DoorstopProxy method touches nothing under Managed/ and drops winhttp.dll and
doorstop_config.ini in the game root instead. install.sh and install.ps1 both
read the pair, in that order, and selftest read only the first -- so a working
Doorstop install was reported as no mod loader at all, which sends a person to
repair an install that is already fine.

Order is the substance of the check, not a detail of it. Doorstop is read only
where the Assembly method left no backup: a leftover winhttp.dll from an
abandoned Doorstop install would otherwise mask a reverted Assembly install
and report a mod loader that does not load.

Nothing here needs a running game; the bridge is forced down so only the
offline half of the report runs.
"""
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
import compose                                      # noqa: E402


class UmmInjectionTest(unittest.TestCase):

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="ti-selftest-")
        self.addCleanup(shutil.rmtree, self.root, True)
        self.managed = os.path.join(self.root, "TerraInvicta_Data", "Managed")
        os.makedirs(self.managed)
        self.ui = os.path.join(self.managed, "UnityEngine.UIModule.dll")
        self._write(self.ui, "patched bytes")
        for name, value in (("GAME_ROOT", self.root),):
            patcher = mock.patch.object(bridge, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        # The report stops after the offline checks when the bridge is down,
        # which is the half these cases are about.
        patcher = mock.patch.object(
            bridge, "call", side_effect=bridge.BridgeError("no game"))
        patcher.start()
        self.addCleanup(patcher.stop)

    @staticmethod
    def _write(path, text):
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)

    def install_assembly(self, reverted=False):
        self._write(self.ui + ".original_",
                    "patched bytes" if reverted else "original bytes")

    def install_doorstop(self):
        self._write(os.path.join(self.root, "winhttp.dll"), "loader")
        self._write(os.path.join(self.root, "doorstop_config.ini"),
                    "[General]\nenabled=true\n")

    def injection(self):
        return compose.selftest({})["offline"]["ummInjection"]

    def test_a_doorstop_install_is_reported_as_present(self):
        self.install_doorstop()
        self.assertEqual(self.injection(), "patched (Doorstop)")

    def test_half_a_doorstop_install_is_not_an_install(self):
        """winhttp.dll alone loads nothing without its config."""
        self._write(os.path.join(self.root, "winhttp.dll"), "loader")
        self.assertIn("no UMM install found", self.injection())

    def test_the_assembly_method_still_reports_patched(self):
        self.install_assembly()
        self.assertEqual(self.injection(), "patched")

    def test_a_reverted_assembly_install_is_not_masked_by_doorstop(self):
        """The order that matters: a leftover Doorstop pair must not turn a
        game update's revert into a clean report."""
        self.install_assembly(reverted=True)
        self.install_doorstop()
        self.assertIn("REVERTED", self.injection())


if __name__ == "__main__":
    unittest.main()
