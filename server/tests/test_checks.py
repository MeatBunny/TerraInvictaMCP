"""End-to-end tests for modcheck's five checks against a synthetic install.

  python3 -m unittest discover -s server/tests

Each case builds a fixture game tree, installs one or two mods into it, and
runs modcheck.run() with a bridge that refuses every call. The entry point is
run(bridge) rather than check_*(None, ...): every check calls session.probe()
unguarded, so a None bridge raises an AttributeError that run() swallows into
an ERROR result and the case passes without reaching the offline path.

Two properties of the offline path shape every assertion here. _status ranks
FAIL > DEGRADED > WARN > PASS and an offline run always populates `degraded`,
so PASS and WARN are unreachable and DEGRADED against FAIL is the only status
signal. And `verdicts` is not `summary.problems`: check_merge excludes OK and
DISABLED from the count, so the two are asserted separately.

Every case asserts something that depends on the mod having been collected,
usually summary.mods. Without that, a case whose point is that modcheck stays
silent passes just as well with no mod installed at all.

Cases pinning behavior that is wrong say so and name the line. A fix belongs
in its own change.
"""
import os
import unittest

# Arms the guard that fails any case which would dial a running game.
import _offline                                     # noqa: F401
import _fixtures
from _fixtures import OMIT, modcheck


def check(result, name):
    return result["checks"][name]


def kinds(findings):
    return [f.get("kind") for f in findings]


def lints(check_result):
    return [f.get("lint") for f in check_result["lints"]]


def verdicts(check_result):
    return [f.get("verdict") for f in check_result["verdicts"]]


NEW_ORG = {"dataName": "Org_New", "friendlyName": "New Group",
           "orgType": "Academic", "tier": 1}


class LocalizeBridge(_fixtures.OfflineBridge):
    """A DLL that serves query.localize: `found` keys resolve cleanly,
    `fell_back` keys resolve through a fallback (default: every other key),
    and the rest resolve nowhere."""

    def __init__(self, found=(), fell_back=None):
        super().__init__()
        self.found, self.fell_back = set(found), fell_back

    def row(self, key):
        hit = key in self.found
        fell = not hit and (self.fell_back is None or key in self.fell_back)
        return {"key": key, "found": hit or fell, "fellBack": fell}

    def send(self, req, timeout=None, port=None):
        self.calls.append(req)
        cmd, args = req["cmd"], req["args"]
        if cmd == "version":
            data = {"game": "unknown"}
        elif cmd == "verbs":
            data = {"verbs": [{"name": "query.localize"}]}
        elif cmd == "query.localize":
            data = {"results": [self.row(k) for k in args["keys"]]}
        else:
            return {"id": req["id"], "ok": False, "error": "unsupported"}
        return {"id": req["id"], "ok": True, "data": data}


class CleanBaselineTest(_fixtures.ModcheckTestCase):
    """E0 and E1. If the clean run is not clean, no other case's delta means
    anything, so every field of it is pinned."""

    def test_e0_a_mod_free_install_reports_nothing(self):
        result = self.run_modcheck()
        self.assert_offline(result)
        self.assertEqual(result["summary"], {
            "status": "DEGRADED", "problems": 0, "warnings": 0,
            "perCheck": {"merge": "DEGRADED", "refs": "DEGRADED",
                         "locale": "DEGRADED", "conflicts": "DEGRADED",
                         "reach": "DEGRADED"}})
        self.assertEqual(result["bridge"], {
            "up": False, "error": "no bridge in the fixture tree",
            "game": None, "servedVerbs": None})
        for name in modcheck.CHECK_NAMES:
            c = check(result, name)
            self.assertEqual([c["verdicts"], c["warnings"], c["lints"]],
                             [[], [], []], "%s is not clean" % name)
            self.assertEqual(c["status"], "DEGRADED", name)

        summary = check(result, "merge")["summary"]
        self.assertEqual(summary["mods"], [])
        self.assertEqual(summary["modCount"], 0)
        self.assertEqual(summary["fieldsComparedInEngine"], 0)
        self.assertEqual(summary["fieldsExaminedStatically"], 0)
        self.assertEqual(summary["problems"], 0)
        self.assertEqual(summary["warnings"], 0)
        self.assertEqual(summary["engine"], "static-only")
        self.assertEqual(summary["enumSource"], "none")
        self.assertNotIn("enumTable", summary)
        self.assertNotIn("note", summary)
        # One note for the unreachable bridge, one for the absent enum table.
        self.assertEqual(len(summary["degraded"]), 2)

        summary = check(result, "refs")["summary"]
        self.assertEqual(summary["source"], "disk")
        self.assertEqual(summary["gameVersion"], "unknown")
        self.assertEqual(summary["problems"], 0)
        self.assertIsNone(summary["scenario"])
        self.assertEqual(summary["techCount"], 4)
        self.assertEqual(summary["refRuleClasses"],
                         ["TIProjectTemplate", "TISpaceShipTemplate",
                          "TITechTemplate"])
        self.assertEqual(summary["baseline"]["suppressedVanillaFindings"], 0)

        summary = check(result, "locale")["summary"]
        self.assertEqual(summary["mods"], [])
        self.assertEqual(summary["keysChecked"], 0)
        self.assertEqual(summary["problems"], 0)
        self.assertIsNone(summary["scenarioPostfix"])
        self.assertEqual(summary["engine"], "disk-approximation")

        summary = check(result, "conflicts")["summary"]
        self.assertEqual(summary["modsExamined"], [])
        self.assertEqual(summary["contestedFields"], 0)
        self.assertEqual(summary["crossSourceCollisions"], 0)
        self.assertEqual(summary["vanillaDlcVariantsNotShown"], 0)
        self.assertEqual(summary["dupeSource"], "file-level")
        self.assertEqual(summary["declaredScenarioTags"], ["s2070"])

        summary = check(result, "reach")["summary"]
        self.assertEqual(summary["gateListsUsed"], {"MetaOrgs": 2,
                                                    "MetaOrgs2070": 1})
        self.assertEqual(summary["problems"], 0)
        self.assertEqual(len(summary["checksRun"]), 2)
        self.assertEqual(summary["baseline"]["suppressedVanillaFindings"], 0)

    def test_e1_a_correct_field_edit_produces_no_finding(self):
        # The false-positive guard the whole suite rests on: a mod that does
        # the ordinary thing correctly has to come back silent.
        self.add_mod("Edit", files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "researchCost": 9999}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        self.assertEqual(result["summary"]["problems"], 0)
        self.assertEqual(result["summary"]["warnings"], 0)
        merge = check(result, "merge")
        self.assertEqual(merge["summary"]["mods"], ["Edit"])
        self.assertEqual(merge["summary"]["fieldsExaminedStatically"], 1)
        for name in modcheck.CHECK_NAMES:
            c = check(result, name)
            self.assertEqual([c["verdicts"], c["warnings"], c["lints"]],
                             [[], [], []], "%s is not clean" % name)


class MergeSimulationTest(_fixtures.ModcheckTestCase):
    """E2 to E5 and E17: what the static half of the merge check sees in an
    array a mod writes."""

    def test_e2_a_field_already_holding_the_vanilla_value_is_a_noop(self):
        self.add_mod("Edit", files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "researchCost": 2000}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        merge = check(result, "merge")
        self.assertEqual(merge["status"], "DEGRADED")
        self.assertEqual(lints(merge), ["noop"])
        self.assertIn("researchCost", merge["lints"][0]["detail"])
        self.assertEqual(merge["verdicts"], [])

    def test_e3_an_empty_array_warns_and_lints_a_noop(self):
        # An empty source array contributes no indices, so the vanilla list
        # survives whole and the mod changed nothing.
        self.add_mod("Edit", files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "effects": []}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        merge = check(result, "merge")
        self.assertEqual(len(merge["warnings"]), 1)
        self.assertIn("index merge cannot clear",
                      merge["warnings"][0]["warning"])
        self.assertEqual(lints(merge), ["noop"])
        # Warnings never raise the status: DEGRADED outranks WARN offline, so
        # a mod that changed nothing it meant to change reads as clean.
        self.assertEqual(merge["status"], "DEGRADED")
        self.assertEqual(merge["summary"]["problems"], 0)

    def test_e4_array_of_objects_misalignment_warns_and_dangles(self):
        self.add_mod("Ship", files={"TISpaceShipTemplate.json": [
            {"dataName": "Ship_Escort",
             "moduleTemplateEntries": [{"moduleName": "Gun_Missing",
                                        "slot": 1}]}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        merge = check(result, "merge")
        self.assertEqual(len(merge["warnings"]), 1)
        self.assertIn('overwrites the vanilla element moduleName='
                      '"Gun_Coilgun"', merge["warnings"][0]["warning"])
        refs = check(result, "refs")
        self.assertEqual(refs["status"], "FAIL")
        self.assertEqual(kinds(refs["verdicts"]), ["danglingRef"])
        self.assertEqual(refs["verdicts"][0]["path"],
                         "moduleTemplateEntries[0].moduleName")

    def test_e5_array_of_strings_misalignment_is_reported_nowhere(self):
        # BUG, pinned as-is: the mod displaces Tech_Beta out of Tech_Delta's
        # prereqs and nothing says so. check_alignment returns at
        # modcheck.py:903 unless both elements are dicts, and the string
        # arrays (prereqs, templateNames, missionsGrantedNames) are exactly
        # where index misalignment happens. Compare test_e4, which is the
        # same defect in an array of objects and is reported twice.
        self.add_mod("Str", files={"TITechTemplate.json": [
            {"dataName": "Tech_Delta", "prereqs": ["Tech_Gamma"]}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        self.assertEqual(result["summary"]["problems"], 0)
        self.assertEqual(result["summary"]["warnings"], 0)
        merge = check(result, "merge")
        self.assertEqual(merge["summary"]["mods"], ["Str"])
        self.assertEqual(merge["summary"]["fieldsExaminedStatically"], 1)
        self.assertEqual(merge["warnings"], [])
        # The merge really did lose the element the report is silent about.
        self.assertEqual(
            modcheck.Universe().classes["TITechTemplate"]["Tech_Delta"]
            ["prereqs"], ["Tech_Gamma", "Tech_Gamma"])

    def test_e17a_an_empty_gate_under_index_merge_never_empties(self):
        self.add_mod("App", files={"TICouncilorAppearanceTemplate.json": [
            {"dataName": "Appearance_One", "allowedGenders": []}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        merge = check(result, "merge")
        self.assertEqual(len(merge["warnings"]), 1)
        self.assertIn("allowedGenders", merge["warnings"][0]["warning"])
        # The merge simulation and the reach check agree: the gate is not
        # empty, so no appearance became unrollable.
        reach = check(result, "reach")
        self.assertEqual(reach["verdicts"], [])
        self.assertEqual(reach["status"], "DEGRADED")

    def test_e17b_an_empty_gate_under_replace_mode_empties(self):
        self.add_mod("App",
                     modinfo={"TemplatesToReplaceArrays":
                              ["TICouncilorAppearanceTemplate.json"]},
                     files={"TICouncilorAppearanceTemplate.json": [
                         {"dataName": "Appearance_One",
                          "allowedGenders": []}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        merge = check(result, "merge")
        self.assertEqual(merge["warnings"], [])
        reach = check(result, "reach")
        self.assertEqual(reach["status"], "FAIL")
        self.assertEqual(kinds(reach["verdicts"]), ["emptyGate"])
        self.assertEqual(reach["verdicts"][0]["path"], "allowedGenders")


class ConflictTest(_fixtures.ModcheckTestCase):
    """E6 to E8: two mods writing the same field."""

    def two_mods(self, order_a, order_b, modinfo_a=None):
        info_a = {"LoadOrder": order_a}
        info_a.update(modinfo_a or {})
        self.add_mod("A", modinfo=info_a, files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "researchCost": 111}]})
        self.add_mod("B", modinfo={"LoadOrder": order_b},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "researchCost": 222}]})

    def test_e6_templates_to_replace_is_a_high_severity_conflict(self):
        self.two_mods(1, 2, {"TemplatesToReplace": ["TITechTemplate.json"]})
        result = self.run_modcheck()
        self.assert_offline(result)
        conflicts = check(result, "conflicts")
        self.assertEqual(conflicts["status"], "FAIL")
        self.assertEqual(verdicts(conflicts), ["MISMATCH"])
        item = conflicts["verdicts"][0]
        self.assertEqual(item["severity"], "high")
        self.assertEqual(item["mods"], ["A", "B"])
        self.assertIn("TemplatesToReplace", item["detail"])

    def test_e7_distinct_load_orders_are_a_warning_not_a_failure(self):
        self.two_mods(1, 2)
        result = self.run_modcheck()
        self.assert_offline(result)
        conflicts = check(result, "conflicts")
        self.assertEqual(conflicts["status"], "DEGRADED")
        self.assertEqual(conflicts["verdicts"], [])
        self.assertEqual(len(conflicts["warnings"]), 1)
        self.assertEqual(conflicts["warnings"][0]["severity"], "medium")
        self.assertIn("B wins by LoadOrder",
                      conflicts["warnings"][0]["warning"])
        self.assertEqual(conflicts["summary"]["contestedFields"], 1)

    def test_e8_a_load_order_tie_is_a_failure(self):
        # Pairs with e7 to isolate the branch: the fixtures differ only in B's
        # LoadOrder.
        self.two_mods(5, 5)
        result = self.run_modcheck()
        self.assert_offline(result)
        conflicts = check(result, "conflicts")
        self.assertEqual(conflicts["status"], "FAIL")
        self.assertEqual(verdicts(conflicts), ["MISMATCH"])
        self.assertEqual(conflicts["verdicts"][0]["severity"], "high")
        self.assertIn("the winner is undefined",
                      conflicts["verdicts"][0]["detail"])


class ReferenceGraphTest(_fixtures.ModcheckTestCase):
    """E11 to E13b: what a mod does to the reference graph."""

    def test_e11_a_dangling_prereq_also_makes_the_tech_unreachable(self):
        self.add_mod("Bad", files={"TITechTemplate.json": [
            {"dataName": "Tech_Delta",
             "prereqs": ["Tech_Beta", "Tech_Missing"]}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        refs = check(result, "refs")
        self.assertEqual(refs["status"], "FAIL")
        self.assertEqual(refs["summary"]["problems"], 2)
        self.assertEqual(kinds(refs["verdicts"]),
                         ["danglingRef", "unreachable"])
        self.assertEqual(refs["verdicts"][0]["path"], "prereqs[1]")
        self.assertEqual(refs["verdicts"][0]["value"], "Tech_Missing")
        self.assertEqual(refs["verdicts"][1]["dataName"], "Tech_Delta")

    def test_e12_two_techs_requiring_each_other_are_a_cycle(self):
        # The empty prereqs element is an index-merge stub hole and is never
        # a dangling reference: the cycle is the only finding besides the two
        # techs it poisons.
        self.add_mod("Cyc", files={"TITechTemplate.json": [
            {"dataName": "Tech_Gamma", "prereqs": ["Tech_Delta"]},
            {"dataName": "Tech_Delta", "prereqs": ["Tech_Gamma", ""]}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        refs = check(result, "refs")
        self.assertEqual(refs["status"], "FAIL")
        self.assertEqual(kinds(refs["verdicts"]),
                         ["cycle", "unreachable", "unreachable"])
        self.assertEqual(refs["verdicts"][0]["members"],
                         ["Tech_Delta", "Tech_Gamma"])
        self.assertEqual([v["dataName"] for v in refs["verdicts"][1:]],
                         ["Tech_Delta", "Tech_Gamma"])

    def test_e13_disabling_an_org_dangles_the_project_that_grants_it(self):
        self.add_mod("Killer", files={"TIOrgTemplate.json": [
            {"dataName": "Org_Gamma", "disable": True}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        merge = check(result, "merge")
        self.assertEqual(verdicts(merge), ["DISABLED"])
        # DISABLED is the data asking for it, so it is a verdict and not a
        # problem; the merge check stays clean while the game is broken.
        self.assertEqual(merge["summary"]["problems"], 0)
        self.assertEqual(merge["status"], "DEGRADED")
        refs = check(result, "refs")
        self.assertEqual(refs["status"], "FAIL")
        self.assertEqual(kinds(refs["verdicts"]), ["danglingRef"])
        self.assertEqual(refs["verdicts"][0]["dataName"], "Project_Alpha")

    def test_e13b_a_mod_scoped_run_hides_the_damage_it_caused(self):
        # BUG, pinned as-is: check_refs filters findings by the finding's own
        # (class, dataName) against uni.mod_touched at modcheck.py:2000-2003.
        # The mod never wrote Project_Alpha, so the reference it broke is
        # invisible to a mod-scoped run and the same install that fails the
        # unscoped run above reports clean here. check_reach filters the same
        # way at modcheck.py:2632-2635.
        self.add_mod("Killer", files={"TIOrgTemplate.json": [
            {"dataName": "Org_Gamma", "disable": True}]})
        result = self.run_modcheck(mod="Killer")
        self.assert_offline(result)
        self.assertEqual(result["summary"]["status"], "DEGRADED")
        self.assertEqual(result["summary"]["problems"], 0)
        self.assertEqual(check(result, "refs")["verdicts"], [])
        # The mod was examined: only the finding was filtered away.
        self.assertEqual(verdicts(check(result, "merge")), ["DISABLED"])


class ReachTest(_fixtures.ModcheckTestCase):
    """E14 to E16b and E32: org registration."""

    def test_e14_an_org_granted_by_a_project_needs_no_gate_list(self):
        self.add_mod("Grant", files={
            "TIOrgTemplate.json": [NEW_ORG],
            "TIProjectTemplate.json": [{"dataName": "Project_Beta",
                                        "orgGranted": "Org_New"}]},
            loc={"TIOrgTemplate.en":
                 "TIOrgTemplate.displayName.Org_New=New Group\n"})
        result = self.run_modcheck()
        self.assert_offline(result)
        self.assertEqual(result["summary"]["problems"], 0)
        self.assertEqual(check(result, "reach")["verdicts"], [])
        merge = check(result, "merge")
        self.assertEqual(merge["summary"]["mods"], ["Grant"])
        self.assertEqual(lints(merge), ["newEntry"])

    def test_e15_a_new_org_with_nothing_around_it_fails_twice(self):
        self.add_mod("Adder", files={"TIOrgTemplate.json": [NEW_ORG]})
        result = self.run_modcheck()
        self.assert_offline(result)
        self.assertEqual(result["summary"]["problems"], 2)
        locale = check(result, "locale")
        self.assertEqual(locale["status"], "FAIL")
        self.assertEqual(kinds(locale["verdicts"]), ["missingKey"])
        self.assertEqual(locale["verdicts"][0]["key"],
                         "TIOrgTemplate.displayName.Org_New")
        reach = check(result, "reach")
        self.assertEqual(reach["status"], "FAIL")
        self.assertEqual(kinds(reach["verdicts"]), ["orgUnregistered"])
        self.assertEqual(reach["verdicts"][0]["dataName"], "Org_New")
        self.assertEqual(lints(check(result, "merge")), ["newEntry"])

    def test_e16_the_same_org_registered_and_localized_is_clean(self):
        # The positive half of e15: registration is consulted rather than
        # assumed, and a mod that restates the gate list in full lands.
        self.add_mod("Adder", files={
            "TIOrgTemplate.json": [NEW_ORG],
            "TIMetaTemplate.json": [{"dataName": "MetaOrgs",
                                     "templateNames": ["Org_Alpha",
                                                       "Org_Beta",
                                                       "Org_New"]}]},
            loc={"TIOrgTemplate.en":
                 "TIOrgTemplate.displayName.Org_New=New Group\n"})
        result = self.run_modcheck()
        self.assert_offline(result)
        self.assertEqual(result["summary"]["problems"], 0)
        self.assertEqual(check(result, "reach")["verdicts"], [])
        self.assertEqual(check(result, "locale")["verdicts"], [])
        self.assertEqual(check(result, "reach")["summary"]["gateListsUsed"],
                         {"MetaOrgs": 3, "MetaOrgs2070": 1})
        self.assertEqual(lints(check(result, "merge")), ["newEntry"])

    def test_e16b_a_one_element_gate_list_displaces_a_vanilla_org(self):
        # The same mod as e16 with the list not restated: the index merge
        # lands Org_New on index 0 and Org_Alpha is registered nowhere.
        self.add_mod("Adder", files={
            "TIOrgTemplate.json": [NEW_ORG],
            "TIMetaTemplate.json": [{"dataName": "MetaOrgs",
                                     "templateNames": ["Org_New"]}]},
            loc={"TIOrgTemplate.en":
                 "TIOrgTemplate.displayName.Org_New=New Group\n"})
        result = self.run_modcheck()
        self.assert_offline(result)
        reach = check(result, "reach")
        self.assertEqual(reach["status"], "FAIL")
        self.assertEqual(kinds(reach["verdicts"]), ["orgUnregistered"])
        self.assertEqual(reach["verdicts"][0]["dataName"], "Org_Alpha")
        self.assertEqual(reach["summary"]["gateListsUsed"],
                         {"MetaOrgs": 2, "MetaOrgs2070": 1})

    def test_e32_baseline_suppression_does_not_swallow_the_mod_finding(self):
        # Vanilla itself carries an unregistered org, so the baseline holds
        # that finding and repeating it would be noise. The mod's identical
        # finding still has to come through.
        orgs = _fixtures.vanilla_templates()["TIOrgTemplate.json"]
        orgs.append({"dataName": "Org_Orphan", "friendlyName": "Orphan",
                     "orgType": "Academic", "tier": 1})
        self.write_vanilla("TIOrgTemplate.json", orgs)
        self.add_mod("Adder", files={"TIOrgTemplate.json": [NEW_ORG]})
        result = self.run_modcheck()
        self.assert_offline(result)
        reach = check(result, "reach")
        self.assertEqual(reach["status"], "FAIL")
        self.assertEqual([v["dataName"] for v in reach["verdicts"]],
                         ["Org_New"])
        self.assertEqual(reach["summary"]["baseline"]
                         ["suppressedVanillaFindings"], 1)


class LocaleTest(_fixtures.ModcheckTestCase):
    """E18 and E18b: keys with no entry behind them."""

    def test_e18_a_localization_only_mod_is_still_collected(self):
        self.add_mod("LocOnly", loc={"TIOrgTemplate.en":
                                     "TIOrgTemplate.displayName.Org_Alpha="
                                     "Renamed\n"
                                     "TIOrgTemplate.displayName."
                                     "Org_Nonexistent=Ghost\n"})
        result = self.run_modcheck()
        self.assert_offline(result)
        locale = check(result, "locale")
        self.assertEqual(locale["summary"]["mods"], ["LocOnly"])
        self.assertEqual(locale["status"], "FAIL")
        self.assertEqual(kinds(locale["verdicts"]), ["phantomKey"])
        self.assertEqual(locale["verdicts"][0]["key"],
                         "TIOrgTemplate.displayName.Org_Nonexistent")
        self.assertEqual(lints(locale), ["enOnly"])
        # collect_mods_disk keeps a mod that carries localization and nothing
        # else, so the merge check lists it too and finds no field to examine.
        merge = check(result, "merge")
        self.assertEqual(merge["summary"]["mods"], ["LocOnly"])
        self.assertEqual(merge["summary"]["fieldsExaminedStatically"], 0)
        self.assertEqual(merge["verdicts"], [])

    def test_e18b_a_scenario_postfix_is_stripped_before_the_lookup(self):
        # Both halves at once: the postfixed key for a real entry is
        # suppressed, and a postfixed key for an entry that does not exist is
        # still reported. The suppressor is what makes a scenario's own
        # localization legal, and what could hide a typo ending in _2070.
        self.add_mod("LocPost", loc={"TIOrgTemplate.en":
                                     "TIOrgTemplate.displayName."
                                     "Org_Alpha_2070=Alpha in 2070\n"
                                     "TIOrgTemplate.displayName."
                                     "Org_Bogus_2070=Ghost\n"})
        result = self.run_modcheck()
        self.assert_offline(result)
        locale = check(result, "locale")
        self.assertEqual([v["key"] for v in locale["verdicts"]],
                         ["TIOrgTemplate.displayName.Org_Bogus_2070"])

    def test_an_en_file_in_a_subfolder_is_read(self):
        # The game reads a mod folder recursively, and mods commonly keep
        # localization under Localization/en/. e15 is the same org with no
        # key anywhere: one missingKey.
        self.add_mod("Deep", files={"TIOrgTemplate.json": [NEW_ORG]},
                     nested={"Localization/en/TIOrgTemplate.en":
                             "TIOrgTemplate.displayName.Org_New=New Group\n"})
        result = self.run_modcheck()
        self.assert_offline(result)
        locale = check(result, "locale")
        self.assertEqual(locale["summary"]["mods"], ["Deep"])
        self.assertEqual(locale["verdicts"], [])
        self.assertEqual(lints(locale), ["enOnly"])

    OBJ_CAMPAIGN = {"dataName": "Objective_New", "objectiveType": "Campaign",
                    "factionDataNames": ["ResistCouncil", "EscapeCouncil"]}
    OBJ_GENERAL = {"dataName": "Objective_New", "objectiveType": "General"}
    OBJ_KEY = "TIObjectiveTemplate.displayName.Objective_New"

    def online_locale(self, bridge, scenario=None):
        self.bridge = bridge
        result = modcheck.check_locale(modcheck._Session(bridge), None,
                                       scenario, True)
        self.assertEqual(result["summary"]["engine"], "in-engine")
        return result

    def localize_calls(self):
        return [c["args"]["keys"] for c in self.bridge.calls
                if c["cmd"] == "query.localize"]

    def test_an_objective_missing_one_faction_key_names_it(self):
        # The engine reads a non-General objective per faction, so the
        # requirement is one key per faction in factionDataNames; only the
        # absent one is reported.
        self.add_mod("Obj", files={"TIObjectiveTemplate.json":
                                   [self.OBJ_CAMPAIGN]},
                     loc={"TIObjectiveTemplate.en":
                          self.OBJ_KEY + ".ResistCouncil=New\n"})
        result = self.run_modcheck()
        self.assert_offline(result)
        locale = check(result, "locale")
        self.assertEqual(kinds(locale["verdicts"]), ["missingKey"])
        self.assertEqual(locale["verdicts"][0]["key"],
                         self.OBJ_KEY + ".EscapeCouncil")

    def test_a_general_objective_with_a_bare_key_is_covered(self):
        self.add_mod("Obj", files={"TIObjectiveTemplate.json":
                                   [self.OBJ_GENERAL]},
                     loc={"TIObjectiveTemplate.en": self.OBJ_KEY + "=New\n"})
        result = self.run_modcheck()
        self.assert_offline(result)
        locale = check(result, "locale")
        self.assertEqual(locale["summary"]["mods"], ["Obj"])
        self.assertEqual(locale["verdicts"], [])

    def test_a_campaign_objective_under_the_bare_key_only_is_missing(self):
        # The engine never reads the bare key for a Campaign objective, so
        # every faction sees the raw key: this is the bug the check exists
        # to catch, offline and online.
        self.add_mod("Obj", files={"TIObjectiveTemplate.json":
                                   [self.OBJ_CAMPAIGN]},
                     loc={"TIObjectiveTemplate.en": self.OBJ_KEY + "=New\n"
                          "TIObjectiveTemplate.description.Objective_New="
                          "Desc\n"})
        offline = check(self.run_modcheck(check="locale"), "locale")
        faction_keys = [self.OBJ_KEY + ".ResistCouncil",
                        self.OBJ_KEY + ".EscapeCouncil"]
        self.assertEqual(kinds(offline["verdicts"]), ["missingKey"] * 2)
        self.assertEqual([v["key"] for v in offline["verdicts"]],
                         faction_keys)
        online = self.online_locale(LocalizeBridge(
            found=[self.OBJ_KEY, "TIObjectiveTemplate.description."
                                 "Objective_New"], fell_back=()))
        self.assertEqual(kinds(online["verdicts"]), ["missingKey"] * 2)
        self.assertEqual([v["key"] for v in online["verdicts"]],
                         faction_keys)
        self.assertEqual(kinds(online["warnings"]), ["missingKey"] * 2)

    def test_a_partly_localized_class_requires_no_key(self):
        # Vanilla ships a displayName for one of its two mining profiles, so
        # a new profile with no key is the vanilla norm, not a finding.
        self.add_mod("Mine", files={"TIMiningProfileTemplate.json": [
            {"dataName": "Mine_New", "friendlyName": "New",
             "modifyBySize": True}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        locale = check(result, "locale")
        self.assertEqual(locale["summary"]["mods"], ["Mine"])
        self.assertEqual(locale["verdicts"], [])

    def test_a_partly_localized_class_is_still_checked_online(self):
        # The vanilla-coverage exemption covers keys found nowhere only; a
        # key the engine had to fall back on is a MISMATCH in any class.
        self.add_mod("Mine", files={"TIMiningProfileTemplate.json": [
            {"dataName": "Mine_New", "friendlyName": "New",
             "modifyBySize": True}]})
        offline = check(self.run_modcheck(check="locale"), "locale")
        self.assertEqual(offline["summary"]["engine"], "disk-approximation")
        self.assertEqual(offline["verdicts"], [])
        self.bridge = LocalizeBridge()
        online = modcheck.check_locale(modcheck._Session(self.bridge),
                                       None, None, True)
        self.assertEqual(online["summary"]["engine"], "in-engine")
        self.assertEqual(kinds(online["verdicts"]), ["fellBack"])
        self.assertEqual(online["verdicts"][0]["key"],
                         "TIMiningProfileTemplate.displayName.Mine_New")

    def test_online_an_objective_asks_for_its_faction_keys(self):
        self.add_mod("Obj", files={"TIObjectiveTemplate.json":
                                   [self.OBJ_CAMPAIGN]})
        self.online_locale(LocalizeBridge())
        [keys] = self.localize_calls()
        self.assertEqual(sorted(keys), sorted(
            "TIObjectiveTemplate.%s.Objective_New.%s" % (field, faction)
            for field in ("displayName", "description")
            for faction in ("ResistCouncil", "EscapeCouncil")))

    def test_online_a_localized_campaign_objective_is_clean(self):
        self.add_mod("Obj", files={"TIObjectiveTemplate.json":
                                   [self.OBJ_CAMPAIGN]})
        online = self.online_locale(LocalizeBridge(found=[
            "TIObjectiveTemplate.%s.Objective_New.%s" % (field, faction)
            for field in ("displayName", "description")
            for faction in ("ResistCouncil", "EscapeCouncil")], fell_back=()))
        self.assertEqual(online["verdicts"], [])
        self.assertEqual(online["warnings"], [])
        self.assertEqual(online["summary"]["keysChecked"], 4)

    def test_online_a_general_objective_reads_the_bare_key(self):
        self.add_mod("Obj", files={"TIObjectiveTemplate.json":
                                   [self.OBJ_GENERAL]})
        online = self.online_locale(LocalizeBridge(found=[self.OBJ_KEY],
                                                   fell_back=()))
        [keys] = self.localize_calls()
        self.assertEqual(sorted(keys), [
            "TIObjectiveTemplate.description.Objective_New", self.OBJ_KEY])
        self.assertEqual(online["verdicts"], [])

    def test_online_one_absent_faction_key_is_one_finding(self):
        self.add_mod("Obj", files={"TIObjectiveTemplate.json":
                                   [self.OBJ_CAMPAIGN]})
        online = self.online_locale(LocalizeBridge(
            found=[self.OBJ_KEY + ".ResistCouncil"]))
        self.assertEqual(kinds(online["verdicts"]), ["fellBack"])
        self.assertEqual(online["verdicts"][0]["key"],
                         self.OBJ_KEY + ".EscapeCouncil")

    def test_online_a_description_found_nowhere_warns(self):
        self.add_mod("Org", files={"TIOrgTemplate.json": [NEW_ORG]})
        online = self.online_locale(LocalizeBridge(
            found=["TIOrgTemplate.displayName.Org_New"], fell_back=()))
        self.assertEqual(online["verdicts"], [])
        self.assertEqual(kinds(online["warnings"]), ["missingKey"])
        self.assertEqual(online["warnings"][0]["key"],
                         "TIOrgTemplate.description.Org_New")

    def test_online_a_key_found_nowhere_on_an_exempt_class_is_nothing(self):
        # Vanilla ships no key for half its mining profiles, so a new one
        # with none is the vanilla norm online as well as offline.
        self.add_mod("Mine", files={"TIMiningProfileTemplate.json": [
            {"dataName": "Mine_New", "friendlyName": "New",
             "modifyBySize": True}]})
        online = self.online_locale(LocalizeBridge(fell_back=()))
        self.assertEqual(online["verdicts"], [])
        self.assertEqual(online["warnings"], [])
        self.assertEqual(online["summary"]["keysChecked"], 2)

    def test_online_no_call_carries_more_than_200_keys(self):
        # One objective with 150 factions is 300 keys across two fields: it
        # has to split across calls, since the DLL refuses more than 200.
        factions = ["F%03d" % i for i in range(150)]
        self.add_mod("Wide", files={"TIObjectiveTemplate.json": [
            {"dataName": "Objective_Wide", "objectiveType": "Campaign",
             "factionDataNames": factions}]})
        online = self.online_locale(LocalizeBridge(found=[
            "TIObjectiveTemplate.%s.Objective_Wide.%s" % (field, faction)
            for field in ("displayName", "description")
            for faction in factions], fell_back=()))
        self.assertEqual(list(map(len, self.localize_calls())), [200, 100])
        self.assertEqual(online["verdicts"], [])
        self.assertEqual(online["summary"]["keysChecked"], 300)

    def test_vanilla_key_shapes_are_not_phantoms(self):
        # Objectives carry a faction suffix after the dataName and narrative
        # events are written field-last; both name an entry that exists.
        self.add_mod("Shapes", loc={"TIOrgTemplate.en":
                                    "TIOrgTemplate.displayName.Org_Alpha."
                                    "ResistCouncil=Alpha\n"
                                    "TIOrgTemplate.Org_Alpha.summary=Alpha\n"})
        result = self.run_modcheck()
        self.assert_offline(result)
        self.assertEqual(check(result, "locale")["verdicts"], [])


class ModFolderTest(_fixtures.ModcheckTestCase):
    """E20 to E25: what modcheck makes of the folder itself."""

    def test_e20a_a_missing_modinfo_is_a_warning(self):
        self.add_mod("NoInfo", modinfo=OMIT,
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "researchCost": 9}]})
        result = self.run_modcheck()
        self.assert_offline(result)
        merge = check(result, "merge")
        self.assertEqual(merge["summary"]["mods"], ["NoInfo"])
        self.assertEqual(len(merge["warnings"]), 1)
        self.assertIn("ModInfo.json unreadable",
                      merge["warnings"][0]["warning"])
        self.assertEqual(merge["status"], "DEGRADED")

    def test_e20b_a_title_that_is_not_the_folder_name_is_a_lint(self):
        self.add_mod("Mismatch", modinfo={"title": "Something Else"},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "researchCost": 9}]})
        merge = check(self.run_modcheck(), "merge")
        self.assertEqual(lints(merge), ["titleMismatch"])
        self.assertIn("Something Else", merge["lints"][0]["detail"])

    def test_e20c_malformed_modinfo_silently_reverts_merge_semantics(self):
        # The mod asks for concat and gets an index merge: read_mod_info
        # returns {} on a parse error and every ModInfo-driven setting reverts
        # to its default. The empty-array warning is the visible consequence;
        # the ModInfo warning beside it is the only thing naming the cause.
        self.add_mod("Broken", modinfo=OMIT,
                     raw={"ModInfo.json":
                          '{"title": "Broken", "TemplatesToConcatArrays": '
                          '["TITechTemplate.json",}'},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "effects": []}]})
        merge = check(self.run_modcheck(), "merge")
        self.assertEqual(len(merge["warnings"]), 2)
        self.assertIn("ModInfo.json is not valid JSON",
                      merge["warnings"][0]["warning"])
        self.assertIn("index merge cannot clear",
                      merge["warnings"][1]["warning"])

    def test_e20c_control_valid_modinfo_keeps_concat_semantics(self):
        self.add_mod("Working",
                     modinfo={"TemplatesToConcatArrays":
                              ["TITechTemplate.json"]},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "effects": []}]})
        merge = check(self.run_modcheck(), "merge")
        self.assertEqual(merge["summary"]["mods"], ["Working"])
        self.assertEqual(merge["warnings"], [])

    def test_e22_an_unmatched_mod_name_reports_clean_with_a_note(self):
        self.add_mod("Real", files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "researchCost": 9}]})
        result = self.run_modcheck(mod="Typo")
        self.assert_offline(result)
        self.assertEqual(result["summary"]["problems"], 0)
        merge = check(result, "merge")
        self.assertEqual(merge["summary"]["mods"], [])
        # The note is the only signal that the name matched nothing, and no
        # other check emits one.
        self.assertEqual(merge["summary"]["note"],
                         "'Typo' matched no enabled template mod")
        self.assertNotIn("note", check(result, "locale")["summary"])
        # The mod that is there is found when nothing scopes the run, so the
        # empty report above is the scoping and not an empty install.
        unscoped = check(self.run_modcheck(), "merge")
        self.assertEqual(unscoped["summary"]["mods"], ["Real"])

    def test_e23_a_json_file_in_a_subfolder_is_invisible(self):
        # BUG, pinned as-is: _mod_from_folder skips directories at
        # modcheck.py:1046, while the game's Mod Manager recurses. The exact
        # crash release.sh refuses to package is one modcheck cannot see. The
        # control below is the same file at the top level, which is caught.
        folder = self.add_mod("Nested",
                              nested={"data/TIWidgetTemplate.json":
                                      '[{"dataName": "Whatever"}]'})
        # The file is on disk where the game would parse it, and the run below
        # still reports an empty install.
        self.assertTrue(os.path.isfile(
            os.path.join(folder, "data", "TIWidgetTemplate.json")))
        result = self.run_modcheck()
        self.assert_offline(result)
        self.assertEqual(result["summary"]["problems"], 0)
        self.assertEqual(check(result, "merge")["summary"]["mods"], [])

    def test_e24_an_unparseable_file_is_excluded_whole(self):
        self.add_mod("Broken", raw={"TITechTemplate.json":
                                    '[{"dataName": "Tech_Beta",'})
        result = self.run_modcheck()
        self.assert_offline(result)
        merge = check(result, "merge")
        self.assertEqual(merge["status"], "FAIL")
        self.assertEqual(verdicts(merge), ["UNPARSED"])
        self.assertEqual(merge["summary"]["problems"], 1)
        refs = check(result, "refs")
        self.assertEqual(len(refs["warnings"]), 1)
        self.assertIn("excluded from the reference graph",
                      refs["warnings"][0]["warning"])

    def test_e25_a_json_name_matching_no_template_is_a_crash_risk(self):
        # The name governs the verdict, not the content: this file parses.
        self.add_mod("Stray", raw={"TIWidgetTemplate.json":
                                   '[{"dataName": "Whatever"}]'})
        result = self.run_modcheck()
        self.assert_offline(result)
        merge = check(result, "merge")
        self.assertEqual(merge["status"], "FAIL")
        self.assertEqual(verdicts(merge), ["CRASH_RISK"])
        self.assertEqual(merge["verdicts"][0]["file"],
                         "TIWidgetTemplate.json")


class EnumTableTest(_fixtures.ModcheckTestCase):
    """E27 and E28: the same defective mod, judged with and without the
    engine's enum table."""

    def bad_enum_mod(self):
        self.add_mod("Enum", files={"TIGunTemplate.json": [
            {"dataName": "Gun_Coilgun", "mount": "Muzzle"}]})

    def test_e27_a_cached_enum_table_catches_a_bad_member(self):
        self.write_enum_cache()
        self.bad_enum_mod()
        result = self.run_modcheck()
        self.assert_offline(result)
        refs = check(result, "refs")
        self.assertEqual(refs["status"], "FAIL")
        self.assertEqual(kinds(refs["verdicts"]), ["enumValue"])
        self.assertEqual(refs["verdicts"][0]["value"], "Muzzle")
        self.assertIn("not a member of enum WeaponMount",
                      refs["verdicts"][0]["detail"])
        self.assertEqual(refs["summary"]["enumSource"], "cache")
        # No globalgamemanagers in the fixture tree, so the version is
        # unknown; the baseline is keyed by the enum fingerprint as well.
        self.assertEqual(refs["summary"]["gameVersion"], "unknown")

    def test_e28_with_no_table_the_same_mod_reports_clean(self):
        self.bad_enum_mod()
        result = self.run_modcheck()
        self.assert_offline(result)
        refs = check(result, "refs")
        self.assertEqual(refs["status"], "DEGRADED")
        self.assertEqual(refs["verdicts"], [])
        self.assertEqual(refs["summary"]["enumSource"], "none")
        self.assertEqual(check(result, "merge")["summary"]["mods"], ["Enum"])


class ScenarioScopingTest(_fixtures.ModcheckTestCase):
    """E29: what a scenario name does to each check."""

    UNSCOPED = {"MetaOrgs": 2, "MetaOrgs2070": 1}

    def gate_lists(self, scenario):
        result = self.run_modcheck(check="reach", scenario=scenario)
        return check(result, "reach")["summary"]["gateListsUsed"]

    def test_e29_a_scenario_drops_entries_its_tags_do_not_cover(self):
        # Scenario_Base declares no tags, so the s2070-tagged gate list and
        # the org it registers are both out of the universe. The unscoped run
        # is asserted in the same case: without it the expected value is a
        # constant that a corpus change quietly turns into the wrong control.
        self.assertEqual(self.gate_lists(None), self.UNSCOPED)
        self.assertEqual(self.gate_lists("Scenario_Base"), {"MetaOrgs": 2})
        # And a scenario keeps the entries carrying its own tags.
        self.assertEqual(self.gate_lists("Scenario_2070"), self.UNSCOPED)

    def test_e29_an_unknown_scenario_is_checked_unscoped(self):
        # check_reach scopes only when the name is a known scenario, and says
        # nothing when it is not, so a typo silently checks the whole
        # universe. refs and locale warn about the same name.
        result = self.run_modcheck(scenario="Nope")
        self.assert_offline(result)
        reach = check(result, "reach")
        self.assertEqual(reach["summary"]["gateListsUsed"], self.UNSCOPED)
        self.assertEqual(reach["warnings"], [])
        for name in ("refs", "locale"):
            warnings = check(result, name)["warnings"]
            self.assertEqual(len(warnings), 1, "%s did not warn" % name)
            self.assertIn("unknown scenario 'Nope'", warnings[0]["warning"])

    def test_e29_the_scenario_postfix_reaches_the_locale_check(self):
        result = self.run_modcheck(check="locale", scenario="Scenario_2070")
        self.assertEqual(check(result, "locale")["summary"]["scenarioPostfix"],
                         "_2070")


class RunShapeTest(_fixtures.ModcheckTestCase):
    """E34 and E35: what run() returns, and when the baseline is written."""

    def test_e34_an_unknown_check_name_returns_a_different_shape(self):
        result = self.run_modcheck(check="bogus")
        # No bridge, no checks, and summary carries an error instead of a
        # status: a caller reading summary["status"] gets a KeyError.
        self.assertEqual(result["status"], "ERROR")
        self.assertNotIn("status", result["summary"])
        self.assertNotIn("checks", result)
        self.assertNotIn("bridge", result)
        self.assertEqual(result["summary"]["error"], "unknown check 'bogus'")
        self.assertEqual(result["summary"]["known"],
                         ["merge", "refs", "locale", "conflicts", "reach",
                          "all"])
        # It returns before building a session, so nothing was even tried.
        self.assertEqual(self.bridge.calls, [])

    def test_e35_one_run_computes_the_baseline_once(self):
        # refs runs before reach and writes the file both of them key off, so
        # only the first of the two reports a recompute. Selecting one check
        # changes which one that is.
        result = self.run_modcheck()
        self.assertIs(check(result, "refs")["summary"]["baseline"]
                      ["recomputed"], True)
        self.assertIs(check(result, "reach")["summary"]["baseline"]
                      ["recomputed"], False)

    def test_e35_reach_alone_computes_the_baseline_itself(self):
        self.assertFalse(os.path.exists(modcheck.BASELINE_PATH))
        result = self.run_modcheck(check="reach")
        self.assertIs(check(result, "reach")["summary"]["baseline"]
                      ["recomputed"], True)
        self.assertTrue(os.path.exists(modcheck.BASELINE_PATH))

    def test_e35_a_second_run_reuses_the_baseline_on_disk(self):
        self.run_modcheck()
        result = self.run_modcheck()
        self.assertIs(check(result, "refs")["summary"]["baseline"]
                      ["recomputed"], False)
        self.assertIs(check(result, "reach")["summary"]["baseline"]
                      ["recomputed"], False)


if __name__ == "__main__":
    unittest.main()
