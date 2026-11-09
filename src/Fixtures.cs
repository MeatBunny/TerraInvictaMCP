using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // nation.set_stat, region.nuke, kill.module.
    //
    // Both force state the way the spawn verbs do: they take the engine's own
    // mutator and leave out the play-legality around it. A fixture proves a
    // mechanism fires, never that the state is reachable through play.
    public static partial class Verbs
    {
        #region region.nuke

        // A nuclear strike arriving on a region, with the faction responsible named by
        // the caller. The console's Nuke always attributes to the active player; the
        // attribution is what the mood machinery, the atrocity bookkeeping and the
        // retaliation read, so a test that is about who nuked whom cannot use it.
        //
        // OnNuclearAttackArrives is the arrival half only: no launch, no travel time,
        // no nuclear stockpile spent. It fires the Mood event and the NuclearStrike
        // game event, then ApplyDamageToRegion at full strength with nuclear set.
        static JToken RegionNuke(JObject args)
        {
            TIRegionState region = ArgState<TIRegionState>(args, "region", "region_name");
            TIFactionState faction = ArgState<TIFactionState>(args, "faction", "faction");
            JToken asked = args != null ? args["nation"] : null;
            TINationState nation = asked != null && asked.Type != JTokenType.Null
                ? ArgNation(args, "nation") : null;

            // ApplyDamageToRegion calls region.nation.ModifyGDP with no null check, and
            // it does so after both events have already fired, so a region with no
            // nation is refused here rather than left half applied.
            TINationState victim = Safe<TINationState>(
                delegate { return region.nation; }, null);
            if (victim == null)
                throw new VerbError("region " + StateName(region)
                    + " has no nation; a nuclear strike is applied through it");

            // The engine's own branch, computed before the call so the reply can say
            // which one ran: severe damage needs the launching nation to be at war with
            // the region's, and an absent nation counts as severe.
            bool severe = nation == null
                || Safe<bool>(delegate { return nation.enemies.Contains(victim); }, false);

            region.OnNuclearAttackArrives(faction, nation);
            // The arrival does not count itself. Every real path runs this increment in
            // the caller (NuclearAttackOnRegion), and without it the region neither
            // reads nor draws as nuked.
            region.ChangeNuclearDetonations(1);

            var o = new JObject();
            o["region"] = Describe(region);
            o["faction"] = Describe(faction);
            o["nation"] = nation != null ? Describe(nation) : (JToken)JValue.CreateNull();
            o["severe"] = severe;
            // The region's own fields ApplyDamageToRegion writes on a nuclear hit,
            // read back off the state. Population is the damage; the three flags are
            // the permanent part, cleared for good when the region was one of them and
            // left alone entirely outside the severe branch. Everything else the call
            // moves (GDP, sustainability, investment, armies, councilors, space
            // facilities) lands on the nation or on other states and is read back with
            // the verb that owns it.
            var after = new JObject();
            Put(after, "populationInMillions",
                delegate { return Num(region.populationInMillions); });
            Put(after, "coreEconomicRegion",
                delegate { return (JToken)region.coreEconomicRegion; });
            Put(after, "resourceRegion",
                delegate { return (JToken)region.resourceRegion; });
            Put(after, "oilRegion", delegate { return (JToken)region.oilRegion; });
            Put(after, "nuclearDetonations",
                delegate { return (JToken)region.nuclearDetonations; });
            o["after"] = after;
            return o;
        }

        #endregion

        #region nation.set_stat

        // One core nation stat. `add` takes a DELTA, because every engine setter
        // for these is an AddTo* method and the backing property setters are all
        // private; set-to-value is AddTo* of (target - current).
        //
        // min/max are the engine's own clamp inside that AddTo*, and they are
        // enforced here rather than left to the clamp: AddToCohesion spills the
        // part that would drive cohesion below zero into democracy and unrest,
        // and AddToInequality spills the part above its maximum into cohesion and
        // unrest, so an out-of-range request would move stats the caller never
        // named. Refusing it keeps every write inside the range, where neither
        // spill can fire.
        class NationStat
        {
            public string name;
            public float min;
            public float max;
            public Func<TINationState, float> read;
            public Action<TINationState, float> add;
        }

        static readonly List<NationStat> nationStats = BuildNationStats();

        static List<NationStat> BuildNationStats()
        {
            var list = new List<NationStat>();
            list.Add(new NationStat { name = "cohesion", min = 0f, max = 10f,
                read = delegate(TINationState n) { return n.cohesion; },
                add = delegate(TINationState n, float d)
                {
                    n.AddToCohesion(d,
                        TINationState.CohesionChangeReason.CohesionReason_Effect);
                } });
            list.Add(new NationStat { name = "democracy", min = 0f, max = 10f,
                read = delegate(TINationState n) { return n.democracy; },
                add = delegate(TINationState n, float d)
                {
                    n.AddToDemocracy(d,
                        TINationState.DemocracyChangeReason.DemReason_EventEffect);
                } });
            list.Add(new NationStat { name = "inequality", min = 1f, max = 9f,
                read = delegate(TINationState n) { return n.inequality; },
                add = delegate(TINationState n, float d)
                {
                    n.AddToInequality(d,
                        TINationState.InequalityChangeReason.InqReason_EventEffects);
                } });
            list.Add(new NationStat { name = "education", min = 1f, max = 255f,
                read = delegate(TINationState n) { return n.education; },
                add = delegate(TINationState n, float d)
                {
                    n.AddToEducation(d,
                        TINationState.EducationChangeReason.EducationReason_EventEffect);
                } });
            return list;
        }

        // Stats the debug console already reaches. This verb refuses them and
        // names the command, rather than growing a second way to do the same
        // thing that a later engine change would have to keep in step. Each
        // entry carries the console command's own argument syntax.
        static readonly string[,] consoleStats = new string[,] {
            { "unrest", "ChangeUnrest <delta> (map selection: nation; a delta, not a target value)" },
            { "miltech", "SetMiltech <value>[, <nation dataName>]" },
            { "militarytechlevel", "SetMiltech <value>[, <nation dataName>]" },
            { "gdp", "ModifyGDP <nation>, <billions>" },
            { "sustainability", "SetSustainability <value> (map selection: nation)" },
            { "nukes", "SetNuclearWeapons <count> (map selection: nation)" },
            { "nuclearweapons", "SetNuclearWeapons <count> (map selection: nation)" },
        };

        static JToken NationSetStat(JObject args)
        {
            TINationState nation = ArgNation(args, "nation");
            string wanted = Str(args, "stat");
            if (string.IsNullOrEmpty(wanted))
                throw new VerbError("missing arg 'stat'; " + StatMenu());
            wanted = wanted.Trim();
            RefuseConsoleStat(wanted);

            NationStat stat = FindStat(wanted);
            if (stat == null)
                throw new VerbError("unknown stat '" + wanted + "'; " + StatMenu());

            // Float() refuses NaN and the infinities for every caller, so what
            // arrives here is a finite number and the range test below means what
            // it says: NaN compares false against both bounds and would otherwise
            // walk straight through it.
            float value = RequiredFloat(args, "value");
            if (value < stat.min || value > stat.max)
                throw new VerbError(stat.name + " is clamped to "
                    + Range(stat) + " by the engine's own setter, and a request "
                    + "outside it would spill into other stats instead of being "
                    + "silently clamped; asked for "
                    + value.ToString(CultureInfo.InvariantCulture));

            float was = stat.read(nation);
            float delta = value - was;
            stat.add(nation, delta);
            JObject after = StatSnapshot(nation);

            var o = new JObject();
            o["nation"] = Describe(nation);
            o["stat"] = stat.name;
            o["requested"] = Num(value);
            o["before"] = Num(was);
            o["delta"] = Num(delta);
            // The read-back, not the request: AddToCohesion returns the delta it
            // was handed rather than the new value, and every one of these
            // re-clamps after adding, so the stat itself is the only truth.
            o["after"] = Safe<JToken>(
                delegate { return Num(stat.read(nation)); }, JValue.CreateNull());
            o["range"] = Range(stat);
            o["stats"] = after;
            return o;
        }

        // Every core stat this verb or the console can move, read back after the
        // write. unrest rides along because it is the other side of both spill paths.
        static JObject StatSnapshot(TINationState nation)
        {
            var o = new JObject();
            for (int i = 0; i < nationStats.Count; i++)
            {
                NationStat stat = nationStats[i];
                o[stat.name] = Safe<JToken>(
                    delegate { return Num(stat.read(nation)); }, JValue.CreateNull());
            }
            Put(o, "unrest", delegate { return Num(nation.unrest); });
            return o;
        }

        static NationStat FindStat(string name)
        {
            for (int i = 0; i < nationStats.Count; i++)
            {
                if (string.Equals(nationStats[i].name, name,
                        StringComparison.OrdinalIgnoreCase))
                    return nationStats[i];
            }
            return null;
        }

        static void RefuseConsoleStat(string name)
        {
            for (int i = 0; i < consoleStats.GetLength(0); i++)
            {
                if (!string.Equals(consoleStats[i, 0], name,
                        StringComparison.OrdinalIgnoreCase)) continue;
                throw new VerbError(name + " has a debug-console command and this "
                    + "verb does not duplicate it: run console line='"
                    + consoleStats[i, 1] + "'. This verb covers the core stats the "
                    + "console cannot reach: " + StatNames());
            }
        }

        static string StatMenu()
        {
            return "stat is one of " + StatNames()
                + "; unrest, miltech, gdp, sustainability and nukes have console "
                + "commands instead; ask for one and this verb names its command";
        }

        static string StatNames()
        {
            var names = new List<string>();
            for (int i = 0; i < nationStats.Count; i++)
                names.Add(nationStats[i].name + " " + Range(nationStats[i]));
            return string.Join(", ", names.ToArray());
        }

        static string Range(NationStat stat)
        {
            return "[" + stat.min.ToString(CultureInfo.InvariantCulture) + ".."
                + stat.max.ToString(CultureInfo.InvariantCulture) + "]";
        }

        #endregion

        #region kill.module

        // Modules listed in a refusal before the list is cut.
        const int MaxModulesListed = 40;

        static JToken KillModule(JObject args)
        {
            JToken moduleArg = args != null ? args["module"] : null;
            int habId = OptionalInt(args, "hab");
            if (moduleArg == null || moduleArg.Type == JTokenType.Null)
            {
                // A hab with no module names the modules it holds: a module state
                // id is not addressable any other way (slot is an index within a
                // sector, so it repeats across a hab), and this listing is the
                // discovery path.
                if (habId >= 0)
                    throw new VerbError("arg 'module' is a module state id; "
                        + ModuleListFor(habId));
                throw new VerbError("missing arg 'module' (a TIHabModuleState id); "
                    + "pass 'hab' alone to list a hab's modules and their ids");
            }

            TIHabModuleState module = Arg<TIHabModuleState>(args, "module");
            TIHabState hab = Safe<TIHabState>(delegate { return module.hab; }, null);
            if (hab == null)
                throw new VerbError("module " + (int)module.ID + " has no hab");
            if (habId >= 0 && habId != (int)hab.ID)
                throw new VerbError("module " + (int)module.ID + " belongs to hab "
                    + (int)hab.ID + " (" + StateName(hab) + "), not " + habId);

            // Refused rather than passed through: DestroyModule skips its whole
            // destruction branch for a module that is not okay, then still runs
            // the full-destruction check, so calling it on an already-dead module
            // of a hab whose last module is gone would destroy the HAB and report
            // it as this call's doing.
            if (!Safe<bool>(delegate { return module.okay; }, false))
                throw new VerbError("module " + (int)module.ID + " is not okay ("
                    + ModuleCondition(module) + "), and destroying it is a no-op "
                    + "that can still take the hab down; " + ModuleList(hab));

            TIFactionState destroyer = null;
            int destroyerId = OptionalInt(args, "destroyer");
            if (destroyerId >= 0) destroyer = ById<TIFactionState>(destroyerId);
            // A switch, not an amount. DestroyModule only ever tests `hate > 0`;
            // what it then applies is the module's tier times the global config's
            // factionHateMultiplierPerModuleDestroyedPerTier, and the number
            // passed in is never read again. Any positive value means the same
            // thing, so the response reports what the engine will actually apply
            // rather than echoing the input as though it were the amount.
            float hate = Float(args, "hate", 0f);
            if (hate < 0f) throw new VerbError("arg 'hate' must be >= 0");
            bool hateOn = hate > 0f;
            if (hateOn && destroyer == null)
                throw new VerbError("arg 'hate' needs a 'destroyer': the engine "
                    + "only applies hate and atrocities when a destroying faction "
                    + "is named");

            bool core = Safe<bool>(delegate
            {
                TIHabModuleTemplate t = module.moduleTemplate;
                return t != null && t.coreModule;
            }, false);
            bool wormhole = Safe<bool>(delegate
            {
                TIHabModuleTemplate t = module.moduleTemplate;
                return t != null && t.SpecialRules != null
                    && t.SpecialRules.Contains(HabModuleSpecialRule.AlienWormhole);
            }, false);
            bool overrideWanted = Flag(args, "override_protection");
            // The engine's own refusal, read before the call so the reply can say
            // whether the flag had anything to override. DestroyModule returns
            // false at IL_005d when the HAB's faction is alien, the hab is that
            // faction's primaryHab, and the module is a core module or carries
            // HabModuleSpecialRule.AlienWormhole (IL_0014-IL_005e).
            TIFactionState owner = Safe<TIFactionState>(
                delegate { return hab.faction; }, null);
            bool alienOwner = owner != null
                && Safe<bool>(delegate { return owner.IsAlienFaction; }, false);
            // The engine's own comparison, which is not reference equality:
            // TIGameState overrides Equals to compare GameStateIDs, and == goes
            // through it. ReferenceEquals would answer differently for two
            // instances carrying one id.
            bool isPrimary = alienOwner && Safe<bool>(
                delegate { return owner.primaryHab == hab; }, false);
            bool blocked = isPrimary && (core || wormhole);
            // The count the engine's own last-module branch reads, taken from the
            // same call it makes: DestroyModule tests `OkayModules().Count` after
            // the destruction and runs DestroyHab on zero. OkayModules caches per
            // frame, and reading it here would poison that branch's answer if the
            // engine did not invalidate the cache -- it does, in
            // TIHabModuleState.DestroyModule, which calls hab.SetModulesDirty()
            // immediately after setting `destroyed`. So this reading and the
            // engine's are the same notion of "okay", one destruction apart.
            //
            // Taken before anything below changes state, and every refusal that
            // reads it is made before the DestroyModule call.
            // Unguarded on purpose: a hab that will not report its okay modules
            // leaves the last-module question unanswerable, and every refusal below
            // tests against 1. The throw reaches the verb's catch-all with nothing
            // changed yet.
            int okayBefore = hab.OkayModules().Count;

            // Whether the engine will actually destroy anything on this call.
            // Under the engine's own refusal it returns false at IL_005d having
            // touched nothing, so neither the hab-destruction branch nor the
            // reporting path below it is reached.
            bool willDestroy = !blocked || overrideWanted;

            // `<= 1`, not `== 1`: OkayModules walks activeSectors only, while the
            // precondition here is the module's own `okay` flag, so a hab can read
            // zero okay modules with a destroyable one in hand and still reach
            // DestroyHab.
            if (blocked && overrideWanted && okayBefore <= 1)
                throw new VerbError("override_protection is refused here: the hab "
                    + "reports " + okayBefore + " okay module(s), so destroying "
                    + "module " + (int)module.ID + " leaves it with none and "
                    + "would take the alien faction's primary hab "
                    + "down with it. DestroyHab has an alien-primary-hab guard of "
                    + "its own, and the primaryHab swap this flag makes would "
                    + "bypass that one as well as the one inside DestroyModule: "
                    + "instead of stopping once it has logged the loss, the engine "
                    + "would clean the notification queue, register the kill, "
                    + "resolve missions, re-home the faction's fleets and archive "
                    + "the hab, and the restore afterwards would leave primaryHab "
                    + "pointing at an archived hab. Taking the hab down is not "
                    + "what the flag is for. To test module loss, destroy a "
                    + "different module; this hab has no other okay one, so give "
                    + "it one with spawn.module first. Nothing was changed");

            // LogHabDestroyed reads destroyingFaction.displayNameCapitalized with no
            // null test (IL_008c on the alien headline, IL_0153 on the ordinary one),
            // after the module is already gone. `<= 1` for the reason given above.
            if (willDestroy && okayBefore <= 1 && destroyer == null)
                throw new VerbError("hab " + (int)hab.ID + " reports " + okayBefore
                    + " okay module(s), so destroying module " + (int)module.ID
                    + " leaves it with none and runs the "
                    + "engine's hab destruction, and that path reports the loss "
                    + "through the destroying faction with no null check: with no "
                    + "'destroyer' it throws a NullReferenceException after the "
                    + "module is already gone. Pass 'destroyer' (a faction id) to "
                    + "make this call, destroy a different module, or take the "
                    + "hab out with kill.state. On the alien faction's primary "
                    + "hab even a named destroyer leaves the hab standing: the "
                    + "engine logs the loss and returns without archiving it. "
                    + "Nothing was changed");

            var o = new JObject();
            o["hab"] = Describe(hab);
            o["module"] = DescribeModule(module);
            o["coreModule"] = core;
            o["alienWormhole"] = wormhole;
            o["engineWouldRefuse"] = blocked;
            o["overrideProtection"] = overrideWanted;
            o["destroyer"] = destroyer != null ? Describe(destroyer) : (JToken)JValue.CreateNull();
            o["okayModulesBefore"] = okayBefore;

            // The override, and only when the refusal would actually fire. The
            // engine's test is an identity comparison against the alien faction's
            // primaryHab, a public field, so pointing that field at some other hab
            // of the same faction for the duration of the one call is the whole
            // trick. It is restored in a finally rather than left to the engine:
            // ResetPrimaryHab returns at its first instruction unless the faction
            // is the active HUMAN one, so nothing else puts the field back. The
            // hab cannot go down under the swap, because the last-okay-module case
            // is refused above, so the restore always names the same live hab the
            // field named before the call.
            bool overrideApplied = blocked && overrideWanted;
            TIHabState savedPrimary = overrideApplied ? owner.primaryHab : null;
            if (overrideApplied) owner.primaryHab = OtherHab(owner, hab);

            // Read BEFORE the destruction, because the destruction changes it.
            // TIHabModuleState.tier is moduleTemplate.tier, and that type's own
            // DestroyModule replaces the template outright: it builds a wreckage
            // name from "DestroyedModule" or "AlienDestroyedModule" plus the old
            // tier plus a random 1-2 suffix (IL_00d4-IL_010b of that method) and
            // hands it to SetModuleTemplate (IL_010e). Afterwards `module.tier`
            // is the WRECKAGE template's tier. Vanilla wreckage entries happen to
            // carry the tier they are named for, so reading it after the call
            // agreed with the engine by data coincidence; a mod that ships a
            // wreckage entry with a different tier breaks that silently.
            //
            // The engine computes its hate from the original: TIHabState's
            // DestroyModule reads get_tier at IL_00c7, applies the hate at
            // IL_00df, and calls the module's own DestroyModule() only at
            // IL_0130. Null when the read throws, which keeps `applied` null
            // rather than making up a number.
            JToken tier = Safe<JToken>(delegate { return (JToken)module.tier; },
                JValue.CreateNull());

            // The engine's own destruction, the same call TIEffectsState's
            // DestroyRandomModules makes with every optional argument left at its
            // default: log it, count it toward the hab's destruction, alert, no
            // hate unless asked, repower the hab afterwards. Decommissioning is
            // the wrong path here -- CompleteDecommissionModule empties the slot
            // and refunds the build cost, which models a teardown, while this
            // leaves wreckage in the slot, fires HabModuleDestroyed, and takes the
            // hab down when the last okay module goes.
            //
            // A throw out of it is caught rather than let out. By the time the
            // engine throws, the module is destroyed and the hab may or may not
            // be: an error envelope carries a message and no data, so the one
            // reading that says what the call left behind would be the one thing
            // the caller could not get. The reply below is built either way, with
            // `engineThrew` naming the exception and the read-backs saying where
            // the state landed. The refusals above cover the throw this has
            // actually been seen to take; anything else is a new one and the
            // caller needs its text, not a bare NullReferenceException.
            bool destroyed;
            JToken threw = JValue.CreateNull();
            try
            {
                destroyed = hab.DestroyModule(destroyer, module, false, false,
                    true, hate, false, false);
            }
            catch (Exception e)
            {
                threw = new JValue(Note(e));
                // Read back rather than assumed either way: the engine sets
                // `destroyed` on the module before anything that can throw.
                destroyed = !Safe<bool>(delegate { return module.okay; }, true);
            }
            finally
            {
                if (overrideApplied) owner.primaryHab = savedPrimary;
            }

            o["engineThrew"] = threw;
            o["overrideApplied"] = overrideApplied;
            if (owner != null)
                Put(o, "primaryHab", delegate
                {
                    TIHabState primary = owner.primaryHab;
                    return primary != null ? Describe(primary)
                                           : (JToken)JValue.CreateNull();
                });
            o["destroyed"] = destroyed;
            // Built here, after the call, because `applied` is a claim about what the
            // engine did and only the return value says that. The tier it works from
            // was read before the call, for the reason given there.
            o["hate"] = HateToken(tier, hateOn, hate, destroyed);
            o["moduleAfter"] = Safe<JToken>(
                delegate { return DescribeModule(module); }, JValue.CreateNull());
            bool habGone = Safe<bool>(delegate { return hab.archived; }, false);
            o["habDestroyed"] = habGone;
            o["okayModulesAfter"] = habGone ? -1 : ModuleCount(hab, true);
            o["presentModulesAfter"] = habGone ? -1 : ModuleCount(hab, false);

            var warnings = new List<string>();
            // First, because everything after it describes a call that ran to the
            // end and this one did not.
            if (threw.Type != JTokenType.Null)
                warnings.Add("DestroyModule THREW: " + threw.ToString()
                    + ". The fields above are read back after the throw, so they say "
                    + "where the state landed; report it");
            // Only when the call ran to the end: after a throw, a false here is
            // "the engine never got to say", not the documented refusal.
            if (!destroyed && threw.Type == JTokenType.Null)
                warnings.Add("the engine refused the destruction: a core module, or "
                    + "one whose template carries HabModuleSpecialRule.AlienWormhole, "
                    + "on the alien faction's primary hab"
                    + (blocked && !overrideWanted
                        ? ", which is this call; pass override_protection=true"
                        : ""));
            if (overrideApplied && destroyed)
                warnings.Add("override_protection swapped primaryHab for one call; "
                    + "this state is one no play path produces");
            if (core && destroyed)
                warnings.Add("a core module was destroyed; this is a hab state no "
                    + "play path produces");
            if (habGone)
                warnings.Add("that was the hab's last okay module, so the engine "
                    + "destroyed the hab itself");
            // Only when something was actually destroyed: the hate branch sits inside
            // the destruction, after the refusal returns false at IL_005d.
            if (hateOn && destroyed)
                warnings.Add("hate was switched on and applied; see the hate object "
                    + "for the amount the engine computed");
            if (hateOn && !destroyed)
                warnings.Add("hate was requested, but nothing was destroyed, so the "
                    + "engine never reached its hate branch");
            if (warnings.Count > 0)
                o["warning"] = string.Join("; ", warnings.ToArray());
            return o;
        }

        // What the engine actually applied, computed the way DestroyModule computes
        // it: tier * factionHateMultiplierPerModuleDestroyedPerTier, with the
        // requested number never entering the arithmetic.
        //
        // `destroyed` is the engine's own return value, and `applied` is zero without
        // it. The hate branch is inside the destruction (TIHabState.DestroyModule
        // tests hate > 0 at IL_00b6-IL_00bd and calls GainFactionHate at IL_00df),
        // while the refusal returns false at IL_005d having touched nothing, so a
        // refused call gained nobody any hate. Reporting the arithmetic anyway was a
        // number the campaign did not hold.
        //
        // `tier` is the module's tier read before the destruction, not the module,
        // because the destruction replaces the module's template with a wreckage
        // entry and the tier goes with it. See the read at the call site. Null when
        // that read threw, and then `applied` is null too: the arithmetic has no
        // number to work from and a zero here would read as "the engine applied
        // none", which is a different claim.
        static JObject HateToken(JToken tier, bool enabled, float requested,
            bool destroyed)
        {
            var o = new JObject();
            o["requested"] = Num(requested);
            o["enabled"] = enabled;
            o["tier"] = tier;
            Put(o, "perTierMultiplier", delegate
            {
                return Num(TemplateManager.global
                    .factionHateMultiplierPerModuleDestroyedPerTier);
            });
            if (!(enabled && destroyed))
                o["applied"] = Num(0f);
            else if (tier == null || tier.Type != JTokenType.Integer)
                o["applied"] = JValue.CreateNull();
            else
            {
                int tierValue = (int)tier;
                o["applied"] = Safe<JToken>(delegate
                {
                    return Num(tierValue * TemplateManager.global
                        .factionHateMultiplierPerModuleDestroyedPerTier);
                }, JValue.CreateNull());
            }
            return o;
        }

        // Somewhere else to point primaryHab while the protected module is
        // destroyed. Any live hab of the same faction will do, because the engine
        // tests identity against the one being worked on and nothing else in the
        // call reads the field. Null when the faction has no other hab, which is
        // equally good: the identity test fails against null too.
        static TIHabState OtherHab(TIFactionState faction, TIHabState except)
        {
            return Safe<TIHabState>(delegate
            {
                List<TIHabState> habs = faction.habs;
                if (habs == null) return null;
                for (int i = 0; i < habs.Count; i++)
                {
                    TIHabState hab = habs[i];
                    if (hab == null || hab == except) continue;
                    if (hab.archived) continue;
                    return hab;
                }
                return null;
            }, null);
        }

        static int ModuleCount(TIHabState hab, bool okayOnly)
        {
            return Safe<int>(delegate
            {
                List<TIHabModuleState> list = okayOnly
                    ? hab.OkayModules() : hab.PresentModules();
                return list != null ? list.Count : 0;
            }, -1);
        }

        // A hab id quoted inside a refusal that is about some OTHER argument.
        // ById throws a VerbError of its own, so resolving the hab while building
        // the message replaces "pass 'module' as a state id" with "no TIHabState
        // with id N" and sends the caller at the wrong argument -- which is the
        // exact failure these refusals exist to prevent. The listing is a hint, so
        // a hab id that does not resolve says so and the refusal it decorates
        // survives intact.
        static string ModuleListFor(int habId)
        {
            TIHabState hab = Safe<TIHabState>(delegate
            {
                return GameStateManager.FindGameState<TIHabState>(
                    new GameStateID(habId), true);
            }, null);
            if (hab == null)
                return "arg 'hab' (" + habId + ") is no TIHabState, so its "
                    + "modules cannot be listed";
            return ModuleList(hab);
        }

        static string ModuleList(TIHabState hab)
        {
            List<TIHabModuleState> modules = Safe<List<TIHabModuleState>>(
                delegate { return hab.PresentModules(); }, null);
            if (modules == null || modules.Count == 0)
                return StateName(hab) + " (" + (int)hab.ID + ") holds no modules";
            var rows = new List<string>();
            int n = modules.Count < MaxModulesListed ? modules.Count : MaxModulesListed;
            for (int i = 0; i < n; i++)
            {
                TIHabModuleState m = modules[i];
                if (m == null) continue;
                rows.Add((int)m.ID + "=" + ModuleTemplateName(m)
                    + " [sector " + Safe<int>(delegate { return m.sector.sectorNum; }, -1)
                    + " slot " + Safe<int>(delegate { return m.slot; }, -1)
                    + ", " + ModuleCondition(m) + "]");
            }
            return StateName(hab) + " (" + (int)hab.ID + ") holds "
                + modules.Count + " modules: " + string.Join(", ", rows.ToArray())
                + (modules.Count > n ? ", ..." : "");
        }

        static string ModuleTemplateName(TIHabModuleState module)
        {
            return Safe<string>(delegate
            {
                TIHabModuleTemplate t = module.moduleTemplate;
                return t != null ? t.dataName : "<empty>";
            }, "<unreadable>");
        }

        static string ModuleCondition(TIHabModuleState module)
        {
            if (Safe<bool>(delegate { return module.empty; }, false)) return "empty";
            if (Safe<bool>(delegate { return module.destroyed; }, false)) return "destroyed";
            if (Safe<bool>(delegate { return module.decommissioning; }, false))
                return "decommissioning";
            if (Safe<bool>(delegate { return module.underConstruction; }, false))
                return "okay, under construction";
            return "okay";
        }

        #endregion
    }
}
