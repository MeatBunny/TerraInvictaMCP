# Changelog

All notable changes to TerraInvictaMCP. Format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.2] - 2026-09-13

### Fixed

- A JSON tool result over the 160,000-character limit was sliced mid-document
  with a plain-text note appended, so the client received invalid JSON marked
  `isError: false` (#12). `json_result` now replaces an oversized result with a
  small valid `response_too_large` diagnostic carrying `textLength` and
  `textLimit`, marked `isError: true`, and `handle_call` keeps that flag
  instead of overwriting it with the handler's own `_failed` state. The handler
  has already run when the limit trips, so the message says a state-changing
  command may have completed and must not be retried blindly. Contributed by
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
- `query.time` reports `campaignToken`: a name for the campaign currently
  loaded, minted by the DLL because nothing in the engine can supply one
  (`ClearAllGameStates` restarts the id allocator, so a second campaign of a
  scenario reuses every id). It changes on each transition into a campaign,
  including the ones no verb ordered.
- `campaignProcess`, on the envelope and on `query.time`: the token's prefix on
  a key of its own, minted once at mod load, so it names the game process
  answering and is a string even with no campaign loaded. The token alone
  cannot say a game was replaced -- its counter restarts at 1 in each process
  -- so this is what tells a new campaign from a new game.
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

- Pause limit. The DLL measures clock stall on every envelope. After five
  minutes with no game-time gain (default; `TIBRIDGE_PAUSE_LIMIT` or
  `pause_limit seconds=0` changes it) every state-changing tool is refused
  with `PAUSE LIMIT EXCEEDED` and read-only tools run with a banner. Tools that
  can move the clock or the game process are exempt. One continuous stall is
  one violation however many calls it refuses.
- Crash detection and recovery. `advance` reads the DLL's `crashed` flag,
  stops the game, confirms the bridge is down, restarts with the newest save
  of the run and stops with `crash_recovered`. A second crash without progress
  is not retried.
- Stale server code detection (`server/codestate.py`). Every loaded
  `server/*.py` is hashed at import and compared against disk on each tool
  dispatch. The MCP server is long-lived, so an edit changes nothing until the
  client reconnects; `observe` and `selftest` now say so.
- `bridge.BridgeTimeout`, distinct from a dead bridge. Tools report
  `GAME_BUSY` instead of "game is not running", and one timed-out poll inside
  `advance` is one lost poll, not the end of the call.

### Changed

- Version 0.1.0 to 0.1.1 in `ModInfo.json`, `src/Verbs.cs` and
  `server/__main__.py`.
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
- `python3 -m unittest discover -s server/tests` is required for any change
  under `server/`, not only `server/modcheck.py`.
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
  in three copies that had drifted, and by nothing at all on the paths that
  use no verb. Leaving a campaign through the options screen and starting
  another one from the start screen left the run target armed, the clock
  parked, the autoresolve record standing and the mission-phase collision
  count describing a campaign that no longer existed. One reset now runs from
  the watchdog on both campaign transitions, and the verbs call the same one.
- `game_start` cleared the autopilot macro flag even when it launched nothing.
  A call that only re-confirms the game is up, and a `load=` into a running
  game, both leave the macro running, and with the flag cleared `advance`
  stopped polling it -- so a macro that switched itself off on an exception
  went unnoticed for the rest of the run.
- `smoke_test` started each scenario through the verb rather than the tool, so
  the counters and the run's recorded save still described the scenario
  before it.
- `smoke_test` let a transport failure end the whole sweep. A game that
  died in the first of six scenarios raised out of the loop: the rows
  already collected went with it, and the five scenarios after it were
  never started and never mentioned. Every per-scenario step catches one
  now and records that scenario's row as `bridge_lost`, or `bridge_busy`
  when the calls timed out, with `reached` naming the step it died in. The
  next scenario relaunches the game the way a menu return that will not
  complete already did, and a relaunch that cannot run is reported in the
  row rather than raised.
- `combat_autoresolve` read a failed `combat.status` poll as a status with
  nothing armed in it, which is the shape of a clean resolve: a bridge that
  stopped answering mid-wait produced `resolved: true`, and the orphan pass
  then took the same empty status for "no combat left" and dropped the
  begin-combat prompt of a fight still on screen. Failed polls are counted,
  five consecutive ones end the wait with `resolved: false`, and the orphan
  pass runs only on a status that was read. The stop reason is `bridge_busy`
  when every unanswered poll timed out and `bridge_lost` otherwise, the same
  split `advance` makes: the wait outlasts the verb timeout by design, so a
  resolution that runs long is the likely cause of the first.
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
  mission phase, while the driver skipped its own hold for that engagement --
  so an engaged run met an open phase with nothing armed, no clock, and no
  poll that would arm again. The engagement is exempt while its
  `StartNewMissionPhase` prefix is installed, which is what makes the
  collision impossible; without the prefix the refusal stands.
- `advance` set full game speed before arming `run_until` and after a refused
  arm, which handed the clock back into the open mission phase the refusal
  withheld it from. The arm goes first, and a phase refusal now costs no
  speed change.
- A campaign transition INTO a campaign reset everything the mod held about
  one. The request queue drains before the per-frame tick, so a driver that
  read `campaign: true` and armed `time.run_until` in that window was answered
  `armed: true` and had the target discarded microseconds later. Only the
  transition out resets now, which every teardown passes through.
- `query.time`'s `stall.sinceLastVerbSeconds` was always 0.0. It was measured
  from the current verb's own stamp, which is recorded before the verb runs,
  so the reply reported no silence however long the client had been away. It
  is measured from the previous verb now, and is null until a second verb has
  run.
- A stall refresh that failed left the pause limit enforcing the reading it
  was meant to replace, so a game that died mid-stall answered every tool with
  `PAUSE LIMIT EXCEEDED` and counted a violation, instead of the error saying
  the bridge was gone. An unreadable refresh is now unknown, and unknown
  refuses nothing and records nothing.
- `game_start` and `game_stop` left a campaign entry pending. A load ordered
  into a game that then died bound its save to whatever campaign came up next,
  which a crash recovery would have reloaded -- resuming a game nobody asked
  for while reporting a recovery. Both forget it, and a save already bound to
  another campaign is dropped rather than re-bound.
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
- `alert.choose` pressed an option on a notification box and then reported
  `answered: false`. Every press past the controller's guard builds a
  `SelectNarrativeEventOption` from the record it holds, so that press
  answered the last story event the campaign saw while the reply said it had
  answered nothing. The press is refused instead. A null option-button panel
  is refused as well and reported as `narrativeBox: null`: it used to read as
  a notification box, which is a claim the reading cannot support.
- `game_start` and `game_stop` were the only things that forgot a campaign
  entry, so a game process this server did not replace kept one alive. A
  person relaunching the game at the console left the run promising that the
  next campaign token was the one it ordered, with a save from the dead
  process behind it -- and the replacement game's first token, which its
  counter restarts at 1, was taken for that campaign. The entry now records
  the process it was ordered in and is dropped when `campaignProcess` says the
  game is a different one, before any campaign comes up.
- `smoke_test` took its log scan inside the try, so the row for a scenario
  that raised or lost the bridge carried no `newExceptions` and no
  `logNoiseIgnored` -- the one row a reader goes to for an exception was the
  one row with no log evidence on it. The scan is taken after the arms, and
  every row carries both.
- `ui.screen show=habitats rename=true` showed the habitats screen and then
  refused the rename, leaving the caller with a screen change it had been
  told nothing was changed by. The hab and ownership checks now run first.
- `advance`'s terminal crash message said the game had crashed again after
  an automatic restart when no restart had run. The recovery budget survives
  a campaign change and is spent when an attempt starts rather than when one
  completes, so a run whose first recovery died at the stop, or that carried
  a spent budget in from an earlier campaign, was told about a restart it
  never had. A crash with no save of this run to reload now says that
  instead, and the restart wording is kept for a restart that completed.

### Documentation

- `docs/PROTOCOL.md`: new "Ship designs" and "UI" sections, "A combat that
  ends with nothing to accept", "Arming again after a failure", the crash
  flag in the execution model, and a warning that `setfaction` is unsafe
  while the game runs.
- `docs/playbook.md`: new "The pause limit", "Stop reasons", "Narrative
  events" and "Combat" sections.
- `docs/PROTOCOL.md`: "Arming again after a failure" lists the five refusals
  one by one, says that attempts alone refuse nothing, and says what clears
  the record.
- `docs/playbook.md`: the pause-limit section covers the `raw`/`batch`
  allowlist, the `pause_limit` / `set_pause_limit` split and a reading that
  cannot be taken; the stop-reason list names the six reasons that count
  toward the stall refusal.
- `docs/PROTOCOL.md`: the `time.run_until` row states the engagement
  exemption and what it is conditional on, the `query.time` row states which
  verb `sinceLastVerbSeconds` measures from, and the transport section says
  that entering a campaign only mints the token.
- `docs/PROTOCOL.md`: the `design.create` row states the class-name and
  slot-collision refusals, the `spawn.fleet` row what an ambiguous display
  name does, the `alert.choose` row what decides whether the controller's
  narrative record describes the box on screen, and the `ui.screen` row the
  order the habitats rename checks run in.
- `docs/playbook.md`: a `smoke_test` sweep survives a game that dies inside
  one scenario, and a named scenario returns a loaded campaign to the menu on
  its own, so the tool can be called from inside a campaign.
- `docs/PROTOCOL.md`: the `query.time` row says that `sinceLastVerbSeconds`
  belongs to the game process rather than to the caller, and that its null
  first reading is unobservable through the tools, because `game_start` and
  `load_game` poll the bridge while they wait for the campaign. A tool call
  with a cold verb cache sends `verbs` first, and the `--call` debug entry measures
  from the previous debug call, so neither reads as the silence a persistent
  client would see.
- `docs/testing-your-mod.md`: "A stopped clock is treated as a stuck test".
- `AGENTS.md`, `docs/CONTRIBUTING.md`: editing `server/*.py` does not reach an
  already-started server; `selftest` reports it as `offline.serverCode`.
- `docs/PROTOCOL.md`: a new "Arguments" section on how a verb reads its
  arguments and what each refusal says; the transport section describes
  `campaignProcess`; the `design.create` row states that the class-name check
  is conditional on saving and gives the `fire_modes` item shape; the
  `alert.choose` row states the press refusal and `narrativeBox`.
- `docs/playbook.md`: the console section says the console is refused past the
  pause limit, through `raw` as well, and names the way out.
- `README.md` lists the new verbs.

### Tests and CI

- 550 unit tests, all passing. New files: `test_advance`, `test_bridge`,
  `test_codestate`, `test_combat`, `test_crash_fixture`, `test_jsonrpc`,
  `test_offline_guard`, `test_overlay`, `test_pause_limit`, `test_saves`,
  `test_schemas`, `test_selftest`, `test_smoke`, `test_tool_annotations`,
  `test_tool_split`. `test_universe` extended.
- The suite no longer reads a running game. Five cases dialed
  127.0.0.1:17470 and passed only because the port was closed on the machine
  that ran them; next to a paused campaign, four of them got that session's
  PAUSE LIMIT banner or the live DLL's argument error in place of the payload
  they had built. Each now holds a fake on both routes to the bridge:
  `compose.bridge` for the stall reading and the campaign note that
  `handle_call` takes before dispatch, and the `bridge` module itself for a
  tool that dispatches a verb. `server/tests/_offline.py` arms a process-wide
  guard that raises on any connection to the bridge port, and
  `test_offline_guard` fails if a test module stops importing it.
- `test_tool_annotations` enforces the `destructiveHint` rule across the
  hand-written tool table: a tool that writes is destructive unless all it
  moves is the clock or the camera.
- `test_schemas` enforces the item-schema rule across the same table: an item
  shape published by any tool states its required keys and closes itself to
  extras, because a half-stated one validates a call the bridge will refuse.
- CI step "no unicode dashes" fails a PR on U+2010 through U+2015 or U+2212
  in source, docs, scripts and config. The unit-test step now covers all of
  `server/tests`.

## [0.1.0] - 2026-08-25

Initial release.

[0.1.1]: https://github.com/MeatBunny/TerraInvictaMCP/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/MeatBunny/TerraInvictaMCP/releases/tag/v0.1.0
