# Changelog

All notable changes to TerraInvictaMCP. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.2.0] - 2026-09-14

This release trims the project and adds a handful of tools. The existing tools
do the same things they did in 0.1.2. The codebase and docs are shorter, and a
few answers on the wire are tighter. If you use the mod through `modcheck`,
`smoke_test`, `advance` and the other tools, you will notice the additions and
fixes below. If you script against `raw`, read the Removed and Changed lists.

### Added

- Five tools wrap console commands, so you name the thing you want instead of
  spelling the console line. `trigger_event` fires one narrative event for
  your faction, and `give_org` hands an org to a councilor or a faction.
  `councilor_xp` gives a councilor experience, and `councilor_trait` adds or
  removes a trait. `nation_stat` sets any of the nine nation stats (cohesion,
  democracy, inequality, education, miltech, sustainability, nukes, unrest,
  gdp) to an absolute value. The console has no command for cohesion,
  democracy, inequality and education. The tool sets these four through the
  engine's own mutator (the `nation.set_stat` bridge verb). Because the
  console only takes a change for unrest and gdp, the tool reads the current
  value first and sends the difference.
- Every tool that sends a console line checks it first. The game's console runs
  the first command name it recognizes anywhere in the line. An org, event or
  trait name can contain the name of another command, and the console then runs
  that command instead. In that case the tool refuses and names the command
  that the console runs. The `console` tool is exempt, because it exists to
  send such a line on purpose.
- The server logs its own calls to `server/calls.log` with one line per tool
  call. Each line shows the time, the tool, how long it took, the outcome, how
  long the game clock stalled, and the arguments. When the server starts, it
  appends a marker line. When the file passes 2 MB, the server trims it.
- The `response_too_large` error carries a `head` field with the first 1,500
  characters of the reply that it dropped. You can see what the oversized
  answer was about before you narrow the request.
- A `test.autoresolve_fault` bridge verb is reachable through `raw` only. It
  records a combat autoresolve as already fired. With it, you can test the
  refusal that stops a second application of a battle. You can do this
  without firing the call whose second firing does the damage.
- Eleven more tools wrap console commands. `nation_event` covers revolutions,
  regime changes, coups, claims, secession, annexation, unification and IP
  investment. `army_edit` and `fleet_edit` set an army's strength or send it
  home, refill a fleet's delta-v, damage a ship or add an officer.
  `councilor_move`, `councilor_detain`, `turn_faction` and `force_missions`
  teleport, detain and turn councilors and toggle mission auto-success.
  `set_player_faction` switches you to an AI faction, and
  `unlock_all_ship_parts` shows every ship part, alien parts included.
  `atmosphere` adds or removes CO2 and aerosols, and `propaganda` runs one
  public-opinion push in one nation or all of them. Each tool refuses the
  inputs that the console misreads (a strength outside 0..1, a misspelled
  priority, a ship name with a comma).
- `grant` takes two more kinds. `all_techs` gives every non-endgame tech and
  completes every project that the faction qualifies for. `slot3_project`
  completes whatever sits in the faction's third research slot. It refuses an
  empty slot, where the console throws an error.
- A `nuke` tool on a new `region.nuke` bridge verb lands one nuclear strike on
  a region through the engine's own arrival path. You name the responsible
  faction and, optionally, the launching nation. The engine then charges mood,
  atrocity and retaliation to the right party. The console's `Nuke` always
  blames the active player. The reply's `severe` says which damage branch the
  engine took.
- The `select` verb has an asset slot. `slot=asset` selects into it, and
  `slot=asset` with no `id` clears it. The console wrappers clear it before
  they run. A councilor or army that you left selected in the UI no longer
  takes a command meant for something else.
- `console` takes `contains` (a regex that keeps only matching output lines)
  and `limit` (a cap on lines returned). `inspect` takes `fields` (an
  allowlist of member names, with the unmatched ones reported under
  `missingFields`) and `limit`. `query` takes `limit` for every kind except
  scenarios. Each capped reply reports `count`, `returned` and `truncated`.
- Every tool that answers in text tells Claude Code that its results can run
  to 160,000 characters. Claude Code then keeps a large reply inline and does
  not spill it to a file.
- The experimental `ai_autopilot victory_bias=true` leans the engaged
  faction's AI toward its own victory objectives. It holds focus on a running
  objective project and doubles the value of objective techs. It rates nations
  and hab sites by what the victory still needs. It is off by default.
  `status` reports how often each of the four patches changed a decision. In
  a ten-year A/B run, all four patches fired, and the faction ended further
  from its victory conditions than the plain AI did. Use it to experiment. Do
  not use it to reach a late game faster.

### Fixed

- If the start menu is still loading after `main_menu`, `campaign_new` waits
  for it. It retries for up to about eight seconds instead of failing at once.
  `smoke_test` no longer records a scenario as failed on that race. A reply
  that needed a retry says so in `handoffRetries`.
- `modcheck locale` no longer reports a missing display name for a new entry
  in a class vanilla itself leaves largely unlocalized (effects, metas, orgs,
  armies). Those were false misses on every install, but the online check
  still reports a key the engine fell back on for those classes.
- `grant` and `give_resources` send their console lines through the same guard
  as the new tools.
- `modcheck` now finds localization files kept in a subfolder such as
  `Localization/en/`. Before, a mod laid out that way had every key reported
  missing when the check ran offline.
- `modcheck` no longer flags two vanilla key shapes as phantom keys. These are
  objective keys with a faction suffix, and narrative event keys with the
  field name last.
- `modcheck` no longer prints a false "game on disk is 1.0.53" note on every
  cached run.
- `modcheck locale` reads objective keys the way the game does, which is the
  bare key for a General objective and one key per faction otherwise. This
  holds offline and online, and the online check now reports a key that
  resolves nowhere in the engine. The earlier rule that accepted either shape
  is gone.
- `give_org` refuses an org that the engine cannot place in the current
  campaign (some nation-bound orgs). The refusal says that nothing was given,
  and no null reference error occurs.

### Changed

- The "rebuild the DLL" hint appears only when the DLL itself answers "unknown
  command", so other failures no longer carry it.
- Several refusal and warning messages are shorter. These include the
  `combat.autoresolve` re-arm refusal, the `fleet.land` and `fleet.transfer`
  errors, and the `kill.module` warnings. The conditions that refuse are
  unchanged.
- `harmony.patches` always reports `harmony` as the linked 0Harmony version,
  and the old fallback reply shape for a missing Harmony is gone.
- `query.state` expands `char` members the same way as the other primitive
  types.
- `faction.diplomacy` resolves `target` first, and then it applies the
  executive-faction gate.
- `combat.stance` and `combat.autoresolve` accept exactly `Pursue`, `Defend`
  and `Evade`. They refuse numeric strings and other enum names.
- `ai.control` refuses an explicit empty `action` or `smart` rather than
  filling in a default.
- Refusals for nation and region arguments name the state type and list
  ambiguous matches as `id=dataName`.
- `campaign.new` refuses `options` given as a bare string. Pass a list.
- When no C# compiler is on PATH, `build.ps1` looks for one in two more places.
  These places are Visual Studio or the Build Tools (found through vswhere)
  and the .NET SDK (`csc.dll` run through `dotnet`). When the only `dotnet` it
  finds has runtimes and no SDK, the failure message says so.
- `query.factions`, `query.habs` and `query.fleets` answer `{items, count,
  returned, truncated}` rather than a bare array, and they take `limit`. A
  script that read the array now reads `items`.
- `query.state` members come back sorted by name, and a member dump carries
  `count`, `returned` and `truncated`.
- `select` replies carry `slot`.

### Removed

- This release removes code and documentation that repeated something already
  said elsewhere, or guarded against a DLL and server of different versions.
  The cut is about a tenth of the project, which reduces 43,111 tracked lines
  to 39,217. The docs shrank the most (`docs/PROTOCOL.md` from 212 KB to 157
  KB), and tool descriptions no longer restate PROTOCOL.md. The offline test
  suite went from 560 to 410 tests. The dropped tests covered cases that a
  surviving test already covered. No tool does anything different because of
  this.
- This release removes reply keys that never carried information. A script
  that reads one of these keys will break. By verb:
  - `spawn.fleet`: `shipsTotal`, `merged`.
  - `combat.start`: `gateFailed`, `warning`. In its `promotion` block, and in
    the same block on `combat.status`: `willPromote`, `blockedBy`, `note`,
    `factions`.
  - `combat.autoresolve`: `promotionAtArming`.
  - `mission.evaluate`: the modifier totals, `maxModifiers`, `maxRolls`.
  - `faction.diplomacy`: `spent` and `label` on the cost.
  - `faction.relations`: `note`.
  - `nation.set_stat`: `reason`, `alsoChanged`.
  - `query.templateDupes`: `source` (always null).
  - `action.list`: the primitive type aliases.
  - `ai.control` status: `holdingClockForMissionPhase`, `holdBoundTripped`
    (both always false, because the mission-phase clock hold they described
    was retired along with its checks on `time.play` and `time.speed`).

## [0.1.2] - 2026-09-13

### Fixed

- The server returned a tool result over the 160,000-character limit as
  invalid JSON marked `isError: false` (#12). It now returns a valid
  `response_too_large` error marked `isError: true`. The handler ran before
  the error, so a state-changing command can already be complete. Contributed
  by Laurentiu-Andronache (#13).

### Documentation

- `docs/playbook.md`: an "Oversized tool responses" section on what the
  diagnostic means and how to narrow a request (#13).

### Tests and CI

- `server/tests/test_results.py`: the size boundary, escaped characters
  counting toward the limit, dispatch, both `_failed` values, and the pause
  banner staying a separate content block. `test_jsonrpc.py`: the request
  after an oversized result is still answered. 560 offline tests.

## [0.1.1] - 2026-09-06

### Added

New bridge verbs (DLL) and the MCP tools that expose them:

- `design.create`, `design.delete`, `design.auto` (`design_create`,
  `design_delete`, `design_auto`): `design.create` builds a
  `TISpaceShipTemplate` from named parts in the AI builder's own field order.
  It runs the engine's ten validity predicates one by one. `design.delete`
  deletes a design and names the `canDelete` gate arm. `design.auto` runs the
  engine's autodesign search. The ship designer is a mouse-only screen, so
  this is the first headless answer to "is this part combination buildable".
  `spawn.fleet` builds ships from a saved design.
- `faction.relations` (`faction_relations` read, `set_faction_relation`
  write): read and set per-pair faction hate, both directions, with
  thresholds and mood. No console command reaches faction hate.
- `hab.build_module` (`hab_build_module`): the Habitats screen's own paying
  `BuildHabModuleAction`, with the engine's upgrade-versus-new decision and
  cost. The paying counterpart to the free `spawn.module` fixture.
- `fleet.land`: put a fleet in a hab site's `landedFleets`, the only thing
  that makes it a `fleet.bombard` target.
- `fleet.transfer`: plan and assign an orbital transfer without flying it.
  Returns `loiter_s`.
- `mission.evaluate`: a contested mission's chance and an optional seeded
  outcome-band tally, run outside the mission phase, with both modifier lists.
- `combat.stance` (`combat_stance`): submit the player's stance through the
  precombat controller. The verb refuses while `combat_autoresolve` is armed.
- `combat.precombat` (`combat_precombat`): press the precombat screen's own
  `close`, `cancel`, `reject` or `live` button for a combat the autoresolver
  refuses to retry. It has no `accept` on purpose. `accept` applies simulated
  damage, and `combat.autoresolve` owns it.
- `game.main_menu` (`main_menu`): return a loaded campaign to the start screen
  in the same process. It replays the options screen's own exit steps. It
  closes any running cinematic first and releases `ai.control`. `smoke_test`
  now cycles scenarios this way instead of relaunching the game.
- `ui.view`, `ui.screen`, `ui.status` (`ui_view`, `ui_screen`, `ui_status`):
  switch between `SolarSystem` and `PoliticalMap`, and open info screens, the
  space-object detail panel or a rename panel. `ui.status` reads the current
  view and screen back in one call.
- `ui.describe`: engine-built panel text for `module.benefits`,
  `module.summary` and `project.unlocks`, so a stat block can be asserted
  instead of screenshotted.
- `ui.options`: options screen status, `open`, `close`, `toggle`. It works
  with no campaign loaded.
- `query.autopilot`: the vanilla Autopilot macro's `Activated`,
  `ignoreExceptions`, `saveRate` and `cycleIndex`.
- `kill_module` promoted from `raw cmd=kill.module` to a tool. New
  `override_protection` flag for protected alien modules.
- `test.crash_the_game` (`crash_the_game`): raise a real unhandled exception so
  the game's own crash handler runs. The DLL and the server both refuse it
  unless `confirm="crash-the-game"`.
- `pause_limit` (read) and `set_pause_limit` (write): the session pause limit
  and this session's stall counters. There are two tools, so that a read of
  the limit is not itself refused over a stalled clock (see Changed).

New arguments and fields on existing verbs and tools:

- Every response envelope, success and error, carries `clockStall`,
  `campaignToken` and `campaignProcess`.
- `query.time` reports `campaignToken` and `campaignProcess`.
- `alert.choose` reports `narrativeBox`: true when the option-button panel is
  on, false when it is off, null when the verb cannot read the panel object.
  In that case, `narrativeBoxNote` says so.
- `saves.load` takes `extension` (`.gz` or `.json`), which says which file
  when both twins of a stem are on disk. `load_game` and `game_start` take it
  too.
- `version` and `query.time` report `crashed`. `query.time` also reports
  `missionPhase` and `stall`.
- `time.run_until` takes `force` and returns `alreadyArmed` and `missionPhase`.
- `advance` takes `narrative_events` (`true`/`"ai"`, `"llm"`, `false`) and
  `force`. The digest adds `daysAdvanced`, `narrativeEvents`, `narrativeNotes`,
  `narrativeBoxWaits`, `narrativeBoxPolls`, `missionPhase`,
  `missionPhaseWaits`, `lostPolls`, `unreadablePromptPolls`, `pauseLimit`,
  `crash`, `autopilot`, `promptsRefusal`, `consecutiveZeroProgressCalls` and
  `bridgeError`.
- `advance` stop reasons: `no_progress`, `crashed`, `crash_recovered`,
  `autopilot_off`, `prompts_unreadable`, `prompts_refused`,
  `active_player_moved`, `pause_limit`, `mission_phase`, `narrative_event`,
  `combat`, `bridge_busy`, `bridge_lost`.
- `prompts.dismiss` takes `narrative` and `narrativeMode` and reports
  `dropped`, `deadEnd`, `stillBlocking`, `single`, `optionDetail`, `pickedBy`
  and `forced`. `prompts.list` marks narrative prompts with `waitingForBox`.
  New answer handlers for `PromptSelectTrajectory` and
  `PromptSelectSpaceCombatStance`.
- `alert.choose` takes `detail` and reports `answered`, `pressLands`, and per
  option `text`, `valid`, `hidden`, `infoHiddenFromFaction`,
  `baseAIPreference` and `outcomes`.
- `combat.start` and `combat.status` report the engine's promotion gate
  (`promotion`, `gateFailed`, `willPromote`, `blockedBy`). You can then
  diagnose a fixture-built fight that the engine silently archives.
  `combat.status` also keeps the autoresolve history: `attempts`, `reached`,
  `errorKind`, `retryable`, `firedAutoresolveSelected`,
  `firedAcceptAutoresolve`.
- `combat_autoresolve` takes `wait` (default true) and `max_seconds` (default
  120) and returns the resolution, or a stall diagnosis, instead of arming and
  returning.
- `spawn.alien_site` takes `region_name` as an alternative to `region` and
  reports `extant`.
- When the path lands on a `Dictionary<K,V>`, `query.state` and `inspect`
  expand it and report what was cut by entries and by characters.
- `observe` reports `stallSession`, `pauseLimit`, `crashed` and, when stale,
  `serverCode`. `selftest` reports `offline.serverCode` and fails on drift.
- `smoke_test` reports `logNoiseIgnored` per row, `relaunchedBecause`, and the
  log-noise allowlist verbatim.
- `ti://saves` entries carry `extension`.
- Id arguments that resolve to the wrong class now say which class they hit.
- `design_create` publishes item schemas for `modules`, `nose_weapons`,
  `hull_weapons`, `fire_modes` and `armor`. A validating client then refuses
  a fractional slot or a missing armor material before it makes the call.

Server:

- Stale server code detection (`server/codestate.py`). The server hashes every
  loaded `server/*.py` at import and compares it against disk on each tool
  dispatch. The MCP server is long-lived, so an edit changes nothing until the
  client reconnects. `observe` and `selftest` now say so.

### Changed

- `advance` answers narrative events by default through the engine's own AI
  response selector and lists each one. Before, it stopped dead. `advance`
  answers only through the alert box, because the prompt and the notification
  are two queue entries. A direct answer to the prompt applied the option
  twice.
- `advance` refuses the third consecutive zero-progress call instead of
  re-arming. `force=true` overrides the refusal. `advance` caps combat arms at
  two per call. An open councilor mission phase holds the clock paused instead
  of arming `run_until` into it. A stalled Autopilot macro stops the run with
  `autopilot_off`.
- `time.run_until` is idempotent. Re-arming the same target answers
  `alreadyArmed` and no longer clears `clockParkedByDriver`.
- `combat_autoresolve` refuses a re-arm after a one-shot fired, a
  non-retryable error, a closing-phase stall, or a second attempt that
  recorded an error. It refuses entirely while `ai_autopilot` is engaged.
- `advance`'s crash recovery reloads the newest save of the run. That is an
  autosave or exit save written since the run entered its campaign, else the
  save the run loaded or last wrote. If there is none, it stops with `crashed`
  naming `load_game`. Before, it reloaded the newest save in the folder, which
  can belong to another campaign entirely.
- `save_game` records the file that it wrote. A run that saves its own scratch
  file then has something to recover to before the first autosave.
- `faction_relations`, `ui_view` and `ui_screen` are each a pure read or a pure
  write. You can still read the relations and screen state of a paused
  campaign under the pause limit.
- Over the pause limit, the server judges `raw` and `batch` by the bridge
  verbs that they carry rather than by their own names. Every verb that is a
  read (`query.*`, `assets.*`, `mods.list`, `harmony.patches`,
  `ui.screenshot`, `ui.tooltip`, `ui.describe`, `combat.status`,
  `prompts.list`, `saves.list`, `action.list`, `ping`, `verbs`, `version`)
  runs with the banner. The server refuses any other verb, as it refuses the
  tool that the verb stands in for. `raw` and `batch` used to be exempt, which
  was a hole the width of the verb table.
- `advance` stops with `bridge_lost` after five clock reads in a row go
  unanswered. Before, it spent the rest of its budget on a bridge that did not
  answer. `bridge_lost` and a terminal `crashed` now count toward the
  zero-progress refusal, because another call does not change either state.
  `crash_recovered` clears it as before.
- `destructiveHint` is now true on `game_start`, `game_stop`, `save_game`,
  `prompts` and `raw`. Clients with a confirmation policy will prompt on them.
- `spawn.fleet` accepts an orbit or a hab only. It refuses a hab site or fleet
  id by name. The old path threw an engine null reference.
- `spawn.army` bounds `strength` to 0..1. `spawn.alien_site` caps `days` at
  3650 and `level` at the engine's own ceiling.
- `prompts mode=drop` no longer silently forfeits narrative prompts. `all`
  reports them under `skipped`.
- `autopilot action=status` reads the macro's real engaged state.
- `smoke_test` separates two known engine `Log::Error` lines from
  `TISpaceShipTemplate.UnnormalizedTemplateSpaceCombatValue` from real
  failures instead of reporting a raw exception count.
- `selftest` recognizes a Unity Mod Manager DoorstopProxy install
  (`winhttp.dll` plus `doorstop_config.ini`). It looks for this install only
  when the Assembly method left no `.original_` backup.
- JSON-RPC loop: the loop handles non-object messages, `params` and `_meta`
  instead of raising. It never answers notifications. A handler exception
  returns `-32603` to that request instead of killing the process.
- `bridge.send` checks that every reply envelope is valid (object, echoes the
  id, carries `ok`). It clears the cached verb set and stall reading on any
  failure, including EOF, timeout and invalid UTF-8.
- String booleans parse correctly on every flag. `"false"` no longer reads as
  true.
- Server socket: the request parse moved outside the lock. `Stop()` closes
  connections before it drains the queue.
- Arguments are parsed by token type. An integer argument takes an integer and
  no longer rounds `2.5` to `2` or reads `true` as `1`. A numeric argument
  takes an integer or a float and no longer converts a string. With the old
  conversion, a thousands separator turned `"1,5"` into `15`. Refusals name
  the argument and the type received. This change affects every id, count,
  slot, tier and limit argument. It also affects `spawn_army strength`,
  `spawn_alien_site` landing days and xenoforming level,
  `set_faction_relation hate`, and `design_create tanks` and armor values.
- `design.create` checks the class name only when it is going to save. Under
  `save: false`, the verb registers nothing and takes no name. You can then
  check whether a loadout is valid under a name that a design or a ship in play
  already holds.
- `smoke_test` says `returnedToMenu` on each row that sent a loaded campaign
  back to the start screen. The return already ran for a named scenario and
  for a sweep, but the row said nothing about it. Before, a `campaign.new`
  refusal for a missing start menu read the same in two cases. In one case,
  the return never ran. In the other case, it ran and did not land.

### Fixed

- Narrative prompts: the screens pass pressed the first live option button as
  a neutral click and silently answered the player's story event with option
  zero. A press made before the engine accepts narrative hotkeys was discarded
  and misreported as answered. A prompt whose event target is gone can never
  be answered and now drops instead of holding the clock for the rest of the
  campaign.
- Prompt removal was measured by `RemovePrompt`'s return value, which reports
  the master list and not the mirror the clock reads. A nation handed to a
  different executive faction returned success with the prompt still blocking.
  The verb now checks that the prompt is gone from the lists that the clock
  reads.
- Skipped prompt entries were matched by prompt name, so one nation's
  undroppable prompt flagged another nation's clean drop.
- After a failed tick, `combat.autoresolve` was able to fire
  `AutoresolveSelected` or `OnAcceptAutoresolveSelected` a second time. The
  same battle's damage then applied twice. Fired flags now survive the
  disarm.
- `ai.control`'s engaged faction inside a combat left the precombat canvas up
  and the bridge reporting "resolving automatically" forever. Both the arm
  path and the settle step now name it.
- A `PromptBeginCombat` stranded by a vanished combat blocked saving and the
  clock with nothing able to clear it. The bridge now removes it. A guard stops
  the removal from dropping the prompt of a queued second combat.
- `prompts.dismiss` and `alert.choose` after a console `setfaction` ran
  human-UI handlers that died in `MapController.Fly`. Both now refuse with
  `activePlayerMoved`.
- `main_menu` with a cinematic running dereferenced the nulled render texture
  and took the process down.
- `advance` treated an unreadable prompt-queue count as an empty queue and
  spun forever. It now counts `unreadablePromptPolls` and stops at five.
- `advance` lost its digest on a transport failure. Every exit now reports.
- Save discovery saw only `.gz`. With the profile set to plain `.json`,
  `save_check` reported an empty folder, `ti://saves` listed nothing, and a
  save named exactly right was "no save named". The server now finds both
  formats. A bare name resolves to the newest file of either format, and the
  reply names the file that was read.
- The live bridge overlay wrote engine values into the vanilla cache. The
  baseline then recorded merged values as vanilla's, and every later run
  filed mod-caused findings as pre-existing. The universe now copies vanilla
  entries. `BASELINE_FORMAT` retires baselines written while the bug was
  live.
- `scenarioTags` and the tag index were able to disagree across the five merge
  modes. A disagreement leaked a scenario-only entry into every scenario or
  hid a now-universal one.
- `spawn.hab` spent an AI's pending-station orbit reservation through
  `FoundHab`. The reservation call is gone.
- NaN and Infinity walked through numeric guards on spawn and action
  arguments into `AddDays`. The bridge now refuses them centrally.
- `kill.module` computed `applied` for refused calls, read the tier after the
  template had become wreckage, and let a hab lookup error replace the real
  refusal.
- `design.create` now refuses the save on a part that the engine would drop in
  silence. The engine's validity check never reads the weapon lists, so it
  never refused such a part.
- `design.create` accepted a class name the engine already holds. The ship
  designer's name field gates on
  `TISpaceShipTemplate.illegalShipClassNames`. The engine rebuilds it on every
  access. It holds the display name of every design and of every ship in play
  that is not archived. The verb had no such gate, so a design was able to
  save under a name that the designer screen paints red. The refusal names the
  blocking design, or says a ship in play carries the class name.
- `design.create` accepted two entries on one slot. `modules`,
  `nose_weapons` and `hull_weapons` all index the same hull
  `shipModuleSlots` list. The engine saves a design with two parts on one
  slot without a word. The part that the finished ship carried then depended
  on the order in which its part lists were read. The verb expands a big
  weapon to the whole slot set that `ValidBigWeaponSlotSets` keys on its first
  slot. So the verb also catches a module on the second slot of a big
  weapon's set. The refusal names both entries.
- `spawn.fleet` and `design.delete` took the first match on an ambiguous
  design display name. Both refuse now and list the candidate data names. A
  data name match still wins outright.
- `fleet.land` and `fleet.transfer` check the null windows inside the engine's
  own `Land` and `AssignTrajectory` up front and repair them after.
- `_armed_note` no longer claims `run_until` stays armed on a budget-exhausted
  call that armed nothing.
- `Autopilot` detection read `isActiveAndEnabled`, which only says the class
  is loaded.
- Three verbs cleared everything that the mod held about a campaign by hand,
  in three drifted copies. The paths that use no verb did not clear it at
  all. One reset now runs from the watchdog on both campaign transitions, and
  the verbs call the same one.
- `game_start` cleared the autopilot macro flag even when it launched nothing.
  A call that only checks that the game is up, and a `load=` into a running
  game, both leave the macro running. With the flag cleared, `advance` stopped
  polling the macro. A macro that switched itself off on an exception then went
  unnoticed for the rest of the run.
- `smoke_test` started each scenario through the verb rather than the tool, so
  the counters and the run's recorded save still described the scenario
  before it.
- `smoke_test` let a transport failure end the whole sweep. The steps of each
  scenario now catch one and record that row as `bridge_lost`. If the calls
  timed out, the row is `bridge_busy`. `reached` names the step. The next
  scenario relaunches the game.
- `combat_autoresolve` read a failed `combat.status` poll as a clean resolve.
  The tool now counts failed polls. Five consecutive failed polls end the
  wait with `resolved: false` (`bridge_busy` when every poll timed out,
  `bridge_lost` otherwise). The orphan pass runs only on a status that the
  tool read.
- The same wait's orphan pass read the precombat screen through the helper
  that answers None on a transport failure. An unread screen therefore
  counted as a canvas that is down. That is the one reading that allows the
  prompt drop. The tool now reads the screen directly. If the read fails, the
  tool leaves the prompt alone and says that the screen was unread.
- `combat.stance` submitted with the precombat canvas down, where the
  controller still holds the fight whose report was just closed. It now
  refuses on the canvas, as `combat.precombat` and the stance prompt's own
  answer already did.
- The autoresolve one-shot record outlived the campaign it described.
  `ClearAllGameStates` restarts the state-id allocator, so the next campaign
  in the process reuses combat ids. The record then refused the first arm of
  a fight that had not happened yet. `saves.load`, `campaign.new` and
  `game.main_menu` clear it.
- `combat.status.retryable` disagreed with the arm that it predicts. It
  refused on attempts alone. It answered for the recorded combat, whatever
  combat the reply was about. Both now read one predicate.
- `time.run_until` refused the arm of an `ai.control` engagement over an open
  mission phase. At the same time, the driver skipped its own hold. An engaged
  run then met an open phase with nothing armed. The engagement is exempt
  while its `StartNewMissionPhase` prefix is installed.
- `advance` set full game speed before arming `run_until` and after a refused
  arm. This handed the clock back into the open mission phase that the refusal
  withheld it from. The arm goes first, and a phase refusal now costs no
  speed change.
- A campaign transition into a campaign reset everything that the mod held
  about one. The reset discarded a `time.run_until` armed in the same frame.
  Only the transition out resets now.
- `query.time`'s `stall.sinceLastVerbSeconds` was always 0.0. The bridge
  measured it from the current verb's own stamp, which the bridge records
  before the verb runs. The reply therefore reported no silence, however long
  the client was away. The bridge now measures it from the previous verb. It
  is null until a second verb runs.
- A stall refresh that failed left the pause limit enforcing the previous
  reading, so a game that died mid-stall answered `PAUSE LIMIT EXCEEDED`. An
  unreadable refresh is now unknown, and unknown refuses nothing.
- `game_start` and `game_stop` left a campaign entry pending, so a load into
  a game that then died bound its save to the next campaign up. Both forget
  it.
- When a client sent the level as a string, the pause limit did not read
  `time speed="0"` as a pause. The limit refuses a pause over a stopped
  clock, but it let this one pass. The server also sent the string to the
  DLL unchanged. The DLL takes the integer token and nothing else, so it
  refused the call. The server now converts the level before both steps.
- `wait_campaign=0` and `wait_campaign=1` were read as no argument at all, and
  the call waited the full default instead. The test
  `value in (None, True, False)` matches 0 and 1, because bool is an int in
  Python.
- `combat_autoresolve`'s busy stop told the caller to call it again on the
  same combat. The resolution often finishes while the polls go unanswered,
  and the tool refuses a second arm on a finished resolution. So the tool
  named that refusal as its own next step. It names `combat_status` first
  now, and the re-arm only if the machine is still armed.
- The same tool's orphan pass read `prompts.list` through the helper that
  answers None on a failure. A queue that nobody was able to read therefore
  reported no standing begin-combat prompt. After that reading, nobody looks
  again for the prompt, and that prompt holds the clock for the rest of the
  campaign.
- `alert.choose` reported the controller's narrative event record on any
  open alert box. `NotificationScreenController` never clears that record.
  A plain notification box therefore carried the name and the whole `detail`
  block of the last story event answered. The narrative option-button panel
  now decides, and a notification box reports no event.
- `alert.choose` pressed an option on a notification box, answering the last
  story event the campaign saw, and reported `answered: false`. The verb now
  refuses the press. It also refuses a null option-button panel and reports
  it as `narrativeBox: null`.
- A game process that this server did not replace kept a campaign entry
  alive. The server then took the first token of the replacement game for the
  ordered campaign. The entry now records the process that it was ordered in.
  The server drops the entry when `campaignProcess` changes.
- `smoke_test` took its log scan inside the try, so the row for a scenario
  that raised carried no `newExceptions` and no `logNoiseIgnored`. The scan
  runs after the arms and every row carries both.
- `ui.screen show=habitats rename=true` showed the habitats screen and then
  refused the rename. The caller was left with a screen change from a call
  that reported no change. The hab and ownership checks now run first.
- `advance`'s terminal crash message claimed a restart that never ran. A
  crash with no save of this run to reload now says so, and the restart
  wording is kept for a restart that completed.

### Documentation

- `docs/PROTOCOL.md`: new "Ship designs", "UI" and "Arguments" sections, "A
  combat that ends with nothing to accept" and "Arming again after a failure".
- `docs/playbook.md`: new "The pause limit", "Stop reasons", "Narrative
  events" and "Combat" sections.
- `docs/testing-your-mod.md`: "A stopped clock is treated as a stuck test".
- `AGENTS.md`, `docs/CONTRIBUTING.md`: editing `server/*.py` does not reach an
  already-started server.
- `README.md` lists the new verbs.

### Tests and CI

- 550 offline unit tests: fifteen new test modules, `test_universe` extended.
- The suite no longer reaches a running game: `server/tests/_offline.py`
  raises on any connection to the bridge port, and `test_offline_guard` fails
  a module that stops importing it.
- `test_tool_annotations` enforces the `destructiveHint` rule and
  `test_schemas` the item-schema rule across the tool table.
- CI step "no unicode dashes" fails a PR on U+2010 through U+2015 or U+2212.
  The unit-test step covers all of `server/tests`.

## [0.1.0] - 2026-08-25

Initial release.

[0.1.1]: https://github.com/MeatBunny/TerraInvictaMCP/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/MeatBunny/TerraInvictaMCP/releases/tag/v0.1.0
