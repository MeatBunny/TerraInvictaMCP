"""Save discovery, for both formats the game writes.

  python3 -m unittest discover -s server/tests

A profile setting decides whether a campaign is written as a gzipped `.gz` or
as plain `.json`, and the DLL lists and resolves saves by both (SaveExtensions
in src/Verbs.cs). The offline readers looked only for `.gz`, so with that
setting off, save_check reported an empty saves folder, the ti://saves
fallback listed nothing, and a save named exactly right was answered with "no
save named". Worse when the two formats sat side by side: an older `.gz`
outranked the save just written and nothing said which file had been read.

Nothing here needs a running game -- that is the point of these two readers.
"""
import gzip
import json
import os
import unittest

# Arms the guard that fails any case which would dial a running game.
import _offline                                     # noqa: F401
import _fixtures
from _fixtures import modcheck
import resources


class SaveFixture(_fixtures.ModcheckTestCase):
    """A saves folder under the fixture tree."""

    def write_save(self, filename, mtime=None, gamestates=None):
        path = os.path.join(modcheck.SAVES_DIR, filename)
        payload = {"gamestates": gamestates or {}}
        opener = gzip.open if filename.endswith(".gz") else open
        with opener(path, "wt", encoding="utf-8") as f:
            json.dump(payload, f)
        if mtime is not None:
            os.utime(path, (mtime, mtime))
        return path


class SaveDiscoveryTest(SaveFixture):

    # ---------------------------------------------------------- discovery

    def test_both_formats_are_listed_newest_first(self):
        self.write_save("scratch-old.gz", mtime=1000)
        self.write_save("scratch-new.json", mtime=2000)
        self.assertEqual(modcheck._list_saves(),
                         ["scratch-new.json", "scratch-old.gz"])

    def test_a_file_that_is_not_a_save_is_ignored(self):
        self.write_save("scratch-plain.json")
        with open(os.path.join(modcheck.SAVES_DIR, "notes.txt"), "w") as f:
            f.write("x")
        self.assertEqual(modcheck._list_saves(), ["scratch-plain.json"])

    # ------------------------------------------------------------- reading

    def test_the_newest_save_wins_across_formats(self):
        self.write_save("scratch-old.gz", mtime=1000)
        self.write_save("scratch-new.json", mtime=2000)
        self.assertEqual(modcheck.save_check()["summary"]["save"],
                         "scratch-new.json")

    # -------------------------------------------------------- by name

    def test_a_name_with_its_extension_resolves_and_reads(self):
        # An uncompressed save is read, not gunzipped.
        self.write_save("scratch-plain.json")
        result = modcheck.save_check("scratch-plain.json")
        self.assertEqual(result["summary"]["save"], "scratch-plain.json")
        self.assertNotIn("error", result["summary"])

    def test_a_bare_name_resolves_in_either_format(self):
        self.write_save("scratch-plain.json")
        self.write_save("scratch-zipped.gz")
        for name, want in (("scratch-plain", "scratch-plain.json"),
                           ("scratch-zipped", "scratch-zipped.gz")):
            with self.subTest(name):
                self.assertEqual(
                    modcheck.save_check(name)["summary"]["save"], want)

    def test_a_bare_name_in_both_formats_takes_the_newer_file(self):
        self.write_save("scratch-both.gz", mtime=1000)
        self.write_save("scratch-both.json", mtime=2000)
        self.assertEqual(modcheck.save_check("scratch-both")["summary"]
                         ["save"], "scratch-both.json")

    def test_a_name_that_is_not_there_still_fails(self):
        self.write_save("scratch-plain.json")
        result = modcheck.save_check("scratch-missing")
        self.assertEqual(result["status"], "FAIL")
        self.assertIn("no save named", result["summary"]["error"])

    def test_an_empty_folder_still_fails(self):
        result = modcheck.save_check()
        self.assertEqual(result["status"], "FAIL")
        self.assertIn("no saves found", result["summary"]["error"])


class SavesResourceTest(SaveFixture):
    """ti://saves lists the folder, newest first, in both formats."""

    def listing(self):
        return json.loads(resources._saves_listing())

    def test_both_formats_are_listed(self):
        self.write_save("scratch-old.gz", mtime=1000)
        self.write_save("scratch-new.json", mtime=2000)
        listing = self.listing()
        self.assertEqual([(s["name"], s["extension"])
                          for s in listing["saves"]],
                         [("scratch-new", ".json"), ("scratch-old", ".gz")])

    def test_a_file_that_is_not_a_save_is_ignored(self):
        with open(os.path.join(modcheck.SAVES_DIR, "notes.txt"), "w") as f:
            f.write("x")
        self.assertEqual(self.listing()["saves"], [])


if __name__ == "__main__":
    unittest.main()
