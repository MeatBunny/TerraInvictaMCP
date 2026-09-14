"""The live overlay, and what it is allowed to write into.

  python3 -m unittest discover -s server/tests

With the game up, check_refs replaces the disk-simulated values of the
ref-checked fields with the engine's own merged values. The universe it writes
into held the vanilla cache's own entry dicts, so those writes landed in
_VANILLA_CACHE -- and the baseline, computed from a vanilla-only universe
built out of that same cache, then recorded the engine's merged values as
vanilla's. Everything downstream of that is wrong in the direction nobody
notices: a finding the mod caused is filed as pre-existing and suppressed, in
this run and in every later run, because the baseline is written to disk.

The baseline file is where the damage outlives the process, so a format marker
retires the ones already written.

Nothing here needs a running game: the bridge is a stand-in that answers
query.template with values the fixture's vanilla does not carry, which is
exactly the difference the overlay exists to apply.
"""
import json
import unittest

# Arms the guard that fails any case which would dial a running game.
import _offline                                     # noqa: F401
import _fixtures
from _fixtures import OfflineBridge, modcheck


class BulkBridge(OfflineBridge):
    """A DLL that serves query.template in bulk mode.

    Tech_Beta comes back pointing at an effect no template defines, which is a
    live-only dangling reference: the fixture's own vanilla is clean.
    """

    ENTRIES = {"TITechTemplate": [{"dataName": "Tech_Beta",
                                   "effects": ["Effect_Missing"]}]}

    def send(self, req, timeout=None, port=None):
        self.calls.append(req)
        cmd, args = req["cmd"], req["args"]
        if cmd == "version":
            data = {"game": "unknown"}
        elif cmd == "verbs":
            data = {"verbs": [{"name": "query.template"}]}
        elif cmd == "query.template":
            data = self.template(args)
        else:
            return {"id": req["id"], "ok": False, "error": "unsupported"}
        return {"id": req["id"], "ok": True, "data": data}

    def template(self, args):
        return {"entries": self.ENTRIES.get(args["type"], []),
                "truncated": False}


class OverlayTest(_fixtures.ModcheckTestCase):

    def vanilla_effects(self, dn="Tech_Beta"):
        """What the module-level vanilla cache holds -- the object the
        baseline is computed from."""
        entries, _err = modcheck.vanilla_entries("TITechTemplate.json")
        return entries[dn]["effects"]

    def run_refs(self, bridge):
        self.bridge = bridge
        return modcheck.check_refs(modcheck._Session(bridge), None, None,
                                   True)

    def test_the_overlay_leaves_the_vanilla_cache_alone(self):
        before = list(self.vanilla_effects())
        self.run_refs(BulkBridge())
        self.assertEqual(self.vanilla_effects(), before)

    def test_a_live_only_dangling_ref_is_reported_not_baselined(self):
        result = self.run_refs(BulkBridge())
        self.assertEqual(result["summary"]["problems"], 1)
        self.assertEqual(
            result["summary"]["baseline"]["suppressedVanillaFindings"], 0)
        self.assertEqual(self.field_values(result["verdicts"], "value"),
                         ["Effect_Missing"])

    def test_the_same_finding_survives_a_later_offline_run(self):
        """The failure this whole file is about was silent one run later: the
        baseline suppressed the finding for every process that read it."""
        self.run_refs(BulkBridge())
        self._reset_caches()
        self.add_mod("Broken", files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "effects": ["Effect_Missing"]}]})
        result = self.run_refs(OfflineBridge())
        self.assertEqual(result["summary"]["problems"], 1)
        self.assertEqual(
            result["summary"]["baseline"]["suppressedVanillaFindings"], 0)


class BaselineFormatTest(_fixtures.ModcheckTestCase):
    """A baseline file is reused only while its format is the current one."""

    def write_baseline(self, **fields):
        base = {"format": modcheck.BASELINE_FORMAT, "gameVersion": "unknown",
                "enumFingerprint": "none", "created": "2026-01-01T00:00:00",
                "refs": ["dangling|TITechTemplate|Tech_Beta|effects[0]|"
                         "Effect_Missing"],
                "reach": []}
        base.update(fields)
        with open(modcheck.BASELINE_PATH, "w") as f:
            json.dump(base, f)

    def recomputed(self):
        _base, fresh = modcheck._compute_baseline("unknown")
        return fresh

    def test_a_current_baseline_is_reused(self):
        self.write_baseline()
        self.assertIs(self.recomputed(), False)

    def test_a_baseline_from_an_older_format_is_recomputed(self):
        """Files written before the overlay stopped writing into the vanilla
        cache may carry engine-only findings, and nothing in them says so."""
        self.write_baseline(format=modcheck.BASELINE_FORMAT - 1)
        self.assertIs(self.recomputed(), True)

    def test_a_baseline_with_no_format_at_all_is_recomputed(self):
        self.write_baseline()
        with open(modcheck.BASELINE_PATH) as f:
            data = json.load(f)
        del data["format"]
        with open(modcheck.BASELINE_PATH, "w") as f:
            json.dump(data, f)
        self.assertIs(self.recomputed(), True)

    def test_the_recomputed_file_carries_the_marker(self):
        self.recomputed()
        with open(modcheck.BASELINE_PATH) as f:
            self.assertEqual(json.load(f)["format"], modcheck.BASELINE_FORMAT)


if __name__ == "__main__":
    unittest.main()
