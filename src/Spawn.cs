using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // spawn.hab, spawn.module, spawn.army, spawn.councilor, spawn.alien_site.
    //
    // Every one of these takes the game's own construction path with the cost,
    // prerequisite and build-time gates left out. That makes them fixtures: a spawn
    // proves a mechanism fires, never that the content is reachable through play.
    public static partial class Verbs
    {
        // Bounds the module list before any of it is resolved. The hab path refuses
        // what does not fit rather than overflowing, so this only keeps an absurd
        // request from turning into an absurd number of template lookups.
        const int MaxSpawnModules = 32;

        // Construction time handed to InitiateConstructModule. Zero dates the module
        // complete now; the method reads any negative value as "use the template's own
        // build time", which is what an uncompleted spawn should leave behind.
        const double BuildNow = 0.0;
        const double BuildNormally = -1.0;

        #region spawn.hab

        static JToken SpawnHab(JObject args)
        {
            TIFactionState faction = Arg<TIFactionState>(args, "faction");
            TIGameState selected = Arg<TIGameState>(args, "location");
            int tier = Int(args, "tier");
            if (tier < 1 || tier > 3) throw new VerbError("arg 'tier' must be 1..3");
            List<string> modules = ModuleNames(args, "modules");
            bool complete = Bool(args, "complete", true);

            // Founding the site and registering the hab happen in one breath, so every
            // name is resolved first: a template miss part way through would leave a
            // hab behind that the call then has to refuse.
            for (int i = 0; i < modules.Count; i++) ModuleTemplate(modules[i]);

            TIGameState location = FoundSite(selected);

            // The module list is deliberately NOT handed to InitializeNewHab. Its
            // founding loop walks sectors as it fills slots, and when it runs out of
            // empty active sectors it resets the slot cursor to zero in the sector it
            // is already in, overwriting what is there. At tier 1 the first thing it
            // overwrites is the core module, and it reports nothing. Modules go in
            // afterwards through AvailableSlots, which can only ever return an empty
            // or destroyed slot.
            TIHabState hab = null;
            try
            {
                hab = GameStateManager.CreateNewGameState<TIHabState>();
                hab.InitializeNewHab(faction, location, faction, tier, 0f, null);
            }
            catch (Exception)
            {
                // Best effort: the sector and module states InitializeNewHab creates
                // before a throw are registered separately and are not reachable from
                // here, so this drops the hab and leaves those orphaned rather than
                // leaving a half-built hab in play.
                if (hab != null) DiscardState<TIHabState>(hab);
                throw;
            }

            // The hab is registered from here on, so nothing below may leave through an
            // error frame: a caller told only "failed" would never learn a real hab is
            // standing. A module that throws joins the ones that did not fit.
            var missed = new JArray();
            var failures = new List<string>();
            int installed = 0;
            for (int i = 0; i < modules.Count; i++)
            {
                try
                {
                    if (Install(hab, modules[i], complete) != null) installed++;
                    else missed.Add(new JValue(modules[i]));
                }
                catch (Exception e)
                {
                    missed.Add(new JValue(modules[i]));
                    failures.Add(modules[i] + ": " + (e is VerbError ? e.Message : Note(e)));
                }
            }
            if (complete)
            {
                try { CompleteHab(hab); }
                catch (Exception e)
                {
                    failures.Add("completion: " + (e is VerbError ? e.Message : Note(e)));
                }
            }

            var o = new JObject();
            o["id"] = (int)hab.ID;
            o["name"] = StateName(hab);
            Put(o, "tier", delegate { return (JToken)hab.tier; });
            Put(o, "habType", delegate { return (JToken)hab.habType.ToString(); });
            o["faction"] = Describe(faction);
            o["location"] = Describe(location);
            o["completed"] = complete;
            o["modules"] = HabModules(hab);
            o["modulesRequested"] = modules.Count;
            o["modulesInstalled"] = installed;
            o["modulesNotInstalled"] = missed;
            // The hab is real and usable either way, so a shortfall is reported rather
            // than thrown; a caller that only checks ok would otherwise never learn the
            // fixture came out smaller than it asked for.
            if (missed.Count > 0 || failures.Count > 0)
            {
                string warning = missed.Count + " of " + modules.Count
                    + " modules were not installed (the hab holds what its tier's"
                    + " active sectors hold)";
                for (int i = 0; i < failures.Count; i++) warning += "; " + failures[i];
                o["warning"] = warning;
            }
            return o;
        }

        // Resolves the requested location to somewhere InitializeNewHab accepts. A hab
        // site or an orbit is used directly; a body or Lagrange point falls back to its
        // first free ground site, else its first orbit. Bodies and Lagrange points both
        // carry an orbits list.
        static TIGameState FoundSite(TIGameState selected)
        {
            if (Safe<bool>(delegate { return selected.isHabSiteState; }, false))
            {
                TIHabSiteState site = selected.ref_habSite;
                if (site == null) throw new VerbError("hab site state has no site");
                if (site.hab != null) throw new VerbError("site is occupied");
                // pendingHab marks a site an AI has committed a founding fleet to.
                // Founding here clears the marker, and the fleet that arrives later
                // overwrites the site's hab reference, orphaning whatever this spawned.
                if (Safe<bool>(delegate { return site.pendingHab; }, false))
                    throw new VerbError("site is reserved for a hab already on its way");
                site.FoundHab();
                return site;
            }
            // An orbit is taken as it is, with nothing claimed. TIOrbitState.FoundHab
            // is a bare decrement of pendingHabs (IL_0002-IL_0009) and the release half
            // of somebody else's reservation: FoundStationOperation.OnOperationConfirm
            // takes one with MarkPendingHab (IL_0033) and that operation's
            // ExecuteOperation gives it back with FoundHab (IL_0006) as its hab becomes
            // real. A spawn takes no reservation, so calling FoundHab here would spend
            // an AI's, and the fleet already on its way would decrement a second time.
            // Leaving it alone orphans nothing: an orbit holds a list of stations
            // rather than one slot, the spawned hab joins that list through AssumeOrbit,
            // and NewStationAllowed reads stationsInOrbit.Count + pendingHabs
            // (IL_000c-IL_0018), so the count stays honest on both halves. The hab-site
            // branch above refuses a reserved site instead, because a site holds exactly
            // one hab and the arriving fleet would overwrite whatever was founded there.
            if (Safe<bool>(delegate { return selected.isOrbitState; }, false))
            {
                TIOrbitState orbit = selected.ref_orbit;
                if (orbit == null) throw new VerbError("orbit state has no orbit");
                return orbit;
            }

            TINaturalSpaceObjectState body = Safe<TINaturalSpaceObjectState>(delegate
            {
                return selected.isNaturalSpaceObjectState
                    ? selected.ref_naturalSpaceObject : null;
            }, null);
            if (body == null)
                throw new VerbError("state " + (int)selected.ID + " is not a hab site, "
                    + "orbit, Lagrange point, or planet/moon");

            TIHabSiteState ground = FreeSite(body);
            if (ground != null)
            {
                ground.FoundHab();
                return ground;
            }
            TIOrbitState first = FirstOrbit(body);
            if (first == null)
                throw new VerbError("no free site or orbit at " + StateName(body));
            return first;
        }

        // Stricter than the engine's own vacantHabSites, which only rejects a site whose
        // hab has present modules: a site holding a module-less hab reads as vacant
        // there, and a site an AI has reserved reads as vacant until its fleet arrives.
        // Founding on either overwrites the site's hab reference and orphans a hab. The
        // direct hab-site path refuses both cases too.
        static TIHabSiteState FreeSite(TINaturalSpaceObjectState body)
        {
            try
            {
                if (!body.isSpaceBodyState) return null;
                List<TIHabSiteState> sites = body.ref_spaceBody.vacantHabSites;
                if (sites == null) return null;
                for (int i = 0; i < sites.Count; i++)
                {
                    TIHabSiteState site = sites[i];
                    if (site != null && site.hab == null && !site.pendingHab) return site;
                }
            }
            catch (Exception) { }
            return null;
        }

        static TIOrbitState FirstOrbit(TINaturalSpaceObjectState body)
        {
            try
            {
                List<TIOrbitState> orbits = body.orbits;
                if (orbits == null) return null;
                for (int i = 0; i < orbits.Count; i++)
                {
                    if (orbits[i] != null) return orbits[i];
                }
            }
            catch (Exception) { }
            return null;
        }

        // Runs after the requested modules are in, so unlike the game's founding path
        // the core is completed last of the two groups. Harmless: completing the core
        // recomputes power management across the whole hab, so modules that went in
        // while it was still under construction are picked up. Within this method the
        // core still goes first, because completing it writes the hab's tier from the
        // core template; taking the pending list afterwards means it is already out.
        static void CompleteHab(TIHabState hab)
        {
            TIHabModuleState core = Safe<TIHabModuleState>(
                delegate { return hab.coreModule; }, null);
            if (core != null) hab.CompleteModuleConstruction(core);

            List<TIHabModuleState> pending = hab.UnderConstructionModules();
            if (pending == null) return;
            for (int i = 0; i < pending.Count; i++)
            {
                TIHabModuleState module = pending[i];
                if (module == null || ReferenceEquals(module, core)) continue;
                hab.CompleteModuleConstruction(module);
            }
        }

        // Installed modules only: the empty slots are the difference between this and
        // the hab's slot count, and listing them would bury the answer.
        static JArray HabModules(TIHabState hab)
        {
            var a = new JArray();
            AddModules(a, Safe<List<TIHabModuleState>>(
                delegate { return hab.CompletedModules(); }, null));
            AddModules(a, Safe<List<TIHabModuleState>>(
                delegate { return hab.UnderConstructionModules(); }, null));
            return a;
        }

        static void AddModules(JArray a, List<TIHabModuleState> modules)
        {
            if (modules == null) return;
            for (int i = 0; i < modules.Count; i++)
            {
                if (modules[i] != null) a.Add(DescribeModule(modules[i]));
            }
        }

        // One shape for every verb that hands a module back, so spawn.module,
        // kill.module, module.power and spawn.hab's module list all agree.
        //
        // The power block is what `module.power` reads its gates from, reported
        // here so a caller sees a refusal coming instead of discovering it. Each
        // predicate is the engine's own, called rather than reimplemented; each
        // read is guarded, so a module in a state one of them dislikes nulls that
        // field and keeps the rest.
        static JToken DescribeModule(TIHabModuleState module)
        {
            var o = new JObject();
            o["id"] = (int)module.ID;
            o["name"] = StateName(module);
            Put(o, "slot", delegate { return (JToken)module.slot; });
            // slot is an index within a sector and repeats across a hab, so the
            // pair is what locates a module the way the listings print it.
            Put(o, "sectorNum", delegate { return (JToken)module.sectorNum; });
            Put(o, "template", delegate
            {
                TIHabModuleTemplate t = module.moduleTemplate;
                return t != null ? (JToken)new JValue(t.dataName) : JValue.CreateNull();
            });
            Put(o, "completed", delegate { return (JToken)module.completed; });
            Put(o, "underConstruction", delegate { return (JToken)module.underConstruction; });
            Put(o, "destroyed", delegate { return (JToken)module.destroyed; });
            Put(o, "decommissioning", delegate { return (JToken)module.decommissioning; });
            Put(o, "powered", delegate { return (JToken)module.powered; });
            // The engine's own `active`, which is exactly functional && powered.
            Put(o, "active", delegate { return (JToken)module.active; });
            // ModulePower(), not template.power: solar output and the escape-
            // velocity power requirement are special rules routed through
            // SolarPowerOutput and EscapeVelocityBasedPowerRequirement, and the
            // template number misses both. Positive produces, negative consumes.
            Put(o, "power", delegate { return (JToken)module.ModulePower(); });
            Put(o, "canPower", delegate { return (JToken)module.CanPower(); });
            Put(o, "canDepower", delegate { return (JToken)module.CanDepower(); });
            Put(o, "canTurnOff", delegate
            {
                TIHabModuleTemplate t = module.moduleTemplate;
                return t != null ? (JToken)new JValue(t.CanTurnOff) : JValue.CreateNull();
            });
            Put(o, "buildCost", delegate { return CostToken(module.buildCost); });
            return o;
        }

        // The recorded price of a module, which is what the decommission refund
        // and every other cost-return path reads. Null is a module that was never
        // given one: the engine's own founding path passes no cost either, so a
        // hab's core module reads null here in a vanilla game too.
        static JToken CostToken(TIResourcesCost cost)
        {
            if (cost == null) return JValue.CreateNull();
            var o = new JObject();
            Put(o, "completionTimeDays", delegate { return Num(cost.completionTime_days); });
            var resources = new JObject();
            List<ResourceValue> values = Safe<List<ResourceValue>>(
                delegate { return cost.resourceCosts; }, null);
            for (int i = 0; values != null && i < values.Count; i++)
                resources[values[i].resource.ToString()] = Num(values[i].value);
            o["resources"] = resources;
            return o;
        }

        #endregion

        #region spawn.module

        static JToken SpawnModule(JObject args)
        {
            TIHabState hab = Arg<TIHabState>(args, "hab");
            string name = Str(args, "module");
            if (string.IsNullOrEmpty(name)) throw new VerbError("missing arg 'module'");
            ModuleTemplate(name);
            bool complete = Bool(args, "complete", true);

            TIHabModuleState slot = Install(hab, name, complete);
            if (slot == null) throw new VerbError("no available slots");

            var o = new JObject();
            o["hab"] = Describe(hab);
            o["module"] = DescribeModule(slot);
            o["completed"] = complete;
            Put(o, "slotsRemaining", delegate
            {
                List<TIHabModuleState> left = hab.AvailableSlots();
                return (JToken)(left != null ? left.Count : 0);
            });
            return o;
        }

        // Installs one module in the hab's first available slot; null when there is
        // none. AvailableSlots reads the active sectors only and returns empty or
        // destroyed slots, so nothing standing is ever displaced.
        //
        // TIHabModuleState.SetModuleTemplate is private, and on its own it would leave
        // the construction flags, build dates, power status and build cost holding
        // whatever the slot carried before. InitiateConstructModule is both the public
        // entry point and the play path's own: it clears all of that first.
        static TIHabModuleState Install(TIHabState hab, string templateName, bool complete)
        {
            List<TIHabModuleState> slots = hab.AvailableSlots();
            if (slots == null || slots.Count == 0) return null;
            TIHabModuleState slot = slots[0];
            slot.InitiateConstructModule(templateName, BuildCost(hab, templateName),
                complete ? BuildNow : BuildNormally);
            if (complete) hab.CompleteModuleConstruction(slot);
            return slot;
        }

        // The price the hab's owner would have paid to build this module from
        // Earth, which is what the play path records: TIHabState.InitiateModule-
        // Construction hands TIHabModuleTemplate.CostFromEarth to
        // InitiateConstructModule, and that method's only use of a cost is
        // `if (cost != null) buildCost = new TIResourcesCost(cost)` -- charging is
        // a separate PayCost call the caller makes afterwards, which this fixture
        // still does not make. So the module carries its price and the faction is
        // not billed.
        //
        // A null cost here is what the verb used to pass always, and it leaves
        // buildCost null: the decommission refund reads that field through a null-
        // conditional, so every refund from a spawned module silently paid nothing.
        //
        // Best-effort: a cost that cannot be computed must not stop the module
        // from being installed, since a fixture with no price is still a fixture.
        static TIResourcesCost BuildCost(TIHabState hab, string templateName)
        {
            return Safe<TIResourcesCost>(delegate
            {
                TIFactionState faction = hab.faction;
                if (faction == null) return null;
                // isUpgrade false: AvailableSlots only ever returns an empty or
                // destroyed slot, so nothing is being upgraded.
                return ModuleTemplate(templateName).CostFromEarth(faction, hab, false);
            }, null);
        }

        static TIHabModuleTemplate ModuleTemplate(string name)
        {
            TIHabModuleTemplate template = TemplateManager.Find<TIHabModuleTemplate>(name, false);
            if (template == null)
                throw new VerbError("no TIHabModuleTemplate named '" + name + "'");
            return template;
        }

        static List<string> ModuleNames(JObject args, string key)
        {
            var names = new List<string>();
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return names;
            var array = t as JArray;
            if (array == null)
                throw new VerbError("arg '" + key + "' must be an array of module data names");
            for (int i = 0; i < array.Count; i++)
            {
                string name = array[i] != null && array[i].Type != JTokenType.Null
                    ? array[i].ToString().Trim() : null;
                if (string.IsNullOrEmpty(name))
                    throw new VerbError("arg '" + key + "' holds an empty module name");
                names.Add(name);
            }
            if (names.Count > MaxSpawnModules)
                throw new VerbError("at most " + MaxSpawnModules + " modules per hab");
            return names;
        }

        #endregion

        #region spawn.army

        static JToken SpawnArmy(JObject args)
        {
            string type = Str(args, "type");
            if (string.IsNullOrEmpty(type)) type = "human";
            TIRegionState region = Arg<TIRegionState>(args, "region");

            if (Same(type, "human")) return SpawnHumanArmy(args, region);
            if (Same(type, "megafauna"))
            {
                // SpawnArmy hands the army to its home nation, which it reads through
                // the region. A region with no nation would throw inside the engine
                // with the army state already registered.
                if (Safe<TINationState>(delegate { return region.nation; }, null) == null)
                    throw new VerbError("region " + StateName(region)
                        + " has no nation; megafauna are added to the region's nation");
                var army = GameStateManager.CreateNewGameState<TIMegafaunaArmyState>();
                try { army.SpawnArmy(region); }
                catch (Exception) { DiscardState<TIMegafaunaArmyState>(army); throw; }
                return DescribeArmy(army, region, null);
            }
            if (Same(type, "invader"))
            {
                // No nation guard: this SpawnArmy also calls AddArmy, but on the alien
                // nation, and its one read of the region's nation is null-guarded.
                var army = GameStateManager.CreateNewGameState<TIAlienArmyState>();
                try { army.SpawnArmy(region); }
                catch (Exception) { DiscardState<TIAlienArmyState>(army); throw; }
                return DescribeArmy(army, region, null);
            }
            throw new VerbError("unknown army type '" + type
                + "' (human, megafauna, invader)");
        }

        // The nation's own army-build path, minus two of its steps: the new-army
        // notification, which is spam in a fixture, and the market adjustment, which is
        // an effect of the build priority rather than of an army existing.
        static JToken SpawnHumanArmy(JObject args, TIRegionState region)
        {
            TINationState nation = Arg<TINationState>(args, "nation");
            TINationState owner = Safe<TINationState>(
                delegate { return region.nation; }, null);
            if (!ReferenceEquals(owner, nation))
                throw new VerbError("region " + StateName(region) + " belongs to "
                    + (owner != null ? StateName(owner) : "no nation")
                    + ", not " + StateName(nation));

            float strength = Bounded("strength", Float(args, "strength", 1f), 0f, 1f,
                "army strength is a 0..1 fraction of full, which every engine"
                + " writer but NewArmy clamps; NewArmy stores it as given, so a"
                + " larger value survives only until the army takes damage or"
                + " heals");

            var army = GameStateManager.CreateNewGameState<TIArmyState>();
            try
            {
                army.createdFromTemplate = false;
                army.deploymentType = DeploymentType.Standard;
                army.controlPointIdx = nation.GetNextArmyControlPointIdx();
                army.homeRegion = region;
                army.NewArmy(ArmyType.Human, 0, strength);
                army.MoveArmyToRegion(region, true);
                nation.AddArmy(army);
                nation.SetDataDirty();
                army.SetGameStateCreated();
            }
            catch (Exception)
            {
                DiscardState<TIArmyState>(army);
                throw;
            }

            return DescribeArmy(army, region, nation);
        }

        static JToken DescribeArmy(TIArmyState army, TIRegionState region, TINationState nation)
        {
            var o = new JObject();
            o["id"] = (int)army.ID;
            o["name"] = StateName(army);
            Put(o, "type", delegate { return (JToken)army.armyType.ToString(); });
            Put(o, "strength", delegate { return Num(army.strength); });
            o["region"] = Describe(region);
            o["nation"] = nation != null ? Describe(nation) : null;
            Put(o, "faction", delegate
            {
                TIFactionState faction = army.faction;
                return faction != null ? Describe(faction) : (JToken)JValue.CreateNull();
            });
            return o;
        }

        #endregion

        #region spawn.councilor

        // The template a fresh councilor is generated from. Every vanilla caller picks
        // one of these two by faction before generating.
        const string CouncilorTemplate = "randomizedCouncilor1";
        const string AlienCouncilorTemplate = "randomizedAlienCouncilor2";

        static JToken SpawnCouncilor(JObject args)
        {
            TIFactionState faction = Arg<TIFactionState>(args, "faction");
            // AddAvailableCouncilor adds to the council list unconditionally, so an
            // overfull council is only ever caught here.
            int empty = Safe<int>(delegate { return faction.emptyCouncilorSlots; }, 0);
            if (empty <= 0) throw new VerbError("council is full");

            TICouncilorTypeTemplate job = null;
            string jobName = Str(args, "job");
            if (!string.IsNullOrEmpty(jobName))
            {
                job = TemplateManager.Find<TICouncilorTypeTemplate>(jobName, false);
                if (job == null)
                    throw new VerbError("no TICouncilorTypeTemplate named '" + jobName + "'");
            }

            TIRegionState home = null;
            int regionId = OptionalInt(args, "region");
            if (regionId >= 0) home = ById<TIRegionState>(regionId);

            // NewCharacterGeneration's very first read is this.template.alien, and
            // CreateNewGameState leaves the template unset. Without this the call throws
            // with the councilor already registered, and the same read runs again from
            // the post-create init on every later save load, which breaks the save.
            // Resolved before anything is created, so a missing template costs nothing.
            string templateName = Safe<bool>(delegate { return faction.IsAlienFaction; }, false)
                ? AlienCouncilorTemplate : CouncilorTemplate;
            TICouncilorTemplate template =
                TemplateManager.Find<TICouncilorTemplate>(templateName, false);
            if (template == null)
                throw new VerbError("no TICouncilorTemplate named '" + templateName + "'");

            var councilor = GameStateManager.CreateNewGameState<TICouncilorState>();
            try
            {
                councilor.InitWithTemplate(template);
                councilor.NewCharacterGeneration(job, home, faction,
                    Flag(args, "max_stats"), false);
                // forced: the hire path (faction assignment, council list, recruit-pool
                // removal) without the recruitment cost or the hire milestones.
                faction.AddAvailableCouncilor(councilor, true);
            }
            catch (Exception)
            {
                DiscardState<TICouncilorState>(councilor);
                throw;
            }

            var o = new JObject();
            o["id"] = (int)councilor.ID;
            o["name"] = StateName(councilor);
            Put(o, "job", delegate
            {
                TICouncilorTypeTemplate t = councilor.typeTemplate;
                return t != null ? (JToken)new JValue(t.dataName) : JValue.CreateNull();
            });
            o["faction"] = Describe(faction);
            Put(o, "location", delegate
            {
                TIGameState at = councilor.location;
                return at != null ? Describe(at) : (JToken)JValue.CreateNull();
            });
            Put(o, "emptySlots", delegate { return (JToken)faction.emptyCouncilorSlots; });
            return o;
        }

        #endregion

        #region spawn.alien_site

        // Ten years, as the ceiling on a landing's countdown. This one is ours:
        // the engine has no maximum, only the default duration a missing 'days'
        // asks for, and anything past a decade is a request the caller meant to
        // be a number of days and got wrong.
        const float MaxLandingDays = 3650f;

        // The xenoforming ceiling, read from the engine rather than copied.
        // TIRegionXenoformingState's static constructor sets spawnArmyValue to
        // 100, and DailyXenoformingGrowth spends the level back down from there
        // (IL_00a6-IL_00cc), so no level in play climbs past it. Guarded, since a
        // ceiling this verb cannot read is no reason to refuse a sane request.
        static float MaxXenoformingLevel()
        {
            return Safe<float>(
                delegate { return TIRegionXenoformingState.spawnArmyValue; }, 100f);
        }

        // Each region carries one holder state per alien site kind, all five created at
        // campaign init and hung on the region. This verb activates the holder that is
        // already there; creating another one would leave the region pointing at the old
        // one and the new state orphaned in the manager.
        static JToken SpawnAlienSite(JObject args)
        {
            TIRegionState region = ArgState<TIRegionState>(args, "region", "region_name");
            string kind = Str(args, "kind");
            if (string.IsNullOrEmpty(kind)) throw new VerbError("missing arg 'kind'");

            var o = new JObject();
            o["region"] = Describe(region);
            var state = new JObject();

            if (Same(kind, "facility"))
            {
                TIRegionAlienFacilityState holder = Holder<TIRegionAlienFacilityState>(
                    delegate { return region.alienFacility; }, "alien facility");
                if (Safe<bool>(delegate { return holder.built; }, false))
                    throw new VerbError("region already has an alien facility");
                holder.BuildFacility();
                o["holder"] = Describe(holder);
                Put(state, "built", delegate { return (JToken)holder.built; });
            }
            else if (Same(kind, "crashdown"))
            {
                TIRegionUFOCrashdownState holder = Holder<TIRegionUFOCrashdownState>(
                    delegate { return region.alienCrashdown; }, "alien crashdown");
                holder.TriggerCrashdown(Flag(args, "first"));
                o["holder"] = Describe(holder);
                Put(state, "crashdownPresent",
                    delegate { return (JToken)holder.crashdownPresent; });
            }
            else if (Same(kind, "landing"))
            {
                TIRegionUFOLandingState holder = Holder<TIRegionUFOLandingState>(
                    delegate { return region.alienLanding; }, "alien landing");
                // Each call adds another army-deployment listener to the same holder,
                // so triggering on a live landing deploys twice.
                if (Safe<bool>(delegate { return holder.landingPresent; }, false))
                    throw new VerbError("region already has an alien landing");
                holder.TriggerLanding(Bounded("days", Float(args, "days", -1f),
                    float.NegativeInfinity, MaxLandingDays,
                    "the landing is marked present and given HP before 'days' is"
                    + " read, so a duration the engine's date arithmetic refuses"
                    + " leaves a landing that never deploys; the engine's own"
                    + " duration is daysToFieldArmyFromUFO (32), and 0 or less"
                    + " asks for it"));
                o["holder"] = Describe(holder);
                Put(state, "landingPresent",
                    delegate { return (JToken)holder.landingPresent; });
            }
            else if (Same(kind, "xenoforming"))
            {
                TIRegionXenoformingState holder = Holder<TIRegionXenoformingState>(
                    delegate { return region.xenoforming; }, "xenoforming");
                float ceiling = MaxXenoformingLevel();
                float level = Bounded("level", RequiredFloat(args, "level"),
                    float.NegativeInfinity, ceiling,
                    "the engine's own ceiling is spawnArmyValue ("
                    + FloatText(ceiling) + "), where xenoforming growth spends the"
                    + " level back down by spawning megafauna; above it, SetIntel"
                    + " dates a faction's reading with AddDays(-level * 12) and"
                    + " throws past DateTime.MinValue on the next daily tick");
                holder.SetXenoformingLevel(level);
                o["holder"] = Describe(holder);
                Put(state, "xenoformingLevel",
                    delegate { return Num(holder.xenoformingLevel); });
                // The engine's own "is there xenoforming here": level > 0. It is
                // what every caller wants after a removal, and deriving it from the
                // level in the client would be guessing at the same comparison.
                Put(state, "extant", delegate { return (JToken)holder.Extant(); });
            }
            else
            {
                throw new VerbError("unknown alien site kind '" + kind
                    + "' (facility, crashdown, landing, xenoforming)");
            }

            o["kind"] = kind.ToLowerInvariant();
            o["state"] = state;
            return o;
        }

        static T Holder<T>(Func<T> read, string what) where T : TIGameState
        {
            T holder = Safe<T>(read, null);
            if (holder == null) throw new VerbError("region has no " + what + " state");
            return holder;
        }

        #endregion

        #region Shared helpers

        // States listed in an ambiguity refusal before the list is cut.
        const int MaxStatesListed = 12;

        // A state by id or by name, for the verbs that take either. The id is the
        // addressable form and stays the primary one, but nothing hands a caller a
        // region id and few callers hold a nation id: the interesting states are
        // named ones, and finding the id first meant a query.state walk before every
        // call. 'nameKey' is the argument carrying the name, and is 'key' itself when
        // one argument takes both (a whole number is then the id). The name matches
        // templateName (the dataName) first and the display name second, so a
        // dataName always wins over a localized string that happens to collide.
        static T ArgState<T>(JObject args, string key, string nameKey)
            where T : TIGameState
        {
            JToken t = args != null ? args[key] : null;
            string wanted = null;
            if (t != null && t.Type != JTokenType.Null)
            {
                // A separate name argument means this one is the id and nothing else.
                if (nameKey != key) return Arg<T>(args, key);
                int id;
                wanted = (t.Type == JTokenType.String ? (string)t : t.ToString()).Trim();
                if (int.TryParse(wanted, NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out id))
                    return ById<T>(id);
            }
            else if (nameKey != key)
            {
                wanted = Str(args, nameKey);
                if (wanted != null) wanted = wanted.Trim();
            }
            if (string.IsNullOrEmpty(wanted))
                throw new VerbError("missing arg '" + key + "': a " + typeof(T).Name
                    + " id, or " + (nameKey == key ? "its" : "'" + nameKey + "' as its")
                    + " dataName or display name");

            var byData = new List<T>();
            var byDisplay = new List<T>();
            foreach (T state in GameStateManager.IterateByClass<T>(false))
            {
                if (state == null) continue;
                if (Same(state.templateName, wanted)) byData.Add(state);
                else if (Same(StateName(state), wanted)
                         || Same(state.displayName, wanted)) byDisplay.Add(state);
            }
            List<T> found = byData.Count > 0 ? byData : byDisplay;
            if (found.Count == 1) return found[0];
            if (found.Count == 0)
                throw new VerbError("no " + typeof(T).Name
                    + " whose dataName or display name is '" + wanted + "'");
            // Two states can share a display name, and picking one would silently
            // act on the wrong country.
            var rows = new List<string>();
            for (int i = 0; i < found.Count && i < MaxStatesListed; i++)
                rows.Add((int)found[i].ID + "=" + found[i].templateName);
            throw new VerbError("'" + wanted + "' matches " + found.Count + " "
                + typeof(T).Name + " (" + string.Join(", ", rows.ToArray())
                + (found.Count > MaxStatesListed ? ", ..." : "")
                + "); pass '" + key + "' as a state id instead");
        }

        // Rollback for a state that was registered and then failed to initialize.
        // Archived without the event, as spawn.fleet does: the state was never
        // announced to anything, so nothing wants to hear it go.
        static void DiscardState<T>(T state) where T : TIGameState
        {
            if (state == null) return;
            try { state.ArchiveState(false); }
            catch (Exception) { }
            try { GameStateManager.RemoveGameState<T>(state.ID, false); }
            catch (Exception) { }
        }

        // Flag() answers false for an absent key. These verbs default to completing what
        // they spawn, so an absent key has to be able to mean true.
        static bool Bool(JObject args, string key, bool fallback)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return fallback;
            return Flag(args, key);
        }

        // A present but non-numeric value is an error rather than a silent fallback:
        // a mistyped strength that quietly became 1.0 would look like a passing test.
        //
        // NaN and the infinities are refused here, for every caller at once, because
        // nothing downstream stops them. They pass a `<= 0` guard; SetXenoformingLevel
        // floors its argument with Mathf.Max(0f, level) (IL_000e-IL_001f), which is
        // `a > b ? a : b` and so answers NaN for NaN; TriggerLanding sets
        // landingPresent and currentHP (IL_0000-IL_000d) before it looks at the
        // argument at all and then tests it with bgt.un.s (IL_0068), an UNORDERED
        // branch that sends NaN down the override path into AddDays; and NewArmy
        // stores strength raw. Every one of them lands in campaign state that no
        // later validation can take back out.
        //
        // The token type is tested before the value is read, for the same reason
        // Int() tests it: the explicit cast on a JToken converts rather than
        // checks. It reads true as 1 and runs Convert.ToSingle over a string, so a
        // thousands separator or a decimal comma ("1,5") becomes a number nobody
        // asked for -- and these arguments are the ones that go straight into
        // campaign state.
        static float Float(JObject args, string key, float fallback)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return fallback;
            if (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)
                throw new VerbError("arg '" + key + "' must be a number, got "
                    + t.Type);
            // Right type, wrong size needs no arm of its own: an integer literal too
            // large for a long is held as a BigInteger, whose conversion saturates
            // to an infinity rather than throwing, and a float literal past the
            // range does the same. Both land on the finiteness test below.
            float value = (float)t;
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new VerbError("arg '" + key + "' must be a finite number");
            return value;
        }

        static float RequiredFloat(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null)
                throw new VerbError("missing arg '" + key + "'");
            return Float(args, key, 0f);
        }

        // Refuses a finite number the engine has no path back out of, before the
        // engine call rather than after it. A fixture skips the play-legality
        // around a mutator; where the engine's own callers could never produce a
        // value, nothing downstream is written to survive one, and the damage
        // lands in campaign state that no later validation removes.
        //
        // 'above' is exclusive and 'max' inclusive, which is the shape these
        // arguments take: a fraction that has to be positive, or a magnitude with
        // a ceiling and a meaningful sentinel below zero. Pass
        // float.NegativeInfinity for 'above' where the engine floors the value
        // itself, and the refusal then names only the bound that bites.
        //
        // Takes a value Float() has already returned, so NaN and the infinities
        // are gone by here and these comparisons mean what they say: NaN answers
        // false against both bounds and would otherwise walk straight through.
        //
        // 'why' is the engine fact that sets the bound. Without it a refusal
        // reads as this verb being fussy about a number the engine would have
        // taken.
        static float Bounded(string key, float value, float above, float max, string why)
        {
            if (value > above && value <= max) return value;
            string range = float.IsNegativeInfinity(above)
                ? "at most " + FloatText(max)
                : "greater than " + FloatText(above) + " and at most " + FloatText(max);
            throw new VerbError("arg '" + key + "' must be " + range + ": " + why
                + "; asked for " + FloatText(value));
        }

        static string FloatText(float value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
