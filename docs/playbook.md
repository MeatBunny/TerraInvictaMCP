# Terra Invicta driving playbook

This guide shows how an agent drives Terra Invicta through this MCP (Model
Context Protocol) server. The in-game DLL runs the bridge, a small TCP
server on `127.0.0.1:17470`, and answers verbs. Each verb is one named
request, finished inside a single frame (wire contract:
`ti://docs/protocol`). The tools own sequencing and judgment. The
`game_start`/`game_stop` tools own the launch cycle on every platform. The
`log_tail`, `save_check`, and `ti://saves` surfaces know where saves and
Player.log are: in the user profile on Windows, and in the Proton prefix on
Linux.

## Session shape

1. Always run `observe` first. It reports bridge and campaign state and names
   the next call.
2. If the game is down, run `game_start`. It launches through Steam and polls
   the bridge until the bridge is up (20-60s). `game_start load=<save>` also
   loads a save and waits for the campaign.
3. Scratch-save rule: never experiment on a campaign a person is playing.
   Run `save_game name=scratch-<topic>` first and work on that copy.
4. Work.
5. When you are done, run `game_stop`. Never leave the game running idle.
   When the process stops, unsaved progress is lost.

## Oversized tool responses

The Python server limits a serialized JSON tool payload to 160,000 characters.
A response that exceeds this limit returns `isError: true` and a complete JSON
diagnostic with `error: "response_too_large"`, `message`, `textLength`,
`textLimit` and `head`. The server omits the original payload. `head` is the
first 1,500 characters of the serialized JSON. This prefix is not parseable on
its own. The size counts the serialized text with its JSON escapes. It does
not count the surrounding MCP envelope. Pause-limit banners stay in separate
content blocks.

This error is a failure to deliver the response after the handler ran. The
operation can already be complete. Before you retry a command that changes
state, inspect the current state. If the tool supports `limit`, `fields` or
`contains`, use them to narrow a read. `console contains=<regex>` keeps only
the matching output lines. `console limit=N` caps the number of lines.
`count`, `returned` and `truncated` report the cut. `inspect fields=[...]`
dumps only the named members. `inspect limit=N` caps the member count.
`query limit=N` caps every kind except scenarios, with the same three fields
on the reply. For custom console commands, make the producer return a smaller
result. Do not interpret missing output as a missing command or a failed
mutation. Claude Code keeps results up to 160,000 characters inline because
the server declares that size hint (`_meta["anthropic/maxResultSizeChars"]`)
on every JSON tool.

## The blocked clock

Pending prompts, open alert boxes, modal screens, and unresolved combat
freeze the strategy clock. No game time passes until all of them are gone.
A run that hangs is almost always blocked. `observe` reports the cause.

`advance` owns the unattended loop. It handles max speed, `run_until`,
prompt dismissal, and combat autoresolve. By default it answers only prompts
with a neutral answer. When a real decision blocks the clock, it returns early
with the alert text and options. Make the decision with
`alert_choose option=N`, then call `advance` again. `advance` runs in chunks
no longer than its `max_seconds` budget and tells you how to continue.
`prompts mode=drop` force-drops an ordinary prompt. It also answers narrative
events on the way past. `mode=drop` sends the same `prompts.dismiss` call as
`mode=answer` and adds only the `all` argument. So the rest of the pass runs
unchanged. The screens pass still presses an option button on any narrative
alert that is open.
`narrative_events=false` leaves a narrative event untouched for
`alert_choose`. With `narrative_events=llm`, an unattended run hands the event
to you and does not answer it (see "Narrative events").

### The pause limit

The pause limit is the longest time that the game can go without gaining
game time. The default is five minutes. `set_pause_limit seconds=N` sets a
different limit for the session, and `0` removes the limit.
`TIBRIDGE_PAUSE_LIMIT` sets the value that the server starts with.
`pause_limit` reads the current value. That read is never refused and never
carries the banner. An agent calls it to find out why everything else is
refused.

Two things count as a stall. The first is a clock that does not move at all.
The second is a clock that crawls, and it is the one that surprises people. A
crawl is speed 1 or 2 with no `run_until` armed. The date ticks over every
frame, but an hour of real time gives only a day of game time. In both cases,
the test is not running.

Past the limit, every tool that changes state is refused with
`PAUSE LIMIT EXCEEDED`. The refusal names the state, the prompt that holds the
clock, and the time since the last verb ran. Read-only tools still run, and
they start their answer with the same banner. Thus a read of a stopped
campaign is never blocked. The tools that move the clock, clear what blocks
it, or end and restart the run are never refused. These tools are `advance`,
`time`, `prompts`, `alert_choose`, the four `combat_*` tools, `autopilot`,
`ai_autopilot`, `game_start`, `game_stop`, `main_menu`, `load_game`,
`campaign_new`, `save_game`, `crash_the_game`, and `set_pause_limit`. For
`time`, `action=pause` and `speed=0` are not exempt, in any spelling that the
wire allows, because they pause the clock. `set_pause_limit` is the way out of
the limit itself.

**`raw` and `batch` are judged by the verbs they carry.** Their own names do not
tell whether a call reads or writes. When the limit judged them by name, every
refused write got through under another spelling. If every verb in a call is a
read, the call runs with the banner. The read verbs are `query.*`, `assets.*`,
`mods.list`, `harmony.patches`, `ui.screenshot`, `ui.tooltip`, `ui.describe`,
`combat.status`, `prompts.list`, `saves.list`, `action.list`, `ping`, `verbs`
and `version`. Any other verb is refused the same way as the tool that it stands
in for. This includes a verb that the allowlist does not know.

If the server cannot read the stall time, it refuses no tool and records no
violation. When `query.time` fails or times out, or when no campaign is
loaded, the stall time is unknown.

Only a gain of game time clears a stall. A successful tool call, a fixture,
or a save does not clear it. `advance` stops with `stopReason: pause_limit`
and does not spend its budget on a clock that is not moving. Three rules keep
`advance` the way out of a stall. First, it never stops on the limit before
it runs one full pass of its loop. Second, it does not stop while it works a
hold that it opened. Third, `force=true` removes the stop entirely.

**A recognized hold reaches its own stop first. The limit is for stalls outside
those holds.** Each of these holds has its own bound: a standing decision, a
narrative box, a combat, an open mission phase, and an unreadable prompt queue.
Each bound counts polls instead of minutes, and each is shorter than the limit.
When `advance` meets one of these holds, it returns `decision`,
`narrative_event`, `combat`, `mission_phase` or `prompts_unreadable`. It also
returns a `next` that tells you what to do. `pause_limit` never fires on these
holds. Inside a call, the hold reports a blocked clock, and the limit does not.
Expect the limit on a stall that nothing recognizes. That stall is a clock that
stays stopped between calls while an agent builds fixtures, thinks, or waits for
something that will not come.

`observe` warns when a stall passes half the limit. Past the limit, `observe`
starts with the banner and the violation record. `observe` always carries
`stallSession`. Thus an orchestrator that reads `observe` after a run sees a
stall that the run cleared later. The DLL also writes one line to Player.log
for each whole five minutes of a stall
(`log_tail which=player pattern='clock stalled'`).

**Fixture setup must fit inside the limit.** The limit catches exactly the
pattern of building campaign state one step at a time. That work happens with
the game paused, and none of it moves the clock. Keep a setup sequence inside
the limit, or call `advance days=1` between steps. That call costs a second or
two and clears the measurement. If a setup must run with the clock stopped,
treat it as a deliberate exception. Run `set_pause_limit seconds=<bigger>` for
that session, and say in the report that you raised the limit. Every `observe`
carries the current limit and the time of its last change. Thus a raised limit
is always visible.

**Read `daysAdvanced` before you read `next`.** Every digest ends with an
instruction to call again. If the last call did not move the clock, do not obey
that instruction. A call that moved zero days reports `stopReason: no_progress`
instead of `max_seconds`. `advance` counts consecutive calls that moved no days.
The second such call says that the run appears stalled and names what to look
at. The third is refused outright and is not re-armed. When you know why the
clock is not moving, `force=true` runs the call anyway.

## Stop reasons

These stop reasons do not mean "ask for more time":

- **`crashed` / `crash_recovered`** -- the game hit its own crash handler. The
  bridge survives the crash. Thus every tool keeps answering over a dead game,
  and the frozen clock looks like a modal alert. To recover, `advance` restarts
  the process once. It reloads one save of this run and names what was lost. It
  prefers the newest autosave or exit save written after the run entered its
  campaign. Without one, it uses the save that the run loaded or last wrote. If
  neither save exists, or the game crashes a second time, `advance` stops with
  `crashed` and names `load_game`. To test this without waiting for a real
  crash, see `crash_the_game` under "Testing mods".
- **`combat`** -- a fight the autoresolver will not retry. See "Combat" below.
- **`autopilot_off`** -- the UI macro switched itself off. See "Autopilot and
  the faction AI".
- **`prompts_unreadable`** -- the clock is frozen and the prompt queue was
  unreadable. An unreadable queue is different from an empty queue. Read
  `prompts` and `log_tail which=player`.
- **`narrative_event`** -- with `narrative_events=llm` only. A story event's box
  is open, and you make the decision. The options are in `alert`, and `next`
  lists them. Answer with `alert_choose option=N` and call `advance` again. See
  "Narrative events".
- **`active_player_moved`** -- the active-player seat moved off the faction that
  the campaign started with. Only `setfaction` does this. The prompt pass
  refused to answer anything. Those answers go through the human UI's handlers.
  The handlers read the seated faction's councilors and crash on a faction that
  the human is not playing. Nothing was answered, dropped or closed. The
  refusal's `seatedFaction` field names the faction to restore. To restore it,
  run `console line="setfaction <that faction>"`, then call again. Reads still
  work. An `ai_autopilot` engagement does not trip this stop, because the
  engagement leaves the seat where it is. If this server has no name for a
  refusal, the refusal arrives as `prompts_refused` with the DLL's own text.
- **`bridge_busy`** and **`bridge_lost`** -- the call ended on the transport and
  not on the game. Busy means a stalled main thread with the game up. Lost means
  a bridge that stopped answering. That is a closed socket, or five clock reads
  in a row with no answer. Each of those reads waited the full verb timeout. A
  series of them is minutes of silence and not a slow poll. The call stops and
  does not spend the rest of its budget on them. For both, run `observe` next.
  It reports whether the process still exists. That fact tells a busy main
  thread apart from a game that is gone.
- **`pause_limit`** -- the game clock went past the pause limit without gaining
  time. The call stopped and did not spend its budget on a clock that is not
  moving. The digest carries the violation record: state, blocking prompt,
  seconds since the last verb. See "The pause limit" above.
- **`mission_phase`** -- a councilor mission phase stayed open for half a minute
  of polling, so `advance` leaves the clock paused. While a phase is open,
  `advance` does not start the clock in any form. It sends no `time.speed`, no
  `run_until` and no `time.play`, and it pauses a running clock. It holds the
  clock because a semimonthly tick that lands inside an open phase corrupts the
  phase. `advance` still runs its dismiss pass on every poll. That pass presses
  the mission-assignment confirmation that closes the player's phase. The digest
  carries `missionPhaseWaits` and the phase block. The phase ends when every
  faction signals its assignments. Read `prompts` for a mission-assignment
  confirmation that is still queued. Read `log_tail which=game` for a planning
  exception. `advance force=true` runs anyway and accepts the collision. Under
  an `ai_autopilot` engagement, no hold occurs. The mod's own guard defers a
  colliding tick instead.

Most of those stop reasons give you something to do and do not count toward
the stall refusal. Six do count: `mission_phase`, `pause_limit`,
`active_player_moved`, `prompts_refused`, `bridge_lost` and a terminal
`crashed`. A call that used up its budget and did nothing at all also counts.
Each of these means that nobody drives the game: `mission_phase` is a phase
that nothing here can close, `pause_limit` is a clock that does not move for
minutes, `active_player_moved` and `prompts_refused` are a pass that will not
run until the seat changes, `bridge_lost` is a bridge that does not answer,
and a terminal `crashed` is a game that crashed again after its restart. If
you call again over any of them without a change, you make the loop that the
refusal exists to break. `crash_recovered` is the opposite case and clears the
counter. The game came back, and the next call has a live game to run.

A bridge timeout during the loop is one lost poll, counted in `lostPolls`. It
does not end the call. Five timeouts in a row end the call as `bridge_lost`.
The game answers verbs on its main thread. A large save, an asset load or a
scene change stalls them past the 30s call budget while the process is alive.
A tool that times out says that the game is UP and tells you to wait. Only a
closed socket tells you to call `game_start`.

## Narrative events

`advance` also answers narrative event popups with the engine's own AI option
strategy. Thus a story event does not stop the run. A press on any option
button of the notification controller answers the event. Read
`narrativeEvents` in the digest. It lists every event that `advance` answered.
Each entry has the event dataName, the option index, the button text, what
that option does (`optionDetail`), and the rule that picked it.
`narrativeNotes` carries the exceptions: an event left open, or an option
pressed with no readable event behind it. It also carries an event whose
target was already gone, so the engine applied nothing, and a prompt dropped
with no option applied.

`narrative_events` sets who decides. It takes three values:

- **`true` or `"ai"`** (the default) -- the engine's own option strategy presses
  a button and the run keeps going.
- **`"llm"`** -- you decide. The first poll that finds an open story-event box
  with live buttons ends the call. `stopReason` is `narrative_event`, the
  options are in `alert`, and `next` lists them. Answer with
  `alert_choose option=N` and call `advance` again. If the outcomes matter, run
  `alert_choose detail=true` first. It reports the outcomes, chances, effects,
  costs and grants of each option from the event template. It also shows the
  options that the popup hides because of your faction's ideology. The handover
  counts as progress. It never trips the stall refusal, however many events a
  run hands over.
- **`false`** -- **this mode is for an attended run only.** Nothing in the
  harness ever answers a story event. An unattended run in this mode stops on
  the first event and again on every call after it. When a person is at the
  screen, use `false`. When an agent drives, use `llm`.

An event with one option is pressed in every mode, `false` included, because
it holds no decision. The digest reports it like any other answer, with
`single: true` and `optionDetail`. Thus every applied option is named.

`advance` answers a narrative event only through its alert box. If `advance`
sees a narrative prompt before its box opens, it reports the prompt as skipped
and takes it on a later poll. It does not answer through the prompt queue,
because the box would then apply the same choice a second time. The box queues
behind any notification on screen, so it can trail its own prompt by a poll or
more. `advance` waits for the box and does not report a decision. It counts the
waits in `narrativeBoxWaits` and stops only if the box never arrives.

The box, and not a counter, drives the wait. `advance` reads `alert_choose`
on every poll of a narrative hold. It tells two states apart and waits a
different time for each. A box that is not rendered yet gets a long wait,
because the drain can take most of a minute. A box that stands open with live
option buttons gets a short wait, because the screens pass takes an open box
on its next pass. A box that stays open across several polls with its prompt
still queued means that the controller refuses the press. That is a real hold.
The stop message says which case happened: a box that never rendered, or a
press that the controller refused. `narrativeBoxPolls` counts the polls
during which the box was open.

One kind of narrative prompt is dropped instead. Nothing can answer it,
because its event template is missing or its target was killed. When the
target is gone, the alert box applies no option. It logs the target away,
closes itself, and does not come back. Without the drop, the prompt holds the
clock for the rest of the campaign. The drop runs for every value of
`narrative_events`.

## Combat

`ai_autopilot action=engage` changes the shape of the `advance` loop. When the
faction is AI-controlled, prompts go to the AI and not to the player queue.
Thus most things that block the clock never queue. Combat is the exception.
The rest of this section covers it.

Only one combat runs at a time in the whole game. If a request comes while
another combat is unresolved, the request is parked on the fleet and is not
lost. Same-faction pairs are refused. A faction on both sides wedges the
simulation permanently. AI-vs-AI combats resolve themselves. A combat that the
player is in waits for `combat_autoresolve`. That tool also closes the
post-combat report. To let a human fly a fight, do not autoresolve it. The
block stays until a person takes it.

**Taking a fight by hand.** The precombat screen asks the player for two things,
and each has its own answer. The stance is `combat_stance stance=Defend` (or
Pursue, or Evade). It submits through the screen's own button. It checks the
stance against what the combat allows for that faction. It reports the stance
that it reads back from the combat, and whether the stance prompt left the
queue. The tool has no faction or combat argument. The engine's submit takes
both from the live screen and takes only the stance from its caller. If a
precombat screen is open and nothing is autoresolving, `prompts` answers this
prompt on its own with Defend. Use `combat_stance` to pick a different stance.
`advance` never reaches this prompt. An unresolved combat sends that loop to
`combat_autoresolve` before its prompt pass runs, and the machine submits the
stance itself. `combat_stance` is refused while `combat_autoresolve` is armed,
because two writers would then act on one screen. `combat_stance` is also
refused when the precombat canvas is down. The controller outlives the screen
and still holds the last fight. A submit there answers a fight whose report is
already closed.

Nothing answers the other prompt, `PromptBeginCombat`, and nothing ever will.
Every button that clears it commits an outcome. The buttons are accept, close,
cancel, reject and live, and `combat_autoresolve` owns accept. Thus this prompt
has no neutral answer. Use `combat_precombat`. Do not drop the prompt. The
canvas, and not the prompt, freezes the clock.

**Building a fight with the fixtures, and the trap in checking it.** The whole
recipe is two `spawn_fleet` calls into the same orbit for two different
factions, then `combat_start`. The engine promotes the combat a frame or more
later. Until then, `combat_status` reports `fleets: [null, null]` and
`hab: null` on a healthy combat. Read `promotion` next to them, and do not trust
`fleets`. `promotion` carries the engine's own cached fleet and hab references.
If a combat vanishes right after `combat_start`, the engine deleted it. Fix the
fixture.

`combat_autoresolve` does not retry a failed autoresolve. It refuses a re-arm
after a one-shot fired, a stable error, a closing-phase stall, or a second
attempt that recorded an error. The refusal names the combat, the phase and
the error. The attempt count alone refuses nothing. `advance` stops on the
refusal and does not use up its budget.

`combat_autoresolve` waits for the resolution itself and reports what it did. If
it stalls, it stops the wait and reports a diagnosis: the phase reached, the
one-shot that fired, and the precombat canvas with its live buttons. If a combat
vanishes before there is anything to accept, `PromptBeginCombat` stays. The
clock stays frozen, and saving is blocked. The DLL clears the prompt when its
machine finds the combat gone and no canvas left. **After that, the tool drops
the prompt only with the canvas down, and only after it read both the status and
the screen.** During this wait, `bridge_busy` means that the polls timed out
with the game up. Another `combat_autoresolve` on the same combat enters the
wait again and does not arm again. A combat that ends with nothing to accept has
two causes. The "A combat that ends with nothing to accept" section of
`ti://docs/protocol` names them.

**When it refuses, do not drop the prompt.** The precombat canvas freezes the
clock, and only a button closes it. `combat_precombat action=status` reports
which buttons are live. `action=cancel` calls the attack off. `action=close`
closes the report. `action=reject` and `action=live` hand the fight to the
tactical layer, which nothing headless drives. There is no `accept`, because
`combat_autoresolve` owns that call. A dead end after the precombat interaction
ended can leave no button. Then a person must clear it.

## Autopilot and the faction AI

Two different things play the player faction. They are not interchangeable.

`autopilot` is the game's own UI macro. It never sets `isAI`, so
`AIDailyFactionPlanner` never runs for your faction. It dismisses prompts,
picks a random available tech on a tech prompt, and sells the first sellable
org. It auto-resolves combat stances and force-advances the clock. The
faction keeps its human difficulty treatment. Thus its play is no sample of
AI play. The random tech pick makes it wrong for anything whose outcome
depends on the tech path. It is still the cheap way to get campaign time.
`save_cycles=N` autosaves every N cycles.

**Set `ignore_exceptions=true` for any unattended run.** Without it, the macro
switches ITSELF off and pauses on the first exception in game code. It reports
this only in the player log. The run then keeps resuming a clock that nobody
drives. When you hunt a crash, leave it unset. That is the case it exists for.
`autopilot action=status` reads back the macro's own engaged state. While the
macro is on, `advance` polls the same reading. When the state flips, `advance`
stops with `stopReason: autopilot_off`.

`ai_autopilot action=engage` hands the faction to the real planner. The
planner plans its councilors, research, nations, wars, habs and fleets and
answers its own prompts. When you want the campaign to develop the way AI
play develops, use it. Before you test anything that depends on the faction
being yours, release the engagement.

- Combat still takes the human path. Precombat control compares the active
  player by reference, and the engagement never reassigns the active player.
  A fight that your faction is in posts a begin-combat prompt that nothing
  clicks. Both planner gates starve behind it. Drive engaged stretches
  through `advance`, which autoresolves, instead of bare `time`.
  `combat_autoresolve` cannot arm on a combat that the engaged faction is in.
  The engagement makes that faction report as not the active player. But the
  precombat controller's own cached reference still treats it as the player.
  So the canvas comes up and holds the clock. `combat_autoresolve` refuses and
  says so. It does not report the fight as resolving on its own. Release the
  engagement and autoresolve, or answer the screen with `combat_precombat`.
- The faction stops getting notifications, like every AI faction. If an alert
  is on screen when you engage, the engagement does not drain it. Answer it
  with `alert_choose` first (a game restart also clears it).
- `smart=brutal` is the default. It changes the difficulty to Brutal during
  each planning call and holds game speed at maximum. Thus an unattended run
  gets as far as it can. For pace, `give_resources` and `grant` do more than
  the difficulty swap does.
- The engagement has side effects, and the release reverts all of them. Steam
  achievements do not unlock. Mission resolution treats the faction as AI for
  the difficulty modifier (zero at Normal). Other AI factions become uniformly
  willing to gang up on it.
- Saves keep the vanilla shape. The flag is cleared during every write. Thus
  a save taken while engaged loads with the faction back under your control.
  If the campaign changes under the engagement, the engagement releases
  itself.
- The clock never pauses while engaged. The engagement's own tick resumes the
  game's phase-start pauses a frame later (`status` counts them as
  `resumedPauses`). When a semimonthly mission-phase tick would land while the
  previous phase's machinery is still busy, the engagement skips that tick, and
  no collision occurs (`deferredPhaseTicks`). A steady climb in that count means
  that planning outruns the phase period. That is harmless. Only the pauses that
  you ask for through the time tools stay: pause, speed 0, or a fired
  `run_until`.
- The two never run together. The macro plays the faction itself, so `engage`
  refuses while `autopilot` is on.

## Time, manually

Before you wait for game days, run `time speed=5`.
`time run_until=YYYY-MM-DD` arms an auto-pause. `time action=status` polls.
Prompts still freeze the clock. `advance` automates this loop.

## Campaign entry

- `load_game` works from the main menu and tears the session down while it
  loads. Poll `observe` until `campaign: true` (20-60s).
- `main_menu` is the way back. It unloads the loaded campaign and returns to the
  start screen in the same process. Thus a second scenario does not need a
  `game_stop` and a `game_start`. It uses the engine's own exit-to-menu path.
  The campaign is gone, and so is anything unsaved. If unsaved state matters,
  run `save_game` first. The tool waits until the bridge reports no campaign
  before it returns. The exit save is opt-in (`save=true`), because the game's
  exit button always saves to the same path. With the exit save on, a scenario
  sweep would overwrite a player's continue-save on every lap. `main_menu`
  closes any running cinematic first and reports it in `cinematicsClosed`.
- `campaign_new` drives the start screen. It is refused while a campaign is
  loaded or a load is in flight. Scenario choice resets the faction list, so the
  verb sets scenario, then `options`, then difficulty (1-4), then faction. The
  tutorial is forced off. `query kind=scenarios` lists the scenario dataNames.
  Under the other categories, it lists the option dataNames for `options`: map
  size (`VeryLightSolarSystem` and similar) and council count. Use at most one
  per category. The returned `options` map is what the launch used. To check
  that a map-size option landed, wait until the campaign is up. Then count the
  live bodies with
  `inspect root=GameStateManager.spaceBodies include_private=true`. Check the
  count and a discriminator body. Reading the meta templates back proves
  nothing. They show file-merged values. They omit the launch's consumed
  options.
- Removing a mod mid-campaign can break a save. `save_check` diffs a save
  against the current merged universe with the game down.

## Templates and localization

- `template` returns merged in-engine values. These values are the ground
  truth, and they differ from the files on disk. Several vanilla and DLC
  files are not strict JSON.
- `template fields=[...]` (bulk mode) projects members across all entries in
  one call. Use it for reference checks instead of one call per entry.
- `localize` resolves through the engine's LocalizationManager, which is the
  only truthful source. Mod and scenario localization never merges to disk.
  `fellBack: true` means the key resolved from a fallback.
- Class index: `ti://templates`.
- `inspect` reads live objects instead of template data, at depth 1. Scalars
  come back verbatim. References collapse to `{id, type, name}`. A list expands
  when its element type is a game state, a template, a string, an enum, a number
  or a bool. A template element comes back as its dataName. That is how you read
  `finishedProjectNames` to check that a `grant` landed. A `Dictionary<K,V>`
  expands only when `path` lands on it, for example
  `inspect id=<faction> path=objectives include_private=true`. A bare dump of
  the faction does not expand it. Use this to read `objectives` and
  `TIControlPoint.controlPointPriorities`. `include_private=true` extends both
  the `path` walk and the dump to non-public members.

## Console

`console line="..."` runs any debug-console command with output capture.
Terminal arguments are comma-separated. The console matches a command name
as a case-insensitive substring, in dictionary order. Always use exact
command names. Some commands print nothing on success. Commands that depend
on a selection (`addtrait`, `killstate`, ...) need `select=<state id>` in the
same call. Typed tools cover the common commands: `trigger_event`,
`give_org`, `councilor_xp`, `councilor_trait`, `nation_stat`, `nation_event`,
`army_edit`, `fleet_edit`, `councilor_move`, `councilor_detain`,
`turn_faction`, `force_missions`, `set_player_faction`, `atmosphere`,
`propaganda` and `unlock_all_ship_parts`. `grant kind=slot3_project` reads
slot 3 first, because the command throws on an empty slot. Never set up a
state with `givealltechs`. It also completes every project whose faction
prerequisites are met. That silently pre-satisfies the thing under test.

Past the pause limit, the console is refused like any other write. So is
`raw cmd=console`, which is judged by the verb that it carries. Move the clock
with `advance`. Or take the limit off with `set_pause_limit seconds=0`, and
say in the report that you did.

**Warning: `setfaction` is not safe while the game is running.** It moves the
seat that the human UI's handlers read, while the faction AI keeps planning. It
also orphans an armed combat. `prompts` and `advance` refuse to run into it
(`stopReason: active_player_moved`). Before anything answers a prompt or drives
a screen, restore the faction that the refusal's `seatedFaction` field names.

## The UI is view-only

The game's screen is view-only for agents. The verbs below are the sanctioned
way to read it. `screenshot` captures inside the game process with the bridge up
(`ui.screenshot`, about a second while Unity writes the PNG). This means an
occluded window still gives a true picture.
`raw cmd=ui.tooltip args={nation, kind}` returns the hover text behind a
nation-panel number without opening the panel.
`raw cmd=ui.describe args={kind, ...}` returns the long-form block behind a
hab module or a project in the same way. `raw cmd=ui.options` reads the
options screen. `usable` is its single verdict. Run it after `main_menu`.
Their rows in `ti://docs/protocol` carry the arguments and the refusals.

Most of the game's UI is behind a control that a mouse opens. `ui_view` and
`ui_screen` open it, and `ui_status` reports the current state. `ui_view`
switches the map. `ui_screen show=<screen>` opens an info screen, and
`detail=<id>` opens the space object detail panel. `rename=true` presses the
rename button on the open panel. That is the only way to photograph a rename
panel, or anything that a mod adds to one. `hab=<id>` lands a frame or two
later, so the reply says `deferred`. Read the screen back with `ui_status`
before you capture it. `rename` needs its own call after `hab`, but it works in
the same call as `detail`. Open the screen. Check with `ui_status` that it is
open. Then run `screenshot`. Do nothing more. Never inject OS-level input
(xdotool or similar). Send no synthetic clicks or keystrokes, for any purpose.
The server's own tools and verbs are the sanctioned path for every state change.
This includes the ones that drive UI controllers internally (`alert_choose`,
`campaign_new`, `load_game`, the combat verbs). If no tool, `raw` verb, or
`console` command covers what a test needs, abort that step with an error. State
the exact action that you were unable to perform and where you looked (tool
table, `raw` verb list). A missing verb is filed and built. Never work around it
with a mouse. A synthetic UI action costs ~50 seconds of screenshot-verify round
trips and fails silently when window focus drifts. A verb costs ~3 seconds and
fails loudly.

## Diagnosis

- `log_tail which=player`: Unity Player.log -- mod loader `[Manager]` lines,
  exceptions, crashes. `log_tail which=game`: Logs/TerraInvicta.log --
  template registration, merge and save messages. This is the first stop when
  a mod misbehaves. `server/calls.log` has one line per tool call: wall clock,
  tool, ms, outcome, clock stall, args cut at 200 characters. The server
  appends to it across server starts and marks each start with a
  `=== server start` line. The server trims the file when it passes 2 MB.
- `selftest` answers one question before all its others: is the running code
  the code on disk? The server is a Python process that your client starts
  once and keeps. Thus an edit to `server/*.py` reaches it only when the
  CLIENT reconnects the server. A new game launch does not reach it. The
  launch replaces the game process and attaches it again to the same server.
  Nothing else catches this. The build is current, the unit suite runs the new
  files, and the mod version stays the same. Every other signal reads healthy,
  but the answers come from code that was replaced hours ago.
  `offline.serverCode` names each file that changed. Drift fails the call,
  because the old code also computed every other line in that report.
  `observe` carries the same `serverCode` line when the code is stale and says
  nothing about it otherwise.
- A game update silently reverts the Unity Mod Manager (UMM, the mod loader)
  injection. `selftest` detects the injection state. The installer repairs it
  or names the fix.
- `selftest` also diffs the DLL's verb registry against the tool table. It
  checks that ModInfo.json, the DLL and the server all state the same mod
  version. It reads the `use mods` setting. If that setting is off, the game
  ignores JSON and localization mods outright.
- A DLL older than the server answers the newer verbs with an error that says
  exactly that. Those verbs include the `designs`, `councilors`, `nations` and
  `scenarios` query kinds. Rebuild the mod and restart the game.
  `selftest`'s verb drift names every gap at once.

## Testing mods

- Run `modcheck` (the check that a mod's templates merged correctly into the
  game) from the main menu: merge, refs, locale, conflicts, reach.
- Run `smoke_test` per scenario: campaign, advance, log scan. Between
  scenarios, it returns to the main menu and does not relaunch the game. A
  single named scenario does the same when a campaign is loaded. Thus you can
  call it from inside a campaign. If the game goes down inside one scenario,
  only that scenario's row records it, and the sweep continues. The row shows
  `bridge_lost`, or `bridge_busy` if the calls timed out. `reached` names the
  step where the game died. The next scenario relaunches the game before it
  starts. The log scan counts an exception or an ERROR line as a failure. A
  small allowlist of engine lines is exempt, because those lines are not
  failures. They are the NaN guards in
  `TISpaceShipTemplate.UnnormalizedTemplateSpaceCombatValue`, which substitute
  a fallback and continue. Every run prints the allowlist in full.
  `logNoiseIgnored` counts, per row, the lines that the allowlist removed.
  Thus you can argue with a pass. A line that names a mod's own data is never
  on the allowlist.
- Fixtures put the object in place, so you do not play to it: `spawn_hab`
  (station or base, tier 1-3, named modules), `spawn_module` (one module in
  a hab's first free slot), `spawn_fleet` (combat fixtures), `spawn_army`
  (human, megafauna, invader), `spawn_councilor`, `spawn_alien_site`
  (facility, crashdown, landing, xenoforming). All of them bypass cost,
  prereqs and build time. They prove that a mechanism fires. They do not
  prove that content is reachable through play.
- Ship designs are the exception in that list. A design proves that the parts
  are buildable, and `spawn_fleet` proves that the design flies.
  `design_create` names every part. It refuses the save on a part that the
  engine drops in silence. Under `legal`, it also refuses a part that the
  faction did not research. `legal=false` is the fixture, and an alien design
  needs it. `design_auto` hands the search to the engine. It is the quickest
  way to ask whether a faction can field a role at all. `design_delete`
  reports the engine's `canDelete` gate. Expect the live-ship arm, because
  nobody can remove a design that something still flies.
- For a player action with no verb of its own, use
  `raw cmd=action.list args={filter}` to find the class and its constructor.
  Then fire it with `raw cmd=action.invoke args={class, args}`. These verbs
  reach all 99 classes in the engine's Actions namespace by reflection. The same
  fixture caveat applies, and more strongly. It skips cost, availability and
  turn order outright. It reports only what it submitted. Read the effect back
  with `query` or `inspect`. To check that the arguments of a call resolve
  before you fire it, add `dryRun`.
- The same applies for state you cannot spawn. `kill_state` destroys a hab or
  kills a councilor, army, fleet or space facility by id. `war` declares war,
  white-peaces, occupies, and clears relations cooldowns. `control_points` hands
  over or scrambles CPs. `prospect` reveals one body or every body. All four are
  typed wrappers over the console, so their output is usually empty. Check that
  the effect landed with `query` or `inspect`. `war action=declare` leaves a
  shared federation and breaks any alliance before the declaration.
- `faction_relations` and `set_faction_relation` cover what `war` and
  `control_points` do not: how one faction feels about another.
  `faction_relations` only reads, so it answers over a stopped clock.
  `set_faction_relation faction=<id> other=<id> hate=<n>` is the write. When
  the thing under test is an AI reaction keyed off hate, use it. Read the
  numbers from the reply. Do not assume that the requested number landed.
- `spawn_hab` also bypasses orbit station capacity, per-slot module validity
  and the base mine-slot reservation. `spawn_module` bypasses tech gating and
  per-slot validity the same way.
- `hab_build_module` is the opposite of `spawn_module`. When the decision is
  the thing under test, use it. The owner pays, the build takes its
  normal time, and the engine makes its own upgrade-versus-new-build choice.
  `target` is a module STATE id, the same addressing `kill_module` and
  `module_power` use.
- To kill one hab module, use `kill_module module=<state id>`. `kill_state`
  does not reach a module. Pass `hab` alone to list a hab's module ids. When
  the last okay module of a hab goes down, expect the hab to go down too.
- `module_power module=<state id> on=true|false` flips one module's power
  switch. It uses the same gates as the Habitats screen. The `PowerFirst`
  refusal (a module that will not shut down unless the hab is already in
  deficit) is the one you will hit first.
- `nation_stat` sets all nine nation stats. The console has no command for
  cohesion, democracy, inequality and education. For these four stats, the
  tool calls the `nation.set_stat` bridge verb, which uses the engine's own
  mutator. You can also reach that verb directly with
  `raw cmd=nation.set_stat args={nation, stat, value}`. The verb refuses the
  five stats that the console serves and names the command for each. It is
  fixture-grade, like the spawns.
- The location for `spawn_fleet` must be an orbit or a hab id. A hab site or a
  fleet is refused. The engine places a new fleet only at those two, and it
  null-dereferences on anything else. `spawn_hab`'s location is a hab site,
  orbit, Lagrange point, or planet/moon.
- Use `screenshot` and `assets` for visual surfaces. `workshop_status` and
  `save_check` work with the game down.
- `raw` reaches any bridge verb the tool table lacks. `batch` runs several
  verbs in one call and stops at the first failure.
- For national policy, use `raw cmd=nation.policies args={nation}` to see what
  a nation can enact and against which targets. Then enact a policy with
  `raw cmd=nation.set_policy args={nation, policy, target}`. Both take names
  or state ids (`policy` takes `PeacefulBreakupOption` or
  `Grant Independence`). The verb runs the AI's own adoption path and keeps
  its legality, so a refusal names the condition that failed. Some options do
  not pass inside the call. They queue a response prompt at the other side.
  `readback.awaitingResponse` says which of the two happened. A queued answer
  lands only after the clock runs.
- For alliances, rivalries and nuclear launches, list the options with
  `raw cmd=faction.diplomacy args={nation}`. Then enact one with
  `raw cmd=faction.diplomacy args={nation, option, target}`. These are the five
  options that `nation.set_policy` refuses. `action.invoke ConfirmPolicyAction`
  also reaches them. It bypasses cost, eligibility and confirmation. Use it only
  when you want the bypass. Read `readback.relation` and
  `readback.awaitingResponse`, and do not assume the result. **Warning:
  `EmployNuclearWeapons` is the actual launch, and nobody can recall it.** It
  needs `confirm:true`. Its target list reaches past enemy territory and
  includes your own capital. Before you fire, check the `nation` of each target
  row.
- To destroy enemy hab modules, use
  `raw cmd=fleet.bombard args={fleet, target, altitude}`. The order runs 14
  game days, so advance the clock and re-read the fleet and hab. If you
  bombard the region of a human nation, you commit a real atrocity
  (`atrocityCommitted: true` in the return). The atrocity is permanent
  campaign state, and a later assertion can trip over it.
- **A fleet is bombardable only while it is landed.**
  `raw cmd=fleet.land args={fleet, site}` is the only path to that state for a
  fleet you do not own. Spawn into an orbit first and land from there.
  `spawn_fleet` onto a hab site is refused.
- **To give a fleet an assigned transfer**, use
  `raw cmd=fleet.transfer args={fleet, destination}`, where `destination` is an
  orbit id. Several fleet orders refuse on this state, and nothing else can set
  it up. Give it an orbit at another body that the faction can already reach.
- **To get a contested mission's chance without running the mission**, use
  `raw cmd=mission.evaluate args={mission, councilor, target}`. It is the only
  way to exercise a rule patched onto the contested resolver without playing to
  a mission. Before you read a chance of 0 as a verdict, read `councilorActive`.
  An inactive councilor returns 0.0 for every value of `revalidate`. No fixture
  can stage that case. The rolls that `outcomes=<n>` makes advance the RNG
  stream. If a later assertion depends on the campaign's rolls, pass `seed`.
  `mission.evaluate` bypasses assignment, eligibility, target validity and the
  mission phase. Thus it proves that the rule fires. It never proves that the
  game allowed the councilor to go.
- **Warning: `crash_the_game confirm="crash-the-game"` crashes the running game
  on purpose and ends the session.** It puts the process into the state that the
  `crashed` stop reason describes. Use it to test a client's crash handling.
  `advance` recovers as it does after a real crash. Anything unsaved is lost, so
  run `save_game` first. Without the exact `confirm` string, the call is
  refused. If the bridge disappears and does not report `crashed`, the game quit
  outright. Read `log_tail which=player`. To stop the game without crashing it,
  call `game_stop`.
