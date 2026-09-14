using System;
using System.Reflection;
using System.Threading;
using UnityEngine;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.Assets;
using PavonisInteractive.TerraInvicta.Tasks;
using PavonisInteractive.TerraInvicta.Systems.GameTime;

namespace TerraInvictaMCP
{
    // True autopilot: the faction AI plays the player's faction.
    //
    // The vanilla `autopilot` console command is a UI macro that never sets
    // TIPlayerState.isAI, so AIDailyFactionPlanner never runs for the player.
    // This flips isAI in place, re-runs the planner's Initialize so its faction
    // list picks the faction up, and gates one postfix on
    // TIFactionState.isActivePlayer, which is the property every corrupting read
    // goes through: FindGoals returns empty for the active player (the AI's whole
    // strategic brain), the ten AIReaction early-outs, the councilor mission
    // planner's goal multipliers, TradeAI, and the AI-only economy bonuses.
    //
    // Accepted residuals, all documented in the tool description: code comparing
    // GameControl.activePlayer by reference is unaffected, so the UI keeps
    // rendering the faction (desirable) and shipbuilding cost scaling stays at the
    // player's flat 1.0; Steam achievements stop unlocking while engaged, which is
    // correct for an AI-driven run.
    public static class AiControl
    {
        // Read on a hot property. Disengaged, the postfix is this one static bool.
        static bool engaged;

        static bool brutal;
        static TIFactionState faction;
        static TIGlobalValuesState campaign;

        // The engaged faction's notification settings as they were before engage.
        // SetActivePlayer gives every faction these as
        // (faction == activePlayer), so a genuine AI faction carries them false;
        // the engaged faction is the one state vanilla never produces, active
        // player and AI-driven at once.
        struct Notifications
        {
            public bool captured;
            public bool regular;
            public bool timer;
            public bool summary;
            public bool overrides;
            public bool alerts;
        }

        static Notifications priorNotifications;

        public static bool Engaged { get { return engaged; } }

        // Whether a colliding mission-phase tick is actually being deferred right
        // now. Both halves are needed: the deferral is the prefix on
        // StartNewMissionPhase, and a game update that renames that method leaves
        // an engagement running with `engaged` true and nothing guarding the
        // phase. Read by time.run_until, which exempts an engagement from the
        // open-phase refusal -- an exemption that is only safe while this is true.
        internal static bool PhaseDeferralActive
        {
            get { return engaged && phaseGuardPatched; }
        }

        // The faction the engagement holds, or null. Read by the combat machine, which
        // has to know that this one faction reports isActivePlayer false while the
        // engine's own UI still treats it as the player.
        internal static TIFactionState EngagedFaction
        {
            get { return engaged ? faction : null; }
        }

        // ---- verb ------------------------------------------------------------

        internal static JToken Control(string action, string smart)
        {
            if (action == "engage") return Engage(smart);
            if (action == "release") return Release("released");
            if (action == "status") return Status(null);
            throw new VerbError("action must be engage, release, or status");
        }

        static JToken Engage(string smart)
        {
            if (engaged) throw new VerbError("already engaged; release first");
            if (smart != "campaign" && smart != "brutal")
                throw new VerbError("smart must be campaign or brutal");
            // Refused rather than degraded: with no setter delegate every Swap is a
            // no-op, and with a window unpatched that planner surface plays at campaign
            // difficulty. Either way a brutal run measures something other than what it
            // claims, and a silent downgrade of the thing being measured is worse than
            // a refusal at the door.
            if (smart == "brutal" && SetDifficulty == null)
                throw new VerbError("the difficulty property setter delegate could not be "
                    + "built (a game update likely changed TIGlobalValuesState.difficulty), "
                    + "so brutal difficulty windows cannot be armed; engage with "
                    + "smart=campaign instead");
            if (smart == "brutal" && !windowsPatched)
                throw new VerbError("not every difficulty window patched (a game update "
                    + "likely renamed a planner method; status reports difficultyWindows "
                    + "false), so part of a brutal run would play at campaign difficulty; "
                    + "engage with smart=campaign instead");
            // Refused rather than degraded: isAI serializes, and LoadGame makes the
            // first non-AI player the active one, so without the shim every save
            // taken while engaged (the game's own autosaves included) loads with
            // the campaign handed to another faction. That is discovered hours into
            // an unattended run, which is the worst possible time.
            if (!saveShimInstalled)
                throw new VerbError("save shim is not installed (GameStateManager."
                    + "SaveAllGameStates did not patch), so a save taken while engaged would "
                    + "load with the campaign handed to another faction; refusing to engage");

            // The vanilla autopilot macro force-unpauses every frame from its own
            // Update and plays the UI itself, so running it
            // under an engagement means two drivers steering one faction.
            if (MacroAutopilotRunning())
                throw new VerbError("the autopilot macro is running; it plays the faction itself "
                    + "and would fight the engagement. Turn it off (autopilot action=off) "
                    + "before engaging");

            GameControl control = GameControl.control;
            TIFactionState player = control != null ? control.activePlayer : null;
            if (player == null) throw new VerbError("no active player faction");
            if (player.player == null) throw new VerbError("active player faction has no player state");

            faction = player;
            // Same accessor the tick compares against, so the identity check
            // cannot disagree with itself.
            campaign = SafeGlobalValues();
            brutal = smart == "brutal";
            deferredPhaseTicks = 0;
            resumedPauses = 0;

            // Order from the plan: flip the flag the planner reads, arm the
            // patches, then rebuild the planner's faction list from the flags.
            faction.player.AssignAIStatus(true);
            SuppressNotifications();
            engaged = true;
            Reinitialize();

            return Status("engaged");
        }

        // Idempotent, and safe with no campaign.
        //
        // Ordered so that a throw anywhere always errs toward handing the human
        // back control: the patch flag and isAI go first, and everything that can
        // throw (planner reinitialization, status building) runs after. A release
        // that half-completes must never leave the faction AI-flagged with the
        // postfix still narrowing isActivePlayer, which is the state QA saw and
        // could only escape with a save reload.
        static JToken Release(string note)
        {
            if (!engaged) return Status(note == null ? null : "not engaged");

            bool sameCampaign = ReferenceEquals(campaign, SafeGlobalValues());
            TIFactionState released = faction;

            // 1. Disarm the patches. From here isActivePlayer reports normally
            // whatever else fails below.
            engaged = false;
            brutal = false;

            // 2. Hand the faction back. Attempted even when the campaign identity
            // no longer matches: writing the flag on a discarded state is harmless,
            // and skipping it on a live one is not.
            if (released != null && released.player != null)
            {
                try { released.player.AssignAIStatus(false); }
                catch (Exception e) { Server.LastError = Verbs.Note(e); }
            }

            // 3. Give the notification pipeline back whatever it had.
            RestoreNotifications(released);

            faction = null;
            campaign = null;

            // 4. Everything that can throw, after the reversal is complete.
            if (sameCampaign) Reinitialize();
            return Status(note);
        }

        static JToken Status(string note)
        {
            var o = new JObject();
            o["engaged"] = engaged;
            o["smart"] = engaged ? (brutal ? "brutal" : "campaign") : null;
            o["faction"] = engaged ? FactionName() : null;
            o["activePlayerPatch"] = activePlayerPatched;
            o["saveShim"] = saveShimInstalled;
            o["alertSuppression"] = audiencePatched;
            o["missionPhaseGuard"] = phaseGuardPatched;
            o["deferredPhaseTicks"] = deferredPhaseTicks;
            o["resumedPauses"] = resumedPauses;
            o["difficultyWindows"] = windowsPatched;
            o["difficultySetter"] = SetDifficulty != null;
            if (note != null) o["note"] = note;
            return o;
        }

        // Matches what SetActivePlayer would give a non-active faction. Four of
        // the five are read somewhere (showRegularNotifications gates the news
        // feed, showSummaryLogs the summary log, checkNotificationOverrides the
        // per-template overrides, showTimerNotifications the timer list);
        // showAlerts is written there and read nowhere in the assembly, and is set
        // anyway so the faction's state matches the engine's own convention rather
        // than half of it.
        static void SuppressNotifications()
        {
            priorNotifications = new Notifications();
            if (faction == null) return;
            try
            {
                priorNotifications.regular = faction.showRegularNotifications;
                priorNotifications.timer = faction.showTimerNotifications;
                priorNotifications.summary = faction.showSummaryLogs;
                priorNotifications.overrides = faction.checkNotificationOverrides;
                priorNotifications.alerts = faction.showAlerts;
                priorNotifications.captured = true;
                SuppressNotificationValues(faction);
            }
            catch (Exception e) { Server.LastError = Verbs.Note(e); }
        }

        static void SuppressNotificationValues(TIFactionState target)
        {
            try
            {
                target.showRegularNotifications = false;
                target.showTimerNotifications = false;
                target.showSummaryLogs = false;
                target.checkNotificationOverrides = false;
                target.showAlerts = false;
            }
            catch (Exception e) { Server.LastError = Verbs.Note(e); }
        }

        static void RestoreNotificationValues(TIFactionState target, Notifications prior)
        {
            try
            {
                target.showRegularNotifications = prior.regular;
                target.showTimerNotifications = prior.timer;
                target.showSummaryLogs = prior.summary;
                target.checkNotificationOverrides = prior.overrides;
                target.showAlerts = prior.alerts;
            }
            catch (Exception e) { Server.LastError = Verbs.Note(e); }
        }

        static void RestoreNotifications(TIFactionState target)
        {
            if (!priorNotifications.captured || target == null)
            {
                priorNotifications = new Notifications();
                return;
            }
            RestoreNotificationValues(target, priorNotifications);
            priorNotifications = new Notifications();
        }

        // Status is the one call that has to answer in every state, including
        // after a crash, so nothing in it is allowed to throw.
        static string FactionName()
        {
            try { return faction != null ? faction.displayName : null; }
            catch (Exception) { return null; }
        }

        // Rebuilds AIFactions, numAIFactions and factionAIData from the current
        // isAI flags. Per-faction AI state needs no other seeding: to-do lists and
        // the saving target rebuild on the next daily pass.
        static void Reinitialize()
        {
            try
            {
                AIDailyFactionPlanner planner = AIDailyFactionPlanner.singleton;
                if (planner != null) planner.Initialize();
            }
            catch (Exception e) { Server.LastError = Verbs.Note(e); }
        }

        // The macro's own on/off switch, which is Autopilot.Activated and not the
        // component's enabled state: Autopilot.Update's first act is to return when
        // Activated is false, and the component sits in the scene enabled either way,
        // so the enabled state answers "is the class loaded" rather than "is the macro
        // playing the faction".
        static bool MacroAutopilotRunning()
        {
            try
            {
                Autopilot macro = Macro();
                return macro != null && macro.Activated;
            }
            catch (Exception) { return false; }
        }

        // Autopilot.Start assigns the singleton, so the field is the cheap path and
        // the scene walk is the fallback for a frame before Start has run.
        static Autopilot Macro()
        {
            try
            {
                Autopilot macro = Autopilot.Singleton;
                if (macro != null) return macro;
            }
            catch (Exception) { }
            try { return UnityEngine.Object.FindObjectOfType<Autopilot>(true); }
            catch (Exception) { return null; }
        }

        // The macro's engaged state, read rather than assumed.
        //
        // Autopilot.LogCallback clears `activated` and pauses the clock on the first
        // LogType.Exception unless IgnoreExeptions is set (the engine spells the field
        // that way). Nothing else reports that, so an unattended run that switched the
        // macro on and walked away keeps resuming a clock nobody is driving. This is
        // the read that catches it, and ignoreExceptions is reported beside it so a
        // caller can see whether the switch it depends on was ever armed.
        internal static JToken MacroStatus()
        {
            var o = new JObject();
            Autopilot macro = Macro();
            o["present"] = macro != null;
            if (macro == null)
            {
                o["activated"] = false;
                o["note"] = "no Autopilot component in the scene, so the macro has "
                    + "never run in this session";
                return o;
            }
            try { o["activated"] = macro.Activated; }
            catch (Exception) { o["activated"] = JValue.CreateNull(); }
            try { o["ignoreExceptions"] = macro.IgnoreExeptions; }
            catch (Exception) { o["ignoreExceptions"] = JValue.CreateNull(); }
            try { o["saveRate"] = macro.SaveRate; }
            catch (Exception) { o["saveRate"] = JValue.CreateNull(); }
            try { o["cycleIndex"] = macro.CycleIndex; }
            catch (Exception) { o["cycleIndex"] = JValue.CreateNull(); }
            return o;
        }

        static TIGlobalValuesState SafeGlobalValues()
        {
            try { return GameStateManager.GlobalValues(); }
            catch (Exception) { return null; }
        }

        // Called from Verbs.Tick. An engagement must never outlive the campaign it
        // was made in: isAI serializes, and a load that arrived with the flag still
        // set would leave the mod believing it owns a faction in a campaign that no
        // longer exists.
        internal static void Tick()
        {
            if (!engaged) return;
            if (!ReferenceEquals(campaign, SafeGlobalValues()))
            {
                Release("auto-released: campaign changed");
                return;
            }
            ResumeThroughPause();
            PushSpeedToMax();
        }

        // Vanilla pauses the clock at every councilor phase start
        // (CouncilorMissionUpdate is a pauseTime event) and expects a human to
        // resume it. An engaged run has no human: without this, the game sits
        // on the Confirm Assignments panel the moment no advance loop happens
        // to be mid-poll, and even with one polling, every phase is a visible
        // pause-then-resume pulse. The deferral prefix makes running through
        // phases safe, so the tick resumes the clock the frame after any pause
        // the wire did not ask for. Honored stops: a driver park
        // (time.pause, speed 0, or a fired run_until -- Verbs tracks these)
        // and an engine-blocked clock, which Play cannot clear and fighting
        // would only mask.
        static void ResumeThroughPause()
        {
            try
            {
                GameTimeManager manager = GameTimeManager.Singleton;
                if (manager == null || !manager.Paused) return;
                if (Verbs.ClockParkedByDriver) return;
                try { if (manager.IsBlocked) return; } catch (Exception) { }
                manager.Play();
                resumedPauses++;
            }
            catch (Exception e) { Server.LastError = Verbs.Note(e); }
        }

        // Reported by status: how many pauses the engaged tick resumed
        // through. Vanilla pauses only when a confirmation panel or another
        // pauseTime event actually posts, so the observed rate is a handful
        // per game year after the story-heavy first year, not one per phase.
        // A run where it spins per-frame means something is re-pausing and
        // needs a look.
        static int resumedPauses;

        // Inherent bias to finish: an engaged run should burn game time as fast as
        // the game allows. Never while paused, never while disengaged.
        static void PushSpeedToMax()
        {
            if (!engaged) return;
            try
            {
                GameTimeManager manager = GameTimeManager.Singleton;
                if (manager == null || manager.Paused) return;
                var speeds = manager.currentSpeeds;
                if (speeds == null || speeds.Count == 0) return;
                int max = speeds.Count - 1;
                if (manager.currentSpeedIndex >= max) return;
                manager.SetSpeed(max, false);
            }
            catch (Exception e) { Server.LastError = Verbs.Note(e); }
        }

        static bool MissionPhaseBusy()
        {
            TIMissionPhaseState phase = GameStateManager.MissionPhase();
            if (phase != null && phase.phaseActive) return true;

            // Reached on every idle frame, since an inactive phase is the common
            // case: the array is the manager's own cached one, so this allocates
            // nothing. A faction still prepping or planning is the same hazard one
            // step earlier, and the phase is global, so any faction counts.
            TIFactionState[] factions = GameStateManager.AllFactions();
            if (factions == null) return false;
            for (int i = 0; i < factions.Length; i++)
            {
                TIFactionState f = factions[i];
                if (f == null) continue;
                if (f.preppingForMissions || f.planningMissions) return true;
            }
            return false;
        }

        // This is the fix for the mid-phase collision, and it works by deferral
        // rather than by pausing. The corruption only ever occurs when a new
        // semimonthly tick lands while the previous phase's machinery is still
        // busy: vanilla's guard clears phaseActive and leaves
        // factionsSignallingComplete populated, in-flight finalizes are dropped,
        // and the stale list ends the next phase early -- a self-sustaining cycle.
        // Skipping the colliding tick makes the collision structurally impossible:
        // the running phase finishes normally (its own last finalize fires
        // SetMissionPhaseInactive and TimeEventComplete), the skipped update is
        // simply not taken, and the next tick lands ~15 game days later. The
        // clock never pauses, so an engaged run has no start-stop rhythm at
        // phase boundaries; under sustained planning load phases thin out
        // instead of colliding, which is the benign direction for an unattended
        // run.
        //
        // Acts only when the original would proceed. StartNewMissionPhase returns
        // early when skipTime equals now (a phase restarted by
        // PostVisualizerCreationInit_6), and skipping there would be a
        // double skip of a phase the load path just rebuilt. Unreachable through
        // today's tick ordering; structural is cheaper than reasoning about it
        // again.
        static bool StartMissionPhasePrefix(TIMissionPhaseState __instance)
        {
            if (__instance == null) return true;
            try
            {
                TIDateTime skip = __instance.skipTime;
                TIDateTime now = TITimeState.Now();
                if (skip != null && now != null && skip.Equals(now)) return true;
                if (!engaged)
                {
                    // Not engaged, so nothing is deferred and the engine runs
                    // exactly as it would without this mod. The count is the
                    // whole point: past this line vanilla logs "fired when
                    // mission phase was already active", clears phaseActive and
                    // returns with factionsSignallingComplete still populated,
                    // which is the corruption. query.time reports it, so a run
                    // can say whether a driver that re-armed the clock over an
                    // open phase is what produced one.
                    if (__instance.phaseActive) missionPhaseCollisions++;
                    return true;
                }
                if (__instance.phaseActive || MissionPhaseBusy())
                {
                    deferredPhaseTicks++;
                    return false;
                }
            }
            catch (Exception e) { Server.LastError = Verbs.Note(e); }
            return true;
        }

        // Reported by status; a run where this climbs steadily is a run whose
        // planning takes longer than the phase period, which is worth knowing
        // even though it is harmless.
        static int deferredPhaseTicks;

        // The engaged deferral and this counter can never both move on one tick:
        // the deferral returns false before the engine reaches its own guard, so
        // deferredPhaseTicks counts collisions prevented and this one counts
        // collisions taken.
        static int missionPhaseCollisions;

        internal static int MissionPhaseCollisions
        {
            get { return missionPhaseCollisions; }
        }

        // Called from the two verbs that unload a campaign. The count describes
        // one campaign's run; carrying it into the next one would report a
        // collision that happened somewhere else.
        internal static void ResetMissionPhaseCollisions()
        {
            missionPhaseCollisions = 0;
        }

        // Called from OnUnload. The assembly is about to be replaced, so anything
        // still held has to come back now.
        internal static void ReleaseForUnload()
        {
            try { Release("released on unload"); }
            catch (Exception) { }
        }

        // Called when the campaign the engagement is bound to is being unloaded
        // on purpose (game.main_menu). The patches read one faction of one
        // campaign, and that campaign's states are about to be cleared.
        internal static void ReleaseForCampaignEnd()
        {
            try { Release("released on return to the main menu"); }
            catch (Exception) { }
        }

        // ---- patch installation ----------------------------------------------

        static bool activePlayerPatched;
        static bool saveShimInstalled;
        static bool audiencePatched;
        static bool phaseGuardPatched;
        static bool windowsPatched;

        // Every patch goes through one warn-and-skip path, applied by hand rather
        // than by attribute: PatchAll throws on an unresolvable target, and a game
        // update that renames one signature would otherwise take the whole harness
        // down with it. A missing patch is reported by status, and a missing save
        // shim refuses engage.
        internal static void Install(Harmony harmony)
        {
            activePlayerPatched = Patch(harmony,
                AccessTools.PropertyGetter(typeof(TIFactionState), "isActivePlayer"),
                "TIFactionState.isActivePlayer",
                null, nameof(ActivePlayerPostfix), null);

            saveShimInstalled = Patch(harmony,
                AccessTools.Method(typeof(GameStateManager), "SaveAllGameStates",
                    new Type[] { typeof(string), typeof(bool) }),
                "GameStateManager.SaveAllGameStates",
                nameof(SavePrefix), null, nameof(SaveFinalizer));

            // The flags above are necessary but not sufficient, and this is the
            // patch that actually stops the alert storm. See the prefix.
            audiencePatched = Patch(harmony,
                AccessTools.Method(typeof(NotificationScreenController), "UpdateNewsFeed",
                    new Type[] { typeof(NewsItemCreated) }),
                "NotificationScreenController.UpdateNewsFeed",
                nameof(NewsFeedPrefix), null, null);

            // Belt for the clock invariant below.
            phaseGuardPatched = Patch(harmony,
                AccessTools.Method(typeof(TIMissionPhaseState), "StartNewMissionPhase"),
                "TIMissionPhaseState.StartNewMissionPhase",
                nameof(StartMissionPhasePrefix), null, null);

            InstallWindows(harmony);
        }

        // The six leaf methods whose synchronous windows cover every read that
        // differs between campaign difficulty and Brutal for a human faction's own
        // planning. Leaves, not their callers: the planner drives several of these
        // from coroutines, and a window spanning a coroutine would leave the swap
        // standing across unrelated work.
        //
        // Patched manually rather than by attribute: three are non-public, one is
        // overloaded, and a missing method must warn and skip rather than take the
        // mod down.
        static void InstallWindows(Harmony harmony)
        {
            var planner = typeof(AIDailyFactionPlanner);
            bool ok = true;

            ok &= Patch(harmony,
                AccessTools.Method(planner, "PerformAITaskGroup",
                    new Type[] { typeof(TIFactionState), typeof(AITaskCategory) }),
                "AIDailyFactionPlanner.PerformAITaskGroup", nameof(FactionArgPrefix));

            ok &= Patch(harmony,
                AccessTools.Method(planner, "ReviewAndSetGoals",
                    new Type[] { typeof(TIFactionState) }),
                "AIDailyFactionPlanner.ReviewAndSetGoals", nameof(FactionArgPrefix));

            ok &= Patch(harmony,
                AccessTools.Method(typeof(AICouncilorMissionPlanner), "PlanMissionsTask",
                    new Type[] { typeof(TIFactionState) }),
                "AICouncilorMissionPlanner.PlanMissionsTask", nameof(FactionArgPrefix));

            ok &= Patch(harmony,
                AccessTools.Method(typeof(FactionGoal_AttackWithFleet),
                    "GetMaximumFleetCombatValueRatio"),
                "FactionGoal_AttackWithFleet.GetMaximumFleetCombatValueRatio",
                nameof(GoalScopePrefix));

            ok &= Patch(harmony,
                AccessTools.Method(planner, "JealousyAndDeescalation"),
                "AIDailyFactionPlanner.JealousyAndDeescalation", nameof(FactionArgPrefix));

            // AIDailyFactionPlanner.AIReaction is deliberately not patched. It
            // iterates every faction internally, and its state argument only
            // selects the event's subject, so a window scoped from that argument
            // would cover every faction's reactive planning rather than the
            // engaged faction's. Its one difficulty read is gated > 1 and inert at
            // Normal or above anyway, so reactive planning on a Forgiving campaign
            // stays at campaign difficulty. Contamination is not worth that.

            windowsPatched = ok;
        }

        static bool Patch(Harmony harmony, MethodBase target, string label, string prefixName)
        {
            return Patch(harmony, target, label, prefixName, null, nameof(RestoreFinalizer));
        }

        // harmony.Patch itself is inside the try: an unresolvable target is only
        // the common failure, and a renamed parameter throws here too.
        static bool Patch(Harmony harmony, MethodBase target, string label,
            string prefixName, string postfixName, string finalizerName)
        {
            if (target == null)
            {
                Main.Log.Log("WARNING: " + label + " not found; that patch is absent.");
                return false;
            }
            try
            {
                harmony.Patch(target,
                    prefix: Method(prefixName),
                    postfix: Method(postfixName),
                    finalizer: Method(finalizerName));
                return true;
            }
            catch (Exception e)
            {
                Main.Log.Log("WARNING: patching " + label + " failed: " + e.Message);
                return false;
            }
        }

        static HarmonyMethod Method(string name)
        {
            return name == null ? null : new HarmonyMethod(typeof(AiControl), name);
        }

        // ---- difficulty windows ----------------------------------------------

        const int NoSwap = int.MinValue;

        // Exactly 4, never anything else: the ship-cost dictionary throws on keys
        // outside 1..4.
        const int Brutal = 4;

        // The campaign's own difficulty while a window holds the spoof, so the save
        // shim can put the real value on disk. NoSwap when no window is open.
        static int openWindowPrior = NoSwap;

        static readonly MethodInfo DifficultySetter =
            AccessTools.PropertySetter(typeof(TIGlobalValuesState), "difficulty");

        // Open delegate built once; MethodInfo.Invoke would box on every call.
        static readonly Action<TIGlobalValuesState, int> SetDifficulty = MakeSetter();

        static Action<TIGlobalValuesState, int> MakeSetter()
        {
            if (DifficultySetter == null) return null;
            try
            {
                return (Action<TIGlobalValuesState, int>)Delegate.CreateDelegate(
                    typeof(Action<TIGlobalValuesState, int>), DifficultySetter);
            }
            catch (Exception) { return null; }
        }

        static void Swap(TIFactionState scope, ref int __state)
        {
            __state = NoSwap;
            if (!engaged || !brutal) return;
            if (SetDifficulty == null) return;
            // Every patched method runs on the main thread today; skipping the swap
            // anywhere else keeps a future game patch from interleaving swap and
            // restore pairs.
            if (Thread.CurrentThread.ManagedThreadId != Main.MainThreadId) return;
            if (!ReferenceEquals(scope, faction)) return;

            TIGlobalValuesState gv = SafeGlobalValues();
            if (gv == null || !ReferenceEquals(gv, campaign)) return;
            int current = gv.difficulty;
            // Nested patched calls no-op while already at target, so restore order
            // between them cannot matter, and only the outermost window records the
            // campaign's own value.
            if (current == Brutal) return;
            __state = current;
            openWindowPrior = current;
            SetDifficulty(gv, Brutal);
        }

        static void FactionArgPrefix(TIFactionState faction, ref int __state)
        {
            Swap(faction, ref __state);
        }

        static void GoalScopePrefix(TIFactionGoalState __instance, ref int __state)
        {
            Swap(__instance != null ? __instance.faction : null, ref __state);
        }

        // Finalizer, not postfix: it runs even when the original, or another mod's
        // patch, throws. difficulty serializes into saves and nothing resets it
        // after campaign creation, so a stranded swap would be permanent. If our
        // prefix never ran, Harmony leaves __state at default(int) = 0, so only
        // values a campaign can actually hold are restored.
        static Exception RestoreFinalizer(Exception __exception, int __state)
        {
            if (__state >= 1 && __state <= 4 && SetDifficulty != null)
            {
                TIGlobalValuesState gv = SafeGlobalValues();
                if (gv != null) SetDifficulty(gv, __state);
                openWindowPrior = NoSwap;
            }
            return __exception;
        }

        // ---- always-on patches ------------------------------------------------

        // The one patch that makes this design work. Disengaged it is a static bool
        // read; engaged it is that plus one field read on a property the game calls
        // constantly.
        //
        // Semantically inert in vanilla play as well: the active player never has
        // isAI true unless this mod set it.
        static void ActivePlayerPostfix(TIFactionState __instance, ref bool __result)
        {
            if (!engaged) return;
            if (!__result) return;
            TIPlayerState player = __instance != null ? __instance.player : null;
            if (player != null && player.isAI) __result = false;
        }

        // Keeps the engaged faction's alerts off the screen. One patch, one
        // decision point.
        //
        // No faction flag can do this. What keeps a genuine AI faction's alerts
        // off screen is not a flag at all: this method appends to the controller's
        // heldNewsItems when item.alertFactions.Contains(activePlayer) OR
        // item.alertBlockFaction == activePlayer, and an AI faction is never the
        // active player. The engaged faction still is.
        // Suppressing the audience alone leaks through the right side of that OR:
        // alertBlockFaction is a public FIELD, so there is no property to patch,
        // and it is set unconditionally to the acting faction by
        // LogTechComplete, LogProjectComplete, LogMissionOutcome (the path
        // behind the councilor crash), LogUniqueProjectSnipedByAnotherFaction
        // and AlertNarrativeEvent.
        //
        // Skipping the whole method loses nothing while engaged: everything it
        // does before that gate (timer list, news list, summary log) is itself
        // gated on putInTimerQueue / putInNewsFeed / putInSummaryLog, and those
        // read the three flags this mod has already turned off. The four
        // defaultFleetArrival* fields SetActivePlayer also writes need no
        // handling for the same reason: whatever they select, the alert still
        // has to pass this gate.
        //
        // This does not touch the alert screen and does not change the game's
        // null-tolerance. It makes the engaged faction notification-silent, which
        // is what every other AI faction already is.
        //
        // Accepted limitation: an item already sitting in heldNewsItems when the
        // engagement starts is not drained. That list is UI-side and private, the
        // game-state queue exposes no clear, and nothing new can arrive while
        // engaged. A session poisoned by a build without this patch needs a game
        // restart to clear it.
        static bool NewsFeedPrefix()
        {
            return !engaged;
        }

        struct SaveState
        {
            public bool clearedAI;
            public int spoofedDifficulty;   // NoSwap when nothing was put back
            public bool restoredNotifications;
            // Carried rather than re-read: the finalizer must reverse what this
            // prefix did, to the faction it did it to.
            public TIFactionState faction;
        }

        // Mandatory, not defensive. isAI persists into saves and LoadGame makes the
        // first TIPlayerState with isAI false the active player, so a save written
        // while engaged (the game's own autosaves included) would load with the
        // campaign handed to another faction, or with no active player at all,
        // which the planner then dereferences. Clearing the flag for the duration
        // of the write makes every save vanilla-shaped.
        //
        // Difficulty rides along for the same reason and closes a whole latent
        // class rather than one known path: a save reached from inside an open
        // brutal window would otherwise write 4 into a campaign that is not Brutal,
        // and difficulty never resets after campaign creation. Whatever reaches
        // disk sees the campaign's own value.
        //
        // Finalizer for both restores, per the same rule as the difficulty windows:
        // an exception mid-save must not leave the engagement half-dismantled.
        static void SavePrefix(out SaveState __state)
        {
            __state = new SaveState();
            __state.clearedAI = false;
            __state.spoofedDifficulty = NoSwap;
            if (!engaged) return;
            __state.faction = faction;

            TIPlayerState player = faction != null ? faction.player : null;
            if (player != null && player.isAI)
            {
                player.AssignAIStatus(false);
                __state.clearedAI = true;
            }

            // The five notification fields are public on TIFactionState and
            // serialize with it. The load path recomputes them through
            // SetActivePlayer, so a suppressed value in a
            // save is self-healing, but a save this mod wrote should still hold
            // what vanilla would have written.
            if (priorNotifications.captured && __state.faction != null)
            {
                RestoreNotificationValues(__state.faction, priorNotifications);
                __state.restoredNotifications = true;
            }

            int prior = openWindowPrior;
            if (prior < 1 || prior > 4 || SetDifficulty == null) return;
            TIGlobalValuesState gv = SafeGlobalValues();
            if (gv == null || gv.difficulty == prior) return;
            __state.spoofedDifficulty = gv.difficulty;
            SetDifficulty(gv, prior);
        }

        static Exception SaveFinalizer(Exception __exception, SaveState __state)
        {
            if (__state.spoofedDifficulty >= 1 && __state.spoofedDifficulty <= 4
                && SetDifficulty != null)
            {
                TIGlobalValuesState gv = SafeGlobalValues();
                if (gv != null) SetDifficulty(gv, __state.spoofedDifficulty);
            }
            if (__state.restoredNotifications && __state.faction != null)
                SuppressNotificationValues(__state.faction);
            if (__state.clearedAI)
            {
                TIPlayerState player = __state.faction != null ? __state.faction.player : null;
                if (player != null) player.AssignAIStatus(true);
            }
            return __exception;
        }
    }

    public static partial class Verbs
    {
        // action=engage|release|status; engage takes smart=campaign|brutal.
        static JToken AiControl_Verb(JObject args)
        {
            // Str returns null for an absent key, not "": release and status carry
            // no smart argument, so reading .Length here threw before the verb body
            // ever ran, which is why both actions NREd in every state while engage
            // (which always sends smart) worked.
            string action = Text(args, "action", "status");
            // Brutal by default: the point of the engagement is an unattended run
            // that gets somewhere, so it plays as well as it can unless the caller
            // asks for the campaign's own difficulty.
            string smart = Text(args, "smart", "brutal");
            return AiControl.Control(action, smart);
        }

        // The vanilla autopilot macro's state. Separate from ai.control, which owns the
        // engagement: these are two different drivers and a run can have either.
        static JToken QueryAutopilot(JObject args)
        {
            return AiControl.MacroStatus();
        }

        static string Text(JObject args, string key, string fallback)
        {
            string value = Str(args, key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
    }
}
