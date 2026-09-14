#!/bin/bash
# Builds TerraInvictaMCP.dll from every .cs under src/. Needs mono-mcs and an
# installed Unity Mod Manager (its UnityModManager/0Harmony live under the
# game's Managed folder).
#
# -nostdlib against the game's own assemblies is deliberate: system Mono's
# profile places some BCL types in different assemblies than Unity's Mono, and a
# typeref emitted against the wrong assembly is a TypeLoadException at runtime.
# Never add system references to make a build error go away.
#
# The game root is three levels up (Mods/{Enabled,Disabled}/<mod>); TI_ROOT
# overrides it for out-of-tree checkouts.
set -e
MOD="$(cd "$(dirname "$0")" && pwd)"

usage() {
    cat <<'USAGE'
usage: build.sh [-h|--help]
Compiles every .cs under src/ into TerraInvictaMCP.dll, written next to this
script (Mods/Enabled/TerraInvictaMCP/), which is where the game loads it from.
Restart the game to pick up a new build.
  -h, --help   this message
Requirements:
  mcs          Mono's C# compiler (package mono-mcs, or mono-devel). Without
               it the build exits 3 and the shipped DLL is used as-is.
  a Terra Invicta install with Unity Mod Manager already installed: the build
  references the game's own assemblies under TerraInvicta_Data/Managed,
  including UnityModManager/UnityModManager.dll and 0Harmony.dll.
Environment:
  TI_ROOT      the Terra Invicta folder. Defaults to three levels up
               (Mods/Enabled/<mod>); set it when this checkout lives
               somewhere else.
USAGE
}
case "${1:-}" in
    -h|--help) usage; exit 0 ;;
    "") ;;
    *) printf 'unknown option: %s\n' "$1" >&2; usage >&2; exit 2 ;;
esac

TI="${TI_ROOT:-$(cd "$MOD/../../.." && pwd)}"
M="$TI/TerraInvicta_Data/Managed"
OUT="$MOD/TerraInvictaMCP.dll"

command -v mcs >/dev/null 2>&1 || {
    echo "no usable C# compiler found." >&2
    echo "  Looked for: mcs on PATH." >&2
    echo "Fix: install Mono's C# compiler (package mono-mcs, or mono-devel), or just use" >&2
    echo "the prebuilt TerraInvictaMCP.dll that ships with the mod and skip building." >&2
    exit 3
}
[ -d "$M" ] || { echo "no game assemblies at $M (set TI_ROOT)" >&2; exit 2; }
[ -f "$M/Assembly-CSharp.dll" ] || {
    echo "no Assembly-CSharp.dll under $M -- $TI is not a Terra Invicta install (set TI_ROOT)" >&2
    exit 2; }
[ -f "$M/UnityModManager/UnityModManager.dll" ] || {
    echo "Unity Mod Manager not installed at $M/UnityModManager" >&2; exit 2; }

REFS=(
    mscorlib.dll
    System.dll
    System.Core.dll
    netstandard.dll
    Assembly-CSharp.dll
    Assembly-CSharp-firstpass.dll
    Newtonsoft.Json.dll
    Unity.Entities.dll
    Unity.TextMeshPro.dll
    UnityEngine.AssetBundleModule.dll
    UnityEngine.AudioModule.dll
    UnityEngine.CoreModule.dll
    UnityEngine.IMGUIModule.dll
    UnityEngine.ScreenCaptureModule.dll
    UnityEngine.UI.dll
    UnityEngine.UIModule.dll
    UnityModManager/UnityModManager.dll
    UnityModManager/0Harmony.dll
)
ARGS=(-target:library -nostdlib -noconfig "-out:$OUT")
MISSING=()
for r in "${REFS[@]}"; do
    [ -f "$M/$r" ] || MISSING+=("$M/$r")
    ARGS+=("-r:$M/$r")
done
if [ "${#MISSING[@]}" -gt 0 ]; then
    echo "missing game assemblies:" >&2
    printf '  %s\n' "${MISSING[@]}" >&2
    exit 2
fi

mapfile -t SRC < <(find "$MOD/src" -name '*.cs' | sort)
[ "${#SRC[@]}" -gt 0 ] || { echo "no .cs under $MOD/src" >&2; exit 2; }

mcs "${ARGS[@]}" "${SRC[@]}"
echo "Built: $(ls -la "$OUT")"
