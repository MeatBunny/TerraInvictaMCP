"""Fixture tree, offline bridge and isolation protocol for modcheck's tests.

  python3 -m unittest discover -s server/tests

modcheck finds the game install through eight module globals derived from its
own __file__, and every one of them has to point into a temporary tree before
a test touches the module. An unpatched global does not raise: it reads the
developer's real install and the case passes for the wrong reason.
BASELINE_PATH is the worst, because _compute_baseline writes it and
swallows OSError, so an unpatched run rewrites the checked-out
server/modcheck_baseline.dat with fixture content and says nothing.

The corpus is a factory rather than a module constant. Several cases mutate
vanilla before running, and a shared structure would carry the mutation into
every case that followed it.

The fixture vanilla set is clean by construction: every reference resolves, no
tech cycle exists, every org is registered or granted, and no dataName repeats.
That is what makes a case's delta attributable to the mod it installed.
"""
import json
import os
import shutil
import sys
import tempfile
import unittest
from unittest import mock

# server/ is a plain directory rather than a package, so the module under test
# is imported the same way modcheck.py imports its own siblings.
SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
# Arms the guard that fails any case which would dial a running game.
import _offline                                     # noqa: E402,F401
import modcheck                                     # noqa: E402


# A distinct sentinel from None: add_mod(modinfo=None) means "the default
# ModInfo.json", add_mod(modinfo=OMIT) means "no ModInfo.json at all".
OMIT = object()


class OfflineBridge:
    """A bridge module stand-in whose every call fails the way a real bridge
    with no game behind it fails.

    The offline entry point is run(bridge), never check_*(None, ...): every
    check calls session.probe() unguarded and run() always builds a _Session,
    so a None bridge raises an AttributeError that run() swallows into an
    ERROR result. A test doing that passes without ever reaching the offline
    path. probe() memoizes its failure, so `calls` stays at one for a whole
    run, which is a one-line proof that nothing reached an online branch.
    """

    class BridgeError(Exception):
        pass

    def __init__(self):
        self.calls = []

    def send(self, req, timeout=None, port=None):
        self.calls.append(req)
        raise self.BridgeError("no bridge in the fixture tree")


# ---------------------------------------------------------------- the corpus

def vanilla_templates():
    """The synthetic vanilla template set: eight files, fresh on every call.

    Field names and shapes are trimmed from the shipped templates so a reader
    can check a case against the real thing. Each class earns its place:
    TITechTemplate is the only class with cycle detection and reachability,
    TIMetaTemplate is the only source of org registration and scenario
    declarations, TICouncilorAppearanceTemplate the only source of emptyGate,
    TISpaceShipTemplate the only class with nested references.
    """
    return {
        "TITechTemplate.json": [
            {"dataName": "Tech_Alpha", "friendlyName": "Alpha",
             "techCategory": "Materials", "researchCost": 1000,
             "prereqs": [], "effects": []},
            {"dataName": "Tech_Beta", "friendlyName": "Beta",
             "techCategory": "Materials", "researchCost": 2000,
             "prereqs": ["Tech_Alpha"], "effects": ["Effect_Boost"]},
            {"dataName": "Tech_Gamma", "friendlyName": "Gamma",
             "techCategory": "Energy", "researchCost": 3000,
             "prereqs": ["Tech_Alpha"], "effects": []},
            {"dataName": "Tech_Delta", "friendlyName": "Delta",
             "techCategory": "Energy", "researchCost": 4000,
             "prereqs": ["Tech_Beta", "Tech_Gamma"], "effects": []},
        ],
        # Org_Gamma is deliberately absent from every gate list: it is
        # reachable only through Project_Alpha's orgGranted, which is what
        # exercises the reach check's `granted` escape.
        "TIOrgTemplate.json": [
            {"dataName": "Org_Alpha", "friendlyName": "Alpha Group",
             "orgType": "Academic", "tier": 1, "costMoney": 10,
             "affinities": ["Cooperate"]},
            {"dataName": "Org_Beta", "friendlyName": "Beta Group",
             "orgType": "Criminal", "tier": 2, "costMoney": 20,
             "affinities": ["Exploit"]},
            {"dataName": "Org_Gamma", "friendlyName": "Gamma Group",
             "orgType": "Military", "tier": 3, "costMoney": 30,
             "affinities": ["Destroy"]},
            {"dataName": "Org_Scenario", "friendlyName": "Scenario Group",
             "orgType": "Academic", "tier": 1, "costMoney": 40,
             "scenarioTags": ["s2070"], "affinities": ["Cooperate"]},
        ],
        "TIMetaTemplate.json": [
            {"dataName": "MetaOrgs", "friendlyName": "Orgs",
             "templateType": "TIOrgTemplate",
             "templateNames": ["Org_Alpha", "Org_Beta"]},
            {"dataName": "MetaOrgs2070", "friendlyName": "Orgs 2070",
             "templateType": "TIOrgTemplate", "scenarioTags": ["s2070"],
             "templateNames": ["Org_Scenario"]},
            {"dataName": "Scenario_Base", "friendlyName": "Base Scenario",
             "templateType": "TIMetaTemplate", "isNewCampaignOption": True,
             "newCampaignOptionCategory": "Scenario", "scenarioTags": [],
             "templateNames": []},
            {"dataName": "Scenario_2070", "friendlyName": "2070 Scenario",
             "templateType": "TIMetaTemplate", "isNewCampaignOption": True,
             "newCampaignOptionCategory": "Scenario",
             "scenarioTags": ["s2070"], "scenarioLocalizationPostfix": "_2070",
             "templateNames": []},
        ],
        "TIProjectTemplate.json": [
            {"dataName": "Project_Alpha", "friendlyName": "Alpha Project",
             "techCategory": "Materials", "researchCost": 5000,
             "prereqs": ["Tech_Alpha"], "effects": ["Effect_Boost"],
             "orgGranted": "Org_Gamma"},
            {"dataName": "Project_Beta", "friendlyName": "Beta Project",
             "techCategory": "Energy", "researchCost": 6000,
             "prereqs": ["Project_Alpha", "Tech_Beta"],
             "altPrereq0": "Tech_Gamma"},
        ],
        # `mount` is the enum-typed field the enum-table cases drive.
        "TIGunTemplate.json": [
            {"dataName": "Gun_Coilgun", "friendlyName": "Coilgun",
             "mount": "OneHull", "crew": 2, "magazine": 500,
             "weightedBuildMaterials": {"metals": 0.9, "volatiles": 0.1}},
            {"dataName": "Gun_Railgun", "friendlyName": "Railgun",
             "mount": "TwoHull", "crew": 3, "magazine": 300,
             "weightedBuildMaterials": {"metals": 0.95, "volatiles": 0.05}},
        ],
        "TIEffectTemplate.json": [
            {"dataName": "Effect_Boost", "operation": "Additive", "value": 1,
             "effectTarget": "SourceFaction", "contexts": ["Research"]},
        ],
        # Vanilla localizes a General objective under the bare key and every
        # other objective per faction; mining profiles are localized for only
        # one of the two entries, so the class is exempt from the offline
        # displayName requirement.
        "TIObjectiveTemplate.json": [
            {"dataName": "Objective_Faction", "objectiveType": "Campaign",
             "starter": True,
             "factionDataNames": ["ResistCouncil", "EscapeCouncil"]},
            {"dataName": "Objective_Bare", "objectiveType": "General",
             "starter": False,
             "factionDataNames": ["ResistCouncil", "EscapeCouncil"]},
        ],
        "TIMiningProfileTemplate.json": [
            {"dataName": "Mine_Carbon", "friendlyName": "Carbon",
             "modifyBySize": True, "water_mean": 35},
            {"dataName": "Mine_Metal", "friendlyName": "Metal",
             "modifyBySize": True, "metals_mean": 60},
        ],
        "TICouncilorAppearanceTemplate.json": [
            {"dataName": "Appearance_One", "string": "M_EUR_01",
             "enable": True, "allowedGenders": ["Male", "Nonbinary"],
             "allowedAncestries": ["European"],
             "allowedJobNames": ["Spy", "Fixer"]},
        ],
        # No hullName/driveName/powerPlantName: those ref rules target classes
        # this corpus does not carry, and a dangling ref in vanilla would land
        # in the baseline and suppress the same finding from a mod.
        "TISpaceShipTemplate.json": [
            {"dataName": "Ship_Escort", "friendlyName": "Escort",
             "role": "Escort", "propellantTanks": 4,
             "moduleTemplateEntries": [{"moduleName": "Gun_Coilgun",
                                        "slot": 1},
                                       {"moduleName": "Gun_Railgun",
                                        "slot": 2}]},
        ],
    }


def _loc_lines(cls, names):
    """displayName and description lines for one class, in the key=value shape
    the game's en files use."""
    out = []
    for dn in names:
        out.append("%s.displayName.%s=%s" % (cls, dn, dn.replace("_", " ")))
        out.append("%s.description.%s=Fixture entry %s." % (cls, dn, dn))
    return "\n".join(out) + "\n"


def vanilla_localization():
    """The en files the fixture game ships.

    TICouncilorAppearanceTemplate and TISpaceShipTemplate have none, matching
    the real install, so _class_is_localized is exercised in both directions:
    a mod touching a ship gets no key check, a mod touching an org does.
    """
    return {
        "TITechTemplate.en": _loc_lines(
            "TITechTemplate", ["Tech_Alpha", "Tech_Beta", "Tech_Gamma",
                               "Tech_Delta"]),
        "TIOrgTemplate.en": _loc_lines(
            "TIOrgTemplate", ["Org_Alpha", "Org_Beta", "Org_Gamma",
                              "Org_Scenario"]),
        "TIMetaTemplate.en": _loc_lines(
            "TIMetaTemplate", ["MetaOrgs", "MetaOrgs2070", "Scenario_Base",
                               "Scenario_2070"]),
        "TIProjectTemplate.en": _loc_lines(
            "TIProjectTemplate", ["Project_Alpha", "Project_Beta"]),
        "TIGunTemplate.en": _loc_lines(
            "TIGunTemplate", ["Gun_Coilgun", "Gun_Railgun"]),
        "TIEffectTemplate.en": _loc_lines(
            "TIEffectTemplate", ["Effect_Boost"]),
        "TIObjectiveTemplate.en": _loc_lines(
            "TIObjectiveTemplate", ["Objective_Faction.ResistCouncil",
                                    "Objective_Faction.EscapeCouncil",
                                    "Objective_Bare"]),
        "TIMiningProfileTemplate.en": _loc_lines(
            "TIMiningProfileTemplate", ["Mine_Carbon"]),
    }


def enum_table_payload():
    """A query.enums response shaped the way the verb answers, small enough to
    read: one enum, one class field bound to it."""
    return {
        "gameVersion": "1.0.51",
        "capturedAt": "2026-01-01T00:00:00",
        "scope": "all",
        "enums": {"WeaponMount": {"OneHull": 0, "TwoHull": 1, "Nose": 2}},
        "classes": {"TIGunTemplate": {"mount": "WeaponMount"}},
    }


# ---------------------------------------------------------------- disk I/O

def write_json(path, obj):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, indent=1)


def write_raw(path, text):
    """For fixtures json.dump cannot produce: malformed JSON, comments, a BOM,
    a localization file."""
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


def build_tree(root, templates=None, localization=None):
    """Write a minimal game install under `root`."""
    tdir = os.path.join(root, "TerraInvicta_Data", "StreamingAssets",
                        "Templates")
    ldir = os.path.join(root, "TerraInvicta_Data", "StreamingAssets",
                        "Localization", "en")
    for d in (tdir, ldir,
              os.path.join(root, "Mods", "Enabled"),
              os.path.join(root, "Mods", "Disabled"),
              os.path.join(root, "state"),
              os.path.join(root, "Saves")):
        os.makedirs(d)
    for name, entries in sorted((templates or {}).items()):
        write_json(os.path.join(tdir, name), entries)
    for name, text in sorted((localization or {}).items()):
        write_raw(os.path.join(ldir, name), text)


# ---------------------------------------------------------------- base case

class ModcheckTestCase(unittest.TestCase):
    """A fixture install in a tmpdir, with every modcheck path global pointed
    at it and every module-level cache reset around the case."""

    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="ti-modcheck-")
        # Every modcheck global that resolves to a path in the game install.
        paths = {
            "ROOT": self.root,
            # _class_is_localized and _vanilla_loc_keys rebuild this path from
            # ROOT independently, so the two have to agree.
            "VANILLA_DIR": os.path.join(self.root, "TerraInvicta_Data",
                                        "StreamingAssets", "Templates"),
            "DLC_DIR": os.path.join(self.root, "DLC_Content"),
            "MODS_ENABLED": os.path.join(self.root, "Mods", "Enabled"),
            "MODS_DISABLED": os.path.join(self.root, "Mods", "Disabled"),
            "BASELINE_PATH": os.path.join(self.root, "state",
                                          "modcheck_baseline.dat"),
            "ENUM_CACHE_PATH": os.path.join(self.root, "state",
                                            "modcheck_enums.dat"),
            "SAVES_DIR": os.path.join(self.root, "Saves"),
        }
        for name, value in sorted(paths.items()):
            patcher = mock.patch.object(modcheck, name, value)
            patcher.start()
            self.addCleanup(patcher.stop)
        # No individual case can catch an unpatched ROOT: it reads the real
        # install, suppresses the finding the case is about, and passes.
        for name in paths:
            value = getattr(modcheck, name)
            self.assertTrue(value.startswith(self.root),
                            "%s escaped the fixture tree: %s" % (name, value))
        self._reset_caches()
        build_tree(self.root, vanilla_templates(), vanilla_localization())
        self.bridge = None

    def tearDown(self):
        self._reset_caches()
        shutil.rmtree(self.root, ignore_errors=True)

    @staticmethod
    def _reset_caches():
        # _VANILLA_CACHE is keyed by bare filename with no directory, so a
        # leftover entry answers the next case's TIOrgTemplate.json with the
        # previous case's parsed content.
        modcheck._VANILLA_CACHE.clear()
        modcheck._VAN_LOC_CACHE.clear()
        modcheck._DISPLAY_NAME_CACHE.clear()
        # A new instance, not a field reset: _cache_tried left at True turns a
        # case that ships an enum cache into a case with no table.
        modcheck.ENUMS = modcheck.EnumTable()

    # -------------------------------------------------------------- fixtures

    def write_vanilla(self, filename, entries):
        """Replace one vanilla template file. Call before running a check: the
        parsed result is cached by filename for the rest of the case."""
        write_json(os.path.join(modcheck.VANILLA_DIR, filename), entries)
        modcheck._VANILLA_CACHE.clear()

    def write_enum_cache(self, payload=None):
        write_json(modcheck.ENUM_CACHE_PATH,
                   enum_table_payload() if payload is None else payload)

    def add_mod(self, name, files=None, modinfo=None, loc=None, raw=None,
                nested=None, disabled=False):
        """One mod folder.

        files   {filename: [entries]}  template files, serialized here
        raw     {filename: text}       files written verbatim
        loc     {filename: text}       localization files
        nested  {relpath: text}        files in a subfolder of the mod
        modinfo dict merged over {"title": name}, or OMIT for no ModInfo.json
        """
        base = modcheck.MODS_DISABLED if disabled else modcheck.MODS_ENABLED
        folder = os.path.join(base, name)
        os.makedirs(folder)
        if modinfo is not OMIT:
            info = {"title": name}
            info.update(modinfo or {})
            write_json(os.path.join(folder, "ModInfo.json"), info)
        for filename, entries in sorted((files or {}).items()):
            write_json(os.path.join(folder, filename), entries)
        for filename, text in sorted((raw or {}).items()):
            write_raw(os.path.join(folder, filename), text)
        for filename, text in sorted((loc or {}).items()):
            write_raw(os.path.join(folder, filename), text)
        for relpath, text in sorted((nested or {}).items()):
            full = os.path.join(folder, *relpath.split("/"))
            if not os.path.isdir(os.path.dirname(full)):
                os.makedirs(os.path.dirname(full))
            write_raw(full, text)
        return folder

    # -------------------------------------------------------------- running

    def run_modcheck(self, mod=None, check="all", scenario=None,
                     verbose=False):
        self.bridge = OfflineBridge()
        return modcheck.run(self.bridge, mod=mod, check=check,
                            scenario=scenario, verbose=verbose)

    def assert_offline(self, result):
        """The precondition every end-to-end case shares.

        _status ranks FAIL > DEGRADED > WARN > PASS and an offline run always
        populates `degraded`, so PASS and WARN are both unreachable here and
        asserting either is a test bug. DEGRADED against FAIL is the only
        status signal offline; everything else is asserted on verdicts,
        warnings, lints and summary.problems.
        """
        self.assertIs(result["bridge"]["up"], False)
        self.assertEqual(len(self.bridge.calls), 1,
                         "an online path was reached: %r" % self.bridge.calls)
        for name, status in sorted(result["summary"]["perCheck"].items()):
            self.assertIn(status, ("DEGRADED", "FAIL"),
                          "%s came back %s offline" % (name, status))

    # ------------------------------------------------------------ accessors

    @staticmethod
    def field_values(findings, key):
        """The `key` of every finding that carries one, in order."""
        return [f[key] for f in findings if key in f]
