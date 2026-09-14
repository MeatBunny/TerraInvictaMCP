# TerraInvictaMCP installer (Windows). Idempotent: safe after every game
# update; a second run changes nothing. Builds the DLL (build.ps1), detects the
# UMM injection a game update reverts and names the fix, offers to register the
# MCP server with each supported client found on PATH, and self-tests the
# server offline.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Yes
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Register claude
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Help
#
# TI_ROOT overrides the game root (three levels up) for out-of-tree checkouts.
param(
    [switch]$Yes,
    [string]$Register,
    [switch]$Help
)

if ($Help) {
    @"
usage: powershell -ExecutionPolicy Bypass -File install.ps1 [-Yes]
                  [-Register claude,opencode,antigravity] [-Help]
  -Yes         register with every supported client found on PATH
  -Register    register with exactly these clients, no prompts
  -Help        this message
Clients: claude (Claude Code, this game folder only), opencode (OpenCode,
global), antigravity (Antigravity, global).

Claude Code's registration covers this game folder and nothing else, so when it
is the only supported client found on PATH it is registered without asking. A
global registration (OpenCode, Antigravity), and any repoint of an existing
registration, is asked for, default No.

With no console to prompt at (piped, scheduled task, CI) those are left alone
unless -Register (or -Yes) says otherwise.

Environment:
  TI_ROOT      the Terra Invicta folder. Defaults to three levels up
               (Mods\Enabled\<mod>); set it when this checkout lives
               somewhere else.

Unity Mod Manager: with the Assembly install method, a game update reverts its
injection into TerraInvicta_Data\Managed\UnityEngine.UIModule.dll. This script
detects that and names the fix -- re-run UnityModManager.exe, pick Terra
Invicta, choose the Assembly method, click Install -- and does not repair it
itself. (The Linux install.sh can, through UMM's console installer under mono;
there is no such path here.) A DoorstopProxy install (winhttp.dll and
doorstop_config.ini in the game folder) has nothing to revert and is reported
as it is.
"@ | Write-Host
    exit 0
}

# "Continue", not "Stop": Windows PowerShell 5.1 turns a redirected stderr
# write from a native command into a terminating error under "Stop", and this
# script probes several native commands that may fail. Every step checks its
# own result.
$ErrorActionPreference = "Continue"
$Mod    = $PSScriptRoot
$TI     = if ($env:TI_ROOT) { $env:TI_ROOT } else { (Resolve-Path (Join-Path $Mod "..\..\..")).Path }
$M      = Join-Path $TI "TerraInvicta_Data\Managed"
$Name   = "TerraInvictaMCP"
$Dll    = Join-Path $Mod "$Name.dll"
$Server = Join-Path $Mod "server"
$Clients = @("claude", "opencode", "antigravity")

function Ok($m)   { Write-Host "  ok    $m" }
function Did($m)  { Write-Host "  DID   $m" }
function Warn($m) { Write-Host "  WARN  $m" }

# --- arguments -------------------------------------------------------------
$RegisterGiven = $PSBoundParameters.ContainsKey("Register")
$RegisterSet   = @()
if ($RegisterGiven) {
    # Split on commas and whitespace both: PowerShell flattens an unquoted
    # -Register claude,opencode into the single string "claude opencode".
    $RegisterSet = @($Register -split '[,\s]+' | Where-Object { $_ })
    foreach ($c in $RegisterSet) {
        if ($Clients -notcontains $c) {
            Write-Host "unknown client in -Register: $c (known: $($Clients -join ', '))"
            exit 2
        }
    }
}

Write-Host "TerraInvictaMCP install ($Mod)"
if (-not (Test-Path (Join-Path $TI "TerraInvicta_Data"))) {
    Write-Host "  FAIL  $TI does not look like the Terra Invicta folder (no TerraInvicta_Data)"
    Write-Host "        If you used Windows Extract All, the zip's folder is one level too deep: TerraInvictaMCP.dll must sit directly in Mods\Enabled\TerraInvictaMCP\"
    exit 1
}

# --- 0. Python: the server is pure stdlib but needs a Python 3. -------------
# Prefer the py launcher, fall back to python (guarding against the Store stub,
# which fails the probe with a nonzero exit).
$PyExe = $null; $PyArgs = @()
if (Get-Command py -ErrorAction SilentlyContinue) {
    & py -3 -c "import sys" 2>$null
    if ($LASTEXITCODE -eq 0) { $PyExe = "py"; $PyArgs = @("-3") }
}
if (-not $PyExe -and (Get-Command python -ErrorAction SilentlyContinue)) {
    & python -c "import sys" 2>$null
    if ($LASTEXITCODE -eq 0) { $PyExe = "python" }
}
if ($PyExe) {
    # Resolve the launcher to the interpreter it actually runs, and register
    # THAT. Two reasons, both learned the hard way:
    #   - `py -3` puts a bare "-3" in the argument list, and at least one
    #     client (OpenCode) parses it as the NUMBER -3 when writing its config,
    #     then refuses to load the config it just wrote ("Expected string,
    #     got -3"). An absolute interpreter has no numeric-looking argument.
    #   - A client may launch the server from a different working directory or
    #     environment than this script; an absolute path cannot be
    #     re-resolved to a different Python later.
    # The launcher stays in use for this script's own probes, where it is fine.
    $Resolved = $null
    try {
        $Resolved = (& $PyExe @PyArgs -c "import sys; print(sys.executable)" 2>$null |
                     Select-Object -First 1)
        if ($Resolved) { $Resolved = $Resolved.Trim() }
    } catch { $Resolved = $null }
    if ($Resolved -and (Test-Path -LiteralPath $Resolved)) {
        $RunExe = $Resolved; $RunArgs = @()
        Ok "python found: $((@($PyExe) + $PyArgs) -join ' ') -> $RunExe"
    } else {
        $RunExe = $PyExe; $RunArgs = $PyArgs
        Ok "python found: $((@($PyExe) + $PyArgs) -join ' ')"
    }
} else {
    $RunExe = $null; $RunArgs = @()
    Warn "no Python 3 found -- install it (winget install Python.Python.3.12, or python.org) and re-run"
}
# The command line the server is launched with, everywhere below.
$PyCmdArgs = @(@($RunArgs) + @($Server))
$PyDisplay = (@($RunExe) + $RunArgs | Where-Object { $_ }) -join " "

# --- 1. Build the DLL when missing or older than any source file. ----------
$Build   = Join-Path $Mod "build.ps1"
$SrcDir  = Join-Path $Mod "src"
$SrcAll  = @()
if (Test-Path $SrcDir) { $SrcAll = @(Get-ChildItem $SrcDir -Filter *.cs -Recurse) }
$PsExe   = if ($PSVersionTable.PSEdition -eq "Core") { "pwsh" } else { "powershell" }
if ($SrcAll.Count -gt 0 -and (Test-Path $Build)) {
    $Stale = $true
    if (Test-Path $Dll) {
        $DllTime = (Get-Item $Dll).LastWriteTime
        $Stale = [bool](@($SrcAll | Where-Object { $_.LastWriteTime -gt $DllTime }).Count)
    }
    if ($Stale) {
        $PrevRoot = $env:TI_ROOT
        $env:TI_ROOT = $TI
        $BuildOut = & $PsExe -NoProfile -ExecutionPolicy Bypass -File $Build 2>&1
        $BuildRc  = $LASTEXITCODE
        $env:TI_ROOT = $PrevRoot
        if ($BuildRc -eq 0) {
            Did "built $Name.dll"
        } else {
            # Exit 3 is build.ps1's "no usable C# compiler here", which is an
            # ordinary state on a stock Windows box, not a failure: the shipped
            # DLL covers it. Pass its explanation through either way.
            $BuildOut | ForEach-Object { Write-Host "        $_" }
            if ($BuildRc -eq 3) {
                if (Test-Path $Dll) { Ok "using shipped $Name.dll (no usable C# compiler here -- see above)" }
                else { Warn "$Name.dll missing and no usable C# compiler -- the in-game bridge will not load" }
            } elseif (Test-Path $Dll) {
                Warn "build failed (exit $BuildRc) -- keeping the existing $Name.dll"
            } else {
                Warn "build failed (exit $BuildRc) and no $Name.dll present -- the in-game bridge will not load"
            }
        }
    } else {
        Ok "$Name.dll up to date"
    }
} elseif (Test-Path $Dll) {
    Ok "using shipped $Name.dll (no src\ or build.ps1 to build from)"
} else {
    Warn "$Name.dll missing and nothing to build it from -- the in-game bridge will not load"
}

# --- 2. UMM injection ------------------------------------------------------
# A game update that replaces UnityEngine.UIModule.dll reverts the injection,
# leaving the file identical to the .original_ backup. On Windows the repair is
# UMM's own GUI, not this script.
# UMM's other install method, DoorstopProxy, touches nothing in Managed\: it
# drops winhttp.dll and doorstop_config.ini in the game root and loads from
# there. It is checked only where the Assembly method left no backup, because a
# leftover winhttp.dll from an abandoned Doorstop install would otherwise mask
# a reverted Assembly install and report a mod loader that does not load.
$Ui   = Join-Path $M "UnityEngine.UIModule.dll"
$Orig = "$Ui.original_"
if (Test-Path $Orig) {
    if ((Get-FileHash $Ui -Algorithm SHA256).Hash -eq (Get-FileHash $Orig -Algorithm SHA256).Hash) {
        Warn "UMM injection reverted by a game update -- re-run the Unity Mod Manager installer, pick Terra Invicta, click Install, then relaunch the game"
    } else {
        Ok "UMM injection present"
    }
} elseif ((Test-Path (Join-Path $TI "winhttp.dll")) -and
          (Test-Path (Join-Path $TI "doorstop_config.ini"))) {
    Ok "UMM injection present (Doorstop)"
} else {
    Warn "no UnityModManager backup next to UnityEngine.UIModule.dll -- if DLL mods do not load, install Unity Mod Manager with the Assembly method"
}

# --- 3. Client registration -------------------------------------------------
$LoneClaude = [bool]((Get-Command claude -ErrorAction SilentlyContinue) -and
                     -not (Get-Command opencode -ErrorAction SilentlyContinue) -and
                     -not (Get-Command agy -ErrorAction SilentlyContinue))

# Every client found and left unregistered, for the closing advice: without it
# the user gets a working install with no tools in it and no idea why.
$script:UnregIds    = @()
$script:UnregLabels = @()
function Add-Unregistered($Id, $Label, $Kind) {
    # Only a fresh registration is missing. A declined REPOINT leaves a working
    # registration in place, and advising -Register for it would silently
    # perform the repoint the user just refused.
    if ($Kind -ne "fresh") { return }
    if ($script:UnregIds -contains $Id) { return }
    $script:UnregIds    += $Id
    $script:UnregLabels += $Label
}

function Should-Register($Id, $Label, $Scope, $Kind) {
    if ($RegisterGiven) {
        if ($RegisterSet -contains $Id) { return $true }
        Ok "$Label found; not in -Register, skipped"
        Add-Unregistered $Id $Label $Kind
        return $false
    }
    if ($Yes) { return $true }
    if ($Id -eq "claude" -and $Kind -eq "fresh" -and $LoneClaude) { return $true }
    if ([Console]::IsInputRedirected) {
        Warn "$Label found but not registered; non-interactive run -- re-run with -Register $Id (or -Yes for every client found)"
        Add-Unregistered $Id $Label $Kind
        return $false
    }
    $reply = Read-Host "  Register the MCP server with $Label ($Scope)? [y/N]"
    if ($reply -match '^[yY]') { return $true }
    Ok "$Label registration declined"
    Add-Unregistered $Id $Label $Kind
    return $false
}

# What a client recorded as the command to launch the server. Dead means one
# thing and nothing else: a rooted path -- the whole string, or its first token
# when arguments follow -- that is not there any more, which is what a Python
# upgrade (winget, python.org) leaves behind. A bare name is resolved through
# PATH at launch and is always live; so is anything this cannot read. Guessing
# either way would end in a rewrite of a registration that was working.
function Test-CommandLive($Text) {
    if (-not $Text)        { return $true }
    if ($Text -eq $RunExe) { return $true }
    $rooted = @($Text, ($Text -split '\s+')[0]) |
              Where-Object { $_ -and [System.IO.Path]::IsPathRooted($_) }
    if (-not $rooted)      { return $true }
    foreach ($c in $rooted) { if (Test-Path -LiteralPath $c) { return $true } }
    return $false
}

# The config object every client-agnostic snippet prints.
function Get-SnippetJson {
    $entry = [pscustomobject]@{ command = $RunExe; args = [string[]]$PyCmdArgs }
    $servers = [pscustomobject]@{ "terra-invicta" = $entry }
    return ([pscustomobject]@{ mcpServers = $servers } | ConvertTo-Json -Depth 20 -Compress)
}

# Antigravity has no MCP subcommand, so its registration is a JSON merge.
# HOME-level only: the workspace path its docs describe is read and then
# ignored, so writing one would look like it worked.
$Home_    = if ($env:USERPROFILE) { $env:USERPROFILE } else { $HOME }
$AgyDir   = Join-Path $Home_ ".gemini\config"
$AgyFile  = Join-Path $AgyDir "mcp_config.json"
$AgyLegacy = Join-Path $Home_ ".gemini\antigravity-cli\mcp_config.json"

function Read-AgyConfig($Path) {
    # Returns $null for absent-or-empty (both mean "{}"), the string
    # "UNREADABLE" for a file we must not overwrite, else the parsed object.
    if ((Test-Path $Path) -and ((Get-Item $Path).Length -gt 0)) {
        try {
            $parsed = Get-Content -Raw -Encoding UTF8 $Path | ConvertFrom-Json
            # Anything that is not a JSON object (array, scalar) is left alone.
            if ($null -eq $parsed -or $parsed.GetType().Name -ne "PSCustomObject") {
                return "UNREADABLE"
            }
            return $parsed
        } catch { return "UNREADABLE" }
    }
    return $null
}
function Write-AgyConfig($Path, $Cfg) {
    if ($null -eq $Cfg) { $Cfg = [pscustomobject]@{} }
    if (-not $Cfg.PSObject.Properties.Match("mcpServers").Count) {
        $Cfg | Add-Member -NotePropertyName "mcpServers" -NotePropertyValue ([pscustomobject]@{})
    } elseif ($null -eq $Cfg.mcpServers -or
              $Cfg.mcpServers.GetType().Name -ne "PSCustomObject") {
        $Cfg.mcpServers = [pscustomobject]@{}   # present but not an object
    }
    # Transport is implicit (command = stdio), the path is literal (no ${VAR}
    # substitution), and "type" is not a supported key here.
    $entry = [pscustomobject]@{ command = $RunExe; args = [string[]]$PyCmdArgs }
    if ($Cfg.mcpServers.PSObject.Properties.Match("terra-invicta").Count) {
        $Cfg.mcpServers."terra-invicta" = $entry
    } else {
        $Cfg.mcpServers | Add-Member -NotePropertyName "terra-invicta" -NotePropertyValue $entry
    }
    $json = $Cfg | ConvertTo-Json -Depth 20
    # No BOM: the client's JSON reader rejects one.
    [System.IO.File]::WriteAllText($Path, $json + "`r`n",
                                   (New-Object System.Text.UTF8Encoding($false)))
}

if (-not $PyExe) {
    Warn "skipped MCP client registration (needs Python)"
} else {
    # Claude Code: local scope keys off the cwd at add time, so add from $TI.
    # Every client CLI is run with empty stdin ("$null |"): a probe that reads
    # the console would swallow the keystrokes meant for the prompts below.
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        Push-Location $TI
        try {
            $Existing = $null | claude mcp get terra-invicta 2>$null
            $Found    = ($LASTEXITCODE -eq 0)
            # What the registration records: the interpreter on the Command
            # line, the server path on the Args line. A Python upgrade (winget,
            # python.org) leaves the recorded interpreter pointing at a file
            # that is gone, and nothing repairs that on its own.
            $RecCmd   = $null
            $RecArgs  = $null
            if ($Found) {
                foreach ($line in @($Existing)) {
                    if (-not $RecCmd  -and $line -match '^\s*Command:\s*(.+?)\s*$') { $RecCmd  = $Matches[1] }
                    if (-not $RecArgs -and $line -match '^\s*Args:\s*(.+?)\s*$')    { $RecArgs = $Matches[1] }
                }
            }
            $CmdLive    = Test-CommandLive $RecCmd
            $SameServer = $Found -and (($Existing -join "`n").Contains($Server))
            $AtPath     = $SameServer -and $CmdLive
            $DoAdd      = $false
            $Repoint    = $false
            $Repair     = $false
            if ($AtPath) {
                Ok "Claude Code: 'terra-invicta' registered (local scope, $TI)"
            } elseif ($SameServer) {
                # The registration the user already opted into, with a dead
                # interpreter under it. Rewriting it changes nothing they chose
                # -- same client, same scope, same server -- so it is a repair
                # and asking would only offer them a broken install. -Register
                # still holds: a run told which clients to touch touches those.
                if ($RegisterGiven -and ($RegisterSet -notcontains "claude")) {
                    Warn "Claude Code: registration points at a Python that is gone -- re-run with -Register claude"
                } else {
                    $DoAdd = $true; $Repair = $true
                }
            } elseif ($Found) {
                # Registered, but at another path. Repointing is a DECISION,
                # not a repair: this may be an extracted release or a second
                # checkout, and silently dragging a working registration over
                # to it is the wrong default. Same gate as a first-time add,
                # which also makes -Register honour it.
                $From = if ($RecArgs) { $RecArgs } else { "an existing registration" }
                $Why = if (-not $CmdLive) {
                    "REPOINT from $From to $Server; the registered Python, $RecCmd, is gone, re-register with $RunExe"
                } else {
                    "REPOINT from an existing registration to $Server"
                }
                if (Should-Register "claude" "Claude Code" $Why "repoint") {
                    $DoAdd = $true; $Repoint = $true
                }
            } elseif (Should-Register "claude" "Claude Code" "this game folder only" "fresh") {
                $DoAdd = $true
            }
            if ($DoAdd) {
                # Argument array, not a bare command line: PowerShell consumes
                # a literal `--` when the target resolves to a .ps1 shim
                # (npm installs several CLIs that way), and the server path
                # then lands as a parameter instead of a passthrough argument.
                # A native .exe is unaffected, but splatting is correct for
                # both, so neither call has to care which it got.
                $ClaudeArgs = @("mcp", "add", "terra-invicta", "--", $RunExe) + $PyCmdArgs
                $null | claude mcp remove terra-invicta 2>$null | Out-Null
                $null | & claude @ClaudeArgs | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    if ($Repair)      { Did "Claude Code: re-registered 'terra-invicta' with $RunExe (the recorded Python was gone)" }
                    elseif ($Repoint) { Did "Claude Code: repointed 'terra-invicta' at $Server" }
                    else          { Did "Claude Code: registered 'terra-invicta' (local scope, $TI)" }
                    Write-Host "        registered for $TI; start Claude Code from that folder (restart it if it is open)"
                } else {
                    Warn "Claude Code: registration failed -- claude mcp add terra-invicta -- $PyDisplay `"$Server`""
                }
            }
        } finally { Pop-Location }
    } else {
        Ok "Claude Code (claude) not on PATH"
    }

    # OpenCode: global scope only, and the non-interactive add form is
    # undocumented, so probe for it and verify that the add landed.
    if (Get-Command opencode -ErrorAction SilentlyContinue) {
        $OcList = ($null | opencode mcp list 2>$null) -join "`n"
        $OcAdd  = {
            $help = ($null | opencode mcp add --help 2>&1) -join "`n"
            if ($LASTEXITCODE -ne 0 -or -not $help.Contains("--env")) { return $false }
            # Splatted for the reason given at the Claude Code call above:
            # opencode installs via npm as a .ps1 shim, so a bare `--` on the
            # command line never reaches it.
            $OcArgs = @("mcp", "add", "terra-invicta", "--", $RunExe) + $PyCmdArgs
            $null | & opencode @OcArgs 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) { return $false }
            return ((($null | opencode mcp list 2>$null) -join "`n").Contains($Server))
        }
        $OcManual = {
            Warn "OpenCode: 'opencode mcp add' did not take the non-interactive form -- add by hand: opencode mcp add terra-invicta -- $PyDisplay `"$Server`""
        }
        # The server path being listed is the whole test. 'opencode mcp list'
        # does print the command it will run, but only inside a table row, and
        # picking the interpreter back out of that is a guess -- a wrong one
        # rewrites a global registration nobody asked to change. A dead
        # interpreter here is left alone and re-added by hand.
        if ($OcList.Contains($Server)) {
            Ok "OpenCode: 'terra-invicta' registered (global)"
        } elseif ($OcList.Contains("terra-invicta")) {
            # Same reasoning as Claude Code above: a repoint is a decision.
            if (Should-Register "opencode" "OpenCode" "REPOINT from an existing registration to $Server" "repoint") {
                if (& $OcAdd) { Did "OpenCode: repointed 'terra-invicta' at $Server" } else { & $OcManual }
            }
        } elseif (Should-Register "opencode" "OpenCode" "global -- every project" "fresh") {
            if (& $OcAdd) { Did "OpenCode: registered 'terra-invicta' (global)" } else { & $OcManual }
        }
    } else {
        Ok "OpenCode (opencode) not on PATH"
    }

    # Antigravity: file-based, HOME level. Only write where the client already
    # keeps its config; creating the tree would guess at a path it may not read.
    if (Get-Command agy -ErrorAction SilentlyContinue) {
        $AgyTarget = $null
        if (Test-Path $AgyDir)            { $AgyTarget = $AgyFile }
        elseif (Test-Path $AgyLegacy)     { $AgyTarget = $AgyLegacy }
        if (-not $AgyTarget) {
            Warn "Antigravity found but $AgyDir does not exist -- create it and add this to mcp_config.json:"
            Write-Host "        $(Get-SnippetJson)"
        } else {
            $Cfg = Read-AgyConfig $AgyTarget
            if ($Cfg -is [string] -and $Cfg -eq "UNREADABLE") {
                Warn "Antigravity: $AgyTarget is not readable JSON, leaving it alone -- add this by hand:"
                Write-Host "        $(Get-SnippetJson)"
            } else {
                $Cur = $null
                if ($Cfg -and $Cfg.PSObject.Properties.Match("mcpServers").Count) {
                    $Cur = $Cfg.mcpServers.PSObject.Properties |
                           Where-Object { $_.Name -eq "terra-invicta" } |
                           ForEach-Object { $_.Value }
                }
                $Same = $false
                if ($Cur) {
                    $Same = ($Cur.command -eq $RunExe) -and
                            ((@($Cur.args) -join "`n") -eq ($PyCmdArgs -join "`n"))
                }
                $DoWrite = $false
                if ($Same) {
                    Ok "Antigravity: 'terra-invicta' registered (global, $AgyTarget)"
                } elseif ($Cur) {
                    # Stale path: ask rather than drag an existing
                    # registration over to whatever copy this is.
                    if (Should-Register "antigravity" "Antigravity" "REPOINT from an existing registration to $Server" "repoint") {
                        $DoWrite = $true
                    }
                } elseif (Should-Register "antigravity" "Antigravity" "global -- every project" "fresh") {
                    $DoWrite = $true
                }
                if ($DoWrite) {
                    try {
                        Write-AgyConfig $AgyTarget $Cfg
                        Did "Antigravity: registered 'terra-invicta' in $AgyTarget -- restart Antigravity or run /mcp to reload it"
                    } catch {
                        Warn "Antigravity: could not write $AgyTarget -- add this by hand:"
                        Write-Host "        $(Get-SnippetJson)"
                    }
                }
            }
        }
    } else {
        Ok "Antigravity (agy) not on PATH"
    }
}

# --- 4. Offline self-test: server\stdio_selfcheck.py drives a real server
# through initialize, tools/list, a resource read and several tool calls, no
# game required, and fails on any stdout line that is not JSON-RPC: the MCP
# transport is newline-delimited JSON on stdout, so one stray print anywhere in
# the server (modcheck.py is imported by tools.py) silently kills every
# client's connection.
$Hygiene = Join-Path $Server "stdio_selfcheck.py"
if (-not $PyExe) {
    Warn "skipped server self-test (needs Python)"
} elseif (-not (Test-Path $Hygiene)) {
    Warn "server\stdio_selfcheck.py not present; skipped self-test"
} else {
    $HygieneOut = & $PyExe @PyArgs $Hygiene 2>&1
    if ($LASTEXITCODE -eq 0) {
        Ok "self-test: clean JSON-RPC"
    } else {
        Warn "server self-test FAILED:"
        $HygieneOut | ForEach-Object { Write-Host "        $_" }
        exit 1
    }
}

if ($script:UnregIds.Count) {
    Write-Host ""
    Write-Host "$($script:UnregLabels -join ', '): no terra-invicta tools there until registered. To register:"
    Write-Host "  .\install.ps1 -Register $($script:UnregIds -join ',')"
    Write-Host ""
}
Write-Host "done"
Write-Host ""
Write-Host "Any other MCP client (Cursor, Cline, Windsurf, ...): add to its MCP config:"
Write-Host "  $(Get-SnippetJson)"
