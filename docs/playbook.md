# Terra Invicta driving playbook

This guide shows how an agent drives Terra Invicta through this MCP (Model
Context Protocol) server. The in-game DLL runs the bridge, a small TCP
server on `127.0.0.1:17470`, and answers verbs. Each verb is one named
request, finished inside a single frame (wire contract:
`ti://docs/protocol`). The tools own sequencing and judgment. The
`game_start`/`game_stop` tools own the launch cycle on every platform, and
the `log_tail`, `save_check`, and `ti://saves` surfaces know where saves and
Player.log live (the user profile on Windows, the Proton prefix on Linux).

## Session shape

1. Run `observe` -- always first. It reports bridge and campaign state and names
   the next call.
2. Run `game_start` when the game is down. It launches through Steam and polls
   the bridge up (20-60s). `game_start load=<save>` also loads a save and
   waits for the campaign.
3. Scratch-save rule: never experiment on a campaign a person is playing.
   Run `save_game name=scratch-<topic>` first and work on that.
4. Work.
5. Run `game_stop` when done. Never leave the game running idle. Unsaved
   progress dies with the process.

## Oversized tool responses

The Python server limits a serialized JSON tool payload to 160,000 characters.
A response that exceeds this limit returns `isError: true` and a complete JSON
diagnostic with `error: "response_too_large"`, `message`, `textLength` and
`textLimit`. The original payload is omitted. The size counts serialized text,
including JSON escapes, before the surrounding MCP envelope; pause-limit
banners remain separate content blocks.

This is a response delivery failure after the handler ran. The operation may
already have completed. Inspect current state before retrying a command that
changes it. For reads, narrow the request with `limit`, `fields` or `contains`
where the tool supports them. For custom console commands, make the producer
return a smaller result. Do not interpret missing output as a missing command
or a failed mutation.

## The blocked clock

Pending prompts, open alert boxes, modal screens, and unresolved combat
freeze the strategy clock. Time stops completely until they are
cleared. A run that hangs is almost always blocked, and `observe` reports
the cause.

`advance` owns the unattended loop. It handles max speed, `run_until`,
prompt dismissal, and combat autoresolve. By default it answers only prompts
with a neutral answer. It returns early with the alert text and options when
a real decision blocks. Make the decision with `alert_choose option=N`, then
call `advance` again. `advance` chunks to its `max_seconds` budget and tells you
how to continue. `prompts mode=drop` force-drops an ordinary prompt, and it answers
narrative events on the way past: the drop adds `all` and nothing else, and the
screens pass still presses an option button on any narrative alert that is up.
`narrative_events=false` is what leaves one untouched for `alert_choose`, and
`narrative_events=llm` is how an unattended run hands one to you instead of
answering it (see "Narrative events").

### The pause limit

There is a limit on how long the game may go without gaining time: five
minutes by default, whatever `set_pause_limit seconds=N` sets for the session,
and none at all at `0`. `TIBRIDGE_PAUSE_LIMIT` sets the value the server starts
with, and `pause_limit` reads the current one -- that read is never refused and
never bannered, since it is what an agent calls to find out why everything else
is being refused.

Two things count as a stall, and the second is the one that surprises people:
the clock not moving at all, and the clock crawling -- speed 1 or 2 with no
`run_until` armed, where the date ticks over every frame while an hour of real
time buys a day of game time. Both are a test that is not running.

Past the limit, every state-changing tool is refused with
`PAUSE LIMIT EXCEEDED`, which names the state, the prompt holding the clock and
how long since any verb ran. Read-only tools still run and lead their answer
with that same banner, so reading a stopped campaign is never blocked. The
tools that move the clock, clear what blocks it, or end and restart the run are
never refused: `advance`, `time` (except `action=pause` and `speed=0`, which
are a pause, in any spelling the wire allows), `prompts`, `alert_choose`, the
four `combat_*` tools, `autopilot`, `ai_autopilot`, `game_start`, `game_stop`,
`main_menu`, `load_game`, `campaign_new`, `save_game`, `crash_the_game`, and
`set_pause_limit`, which is the way out of the limit itself. Those skip the
stall reading altogether rather than taking one and ignoring it: a reading
costs a `query.time`, and against the wedged main thread that `game_stop`
exists to recover from a `query.time` costs the full bridge timeout.

**`raw` and `batch` are judged by the verbs they carry.** Their own names say
nothing about whether a call reads or writes, and taking them at their name let
every refused write through under another spelling. A call whose every verb is
a read runs with the banner: `query.*`, `assets.*`, `mods.list`,
`harmony.patches`, `ui.screenshot`, `ui.tooltip`, `ui.describe`,
`combat.status`, `prompts.list`, `saves.list`, `action.list`, `ping`, `verbs`
and `version`. Anything else is refused exactly as the tool it stands in for
would be, a verb the allowlist does not know included.

A reading that cannot be taken refuses nothing. A `query.time` that fails
leaves the limit with no measurement at all -- not the last one, which
described a game that may since have died -- so a bridge that has gone away
answers with its own error rather than a stall report, and no violation is
recorded for it.

What clears a stall is the game clock gaining time. Nothing else does: not a
successful tool call, not a fixture, not a save. `advance` stops with
`stopReason: pause_limit` rather than spending its budget on a clock that is
not moving, and three rules keep it the way out: it never stops on the limit
before it has run one full pass of its loop, it does not stop while it is
working a hold it opened, and `force=true` suppresses the stop entirely.

**A recognised hold reaches its own stop first, and the limit is for stalls
outside them.** A standing decision, a narrative box, a combat, an open mission
phase and an unreadable prompt queue each have a bound of their own, measured
in polls rather than in minutes, and each is shorter than the limit. So an
`advance` that meets one comes back with `decision`, `narrative_event`,
`combat`, `mission_phase` or `prompts_unreadable` and a `next` that says what
to do about it, and `pause_limit` never fires on it. Do not expect the limit to
be what reports a blocked clock inside a call: expect it on the stall nothing
recognises, which is the clock sitting stopped between calls while an agent
builds fixtures, thinks, or waits on something that is not coming.

`observe` warns past half the limit, leads with the banner and the violation
record past it, and always carries `stallSession`: the current limit, whether
it is the default, when it was last changed, `stallViolations` (one per stall
episode, however many calls it refused), `refusedCalls`, `longestStallSeconds`,
and the record
taken when the limit was crossed -- time, state, blocking prompt, seconds since
the last verb, and the first tool refused. An orchestrator reading `observe`
after a run therefore sees a stall the run has since cleared. The DLL also
writes one line per whole five minutes to Player.log
(`log_tail which=player pattern='clock stalled'`).

**Fixture setup has to fit inside the limit.** Building campaign state a step
at a time is exactly the pattern the limit catches: it happens with the game
paused, and none of it moves the clock. Either keep a setup sequence inside the
limit, or call `advance days=1` between steps, which costs a second or two and
clears the measurement. A setup that genuinely has to run with the clock
stopped is a deliberate exception: `set_pause_limit seconds=<bigger>` for that
session, and say in the report that it was raised. Every `observe` carries the
current limit and the moment it last changed, so a raised one cannot pass
unnoticed.

**Read `daysAdvanced` before you read `next`.** Every digest ends by telling
you to call again, and that sentence is worth obeying only when the last call
moved the clock. A call that moved zero days says `stopReason: no_progress`
rather than `max_seconds`, and `advance` counts consecutive fruitless calls
across calls: the second says the run appears stalled and names what to look
at, and the third is refused outright instead of re-armed. `force=true` runs it
anyway, once you know why the clock is not moving.

## Stop reasons

These stop reasons are not "ask for more time":

- **`crashed` / `crash_recovered`** -- the game hit its own crash handler. The
  bridge survives it, so every tool keeps answering over a dead game and the
  frozen clock reads like a modal alert; `observe` and `advance` read
  `GameControl.handlingException` rather than guessing. Recovery is a process
  restart, because loading a save inside the same process does not clear the
  flag and comes back permanently degraded. `advance` does that restart once,
  reloading a save and naming in the digest what was lost. **The save it
  reloads is the newest save OF THIS RUN**: an autosave, combat autosave or
  exit save the game wrote after the run entered its campaign, and failing
  that the save the run loaded (`load_game`, `game_start load=`, or the last
  `save_game`). The newest save on disk is not a substitute -- the folder holds
  other campaigns and other runs, and restarting into one of those resumes a
  game nobody asked for while reporting a recovery. A run started with
  `campaign_new` that crashes before its first autosave therefore has nothing
  to come back to and stops with `crashed`, naming `load_game`; so does a
  crash that recurs after the restart, rather than looping. A fresh campaign is
  never started for you. To test that this works rather than wait for a real
  crash, see `crash_the_game` under "Testing mods".
- **`combat`** -- a fight the autoresolver will not retry. See "Combat" below.
- **`autopilot_off`** -- the UI macro switched itself off. See "Autopilot and the
  faction AI".
- **`prompts_unreadable`** -- the clock is frozen and the prompt queue could not
  be read, which is not the same as empty. Read `prompts` and `log_tail
  which=player`.
- **`narrative_event`** -- `narrative_events=llm` only: a story event's box is
  open and the decision is yours. The options are in `alert` and `next` lists
  them; answer with `alert_choose option=N` and call `advance` again. See
  "Narrative events".
- **`active_player_moved`** -- the active-player seat has moved off the
  faction the campaign came up with, which only `setfaction` does, so the
  prompt pass refused to answer anything: those answers go through the human
  UI's handlers, which read the seated faction's councilors and crash on a
  faction the human is not playing. Nothing was answered, dropped or closed.
  The refusal names the faction to put back in `seatedFaction`; `console
  line="setfaction <that faction>"`, then call again. Reading is unaffected. An
  `ai_autopilot` engagement does not trip it: the engagement leaves the seat
  where it is. A refusal this server does not have a name for arrives as
  `prompts_refused` with the DLL's own text.
- **`bridge_busy`** and **`bridge_lost`** -- the call ended on the transport
  rather than on the game. Busy is a stalled main thread and the game is up;
  lost is a bridge that stopped answering, either a closed socket or five
  clock reads in a row that went unanswered. Each of those waited the full
  verb timeout, so a run of them is minutes of silence rather than a slow
  poll, and the call stops instead of spending the rest of its budget on it.
  `observe` is the next step for both: it reports whether the process is
  still there, which is what tells a busy main thread from a game that is
  gone.
- **`pause_limit`** -- the game clock has gone past the pause limit without
  gaining time, so the call stopped rather than spending its budget on a clock
  that is not moving. The digest carries the violation record: state, blocking
  prompt, seconds since the last verb. See "The pause limit" above.
- **`mission_phase`** -- a councilor mission phase stayed open for half a
  minute of polling, so the clock is left paused. While a phase is open
  `advance` hands the clock back in no form at all -- no `time.speed`, no
  `run_until`, no `time.play`, and a running clock is paused -- because a
  semimonthly tick landing inside an open phase is what corrupts it. It still
  runs its dismiss pass every poll, which is what presses the
  mission-assignment confirmation that closes the player's phase. The digest
  carries `missionPhaseWaits` and the phase block. The phase ends when every
  faction has signalled its assignments, so read `prompts` for a
  mission-assignment confirmation still queued and `log_tail which=game` for a
  planning exception. `advance force=true` runs anyway and accepts the
  collision. Under an `ai_autopilot` engagement there is no hold: the mod's own
  guard defers a colliding tick instead.

Most of those hand you something to do and do not count toward the stall
refusal. Six do: `mission_phase`, `pause_limit`, `active_player_moved`,
`prompts_refused`, `bridge_lost` and a terminal `crashed`, along with a call
that ran out its budget having done nothing at all. A phase nothing here can
close, a clock that has not moved in minutes, a pass that will not run until
the seat changes, a bridge that is not answering and a game that crashed again
after its restart are all a game nobody is driving, and calling again over any
of them without changing something is the loop the refusal exists to break.
`crash_recovered` is the opposite case and clears the counter: the game came
back and the next call has a live one to run.

A bridge timeout during the loop is one lost poll, counted in `lostPolls`, not
the end of the call -- until five in a row, which ends it as `bridge_lost`.
Verbs are answered on the game's main thread, so a large save, an asset load or
a scene change stalls them past the 30s call budget with the process perfectly
alive. A tool that times out says the game is UP and to wait; only a closed
socket says to call `game_start`.

## Narrative events

Narrative event popups are answered too, by the engine's own AI option
strategy, so a story event does not stop the run. Pressing any option button
on the notification controller IS answering the event. **Read
`narrativeEvents` in the digest**: every one it answered is listed there with
the event dataName, the option index, the button text, what that option does
(`optionDetail`) and which rule picked it. `narrativeNotes` carries the
exceptions -- an event left standing, an option pressed with no readable event
behind it, an event whose target was already gone so the engine applied
nothing, and a prompt dropped with no option applied.

`narrative_events` chooses who decides, and it takes three answers:

- **`true` or `"ai"`** (the default) -- the engine's own option strategy
  presses a button and the run keeps going.
- **`"llm"`** -- you decide. The first poll that finds a story event's box open
  with live buttons ends the call: `stopReason` is `narrative_event`, the
  options are in `alert`, and `next` lists them. Answer with `alert_choose
  option=N` and call `advance` again. `alert_choose detail=true` first if the
  outcomes matter: it reports each option's outcomes, chances, effects, costs
  and grants off the event template, which crosses the popup's own gate on the
  options the game hides from your faction's ideology. The handover counts as
  progress, so it never trips the stall refusal however many events a run hands
  over.
- **`false`** -- **attended only**. Nothing in the harness will ever answer a
  story event, so an unattended run under this mode stops on the first one and
  stops again on every call after it. Use it when a person is at the screen;
  use `llm` when an agent is driving.

An event offering one option is pressed in every mode, `false` included, since
there is no decision in it. It is reported like any other answer, with
`single: true` and `optionDetail`, so nothing is applied without being named.

Only the alert box answers one. A narrative prompt seen before its box has
come up is reported as skipped and taken on a later poll, because answering it
through the prompt queue would leave the box to apply the same choice a second
time. That box queues behind whatever notification is on screen, so it can trail
its own prompt by a poll or more; `advance` waits it out rather than reporting a
decision, counting the waits in `narrativeBoxWaits`, and stops only if the box
never arrives.

The wait is driven by the box rather than by a counter. `advance` reads
`alert_choose` on every poll of a narrative hold, and the two states it tells
apart get different patience: a box that has not rendered gets a long leash
(the drain can take most of a minute), and a box standing open with live
option buttons gets a short one, because the screens pass takes an open box on
its very next pass. A box open across several polls with its prompt still
queued means the controller is refusing the press, and that is a real hold. The
stop message says which of the two happened, and `narrativeBoxPolls` counts
the polls the box was actually up.

One narrative prompt is dropped instead: the one nothing can answer, because
its event template is missing or its target has been killed. The alert box
applies no option when the target is gone -- it logs the target away, tears
itself down and does not come back -- so without the drop the prompt would hold
the clock for the rest of the campaign. The drop runs whichever way
`narrative_events` is set.

## Combat

`ai_autopilot action=engage` changes the shape of the `advance` loop. With the
faction AI-controlled, prompts route to the AI instead of the player queue.
This means most things that block the clock never queue. Combat is the
exception, and the rest of this section covers it.

One combat runs at a time globally. A request made while
another is unresolved is parked on the fleet instead of lost. Same-faction
pairs are refused. A faction on both sides wedges the simulation
permanently. AI-vs-AI combats resolve themselves. A combat the player is in
waits for `combat_autoresolve`, which also closes the post-combat report. To
let a human fly a fight, simply do not autoresolve it. The block persists
until a person takes it.

**Taking a fight by hand.** The precombat screen asks the player for two
things, and they have separate answers. The stance is `combat_stance
stance=Defend` (or Pursue, or Evade): it submits through the screen's own
button, checks the stance against what the combat allows that faction, and
reports the stance read back off the combat plus whether the stance prompt
left the queue. There is no faction or combat argument, because the engine's
submit takes both from the live screen and only the stance from its caller.
`prompts` answers this prompt the same way on its own, with Defend, whenever a
precombat screen is up and nothing is autoresolving, so `combat_stance` is how
to pick a different one. `advance` never reaches it: an unresolved combat sends
that loop to `combat_autoresolve` before its prompt pass runs, and the machine
submits the stance itself. Which is also why `combat_stance` is refused while
`combat_autoresolve` is armed -- two writers on one screen -- and why it is
refused with the precombat canvas down: the controller outlives the screen and
still holds the last fight, so a submit there answers a fight whose report has
already been closed.

The other prompt, `PromptBeginCombat`, is answered by nothing and never will
be. Every button that clears it commits an outcome -- close, cancel, reject,
live -- and one of them is the accept `combat_autoresolve` owns, so there is no
neutral answer to give it. `combat_precombat` is the path, and dropping the
prompt is not: it is the canvas that freezes the clock.

**Building a fight with the fixtures, and the trap in checking it.**
`spawn_fleet` twice into the same orbit for two different factions, then
`combat_start`, is the whole recipe. `combat_start` does not initialize the
combat: it caches the two fleets and the hab, and the engine promotes the
combat a frame or more later. Until then `combat_status` reports
`fleets: [null, null]` and `hab: null` on a combat that is perfectly healthy,
which reads exactly like a fight with nothing in it. **Do not conclude anything
from those two fields on an uninitialized combat.** Read `promotion` beside
them instead: it carries the cached fleets with their ship counts, the faction
list, and `willPromote` / `blockedBy`, which is the engine's own gate. A combat
that fails that gate is archived and removed outright the next frame, so
`willPromote: false` right after `combat_start` is the answer, and waiting for
the combat to appear is waiting for something that has already been deleted.
The gate wants both fleets valid, both holding at least one ship, and their two
factions distinct and non-null.

`blockedBy` is diagnostic-only, and trying to make it fire is wasted time. Every
clause of the gate is already covered by a refusal `combat_start` makes first,
and a combat this harness starts is promoted or archived before the next call
lands, so no call sequence reaches a failing gate. The field is there for a
combat the harness did not start, where it is the only record of why the combat
vanished. If `combat_start` ever does answer `gateFailed: true`, that is a bug
in the verb and worth reporting, not a state to reproduce.

A combat that failed to autoresolve is not simply retried. Arming re-enters the
machine at the start, and two of its steps are one-shots: a second
`AutoresolveSelected` raises a second precombat-complete event, and a second
accept applies the same simulated damage to the real states again. So
`combat_autoresolve` refuses a re-arm once either has fired, when the failure is
one no retry changes, when the stall was in the closing phase, and on a second
attempt that recorded an error, and it names the combat, the phase it reached
and the error. The attempt count on its own refuses nothing: a combat both
sides of which turned out to be AI disarms cleanly with a note, and arming on
that one again is harmless.
`advance` stops on that rather than grinding to its budget.

Nothing has ever reached the double-apply guard, and there is no point trying.
Live runs resolve on the first arm with `attempts: 1` and apply damage once.
The one way to fake a stale precombat screen from outside was a console
`setfaction` with the screen up, and that is retired: it orphans the armed
combat, so the combat id stops resolving and the canvas is gone, and it moves
the seat, which is unsafe while the faction AI is planning missions -- the
planner runs its assignments through the human UI's map controller and the game
dies there. `prompts` refuses to run once the seat has moved off the faction the
campaign came up with, for the same reason, and says so.

`combat_autoresolve` waits for the resolution itself and reports what it did.
On a stall it stops waiting and diagnoses: the phase reached, which of the two
one-shots fired, and the precombat canvas with its live buttons. One stall has
a cause worth knowing, because the tool now clears up after it. A combat can
vanish before there is anything to accept, and then no button ever runs -- and
every engine path that removes `PromptBeginCombat` is one of the precombat
screen's own buttons. The prompt is left standing, the clock stays frozen and
saving stays blocked. The DLL clears it when its machine finds the combat gone
with no canvas left; the tool checks for it again afterwards and drops it, but
**only with the canvas down**. While the canvas is up the prompt is not what
freezes the clock. Both readings the drop rests on -- the status and the screen
-- have to have been read: a wait that ends on unanswered status polls, or a
screen read that fails, reports `resolved: false` and leaves every prompt
alone. `bridge_busy` there means the polls timed out with the game up, and the
way back is another `combat_autoresolve` on the same combat, which re-enters the
wait without arming anything a second time.

Two things make a combat vanish like that, and the report says which.
`promotionAtArming` is the promotion gate above, snapshotted when the machine
armed; a `willPromote: false` there means the engine deleted the combat rather
than fought it, and the fix is the fixture, not a retry. The other is a combat
whose second fleet and hab are both gone by the time `Autoresolve` runs, which
past a successful promotion only reaches a hab-only fight whose defender
evaded. A two-fleet fight cannot end that way.

**When it refuses, do not drop the prompt.** It is the precombat canvas that
freezes the clock, not `PromptBeginCombat`, and only a button takes the canvas
down. Read `combat_precombat action=status` first: it reports which buttons are
live, and that depends on how far the resolution got. Ending the precombat
interaction deactivates the screen's own UI object, so a dead end reached after
that point may have no close or cancel button left, and then it needs a person.
Before that point there is a live cancel: `action=cancel` calls the attack off
entirely and `action=close` closes the report. `action=reject` and `action=live`
hand the fight to the tactical layer, which nothing headless drives, so they
trade one stall for another. There is no `accept`: `combat_autoresolve` owns
that call so it happens exactly once.

## Autopilot and the faction AI

Two different things play the player faction, and they are not
interchangeable.

`autopilot` is the game's own UI macro. It never sets `isAI`, so
`AIDailyFactionPlanner` never runs for your faction. It dismisses prompts,
picks a random available tech on a tech prompt, and sells the first sellable
org. It auto-resolves combat stances and force-advances the clock. The
faction keeps its human difficulty treatment. That makes it
no sample of AI play. The random tech pick makes it wrong for anything
whose outcome depends on the tech path. It is still the cheap way to buy
campaign time. `save_cycles=N` autosaves every N cycles.

**Set `ignore_exceptions=true` for any unattended run.** Without it the macro
switches ITSELF off and pauses on the first exception in game code, and says so
only in the player log; the run then keeps resuming a clock nobody is driving.
Leave it unset when hunting a crash, which is the case it exists for.
`autopilot action=status` reads the macro's own engaged state back, and
`advance` polls that same reading while the macro is on, stopping with
`stopReason: autopilot_off` when it has flipped.

`ai_autopilot action=engage` hands the faction to the real planner. The
planner plans its councilors, research, nations, wars, habs and fleets and
answers its own prompts. Use it when the campaign should develop the way AI
play develops. Release before testing anything that depends on the faction
being yours.

- Combat still takes the human path. Precombat control compares the active
  player by reference and the engagement never reassigns it. A fight your
  faction is in posts a begin-combat prompt nothing clicks, and both planner
  gates starve behind it. Drive engaged stretches through `advance`, which
  autoresolves, instead of bare `time`. `combat_autoresolve` cannot arm on a
  combat the engaged faction is in -- the engagement makes that faction report
  as not the active player, while the precombat controller's own cached
  reference still treats it as the player, so the canvas comes up and holds the
  clock. It refuses and says so rather than reporting the fight as resolving on
  its own. Release the engagement and autoresolve, or answer the screen with
  `combat_precombat`.
- The faction goes notification-silent the way every AI faction is. An alert
  already on screen when you engage is not drained. Answer it with
  `alert_choose` first (a game restart also clears it).
- `smart=brutal`, the default, swaps difficulty to Brutal for the duration of
  each planning call and holds game speed at maximum. This helps an unattended run
  get as far as it can. Scope honestly. For a human faction's own planning
  only three dials differ at Normal or above (attack-fleet strength ratio,
  gang-up ideological distance, passive-research weight). Two more differ only
  on Forgiving. The rest of Brutal changes numeric treatment of the aliens. It ignores
  decision quality. For pace, `give_resources` and `grant` do more than the
  difficulty swap does.
- Side effects while engaged, all reverted on release: Steam achievements do
  not unlock. Mission resolution treats the faction as AI for the difficulty
  modifier (zero at Normal). Other AI factions become uniformly willing
  to gang up on it.
- Saves stay vanilla-shaped. The flag is cleared for the duration of every
  write, so a save taken while engaged loads with the faction back under your
  control. The engagement also releases itself if the campaign changes under
  it.
- The clock never pauses while engaged. The game's own phase-start pauses are
  resumed by the tick a frame later (`status` counts them as
  `resumedPauses`). A semimonthly mission-phase tick that would land while
  the previous phase's machinery is still busy is skipped instead of
  colliding (`deferredPhaseTicks`). A steady climb means planning is
  outrunning the phase period, which is harmless. Only pauses you ask for
  through the time tools -- pause, speed 0, a fired `run_until` -- stick.
- The two never run together. The macro plays the faction itself, so `engage`
  refuses while `autopilot` is on.

## Time, manually

Run `time speed=5` before waiting on game days. `time run_until=YYYY-MM-DD`
arms an auto-pause. `time action=status` polls. Prompts still freeze the
clock. This is the loop `advance` automates.

## Campaign entry

- `load_game` works from the main menu and tears the session down while
  loading. Poll `observe` until `campaign: true` (20-60s).
- `main_menu` is the way back. It unloads the loaded campaign and returns to
  the start screen **in the same process**, so a second scenario no longer
  costs a `game_stop` plus a `game_start`. It is the engine's own
  exit-to-menu path, which means the campaign is gone and anything unsaved
  with it: `save_game` first if it matters. The tool waits until the bridge
  reports no campaign before returning. The exit save is opt-in (`save=true`),
  because the button's version always writes to the same path and a scenario
  sweep would overwrite a player's continue-save on every lap. It also closes
  any cinematic still on screen first and reports what it closed in
  `cinematicsClosed`: the campaign unload nulls out the render texture every
  video player points at, and a cinematic left running dereferences that
  texture on its next frame and takes the process down. An alert cinematic is
  ordinary campaign state, so this is not a rare path -- if a `main_menu` on an
  older build ever crashes the game with a `Cinematic2DController` stack, that
  is what happened.
- `campaign_new` drives the start screen. It is refused while a campaign is
  loaded or a load is in flight. Scenario choice resets the faction list, so
  the verb sets scenario, then `options`, then difficulty (1-4), then
  faction. The tutorial is forced off. `query kind=scenarios` lists both the
  scenario dataNames and, under the other categories, the option dataNames
  for `options` -- map size (`VeryLightSolarSystem` and friends) and council
  count. Use at most one per category. The returned `options` map is what the
  launch used. To confirm a map-size option landed once the campaign is up,
  census the live bodies with
  `inspect root=GameStateManager.spaceBodies include_private=true` and check
  the count and a discriminator body. Reading the meta templates back proves
  nothing. They show file-merged values. They omit the launch's consumed options.
- Removing a mod mid-campaign can break a save. `save_check` diffs a save
  against the current merged universe with the game down.

## Templates and localization

- `template` returns merged in-engine values -- ground truth. This differs from files on
  disk. Several vanilla/DLC files are not strict JSON.
- Template universe: before a campaign starts, `template` sees the
  un-resolved union of every scenario's data. Vanilla, DLC, and mod entries
  coexist. Scenario variants are distinguished by `scenarioTags`. Campaign start
  runs scenario resolution once, destructively. Afterwards only the loaded
  scenario's resolved set is visible. Run cross-scenario audits (`modcheck`,
  template sweeps) from the main menu. Run checks for what the game actually uses
  inside a campaign.
- `template fields=[...]` (bulk mode) projects members across all entries in
  one call. Use it for reference checks instead of one call per entry.
- `localize` resolves through the engine's LocalizationManager -- the only
  truthful source. Mod and scenario localization never merges to disk.
  `fellBack: true` means the key resolved from a fallback.
- Class index: `ti://templates`.
- `inspect` reads live objects instead of template data, at depth 1. Scalars
  come back verbatim. References collapse to `{id, type, name}`. A list
  expands only when its element type is a game state, a template (each as its
  dataName -- this is how you read `finishedProjectNames`/`completedProjects`
  to verify a `grant`), a string, an enum, a number or a bool. A
  `Dictionary<K,V>` expands the same way when both its key and value types
  pass that test, but **only when `path` lands on the dictionary itself** --
  `inspect id=<faction> path=objectives include_private=true`, not a bare
  dump of the faction. In a member dump it stays a bare type name, as a
  nested list does, because `TIFactionState` alone carries 36 expandable
  dictionaries and one of them holds 200 prebuilt tech tooltips. Walked onto,
  it comes back as a JSON object keyed by the rendered key, and keys that
  render alike come back as an array of `{key, value}` pairs instead, so
  nothing is lost silently. This is how `objectives`, `objectiveNames` and
  `TIControlPoint.controlPointPriorities` are read.
  `include_private=true` extends both the `path` walk and the dump to
  non-public members.

## Console

`console line="..."` runs any debug-console command with output capture.
Terminal arguments are comma-separated. Matching is case-insensitive
substring in dictionary order. Always use exact command names. Some
commands print nothing on success. Selection-dependent commands (`addtrait`,
`killstate`, ...) need `select=<state id>` in the same call.

Past the pause limit the console is refused like any other write, and so is
`raw cmd=console`, which is judged by the verb it carries: move the clock with
`advance`, or take the limit off with `set_pause_limit seconds=0` and say in
the report that you did.

**`setfaction` is not safe while the game is running.** The seat it moves is
the one the human UI's handlers read, and the faction AI keeps planning: its
mission planner assigns through the map controller and kills the game there. It
also orphans an armed combat. If a test needs it, put the original faction back
in the seat before anything answers a prompt or drives a screen. `prompts` and
`advance` refuse rather than run into it -- `stopReason:
active_player_moved` -- and the way out is `setfaction` back to the faction the
refusal names in `seatedFaction`. What the refusal tests is which faction holds
the seat, not the seated player's AI flag: the engine rewrites that flag from
the seat it has just assigned, so the faction handed the seat always reads as
non-AI and the flag can never tell you the seat moved.

## The UI is view-only

The game's screen is view-only for agents. Two verbs are the sanctioned way
to read it. `screenshot` captures inside the game process with the bridge up
(`ui.screenshot`, about a second while Unity writes the PNG). This means an
occluded window still yields a true picture. `raw cmd=ui.tooltip
args={nation, kind}` returns the
hover text behind a nation-panel number without opening the panel, and
`raw cmd=ui.describe args={kind, ...}` returns the long-form block behind a
hab module or a project the same way: `module.benefits` (module template
dataName, optional faction and hab), `module.summary` (an installed
module's **state id**, which is the asymmetry to watch) and
`project.unlocks` (project dataName). `raw cmd=ui.options` reads the options
screen -- whether the controller exists, is the one the engine holds, is
enabled and still has a canvas, with `usable` as the single verdict -- and
presses nothing at `action=status`, which is its default. That is the check to
run after `game_main_menu`, which takes that canvas down and disables the
component. It also has `action=open|close|toggle`, which call the engine's own
`MainMenu()` toggle, the one the escape key and the menu button call; those
pause or resume the clock exactly as the button does, so read `paused` on both
sides and put it back. A mining module needs a faction,
and either a hab that sits on a hab site or no hab at all: the
engine reads a per-faction mine size modifier and a per-site daily
production with no null checks, so a missing faction or an orbital
station is refused rather than thrown. That holds for both module
kinds -- `module.summary` reaches the same builder through the module's
own faction and hab, and `spawn.module` is what can put a mining module
on a station in the first place. Every other module is fine with
nothing passed. Its `text` is untrimmed, so a character-exact
assertion on a mod's own string works.

`screenshot` photographs whatever is on the screen, and most of the game's UI
is behind something a mouse would open, which used to make it unphotographable
here. `ui_view` and `ui_screen` are what open it, and `ui_status` is what says
where things stand. `ui_view view=PoliticalMap` or `view=SolarSystem` switches
the map (it closes any open info screen first, so the screen does not sit on
top of the capture); `ui_screen show=habitats` and its siblings -- fleets,
research, nations, intel, objectives, council -- open an info screen,
`ui_screen detail=<id>` opens the space object detail panel on a hab, fleet or
body, and `ui_screen rename=true` presses the rename button on the panel that
is up, which is the only way to photograph a rename panel or anything a mod
adds to one. Both change UI state, so both are refused past the pause limit
like any other state change, and each needs an argument that does something:
`ui_view` without a `view`, and `ui_screen` with no `show`, `hide`, `hab`,
`detail` or `rename`, are refused rather than read. `ui_status` is the read
and takes no arguments: the current view, the active scene, the screens this
campaign registered and which one is up, in one call that changes nothing and
answers over a stopped clock. Two things to know: `hab=<id>` raises the game's
own event and the listener runs a frame or two later, so the reply says
`deferred` and you call `ui_status` to read the screen back before capturing;
and `rename` needs its own call after `hab`, though it works in the same call
as `detail`. Open, confirm with `ui_status`, then `screenshot`. Do
nothing more. Never inject OS-level input (xdotool or similar) -- no
synthetic clicks or keystrokes, for any purpose. The server's own tools and
verbs are the sanctioned path for every state change. This includes the ones
that drive UI controllers internally (`alert_choose`, `campaign_new`,
`load_game`, the combat verbs). If no tool, `raw` verb, or `console`
command covers what a test needs, abort that step with an error. State the
exact action you could not perform and where you looked (tool table,
`raw` verb list). A missing verb is filed and built. Never work around it
with a mouse. A synthetic UI action costs ~50 seconds of screenshot-verify
round trips and fails silently when window focus drifts. A verb costs ~3
seconds and fails loudly.

## Diagnosis

- `log_tail which=player`: Unity Player.log -- mod loader `[Manager]` lines,
  exceptions, crashes. `log_tail which=game`: Logs/TerraInvicta.log --
  template registration, merge and save messages. This is the first stop when a mod
  misbehaves.
- `selftest` answers one question before all its others: is the running code
  the code on disk? The server is a Python process your client starts once and
  keeps, so an edit to `server/*.py` reaches it only when the CLIENT
  reconnects the server. Launching the game again does not: that replaces the
  game process and reattaches it to the same server. Nothing else catches
  this. The build is current, the unit suite runs the new files, and the mod
  version has not moved, so every other signal reads healthy while the
  answers come out of code that was replaced hours ago. `offline.serverCode`
  names each file that changed, and drift fails the call, because every other
  line in that report was computed by the old code too. `observe` carries the
  same `serverCode` line whenever it is stale and stays silent otherwise.
- A game update silently reverts the Unity Mod Manager (UMM, the mod loader)
  injection (new UnityEngine.UIModule.dll). `selftest` detects the injection
  state. The installer (`install.sh` on Linux, `install.ps1` on Windows)
  checks an Assembly install (`.original_` backup in Managed/) for that
  revert, repairs it on Linux when it can find UMM's console installer, and
  names the fix otherwise; a Doorstop install (`winhttp.dll` and
  `doorstop_config.ini` in the game root) it reports as present, since a
  game update leaves those files alone.
- `selftest` also diffs the DLL's verb registry against the tool table.
  It checks that ModInfo.json, the DLL and the server all state the same mod
  version. It reads the `use mods` setting (off means JSON/localization mods
  are ignored outright).
- A DLL older than the server answers its newer verbs -- the `designs`,
  `councilors`, `nations` and `scenarios` query kinds among them -- with an
  error saying exactly that. Rebuild the mod and restart the game.
  `selftest`'s verb drift names every gap at once.

## Testing mods

- Run `modcheck` (the check that a mod's templates merged correctly into the
  game) from the main menu: merge, refs, locale, conflicts, reach.
- Run `smoke_test` per scenario: campaign, advance, log scan. It returns to
  the main menu between scenarios rather than relaunching the game, and a
  single named scenario does the same for itself when a campaign is loaded, so
  it can be called from inside one. A game that goes down inside one scenario
  is that scenario's row -- `bridge_lost`, or `bridge_busy` when the calls
  timed out, with `reached` naming the step it died in -- rather than the end
  of the sweep, and the next scenario relaunches the game before it starts.
  The log scan counts an exception or an ERROR line as a failure, minus a
  small allowlist of engine lines that are not one -- the NaN guards in
  `TISpaceShipTemplate.UnnormalizedTemplateSpaceCombatValue`, which substitute
  a fallback and carry on. The allowlist is printed in full on every run and
  what it swallowed is counted per row in `logNoiseIgnored`, so a pass can be
  argued with. A line naming a mod's own data is never on it.
- Fixtures put the object in place instead of playing to it: `spawn_hab`
  (station or base, tier 1-3, named modules), `spawn_module` (one module in
  a hab's first free slot), `spawn_fleet` (combat fixtures), `spawn_army`
  (human, megafauna, invader), `spawn_councilor`, `spawn_alien_site`
  (facility, crashdown, landing, xenoforming). All of them bypass cost,
  prereqs and build time. They prove a mechanism fires. They do not prove content
  is reachable through play.
- Ship designs are the exception in that list, and the pairing is worth
  remembering: a design proves buildability of the parts, `spawn_fleet` proves
  it flies. `design_create` names every part -- hull, drive, power plant,
  radiator, three armor facings, tanks, modules and both weapon groups -- and
  runs the engine's ten validity predicates one at a time, so `reason` names the
  first failure instead of leaving a bare false. It also reports every entry the
  engine would drop in silence, weapons included, and refuses the save on one:
  the engine's own validity check never reads the weapon lists, so a weapon in a
  slot the hull refuses would otherwise save as a warship with no weapon. It
  checks each part with
  the engine's research gate and refuses the save on an illegal one, which makes
  it a real answer about the parts rather than a fixture; `legal=false` is the
  fixture and says so, and it is also what an alien design needs, since the
  engine's research gate reads nearly every alien part as illegal.
  `design_auto` hands the whole search to the engine and
  returns its `ShipDesignerOutcome` by name, which is the quickest way to ask
  whether a faction can field a role at all. `design_delete` reports the
  engine's own `canDelete` gate before it tries anything and names which of its
  three arms fired. Expect the live-ship arm rather than the queue: `spawn_fleet`
  builds ships of a design, and a design something is still flying cannot be
  removed until those ships are gone.
- Any player action with no verb of its own is `raw cmd=action.list
  args={filter}` to find the class and its constructor, then `raw
  cmd=action.invoke args={class, args}` to fire it -- all 99 classes in the
  engine's Actions namespace, reached by reflection. The same fixture caveat applies,
  and louder. It skips cost, availability and turn order outright. It reports
  only what it submitted, so read the effect back with `query` or `inspect`.
  Add `dryRun` to check a call's arguments resolve before firing it.
- The same applies for state you cannot spawn. `kill_state` destroys a hab or kills
  a councilor, army, fleet or space facility by id. `war` declares war,
  white-peaces, occupies, and clears relations cooldowns. `control_points`
  hands over or scrambles CPs. `prospect` reveals one body or every body.
  All four are typed wrappers over the console, so their output is usually
  empty -- verify the effect with `query` or `inspect`. Do not assume success just because
  the call returned. They select the target, check its type and then send one
  console line. This keeps `war action=set_occupied` from crashing
  the underlying command on a selection holding no region. Details their tool
  text leaves out: `killstate` branches on what is selected, so one call
  covers all five target kinds. `war action=declare` leaves a shared
  federation and breaks any alliance before the declaration. `action=occupy`
  works army by army. Each one sets the region it stands in to 100%
  occupied for its nation. `control_points action=give_one` hands over the
  nation's first native CP, or its executive CP when none qualifies. Finally,
  `action=randomize` rolls a human faction per CP.
- `faction_relations` and `set_faction_relation` sit beside `war` and
  `control_points` and cover what they do not: how one faction feels about
  another. `faction_relations` reads and nothing else -- bare it is the active
  player's whole table, `faction=<id>` is someone else's, `other=<id>` narrows
  to one pair -- so it answers over a stopped clock like any other read.
  `set_faction_relation faction=<id> other=<id> hate=<n>` is the write. Set it
  when the thing under test is an AI reaction keyed off hate, rather than
  playing until the factions have reasons. Two habits. Read the numbers out of
  the reply rather than assuming the one asked for landed: the engine clamps to
  the pair's min and max, scales an alien faction's increase by 0.6 until the
  aliens go loud, and moves the target's assessed alien hate on top. And pass
  `cant_conflagrate=true` unless the spread is what is being tested, because an
  increase also reaches the alien proxy and the alien appeaser by default, which
  moves state a later assertion may be reading.
- `spawn_hab` also bypasses orbit station capacity, per-slot module validity
  and the base mine-slot reservation. `spawn_module` bypasses tech gating and
  per-slot validity the same way.
- `hab_build_module` is the opposite of `spawn_module` and the one to use when
  the decision is the thing under test. It submits the same action the
  Habitats screen's confirm popup submits, so the owner is charged, the build
  takes its normal time, and the engine's own upgrade-versus-new-build choice
  is made: with no `target` it prefers a slot holding a module this one
  upgrades from over an empty slot, and `decision` reports which it took, what
  it replaces and whether the price was the upgrade one. It refuses what the
  screen would refuse -- module not allowed here, no slot that will take it,
  not affordable -- and the refusal carries the allowed modules, the slot
  verdicts, or the cost against the treasury. `target` is a module STATE id,
  the same addressing `kill_module` and `module_power` use.
- One hab module is `kill_module module=<state id>`, which `kill_state` does
  not reach (the console's own DestroyModule needs the owning faction's Habs
  screen). Pass `hab` alone to list a hab's module ids. Expect the hab itself
  to go down when its last okay module does. The engine refuses exactly one
  case, a core module or an AlienWormhole module on the alien faction's primary
  hab, and `override_protection=true` is the way past it. Use it only for the
  branches that refusal makes unreachable: no play path destroys that module,
  so the state it leaves proves nothing about play, and the reply says so.
- `module_power module=<state id> on=true|false` flips one module's power
  switch. This is gated the way the Habitats screen gates it and read back afterwards,
  because the engine's setter coerces in silence. `hab` alone is refused with the
  same module listing `kill_module` uses. A `PowerFirst` module -- 24 vanilla
  templates carry the rule, Shipyard and Farm and the barracks among them --
  refuses to shut down unless the hab is already running a power deficit.
  This is the refusal you will hit first.
- Nation stats the console cannot touch are `raw cmd=nation.set_stat
  args={nation, stat, value}`: cohesion, democracy, inequality and education.
  Each is set to a value inside the engine's own range. It refuses unrest,
  miltech, GDP, sustainability and nukes by name and gives you the console
  command for each. This is fixture-grade like the spawns -- it forces the number and
  consults nothing.
- `spawn_fleet`'s location must be an orbit or a hab id; a hab site or a
  fleet is refused, because the engine only places a new fleet at those two
  and null-dereferences on anything else. `spawn_hab`'s location is a hab
  site, orbit, Lagrange point, or planet/moon.
- Use `screenshot` and `assets` for visual surfaces. `workshop_status` and
  `save_check` work with the game down.
- `raw` reaches any bridge verb the tool table lacks. `batch`
  runs several fail-fast in one call.
- National policy is `raw cmd=nation.policies args={nation}` to see what a
  nation can enact and against which targets, then `raw cmd=nation.set_policy
  args={nation, policy, target}` to enact it. Both take names or state ids
  (`policy` takes `PeacefulBreakupOption` or `Grant Independence`). The verb
  runs the AI's own adoption path and keeps its legality. A refusal names
  the condition that failed (executive faction, `benefitsDisabled`,
  `Allowed()`, faction-level handling) instead of forcing anything. Five of
  the options it enacts can wait on the other side instead of passing at
  once. Join federation, unification and demand claim pass inside the call
  only when the target's faction is the enacting nation's executive faction.
  Otherwise they queue a response prompt. Seek peace always queues. Leave
  federation always passes inside the call unless the federation is
  hegemonic. This puts it back under the executive-faction test.
  `readback.awaitingResponse` says which happened (null means the queue could
  not be read, with `readback.promptProbe` naming why). The clock has to
  run before a queued answer lands. Everything else, Grant Independence
  included, passes inside the call with `readback.enactedNow` true.
- Alliances, rivalries and nuclear launches are `raw cmd=faction.diplomacy
  args={nation}` to list, then `args={nation, option, target}` to enact. Those
  five options are the ones `nation.set_policy` refuses. This is the
  contracted path to them. `action.invoke ConfirmPolicyAction` also reaches
  them but bypasses cost, eligibility and confirmation entirely. Use it
  only when you want the bypass. The verb keeps their legality. The target has
  to be in the option's own eligible list. The option's own `Allowed()` has to
  hold. The four relationship changes charge the executive faction the
  same cost the UI pays before enacting. A broke faction is a refusal
  naming the cost. Propose alliance and end rivalry queue a prompt at the
  other side unless one faction holds both nations' executive control points.
  Read `readback.relation` and `readback.awaitingResponse` instead of
  assuming. **`EmployNuclearWeapons` is the actual launch**. It needs
  `confirm:true`. Once made the strike cannot be recalled. The damage
  lands 1800 seconds of game time later whatever you do next. Its target list
  reaches past enemy territory. The defensive-nuke branch puts your own and
  your allies' regions in it wherever an enemy army is standing, capital
  included. Check each target row's `nation` field before firing.
- Destroying enemy hab modules is `raw cmd=fleet.bombard args={fleet, target,
  altitude}`. It orders the engine's own `BombardOperation` and refuses with
  the preconditions it read. A hab is only bombardable at a body other than
  Earth. Hits pick weighted-random modules (zero `okayModules` means the next
  hit kills the hab). The order runs 14 game days, so advance the clock
  and re-read the fleet and hab. Bombarding a human nation's region commits a
  real atrocity (`atrocityCommitted: true` in the return) -- permanent
  campaign state a later assertion may trip over.
- **A fleet is bombardable too, and only while it is landed.** The targets at
  a body other than Earth are the habs and the fleets sitting in a hab site's
  `landedFleets`, so an enemy fleet in orbit cannot be hit at all. A fleet
  docked at a surface base is already in that list; anything else has to be
  put there with `raw cmd=fleet.land args={fleet, site}`, which is the only
  path to the state for a fleet you do not own -- no console command lands
  one, and the UI order is on the fleet's own panel. Spawn into an orbit
  first and land from there: `spawn_fleet` onto a hab site is refused. The
  verb refuses a site that carries a hab, since a fleet reaches a hab by
  docking.
- **A fleet with a transfer assigned** is `raw cmd=fleet.transfer
  args={fleet, destination}`, `destination` being an orbit id. It leaves the
  fleet in its own orbit with a trajectory and an operation counting down to
  the launch, which is the state several fleet orders refuse on and which
  nothing else can set up. It takes the candidate with the longest wait before
  launch and refuses when the longest is the engine's one-second "leave now",
  so it never flies a fleet out from under you; the wait it picked comes back
  as `loiter_s`. Give it somewhere that needs a launch window -- an orbit at
  another body -- and one the faction can already reach, since a destination
  outside its exploration reach is refused. The engine's own fleet-side gate is
  asked whole, so it also refuses a landed fleet, one that fails
  `isCapableOfTransfering`, and one already running a blocking operation that a
  transfer may not interrupt. The refusal prints every clause it read.
- **A contested mission's chance, without running the mission**, is `raw
  cmd=mission.evaluate args={mission, councilor, target}` -- `mission` a
  `TIMissionTemplate` dataName, the other two state ids. This is the only
  way to exercise a rule patched onto `TIMissionResolution_Contested`'s
  `GetSuccessChance` or `GetMissionOutcome` without playing to a mission and
  waiting for the phase to run it. The chance call writes no campaign state
  and draws no random number. **Only its target-validity zero is behind
  `revalidate`**: an inactive councilor returns 0.0 whatever `revalidate` is
  set to, so always read `councilorActive` before reading a chance of 0 as a
  verdict -- a detained or dead councilor otherwise looks exactly like a
  suppression rule firing. `outcomes=<n>` rolls that many (capped at 1000,
  since each roll rebuilds the whole modifier tree twice on the main thread)
  and tallies them by band; those rolls **do** advance the RNG stream, so
  pass `seed` when a later assertion depends on the campaign's rolls. A roll
  cannot honour `revalidate`, so read `rollChance`, the chance a tally comes
  from. It is reported whether or not rolls were asked for: with `outcomes`
  omitted, `rollChance` differing from `chance` is the only thing that says
  the target did not validate. The two modifier lists show which lever moves the
  number, and carry the values computed with the mission's own cost
  resource, which is what fed `chance`. The councilor has to be one a
  faction has seated: a recruit-pool councilor has no faction, which the
  contested resolver reads unguarded, so it is refused up front. No fixture
  can currently deactivate a councilor in place (`kill_state` deletes it;
  `detainme` throws headless), so the inactive-councilor case cannot be
  staged -- read `councilorActive` rather than assuming a test covered it. It bypasses assignment, eligibility, target validity and the
  mission phase entirely, so it proves the rule fires and never that a
  councilor could have been sent.
- `crash_the_game confirm="crash-the-game"` **crashes the running game on
  purpose and ends the session.** It is the fixture for crash handling
  itself: it reports an unhandled exception to Unity's log handler, which is
  the one thing the game's own crash handler acts on, so the process ends up
  in exactly the state the `crashed` stop reason describes. Nothing throws,
  so the bridge answers the call normally from an already-crashed game. Use
  it to check that a client notices the crash and recovers. Recovery is the
  same as after a real crash, so `advance` does it for you: it restarts the
  process, reloads the newest save of the run and reports `crash_recovered`.
  Fire it on a campaign the run reached through `campaign_new` with no
  autosave yet and the recovery has nothing it may reload, which is a
  `crashed` stop rather than a failure. By hand it
  is `game_stop`, `game_start`, `load_game`. Either way anything unsaved is
  gone -- `save_game` first if the campaign is worth keeping. Without the
  exact `confirm` string the call is refused and nothing happens, so there
  is no way to fire it by mistake. If the bridge disappears instead of
  reporting `crashed`, the crash dialog itself failed and the game quit
  outright; that is a different failure and worth a `log_tail which=player`.
  To stop the game without crashing it, call `game_stop`.
