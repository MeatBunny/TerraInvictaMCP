# Builds TerraInvictaMCP.dll from every .cs under src\. Needs a C# compiler and
# an installed Unity Mod Manager (its UnityModManager/0Harmony live under the
# game's Managed folder).
#
# Compiler probe order: Mono's mcs on PATH, a Roslyn csc on PATH, the Visual
# Studio / Build Tools csc.exe via vswhere, then the .NET SDK's csc.dll run
# through dotnet. The pre-Roslyn .NET Framework csc (C# 5 at most) is refused;
# see -Help.
#
# -nostdlib against the game's own assemblies is deliberate: a system C#
# profile places some BCL types in different assemblies than Unity's Mono, and a
# typeref emitted against the wrong assembly is a TypeLoadException at runtime.
# Never add system references to make a build error go away.
#
# The game root is three levels up (Mods\{Enabled,Disabled}\<mod>); TI_ROOT
# overrides it for out-of-tree checkouts.
#
# Run from this folder:  powershell -ExecutionPolicy Bypass -File build.ps1

param([switch]$Help)

if ($Help) {
    @"
usage: build.ps1 [-Help]
Compiles every .cs under src\ into TerraInvictaMCP.dll, written next to this
script (Mods\Enabled\TerraInvictaMCP\), which is where the game loads it from.
Restart the game to pick up a new build.
  -Help        this message
Requirements:
  a C# compiler: Mono's mcs or a Roslyn csc on PATH, or Visual Studio /
  Build Tools (MSBuild's Roslyn\csc.exe, located through vswhere.exe), or the
  .NET SDK (<sdk>\Roslyn\bincore\csc.dll, run through dotnet on PATH).
  A pre-Roslyn csc found on PATH (the in-box .NET Framework compiler) is
  detected and REFUSED: it accepts C# 5 at most and the sources use C# 6, so
  it can only produce syntax errors that name the wrong problem.
  a Terra Invicta install with Unity Mod Manager already installed: the build
  references the game's own assemblies under TerraInvicta_Data\Managed,
  including UnityModManager\UnityModManager.dll and 0Harmony.dll.
Environment:
  TI_ROOT      the Terra Invicta folder. Defaults to three levels up
               (Mods\Enabled\<mod>); set it when this checkout lives
               somewhere else.
Run from this folder:  powershell -ExecutionPolicy Bypass -File build.ps1
"@ | Write-Host
    exit 0
}

# "Continue", not "Stop": Windows PowerShell 5.1 turns a redirected stderr write
# from a native command into a terminating error under "Stop", and a compiler
# writes warnings to stderr. The exit code is checked instead.
$ErrorActionPreference = "Continue"
$Mod = $PSScriptRoot
$TI  = if ($env:TI_ROOT) { $env:TI_ROOT } else { (Resolve-Path (Join-Path $Mod "..\..\..")).Path }
$M   = Join-Path $TI "TerraInvicta_Data\Managed"
$Out = Join-Path $Mod "TerraInvictaMCP.dll"

if (-not (Test-Path $M)) {
    Write-Host "no game assemblies at $M (set TI_ROOT)"; exit 2
}
if (-not (Test-Path (Join-Path $M "Assembly-CSharp.dll"))) {
    Write-Host "no Assembly-CSharp.dll under $M -- $TI is not a Terra Invicta install (set TI_ROOT)"
    exit 2
}
if (-not (Test-Path (Join-Path $M "UnityModManager\UnityModManager.dll"))) {
    Write-Host "Unity Mod Manager not installed at $M\UnityModManager"; exit 2
}

$Refs = @(
    "mscorlib.dll"
    "System.dll"
    "System.Core.dll"
    "netstandard.dll"
    "Assembly-CSharp.dll"
    "Assembly-CSharp-firstpass.dll"
    "Newtonsoft.Json.dll"
    "Unity.Entities.dll"
    "Unity.TextMeshPro.dll"
    "UnityEngine.AssetBundleModule.dll"
    "UnityEngine.AudioModule.dll"
    "UnityEngine.CoreModule.dll"
    "UnityEngine.IMGUIModule.dll"
    "UnityEngine.ScreenCaptureModule.dll"
    "UnityEngine.UI.dll"
    "UnityEngine.UIModule.dll"
    "UnityModManager\UnityModManager.dll"
    "UnityModManager\0Harmony.dll"
)
$RefPaths = $Refs | ForEach-Object { Join-Path $M $_ }
$Missing  = $RefPaths | Where-Object { -not (Test-Path $_) }
if ($Missing) {
    Write-Host "missing game assemblies:"
    $Missing | ForEach-Object { Write-Host "  $_" }
    exit 2
}

$SrcDir = Join-Path $Mod "src"
if (-not (Test-Path $SrcDir)) { Write-Host "no src\ under $Mod"; exit 2 }
$Src = @(Get-ChildItem $SrcDir -Filter *.cs -Recurse | Sort-Object FullName)
if ($Src.Count -eq 0) { Write-Host "no .cs under $SrcDir"; exit 2 }

# Roslyn's csc prints a version and exits 0. The pre-Roslyn .NET Framework
# compiler does not know the switch: it exits nonzero with error CS2007.
function Test-RoslynCsc($Path, [string[]]$Prefix = @()) {
    $out = & $Path @Prefix -version 2>&1
    if ($LASTEXITCODE -ne 0) { return $false }
    return (($out -join " ") -match '\d+\.\d+')
}

# Compiler probe. Get-Command honours PATHEXT, so a .cmd/.bat shim resolves the
# same as a native .exe.
$Compiler = $null
$Prefix   = @()        # arguments before the compiler's own (the csc.dll for dotnet)
$Kind     = $null
$Refused  = $null      # a pre-Roslyn csc that was found and will not be used
$NoSdk    = $null      # a dotnet that turned out to be runtime-only
$Found = Get-Command mcs -ErrorAction SilentlyContinue
if ($Found) { $Compiler = $Found.Source; $Kind = "mcs" }
if (-not $Compiler) {
    $Found = Get-Command csc -ErrorAction SilentlyContinue
    if ($Found) {
        if (Test-RoslynCsc $Found.Source) { $Compiler = $Found.Source; $Kind = "csc" }
        else { $Refused = $Found.Source }
    }
}
if (-not $Compiler -and ${env:ProgramFiles(x86)}) {
    $VsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $VsWhere) {
        $Found = & $VsWhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\Roslyn\csc.exe" 2>$null |
            Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if ($Found) { $Compiler = $Found; $Kind = "csc" }
    }
}
if (-not $Compiler) {
    $Dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($Dotnet) {
        $Sdk = @(& $Dotnet.Source --list-sdks 2>$null) | Select-Object -Last 1
        if (-not $Sdk) { $NoSdk = $Dotnet.Source }   # runtime-only install: no SDK, no csc.dll
        if ($Sdk -match '^(\S+) \[(.+)\]$') {
            $Dll = Join-Path (Join-Path (Join-Path $Matches[2] $Matches[1]) "Roslyn\bincore") "csc.dll"
            if ((Test-Path $Dll) -and (Test-RoslynCsc $Dotnet.Source @($Dll))) {
                $Compiler = $Dotnet.Source; $Prefix = @($Dll); $Kind = "csc"
            }
        }
    }
}
if (-not $Compiler) {
    Write-Host "no usable C# compiler found."
    if ($Refused) {
        Write-Host "  Refusing $Refused"
        Write-Host "  It is the .NET Framework C# 5 compiler, and this mod's source uses C# 6"
        Write-Host "  (nameof), so it cannot build it. Not invoked."
    }
    if ($NoSdk) {
        Write-Host "  $NoSdk has runtimes only; dotnet --list-sdks lists no SDK, so no csc.dll."
    }
    Write-Host "  Looked for: mcs on PATH, a Roslyn csc on PATH, Visual Studio / Build Tools"
    Write-Host "  (vswhere.exe), the .NET SDK (dotnet --list-sdks)."
    Write-Host "Fix: install Mono (mono-project.com), or a modern C# compiler (Visual Studio"
    Write-Host "Build Tools, or the .NET SDK's Roslyn csc), or just use the prebuilt"
    Write-Host "TerraInvictaMCP.dll that ships with the mod and skip building."
    exit 3
}

# mcs and csc take the same job with different switch spellings; keep the two
# argument lists apart rather than trying to share one.
if ($Kind -eq "mcs") {
    $CArgs = @("-target:library", "-nostdlib", "-noconfig", "-out:$Out")
    $CArgs += ($RefPaths | ForEach-Object { "-r:$_" })
} else {
    $CArgs = @("/target:library", "/nostdlib", "/noconfig", "/out:$Out")
    $CArgs += ($RefPaths | ForEach-Object { "/reference:$_" })
}
$CArgs += $Src.FullName

Write-Host "Compiling $($Src.Count) source file(s) with $(@($Compiler) + $Prefix -join ' ')"
& $Compiler @Prefix @CArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "build FAILED (exit $LASTEXITCODE)"
    exit 1
}
Write-Host "Built: $Out ($((Get-Item $Out).Length) bytes)"
