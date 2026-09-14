#!/usr/bin/env python3
"""Stdout-hygiene check for the MCP stdio server.

  python3 server/stdio_selfcheck.py

The MCP transport is newline-delimited JSON-RPC on stdout, so ANYTHING else
written there -- a stray print, a warning, a traceback, a progress line --
corrupts the stream and the client drops the connection, usually with a parse
error naming a character rather than the module that emitted it. Clients do
not recover.

This drives a real server subprocess through initialize, tools/list,
resources/list, a resource read, and several tools/call requests (modcheck
among them, since modcheck.py is imported by tools.py and is the largest body
of code that could print), then asserts that every line the server wrote to
stdout parses as JSON-RPC. It needs no running game: a tool call that reaches
the bridge comes back as a tool error, which is still a well-formed response
and is exactly as good a carrier for stray output.

Exit 0 when the stream is clean, 1 when it is not (offending lines quoted),
2 on a harness failure.
"""
import json
import os
import subprocess
import sys

SERVER_DIR = os.path.dirname(os.path.abspath(__file__))
TIMEOUT = 300

# id: (method, params). Ids are checked off against the responses, so a server
# that dies partway is a failure rather than a clean-looking short stream.
REQUESTS = [
    (1, "initialize", {"protocolVersion": "2025-06-18",
                       "capabilities": {},
                       "clientInfo": {"name": "stdio_selfcheck",
                                      "version": "1"}}),
    (2, "tools/list", {}),
    (3, "resources/list", {}),
    (4, "resources/read", {"uri": "ti://docs/protocol"}),
    (5, "tools/call", {"name": "selftest", "arguments": {}}),
    (6, "tools/call", {"name": "modcheck",
                       "arguments": {"check": "merge"}}),
    (7, "tools/call", {"name": "save_check", "arguments": {}}),
    (8, "tools/call", {"name": "no_such_tool", "arguments": {}}),
    (9, "ping", {}),
]


def main():
    init, *rest = [json.dumps({"jsonrpc": "2.0", "id": msg_id,
                               "method": method, "params": params})
                   for msg_id, method, params in REQUESTS]
    # initialize goes first; the initialized notification rides after it.
    lines = [init, json.dumps({"jsonrpc": "2.0",
                               "method": "notifications/initialized"}), *rest]
    stdin = "".join(l + "\n" for l in lines)

    try:
        proc = subprocess.run([sys.executable, SERVER_DIR], input=stdin,
                              capture_output=True, text=True, timeout=TIMEOUT)
    except OSError as e:
        sys.stderr.write("cannot run the server at %s: %s\n" % (SERVER_DIR, e))
        return 2
    except subprocess.TimeoutExpired:
        sys.stderr.write("server did not exit within %ds of stdin EOF\n"
                         % TIMEOUT)
        return 2

    bad = []
    seen_ids = set()
    notifications = 0
    out = proc.stdout.splitlines()
    for num, line in enumerate(out, 1):
        if not line.strip():
            # A blank line is not fatal to a client, but nothing here should
            # be writing one either.
            bad.append((num, "(blank line)"))
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError as e:
            bad.append((num, "not JSON (%s): %s" % (e, line[:200])))
            continue
        if not isinstance(msg, dict) or msg.get("jsonrpc") != "2.0":
            bad.append((num, "not a JSON-RPC 2.0 message: %s" % line[:200]))
            continue
        if msg.get("id") is not None:
            seen_ids.add(msg["id"])
        else:
            notifications += 1

    total = len(out)
    missing = sorted({r[0] for r in REQUESTS} - seen_ids)
    ok = not bad and not missing and proc.returncode == 0

    print("stdio hygiene: %d stdout line(s), %d response(s), %d notification(s)"
          % (total, len(seen_ids), notifications))
    if proc.returncode != 0:
        print("FAIL: server exited %d" % proc.returncode)
    for num, why in bad:
        print("FAIL: stdout line %d is not JSON-RPC -- %s" % (num, why))
    if missing:
        print("FAIL: no response for request id(s) %s -- the server stopped "
              "early" % ", ".join(str(m) for m in missing))
    if not ok and proc.stderr.strip():
        sys.stderr.write("--- server stderr ---\n%s\n" % proc.stderr.strip())
    print("stdout is clean JSON-RPC" if ok else "stdout is NOT clean")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
