#!/bin/bash
# TerraInvictaMCP installer (Linux). Idempotent: safe after every game
# update; a second run changes nothing. Builds the DLL (build.sh), repairs what
# game updates revert (UMM injection), offers to register the MCP server with
# each supported client found on PATH, and self-tests the server offline.
#
#   ./install.sh                      register, asking before each global one
#   ./install.sh --yes                register with every client found
#   ./install.sh --register=claude    register with exactly these, no prompts
#
# TI_ROOT overrides the game root (three levels up) for out-of-tree checkouts.
set -eu

# Git Bash, MSYS and Cygwin run this script happily and then get the paths, the
# client CLIs and the build wrong in ways that look like the mod is broken.
# install.ps1 is the Windows installer.
case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*)
        echo "on Windows run install.ps1 (powershell -ExecutionPolicy Bypass -File install.ps1)" >&2
        exit 2 ;;
esac
MOD="$(cd "$(dirname "$0")" && pwd)"
TI="${TI_ROOT:-$(cd "$MOD/../../.." && pwd)}"
M="$TI/TerraInvicta_Data/Managed"
NAME="TerraInvictaMCP"
DLL="$MOD/$NAME.dll"
SERVER="$MOD/server"
CLIENTS="claude opencode antigravity"

ok()   { printf '  ok    %s\n' "$1"; }
did()  { printf '  DID   %s\n' "$1"; }
warn() { printf '  WARN  %s\n' "$1"; }
note() { printf '        %s\n' "$1"; }
usage() {
    cat <<USAGE
usage: install.sh [--yes] [--register=claude,opencode,antigravity]
  --yes, -y            register with every supported client found on PATH
  --register=a,b       register with exactly these clients, no prompts
  --help, -h           this message
Clients: claude (Claude Code, this game folder only), opencode (OpenCode,
global), antigravity (Antigravity, global).

Claude Code's registration covers this game folder and nothing else, so when it
is the only supported client found on PATH it is registered without asking. A
global registration (OpenCode, Antigravity), and any repoint of an existing
registration, is asked for, default No.

With no terminal (piped, cron, CI) those are left alone unless --register= (or
--yes) says otherwise.

Environment:
  TI_ROOT              the Terra Invicta folder. Defaults to three levels up
                       (Mods/Enabled/<mod>); set it when this checkout lives
                       somewhere else.
  UMM_INSTALLER_DIR    the unpacked Unity Mod Manager installer (the folder
                       holding Console.exe), used to repair the mod loader
                       when a game update reverts its injection (the Assembly
                       method). Needs mono. Without it the script reports the
                       reverted injection and names the manual fix. A Doorstop
                       install has nothing to revert and is reported as it is.
USAGE
}

# --- arguments -------------------------------------------------------------
ASSUME_YES=0
REGISTER_GIVEN=0
REGISTER_SET=""
while [ $# -gt 0 ]; do
    case "$1" in
        -y|--yes)     ASSUME_YES=1 ;;
        --register=*) REGISTER_SET="${1#--register=}"; REGISTER_GIVEN=1 ;;
        --register)
            if [ $# -lt 2 ]; then echo "--register needs a value" >&2; exit 2; fi
            REGISTER_SET="$2"; REGISTER_GIVEN=1; shift ;;
        -h|--help)    usage; exit 0 ;;
        *) printf 'unknown option: %s\n' "$1" >&2; usage >&2; exit 2 ;;
    esac
    shift
done
if [ "$REGISTER_GIVEN" -eq 1 ]; then
    OLD_IFS=$IFS; IFS=,
    for c in $REGISTER_SET; do
        [ -n "$c" ] || continue
        case " $CLIENTS " in
            *" $c "*) ;;
            *) printf 'unknown client in --register: %s (known: %s)\n' "$c" "$CLIENTS" >&2; exit 2 ;;
        esac
    done
    IFS=$OLD_IFS
fi

echo "TerraInvictaMCP install ($MOD)"
if [ ! -d "$TI/TerraInvicta_Data" ]; then
    printf '  FAIL  %s does not look like the Terra Invicta folder (no TerraInvicta_Data)\n' "$TI"
    printf '        If you used Windows Extract All, the zip'"'"'s folder is one level too deep: TerraInvictaMCP.dll must sit directly in Mods/Enabled/TerraInvictaMCP/\n'
    exit 1
fi

# --- 0. Python: the server is pure stdlib but needs Python 3.7 or newer. -----
# 3.7 is the floor the sources actually use (subprocess.run capture_output/text,
# datetime.date.fromisoformat). An older 3 installs and registers fine and then
# fails on the first tool call, so reject it here and name what it found.
PY=""
OLD_PY=""
for c in python3 python; do
    command -v "$c" >/dev/null 2>&1 || continue
    if "$c" -c 'import sys; sys.exit(0 if sys.version_info[:2] >= (3, 7) else 1)' >/dev/null 2>&1; then
        PY="$c"; break
    fi
    if [ -z "$OLD_PY" ]; then
        OLD_PY="$("$c" -c 'import sys; print("%s %d.%d" % ((sys.argv[1],) + sys.version_info[:2]))' \
                  "$c" 2>/dev/null)" || OLD_PY="$c (version unknown)"
    fi
done
if [ -n "$PY" ]; then
    ok "python found: $PY"
elif [ -n "$OLD_PY" ]; then
    warn "Python too old: found $OLD_PY, need 3.7 or newer -- install a newer python3 and re-run"
else
    warn "no Python 3 found -- install python3 (3.7 or newer) and re-run"
fi

# --- 1. Build the DLL when missing or older than any source file. ----------
HAVE_SRC=0
if [ -d "$MOD/src" ] && [ -n "$(find "$MOD/src" -name '*.cs' -print | head -1)" ]; then
    HAVE_SRC=1
fi
if [ "$HAVE_SRC" -eq 1 ] && [ -f "$MOD/build.sh" ]; then
    STALE=1
    if [ -f "$DLL" ] && [ -z "$(find "$MOD/src" -name '*.cs' -newer "$DLL" -print | head -1)" ]; then
        STALE=0
    fi
    if [ "$STALE" -eq 1 ]; then
        BUILD_RC=0
        TI_ROOT="$TI" bash "$MOD/build.sh" >/dev/null || BUILD_RC=$?
        # Exit 3 is build.sh's "no usable C# compiler here", which is an
        # ordinary state, not a failure: the shipped DLL covers it. Either way
        # build.sh has already explained itself on stderr.
        if [ "$BUILD_RC" -eq 0 ]; then
            did "built $NAME.dll"
        elif [ "$BUILD_RC" -eq 3 ] && [ -f "$DLL" ]; then
            ok "using shipped $NAME.dll (no usable C# compiler here -- see above)"
        elif [ "$BUILD_RC" -eq 3 ]; then
            warn "$NAME.dll missing and no usable C# compiler -- the in-game bridge will not load"
        elif [ -f "$DLL" ]; then
            warn "build failed (exit $BUILD_RC) -- keeping the existing $NAME.dll"
        else
            warn "build failed (exit $BUILD_RC) and no $NAME.dll present -- the in-game bridge will not load"
        fi
    else
        ok "$NAME.dll up to date"
    fi
elif [ -f "$DLL" ]; then
    ok "using shipped $NAME.dll (no src/ or build.sh to build from)"
else
    warn "$NAME.dll missing and nothing to build it from -- the in-game bridge will not load"
fi

# --- 2. UMM injection ------------------------------------------------------
# A game update that replaces UnityEngine.UIModule.dll reverts the injection,
# leaving the file identical to the .original_ backup.
# UMM's other install method, DoorstopProxy, touches nothing in Managed/: it
# drops winhttp.dll and doorstop_config.ini in the game root and loads from
# there. It is checked only where the Assembly method left no backup, because a
# leftover winhttp.dll from an abandoned Doorstop install would otherwise mask
# a reverted Assembly install and report a mod loader that does not load.
UI="$M/UnityEngine.UIModule.dll"
ORIG="$UI.original_"
if [ -f "$ORIG" ]; then
    if cmp -s "$UI" "$ORIG"; then
        # UMM's own console installer performs the repair. Most installs do not
        # have it unpacked anywhere findable, so only try when it is here.
        UMM="${UMM_INSTALLER_DIR:-$TI/tools/UnityModManager/UnityModManagerInstaller}"
        if [ -f "$UMM/Console.exe" ] && command -v mono >/dev/null 2>&1; then
            ( cd "$UMM" && printf '97\nn\nI\n' | mono Console.exe >/dev/null )
            did "reinstalled UMM injection (a game update had reverted it)"
        else
            warn "UMM injection reverted by a game update -- re-run the Unity Mod Manager installer, pick Terra Invicta, click Install, then relaunch the game"
        fi
    else
        ok "UMM injection present"
    fi
elif [ -f "$TI/winhttp.dll" ] && [ -f "$TI/doorstop_config.ini" ]; then
    ok "UMM injection present (Doorstop)"
else
    warn "no UnityModManager backup next to UnityEngine.UIModule.dll -- if DLL mods do not load, install Unity Mod Manager with the Assembly method"
fi

# --- 3. Client registration -------------------------------------------------
LONE_CLAUDE=0
if command -v claude >/dev/null 2>&1 &&
   ! command -v opencode >/dev/null 2>&1 &&
   ! command -v agy >/dev/null 2>&1; then
    LONE_CLAUDE=1
fi

# Every client found and left unregistered, for the closing advice: without it
# the user gets a working install with no tools in it and no idea why.
UNREG_IDS=""
UNREG_LABELS=""
unregistered() {   # <id> <label> <fresh|repoint>
    # Only a fresh registration is missing. A declined REPOINT leaves a working
    # registration in place, and advising --register= for it would silently
    # perform the repoint the user just refused.
    [ "$3" = fresh ] || return 0
    case ",$UNREG_IDS," in *",$1,"*) return 0 ;; esac
    UNREG_IDS="${UNREG_IDS:+$UNREG_IDS,}$1"
    UNREG_LABELS="${UNREG_LABELS:+$UNREG_LABELS, }$2"
}

should_register() {   # <id> <label> <scope> <fresh|repoint>
    local _id=$1 _label=$2 _scope=$3 _kind=$4 _reply
    if [ "$REGISTER_GIVEN" -eq 1 ]; then
        case ",$REGISTER_SET," in
            *",$_id,"*) return 0 ;;
            *) ok "$_label found; not in --register, skipped"
               unregistered "$_id" "$_label" "$_kind"; return 1 ;;
        esac
    fi
    if [ "$ASSUME_YES" -eq 1 ]; then return 0; fi
    if [ "$_id" = claude ] && [ "$_kind" = fresh ] && [ "$LONE_CLAUDE" -eq 1 ]; then
        return 0
    fi
    if [ ! -t 0 ]; then
        warn "$_label found but not registered; non-interactive run -- re-run with --register=$_id (or --yes for every client found)"
        unregistered "$_id" "$_label" "$_kind"
        return 1
    fi
    printf '  Register the MCP server with %s (%s)? [y/N] ' "$_label" "$_scope"
    _reply=""
    read -r _reply || _reply=""
    case "$_reply" in
        [yY]*) return 0 ;;
        *) ok "$_label registration declined"
           unregistered "$_id" "$_label" "$_kind"; return 1 ;;
    esac
}

# Antigravity has no MCP subcommand, so its registration is a JSON merge.
# HOME-level only: the workspace path its docs describe is read and then
# ignored, so writing one would look like it worked.
AGY_DIR="$HOME/.gemini/config"
AGY_FILE="$AGY_DIR/mcp_config.json"
AGY_LEGACY="$HOME/.gemini/antigravity-cli/mcp_config.json"
AGY_TARGET=""
agy_json() {   # <mode: check|write> <path>; check exits 0 current, 1 absent, 2 stale, 3 unreadable
    "$PY" - "$1" "$2" "$PY" "$SERVER" <<'EOF'
import json, os, sys
mode, path, cmd, server = sys.argv[1:5]
try:
    raw = open(path, "rb").read() if os.path.exists(path) else b""
except OSError:
    sys.exit(3)
if raw.strip():
    try:
        cfg = json.loads(raw.decode("utf-8-sig"))
    except ValueError:
        sys.exit(3)                      # never overwrite a file we cannot read
    if not isinstance(cfg, dict):
        sys.exit(3)
else:
    cfg = {}                             # absent or zero bytes: both mean empty
servers = cfg.get("mcpServers")
if not isinstance(servers, dict):
    servers = {}
    cfg["mcpServers"] = servers
cur = servers.get("terra-invicta")
if mode == "check":
    if not isinstance(cur, dict):
        sys.exit(1)
    same = cur.get("command") == cmd and list(cur.get("args") or []) == [server]
    sys.exit(0 if same else 2)
entry = dict(cur) if isinstance(cur, dict) else {}
entry["command"] = cmd                   # transport is implicit: command = stdio
entry["args"] = [server]                 # literal path; no ${VAR} substitution
entry.pop("type", None)                  # not a supported key here
servers["terra-invicta"] = entry
tmp = path + ".tmp"
with open(tmp, "w", encoding="utf-8") as fh:
    json.dump(cfg, fh, indent=2)
    fh.write("\n")
os.replace(tmp, path)
EOF
}
agy_snippet() {   # [indent], default 8 spaces
    printf '%s{"mcpServers": {"terra-invicta": {"command": "%s", "args": ["%s"]}}}\n' \
        "${1:-        }" "${PY:-python3}" "$SERVER"
}
agy_register() {
    if agy_json write "$AGY_TARGET"; then
        did "Antigravity: registered 'terra-invicta' in $AGY_TARGET -- restart Antigravity or run /mcp to reload it"
    else
        warn "Antigravity: could not write $AGY_TARGET -- add this by hand:"
        agy_snippet
    fi
}

if [ -z "$PY" ]; then
    warn "skipped MCP client registration (needs Python)"
else
    # Claude Code: local scope keys off the cwd at add time, so add from $TI.
    # Every client CLI is run with stdin closed: a probe that reads the terminal
    # would swallow the keystrokes meant for the prompts below.
    if command -v claude >/dev/null 2>&1; then
        cl() { ( cd "$TI" && claude "$@" </dev/null ); }
        cl_out=$(cl mcp get terra-invicta 2>/dev/null) && cl_rc=0 || cl_rc=$?
        claude_add() {
            cl mcp remove terra-invicta >/dev/null 2>&1 || true
            cl mcp add terra-invicta -- "$PY" "$SERVER" >/dev/null
        }
        if [ "$cl_rc" -eq 0 ] && printf '%s' "$cl_out" | grep -qF "$SERVER"; then
            ok "Claude Code: 'terra-invicta' registered (local scope, $TI)"
        elif [ "$cl_rc" -eq 0 ]; then
            # Registered, but at another path. Repointing is a DECISION, not a
            # repair: this script may be running from an extracted release or a
            # second checkout, and silently dragging a working registration over
            # to it is the wrong default. So it goes through the same gate as a
            # first-time registration, which also makes --register= honour it.
            if should_register claude "Claude Code" \
                    "REPOINT from an existing registration to $SERVER" repoint; then
                if claude_add; then
                    did "Claude Code: repointed 'terra-invicta' at $SERVER"
                    note "registered for $TI; start Claude Code from that folder (restart it if it is open)"
                else warn "Claude Code: re-registration failed -- claude mcp add terra-invicta -- $PY \"$SERVER\""; fi
            fi
        elif should_register claude "Claude Code" "this game folder only" fresh; then
            if claude_add; then
                did "Claude Code: registered 'terra-invicta' (local scope, $TI)"
                note "registered for $TI; start Claude Code from that folder (restart it if it is open)"
            else warn "Claude Code: registration failed -- claude mcp add terra-invicta -- $PY \"$SERVER\""; fi
        fi
    else
        ok "Claude Code (claude) not on PATH"
    fi

    # OpenCode: global scope only, and the non-interactive add form is
    # undocumented, so probe for it and verify that the add landed.
    if command -v opencode >/dev/null 2>&1; then
        # 'mcp list' health-checks every configured server, so cap it.
        oc() {
            if command -v timeout >/dev/null 2>&1; then timeout 60 opencode "$@" </dev/null
            else opencode "$@" </dev/null; fi
        }
        oc_list=$(oc mcp list 2>/dev/null || true)
        opencode_add() {
            oc_help=$(oc mcp add --help 2>&1) || return 1
            printf '%s' "$oc_help" | grep -q -- "--env" || return 1
            oc mcp add terra-invicta -- "$PY" "$SERVER" >/dev/null 2>&1 || return 1
            oc mcp list 2>/dev/null | grep -qF "$SERVER"
        }
        oc_manual() {
            warn "OpenCode: 'opencode mcp add' did not take the non-interactive form -- add by hand: opencode mcp add terra-invicta -- $PY \"$SERVER\""
        }
        if printf '%s' "$oc_list" | grep -qF "$SERVER"; then
            ok "OpenCode: 'terra-invicta' registered (global)"
        elif printf '%s' "$oc_list" | grep -qF "terra-invicta"; then
            # Same reasoning as Claude Code above: a repoint is a decision.
            if should_register opencode "OpenCode" \
                    "REPOINT from an existing registration to $SERVER" repoint; then
                if opencode_add; then did "OpenCode: repointed 'terra-invicta' at $SERVER"
                else oc_manual; fi
            fi
        elif should_register opencode "OpenCode" "global -- every project" fresh; then
            if opencode_add; then did "OpenCode: registered 'terra-invicta' (global)"
            else oc_manual; fi
        fi
    else
        ok "OpenCode (opencode) not on PATH"
    fi

    # Antigravity: file-based, HOME level. Only write where the client already
    # keeps its config; creating the tree would guess at a path it may not read.
    if command -v agy >/dev/null 2>&1; then
        if [ -d "$AGY_DIR" ]; then AGY_TARGET="$AGY_FILE"
        elif [ -f "$AGY_LEGACY" ]; then AGY_TARGET="$AGY_LEGACY"
        fi
        if [ -z "$AGY_TARGET" ]; then
            warn "Antigravity found but $AGY_DIR does not exist -- create it and add this to mcp_config.json:"
            agy_snippet
        else
            agy_json check "$AGY_TARGET" && agy_rc=0 || agy_rc=$?
            case "$agy_rc" in
                0) ok "Antigravity: 'terra-invicta' registered (global, $AGY_TARGET)" ;;
                2) # Stale path. Same reasoning as the two clients above: this
                   # may be an extracted release rather than the install the
                   # user actually runs, so ask rather than drag it over.
                   if should_register antigravity "Antigravity" \
                           "REPOINT from an existing registration to $SERVER" repoint; then
                       agy_register
                   fi ;;
                3) warn "Antigravity: $AGY_TARGET is not readable JSON, leaving it alone -- add this by hand:"
                   agy_snippet ;;
                *) if should_register antigravity "Antigravity" "global -- every project" fresh; then
                       agy_register
                   fi ;;
            esac
        fi
    else
        ok "Antigravity (agy) not on PATH"
    fi
fi

# --- 4. Offline self-test: server/stdio_selfcheck.py drives a real server
# through initialize, tools/list, a resource read and several tool calls, no
# game required, and fails on any stdout line that is not JSON-RPC: the MCP
# transport is newline-delimited JSON on stdout, so one stray print anywhere in
# the server (modcheck.py is imported by tools.py) silently kills every
# client's connection.
if [ -z "$PY" ]; then
    warn "skipped server self-test (needs Python)"
elif [ ! -f "$SERVER/stdio_selfcheck.py" ]; then
    warn "server/stdio_selfcheck.py not present; skipped self-test"
elif out="$("$PY" "$SERVER/stdio_selfcheck.py" 2>&1)"; then
    ok "self-test: clean JSON-RPC"
else
    warn "server self-test FAILED:"
    printf '%s\n' "$out" | sed 's/^/        /'
    exit 1
fi

if [ -n "$UNREG_IDS" ]; then
    echo ""
    printf '%s: no terra-invicta tools there until registered. To register:\n' "$UNREG_LABELS"
    printf '  ./install.sh --register=%s\n' "$UNREG_IDS"
    echo ""
fi
echo "done"
echo ""
echo "Any other MCP client (Cursor, Cline, Windsurf, ...): add to its MCP config:"
agy_snippet '  '
