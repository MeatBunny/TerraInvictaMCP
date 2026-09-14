#!/bin/bash
# Builds the DLL and packages a release zip that unzips straight into
# Mods/Enabled/: release/TerraInvictaMCP-<version>.zip, whose only top-level
# entry is the folder TerraInvictaMCP/.
#
#   ./release.sh              build, then package
#   ./release.sh --no-build   package the DLL already in the folder
#
# The version comes from ModInfo.json.
set -euo pipefail

MOD="$(cd "$(dirname "$0")" && pwd)"
NAME="TerraInvictaMCP"
DLL="$NAME.dll"
OUT_DIR="$MOD/release"
BUILD=1

usage() {
    cat <<'USAGE'
usage: release.sh [--no-build] [-h|--help]
Builds the DLL and packages release/TerraInvictaMCP-<version>.zip, whose only
top-level entry is the folder TerraInvictaMCP/, so it unzips straight into
Mods/Enabled/. The version comes from ModInfo.json.
  --no-build   package the DLL already in the folder, skipping build.sh
  -h, --help   this message
In the zip: ModInfo.json, TerraInvictaMCP.dll, README.md, LICENSE, install.sh,
install.ps1, build.sh, build.ps1, src/, server/*.py, server/tests/*.py and docs/*.md.
Out of it: .git, .gitignore, .gitattributes, .github/, AGENTS.md, SECURITY.md, __pycache__,
*.cache, the generated modcheck_*.dat caches, and release/ itself. Everything
shipped is copied by name, so nothing new lands in a release by accident.
USAGE
}
case "${1:-}" in
    --no-build) BUILD=0 ;;
    -h|--help) usage; exit 0 ;;
    "") ;;
    *) printf 'unknown option: %s\n' "$1" >&2; usage >&2; exit 2 ;;
esac

VERSION="$(sed -n 's/.*"Version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
    "$MOD/ModInfo.json" | head -1)"
[ -n "$VERSION" ] || {
    echo "no \"Version\" in $MOD/ModInfo.json" >&2; exit 1; }

if [ "$BUILD" = 1 ]; then
    echo "== building $DLL"
    "$MOD/build.sh"
fi
[ -f "$MOD/$DLL" ] || {
    echo "no $DLL in $MOD after the build -- refusing to package a release" \
        "without it" >&2
    exit 1; }

STAGE="$OUT_DIR/stage"
ZIP="$OUT_DIR/$NAME-$VERSION.zip"
rm -rf "$STAGE" "$ZIP"
mkdir -p "$STAGE/$NAME"

echo "== staging"
cp "$MOD/ModInfo.json" "$MOD/$DLL" "$MOD/README.md" "$MOD/LICENSE" \
   "$MOD/install.sh" "$MOD/install.ps1" "$MOD/build.sh" "$MOD/build.ps1" \
   "$STAGE/$NAME/"
mkdir -p "$STAGE/$NAME/src"
cp -r "$MOD/src/." "$STAGE/$NAME/src/"
find "$STAGE/$NAME/src" -type f ! -name '*.cs' -delete
mkdir -p "$STAGE/$NAME/server"
# *.py only: this is what keeps __pycache__ and the generated modcheck_*.dat
# caches out of the archive.
cp "$MOD"/server/*.py "$STAGE/$NAME/server/"
mkdir -p "$STAGE/$NAME/server/tests"
cp "$MOD"/server/tests/*.py "$STAGE/$NAME/server/tests/"
# docs/*.md by glob, so a new doc ships without editing this list while an
# editor's swap file or a stray dotfile in docs/ does not.
mkdir -p "$STAGE/$NAME/docs"
cp "$MOD"/docs/*.md "$STAGE/$NAME/docs/"
chmod +x "$STAGE/$NAME/install.sh" "$STAGE/$NAME/build.sh"

# The game's Mod Manager parses EVERY .json under a mod folder, recursively, as
# a template array, and one it cannot match to a template file is an error
# popup at launch for every user. ModInfo.json is the only one allowed.
# -iname, not -name: the Windows install and the Wine prefix both match
# filenames case-insensitively, so a staged cache.JSON is the same launch
# error as cache.json and has to be caught here too.
STRAY="$(find "$STAGE" -iname '*.json' -not -path "$STAGE/$NAME/ModInfo.json")"
[ -z "$STRAY" ] || {
    echo "packaging refused: .json files other than ModInfo.json in the" \
        "staged mod folder -- the game parses every one as a template array" \
        "and pops an error at launch:" >&2
    echo "$STRAY" >&2
    exit 1; }

# Globbed in above, so a missing one is not a cp failure: check by name.
for f in server/__main__.py docs/PROTOCOL.md src/Verbs.cs; do
    [ -e "$STAGE/$NAME/$f" ] || { echo "staged tree is missing $f" >&2; exit 1; }
done

# Last thing staged, so it is newer than every other staged file:
# install.sh/install.ps1 rebuild when any source is newer than the DLL, and a
# downloaded zip must never turn "unzip and run the installer" into a build on
# a machine with no compiler. Zip timestamps have 2-second resolution and no
# sub-second part, so equal mtimes can compare either way after a round trip --
# push the DLL a full 2 seconds clear rather than matching them.
touch -d '+2 seconds' "$STAGE/$NAME/$DLL"

echo "== packaging $ZIP"
(cd "$STAGE" && zip -qr "$ZIP" "$NAME")
rm -rf "$STAGE"

echo "== contents"
unzip -l "$ZIP"
echo
echo "Release: $ZIP"
