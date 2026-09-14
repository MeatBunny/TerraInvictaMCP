"""Is the code this process is running the code that is on disk?

The MCP server is a long-lived Python process the client spawns once and
keeps for the whole session. Editing server/*.py changes nothing in it: the
files are read at interpreter start and never again. Every other signal
reports health while that is true -- the DLL build is current, the unit suite
runs the new files, and version agreement passes because the mod version does
not move during development -- so a run can spend hours reading answers
produced by code that was replaced before it began.

The record has to be taken when the code is loaded, not when the check runs.
A check that hashes a file at check time and compares it against that same
file agrees with itself no matter what happened in between, which is the
failure this module exists to catch. So `note()` snapshots each server module
the first time this process is seen holding it, and never re-snapshots; only
`drift()` reads the disk again.

Content hashes, not timestamps: git checkout, a copy, or a formatter rewrite
moves an mtime without changing a byte, and a false stale verdict costs the
same reconnect as a real one.
"""
import hashlib
import os
import sys
import time

# This module's own directory. Only the server's top-level .py files live
# here; server/tests/ is a subdirectory and is deliberately out of scope,
# since the server process never loads a test.
SERVER_DIR = os.path.dirname(os.path.abspath(__file__))

# Absolute path -> sha256 of the bytes this process loaded. Written once per
# path by note().
_RECORDED = {}

_STARTED = time.strftime("%Y-%m-%d %H:%M:%S")

FIX = ("Reconnect this MCP server in the client (Claude Code: /mcp, then "
       "reconnect the terra-invicta server; other clients: restart the "
       "client). Relaunching the game does NOT do it: the server is a "
       "separate Python process the client owns, and a new game process "
       "reattaches to the same stale server.")


def _digest(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def _loaded_files():
    """Absolute paths of this server's own .py files loaded in this process."""
    found = []
    for module in list(sys.modules.values()):
        path = getattr(module, "__file__", None)
        if not path:
            continue
        path = os.path.abspath(path)
        if path.endswith(".py") and os.path.dirname(path) == SERVER_DIR:
            found.append(path)
    return found


def note():
    """Record what is on disk for every loaded server module not seen yet.

    Returns the paths newly recorded. Already-recorded paths are left alone,
    which is the whole point: the recorded value must keep describing load
    time. Called at import and once per tool dispatch, so a module imported
    late is pinned within one call of its import instead of never.
    """
    new = []
    for path in _loaded_files():
        if path in _RECORDED:
            continue
        _RECORDED[path] = _digest(path)
        new.append(path)
    return new


def drift():
    """[(name, why)] for every recorded file that no longer matches, sorted.

    Empty means the running code is the code on disk.
    """
    rows = []
    for path in sorted(_RECORDED):
        name = os.path.join("server", os.path.basename(path))
        try:
            current = _digest(path)
        except OSError as e:
            rows.append((name, "is gone or unreadable now (%s)" % e))
            continue
        if current != _RECORDED[path]:
            rows.append((name, "changed on disk"))
    return rows


def stale():
    """The failure sentence naming every drifted file, or None when clean."""
    rows = drift()
    if not rows:
        return None
    return ("STALE -- %s since this server process loaded it at %s. This "
            "process is still running the OLD code, so every answer it gives "
            "(including this one) describes code that no longer exists on "
            "disk. %s"
            % ("; ".join("%s %s" % (name, why) for name, why in rows),
               _STARTED, FIX))


def check():
    """(line, failed): the verdict sentence and whether it is a failure.

    Both from one pass over the files. A caller that wants the sentence and
    the verdict separately would otherwise hash everything twice.
    """
    bad = stale()
    if bad:
        return bad, True
    return ("current -- %d loaded server file(s) match the bytes this "
            "process read at %s" % (len(_RECORDED), _STARTED)), False
