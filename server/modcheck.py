"""Template/DLL acceptance library for TerraInvictaMCP.

The validation engine behind the `modcheck`, `save_check` and `workshop_status`
tools. Everything returns structured dicts; nothing prints except the
`__main__` debug entry.

Public interface:

    run(bridge, mod=None, check="all", scenario=None, verbose=False) -> dict
        bridge is a module-like object exposing send(request, timeout, port)
        and BridgeError. check selects merge, refs, locale, conflicts, reach,
        or all. Each check returns {status, summary, verdicts, warnings,
        lints} with a nextStep string per finding.

    save_check(name=None) -> dict
        Offline save compatibility: templateName references and
        TIMetadataState metadata diffed against the merged template universe.

    workshop_status(mod=None) -> dict
        WorkshopItemInfo.xml vs upstream Steam Workshop state.

Merge knowledge carried over from the original engine: field-wise merge by
dataName, arrays merged by index, TemplatesToConcatArrays /
TemplatesToReplaceArrays / TemplatesToReplace, enum name-vs-ordinal
resolution from the engine's own enum table (the `query.enums` verb, cached
to modcheck_enums.dat), blank placeholders, cross-mod shadowing via
LoadOrder, lenient JSON reading.

Scenario semantics: DLC_Content/ is a third template source loaded even with
mods off; registration order is metatemplates, vanilla, DLC with
first-registered-wins on (type, dataName) duplicates. Equal scenarioTags on a
collision is a shadow; differing tags are legal scenario variants; an
untagged-vs-untagged collision is a silent drop; a tag no scenario declares
is unreachable content.
"""
import collections
import gzip
import hashlib
import json
import math
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

_HERE = os.path.dirname(os.path.abspath(__file__))
# server/ sits inside the mod folder; the game root is ../../.. from the mod
# folder whether the mod sits in Enabled or Disabled.
ROOT = os.path.abspath(os.path.join(_HERE, "..", "..", "..", ".."))
VANILLA_DIR = os.path.join(ROOT, "TerraInvicta_Data", "StreamingAssets", "Templates")
DLC_DIR = os.path.join(ROOT, "DLC_Content")
MODS_ENABLED = os.path.join(ROOT, "Mods", "Enabled")
MODS_DISABLED = os.path.join(ROOT, "Mods", "Disabled")
BASELINE_PATH = os.path.join(_HERE, "modcheck_baseline.dat")
# .dat, not .json, and not by accident: the game's Mod Manager walks a mod
# folder recursively and tries to parse every .json in it as a template array.
# A JSON object under any name raises a parse error and a popup at launch.
# modcheck_baseline.dat carries the same extension for the same reason.
ENUM_CACHE_PATH = os.path.join(_HERE, "modcheck_enums.dat")

WORKSHOP_API = ("https://api.steampowered.com/ISteamRemoteStorage/"
                "GetPublishedFileDetails/v1/")

# Newtonsoft writes the concrete type of a polymorphic member here. It is a
# deserializer instruction, not template data, and never appears in the merged
# entry.
TYPE_DISCRIMINATOR = "$type"

LOCALIZATION_RE = re.compile(
    r"\.(en|fr|deu|ger|spa|esp|chs|cht|cze|rus|pol|jpn|kor|por|ita|tur|ukr)$", re.I)

CHECK_NAMES = ("merge", "refs", "locale", "conflicts", "reach")


# ---------------------------------------------------------------- bridge import

sys.path.insert(0, _HERE)
import bridge as _bridge_default                    # noqa: E402

# Saves: bridge owns the resolution (Proton prefix on Linux, registry-resolved
# Documents on Windows, TIBRIDGE_SAVES over both) so the tools and modcheck can
# never disagree about where the saves are.
SAVES_DIR = _bridge_default.SAVES_DIR


class _Session:
    """One modcheck run's view of the bridge: id counter, feature detection."""

    def __init__(self, bridge_mod, timeout=25.0):
        self.bridge = bridge_mod
        self.timeout = timeout
        self.next_id = 1
        self.up = None          # None = not probed yet
        self.served = set()     # verbs the DLL serves, when known
        self.version = {}
        self.error = None

    def call(self, cmd, **args):
        """One verb call. Returns the reply dict; raises the bridge's
        BridgeError on transport failure."""
        req = {"id": self.next_id, "cmd": cmd, "args": args}
        self.next_id += 1
        return self.bridge.send(req, timeout=self.timeout, port=None)

    def probe(self):
        """Feature-detect: is the bridge up, and which verbs does it serve."""
        if self.up is not None:
            return self.up
        try:
            ver = self.call("version")
            self.version = ver.get("data", {}) if ver.get("ok") else {}
            verbs = self.call("verbs")
            if verbs.get("ok"):
                self.served = {v.get("name") for v in
                               verbs.get("data", {}).get("verbs", [])}
            self.up = True
        except self.bridge.BridgeError as e:
            self.up = False
            self.error = str(e)
        except Exception as e:  # a reply that is not the protocol's shape
            self.up = False
            self.error = "bridge answered garbage: %s" % e
        return self.up

    def serves(self, verb):
        return self.probe() and verb in self.served


# ---------------------------------------------------------------- lenient JSON

def strip_json_comments(text):
    """Remove // and /* */ comments and trailing commas, respecting string
    literals. The game parses templates with Newtonsoft, which accepts both
    comment forms; a naive regex eats the // inside every URL."""
    out = []
    i, n = 0, len(text)
    in_str = False
    while i < n:
        c = text[i]
        if in_str:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 2
                continue
            if c == '"':
                in_str = False
            i += 1
            continue
        if c == '"':
            in_str = True
            out.append(c)
            i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] not in "\r\n":
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            end = text.find("*/", i + 2)
            i = n if end < 0 else end + 2
            continue
        out.append(c)
        i += 1
    joined = "".join(out)
    return re.sub(r",(\s*[}\]])", r"\1", joined)


def load_json(path):
    with open(path, "r", encoding="utf-8-sig", errors="replace") as f:
        return json.loads(strip_json_comments(f.read()))


def load_entries(path):
    """Parse a template file into {dataName: entry}. Returns (entries, error)."""
    try:
        data = load_json(path)
    except (OSError, ValueError) as e:
        return None, str(e)
    if not isinstance(data, list):
        return None, "top level is %s, not an array" % type(data).__name__
    entries = {}
    for i, entry in enumerate(data):
        if not isinstance(entry, dict):
            return None, "element %d is not an object" % i
        name = entry.get("dataName")
        if not isinstance(name, str) or not name:
            return None, "element %d has no dataName" % i
        entries[name] = entry
    return entries, None


def get_ci(d, key, default=None):
    """ModInfo keys are spelled inconsistently across mods; match
    case-insensitively."""
    if not isinstance(d, dict):
        return default
    low = key.lower()
    for k, v in d.items():
        if k.lower() == low:
            return v
    return default


# ---------------------------------------------------------------- enum table

# Unity writes its own editor version into globalgamemanagers alongside the
# game's bundleVersion; the editor one carries the fNN suffix, so the
# alternation matches it first and it can be skipped by shape.
_VERSION_RE = re.compile(r"\d{4}\.\d+\.\d+f\d+|\d+\.\d+\.\d+")
_EDITOR_VERSION_RE = re.compile(r"\d{4}\.\d+\.\d+f\d+$")


def _version_from_unity():
    """Unity's bundleVersion, near the head of globalgamemanagers. Present on
    every install, with or without a log, a campaign, or the game running."""
    path = os.path.join(ROOT, "TerraInvicta_Data", "globalgamemanagers")
    try:
        with open(path, "rb") as f:
            head = f.read(8192)
    except OSError:
        return None
    for m in _VERSION_RE.finditer(head.decode("latin-1")):
        if _EDITOR_VERSION_RE.match(m.group(0)):
            continue        # the Unity editor's version, not the game's
        return m.group(0)
    return None


# The installed game version, for baseline keying.
_game_version = lambda: _version_from_unity() or "unknown"   # noqa: E731


def _ordinal_set(value):
    """The ordinals one member name carries. Normally a single number; an
    array when two enums sharing a simple name declare the same member with
    different values, which the verb unions rather than resolving."""
    if isinstance(value, bool):
        return frozenset()
    if isinstance(value, int):
        return frozenset((value,))
    if isinstance(value, list):
        return frozenset(v for v in value
                         if isinstance(v, int) and not isinstance(v, bool))
    return frozenset()


class EnumTable:
    """The engine's own enum knowledge, from the `query.enums` verb.

    Two tables, both keyed by simple type name because that is what a
    template JSON value and a field's declared type are each read as:

      - enum name -> {member name: ordinal}. The game serializes enums by
        name; a mod may write either a name or an ordinal, and an empty
        string parses to the enum's zero member, whose name is not
        predictable from the field name alone.
      - template class name -> {field: enum name}, for validating enum-typed
        values without cross-class field-name collisions. The verb has
        already walked base classes, so this is a flat lookup.

    Resolution order is the engine first, then the on-disk cache the last
    engine answer left behind. A live engine always wins: the cache exists so
    a user who has run the game once keeps enum checking with the game down,
    and a version mismatch against it is reported rather than treated as
    grounds to throw the table away.
    """

    def __init__(self):
        self.source = None        # "bridge" | "cache" | None
        self.meta = {}            # gameVersion, capturedAt, counts, fingerprint
        self.fingerprint = None
        self.enums = {}           # enum name -> {member: ordinal or [ordinals]}
        self.classes = {}         # class name -> {field: enum name}
        self.type_members = {}    # enum name -> {member: frozenset(ordinals)}
        self.name_values = {}     # member name -> set of ordinals, all enums
        self.type_ordinals = {}   # enum name -> set of ordinals
        self.errors = []          # the verb's own partial-reflection reports
        self.scope = "all"        # "all" | "templates" (the budget fallback)
        self.bridge_error = None
        self.bridge_error_kind = None   # "transport" | "verb"
        self.stale_against = None       # live version the table does not match
        self._budget_refused = False
        self._cache_tried = False

    # ------------------------------------------------------------ resolution

    def ensure(self, session=None):
        """Resolve the table, and re-try the engine on every call until it
        answers: a run that started with the game down and a cached table
        should upgrade the moment the engine is reachable.

        The singleton outlives any one run, so an adopted engine table is only
        kept while it still describes the engine now answering. The documented
        post-update sequence (install.sh, game_start, modcheck) happens inside
        a session that has usually run modcheck already, and the table it holds
        is the one from before the update."""
        if self.source == "bridge" and not self._stale_for(session):
            return
        payload = self._from_bridge(session)
        if payload is not None:
            self._adopt(payload, "bridge")
            self._write_cache(payload)
            return
        if self.source == "bridge":
            # The re-fetch failed. A table from the previous version still
            # beats none; notes() reports the version it no longer matches.
            return
        if self.source == "cache" or self._cache_tried:
            return
        self._cache_tried = True
        payload = self.read_cache()
        if payload is not None:
            self._adopt(payload, "cache")

    def _stale_for(self, session):
        """Does the held table describe a different game than the engine now
        answering? Compared against the live version only; disk sources say
        nothing about the process that is running."""
        if session is None or not session.probe():
            self.stale_against = None
            return False
        live = (session.version or {}).get("game")
        mine = self.meta.get("gameVersion")
        if live and mine and live != mine:
            self.stale_against = live
            return True
        self.stale_against = None
        return False

    def _from_bridge(self, session):
        if session is None:
            self._fail("transport", "no bridge session")
            return None
        if not session.probe():
            self._fail("transport", session.error or "game not running")
            return None
        if not session.serves("query.enums"):
            self._fail("verb", "the running DLL does not serve query.enums")
            return None
        data = self._call_enums(session, "all")
        if data is None and self._budget_refused:
            # The whole assembly does not fit the response budget. The verb's
            # own escape hatch keeps only the enums a template field can hold,
            # which is what the enum checks read.
            data = self._call_enums(session, "templates")
        if data is None:
            return None
        # One stamp for both the adopted table and the cache written from it.
        data["capturedAt"] = time.strftime("%Y-%m-%dT%H:%M:%S")
        self.bridge_error = None
        self.bridge_error_kind = None
        return data

    def _call_enums(self, session, scope):
        self._budget_refused = False
        try:
            resp = session.call("query.enums", scope=scope)
        except Exception as e:
            self._fail("transport", "the query.enums call did not complete: %s" % e)
            return None
        if not resp.get("ok"):
            err = resp.get("error") or "no error text"
            self._budget_refused = "budget" in str(err).lower()
            self._fail("verb", "query.enums (scope %s) answered with an error: %s"
                               % (scope, err))
            return None
        data = resp.get("data")
        if not self._well_formed(data):
            self._fail("verb", "query.enums (scope %s) returned an unexpected shape"
                               % scope)
            return None
        return data

    def _fail(self, kind, message):
        self.bridge_error = message
        self.bridge_error_kind = kind      # "transport" | "verb"

    @staticmethod
    def _well_formed(doc):
        return (isinstance(doc, dict) and isinstance(doc.get("enums"), dict)
                and doc["enums"] and isinstance(doc.get("classes"), dict))

    @staticmethod
    def read_cache():
        """The cached verb response, or None when it is absent or unusable."""
        try:
            with open(ENUM_CACHE_PATH, encoding="utf-8") as f:
                doc = json.load(f)
        except (OSError, ValueError):
            return None
        return doc if EnumTable._well_formed(doc) else None

    def _write_cache(self, doc):
        # Temp file plus rename: an interrupted write must not leave the next
        # offline run with no table. A read-only install still gets a working
        # table this run.
        tmp = "%s.%d.tmp" % (ENUM_CACHE_PATH, os.getpid())
        try:
            with open(tmp, "w", encoding="utf-8") as f:
                json.dump(doc, f, separators=(",", ":"), sort_keys=True)
            os.replace(tmp, ENUM_CACHE_PATH)
        except OSError:
            try:
                os.remove(tmp)
            except OSError:
                pass

    def _adopt(self, payload, source):
        self.enums = {k: v for k, v in payload["enums"].items()
                      if isinstance(v, dict)}
        self.classes = {k: v for k, v in payload["classes"].items()
                        if isinstance(v, dict)}
        self.errors = [e for e in (payload.get("errors") or [])
                       if isinstance(e, str)]
        self.scope = payload.get("scope") or "all"
        self.type_members = {}
        self.name_values = {}
        self.type_ordinals = {}
        members = 0
        for etype, table in self.enums.items():
            by_member = {}
            ordinals = set()
            for member, value in table.items():
                members += 1
                mine = _ordinal_set(value)
                by_member[member] = mine
                ordinals |= mine
                if mine:
                    self.name_values.setdefault(member, set()).update(mine)
            self.type_members[etype] = by_member
            self.type_ordinals[etype] = ordinals
        blob = json.dumps([self.enums, self.classes], sort_keys=True,
                          separators=(",", ":"))
        self.fingerprint = hashlib.sha256(
            blob.encode("utf-8")).hexdigest()[:16]
        self.source = source
        self.stale_against = None
        self.meta = {
            "gameVersion": payload.get("gameVersion"),
            "capturedAt": payload.get("capturedAt")
                          or time.strftime("%Y-%m-%dT%H:%M:%S"),
            "scope": self.scope,
            "enums": len(self.enums),
            "members": members,
            "classes": len(self.classes),
            "fields": sum(len(f) for f in self.classes.values()),
            "reflectionErrors": len(self.errors),
            "fingerprint": self.fingerprint,
        }

    # ------------------------------------------------------------ reporting

    def _reason(self):
        """Why the engine did not supply the table. A verb-level refusal is
        not a missing game: the engine answered, it just could not serve
        this."""
        if self.bridge_error_kind == "verb":
            return ("the engine answered but the enum table did not (%s)"
                    % self.bridge_error)
        return ("the game is not running or its bridge is unreachable (%s)"
                % (self.bridge_error or "not probed"))

    def _action(self):
        if self.bridge_error_kind == "verb":
            return ("Rebuild and reinstall TerraInvictaMCP (install.sh) and "
                    "restart the game so the running DLL serves query.enums, "
                    "then rerun.")
        return "Start the game and rerun."

    def notes(self):
        """Prose for a check's `degraded` list: what is weaker than it should
        be, and the one action that fixes it. Empty when the engine supplied a
        complete, current table."""
        out = []
        if self.source is None:
            tail = self._action()
            if self.bridge_error_kind != "verb":
                tail = ("Start the game once with TerraInvictaMCP installed "
                        "and rerun -- modcheck writes the engine's enum table "
                        "to that file, and every later run reuses it with the "
                        "game down.")
            return [("enum checks were skipped: %s, and there is no cached "
                     "table at %s. %s"
                     % (self._reason(), ENUM_CACHE_PATH, tail))]
        if self.source == "cache":
            msg = ("enum knowledge came from the cached table in %s (captured "
                   "%s against game version %s) because %s. %s"
                   % (os.path.basename(ENUM_CACHE_PATH),
                      self.meta.get("capturedAt") or "unknown",
                      self.meta.get("gameVersion") or "unknown",
                      self._reason(), self._action()))
            out.append(msg)
        if self.stale_against:
            out.append(
                "the enum table in hand was captured against game version %s "
                "and the running engine reports %s; the re-fetch failed (%s). "
                "Rerun once the bridge answers to pick up the new table."
                % (self.meta.get("gameVersion") or "unknown",
                   self.stale_against, self.bridge_error or "unknown"))
        if self.scope == "templates":
            out.append(
                "the full enum table exceeded the bridge response budget, so "
                "only enums reachable from a template field were fetched. "
                "Enum-typed values nested inside an entry are compared by name "
                "across a smaller table and can read as a mismatch.")
        if self.errors:
            out.append(
                "the engine reported %d problem(s) while reading its own "
                "types, so this table is incomplete: %s. Check Player.log for "
                "another DLL mod failing to load; enums and template fields on "
                "the types that did not load are silently absent here."
                % (len(self.errors), "; ".join(self.errors[:3])))
        return out

    # ------------------------------------------------------------ lookups

    def is_zero_member(self, name, cls=None, field=None):
        """Is `name` the zero member? With a class and field, of that field's
        own enum; without them, of any enum that declares the name."""
        return 0 in self.ordinals_for(name, cls, field)

    def matches_ordinal(self, name, ordinal, cls=None, field=None):
        """Does `name` carry `ordinal`? Scoped to the field's own enum when
        the caller knows it."""
        return ordinal in self.ordinals_for(name, cls, field)

    def ordinals_for(self, name, cls=None, field=None):
        """Every ordinal `name` can mean: the field's own enum when the caller
        supplies one, otherwise every enum that declares the name."""
        etype = self.enum_type_for(cls, field) if cls and field else None
        if etype:
            return self.type_members[etype].get(name, frozenset())
        return self.name_values.get(name, frozenset())

    def enum_type_for(self, cls, field):
        """The enum type of `field` on `cls`. The verb flattens base classes,
        so no chain walk is needed here."""
        etype = self.classes.get(cls, {}).get(field)
        return etype if etype in self.enums else None

    def valid_member(self, cls, field, value):
        """Is `value` a legal member (or ordinal) for the enum this class's
        field carries? Returns None when the field is not enum-typed on the
        class or the table is empty."""
        etype = self.enum_type_for(cls, field)
        if not etype:
            return None
        if isinstance(value, str):
            return value in self.type_members[etype]
        if isinstance(value, int) and not isinstance(value, bool):
            return value in self.type_ordinals[etype]
        return None


ENUMS = EnumTable()


def _enum_meta():
    """The two summary keys the merge and refs checks report the table with."""
    meta = {"enumSource": ENUMS.source or "none"}
    if ENUMS.source:
        meta["enumTable"] = dict(ENUMS.meta)
    return meta


# ---------------------------------------------------------------- merge sim

def numbers_equal(a, b):
    return math.isclose(float(a), float(b), rel_tol=1e-6, abs_tol=1e-9)


def as_number(v):
    if isinstance(v, (int, float)) and not isinstance(v, bool):
        return float(v)
    if isinstance(v, str):
        try:
            return float(v.strip())
        except ValueError:
            return None
    return None


def as_bool(v):
    if isinstance(v, bool):
        return v
    if isinstance(v, str) and v.strip().lower() in ("true", "false"):
        return v.strip().lower() == "true"
    return None


def is_blank(v):
    """A placeholder a mod writes to keep the vanilla shape without setting a
    value."""
    if v is None or v == "":
        return True
    if isinstance(v, dict):
        return all(is_blank(x) for x in v.values())
    if isinstance(v, list):
        return all(is_blank(x) for x in v)
    return False


def merge_value(base, patch, array_mode):
    """Newtonsoft-style merge of one member: dicts field-wise, arrays by index
    (or concatenated / replaced per ModInfo), null leaves the base alone."""
    if patch is None:
        return base
    if isinstance(patch, dict):
        if not isinstance(base, dict):
            return {k: v for k, v in patch.items() if k != TYPE_DISCRIMINATOR}
        out = dict(base)
        for k, v in patch.items():
            if k == TYPE_DISCRIMINATOR:
                continue
            out[k] = merge_value(base.get(k), v, array_mode)
        return out
    if isinstance(patch, list):
        if array_mode == "concat" and isinstance(base, list):
            return base + patch
        if array_mode == "replace" or not isinstance(base, list):
            return patch
        out = list(base)
        for i, item in enumerate(patch):
            if i < len(out):
                out[i] = merge_value(out[i], item, array_mode)
            else:
                out.append(item)
        return out
    return patch


# ---------------------------------------------------------------- comparison

# Per-file merge context: array_mode is "merge" | "concat" | "replace",
# template_class scopes enum-typed field lookups.
Ctx = collections.namedtuple("Ctx", "array_mode template_class",
                             defaults=(None,))


class Compare:
    """Collects problems and notes while walking one mod entry against the
    merged one."""

    def __init__(self, ctx):
        self.ctx = ctx
        self.problems = []
        self.notes = []
        self.warnings = []
        self.noops = []
        self.checked = 0

    def problem(self, path, msg):
        self.problems.append("%s: %s" % (path, msg))

    def note(self, path, msg):
        self.notes.append("%s: %s" % (path, msg))

    def walk(self, path, modv, gamev, game_has):
        if modv is None:
            # MergeNullValueHandling is Ignore: a null leaves the vanilla
            # value alone.
            return
        if not game_has:
            self.note(path, "no such member on the merged entry; the template "
                            "class does not carry this field, so the value is "
                            "inert")
            return

        if isinstance(modv, dict):
            self.walk_dict(path, modv, gamev)
            return
        if isinstance(modv, list):
            self.walk_list(path, modv, gamev)
            return

        self.checked += 1
        if self.scalars_equal(modv, gamev, path):
            return
        if is_blank(modv):
            self.checked -= 1
            self.note(path, "mod wrote a blank placeholder, game holds %s"
                      % json.dumps(gamev))
            return
        self.problem(path, "expected %s, game has %s"
                     % (json.dumps(modv), json.dumps(gamev)))

    def enum_context(self, path):
        """(class, field) when `path` names a top-level field of the entry, so
        an enum lookup can be scoped to that field's own enum type. Nested and
        array paths have no entry in the engine's class table, so they get no
        context and fall back to a name-wide match."""
        if not path or "." in path or "[" in path:
            return None, None
        return self.ctx.template_class, path

    def scalars_equal(self, modv, gamev, path=None):
        if modv == gamev:
            return True
        mb, gb = as_bool(modv), as_bool(gamev)
        if mb is not None and gb is not None:
            return mb == gb
        mn, gn = as_number(modv), as_number(gamev)
        if mn is not None and gn is not None:
            return numbers_equal(mn, gn)
        cls, field = self.enum_context(path)
        if isinstance(modv, str) and isinstance(gamev, str):
            if modv.casefold() == gamev.casefold():
                return True
            # An empty string parses to the enum's zero member.
            if modv == "" and ENUMS.is_zero_member(gamev, cls, field):
                return True
            return False
        # Enum written as an ordinal on one side and a name on the other.
        if isinstance(modv, str) and isinstance(gamev, int) \
                and not isinstance(gamev, bool):
            return ENUMS.matches_ordinal(modv, gamev, cls, field)
        if isinstance(gamev, str) and isinstance(modv, int) \
                and not isinstance(modv, bool):
            return ENUMS.matches_ordinal(gamev, modv, cls, field)
        return False

    def walk_dict(self, path, modv, gamev):
        if gamev is None:
            if is_blank(modv):
                self.note(path, "mod wrote a blank placeholder object, game "
                                "holds null")
            else:
                self.problem(path, "expected an object, game has null")
            return
        if not isinstance(gamev, dict):
            self.problem(path, "expected an object, game has %s"
                         % json.dumps(gamev)[:120])
            return
        for k, v in modv.items():
            if k == TYPE_DISCRIMINATOR:
                continue
            self.walk("%s.%s" % (path, k), v, gamev.get(k), k in gamev)

    def walk_list(self, path, modv, gamev):
        if gamev is None:
            if is_blank(modv):
                self.note(path, "mod wrote a blank placeholder array, game "
                                "holds null")
            else:
                self.problem(path, "expected an array, game has null")
            return
        if not isinstance(gamev, list):
            self.problem(path, "expected an array, game has %s"
                         % json.dumps(gamev)[:120])
            return

        if self.ctx.array_mode == "concat":
            for i, item in enumerate(modv):
                if not any(self.deep_equal(item, g) for g in gamev):
                    if is_blank(item):
                        self.note("%s[%d]" % (path, i),
                                  "blank placeholder element, not asserted")
                    else:
                        self.problem("%s[%d]" % (path, i),
                                     "%s is not anywhere in the merged array"
                                     % json.dumps(item)[:120])
                else:
                    self.checked += 1
            return

        if self.ctx.array_mode == "replace" and len(modv) != len(gamev):
            self.problem(path, "replace mode: mod has %d element(s), game "
                               "has %d" % (len(modv), len(gamev)))
        for i, item in enumerate(modv):
            if i >= len(gamev):
                self.problem("%s[%d]" % (path, i),
                             "merged array stops at %d element(s)" % len(gamev))
                continue
            self.walk("%s[%d]" % (path, i), item, gamev[i], True)
        # Array pathologies against vanilla (an empty array that cannot
        # clear, a displaced element) are the static pass's job.
        if self.ctx.array_mode == "merge" and len(gamev) > len(modv):
            self.note(path, "%d trailing vanilla element(s) kept past the "
                            "mod's %d (arrays merge by index)"
                            % (len(gamev) - len(modv), len(modv)))

    def check_alignment(self, path, item, vitem, mod_list):
        """An index-merged element that overwrites a vanilla element nothing
        restates.

        Arrays of objects in these templates are keyed by their first member.
        Under an index merge, a mod element at position i lands on vanilla's
        element i whatever it is named, so a mod array of a different length
        than vanilla's silently drops whatever it displaced. Overwriting a
        name the mod also writes at some other index is the ordinary case of
        editing a list in place, so only a name that disappears from the array
        entirely is worth reporting."""
        if not isinstance(item, dict) or not isinstance(vitem, dict):
            return
        for key, vvalue in vitem.items():
            if key not in item or not isinstance(vvalue, str) or not vvalue:
                continue
            mvalue = item[key]
            if not isinstance(mvalue, str) or not mvalue or mvalue == vvalue:
                return
            restated = any(isinstance(e, dict) and e.get(key) == vvalue
                           for e in mod_list)
            if not restated:
                self.warnings.append(
                    "%s: overwrites the vanilla element %s=%s, which the mod "
                    "never restates" % (path, key, json.dumps(vvalue)))
            return

    def deep_equal(self, modv, gamev):
        probe = Compare(self.ctx)
        probe.walk("x", modv, gamev, True)
        return not probe.problems


# ---------------------------------------------------------------- disk model

_VANILLA_CACHE = {}


def vanilla_entries(filename):
    if filename not in _VANILLA_CACHE:
        _VANILLA_CACHE[filename] = load_entries(os.path.join(VANILLA_DIR, filename))
    return _VANILLA_CACHE[filename]


def vanilla_filenames():
    try:
        return sorted(f for f in os.listdir(VANILLA_DIR) if f.endswith(".json"))
    except OSError:
        return []


def dlc_template_dirs():
    """[(label, templates_dir)] for every DLC scenario package on disk."""
    out = []
    if not os.path.isdir(DLC_DIR):
        return out
    for dlc in sorted(os.listdir(DLC_DIR)):
        base = os.path.join(DLC_DIR, dlc)
        if not os.path.isdir(base):
            continue
        for sub in sorted(os.listdir(base)):
            tdir = os.path.join(base, sub, "Templates")
            if os.path.isdir(tdir):
                out.append(("%s/%s" % (dlc, sub), tdir))
    return out


class ModFile:
    def __init__(self, filename, entries, error, array_mode, whole_replace):
        self.filename = filename
        self.entries = entries or {}
        self.error = error
        self.array_mode = array_mode
        self.whole_replace = whole_replace
        self.template_class = os.path.splitext(filename)[0]


class Mod:
    def __init__(self, name, folder):
        self.name = name
        self.folder = folder
        self.load_order = 0
        self.files = []
        self.modinfo = {}
        self.modinfo_error = None
        self.stray_json = []     # JSON files that match no vanilla template
        self.loc_files = []      # localization files in the folder


def read_mod_info(folder):
    path = os.path.join(folder, "ModInfo.json")
    try:
        return load_json(path), None
    except OSError as e:
        return {}, "ModInfo.json unreadable: %s" % e
    except ValueError as e:
        return {}, "ModInfo.json is not valid JSON: %s" % e


def name_list(info, key):
    v = get_ci(info, key)
    if not isinstance(v, list):
        return set()
    return {str(x).strip().lower() for x in v if isinstance(x, str) and x.strip()}


def _mod_from_folder(name, folder):
    """One enabled mod, read from disk."""
    mod = Mod(name, folder)
    mod.modinfo, mod.modinfo_error = read_mod_info(folder)
    mod.load_order = get_ci(mod.modinfo, "LoadOrder", 0) or 0
    vanilla = set(vanilla_filenames())
    concat = name_list(mod.modinfo, "TemplatesToConcatArrays")
    replace_arr = name_list(mod.modinfo, "TemplatesToReplaceArrays")
    whole = name_list(mod.modinfo, "TemplatesToReplace")
    try:
        names = sorted(os.listdir(folder))
    except OSError:
        names = []
    for n in names:
        full = os.path.join(folder, n)
        if os.path.isdir(full):
            # The game reads a mod folder recursively; localization commonly
            # sits under Localization/<lang>/.
            for dirpath, _dirs, files in os.walk(full):
                mod.loc_files.extend(os.path.join(dirpath, f)
                                     for f in sorted(files)
                                     if LOCALIZATION_RE.search(f))
            continue
        if LOCALIZATION_RE.search(n):
            mod.loc_files.append(full)
            continue
        if not n.endswith(".json") or n == "ModInfo.json":
            continue
        if n not in vanilla:
            mod.stray_json.append(n)
            continue
        low = n.lower()
        mode = ("concat" if low in concat else
                "replace" if low in replace_arr else "merge")
        entries, err = load_entries(full)
        mod.files.append(ModFile(n, entries, err, mode, low in whole))
    return mod


def collect_mods_disk(only=None):
    """Enabled template mods read straight from Mods/Enabled/."""
    mods = {}
    try:
        folders = sorted(os.listdir(MODS_ENABLED))
    except OSError:
        return mods
    for name in folders:
        folder = os.path.join(MODS_ENABLED, name)
        if not os.path.isdir(folder):
            continue
        if only and name != only:
            continue
        mod = _mod_from_folder(name, folder)
        if mod.files or mod.stray_json or mod.loc_files:
            mods[name] = mod
    return mods


def collect_mods_bridge(mods_list, only=None):
    """Enabled template mods as the game's own loader records them
    (mods.list), with the JSON re-read from disk."""
    files = mods_list["templates"]["files"]
    mods = {}
    for rec in files:
        name = rec["mod"]
        if only and name != only:
            continue
        mod = mods.get(name)
        if mod is None:
            folder = os.path.join(MODS_ENABLED, name)
            mod = mods[name] = _mod_from_folder(name, folder)
            mod.files = []      # rebuilt from the loader's records below
        if rec.get("loadOrder") is not None:
            mod.load_order = rec["loadOrder"]
        concat = name_list(mod.modinfo, "TemplatesToConcatArrays")
        replace_arr = name_list(mod.modinfo, "TemplatesToReplaceArrays")
        whole = name_list(mod.modinfo, "TemplatesToReplace")
        fn = rec["file"]
        low = fn.lower()
        mode = ("concat" if low in concat else
                "replace" if low in replace_arr else "merge")
        # The loader records `mod` as the file's immediate parent folder, so
        # a file nested below the mod folder resolves through the recorded
        # (Wine-side) path instead.
        full = os.path.join(MODS_ENABLED, rec["mod"], rec["file"])
        if not os.path.isfile(full):
            rp = re.sub(r"^[A-Za-z]:", "",
                        (rec.get("path") or "").replace("\\", "/"))
            if os.path.isfile(rp):
                full = rp
        entries, err = load_entries(full)
        mod.files.append(ModFile(fn, entries, err, mode, low in whole))
    return mods


def build_field_index(all_mods):
    """(templateClass, dataName, field) -> [(modName, loadOrder, value, modfile)]."""
    index = {}
    for mod in all_mods.values():
        for mf in mod.files:
            for dn, entry in mf.entries.items():
                for field, value in entry.items():
                    if field == "dataName":
                        continue
                    index.setdefault((mf.template_class, dn, field), []).append(
                        (mod.name, mod.load_order, value, mf))
    return index


# ---------------------------------------------------------------- universe

class Universe:
    """The merged template universe simulated from disk: vanilla, then DLC
    (first-registered-wins on duplicates), then enabled mods merged field-wise
    by dataName in LoadOrder."""

    def __init__(self, include_mods=True, only_mod=None):
        self.classes = {}        # class -> {dataName: merged entry}
        self.tags = {}           # (class, dataName) -> scenarioTags list
        self.origin = {}         # (class, dataName) -> source label
        self.mod_touched = {}    # (class, dataName) -> [mod names]
        self.collisions = []     # cross-source duplicate records
        self.file_errors = []    # unreadable template files
        self.scenarios = {}      # scenario dataName -> info
        self.declared_tags = set()
        self._build(include_mods, only_mod)

    def _register_source(self, label, tdir):
        for fn in sorted(os.listdir(tdir)):
            if not fn.endswith(".json"):
                continue
            cls = os.path.splitext(fn)[0]
            if label == "vanilla":
                entries, err = vanilla_entries(fn)
            else:
                entries, err = load_entries(os.path.join(tdir, fn))
            if err:
                self.file_errors.append({"source": label, "file": fn,
                                         "error": err})
                continue
            bucket = self.classes.setdefault(cls, {})
            for dn, entry in entries.items():
                key = (cls, dn)
                tags = entry.get("scenarioTags") or []
                if dn in bucket:
                    prev_tags = self.tags.get(key) or []
                    self.collisions.append({
                        "class": cls, "dataName": dn,
                        "winner": self.origin[key], "loser": label,
                        "winnerTags": list(prev_tags), "loserTags": list(tags)})
                    # first-registered-wins: the earlier entry stays live
                    continue
                # A copy: vanilla_entries hands out the module-level cache's
                # own dicts, and everything downstream of here writes into the
                # universe -- the mod merge, and the live overlay that
                # replaces disk values with the engine's. A write reaching the
                # cache would outlive the run and land in the baseline, where
                # it suppresses the finding it created.
                bucket[dn] = dict(entry)
                self.tags[key] = tags
                self.origin[key] = label

    def _build(self, include_mods, only_mod):
        if os.path.isdir(VANILLA_DIR):
            self._register_source("vanilla", VANILLA_DIR)
        for label, tdir in dlc_template_dirs():
            self._register_source("dlc:" + label, tdir)

        if include_mods:
            mods = collect_mods_disk(only=None)
            for mod in sorted(mods.values(),
                              key=lambda m: (m.load_order, m.name)):
                if only_mod and mod.name != only_mod:
                    continue
                for mf in mod.files:
                    if mf.error:
                        self.file_errors.append({"source": "mod:" + mod.name,
                                                 "file": mf.filename,
                                                 "error": mf.error})
                        continue
                    bucket = self.classes.setdefault(mf.template_class, {})
                    for dn, entry in mf.entries.items():
                        key = (mf.template_class, dn)
                        self.mod_touched.setdefault(key, []).append(mod.name)
                        if mf.whole_replace or dn not in bucket:
                            merged = {k: v for k, v in entry.items()
                                      if k != TYPE_DISCRIMINATOR}
                        else:
                            merged = merge_value(bucket[dn], entry,
                                                 mf.array_mode)
                        bucket[dn] = merged
                        # Read off the merged entry, unconditionally. The
                        # mod's own patch carries scenarioTags only when it
                        # sets them, and the two disagree in both directions:
                        # a field merge that leaves vanilla's tags alone, and
                        # a whole replacement that drops them.
                        self.tags[key] = merged.get("scenarioTags") or []
                        self.origin.setdefault(key, "mod:" + mod.name)

            # Drop entries whose merged disable is true: TemplateManager never
            # registers them.
            for cls, bucket in self.classes.items():
                dead = [dn for dn, e in bucket.items() if bool(e.get("disable"))]
                for dn in dead:
                    del bucket[dn]

        # Scenario declarations, from every registered TIMetaTemplate entry.
        # Read after the mod merge: a mod may add a scenario or patch one of
        # the fields read here (a localization postfix, the tag list).
        for dn, entry in (self.classes.get("TIMetaTemplate") or {}).items():
            tags = entry.get("scenarioTags") or []
            if entry.get("newCampaignOptionCategory") == "Scenario" or \
                    entry.get("isNewCampaignOption"):
                self.scenarios[dn] = {
                    "tags": list(tags),
                    "postfix": entry.get("scenarioLocalizationPostfix"),
                }
                self.declared_tags.update(tags)

    def names(self, cls):
        return set(self.classes.get(cls) or ())

    def all_data_names(self):
        out = set()
        for bucket in self.classes.values():
            out.update(bucket)
        return out

    def live_for_scenario(self, cls, dn, scenario_tags):
        tags = self.tags.get((cls, dn)) or []
        return set(tags) <= set(scenario_tags)


# ---------------------------------------------------------------- findings

def _finding(**kw):
    return {k: v for k, v in kw.items() if v not in (None, [], "")}


def _check_result(status, summary, verdicts, warnings, lints, verbose,
                  cap=40):
    """Assemble one check's dict; non-verbose keeps summary counts intact and
    truncates the per-entry lists."""
    out = {"status": status, "summary": summary,
           "verdicts": verdicts, "warnings": warnings, "lints": lints}
    if not verbose:
        for key in ("verdicts", "warnings", "lints"):
            items = out[key]
            if len(items) > cap:
                out[key] = items[:cap]
                out["summary"]["%sTruncated" % key] = len(items) - cap
    return out


def _status(problems, warnings, degraded):
    if problems:
        return "FAIL"
    if degraded:
        return "DEGRADED"
    if warnings:
        return "WARN"
    return "PASS"


# ---------------------------------------------------------------- merge check

_NEXT = {
    "MISMATCH": "the merged value differs from what the mod wrote; run the "
                "conflicts check and compare LoadOrder with the other mods "
                "touching this entry",
    "SHADOWED": "another enabled mod writes this field with a higher (or "
                "equal) LoadOrder; raise this mod's LoadOrder, or add the "
                "file to TemplatesToConcatArrays / TemplatesToReplace",
    "MISSING": "the merged entry never registered; check the file name "
               "matches a vanilla template file and a new entry carries all "
               "required fields",
    "UNPARSED": "fix the JSON syntax; the game's Newtonsoft parser fails on "
                "this file the same way",
    "CRASH_RISK": "rename or remove the file; a mod JSON file whose name "
                  "matches no vanilla template file crashes template load",
    "DISABLED": "the entry carries disable: true, so the game never "
                "registers it; drop the disable field if that is unintended",
}


def _merge_offline_mod(mod, verdicts, warnings, lints):
    """The static half of the merge check: everything knowable without the
    game. Returns the count of fields statically examined."""
    checked = 0
    for stray in mod.stray_json:
        verdicts.append(_finding(
            mod=mod.name, file=stray, verdict="CRASH_RISK",
            detail="JSON file matches no vanilla template file",
            nextStep=_NEXT["CRASH_RISK"]))
    title = get_ci(mod.modinfo, "title")
    if isinstance(title, str) and title and title != mod.name:
        lints.append(_finding(
            mod=mod.name, lint="titleMismatch",
            detail="ModInfo title %r differs from the folder name %r"
                   % (title, mod.name),
            nextStep="set ModInfo.json title to the folder name; the loader "
                     "matches them"))
    if mod.modinfo_error:
        warnings.append(_finding(mod=mod.name, warning=mod.modinfo_error,
                                 nextStep="fix ModInfo.json"))
    for mf in mod.files:
        if mf.error:
            verdicts.append(_finding(
                mod=mod.name, file=mf.filename, verdict="UNPARSED",
                detail="mod file could not be parsed: %s" % mf.error,
                nextStep=_NEXT["UNPARSED"]))
            continue
        van, van_err = vanilla_entries(mf.filename)
        if van_err:
            warnings.append(_finding(
                mod=mod.name, file=mf.filename,
                warning="vanilla %s could not be parsed (%s); new-entry "
                        "detection unavailable" % (mf.filename, van_err)))
        ctx = Ctx(mf.array_mode, mf.template_class)
        for dn in sorted(mf.entries):
            entry = mf.entries[dn]
            van_entry = (van or {}).get(dn)
            if bool(merge_value(van_entry or {}, entry,
                                mf.array_mode).get("disable")):
                verdicts.append(_finding(
                    mod=mod.name, file=mf.filename, dataName=dn,
                    verdict="DISABLED",
                    detail="the merged entry carries disable: true",
                    nextStep=_NEXT["DISABLED"]))
            new_entry = van is not None and dn not in van
            cmp = Compare(ctx)
            for field, value in entry.items():
                if field == "dataName":
                    continue
                checked += 1
                if van_entry is not None and field in van_entry and \
                        value is not None and cmp.deep_equal(value,
                                                             van_entry[field]):
                    cmp.noops.append(field)
                # Static array pathologies against vanilla.
                if isinstance(value, list) and mf.array_mode == "merge" and \
                        van_entry is not None and \
                        isinstance(van_entry.get(field), list):
                    vlist = van_entry[field]
                    if not value and vlist:
                        cmp.warnings.append(
                            "%s: mod writes an empty array, which an index "
                            "merge cannot clear; all %d vanilla element(s) "
                            "survive" % (field, len(vlist)))
                    for i, item in enumerate(value):
                        vitem = vlist[i] if i < len(vlist) else None
                        cmp.check_alignment("%s[%d]" % (field, i), item,
                                            vitem, value)
            for w in cmp.warnings:
                warnings.append(_finding(
                    mod=mod.name, file=mf.filename, dataName=dn, warning=w,
                    nextStep="restate the full vanilla array, or list the "
                             "file in TemplatesToConcatArrays / "
                             "TemplatesToReplaceArrays"))
            if cmp.noops:
                lints.append(_finding(
                    mod=mod.name, file=mf.filename, dataName=dn, lint="noop",
                    detail="%d field(s) already hold the vanilla value and "
                           "change nothing: %s"
                           % (len(cmp.noops), ", ".join(sorted(cmp.noops))),
                    nextStep="drop the fields, or leave them as documentation"))
            if new_entry:
                lints.append(_finding(
                    mod=mod.name, file=mf.filename, dataName=dn, lint="newEntry",
                    detail="new entry (no vanilla dataName match)"))
    return checked


def _merge_online(session, mods, index, verbose, verdicts, warnings, lints):
    """The in-engine half: every field the mod sets vs the merged entry the
    running game holds, plus DLL mod status."""
    checked = 0
    cache = {}
    for mod in mods.values():
        for mf in mod.files:
            if mf.error:
                continue    # already reported by the static half
            van, _van_err = vanilla_entries(mf.filename)
            for dn in sorted(mf.entries):
                key = (mf.template_class, dn)
                if key not in cache:
                    cache[key] = session.call("query.template",
                                              type=mf.template_class,
                                              dataName=dn)
                resp = cache[key]
                if not resp.get("ok"):
                    entry = mf.entries[dn]
                    van_entry = (van or {}).get(dn)
                    if bool(merge_value(van_entry or {}, entry,
                                        mf.array_mode).get("disable")):
                        continue    # DISABLED, reported statically
                    verdicts.append(_finding(
                        mod=mod.name, file=mf.filename, dataName=dn,
                        verdict="MISSING",
                        detail="query.template %s dataName=%s: %s"
                               % (mf.template_class, dn, resp.get("error")),
                        nextStep=_NEXT["MISSING"]))
                    continue
                game = resp["data"].get("template")
                for e in resp["data"].get("errors") or []:
                    if verbose:
                        lints.append(_finding(
                            mod=mod.name, file=mf.filename, dataName=dn,
                            lint="serializer", detail="bridge serializer: %s" % e))
                if not isinstance(game, dict):
                    verdicts.append(_finding(
                        mod=mod.name, file=mf.filename, dataName=dn,
                        verdict="MISSING",
                        detail="the merged entry came back empty",
                        nextStep=_NEXT["MISSING"]))
                    continue
                ctx = Ctx(mf.array_mode, mf.template_class)
                cmp = Compare(ctx)
                for field, value in mf.entries[dn].items():
                    if field == "dataName":
                        continue
                    cmp.walk(field, value, game.get(field), field in game)
                checked += cmp.checked
                if verbose:
                    for nt in cmp.notes:
                        lints.append(_finding(mod=mod.name, file=mf.filename,
                                              dataName=dn, lint="mergeNote",
                                              detail=nt))
                if cmp.problems:
                    shadow = _find_shadowers(mf, dn, mod, index, game,
                                             cmp.problems, ctx)
                    verdicts.append(_finding(
                        mod=mod.name, file=mf.filename, dataName=dn,
                        verdict="SHADOWED" if shadow else "MISMATCH",
                        detail="; ".join(cmp.problems),
                        shadowedBy=shadow or None,
                        nextStep=_NEXT["SHADOWED" if shadow else "MISMATCH"]))
                elif verbose:
                    verdicts.append(_finding(mod=mod.name, file=mf.filename,
                                             dataName=dn, verdict="OK",
                                             fieldsCompared=cmp.checked))
    return checked


def _find_shadowers(mf, data_name, mod, index, game, problems, ctx):
    """Name the mods whose value for a failing field is the one the game
    holds."""
    fields = {p.split(":")[0].split(".")[0].split("[")[0] for p in problems}
    out = []
    for field in sorted(fields):
        setters = index.get((mf.template_class, data_name, field), [])
        for other_name, other_order, other_value, _mf in setters:
            if other_name == mod.name:
                continue
            probe = Compare(ctx)
            probe.walk(field, other_value, game.get(field), field in game)
            if not probe.problems:
                out.append("%s (load order %s) set %s to the value the game "
                           "holds" % (other_name, other_order, field))
    return out


def _merge_dll_online(session, mods_list, only, verdicts, warnings):
    entries = mods_list.get("dll") or []
    disabled_folders = {n.lower() for n in
                        (mods_list["templates"]["folders"].get("disabled") or [])}
    for e in entries:
        mod_id = e.get("id") or e.get("displayName") or "?"
        if only and mod_id != only and e.get("displayName") != only:
            continue
        faults = []
        for flag in ("enabled", "active", "loaded", "started"):
            if not e.get(flag):
                faults.append("not %s" % flag)
        if e.get("errorOnLoading"):
            faults.append("errorOnLoading")
        if not e.get("hasAssembly"):
            faults.append("no assembly")
        path = (e.get("path") or "").replace("\\", "/").lower()
        if "/mods/enabled/" not in path:
            faults.append("loaded from outside Mods/Enabled (%s)" % e.get("path"))
        for folder in disabled_folders:
            if "/" + folder + "/" in path:
                faults.append("folder is in Mods/Disabled yet the mod is loaded")
        count = None
        try:
            resp = session.call("harmony.patches", owner=mod_id)
            if resp.get("ok"):
                data = resp["data"]
                methods = data if isinstance(data, list) else \
                    data.get("methods", data)
                count = len(methods) if isinstance(methods, (list, dict)) else 0
            else:
                faults.append("harmony.patches failed: %s" % resp.get("error"))
        except session.bridge.BridgeError as ex:
            faults.append("harmony.patches unreachable: %s" % ex)
        if faults:
            verdicts.append(_finding(
                mod=mod_id, kind="dll", verdict="MISMATCH",
                detail="; ".join(faults),
                nextStep="check Player.log for the [Manager] load lines; a "
                         "game update replacing UnityEngine.UIModule.dll "
                         "silently reverts the UMM injection"))
        elif count == 0:
            warnings.append(_finding(
                mod=mod_id, kind="dll",
                warning="loaded with no Harmony patches (a mod that only "
                        "registers terminal commands looks like this)"))


def check_merge(session, only_mod, verbose):
    verdicts, warnings, lints = [], [], []
    degraded = []
    # Before any comparison: the value walk consults the enum table, and the
    # engine is the source for it.
    ENUMS.ensure(session)
    online = session.probe()
    mods_list = None
    if online:
        try:
            resp = session.call("mods.list")
            if resp.get("ok"):
                mods_list = resp["data"]
            else:
                degraded.append("mods.list failed: %s" % resp.get("error"))
        except session.bridge.BridgeError as e:
            degraded.append("bridge stopped answering: %s" % e)
            online = False

    if mods_list is not None:
        use_mods = mods_list.get("useMods",
                                 mods_list["templates"].get("useMods"))
        if not use_mods:
            warnings.append(_finding(
                warning="the 'use mods' setting is off; JSON and localization "
                        "mods are being ignored by the game",
                nextStep="tick 'use mods' in the in-game Mods menu and restart"))
        mods = collect_mods_bridge(mods_list, only_mod)
        index = build_field_index(collect_mods_bridge(mods_list, None))
    else:
        degraded.append(
            "bridge unreachable (%s); merged in-engine values were not "
            "verified -- static file analysis only. Start the game and rerun "
            "for the full merge check." % (session.error or "game not running"))
        mods = collect_mods_disk(only_mod)
        index = build_field_index(collect_mods_disk(None))
        use_mods = _profile_use_mods()
        if use_mods is False:
            warnings.append(_finding(
                warning="PlayerOptions.TIProfile has UseMods:False; JSON and "
                        "localization mods will be ignored at next launch",
                nextStep="tick 'use mods' in the in-game Mods menu"))

    fields_static = 0
    for mod in mods.values():
        fields_static += _merge_offline_mod(mod, verdicts, warnings, lints)

    fields_engine = 0
    if mods_list is not None:
        try:
            fields_engine = _merge_online(session, mods, index, verbose,
                                          verdicts, warnings, lints)
            _merge_dll_online(session, mods_list, only_mod, verdicts, warnings)
        except session.bridge.BridgeError as e:
            degraded.append(
                "bridge stopped answering mid-run: %s -- the game's main "
                "thread is probably wedged inside a query; it will need a "
                "restart" % e)

    degraded.extend(ENUMS.notes())
    # DISABLED is the data asking for it (TemplateManager drops an entry
    # whose merged disable is true), not a merge failure.
    problems = [v for v in verdicts
                if v.get("verdict") not in ("OK", "DISABLED")]
    summary = {
        "mods": sorted(mods),
        "modCount": len(mods),
        "fieldsComparedInEngine": fields_engine,
        "fieldsExaminedStatically": fields_static,
        "problems": len(problems),
        "warnings": len(warnings),
        "engine": "in-engine" if mods_list is not None else "static-only",
    }
    summary.update(_enum_meta())
    if degraded:
        summary["degraded"] = degraded
    if only_mod and not mods:
        summary["note"] = "%r matched no enabled template mod" % only_mod
    status = _status(problems, warnings, degraded)
    return _check_result(status, summary, verdicts, warnings, lints, verbose)


def _profile_use_mods():
    path = os.path.join(SAVES_DIR, "PlayerOptions.TIProfile")
    try:
        with open(path, errors="replace") as f:
            for line in f:
                if line.startswith("UseMods:"):
                    return line.strip().split(":", 1)[1] == "True"
    except OSError:
        pass
    return None


# ---------------------------------------------------------------- refs check

# field -> referenced template class(es); "moduleName" unions resolve weapon
# and utility modules across their template files.
WEAPON_CLASSES = ("TILaserWeaponTemplate", "TIMissileTemplate", "TIGunTemplate",
                  "TIMagneticGunTemplate", "TIParticleWeaponTemplate",
                  "TIPlasmaWeaponTemplate")
MODULE_CLASSES = WEAPON_CLASSES + ("TIUtilityModuleTemplate",
                                   "TIBatteryTemplate", "TIHeatSinkTemplate")

REF_RULES = {
    "TITechTemplate": {
        "prereqs": ("TITechTemplate",),
        "effects": ("TIEffectTemplate",),
    },
    "TIProjectTemplate": {
        "prereqs": ("TITechTemplate", "TIProjectTemplate"),
        "altPrereq0": ("TITechTemplate", "TIProjectTemplate"),
        "effects": ("TIEffectTemplate",),
        "orgGranted": ("TIOrgTemplate",),
        "factionPrereq": ("TIFactionTemplate",),
        "requiredObjectiveName": ("TIObjectiveTemplate",),
        "altRequiredObjectiveName": ("TIObjectiveTemplate",),
    },
    "TISpaceShipTemplate": {
        "hullName": ("TIShipHullTemplate",),
        "driveName": ("TIDriveTemplate",),
        "powerPlantName": ("TIPowerPlantTemplate",),
        "radiatorName": ("TIRadiatorTemplate",),
        "batteryName": ("TIBatteryTemplate",),
        "factionName": ("TIFactionTemplate",),
    },
}
# Ship weapon/module slot entries and armor, nested one level down.
SHIP_NESTED = {
    "noseWeaponTemplateEntries": ("moduleName", WEAPON_CLASSES),
    "hullWeaponTemplateEntries": ("moduleName", WEAPON_CLASSES),
    "moduleTemplateEntries": ("moduleName", MODULE_CLASSES),
    "noseArmor": ("materialName", ("TIShipArmorTemplate",)),
    "lateralArmor": ("materialName", ("TIShipArmorTemplate",)),
    "tailArmor": ("materialName", ("TIShipArmorTemplate",)),
}
REF_SPECIAL = {"Empty", "None", "none", ""}

# Classes swept for enum-typed field values.
ENUM_SCAN_SKIP = {"TIGlobalConfig"}


def _ref_findings(universe, session=None):
    """All refs findings over a universe. Returns (findings, meta); each
    finding carries a stable `key` for baseline delta."""
    ENUMS.ensure(session)
    findings = []

    def dangling(cls, dn, path, value, targets):
        key = "dangling|%s|%s|%s|%s" % (cls, dn, path, value)
        findings.append(_finding(
            kind="danglingRef", key=key, **{"class": cls}, dataName=dn,
            path=path, value=value, targets=list(targets),
            detail="%s.%s %s references %r, which no %s entry defines"
                   % (cls, dn, path, value, " / ".join(targets)),
            nextStep="fix the spelling, or add the missing entry (check "
                     "disable flags: a disabled entry never registers)"))

    def resolve(value, targets):
        return any(value in universe.names(t) for t in targets)

    for cls, rules in REF_RULES.items():
        bucket = universe.classes.get(cls) or {}
        for dn, entry in bucket.items():
            for field, targets in rules.items():
                v = entry.get(field)
                if v is None:
                    continue
                if isinstance(v, str):
                    if v in REF_SPECIAL or resolve(v, targets):
                        continue
                    dangling(cls, dn, field, v, targets)
                elif isinstance(v, list):
                    for i, item in enumerate(v):
                        if not isinstance(item, str):
                            continue
                        if item == "":
                            continue    # index-stub hole, never a dangling ref
                        if item in REF_SPECIAL or resolve(item, targets):
                            continue
                        dangling(cls, dn, "%s[%d]" % (field, i), item, targets)

    ships = universe.classes.get("TISpaceShipTemplate") or {}
    for dn, entry in ships.items():
        for field, (sub, targets) in SHIP_NESTED.items():
            v = entry.get(field)
            items = v if isinstance(v, list) else [v] if isinstance(v, dict) else []
            for i, item in enumerate(items):
                if not isinstance(item, dict):
                    continue
                name = item.get(sub)
                if not isinstance(name, str) or name in REF_SPECIAL:
                    continue
                if not resolve(name, targets):
                    path = "%s[%d].%s" % (field, i, sub) \
                        if isinstance(v, list) else "%s.%s" % (field, sub)
                    dangling("TISpaceShipTemplate", dn, path, name, targets)

    # Tech-tree cycles and unreachable nodes.
    techs = universe.classes.get("TITechTemplate") or {}
    prereqs = {}
    for dn, entry in techs.items():
        p = entry.get("prereqs")
        prereqs[dn] = [x for x in p if isinstance(x, str) and x] \
            if isinstance(p, list) else []
    state = {}      # 0 visiting, 1 ok, 2 bad
    on_cycle = set()
    for start in prereqs:
        if start in state:
            continue
        stack = [(start, iter(prereqs[start]))]
        state[start] = 0
        while stack:
            node, it = stack[-1]
            advanced = False
            for dep in it:
                if dep not in prereqs:
                    continue    # dangling, already reported
                s = state.get(dep)
                if s is None:
                    state[dep] = 0
                    stack.append((dep, iter(prereqs[dep])))
                    advanced = True
                    break
                if s == 0:
                    cyc = [dep]
                    for n, _ in reversed(stack):
                        if n == dep:
                            break
                        cyc.append(n)
                    for n in cyc:
                        on_cycle.add(n)
            if not advanced:
                stack.pop()
                state[node] = 1
    for members in ({tuple(sorted(on_cycle))} if on_cycle else set()):
        key = "cycle|%s" % ",".join(members)
        findings.append(_finding(
            kind="cycle", key=key, **{"class": "TITechTemplate"},
            members=list(members),
            detail="tech prerequisite cycle: %s" % " -> ".join(members),
            nextStep="break the cycle; none of these techs can ever unlock"))

    # Fixpoint: a tech is researchable when every prereq resolves and is
    # itself researchable. A dangling prereq never enters the set, a cycle
    # member is excluded, and their dependents never enter either.
    reachable = set()
    changed = True
    while changed:
        changed = False
        for dn, deps in prereqs.items():
            if dn not in reachable and dn not in on_cycle and \
                    all(d in reachable for d in deps):
                reachable.add(dn)
                changed = True

    for dn in sorted(prereqs):
        if dn not in reachable:
            key = "unreachable|TITechTemplate|%s" % dn
            findings.append(_finding(
                kind="unreachable", key=key, **{"class": "TITechTemplate"},
                dataName=dn,
                detail="tech %s can never be researched (a prerequisite chain "
                       "is missing or cyclic)" % dn,
                nextStep="follow the prereqs list to the dangling or cyclic "
                         "link and fix it"))

    # Enum-typed field values outside the engine's own member sets.
    if ENUMS.source:
        for cls, bucket in universe.classes.items():
            if cls in ENUM_SCAN_SKIP:
                continue
            for dn, entry in bucket.items():
                for field, value in entry.items():
                    if field in ("dataName", "friendlyName"):
                        continue
                    if not isinstance(value, str) or value == "":
                        continue
                    ok = ENUMS.valid_member(cls, field, value)
                    if ok is False:
                        key = "enum|%s|%s|%s|%s" % (cls, dn, field, value)
                        etype = ENUMS.enum_type_for(cls, field)
                        findings.append(_finding(
                            kind="enumValue", key=key, **{"class": cls},
                            dataName=dn, path=field, value=value,
                            detail="%s.%s %s=%r is not a member of enum %s"
                                   % (cls, dn, field, value, etype),
                            nextStep="use one of the member names the engine "
                                     "reports for enum %s (the query.enums "
                                     "verb lists them)" % etype))
    meta = {"techCount": len(techs),
            "refRuleClasses": sorted(set(REF_RULES) | {"TISpaceShipTemplate"})}
    meta.update(_enum_meta())
    return findings, meta


# Bumped whenever a defect could have written findings into a baseline file
# that vanilla alone would not produce: a file at any other value recomputes
# once, the way a game version change does. 1 retires every baseline written
# while the live overlay could still write engine values into the vanilla
# cache, since those runs recorded engine-only findings as pre-existing.
BASELINE_FORMAT = 1


def _load_baseline(version, enum_fingerprint):
    try:
        with open(BASELINE_PATH) as f:
            data = json.load(f)
    except (OSError, ValueError):
        return None
    if data.get("format") != BASELINE_FORMAT:
        return None
    if data.get("gameVersion") != version:
        return None
    # The stored keys include enum| findings, so the baseline is only valid
    # for the enum table it was computed against. A file written before this
    # key existed has no enumFingerprint and recomputes once.
    if data.get("enumFingerprint") != enum_fingerprint:
        return None
    return data


def _compute_baseline(version, session=None):
    """Refs and reach findings over vanilla + DLC only, cached next to the
    module and keyed by game version and enum table. Vanilla scores clean on
    the design intent of these checks; whatever it does trip is noise every
    merge would repeat, so only deltas are reported."""
    ENUMS.ensure(session)
    fingerprint = ENUMS.fingerprint or "none"
    base = _load_baseline(version, fingerprint)
    if base is not None:
        return base, False
    uni = Universe(include_mods=False)
    ref_findings, _ = _ref_findings(uni, session)
    reach_findings, _ = _reach_findings(uni)
    base = {
        "format": BASELINE_FORMAT,
        "gameVersion": version,
        "enumFingerprint": fingerprint,
        "created": time.strftime("%Y-%m-%dT%H:%M:%S"),
        "refs": sorted(f["key"] for f in ref_findings),
        "reach": sorted(f["key"] for f in reach_findings),
    }
    try:
        with open(BASELINE_PATH, "w") as f:
            json.dump(base, f, indent=1)
    except OSError:
        pass
    return base, True


def check_refs(session, only_mod, scenario, verbose):
    verdicts, warnings, lints = [], [], []
    degraded = []
    online = session.probe()
    version = session.version.get("game") if online else None
    version = version or _game_version()

    source = "bridge" if online and session.serves("query.template") \
        else "disk"

    uni = Universe(include_mods=True, only_mod=None)
    for fe in uni.file_errors:
        if fe["source"].startswith("mod:"):
            warnings.append(_finding(
                mod=fe["source"][4:], file=fe["file"],
                warning="unparseable, excluded from the reference graph: %s"
                        % fe["error"]))

    if source == "bridge":
        _overlay_bridge_bulk(session, uni, degraded)
    else:
        degraded.append(
            "bridge unreachable; the reference graph was computed from a "
            "disk-side merge simulation (vanilla + DLC + enabled mods). "
            "In-engine merged values may differ; rerun with the game up.")

    if scenario:
        info = uni.scenarios.get(scenario)
        if info is None:
            warnings.append(_finding(
                warning="unknown scenario %r; known: %s"
                        % (scenario, ", ".join(sorted(uni.scenarios))),
                nextStep="pass one of the scenario dataNames"))
        else:
            # Scope the universe to entries live under this scenario's tags.
            stags = set(info["tags"])
            for cls, bucket in uni.classes.items():
                dead = [dn for dn in bucket
                        if not uni.live_for_scenario(cls, dn, stags)]
                for dn in dead:
                    del bucket[dn]

    baseline, fresh = _compute_baseline(version, session)
    findings, meta = _ref_findings(uni, session)
    degraded.extend(ENUMS.notes())
    suppressed = 0
    for f in findings:
        if f["key"] in set(baseline["refs"]):
            suppressed += 1
            continue
        if only_mod:
            touched = uni.mod_touched.get((f.get("class"), f.get("dataName")), [])
            if only_mod not in touched:
                continue
        f.pop("key", None)
        if f["kind"] in ("danglingRef", "enumValue", "cycle", "unreachable"):
            verdicts.append(f)

    problems = len(verdicts)
    summary = {
        "source": source,
        "gameVersion": version,
        "baseline": {"path": BASELINE_PATH, "gameVersion": version,
                     "recomputed": fresh,
                     "suppressedVanillaFindings": suppressed},
        "problems": problems,
        "scenario": scenario,
    }
    summary.update(meta)
    if degraded:
        summary["degraded"] = degraded
    status = _status(problems, warnings, degraded)
    return _check_result(status, summary, verdicts, warnings, lints, verbose)


def _bulk_fields_for(cls):
    fields = set()
    for f in (REF_RULES.get(cls) or {}):
        fields.add(f)
    if cls == "TISpaceShipTemplate":
        fields.update(SHIP_NESTED)
    return sorted(fields)


def _overlay_bridge_bulk(session, uni, degraded):
    """Replace the disk-simulated values of the ref-checked fields with the
    engine's own merged values, one bulk call per class."""
    for cls in list(REF_RULES):
        fields = _bulk_fields_for(cls)
        offset = 0
        try:
            while True:
                resp = session.call("query.template", type=cls, fields=fields,
                                    limit=500, offset=offset)
                if not resp.get("ok"):
                    degraded.append("query.template bulk %s failed: %s"
                                    % (cls, resp.get("error")))
                    break
                data = resp["data"]
                entries = data.get("entries") or []
                bucket = uni.classes.setdefault(cls, {})
                for e in entries:
                    dn = e.get("dataName")
                    if not dn:
                        continue
                    # A copy, so the overlay's engine-sourced values are
                    # written into this universe and nowhere else.
                    merged = dict(bucket.get(dn) or {"dataName": dn})
                    for f in fields:
                        if f in e:
                            merged[f] = e[f]
                    bucket[dn] = merged
                offset += len(entries)
                if not data.get("truncated") or not entries:
                    break
        except session.bridge.BridgeError as e:
            degraded.append("bridge stopped answering during bulk %s: %s"
                            % (cls, e))
            return



# ---------------------------------------------------------------- locale check

_LOC_KEY_RE = re.compile(r"^([A-Za-z0-9_]+)\.([A-Za-z0-9_]+)\.(.+)$")


def _parse_loc_file(path):
    """{key: value} from a key=value localization file. Returns (keys, error);
    an unreadable file reads downstream as "localizes nothing", so the caller
    has to be able to tell the two apart."""
    out = {}
    try:
        with open(path, encoding="utf-8-sig", errors="replace") as f:
            for line in f:
                line = line.rstrip("\r\n")
                if not line or line.startswith("//") or "=" not in line:
                    continue
                k, v = line.split("=", 1)
                out[k.strip()] = v
    except OSError as e:
        # str(e) already names the file for open() failures.
        return {}, str(e)
    return out, None


def check_locale(session, only_mod, scenario, verbose):
    verdicts, warnings, lints = [], [], []
    degraded = []
    mods = collect_mods_disk(only_mod)
    uni = Universe(include_mods=True)
    postfix = None
    if scenario:
        info = uni.scenarios.get(scenario)
        if info is None:
            warnings.append(_finding(
                warning="unknown scenario %r; known: %s"
                        % (scenario, ", ".join(sorted(uni.scenarios)))))
        else:
            postfix = info.get("postfix")

    online = session.probe() and session.serves("query.localize")
    if not online:
        degraded.append(
            "query.localize verb unavailable (%s); resolution and fallback "
            "checks skipped -- only phantom-key and file-shape checks ran. "
            "Mod and scenario localization never merges to disk, so the "
            "engine is the only truthful source."
            % ("bridge down" if not session.probe() else "DLL does not serve it"))

    checked_keys = 0
    for mod in mods.values():
        # dataNames this mod added or touched, per template class.
        touched = {}
        for mf in mod.files:
            for dn in mf.entries:
                touched.setdefault(mf.template_class, set()).add(dn)

        langs = set()
        mod_keys = {}
        for lf in mod.loc_files:
            m = LOCALIZATION_RE.search(lf)
            if m:
                langs.add(m.group(1).lower())
            if lf.lower().endswith(".en"):
                keys, err = _parse_loc_file(lf)
                if err:
                    warnings.append(_finding(
                        mod=mod.name,
                        warning="unreadable localization file: %s; its keys "
                                "are absent from this report" % err))
                mod_keys.update(keys)
        if langs and langs == {"en"}:
            lints.append(_finding(
                mod=mod.name, lint="enOnly",
                detail="ships only .en localization; other languages fall "
                       "back to the key or vanilla text"))

        # Phantom keys: localized, but no template entry behind them.
        for key in mod_keys:
            m = _LOC_KEY_RE.match(key)
            if not m:
                continue
            cls, field, dn = m.groups()
            if cls not in uni.classes:
                continue    # not a template-keyed line
            base_dn = dn
            for sc in uni.scenarios.values():
                pf = sc.get("postfix")
                if pf and base_dn.endswith(pf):
                    base_dn = base_dn[:-len(pf)]
                    break
            known = uni.classes[cls]
            # Two vanilla key shapes the matcher misreads: objectives carry a
            # faction suffix (<Class>.<field>.<dataName>.<Faction>) and
            # narrative events are written field-last
            # (<Class>.<dataName>.<field>).
            if base_dn.rpartition(".")[0] in known or field in known:
                continue
            if base_dn not in known:
                verdicts.append(_finding(
                    mod=mod.name, verdict="MISMATCH", kind="phantomKey",
                    key=key,
                    detail="localization key %r has no backing %s entry"
                           % (key, cls),
                    nextStep="fix the dataName in the key, or remove the line"))

        if not touched:
            continue
        # Classes vanilla never localizes (no <Class>.en ships with the game,
        # e.g. TISpaceShipTemplate, whose friendlyName lives in the JSON) are
        # not key-checked.
        touched = {cls: dns for cls, dns in touched.items()
                   if _class_is_localized(cls)}

        # Differential coverage through the engine.
        if online:
            want = []
            for cls, dns in sorted(touched.items()):
                for dn in sorted(dns):
                    for field in ("displayName", "description"):
                        want.append((mod.name, cls, dn, field,
                                     "%s.%s.%s" % (cls, field, dn)))
            for i in range(0, len(want), 100):
                chunk = want[i:i + 100]
                keys = [w[4] for w in chunk]
                if postfix:
                    keys += [k + postfix for k in keys]
                try:
                    resp = session.call("query.localize", keys=keys)
                except session.bridge.BridgeError as e:
                    degraded.append("bridge stopped answering: %s" % e)
                    online = False
                    break
                if not resp.get("ok"):
                    degraded.append("query.localize failed: %s"
                                    % resp.get("error"))
                    break
                got = {r.get("key"): r for r in
                       (resp["data"] if isinstance(resp["data"], list)
                        else resp["data"].get("results") or [])}
                for mname, cls, dn, field, key in chunk:
                    checked_keys += 1
                    r = got.get(key)
                    if r is None:
                        continue
                    if r.get("fellBack"):
                        sev = "MISMATCH" if field == "displayName" else "WARN"
                        item = _finding(
                            mod=mname, verdict=sev, kind="fellBack", key=key,
                            detail="%s did not resolve; the engine fell back"
                                   % key,
                            nextStep="add the key to the mod's .en file (and "
                                     "the scenario-postfixed variant when the "
                                     "entry is scenario content)")
                        (verdicts if sev == "MISMATCH" else warnings).append(
                            item if sev == "MISMATCH" else
                            _finding(mod=mname, warning=item["detail"], key=key,
                                     nextStep=item["nextStep"]))
                    if postfix:
                        pr = got.get(key + postfix)
                        if pr is not None and pr.get("fellBack"):
                            warnings.append(_finding(
                                mod=mname, key=key + postfix,
                                warning="scenario-postfixed key fell back to "
                                        "the base text under %s" % scenario,
                                nextStep="add the postfixed key if the "
                                         "scenario needs different text"))
        else:
            # Offline approximation: keys present in the mod's own .en file.
            for cls, dns in sorted(touched.items()):
                van, _ = vanilla_entries(cls + ".json")
                van_loc = _vanilla_loc_keys(cls)
                for dn in sorted(dns):
                    new_entry = van is not None and dn not in van
                    key = "%s.displayName.%s" % (cls, dn)
                    checked_keys += 1
                    if new_entry and key not in mod_keys and \
                            key not in van_loc:
                        verdicts.append(_finding(
                            mod=mod.name, verdict="MISMATCH", kind="missingKey",
                            key=key,
                            detail="new entry %s/%s has no displayName key in "
                                   "the mod's .en file or vanilla" % (cls, dn),
                            nextStep="add %s=... to the mod's .en file" % key))
                    if postfix:
                        pkey = key + postfix
                        if pkey in mod_keys and key not in mod_keys and \
                                key not in van_loc:
                            warnings.append(_finding(
                                mod=mod.name, key=pkey,
                                warning="postfixed key present but the base "
                                        "key resolves nowhere on disk"))
                        if new_entry and key in mod_keys and \
                                pkey not in mod_keys:
                            lints.append(_finding(
                                mod=mod.name, lint="postfixGap", key=pkey,
                                detail="no scenario-postfixed variant for %s "
                                       "under %s; the base text will serve"
                                       % (key, scenario)))

    problems = len(verdicts)
    summary = {
        "mods": sorted(mods),
        "keysChecked": checked_keys,
        "problems": problems,
        "scenario": scenario,
        "scenarioPostfix": postfix,
        "engine": "in-engine" if online else "disk-approximation",
    }
    if degraded:
        summary["degraded"] = degraded
    status = _status(problems, warnings, degraded)
    return _check_result(status, summary, verdicts, warnings, lints, verbose)


def _class_is_localized(cls):
    return os.path.exists(os.path.join(
        ROOT, "TerraInvicta_Data", "StreamingAssets", "Localization", "en",
        cls + ".en"))


_VAN_LOC_CACHE = {}


def _vanilla_loc_keys(cls):
    """The vanilla en localization keys for one template class."""
    if cls in _VAN_LOC_CACHE:
        return _VAN_LOC_CACHE[cls]
    path = os.path.join(ROOT, "TerraInvicta_Data", "StreamingAssets",
                        "Localization", "en", cls + ".en")
    pairs, _err = _parse_loc_file(path)
    keys = set(pairs)
    # DLC localization is a second source.
    for label, tdir in dlc_template_dirs():
        loc = os.path.join(os.path.dirname(tdir), "..", "Localization", "en",
                           cls + ".en")
        loc = os.path.normpath(loc)
        if os.path.exists(loc):
            pairs, _err = _parse_loc_file(loc)
            keys.update(pairs)
    _VAN_LOC_CACHE[cls] = keys
    return keys


# ---------------------------------------------------------------- conflicts

def check_conflicts(session, only_mod, scenario, verbose):
    verdicts, warnings, lints = [], [], []
    degraded = []

    dupes_source = "file-level"
    if session.probe() and "query.templateDupes" in session.served:
        dupes_source = "engine"

    mods = collect_mods_disk(None)
    index = build_field_index(mods)
    whole_replacers = {}    # file lower -> [mod names]
    for mod in mods.values():
        for mf in mod.files:
            if mf.whole_replace:
                whole_replacers.setdefault(mf.filename.lower(), []).append(mod.name)

    pair_count = 0
    for (cls, dn, field), setters in sorted(index.items()):
        by_mod = {}
        for mname, order, value, mf in setters:
            by_mod[mname] = (order, value, mf)
        if len(by_mod) < 2:
            continue
        if only_mod and only_mod not in by_mod:
            continue
        pair_count += 1
        names = sorted(by_mod)
        values = [by_mod[n][1] for n in names]
        orders = {n: by_mod[n][0] for n in names}
        modes = {n: by_mod[n][2].array_mode for n in names}
        fname = next(iter(by_mod.values()))[2].filename

        all_equal = all(json.dumps(v, sort_keys=True) ==
                        json.dumps(values[0], sort_keys=True) for v in values)
        replaced_by = whole_replacers.get(fname.lower(), [])
        is_array = any(isinstance(v, list) for v in values)
        concat = is_array and all(modes[n] == "concat" for n in names)

        if replaced_by:
            severity = "high"
            detail = ("%s/%s.%s: %s lists %s in TemplatesToReplace, which "
                      "discards every other mod's changes to the file"
                      % (cls, dn, field, ", ".join(replaced_by), fname))
            next_step = ("only one mod can own a replaced file; drop the "
                         "TemplatesToReplace listing or the other mods' edits")
        elif concat:
            severity = "info"
            detail = ("%s/%s.%s: both mods concatenate this array "
                      "(TemplatesToConcatArrays); their elements coexist"
                      % (cls, dn, field))
            next_step = "nothing to do unless the combined list misbehaves"
        elif all_equal:
            severity = "low"
            detail = ("%s/%s.%s written identically by %s; redundant but "
                      "harmless" % (cls, dn, field, ", ".join(names)))
            next_step = "drop the duplicate write from one mod"
        else:
            same_order = len({orders[n] for n in names}) < len(names)
            severity = "high" if same_order else "medium"
            winner = max(names, key=lambda n: orders[n])
            overlap = ""
            if is_array and all(isinstance(v, list) for v in values):
                lo = min(len(v) for v in values)
                idx = [i for i in range(lo)
                       if not all(is_blank(v[i]) for v in values)]
                if idx:
                    overlap = "; overlapping array index(es) %s" % idx[:10]
            detail = ("%s/%s.%s written differently by %s%s; "
                      "%s" % (cls, dn, field, ", ".join(
                          "%s (LoadOrder %s)" % (n, orders[n]) for n in names),
                          overlap,
                          "LoadOrder ties -- the winner is undefined"
                          if same_order else
                          "%s wins by LoadOrder" % winner))
            next_step = ("set distinct LoadOrders so the intended mod wins"
                         + (", or add %s to TemplatesToConcatArrays in both "
                            "mods if the array elements should coexist" % fname
                            if is_array else ""))
        item = _finding(**{"class": cls}, dataName=dn, field=field,
                        file=fname, mods=names, severity=severity,
                        detail=detail, nextStep=next_step)
        if severity == "high":
            item["verdict"] = "MISMATCH"
            verdicts.append(item)
        elif severity == "medium":
            warnings.append(_finding(warning=detail, **{
                "class": cls}, dataName=dn, field=field, mods=names,
                severity=severity, nextStep=next_step))
        else:
            lints.append(_finding(lint="overlap", severity=severity,
                                  detail=detail, mods=names,
                                  nextStep=next_step))

    # Cross-source duplicates: metatemplates/vanilla/DLC first-registered-wins,
    # scenario-tag semantics.
    uni = Universe(include_mods=True)
    if dupes_source == "engine":
        try:
            resp = session.call("query.templateDupes")
            if resp.get("ok"):
                rows = resp["data"] if isinstance(resp["data"], list) else \
                    resp["data"].get("duplicates") or []
                for row in rows:
                    cands = row.get("candidates") or []
                    dead = [c for c in cands if not c.get("live")]
                    for _ in dead:
                        lints.append(_finding(
                            lint="engineDupe", **{"class": row.get("type")},
                            dataName=row.get("dataName"),
                            detail="a parked registration lost to the live one"))
            else:
                dupes_source = "file-level"
                degraded.append("query.templateDupes failed: %s"
                                % resp.get("error"))
        except session.bridge.BridgeError as e:
            dupes_source = "file-level"
            degraded.append("bridge stopped answering: %s" % e)
    variant_only_count = 0
    if dupes_source == "file-level":
        if session.probe() is False:
            degraded.append(
                "engine duplicate/shadow tables unavailable (bridge down); "
                "collisions below come from file-level analysis of vanilla, "
                "DLC_Content and enabled mods, which cannot see what the "
                "loader actually kept")
    for col in uni.collisions:
        wt, lt = set(col["winnerTags"]), set(col["loserTags"])
        base = {"class": col["class"]}
        if wt == lt:
            if not wt and col["winner"] == "vanilla":
                lints.append(_finding(
                    lint="silentDrop", **base, dataName=col["dataName"],
                    detail="untagged %s entry in %s collides with untagged "
                           "vanilla; first-registered-wins drops it silently"
                           % (col["class"], col["loser"]),
                    nextStep="rename the entry, or tag it for a scenario"))
            else:
                verdicts.append(_finding(
                    verdict="SHADOWED", **base, dataName=col["dataName"],
                    detail="(%s, %s) registered by both %s and %s with equal "
                           "scenarioTags %s; first registration wins, %s's "
                           "copy is dead"
                           % (col["class"], col["dataName"], col["winner"],
                              col["loser"], sorted(wt), col["loser"]),
                    nextStep="rename the loser's dataName or change its "
                             "scenarioTags"))
        else:
            # Legal scenario variants. Reported per scenario when one is
            # named; otherwise only when a mod is party to the collision --
            # vanilla-vs-DLC variants are the shipped design, not findings.
            mod_involved = (col["class"], col["dataName"]) in uni.mod_touched
            if not scenario and not mod_involved:
                variant_only_count += 1
                continue
            item = _finding(
                lint="scenarioVariant", **base, dataName=col["dataName"],
                detail="(%s, %s) has variants from %s (tags %s) and %s "
                       "(tags %s); they coexist until campaign start, when "
                       "resolution keeps entries whose tags are a subset of "
                       "the scenario's and ranks by tag intersection"
                       % (col["class"], col["dataName"], col["winner"],
                          sorted(wt), col["loser"], sorted(lt)))
            lints.append(item)
        unknown = (wt | lt) - uni.declared_tags
        for tag in sorted(unknown):
            lints.append(_finding(
                lint="unreachableTag", **base, dataName=col["dataName"],
                detail="scenarioTag %r is declared by no scenario; entries "
                       "carrying it are unreachable content" % tag,
                nextStep="declare the tag on a scenario metatemplate, or "
                         "drop it"))
    # Tags on mod entries that no scenario declares.
    for (cls, dn), tags in uni.tags.items():
        if (cls, dn) in uni.mod_touched:
            for tag in tags:
                if tag not in uni.declared_tags:
                    lints.append(_finding(
                        lint="unreachableTag", **{"class": cls}, dataName=dn,
                        mods=uni.mod_touched.get((cls, dn)),
                        detail="scenarioTag %r on %s/%s is declared by no "
                               "scenario; the entry can never resolve into a "
                               "campaign" % (tag, cls, dn),
                        nextStep="use a declared tag (%s) or add a scenario "
                                 "declaring this one"
                                 % ", ".join(sorted(uni.declared_tags))))

    problems = len(verdicts)
    summary = {
        "modsExamined": sorted(mods),
        "contestedFields": pair_count,
        "crossSourceCollisions": len(uni.collisions),
        "vanillaDlcVariantsNotShown": variant_only_count,
        "dupeSource": dupes_source,
        "problems": problems,
        "declaredScenarioTags": sorted(uni.declared_tags),
    }
    if degraded:
        summary["degraded"] = degraded
    status = _status(problems, warnings, degraded)
    return _check_result(status, summary, verdicts, warnings, lints, verbose)


# ---------------------------------------------------------------- reach check

def _reach_findings(universe):
    """Gate-list findings over a universe, keyed for baseline delta."""
    findings = []

    # Org registration: an org exists only through a TIMetaTemplate entry of
    # templateType TIOrgTemplate listing it.
    gate_lists = {}
    for dn, entry in (universe.classes.get("TIMetaTemplate") or {}).items():
        if entry.get("templateType") == "TIOrgTemplate":
            gate_lists[dn] = [x for x in (entry.get("templateNames") or [])
                              if isinstance(x, str) and x]
    registered = set()
    for names in gate_lists.values():
        registered.update(names)
    # Orgs granted outside the gate lists are still reachable: project
    # orgGranted, and the factions' own spaceOrg / winningOrg.
    granted = set()
    for entry in (universe.classes.get("TIProjectTemplate") or {}).values():
        og = entry.get("orgGranted")
        if isinstance(og, str) and og:
            granted.add(og)
    for entry in (universe.classes.get("TIFactionTemplate") or {}).values():
        for f in ("spaceOrg", "winningOrg"):
            v = entry.get(f)
            if isinstance(v, str) and v:
                granted.add(v)
    for dn in sorted(universe.classes.get("TIOrgTemplate") or {}):
        if dn in registered or dn in granted:
            continue
        findings.append(_finding(
            kind="orgUnregistered", key="org|%s" % dn,
            **{"class": "TIOrgTemplate"}, dataName=dn,
            detail="org %s is in TIOrgTemplate but absent from every "
                   "TIMetaTemplate org registration list and granted by no "
                   "project or faction; it can never appear in a campaign" % dn,
            nextStep="add the dataName to an org metatemplate list (%s) via "
                     "a TIMetaTemplate.json mod file with the list restated "
                     "or concatenated" % ", ".join(sorted(gate_lists))))

    # Councilor appearances with an empty gate can never be picked.
    for dn, entry in sorted((universe.classes.get(
            "TICouncilorAppearanceTemplate") or {}).items()):
        for gate in ("allowedGenders", "allowedAncestries", "allowedJobNames"):
            v = entry.get(gate)
            if isinstance(v, list) and not [x for x in v if not is_blank(x)]:
                findings.append(_finding(
                    kind="emptyGate", key="appearance|%s|%s" % (dn, gate),
                    **{"class": "TICouncilorAppearanceTemplate"}, dataName=dn,
                    path=gate,
                    detail="appearance %s has an empty %s; no councilor can "
                           "ever roll it" % (dn, gate),
                    nextStep="fill the list, or remove the appearance entry"))
    return findings, gate_lists


def check_reach(session, only_mod, scenario, verbose):
    verdicts, warnings, lints = [], [], []
    degraded = []
    # The live engine first, as in check_refs: the baseline key must not be
    # read off disk state that _compute_baseline is about to rewrite.
    online = session.probe()
    version = (session.version.get("game") if online else None) \
        or _game_version()
    uni = Universe(include_mods=True)
    if scenario and scenario in uni.scenarios:
        stags = set(uni.scenarios[scenario]["tags"])
        for cls, bucket in uni.classes.items():
            for dn in [d for d in bucket
                       if not uni.live_for_scenario(cls, d, stags)]:
                del bucket[dn]
    if not online:
        degraded.append(
            "computed from the disk-side merge simulation; the engine's own "
            "registration state was not consulted (game not running)")

    baseline, fresh = _compute_baseline(version, session)
    findings, gate_lists = _reach_findings(uni)
    suppressed = 0
    for f in findings:
        if f["key"] in set(baseline["reach"]):
            suppressed += 1
            continue
        if only_mod:
            touched = uni.mod_touched.get((f.get("class"), f.get("dataName")), [])
            if only_mod not in touched:
                continue
        f.pop("key", None)
        verdicts.append(f)

    problems = len(verdicts)
    summary = {
        "gateListsUsed": {dn: len(names) for dn, names in sorted(
            gate_lists.items())},
        "checksRun": ["TIMetaTemplate org registration lists",
                      "TICouncilorAppearanceTemplate allowedGenders/"
                      "allowedAncestries/allowedJobNames"],
        "baseline": {"path": BASELINE_PATH, "gameVersion": version,
                     "recomputed": fresh,
                     "suppressedVanillaFindings": suppressed},
        "problems": problems,
        "scenario": scenario,
    }
    if degraded:
        summary["degraded"] = degraded
    status = _status(problems, warnings, degraded)
    return _check_result(status, summary, verdicts, warnings, lints, verbose)


# ---------------------------------------------------------------- save_check

# Both formats the game writes: a profile setting decides which, and the DLL
# lists and resolves saves by the same pair (SaveExtensions in src/Verbs.cs).
# Reading only .gz here made a save the game had just written invisible.
SAVE_EXTENSIONS = (".gz", ".json")


def _list_saves():
    """Save file names, newest first, in either format."""
    try:
        return sorted(
            (f for f in os.listdir(SAVES_DIR)
             if f.endswith(SAVE_EXTENSIONS)),
            key=lambda f: os.path.getmtime(os.path.join(SAVES_DIR, f)),
            reverse=True)
    except OSError:
        return []


def _resolve_save(name, saves):
    """The save `name` refers to, or None. Accepts the file name as it sits on
    disk and the bare name the game shows, which is the same name for a `.gz`
    and a `.json` save; `saves` is newest first, so a bare name that matches
    both resolves to the one written last."""
    if name in saves:
        return name
    for f in saves:
        base, ext = os.path.splitext(f)
        if ext in SAVE_EXTENSIONS and base == name:
            return f
    return None


def save_check(name=None):
    """Offline save compatibility: the save's templateName references and
    TIMetadataState metadata vs the merged template universe."""
    verdicts, warnings, lints = [], [], []
    saves = _list_saves()
    if not saves:
        return _check_result("FAIL", {"error": "no saves found in %s"
                                      % SAVES_DIR}, [], [], [], True)
    if name:
        fname = _resolve_save(name, saves)
        if fname is None:
            return _check_result("FAIL", {"error": "no save named %r" % name,
                                          "saves": saves[:20]}, [], [], [], True)
    else:
        fname = saves[0]
    path = os.path.join(SAVES_DIR, fname)
    # An uncompressed save is plain JSON on disk; only the .gz form goes
    # through gzip.
    opener = gzip.open if fname.endswith(".gz") else open
    try:
        with opener(path, "rt", encoding="utf-8-sig", errors="replace") as f:
            data = json.load(f)
    except (OSError, ValueError, EOFError) as e:
        return _check_result("FAIL", {"error": "could not read %s: %s"
                                      % (fname, e)}, [], [], [], True)

    gamestates = data.get("gamestates") or {}
    meta = {}
    for k, v in gamestates.items():
        if k.endswith("TIMetadataState") and isinstance(v, list) and v:
            meta = v[0].get("Value") or {}
            break

    # Metadata checks: run with or without the game.
    installed_dlc = sorted(os.listdir(DLC_DIR)) if os.path.isdir(DLC_DIR) else []
    required_dlc = meta.get("requiredDLC") or []
    norm = lambda s: re.sub(r"[^a-z0-9]", "", str(s).lower())
    installed_norm = {norm(d) for d in installed_dlc}
    for dlc in required_dlc:
        if norm(dlc) not in installed_norm:
            verdicts.append(_finding(
                verdict="MISSING", kind="dlc", value=dlc,
                detail="save requires DLC %r, which is not in DLC_Content/"
                       % dlc,
                nextStep="install the DLC before loading this save"))
    use_mods = _profile_use_mods()
    if meta.get("playedWithMods") and use_mods is False:
        warnings.append(_finding(
            warning="save was played with mods but the profile has "
                    "UseMods:False; template mods will be missing on load",
            nextStep="tick 'use mods' in the in-game Mods menu first"))

    # templateName references vs the merged universe.
    refs = set()

    def walk(o):
        if isinstance(o, dict):
            for k, v in o.items():
                if k == "templateName" and isinstance(v, str) and v:
                    refs.add(v)
                else:
                    walk(v)
        elif isinstance(o, list):
            for v in o:
                walk(v)

    walk(gamestates)
    uni = Universe(include_mods=True)
    known = uni.all_data_names()
    # Runtime-derived names: the game clones each faction's ship designs as
    # "<design>_<faction>" at campaign start. Such a ref resolves when both
    # halves do.
    factions = uni.names("TIFactionTemplate")
    # Names the engine builds in code, never present in any template file
    # (ldstr constants in Assembly-CSharp: runtime ship/fleet instancing).
    runtime_re = re.compile(
        r"^GenericFleetTemplate$|ShipTemplate\d*$|^importedShipTemplate|"
        r"^playerShipTemplate")
    derived = 0
    dangling = []
    for dn in sorted(refs - known):
        base, sep, fac = dn.rpartition("_")
        if sep and fac in factions and base in known:
            derived += 1
            refs.add(base)      # the base design is what a mod provides
            continue
        if runtime_re.search(dn):
            derived += 1
            continue
        dangling.append(dn)

    # Name what dangles per disabled mod, and what each enabled mod uniquely
    # provides (what would dangle if that mod were removed).
    disabled_defs = _mod_data_names(MODS_DISABLED)
    enabled_defs = _mod_data_names(MODS_ENABLED)
    vanilla_dlc = Universe(include_mods=False).all_data_names()
    for dn in dangling:
        providers = sorted(m for m, names in disabled_defs.items()
                           if dn in names)
        verdicts.append(_finding(
            verdict="MISSING", kind="templateRef", value=dn,
            detail="save references template %r, which the merged universe "
                   "does not define%s"
                   % (dn, "; defined by disabled mod(s): %s"
                      % ", ".join(providers) if providers else ""),
            nextStep=("re-enable %s before loading" % ", ".join(providers))
            if providers else
            "the defining mod is gone entirely; the save will lose or crash "
            "on these objects"))
    at_risk = {}
    for m, names in enabled_defs.items():
        unique = (names - vanilla_dlc) & refs
        if unique:
            at_risk[m] = sorted(unique)
    if at_risk:
        lints.append(_finding(
            lint="modDependencies",
            detail="the save references mod-added templates; disabling these "
                   "mods dangles them",
            perMod={m: v[:20] for m, v in sorted(at_risk.items())},
            nextStep="keep these mods enabled for this campaign"))

    problems = len(verdicts)
    summary = {
        "save": fname,
        "savedAt": time.strftime("%Y-%m-%dT%H:%M:%S",
                                 time.localtime(os.path.getmtime(path))),
        "playerFaction": meta.get("playerFactionName"),
        "gameTime": meta.get("gameTimeString"),
        "playedWithMods": meta.get("playedWithMods"),
        "requiredDLC": required_dlc,
        "installedDLC": installed_dlc,
        "templateRefs": len(refs),
        "runtimeDerivedRefs": derived,
        "danglingRefs": len(dangling),
        "problems": problems,
    }
    return _check_result(_status(problems, warnings, []), summary, verdicts,
                         warnings, lints, True)


def _mod_data_names(mods_dir):
    """mod folder name -> set of dataNames its template files define."""
    out = {}
    try:
        folders = sorted(os.listdir(mods_dir))
    except OSError:
        return out
    for name in folders:
        folder = os.path.join(mods_dir, name)
        if not os.path.isdir(folder):
            continue
        names = set()
        for n in os.listdir(folder):
            if not n.endswith(".json") or n == "ModInfo.json":
                continue
            entries, err = load_entries(os.path.join(folder, n))
            if entries:
                names.update(entries)
        if names:
            out[name] = names
    return out


# ---------------------------------------------------------------- workshop

_OUTDATED_RE = re.compile(
    r"(outdated|deprecated|abandoned|no longer (?:works|work|maintained|"
    r"updated|supported)|not (?:been )?(?:updated|maintained)|"
    r"broken (?:on|in|with|since))", re.I)


def workshop_status(mod=None):
    """Each subscribed mod's WorkshopItemInfo.xml vs upstream Steam Workshop
    state. Network failure degrades to a warning."""
    verdicts, warnings, lints = [], [], []
    items = []
    for state, mods_dir in (("enabled", MODS_ENABLED),
                            ("disabled", MODS_DISABLED)):
        try:
            folders = sorted(os.listdir(mods_dir))
        except OSError:
            continue
        for name in folders:
            if mod and name != mod:
                continue
            folder = os.path.join(mods_dir, name)
            xml_path = os.path.join(folder, "WorkshopItemInfo.xml")
            if not os.path.isfile(xml_path):
                continue
            item = {"mod": name, "state": state}
            try:
                root = ET.parse(xml_path).getroot()
                item["publishedFileId"] = (root.findtext("PublishedFileId")
                                           or "").strip()
                item["name"] = (root.findtext("Name") or "").strip()
                desc = root.findtext("Description") or ""
            except ET.ParseError as e:
                warnings.append(_finding(mod=name,
                                         warning="WorkshopItemInfo.xml "
                                                 "unparseable: %s" % e))
                continue
            m = _OUTDATED_RE.search(desc)
            if m:
                item["selfDeclared"] = m.group(1)
            mtime = 0
            for dirpath, _dirs, files in os.walk(folder):
                for f in files:
                    try:
                        mtime = max(mtime, os.path.getmtime(
                            os.path.join(dirpath, f)))
                    except OSError:
                        pass
            item["localMtime"] = int(mtime)
            item["localMtimeText"] = time.strftime(
                "%Y-%m-%d", time.localtime(mtime)) if mtime else None
            items.append(item)

    ids = [i["publishedFileId"] for i in items
           if i.get("publishedFileId", "").isdigit()]
    upstream = {}
    net_error = None
    if ids:
        form = {"itemcount": str(len(ids))}
        for n, fid in enumerate(ids):
            form["publishedfileids[%d]" % n] = fid
        try:
            req = urllib.request.Request(
                WORKSHOP_API,
                data=urllib.parse.urlencode(form).encode(),
                headers={"Content-Type": "application/x-www-form-urlencoded"})
            with urllib.request.urlopen(req, timeout=20) as resp:
                payload = json.loads(resp.read().decode("utf-8", "replace"))
            for d in (payload.get("response", {})
                      .get("publishedfiledetails") or []):
                upstream[str(d.get("publishedfileid"))] = d
        except (urllib.error.URLError, OSError, ValueError) as e:
            net_error = str(e)
            warnings.append(_finding(
                warning="Steam Workshop API unreachable: %s; upstream "
                        "update times and subscriber counts unavailable"
                        % net_error,
                nextStep="rerun with network access for the upstream half"))

    game_version = _game_version()
    now = time.time()
    for item in items:
        fid = item.get("publishedFileId")
        d = upstream.get(fid)
        if d:
            if int(d.get("result", 0)) != 1:
                warnings.append(_finding(
                    mod=item["mod"],
                    warning="workshop item %s no longer resolves (result=%s); "
                            "likely removed or delisted"
                            % (fid, d.get("result")),
                    nextStep="find a replacement; Steam will not restore or "
                             "update this item"))
                item["upstream"] = {"result": d.get("result")}
                continue
            up = {
                "title": d.get("title"),
                "timeUpdated": d.get("time_updated"),
                "timeUpdatedText": time.strftime(
                    "%Y-%m-%d", time.localtime(d["time_updated"]))
                    if d.get("time_updated") else None,
                "subscriptions": d.get("subscriptions"),
                "lifetimeSubscriptions": d.get("lifetime_subscriptions"),
                "views": d.get("views"),
            }
            item["upstream"] = up
            m = _OUTDATED_RE.search(d.get("description") or "")
            if m:
                item["selfDeclared"] = m.group(1)
            tu = d.get("time_updated") or 0
            if tu and item["localMtime"] and tu > item["localMtime"] + 3600:
                verdicts.append(_finding(
                    mod=item["mod"], verdict="MISMATCH", kind="stale",
                    detail="upstream updated %s, local copy dates %s; Steam "
                           "has not synced the update into the game yet"
                           % (up["timeUpdatedText"], item["localMtimeText"]),
                    nextStep="launch Steam/the game so the Workshop item "
                             "re-syncs, then re-test"))
            if tu and now - tu > 2 * 365 * 86400:
                lints.append(_finding(
                    mod=item["mod"], lint="dormant",
                    detail="no upstream update since %s; likely abandoned"
                           % up["timeUpdatedText"]))
        if item.get("selfDeclared"):
            warnings.append(_finding(
                mod=item["mod"],
                warning="description self-declares a problem (%r)"
                        % item["selfDeclared"],
                nextStep="read the workshop page before trusting the mod on "
                         "the current game version"))

    problems = len(verdicts)
    summary = {
        "items": len(items),
        "gameVersion": game_version,
        "upstreamQueried": len(upstream),
        "problems": problems,
    }
    if net_error:
        summary["degraded"] = ["network: %s" % net_error]
    result = {"status": _status(problems, warnings,
                                [net_error] if net_error else []),
              "summary": summary, "verdicts": verdicts, "warnings": warnings,
              "lints": lints, "mods": items}
    return result


# ---------------------------------------------------------------- run

def run(bridge, mod=None, check="all", scenario=None, verbose=False):
    """The modcheck tool's engine. `bridge` is a module-like object exposing
    send(request, timeout, port) and BridgeError."""
    if check in (None, "", "all"):
        selected = list(CHECK_NAMES)
    elif check in CHECK_NAMES:
        selected = [check]
    else:
        return _check_result("ERROR", {"error": "unknown check %r" % check,
                                       "known": list(CHECK_NAMES) + ["all"]},
                             [], [], [], True)

    session = _Session(bridge)
    session.probe()
    checks = {}
    runners = {
        "merge": lambda: check_merge(session, mod, verbose),
        "refs": lambda: check_refs(session, mod, scenario, verbose),
        "locale": lambda: check_locale(session, mod, scenario, verbose),
        "conflicts": lambda: check_conflicts(session, mod, scenario, verbose),
        "reach": lambda: check_reach(session, mod, scenario, verbose),
    }
    for name in selected:
        try:
            checks[name] = runners[name]()
        except Exception as e:  # a check must never take the tool down
            checks[name] = _check_result(
                "ERROR", {"error": "%s: %s" % (type(e).__name__, e)},
                [], [], [], True)

    problems = sum(c["summary"].get("problems", len(c.get("verdicts") or []))
                   for c in checks.values())
    warn_count = sum(len(c.get("warnings") or []) +
                     c["summary"].get("warningsTruncated", 0)
                     for c in checks.values())
    statuses = [c["status"] for c in checks.values()]
    if "ERROR" in statuses:
        overall = "ERROR"
    elif "FAIL" in statuses:
        overall = "FAIL"
    elif "DEGRADED" in statuses:
        overall = "DEGRADED"
    elif "WARN" in statuses:
        overall = "WARN"
    else:
        overall = "PASS"

    return {
        "check": check or "all",
        "mod": mod,
        "scenario": scenario,
        "bridge": {
            "up": bool(session.up),
            "error": session.error,
            "game": session.version.get("game"),
            "servedVerbs": len(session.served) if session.served else None,
        },
        "summary": {
            "status": overall,
            "problems": problems,
            "warnings": warn_count,
            "perCheck": {n: c["status"] for n, c in checks.items()},
        },
        "checks": checks,
    }


# ---------------------------------------------------------------- debug entry

if __name__ == "__main__":
    # Debug entry, not a CLI product: python3 modcheck.py [check] [mod]
    _check = sys.argv[1] if len(sys.argv) > 1 else "all"
    _mod = sys.argv[2] if len(sys.argv) > 2 else None
    if _check == "save_check":
        _out = save_check(_mod)
    elif _check == "workshop_status":
        _out = workshop_status(_mod)
    else:
        _out = run(_bridge_default, mod=_mod, check=_check)
    print(json.dumps(_out, indent=2))
