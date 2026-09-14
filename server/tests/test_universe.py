"""Merge-semantics tests for modcheck's Universe.

  python3 -m unittest discover -s server/tests

Universe is the disk-side simulation of what the game holds after template
load: vanilla, then DLC, then every enabled mod merged field-wise by dataName
in LoadOrder. Every offline verdict is computed from it, so a merge rule
simulated wrongly here produces a confident wrong answer from all five checks
at once. These cases drive it through real files rather than through
merge_value, because the ModInfo-driven part of the rule (which array mode a
file gets, which file is replaced whole) lives in the reader, not the merge.
"""
import shutil
import unittest

# Arms the guard that fails any case which would dial a running game.
import _offline                                     # noqa: F401
import _fixtures
from _fixtures import modcheck


class UniverseTest(_fixtures.ModcheckTestCase):

    def universe(self, **kw):
        return modcheck.Universe(**kw)

    def tech(self, uni, dn="Tech_Beta"):
        return uni.classes["TITechTemplate"][dn]

    # ------------------------------------------------------------ field-wise

    def test_vanilla_only_universe_holds_the_shipped_entries(self):
        uni = self.universe(include_mods=False)
        self.assertEqual(sorted(uni.classes["TITechTemplate"]),
                         ["Tech_Alpha", "Tech_Beta", "Tech_Delta",
                          "Tech_Gamma"])
        self.assertEqual(uni.origin[("TITechTemplate", "Tech_Beta")],
                         "vanilla")
        self.assertEqual(uni.mod_touched, {})

    def test_a_mod_field_lands_and_the_rest_of_the_entry_survives(self):
        self.add_mod("Edit", files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "researchCost": 9999}]})
        entry = self.tech(self.universe())
        self.assertEqual(entry["researchCost"], 9999)
        self.assertEqual(entry["friendlyName"], "Beta")
        self.assertEqual(entry["prereqs"], ["Tech_Alpha"])

    def test_mods_off_leaves_the_vanilla_value(self):
        self.add_mod("Edit", files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "researchCost": 9999}]})
        self.assertEqual(self.tech(self.universe(include_mods=False))
                         ["researchCost"], 2000)

    def test_only_mod_excludes_every_other_mod(self):
        self.add_mod("A", files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "researchCost": 111}]})
        self.add_mod("B", files={"TITechTemplate.json": [
            {"dataName": "Tech_Gamma", "researchCost": 222}]})
        uni = self.universe(only_mod="A")
        self.assertEqual(self.tech(uni)["researchCost"], 111)
        self.assertEqual(self.tech(uni, "Tech_Gamma")["researchCost"], 3000)

    # ------------------------------------------------------------ load order

    def test_the_higher_load_order_wins(self):
        self.add_mod("Low", modinfo={"LoadOrder": 1},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "researchCost": 111}]})
        self.add_mod("High", modinfo={"LoadOrder": 2},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "researchCost": 222}]})
        self.assertEqual(self.tech(self.universe())["researchCost"], 222)

    def test_the_higher_load_order_wins_with_the_orders_swapped(self):
        # Asserted in both directions on purpose: a simulation that merged in
        # folder order rather than LoadOrder passes the first case alone,
        # because Low sorts before High alphabetically as well.
        self.add_mod("Low", modinfo={"LoadOrder": 2},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "researchCost": 111}]})
        self.add_mod("High", modinfo={"LoadOrder": 1},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "researchCost": 222}]})
        self.assertEqual(self.tech(self.universe())["researchCost"], 111)

    # ---------------------------------------------------------- array modes

    def test_arrays_merge_by_index_and_drop_what_they_displace(self):
        # Tech_Delta's vanilla prereqs are [Tech_Beta, Tech_Gamma]; a
        # one-element mod array lands on index 0 and Tech_Beta is gone.
        self.add_mod("Edit", files={"TITechTemplate.json": [
            {"dataName": "Tech_Delta", "prereqs": ["Tech_Alpha"]}]})
        self.assertEqual(self.tech(self.universe(), "Tech_Delta")["prereqs"],
                         ["Tech_Alpha", "Tech_Gamma"])

    def test_an_empty_array_cannot_clear_a_vanilla_list(self):
        self.add_mod("Edit", files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "effects": []}]})
        self.assertEqual(self.tech(self.universe())["effects"],
                         ["Effect_Boost"])

    def test_templates_to_concat_arrays_appends(self):
        self.add_mod("Edit",
                     modinfo={"TemplatesToConcatArrays":
                              ["TITechTemplate.json"]},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Delta",
                          "prereqs": ["Tech_Alpha"]}]})
        self.assertEqual(self.tech(self.universe(), "Tech_Delta")["prereqs"],
                         ["Tech_Beta", "Tech_Gamma", "Tech_Alpha"])

    def test_templates_to_replace_arrays_replaces_the_list(self):
        self.add_mod("Edit",
                     modinfo={"TemplatesToReplaceArrays":
                              ["TITechTemplate.json"]},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Delta",
                          "prereqs": ["Tech_Alpha"]}]})
        self.assertEqual(self.tech(self.universe(), "Tech_Delta")["prereqs"],
                         ["Tech_Alpha"])

    # -------------------------------------------------------- whole replace

    def test_templates_to_replace_wipes_every_vanilla_field(self):
        # The filename is listed in a different case than the file on disk:
        # name_list lowercases both sides, and a simulation comparing them
        # raw would silently fall back to a field-wise merge.
        self.add_mod("Owner",
                     modinfo={"TemplatesToReplace": ["TItechTEMPLATE.JSON"]},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "researchCost": 9999}]})
        entry = self.tech(self.universe())
        self.assertEqual(entry, {"dataName": "Tech_Beta",
                                 "researchCost": 9999})
        self.assertNotIn("prereqs", entry)

    # -------------------------------------------------------------- disable

    def test_a_disabled_entry_is_dropped_from_the_universe(self):
        self.add_mod("Killer", files={"TIOrgTemplate.json": [
            {"dataName": "Org_Beta", "disable": True}]})
        uni = self.universe()
        self.assertNotIn("Org_Beta", uni.classes["TIOrgTemplate"])
        self.assertIn("Org_Alpha", uni.classes["TIOrgTemplate"])

    def test_disable_false_leaves_the_entry_registered(self):
        self.add_mod("Killer", files={"TIOrgTemplate.json": [
            {"dataName": "Org_Beta", "disable": False}]})
        self.assertIn("Org_Beta", self.universe().classes["TIOrgTemplate"])

    # ------------------------------------------------------- new entries

    def test_a_new_entry_is_added_and_its_origin_is_the_mod(self):
        self.add_mod("Adder", files={"TIOrgTemplate.json": [
            {"dataName": "Org_New", "friendlyName": "New Group",
             "orgType": "Academic", "tier": 1}]})
        uni = self.universe()
        key = ("TIOrgTemplate", "Org_New")
        self.assertIn("Org_New", uni.classes["TIOrgTemplate"])
        self.assertEqual(uni.origin[key], "mod:Adder")
        self.assertEqual(uni.mod_touched[key], ["Adder"])

    def test_touching_a_vanilla_entry_leaves_the_origin_alone(self):
        self.add_mod("Edit", files={"TITechTemplate.json": [
            {"dataName": "Tech_Beta", "researchCost": 9999}]})
        uni = self.universe()
        key = ("TITechTemplate", "Tech_Beta")
        self.assertEqual(uni.origin[key], "vanilla")
        self.assertEqual(uni.mod_touched[key], ["Edit"])

    def test_mod_touched_lists_every_mod_in_load_order(self):
        self.add_mod("Second", modinfo={"LoadOrder": 9},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "researchCost": 2}]})
        self.add_mod("First", modinfo={"LoadOrder": 1},
                     files={"TITechTemplate.json": [
                         {"dataName": "Tech_Beta", "researchCost": 1}]})
        self.assertEqual(
            self.universe().mod_touched[("TITechTemplate", "Tech_Beta")],
            ["First", "Second"])

    # ------------------------------------------------------------ scenarios

    def test_scenario_declarations_come_from_the_metatemplates(self):
        uni = self.universe()
        self.assertEqual(sorted(uni.scenarios), ["Scenario_2070",
                                                 "Scenario_Base"])
        self.assertEqual(uni.declared_tags, {"s2070"})
        self.assertEqual(uni.scenarios["Scenario_2070"]["postfix"], "_2070")

    def test_scenario_tags_decide_what_is_live(self):
        self.assertTrue(uni_live(self.universe(), "Org_Alpha", ["s2070"]))
        self.assertTrue(uni_live(self.universe(), "Org_Scenario", ["s2070"]))
        self.assertFalse(uni_live(self.universe(), "Org_Scenario", []))

    def test_a_mod_can_retag_a_vanilla_entry(self):
        self.add_mod("Tagger", files={"TIOrgTemplate.json": [
            {"dataName": "Org_Alpha", "scenarioTags": ["s2070"]}]})
        uni = self.universe()
        self.assertEqual(uni.tags[("TIOrgTemplate", "Org_Alpha")], ["s2070"])

    # The tag index decides reachability, and it is a second copy of a value
    # the merged entry already holds. The two must not be able to disagree:
    # they did, in both directions, whenever the mod's own patch was read
    # instead of the merge it produced. A patch that leaves scenarioTags alone
    # left the index empty and leaked a scenario-only entry into every
    # scenario; a whole replacement that carries no tags left the index
    # holding vanilla's and hid a now-universal entry from the base game.

    def patched_scenario_org(self, modinfo, patch):
        """One mod patching the tagged org, installed and then removed so the
        next merge mode starts from the same vanilla tree."""
        folder = self.add_mod(
            "Tags", modinfo=modinfo,
            files={"TIOrgTemplate.json": [dict(patch,
                                               dataName="Org_Scenario")]})
        try:
            return self.universe()
        finally:
            shutil.rmtree(folder)

    def test_the_tag_index_matches_the_merged_entry(self):
        for label, modinfo, patch in [
                ("a merge that does not mention tags", {},
                 {"costMoney": 41}),
                ("a merge setting an empty tag list", {},
                 {"scenarioTags": []}),
                ("a merge setting tags to null", {},
                 {"scenarioTags": None}),
                ("a concatenated tag list",
                 {"TemplatesToConcatArrays": ["TIOrgTemplate.json"]},
                 {"scenarioTags": ["extra"]}),
                ("a whole replacement carrying no tags",
                 {"TemplatesToReplace": ["TIOrgTemplate.json"]},
                 {"costMoney": 41})]:
            with self.subTest(label):
                uni = self.patched_scenario_org(modinfo, patch)
                key = ("TIOrgTemplate", "Org_Scenario")
                merged = uni.classes[key[0]][key[1]]
                self.assertEqual(uni.tags[key],
                                 merged.get("scenarioTags") or [])

    def test_a_merge_that_ignores_tags_keeps_the_entry_scenario_scoped(self):
        self.add_mod("Editor", files={"TIOrgTemplate.json": [
            {"dataName": "Org_Scenario", "costMoney": 41}]})
        uni = self.universe()
        self.assertEqual(uni.tags[("TIOrgTemplate", "Org_Scenario")],
                         ["s2070"])
        self.assertFalse(uni_live(uni, "Org_Scenario", []))

    def test_a_whole_replacement_without_tags_untags_the_entry(self):
        self.add_mod("Owner",
                     modinfo={"TemplatesToReplace": ["TIOrgTemplate.json"]},
                     files={"TIOrgTemplate.json": [
                         {"dataName": "Org_Scenario", "costMoney": 41}]})
        uni = self.universe()
        self.assertEqual(uni.tags[("TIOrgTemplate", "Org_Scenario")], [])
        self.assertTrue(uni_live(uni, "Org_Scenario", []))

    # ------------------------------------------------------- unreadable files

    def test_an_unparseable_mod_file_is_recorded_and_excluded(self):
        self.add_mod("Broken", raw={"TITechTemplate.json":
                                    '[{"dataName": "Tech_Beta",'})
        uni = self.universe()
        self.assertEqual(len(uni.file_errors), 1)
        self.assertEqual(uni.file_errors[0]["source"], "mod:Broken")
        self.assertEqual(uni.file_errors[0]["file"], "TITechTemplate.json")
        # The file is excluded whole: the entry keeps its vanilla value.
        self.assertEqual(self.tech(uni)["researchCost"], 2000)


def uni_live(uni, org, scenario_tags):
    return uni.live_for_scenario("TIOrgTemplate", org, scenario_tags)


if __name__ == "__main__":
    unittest.main()
