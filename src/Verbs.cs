using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.Debugging;
using PavonisInteractive.TerraInvicta.Systems.Bootstrap;
using PavonisInteractive.TerraInvicta.Systems.GameTime;

namespace TerraInvictaMCP
{
    // Thrown for expected failures; the message goes straight into the error frame.
    public class VerbError : Exception
    {
        public VerbError(string message) : base(message) { }
    }

    delegate JToken VerbFn(JObject args);

    class Verb
    {
        public VerbFn fn;
        public bool needsCampaign;
    }

    public static partial class Verbs
    {
        public const string ModVersion = "0.1.2";

        // query.state fans out over one object's members; a ref list longer than this
        // is a graph the client should walk with more queries instead.
        const int MaxRefListLength = 200;

        static readonly Dictionary<string, Verb> table = BuildTable();

        static Dictionary<string, Verb> BuildTable()
        {
            var t = new Dictionary<string, Verb>(StringComparer.Ordinal);
            Add(t, "ping", false, Ping);
            Add(t, "version", false, Version);
            Add(t, "verbs", false, VerbsList);
            Add(t, "console", true, Console);
            Add(t, "select", true, Select);
            Add(t, "query.time", true, QueryTime);
            Add(t, "query.factions", true, QueryFactions);
            Add(t, "query.habs", true, QueryHabs);
            Add(t, "query.fleets", true, QueryFleets);
            Add(t, "query.state", true, QueryState);
            Add(t, "time.pause", true, TimePause);
            Add(t, "time.play", true, TimePlay);
            Add(t, "time.speed", true, TimeSpeed);
            Add(t, "time.run_until", true, TimeRunUntil);
            Add(t, "saves.list", false, SavesList);
            Add(t, "saves.save", true, SavesSave);
            Add(t, "saves.load", false, SavesLoad);
            Add(t, "campaign.new", false, CampaignNew);
            Add(t, "game.main_menu", false, GameMainMenu);
            Add(t, "prompts.list", true, PromptsList);
            Add(t, "prompts.dismiss", true, PromptsDismiss);
            Add(t, "alert.choose", true, AlertChoose);
            Add(t, "spawn.fleet", true, SpawnFleet);
            Add(t, "combat.start", true, CombatStart);
            Add(t, "combat.status", true, CombatStatus);
            Add(t, "combat.autoresolve", true, CombatAutoresolve);
            Add(t, "combat.precombat", true, CombatPrecombat);
            Add(t, "combat.stance", true, CombatSetStance);
            Add(t, "fleet.bombard", true, FleetBombard);
            Add(t, "fleet.land", true, FleetLand);
            Add(t, "fleet.transfer", true, FleetTransfer);
            Add(t, "nation.policies", true, NationPolicies);
            Add(t, "nation.set_policy", true, NationSetPolicy);
            Add(t, "faction.diplomacy", true, FactionDiplomacy);
            Add(t, "faction.relations", true, FactionRelations);
            Add(t, "nation.set_stat", true, NationSetStat);
            Add(t, "kill.module", true, KillModule);
            Add(t, "module.power", true, ModuleSetPower);
            Add(t, "hab.build_module", true, HabBuildModule);
            Add(t, "mods.list", false, ModsList);
            Add(t, "query.template", false, QueryTemplate);
            Add(t, "query.enums", false, QueryEnums);
            Add(t, "harmony.patches", false, HarmonyPatches);
            Add(t, "query.localize", false, QueryLocalize);
            Add(t, "query.scenarios", false, QueryScenarios);
            Add(t, "query.scenario", true, QueryScenario);
            Add(t, "query.templateDupes", false, QueryTemplateDupes);
            Add(t, "assets.bundles", false, AssetsBundles);
            Add(t, "assets.resolve", false, AssetsResolve);
            Add(t, "query.designs", true, QueryDesigns);
            Add(t, "query.councilors", true, QueryCouncilors);
            Add(t, "query.nations", true, QueryNations);
            Add(t, "spawn.hab", true, SpawnHab);
            Add(t, "spawn.module", true, SpawnModule);
            Add(t, "spawn.army", true, SpawnArmy);
            Add(t, "spawn.councilor", true, SpawnCouncilor);
            Add(t, "spawn.alien_site", true, SpawnAlienSite);
            Add(t, "design.create", true, DesignCreate);
            Add(t, "design.delete", true, DesignDelete);
            Add(t, "design.auto", true, DesignAuto);
            Add(t, "ai.control", true, AiControl_Verb);
            Add(t, "query.autopilot", true, QueryAutopilot);
            Add(t, "ui.screenshot", false, UiScreenshot);
            Add(t, "ui.options", false, UiOptions);
            Add(t, "ui.view", true, UiView);
            Add(t, "ui.screen", true, UiScreen);
            Add(t, "ui.tooltip", true, UiTooltip);
            Add(t, "ui.describe", true, UiDescribe);
            Add(t, "mission.evaluate", true, MissionEvaluate);
            Add(t, "action.list", false, ActionList);
            Add(t, "action.invoke", true, ActionInvoke);
            Add(t, "test.crash_the_game", true, TestCrashTheGame);
            return t;
        }

        static void Add(Dictionary<string, Verb> t, string name, bool needsCampaign, VerbFn fn)
        {
            var v = new Verb();
            v.fn = fn;
            v.needsCampaign = needsCampaign;
            t[name] = v;
        }

        public static string Execute(Request req)
        {
            long id = req != null ? req.id : 0;
            // Main thread, inside Server.Drain. Recorded before the verb runs
            // rather than after, so a verb that takes a while does not read as
            // a client that went quiet. The stamp it replaces is kept: a reply
            // built INSIDE a verb has to answer how long the client was away
            // before this call, and the current stamp is this call itself.
            prevVerbAt = lastVerbAt;
            prevVerbKnown = lastVerbKnown;
            lastVerbAt = RealTime();
            lastVerbKnown = true;
            try
            {
                if (req == null) return Error(0, "empty request");
                if (req.parseError != null) return Error(id, req.parseError);
                Verb verb;
                if (!table.TryGetValue(req.cmd, out verb))
                    return Error(id, "unknown command '" + req.cmd + "'");
                if (verb.needsCampaign && !HasCampaign) return Error(id, "no campaign");
                JToken data = verb.fn(req.args != null ? req.args : new JObject());
                return Ok(id, data);
            }
            catch (VerbError e)
            {
                return Error(id, e.Message);
            }
            catch (Exception e)
            {
                return Error(id, Note(e));
            }
        }

        // Global values exist early in the load, minutes before visualizers finish;
        // mutating verbs in that window corrupt the campaign (observed: fleets spawned
        // mid-bootstrap crashed the visualizer loader). loadcycle100 is the game's own
        // "100% Loaded" flag, cleared again on unload.
        public static bool HasCampaign
        {
            get
            {
                try { return GameStateManager.GlobalValues() != null && GameControl.loadcycle100; }
                catch (Exception) { return false; }
            }
        }

        static string Ok(long id, JToken data)
        {
            var o = new JObject();
            o["id"] = id;
            o["ok"] = true;
            // On every envelope, success and failure both: a client learns the
            // stall from the calls it was making anyway, and the failures are
            // exactly where it would otherwise learn nothing. Response fields
            // may grow, so a client that does not know the key ignores it.
            o["clockStall"] = Stall();
            // Beside it for the same reason: a client that never asks the right
            // verb at the right moment would otherwise miss a campaign swap it
            // did not order, and every counter it keeps would describe a
            // campaign that is gone.
            o["campaignToken"] = CampaignTokenValue();
            o["campaignProcess"] = CampaignProcessValue();
            o["data"] = data != null ? data : JValue.CreateNull();
            return o.ToString(Formatting.None);
        }

        internal static string Error(long id, string message)
        {
            var o = new JObject();
            o["id"] = id;
            o["ok"] = false;
            o["clockStall"] = Stall();
            o["campaignToken"] = CampaignTokenValue();
            o["campaignProcess"] = CampaignProcessValue();
            o["error"] = string.IsNullOrEmpty(message) ? "error" : message;
            return o.ToString(Formatting.None);
        }

        static JToken Ping(JObject args)
        {
            return new JValue("pong");
        }

        static JToken Version(JObject args)
        {
            var o = new JObject();
            o["mod"] = ModVersion;
            o["game"] = Application.version;
            // Reported here as well as on query.time because this verb answers with no
            // campaign, and a crash caught at the main menu would otherwise be invisible
            // to a client whose only other window on it needs one.
            o["crashed"] = Crashed();
            return o;
        }

        // The game's own crash latch. GlobalInstaller.HandleException sets
        // GameControl.handlingException before anything else it does, then shows the
        // crash dialog, calls GameTimeManager.PauseAndBlock, clears every event listener
        // and sets TIInputManager.acceptingInput false.
        //
        // The bridge outlives all of that -- the mod's update runs off the mod loader's
        // own MonoBehaviour rather than the game's canvas or ECS pipeline -- so every
        // verb keeps answering over a dead game, and the blocked clock it reports reads
        // exactly like a modal alert. This field is the difference, and it is one static
        // bool read: the field has a single writer in the whole assembly and no clearer,
        // and it survives a scene load, so a save loaded into the same process comes back
        // permanently degraded. Recovery is a process restart.
        static JToken Crashed()
        {
            return Safe<JToken>(
                delegate { return new JValue(GameControl.handlingException); },
                JValue.CreateNull());
        }

        // The literal test.crash_the_game demands. Spelled out rather than a boolean so
        // no client's default-filling can supply it by accident.
        const string CrashConfirm = "crash-the-game";

        // The message the crash panel and both logs will carry. It says the game did
        // not fail on its own, because the person reading it may not be the person who
        // ran the verb.
        const string CrashFixtureMessage =
            "TerraInvictaMCP test fixture: this exception was raised deliberately by "
            + "the test.crash_the_game verb to exercise the game's crash handler. The "
            + "game did not fail on its own. Quit, launch it again and load a save.";

        // Puts the game into the crash state above, on purpose, so a client's crash
        // detection and recovery can be tested without waiting for a real defect.
        //
        // UnityEngine.Debug.LogException raises Application.logMessageReceived with
        // LogType.Exception, and that is the one LogType HandleException acts on: it
        // compares its `type` argument against 4 and returns immediately for anything
        // else. LogException logs rather than throws, so nothing unwinds through
        // Execute's catch-all and this verb answers normally from a game that is
        // already crashed. Unity dispatches the callback synchronously on the calling
        // thread, and verbs run on the main thread, so handlingException is already set
        // by the time LogException returns: the reply reports the flag itself rather
        // than an intention.
        //
        // The console verb cannot do this. TerminalController.ParseCommand does not
        // catch, so a throwing command propagates out of it, straight into Execute's
        // catch-all, which turns it into an ordinary JSON error. Unity's log handler
        // never sees it and HandleException never runs.
        static JToken TestCrashTheGame(JObject args)
        {
            if (Str(args, "confirm") != CrashConfirm)
                throw new VerbError(
                    "test.crash_the_game ENDS THIS SESSION. It raises a real unhandled "
                    + "exception so the game's own crash handler runs: the crash panel "
                    + "comes up, the clock is paused and blocked, every event listener "
                    + "is cleared and input is switched off. Nothing inside the process "
                    + "undoes that, so recovery is quitting the game, launching it "
                    + "again and loading a save; anything unsaved is lost. It exists to "
                    + "test crash detection and crash recovery and has no other use. To "
                    + "run it anyway, pass confirm=\"" + CrashConfirm + "\".");

            if (Safe<bool>(delegate { return GameControl.handlingException; }, false))
            {
                var already = new JObject();
                already["triggered"] = false;
                already["crashed"] = true;
                already["note"] =
                    "the game is already in its crash state. handlingException is a "
                    + "latch with no clearer, so HandleException returns immediately "
                    + "and a second exception would change nothing. Recovery is a "
                    + "process restart and a save reload.";
                return already;
            }

            string blocker = CrashHandlerBlocker();
            if (blocker != null)
                throw new VerbError(
                    "refusing to fire: " + blocker + ". HandleException would throw on "
                    + "that, fall into its own catch and call GameControl.Stop, which "
                    + "is Application.Quit outside the editor. The process would exit "
                    + "instead of entering the crash state this fixture exists to "
                    + "produce, and a client watching for the crash flag would see the "
                    + "bridge disappear instead.");

            UnityEngine.Debug.LogException(new Exception(CrashFixtureMessage));

            var o = new JObject();
            o["triggered"] = true;
            o["crashed"] = Crashed();
            o["exception"] = CrashFixtureMessage;
            o["recovery"] =
                "quit the game, launch it again, load a save. handlingException has one "
                + "writer in the assembly and no clearer, and it survives a scene load, "
                + "so loading a save into this process leaves it degraded.";
            if (!Safe<bool>(delegate { return GameControl.handlingException; }, false))
                o["note"] =
                    "the exception was logged and handlingException did NOT latch, so "
                    + "this process is not in the crash state and nothing has been "
                    + "tested. Either Debug.unityLogger.logEnabled is false, or no "
                    + "handler is subscribed to Application.logMessageReceived, or the "
                    + "game build has moved.";
            return o;
        }

        // Everything HandleException dereferences before the crash panel is up:
        // GameTimeManager.Singleton for PauseAndBlock, GameControl.canvasStack and its
        // OptionsScreen for ShowExceptionDialog, GameControl.eventManager for
        // ClearAllEvents. A null in any of them throws inside the handler, and the
        // handler's own catch quits the process. Returns the reason to refuse, or null
        // when the handler can run.
        //
        // The serialized fields ShowExceptionDialog then touches on the controller
        // itself (moddingText, crashExceptionText, crashPanel) are not checkable from
        // here, so a quit remains possible even past this. That is why the tool
        // documents both outcomes.
        static string CrashHandlerBlocker()
        {
            if (Safe<bool>(delegate { return GameTimeManager.Singleton == null; }, true))
                return "there is no GameTimeManager";
            if (Safe<bool>(delegate { return GameControl.canvasStack == null; }, true))
                return "there is no canvas stack";
            if (Safe<bool>(
                    delegate {
                        return !(GameControl.canvasStack.OptionsScreen
                                 is OptionsScreenController);
                    }, true))
                return "the options screen is not an OptionsScreenController, so there "
                     + "is no crash panel to show";
            if (Safe<bool>(delegate { return GameControl.eventManager == null; }, true))
                return "there is no event manager";
            return null;
        }

        // The dispatch table itself, so a client can detect verb drift against the
        // build it was written for.
        static JToken VerbsList(JObject args)
        {
            var names = new List<string>(table.Keys);
            names.Sort(StringComparer.Ordinal);
            var a = new JArray();
            for (int i = 0; i < names.Count; i++)
            {
                var o = new JObject();
                o["name"] = names[i];
                o["needsCampaign"] = table[names[i]].needsCampaign;
                a.Add(o);
            }
            var result = new JObject();
            result["verbs"] = a;
            return result;
        }

        static JToken Console(JObject args)
        {
            string line = Str(args, "line");
            if (string.IsNullOrEmpty(line)) throw new VerbError("missing arg 'line'");

            var container = GlobalInstaller.container;
            if (container == null) throw new VerbError("no DI container");
            TerminalController terminal = container.Resolve<TerminalController>();
            if (terminal == null) throw new VerbError("no terminal controller");

            var output = new List<string>();
            var errors = new List<string>();
            Action<string> onOutput = delegate(string s) { output.Add(s); };
            Action<string> onError = delegate(string s) { errors.Add(s); };

            terminal.OnOutput += onOutput;
            terminal.OnOutputError += onError;
            try
            {
                terminal.ParseCommand(line);
            }
            finally
            {
                terminal.OnOutput -= onOutput;
                terminal.OnOutputError -= onError;
            }

            var o = new JObject();
            o["output"] = ToArray(output);
            o["errors"] = ToArray(errors);
            return o;
        }

        static JToken Select(JObject args)
        {
            TIGameState state = Arg<TIGameState>(args, "id");
            GeneralControlsController.SetUIOtherSelectedState(state);
            return Describe(state);
        }

        static JToken QueryTime(JObject args)
        {
            GameTimeManager manager = GameTimeManager.Singleton;
            if (manager == null) throw new VerbError("no campaign");
            TIDateTime now = manager.currentTime;
            var o = new JObject();
            o["date"] = now != null ? InvariantDate(now) : null;
            o["speed"] = manager.currentSpeedIndex;
            o["paused"] = manager.Paused;
            // Paused is just speed index 0. A blocking prompt freezes time with the
            // index untouched, so a client polling for progress needs both.
            o["blocked"] = Blocked(manager);
            o["armed"] = runUntilTarget != null;
            o["run_until"] = runUntilText;
            // On the same reply as `blocked`, because a caller polling a frozen clock
            // has to be able to tell a modal decision from a dead game without a second
            // round trip. See Crashed.
            o["crashed"] = Crashed();
            // Here rather than on a verb of its own: a driver reads this reply
            // before every re-arm, and a second round trip would leave a window
            // between the reading and the arm.
            o["missionPhase"] = MissionPhase();
            // The envelope carries the seconds already; this is the diagnosis
            // that goes with them -- how long since anything called, and which
            // of the four ways the clock can be still this one is.
            o["stall"] = StallBlock(manager);
            // The same token the envelope carries, on the reply a driver already
            // reads every poll. Null between campaigns.
            o["campaignToken"] = CampaignTokenValue();
            o["campaignProcess"] = CampaignProcessValue();
            return o;
        }

        // The mission-phase machinery, read exactly the way
        // AiControl.MissionPhaseBusy reads it: the global phase flag, plus the
        // two per-faction flags that mean a phase is about to start or is still
        // winding down. Both faction flags are fsIgnore, so polling them is
        // load-safe. `collisions` is the engine's own "already active" branch,
        // counted by the StartNewMissionPhase prefix.
        static JToken MissionPhase()
        {
            bool active = false;
            bool prepping = false;
            bool planning = false;
            try
            {
                TIMissionPhaseState phase = GameStateManager.MissionPhase();
                active = phase != null && phase.phaseActive;
                TIFactionState[] factions = GameStateManager.AllFactions();
                if (factions != null)
                {
                    for (int i = 0; i < factions.Length; i++)
                    {
                        TIFactionState f = factions[i];
                        if (f == null) continue;
                        if (f.preppingForMissions) prepping = true;
                        if (f.planningMissions) planning = true;
                    }
                }
            }
            // A read that throws reports the phase as closed, which is what
            // every caller did before this key existed. The error is recorded
            // rather than swallowed.
            catch (Exception e) { Server.LastError = Note(e); }
            var o = new JObject();
            o["active"] = active;
            o["prepping"] = prepping;
            o["planning"] = planning;
            o["collisions"] = AiControl.MissionPhaseCollisions;
            return o;
        }

        // True when the block above says a phase is open. Prepping and planning
        // are reported but do not refuse an arm: they are set for whole
        // stretches of an ordinary tick and refusing on them would stop every
        // run, while only phaseActive names the branch that corrupts.
        static bool PhaseIsActive(JToken phase)
        {
            var o = phase as JObject;
            if (o == null) return false;
            JToken t = o["active"];
            return t != null && t.Type == JTokenType.Boolean && (bool)t;
        }

        static JToken Blocked(GameTimeManager manager)
        {
            return Safe<JToken>(delegate { return new JValue(manager.IsBlocked); }, JValue.CreateNull());
        }

        static JToken QueryFactions(JObject args)
        {
            var a = new JArray();
            TIFactionState[] factions = GameStateManager.AllFactions();
            if (factions == null) return a;
            for (int i = 0; i < factions.Length; i++)
            {
                TIFactionState f = factions[i];
                if (f == null) continue;
                var o = new JObject();
                o["id"] = (int)f.ID;
                o["name"] = StateName(f);
                Put(o, "alien", delegate { return (JToken)f.IsAlienFaction; });
                // isActivePlayer reads GameControl.control, which is torn down during a
                // scene swap.
                Put(o, "activePlayer", delegate { return (JToken)f.isActivePlayer; });
                o["resources"] = Resources(f);
                Put(o, "estimatedAlienHate", delegate { return Num(f.GetEstimatedAlienHate()); });
                a.Add(o);
            }
            return a;
        }

        static JToken Resources(TIFactionState f)
        {
            var o = new JObject();
            Dictionary<FactionResource, float> copy;
            try { copy = f.copyResources; }
            catch (Exception) { return null; }
            if (copy == null) return null;
            foreach (KeyValuePair<FactionResource, float> kv in copy)
                o[kv.Key.ToString()] = Num(kv.Value);
            return o;
        }

        static JToken QueryHabs(JObject args)
        {
            int factionFilter = OptionalInt(args, "faction");
            var a = new JArray();
            foreach (TIHabState hab in GameStateManager.IterateByClass<TIHabState>(false))
            {
                if (hab == null) continue;
                TIFactionState owner = FactionOf(hab);
                if (factionFilter >= 0 && (owner == null || (int)owner.ID != factionFilter)) continue;
                var o = new JObject();
                o["id"] = (int)hab.ID;
                o["name"] = StateName(hab);
                o["tier"] = hab.tier;
                o["type"] = hab.habType.ToString();
                o["faction"] = owner != null ? Describe(owner) : null;
                o["location"] = Where(hab);
                Put(o, "moduleCount", delegate { return (JToken)hab.numCompletedModules; });
                a.Add(o);
            }
            return a;
        }

        // Site first: a hab that sits on a body reports the body's site, and only a
        // free-flying one falls back to its orbit.
        static string Where(TIHabState hab)
        {
            try
            {
                TIGameState site = hab.ref_habSite;
                if (site != null) return StateName(site);
            }
            catch (Exception) { }
            try
            {
                TIGameState orbit = hab.ref_orbit;
                if (orbit != null) return StateName(orbit);
            }
            catch (Exception) { }
            return null;
        }

        static JToken QueryFleets(JObject args)
        {
            int factionFilter = OptionalInt(args, "faction");
            var a = new JArray();
            foreach (TISpaceFleetState fleet in GameStateManager.IterateByClass<TISpaceFleetState>(false))
            {
                if (fleet == null) continue;
                TIFactionState owner = FactionOf(fleet);
                if (factionFilter >= 0 && (owner == null || (int)owner.ID != factionFilter)) continue;
                var o = new JObject();
                o["id"] = (int)fleet.ID;
                o["name"] = StateName(fleet);
                o["faction"] = owner != null ? Describe(owner) : null;
                string location = null;
                try
                {
                    TIGameState at = fleet.location;
                    if (at != null) location = StateName(at);
                }
                catch (Exception) { }
                o["location"] = location;
                Put(o, "ships", delegate { return (JToken)(fleet.ships != null ? fleet.ships.Count : 0); });
                o["inCombat"] = fleet.inCombat;
                Put(o, "inCombatOrWaiting", delegate { return (JToken)fleet.inCombatOrWaitingForCombat; });
                a.Add(o);
            }
            return a;
        }

        static TIFactionState FactionOf(TIGameState state)
        {
            return Safe<TIFactionState>(delegate { return state.ref_faction; }, null);
        }

        // A path longer than this is a graph query, which this verb is not.
        const int MaxPathSegments = 16;

        // One dot-separated step: a member name and at most one list index.
        class Segment
        {
            public string name;
            public int index;
        }

        static JToken QueryState(JObject args)
        {
            bool includePrivate = Flag(args, "include_private");
            string root = Str(args, "root");
            JToken idToken = args != null ? args["id"] : null;
            bool hasId = idToken != null && idToken.Type != JTokenType.Null;
            bool hasRoot = !string.IsNullOrEmpty(root);
            if (hasId && hasRoot)
                throw new VerbError("pass 'id' or 'root', not both");
            if (!hasId && !hasRoot)
                throw new VerbError("pass 'id' (a game state id) or "
                    + "'root' (a static entry point, e.g. 'GameControl.control')");

            string pathText = Str(args, "path");
            object current = hasId
                ? (object)ById<TIGameState>(Int(args, "id"))
                : Root(root, includePrivate);
            current = Walk(current, ParsePath(pathText), includePrivate, "", 0);

            var o = new JObject();
            o["root"] = hasRoot ? (JToken)new JValue(root) : JValue.CreateNull();
            o["path"] = !string.IsNullOrEmpty(pathText)
                ? (JToken)new JValue(pathText) : JValue.CreateNull();
            o["type"] = current != null
                ? (JToken)new JValue(current.GetType().Name) : JValue.CreateNull();

            var state = current as TIGameState;
            if (state != null)
            {
                o["id"] = (int)state.ID;
                o["name"] = StateName(state);
                o["members"] = Members(state, includePrivate);
                return o;
            }
            if (current == null)
            {
                o["value"] = JValue.CreateNull();
                return o;
            }
            // Anything Value() or Element() renders whole is a value; everything else
            // gets the same member dump a game state gets.
            JToken scalar = ScalarToken(current);
            if (scalar != null)
            {
                o["value"] = scalar;
                return o;
            }
            JToken collection = Collection(current, current.GetType(), o);
            if (collection != null)
            {
                o["value"] = collection;
                return o;
            }
            o["members"] = Members(current, includePrivate);
            return o;
        }

        // The endpoint dump, identical in shape whatever the walk landed on.
        static JObject Members(object target, bool includePrivate)
        {
            var members = new JObject();
            List<MemberInfo> list = ReadableMembers(target.GetType(), includePrivate);
            for (int i = 0; i < list.Count; i++)
            {
                MemberInfo m = list[i];
                if (IsObsolete(m)) continue;
                FieldInfo f = m as FieldInfo;
                if (f != null)
                {
                    members[f.Name] = Read(target, f, null);
                    continue;
                }
                PropertyInfo p = (PropertyInfo)m;
                // A public property can still hide its getter.
                if (p.GetGetMethod(includePrivate) == null) continue;
                members[p.Name] = Read(target, null, p);
            }
            return members;
        }

        // Values rendered whole rather than dumped member by member. Templates collapse
        // to a data name, as they do inside an expanded list.
        static JToken ScalarToken(object v)
        {
            var template = v as TIDataTemplate;
            if (template != null)
            {
                return Safe<JToken>(
                    delegate { return new JValue(template.dataName); }, JValue.CreateNull());
            }
            Type t = v.GetType();
            if (v is string || v is TIDateTime || t.IsEnum) return Value(v);
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean:
                case TypeCode.Char:
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return Value(v);
            }
            return null;
        }

        #region Path walking

        // The cap is applied to the split, before a single segment is parsed: the line
        // reader accepts a megabyte, and anything that walks or resolves per part has to
        // be kept away from a path with half a million of them.
        static List<Segment> ParsePath(string path)
        {
            var segments = new List<Segment>();
            if (string.IsNullOrEmpty(path)) return segments;
            string[] parts = path.Split('.');
            if (parts.Length > MaxPathSegments)
                throw new VerbError("path has " + parts.Length + " segments; at most "
                    + MaxPathSegments);
            for (int i = 0; i < parts.Length; i++) segments.Add(ParseSegment(parts[i]));
            return segments;
        }

        // name, or name[n]. One indexer per segment: a second would need the element
        // type resolved mid-parse for no gain over splitting the path.
        static Segment ParseSegment(string text)
        {
            string part = text != null ? text.Trim() : "";
            if (part.Length == 0) throw new VerbError("empty segment in path");
            var seg = new Segment();
            seg.index = -1;
            int open = part.IndexOf('[');
            if (open >= 0)
            {
                if (part[part.Length - 1] != ']')
                    throw new VerbError("segment '" + part + "' has a malformed indexer");
                // Caught here so "a[1][2]" reports the real problem instead of failing
                // as a malformed integer.
                if (part.IndexOf('[', open + 1) >= 0)
                    throw new VerbError("segment '" + text + "' carries more than one "
                        + "indexer; split it across two segments");
                string inner = part.Substring(open + 1, part.Length - open - 2);
                int index;
                if (!int.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out index) || index < 0)
                    throw new VerbError("segment '" + part
                        + "' needs a non-negative integer index");
                seg.index = index;
                part = part.Substring(0, open).Trim();
                if (part.Length == 0)
                    throw new VerbError("segment '" + text + "' has an indexer but no member name");
                if (part.IndexOf('[') >= 0 || part.IndexOf(']') >= 0)
                    throw new VerbError("segment '" + text + "' carries more than one indexer");
            }
            else if (part.IndexOf(']') >= 0)
            {
                throw new VerbError("segment '" + part + "' has a malformed indexer");
            }
            seg.name = part;
            return seg;
        }

        // One step per segment, each read guarded. A failed step names the segment and
        // the type it failed on rather than answering null, because a null answer and a
        // wrong member name look identical to the caller.
        // offset continues the numbering a caller has already started, so the segments
        // walked out of a root follow the static member rather than restarting at 1.
        static object Walk(object current, List<Segment> segments, bool includePrivate,
            string where, int offset)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                Segment seg = segments[i];
                int n = offset + i + 1;
                if (current == null)
                    throw new VerbError("cannot read '" + seg.name + "' at " + where
                        + "segment " + n + ": the previous step was null");
                Type type = current.GetType();
                MemberInfo member = FindMember(type, seg.name, includePrivate, false);
                if (member == null)
                    throw new VerbError("no member '" + seg.name + "' on " + type.Name
                        + " at " + where + "segment " + n);
                current = ReadMember(current, member, seg.name, type, where, n);
                if (seg.index >= 0) current = Index(current, seg, where, n);
            }
            return current;
        }

        static object ReadMember(object target, MemberInfo member, string name, Type type,
            string where, int n)
        {
            try
            {
                FieldInfo f = member as FieldInfo;
                if (f != null) return f.GetValue(f.IsStatic ? null : target);
                var p = (PropertyInfo)member;
                MethodInfo getter = p.GetGetMethod(true);
                if (getter == null)
                    throw new VerbError("member '" + name + "' on " + type.Name
                        + " at " + where + "segment " + n + " has no getter");
                return p.GetValue(getter.IsStatic ? null : target, null);
            }
            catch (VerbError) { throw; }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                throw new VerbError("reading '" + name + "' on " + type.Name + " at "
                    + where + "segment " + n + " threw " + Note(inner));
            }
            catch (Exception e)
            {
                throw new VerbError("reading '" + name + "' on " + type.Name + " at "
                    + where + "segment " + n + " threw " + Note(e));
            }
        }

        // Lists and arrays only. A dictionary needs a key rather than an index, and
        // guessing which key type was meant is worse than refusing.
        static object Index(object current, Segment seg, string where, int n)
        {
            if (current == null)
                throw new VerbError("'" + seg.name + "' at " + where + "segment " + n
                    + " is null and cannot be indexed");
            var list = current as System.Collections.IList;
            if (list == null)
                throw new VerbError("'" + seg.name + "' at " + where + "segment " + n
                    + " is a " + current.GetType().Name + ", not a list or array");
            int count = Safe<int>(delegate { return list.Count; }, -1);
            if (count < 0)
                throw new VerbError("'" + seg.name + "' at " + where + "segment " + n
                    + " would not report a count");
            if (seg.index >= count)
                throw new VerbError("index " + seg.index + " is out of range for '"
                    + seg.name + "' at " + where + "segment " + n + " (" + count + " entries)");
            try { return list[seg.index]; }
            catch (Exception e)
            {
                throw new VerbError("indexing '" + seg.name + "' at " + where + "segment "
                    + n + " threw " + Note(e));
            }
        }

        // Fields before properties: a property getter runs game code, and some of them
        // mutate. Private members are not inherited by reflection, so the base chain is
        // walked by hand when they are asked for.
        static MemberInfo FindMember(Type type, string name, bool includePrivate, bool wantStatic)
        {
            BindingFlags scope = wantStatic ? BindingFlags.Static : BindingFlags.Instance;
            MemberInfo found = Lookup(type, name, BindingFlags.Public | scope);
            if (found == null)
            {
                found = Lookup(type, name,
                    BindingFlags.Public | scope | BindingFlags.IgnoreCase);
            }
            if (found != null || !includePrivate) return found;

            BindingFlags priv = BindingFlags.NonPublic | scope | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null; t = t.BaseType)
            {
                found = Lookup(t, name, priv);
                if (found == null) found = Lookup(t, name, priv | BindingFlags.IgnoreCase);
                if (found != null) return found;
            }
            return null;
        }

        // A member re-declared with a narrower type in a subclass leaves two of that name
        // in the hierarchy, and reflection refuses to pick: TIHabState, TISpaceBodyState,
        // TILagrangePointState and TISpaceFleetState all narrow TISpaceObjectState's
        // template that way. Swallowing that would answer "no member 'template'" for
        // something the dump plainly lists, so the ambiguity is resolved the way the dump
        // resolves it, by taking the most derived declaration.
        static MemberInfo Lookup(Type type, string name, BindingFlags flags)
        {
            try
            {
                FieldInfo f = type.GetField(name, flags);
                if (f != null) return f;
            }
            catch (AmbiguousMatchException)
            {
                MemberInfo hit = MostDerived(type, name, flags, true);
                if (hit != null) return hit;
            }
            catch (Exception) { }

            try
            {
                PropertyInfo p = type.GetProperty(name, flags);
                return p != null && p.GetIndexParameters().Length == 0 ? (MemberInfo)p : null;
            }
            catch (AmbiguousMatchException)
            {
                return MostDerived(type, name, flags, false);
            }
            catch (Exception) { return null; }
        }

        // Down from the type itself, one declaring type at a time, so the first hit is
        // the most derived one.
        static MemberInfo MostDerived(Type type, string name, BindingFlags flags, bool field)
        {
            BindingFlags declared = flags | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null; t = t.BaseType)
            {
                Type declaring = t;
                MemberInfo hit = Safe<MemberInfo>(delegate
                {
                    if (field) return declaring.GetField(name, declared);
                    PropertyInfo p = declaring.GetProperty(name, declared);
                    return p != null && p.GetIndexParameters().Length == 0 ? (MemberInfo)p : null;
                }, null);
                if (hit != null) return hit;
            }
            return null;
        }

        // "TypeName.Member" or "TypeName.Member.Member...". A namespace-qualified type
        // name carries dots of its own, so the longest prefix that resolves to a type
        // wins and everything after it is walked as members, the first one static.
        static object Root(string root, bool includePrivate)
        {
            string[] parts = root.Split('.');
            if (parts.Length < 2)
                throw new VerbError("arg 'root' must be 'TypeName.Member', e.g. "
                    + "'GameControl.control'");
            // Before any resolution: every prefix that misses costs a sweep over every
            // loaded assembly, the prefixes are distinct so the cache cannot help within
            // one call, and the queue drains on the main thread. A root with hundreds of
            // dots would hang the game rather than fail.
            if (parts.Length > MaxPathSegments)
                throw new VerbError("root has " + parts.Length
                    + " dot-separated parts; at most " + MaxPathSegments);
            for (int cut = parts.Length - 1; cut >= 1; cut--)
            {
                Type type = GameType(string.Join(".", parts, 0, cut));
                if (type == null) continue;

                Segment first = ParseSegment(parts[cut]);
                MemberInfo member = FindMember(type, first.name, includePrivate, true);
                if (member == null)
                    throw new VerbError("no static member '" + first.name + "' on "
                        + type.Name);
                object current = ReadMember(null, member, first.name, type, "root ", 1);
                if (first.index >= 0) current = Index(current, first, "root ", 1);

                var rest = new List<Segment>();
                for (int i = cut + 1; i < parts.Length; i++)
                    rest.Add(ParseSegment(parts[i]));
                // The static member was segment 1, so the rest continue from there.
                return Walk(current, rest, includePrivate, "root ", 1);
            }
            throw new VerbError("no type in the loaded assemblies matches any prefix of "
                + "root '" + root + "'");
        }

        // Resolved names are cached, misses included: the by-name sweep walks every type
        // in every loaded assembly and must not run twice for the same miss.
        static readonly Dictionary<string, Type> rootTypes =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        static Type GameType(string name)
        {
            Type cached;
            if (rootTypes.TryGetValue(name, out cached)) return cached;
            Type found = FindGameType(name);
            rootTypes[name] = found;
            return found;
        }

        static Type FindGameType(string name)
        {
            // Assembly-CSharp first, and a bare name is retried inside the game's own
            // namespace, where most of it lives.
            Assembly game = typeof(TIGameState).Assembly;
            Type t = LoadType(game, name);
            if (t == null) t = LoadType(game, "PavonisInteractive.TerraInvicta." + name);
            if (t != null) return t;

            Assembly[] loaded;
            try { loaded = AppDomain.CurrentDomain.GetAssemblies(); }
            catch (Exception) { return null; }
            for (int i = 0; i < loaded.Length; i++)
            {
                t = LoadType(loaded[i], name);
                if (t != null) return t;
            }
            // A bare class name in a namespace nobody guessed still resolves.
            for (int i = 0; i < loaded.Length; i++)
            {
                Type[] types;
                try { types = loaded[i].GetTypes(); }
                catch (Exception) { continue; }
                for (int j = 0; j < types.Length; j++)
                {
                    if (string.Equals(types[j].Name, name, StringComparison.Ordinal))
                        return types[j];
                }
            }
            return null;
        }

        #endregion

        // Getters run arbitrary game code, and plenty of them assume a live visualizer,
        // so a throwing member is reported in place and the dump continues.
        static JToken Read(object target, FieldInfo field, PropertyInfo prop)
        {
            try
            {
                object v = field != null ? field.GetValue(target) : prop.GetValue(target, null);
                return Value(v);
            }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                return new JValue("<error: " + inner.GetType().Name + ">");
            }
            catch (Exception e)
            {
                return new JValue("<error: " + e.GetType().Name + ">");
            }
        }

        static bool IsObsolete(MemberInfo m)
        {
            return Safe<bool>(delegate { return m.IsDefined(typeof(ObsoleteAttribute), true); }, false);
        }

        // Depth 1: scalars verbatim, game states collapsed, anything else named.
        static JToken Value(object v)
        {
            if (v == null) return JValue.CreateNull();

            var gs = v as TIGameState;
            if (gs != null) return Describe(gs);

            var date = v as TIDateTime;
            if (date != null) return new JValue(InvariantDate(date));

            var s = v as string;
            if (s != null) return new JValue(s);

            Type t = v.GetType();
            if (t.IsEnum) return new JValue(v.ToString());

            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Boolean:
                    return new JValue((bool)v);
                case TypeCode.Char:
                    return new JValue(v.ToString());
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                    return new JValue(Convert.ToInt64(v, CultureInfo.InvariantCulture));
                case TypeCode.UInt64:
                    return new JValue(Convert.ToUInt64(v, CultureInfo.InvariantCulture));
                case TypeCode.Single:
                    return Num((float)v);
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return Num(Convert.ToDouble(v, CultureInfo.InvariantCulture));
            }

            JToken list = ListValue(v, t, null);
            if (list != null) return list;
            return new JValue(t.Name);
        }

        // Endpoint only: a member dump never expands a dictionary (TIFactionState
        // has 46, some huge).
        static JToken Collection(object v, Type t, JObject report)
        {
            JToken list = ListValue(v, t, report);
            if (list != null) return list;
            return DictValue(v, t, report);
        }

        // What was rendered against what was there. The caps below bound the work, and
        // an answer cut at a cap is indistinguishable from a complete one unless the
        // response says so: `inspect id=<faction> path=intel` answering 200 pairs makes
        // "key K is absent" an undetectable false negative past the cap.
        // mission.evaluate reports attackingModifiersTotal beside its capped list for
        // the same reason. Only the endpoint gets these -- a member dump renders no
        // dictionary at all and cuts its lists at the same 200 as it always has.
        static void ReportCut(JObject report, int count, int shown, string cutBy)
        {
            if (report == null) return;
            report["valueCount"] = count >= 0
                ? (JToken)new JValue(count) : JValue.CreateNull();
            report["valueShown"] = shown;
            report["valueTruncated"] = cutBy != null;
            report["valueTruncatedBy"] = cutBy != null
                ? (JToken)new JValue(cutBy) : JValue.CreateNull();
        }

        // Lists and single-dimension arrays expand when their declared element type is
        // something depth 1 can render: a game state, a template, a string, an enum, or
        // a number or bool. A nested list, or a collection of arbitrary objects, stays
        // a type name. An over-long list is cut at the cap: at the endpoint the cut is
        // reported through ReportCut, and inside a member dump it stays silent, as it
        // has always been.
        static JToken ListValue(object v, Type t, JObject report)
        {
            Type element = null;
            if (t.IsArray && t.GetArrayRank() == 1) element = t.GetElementType();
            else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
                element = t.GetGenericArguments()[0];
            if (element == null || !Expandable(element)) return null;

            var items = v as System.Collections.IEnumerable;
            if (items == null) return null;
            var a = new JArray();
            int n = 0;
            bool cut = false;
            foreach (object item in items)
            {
                if (n >= MaxRefListLength) { cut = true; break; }
                n++;
                a.Add(Element(item));
            }
            // Every type this expands is a List<T> or a single-dimension array, so the
            // collection's own count is one property read rather than a second walk.
            var sized = v as System.Collections.ICollection;
            ReportCut(report, sized != null ? sized.Count : -1, n,
                cut ? "entries" : null);
            return a;
        }

        // The declared element type decides, so a list is expanded or named as a whole
        // rather than per entry.
        static bool Expandable(Type element)
        {
            if (typeof(TIGameState).IsAssignableFrom(element)) return true;
            if (typeof(TIDataTemplate).IsAssignableFrom(element)) return true;
            if (element == typeof(string) || element.IsEnum) return true;
            switch (Type.GetTypeCode(element))
            {
                case TypeCode.Boolean:
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return true;
            }
            return false;
        }

        // Templates collapse to their data name, the handle every other verb takes;
        // everything else an expandable list can hold is already a Value case. The
        // name is read through a guard because it is a property like any other.
        static JToken Element(object item)
        {
            if (item == null) return JValue.CreateNull();
            var template = item as TIDataTemplate;
            if (template != null)
            {
                // The error string keeps a throwing dataName getter distinguishable
                // from a genuinely null entry, matching the dump's <error: X> idiom.
                return Safe<JToken>(
                    delegate { return new JValue(template.dataName); },
                    new JValue("<error: dataName>"));
            }
            return Value(item);
        }

        // Both type args must be Expandable; duplicate rendered keys switch the
        // shape to pairs; MaxDictChars bounds size.
        const int MaxDictChars = 64 * 1024;

        static JToken DictValue(object v, Type t, JObject report)
        {
            if (!t.IsGenericType || t.GetGenericTypeDefinition() != typeof(Dictionary<,>))
                return null;
            Type[] sides = t.GetGenericArguments();
            if (!Expandable(sides[0]) || !Expandable(sides[1])) return null;
            var dict = v as System.Collections.IDictionary;
            if (dict == null) return null;

            var keys = new List<string>();
            var values = new List<JToken>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int chars = 0;
            string cutBy = null;
            System.Collections.IDictionaryEnumerator walk = dict.GetEnumerator();
            while (walk.MoveNext())
            {
                if (keys.Count >= MaxRefListLength) { cutBy = "entries"; break; }
                string key = KeyText(walk.Key);
                JToken value = Element(walk.Value);
                // Measured before it is kept, so the budget bounds what the response
                // carries. Testing the running total first let one entry of any size
                // through: a single prebuilt tooltip string is the case that matters,
                // and it is exactly the one this cap exists for.
                int size = key.Length + Safe<int>(
                    delegate { return value.ToString(Formatting.None).Length; }, 0);
                if (chars + size > MaxDictChars && keys.Count > 0)
                {
                    cutBy = "characters";
                    break;
                }
                chars += size;
                int seen;
                counts[key] = counts.TryGetValue(key, out seen) ? seen + 1 : 1;
                keys.Add(key);
                values.Add(value);
            }
            ReportCut(report, dict.Count, keys.Count, cutBy);

            // Which shape this came back as. The uniqueness test can only see the
            // entries the walk reached, and dictionary enumeration order is not stable
            // across mutations, so the same path can answer an object on one call and an
            // array of pairs on the next. A client reading `value` by key needs to be
            // told which it got rather than inferring it from the JSON type.
            bool unique = counts.Count == keys.Count;
            if (report != null)
                report["valueShape"] = unique ? "object" : "pairs";
            if (unique)
            {
                var o = new JObject();
                for (int i = 0; i < keys.Count; i++) o[keys[i]] = values[i];
                return o;
            }
            var pairs = new JArray();
            for (int i = 0; i < keys.Count; i++)
            {
                var pair = new JObject();
                pair["key"] = keys[i];
                pair["value"] = values[i];
                if (counts[keys[i]] > 1) pair["duplicateKey"] = true;
                pairs.Add(pair);
            }
            return pairs;
        }

        // The key's own text, which is what a JSON object member name has to be. Every
        // Expandable key type already renders to one through Element -- a template to
        // its data name, a string to itself, an enum to its member name, a number to
        // its digits -- except a game state, which Element describes as an object; that
        // one takes the "name (id)" form the refusals across this mod already use, so a
        // key is always a name rather than a nested JSON blob. Numbers are converted
        // under the invariant culture, since JValue.ToString() would pick up the
        // current one and hand back a decimal comma on some machines.
        static string KeyText(object key)
        {
            if (key == null) return "null";
            var state = key as TIGameState;
            if (state != null)
            {
                return Safe<string>(delegate
                {
                    string name = StateName(state);
                    return (string.IsNullOrEmpty(name) ? state.GetType().Name : name)
                        + " (" + (int)state.ID + ")";
                }, "<error: key>");
            }
            JToken token = Element(key);
            var scalar = token as JValue;
            if (scalar == null) return token.ToString(Formatting.None);
            if (scalar.Value == null) return "null";
            return Safe<string>(
                delegate { return Convert.ToString(scalar.Value, CultureInfo.InvariantCulture); },
                scalar.Value.ToString());
        }

        // NaN and the infinities are not JSON numbers; they ride as strings so a client
        // parser never chokes on a response. Floats stay floats so the widening noise of
        // a double cast stays out of the output.
        static JToken Num(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f))
                return new JValue(f.ToString(CultureInfo.InvariantCulture));
            return new JValue(f);
        }

        static JToken Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
                return new JValue(d.ToString(CultureInfo.InvariantCulture));
            return new JValue(d);
        }

        static JToken TimePause(JObject args)
        {
            Manager().Pause();
            clockParkedByDriver = true;
            return QueryTime(args);
        }

        static JToken TimePlay(JObject args)
        {
            clockParkedByDriver = false;
            Manager().Play();
            return QueryTime(args);
        }

        static JToken TimeSpeed(JObject args)
        {
            int level = Int(args, "level");
            if (level < 0) throw new VerbError("arg 'level' must be >= 0");
            GameTimeManager manager = Manager();
            int count = -1;
            try
            {
                var speeds = manager.currentSpeeds;
                if (speeds != null) count = speeds.Count;
            }
            catch (Exception) { }
            if (count > 0 && level >= count)
                throw new VerbError("arg 'level' must be < " + count);
            clockParkedByDriver = level == 0;
            manager.SetSpeed(level, false);
            return QueryTime(args);
        }

        static GameTimeManager Manager()
        {
            GameTimeManager manager = GameTimeManager.Singleton;
            if (manager == null) throw new VerbError("no time manager");
            return manager;
        }

        // Armed target for time.run_until. Held as a bare date, never a game object, so
        // a scene reload between frames leaves nothing stale behind.
        static TIDateTime runUntilTarget;
        static string runUntilText;

        // True only when the wire stopped the clock on purpose: time.pause,
        // time.speed level 0, or a fired run_until park. Vanilla's own
        // pauseTime events (councilor phase starts and the like) never set it,
        // which is how the engaged tick tells a requested stop from a pause
        // that expects a human -- it honors the former and resumes through the
        // latter (AiControl.ResumeThroughPause).
        static bool clockParkedByDriver;
        internal static bool ClockParkedByDriver { get { return clockParkedByDriver; } }

        // Everything held about one campaign; ids repeat across campaigns.
        // Not the seat: see ForgetSeat.
        internal static void ResetCampaignRunState()
        {
            runUntilTarget = null;
            runUntilText = null;
            clockParkedByDriver = false;
            Disarm();
            autoError = null;
            autoNote = null;
            ResetHistory();
            AiControl.ResetMissionPhaseCollisions();
        }

        static JToken TimeRunUntil(JObject args)
        {
            string text = Str(args, "date");
            if (string.IsNullOrEmpty(text)) throw new VerbError("missing arg 'date'");
            DateTime parsed;
            try
            {
                parsed = DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw new VerbError("arg 'date' must be yyyy-MM-dd");
            }
            var target = new TIDateTime(parsed.Year, parsed.Month, parsed.Day);
            string normalized = InvariantDate(target);
            JToken phase = MissionPhase();

            // Idempotent. A driver that re-states the target it already has is
            // asking for nothing, and doing the work anyway cleared
            // clockParkedByDriver -- so a park the wire had just been given was
            // un-parked by the next poll that re-armed. Nothing is touched here,
            // the park included.
            if (runUntilTarget != null && runUntilText == normalized)
            {
                var same = new JObject();
                same["armed"] = true;
                same["alreadyArmed"] = true;
                same["target"] = runUntilText;
                same["missionPhase"] = phase;
                return same;
            }

            // A NEW target while a mission phase is open is refused. Arming is a
            // statement of intent to run: it clears the park and the clock goes
            // back to full speed, and a semimonthly tick landing inside an open
            // phase is what corrupts it -- the engine's own guard clears
            // phaseActive and leaves factionsSignallingComplete populated, and
            // the stale list ends the next phase early. Close the phase first
            // (prompts.dismiss presses the assignment confirmation), or say
            // force=true and own the outcome.
            //
            // An engagement that is deferring colliding ticks is exempt, and only
            // while it is: the collision cannot happen, and a driver reading the
            // engagement skips its own hold, so a refusal here would leave that
            // run with the clock down and nothing that ever re-arms it. The
            // deferral needs the StartNewMissionPhase prefix installed, which is
            // why the test is not `engaged` alone.
            if (PhaseIsActive(phase) && !Flag(args, "force")
                && !AiControl.PhaseDeferralActive)
                throw new VerbError("mission_phase: a mission phase is open "
                    + "(missionPhase " + phase.ToString(Formatting.None) + "), and arming a "
                    + "new run_until target hands the clock back into it. Close the phase "
                    + "first -- prompts.dismiss presses the mission-assignment confirmation "
                    + "-- or pass force=true to arm anyway");

            runUntilTarget = target;
            runUntilText = normalized;
            clockParkedByDriver = false;
            var o = new JObject();
            o["armed"] = true;
            o["alreadyArmed"] = false;
            o["target"] = runUntilText;
            o["missionPhase"] = phase;
            return o;
        }

        // Main thread, once a frame. Each armed job is isolated so a failure in one does
        // not stop the other from being serviced.
        public static void Tick()
        {
            try { TickClockWatch(); }
            catch (Exception e) { Server.LastError = Note(e); }
            try { TickRunUntil(); }
            catch (Exception e) { Server.LastError = Note(e); }
            TickAutoresolve();
            // An engagement must not survive the campaign it was made in.
            try { AiControl.Tick(); }
            catch (Exception e) { Server.LastError = Note(e); }
        }

        // ------------------------------------------------------- clock watchdog
        // Watchdog: real-time stall and crawl detection; no allocation per frame,
        // so no Safe<T> here.

        // The sampled game date, packed into one comparable value. currentTime
        // hands back a fresh object, so the comparison has to be by value.
        static long clockKey;
        static bool clockKeyKnown;
        // Real seconds, from Unity's realtimeSinceStartup: it keeps running
        // while the game clock is stopped, which is the whole point. `movedAt`
        // is the last change of any size; `healthyAt` is the last change that
        // was not a crawl, and the stall is measured from it.
        static float clockMovedAt;
        static float clockHealthyAt;
        // Computed by the tick and read by the envelope, so building a response
        // never calls a Unity API.
        static float clockStallSeconds;
        static bool clockStallKnown;
        static bool clockCrawling;
        static bool hadCampaign;

        // Campaign token: counter behind a per-process prefix, since state ids and
        // dates repeat across campaigns.
        static readonly string campaignTokenPrefix =
            DateTime.UtcNow.Ticks.ToString("x", CultureInfo.InvariantCulture);
        static int campaignCount;
        static string campaignToken;

        // Which faction held the active-player seat when this campaign came up,
        // and whether anything has been latched yet.
        //
        // The engine's own isAI flag cannot answer "has the seat been moved".
        // GameControl.SetActivePlayer -- the only engine caller of
        // TIPlayerState.AssignAIStatus, and what the console's setfaction goes
        // through -- walks every player state and writes
        // isAI = (control.activePlayer != player.faction). So whichever faction
        // has just been given the seat always reads isAI false, moved or not,
        // and the faction that just lost it reads true. The identity of the
        // seated faction is the only thing that changes, so that is what is
        // latched and compared.
        static TIFactionState seatedFaction;
        static bool seatLatched;
        // Set by ForgetSeat and cleared by one place only: the no-campaign branch
        // of TickClockWatch. It holds the latch open across the frames between a
        // verb discarding the gamestate and the engine finishing the job. See
        // ForgetSeat for why those frames exist.
        static bool seatTeardown;
        static float lastVerbAt;
        static bool lastVerbKnown;
        // The stamp of the verb before the one being served. The watchdog's tick
        // wants the latest one -- it runs between calls, and the last verb is the
        // last verb. A reply wants this one, because the latest is the call being
        // answered and would report every silence as zero.
        static float prevVerbAt;
        static bool prevVerbKnown;
        static float lastRealTime;
        // Whole five-minute marks of the CURRENT stall already logged; reset
        // when the clock recovers, so each stall gets its own lines.
        static int stallWarnMarks;

        const float StallWarnEverySeconds = 300f;
        // A frame or two without a date change is not a stopped clock: the
        // engine advances time from its own update, which can miss a frame.
        const float MovingWindowSeconds = 1f;
        // Below this speed index, with nothing armed, the clock is crawling.
        const int CrawlSpeedIndex = 3;

        // Guarded because a tick must never throw. A failed read reports the
        // last value rather than jumping the measurement to zero.
        static float RealTime()
        {
            try { lastRealTime = Time.realtimeSinceStartup; }
            catch (Exception) { }
            return lastRealTime;
        }

        // Not a timestamp: one value that changes whenever any field of the
        // game date does, so "has the clock moved" is a single comparison and
        // no allocation. Each multiplier is one past its field's range.
        static long ClockKey(TIDateTime t)
        {
            long k = t.year;
            k = k * 13 + t.month;
            k = k * 32 + t.day;
            k = k * 25 + t.hour;
            k = k * 61 + t.minute;
            k = k * 61 + t.second;
            return k * 1000 + t.millisecond;
        }

        // Record whoever holds the seat right now. Guarded, and a read that
        // fails or finds no faction leaves the latch open for the next tick:
        // a wrong latch is worse than a late one, since everything downstream
        // compares against it. Refuses outright while a teardown is pending,
        // which is the same rule for the same reason.
        static void LatchSeat()
        {
            if (seatTeardown) return;
            TIFactionState seat = null;
            try
            {
                GameControl control = GameControl.control;
                if (control != null) seat = control.activePlayer;
            }
            catch (Exception) { return; }
            if (seat == null) return;
            seatedFaction = seat;
            seatLatched = true;
        }

        // Called by the verbs that replace or discard the gamestate. The next
        // tick with a campaign present latches whatever faction the new one
        // seated, so a load or a new campaign is never read as a moved seat.
        // Teardown is a coroutine, so HasCampaign stays true for a frame; the flag
        // stops the next tick re-latching the outgoing seat.
        internal static void ForgetSeat()
        {
            seatedFaction = null;
            seatLatched = false;
            seatTeardown = true;
        }

        // The faction latched at campaign start, for a reply that wants to name
        // the seat a caller should put back. Null when nothing is latched.
        internal static TIFactionState SeatedFaction
        {
            get { return seatLatched ? seatedFaction : null; }
        }

        // True when the active-player seat is held by a faction other than the
        // one this campaign came up with. That is what a console `setfaction`
        // does, and it is the state the human UI's answer handlers cannot
        // survive.
        //
        // An engagement is not a moved seat. AiControl.Engage flips isAI on the
        // faction ALREADY seated and never calls SetActivePlayer, so the
        // comparison below would answer false on its own; the explicit skip is
        // here so a later change to the engagement cannot turn it into a
        // refusal of the one pass an unattended run depends on.
        //
        // Every failure answers false. This gates prompts.dismiss, which the
        // unattended loop calls every poll, and a guard that refused on a read
        // it could not make would take that loop out on the first build that
        // moves a field.
        internal static bool ActivePlayerSeatMoved()
        {
            if (AiControl.Engaged) return false;
            if (!seatLatched || seatedFaction == null) return false;
            TIFactionState seat = null;
            try
            {
                GameControl control = GameControl.control;
                if (control != null) seat = control.activePlayer;
            }
            catch (Exception) { return false; }
            if (seat == null) return false;
            // Reference identity on purpose. TIGameState overloads == to compare
            // GameStateIDs, and ids are reused across campaigns, so an id match
            // across a gamestate swap would read as an unmoved seat. Within one
            // campaign there is one instance per faction, so the two agree.
            return !ReferenceEquals(seat, seatedFaction);
        }

        static void TickClockWatch()
        {
            float now = RealTime();
            // One try/catch, no lambda: this runs every frame.
            bool campaign = false;
            GameTimeManager manager = null;
            TIDateTime cur = null;
            bool crawl = false;
            try
            {
                // The property itself, not a copy of its body: it has its own
                // try/catch, allocates nothing, and a second statement of the
                // same two reads would drift from it.
                campaign = HasCampaign;
                if (campaign)
                {
                    manager = GameTimeManager.Singleton;
                    if (manager != null)
                    {
                        cur = manager.currentTime;
                        // Speed 1 and 2 with nothing armed. An armed run_until
                        // is a driver asking for a bounded slow run and says
                        // when it ends, so it is not a crawl however slow.
                        crawl = runUntilTarget == null
                            && manager.currentSpeedIndex < CrawlSpeedIndex;
                    }
                }
            }
            catch (Exception) { return; }
            if (!campaign)
            {
                // The transition out, taken once. This branch runs every frame the
                // start screen is up, and the reset below is not free; it is also
                // the only notice this mod gets of the in-game Options > Exit,
                // which goes through no verb and no hook of ours.
                if (hadCampaign)
                {
                    hadCampaign = false;
                    campaignToken = null;
                    ResetCampaignRunState();
                }
                // Nothing to measure between campaigns.
                clockKeyKnown = false;
                clockStallKnown = false;
                clockStallSeconds = 0f;
                clockCrawling = false;
                stallWarnMarks = 0;
                // The seat belonged to the campaign that is gone. This is also
                // the one place a pending teardown clears, so it is cleared
                // after the call: ForgetSeat sets the flag.
                ForgetSeat();
                seatTeardown = false;
                return;
            }
            // A campaign is present and nothing holds the seat yet: either this
            // is its first frame, or a verb that replaces the gamestate cleared
            // the latch. Ahead of the time-manager test below, because the seat
            // is set before the clock is up and a campaign whose manager is not
            // ready yet would otherwise go unlatched.
            //
            // The second case is why LatchSeat refuses while a teardown is
            // pending: the campaign a load or a return to the menu is discarding
            // is still present on the frames right after the verb runs, and this
            // line would re-latch it.
            if (!seatLatched) LatchSeat();
            if (manager == null || cur == null) return;
            long key = ClockKey(cur);
            if (!hadCampaign)
            {
                // First frame of a campaign. The timer restarts here, or a save
                // reloaded to the date it was saved at would inherit the stall
                // of the campaign it replaced.
                hadCampaign = true;
                // The campaign this process is now in. Bumped here rather than in
                // the verbs, so a campaign started from the start screen by hand
                // gets a token of its own like any other.
                campaignCount++;
                campaignToken = campaignTokenPrefix + "-"
                    + campaignCount.ToString(CultureInfo.InvariantCulture);
                // No reset here. Everything a campaign leaves behind is cleared on
                // the way OUT, in the branch above, which every teardown passes
                // through -- the in-game Options > Exit included, since
                // loadcycle100 goes false before the next campaign is up. Resetting
                // on the way IN would instead discard what a driver armed in this
                // same frame: OnUpdate drains the request queue before it calls
                // this tick, so a client that read `campaign: true` and armed
                // time.run_until in that window was answered `armed: true` and then
                // had the target nulled a few microseconds later.
                clockKeyKnown = false;
                stallWarnMarks = 0;
                clockMovedAt = now;
                clockHealthyAt = now;
            }
            if (!clockKeyKnown)
            {
                clockKey = key;
                clockKeyKnown = true;
            }
            else if (key != clockKey)
            {
                clockKey = key;
                clockMovedAt = now;
                // A crawl is movement, so it keeps `movedAt` fresh and the state
                // reads `running`; it does not clear the stall.
                if (!crawl)
                {
                    clockHealthyAt = now;
                    stallWarnMarks = 0;
                }
            }
            clockCrawling = ClockIsMoving(now) && crawl;
            clockStallSeconds = now - clockHealthyAt;
            clockStallKnown = true;
            int marks = (int)(clockStallSeconds / StallWarnEverySeconds);
            if (marks <= stallWarnMarks) return;
            stallWarnMarks = marks;
            // Only once something has driven this session through the bridge.
            // The mod ships to players, and a player who pauses for five
            // minutes with it installed has done nothing wrong; the line is
            // about a harness whose clock stopped. The marks advance either
            // way, so a client that connects mid-stall gets the next one.
            if (!lastVerbKnown) return;
            // One line per whole five minutes, through the UMM logger, which
            // adds the [TerraInvictaMCP] prefix and writes to Player.log
            // (log_tail which=player). The four values are the diagnosis: no
            // verbs for twenty minutes means the agent is thinking or dead,
            // verbs every few seconds over a stopped clock means an agent loop,
            // and a named prompt means a decision nobody is taking.
            try
            {
                string prompt = BlockingPromptName();
                Main.Log.Log("clock stalled " + (int)clockStallSeconds
                    + " s, state=" + ClockStateName(manager, now)
                    + ", prompt=" + (string.IsNullOrEmpty(prompt) ? "none" : prompt)
                    + ", last verb " + SinceLastVerbText() + " s ago");
            }
            catch (Exception e) { Server.LastError = Note(e); }
        }

        static bool ClockIsMoving(float now)
        {
            return clockKeyKnown && now - clockMovedAt < MovingWindowSeconds;
        }

        // Named from observed movement plus the manager's own flags. `combat`
        // is the SpaceCombat view, where the strategic clock is skipped while
        // paused and blocked both read normal; `stopped` is a clock that is not
        // moving with none of the flags set to explain it.
        static string ClockStateName(GameTimeManager manager, float now)
        {
            if (ClockIsMoving(now)) return "running";
            if (Safe<bool>(delegate { return manager.Paused; }, false)) return "paused";
            if (Safe<bool>(delegate { return manager.IsBlocked; }, false)) return "blocked";
            if (InSpaceCombatView()) return "combat";
            return "stopped";
        }

        static bool InSpaceCombatView()
        {
            return Safe<bool>(delegate
            {
                GameControl control = GameControl.control;
                if (control == null) return false;
                ViewControl views = control.viewMgr;
                return views != null && views.currentView == ViewType.SpaceCombat;
            }, false);
        }

        // The first prompt holding the active player's clock, for the log line.
        // Same two lists TIPromptQueueState.anyActivePlayerBlocking counts.
        static string BlockingPromptName()
        {
            try
            {
                TIPromptQueueState queue = GameStateManager.PromptQueue();
                if (queue == null) return null;
                string name = FirstPromptName(queue.activePlayerFactionPromptList);
                if (name != null) return name;
                return FirstPromptName(queue.activePlayerNationPromptList);
            }
            catch (Exception) { return null; }
        }

        static string FirstPromptName(List<Prompt> list)
        {
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                // Prompt is a struct, so there is no null entry to skip.
                Prompt prompt = list[i];
                string name = Safe<string>(delegate { return prompt.name; }, null);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            return null;
        }

        static string SinceLastVerbText()
        {
            if (!lastVerbKnown) return "?";
            return ((int)(RealTime() - lastVerbAt)).ToString(CultureInfo.InvariantCulture);
        }

        // Seconds of stall, or null when there is nothing to measure: no
        // campaign, or no tick yet since one loaded.
        static JToken Stall()
        {
            if (!clockStallKnown) return JValue.CreateNull();
            return new JValue(Math.Round((double)clockStallSeconds, 1));
        }

        // The campaign token as a JSON value: a string while a campaign is loaded,
        // null between campaigns. One static read, no Unity API, so it costs an
        // envelope nothing and is safe on whichever thread builds the reply.
        static JToken CampaignTokenValue()
        {
            string token = campaignToken;
            return token != null ? (JToken)new JValue(token) : JValue.CreateNull();
        }

        // The token's prefix on a key of its own: a name for the process answering,
        // taken once at mod load and never written again, so it is a string on
        // every reply including the ones sent between campaigns.
        //
        // The token alone cannot say which process minted it. The counter behind
        // the prefix restarts at 1 in every process, so a client that kept a
        // campaign token across a game it did not replace -- a person relaunching
        // at the console -- can be handed "<other prefix>-1" for a campaign it
        // never ordered and take it for the one it did. Comparing this key is how
        // a client tells a new campaign from a new game.
        static JToken CampaignProcessValue()
        {
            return new JValue(campaignTokenPrefix);
        }

        // {seconds, sinceLastVerbSeconds, state, crawl} for query.time.
        //
        // The silence is measured from the PREVIOUS verb. This block is built
        // inside a verb, so the current stamp is the call being answered and the
        // key would read 0.0 on every reply -- which is the one value that means
        // "a client is calling constantly", the opposite of what a client asking
        // after a quiet stretch is being told. Null until a second verb runs.
        static JToken StallBlock(GameTimeManager manager)
        {
            float now = RealTime();
            var o = new JObject();
            o["seconds"] = Stall();
            o["sinceLastVerbSeconds"] = prevVerbKnown
                ? (JToken)new JValue(Math.Round((double)(now - prevVerbAt), 1))
                : JValue.CreateNull();
            o["state"] = clockStallKnown
                ? (JToken)new JValue(ClockStateName(manager, now))
                : JValue.CreateNull();
            o["crawl"] = clockCrawling;
            return o;
        }

        // Disarms before pausing so a failed Pause cannot leave a target that fires forever.
        static void TickRunUntil()
        {
            TIDateTime target = runUntilTarget;
            if (target == null) return;
            if (!HasCampaign) return;
            GameTimeManager manager = GameTimeManager.Singleton;
            if (manager == null) return;
            TIDateTime now = manager.currentTime;
            if (now == null) return;
            if (now < target) return;
            runUntilTarget = null;
            runUntilText = null;
            // Parked before Pause: AiControl.Tick runs later this same frame
            // and must already see the park, or it resumes right through the
            // stop the driver asked for.
            clockParkedByDriver = true;
            manager.Pause();
        }

        // Which extension the game writes depends on a profile setting, so both are listed.
        static readonly string[] SaveExtensions = new string[] { ".gz", ".json" };

        static JToken SavesList(JObject args)
        {
            string folder = SaveFolder();
            var a = new JArray();
            if (!Directory.Exists(folder)) return a;
            for (int e = 0; e < SaveExtensions.Length; e++)
            {
                string[] paths = Directory.GetFiles(folder, "*" + SaveExtensions[e]);
                Array.Sort(paths, StringComparer.Ordinal);
                for (int i = 0; i < paths.Length; i++)
                {
                    var o = new JObject();
                    o["name"] = Path.GetFileNameWithoutExtension(paths[i]);
                    o["extension"] = SaveExtensions[e];
                    try
                    {
                        o["mtime"] = File.GetLastWriteTime(paths[i])
                            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    }
                    catch (Exception) { o["mtime"] = null; }
                    a.Add(o);
                }
            }
            return a;
        }

        static JToken SavesSave(JObject args)
        {
            string name = SaveName(args);
            // Every vanilla save path asks this first: a save-blocking prompt or an
            // autoresolving combat means the gamestate is mid-change.
            if (SaveMenuController.SavingIsBlocked()) throw new VerbError("saving blocked");
            string path = TIUtilities.GetSaveFilePath(name);
            if (!GameStateManager.SaveAllGameStates(path, true))
                throw new VerbError("save failed");
            var o = new JObject();
            o["path"] = path;
            return o;
        }

        static JToken SavesLoad(JObject args)
        {
            string name = SaveName(args);
            string path = ExistingSavePath(name, Str(args, "extension"));
            if (path == null) throw new VerbError("no save named '" + name + "'");

            // No Singleton on this controller; the component lives on the pause menu
            // inside the running scene, which is inactive.
            LoadMenuController menu = Require<LoadMenuController>("load menu controller");
            if (menu.importMode) throw new VerbError("load menu is in import mode");
            if (menu.loadingScreen == null) throw new VerbError("load menu has no loading screen");
            // LoadSaveFilePath destroys the campaign's view data before it touches its
            // scene manager, so every reason to refuse has to be settled up front.
            EnsureSceneManager(menu);

            // Anything armed against the outgoing campaign must not act on the incoming
            // one. State ids are reused across campaigns, so an armed autoresolve would
            // happily bind whatever combat inherits the id and start submitting stances.
            // Done here as well as from the watchdog's transition, because this verb
            // acts a frame or more before the engine reports the campaign gone.
            ResetCampaignRunState();
            // The seat: the save names its own active player, which need not
            // be the faction seated in the campaign this replaces. Cleared here
            // rather than re-read, because the load is asynchronous and the seat
            // is not the new one yet; the tick latches it once the campaign is up.
            ForgetSeat();
            menu.LoadSaveFilePath(path);

            var o = new JObject();
            o["loading"] = true;
            o["path"] = path;
            return o;
        }

        static readonly FieldInfo sceneManagerField = SceneManagerField();

        static FieldInfo SceneManagerField()
        {
            try
            {
                return typeof(LoadMenuController).GetField("sceneManager",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception) { return null; }
        }

        // The menu's scene manager is injected from Unity's Start, which never runs on an
        // object that has stayed inactive since the scene loaded. Backfill it the same way
        // Start would.
        static void EnsureSceneManager(LoadMenuController menu)
        {
            if (sceneManagerField == null) throw new VerbError("no sceneManager field on load menu");
            if (sceneManagerField.GetValue(menu) != null) return;

            SceneManager scenes = null;
            try
            {
                var container = SolarSystemInstaller.container;
                if (container != null) scenes = container.Resolve<SceneManager>();
            }
            catch (Exception) { }
            if (scenes == null)
            {
                try { scenes = SceneManager.self; }
                catch (Exception) { }
            }
            if (scenes == null) throw new VerbError("no scene manager");
            sceneManagerField.SetValue(menu, scenes);
        }

        static string SaveFolder()
        {
            string folder = CreateSaveFileScrollList.GetSaveFolderPath();
            if (string.IsNullOrEmpty(folder)) throw new VerbError("no save folder");
            return folder;
        }

        // The game writes through Wine, where these still name devices rather than files:
        // a save to one of them reports success and leaves nothing on disk.
        static readonly string[] ReservedNames = new string[] {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        // Save names address one folder, so anything that could walk out of it is refused.
        static string SaveName(JObject args)
        {
            string name = Str(args, "name");
            if (string.IsNullOrEmpty(name)) throw new VerbError("missing arg 'name'");
            name = name.Trim();
            if (name.Length == 0) throw new VerbError("arg 'name' is empty");
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0
                || name.IndexOf(':') >= 0 || name.IndexOf("..") >= 0)
                throw new VerbError("arg 'name' must be a plain file name");

            // A device name claims every extension, so the stem is what has to be checked.
            int dot = name.IndexOf('.');
            string stem = dot >= 0 ? name.Substring(0, dot) : name;
            for (int i = 0; i < ReservedNames.Length; i++)
            {
                if (string.Equals(stem, ReservedNames[i], StringComparison.OrdinalIgnoreCase))
                    throw new VerbError("arg 'name' is a reserved device name");
            }
            return name;
        }

        // The configured extension depends on a profile setting, so both are tried.
        //
        // `extension` names which one, for the case the search cannot decide: both
        // twins of a stem on disk, the caller holding the mtime of one of them. It
        // has to be an argument of its own, because the name cannot carry it --
        // GetSaveFilePath APPENDS the profile's extension to whatever it is given,
        // so "Autosave.json" resolves to "Autosave.json.gz" and exists nowhere.
        // Refused unless it is one the game writes, since it reaches a file path.
        static string ExistingSavePath(string name, string extension)
        {
            if (!string.IsNullOrEmpty(extension))
            {
                string want = extension.StartsWith(".") ? extension : "." + extension;
                bool known = false;
                for (int i = 0; i < SaveExtensions.Length; i++)
                {
                    if (string.Equals(want, SaveExtensions[i],
                            StringComparison.OrdinalIgnoreCase))
                    {
                        want = SaveExtensions[i];
                        known = true;
                        break;
                    }
                }
                if (!known)
                    throw new VerbError("arg 'extension' must be one of "
                        + string.Join(", ", SaveExtensions) + ", not '" + extension + "'");
                string exact = Path.Combine(SaveFolder(), name + want);
                return File.Exists(exact) ? exact : null;
            }
            string preferred = TIUtilities.GetSaveFilePath(name);
            if (File.Exists(preferred)) return preferred;
            string folder = SaveFolder();
            for (int i = 0; i < SaveExtensions.Length; i++)
            {
                string path = Path.Combine(folder, name + SaveExtensions[i]);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // TIDateTime.ToString(string) forwards to DateTime.ToString(string), which picks up
        // the current culture's calendar and digits. The date fields are plain ints, so the
        // frame is built from them under the invariant culture.
        static string InvariantDate(TIDateTime t)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "{0:D4}-{1:D2}-{2:D2} {3:D2}:{4:D2}:{5:D2}",
                t.year, t.month, t.day, t.hour, t.minute, t.second);
        }

        static JToken Describe(TIGameState state)
        {
            var o = new JObject();
            o["id"] = (int)state.ID;
            o["type"] = state.GetType().Name;
            o["name"] = StateName(state);
            return o;
        }

        static string StateName(TIGameState state)
        {
            string name = null;
            try
            {
                GameControl control = GameControl.control;
                name = state.GetDisplayName(control != null ? control.activePlayer : null);
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(name))
            {
                try { name = state.displayName; }
                catch (Exception) { }
            }
            return name;
        }

        static JArray ToArray(List<string> lines)
        {
            return ToArray(lines, int.MaxValue);
        }

        static JArray ToArray(List<string> lines, int max)
        {
            var a = new JArray();
            if (lines == null) return a;
            int n = lines.Count < max ? lines.Count : max;
            for (int i = 0; i < n; i++) a.Add(new JValue(lines[i]));
            return a;
        }

        static string Str(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return null;
            return t.Type == JTokenType.String ? (string)t : t.ToString();
        }

        static int Int(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) throw new VerbError("missing arg '" + key + "'");
            return WholeNumber(t, key);
        }

        // Filter args are absent far more often than not; -1 means "no filter", and
        // state IDs are never negative.
        static int OptionalInt(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return -1;
            return WholeNumber(t, key);
        }

        // JSON.NET's cast converts strings and bools; require the integer token type.
        static int WholeNumber(JToken t, string key)
        {
            if (t.Type != JTokenType.Integer)
                throw new VerbError("arg '" + key + "' must be an integer, got "
                    + t.Type);
            // Right type, wrong size: JSON.NET holds an integer literal too large
            // for a long as a BigInteger, and the conversion throws on it.
            try { return (int)t; }
            catch (Exception)
            {
                throw new VerbError("arg '" + key + "' is out of range for a "
                    + "32-bit integer");
            }
        }

        #region Shared helpers

        // Guarded member write: a throwing read records null so the rest of the
        // response survives.
        static void Put(JObject o, string key, Func<JToken> read)
        {
            try { o[key] = read(); }
            catch (Exception) { o[key] = JValue.CreateNull(); }
        }

        static T Safe<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception) { return fallback; }
        }

        static T ById<T>(int id) where T : TIGameState
        {
            T state = GameStateManager.FindGameState<T>(new GameStateID(id), true);
            if (state == null) throw new VerbError("no " + typeof(T).Name + " with id " + id);
            return state;
        }

        // A state id that has to be one particular class. ById would answer "no
        // TICouncilorState with id 42" for an id that resolves perfectly well to
        // something else, which sends the caller looking for a missing state
        // rather than at the wrong argument.
        static T Arg<T>(JObject args, string key) where T : TIGameState
        {
            int id = Int(args, key);
            // Typed lookup first; the non-generic scan is O(campaign) and runs only
            // on the miss, to name the wrong type.
            T typed = GameStateManager.FindGameState<T>(new GameStateID(id), true);
            if (typed != null) return typed;
            TIGameState state = GameStateManager.FindGameState(new GameStateID(id));
            if (state == null)
                throw new VerbError("arg '" + key + "': no game state with id " + id);
            throw new VerbError("arg '" + key + "': id " + id + " is a "
                + state.GetType().Name + " (" + StateName(state) + "), not a "
                + typeof(T).Name);
        }

        // Inactive objects included: several controllers live on disabled canvases.
        static T Find<T>() where T : UnityEngine.Component
        {
            try { return UnityEngine.Object.FindObjectOfType<T>(true); }
            catch (Exception) { return null; }
        }

        static T Require<T>(string what) where T : UnityEngine.Component
        {
            T found = Find<T>();
            if (found == null) throw new VerbError("no " + what);
            return found;
        }

        internal static string Note(Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }

        // A button the game would accept a click on: live in the hierarchy, enabled,
        // interactable, and on an enabled canvas.
        static bool Clickable(UnityEngine.UI.Button button)
        {
            try
            {
                if (button == null || !button.gameObject.activeInHierarchy) return false;
                if (!button.enabled || !button.interactable) return false;
                UnityEngine.Canvas canvas = button.GetComponentInParent<UnityEngine.Canvas>();
                return canvas != null && canvas.enabled;
            }
            catch (Exception) { return false; }
        }

        static bool Clickable(UnityEngine.GameObject buttonObject)
        {
            try
            {
                if (buttonObject == null || !buttonObject.activeInHierarchy) return false;
                return Clickable(buttonObject.GetComponentInChildren<UnityEngine.UI.Button>());
            }
            catch (Exception) { return false; }
        }

        // Public instance fields, then the readable non-indexed public instance
        // properties. Callers apply their own attribute filters.
        static List<MemberInfo> ReadableMembers(Type type)
        {
            return ReadableMembers(type, false);
        }

        // With includePrivate, the non-public instance members follow, taken one
        // declaring type at a time because reflection does not inherit them. A private
        // member whose name a public one already took is skipped, so asking for more
        // never changes what the public dump reported.
        static List<MemberInfo> ReadableMembers(Type type, bool includePrivate)
        {
            var members = new List<MemberInfo>();
            var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
            Collect(members, seen, type, BindingFlags.Public | BindingFlags.Instance);
            if (!includePrivate) return members;
            const BindingFlags priv = BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.DeclaredOnly;
            for (Type t = type; t != null; t = t.BaseType) Collect(members, seen, t, priv);
            return members;
        }

        static void Collect(List<MemberInfo> members, Dictionary<string, bool> seen,
            Type type, BindingFlags flags)
        {
            FieldInfo[] fields = Safe<FieldInfo[]>(
                delegate { return type.GetFields(flags); }, null);
            for (int i = 0; fields != null && i < fields.Length; i++)
            {
                if (seen.ContainsKey(fields[i].Name)) continue;
                seen[fields[i].Name] = true;
                members.Add(fields[i]);
            }
            PropertyInfo[] props = Safe<PropertyInfo[]>(
                delegate { return type.GetProperties(flags); }, null);
            for (int i = 0; props != null && i < props.Length; i++)
            {
                PropertyInfo p = props[i];
                if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                if (seen.ContainsKey(p.Name)) continue;
                seen[p.Name] = true;
                members.Add(p);
            }
        }

        #endregion
    }
}
