"""The item shapes inside a tool's array and object arguments.

  python3 -m unittest discover -s server/tests

A tool argument declared `array` with no `items` promises nothing about what
goes in it, so a client can put anything there and find out from the bridge.
For design_create that costs a round trip and gets an answer about the verb
rather than about the field: the bridge takes an integer token for a slot or an
armor value and nothing else, refusing 2.5 and "3" alike, so a bare string in a
module list is refused either way. Declaring the item shape moves the refusal
to the client, before the call.

The cases here are the ones a mistake actually reaches: modules, nose_weapons
and hull_weapons are lists of {slot, name}, fire_modes is a list of
{slot, mode}, and armor carries three facings of {material, value}. Slots and
armor points are `integer` and not `number`, matching what the bridge accepts.

The last case is the general one. Any item schema published by any tool states
its required keys and closes itself to extras, because a half-stated item shape
is worse than none: it validates a call the bridge will refuse.

Nothing here needs a running game.
"""
import os
import sys
import unittest


SERVER_DIR = os.path.abspath(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
if SERVER_DIR not in sys.path:
    sys.path.insert(0, SERVER_DIR)
# Arms the guard that fails any case which would dial a running game.
import _offline                                     # noqa: E402,F401
import tools                                        # noqa: E402


ENTRY_LISTS = ("modules", "nose_weapons", "hull_weapons")


def published(name):
    for definition in tools.TOOL_DEFS:
        if definition["name"] == name:
            return definition["inputSchema"]
    raise AssertionError("no tool named %s" % name)


def walk(schema, path="design_create"):
    """Every object schema reachable from this one, with the path to it."""
    if not isinstance(schema, dict):
        return
    if schema.get("type") == "object" or "properties" in schema:
        yield path, schema
    for key, prop in (schema.get("properties") or {}).items():
        for found in walk(prop, "%s.%s" % (path, key)):
            yield found
    if "items" in schema:
        for found in walk(schema["items"], "%s[]" % path):
            yield found


class DesignCreateItemsTest(unittest.TestCase):

    def setUp(self):
        self.schema = published("design_create")

    def assert_item(self, key, other):
        """The list at `key` publishes a closed item schema of an integer
        `slot` and a string `other`, both required."""
        prop = self.schema["properties"][key]
        self.assertEqual("array", prop["type"], key)
        item = prop.get("items")
        self.assertIsNotNone(item, "%s publishes no item schema" % key)
        self.assertEqual("object", item["type"], key)
        self.assertEqual({"slot", other}, set(item["properties"]), key)
        self.assertEqual("integer", item["properties"]["slot"]["type"], key)
        self.assertEqual("string", item["properties"][other]["type"], key)
        self.assertEqual({"slot", other}, set(item["required"]), key)
        self.assertFalse(item["additionalProperties"], key)

    def test_each_entry_list_declares_its_item_shape(self):
        for key in ENTRY_LISTS:
            self.assert_item(key, "name")
        # A fire mode is keyed on the slot like a part entry, but its other
        # key is the FireMode name and not a template data name.
        self.assert_item("fire_modes", "mode")

    def test_armor_declares_three_facings_of_material_and_value(self):
        armor = self.schema["properties"]["armor"]
        self.assertEqual("object", armor["type"])
        self.assertEqual({"nose", "lateral", "tail"},
                         set(armor["properties"]))
        # ValidTemplate tests all three facings, so a missing one is not a
        # partial design, it is an invalid one.
        self.assertEqual({"nose", "lateral", "tail"}, set(armor["required"]))
        for facing, prop in sorted(armor["properties"].items()):
            self.assertEqual("object", prop["type"], facing)
            self.assertEqual({"material", "value"}, set(prop["properties"]),
                             facing)
            self.assertEqual("string",
                             prop["properties"]["material"]["type"], facing)
            self.assertEqual("integer",
                             prop["properties"]["value"]["type"], facing)
            self.assertEqual({"material", "value"}, set(prop["required"]),
                             facing)
            self.assertFalse(prop["additionalProperties"], facing)

    def test_armor_stays_required_and_the_lists_optional(self):
        self.assertIn("armor", self.schema["required"])
        for key in ENTRY_LISTS + ("fire_modes",):
            self.assertNotIn(key, self.schema["required"], key)

    def test_no_internal_marker_reaches_the_published_schema(self):
        for path, schema in walk(self.schema):
            self.assertNotIn("_req", schema, path)


class EveryItemSchemaTest(unittest.TestCase):

    def test_an_item_schema_is_closed_and_states_its_required_keys(self):
        for definition in tools.TOOL_DEFS:
            root = definition["inputSchema"]
            for path, schema in walk(root, definition["name"]):
                if not path.endswith("[]"):
                    continue
                self.assertIn("required", schema,
                              "%s states no required keys" % path)
                self.assertIn("additionalProperties", schema,
                              "%s accepts unknown keys" % path)
                self.assertFalse(schema["additionalProperties"], path)
                self.assertEqual(set(schema["required"]),
                                 set(schema["required"])
                                 & set(schema["properties"]),
                                 "%s requires a key it does not declare"
                                 % path)


if __name__ == "__main__":
    unittest.main()
