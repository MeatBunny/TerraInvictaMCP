"""Static reference resources. Nothing here is the only path to something
needed mid-task; the tools carry their own guidance.
"""
import datetime
import json
import os

import bridge
import modcheck

DOCS = os.path.join(bridge.MOD_DIR, "docs")
TEMPLATES_DIR = os.path.join(bridge.GAME_ROOT, "TerraInvicta_Data",
                             "StreamingAssets", "Templates")

RESOURCES = [
    ("ti://docs/protocol", "Bridge wire protocol", "text/markdown",
     "PROTOCOL.md -- the in-game DLL's verb contract: framing, every verb, "
     "combat/prompt/campaign mechanics, and what each spawn fixture "
     "bypasses"),
    ("ti://docs/playbook", "Operational playbook", "text/markdown",
     "Full driving playbook: session shape, the blocked clock, campaign "
     "entry, template universe, console use, diagnosis"),
    ("ti://templates", "Template class index", "application/json",
     "Template classes the template tool accepts, from the game's Templates "
     "directory"),
    ("ti://saves", "Save listing", "application/json",
     "Saved games with timestamps, from the on-disk saves directory"),
    ("ti://logs/game", "Game log tail", "text/plain",
     "Tail of Logs/TerraInvicta.log: template registration, mod merge, save "
     "messages"),
]


def list_resources():
    return {"resources": [{"uri": uri, "name": name, "mimeType": mime,
                           "description": desc}
                          for uri, name, mime, desc in RESOURCES]}


def _read_file(path):
    # A missing doc is a resource that explains itself, the way every other
    # reader here behaves; an uncaught OSError would break the whole read.
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            return f.read()
    except OSError as e:
        return "(cannot read %s: %s)" % (path, e)


def _templates_index():
    try:
        names = sorted(f[:-5] for f in os.listdir(TEMPLATES_DIR)
                       if f.endswith(".json"))
    except OSError as e:
        return json.dumps({"error": str(e)})
    return json.dumps(
        {"classes": names, "count": len(names),
         "note": "vanilla template classes; DLC and mod entries merge "
                 "in-engine on top -- query them with the template tool"},
        indent=2)


def _saves_listing():
    saves = []
    for f in modcheck._list_saves():
        name, ext = os.path.splitext(f)
        path = os.path.join(modcheck.SAVES_DIR, f)
        saves.append({
            "name": name,
            "extension": ext,
            "modified": datetime.datetime.fromtimestamp(
                os.path.getmtime(path)).isoformat(timespec="seconds"),
            "bytes": os.path.getsize(path)})
    return json.dumps({"saves": saves}, indent=2)


def read_resource(uri):
    mime = next((m for u, _n, m, _d in RESOURCES if u == uri), None)
    if uri == "ti://docs/protocol":
        text = _read_file(os.path.join(DOCS, "PROTOCOL.md"))
    elif uri == "ti://docs/playbook":
        text = _read_file(os.path.join(DOCS, "playbook.md"))
    elif uri == "ti://templates":
        text = _templates_index()
    elif uri == "ti://saves":
        text = _saves_listing()
    elif uri == "ti://logs/game":
        try:
            text = "\n".join(bridge.tail_log(400, None, bridge.GAME_LOG))
        except OSError as e:
            text = "(cannot read %s: %s)" % (bridge.GAME_LOG, e)
    else:
        raise KeyError(uri)
    return {"contents": [{"uri": uri, "mimeType": mime, "text": text}]}
