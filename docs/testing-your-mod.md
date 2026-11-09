# Testing your mod

This guide is for mod authors. You do not run these checks yourself. You
ask your AI assistant for them in plain English. This page tells you what
to ask for, what the answers mean, and how to fix common problems.

## The five-minute check

With your mod enabled, ask:

> "Start the game and check my mods."

You will get one overall result for each mod: PASS, WARN, DEGRADED, FAIL or
ERROR. **WARN with a few warnings is normal.** A warning tells you something
worth knowing. It does not always mean that you broke something. You also get a
verdict for each value that your mod sets. What these verdicts mean:

| Verdict | Meaning | What to do |
|---|---|---|
| **OK** | Your value is in the running game exactly as you wrote it. | Nothing. |
| **SHADOWED** | Another mod set the same field after yours. The report names it. | Decide which mod gets the field. The mod with the higher `LoadOrder` in its ModInfo.json takes the field. |
| **MISSING** | Your entry never reached the game. | Make sure that `dataName` has no typing mistake. Make sure that your file name exactly matches a real template file. |
| **MISMATCH** | The game holds a value that is different from the value in your file. | The usual cause is the array trap. See below. |
| **DISABLED** | Your entry has `disable: true` and the game skipped it. | If you want the entry in the game, delete that setting. Otherwise, do nothing. |
| **UNPARSED** | Your JSON file has a syntax error. | The report points at the file. A missing comma or bracket is the usual cause. |
| **CRASH_RISK** | A JSON file in your mod does not match any game template file name. | Rename or remove the file. Otherwise, the game can crash at startup. |

### The array trap (the #1 real-world mod bug)

If your mod changes a list (for example incomes, class chances, or special
rules), the game matches list entries **by position**. It ignores the name. If
the base game has four entries and you write one, yours lands on position one
and the other three stay unchanged. If you write a new entry to add it, your
entry overwrites the entry at that position.

There are two fixes. Either one works:

- Restate **the whole list** in your mod, in order, even the parts that you
  did not change.
- Add the file name to `TemplatesToConcatArrays` in your ModInfo.json.
  This makes your entries append to the list. They do not overwrite other
  entries.

The checker reports a suspected case as "index-merge misalignment". It names
the position that it thinks you overwrote.

## Checking references and text

Ask:

> "Check that everything my mod references actually exists."

This finds a tech that requires a tech you renamed, or a project that grants
a missing effect. It also finds a ship design that points at a missing
weapon. The base game passes these checks. Anything that the report lists
is a real error.

> "Check my mod's text."

Every name and description that your mod uses goes through the game text
system. A "fell back" result means that the game will show original text or
a raw internal key in place of your text. If your mod ships only English
text, the report shows a note. This note is normal.

## Testing in a live game

> "Start a new campaign with my mod and run the first month. Tell me if
> anything breaks."

The assistant starts the campaign for any scenario, including the DLC ones.
It runs the clock at full speed, deals with routine popups, and watches the
logs for errors. Two things to know:

- **The clock freezes for decisions.** Popups, story events, and unresolved
  battles all stop time. The assistant handles routine popups
  automatically. When a real choice comes up, the assistant stops and shows
  you the event text and its options. You can choose. If you told the
  assistant your preference, it can choose instead.
- **Story events can be forced.** If your mod changes an event, ask for it
  by name: "trigger the Dry Hole event and pick option two". You do not have
  to wait for it to fire naturally.

**A stopped clock is treated as a stuck test.** If game time does not move
for five minutes, the assistant refuses any action that changes the game. It
does this because a clock that stays still that long nearly always means a
stuck test. The assistant tells you so. To clear this state, move the clock,
or start or load a game. You can still ask for values, text, logs and
screenshots during this time. They come back with a warning attached. Some
setups take longer than five minutes with time stopped. If yours does, say
so: "raise the pause limit to twenty minutes while we build this up." Or
advance one day between setup steps. That works just as well.

For ship mods:

> "Spawn a fleet of my new ships and have them fight an alien fleet."

The battle resolves in the background and you get the outcome. This proves
that the design builds, flies, and shoots.

For portrait and art mods:

> "Check that my portrait pack's images all load."

The checker checks every image path that your mod declares against the
loaded asset bundles. It finds a typing mistake in a path. Without this
check, such a mistake shows up as a blank councilor photo with nothing in
any log.

## Forcing the exact situation your mod changes

The assistant can put the game into the state that your mod needs. You do
not have to play for hours to reach it:

- **Nation stats.** For example, ask "Set France's cohesion to 2". The
  assistant can force cohesion, democracy, inequality and education to a
  value. In seconds, you can test a threshold that your mod adds.
- **Events, orgs and councilors.** For example, ask "Fire the Dry Hole event",
  "give one of my councilors the org my mod adds", "give that councilor 500
  XP", "add the Paranoid trait", or "set France's unrest to 2". Each request
  is one call (`trigger_event`, `give_org`, `councilor_xp`,
  `councilor_trait`, `nation_stat`). `nation_stat` also covers miltech,
  sustainability, nukes, unrest and GDP, in addition to the four stats
  above.
- **Upheaval and borders.** For example, ask "Start a coup in France", "give
  Kurdistan its independence", "have Germany absorb Austria", or "hand this
  region to Poland". Revolutions, regime changes, coups, secessions,
  annexations, unifications and direct IP investment are each one call
  (`nation_event`).
- **Armies, fleets and councilors.** For example, ask "Leave that army at
  quarter strength" or "send it home". You can also ask "drain the fleet to
  half its delta-v", "damage one of its ships", or "put an officer on the
  Enterprise". The calls are `army_edit` and `fleet_edit`. For councilors,
  ask "move this councilor to Paris" or "have the Servants detain him"
  (`councilor_move`, `councilor_detain`).
- **Faction-wide switches.** For example, ask "Turn one of the Servants'
  councilors into my mole", "make every mission succeed", or "play as the
  Academy for a while". You can also ask "add 20 ppm of CO2", "push my
  ideology in every nation", or "unlock every ship part". The calls are
  `turn_faction`, `force_missions`, `set_player_faction`, `atmosphere`,
  `propaganda` and `unlock_all_ship_parts`. `grant` also finishes a
  faction's slot-3 project. As a last resort, `grant` finishes every tech at
  once.
- **Policies and diplomacy.** For example, ask "Have Switzerland enact Grant
  Independence" or "make the USA and Canada allies". The assistant enacts
  these directly. The game rules still apply, so a refusal comes back with
  the reason.
- **Losing things.** For example, ask "Destroy the mining complex on that
  station". The assistant destroys one named module at a time. You can test
  a mod that reacts to damage or loss without staging a battle.
- **Screenshots that always show the game.** Captures happen inside the
  game itself. If the window is covered or in the background, the picture
  still shows the game.
- **Tooltips as text.** The hover text behind a nation's numbers reads
  back as plain text. You do not need a screenshot.

### Comparing a tooltip before and after

Suppose that your mod adds an investment bonus to nations. Ask:

> "Start a campaign, read the investment tooltip for Germany, trigger my
> mod's bonus, and read the tooltip again. Did my line show up and did
> the total move?"

The assistant quotes the actual tooltip text back to you, before and after.
This is the same text that a player sees on hover. The check compares two
text strings side by side. You do not have to guess from two screenshots.

## Scenario mods and the Dark Skies DLC

Scenarios like the DLC's 2003 and Broken Earth carry their own variants of
game data. A `scenarioTags` field on each entry selects this data. Two
things matter for you:

- If your entry has the same name as a base game entry and neither entry
  has `scenarioTags`, the game keeps the base game entry. It drops your
  entry and gives no message. The checker reports this case.
- What players see depends on the scenario they pick. Ask "check my mod in
  the 2003 scenario" to get verdicts for that scenario's view of the data.

## Keeping your mod healthy over time

> "Are my installed mods up to date on the Workshop?"

This compares your local copies with the Workshop. It flags mods whose
authors moved on. This tells you whether a conflict is worth a report to
the author.

> "Will my save still work if I disable mod X?"

Saves remember which mod content they use. This names exactly what breaks
without that mod, before you find out the hard way mid-campaign.

After every game update, run the installer again in the mod folder
(`./install.sh`, or `install.ps1` on Windows). Then ask for a mod check
again. Updates rewrite game files and quietly break things that worked
yesterday.

## When the game crashes

Ask:

> "The game crashed -- what happened?"

The assistant reads both game logs and can usually name the mod and file at
fault. If you file a bug against a mod, ask the assistant to write up what
it found. The log lines and the reproduction steps make a report that an
author can fix.

## What this can't check

A person must judge whether your art looks good, whether your balance is
fun, and whether UI layouts render correctly. Screenshots help, but eyes are
the tool. Audio, playing video portraits, and multiplayer are untested
territory.
