using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.SpaceCombat.UI;

namespace TerraInvictaMCP
{
    // spawn.fleet, combat.start, combat.status, combat.autoresolve, fleet.bombard,
    // fleet.land, fleet.transfer.
    public static partial class Verbs
    {
        const int MaxSpawnShips = 20;

        // An armed autoresolve that stops making progress disarms rather than sitting
        // forever; a minute of frames is far longer than any observed simulation.
        const int AutoresolveFrameBudget = 3600;

        static JToken SpawnFleet(JObject args)
        {
            TIFactionState faction = Arg<TIFactionState>(args, "faction");
            TIGameState location = Location(Int(args, "location"));
            int count = Int(args, "ships");
            if (count < 1 || count > MaxSpawnShips)
                throw new VerbError("arg 'ships' must be 1.." + MaxSpawnShips);

            TISpaceShipTemplate design = Design(faction, Str(args, "design"));
            // CompleteShipInitialization dereferences designingFaction, which resolves
            // lazily from the template's factionName and is null when that name is not a
            // faction. Settle it before anything is created.
            TIFactionState designer = design.designingFaction;
            if (designer == null)
                throw new VerbError("design '" + design.dataName + "' has no designing faction");

            // Every ship is registered in GameStateManager the moment it is created, so a
            // failure part way through the batch would leave the rest behind to ride into
            // the next save.
            var ships = new List<TISpaceShipState>();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var ship = design.CreateGameState() as TISpaceShipState;
                    if (ship == null) throw new VerbError("design did not create a ship state");
                    ships.Add(ship);
                    ship.InitWithTemplate(design);
                    ship.CompleteShipInitialization();
                    string name = null;
                    try { name = TISpaceAssetState.GetRandomAssetName(ship, faction); }
                    catch (Exception) { }
                    ship.SetDisplayName(!string.IsNullOrEmpty(name) ? name : design.displayName);
                }
            }
            catch (Exception)
            {
                for (int i = 0; i < ships.Count; i++)
                    DiscardState<TISpaceShipState>(ships[i]);
                throw;
            }

            TISpaceFleetState fleet = TISpaceFleetState.CreateAtRunTime(
                faction, ships, location, null, null, false, false, null);
            if (fleet == null) throw new VerbError("fleet creation returned null");

            var o = new JObject();
            o["id"] = (int)fleet.ID;
            o["name"] = StateName(fleet);
            o["faction"] = Describe(faction);
            o["location"] = Describe(location);
            o["design"] = DescribeDesign(design);
            var a = new JArray();
            for (int i = 0; i < ships.Count; i++) a.Add(Describe(ships[i]));
            o["ships"] = a;
            return o;
        }

        // CreateAtRunTime asks the location exactly two questions when parentFleet is
        // null, which is what this verb passes: location.isHabState at IL_00e9 and
        // location.isOrbitState at IL_0112. A state that answers neither falls through to
        // IL_0143 with no orbit assumed at all, and the half-placed fleet then reaches
        // SpaceObjectController.UpdateOrbitComponentForAsset (IL_0338), which asks it for
        // its barycenter -- null for a fleet with no dockedLocation, no transfer and no
        // orbit -- and dereferences it at IL_0166. That is an engine
        // NullReferenceException raised after the ships and the fleet are registered.
        //
        // The hab-site landing at IL_01d9 and the fleet join at IL_0262 are on the other
        // side of the IL_00dd branch, reached only when a parent fleet is passed, and both
        // sit under an unguarded location.ref_fleet dereference at IL_01cd. They are out
        // of reach from here, so a hab site and a fleet are refused by name with the path
        // that does work.
        static TIGameState Location(int id)
        {
            TIGameState state = ById<TIGameState>(id);
            if (Safe<bool>(delegate { return state.isOrbitState; }, false)) return state;
            if (Safe<bool>(delegate { return state.isHabState; }, false)) return state;

            string what = StateName(state) + " (" + id + ", " + state.GetType().Name + ")";
            if (Safe<bool>(delegate { return state.isHabSiteState; }, false))
                throw new VerbError(what + " is a hab site. CreateAtRunTime lands a fleet "
                    + "on one only when it is given a parent fleet, which this verb never "
                    + "passes, and dereferences a null on the way there. Spawn into an "
                    + "orbit at the site's body and land the fleet with "
                    + "raw cmd=fleet.land args={fleet, site}");
            if (Safe<bool>(delegate { return state.isSpaceFleetState; }, false))
                throw new VerbError(what + " is a fleet. Spawn at the orbit it is in "
                    + "instead, which is where CreateAtRunTime will place the new ships");
            throw new VerbError(what + " is neither an orbit nor a hab, and those are the "
                + "only two locations CreateAtRunTime places a new fleet at");
        }

        // Default ranking: most weapon mounts, then the newest refit, then data name for a
        // stable answer. Both entry lists are plain fields, so ranking runs no game code.
        static TISpaceShipTemplate Design(TIFactionState faction, string wanted)
        {
            List<TISpaceShipTemplate> designs = faction.shipDesigns;
            if (designs == null || designs.Count == 0)
                throw new VerbError("faction has no ship designs");

            if (!string.IsNullOrEmpty(wanted))
            {
                // A data name is unique, so a match on one ends the search. A display
                // name is not: the designer's class-name field refuses a name another
                // design already holds, but that check lives in the UI, and nothing
                // enforces it on a design that arrived any other way. Two designs of one
                // name used to resolve to whichever came first in the faction's list,
                // which is a design.delete or a spawn.fleet against a design the caller
                // did not name.
                for (int i = 0; i < designs.Count; i++)
                {
                    TISpaceShipTemplate d = designs[i];
                    if (d == null) continue;
                    if (Same(d.dataName, wanted)) return d;
                }

                var named = new List<TISpaceShipTemplate>();
                for (int i = 0; i < designs.Count; i++)
                {
                    TISpaceShipTemplate d = designs[i];
                    if (d == null) continue;
                    if (Same(DesignName(d), wanted)) named.Add(d);
                }
                if (named.Count == 1) return named[0];
                if (named.Count > 1)
                {
                    var dataNames = new List<string>();
                    for (int i = 0; i < named.Count; i++) dataNames.Add(named[i].dataName);
                    throw new VerbError("faction has " + named.Count + " designs whose "
                        + "display name is '" + wanted + "': "
                        + string.Join(", ", dataNames.ToArray())
                        + ". Name one of those data names; choosing one here would act "
                        + "on a design that was not asked for");
                }
                throw new VerbError("faction has no design named '" + wanted + "'");
            }

            TISpaceShipTemplate best = null;
            int bestWeapons = -1;
            int bestRefit = -1;
            for (int i = 0; i < designs.Count; i++)
            {
                TISpaceShipTemplate d = designs[i];
                if (d == null) continue;
                int weapons = Weapons(d);
                int refit = 0;
                try { refit = d.refitIteration; }
                catch (Exception) { }
                if (best != null)
                {
                    if (weapons < bestWeapons) continue;
                    if (weapons == bestWeapons && refit < bestRefit) continue;
                    if (weapons == bestWeapons && refit == bestRefit
                        && string.CompareOrdinal(d.dataName, best.dataName) >= 0) continue;
                }
                best = d;
                bestWeapons = weapons;
                bestRefit = refit;
            }
            if (best == null) throw new VerbError("faction has no usable ship design");
            return best;
        }

        static int Weapons(TISpaceShipTemplate d)
        {
            int n = 0;
            try { if (d.noseWeaponTemplateEntries != null) n += d.noseWeaponTemplateEntries.Count; }
            catch (Exception) { }
            try { if (d.hullWeaponTemplateEntries != null) n += d.hullWeaponTemplateEntries.Count; }
            catch (Exception) { }
            return n;
        }

        static string DesignName(TISpaceShipTemplate d)
        {
            return Safe<string>(delegate { return d.displayName; }, null);
        }

        static bool Same(string a, string b)
        {
            return a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static JToken DescribeDesign(TISpaceShipTemplate d)
        {
            var o = new JObject();
            o["dataName"] = d.dataName;
            o["name"] = DesignName(d);
            o["weapons"] = Weapons(d);
            return o;
        }

        static JToken CombatStart(JObject args)
        {
            TISpaceFleetState attacker = Arg<TISpaceFleetState>(args, "attacker");
            TISpaceFleetState defender = Arg<TISpaceFleetState>(args, "defender");
            if (ReferenceEquals(attacker, defender)) throw new VerbError("a fleet cannot attack itself");

            // InitiateCombat does not check that the two fleets are hostile, and
            // InitializeCombat then adds one assets entry and one allowedStances entry per
            // faction with no duplicate guard. One faction on both sides throws on the
            // second add, inside SimulationTimeTick and before the combat is archived, so
            // it re-throws every frame and the strategy clock never runs again.
            TIFactionState attackerFaction = FleetFaction(attacker, "attacker");
            TIFactionState defenderFaction = FleetFaction(defender, "defender");
            if (ReferenceEquals(attackerFaction, defenderFaction))
                throw new VerbError("both fleets belong to " + StateName(attackerFaction)
                    + "; a faction cannot fight itself");

            TIHabState hab = null;
            int habId = OptionalInt(args, "hab");
            if (habId >= 0) hab = ById<TIHabState>(habId);

            bool started = attacker.InitiateCombat(defender, hab, false);

            var o = new JObject();
            o["started"] = started;
            // A false return is not a failure: one combat runs at a time globally, and the
            // request is parked on the fleet until the current one clears.
            o["queued"] = !started;
            TISpaceCombatState combat = FleetCombat(attacker);
            if (combat == null) combat = FleetCombat(defender);
            o["combat"] = combat != null ? Describe(combat) : null;
            // InitiateCombat only CACHES the two fleets and the hab; the engine
            // promotes the combat a frame or more later and archives it outright if
            // the cached assets fail its gate. Reporting the gate here is what turns
            // that silent disappearance into an answer at the call that caused it.
            // See PromotionGate.
            o["promotion"] = PromotionGate(combat);
            return o;
        }

        static TIFactionState FleetFaction(TISpaceFleetState fleet, string role)
        {
            TIFactionState faction = null;
            try { faction = fleet.faction; }
            catch (Exception) { }
            if (faction == null) throw new VerbError(role + " fleet has no faction");
            return faction;
        }

        static TISpaceCombatState FleetCombat(TISpaceFleetState fleet)
        {
            try
            {
                TISpaceCombatState combat = fleet.combatState;
                return combat != null && !combat.archived ? combat : null;
            }
            catch (Exception) { return null; }
        }

        static JToken CombatStatus(JObject args)
        {
            var o = new JObject();
            TISpaceCombatState active = ActiveCombat();
            o["active"] = active != null ? DescribeCombat(active) : null;

            var pending = new JArray();
            foreach (TISpaceCombatState combat in GameStateManager.IterateByClass<TISpaceCombatState>(false))
            {
                if (combat == null || combat.archived) continue;
                if (ReferenceEquals(combat, active)) continue;
                pending.Add(DescribeCombat(combat));
            }
            o["pending"] = pending;

            // Same field query.time reports: an un-archived combat freezes the strategy
            // clock whether or not the speed index changed.
            var manager = PavonisInteractive.TerraInvicta.Systems.GameTime.GameTimeManager.Singleton;
            o["blocked"] = manager != null ? Blocked(manager) : JValue.CreateNull();
            o["autoresolve"] = AutoresolveStatus(active);
            return o;
        }

        static TISpaceCombatState ActiveCombat()
        {
            try
            {
                TISpaceCombatState combat = TISpaceCombatState.CurrentActiveCombat;
                return combat != null && !combat.archived ? combat : null;
            }
            catch (Exception) { return null; }
        }

        static JToken DescribeCombat(TISpaceCombatState combat)
        {
            var o = new JObject();
            o["id"] = (int)combat.ID;
            o["name"] = StateName(combat);
            o["archived"] = combat.archived;
            Put(o, "initialized", delegate { return (JToken)combat.initialized; });
            Put(o, "active", delegate { return (JToken)combat.active; });
            Put(o, "autoresolve", delegate { return (JToken)combat.autoresolve; });
            Put(o, "autoresolving", delegate { return (JToken)combat.autoresolving; });
            Put(o, "mayRejectAutoresolve", delegate { return (JToken)combat.mayRejectAutoresolve; });
            Put(o, "combatOccurs", delegate { return (JToken)combat.combatOccurs; });
            Put(o, "requiresBidding", delegate { return (JToken)combat.requiresBidding; });
            Put(o, "stancesSelected", delegate { return (JToken)combat.HaveStancesBeenSelected; });
            Put(o, "bidsSubmitted", delegate { return (JToken)combat.HaveBidsBeenSubmitted; });
            Put(o, "simulated", delegate { return (JToken)(combat.SimulatedCombat != null); });

            bool playerInvolved = false;
            var participants = new JArray();
            TIFactionState[] factions = CombatFactions(combat);
            for (int i = 0; i < factions.Length; i++)
            {
                TIFactionState f = factions[i];
                if (f == null) continue;
                var p = new JObject();
                p["faction"] = Describe(f);
                bool ai = IsAI(f);
                p["ai"] = ai;
                bool activePlayer = IsActivePlayer(f);
                p["activePlayer"] = activePlayer;
                if (activePlayer) playerInvolved = true;
                CombatStance? stance = StanceOf(combat, f);
                p["stance"] = stance != null
                    ? (JToken)new JValue(stance.Value.ToString()) : JValue.CreateNull();
                p["bid"] = BidOf(combat, f);
                participants.Add(p);
            }
            o["participants"] = participants;
            o["playerInvolved"] = playerInvolved;

            var fleets = new JArray();
            try
            {
                TISpaceFleetState[] list = combat.fleets;
                if (list != null)
                {
                    for (int i = 0; i < list.Length; i++)
                        fleets.Add(list[i] != null ? Describe(list[i]) : (JToken)JValue.CreateNull());
                }
            }
            catch (Exception) { }
            o["fleets"] = fleets;
            Put(o, "hab", delegate { return combat.hab != null ? Describe(combat.hab) : (JToken)null; });
            o["promotion"] = PromotionGate(combat);
            return o;
        }

        // The assets combat.start cached, which is the only thing a combat has
        // before the engine promotes it.
        //
        // `fleets` and `hab` above are EMPTY for a combat that is perfectly healthy and
        // simply has not been promoted yet, and a status poll taken right after
        // combat.start is exactly that case. TISpaceFleetState.InitiateCombat ends at
        // CacheCombatAssets(this, targetFleet, hab) (IL_0651) and returns; the real
        // fields -- fleets[0], fleets[1], factions and hab -- are written only by
        // TISpaceCombatState.InitializeCombat, whose one caller is
        // TISpaceCombatState.StartCombatFromStrategyLayer, run one combat per frame from
        // SimulationTimeTick.OnUpdate whatever the clock speed and whether or not it is
        // paused. So `fleets: [null, null], hab: null` on an uninitialized combat means
        // "not promoted yet", not "no second fleet and no hab".
        static JToken PromotionGate(TISpaceCombatState combat)
        {
            if (combat == null) return JValue.CreateNull();
            var o = new JObject();
            bool initialized = Initialized(combat);
            o["initialized"] = initialized;

            TISpaceFleetState f1 = Safe<TISpaceFleetState>(
                delegate { return combat.cachedFleet1; }, null);
            TISpaceFleetState f2 = Safe<TISpaceFleetState>(
                delegate { return combat.cachedFleet2; }, null);
            TIHabState hab = Safe<TIHabState>(delegate { return combat.cachedHab; }, null);
            bool allowNoAttacker = Safe<bool>(
                delegate { return combat.allowNoAttackingFleetAtInitialization; }, false);

            o["cachedFleet1"] = CachedFleet(f1);
            o["cachedFleet2"] = CachedFleet(f2);
            o["cachedHab"] = hab != null ? Describe(hab) : (JToken)JValue.CreateNull();
            o["allowNoAttackingFleetAtInitialization"] = allowNoAttacker;
            return o;
        }

        static JToken CachedFleet(TISpaceFleetState fleet)
        {
            if (fleet == null) return JValue.CreateNull();
            JToken described = Safe<JToken>(delegate { return Describe(fleet); }, null);
            var o = described as JObject;
            if (o == null) o = new JObject();
            o["valid"] = Safe<JToken>(
                delegate { return (JToken)TIGameState.Valid(fleet); }, JValue.CreateNull());
            o["ships"] = ShipCount(fleet);
            return o;
        }

        // -1 for a fleet whose ship list cannot be read at all, which is a different
        // thing from an empty one and is what the gate's own null dereference would be.
        static int ShipCount(TISpaceFleetState fleet)
        {
            if (fleet == null) return -1;
            return Safe<int>(delegate
            {
                List<TISpaceShipState> ships = fleet.ships;
                return ships != null ? ships.Count : -1;
            }, -1);
        }

        static TIFactionState[] CombatFactions(TISpaceCombatState combat)
        {
            try
            {
                TIFactionState[] factions = combat.factions;
                return factions != null ? factions : new TIFactionState[0];
            }
            catch (Exception) { return new TIFactionState[0]; }
        }

        // Null when the faction has no entry in the stance dictionary at all.
        static CombatStance? StanceOf(TISpaceCombatState combat, TIFactionState faction)
        {
            try
            {
                Dictionary<TIFactionState, CombatStance> stances = combat.stances;
                CombatStance stance;
                if (stances != null && stances.TryGetValue(faction, out stance)) return stance;
            }
            catch (Exception) { }
            return null;
        }

        // NotYetSet is a placeholder the game writes into the dictionary, and its own
        // HaveStancesBeenSelected rejects it, so presence alone does not mean submitted.
        static bool StanceSet(TISpaceCombatState combat, TIFactionState faction)
        {
            CombatStance? stance = StanceOf(combat, faction);
            return stance != null && stance.Value != CombatStance.NotYetSet;
        }

        // A bid of zero is a real bid, so bids go by presence, which is what a
        // non-null BidOf is: Num() never returns null, so a bid reads as a bid.
        static bool HasBid(TISpaceCombatState combat, TIFactionState faction)
        {
            return BidOf(combat, faction).Type != JTokenType.Null;
        }

        static JToken BidOf(TISpaceCombatState combat, TIFactionState faction)
        {
            try
            {
                Dictionary<TIFactionState, float> bids = combat.bids_kps;
                float bid;
                if (bids != null && bids.TryGetValue(faction, out bid)) return Num(bid);
            }
            catch (Exception) { }
            return JValue.CreateNull();
        }

        static bool IsAI(TIFactionState faction)
        {
            return Safe<bool>(delegate
            {
                TIPlayerState player = faction.player;
                return player == null || player.isAI;
            }, true);
        }

        static bool IsActivePlayer(TIFactionState faction)
        {
            return Safe<bool>(delegate { return faction.isActivePlayer; }, false);
        }

        static TIFactionState ActivePlayer()
        {
            try
            {
                GameControl control = GameControl.control;
                return control != null ? control.activePlayer : null;
            }
            catch (Exception) { return null; }
        }

        // Armed autoresolve. Held as an int id, never a game object, so a scene reload
        // between frames leaves nothing stale behind.
        enum AutoPhase { Idle, Settling, Firing, Simulating, Closing }

        static int autoCombatId;
        static AutoPhase autoPhase;
        static int autoFrames;
        static string autoError;
        static string autoNote;
        // -1 = no override; otherwise a CombatStance value requested by the client.
        static int autoStance = -1;
        // Cached across the frames of one armed autoresolve: FindObjectOfType walks
        // the scene, and the tick would otherwise pay that walk every frame.
        static PrecombatController autoPrecombat;

        // How a failure answers "may this be armed again on the same combat".
        //
        //   Transient  the machine was ahead of the engine and one more arm may work
        //   Stable     nothing a re-arm does changes the condition
        //   DeadEnd    the combat is gone and its canvas is still up, so no arm helps
        //   Unknown    an exception the machine did not classify
        enum AutoFail { Unknown, Transient, Stable, DeadEnd }

        // A failure carrying its class, so the arming decision reads a field rather
        // than matching on message text. Message wording is the part most likely to
        // be edited, and a classifier keyed on it fails silently when it is.
        class AutoresolveFailure : VerbError
        {
            public readonly AutoFail kind;

            public AutoresolveFailure(AutoFail kind, string message) : base(message)
            {
                this.kind = kind;
            }
        }

        // What the last failure on a combat left behind. Disarm clears the working
        // machine; these outlive it deliberately, because the question they answer is
        // about the combat and not about the run, and the disarm is exactly the event
        // that would otherwise erase the answer. They are cleared only when a different
        // combat is armed.
        //
        // autoFiredSelect and autoFiredAccept, rather than the phase alone: the phase
        // is ambiguous at Simulating, which is reached BOTH by FireStep advancing before
        // it calls AutoresolveSelected and by an arm on a combat that was already
        // simulating when the caller found it. Only the first of those fired a one-shot.
        // The phase is still carried, because it is what a report should name and what
        // says how far the machine got.
        static int autoHistoryCombatId = -1;
        static AutoPhase autoReached;
        static AutoFail autoErrorKind;
        static int autoAttempts;
        // AutoresolveSelected has been called on this combat. It raises PrecombatComplete,
        // whose listener chain runs the simulation; a second one runs it again.
        static bool autoFiredSelect;
        // OnAcceptAutoresolveSelected has been called on this combat. It reaches
        // TISpaceCombatState.ApplySimulatedCombat, which writes damage into the real
        // states. A second call applies the same battle twice.
        static bool autoFiredAccept;

        // Arms allowed on one combat before the machine stops trying. The second arm is
        // the retry a transient failure is worth; a third would be a loop.
        const int MaxAutoresolveAttempts = 2;

        static JToken CombatAutoresolve(JObject args)
        {
            TISpaceCombatState combat = TargetCombat(args);
            if (combat == null) throw new VerbError("no combat");

            if (autoPhase != AutoPhase.Idle && autoCombatId != (int)combat.ID)
                throw new VerbError("already resolving combat " + autoCombatId);

            // factions is only filled by InitializeCombat, so before promotion every
            // combat looks AI-only. Arm regardless and settle the question in SettleStep,
            // which already waits for initialized.
            if (Initialized(combat) && !PlayerInvolved(combat))
            {
                // An engagement is the one state in which "no active player in this
                // combat" is a lie. AiControl's postfix makes the engaged faction report
                // isActivePlayer false, which is what keeps the AI's strategic reads
                // honest, but PrecombatController takes its faction from
                // CanvasControllerBase.activePlayer, which is copied from GameControl and
                // is untouched by that patch. So the canvas comes up, freezes the clock
                // through SimulationTimeTick, and waits for a button nobody presses --
                // while this branch reports the combat as resolving on its own and the
                // caller re-asks every poll forever. Named instead.
                if (EngagedIn(combat))
                    throw new VerbError("combat " + (int)combat.ID + " involves the "
                        + "faction handed to the AI by ai.control, which reports as not "
                        + "the active player while engaged; nothing here can arm on it, "
                        + "and the engine's precombat canvas still comes up and still "
                        + "freezes the clock. Release the engagement (ai.control "
                        + "action=release) and autoresolve, or answer the screen with "
                        + "combat.precombat");
                // Both sides are AI: the precombat listeners answer their own stance
                // prompts, OnPrecombatComplete runs Autoresolve, and the simulation
                // callback applies the result. Nothing for the bridge to do.
                var auto = new JObject();
                auto["armed"] = false;
                auto["resolving"] = "automatic";
                auto["combat"] = Describe(combat);
                return auto;
            }

            if (autoPhase == AutoPhase.Idle)
            {
                RefuseUnsafeRearm(combat);
                if (autoHistoryCombatId != (int)combat.ID)
                {
                    // A different combat: the history describes the old one and would
                    // otherwise refuse arms on this one, or count its attempts.
                    autoHistoryCombatId = (int)combat.ID;
                    autoReached = AutoPhase.Idle;
                    autoErrorKind = AutoFail.Unknown;
                    autoAttempts = 0;
                    autoFiredSelect = false;
                    autoFiredAccept = false;
                }
                autoAttempts++;
                autoCombatId = (int)combat.ID;
                autoFrames = 0;
                autoError = null;
                autoNote = null;
                autoStance = ParseStance(args);
                // A combat already carrying a simulation only needs the apply step.
                autoPhase = Simulated(combat) ? AutoPhase.Simulating : AutoPhase.Settling;
                Reached(autoPhase);
            }
            return AutoresolveStatus(combat);
        }

        // Whether arming again on this combat is allowed at all.
        //
        // A failed tick disarms, so the next poll sees a combat present and nothing
        // armed and calls this verb again, which re-enters the machine from the start.
        // That defeats the two one-shot guards the steps rely on: they advance the phase
        // before making a call that must not repeat, and an arm resets the phase.
        // Re-running AutoresolveSelected raises a second PrecombatComplete; re-running
        // OnAcceptAutoresolveSelected reaches ApplySimulatedCombat, which writes combat
        // damage into the real game states. That is corruption rather than a stall, so
        // the refusals here come before anything else the arm would do.
        static void RefuseUnsafeRearm(TISpaceCombatState combat)
        {
            string refusal = UnsafeRearmReason((int)combat.ID);
            if (refusal != null) throw new VerbError(refusal);
        }

        // The refusal itself, as a string rather than a throw, so the reported
        // `retryable` is the very decision the arm makes instead of a second copy of
        // it. Written twice, the two disagreed twice: on the attempt budget, which
        // only counts against a combat that recorded an error, and on scope, since
        // the record refuses arms on the combat it names and on no other. Null means
        // an arm is allowed.
        //
        // Keyed on the id, because a caller holding the combat state and a status
        // reply describing a combat that may already be archived and removed both
        // need the same answer.
        static string UnsafeRearmReason(int combatId)
        {
            if (autoHistoryCombatId < 0 || autoHistoryCombatId != combatId) return null;

            string what = "combat " + combatId + " reached phase "
                + autoReached.ToString().ToLowerInvariant()
                + (autoError != null ? " and failed with: " + autoError : "");

            string reason = null;
            if (autoFiredAccept)
                reason = "OnAcceptAutoresolveSelected has already fired on it, and that "
                    + "reaches ApplySimulatedCombat, which writes damage into the real "
                    + "states: arming again would apply the same battle twice";
            else if (autoFiredSelect)
                reason = "AutoresolveSelected has already fired on it, raising "
                    + "PrecombatComplete, and arming again would raise a second one";
            else if (autoErrorKind == AutoFail.Stable)
                reason = "nothing a retry does changes that condition";
            else if (autoErrorKind == AutoFail.DeadEnd)
                reason = "the combat is gone and the precombat canvas is still up "
                    + "holding the clock; no arm reaches it";
            // Gated on a recorded error: attempts alone are not failures. The budget is a
            // count, so two failures alternating between two messages still trip it.
            else if (autoAttempts >= MaxAutoresolveAttempts && autoError != null)
                reason = "that is the retry budget, over " + autoAttempts + " attempts";
            if (reason == null) return null;

            // The way out is named as a thing to CHECK, not as a thing that will work:
            // both one-shots run through EndPrecombatInteraction, which hides the canvas,
            // so its close and cancel buttons may already be gone.
            return what + ". " + reason + ". Inspect with combat.status, and with "
                + "combat.precombat action=status, which reports whether the canvas is up "
                + "and which of its buttons are live; if none are, it needs a person";
        }

        // Records the furthest phase this combat's machine has entered. Called on every
        // advance, so a disarm cannot lose it.
        static void Reached(AutoPhase phase)
        {
            if (phase > autoReached) autoReached = phase;
        }

        // True when ai.control holds the faction and that faction is in this combat.
        static bool EngagedIn(TISpaceCombatState combat)
        {
            if (!AiControl.Engaged) return false;
            TIFactionState engaged = AiControl.EngagedFaction;
            if (engaged == null) return false;
            TIFactionState[] factions = CombatFactions(combat);
            for (int i = 0; i < factions.Length; i++)
            {
                if (ReferenceEquals(factions[i], engaged)) return true;
            }
            return false;
        }

        static bool Initialized(TISpaceCombatState combat)
        {
            return Safe<bool>(delegate { return combat.initialized; }, false);
        }

        static bool Simulated(TISpaceCombatState combat)
        {
            try { return combat.autoresolving || combat.SimulatedCombat != null; }
            catch (Exception) { return false; }
        }

        static bool PlayerInvolved(TISpaceCombatState combat)
        {
            TIFactionState[] factions = CombatFactions(combat);
            for (int i = 0; i < factions.Length; i++)
            {
                if (factions[i] != null && IsActivePlayer(factions[i])) return true;
            }
            return false;
        }

        static TISpaceCombatState TargetCombat(JObject args)
        {
            int id = OptionalInt(args, "combat");
            if (id >= 0)
            {
                TISpaceCombatState combat = ById<TISpaceCombatState>(id);
                if (combat.archived) throw new VerbError("combat " + id + " is archived");
                return combat;
            }
            TISpaceCombatState active = ActiveCombat();
            if (active != null) return active;
            foreach (TISpaceCombatState combat in GameStateManager.IterateByClass<TISpaceCombatState>(false))
            {
                if (combat != null && !combat.archived) return combat;
            }
            return null;
        }

        // `combat` is the fight the surrounding reply is about, and the arming
        // decision below is answered for it. With none in view -- combat.status with
        // nothing active -- the answer is about the combat the record names, which is
        // the one `attemptedCombat` reports beside it.
        static JToken AutoresolveStatus(TISpaceCombatState combat)
        {
            var o = new JObject();
            o["armed"] = autoPhase != AutoPhase.Idle;
            o["phase"] = autoPhase.ToString().ToLowerInvariant();
            o["combat"] = autoPhase != AutoPhase.Idle
                ? (JToken)new JValue(autoCombatId) : JValue.CreateNull();
            o["frames"] = autoFrames;
            o["error"] = autoError;
            o["note"] = autoNote;
            // The history, which outlives the disarm a failure makes. Without it a
            // re-arm looks exactly like a first arm from outside, and the caller has no
            // way to see that this combat has already been tried and how far it got.
            o["attemptedCombat"] = autoHistoryCombatId >= 0
                ? (JToken)new JValue(autoHistoryCombatId) : JValue.CreateNull();
            o["attempts"] = autoAttempts;
            o["reached"] = autoReached.ToString().ToLowerInvariant();
            o["errorKind"] = autoErrorKind.ToString().ToLowerInvariant();
            o["retryable"] = Retryable(combat);
            o["firedAutoresolveSelected"] = autoFiredSelect;
            o["firedAcceptAutoresolve"] = autoFiredAccept;
            return o;
        }

        // Whether another arm would be accepted, from the predicate the arm itself
        // refuses on, so the report cannot disagree with the decision.
        static bool Retryable(TISpaceCombatState combat)
        {
            int id = combat != null ? (int)combat.ID : autoHistoryCombatId;
            return UnsafeRearmReason(id) == null;
        }

        // Clears the working machine only. The history above is deliberately left
        // standing: the disarm is what a failed tick does, and the arming decision that
        // follows it is exactly the thing that needs to know what failed.
        static void Disarm()
        {
            autoCombatId = 0;
            autoPhase = AutoPhase.Idle;
            autoFrames = 0;
            autoStance = -1;
            autoPrecombat = null;
        }

        // Forgets the record of what the last arm on a combat left behind. Disarm
        // leaves it standing on purpose, because the arming decision that follows a
        // failed tick is exactly the thing that needs it; a campaign transition is the
        // other case, where the combat it names is about to stop existing and the id
        // is handed straight back out to whatever the next campaign allocates first.
        //
        // Called from saves.load, campaign.new and game.main_menu. Those three calls
        // collapse into the one campaign reset that clears the run state beside them.
        static void ResetHistory()
        {
            autoHistoryCombatId = -1;
            autoReached = AutoPhase.Idle;
            autoErrorKind = AutoFail.Unknown;
            autoAttempts = 0;
            autoFiredSelect = false;
            autoFiredAccept = false;
        }

        // Optional client stance override: {"stance": "Pursue"}. Validated against the
        // enum here and against allowedStances at submit time.
        static int ParseStance(JObject args)
        {
            JToken t = args["stance"];
            if (t == null || t.Type == JTokenType.Null) return -1;
            string s = t.ToString();
            CombatStance v;
            // Name back, because TryParse also takes "2"; only the three documented
            // stances are choices (NotYetSet is the unanswered marker, the
            // ExtendedPursuit members are a separate toggle the engine never lists).
            if (!Enum.TryParse<CombatStance>(s, true, out v)
                || !string.Equals(v.ToString(), s, StringComparison.OrdinalIgnoreCase)
                || v < CombatStance.Pursue || v > CombatStance.Evade)
                throw new VerbError("unknown stance '" + s + "' (Pursue, Defend, Evade)");
            return (int)v;
        }

        // Main thread, once a frame. Every step is guarded so a throw disarms rather than
        // leaving half-armed state behind, and the phase advances before any call that
        // must not run twice.
        static void TickAutoresolve()
        {
            if (autoPhase == AutoPhase.Idle) return;
            try
            {
                if (!HasCampaign) { Disarm(); return; }

                if (++autoFrames > AutoresolveFrameBudget)
                    // A stall in the closing phase is the one that no arm reaches: by
                    // then the combat is archived, so a re-arm's TargetCombat skips it
                    // entirely while the precombat canvas is still up and still holding
                    // the clock. Only a button clears that.
                    throw new AutoresolveFailure(
                        autoPhase == AutoPhase.Closing ? AutoFail.DeadEnd
                                                       : AutoFail.Unknown,
                        "autoresolve stalled in phase "
                        + autoPhase.ToString().ToLowerInvariant());

                // The closing phase outlives the combat: applying the result archives it
                // and drops it from the manager, so this runs before the lookup.
                if (autoPhase == AutoPhase.Closing) { CloseStep(); return; }

                // Null is a live outcome here (a resolved combat leaves the manager),
                // so this lookup does not go through ById.
                var combat = GameStateManager.FindGameState<TISpaceCombatState>(
                    new GameStateID(autoCombatId), true);
                if (combat == null || combat.archived)
                {
                    // Read before Disarm, which zeroes it.
                    int autoCombatIdBefore = autoCombatId;
                    // A successful evade ends the combat with no simulation: the game
                    // archives it and leaves the escape report on the precombat canvas,
                    // which freezes the clock until closed.
                    if (CanvasUp(FindPrecombat()))
                    {
                        autoPhase = AutoPhase.Closing;
                        Reached(autoPhase);
                        autoNote = "combat ended before simulation";
                        return;
                    }
                    // Gone with no canvas left to close, so no button will run and a
                    // standing PromptBeginCombat would hold the clock; see PromotionGate.
                    string left = ClearOrphanBeginCombatPrompt(
                        FindPrecombat(), autoCombatIdBefore)
                        ? " and cleared the PromptBeginCombat it left standing"
                        : "";
                    Disarm();
                    autoNote = "combat " + autoCombatIdBefore
                        + " ended before this machine reached its accept step" + left;
                    return;
                }

                switch (autoPhase)
                {
                    case AutoPhase.Settling: SettleStep(combat); break;
                    case AutoPhase.Firing: FireStep(combat); break;
                    case AutoPhase.Simulating: WaitStep(combat); break;
                }
            }
            catch (Exception e)
            {
                var classified = e as AutoresolveFailure;
                autoError = e is VerbError ? e.Message : Note(e);
                // Recorded beside the message so the next arm decides from a field
                // rather than from wording. An unclassified throw is Unknown, which
                // still gets the one retry the attempt budget allows.
                autoErrorKind = classified != null ? classified.kind : AutoFail.Unknown;
                autoNote = null;
                Disarm();
            }
        }

        // Everything the precombat screen would have collected from the player, collected
        // through the same buttons. HandlePrompts only answers prompts for AI factions, so
        // the wait loop inside OnPrecombatComplete may only ever be left waiting on AI.
        static void SettleStep(TISpaceCombatState combat)
        {
            if (Simulated(combat))
            {
                autoPhase = AutoPhase.Simulating;
                Reached(autoPhase);
                return;
            }
            if (!combat.initialized) return;
            // Now that factions is populated the question can finally be answered.
            if (!PlayerInvolved(combat))
            {
                // Except under an engagement, where the answer is a lie: the engaged
                // faction reports isActivePlayer false while PrecombatController still
                // takes the player from GameControl, so the canvas comes up and holds
                // the clock with nothing driving it. Disarming quietly here is what let
                // the caller re-arm every poll forever, so this records a failure the
                // next arm refuses instead.
                if (EngagedIn(combat))
                    throw new AutoresolveFailure(AutoFail.Stable,
                        "combat " + (int)combat.ID + " involves the faction handed to "
                        + "the AI by ai.control, which reports as not the active player "
                        + "while engaged; nothing here can drive it, and the engine's "
                        + "precombat canvas still comes up and still freezes the clock. "
                        + "Release the engagement and autoresolve, or answer the screen "
                        + "with combat.precombat");
                Disarm();
                autoNote = "resolving automatically";
                return;
            }

            PrecombatController precombat = Precombat();
            if (!ReferenceEquals(precombat.combat, combat)) return;

            combat.autoresolve = true;

            TIFactionState player = ControllerPlayer(precombat);
            if (combat.IncludesFaction(player) && !StanceSet(combat, player))
                precombat.StanceSubmit((int)Stance(combat, player));

            if (!combat.HaveStancesBeenSelected) { RequireAIOnly(combat, false); return; }

            if (combat.requiresBidding && !combat.HaveBidsBeenSubmitted)
            {
                if (combat.IncludesFaction(player) && !HasBid(combat, player))
                    precombat.BidSubmit();
                if (!combat.HaveBidsBeenSubmitted) { RequireAIOnly(combat, true); return; }
            }

            autoPhase = AutoPhase.Firing;
            Reached(autoPhase);
        }

        static void FireStep(TISpaceCombatState combat)
        {
            PrecombatController precombat = Precombat();
            if (!ReferenceEquals(precombat.combat, combat))
                throw new AutoresolveFailure(AutoFail.Stable,
                    "precombat controller lost the combat");
            // Advance first: EndPrecombatInteraction is a one-shot, and a throw out of the
            // event chain must not leave the phase armed to fire it again. The flag is
            // the same statement made durably: the phase is reset by the next arm, and
            // this is what refuses that arm.
            autoPhase = AutoPhase.Simulating;
            Reached(autoPhase);
            autoFiredSelect = true;
            precombat.AutoresolveSelected();
        }

        static void WaitStep(TISpaceCombatState combat)
        {
            if (combat.autoresolving) return;
            if (combat.SimulatedCombat == null) return;
            PrecombatController precombat = Precombat();
            if (!ReferenceEquals(precombat.combat, combat))
                throw new AutoresolveFailure(AutoFail.Stable,
                    "precombat controller lost the combat");
            ControllerPlayer(precombat);
            // Advance first: ApplySimulatedCombat writes damage into the real states and
            // must never run twice. The flag survives the disarm a later failure makes,
            // which the phase does not.
            autoPhase = AutoPhase.Closing;
            Reached(autoPhase);
            autoFiredAccept = true;
            // The button's own chain: clears PromptBeginCombat, hides the accept panel,
            // and applies the simulation.
            precombat.OnAcceptAutoresolveSelected();
        }

        // Applying the result raises CombatEnds, which puts the post-combat report on the
        // precombat canvas and leaves Canvas.enabled true. SimulationTimeTick returns early
        // while that canvas is enabled, so until it is closed no further combat is promoted
        // and the clock never advances. Which close button is live depends on the outcome:
        // a fought combat shows the post-combat report, an escape shows the main panel in
        // report mode. The Autopilot dispatches on whichever is clickable; so does this.
        // Both closes also replay a combat that was initiated while the report was up.
        static void CloseStep()
        {
            PrecombatController precombat = Precombat();
            if (!CanvasUp(precombat)) { Disarm(); return; }
            if (Clickable(precombat.postCombatCloseButton))
            {
                Disarm();
                // The one close branch that leaves PromptBeginCombat standing.
                // OnClosePostCombatButtonSelected hides the post-combat panel and either
                // replays a delayed combat or hides the canvas, and touches no prompt;
                // CloseResolveSelected, LiveResolveSelected, CancelAttackButton,
                // OnAcceptAutoresolveSelected and OnRejectAutoresolveSelected all
                // remove it first. That is invisible on the ordinary path, where
                // OnAcceptAutoresolveSelected already removed it, and it strands a
                // save-blocking faction prompt on the path where the combat ended before
                // simulation, because the step that would have removed it never ran.
                ClearBeginCombatPrompt(precombat);
                precombat.OnClosePostCombatButtonSelected();
                return;
            }
            if (Clickable(precombat.closeButton))
            {
                Disarm();
                precombat.CloseResolveSelected();
                return;
            }
            // Neither button is up yet; the frame budget bounds this wait.
        }

        // The engine's own removal, with the engine's own arguments.
        // PrecombatController.OnCombatInitiated queues
        // (activePlayer, combat, null, "PromptBeginCombat", 0), and every path that
        // clears it removes that same tuple. A prompt is matched on all five fields,
        // so a removal built from a different combat removes nothing.
        //
        // The controller can already have let go of the combat by the time the report
        // is closed, and a tuple built from a null combat matches nothing either, so a
        // standing prompt of that name on the active player's own list is taken as the
        // one this canvas queued. Only the precombat screen queues this name, and it
        // holds one combat at a time.
        //
        // Nothing else clears it. PromptBeginCombat is in neither this mod's answer
        // table nor the engine's AI handler, and an unhandled faction prompt is ignored
        // in silence rather than dropped, so one left standing blocks saving and holds
        // the clock for the rest of the campaign.
        //
        // Returns whether a prompt was actually taken off the queue, so a caller
        // can say which of the two it did. The close path ignores it: there the
        // ordinary case is that OnAcceptAutoresolveSelected already removed it.
        static bool ClearBeginCombatPrompt(PrecombatController precombat)
        {
            bool cleared = false;
            try
            {
                if (precombat == null) return false;
                TIFactionState player = precombat.activePlayer;
                if (player == null) return false;
                TISpaceCombatState combat = Safe<TISpaceCombatState>(
                    delegate { return precombat.combat; }, null);
                if (combat != null)
                {
                    // RemovePromptStatic returns nothing and matches on all five
                    // fields, so whether it took anything is a before-and-after
                    // count rather than a return value.
                    int before = FactionPromptsNamed("PromptBeginCombat");
                    TIPromptQueueState.RemovePromptStatic(
                        player, combat, null, "PromptBeginCombat", 0);
                    return FactionPromptsNamed("PromptBeginCombat") < before;
                }
                TIPromptQueueState queue = PromptQueue();
                List<Prompt> standing = FactionPrompts(queue);
                for (int i = 0; i < standing.Count; i++)
                {
                    if (standing[i].name != "PromptBeginCombat") continue;
                    queue.RemovePrompt(standing[i]);
                    if (StillBlocking(queue, standing[i])) continue;
                    cleared = true;
                    autoNote = "closed the post-combat report and cleared a standing "
                        + "PromptBeginCombat the controller no longer had a combat for";
                }
            }
            catch (Exception) { }
            return cleared;
        }

        // The clear made on the vanish path, where the combat is gone and no
        // canvas is left. Gated on the controller not having moved on: one
        // combat runs at a time, but a second can be parked on a fleet waiting
        // for this one, and a prompt standing for THAT one is live. Its canvas
        // has simply not come up yet, and dropping it would strand the fight
        // this code exists to stop stranding.
        static bool ClearOrphanBeginCombatPrompt(PrecombatController precombat, int ours)
        {
            try
            {
                TISpaceCombatState held = precombat != null
                    ? Safe<TISpaceCombatState>(
                        delegate { return precombat.combat; }, null)
                    : null;
                if (held != null && (int)held.ID != ours) return false;
                return ClearBeginCombatPrompt(precombat);
            }
            catch (Exception) { return false; }
        }

        // Standing prompts of one name on the active player's own faction list. Both
        // precombat prompts are queued against the faction, so that is the list they
        // block the clock from.
        static int FactionPromptsNamed(string name)
        {
            int count = 0;
            try
            {
                List<Prompt> standing = FactionPrompts(PromptQueue());
                for (int i = 0; i < standing.Count; i++)
                {
                    if (standing[i].name == name) count++;
                }
            }
            catch (Exception) { return 0; }
            return count;
        }

        static bool CanvasUp(PrecombatController precombat)
        {
            try
            {
                if (precombat == null) return false;
                UnityEngine.Canvas canvas = precombat.Canvas;
                return canvas != null && canvas.enabled;
            }
            catch (Exception) { return false; }
        }

        // Waiting is only safe while every outstanding stance or bid belongs to an AI
        // faction; a human one would never be answered and the wait loop would hang.
        static void RequireAIOnly(TISpaceCombatState combat, bool bids)
        {
            TIFactionState[] factions = CombatFactions(combat);
            for (int i = 0; i < factions.Length; i++)
            {
                TIFactionState f = factions[i];
                if (f == null) continue;
                bool have = bids ? HasBid(combat, f) : StanceSet(combat, f);
                if (have) continue;
                if (!IsAI(f))
                    // Stable: the missing answer belongs to a second seated player and
                    // no arm of this machine can produce it.
                    throw new AutoresolveFailure(AutoFail.Stable,
                        "combat needs a " + (bids ? "bid" : "stance")
                        + " from non-AI faction " + StateName(f));
            }
        }

        static CombatStance Stance(TISpaceCombatState combat, TIFactionState faction)
        {
            List<CombatStance> list = AllowedStances(combat, faction);
            if (autoStance >= 0)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if ((int)list[i] == autoStance) return (CombatStance)autoStance;
                }
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == CombatStance.Defend) return CombatStance.Defend;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] != CombatStance.NotYetSet) return list[i];
            }
            // What the vanilla Autopilot submits when it drives the precombat screen.
            return CombatStance.Defend;
        }

        // No Singleton on this controller, and its canvas is disabled for AI-only
        // combats, so inactive objects have to be searched too. Unity fakes null on a
        // destroyed component, so the cache liveness check is the bool operator.
        static PrecombatController FindPrecombat()
        {
            if (autoPrecombat) return autoPrecombat;
            autoPrecombat = Find<PrecombatController>();
            return autoPrecombat;
        }

        static PrecombatController Precombat()
        {
            PrecombatController precombat = FindPrecombat();
            // Transient: the controller is instantiated with the combat scene and a
            // machine that got ahead of that may find it on the next arm.
            if (precombat == null)
                throw new AutoresolveFailure(AutoFail.Transient,
                    "no precombat controller");
            return precombat;
        }

        // The precombat buttons submit on behalf of the controller's own activePlayer,
        // which is copied from GameControl by SetActivePlayer. A controller that has not
        // caught up would answer for the wrong faction.
        static TIFactionState ControllerPlayer(PrecombatController precombat)
        {
            TIFactionState player = precombat.activePlayer;
            // Transient: SetActivePlayer fills this in, and a machine that arrived
            // before it ran finds it on the next arm.
            if (player == null)
                throw new AutoresolveFailure(AutoFail.Transient,
                    "precombat controller has no active player");
            // Stable: the controller is pointed at a faction that is not the one
            // GameControl holds. Arming again re-enters the same comparison against the
            // same two references and gets the same answer.
            if (!ReferenceEquals(player, ActivePlayer()))
                throw new AutoresolveFailure(AutoFail.Stable,
                    "precombat controller has a stale active player");
            return player;
        }

        #region combat.precombat

        // The precombat screen's buttons, for a combat the autoresolver cannot finish.
        //
        // This exists because dropping PromptBeginCombat does not unfreeze the clock:
        // SimulationTimeTick returns early while the precombat canvas is enabled, and
        // only a button on that canvas takes it down. Every path the autoresolve machine
        // refuses to retry ends with that canvas standing, so without this the harness
        // can name the dead end and not clear it.
        //
        // There is deliberately no `accept`. OnAcceptAutoresolveSelected is the call
        // that reaches ApplySimulatedCombat and writes damage into the real states;
        // combat.autoresolve owns it, fires it once, and refuses to fire it again. A
        // second door to it here would reintroduce exactly the double-apply this whole
        // machine guards against.
        static JToken CombatPrecombat(JObject args)
        {
            string action = Str(args, "action");
            if (string.IsNullOrEmpty(action)) action = "status";
            action = action.ToLowerInvariant();

            PrecombatController precombat = FindPrecombat();
            var o = new JObject();
            o["canvasUp"] = CanvasUp(precombat);
            o["buttons"] = PrecombatButtons(precombat);
            TISpaceCombatState onScreen = Safe<TISpaceCombatState>(
                delegate { return precombat.combat; }, null);
            o["combat"] = onScreen != null
                ? Safe<JToken>(delegate { return Describe(onScreen); },
                               JValue.CreateNull())
                : (JToken)JValue.CreateNull();
            o["autoresolve"] = AutoresolveStatus(onScreen);
            if (action == "status") return o;

            if (precombat == null) throw new VerbError("no precombat controller");
            if (!CanvasUp(precombat))
                throw new VerbError("the precombat canvas is not up, so none of its "
                    + "buttons exists to press; nothing was called");
            // The machine drives this same controller a step per frame. Two drivers on
            // one screen is how a one-shot fires twice.
            if (autoPhase != AutoPhase.Idle)
                throw new VerbError("combat.autoresolve is armed on combat "
                    + autoCombatId + " and drives this screen itself; nothing was "
                    + "pressed. Let it finish or fail first");

            o["pressed"] = action;
            switch (action)
            {
                case "close":
                    // CloseResolveSelected removes PromptBeginCombat and raises
                    // PrecombatComplete. The post-combat report has its own close, and
                    // that one clears no prompt, so it is paired with the removal here
                    // the same way CloseStep pairs it.
                    if (Clickable(precombat.postCombatCloseButton))
                    {
                        ClearBeginCombatPrompt(precombat);
                        precombat.OnClosePostCombatButtonSelected();
                        o["via"] = "OnClosePostCombatButtonSelected";
                    }
                    else if (Clickable(precombat.closeButton))
                    {
                        precombat.CloseResolveSelected();
                        o["via"] = "CloseResolveSelected";
                    }
                    else
                    {
                        throw new VerbError("neither close button is clickable; "
                            + "`buttons` says which are");
                    }
                    break;
                case "cancel":
                    // Calls off the attack: clears the stance and begin-combat prompts,
                    // hides the canvas, runs TISpaceCombatState.CancelCombat and returns
                    // the camera to Earth. The one action here that ends a combat with
                    // no battle and no simulation.
                    RequireClickable(precombat.cancelAttackButton, "cancelAttackButton");
                    precombat.CancelAttackButton();
                    o["via"] = "CancelAttackButton";
                    break;
                case "reject":
                    // Refuses the offered simulation: clears PromptBeginCombat, sets
                    // combat.autoresolve false and calls
                    // SpaceCombatManager.AutoresolveRejected, which hands the fight to
                    // the tactical layer. Nothing headless drives that layer.
                    RequireClickable(precombat.rejectAutoresolveButton,
                        "rejectAutoresolveButton");
                    precombat.OnRejectAutoresolveSelected();
                    o["via"] = "OnRejectAutoresolveSelected";
                    o["warning"] = "the fight is now live rather than autoresolved, and "
                        + "no verb drives a live battle";
                    break;
                case "live":
                    // Same destination as reject, from the pre-simulation screen.
                    RequireClickable(precombat.liveResolveButton, "liveResolveButton");
                    precombat.LiveResolveSelected();
                    o["via"] = "LiveResolveSelected";
                    o["warning"] = "the fight is now live rather than autoresolved, and "
                        + "no verb drives a live battle";
                    break;
                default:
                    throw new VerbError("action must be one of status, close, cancel, "
                        + "reject, live; there is no 'accept' on purpose -- accepting "
                        + "an autoresolve applies simulated damage to the real states "
                        + "and combat.autoresolve owns that call so it happens exactly "
                        + "once");
            }
            // Read back after the press: the caller's next decision is about what the
            // screen is now, not what it was.
            o["canvasUpAfter"] = CanvasUp(precombat);
            o["buttonsAfter"] = PrecombatButtons(precombat);
            return o;
        }

        static void RequireClickable(UnityEngine.UI.Button button, string name)
        {
            if (!Clickable(button))
                throw new VerbError(name + " is not clickable; `buttons` says which are");
        }

        static void RequireClickable(UnityEngine.GameObject button, string name)
        {
            if (!Clickable(button))
                throw new VerbError(name + " is not clickable; `buttons` says which are");
        }

        static JToken PrecombatButtons(PrecombatController precombat)
        {
            var o = new JObject();
            if (precombat == null) return o;
            o["postCombatClose"] = Safe<bool>(
                delegate { return Clickable(precombat.postCombatCloseButton); }, false);
            o["close"] = Safe<bool>(
                delegate { return Clickable(precombat.closeButton); }, false);
            o["cancelAttack"] = Safe<bool>(
                delegate { return Clickable(precombat.cancelAttackButton); }, false);
            o["rejectAutoresolve"] = Safe<bool>(
                delegate { return Clickable(precombat.rejectAutoresolveButton); }, false);
            o["liveResolve"] = Safe<bool>(
                delegate { return Clickable(precombat.liveResolveButton); }, false);
            o["autoResolve"] = Safe<bool>(
                delegate { return Clickable(precombat.autoResolveButton); }, false);
            return o;
        }

        #endregion

        #region combat.stance

        // The gate both stance paths take, in the order they both took it: a live
        // controller, its canvas up, the autoresolve machine idle, a combat on the
        // screen, and an active player GameControl agrees with who is in that combat.
        //
        // The canvas clause is the same gate combat.precombat takes, and for the same
        // reason: the controller survives the screen, so with the canvas down it still
        // holds a combat -- the finished one whose report was just closed, or a stale
        // one while OnCombatInitiated defers the next -- and StanceSubmit would run on
        // that, on behalf of a screen the player never saw. The autoPhase clause is
        // two drivers on one screen: the machine's own SettleStep submits this same
        // stance a step per frame, and a second submit races it into a second
        // SelectCombatStance on the same combat. ControllerPlayer throws when the
        // controller has no active player yet or holds one that is not the faction
        // GameControl holds, because the buttons submit on behalf of the controller's
        // activePlayer and a controller that has not caught up would answer for the
        // wrong faction.
        //
        // Each caller keeps its own extra check: the stance argument here, the
        // prompt-combat identity in the prompt answer.
        static void StanceScreen(out PrecombatController precombat,
                                 out TISpaceCombatState combat,
                                 out TIFactionState player)
        {
            PrecombatController screen = FindPrecombat();
            if (screen == null)
                throw new VerbError("no precombat controller, so there is no screen to "
                    + "submit a stance on; combat.status reports whether a combat is "
                    + "pending at all");
            if (!CanvasUp(screen))
                throw new VerbError("the precombat canvas is not up, so the stance "
                    + "button does not exist to press; nothing was submitted. "
                    + "combat.precombat action=status reports the canvas and its "
                    + "buttons, and combat.status what is pending");
            if (autoPhase != AutoPhase.Idle)
                throw new VerbError("combat.autoresolve is armed on combat "
                    + autoCombatId + " and submits the stance itself a step per frame; "
                    + "nothing was submitted here. Let it finish or fail first, then "
                    + "read combat.status");
            TISpaceCombatState onScreen = Safe<TISpaceCombatState>(
                delegate { return screen.combat; }, null);
            if (onScreen == null)
                throw new VerbError("the precombat controller holds no combat, so "
                    + "there is nothing to submit a stance for");
            TIFactionState seated = ControllerPlayer(screen);
            if (!Safe<bool>(delegate { return onScreen.IncludesFaction(seated); }, false))
                throw new VerbError("faction " + StateName(seated) + " is not in combat "
                    + (int)onScreen.ID + ", so it has no stance to submit");
            precombat = screen;
            combat = onScreen;
            player = seated;
        }

        // The player's own stance, submitted by hand, for a fight this harness is not
        // autoresolving. combat.autoresolve submits one on its way past; this is the
        // verb for the other case, where a person or a caller means to take the fight
        // and the stance prompt is what freezes the clock until it is answered.
        //
        // It acts on the LIVE precombat controller and on that controller's own active
        // player, and takes neither from the caller, because StanceSubmit does the
        // same: PrecombatController::StanceSubmit(int32) builds
        // SelectCombatStance(get_combat(), get_activePlayer(), stance) and hands it to
        // that faction's playerControl. A `faction` or a `combat` argument could not
        // be honoured and would report a success it never made. Another faction's
        // stance stays action.invoke SelectCombatStance, with the out-of-band caveat
        // that carries.
        static JToken CombatSetStance(JObject args)
        {
            int wanted = ParseStance(args);
            if (wanted < 0)
                throw new VerbError("arg 'stance' is required: Pursue, Defend or Evade");

            PrecombatController precombat;
            TISpaceCombatState combat;
            TIFactionState player;
            StanceScreen(out precombat, out combat, out player);

            List<CombatStance> allowed = AllowedStances(combat, player);
            if (!Allows(allowed, wanted))
                throw new VerbError("stance " + ((CombatStance)wanted).ToString()
                    + " is not allowed for " + StateName(player) + " in combat "
                    + (int)combat.ID + "; allowed: " + StanceList(allowed));

            var o = new JObject();
            o["combat"] = Describe(combat);
            o["faction"] = Describe(player);
            o["requested"] = ((CombatStance)wanted).ToString();
            o["allowed"] = StanceNames(allowed);
            int promptsBefore = FactionPromptsNamed(StancePrompt);

            precombat.StanceSubmit(wanted);

            // Read back rather than assumed. SelectCombatStance runs synchronously --
            // Player::StartAction is a bare call to PlayerAction::Execute -- so the
            // write and the prompt removal have both happened by the time this line
            // runs, and reporting the request as the outcome would hide a build where
            // they had not.
            CombatStance? now = StanceOf(combat, player);
            bool submitted = now != null && (int)now.Value == wanted;
            int promptsAfter = FactionPromptsNamed(StancePrompt);
            o["stance"] = now != null ? (JToken)new JValue(now.Value.ToString())
                                      : JValue.CreateNull();
            o["submitted"] = submitted;
            o["stancePromptsBefore"] = promptsBefore;
            o["stancePrompts"] = promptsAfter;
            o["stancesSelected"] = Safe<JToken>(
                delegate { return (JToken)combat.HaveStancesBeenSelected; },
                JValue.CreateNull());
            o["beginCombatPrompts"] = FactionPromptsNamed("PromptBeginCombat");
            if (submitted && promptsAfter == 0) return o;
            // Said rather than thrown: the submit has already run, and a throw would
            // lose the whole reply with it. The two ways this ends short are different
            // problems, so each is named.
            o["warning"] = (submitted
                    ? "the stance was written"
                    : "the stance did NOT read back off combat.stances")
                + (promptsAfter > 0
                    ? " and " + promptsAfter + " " + StancePrompt + " prompt(s) are "
                        + "still queued, so the clock is still blocked"
                    : " and the stance prompt is gone");
            return o;
        }

        // The prompt SelectCombatStance removes on its way through: it is queued
        // against the faction, so it lands on the active player's faction list and
        // freezes the clock there.
        const string StancePrompt = "PromptSelectSpaceCombatStance";

        // The engine's own answer to "what may this faction pick", and what Stance()
        // picks out of. Never null: an unreadable table is an empty list, which
        // refuses every stance rather than submitting into a combat that has no entry
        // for this faction.
        static List<CombatStance> AllowedStances(TISpaceCombatState combat,
                                                 TIFactionState faction)
        {
            return Safe<List<CombatStance>>(delegate
            {
                Dictionary<TIFactionState, List<CombatStance>> allowed = combat.allowedStances;
                List<CombatStance> list;
                if (allowed != null && allowed.TryGetValue(faction, out list)
                    && list != null)
                    return list;
                return new List<CombatStance>();
            }, new List<CombatStance>());
        }

        // NotYetSet is in the table as the unanswered marker rather than as a choice,
        // and submitting it would answer the prompt with "no answer".
        static bool Allows(List<CombatStance> allowed, int stance)
        {
            for (int i = 0; i < allowed.Count; i++)
            {
                if ((int)allowed[i] == stance && allowed[i] != CombatStance.NotYetSet)
                    return true;
            }
            return false;
        }

        static JArray StanceNames(List<CombatStance> allowed)
        {
            var a = new JArray();
            for (int i = 0; i < allowed.Count; i++)
                a.Add(new JValue(allowed[i].ToString()));
            return a;
        }

        static string StanceList(List<CombatStance> allowed)
        {
            if (allowed.Count == 0) return "none (the combat has no entry for it)";
            var parts = new string[allowed.Count];
            for (int i = 0; i < allowed.Count; i++) parts[i] = allowed[i].ToString();
            return string.Join(", ", parts);
        }

        // The neutral answer for PromptSelectSpaceCombatStance, which the engine's own
        // prompt handler serves to AI factions only, so the active player's copy sits
        // there and holds the clock.
        //
        // It runs the same submit the verb above runs, through the same screen button
        // path, and picks with Stance() -- Defend when the combat allows it, which is
        // what the vanilla Autopilot submits. Three conditions gate it, and each is
        // reported as the skip reason rather than answered around:
        //
        //  - A live precombat controller with the canvas up. Without it there is no
        //    screen and StanceSubmit has no combat to name.
        //  - The autoresolve machine idle. Armed, it submits the stance itself.
        //  - The screen showing the combat THIS prompt belongs to. The two diverge
        //    while a post-combat report is up; see the check below.
        //
        // PromptBeginCombat has no entry in the table at all, and cannot: every button
        // that clears it commits an outcome (close, cancel, reject, live), and one of
        // them is the accept the autoresolve machine owns. combat.precombat is the
        // contracted path.
        static string AnswerCombatStance(Prompt prompt, TIFactionState faction,
                                         JObject entry)
        {
            PrecombatController precombat;
            TISpaceCombatState combat;
            TIFactionState player;
            StanceScreen(out precombat, out combat, out player);

            // The prompt names its own combat and the screen need not be showing
            // it. SpaceCombatManager.CombatInit queues this prompt against each
            // faction with the new combat in the relatedGameState slot
            // (AddPrompt(factions[i], null, combatState,
            // "PromptSelectSpaceCombatStance", 0), IL_005e-IL_00a3), and it queues
            // it whatever the screen is doing. PrecombatController.OnCombatInitiated
            // stashes the event in delayedCombatInitiationEvent and RETURNS before
            // set_combat whenever the post-combat report is still up
            // (IL_0000-IL_0014), so with a report on screen the controller still
            // holds the finished fight while the prompt belongs to the new one.
            //
            // Submitting on the screen's combat there answers the wrong fight and
            // reports it as this prompt's answer: SelectCombatStance.Execute removes
            // the prompt keyed on ITS combat (RemovePromptStatic at IL_0023), so the
            // prompt in hand would still be blocking the clock afterwards. Skipped
            // with the reason instead, which is the same shape the diplomacy and
            // policy prompt checks use.
            TISpaceCombatState promptCombat = Safe<TISpaceCombatState>(
                delegate { return prompt.relatedGameState as TISpaceCombatState; },
                null);
            if (promptCombat == null)
                throw new VerbError("the prompt carries no combat in its "
                    + "relatedGameState, so there is no way to tell whether the "
                    + "precombat screen is showing the fight it belongs to");
            if (!ReferenceEquals(promptCombat, combat))
                throw new VerbError("the prompt belongs to combat "
                    + (int)promptCombat.ID + " and the precombat screen is showing "
                    + "combat " + (int)combat.ID + ", so a submit here would answer "
                    + "the wrong fight and leave this prompt blocking the clock. A "
                    + "post-combat report still up is the usual cause -- "
                    + "OnCombatInitiated defers the new combat until it is closed; "
                    + "combat.precombat action=close closes it");

            CombatStance pick = Stance(combat, player);
            if (!Allows(AllowedStances(combat, player), (int)pick))
                throw new VerbError("combat " + (int)combat.ID + " allows this faction "
                    + "no stance to submit (" + StanceList(AllowedStances(combat, player))
                    + ")");
            precombat.StanceSubmit((int)pick);

            // Measured against the list the clock reads, not assumed from the call.
            // The engine removes the prompt inside SelectCombatStance.Execute
            // (RemovePromptStatic at its IL_0023), so a prompt still standing here is
            // a submit that did not take and must not be reported as an answer.
            if (StillBlocking(PromptQueue(), prompt))
                throw new VerbError("StanceSubmit ran with stance " + pick
                    + " and the prompt is still in the active player's prompt lists");
            // Written only on the answer, so a skipped entry never carries a stance
            // that reads as one this pass set.
            entry["combat"] = (int)combat.ID;
            entry["stance"] = pick.ToString();
            return "StanceSubmit " + pick;
        }

        #endregion

        #region fleet.bombard

        // Possible targets are listed in the refusal message; on Earth the list is every
        // region the fleet may hit, which is most of the map.
        const int MaxBombardTargetsNamed = 10;

        static JToken FleetBombard(JObject args)
        {
            TISpaceFleetState fleet = Arg<TISpaceFleetState>(args, "fleet");
            TIGameState target = Arg<TIGameState>(args, "target");
            string altitude = Str(args, "altitude");
            if (string.IsNullOrEmpty(altitude)) altitude = "low";
            BombardOperation op = BombardOp(altitude);

            // The engine's own non-UI order path is
            // AIDailyFactionPlanner.SingleFleetOperation: test the target against
            // GetPossibleTargets, then OnOperationConfirm(actor, target, null, null).
            // ActorCanPerformOperation is the gate the UI applies before it offers the
            // order at all, and it subsumes every fleet-side precondition, so it runs
            // first here to turn one refusing bool into a message naming the state.
            if (!op.ActorCanPerformOperation(fleet, target))
                throw new VerbError("fleet " + (int)fleet.ID + " cannot bombard: "
                    + BombardRefusal(fleet, op, target));

            List<TIGameState> possible = null;
            try { possible = op.GetPossibleTargets(fleet, null); }
            catch (Exception e) { throw new VerbError("GetPossibleTargets threw: " + Note(e)); }
            if (possible == null || !possible.Contains(target))
                throw new VerbError(StateName(target) + " (" + (int)target.ID
                    + ") is not a possible bombardment target for fleet " + (int)fleet.ID
                    + "; " + BombardTargetList(possible));

            // OnOperationConfirm's first act is ValidOperation, which runs before
            // anything is written, so a false return leaves no half-applied order.
            // Confirming against a human nation's region also commits an atrocity
            // (AtrocityCause.SpaceBombardHumanNationRegions) before the bombardment
            // starts -- permanent campaign state, reported below.
            bool atrocity = target.isRegionState && !target.ref_nation.alienNation;
            if (!op.OnOperationConfirm(fleet, target, null, null))
                throw new VerbError("the engine refused the bombardment order: "
                    + BombardRefusal(fleet, op, target));
            if (!fleet.bombarding)
                throw new VerbError("OnOperationConfirm returned true but the fleet is "
                    + "not bombarding; the engine's write order has changed -- treat the "
                    + "order as suspect and inspect the fleet's operations");

            var o = new JObject();
            o["atrocityCommitted"] = atrocity;
            o["fleet"] = Describe(fleet);
            o["target"] = Describe(target);
            o["operation"] = op.GetType().Name;
            // Read back from the fleet rather than recomputed: this is the state the
            // caller will be polling.
            Put(o, "bombarding", delegate { return (JToken)fleet.bombarding; });
            Put(o, "bombardmentTarget", delegate
            {
                TIGameState t = fleet.bombardmentTarget;
                return t != null ? Describe(t) : JValue.CreateNull();
            });
            Put(o, "altitudeKm", delegate { return (JToken)fleet.bombardmentAltitude_km; });
            Put(o, "endBombardmentReason",
                delegate { return (JToken)fleet.endBombardmentReason.ToString(); });

            // Bombardment runs for BOMBARDMENT_DURATION_DAYS and is then ended by the
            // operation's own ExecuteOperation, so the caller advances the clock and
            // re-reads the fleet.
            OperationData data = BombardOperationData(fleet, op, target);
            o["durationDays"] = BombardOperation.BOMBARDMENT_DURATION_DAYS;
            Put(o, "completes", delegate
            {
                return data != null && data.completionDate != null
                    ? (JToken)InvariantDate(data.completionDate) : JValue.CreateNull();
            });

            // Only when the TARGET is the hab. A landed-fleet target also resolves a
            // ref_hab (the site's hab hosting it), but that hab is not what is being
            // bombarded and polling it would watch the wrong object.
            TIHabState hab = target as TIHabState;
            if (hab != null)
            {
                var h = new JObject();
                Put(h, "underBombardment", delegate { return (JToken)hab.underBombardment; });
                // Each hit picks one weighted-random module out of OkayModules() less
                // the core, so a named module is destroyed by polling this count and
                // repeating, never by asking for it. When okayModules reaches zero the
                // next hit destroys the hab outright.
                Put(h, "okayModules", delegate { return (JToken)hab.OkayModules().Count; });
                Put(h, "presentModules", delegate { return (JToken)hab.PresentModules().Count; });
                o["hab"] = h;
            }
            return o;
        }

        // The three concrete BombardOperation subclasses differ only in the altitude
        // they hand InitiateBombardment: TIGlobalConfig low/med/highBombardmentAltitude_km,
        // 200/400/600 km on the shipped values. Closer is more damage per hit and more
        // exposure to the target's own defenses.
        static BombardOperation BombardOp(string altitude)
        {
            altitude = altitude.ToLowerInvariant();
            Type type = altitude == "low" ? typeof(BombardOperation_Low)
                      : altitude == "med" ? typeof(BombardOperation_Med)
                      : altitude == "high" ? typeof(BombardOperation_High) : null;
            if (type == null)
                throw new VerbError("arg 'altitude' must be low, med or high");
            return RegistryOp<BombardOperation>(type, "");
        }

        // The registry instance, never a fresh one: the interrupt check resolves a
        // blocking operation's BreakthroughOps through this same dictionary and compares
        // instances (IL_005c-IL_005d compares against `this`), and OperationData stores
        // the instance for the cancel path, so a private copy would compare unequal to
        // itself everywhere the engine looks. `note` is the caller's tail on the
        // not-registered refusal.
        static T RegistryOp<T>(Type type, string note) where T : class
        {
            var lookup = OperationsManager.operationsLookup;
            IOperation op = null;
            if (lookup == null || !lookup.TryGetValue(type, out op) || op == null)
                throw new VerbError("OperationsManager has no " + type.Name
                    + " registered" + note);
            T typed = op as T;
            if (typed == null)
                throw new VerbError("OperationsManager holds a " + op.GetType().Name
                    + " under " + type.Name);
            return typed;
        }

        // Every clause ActorCanPerformOperation reads, reported as observed rather than
        // guessed at: the method answers one bool, and these values are what a caller
        // needs to see to fix the setup.
        static string BombardRefusal(TISpaceFleetState fleet, BombardOperation op,
                                     TIGameState target)
        {
            var parts = new List<string>();
            parts.Add("transferAssigned="
                + Safe<bool>(delegate { return fleet.transferAssigned; }, false));
            parts.Add("dockedOrLanded="
                + Safe<bool>(delegate { return fleet.dockedOrLanded; }, false));
            parts.Add("orbitBody=" + Safe<string>(
                delegate { return StateName(fleet.orbitState.barycenter); }, "?"));
            parts.Add("interfaceOrbit="
                + Safe<bool>(delegate { return fleet.orbitState.interfaceOrbit; }, false));
            // A synchronous orbit (GEO, ASO) is refused outright.
            parts.Add("synchOrbit="
                + Safe<bool>(delegate { return fleet.orbitState.template.synch; }, false));
            parts.Add("inCombat="
                + Safe<bool>(delegate { return fleet.inCombatOrWaitingForCombat; }, false));
            // Zero means no ship in the fleet carries a weapon that reaches the surface.
            parts.Add("bombardmentValue=" + Safe<float>(
                delegate { return fleet.BombardmentValue(fleet.ref_spaceBody); }, 0f)
                .ToString(CultureInfo.InvariantCulture));
            parts.Add("blockedBy=" + (Blocker(fleet, op) ?? "none"));
            parts.Add("possibleTargets=" + Safe<int>(
                delegate { return op.GetPossibleTargets(fleet, null).Count; }, -1));
            if (target != null)
                parts.Add("targetArchived="
                    + Safe<bool>(delegate { return target.archived; }, false));
            return string.Join(" ", parts.ToArray());
        }

        // The operation already on the fleet that would refuse the interrupt, for
        // whichever operation is being attempted: blocking, and not one this operation is
        // allowed to break through. The comparison is against the registry instance
        // because that is what the engine compares.
        static string Blocker(TISpaceFleetState fleet, IOperation op)
        {
            try
            {
                List<OperationData> current = fleet.CurrentOperations();
                if (current == null) return null;
                for (int i = 0; i < current.Count; i++)
                {
                    OperationData d = current[i];
                    if (d == null || d.operation == null) continue;
                    if (!d.operation.IsBlockingOperation()) continue;
                    var fleetOp = d.operation as TISpaceFleetOperationTemplate;
                    if (fleetOp != null && BreaksThrough(fleetOp, op)) continue;
                    return d.operation.GetType().Name;
                }
            }
            catch (Exception) { }
            return null;
        }

        static bool BreaksThrough(TISpaceFleetOperationTemplate blocking, IOperation op)
        {
            List<Type> allowed = blocking.BreakthroughOps();
            if (allowed == null) return false;
            var lookup = OperationsManager.operationsLookup;
            for (int i = 0; i < allowed.Count; i++)
            {
                IOperation instance = null;
                if (lookup != null && lookup.TryGetValue(allowed[i], out instance)
                    && ReferenceEquals(instance, op)) return true;
            }
            return false;
        }

        static string BombardTargetList(List<TIGameState> possible)
        {
            if (possible == null || possible.Count == 0)
                return "the fleet has no possible bombardment targets here "
                    + "(habs are only targets at bodies other than Earth)";
            var names = new List<string>();
            int n = possible.Count < MaxBombardTargetsNamed
                ? possible.Count : MaxBombardTargetsNamed;
            for (int i = 0; i < n; i++)
            {
                TIGameState t = possible[i];
                if (t == null) continue;
                names.Add(StateName(t) + " (" + (int)t.ID + ")");
            }
            return "possible (" + possible.Count + "): " + string.Join(", ", names.ToArray())
                + (possible.Count > n ? ", ..." : "");
        }

        // OperationConfirmed appends, and BombardOperation is Repeatable, so the last
        // match is the order this call placed.
        static OperationData BombardOperationData(TISpaceFleetState fleet, IOperation op,
                                                  TIGameState target)
        {
            try
            {
                List<OperationData> current = fleet.CurrentOperations();
                if (current == null) return null;
                for (int i = current.Count - 1; i >= 0; i--)
                {
                    OperationData d = current[i];
                    if (d == null) continue;
                    if (!ReferenceEquals(d.operation, op)) continue;
                    if (!ReferenceEquals(d.target, target)) continue;
                    return d;
                }
            }
            catch (Exception) { }
            return null;
        }

        #endregion

        #region fleet.land

        // Lands a fleet on a hab site. This is a fixture: TISpaceFleetState.Land
        // checks nothing -- not the body, not delta-V, not combat, not ownership --
        // and this verb takes it as-is, the way spawn.fleet takes the engine's own
        // construction path without the cost around it.
        //
        // It exists for fleet.bombard above. A fleet is a bombardment target only
        // while it sits in some hab site's landedFleets on the bombarding fleet's
        // own body, and Land is the only engine call that writes that list.
        static JToken FleetLand(JObject args)
        {
            TISpaceFleetState fleet = Arg<TISpaceFleetState>(args, "fleet");
            TIHabSiteState site = Arg<TIHabSiteState>(args, "site");
            RequireLive("fleet", fleet);
            RequireLive("hab site", site);

            TISpaceBodyState body = Safe<TISpaceBodyState>(
                delegate { return site.parentBody; }, null);
            TISpaceBodyState fleetBody = Safe<TISpaceBodyState>(
                delegate { return fleet.ref_spaceBody; }, null);

            var o = new JObject();
            o["fleet"] = Describe(fleet);
            o["site"] = Describe(site);
            o["body"] = body != null ? Describe(body) : (JToken)JValue.CreateNull();
            o["before"] = LandingState(fleet, site);

            // Already there. Landing again would cancel the fleet's operations and
            // spend a second landing's delta-V for a list membership it already
            // holds, so this answers as a no-op the way module.power does. Both
            // halves are required: a fleet whose dockedLocation is the site but
            // which is missing from landedFleets is the state this verb exists to
            // produce, so that one falls through and is landed for real.
            bool atSite = ReferenceEquals(Safe<TISpaceGameState>(
                delegate { return fleet.dockedLocation; }, null), site);
            bool listed = Safe<bool>(delegate
            {
                List<TISpaceFleetState> landed = site.landedFleets;
                return landed != null && landed.Contains(fleet);
            }, false);
            if (atSite && listed)
            {
                o["departedFrom"] = JValue.CreateNull();
                o["changed"] = false;
                o["after"] = o["before"].DeepClone();
                // The site warnings belong here too. They are properties of where the
                // fleet is sitting, not of what this call did, and they stay true on a
                // repeat call -- so answering a retry with "nothing was called" alone
                // would drop the Earth warning a caller reads before ordering a
                // bombardment.
                o["warning"] = Warned(SiteWarnings(fleetBody, body),
                    "the fleet was already landed at this site; nothing was called");
                return o;
            }

            // Land does not clear a trajectory, so a fleet under way would come out
            // landed and still flying.
            if (Safe<bool>(delegate { return fleet.transferAssigned; }, false))
                throw new VerbError("fleet " + (int)fleet.ID
                    + " has a transfer assigned (transferAssigned=true) and Land "
                    + "does not clear the trajectory; the fleet would be landed and "
                    + "still under way");
            if (Safe<bool>(delegate { return fleet.inCombatOrWaitingForCombat; }, false))
                throw new VerbError("fleet " + (int)fleet.ID
                    + " is in combat or waiting for it (inCombatOrWaitingForCombat"
                    + "=true); Land teleports every ship into the docked formation "
                    + "while the combat still holds them");

            // A site carrying a hab is the engine's docking case, not its landing
            // case: LandOnSurfaceOperation branches on hab.IsBase and calls Dock,
            // and TIHabState.DockFleet then adds the fleet to this same site's
            // landedFleets, so a fleet docked at a base here is already a
            // bombardment target. Land alone would set dockedLocation to the site --
            // which makes dockedAtHab true through site.ref_hab -- without adding
            // the fleet to the hab's dockedFleets, and the load-time sweep only ever
            // removes entries from a list, so nothing would repair it. Any hab, not
            // just a base: DepartFromDockingLocation calls hab.LaunchFleet OR
            // habSite.LaunchFleet, never both, and TIHabState.LaunchFleet reaches
            // the site's landedFleets only when the hab IsBase, so a fleet landed
            // under a non-base hab is stranded in landedFleets with a null
            // dockedLocation the next time it departs.
            TIHabState siteHab = Safe<TIHabState>(delegate { return site.hab; }, null);
            if (siteHab != null)
                throw new VerbError(StateName(site) + " (" + (int)site.ID
                    + ") carries the hab " + StateName(siteHab) + " ("
                    + (int)siteHab.ID + ", habType="
                    + Safe<string>(delegate { return siteHab.habType.ToString(); }, "?")
                    + "); a fleet reaches a hab by docking rather than landing, and "
                    + "docking at a base puts the fleet in this site's landedFleets "
                    + "anyway, so a fleet docked there is already a bombardment "
                    + "target");

            // The last check before anything is written, and a check rather than a
            // try/catch because Land is not atomic: it writes dockedLocation = site
            // at IL_0021 and does not reach site.LandFleet(this) until IL_005c, and
            // in between its per-ship lambda calls
            // site.DeltaVToLandFromInterface_kps(fleet.orbitState, ...), which
            // dereferences site.parentBody, and dereferences the orbit it substitutes
            // out of parentBody.interfaceOrbits when the orbit argument is null. A
            // throw in there leaves the fleet pointing at this site, absent from its
            // landedFleets, with its previous membership already removed by the
            // departure below -- and the load-time sweep only trims landedFleets, it
            // never resets dockedLocation, so nothing repairs it.
            if (body == null)
                throw new VerbError("hab site " + (int)site.ID + " ("
                    + StateName(site) + ") has no parentBody, which Land dereferences "
                    + "while charging each ship for the landing");
            if (!Safe<bool>(delegate { return fleet.orbitState != null; }, false))
                throw new VerbError("fleet " + (int)fleet.ID + " has no orbitState, "
                    + "which Land passes to the site's landing delta-V and which the "
                    + "engine only substitutes for out of the body's interface orbits");
            // The third null in that window, and the one fleet.transfer below already
            // guards: Land reads get_ships() at IL_0027 and calls ForEach on it at
            // IL_0038, six instructions after it has written dockedLocation.
            if (!Safe<bool>(delegate { return fleet.ships != null; }, false))
                throw new VerbError("fleet " + (int)fleet.ID + " has no ships list, "
                    + "which Land calls ForEach on to charge each ship for the descent, "
                    + "after it has already pointed the fleet at the site");

            // The departure the engine makes first. DepartFromDockingLocation
            // cancels the operations that do not survive it, ends any bombardment
            // aimed at the old location, removes the fleet from the old hab's
            // dockedFleets AND the old site's landedFleets, and clears
            // dockedLocation. Skipping it is what leaves the stale membership the
            // load-time sweep later reports as "Bad fleet ... was included in
            // landedFleets in this savegame".
            //
            // It dereferences a parentBody of its own, on the site the fleet is
            // leaving rather than the one it is going to, and the check above reads
            // only the destination. So the origin is checked here on the same
            // grounds, and the call is wrapped: it cancels every operation that does
            // not survive a departure before it reaches that dereference, so a throw
            // inside it costs the fleet its orders, and Verbs.Execute would answer
            // that with a bare error carrying no before, no after and no changed.
            JToken departed = JValue.CreateNull();
            if (Safe<bool>(delegate { return fleet.dockedOrLanded; }, false))
            {
                TISpaceGameState from = fleet.dockedLocation;
                departed = from != null ? Describe(from) : JValue.CreateNull();
                TIHabSiteState fromSite = Safe<TIHabSiteState>(
                    delegate { return from != null ? from.ref_habSite : null; }, null);
                if (fromSite != null
                    && !Safe<bool>(delegate { return fromSite.parentBody != null; }, false))
                    throw new VerbError("the fleet is at hab site " + (int)fromSite.ID
                        + " (" + StateName(fromSite) + "), which has no parentBody, and "
                        + "DepartFromDockingLocation reads that body's interface orbits "
                        + "with no null check; the departure would throw after it had "
                        + "already cancelled the fleet's operations");
                try { fleet.DepartFromDockingLocation(); }
                catch (Exception e)
                {
                    throw new VerbError("DepartFromDockingLocation threw for fleet "
                        + (int)fleet.ID + ": " + Note(e) + "; nothing was landed, some "
                        + "operations may be cancelled, read the fleet back");
                }
            }
            o["departedFrom"] = departed;

            // The checks above cover every null the descent dereferences, so this
            // catch is for what a check cannot see: AssignFormation and
            // TeleportAllToFormation run between the dockedLocation write at IL_0021
            // and site.LandFleet at IL_005c, and neither is a value this verb can
            // inspect first.
            //
            // What the repair turns on is landedFleets, not dockedLocation. Land does
            // not stop at LandFleet: it goes on to pull the fleet out of
            // orbitState.assetsInOrbit (IL_0062-IL_0079) and fire
            // FleetArrivesAtDestination (IL_007a onward), so a throw in that tail leaves
            // a fleet that IS landed -- in the site's list and out of its orbit -- and
            // nulling dockedLocation on top of that would be the corruption rather than
            // the repair. Before LandFleet the fleet is in neither, and dockedLocation
            // was null on entry here on every path (the fleet was not docked, or
            // DepartFromDockingLocation cleared it at its own IL_019f), so putting it
            // back is a restore. Membership separates the two exactly.
            try { fleet.Land(site); }
            catch (Exception e)
            {
                // The one repair: pointed at the site but never in its landedFleets is
                // the state nothing else undoes. A membership that could not be READ is
                // not a membership of false, so an unreadable list writes nothing.
                JToken membership = Safe<JToken>(delegate
                {
                    List<TISpaceFleetState> landed = site.landedFleets;
                    return (JToken)new JValue(landed != null && landed.Contains(fleet));
                }, JValue.CreateNull());
                if (membership.Type == JTokenType.Boolean && !(bool)membership
                    && ReferenceEquals(Safe<TISpaceGameState>(
                        delegate { return fleet.dockedLocation; }, null), site))
                {
                    try { fleet.dockedLocation = null; }
                    catch (Exception) { }
                }
                throw new VerbError("TISpaceFleetState.Land threw for fleet "
                    + (int)fleet.ID + " at " + StateName(site) + " (" + (int)site.ID
                    + "): " + Note(e) + ". Land force-cancels the fleet's current "
                    + "operations before it writes anything, so some or all of them are "
                    + "gone. after: " + LandingState(fleet, site)
                        .ToString(Newtonsoft.Json.Formatting.None));
            }

            o["changed"] = true;
            o["after"] = LandingState(fleet, site);

            string warning = Warned(SiteWarnings(fleetBody, body), null);
            if (warning != null) o["warning"] = warning;
            return o;
        }

        // What is true about this fleet at this site whatever the call did, so the
        // no-op branch and the landing report both carry it.
        static List<string> SiteWarnings(TISpaceBodyState fleetBody,
            TISpaceBodyState body)
        {
            var warnings = new List<string>();
            if (body != null && Safe<bool>(delegate { return body.isEarth; }, false))
                warnings.Add("this site is on Earth, where BombardOperation's "
                    + "targets are regions, armies, space facilities, xenoforming "
                    + "and known alien sites -- never habs or landed fleets, so the "
                    + "landing is not a bombardment target");
            // Phrased as the disagreement rather than as a crossing that happened,
            // because this list is also reported on the already-landed branch, which
            // makes no Land call for a past tense to describe.
            if (fleetBody != null && body != null && !ReferenceEquals(fleetBody, body))
                warnings.Add("the fleet reads as at " + StateName(fleetBody)
                    + " while the site is at " + StateName(body)
                    + "; Land checks neither the body nor the delta-V it charges, "
                    + "so a landing across that gap moves the fleet between bodies "
                    + "with no transfer and no delta-V charged");
            return warnings;
        }

        static string Warned(List<string> warnings, string extra)
        {
            if (extra != null) warnings.Add(extra);
            return warnings.Count > 0 ? string.Join("; ", warnings.ToArray()) : null;
        }

        // Where the fleet thinks it is, and the two facts that decide whether the
        // landing is real: membership in the site's landedFleets, and the predicate
        // TIHabSiteState.PostGlobalGameStateCreateInit_2 applies to that list on
        // every load (location.ref_habSite == the site), which is what a landing has
        // to satisfy to survive a save and reload.
        static JObject LandingState(TISpaceFleetState fleet, TIHabSiteState site)
        {
            var o = new JObject();
            Put(o, "location", delegate
            {
                TISpaceGameState loc = fleet.dockedLocation;
                return loc != null ? Describe(loc) : JValue.CreateNull();
            });
            Put(o, "dockedOrLanded", delegate { return (JToken)fleet.dockedOrLanded; });
            Put(o, "landed", delegate { return (JToken)fleet.landed; });
            Put(o, "dockedAtHab", delegate { return (JToken)fleet.dockedAtHab; });
            Put(o, "dockedAtStation", delegate { return (JToken)fleet.dockedAtStation; });
            Put(o, "landedAtBase", delegate { return (JToken)fleet.landedAtBase; });
            Put(o, "landedInOutback", delegate { return (JToken)fleet.landedInOutback; });
            Put(o, "inLandedFleets", delegate
            {
                List<TISpaceFleetState> landed = site.landedFleets;
                return (JToken)(landed != null && landed.Contains(fleet));
            });
            Put(o, "locationIsSite", delegate
            {
                TISpaceGameState loc = fleet.location;
                return (JToken)ReferenceEquals(
                    loc != null ? loc.ref_habSite : null, site);
            });
            return o;
        }

        // The one write the engine undoes behind the caller's back: a landedFleets
        // entry whose fleet is deleted is dropped on the next load, with an error
        // logged, so a landing written for a dead state looks right for the rest of
        // the session and is gone afterwards. fleet.transfer below guards the same way.
        static void RequireLive(string what, TIGameState state)
        {
            // Read directly rather than through Safe. A Safe fallback here has to be
            // one of the two answers, and the only one that lets the verb continue is
            // false -- which makes a state mid-teardown whose getter throws walk
            // straight through the guard that exists for it. Every other Safe fallback
            // in this file fails toward refusing; a liveness question cannot express
            // that as a fallback value, so it is a catch instead.
            bool deleted, archived;
            try
            {
                deleted = state.deleted;
                archived = state.archived;
            }
            catch (Exception e)
            {
                throw new VerbError(what + " " + (int)state.ID + " (" + StateName(state)
                    + ") threw while reporting whether it is live: " + Note(e)
                    + "; a state that cannot answer that is treated as dead");
            }
            if (!deleted && !archived) return;
            throw new VerbError(what + " " + (int)state.ID + " (" + StateName(state)
                + ") is deleted=" + deleted + " archived=" + archived
                + "; a write onto a dead state does not survive a save and reload");
        }

        #endregion

        #region fleet.transfer

        // The registry instance, for the reason RegistryOp takes one.
        // OperationsManager.Initalize registers one TransferOperation into
        // fleetOperations and keys the lookup by GetType(), so this entry exists
        // wherever a campaign is loaded.
        static TransferOperation TransferOp()
        {
            return RegistryOp<TransferOperation>(typeof(TransferOperation),
                ", so neither the fleet-side gate nor the target list LaunchFleet's "
                + "ValidOperation applies can be asked");
        }

        // Every clause TransferOperation.ActorCanPerformOperation reads, observed rather
        // than guessed at, the way BombardRefusal reports the bombardment gate. The
        // method answers one bool and these are the values a caller has to see to fix
        // the setup.
        // Takes the registry instance the caller already resolved rather than resolving
        // it again: GetPossibleTargets walks every orbit in the game calling CanExplore,
        // and this whole string is built for a call that is about to throw.
        static string TransferRefusal(TISpaceFleetState fleet, TransferOperation transferOp)
        {
            var parts = new List<string>();
            parts.Add("mayLegallyStartATransfer=" + Safe<string>(
                delegate { return fleet.mayLegallyStartATransfer.ToString(); }, "?"));
            parts.Add("isCapableOfTransfering=" + Safe<string>(
                delegate { return fleet.isCapableOfTransfering.ToString(); }, "?"));
            // The one fleet.land above produces and nothing else in this fixture family
            // could: Land sets dockedLocation without touching orbitState, so a landed
            // fleet still answers every other check as if it were in its orbit.
            parts.Add("landed=" + Safe<string>(
                delegate { return fleet.landed.ToString(); }, "?"));
            parts.Add("dockedOrLanded=" + Safe<string>(
                delegate { return fleet.dockedOrLanded.ToString(); }, "?"));
            parts.Add("inCombat=" + Safe<string>(
                delegate { return fleet.inCombatOrWaitingForCombat.ToString(); }, "?"));
            parts.Add("transferAssigned=" + Safe<string>(
                delegate { return fleet.transferAssigned.ToString(); }, "?"));
            // The base clause, which runs for a human faction only and is the whole of
            // what the AI path skips.
            parts.Add("possibleTargets=" + Safe<int>(delegate
            {
                List<TIGameState> all = transferOp.GetPossibleTargets(fleet, null);
                return all != null ? all.Count : -1;
            }, -1));
            parts.Add("blockedBy=" + (Blocker(fleet, transferOp) ?? "none"));
            return string.Join(" ", parts.ToArray());
        }

        // AssignTrajectory has no undo: its setter is private and the method logs an
        // error and returns on a null argument rather than clearing (IL_0000-IL_001d),
        // so the backing field is the only way back. Called on the throw path alone,
        // where the alternative is a fleet that stays transferAssigned for the rest of
        // the campaign with nothing counting down and no load-time repair that drops it.
        static readonly FieldInfo trajectoryField = FindTrajectoryField();

        static FieldInfo FindTrajectoryField()
        {
            try
            {
                return typeof(TISpaceFleetState).GetField(
                    "<trajectory>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception) { return null; }
        }

        // Always ends a sentence, because it is appended to a refusal: the caller has to
        // learn whether the fleet was left holding an orphan.
        static string ClearTrajectory(TISpaceFleetState fleet)
        {
            // The fallback assumes a trajectory is there, so an unreadable fleet is
            // repaired rather than left alone.
            if (!Safe<bool>(delegate { return fleet.transferAssigned; }, true))
                return "The fleet holds no trajectory, so nothing was left assigned.";
            if (trajectoryField == null)
                return "The fleet is still transferAssigned and this build has no "
                    + "<trajectory>k__BackingField to clear it through, so the "
                    + "trajectory is orphaned and the fleet will refuse every order "
                    + "that reads it.";
            try { trajectoryField.SetValue(fleet, null); }
            catch (Exception undo)
            {
                return "The fleet is still transferAssigned and clearing the trajectory "
                    + "threw as well (" + Note(undo) + "), so it is orphaned.";
            }
            // Three answers, not two. The read above may assume the worst because its
            // fallback drives a repair and repairing a fleet that needed nothing costs
            // nothing. This read drives a claim about what the repair achieved, and a
            // read that threw is not a fleet that still reads transferAssigned. Saying
            // "orphaned" on an unreadable fleet sends the caller after a state that may
            // not exist.
            int assigned = Safe<int>(
                delegate { return fleet.transferAssigned ? 1 : 0; }, -1);
            if (assigned < 0)
                return "The trajectory was cleared and the fleet's transferAssigned "
                    + "could not be read back, so whether it is orphaned is unknown.";
            if (assigned > 0)
                return "The trajectory was cleared and the fleet still reads "
                    + "transferAssigned, so it is orphaned.";
            // Not "as it was". LaunchFleet confirms its operation at IL_0049, before
            // the logging and notification tail, so a throw after that point leaves a
            // registered TransferOperation beside the cleared trajectory. Nothing
            // flies -- ExecuteOperation no-ops while transferAssigned is false -- but
            // the fleet's operation list is not what it was, so read `after`.
            return "The trajectory already assigned was cleared. A TransferOperation "
                + "may still be registered, since LaunchFleet confirms it before the "
                + "tail that threw; nothing will fly on it, and 'after' reports it.";
        }

        // What the trajectory UI asks the planner for: ThrustProfileTool's
        // GenerateCandidateTrajectories requests 0x40 candidates with no placeholder
        // trajectories, no stop-on-first-success and a sample multiplier of 1. This verb
        // repeats that request unchanged.
        const int TrajectoryRequestSize = 64;

        // The loiter BuildSingleTrajectory_Common gives a trajectory that would otherwise
        // launch the instant it is assigned (IL_00fe-IL_0120). A candidate at this floor
        // is the engine's way of saying "leave now", so it is refused rather than
        // assigned; the epsilon is for the float round trip through TimeSpan.
        const double LaunchLoiterFloor_s = 1.0;
        const double LaunchLoiterEpsilon_s = 0.001;

        // Puts a fleet into the settled transfer-assigned state: a trajectory assigned, a
        // launch still ahead of it, the fleet still in its orbit, and a TransferOperation
        // counting down. Nothing else in the fixture family reaches it. action.invoke
        // reports AssignOrbitalTransfer's transfer parameter as nullOnly, so only a
        // literal null can be handed to it and never a real IOrbitalTransfer;
        // ApproachDockAction resolves on the spot instead of leaving the state standing;
        // and no console command plans a transfer.
        //
        // Two engine calls, in the order the trajectory UI makes them.
        // MasterTransferPlanner.RequestTrajectories builds the candidates and hands them
        // to the callback synchronously -- all three of its Invoke sites (IL_00e5,
        // IL_023a, IL_0979) are inside the method, so the array is in hand when it
        // returns. AssignTrajectory then stores one, and transferAssigned is exactly
        // trajectory != null. LaunchFleet settles it: on a launchTime still in the future
        // it takes its first branch (IL_0000-IL_0084), which drops the transfer-plan
        // visual, confirms a TransferOperation for the launch date and returns with the
        // fleet where it was. At or past that time the same call departs the fleet,
        // cancels its operations and flies it. Nothing is written by hand: the planner
        // sets the trajectory's fleet, originOrbit, destination, commonBarycenter and
        // assignedTime while building it, which is what the load-time trajectory repairs
        // check.
        //
        // The candidate is picked by loiter, not by comparing the launch to now. Every
        // trajectory the planner returns launches later than now by construction:
        // BuildSingleTrajectory_Common clamps a launch in the past up to assignedTime,
        // which it has just set to Now(), and then at IL_00fe-IL_0120 pushes launchTime a
        // second past it whenever the two are equal and forceImmediateLaunch is false --
        // which every BuildSingleTrajectory override passes, and the torch solver plans
        // from Now() itself. So a launch-now candidate arrives as now + 1s, a
        // "launchTime > now" test passes it, and picking the earliest picks exactly the
        // one that flies on the first tick. The wait is what separates the two, so the
        // pick is the largest launchTime - now and the refusal is at the one-second
        // floor. The only trajectory that launches exactly at assignedTime is
        // Trajectory_Patched.BuildEmptyTrajectory, for a destination the fleet is already
        // in, which is refused by identity below.
        static JToken FleetTransfer(JObject args)
        {
            TISpaceFleetState fleet = Arg<TISpaceFleetState>(args, "fleet");
            TIOrbitState destination = Arg<TIOrbitState>(args, "destination");
            RequireLive("fleet", fleet);
            RequireLive("orbit", destination);

            var o = new JObject();
            o["fleet"] = Describe(fleet);
            o["destination"] = Describe(destination);
            o["before"] = TransferState(fleet);

            // Already carrying one. Replacing a trajectory is the engine's separate
            // change-trajectory order, with rules of its own, so this answers as a no-op
            // the way fleet.land does for a fleet already at the site.
            if (Safe<bool>(delegate { return fleet.transferAssigned; }, false))
            {
                o["trajectory"] = JValue.CreateNull();
                o["loiter_s"] = JValue.CreateNull();
                o["candidates"] = 0;
                o["outcome"] = JValue.CreateNull();
                o["lowestDV_kps"] = JValue.CreateNull();
                o["changed"] = false;
                o["after"] = o["before"].DeepClone();
                o["warning"] = "the fleet already has a transfer assigned "
                    + "(transferAssigned=true); nothing was called";
                return o;
            }

            // Every refusal below is a check rather than a try/catch, because the two
            // writes are not one step: AssignTrajectory lands before LaunchFleet is
            // called, and a fleet left holding a trajectory with nothing counting down to
            // its launch is a state the engine never produces and never repairs.

            // The engine's own fleet-side gate, asked whole rather than reproduced
            // clause by clause, for the reason fleet.bombard above gives for asking the
            // same method: ActorCanPerformOperation subsumes every fleet-side
            // precondition. TransferOperation's reads, in its order, are the interrupt
            // check, the base ActorCanPerformOperation for a human faction only, then
            // mayLegallyStartATransfer, isCapableOfTransfering, !landed and
            // !inCombatOrWaitingForCombat.
            //
            // `landed` is why this matters. fleet.land above sets dockedLocation and
            // never nulls orbitState, and GetPossibleTargets keeps the whole
            // destination list for a docked or landed fleet (its orbit-removal branch
            // at IL_0020 is taken only when the fleet is NOT docked or landed), so a
            // landed fleet passed the origin check and the target check both and came
            // out landed AND transfer-assigned -- a pairing the engine refuses at
            // ActorCanPerformOperation IL_003d, and one fleet.land's own
            // transferAssigned guard prevents from the other direction.
            TransferOperation transferOp = TransferOp();
            bool actorCan;
            try { actorCan = transferOp.ActorCanPerformOperation(fleet, destination); }
            catch (Exception e)
            {
                // Not swallowed into a refusal about the fleet's state: the base clause
                // reaches GetPossibleTargets, which dereferences the fleet's faction
                // unguarded, so a throw here is a missing faction and not a verdict.
                throw new VerbError("TransferOperation.ActorCanPerformOperation threw "
                    + "for fleet " + (int)fleet.ID + ": " + Note(e) + ". Nothing about "
                    + "the transfer has been established");
            }
            if (!actorCan)
                throw new VerbError("fleet " + (int)fleet.ID + " cannot start a "
                    + "transfer: " + TransferRefusal(fleet, transferOp));

            TIOrbitState origin = Safe<TIOrbitState>(
                delegate { return fleet.orbitState; }, null);
            if (origin == null)
                throw new VerbError("fleet " + (int)fleet.ID + " has no orbitState, which "
                    + "is the origin the planner reads through ref_orbit and the orbit "
                    + "LaunchFleet leaves the fleet sitting in until the launch");
            if (ReferenceEquals(origin, destination))
                throw new VerbError("fleet " + (int)fleet.ID + " is already in "
                    + StateName(destination) + " (" + (int)destination.ID + "); the "
                    + "planner answers a destination the fleet is already in with an "
                    + "empty trajectory launching now (IL_0051-IL_0102), and LaunchFleet "
                    + "would launch on it immediately");

            // A NULL ships list no longer arrives here: isCapableOfTransfering reads it
            // inside the gate above, so that case comes back as the gate throwing. This
            // catches the empty list, which the gate can pass and the planner cannot use.
            List<TISpaceShipState> ships = Safe<List<TISpaceShipState>>(
                delegate { return fleet.ships; }, null);
            if (ships == null || ships.Count == 0)
                throw new VerbError("fleet " + (int)fleet.ID + " has no ships; the "
                    + "planner takes the fleet's cruise acceleration and delta-V from "
                    + "them and has nothing to plan with");
            float accel = Safe<float>(
                delegate { return fleet.cruiseAcceleration_mps2; }, 0f);
            if (!(accel > 0f))
                throw new VerbError("fleet " + (int)fleet.ID + " has "
                    + "cruiseAcceleration_mps2=" + accel.ToString(CultureInfo.InvariantCulture)
                    + "; the trajectory UI refuses to ask for candidates at all below "
                    + "this, and the planner divides transfer distances by it");

            // The gate that decides the invariant the refusals above protect, and the
            // only one that arrives after the planner would otherwise have had its say.
            // LaunchFleet confirms its TransferOperation through
            // TIOperationTemplate.OnOperationConfirm_Base, which returns false at IL_000b
            // when ValidOperation fails, and ValidOperation is exactly
            // GetPossibleTargets(fleet).Contains(destination). On a miss no operation is
            // registered while AssignTrajectory has already stored the trajectory, and no
            // repair on the fleet load path drops an orphaned one, so the fleet would
            // stay transferAssigned with nothing counting down for the rest of the
            // campaign, refusing every order that reads it.
            //
            // GetPossibleTargets is the list ValidOperation asks about, so it is the one
            // asked here rather than ValidTransferDestinationForFleet, which answers a
            // near neighbour of the same question. It reads only the fleet and its
            // faction, never a template field, so the registry instance resolved above
            // answers exactly what the fresh one LaunchFleet builds at IL_0031 would.
            List<TIGameState> targets;
            try { targets = transferOp.GetPossibleTargets(fleet, destination); }
            catch (Exception e)
            {
                // Reported as the throw it is. Swallowing it into the refusal below
                // would name exploration reach or a station's own orbit as the cause
                // when no list was ever produced, and the caller would change a
                // destination that was never in question. GetPossibleTargets reads
                // ref_fleet.faction at IL_000d and that faction's
                // TargetableOrbitsForNavigation at IL_0016, neither guarded.
                throw new VerbError("TransferOperation.GetPossibleTargets threw for "
                    + "fleet " + (int)fleet.ID + ": " + Note(e) + ". No target list was "
                    + "produced, so nothing has been established about "
                    + StateName(destination) + " (" + (int)destination.ID + ")");
            }
            if (targets == null || !targets.Contains(destination))
                throw new VerbError(StateName(destination) + " (" + (int)destination.ID
                    + ") is not in TransferOperation.GetPossibleTargets for fleet "
                    + (int)fleet.ID + ", so LaunchFleet would store the trajectory and "
                    + "register no operation for it, leaving the fleet transfer-assigned "
                    + "with nothing counting down and no repair that clears it. The list "
                    + "is the faction's TargetableOrbitsForNavigation, so the usual causes "
                    + "are an orbit outside the faction's exploration reach (the planner "
                    + "never consults CanExplore) and, for a fleet docked at a station, "
                    + "that station's own orbit, which the list strips at IL_00b8-IL_00ce");

            Trajectory[] candidates = null;
            double lowestDV_kps = 0.0;
            TransferResult result = null;
            try
            {
                result = MasterTransferPlanner.RequestTrajectories(
                    fleet, destination, TrajectoryRequestSize,
                    delegate(Trajectory[] found) { candidates = found; },
                    out lowestDV_kps, false, false, 1.0);
            }
            catch (Exception e)
            {
                // The trajectory UI wraps this same call in a catch of its own rather
                // than preventing the throw, so a caller gets it as a refusal here.
                throw new VerbError("the transfer planner threw for this fleet and "
                    + "destination: " + Note(e));
            }

            // Reported, not gated on: RequestTrajectories hands the candidates to the
            // callback at IL_0979 and only then decides its outcome at IL_097e and
            // IL_09a3, so a non-Success outcome can arrive alongside real trajectories.
            // Those trajectories are what the trajectory UI would offer, and they are
            // what this verb acts on.
            string outcome = Safe<string>(
                delegate { return result.Result.ToString(); }, "no TransferResult");
            int count = candidates != null ? candidates.Length : 0;

            // Longest wait wins. A candidate at the one-second floor is the engine's
            // launch-now case wearing a future timestamp, and LaunchFleet would park the
            // fleet a game-second from a departure the first clock tick carries out.
            TIDateTime now = TITimeState.Now();
            Trajectory chosen = null;
            double chosenLoiter_s = 0.0;
            double bestLoiter_s = 0.0;
            // Counted, not assumed. A launch time that cannot be read is not a
            // candidate at the launch-now floor: a Safe fallback of 0.0 would leave
            // bestLoiter_s at its initializer and make the refusal below claim "the
            // longest wait among them is 0s", which sends the caller at the destination
            // when the fault is a date read.
            int waitsRead = 0;
            for (int i = 0; i < count; i++)
            {
                Trajectory candidate = candidates[i];
                if (candidate == null) continue;
                TIDateTime launch = Safe<TIDateTime>(
                    delegate { return candidate.launchTime; }, null);
                if (launch == null) continue;
                double loiter_s;
                try { loiter_s = launch.DifferenceInSeconds(now); }
                catch (Exception) { continue; }
                waitsRead++;
                if (loiter_s > bestLoiter_s) bestLoiter_s = loiter_s;
                if (loiter_s <= LaunchLoiterFloor_s + LaunchLoiterEpsilon_s) continue;
                if (chosen != null && loiter_s <= chosenLoiter_s) continue;
                chosen = candidate;
                chosenLoiter_s = loiter_s;
            }
            if (chosen == null && count > 0 && waitsRead == 0)
                throw new VerbError("the planner returned " + count + " candidate(s) for "
                    + StateName(destination) + " (" + (int)destination.ID + ") with "
                    + "outcome " + outcome + ", and not one of them answered a launch "
                    + "time this call could read, so no wait could be measured. That is "
                    + "a date read failing, not a verdict about the destination");
            if (chosen == null && count == 0)
                throw new VerbError("the planner found no trajectory at all from "
                    + StateName(origin) + " (" + (int)origin.ID + ") to "
                    + StateName(destination) + " (" + (int)destination.ID + "); its "
                    + "outcome was " + outcome + ", which names what it ran out of");
            if (chosen == null)
                throw new VerbError("the planner returned " + count + " candidate(s) for "
                    + StateName(destination) + " (" + (int)destination.ID + ") with "
                    + "outcome " + outcome + ", and the longest wait among them is "
                    + bestLoiter_s.ToString("0.###", CultureInfo.InvariantCulture)
                    + "s, at or under the one-second loiter the engine gives a trajectory "
                    + "that means 'leave now'. Assigning it would park the fleet a "
                    + "game-second from a departure the first clock tick carries out. "
                    + "Pick a destination whose transfer has to wait for a launch window");

            // The one try/catch in this verb, and it is here because these two calls
            // ARE the state change: AssignTrajectory stores the trajectory and
            // LaunchFleet is what registers the countdown, so a throw in between leaves
            // exactly the orphan every refusal above exists to prevent. LaunchFleet's
            // future-launch branch reaches get_controller(), get_gameObjectLink() and
            // GameObjectExtensions.Remove<TransferPlanComponent> at IL_0018-IL_002c,
            // before it confirms anything -- Unity state a headless run can be missing.
            // Verbs.Execute would otherwise answer a bare error with no before, no
            // after and no changed, over a fleet left transfer-assigned for the rest of
            // the campaign.
            try
            {
                fleet.AssignTrajectory(chosen);
                // The argument is unread on the branch this takes: LaunchFleet returns
                // at IL_0084 without ever loading it when the launch is still ahead.
                fleet.LaunchFleet(false);
            }
            catch (Exception e)
            {
                throw new VerbError("the transfer threw between AssignTrajectory and "
                    + "the end of LaunchFleet: " + Note(e) + ". " + ClearTrajectory(fleet));
            }

            o["trajectory"] = DescribeTrajectory(chosen);
            o["loiter_s"] = chosenLoiter_s;
            o["candidates"] = count;
            o["outcome"] = outcome;
            // +Infinity is what RequestTrajectories initializes this out-param to, so a
            // return that never wrote it means no delta-V was measured. Answered as
            // null, the way the no-op branch above answers it, rather than riding as
            // the string "Infinity" under a key that is a number everywhere else.
            o["lowestDV_kps"] = double.IsNaN(lowestDV_kps) || double.IsInfinity(lowestDV_kps)
                ? JValue.CreateNull() : Num(lowestDV_kps);
            o["changed"] = true;
            JObject after = TransferState(fleet);
            o["after"] = after;

            var warnings = new List<string>();
            if (!Safe<bool>(delegate { return fleet.transferAssigned; }, false))
                warnings.Add("the fleet came out with no trajectory, so something "
                    + "cleared it between AssignTrajectory and this read");
            if (Safe<bool>(delegate { return fleet.inTransfer; }, false))
                warnings.Add("the fleet came out under way (inTransfer=true) rather than "
                    + "waiting in its orbit, so LaunchFleet took its launch branch");
            JToken registered = after["transferOperation"];
            JToken readError = registered != null && registered.Type == JTokenType.Object
                ? registered["error"] : null;
            if (readError != null)
                warnings.Add("the fleet's operation list could not be read ("
                    + readError.ToString() + "), so whether LaunchFleet registered a "
                    + "TransferOperation is unknown");
            else if (registered == null || registered.Type == JTokenType.Null)
                warnings.Add("LaunchFleet registered no TransferOperation, so the "
                    + "transfer is assigned with nothing counting down to its launch. "
                    + "The cause is not the destination: GetPossibleTargets held it "
                    + "when this call checked, which is the whole of what "
                    + "ValidOperation asks. Clear the trajectory before ordering this "
                    + "fleet again");
            if (warnings.Count > 0)
                o["warning"] = string.Join("; ", warnings.ToArray());
            return o;
        }

        // Where the fleet is and what it has been told to do, the pair fleet.land's
        // before/after reports for landing. transferAssigned is trajectory != null and
        // inTransfer is that and orbitState == null, so the two of them separate an
        // assigned transfer from one already under way.
        static JObject TransferState(TISpaceFleetState fleet)
        {
            var o = new JObject();
            Put(o, "orbit", delegate
            {
                TIOrbitState orbit = fleet.orbitState;
                return orbit != null ? Describe(orbit) : JValue.CreateNull();
            });
            Put(o, "location", delegate
            {
                TISpaceGameState loc = fleet.dockedLocation;
                return loc != null ? Describe(loc) : JValue.CreateNull();
            });
            Put(o, "dockedOrLanded", delegate { return (JToken)fleet.dockedOrLanded; });
            // The clause ActorCanPerformOperation refuses on and this state could not
            // show: a landed fleet reads dockedOrLanded true alongside a station-docked
            // one, and only this separates them.
            Put(o, "landed", delegate { return (JToken)fleet.landed; });
            Put(o, "transferAssigned", delegate { return (JToken)fleet.transferAssigned; });
            Put(o, "inTransfer", delegate { return (JToken)fleet.inTransfer; });
            Put(o, "trajectory", delegate
            {
                Trajectory t = fleet.trajectory;
                return t != null ? DescribeTrajectory(t) : JValue.CreateNull();
            });
            // Not through Put. Put turns a throw into null, and null here is also the
            // answer for "no operation was registered" -- the one fleet.transfer's
            // closing warning reads as proof of a specific cause. The two are kept
            // apart so a CurrentOperations() throw cannot be reported as a verdict.
            try { o["transferOperation"] = TransferOperationData(fleet); }
            catch (Exception e)
            {
                var failed = new JObject();
                failed["error"] = Note(e);
                o["transferOperation"] = failed;
            }
            return o;
        }

        static JToken DescribeTrajectory(Trajectory t)
        {
            var o = new JObject();
            Put(o, "model", delegate { return (JToken)t.GetDisplayName(); });
            Put(o, "destination", delegate
            {
                TISpaceGameState d = t.destination;
                return d != null ? Describe(d) : JValue.CreateNull();
            });
            Put(o, "launchTime", delegate { return (JToken)InvariantDate(t.launchTime); });
            Put(o, "arrivalTime", delegate { return (JToken)InvariantDate(t.arrivalTime); });
            Put(o, "dv_mps", delegate { return (JToken)t.DV_mps; });
            Put(o, "launched", delegate { return (JToken)t.launched; });
            return o;
        }

        // The countdown LaunchFleet registers, read back off the fleet rather than
        // assumed: OnOperationConfirm_Base only reaches OperationConfirmed when
        // TransferOperation.GetPossibleTargets holds the destination, so the operation is
        // the half of this state that can quietly fail to appear.
        static JToken TransferOperationData(TISpaceFleetState fleet)
        {
            List<OperationData> current = fleet.CurrentOperations();
            if (current == null) return JValue.CreateNull();
            for (int i = current.Count - 1; i >= 0; i--)
            {
                OperationData data = current[i];
                if (data == null || !(data.operation is TransferOperation)) continue;
                var op = new JObject();
                op["operation"] = data.operation.GetType().Name;
                Put(op, "target", delegate
                {
                    TIGameState target = data.target;
                    return target != null ? Describe(target) : JValue.CreateNull();
                });
                Put(op, "completes", delegate
                {
                    return data.completionDate != null
                        ? (JToken)InvariantDate(data.completionDate) : JValue.CreateNull();
                });
                return op;
            }
            return JValue.CreateNull();
        }

        #endregion
    }
}
