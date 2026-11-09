"""Offline tests for the console wrappers added around the state-forcing set.

  python3 -m unittest discover -s server/tests

Each wrapper is a fixed console line plus, for the selection-driven ones, a
select call the bridge answers with {id, type, name}. The fake bridge records
both, so a case asserts the exact line and the exact call order. nation_stat
also routes four stats to the DLL verb and reads the current value for the
two delta commands, and _run_console refuses a line whose argument contains
another command name.
"""
import os
import sys
import unittest
from unittest import mock


SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
import _offline                                     # noqa: E402,F401
import bridge                                       # noqa: E402
import compose                                      # noqa: E402
import tools                                        # noqa: E402


CLEAR_ASSET = ("select", {"slot": "asset"})
COUNCILOR = {"id": 7, "type": "TICouncilorState", "name": "John Thomas"}
NATION = {"id": 40, "type": "TINationState", "name": "France"}
REGION = {"id": 41, "type": "TIRegionState", "name": "Paris"}
ARMY = {"id": 50, "type": "TIArmyState", "name": "1st Army"}
FLEET = {"id": 60, "type": "TISpaceFleetState", "name": "Home Fleet"}
HAB = {"id": 70, "type": "TIHabState", "name": "LEO1"}
FACTIONS = [{"id": 1, "name": "The Resistance"}, {"id": 2, "name": "The Servants"}]
TEMPLATES = {(1, "template"): "ResistCouncil", (2, "template"): "Servants"}


class FakeBridge:
    """Records every verb call; answers select and query.state from a table.

    query.state values are keyed by path, or by (id, path) when the same path
    answers differently per state; a path with no entry raises the bridge's
    own VerbError, the way a step that fails to resolve does.
    """

    VerbError = bridge.VerbError     # compose reads it off the patched module

    def __init__(self, selected=None, values=None, factions=None):
        self.selected = selected
        self.values = values or {}
        self.factions = factions
        self.calls = []

    def call(self, verb, args=None, timeout=None):
        self.calls.append((verb, args))
        if verb == "select":
            if isinstance(self.selected, dict) and "type" not in self.selected:
                return self.selected.get(args.get("id"))     # per-id table
            return self.selected
        if verb == "query.factions":
            n = len(self.factions or [])
            return {"items": self.factions or [], "count": n,
                    "returned": n, "truncated": False}
        if verb == "query.state":
            key = (args["id"], args["path"])
            if key not in self.values and args["path"] not in self.values:
                raise bridge.VerbError("no member at %r" % (key,))
            value = self.values.get(key, self.values.get(args["path"]))
            return {"type": "System.Single", "value": value}
        if verb == "nation.set_stat":
            return {"applied": True}
        return ""


def run(handler, args, **kw):
    fake = FakeBridge(**kw)
    with mock.patch.object(compose, "bridge", fake):
        data = handler(args, None)
    return fake.calls, data


class SimpleLinesTest(unittest.TestCase):

    def test_trigger_event_forced_by_default(self):
        calls, data = run(compose.trigger_event, {"name": "OneHumanity"})
        self.assertEqual(calls, [("console", {"line": "triggerevent OneHumanity"})])
        self.assertEqual(data["sent"], "triggerevent OneHumanity")

    def test_trigger_event_not_forced(self):
        calls, _ = run(compose.trigger_event,
                       {"name": "event_DryHole", "force": False})
        self.assertEqual(calls,
                         [("console", {"line": "triggerevent event_DryHole, nf"})])

    def test_give_org(self):
        calls, _ = run(compose.give_org,
                       {"target": "John Thomas", "org": "Org_Goldman"})
        self.assertEqual(calls,
                         [("console", {"line": "giveorg John Thomas, Org_Goldman"})])

    def test_missing_arguments_are_refused_before_any_call(self):
        for handler, args in ((compose.trigger_event, {}),
                              (compose.give_org, {"org": "x"}),
                              (compose.councilor_xp, {"councilor": 7}),
                              (compose.councilor_trait,
                               {"action": "add", "councilor": 7}),
                              (compose.nation_stat, {"nation": 40, "stat": "gdp"})):
            with self.assertRaises(compose.ToolError):
                run(handler, args)


class SelectedCouncilorTest(unittest.TestCase):

    def test_xp_selects_then_sends(self):
        calls, data = run(compose.councilor_xp,
                          {"councilor": 7, "amount": 250}, selected=COUNCILOR)
        self.assertEqual(calls, [CLEAR_ASSET, ("select", {"id": 7}),
                                 ("console", {"line": "givexp 250"})])
        self.assertEqual(data["selected"], COUNCILOR)

    def test_xp_refuses_a_non_councilor(self):
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.councilor_xp, {"councilor": 40, "amount": 1},
                selected=NATION)
        self.assertIn("TINationState", str(cm.exception))

    def test_trait_add_and_remove(self):
        for action in ("add", "remove"):
            calls, _ = run(compose.councilor_trait,
                           {"action": action, "councilor": 7,
                            "trait": "Affluent"}, selected=COUNCILOR)
            self.assertEqual(calls, [CLEAR_ASSET, ("select", {"id": 7}),
                                     ("console",
                                      {"line": "%strait Affluent" % action})])

    def test_trait_unknown_action(self):
        with self.assertRaises(compose.ToolError):
            run(compose.councilor_trait,
                {"action": "toggle", "councilor": 7, "trait": "Affluent"})


class NationStatTest(unittest.TestCase):

    def test_four_stats_go_to_the_verb(self):
        for stat in ("cohesion", "democracy", "inequality", "education"):
            calls, data = run(compose.nation_stat,
                              {"nation": 40, "stat": stat, "value": 3})
            self.assertEqual(calls, [("nation.set_stat",
                                      {"nation": 40, "stat": stat, "value": 3})])
            self.assertEqual(data, {"applied": True})

    def test_three_absolute_console_stats_select_first(self):
        expected = {"miltech": "SetMiltech 5.5", "sustainability":
                    "SetSustainability 2", "nukes": "SetNuclearWeapons 12"}
        values = {"miltech": 5.5, "sustainability": 2, "nukes": 12.0}
        for stat, line in expected.items():
            calls, _ = run(compose.nation_stat,
                           {"nation": 40, "stat": stat, "value": values[stat]},
                           selected=NATION)
            before = [CLEAR_ASSET] if stat == "miltech" else []
            self.assertEqual(calls, before + [("select", {"id": 40}),
                                              ("console", {"line": line})],
                             stat)

    def test_unrest_is_a_delta_from_the_current_value(self):
        calls, _ = run(compose.nation_stat,
                       {"nation": 40, "stat": "unrest", "value": 2.5},
                       selected=NATION, values={"unrest": 4.0})
        self.assertEqual(calls, [
            ("query.state", {"id": 40, "path": "unrest"}),
            ("select", {"id": 40}),
            ("console", {"line": "ChangeUnrest -1.5"})])

    def test_gdp_is_a_delta_in_billions_by_name(self):
        calls, data = run(compose.nation_stat,
                          {"nation": 40, "stat": "gdp", "value": 3.0e12},
                          selected=NATION, values={"GDP": 2.5e12})
        self.assertEqual(calls, [
            ("select", {"id": 40}),
            ("query.state", {"id": 40, "path": "GDP"}),
            ("console", {"line": "ModifyGDP France, 500.0"})])
        self.assertEqual(data["selected"], NATION)

    def test_gdp_refuses_a_name_with_a_comma(self):
        odd = dict(NATION, name="Korea, Republic of")
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.nation_stat,
                {"nation": 40, "stat": "gdp", "value": 1e12}, selected=odd,
                values={"GDP": 1e12})
        self.assertIn("comma", str(cm.exception))

    def test_console_stats_refuse_a_non_nation(self):
        with self.assertRaises(compose.ToolError):
            run(compose.nation_stat,
                {"nation": 7, "stat": "miltech", "value": 3}, selected=COUNCILOR)

    def test_unknown_stat_lists_the_nine(self):
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.nation_stat,
                {"nation": 40, "stat": "population", "value": 1})
        text = str(cm.exception)
        for stat in ("cohesion", "democracy", "inequality", "education",
                     "miltech", "sustainability", "nukes", "unrest", "gdp"):
            self.assertIn(stat, text)


class NukeTest(unittest.TestCase):

    def test_arguments_pass_through_verbatim(self):
        args = {"region_name": "Paris", "faction": "ResistCouncil", "nation": 40}
        calls, _ = run(compose.nuke, args)
        self.assertEqual(calls, [("region.nuke", args)])
        calls, _ = run(compose.nuke, {"region": 3, "faction": 1})
        self.assertEqual(calls, [("region.nuke", {"region": 3, "faction": 1})])

    def test_refused_without_a_region_or_a_faction(self):
        for args in ({"faction": 1}, {"region": 3}, {"region_name": ""}):
            with self.assertRaises(compose.ToolError):
                run(compose.nuke, args)

    def test_the_verb_is_declared_so_selftest_covers_it(self):
        self.assertIn("region.nuke", tools.BRIDGE_VERBS_USED)


class NationEventTest(unittest.TestCase):

    def test_selection_actions_select_then_send(self):
        for action, line in (("regime_change", "RegimeChange"),
                             ("coup", "StartCoup"),
                             ("take_all_claims", "TakeAllClaims"),
                             ("add_sto", "AddSTO 1")):    # bare AddSTO is 0
            calls, _ = run(compose.nation_event,
                           {"action": action, "nation": 40}, selected=NATION)
            self.assertEqual(calls, [("select", {"id": 40}),
                                     ("console", {"line": line})], action)

    def test_revolution_clears_the_asset_slot_first(self):
        calls, _ = run(compose.nation_event,
                       {"action": "revolution", "nation": 40}, selected=NATION)
        self.assertEqual(calls, [CLEAR_ASSET, ("select", {"id": 40}),
                                 ("console", {"line": "Revolution"})])

    def test_add_sto_count(self):
        calls, _ = run(compose.nation_event,
                       {"action": "add_sto", "nation": 40, "count": 3},
                       selected=NATION)
        self.assertEqual(calls[-1], ("console", {"line": "AddSTO 3"}))
        with self.assertRaises(compose.ToolError):
            run(compose.nation_event,
                {"action": "add_sto", "nation": 40, "count": "two"},
                selected=NATION)

    def test_give_ip_forms(self):
        calls, _ = run(compose.nation_event,
                       {"action": "give_ip", "nation": 40, "amount": 50},
                       selected=NATION)
        self.assertEqual(calls, [("select", {"id": 40}),
                                 ("console", {"line": "GiveIPs 50"})])
        calls, _ = run(compose.nation_event,
                       {"action": "give_ip", "region": 41, "amount": 50,
                        "priority": "Welfare"}, selected=REGION)
        self.assertEqual(calls, [("select", {"id": 41}),
                                 ("console", {"line": "GiveIPs Welfare,50"})])

    def test_give_ip_refusals(self):
        for args in ({"action": "give_ip", "nation": 40},
                     {"action": "give_ip", "nation": 40, "region": 41,
                      "amount": 1},
                     {"action": "give_ip", "amount": 1},
                     {"action": "give_ip", "nation": 40, "amount": 1,
                      "priority": "Welfair"}):
            with self.assertRaises(compose.ToolError):
                run(compose.nation_event, args, selected=NATION)

    def test_transfer_region_selects_the_region(self):
        calls, _ = run(compose.nation_event,
                       {"action": "transfer_region", "region": 41,
                        "target": "Germany"}, selected=REGION)
        self.assertEqual(calls, [("select", {"id": 41}),
                                 ("console", {"line": "TransferRegion Germany"})])
        with self.assertRaises(compose.ToolError):
            run(compose.nation_event,
                {"action": "transfer_region", "nation": 40, "target": "Germany"},
                selected=NATION)

    def test_name_actions_take_no_selection(self):
        for args, line in (({"action": "secede", "target": "Kurdistan"},
                            "Secede Kurdistan"),
                           ({"action": "absorb", "first": "Germany",
                             "second": "Austria"}, "Absorb Germany,Austria"),
                           ({"action": "unify", "first": "France",
                             "second": "Germany"}, "Unify France,Germany"),
                           ({"action": "independence_day"}, "IndependenceDay")):
            calls, _ = run(compose.nation_event, args)
            self.assertEqual(calls, [("console", {"line": line})])
        with self.assertRaises(compose.ToolError):
            run(compose.nation_event, {"action": "secede", "target": "x",
                                       "nation": 40})

    def test_arguments_checked_both_ways(self):
        for args in ({"action": "coup"},
                     {"action": "coup", "nation": 40, "target": "x"},
                     {"action": "coup", "nation": 40, "count": 1},
                     {"action": "absorb", "first": "Germany"},
                     {"action": "absorb", "first": "Korea, Republic of",
                      "second": "Japan"},
                     {"action": "secede", "target": "Korea, Republic of"},
                     {"action": "transfer_region", "region": 41,
                      "target": "Korea, Republic of"},
                     {"action": "secede"},
                     {"action": "independence_day", "target": "x"},
                     {"action": "revolt", "nation": 40}):
            with self.assertRaises(compose.ToolError):
                run(compose.nation_event, args, selected=NATION)

    def test_a_non_nation_is_refused(self):
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.nation_event, {"action": "coup", "nation": 7},
                selected=COUNCILOR)
        self.assertIn("TICouncilorState", str(cm.exception))


class ArmyFleetTest(unittest.TestCase):

    def test_army_actions(self):
        calls, _ = run(compose.army_edit,
                       {"action": "set_health", "army": 50, "value": 0.25},
                       selected=ARMY)
        self.assertEqual(calls, [CLEAR_ASSET, ("select", {"id": 50}),
                                 ("console", {"line": "SetArmyHealth 0.25"})])
        calls, _ = run(compose.army_edit, {"action": "go_home", "army": 50},
                       selected=ARMY)
        self.assertEqual(calls, [CLEAR_ASSET, ("select", {"id": 50}),
                                 ("console", {"line": "ArmyGoHome"})])

    def test_army_refusals(self):
        for args in ({"action": "set_health", "army": 50},
                     {"action": "set_health", "army": 50, "value": 1.5},
                     {"action": "set_health", "army": 50, "value": "full"},
                     {"action": "go_home", "army": 50, "value": 1},
                     {"action": "go_home"},
                     {"action": "heal", "army": 50}):
            with self.assertRaises(compose.ToolError):
                run(compose.army_edit, args, selected=ARMY)
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.army_edit, {"action": "go_home", "army": 40},
                selected=NATION)
        self.assertIn("TINationState", str(cm.exception))

    def test_fleet_actions(self):
        calls, _ = run(compose.fleet_edit,
                       {"action": "set_dv", "fleet": 60, "value": 0.5},
                       selected=FLEET)
        self.assertEqual(calls, [CLEAR_ASSET, ("select", {"id": 60}),
                                 ("console", {"line": "SetDV 0.5"})])
        calls, _ = run(compose.fleet_edit,
                       {"action": "damage_ship", "fleet": 60, "value": 300},
                       selected=FLEET)
        self.assertEqual(calls, [CLEAR_ASSET, ("select", {"id": 60}),
                                 ("console", {"line": "DamageShip 300"})])
        calls, _ = run(compose.fleet_edit,
                       {"action": "add_officer", "ship": "Enterprise",
                        "officer": "Officer_Admiral"})
        self.assertEqual(calls, [("console",
                                  {"line": "AddOfficer Enterprise, Officer_Admiral"})])

    def test_fleet_refusals(self):
        for args in ({"action": "set_dv", "fleet": 60},
                     {"action": "set_dv", "fleet": 60, "value": 50},
                     {"action": "set_dv", "fleet": 60, "value": "half"},
                     {"action": "damage_ship", "fleet": 60, "value": "x"},
                     {"action": "damage_ship", "value": 1},
                     {"action": "add_officer", "ship": "Enterprise"},
                     {"action": "add_officer", "ship": "Lewis, Clark",
                      "officer": "Officer_Admiral"},
                     {"action": "add_officer", "ship": "E", "officer": "O",
                      "fleet": 60},
                     {"action": "refuel", "fleet": 60}):
            with self.assertRaises(compose.ToolError):
                run(compose.fleet_edit, args, selected=FLEET)
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.fleet_edit, {"action": "set_dv", "fleet": 50,
                                     "value": 1}, selected=ARMY)
        self.assertIn("TIArmyState", str(cm.exception))


class CouncilorMoveDetainTest(unittest.TestCase):

    def test_move_reads_the_name_then_selects_the_destination(self):
        for dest in (REGION, FLEET, HAB):
            calls, data = run(compose.councilor_move,
                              {"councilor": 7, "destination": dest["id"]},
                              selected={7: COUNCILOR, dest["id"]: dest},
                              values={"displayName": "John Thomas"})
            self.assertEqual(calls, [
                ("select", {"id": 7}),
                ("query.state", {"id": 7, "path": "displayName"}),
                CLEAR_ASSET, ("select", {"id": dest["id"]}),
                ("console", {"line": "teleport John Thomas"})])
            self.assertEqual(data["selected"], dest)

    def test_move_refuses_a_non_councilor_before_reading_anything(self):
        fake = FakeBridge(selected={40: NATION, 41: REGION},
                          values={"displayName": "France"})
        with mock.patch.object(compose, "bridge", fake):
            with self.assertRaises(compose.ToolError) as cm:
                compose.councilor_move({"councilor": 40, "destination": 41}, None)
        self.assertIn("TINationState", str(cm.exception))
        self.assertEqual(fake.calls, [("select", {"id": 40})])

    def test_move_refusals(self):
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.councilor_move, {"councilor": 7, "destination": 40},
                selected={7: COUNCILOR, 40: NATION},
                values={"displayName": "John Thomas"})
        self.assertIn("TINationState", str(cm.exception))
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.councilor_move, {"councilor": 7, "destination": 41},
                selected={7: COUNCILOR, 41: REGION},
                values={"displayName": "Thomas, John"})
        self.assertIn("comma", str(cm.exception))
        with self.assertRaises(compose.ToolError):
            run(compose.councilor_move, {"councilor": 7})

    def test_detain_fills_both_slots(self):
        # DetainCouncilor dereferences the asset slot with no null check, so
        # the councilor goes into the asset slot too, after the type check.
        calls, data = run(compose.councilor_detain, {"councilor": 7},
                          selected=COUNCILOR)
        self.assertEqual(calls, [("select", {"id": 7}),
                                 ("select", {"slot": "asset", "id": 7}),
                                 ("console", {"line": "detainme"})])
        self.assertEqual(data["selectedAsset"], COUNCILOR)
        calls, _ = run(compose.councilor_detain,
                       {"councilor": 7, "by_faction": "Servants"},
                       selected=COUNCILOR)
        self.assertEqual(calls[-1], ("console", {"line": "detainme Servants"}))

    def test_detain_refuses_a_non_councilor_before_touching_the_asset_slot(self):
        fake = FakeBridge(selected=NATION)
        with mock.patch.object(compose, "bridge", fake):
            with self.assertRaises(compose.ToolError):
                compose.councilor_detain({"councilor": 40}, None)
        self.assertEqual(fake.calls, [("select", {"id": 40})])
        with self.assertRaises(compose.ToolError):
            run(compose.councilor_detain, {})


class SmallToolsTest(unittest.TestCase):

    def test_exact_lines(self):
        for handler, args, line in (
                (compose.turn_faction, {"faction": "Servants"},
                 "turnfaction Servants"),
                (compose.set_player_faction, {"faction": "Servants"},
                 "setfaction Servants"),
                (compose.atmosphere, {"action": "aerosols", "ppm": 5},
                 "aerosols 5"),
                (compose.atmosphere, {"action": "co2", "ppm": -10},
                 "addco2 -10"),
                (compose.propaganda, {"faction": "ResistCouncil"},
                 "propaganda ResistCouncil"),
                (compose.propaganda, {"faction": "ResistCouncil", "strength": 2},
                 "propaganda ResistCouncil, 2"),
                (compose.propaganda, {"faction": "ResistCouncil",
                                      "nation": "France"},
                 "propaganda ResistCouncil, 1, France"),
                (compose.unlock_all_ship_parts, {}, "UnlockAllShipParts")):
            calls, _ = run(handler, args)
            self.assertEqual(calls, [("console", {"line": line})])

    def test_force_missions_notes_the_unreadable_state(self):
        calls, data = run(compose.force_missions, {})
        self.assertEqual(calls, [("console", {"line": "forcemissions"})])
        self.assertIn("readable", data["note"])

    def test_refusals(self):
        for handler, args in ((compose.turn_faction, {}),
                              (compose.set_player_faction, {}),
                              (compose.set_player_faction,
                               {"faction": "AlienCouncil"}),
                              (compose.atmosphere, {"action": "co2"}),
                              (compose.atmosphere, {"action": "methane",
                                                    "ppm": 1}),
                              (compose.atmosphere, {"action": "co2",
                                                    "ppm": "lots"}),
                              (compose.propaganda, {}),
                              (compose.propaganda, {"faction": "x",
                                                    "nation": "Korea, Republic of"})):
            with self.assertRaises(compose.ToolError):
                run(handler, args)


class GrantFactionKindsTest(unittest.TestCase):

    def test_all_techs(self):
        calls, _ = run(compose.grant, {"kind": "all_techs",
                                       "faction": "ResistCouncil"})
        self.assertEqual(calls, [("console", {"line": "givealltechs ResistCouncil"})])
        for args in ({"kind": "all_techs"},
                     {"kind": "all_techs", "faction": "x", "name": "y"},
                     {"kind": "slot3_project"}):
            with self.assertRaises(compose.ToolError):
                run(compose.grant, args)

    def test_slot3_reads_the_slot_before_sending(self):
        values = dict(TEMPLATES)
        values[(2, "currentProjectProgress[0].slot")] = 1
        values[(2, "currentProjectProgress[1].slot")] = 3
        values[(2, "currentProjectProgress[1].projectTemplateName")] = "Project_X"
        calls, data = run(compose.grant, {"kind": "slot3_project",
                                          "faction": "Servants"},
                          values=values, factions=FACTIONS)
        self.assertEqual(calls, [
            ("query.factions", None),
            ("query.state", {"id": 1, "path": "template"}),
            ("query.state", {"id": 2, "path": "template"}),
            ("query.state", {"id": 2, "path": "currentProjectProgress[0].slot"}),
            ("query.state", {"id": 2, "path": "currentProjectProgress[1].slot"}),
            ("query.state", {"id": 2,
                             "path": "currentProjectProgress[1].projectTemplateName"}),
            ("console", {"line": "completeproject Servants"})])
        self.assertEqual(data["project"], "Project_X")

    def test_slot3_empty_is_refused_even_with_force(self):
        values = dict(TEMPLATES)
        values[(2, "currentProjectProgress[0].slot")] = 1
        for force in (False, True):
            with self.assertRaises(compose.ToolError) as cm:
                run(compose.grant, {"kind": "slot3_project", "faction": "Servants",
                                    "force": force},
                    values=values, factions=FACTIONS)
            self.assertIn("throw", str(cm.exception))

    def test_slot3_unknown_faction_refused_unless_forced(self):
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.grant, {"kind": "slot3_project", "faction": "Nobody"},
                values=dict(TEMPLATES), factions=FACTIONS)
        self.assertIn("force=true", str(cm.exception))
        calls, _ = run(compose.grant, {"kind": "slot3_project", "faction": "Nobody",
                                       "force": True},
                       values=dict(TEMPLATES), factions=FACTIONS)
        self.assertEqual(calls[-1], ("console", {"line": "completeproject Nobody"}))


class GiveOrgNullRefTest(unittest.TestCase):

    def test_the_engine_null_reference_becomes_a_plain_refusal(self):
        fake = FakeBridge()

        def boom(verb, args=None, timeout=None):
            raise bridge.VerbError("System.NullReferenceException: Object reference")
        fake.call = boom
        with mock.patch.object(compose, "bridge", fake):
            with self.assertRaises(compose.ToolError) as cm:
                compose.give_org({"target": "John Thomas",
                                  "org": "CentralIntelligenceAgency"}, None)
        text = str(cm.exception)
        self.assertIn("nothing was given", text)
        self.assertIn("log_tail which=player", text)

    def test_other_bridge_errors_pass_through(self):
        fake = FakeBridge()

        def boom(verb, args=None, timeout=None):
            raise bridge.VerbError("no campaign loaded")
        fake.call = boom
        with mock.patch.object(compose, "bridge", fake):
            with self.assertRaises(bridge.VerbError):
                compose.give_org({"target": "a", "org": "b"}, None)


class GuardTest(unittest.TestCase):

    def send(self, line):
        fake = FakeBridge()
        with mock.patch.object(compose, "bridge", fake):
            compose._run_console(line)
        return fake.calls

    def test_a_name_registered_earlier_in_an_argument_is_refused(self):
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.grant, {"kind": "project",
                                "name": "Project_AdvancedProspectingSurveys"})
        text = str(cm.exception)
        self.assertIn("'prospect'", text)
        self.assertIn("'giveproject'", text)
        self.assertIn("console tool", text)
        with self.assertRaises(compose.ToolError) as cm:
            run(compose.grant, {"kind": "tech", "name": "NetworkedPropaganda"})
        self.assertIn("'propaganda'", str(cm.exception))

    def test_a_name_registered_later_is_harmless(self):
        calls, _ = run(compose.trigger_event, {"name": "event_OfficerInsights"})
        self.assertEqual(calls, [("console",
                                  {"line": "triggerevent event_OfficerInsights"})])
        calls, _ = run(compose.grant, {"kind": "project",
                                       "name": "Project_NationalResearchOversight"})
        self.assertEqual(len(calls), 1)

    def test_a_clean_name_passes(self):
        calls, _ = run(compose.trigger_event, {"name": "OneHumanity"})
        self.assertEqual(len(calls), 1)

    def test_the_console_tool_bypasses_the_guard(self):
        fake = FakeBridge()
        with mock.patch.object(compose, "bridge", fake):
            compose.console({"line": "giveproject Project_AdvancedProspectingSurveys"},
                            None)
        self.assertEqual(fake.calls, [
            ("console", {"line": "giveproject Project_AdvancedProspectingSurveys"})])

    def test_grant_keeps_its_shape(self):
        calls, data = run(compose.grant, {"kind": "tech", "name": "Tech_X"})
        self.assertEqual(calls, [("console", {"line": "givetech Tech_X"})])
        self.assertEqual(data, {"sent": "givetech Tech_X", "output": ""})
        calls, data = run(compose.give_resources, {})
        self.assertEqual(data, {"sent": "givemetonsofresources", "output": ""})

    def test_every_wrapper_line_passes_the_guard(self):
        for line in ("prospect Mars", "revealsites", "killstate",
                     "DeclareWar Canada", "GiveAllCPs France,Resistance",
                     "triggerevent x", "giveorg a, b", "SetNuclearWeapons 3",
                     "givemetonsofresources", "addresource Money,100,",
                     "givexp 250", "addtrait Affluent", "SetMiltech 5.5",
                     "ChangeUnrest -1.5", "ModifyGDP France, 500.0",
                     "Revolution", "RegimeChange", "StartCoup", "TakeAllClaims",
                     "AddSTO 1", "AddSTO 3", "GiveIPs Welfare,50", "GiveIPs 50",
                     "TransferRegion Germany", "Secede Kurdistan",
                     "Absorb Germany,Austria", "Unify France,Germany",
                     "IndependenceDay", "SetArmyHealth 0.25", "ArmyGoHome",
                     "SetDV 0.5", "DamageShip 300",
                     "AddOfficer Enterprise, Officer_Admiral",
                     "teleport John Thomas", "detainme", "detainme Servants",
                     "turnfaction Servants", "forcemissions",
                     "setfaction Servants", "aerosols 5", "addco2 -10",
                     "propaganda ResistCouncil, 1, France",
                     "UnlockAllShipParts", "givealltechs ResistCouncil",
                     "completeproject Servants"):
            self.assertEqual(self.send(line), [("console", {"line": line})])

    def test_the_tuple_holds_every_registered_name_once(self):
        self.assertEqual(len(compose.CONSOLE_NAMES), 95)
        self.assertEqual(len(set(compose.CONSOLE_NAMES)), 95)
        self.assertNotIn("drains", compose.CONSOLE_NAMES)
        self.assertIn("addco2", compose.CONSOLE_NAMES)


if __name__ == "__main__":
    unittest.main()
