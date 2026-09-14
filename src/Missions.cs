using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // mission.evaluate.
    //
    // A councilor mission's contested resolution, evaluated without running it.
    // The mission phase is the only place the engine calls these itself, and it
    // calls them with a councilor already assigned, a target already validated
    // and a clock already frozen; a rule that lives as a patch on either method
    // is otherwise reachable only by playing to a mission and waiting for the
    // phase. This calls both directly, so the patched path runs with none of
    // that timing.
    //
    // Both are safe to call out of phase, which is why this verb can exist:
    //
    // TIMissionResolution_Contested.GetSuccessChance draws no random number and
    // writes no campaign state. It is not pure: GetAllModifiers AddRanges the
    // resolution template's OWN persistent attackingModifiers and
    // defendingModifiers instances rather than copies (only the trait, context
    // and campaign-difficulty modifiers are constructed per call), and
    // TIMissionModifier_ResourceSpent.GetModifier stores its resource argument
    // into a field on one of those shared instances before computing. That
    // write is the engine's own idiom -- the mission panel drives it the same
    // way every time it refreshes -- and it decides nothing but the modifier's
    // own display name, so calling out of phase is safe. It is stated here
    // because "pure" is the licence this verb runs on, and it would be wrong.
    //
    // ONLY THE TARGET-VALIDITY ZERO IS GATED. The method opens
    // `ldarg.s 5; brfalse.s IL_001f`, and IL_001f is the councilor.active test:
    // reValidateTarget == false jumps PAST the target validation and INTO the
    // active check. So an inactive councilor returns 0.0 at every setting of
    // `revalidate`, which is why councilorActive is reported unconditionally --
    // a detained or dead councilor otherwise reads as a suppression rule
    // firing, on exactly the claim this verb exists to prove.
    //
    // GetMissionOutcome mutates nothing either, but it CONSUMES the RNG: one
    // RandomFloatValue draw, plus a second RandomRange draw when the councilor
    // is turned. That is the whole reason `outcomes` defaults to 0 and `seed`
    // exists. TIUtilities' generator and its stored-state stack are both
    // [ThreadStatic] and every verb runs on the main thread, so a push here
    // cannot disturb the AI planner's threadpool work -- and the planner's own
    // GetSuccessChance calls touch no RNG in any case. PopRandomState throws on
    // an empty stack, so the pop is in a finally and is only ever made when the
    // matching push was.
    //
    // GetMissionOutcome also recomputes the chance itself, with
    // reValidateTarget hard-coded false at its IL_0006. A roll therefore cannot
    // honour `revalidate`, which is what rollChance below reports.
    public static partial class Verbs
    {
        #region mission.evaluate

        // A roll is not two float draws. GetMissionOutcome recomputes the chance
        // every iteration, and that chance is Difficulty, which is
        // SumAttackingModifiers plus SumDefendingModifiers, each of which builds
        // the whole modifier tree through GetAllModifiers. So one roll costs TWO
        // full tree builds -- allocations, per-trait and per-context
        // construction, a LINQ pass -- on the main thread, inside the verb, with
        // the game waiting on it.
        //
        // The cap is therefore set on tree builds rather than on draws, and on
        // what a tally actually has to settle: distinguishing the four outcome
        // bands. At 1000 rolls the standard error on a band's proportion is
        // under 1.6 points, finer than any band boundary this reports, and the
        // cost is 2000 tree builds. Ten times that buys a third decimal nobody
        // asserts on and pays for it in a visible stall.
        const int MaxMissionRolls = 1000;

        // Candidate names quoted in a resolution failure, here and in action.invoke.
        const int MaxNamesListed = 20;

        // Modifiers listed per side. The engine's own lists run to a handful,
        // so this is a ceiling rather than a live cut -- but a list sold as the
        // tuning lever must never be quietly short, so the engine's own count
        // is reported beside each list the way rollsRequested is reported
        // beside rolls, and a caller can compare it against maxModifiers.
        const int MaxMissionModifiers = 64;

        // Effects are never claimed, in the voice action.invoke uses: this
        // reports the call it made and what came back, and nothing about
        // whether the mission could have been ordered.
        const string EvaluateNote = "evaluated, not run: this bypasses "
            + "assignment, eligibility, target validity and the mission phase "
            + "entirely, and nothing in the campaign is written. "
            + "GetSuccessChance draws no random number; each outcome roll draws "
            + "from the calling thread's RNG stream (twice for a turned "
            + "councilor) and advances it unless 'seed' is given. A chance of 0 "
            + "can come from an inactive councilor at ANY setting of "
            + "'revalidate' -- only the target-validity zero is gated -- so "
            + "read 'councilorActive' before reading a 0 as a verdict. Rolls "
            + "cannot honour 'revalidate': GetMissionOutcome recomputes the "
            + "chance with it hard-coded false, so 'rollChance' is the chance "
            + "the tally was generated from.";

        static JToken MissionEvaluate(JObject args)
        {
            // Everything resolves before anything is called, so a refusal never
            // leaves a half-evaluated call behind and never passes a null on.
            // Trimmed before anything reads it. TemplateManager.Find matches the
            // padded string exactly as ui.describe's two lookups do, and
            // NearMissionNames searches the same padded string, so an untrimmed name
            // misses AND has its near-name hint come back empty.
            string wanted = Str(args, "mission");
            if (wanted != null) wanted = wanted.Trim();
            if (string.IsNullOrEmpty(wanted))
                throw new VerbError("missing arg 'mission' (a TIMissionTemplate "
                    + "dataName)");
            TIMissionTemplate mission = TemplateManager.Find<TIMissionTemplate>(
                wanted, false);
            if (mission == null)
                throw new VerbError("no TIMissionTemplate named '" + wanted + "'"
                    + NearMissionNames(wanted)
                    + "; query.template type=TIMissionTemplate lists them");

            TICouncilorState councilor = Arg<TICouncilorState>(args, "councilor");
            TIGameState target = Arg<TIGameState>(args, "target");
            RequireSeatedCouncilor(councilor);
            float resources = Float(args, "resources", 0f);
            bool revalidate = Flag(args, "revalidate");

            TIMissionResolution resolution = Safe<TIMissionResolution>(
                delegate { return mission.resolutionMethod; }, null);
            if (resolution == null)
                throw new VerbError("mission '" + mission.dataName + "' has no "
                    + "resolutionMethod, so there is nothing to evaluate");
            var contested = resolution as TIMissionResolution_Contested;
            if (contested == null)
                throw new VerbError("mission '" + mission.dataName + "' resolves "
                    + "through " + resolution.GetType().Name + ", not "
                    + "TIMissionResolution_Contested; this verb evaluates the "
                    + "contested resolver only (TIMissionResolution_Automatic "
                    + "answers a constant 1.0 chance and a constant Success "
                    + "outcome, so there is nothing here to evaluate)");

            // Read through the token rather than OptionalInt's -1-means-absent:
            // a caller who asked for nothing must not be told it requested -1.
            int rollsAsked = 0;
            JToken rollsArg = args != null ? args["outcomes"] : null;
            if (rollsArg != null && rollsArg.Type != JTokenType.Null)
                rollsAsked = Int(args, "outcomes");
            int rolls = rollsAsked;
            if (rolls < 0) rolls = 0;
            if (rolls > MaxMissionRolls) rolls = MaxMissionRolls;

            JToken seedArg = args != null ? args["seed"] : null;
            bool seeded = seedArg != null && seedArg.Type != JTokenType.Null;
            int seed = seeded ? Int(args, "seed") : 0;

            // SumModifiers' own expression, null guard included: it reads
            // mission.cost and falls back to FactionResource.None when the
            // mission has no cost at all. Every modifier value below is computed
            // with this, because the chance was.
            FactionResource resource = Safe<FactionResource>(delegate
            {
                TIMissionCost cost = mission.cost;
                return cost != null ? cost.resourceType : FactionResource.None;
            }, FactionResource.None);

            float chance = SuccessChance(contested, mission, councilor, target,
                resources, revalidate);

            var o = new JObject();
            o["mission"] = mission.dataName;
            o["resolution"] = resolution.GetType().Name;
            o["councilor"] = Describe(councilor);
            // Unconditional, both of them. A chance of 0 out of an inactive
            // councilor under revalidate=true is the engine's early return and
            // not a verdict about the mission, and `turned` is what decides
            // whether a roll costs one RNG draw or two.
            Put(o, "councilorActive", delegate { return (JToken)councilor.active; });
            Put(o, "councilorTurned", delegate { return (JToken)councilor.turned; });
            o["target"] = Describe(target);
            o["resources"] = Num(resources);
            o["revalidate"] = revalidate;
            o["chance"] = Num(chance);
            // The two modifier lists the chance was built out of, so a caller
            // tuning a chance into a band knows which lever to move instead of
            // guessing at it.
            // Which resource the values below were computed with, so a caller
            // reading a ResourceSpent row can see why it reads Money or Ops.
            o["costResource"] = resource.ToString();
            // The engine's own list lengths travel with the lists. Both are
            // shorter than maxModifiers on every stock mission, and a caller
            // reading a lever off a list has to be able to tell that from a
            // list that was cut.
            int attackingTotal, defendingTotal;
            string attackingError, defendingError;
            o["attackingModifiers"] = Modifiers(contested, mission, councilor,
                target, resources, resource, true, out attackingTotal,
                out attackingError);
            o["defendingModifiers"] = Modifiers(contested, mission, councilor,
                target, resources, resource, false, out defendingTotal,
                out defendingError);
            // -1 for a list the engine could not produce, never 0. A swallowed throw
            // reported as an empty list with total 0 is byte-identical to a mission
            // with no modifiers on that side, on the very field this pair exists to
            // let a caller check the list against.
            o["attackingModifiersTotal"] = attackingTotal;
            o["defendingModifiersTotal"] = defendingTotal;
            o["attackingModifiersError"] = attackingError != null
                ? (JToken)new JValue(attackingError) : JValue.CreateNull();
            o["defendingModifiersError"] = defendingError != null
                ? (JToken)new JValue(defendingError) : JValue.CreateNull();
            o["maxModifiers"] = MaxMissionModifiers;
            o["rollsRequested"] = rollsAsked;
            o["rolls"] = rolls;
            o["maxRolls"] = MaxMissionRolls;
            o["seed"] = seeded ? (JToken)new JValue(seed) : JValue.CreateNull();

            // GetMissionOutcome recomputes the chance with reValidateTarget
            // hard-coded false, so under revalidate=true the tally comes from a
            // different number than `chance`. Reporting both, rather than
            // refusing the combination: revalidate=true on a VALID target is
            // perfectly coherent and the two agree, and it is only the invalid
            // one that diverges. With revalidate=false the call above already
            // used false, so there is nothing to recompute.
            // Guarded like the first, and this is the one that can actually throw:
            // GetSuccessChance returns 0.0 at IL_0027 when reValidateTarget is true and
            // the target fails validation, never reaching Difficulty at IL_0033, while
            // reValidateTarget=false walks straight into the modifier chain. So a
            // wrong-kind target under revalidate=true survives the call above and
            // throws here.
            float rollChance = revalidate
                ? SuccessChance(contested, mission, councilor, target, resources, false)
                : chance;
            // Reported whether or not a roll was asked for, and so is the divergence
            // below. The two numbers are what says the target did not validate: with
            // 'outcomes' omitted, gating this on rolls left `chance: 0.0,
            // councilorActive: true, rollChance: null` and no warning -- which is
            // exactly what a mission the engine rates at zero looks like, the
            // misreading this verb's note exists to prevent.
            o["rollChance"] = Num(rollChance);
            o["outcomes"] = rolls > 0
                ? Rolls(contested, mission, councilor, target, resources, rolls,
                    seeded, seed)
                : JValue.CreateNull();
            // Exact float comparison on purpose: both numbers come out of the same
            // method over the same inputs, so any difference at all is the gate and
            // nothing else. And the gated-zero reading is only asserted when `chance`
            // IS zero, rather than assumed from the divergence.
            if (revalidate && rollChance != chance)
            {
                string tally = rolls > 0
                    ? "the tally was generated from 'rollChance'"
                    : "a tally would be generated from 'rollChance'";
                o["warning"] = (chance == 0f
                        ? "'revalidate' was true and the target did not validate, so "
                          + "'chance' is the gated 0.0 while "
                        : "'revalidate' was true and the two chances differ, so "
                          + "'chance' is the revalidated number while ")
                    + tally + " ("
                    + rollChance.ToString(CultureInfo.InvariantCulture)
                    + "): GetMissionOutcome recomputes the chance with "
                    + "reValidateTarget hard-coded false and cannot honour the "
                    + "argument. Re-run with revalidate=false for one number.";
            }
            o["note"] = EvaluateNote;
            return o;
        }

        // Target validity is deliberately bypassed and `target` is any TIGameState, so
        // a target of a kind this mission's modifiers do not expect walks into the
        // contested chain and throws inside the engine. Named here instead, because the
        // contract this verb states is that everything resolves before anything is
        // called, and a bare NullReferenceException out of the engine reads as a bug in
        // the harness rather than as a wrong argument. Every call into the resolver
        // goes through this, including the roll loop's.
        static float SuccessChance(TIMissionResolution_Contested contested,
            TIMissionTemplate mission, TICouncilorState councilor,
            TIGameState target, float resources, bool revalidate)
        {
            try
            {
                return contested.GetSuccessChance(mission, councilor, target,
                    resources, revalidate);
            }
            catch (Exception e)
            {
                throw new VerbError(ResolverThrew("GetSuccessChance", mission, target, e));
            }
        }

        static string ResolverThrew(string method, TIMissionTemplate mission,
            TIGameState target, Exception e)
        {
            return "TIMissionResolution_Contested." + method + " threw for mission '"
                + mission.dataName + "' against " + StateName(target) + " ("
                + (int)target.ID + ", a " + target.GetType().Name + "): " + Note(e)
                + ". This verb does not validate the target, so the usual cause is a "
                + "target of a kind this mission's modifiers do not expect; nothing "
                + "was written";
        }

        // The roll loop, and only the roll loop, runs under the pushed seed:
        // GetSuccessChance above draws nothing, so widening the window would
        // claim a determinism the chance does not need.
        static JToken Rolls(TIMissionResolution_Contested contested,
            TIMissionTemplate mission, TICouncilorState councilor,
            TIGameState target, float resources, int rolls, bool seeded, int seed)
        {
            var counts = new Dictionary<int, int>();
            if (seeded) TIUtilities.PushRandomState(seed);
            try
            {
                for (int i = 0; i < rolls; i++)
                {
                    TIMissionResult result;
                    try
                    {
                        result = contested.GetMissionOutcome(
                            mission, councilor, target, resources);
                    }
                    catch (Exception e)
                    {
                        // GetMissionOutcome recomputes the chance with reValidateTarget
                        // hard-coded false, so it reaches the modifier chain on every
                        // iteration and throws on the same wrong-kind target. Named the
                        // same way, and the finally below still pops the seed.
                        throw new VerbError(ResolverThrew("GetMissionOutcome",
                            mission, target, e));
                    }
                    int band = (int)result.outcome;
                    int seen;
                    counts[band] = counts.TryGetValue(band, out seen) ? seen + 1 : 1;
                }
            }
            finally
            {
                // PopRandomState throws on an empty stack, so it is made only
                // for a push this call actually made.
                if (seeded) TIUtilities.PopRandomState();
            }

            // Every member of the enum, zeros included: an assertion that a band
            // never came up is the useful one, and a missing key cannot say that.
            var o = new JObject();
            Array members = Enum.GetValues(typeof(TIMissionOutcome));
            for (int i = 0; i < members.Length; i++)
            {
                var outcome = (TIMissionOutcome)members.GetValue(i);
                int band = (int)outcome;
                int seen;
                o[outcome.ToString()] = counts.TryGetValue(band, out seen) ? seen : 0;
                counts.Remove(band);
            }
            // An outcome value with no enum member behind it would otherwise be
            // dropped from a tally that claims to be complete.
            foreach (KeyValuePair<int, int> left in counts)
                o[left.Key.ToString()] = left.Value;
            return o;
        }

        // The engine's own non-zero lists, re-read with the resource SumModifiers
        // uses so the reported values are the ones that fed `chance`.
        //
        // The filter inside GetNonZeroModifiers passes FactionResource.None,
        // which is why a ResourceSpent modifier can survive it and then report a
        // different number here. That is the engine's own inconsistency, not one
        // introduced here: SumModifiers builds the chance with
        // mission.cost.resourceType, so that is the number worth reporting.
        static JToken Modifiers(TIMissionResolution_Contested contested,
            TIMissionTemplate mission, TICouncilorState councilor,
            TIGameState target, float resources, FactionResource resource,
            bool attacking, out int total, out string error)
        {
            total = 0;
            error = null;
            var a = new JArray();
            List<TIMissionModifier> list;
            try
            {
                list = attacking
                    ? contested.GetAttackingNonZeroModifiers(mission, councilor,
                        target, resources)
                    : contested.GetDefendingNonZeroModifiers(mission, councilor,
                        target, resources);
            }
            catch (Exception e)
            {
                // Not a Safe fallback of null. An empty list beside total 0 is what a
                // mission with no modifiers on this side answers, so a swallowed throw
                // would be indistinguishable from it on the field a caller checks the
                // list against.
                total = -1;
                error = Note(e);
                return a;
            }
            if (list == null)
            {
                total = -1;
                error = "the engine returned no list";
                return a;
            }
            // What the engine handed over, before the cap and before the null
            // entries the loop skips: this is the number maxModifiers is worth
            // comparing against.
            total = list.Count;
            for (int i = 0; i < list.Count && i < MaxMissionModifiers; i++)
            {
                TIMissionModifier modifier = list[i];
                if (modifier == null) continue;
                var o = new JObject();
                o["type"] = modifier.GetType().Name;
                // VALUE BEFORE NAME, and with the mission's own resource. Both
                // halves matter, and both are the panel's own order:
                // CouncilorMissionCanvasController.UpdateModifierList reads
                // cost.resourceType, calls GetModifier, and only then reads
                // displayName.
                //
                // TIMissionModifier_ResourceSpent.GetModifier stores the
                // resource into a field its own get_displayName reads back
                // through TIUtilities.GetResourceString. Reading the name first
                // would report whatever the previous caller left there, and
                // passing None would build "UI.Global.None", a key with no
                // localization entry, in place of "Money" or "Ops".
                //
                // The resource also changes the NUMBER: that modifier is
                // 1 + log2(r / missionMoneyMultiplier) for Money and
                // 1 + log2(r) otherwise, and SumModifiers -- which is what
                // built `chance` above -- passes exactly this expression. A
                // list sold as the tuning lever has to hold the values that
                // actually fed the chance.
                //
                // So the name is read ONLY after that call returned. Put would have
                // turned a throwing GetModifier into value: null and then read the name
                // anyway, off whatever resource the last successful caller left in the
                // shared instance -- a null value beside a confidently wrong name, with
                // nothing marking either.
                string failure = null;
                try
                {
                    o["value"] = Num(modifier.GetModifier(councilor, target, resources,
                        resource));
                }
                catch (Exception e)
                {
                    o["value"] = JValue.CreateNull();
                    failure = Note(e);
                }
                if (failure == null)
                    Put(o, "name", delegate { return (JToken)modifier.displayName; });
                else
                {
                    o["name"] = JValue.CreateNull();
                    o["error"] = failure;
                }
                a.Add(o);
            }
            return a;
        }

        // A councilor off a faction's council has no faction, and the contested
        // chain reads that faction without guarding it: GetAllModifiers stores
        // councilor.faction into each context modifier's sourceFaction, and
        // GetAttribute reaches through it for the org and trait bonuses. The
        // result is an unhandled NullReferenceException out of the engine.
        //
        // Refused here, before any engine call, because a recruit-pool councilor
        // is a perfectly ordinary id: query.councilors hands them out, so this is
        // reached by accident rather than by misuse. faction is a plain
        // auto-property backed by a field, so reading it to make this test cannot
        // itself throw.
        static void RequireSeatedCouncilor(TICouncilorState councilor)
        {
            TIFactionState faction = Safe<TIFactionState>(
                delegate { return councilor.faction; }, null);
            if (faction != null) return;
            throw new VerbError("councilor " + (int)councilor.ID + " ("
                + StateName(councilor) + ") has no faction, so it sits in the "
                + "recruit pool rather than on a council. The contested resolver "
                + "reads the councilor's faction without a null check and would "
                + "throw inside the engine; pass a councilor a faction has "
                + "actually seated (query.councilors faction=<id> lists those)");
        }

        static string NearMissionNames(string wanted)
        {
            var names = new List<string>();
            try
            {
                foreach (TIMissionTemplate t in
                    TemplateManager.IterateByClass<TIMissionTemplate>(true))
                    if (t != null && t.dataName != null) names.Add(t.dataName);
            }
            catch (Exception) { return ""; }
            return Near(names, wanted);
        }

        // The "closest by name" tail a not-found refusal carries: a case-insensitive
        // substring pass over the candidate names, capped, with an ellipsis when more
        // matched than are listed. Shared with action.invoke's class resolution, whose
        // candidates come from the action catalog instead.
        static string Near(List<string> names, string wanted)
        {
            if (string.IsNullOrEmpty(wanted) || names == null) return "";
            var near = new List<string>();
            int total = 0;
            for (int i = 0; i < names.Count; i++)
            {
                if (names[i] == null) continue;
                if (names[i].IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                total++;
                if (near.Count < MaxNamesListed) near.Add(names[i]);
            }
            if (near.Count == 0) return "";
            return "; closest by name: " + string.Join(", ", near.ToArray())
                + (total > near.Count ? ", ..." : "");
        }

        #endregion
    }
}
