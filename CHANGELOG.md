# Changelog

All notable changes to TerraInvictaMCP. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Removed

- Offline tests a surviving test already covered: 560 to 410 tests.
- Server dead code: reply-shape parsers for shapes the DLL never sends, the
  `NARRATIVE_MODES` constant, `verdict()`, the optional modcheck import, the
  disk save listing copied from modcheck, the mixed-version DLL defenses
  (`_verb_names`, `_require`, the server-side `COMBAT_ARM_LIMIT`) and
  compose's copy of the crash refusal, which the DLL makes.
- modcheck: the fallback for a missing `bridge.py`, two of the three version
  readers, `Compare.field` noops, the three-path bridge file probe, the online
  duplicate of the static array warnings and the deduper that hid it, the
  per-entry overlay fallback, unread `ModFile`, `Universe` and scenario
  fields, `unchecked`/`classify_extras`, `scenarioResolution` and
  `versionsMentioned`.
- Documentation that restated another document: the README verb families and
  live-game capability list, the AGENTS.md command list, SECURITY.md's copy of
  the README, PROTOCOL.md's engine walk-throughs and QA history, the
  playbook's third copies of verb semantics, and the CHANGELOG essays.
- Tool descriptions: the parts that restated PROTOCOL.md mechanics.
- The retired mission-phase clock hold in `AiControl.cs`:
  `HoldClockForMissionPhase`, the `Held()` gates on `time.play` and
  `time.speed`, and the two always-false `ai.control` status fields
  `holdingClockForMissionPhase` and `holdBoundTripped`.
- Scripts: the consent policy stated once, one server handshake instead of
  two, one snippet printer, the release.sh sweeps for files its staging cannot
  admit, the `zip` Python fallbacks.

### Fixed

- modcheck found no localization files for a mod that keeps them under a
  subfolder such as `Localization/en/`, so every key was reported missing
  offline; discovery walks subfolders.
- The phantom-key check accepts the two vanilla key shapes that walk exposed
  (faction-suffixed objectives, field-last narrative events).
- The false "game on disk is 1.0.53" note on every cached modcheck run.

### Changed

- The "rebuild the DLL" hint keys on the DLL's own "unknown command" refusal.
- DLL refusal and warning wording shortened (the `combat.autoresolve` re-arm
  refusal, the `fleet.land` and `fleet.transfer` throw text, the
  `kill.module` warnings); no change to which condition refuses.

## [0.1.2] - 2026-09-13

### Fixed

- A tool result over the 160,000-character limit was returned as invalid JSON
  marked `isError: false` (#12). It now returns a valid `response_too_large`
  error marked `isError: true`; the handler has already run, so a
  state-changing command may have completed. Contributed by
  Laurentiu-Andronache (#13).

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
  `design_delete`, `design_auto`): build a `TISpaceShipTemplate` from named
  parts in the AI builder's own field order, run the engine's ten validity
  predicates one by one, delete a design with the `canDelete` gate arm named,
  or run the engine's autodesign search. The ship designer is a mouse-only
  screen, so this is the first headless answer to "is this part combination
  buildable". `spawn.fleet` builds ships from a saved design.
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
  precombat controller. Refused while `combat_autoresolve` is armed.
- `combat.precombat` (`combat_precombat`): press the precombat screen's own
  `close`, `cancel`, `reject` or `live` button for a combat the autoresolver
  refuses to retry. No `accept` on purpose; that applies simulated damage and
  `combat.autoresolve` owns it.
- `game.main_menu` (`main_menu`): return a loaded campaign to the start screen
  in the same process, replaying the options screen's own exit steps. Closes
  any running cinematic first and releases `ai.control`. `smoke_test` now
  cycles scenarios this way instead of relaunching the game.
- `ui.view`, `ui.screen`, `ui.status` (`ui_view`, `ui_screen`, `ui_status`):
  switch between `SolarSystem` and `PoliticalMap`, open info screens, the
  space-object detail panel or a rename panel, and read both back in one call.
- `ui.describe`: engine-built panel text for `module.benefits`,
  `module.summary` and `project.unlocks`, so a stat block can be asserted
  instead of screenshotted.
- `ui.options`: options screen status, `open`, `close`, `toggle`. Works with no
  campaign loaded.
- `query.autopilot`: the vanilla Autopilot macro's `Activated`,
  `ignoreExceptions`, `saveRate` and `cycleIndex`.
- `kill_module` promoted from `raw cmd=kill.module` to a tool. New
  `override_protection` flag for protected alien modules.
- `test.crash_the_game` (`crash_the_game`): raise a real unhandled exception so
  the game's own crash handler runs. Refused unless
  `confirm="crash-the-game"`, checked in the DLL and again in the server.
- `pause_limit` (read) and `set_pause_limit` (write): the session pause limit
  and this session's stall counters. Two tools rather than one, so reading the
  limit is not itself refused over a stalled clock (see Changed).

New arguments and fields on existing verbs and tools:

- Every response envelope, success and error, carries `clockStall`,
  `campaignToken` and `campaignProcess`.
- `query.time` reports `campaignToken` and `campaignProcess`.
- `alert.choose` reports `narrativeBox`: true when the option-button panel is
  on, false when it is off, null when the panel object could not be read, with
  `narrativeBoxNote` saying so.
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
  (`promotion`, `gateFailed`, `willPromote`, `blockedBy`), so a fixture-built
  fight the engine silently archives is diagnosable. `combat.status` also
  keeps the autoresolve history: `attempts`, `reached`, `errorKind`,
  `retryable`, `firedAutoresolveSelected`, `firedAcceptAutoresolve`.
- `combat_autoresolve` takes `wait` (default true) and `max_seconds` (default
  120) and returns the resolution, or a stall diagnosis, instead of arming and
  returning.
- `spawn.alien_site` takes `region_name` as an alternative to `region` and
  reports `extant`.
- `query.state` and `inspect` expand a `Dictionary<K,V>` when the path lands
  on one and report what was cut by entries and by characters.
- `observe` reports `stallSession`, `pauseLimit`, `crashed` and, when stale,
  `serverCode`. `selftest` reports `offline.serverCode` and fails on drift.
- `smoke_test` reports `logNoiseIgnored` per row, `relaunchedBecause`, and the
  log-noise allowlist verbatim.
- `ti://saves` entries carry `extension`.
- Id arguments that resolve to the wrong class now say which class they hit.
- `design_create` publishes item schemas for `modules`, `nose_weapons`,
  `hull_weapons`, `fire_modes` and `armor`, so a validating client refuses a
  fractional slot or a missing armor material before the call is made.

Server:

- Stale server code detection (`server/codestate.py`). Every loaded
  `server/*.py` is hashed at import and compared against disk on each tool
  dispatch. The MCP server is long-lived, so an edit changes nothing until the
  client reconnects; `observe` and `selftest` now say so.

### Changed

- `advance` answers narrative events by default through the engine's own AI
  response selector and lists each one, where it used to stop dead. Only the
  alert box may answer: the prompt and the notification are two queue entries,
  and answering the prompt directly applied the option twice.
- `advance` refuses the third consecutive zero-progress call instead of
  re-arming; `force=true` overrides. Combat arms are capped at two per call.
  An open councilor mission phase holds the clock paused instead of arming
  `run_until` into it. A stalled Autopilot macro stops the run with
  `autopilot_off`.
- `time.run_until` is idempotent. Re-arming the same target answers
  `alreadyArmed` and no longer clears `clockParkedByDriver`.
- `combat_autoresolve` refuses a re-arm after a one-shot fired, a
  non-retryable error, a closing-phase stall, or a second attempt that
  recorded an error, and refuses entirely while `ai_autopilot` is engaged.
- `advance`'s crash recovery reloads the newest save OF THE RUN -- an autosave
  or exit save written since the run entered its campaign, else the save the
  run loaded or last wrote -- and stops with `crashed` naming `load_game` when
  there is none. It used to reload the newest save in the folder, which can
  belong to another campaign entirely.
- `save_game` records the file it wrote, so a run that saves its own scratch
  file has something to recover to before the first autosave.
- `faction_relations`, `ui_view` and `ui_screen` are each a pure read or a pure
  write, so a paused campaign's relations and screen state can still be read
  under the pause limit.
- `raw` and `batch` over the pause limit are judged by the bridge verbs they
  carry rather than by their own names. Every verb a read (`query.*`,
  `assets.*`, `mods.list`, `harmony.patches`, `ui.screenshot`, `ui.tooltip`,
  `ui.describe`, `combat.status`, `prompts.list`, `saves.list`, `action.list`,
  `ping`, `verbs`, `version`) runs
  with the banner; anything else is refused as the tool it stands in for would
  be. They used to be exempt, which was a hole the width of the verb table.
- `advance` stops with `bridge_lost` after five clock reads in a row go
  unanswered, instead of spending the rest of its budget on a bridge that is
  not answering. `bridge_lost` and a terminal `crashed` now count toward the
  zero-progress refusal, since neither is a state another call changes;
  `crash_recovered` clears it as before.
- `destructiveHint` is now true on `game_start`, `game_stop`, `save_game`,
  `prompts` and `raw`. Clients with a confirmation policy will prompt on them.
- `spawn.fleet` accepts an orbit or a hab only. A hab site or fleet id is
  refused by name; the old path threw an engine null reference.
- `spawn.army` bounds `strength` to 0..1. `spawn.alien_site` caps `days` at
  3650 and `level` at the engine's own ceiling.
- `prompts mode=drop` no longer silently forfeits narrative prompts; `all`
  reports them under `skipped`.
- `autopilot action=status` reads the macro's real engaged state.
- `smoke_test` separates two known engine `Log::Error` lines from
  `TISpaceShipTemplate.UnnormalizedTemplateSpaceCombatValue` from real
  failures instead of reporting a raw exception count.
- `selftest` recognizes a Unity Mod Manager DoorstopProxy install
  (`winhttp.dll` plus `doorstop_config.ini`), consulted only when the Assembly
  method left no `.original_` backup.
- JSON-RPC loop: non-object messages, `params` and `_meta` are handled instead
  of raising, notifications are never answered, and a handler exception
  returns `-32603` to that request instead of killing the process.
- `bridge.send` validates every reply envelope (object, echoes the id,
  carries `ok`) and clears the cached verb set and stall reading on any
  failure, including EOF, timeout and invalid UTF-8.
- String booleans parse correctly on every flag; `"false"` no longer reads as
  true.
- Server socket: request parse moved outside the lock, `Stop()` closes
  connections before draining the queue.
- Arguments are parsed by token type. An integer argument takes an integer and
  no longer rounds `2.5` to `2` or reads `true` as `1`; a numeric argument
  takes an integer or a float and no longer converts a string, where a
  thousands separator could turn `"1,5"` into `15`. Refusals name the argument
  and the type received. Affects every id, count, slot, tier and limit
  argument, plus `spawn_army strength`, `spawn_alien_site` landing days and
  xenoforming level, `set_faction_relation hate`, and `design_create tanks` and
  armor values.
- `design.create` checks the class name only when it is going to save. Under
  `save: false` nothing is registered and no name is taken, so a loadout can be
  validated under a name a design or a ship in play already holds.
- `smoke_test` says `returnedToMenu` on each row that sent a loaded campaign
  back to the start screen. The return itself already ran for a named
  scenario as well as a sweep, and the row said nothing about it, so a
  `campaign.new` refused for a missing start menu read the same whether the
  return had never run or had run and not landed.

### Fixed

- Narrative prompts: the screens pass pressed the first live option button as
  a neutral click and silently answered the player's story event with option
  zero. A press made before the engine accepts narrative hotkeys was discarded
  and misreported as answered. A prompt whose event target is gone can never
  be answered and now drops instead of holding the clock for the rest of the
  campaign.
- Prompt removal was measured by `RemovePrompt`'s return value, which reports
  the master list and not the mirror the clock reads. A nation handed to a
  different executive faction returned success with the prompt still
  blocking. Removal is now verified against the lists the clock reads.
- Skipped prompt entries were matched by prompt name, so one nation's
  undroppable prompt flagged another nation's clean drop.
- `combat.autoresolve` could double-fire `AutoresolveSelected` or
  `OnAcceptAutoresolveSelected` after a failed tick, applying the same
  battle's damage twice. Fired flags now survive the disarm.
- `ai.control`'s engaged faction inside a combat left the precombat canvas up
  and the bridge reporting "resolving automatically" forever. Both the arm
  path and the settle step now name it.
- A `PromptBeginCombat` stranded by a vanished combat blocked saving and the
  clock with nothing able to clear it. It is now removed, guarded against
  dropping a queued second combat's prompt.
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
  save named exactly right was "no save named". Both formats are found, a bare
  name resolves to the newest of either, and the file read is named.
- The live bridge overlay wrote engine values into the vanilla cache, so the
  baseline recorded merged values as vanilla's and filed mod-caused findings
  as pre-existing on every later run. The universe now copies vanilla
  entries, and `BASELINE_FORMAT` retires baselines written while the bug was
  live.
- `scenarioTags` and the tag index could disagree across the five merge
  modes, leaking a scenario-only entry into every scenario or hiding a
  now-universal one.
- `spawn.hab` spent an AI's pending-station orbit reservation through
  `FoundHab`; the reservation call is gone.
- NaN and Infinity walked through numeric guards on spawn and action
  arguments into `AddDays`; refused centrally now.
- `kill.module` computed `applied` for refused calls, read the tier after the
  template had become wreckage, and let a hab lookup error replace the real
  refusal.
- `design.create` refused the save when the engine would drop a part in
  silence; its validity check never reads the weapon lists.
- `design.create` accepted a class name the engine already holds. The ship
  designer's name field gates on
  `TISpaceShipTemplate.illegalShipClassNames`, rebuilt on every access from
  every design's display name plus the display name of every ship in play
  that is not archived, and the verb had no such gate: a design could save
  under a name the designer screen paints red. The refusal names the
  blocking design, or says a ship in play carries the class name.
- `design.create` accepted two entries on one slot. `modules`,
  `nose_weapons` and `hull_weapons` all index the same hull
  `shipModuleSlots` list, and the engine saves a design with two parts on
  one slot without a word, so which part the finished ship carried depended
  on the order its part lists were read in. A big weapon is expanded to the
  whole slot set `ValidBigWeaponSlotSets` keys on its first slot, so a
  module on the second slot of a big weapon's set is caught as well. The
  refusal names both entries.
- `spawn.fleet` and `design.delete` took the first match on an ambiguous
  design display name. Both refuse now, listing the candidate data names;
  a data name match still wins outright.
- `fleet.land` and `fleet.transfer` null windows inside the engine's own
  `Land` and `AssignTrajectory` are checked up front and repaired after.
- `_armed_note` no longer claims `run_until` stays armed on a budget-exhausted
  call that armed nothing.
- `Autopilot` detection read `isActiveAndEnabled`, which only says the class
  is loaded.
- Everything the mod held about a campaign was cleared by hand in three verbs,
  in three drifted copies, and not at all on the paths that use no verb. One
  reset now runs from the watchdog on both campaign transitions, and the verbs
  call the same one.
- `game_start` cleared the autopilot macro flag even when it launched nothing.
  A call that only re-confirms the game is up, and a `load=` into a running
  game, both leave the macro running, and with the flag cleared `advance`
  stopped polling it -- so a macro that switched itself off on an exception
  went unnoticed for the rest of the run.
- `smoke_test` started each scenario through the verb rather than the tool, so
  the counters and the run's recorded save still described the scenario
  before it.
- `smoke_test` let a transport failure end the whole sweep. Each scenario's
  steps now catch one and record that row as `bridge_lost`, or `bridge_busy`
  when the calls timed out, with `reached` naming the step; the next scenario
  relaunches the game.
- `combat_autoresolve` read a failed `combat.status` poll as a clean resolve.
  Failed polls are counted, five consecutive ones end the wait with
  `resolved: false` (`bridge_busy` when every poll timed out, `bridge_lost`
  otherwise), and the orphan pass runs only on a status that was read.
- The same wait's orphan pass read the precombat screen through the helper
  that answers None on a transport failure, so an unread screen counted as a
  canvas that is down -- the one reading that allows the prompt drop. The
  screen is read directly now, and a read that fails leaves the prompt alone
  and says the screen was unread.
- `combat.stance` submitted with the precombat canvas down, where the
  controller still holds the fight whose report was just closed. It now
  refuses on the canvas, as `combat.precombat` and the stance prompt's own
  answer already did.
- The autoresolve one-shot record outlived the campaign it described.
  `ClearAllGameStates` restarts the state-id allocator, so the next campaign
  in the process reuses combat ids and the record refused the first arm of a
  fight that had not happened yet. `saves.load`, `campaign.new` and
  `game.main_menu` clear it.
- `combat.status.retryable` disagreed with the arm it predicts, refusing on
  attempts alone and answering for the recorded combat whatever combat the
  reply was about. Both now read one predicate.
- `time.run_until` refused an `ai.control` engagement's arm over an open
  mission phase while the driver skipped its own hold, so an engaged run met
  an open phase with nothing armed. The engagement is exempt while its
  `StartNewMissionPhase` prefix is installed.
- `advance` set full game speed before arming `run_until` and after a refused
  arm, which handed the clock back into the open mission phase the refusal
  withheld it from. The arm goes first, and a phase refusal now costs no
  speed change.
- A campaign transition INTO a campaign reset everything the mod held about
  one, discarding a `time.run_until` armed in the same frame. Only the
  transition out resets now.
- `query.time`'s `stall.sinceLastVerbSeconds` was always 0.0. It was measured
  from the current verb's own stamp, which is recorded before the verb runs,
  so the reply reported no silence however long the client had been away. It
  is measured from the previous verb now, and is null until a second verb has
  run.
- A stall refresh that failed left the pause limit enforcing the previous
  reading, so a game that died mid-stall answered `PAUSE LIMIT EXCEEDED`. An
  unreadable refresh is now unknown, and unknown refuses nothing.
- `game_start` and `game_stop` left a campaign entry pending, so a load into
  a game that then died bound its save to the next campaign up. Both forget
  it.
- `time speed="0"` was not read as a pause by the limit, so the one call that
  stops the clock got through under a stopped clock whenever a client sent the
  level as a string. The two sides of that argument now agree: the level is
  coerced the same way on the way out to the DLL, which takes the integer
  token and nothing else and used to refuse the string it was handed.
- `wait_campaign=0` and `wait_campaign=1` were read as no argument at all and
  waited the full default instead, because `value in (None, True, False)`
  matches 0 and 1: bool is an int in Python.
- `combat_autoresolve`'s busy stop told the caller to call it again on the
  same combat. The resolution often finishes while the polls are going
  unanswered, and a second arm on a finished one is refused -- so the tool
  named the refusal as its own next step. It names `combat_status` first now,
  and the re-arm only if the machine is still armed.
- The same tool's orphan pass read `prompts.list` through the helper that
  answers None on a failure, so a queue nobody could read reported no standing
  begin-combat prompt -- the reading that stops anyone looking again for the
  prompt that holds the clock for the rest of the campaign.
- `alert.choose` reported the controller's narrative event record on any
  open alert box. `NotificationScreenController` never clears that record,
  so a plain notification box carried the name and the whole `detail` block
  of the last story event answered. The narrative option-button panel is
  now what decides, and a notification box reports no event.
- `alert.choose` pressed an option on a notification box, answering the last
  story event the campaign saw, and reported `answered: false`. The press is
  refused; a null option-button panel is refused too and reported as
  `narrativeBox: null`.
- A game process this server did not replace kept a campaign entry alive, so
  the replacement game's first token was taken for the ordered campaign. The
  entry records the process it was ordered in and is dropped when
  `campaignProcess` changes.
- `smoke_test` took its log scan inside the try, so the row for a scenario
  that raised carried no `newExceptions` and no `logNoiseIgnored`. The scan
  runs after the arms and every row carries both.
- `ui.screen show=habitats rename=true` showed the habitats screen and then
  refused the rename, leaving the caller with a screen change it had been
  told nothing was changed by. The hab and ownership checks now run first.
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
