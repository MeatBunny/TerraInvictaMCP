using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // nation.policies, nation.set_policy.
    //
    // National policy has no console command and no headless path in the UI, so the
    // engine's own non-UI enactment is the model: NationAI_AdoptPolicy.Execute, which
    // is exactly `if (policy.Allowed(nation)) policy.OnConfirm(nation, target)`. These
    // verbs preserve that legality rather than forcing state -- forcing is what
    // control_points and the spawn fixtures are for.
    public static partial class Verbs
    {
        // Targets per option in the enumeration. WarOption offers every nation on the
        // map, which is the only list that comes near this.
        const int MaxPolicyTargets = 200;

        // Candidates named in a resolution failure before the list is cut.
        const int MaxPolicyNamesListed = 20;

        #region nation.policies

        static JToken NationPolicies(JObject args)
        {
            TINationState nation = ArgNation(args, "nation");
            bool includeCancel = Bool(args, "include_cancel", false);
            // Before the enumeration, which dereferences the executive control point
            // through a bare indexer. A nation with none is exactly what a caller
            // enumerating Grant Independence targets reaches for.
            RequireExecutiveControlPoint(nation);

            var o = new JObject();
            o["nation"] = Describe(nation);
            o["executive"] = Executive(nation);
            o["includeCancel"] = includeCancel;

            // The engine's own enumeration, one row per (policy, target) pair, grouped
            // here into one row per policy. A policy that takes no target contributes a
            // single row with a null target, which becomes an empty target list.
            List<PolicyOptionWithTarget> pairs;
            try { pairs = nation.AvailableSetPolicyOptionsWithTargets(includeCancel); }
            catch (Exception e)
            {
                throw new VerbError("AvailableSetPolicyOptionsWithTargets threw: " + Note(e));
            }

            var order = new List<TIPolicyOption>();
            var targets = new Dictionary<string, JArray>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; pairs != null && i < pairs.Count; i++)
            {
                PolicyOptionWithTarget pair = pairs[i];
                if (pair == null) continue;
                TIPolicyOption policy = Safe<TIPolicyOption>(
                    delegate { return pair.policy; }, null);
                if (policy == null) continue;
                string key = PolicyName(policy);
                if (!targets.ContainsKey(key))
                {
                    order.Add(policy);
                    targets[key] = new JArray();
                    counts[key] = 0;
                }
                if (pair.target == null) continue;
                counts[key] = counts[key] + 1;
                if (targets[key].Count < MaxPolicyTargets)
                    targets[key].Add(Describe(pair.target));
            }

            var available = new JArray();
            var offered = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int i = 0; i < order.Count; i++)
            {
                TIPolicyOption policy = order[i];
                string key = PolicyName(policy);
                offered[key] = true;
                JObject row = DescribePolicy(policy);
                row["targetCount"] = counts[key];
                row["targets"] = targets[key];
                available.Add(row);
            }
            o["available"] = available;

            // Everything the registry holds that the nation is not offering, each with
            // the conditions that decide it. Without this an agent that does not see
            // the option it wants has no way to learn which gate refused it.
            var unavailable = new JArray();
            List<TIPolicyOption> all = AllPolicyOptions();
            for (int i = 0; i < all.Count; i++)
            {
                TIPolicyOption policy = all[i];
                if (offered.ContainsKey(PolicyName(policy))) continue;
                JObject row = DescribePolicy(policy);
                row["allowed"] = Safe<JToken>(
                    delegate { return (JToken)policy.Allowed(nation); }, JValue.CreateNull());
                row["possibleTargets"] = PossibleCount(policy, nation);
                unavailable.Add(row);
            }
            o["unavailable"] = unavailable;
            return o;
        }

        #endregion

        #region nation.set_policy

        static JToken NationSetPolicy(JObject args)
        {
            TINationState nation = ArgNation(args, "nation");
            TIPolicyOption policy = ArgPolicy(args, "policy");

            // Gate 1: the control point the policy list hangs off.
            TIControlPoint executive = RequireExecutiveControlPoint(nation);
            // Gate 2: its own flag, checked before anything else in the engine's list.
            if (Safe<bool>(delegate { return executive.benefitsDisabled; }, false))
                throw new VerbError("the executive control point of " + StateName(nation)
                    + " has benefitsDisabled set (" + ExecutiveText(nation)
                    + "), which empties the nation's policy list entirely");

            // Gate 3: membership in the engine's own list, which is Allowed() plus
            // HandledAtFactionLevel plus the cancel filter.
            RequirePolicyOffered(nation, policy);

            IList<TIGameState> possible = PossibleTargets(policy, nation);
            bool requiresTargets = Safe<bool>(
                delegate { return policy.RequiresTargets(); }, true);
            bool defaulted = false;
            TIGameState target = ArgPolicyTarget(args, "target", policy, nation, possible);
            if (target == null)
            {
                if (requiresTargets)
                    throw new VerbError(PolicyName(policy) + " requires a target; "
                        + PolicyTargetList(possible));
                // A target-less option still offers one when the target is the nation
                // itself (DisarmNuclearWeaponsOption), and the adopt action cannot carry
                // a null. Taking the single offer keeps that option enactable.
                if (possible != null && possible.Count == 1)
                {
                    target = possible[0];
                    defaulted = true;
                }
            }

            var o = new JObject();
            o["nation"] = Describe(nation);
            o["executiveFaction"] = FactionToken(nation);
            o["policy"] = DescribePolicy(policy);
            o["target"] = target != null ? Describe(target) : JValue.CreateNull();
            o["targetDefaulted"] = defaulted;
            // The nation-side path charges nothing: the only cost in national policy is
            // the SetNationalPolicy councilor mission's, and this verb does not run that
            // mission. CancelOption would refund exactly that value, which is why the
            // gate above refuses it.
            o["cost"] = JValue.CreateNull();

            // The notification queue is never empty in a live campaign, so the policy
            // record is found by what this call pushed on top of this snapshot.
            string promptName = PromptNameOf(policy);
            object newsBefore = NewestNotification();

            o["enactedVia"] = Enact(nation, policy, target);

            var back = new JObject();
            JObject news = NewPolicyNotification(newsBefore);
            back["notification"] = news != null ? (JToken)news : JValue.CreateNull();
            // EnactPolicy writes LogPolicyAdopted, so a new one is evidence the policy
            // passed inside this call.
            back["enactedNow"] = news != null;
            back["promptName"] = promptName != null
                ? (JToken)new JValue(promptName) : JValue.CreateNull();
            // Null, never false, when the queue could not be read: false here is the
            // same shape as "the engine recorded nothing", which a client is told to
            // distrust, so an unreadable queue must not counterfeit it.
            string probe = PromptProbe();
            back["promptProbe"] = probe != null
                ? (JToken)new JValue(probe) : JValue.CreateNull();
            back["awaitingResponse"] = promptName == null
                ? (JToken)new JValue(false)
                : (probe != null
                    ? JValue.CreateNull()
                    : (JToken)new JValue(HasPolicyPrompt(promptName, nation)));
            back["stillAllowed"] = Safe<JToken>(
                delegate { return (JToken)policy.Allowed(nation); }, JValue.CreateNull());
            back["targetStillPossible"] = target == null
                ? JValue.CreateNull()
                : StillPossible(policy, nation, target);
            o["readback"] = back;
            return o;
        }

        // The AI's own headless enactment. NationAI_AdoptPolicy.Execute is
        // `if (policy.Allowed(nation)) policy.OnConfirm(nation, target)`, and
        // GameControl.StartSimulationAction is a bare Execute() call, so this runs
        // inside the frame and the read-backs below see its result.
        static string Enact(TINationState nation, TIPolicyOption policy, TIGameState target)
        {
            if (target != null)
            {
                GameControl.StartSimulationAction(
                    new PavonisInteractive.TerraInvicta.Actions.NationAI_AdoptPolicy(
                        nation, target, policy));
                return "NationAI_AdoptPolicy.Execute";
            }
            // That action's constructor reads target.ID, so a policy with no target at
            // all cannot ride it; its Execute body runs directly instead. The Allowed()
            // check the action would make has already run in RequirePolicyOffered.
            policy.OnConfirm(nation, null);
            return "TIPolicyOption.OnConfirm";
        }

        #endregion

        #region gates and refusals

        // The engine's own set-policy list is the gate the UI applies before it offers
        // anything, so membership in it is the whole legality test. The list is filtered
        // out of PolicyManager.policies, which is where ArgPolicy resolves from, so the
        // instances compare equal.
        //
        // includeCancel stays false, the engine's own default: CancelOption drops the
        // policy selection a landed SetNationalPolicy mission is waiting on, and its
        // OnPassage refunds that mission's cost to the executive faction. This verb never
        // runs the mission, so cancelling here would be a refund for nothing.
        static void RequirePolicyOffered(TINationState nation, TIPolicyOption policy)
        {
            if (policy is CancelOption)
                throw new VerbError("CancelOption drops a policy selection a completed "
                    + "SetNationalPolicy mission is waiting on, and refunds that mission's "
                    + "cost to the executive faction; this verb never runs the mission, so "
                    + "it will not enact it. Clear a pending PromptSelectPolicy with the "
                    + "prompts verbs instead");

            List<TIPolicyOption> offered;
            try { offered = nation.availableSetPolicyOptions(false); }
            catch (Exception e)
            {
                throw new VerbError("availableSetPolicyOptions threw: " + Note(e));
            }
            if (offered != null && offered.Contains(policy)) return;

            bool handled = Safe<bool>(
                delegate { return policy.HandledAtFactionLevel(); }, false);
            if (handled)
                throw new VerbError(PolicyName(policy) + " is handled at faction level, "
                    + "not through a nation's set-policy list, so this verb will not "
                    + "enact it; " + PolicyConditions(nation, policy));

            JToken allowed = Safe<JToken>(
                delegate { return (JToken)policy.Allowed(nation); }, null);
            if (allowed == null)
                throw new VerbError(PolicyName(policy) + ".Allowed(" + StateName(nation)
                    + ") threw; " + PolicyConditions(nation, policy));
            if (!(bool)allowed)
                throw new VerbError(PolicyName(policy) + ".Allowed() is false for "
                    + StateName(nation) + " (" + (int)nation.ID + "); "
                    + PolicyConditions(nation, policy));

            throw new VerbError(PolicyName(policy) + " is not in the "
                + (offered != null ? offered.Count : 0) + " options "
                + StateName(nation) + " offers; " + PolicyConditions(nation, policy));
        }

        // Every clause the gates read, reported as observed. Allowed() answers one bool
        // and the interesting ones (ExecutivePowerConsolidated, the possible-target
        // count) are what a caller has to change to make it true.
        static string PolicyConditions(TINationState nation, TIPolicyOption policy)
        {
            var parts = new List<string>();
            parts.Add(ExecutiveText(nation));
            parts.Add("benefitsDisabled=" + Safe<bool>(
                delegate { return nation.executiveControlPoint.benefitsDisabled; }, false));
            parts.Add("executivePowerConsolidated=" + Safe<bool>(
                delegate { return nation.ExecutivePowerConsolidated; }, false));
            parts.Add("daysUntilExecutivePowerConsolidated=" + Safe<float>(
                delegate { return nation.daysUntilExecutivePowerConsolidated; }, -1f)
                .ToString(CultureInfo.InvariantCulture));
            parts.Add("allowed=" + Safe<string>(
                delegate { return policy.Allowed(nation).ToString(); }, "<threw>"));
            parts.Add("handledAtFactionLevel=" + Safe<bool>(
                delegate { return policy.HandledAtFactionLevel(); }, false));
            parts.Add("requiresTargets=" + Safe<bool>(
                delegate { return policy.RequiresTargets(); }, true));
            parts.Add("possibleTargets=" + PossibleCount(policy, nation));
            return string.Join(" ", parts.ToArray());
        }

        // executiveControlPoint is controlPoints[maxControlPointIndex], a bare indexer,
        // and availableSetPolicyOptions dereferences it with no guard of its own. A
        // nation holding no control points -- every non-extant one, which is what a
        // Grant Independence caller enumerates -- throws out of both verbs without this.
        static TIControlPoint RequireExecutiveControlPoint(TINationState nation)
        {
            TIControlPoint executive = Safe<TIControlPoint>(
                delegate { return nation.executiveControlPoint; }, null);
            if (executive != null) return executive;
            int count = Safe<int>(delegate
            {
                List<TIControlPoint> points = nation.controlPoints;
                return points != null ? points.Count : 0;
            }, 0);
            throw new VerbError(StateName(nation) + " (" + (int)nation.ID + ") has no "
                + "executive control point (" + count + " control points), so it holds "
                + "no policy options at all; a nation with no regions has none");
        }

        static string ExecutiveText(TINationState nation)
        {
            TIFactionState faction = ExecutiveFaction(nation);
            return "executive=" + (faction != null
                ? StateName(faction) + " (" + (int)faction.ID + ")" : "unheld");
        }

        static TIFactionState ExecutiveFaction(TINationState nation)
        {
            return Safe<TIFactionState>(delegate { return nation.executiveFaction; }, null);
        }

        static JToken FactionToken(TINationState nation)
        {
            TIFactionState faction = ExecutiveFaction(nation);
            return faction != null ? Describe(faction) : JValue.CreateNull();
        }

        static JObject Executive(TINationState nation)
        {
            var o = new JObject();
            o["faction"] = FactionToken(nation);
            Put(o, "benefitsDisabled",
                delegate { return (JToken)nation.executiveControlPoint.benefitsDisabled; });
            Put(o, "powerConsolidated",
                delegate { return (JToken)nation.ExecutivePowerConsolidated; });
            Put(o, "daysUntilConsolidated",
                delegate { return Num(nation.daysUntilExecutivePowerConsolidated); });
            return o;
        }

        #endregion

        #region read-back

        // TIPolicyOptionWithConfirm is the whole set of options that can queue a
        // response instead of passing at once; everything else passes inside the call.
        static string PromptNameOf(TIPolicyOption policy)
        {
            var withConfirm = policy as TIPolicyOptionWithConfirm;
            if (withConfirm == null) return null;
            return Safe<string>(delegate { return withConfirm.PromptName; }, null);
        }

        // The two queue lists are private, and the public ones are scoped to the active
        // player: a policy prompt raised against a nation nobody is playing lands in
        // neither. Cached FieldInfos, resolved once.
        static readonly FieldInfo promptFactionListField = PromptListField("factionList");
        static readonly FieldInfo promptNationListField = PromptListField("nationList");

        // A rename in the engine has to be loud: a silently missing field would report
        // "no prompt" forever, which reads as the engine having recorded nothing at all.
        static FieldInfo PromptListField(string name)
        {
            FieldInfo field = null;
            try
            {
                field = typeof(TIPromptQueueState).GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception) { }
            if (field == null && Main.Log != null)
                Main.Log.Log("WARNING: TIPromptQueueState." + name + " not found; "
                    + "nation.set_policy reports awaitingResponse as null.");
            return field;
        }

        // Names the queue lists that would not resolve, null when both did. Carried in
        // the response so a client sees the reason rather than a false negative.
        static string PromptProbe()
        {
            var missing = new List<string>();
            if (promptFactionListField == null) missing.Add("factionList");
            if (promptNationListField == null) missing.Add("nationList");
            if (missing.Count == 0) return null;
            return "TIPromptQueueState." + string.Join(" and ", missing.ToArray())
                + " did not resolve; awaitingResponse is unknown";
        }

        // A response prompt raised by THIS nation. Every PromptPolicyResponse, the base
        // one and all three overrides, passes the enacting nation as promptingGameState,
        // so that plus the name identifies the proposal. Matching the name alone --
        // which is all HasAnyPromptofType offers -- would be answered by any other
        // nation's proposal of the same type anywhere on the map.
        //
        // `target` narrows the match to one proposal. nation.set_policy passes null,
        // because its options raise prompts at three different acting states (the
        // target, the target's nation, the enemy war leader) and it does not repeat
        // that per-option logic. faction.diplomacy passes the target: both of its async
        // options take the base PromptPolicyResponse, which passes
        // `policyTarget as TIPolityState` as actingState, so the triple (name,
        // promptingGameState, actingState) identifies one proposal. Without the
        // actingState term, the USA proposing to Canada and then to Mexico reads the
        // Mexico call as already-queued.
        static bool HasPolicyPrompt(string promptName, TINationState nation,
            TIGameState target = null)
        {
            if (string.IsNullOrEmpty(promptName) || nation == null) return false;
            TIPromptQueueState queue = Safe<TIPromptQueueState>(
                delegate { return GameStateManager.PromptQueue(); }, null);
            if (queue == null) return false;
            return PromptRaisedBy(promptFactionListField, queue, promptName, nation, target)
                || PromptRaisedBy(promptNationListField, queue, promptName, nation, target);
        }

        static bool PromptRaisedBy(FieldInfo field, TIPromptQueueState queue,
                                   string promptName, TINationState nation,
                                   TIGameState target)
        {
            if (field == null) return false;
            List<Prompt> list = Safe<List<Prompt>>(
                delegate { return field.GetValue(queue) as List<Prompt>; }, null);
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                Prompt prompt = list[i];
                if (!string.Equals(Safe<string>(delegate { return prompt.name; }, null),
                        promptName, StringComparison.Ordinal)) continue;
                TIGameState raiser = Safe<TIGameState>(
                    delegate { return prompt.promptingGameState; }, null);
                if (!ReferenceEquals(raiser, nation)) continue;
                if (target == null) return true;
                TIGameState acting = Safe<TIGameState>(
                    delegate { return prompt.actingState; }, null);
                if (ReferenceEquals(acting, target)) return true;
            }
            return false;
        }

        // AddItem inserts at index 0, so the newest notification is the head. Held as an
        // object reference rather than a copy: the comparison below is identity. Null
        // means an empty queue OR a read that threw, and the two are not distinguished:
        // in the second case the walk below has no stopping point and an older
        // LogPolicyAdopted still in the queue would be read as this call's, so a true
        // enactedNow whose notification date is not today is not evidence.
        static object NewestNotification()
        {
            return Safe<object>(delegate
            {
                TINotificationQueueState queue = GameStateManager.NotificationQueue();
                if (queue == null) return null;
                List<NotificationQueueItem> items = queue.notificationQueue;
                return items != null && items.Count > 0 ? items[0] : null;
            }, null);
        }

        // Everything pushed since the snapshot, newest first, searched for the policy
        // record. Not just the head: DeclareIndependenceOption, TransferRegionsOption and
        // EndWarOption override EnactPolicy to log BEFORE OnPassage, so anything their
        // passage pushes (an independence notice, a region handover) sits on top of it,
        // and an unrelated notification can land between the two reads either way.
        static JObject NewPolicyNotification(object before)
        {
            List<NotificationQueueItem> items = Safe<List<NotificationQueueItem>>(delegate
            {
                TINotificationQueueState queue = GameStateManager.NotificationQueue();
                return queue != null ? queue.notificationQueue : null;
            }, null);
            if (items == null) return null;
            for (int i = 0; i < items.Count; i++)
            {
                NotificationQueueItem item = items[i];
                // The snapshot and everything below it was already queued.
                if (ReferenceEquals(item, before)) return null;
                if (item == null) continue;
                if (!string.Equals(Safe<string>(delegate { return item.templateName; }, null),
                        "LogPolicyAdopted", StringComparison.Ordinal)) continue;
                var o = new JObject();
                Put(o, "templateName", delegate { return (JToken)item.templateName; });
                Put(o, "headline", delegate { return (JToken)item.itemHeadline; });
                Put(o, "summary", delegate { return (JToken)item.itemSummary; });
                Put(o, "date", delegate { return (JToken)item.dateTimeString; });
                return o;
            }
            return null;
        }

        static JToken StillPossible(TIPolicyOption policy, TINationState nation,
                                    TIGameState target)
        {
            IList<TIGameState> possible = Safe<IList<TIGameState>>(
                delegate { return policy.GetPossibleTargets(nation); }, null);
            if (possible == null) return new JValue(false);
            return new JValue(possible.Contains(target));
        }

        #endregion

        #region resolution

        // The registry instances, never fresh ones. TIPolicyOption's constructor
        // registers itself with TemplateManager, PolicyOptionWithTarget.policy resolves
        // back through this same dictionary, and the faction's plannedPolicies list is
        // compared against instances out of it, so a private copy would be a second
        // template entry that matches nothing the engine holds.
        static List<TIPolicyOption> AllPolicyOptions()
        {
            var list = new List<TIPolicyOption>();
            Dictionary<PolicyType, IPolicyOption> registry = PolicyManager.policies;
            if (registry == null) return list;
            foreach (KeyValuePair<PolicyType, IPolicyOption> kv in registry)
            {
                var policy = kv.Value as TIPolicyOption;
                if (policy != null) list.Add(policy);
            }
            return list;
        }

        // Data name, PolicyType name and display name all resolve. The first two are
        // the same string by construction: TIPolicyOption's constructor sets dataName
        // from the runtime type, and PolicyType's members are named after those types.
        static TIPolicyOption ArgPolicy(JObject args, string key)
        {
            string name = Str(args, key);
            if (string.IsNullOrEmpty(name)) throw new VerbError("missing arg '" + key + "'");
            name = name.Trim();
            List<TIPolicyOption> all = AllPolicyOptions();
            if (all.Count == 0) throw new VerbError("PolicyManager holds no policy options");
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(PolicyName(all[i]), name, StringComparison.OrdinalIgnoreCase))
                    return all[i];
            }
            for (int i = 0; i < all.Count; i++)
            {
                TIPolicyOption option = all[i];
                if (Same(Safe<string>(delegate { return option.GetDisplayName(); }, null), name))
                    return option;
            }
            var names = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                TIPolicyOption option = all[i];
                string display = Safe<string>(
                    delegate { return option.GetDisplayName(); }, null);
                names.Add(PolicyName(option)
                    + (string.IsNullOrEmpty(display) ? "" : " (" + display + ")"));
            }
            names.Sort(StringComparer.Ordinal);
            throw new VerbError("no policy option named '" + name + "'; registered: "
                + string.Join(", ", names.ToArray()));
        }

        static string PolicyName(TIPolicyOption policy)
        {
            string name = Safe<string>(delegate { return policy.dataName; }, null);
            return !string.IsNullOrEmpty(name) ? name : policy.GetType().Name;
        }

        static JObject DescribePolicy(TIPolicyOption policy)
        {
            var o = new JObject();
            o["dataName"] = PolicyName(policy);
            Put(o, "displayName", delegate { return (JToken)policy.GetDisplayName(); });
            Put(o, "policyType", delegate { return (JToken)policy.GetPolicyType().ToString(); });
            Put(o, "requiresTargets", delegate { return (JToken)policy.RequiresTargets(); });
            Put(o, "requiresTargetConfirm",
                delegate { return (JToken)policy.RequiresTargetConfirm(); });
            Put(o, "handledAtFactionLevel",
                delegate { return (JToken)policy.HandledAtFactionLevel(); });
            return o;
        }

        // CancelOption answers null rather than an empty list, so every caller of this
        // has to survive a null.
        static IList<TIGameState> PossibleTargets(TIPolicyOption policy, TINationState nation)
        {
            try { return policy.GetPossibleTargets(nation); }
            catch (Exception e)
            {
                throw new VerbError(PolicyName(policy) + ".GetPossibleTargets threw: "
                    + Note(e));
            }
        }

        static int PossibleCount(TIPolicyOption policy, TINationState nation)
        {
            return Safe<int>(delegate
            {
                IList<TIGameState> possible = policy.GetPossibleTargets(nation);
                return possible != null ? possible.Count : 0;
            }, -1);
        }

        // A state id, or a name matched inside the option's own possible-target list --
        // which is also the membership test, so a target the engine would not accept is
        // refused with the list that it would.
        static TIGameState ArgPolicyTarget(JObject args, string key, TIPolicyOption policy,
            TINationState nation, IList<TIGameState> possible)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return null;

            TIGameState target;
            int id;
            string text = (t.Type == JTokenType.String ? (string)t : t.ToString()).Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                target = ById<TIGameState>(id);
            }
            else
            {
                target = MatchState(possible, text);
                if (target == null)
                    throw new VerbError("no target named '" + text + "' for "
                        + PolicyName(policy) + " in " + StateName(nation) + "; "
                        + PolicyTargetList(possible));
            }
            if (possible == null || !possible.Contains(target))
                throw new VerbError(StateName(target) + " (" + (int)target.ID
                    + ") is not a possible target for " + PolicyName(policy) + " in "
                    + StateName(nation) + "; " + PolicyTargetList(possible));
            return target;
        }

        static TIGameState MatchState(IList<TIGameState> states, string name)
        {
            if (states == null) return null;
            for (int i = 0; i < states.Count; i++)
            {
                TIGameState state = states[i];
                if (state == null) continue;
                if (Same(Safe<string>(delegate { return state.templateName; }, null), name))
                    return state;
            }
            for (int i = 0; i < states.Count; i++)
            {
                TIGameState state = states[i];
                if (state == null) continue;
                if (Same(StateName(state), name)
                    || Same(Safe<string>(delegate { return state.displayName; }, null), name))
                    return state;
            }
            return null;
        }

        static string PolicyTargetList(IList<TIGameState> possible)
        {
            if (possible == null || possible.Count == 0)
                return "the option offers no targets at all here";
            var names = new List<string>();
            int n = possible.Count < MaxPolicyNamesListed ? possible.Count : MaxPolicyNamesListed;
            for (int i = 0; i < n; i++)
            {
                TIGameState state = possible[i];
                if (state == null) continue;
                names.Add(StateName(state) + " (" + (int)state.ID + ")");
            }
            return "possible (" + possible.Count + "): "
                + string.Join(", ", names.ToArray())
                + (possible.Count > n ? ", ..." : "");
        }

        // A state id, or a nation's data name or display name. Nations are the one
        // argument a policy caller reads off the map rather than out of an earlier
        // response, and a non-extant one (the target of Grant Independence before it
        // exists) has no id worth quoting.
        static TINationState ArgNation(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null)
                throw new VerbError("missing arg '" + key + "'");
            string name = (t.Type == JTokenType.String ? (string)t : t.ToString()).Trim();
            int id;
            if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                return ById<TINationState>(id);
            if (name.Length == 0) throw new VerbError("arg '" + key + "' is empty");
            var byData = new List<TINationState>();
            var byDisplay = new List<TINationState>();
            foreach (TINationState nation in GameStateManager.IterateByClass<TINationState>(false))
            {
                if (nation == null) continue;
                if (Same(Safe<string>(delegate { return nation.templateName; }, null), name))
                {
                    byData.Add(nation);
                    continue;
                }
                if (Same(StateName(nation), name)
                    || Same(Safe<string>(delegate { return nation.displayName; }, null), name))
                    byDisplay.Add(nation);
            }
            List<TINationState> hits = byData.Count > 0 ? byData : byDisplay;
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0)
                throw new VerbError("no nation matches '" + name
                    + "'; names and ids come from query.nations");
            var listed = new List<string>();
            for (int i = 0; i < hits.Count && i < MaxPolicyNamesListed; i++)
                listed.Add(StateName(hits[i]) + " (" + (int)hits[i].ID + ")");
            throw new VerbError("'" + name + "' matches " + hits.Count
                + " nations: " + string.Join(", ", listed.ToArray())
                + "; pass a state id instead");
        }

        #endregion
    }
}
