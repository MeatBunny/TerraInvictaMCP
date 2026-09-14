# Testing your mod

This guide is for mod authors. You do not run these checks yourself --
you ask your AI assistant for them in plain English. This page tells you what
to ask for, what the answers mean, and how to fix common problems.

## The five-minute check

With your mod enabled, ask:

> "Start the game and check my mods."

You will get a verdict for each mod. **PASS with a few warnings is normal**
-- warnings are things worth knowing. They do not always mean you broke
something. What the verdicts mean:

| Verdict | Meaning | What to do |
|---|---|---|
| **OK** | Your value is in the running game exactly as you wrote it. | Nothing. |
| **SHADOWED** | Another mod set the same field after yours. The report names it. | Decide who should win. The mod with the higher `LoadOrder` in its ModInfo.json takes the field. |
| **MISSING** | Your entry never reached the game. | Check for a typed mistake in `dataName`, or your file name does not exactly match a real template file. |
| **MISMATCH** | The game holds a different value than your file says. | This is usually the array trap -- see below. |
| **DISABLED** | Your entry has `disable: true` and the game skipped it. | Fix this only if you want the entry enabled. |
| **UNPARSED** | Your JSON file has a syntax error. | The report points at the file. A missing comma or bracket is the usual cause. |
| **CRASH_RISK** | A JSON file in your mod does not match any game template file name. | Rename or remove it -- the game can crash at startup over this. |

### The array trap (the #1 real-world mod bug)

When your mod changes a **list** -- incomes, class chances, special
rules -- the game matches list entries **by position**. It ignores the
name. If the base game has four entries and you write one, yours lands on
position one and the other three stay unchanged. If you write a *new* entry
to add it, you *overwrite whatever was at that position*.

Two fixes; either one works:

- Restate the **whole list** in your mod, in order, even the parts you did not
  change.
- Add the file name to `TemplatesToConcatArrays` in your ModInfo.json.
  This makes your entries append to the list. They will not overwrite.

The checker flags suspected cases as "index-merge misalignment" and names the
position it thinks you overwrote.

## Checking references and text

Ask:

> "Check that everything my mod references actually exists."

This catches a tech requiring a tech you renamed, a project granting an
effect that is missing, or a ship design pointing at a missing weapon. The
base game passes these checks. Anything reported is a real error.

> "Check my mod's text."

Every name and description your mod uses goes through the
game text system. A "fell back" result means the game will show original
text or a raw internal key where your text should be. If your mod
only ships English text, the report shows a note -- it is
normal.

## Testing in a live game

> "Start a new campaign with my mod and run the first month. Tell me if
> anything breaks."

The assistant starts the campaign for any scenario, including the DLC ones.
It runs the clock at full speed, deals with routine popups, and watches the
logs for errors. Two things to know:

- **The clock freezes for decisions.** Popups, story events, and unresolved
  battles all stop time. The assistant handles routine popups automatically. When a real
  choice comes up, the assistant stops and shows you the event text and its
  options. *You* can choose, or the assistant can choose if you told it your preference.
- **Story events can be forced.** If your mod changes an event, ask for it by
  name: "trigger the Dry Hole event and pick option two" -- you do not have to wait for it
  to fire naturally.

**A stopped clock is treated as a stuck test.** If game time does not move for
five minutes, the assistant starts refusing anything that would change the
game, and says so: a clock that has sat still that long is nearly always a
test that has quietly wedged rather than one still working. Moving the clock
clears it, and so does starting or loading a game. Reading keeps working the
whole time -- values, text, logs and screenshots all still answer, with a
warning attached. Setting up an elaborate situation can legitimately take
longer than five minutes with time stopped, so if that is the plan, say so:
"raise the pause limit to twenty minutes while we build this up." Advancing a
day between setup steps works just as well.

For ship mods:

> "Spawn a fleet of my new ships and have them fight an alien fleet."

The battle resolves in the background and you get the outcome. This proves
the design builds, flies, and shoots.

For portrait and art mods:

> "Check that my portrait pack's images all load."

Every image path your mod declares is tested against the loaded asset
bundles. A typed mistake in a path -- which otherwise shows up as a
blank councilor photo with nothing in any log -- is caught here.

## Forcing the exact situation your mod changes

The assistant can put the game into the state your mod needs. You do not have
to play for hours to reach it:

- **Nation stats.** "Set France's cohesion to 2" -- cohesion, democracy,
  inequality and education can all be forced to a value. A threshold
  your mod adds is testable in seconds.
- **Policies and diplomacy.** "Have Switzerland enact Grant Independence"
  or "make the USA and Canada allies" -- enacted directly. The game
  rules still apply, so a refusal comes back with the reason.
- **Losing things.** "Destroy the mining complex on that station" -- one
  named module at a time. A mod that reacts to damage or loss is
  testable without staging a battle.
- **Screenshots that always show the game.** Captures happen inside the
  game itself. The picture shows the game even when the window is covered
  or in the background.
- **Tooltips as text.** The hover text behind a nation's numbers reads
  back as plain text. You do not need a screenshot.

### Comparing a tooltip before and after

Say your mod adds an investment bonus to nations. Ask:

> "Start a campaign, read the investment tooltip for Germany, trigger my
> mod's bonus, and read the tooltip again. Did my line show up and did
> the total move?"

The assistant quotes the actual tooltip text back to you -- the same
words a player sees on hover -- before and after. The check compares
two text strings side by side. You do not have to guess from two screenshots.

## Scenario mods and the Dark Skies DLC

Scenarios like the DLC's 2003 and Broken Earth carry their own variants of
game data. Tags select this data. Two things matter for you:

- If your entry collides with a base game name **and neither carries tags**,
  the game silently keeps the base game entry and drops yours. The checker calls this
  out.
- What players see depends on the scenario they pick. Ask "check my mod in
  the 2003 scenario" to get verdicts for that scenario's view of the data.

## Keeping your mod healthy over time

> "Are my installed mods up to date on the Workshop?"

This compares your local copies against the Workshop. It flags mods whose
authors have moved on -- this is useful for knowing whether a conflict
is worth reporting to the author.

> "Will my save still work if I disable mod X?"

Saves remember which mod content they use. This names exactly what would
break -- before you find out the hard way mid-campaign.

After every **game update**, run the installer again in the mod folder
(`./install.sh`, or `install.ps1` on Windows). Then ask for a mod check
again. Updates rewrite game files and quietly break things that worked
yesterday.

## When the game crashes

Ask:

> "The game crashed -- what happened?"

The assistant reads both game logs and can usually name the mod and file at
fault. If you are filing a bug against a mod, ask the assistant to write up
what it found -- the log lines plus the reproduction steps make a
report an author can fix.

## What this can't check

Whether your art looks *good*, whether your balance is *fun*, and whether UI
layouts render correctly are still human judgment --
screenshots help, but eyes are the tool. Audio, playing video portraits,
and multiplayer are untested territory.
