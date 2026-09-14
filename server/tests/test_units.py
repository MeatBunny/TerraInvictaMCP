"""Unit tests for modcheck's pure functions.

  python3 -m unittest discover -s server/tests

These are the pieces every verdict is computed from: the merge simulation of
one member, the lenient JSON reader, the template file parser, the walk that
compares a mod's value against the merged one, and the shadow attribution that
turns a MISMATCH into a SHADOWED. A defect here reads as a confident wrong
verdict rather than a crash, so nothing downstream catches it.

Several cases pin behavior that is wrong on purpose. Each says so and names
the line, because a test suite landing at the same time as a fix would make
both unreviewable.
"""
import os
import unittest
from unittest import mock

# Arms the guard that fails any case which would dial a running game.
import _offline                                     # noqa: F401
import _fixtures
from _fixtures import modcheck


class MergeValueTest(unittest.TestCase):
    """merge_value is the whole merge simulation for one member."""

    CASES = (
        # (base, patch, mode, want)
        # dicts merge field-wise, nested ones too
        ({"a": 1, "b": 2}, {"b": 3}, "merge", {"a": 1, "b": 3}),
        ({"a": {"x": 1, "y": 2}}, {"a": {"y": 9}}, "merge",
         {"a": {"x": 1, "y": 9}}),
        # arrays merge by index, element-wise for arrays of objects, and a
        # longer patch appends
        ([1, 2, 3], [9], "merge", [9, 2, 3]),
        ([{"n": "a", "v": 1}, {"n": "b"}], [{"v": 2}], "merge",
         [{"n": "a", "v": 2}, {"n": "b"}]),
        ([1], [9, 8], "merge", [9, 8]),
        # concat appends instead of overwriting; replace discards the base
        ([1, 2], [3], "concat", [1, 2, 3]),
        ([1, 2], [3], "replace", [3]),
        # The reason clearing a list needs TemplatesToReplaceArrays: an empty
        # source array contributes no indices, so an index merge is a no-op.
        ([1, 2], [], "merge", [1, 2]),
        ([1, 2], [], "concat", [1, 2]),
        ([1, 2], [], "replace", []),
        # MergeNullValueHandling is Ignore in the engine.
        (5, None, "merge", 5),
        ({"a": 1}, {"a": None}, "merge", {"a": 1}),
        # $type is a Newtonsoft deserializer instruction and never appears on
        # the merged entry, with or without a base.
        ({"a": 1}, {"$type": "X, Y", "b": 2}, "merge", {"a": 1, "b": 2}),
        (None, {"$type": "X, Y", "b": 2}, "merge", {"b": 2}),
        # scalars override
        (1, 2, "merge", 2),
        ("a", "b", "merge", "b"),
        # an array over a non-array base replaces it
        (None, [1], "merge", [1]),
        ("x", [1], "merge", [1]),
    )

    def test_merge_value(self):
        for base, patch, mode, want in self.CASES:
            with self.subTest(base=base, patch=patch, mode=mode):
                self.assertEqual(modcheck.merge_value(base, patch, mode),
                                 want)


class StripJsonCommentsTest(unittest.TestCase):
    """The lenient reader stands in for Newtonsoft, which accepts both comment
    forms. Whatever it does differently is a value modcheck compares against
    that the game never sees."""

    def test_double_slash_in_a_url_survives(self):
        text = '{"url": "https://example.com/x"}'
        self.assertEqual(modcheck.strip_json_comments(text), text)

    def test_line_comment_is_removed(self):
        self.assertEqual(
            modcheck.strip_json_comments('{"a": 1} // trailing note'),
            '{"a": 1} ')

    def test_escaped_quote_does_not_end_the_string(self):
        text = r'{"a": "he said \"hi\" // still inside the string"}'
        self.assertEqual(modcheck.strip_json_comments(text), text)

    def test_multi_line_block_comment_is_removed(self):
        text = '{\n/* a note\n   over lines */\n"a": 1}'
        self.assertEqual(modcheck.strip_json_comments(text), '{\n\n"a": 1}')

    def test_unterminated_block_comment_eats_the_rest(self):
        self.assertEqual(modcheck.strip_json_comments('{"a": 1} /* oops'),
                         '{"a": 1} ')

    def test_trailing_commas_are_removed(self):
        # Only the comma goes: the whitespace before the closing brace is a
        # capture group and is put back.
        self.assertEqual(modcheck.strip_json_comments('[1, 2, ]'), '[1, 2 ]')
        self.assertEqual(modcheck.strip_json_comments('{"a": 1,\n}'),
                         '{"a": 1\n}')

    def test_trailing_comma_removal_corrupts_string_literals(self):
        # BUG, pinned as-is: the trailing-comma re.sub at modcheck.py:207 runs
        # over the joined text including string literals, so a comma followed
        # by a brace inside a string is eaten. Newtonsoft does not do this, so
        # modcheck compares against a value the game never holds.
        self.assertEqual(modcheck.strip_json_comments('{"a": "x, }y"}'),
                         '{"a": "x }y"}')


class LoadEntriesTest(_fixtures.ModcheckTestCase):
    """load_entries turns one template file into {dataName: entry}, and its
    error string is what a mod file's UNPARSED verdict carries."""

    def _load(self, text):
        # The name ends in .json and the file lives in the fixture tmpdir, not
        # in the mod folder, so nothing in the repository gains a .json.
        path = os.path.join(self.root, "sample.json")
        _fixtures.write_raw(path, text)
        return modcheck.load_entries(path)

    def test_reads_entries_by_data_name(self):
        entries, err = self._load('[{"dataName": "A", "x": 1}]')
        self.assertIsNone(err)
        self.assertEqual(entries, {"A": {"dataName": "A", "x": 1}})

    def test_comments_and_trailing_commas_are_accepted(self):
        entries, err = self._load(
            '[\n// a note\n{"dataName": "A", "x": 1,},\n]')
        self.assertIsNone(err)
        self.assertEqual(entries, {"A": {"dataName": "A", "x": 1}})

    def test_non_array_top_level_is_an_error(self):
        entries, err = self._load('{"dataName": "A"}')
        self.assertIsNone(entries)
        self.assertEqual(err, "top level is dict, not an array")

    def test_non_object_element_is_an_error(self):
        entries, err = self._load('[1]')
        self.assertIsNone(entries)
        self.assertEqual(err, "element 0 is not an object")

    def test_missing_or_blank_data_name_is_an_error(self):
        for text in ('[{"x": 1}]', '[{"dataName": ""}]'):
            entries, err = self._load(text)
            self.assertIsNone(entries, text)
            self.assertEqual(err, "element 0 has no dataName", text)

    def test_malformed_json_reports_the_parser_message(self):
        entries, err = self._load('[{"dataName": "A"')
        self.assertIsNone(entries)
        self.assertIn("Expecting", err)

    def test_duplicate_data_name_silently_drops_the_first_copy(self):
        # BUG, pinned as-is: modcheck.py:230 assigns into a dict, so the
        # second entry wins and the first vanishes with no finding anywhere.
        # The game's own loader is the authority on what it does with the
        # duplicate; modcheck reports nothing either way.
        entries, err = self._load(
            '[{"dataName": "A", "x": 1}, {"dataName": "A", "x": 2}]')
        self.assertIsNone(err)
        self.assertEqual(entries, {"A": {"dataName": "A", "x": 2}})

    def test_missing_file_reports_an_error(self):
        entries, err = modcheck.load_entries(
            os.path.join(self.root, "absent.json"))
        self.assertIsNone(entries)
        self.assertIn("absent.json", err)


class CompareTest(unittest.TestCase):
    """Compare walks one mod entry against the merged one. Its problems list
    is what becomes a MISMATCH verdict."""

    def cmp(self, array_mode="merge", cls="TIGunTemplate"):
        return modcheck.Compare(modcheck.Ctx(array_mode, cls))

    def test_field_mismatch_becomes_a_problem(self):
        c = self.cmp()
        c.walk("crew", 4, 2, True)
        self.assertEqual(c.problems, ["crew: expected 4, game has 2"])

    def test_absent_member_is_a_note_not_a_problem(self):
        c = self.cmp()
        c.walk("noSuchField", 1, None, False)
        self.assertEqual(c.problems, [])
        self.assertEqual(len(c.notes), 1)
        self.assertIn("no such member", c.notes[0])

    def test_numbers_compare_across_types_and_strings(self):
        c = self.cmp()
        c.walk("crew", "5", 5.0, True)
        c.walk("mass", 1, 1.0000000001, True)
        self.assertEqual(c.problems, [])
        self.assertEqual(c.checked, 2)

    def test_booleans_compare_across_string_spelling(self):
        c = self.cmp()
        c.walk("enable", "true", True, True)
        c.walk("hide", "False", False, True)
        self.assertEqual(c.problems, [])

    def test_strings_compare_case_insensitively(self):
        c = self.cmp()
        c.walk("mount", "onehull", "OneHull", True)
        self.assertEqual(c.problems, [])

    def test_blank_placeholder_is_a_note_and_is_not_counted(self):
        c = self.cmp()
        c.walk("mount", "", "OneHull", True)
        self.assertEqual(c.problems, [])
        self.assertEqual(c.checked, 0)
        self.assertIn("blank placeholder", c.notes[0])

    def test_merge_mode_keeps_trailing_vanilla_elements_as_a_note(self):
        c = self.cmp()
        c.walk("list", ["a"], ["a", "b"], True)
        self.assertEqual(c.problems, [])
        self.assertEqual(c.warnings, [])
        self.assertIn("trailing vanilla element", c.notes[0])

    def test_merge_mode_leaves_the_empty_array_warning_to_the_static_pass(self):
        # The static pass in _merge_offline_mod raises the empty-array
        # warning against vanilla (test_checks e3); the in-engine walk only
        # notes the surviving elements, so the two never report it twice.
        c = self.cmp()
        c.walk("list", [], ["a", "b"], True)
        self.assertEqual(c.problems, [])
        self.assertEqual(c.warnings, [])
        self.assertIn("trailing vanilla element", c.notes[0])

    def test_concat_mode_accepts_an_element_anywhere_in_the_result(self):
        c = self.cmp("concat")
        c.walk("list", ["b"], ["a", "b"], True)
        self.assertEqual(c.problems, [])
        self.assertEqual(c.checked, 1)

    def test_concat_mode_reports_an_element_that_is_absent(self):
        c = self.cmp("concat")
        c.walk("list", ["z"], ["a", "b"], True)
        self.assertEqual(len(c.problems), 1)
        self.assertIn("not anywhere in the merged array", c.problems[0])

    def test_replace_mode_reports_a_length_difference(self):
        c = self.cmp("replace")
        c.walk("list", ["a"], ["a", "b"], True)
        self.assertEqual(len(c.problems), 1)
        self.assertIn("replace mode: mod has 1 element(s), game has 2",
                      c.problems[0])

    def test_element_past_the_end_of_the_merged_array_is_a_problem(self):
        c = self.cmp()
        c.walk("list", ["a", "b"], ["a"], True)
        self.assertEqual(len(c.problems), 1)
        self.assertIn("merged array stops at 1 element(s)", c.problems[0])

    def test_object_against_null_is_a_problem(self):
        c = self.cmp()
        c.walk("armor", {"materialName": "X"}, None, True)
        self.assertEqual(c.problems, ["armor: expected an object, game has "
                                      "null"])

    def test_blank_object_against_null_is_a_note(self):
        c = self.cmp()
        c.walk("armor", {"materialName": ""}, None, True)
        self.assertEqual(c.problems, [])
        self.assertIn("blank placeholder object", c.notes[0])

    def test_alignment_warns_when_an_element_displaces_a_vanilla_name(self):
        c = self.cmp()
        mod_list = [{"moduleName": "Gun_Railgun"}]
        c.check_alignment("modules[0]", mod_list[0],
                          {"moduleName": "Gun_Coilgun"}, mod_list)
        self.assertEqual(len(c.warnings), 1)
        self.assertIn("overwrites the vanilla element", c.warnings[0])

    def test_alignment_is_silent_when_the_name_is_restated_elsewhere(self):
        c = self.cmp()
        mod_list = [{"moduleName": "Gun_Railgun"},
                    {"moduleName": "Gun_Coilgun"}]
        c.check_alignment("modules[0]", mod_list[0],
                          {"moduleName": "Gun_Coilgun"}, mod_list)
        self.assertEqual(c.warnings, [])


class EnumScopingTest(unittest.TestCase):
    """A member name shared by two enums resolves differently depending on
    whether the walk knows which field it is looking at."""

    def setUp(self):
        table = modcheck.EnumTable()
        table._adopt({
            "enums": {"WeaponMount": {"OneHull": 0, "TwoHull": 1, "Nose": 2},
                      "MountPoint": {"Nose": 7, "Tail": 8}},
            "classes": {"TIGunTemplate": {"mount": "WeaponMount"}},
        }, "cache")
        patcher = mock.patch.object(modcheck, "ENUMS", table)
        patcher.start()
        self.addCleanup(patcher.stop)

    def cmp(self):
        return modcheck.Compare(modcheck.Ctx("merge", "TIGunTemplate"))

    def test_top_level_field_scopes_the_name_to_its_own_enum(self):
        c = self.cmp()
        self.assertTrue(c.scalars_equal("Nose", 2, "mount"))
        self.assertFalse(c.scalars_equal("Nose", 7, "mount"))

    def test_a_nested_path_falls_back_to_every_enum_declaring_the_name(self):
        # walk_dict and walk_list build paths with dots and brackets, and the
        # engine's class table has no entry for those, so the scoped lookup is
        # unavailable and any enum declaring the name matches.
        c = self.cmp()
        self.assertTrue(c.scalars_equal("Nose", 7, "slot.mount"))
        self.assertTrue(c.scalars_equal("Nose", 7, "slots[0]"))

    def test_empty_string_matches_the_zero_member(self):
        c = self.cmp()
        self.assertTrue(c.scalars_equal("", "OneHull", "mount"))
        self.assertFalse(c.scalars_equal("", "TwoHull", "mount"))

    def test_valid_member_is_none_when_the_field_is_not_enum_typed(self):
        self.assertIsNone(modcheck.ENUMS.valid_member("TIGunTemplate", "crew",
                                                      "OneHull"))
        self.assertIs(modcheck.ENUMS.valid_member("TIGunTemplate", "mount",
                                                  "Nose"), True)
        self.assertIs(modcheck.ENUMS.valid_member("TIGunTemplate", "mount",
                                                  "Tail"), False)


class FindShadowersTest(unittest.TestCase):
    """_find_shadowers is what turns a MISMATCH into a SHADOWED verdict. It
    only runs on the online path, so this is the only test of it."""

    def setUp(self):
        self.ctx = modcheck.Ctx("merge", "TITechTemplate")
        self.mod = modcheck.Mod("Mine", "/fixture/Mine")
        self.mf = modcheck.ModFile("TITechTemplate.json",
                                   {"Tech_Beta": {"researchCost": 1234}},
                                   None, "merge", False)
        self.other = modcheck.ModFile("TITechTemplate.json", {}, None,
                                      "merge", False)

    def find(self, setters, game, problems):
        index = {("TITechTemplate", "Tech_Beta", "researchCost"): setters}
        return modcheck._find_shadowers(self.mf, "Tech_Beta", self.mod, index,
                                        game, problems, self.ctx)

    def test_names_the_mod_holding_the_value_the_game_has(self):
        out = self.find([("Theirs", 5, 9999, self.other)],
                        {"researchCost": 9999},
                        ["researchCost: expected 1234, game has 9999"])
        self.assertEqual(out, ["Theirs (load order 5) set researchCost to the "
                               "value the game holds"])

    def test_a_mod_whose_value_is_not_the_merged_one_is_not_named(self):
        out = self.find([("Theirs", 5, 4321, self.other)],
                        {"researchCost": 9999},
                        ["researchCost: expected 1234, game has 9999"])
        self.assertEqual(out, [])

    def test_the_reporting_mod_never_shadows_itself(self):
        out = self.find([("Mine", 0, 9999, self.mf)],
                        {"researchCost": 9999},
                        ["researchCost: expected 1234, game has 9999"])
        self.assertEqual(out, [])

    def test_a_nested_problem_path_resolves_to_its_top_level_field(self):
        # The problem path is "materials.metals"; the index is keyed by the
        # top-level field, so the split at modcheck.py:1483 has to strip the
        # rest before the lookup.
        index = {("TITechTemplate", "Tech_Beta", "materials"):
                 [("Theirs", 5, {"metals": 0.5}, self.other)]}
        out = modcheck._find_shadowers(
            self.mf, "Tech_Beta", self.mod, index,
            {"materials": {"metals": 0.5}},
            ["materials.metals: expected 0.9, game has 0.5"], self.ctx)
        self.assertEqual(len(out), 1)
        self.assertIn("set materials to the value the game holds", out[0])



if __name__ == "__main__":
    unittest.main()
