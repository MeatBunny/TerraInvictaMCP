using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // faction.diplomacy.
    //
    // The five policy options whose HandledAtFactionLevel is true -- ProposeAlliance,
    // EndAlliance, InitiateRivalry, EndRivalry, EmployNuclearWeapons -- are the whole
    // set nation.set_policy refuses, and this is the only contracted verb that reaches
    // them. Not the only reach: action.invoke can construct ConfirmPolicyAction with any
    // registered option by data name, which enacts it with no cost, no eligibility check
    // and no confirm. That layer is fixture-grade and legality-bypassing by design, the
    // same as the spawn verbs; this one is the gated path.
    //
    // ConfirmPolicyAction.Execute, the wrapper the two other call sites use, is a bare
    // `policy.OnConfirm(nation, target)` with no Allowed() re-check, and it returns
    // immediately afterwards for a HandledAtFactionLevel option. So nothing downstream
    // enforces legality and the gates below are the only guardrail -- the same discipline
    // RequirePolicyOffered applies in Policy.cs, applied here against each option's own
    // eligible list.
    public static partial class Verbs
    {
        // TIRegionState.NuclearAttackOnRegion schedules the damage event at
        // currentTime + 1800 seconds; the launch itself is immediate and nothing in
        // the engine cancels the queued event.
        const double NuclearArrivalDelaySeconds = 1800.0;

        // The label TINationState.HandleFactionLevelRelationshipChanges passes to
        // PayCost, so the faction's resource log reads the same either way.
        const string RelationshipCostLabel = "Relationship Change";

        #region entry

        static JToken FactionDiplomacy(JObject args)
        {
            TINationState nation = ArgNation(args, "nation");
            string name = Str(args, "option");
            if (name != null) name = name.Trim();
            if (string.IsNullOrEmpty(name)) return DiplomacyList(nation);
            return DiplomacyEnact(args, nation, ArgDiplomacyOption(name));
        }

        #endregion

        #region list mode

        static JToken DiplomacyList(TINationState nation)
        {
            List<TIPolicyOption> options = FactionLevelOptions();
            TIFactionState faction = ExecutiveFaction(nation);
            TIResourcesCost cost = RelationshipChangeCost();
            JToken payable = CostPayable(cost, faction);

            var o = new JObject();
            o["nation"] = Describe(nation);
            o["executiveFaction"] = faction != null ? Describe(faction) : JValue.CreateNull();
            o["cost"] = cost != null ? CostToken(cost) : JValue.CreateNull();
            o["costPayable"] = payable;
            o["holdings"] = ResourceSnapshot(cost, faction);
            Put(o, "nuclearWeapons", delegate { return (JToken)nation.numNuclearWeapons; });
            o["optionCount"] = options.Count;
            string audit = DiplomacyAudit();
            o["optionsAudit"] = audit != null ? (JToken)new JValue(audit) : JValue.CreateNull();

            var rows = new JArray();
            for (int i = 0; i < options.Count; i++)
            {
                TIPolicyOption policy = options[i];
                JObject row = DescribeDiplomacyOption(policy);
                row["allowed"] = Safe<JToken>(
                    delegate { return (JToken)policy.Allowed(nation); }, JValue.CreateNull());

                IList<TIGameState> possible = Safe<IList<TIGameState>>(
                    delegate { return policy.GetPossibleTargets(nation); }, null);
                row["targetCount"] = possible != null ? possible.Count : -1;
                var targets = new JArray();
                for (int j = 0; possible != null && j < possible.Count
                        && j < MaxPolicyTargets; j++)
                {
                    if (possible[j] == null) continue;
                    targets.Add(DiplomacyTargetToken(possible[j]));
                }
                row["targets"] = targets;
                row["costPayable"] = IsRelationOption(policy) ? payable : JValue.CreateNull();
                string blocked = DiplomacyBlocker(policy, nation, faction, cost, possible);
                row["blockedBy"] = blocked != null
                    ? (JToken)new JValue(blocked) : JValue.CreateNull();
                rows.Add(row);
            }
            o["options"] = rows;
            return o;
        }

        // The first gate an enact call would hit, given a target out of this option's
        // own list. Null means only `confirm` stands between the caller and enactment.
        // Same order and same tests as DiplomacyEnact, so a row with no blocker and an
        // enact call that refuses would be a bug rather than a difference of opinion.
        static string DiplomacyBlocker(TIPolicyOption policy, TINationState nation,
            TIFactionState faction, TIResourcesCost cost, IList<TIGameState> possible)
        {
            bool relation = IsRelationOption(policy);
            if (relation && faction == null)
                return "the nation has no executive faction";
            if (possible == null || possible.Count == 0)
                return "the option offers no eligible target here";
            JToken allowed = Safe<JToken>(
                delegate { return (JToken)policy.Allowed(nation); }, null);
            if (allowed == null) return "the option's Allowed() threw";
            if (!(bool)allowed)
                return "the option has targets but Allowed() is false: "
                    + AllowedConditions(policy, nation, possible);
            if (relation && cost == null)
                return "TINationState.FactionLevelRelationShipChangeCost did not resolve";
            if (relation && !Safe<bool>(delegate { return cost.CanAfford(faction); }, false))
                return StateName(faction) + " cannot pay " + CostText(cost)
                    + ", which HandleFactionLevelRelationshipChanges charges before "
                    + "enacting any of the four relationship options; it holds "
                    + HoldingsText(cost, faction);
            return null;
        }

        // What an Allowed() that is false while the target list is not empty could be
        // reading. Only one vanilla option has such a clause, and naming the stockpile
        // is the difference between a usable refusal and a tautology.
        static string AllowedConditions(TIPolicyOption policy, TINationState nation,
            IList<TIGameState> possible)
        {
            var parts = new List<string>();
            parts.Add("possibleTargets=" + (possible != null ? possible.Count : -1));
            if (policy is EmployNuclearWeaponsOption)
                parts.Add("numNuclearWeapons=" + Safe<int>(
                    delegate { return nation.numNuclearWeapons; }, -1)
                    + " (Allowed requires more than 0; NuclearWeaponsTargets carries no "
                    + "stockpile term of its own, so a nation at war with an empty "
                    + "stockpile still lists targets)");
            return string.Join(" ", parts.ToArray());
        }

        #endregion

        #region enact mode

        static JToken DiplomacyEnact(JObject args, TINationState nation, TIPolicyOption policy)
        {
            bool relation = IsRelationOption(policy);
            // Everything at faction level that is not one of the four relation changes
            // needs an explicit confirm. Keyed on the negative so a sixth option added
            // by a game update lands on the cautious side rather than the silent one;
            // the audit string names the drift either way.
            bool requiresConfirm = !relation;

            TIFactionState faction = ExecutiveFaction(nation);

            // Gate 4: the option's own eligible list, which is the Can* check.
            IList<TIGameState> possible = PossibleTargets(policy, nation);
            TIGameState target = DiplomacyTarget(args, policy, nation, possible);

            // Gates 2, 5 and 6 -- executive faction, the option's own Allowed(), and the
            // cost the UI pays before enacting -- are exactly what the discovery mode
            // reports as blockedBy, so they are the same call. A row with no blocker and
            // an enact call that refuses would be a bug rather than a difference of
            // opinion.
            TIResourcesCost cost = relation ? RelationshipChangeCost() : null;
            string blocked = DiplomacyBlocker(policy, nation, faction, cost, possible);
            if (blocked != null) throw new VerbError(blocked);

            // Gate 7: explicit confirmation, for the option that cannot be undone.
            bool confirm = Bool(args, "confirm", false);
            if (requiresConfirm && !confirm)
                throw new VerbError(IrreversibleRefusal(policy, nation, target));

            var o = new JObject();
            o["nation"] = Describe(nation);
            o["executiveFaction"] = faction != null ? Describe(faction) : JValue.CreateNull();
            o["option"] = DescribeDiplomacyOption(policy);
            o["target"] = DiplomacyTargetToken(target);
            o["confirm"] = confirm;
            o["cost"] = cost != null ? CostToken(cost) : JValue.CreateNull();
            string audit = DiplomacyAudit();
            o["optionsAudit"] = audit != null ? (JToken)new JValue(audit) : JValue.CreateNull();

            // Snapshots taken before anything moves; every read-back below is against
            // one of these.
            TINationState targetNation = target as TINationState;
            bool wasAlly = RelationHolds(nation, targetNation, true);
            bool wasRival = RelationHolds(nation, targetNation, false);
            int nukesBefore = Safe<int>(delegate { return nation.numNuclearWeapons; }, -1);
            var targetRegion = target as TIRegionState;
            int detonationsBefore = targetRegion != null
                ? Safe<int>(delegate { return targetRegion.nuclearDetonations; }, -1) : -1;
            JObject holdingsBefore = ResourceSnapshot(cost, faction);
            string promptName = PromptNameOf(policy);
            string probe = PromptProbe();
            // A response prompt of this type from this same nation against this same
            // target can already be outstanding from an earlier proposal, and the queue
            // carries no call identity. Snapshotting it here is what makes
            // awaitingResponse attributable to this call rather than merely true.
            bool promptBefore = promptName != null && probe == null
                && HasPolicyPrompt(promptName, nation, target);
            object newsBefore = NewestNotification();

            bool paid = false;
            if (relation)
            {
                // Same call, same label, same order as the engine's own entry point:
                // the cost is charged before OnConfirm, not after, and PayCost makes no
                // affordability check of its own -- the gate above is it. The two
                // options that queue a response prompt are charged here too, because
                // HandleFactionLevelRelationshipChanges pays before it dispatches and
                // does not care whether the other side ever answers.
                try { cost.PayCost(faction, RelationshipCostLabel); }
                catch (Exception e)
                {
                    throw new VerbError("PayCost threw before " + PolicyName(policy)
                        + " was enacted: " + Note(e));
                }
                paid = true;
            }
            o["costPaid"] = paid;

            try { policy.OnConfirm(nation, target); }
            catch (Exception e)
            {
                throw new VerbError(PolicyName(policy) + ".OnConfirm threw"
                    + (paid ? " after the cost was charged" : "") + ": " + Note(e));
            }
            o["enactedVia"] = "TIPolicyOption.OnConfirm";

            var back = new JObject();
            JObject news = NewPolicyNotification(newsBefore);
            back["notification"] = news != null ? (JToken)news : JValue.CreateNull();
            back["enactedNow"] = news != null;
            back["promptName"] = promptName != null
                ? (JToken)new JValue(promptName) : JValue.CreateNull();
            back["promptProbe"] = probe != null
                ? (JToken)new JValue(probe) : JValue.CreateNull();
            bool unknownPrompt = promptName == null || probe != null;
            bool waiting = !unknownPrompt && HasPolicyPrompt(promptName, nation, target);
            back["awaitingResponse"] = promptName == null
                ? (JToken)new JValue(false)
                : (probe != null ? JValue.CreateNull() : (JToken)new JValue(waiting));
            back["promptAlreadyQueued"] = unknownPrompt
                ? JValue.CreateNull() : (JToken)new JValue(promptBefore);
            back["promptQueuedByThisCall"] = unknownPrompt
                ? JValue.CreateNull() : (JToken)new JValue(waiting && !promptBefore);

            back["relation"] = relation
                ? (JToken)RelationReadback(nation, targetNation, wasAlly, wasRival)
                : JValue.CreateNull();
            back["nuclear"] = relation
                ? JValue.CreateNull()
                : (JToken)NuclearReadback(nation, targetRegion, nukesBefore,
                    detonationsBefore);
            back["cost"] = paid
                ? (JToken)CostReadback(cost, faction, holdingsBefore)
                : JValue.CreateNull();
            back["stillAllowed"] = Safe<JToken>(
                delegate { return (JToken)policy.Allowed(nation); }, JValue.CreateNull());
            back["targetStillPossible"] = StillPossible(policy, nation, target);
            o["readback"] = back;
            return o;
        }

        #endregion

        #region read-back

        // Allies and rivals as they stand now against the snapshot. The engine's own
        // record (LogPolicyAdopted) says a policy passed, not that it took effect:
        // InitiateRivalryOption.OnPassage re-checks CanRival and silently does nothing
        // when it has gone false, while EnactPolicy logs either way.
        static JObject RelationReadback(TINationState nation, TINationState target,
            bool wasAlly, bool wasRival)
        {
            bool isAlly = RelationHolds(nation, target, true);
            bool isRival = RelationHolds(nation, target, false);
            var o = new JObject();
            o["targetWasAlly"] = wasAlly;
            o["targetIsAlly"] = isAlly;
            o["targetWasRival"] = wasRival;
            o["targetIsRival"] = isRival;
            o["changed"] = isAlly != wasAlly || isRival != wasRival;
            return o;
        }

        static bool RelationHolds(TINationState nation, TINationState target, bool ally)
        {
            if (nation == null || target == null) return false;
            return Safe<bool>(delegate
            {
                List<TINationState> list = ally ? nation.allies : nation.rivals;
                return list != null && list.Contains(target);
            }, false);
        }

        // The launch, as far as this frame can see it: the warhead is off the books and
        // the region's detonation count is up, but the damage rides a queued time event.
        static JObject NuclearReadback(TINationState nation, TIRegionState region,
            int nukesBefore, int detonationsBefore)
        {
            var o = new JObject();
            o["stockpileBefore"] = nukesBefore;
            Put(o, "stockpileAfter", delegate { return (JToken)nation.numNuclearWeapons; });
            o["regionDetonationsBefore"] = detonationsBefore;
            o["regionDetonationsAfter"] = region != null
                ? Safe<JToken>(delegate { return (JToken)region.nuclearDetonations; },
                    JValue.CreateNull())
                : JValue.CreateNull();
            o["damageDelaySeconds"] = NuclearArrivalDelaySeconds;
            TIDateTime arrival = Safe<TIDateTime>(delegate
            {
                TIDateTime now = TITimeState.Now();
                if (now == null) return null;
                now.AddSeconds(NuclearArrivalDelaySeconds);
                return now;
            }, null);
            o["damageArrivesAbout"] = arrival != null
                ? (JToken)new JValue(InvariantDate(arrival)) : JValue.CreateNull();
            o["note"] = "the strike is away: TIRegionState.NuclearAttackOnRegion fired the "
                + "NuclearLaunch event, recorded the exchange in "
                + "TIGlobalValuesState.currentNuclearExchanges, tallied the war and "
                + "queued the damage as a time event at the date above. Nothing recalls "
                + "that event. Advance the clock past it and read the region back with "
                + "query.state for the casualties, unrest and environmental effects";
            return o;
        }

        static JObject CostReadback(TIResourcesCost cost, TIFactionState faction,
            JObject before)
        {
            var o = new JObject();
            o["before"] = before;
            JObject after = ResourceSnapshot(cost, faction);
            o["after"] = after;
            return o;
        }

        #endregion

        #region cost

        static TIResourcesCost RelationshipChangeCost()
        {
            return Safe<TIResourcesCost>(
                delegate { return TINationState.FactionLevelRelationShipChangeCost; }, null);
        }

        static JToken CostPayable(TIResourcesCost cost, TIFactionState faction)
        {
            if (cost == null || faction == null) return JValue.CreateNull();
            return Safe<JToken>(
                delegate { return (JToken)cost.CanAfford(faction); }, JValue.CreateNull());
        }

        // The faction's current holding of each resource the cost touches. Empty for a
        // faction that could not be read, which is also what a null cost gives.
        static JObject ResourceSnapshot(TIResourcesCost cost, TIFactionState faction)
        {
            var o = new JObject();
            if (cost == null || faction == null) return o;
            List<ResourceValue> values = Safe<List<ResourceValue>>(
                delegate { return cost.resourceCosts; }, null);
            for (int i = 0; values != null && i < values.Count; i++)
            {
                ResourceValue value = values[i];
                Put(o, value.resource.ToString(), delegate
                {
                    return Num(faction.GetCurrentResourceAmount(value.resource));
                });
            }
            return o;
        }

        static string CostText(TIResourcesCost cost)
        {
            if (cost == null) return "an unreadable cost";
            List<ResourceValue> values = Safe<List<ResourceValue>>(
                delegate { return cost.resourceCosts; }, null);
            if (values == null || values.Count == 0) return "a cost with no resources";
            var parts = new List<string>();
            for (int i = 0; i < values.Count; i++)
                parts.Add(values[i].value.ToString("0.##", CultureInfo.InvariantCulture)
                    + " " + values[i].resource);
            return string.Join(" + ", parts.ToArray());
        }

        static string HoldingsText(TIResourcesCost cost, TIFactionState faction)
        {
            JObject snapshot = ResourceSnapshot(cost, faction);
            var parts = new List<string>();
            foreach (KeyValuePair<string, JToken> kv in snapshot)
                parts.Add(kv.Key + "="
                    + (kv.Value != null ? kv.Value.ToString() : "<unreadable>"));
            if (parts.Count == 0) return "nothing this call could read";
            return string.Join(" ", parts.ToArray());
        }

        #endregion

        #region options

        // Every option the registry holds whose HandledAtFactionLevel is true. Registry
        // instances only, as everywhere else: TIPolicyOption's constructor registers the
        // instance with TemplateManager, so a fresh one would be a second template entry
        // matching nothing the engine holds. (The engine's own UI does construct fresh
        // ones here; that is its business, not a pattern to copy.)
        static List<TIPolicyOption> FactionLevelOptions()
        {
            var list = new List<TIPolicyOption>();
            List<TIPolicyOption> all = AllPolicyOptions();
            for (int i = 0; i < all.Count; i++)
            {
                TIPolicyOption policy = all[i];
                if (Safe<bool>(delegate { return policy.HandledAtFactionLevel(); }, false))
                    list.Add(policy);
            }
            return list;
        }

        // The four options HandleFactionLevelRelationshipChanges dispatches on, which is
        // exactly the set it charges FactionLevelRelationShipChangeCost for: its switch
        // key is the option's own relationChange, and the base property answers None.
        // Charging is keyed on the same value rather than on a list of type names, so
        // the cost rule cannot drift away from the dispatcher it mirrors.
        static bool IsRelationOption(TIPolicyOption policy)
        {
            return Safe<bool>(
                delegate { return policy.relationChange != RelationChange.None; }, false);
        }

        static JObject DescribeDiplomacyOption(TIPolicyOption policy)
        {
            JObject o = DescribePolicy(policy);
            Put(o, "relationChange",
                delegate { return (JToken)policy.relationChange.ToString(); });
            string prompt = PromptNameOf(policy);
            o["async"] = prompt != null;
            o["promptName"] = prompt != null
                ? (JToken)new JValue(prompt) : JValue.CreateNull();
            o["charged"] = IsRelationOption(policy);
            o["requiresConfirm"] = !IsRelationOption(policy);
            return o;
        }

        // Data name, PolicyType name or display name, resolved against the five only. A
        // name that is a real option but not one of them is refused towards the verb
        // that does enact it, which is the mistake worth naming precisely.
        static TIPolicyOption ArgDiplomacyOption(string name)
        {
            List<TIPolicyOption> options = FactionLevelOptions();
            if (options.Count == 0)
                throw new VerbError("PolicyManager holds no faction-level policy options");
            for (int i = 0; i < options.Count; i++)
            {
                if (string.Equals(PolicyName(options[i]), name,
                        StringComparison.OrdinalIgnoreCase))
                    return options[i];
            }
            for (int i = 0; i < options.Count; i++)
            {
                TIPolicyOption option = options[i];
                if (Same(Safe<string>(delegate { return option.GetDisplayName(); }, null), name))
                    return option;
            }

            List<TIPolicyOption> all = AllPolicyOptions();
            for (int i = 0; i < all.Count; i++)
            {
                TIPolicyOption option = all[i];
                bool hit = string.Equals(PolicyName(option), name,
                        StringComparison.OrdinalIgnoreCase)
                    || Same(Safe<string>(delegate { return option.GetDisplayName(); }, null),
                        name);
                if (!hit) continue;
                throw new VerbError(PolicyName(option) + " is not handled at faction "
                    + "level, so it belongs to nation.set_policy rather than this verb; "
                    + "this verb enacts only " + DiplomacyOptionList(options));
            }
            throw new VerbError("no policy option named '" + name + "'; this verb enacts "
                + DiplomacyOptionList(options));
        }

        static string DiplomacyOptionList(List<TIPolicyOption> options)
        {
            var names = new List<string>();
            for (int i = 0; i < options.Count; i++)
            {
                TIPolicyOption option = options[i];
                string display = Safe<string>(
                    delegate { return option.GetDisplayName(); }, null);
                names.Add(PolicyName(option)
                    + (string.IsNullOrEmpty(display) ? "" : " (" + display + ")"));
            }
            names.Sort(StringComparer.Ordinal);
            return string.Join(", ", names.ToArray());
        }

        #endregion

        #region audit

        // The verb rests on three facts about the registry: the faction-level set has
        // exactly five members, the four that carry a RelationChange are the ones the
        // engine charges for, and the one that does not is the nuclear launch. Each is
        // read out of the live registry rather than assumed, and any drift is named
        // here, logged once, and carried in every response -- the same treatment
        // Policy.cs gives a prompt-queue field that stops resolving. Nothing is refused
        // on drift: a sixth option would still be enactable, and would require confirm.
        static bool auditRun;
        static string auditNote;

        static string DiplomacyAudit()
        {
            if (auditRun) return auditNote;
            // An empty registry is PolicyManager.Initialize not having run yet, not
            // drift, and caching that verdict would report it for the rest of the
            // session. Both verb modes need a campaign, so this should not be
            // reachable; it is not worth being wrong about permanently if it is.
            if (FactionLevelOptions().Count == 0)
                return "PolicyManager holds no faction-level options yet; the registry "
                    + "is filled by PolicyManager.Initialize";
            auditRun = true;
            auditNote = ComputeDiplomacyAudit();
            if (auditNote != null && Main.Log != null)
                Main.Log.Log("WARNING: faction.diplomacy option audit: " + auditNote);
            return auditNote;
        }

        static string ComputeDiplomacyAudit()
        {
            List<TIPolicyOption> options = FactionLevelOptions();
            var problems = new List<string>();
            if (options.Count != 5)
                problems.Add("HandledAtFactionLevel is true for " + options.Count
                    + " options, not the 5 this verb and nation.set_policy are written "
                    + "against (" + DiplomacyOptionList(options) + ")");

            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var uncharged = new List<string>();
            for (int i = 0; i < options.Count; i++)
            {
                TIPolicyOption policy = options[i];
                string key = PolicyName(policy);
                if (!IsRelationOption(policy)) { uncharged.Add(key); continue; }
                string change = Safe<string>(
                    delegate { return policy.relationChange.ToString(); }, "<threw>");
                if (seen.ContainsKey(change))
                    problems.Add(change + " is claimed by both " + seen[change] + " and "
                        + key + ", so the cost dispatcher's switch is no longer 1:1");
                else seen[change] = key;
            }
            string[] expected = { "NormalToAlly", "AllyToNormal", "RivalToNormal",
                                  "NormalToRival" };
            for (int i = 0; i < expected.Length; i++)
            {
                if (!seen.ContainsKey(expected[i]))
                    problems.Add("no faction-level option carries RelationChange."
                        + expected[i] + ", which "
                        + "HandleFactionLevelRelationshipChanges still switches on");
            }
            if (uncharged.Count != 1
                || !string.Equals(uncharged[0], typeof(EmployNuclearWeaponsOption).Name,
                    StringComparison.Ordinal))
            {
                problems.Add("the uncharged faction-level options are ["
                    + string.Join(", ", uncharged.ToArray()) + "], not the single "
                    + typeof(EmployNuclearWeaponsOption).Name + " this verb documents; "
                    + "every one of them requires confirm:true");
            }
            if (problems.Count == 0) return null;
            return string.Join("; ", problems.ToArray());
        }

        #endregion

        #region targets and refusals

        static JToken DiplomacyTargetToken(TIGameState target)
        {
            if (target == null) return JValue.CreateNull();
            var o = Describe(target) as JObject;
            if (o == null) return JValue.CreateNull();
            var region = target as TIRegionState;
            if (region != null)
                Put(o, "nation", delegate
                {
                    TINationState owner = region.nation;
                    return owner != null ? Describe(owner) : JValue.CreateNull();
                });
            return o;
        }

        // A state id, or a name matched inside the option's own eligible list. A name
        // that is a real nation but not an eligible one is resolved anyway, so the
        // refusal can carry the engine's own clause-by-clause verdict on it.
        static TIGameState DiplomacyTarget(JObject args, TIPolicyOption policy,
            TINationState nation, IList<TIGameState> possible)
        {
            JToken t = args != null ? args["target"] : null;
            if (t == null || t.Type == JTokenType.Null)
                throw new VerbError(PolicyName(policy) + " requires a target; "
                    + PolicyTargetList(possible));
            string text = (t.Type == JTokenType.String ? (string)t : t.ToString()).Trim();
            if (text.Length == 0) throw new VerbError("arg 'target' is empty");

            TIGameState target;
            int id;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                target = ById<TIGameState>(id);
            else
            {
                target = MatchState(possible, text);
                if (target == null && IsRelationOption(policy))
                    target = Safe<TIGameState>(
                        delegate { return ArgNation(args, "target"); }, null);
                if (target == null)
                    throw new VerbError("no target named '" + text + "' for "
                        + PolicyName(policy) + " in " + StateName(nation) + "; "
                        + PolicyTargetList(possible));
            }

            if (possible == null || !possible.Contains(target))
                throw new VerbError(StateName(target) + " (" + (int)target.ID
                    + ") is not eligible for " + PolicyName(policy) + " in "
                    + StateName(nation) + "; " + PolicyTargetList(possible)
                    + EligibilityNote(policy, nation, target));
            return target;
        }

        // The engine's own feedback string for the Can* check behind this option's
        // list, clause by clause, with the TMP markup stripped. Only the four relation
        // options have one; the nuclear list is a region filter with no such text.
        static string EligibilityNote(TIPolicyOption policy, TINationState nation,
            TIGameState target)
        {
            var other = target as TINationState;
            if (other == null) return "";
            string change = Safe<string>(
                delegate { return policy.relationChange.ToString(); }, null);
            string feedback = Safe<string>(delegate
            {
                if (string.Equals(change, "NormalToAlly", StringComparison.Ordinal))
                    return nation.CanAllyFeedback(other);
                if (string.Equals(change, "AllyToNormal", StringComparison.Ordinal))
                    return nation.CanEndAllianceFeedback(other);
                if (string.Equals(change, "RivalToNormal", StringComparison.Ordinal))
                    return nation.CanEndRivalryFeedback(other);
                if (string.Equals(change, "NormalToRival", StringComparison.Ordinal))
                    return nation.CanRivalFeedback(other);
                return null;
            }, null);
            if (string.IsNullOrEmpty(feedback)) return "";
            string plain = Safe<string>(
                delegate { return tagPattern.Replace(feedback, ""); }, feedback);
            plain = plain.Replace("\r", " ").Replace("\n", " ").Trim();
            while (plain.Contains("  ")) plain = plain.Replace("  ", " ");
            if (plain.Length == 0) return "";
            return "; the engine's own check reads: " + plain;
        }

        static string IrreversibleRefusal(TIPolicyOption policy, TINationState nation,
            TIGameState target)
        {
            bool nuclear = policy is EmployNuclearWeaponsOption;
            string head = PolicyName(policy) + " against " + StateName(target) + " ("
                + (int)target.ID + ") from " + StateName(nation) + " needs "
                + "confirm:true and did not get it. ";
            if (!nuclear)
                return head + "Every faction-level option that is not one of the four "
                    + "relationship changes is treated as irreversible by this verb, "
                    + "because nothing downstream re-checks it: ConfirmPolicyAction "
                    + "calls OnConfirm with no Allowed() test and returns immediately "
                    + "afterwards";
            return head + "This is the launch itself, not an order that can be recalled. "
                + "OnConfirm runs NuclearAttackOnRegion straight away: a warhead comes "
                + "off the stockpile, the region's detonation count goes up, the war "
                + "tallies the strike, and the damage is queued as a time event "
                + NuclearArrivalDelaySeconds.ToString("0", CultureInfo.InvariantCulture)
                + " seconds of game time ahead. Nothing in the engine cancels that "
                + "event. The strike also stands permanently in the campaign's "
                + "environmental record. Pass confirm:true to launch";
        }

        #endregion

        #region faction.relations

        // How one faction feels about another, read and written.
        //
        // Faction hate is the last piece of campaign state with no headless path.
        // No console command sets one; war, control_points and the spawn verbs
        // write engine state directly and never touch it; and the only in-game
        // route is playing until the two factions have reasons. So an AI reaction
        // that keys off hate -- a war declaration, a refused trade, the alien
        // response -- could not be set up and therefore could not be tested.
        //
        // The setter is TIFactionState.SetFactionHate(other, value, cantConflagrate,
        // cause), which is a wrapper: it takes the difference from the current
        // value and hands it to GainFactionHate as a delta, with randomize false
        // (IL_0000-IL_002b), so nothing here is a partial or noisy write. What
        // GainFactionHate then does to that delta is why every number in the reply
        // is read back rather than echoed:
        //   - it returns at once, writing nothing, when the pair are permanent
        //     allies (IL_0009-IL_0012). Refused here rather than reported as a set.
        //   - an alien faction's positive delta is scaled by 0.6 unless the aliens
        //     have gone loud (IL_0059-IL_006f).
        //   - the result is clamped to MinimumFactionHate..MaximumFactionHate for
        //     the pair (IL_0071-IL_00b1). The maximum is +Infinity for a human
        //     subject and a real ceiling for the alien faction.
        //   - an alien subject also pushes the target's assessed alien hate
        //     (IL_00f5-IL_0100).
        //   - and unless cantConflagrate is set, the change spreads to the alien
        //     proxy and the alien appeaser and back again (IL_0105 jumps the whole
        //     cascade when it is true).
        static JToken FactionRelations(JObject args)
        {
            TIFactionState subject = RelationsSubject(args);
            JToken other = args != null ? args["other"] : null;
            if (other == null || other.Type == JTokenType.Null)
            {
                if (args != null && args["hate"] != null
                    && args["hate"].Type != JTokenType.Null)
                    throw new VerbError("arg 'hate' needs an 'other': hate is held "
                        + "per pair and there is no faction-wide value to set. "
                        + "Nothing was changed");
                return RelationsList(subject);
            }

            TIFactionState target = Arg<TIFactionState>(args, "other");
            // == on a game state is the engine's own operator, an id comparison
            // rather than reference equality.
            if (subject == target)
                throw new VerbError("'faction' and 'other' are the same faction ("
                    + (int)subject.ID + "); hate is held between two. Nothing was "
                    + "changed");

            JToken wanted = args["hate"];
            if (wanted == null || wanted.Type == JTokenType.Null)
                return RelationsPair(subject, target);

            // Token type first, the way Int() and Float() test theirs: the explicit
            // cast on a JToken is a conversion, so true would become 1.0 and
            // Convert.ToSingle would read "1,5" as some number of its own. Hate is
            // written straight into the faction pair's table.
            if (wanted.Type != JTokenType.Integer && wanted.Type != JTokenType.Float)
                throw new VerbError("arg 'hate' must be a number, got "
                    + wanted.Type + "; nothing was changed");
            float value;
            try { value = (float)wanted; }
            catch (Exception)
            { throw new VerbError("arg 'hate' is out of range for a number; "
                + "nothing was changed"); }
            // A non-finite value would land in the faction's hate dictionary and
            // poison every later comparison against it, including the two
            // thresholds the mood and the war checks read.
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new VerbError("arg 'hate' must be a finite number; nothing "
                    + "was changed");
            return RelationsSet(args, subject, target, value);
        }

        // The active player unless a faction is named. Reading the whole table for
        // whoever is playing is the common call, and requiring the id for it would
        // mean a query.factions round trip before every one.
        static TIFactionState RelationsSubject(JObject args)
        {
            JToken id = args != null ? args["faction"] : null;
            if (id != null && id.Type != JTokenType.Null)
                return Arg<TIFactionState>(args, "faction");
            TIFactionState player = ActivePlayer();
            if (player == null)
                throw new VerbError("no active player faction to read relations "
                    + "for; pass 'faction' as a faction state id");
            return player;
        }

        static JToken RelationsList(TIFactionState subject)
        {
            var o = new JObject();
            o["faction"] = Describe(subject);
            o["thresholds"] = HateThresholds();
            var rows = new JArray();
            TIFactionState[] factions = GameStateManager.AllFactions();
            for (int i = 0; factions != null && i < factions.Length; i++)
            {
                TIFactionState f = factions[i];
                if (f == null || f == subject) continue;
                rows.Add(RelationRow(subject, f));
            }
            o["relations"] = rows;
            return o;
        }

        static JToken RelationsPair(TIFactionState subject, TIFactionState target)
        {
            var o = new JObject();
            o["faction"] = Describe(subject);
            o["thresholds"] = HateThresholds();
            o["relation"] = RelationRow(subject, target);
            return o;
        }

        // Both directions, because hate is not symmetric: each faction keeps its
        // own dictionary and a one-sided reading reads as a mutual state that is
        // not there.
        static JToken RelationRow(TIFactionState subject, TIFactionState target)
        {
            var o = new JObject();
            o["faction"] = Describe(target);
            Put(o, "hate", delegate
            { return Num(subject.GetFactionHate(target)); });
            Put(o, "hateFromThem", delegate
            { return Num(target.GetFactionHate(subject)); });
            // The engine's own word for the pair's state, from the same two
            // thresholds: "Tolerance", "Conflicted" or "War".
            Put(o, "mood", delegate
            { return (JToken)subject.GetDiplomacyMood(target); });
            Put(o, "moodFromThem", delegate
            { return (JToken)target.GetDiplomacyMood(subject); });
            Put(o, "min", delegate
            { return Num(subject.MinimumFactionHate(target)); });
            Put(o, "max", delegate
            { return Num(subject.MaximumFactionHate(target)); });
            Put(o, "permanentAlly", delegate
            { return (JToken)subject.permanentAlly(target); });
            return o;
        }

        static JToken HateThresholds()
        {
            var o = new JObject();
            Put(o, "conflict", delegate
            { return Num(TemplateManager.global.factionHateConflictThreshold); });
            Put(o, "war", delegate
            { return Num(TemplateManager.global.factionHateWarThreshold); });
            return o;
        }

        static JToken RelationsSet(JObject args, TIFactionState subject,
            TIFactionState target, float value)
        {
            // Refused rather than attempted: GainFactionHate returns before its
            // first write for a permanent ally, so the call would report a set that
            // never happened.
            if (Safe<bool>(delegate { return subject.permanentAlly(target); }, false))
                throw new VerbError("the two factions are permanent allies "
                    + "(TIFactionState.permanentAlly), and GainFactionHate returns "
                    + "before writing anything for that pair. Nothing was changed");

            bool cantConflagrate = Flag(args, "cant_conflagrate");
            string cause = Str(args, "cause");
            if (string.IsNullOrEmpty(cause)) cause = HateSetCause;

            // The delta the wrapper will compute, because three of the engine's
            // behaviours below key off its sign rather than off the new value.
            float was = Safe<float>(
                delegate { return subject.GetFactionHate(target); }, 0f);
            float delta = value - was;
            bool alien = Safe<bool>(
                delegate { return subject.IsAlienFaction; }, false);

            var o = new JObject();
            o["faction"] = Describe(subject);
            o["other"] = Describe(target);
            o["requested"] = Num(value);
            o["delta"] = Num(delta);
            o["cause"] = cause;
            o["cantConflagrate"] = cantConflagrate;
            o["before"] = RelationRow(subject, target);

            try { subject.SetFactionHate(target, value, cantConflagrate, cause); }
            catch (Exception e)
            {
                throw new VerbError("SetFactionHate threw: " + Note(e)
                    + ". The value may be partly written; call this verb with no "
                    + "'hate' to read the pair back");
            }

            o["after"] = RelationRow(subject, target);
            float applied = Safe<float>(
                delegate { return subject.GetFactionHate(target); }, value);
            o["hate"] = Num(applied);
            // Compared with a tolerance rather than exactly. SetFactionHate goes
            // through GainFactionHate, which stores dict[other] + (value - old)
            // rather than assigning the value, so an ordinary write comes back a
            // few ten-millionths off: 0.1 asked of a table holding 47.3 reads
            // 0.09999847. An exact test would report an ordinary write as unapplied.
            // A thousandth is far below anything the engine's own clamps move.
            const float appliedTolerance = 1e-3f;
            bool landed = Math.Abs(applied - value) <= appliedTolerance;
            o["applied"] = landed;

            // An alien subject also pushes the target's assessed alien hate, which is
            // the number the target's own screens show.
            if (alien)
                Put(o, "assessedAlienHate", delegate
                { return Num(target.GetEstimatedAlienHate()); });
            return o;
        }

        // The label the resource and diplomacy logs carry for a value this verb
        // forced. SetFactionHate's own default is "Hard Set", which says nothing
        // about who did it.
        const string HateSetCause = "Bridge Set";

        #endregion
    }
}
