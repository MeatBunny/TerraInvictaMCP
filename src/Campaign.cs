using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // campaign.new, prompts.list, prompts.dismiss.
    public static partial class Verbs
    {
        // The start screen offers four difficulties; the campaign records the index plus one.
        static readonly string[] DifficultyNames =
            new string[] { "Forgiving", "Normal", "Veteran", "Brutal" };
        const int DefaultDifficulty = 1;

        // The alert box restacks a new notification behind each press, so the loop that
        // drains it needs a bound.
        const int MaxAlertClicks = 64;

        static JToken CampaignNew(JObject args)
        {
            // The start screen scene is unloaded while a campaign runs, so its controller
            // would be missing anyway; the explicit refusal says why.
            if (HasCampaign) throw new VerbError("a campaign is loaded; return to the main menu first");
            StartMenuController menu = Require<StartMenuController>("start menu controller");
            if (menu.fatalStartupError) throw new VerbError("start menu reports a fatal startup error");
            RequireStartMenuReady(menu);

            // Order matters: a scenario change rebuilds the allowed-faction list and resets
            // the faction dropdown, so the scenario is settled before the faction is picked.
            string scenario = Str(args, "scenario");
            if (!string.IsNullOrEmpty(scenario))
                menu.UpdateStartOptions("Scenario", ScenarioTemplate(scenario));

            // The other option categories, written the same way the start screen's own
            // dropdowns write them. Still ahead of the faction pick: a FactionCouncils
            // option rebuilds the allowed-faction list and resets the dropdown too.
            ApplyStartOptions(menu, args);

            int difficulty = DifficultyIndex(args);
            menu.selectDifficultyDropdown.value = difficulty;
            menu.OnDifficultyChanged();

            TIFactionTemplate chosen = SelectFaction(menu, Str(args, "faction"));

            // The toggle is forced on for a player's first ever game, and the tutorial gates
            // the campaign behind scripted steps no client can answer.
            menu.tutorialToggle.isOn = false;
            menu.OnToggleTutorial();

            // The launch reads the dropdown, so the report is checked against the live
            // control rather than against what was asked for. Two option categories reset
            // that dropdown; this is the statement order above turned into a check.
            RequireFactionHolds(menu, chosen);

            // Built before the launch: the launch tears down this scene.
            var o = new JObject();
            o["scenario"] = ScenarioReport(menu);
            o["options"] = StartOptionsReport(menu);
            o["faction"] = FactionReport(chosen);
            o["difficulty"] = difficulty + 1;
            o["difficultyName"] = DifficultyNames[difficulty];
            o["tutorial"] = false;
            o["loading"] = true;

            // Nothing latched from an earlier campaign may survive into this one.
            // The launch is asynchronous, so the seat is read by the tick once
            // the new campaign reports itself loaded.
            ForgetSeat();
            // Nor anything armed or recorded against it. Combat ids start again at
            // the same numbers this process already used, so a record left by the
            // last campaign would answer for a fight in this one that has not
            // happened yet.
            ResetCampaignRunState();

            // Same call the "start campaign" button makes: default campaign options, then
            // the scene load that builds the new gamestate.
            menu.OnLaunchLongCampaignClicked();
            return o;
        }

        // game.main_menu: a loaded campaign back to the start screen, in one
        // process, so campaign.new can run again without a relaunch.
        //
        // The engine does support it. OptionsScreenController.OnConfirmExit with
        // fullExiting false does four things and then returns:
        //
        //   1. writes the exit save, unless SaveMenuController.SavingIsBlocked()
        //      or TIGlobalValuesState.isSpaceCombatEnabled
        //   2. AudioManager.StopAllEvents()
        //   3. hides its own canvas and disables the component
        //   4. GameControl.control.viewMgr.GotoView(ViewType.MainMenu)
        //
        // and GotoView's MainMenu arm is what actually unloads the campaign:
        // ClearGameData(false), which stops every coroutine, clears the templates
        // and reloads them, and runs a CleanupData coroutine ending in
        // GameStateManager.ClearAllGameStates() and GameControl.ResetLoadingState()
        // -- the call that clears loadcycle100, which is half of this bridge's own
        // "is a campaign loaded" test. Then it loads StartScreenScene
        // asynchronously. So the campaign is gone a frame or more after this verb
        // answers, and a client polls for it.
        //
        // The steps are replayed rather than OnConfirmExit being called, for one
        // reason: that method branches on the private `fullExiting` field, and the
        // true arm calls Application.Quit(). Reflecting a private field to steer a
        // method away from quitting the game is a worse bargain than making the
        // four public calls it would have made.
        //
        // Two deviations, both deliberate:
        //
        //  - The exit save is OPT-IN (`save`, default false). The button always
        //    writes it, and it always writes it to the SAME path, so a harness
        //    cycling scenarios would overwrite the player's continue-save on every
        //    lap. saves.save writes a named one on purpose.
        //  - The options canvas is only torn down when it is actually up. The
        //    button runs from a visible screen; this verb usually runs with
        //    nothing open, and disabling a controller that was never shown is a
        //    state the play path never produces.
        static JToken GameMainMenu(JObject args)
        {
            var o = new JObject();
            if (!HasCampaign)
            {
                o["returning"] = false;
                o["campaign"] = false;
                o["note"] = "no campaign is loaded; the main menu is already up "
                    + "or a load is still in flight. campaign.new answers from here";
                return o;
            }

            GameControl control = GameControl.control;
            if (control == null) throw new VerbError("no GameControl");
            ViewControl views = Safe<ViewControl>(
                delegate { return control.viewMgr; }, null);
            if (views == null)
                throw new VerbError("GameControl has no viewMgr, which is the only "
                    + "thing that unloads a campaign; nothing was touched");

            bool save = Bool(args, "save", false);
            o["savingBlocked"] = Safe<bool>(
                delegate { return SaveMenuController.SavingIsBlocked(); }, false);
            o["spaceCombatEnabled"] = Safe<bool>(
                delegate { return TIGlobalValuesState.isSpaceCombatEnabled; }, false);
            o["exitSaveWritten"] = false;
            if (save)
            {
                // The button's own condition. Writing over either of these is what
                // the engine refuses to do, and it refuses for the same reason a
                // save taken mid-combat or mid-teardown is not loadable.
                if ((bool)o["savingBlocked"] || (bool)o["spaceCombatEnabled"])
                    o["exitSaveNote"] = "the exit save was asked for and skipped, "
                        + "on the engine's own condition: saving is blocked or "
                        + "space combat is live";
                else
                    o["exitSaveWritten"] = Safe<bool>(delegate
                    {
                        return GameStateManager.SaveAllGameStates(
                            StartMenuController.exitSaveFilePath, true);
                    }, false);
            }

            // Anything armed against the campaign now being unloaded must not act
            // on the next one. State ids are reused across campaigns, which is the
            // same reason saves.load clears these, and this verb answers frames
            // before the engine reports the campaign gone.
            ResetCampaignRunState();
            // The engagement patches isActivePlayer for one faction of one
            // campaign. That campaign is about to stop existing.
            o["aiControlReleased"] = AiControl.Engaged;
            try { AiControl.Release("released on return to the main menu"); }
            catch (Exception) { }
            // The latched seat belongs to the campaign being unloaded. The tick
            // latches the next one when a campaign is up again.
            ForgetSeat();

            // The button pauses before it asks for confirmation; the clock is
            // about to be torn down either way, and a paused one cannot tick
            // through the teardown.
            Safe<bool>(delegate
            {
                PavonisInteractive.TerraInvicta.Systems.GameTime.GameTimeManager clock =
                    PavonisInteractive.TerraInvicta.Systems.GameTime.GameTimeManager.Singleton;
                if (clock != null) clock.Pause();
                return true;
            }, false);

            // A cinematic still on screen is torn out from under itself by the
            // GotoView below and takes the process down a frame later. See
            // CloseLiveCinematics for the chain. Ahead of StopAllEvents because
            // the engine's own close restores audio state this then stops.
            o["cinematicsClosed"] = CloseLiveCinematics();

            try { PavonisInteractive.TerraInvicta.Audio.AudioManager.StopAllEvents(); }
            catch (Exception) { }

            OptionsScreenController options = Find<OptionsScreenController>();
            if (options != null && Safe<bool>(delegate
                {
                    UnityEngine.Canvas canvas = options.Canvas;
                    return canvas != null && canvas.enabled;
                }, false))
            {
                try { options.Hide(); options.enabled = false; }
                catch (Exception) { }
                o["optionsScreenClosed"] = true;
            }
            else o["optionsScreenClosed"] = false;

            views.GotoView(ViewType.MainMenu);

            o["returning"] = true;
            o["campaign"] = true;
            o["next"] = "the campaign unloads over the next frames and "
                + "StartScreenScene loads asynchronously. Poll `version` (which "
                + "answers with no campaign) or any campaign verb until it "
                + "returns \"no campaign\", then campaign.new";
            return o;
        }

        // Takes every live 2D cinematic down before the campaign is unloaded,
        // because the unload destroys what the cinematic dereferences next frame.
        //
        // GotoView(ViewType.MainMenu) is the switch arm at IL_0182 (ViewType
        // MainMenu is 1, and the switch is on newView - 1). That arm calls
        // ClearGameData(false) at IL_01b4 and then LoadSceneAsync
        // ("StartScreenScene") at IL_01b9 -- asynchronous, so the campaign
        // scene's MonoBehaviours keep running for frames afterwards.
        // ClearGameData branches on its loadGame argument at IL_0020 and, with
        // it false, calls ViewControl.CleanupTextures at IL_0023.
        // CleanupTextures walks FindObjectsOfType<VideoPlayer>() at IL_00a8 and
        // for each player with a target texture sets that texture to NULL
        // (IL_00d6) before releasing it (IL_00dd).
        //
        // Cinematic2DController.Update calls OnClickCloseCinematic at IL_0034
        // whenever hasBeenPrepared, the GameObject is active, the video is not
        // playing and `active` is set; OnClickCloseCinematic reads
        // videoPlayer.targetTexture at IL_0042 and calls Release() on it at
        // IL_0047 with no null test. So a cinematic that is still up when the
        // teardown runs raises, one frame later,
        //
        //   NullReferenceException
        //     at Cinematic2DController.OnClickCloseCinematic () [0x00047]
        //     at Cinematic2DController.Update () [0x00033]
        //
        // which is an engine crash: the game speed goes to 0 and the process
        // has to be restarted. saves.load is NOT exposed to it --
        // LoadMenuController.LoadSaveFilePath passes loadGame TRUE (IL_0060),
        // which is the arm that skips CleanupTextures entirely.
        //
        // `active` is set true in Begin (IL_00f9) and is never set false
        // anywhere in the class, so nothing short of deactivating the
        // GameObject stops that Update path once a cinematic has played.
        // Deactivation is therefore the guarantee, and it is taken whatever the
        // engine's own close did.
        //
        // The engine's close is still called first, because it owns two effects
        // that outlive the campaign scene: the cinematic's own start
        // (<BeginWhenPrepared>d__28.MoveNext, IL_0037) sets the STATIC
        // TIInputManager.acceptingInput false and mutes the UI audio bus, and
        // OnClickCloseCinematic (IL_0010, IL_0015) is what puts both back.
        // Its first branch is skipped deliberately: with introQueued set it
        // starts the QUEUED cinematic and returns (IL_0006-IL_000e), which
        // would leave a live one behind.
        //
        // Closing rather than refusing: an alert cinematic is ordinary campaign
        // state, nothing else in the bridge closes one, and a refusal would
        // leave a client with no way back to the menu at all.
        static JToken CloseLiveCinematics()
        {
            var closed = new JArray();
            // Active objects only, which is exactly the hazard set: Unity runs
            // no Update on an inactive GameObject.
            Cinematic2DController[] all = Safe<Cinematic2DController[]>(delegate
            {
                return UnityEngine.Object.FindObjectsOfType<Cinematic2DController>();
            }, null);
            if (all == null) return closed;
            for (int i = 0; i < all.Length; i++)
            {
                Cinematic2DController c = all[i];
                if (c == null) continue;
                if (!Safe<bool>(delegate
                    {
                        return c.gameObject.activeInHierarchy && c.enabled;
                    }, false)) continue;

                var one = new JObject();
                one["name"] = Safe<string>(delegate { return c.gameObject.name; }, null);
                one["instanceId"] = Safe<int>(delegate { return c.GetInstanceID(); }, 0);
                one["introQueued"] = Safe<bool>(delegate { return c.introQueued; }, false);
                try { c.introQueued = false; }
                catch (Exception) { }
                try { c.OnClickCloseCinematic(); one["engineClose"] = true; }
                catch (Exception e)
                {
                    one["engineClose"] = false;
                    one["engineCloseNote"] = Note(e);
                }
                try { c.gameObject.SetActive(false); one["deactivated"] = true; }
                catch (Exception e)
                {
                    one["deactivated"] = false;
                    one["deactivateNote"] = Note(e);
                }
                closed.Add(one);
            }
            return closed;
        }

        static readonly FieldInfo startSceneManagerField = StartMenuField("sceneManager");
        static readonly FieldInfo allowedFactionsField = StartMenuField("currentAllowedFactions");
        static readonly FieldInfo startOptionsField = StartMenuField("currentStartOptions");

        static FieldInfo StartMenuField(string name)
        {
            try
            {
                return typeof(StartMenuController).GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception) { return null; }
        }

        // The scene manager is resolved in the menu's Start. Without it the launch call
        // throws part way through, after it has already activated the loading screen.
        static void RequireStartMenuReady(StartMenuController menu)
        {
            if (startSceneManagerField == null)
                throw new VerbError("no sceneManager field on start menu");
            if (startSceneManagerField.GetValue(menu) == null)
                throw new VerbError("start menu is not initialized");
            // A scene load runs for tens of seconds and leaves no campaign behind until it
            // finishes, so no gamestate here can tell that a launch has already been issued.
            // Both routes out of this menu raise their own loading screen first, and that is
            // the flag: a second launch on top of the first reloads the scene mid-bootstrap.
            if (Launching(menu.loadingScreen) || Launching(LoadMenuScreen(menu)))
                throw new VerbError("a launch is already in progress");
        }

        static bool Launching(UnityEngine.GameObject screen)
        {
            return Safe<bool>(delegate { return screen != null && screen.activeSelf; }, false);
        }

        static UnityEngine.GameObject LoadMenuScreen(StartMenuController menu)
        {
            try
            {
                LoadMenuController loads = menu.loadMenuController;
                return loads != null ? loads.loadingScreen : null;
            }
            catch (Exception) { return null; }
        }

        static TIMetaTemplate ScenarioTemplate(string wanted)
        {
            var names = new List<string>();
            foreach (TIMetaTemplate t in TemplateManager.IterateByClass<TIMetaTemplate>(true))
            {
                if (t == null || !t.isNewCampaignOption) continue;
                if (!string.Equals(t.newCampaignOptionCategory, "Scenario", StringComparison.Ordinal)) continue;
                if (Same(t.dataName, wanted) || Same(ScenarioName(t), wanted)) return t;
                names.Add(t.dataName);
            }
            throw new VerbError("no scenario named '" + wanted + "' ("
                + string.Join(", ", names.ToArray()) + ")");
        }

        static string ScenarioName(TIMetaTemplate t)
        {
            return Safe<string>(delegate { return t.displayNameCurrentForStartScreen(); }, null);
        }

        static JToken ScenarioReport(StartMenuController menu)
        {
            TIMetaTemplate t = null;
            try { t = menu.GetSelectedScenarioMetaTemplate(); }
            catch (Exception) { }
            if (t == null) return JValue.CreateNull();
            var o = new JObject();
            o["dataName"] = t.dataName;
            o["name"] = ScenarioName(t);
            return o;
        }

        // 'options' is a list of TIMetaTemplate dataNames, one per category at most. Each
        // template names the category it belongs to, so the wire format carries names
        // alone and the write goes where the picker would have sent it.
        static void ApplyStartOptions(StartMenuController menu, JObject args)
        {
            List<string> wanted = OptionNames(args);
            var chosen = new List<TIMetaTemplate>();
            var taken = new Dictionary<string, string>(StringComparer.Ordinal);
            // Resolved in full before the first write, so a rejected list leaves the
            // option map as it was rather than half applied.
            for (int i = 0; i < wanted.Count; i++)
            {
                TIMetaTemplate t = OptionTemplate(wanted[i]);
                string category = t.newCampaignOptionCategory;
                string first;
                // The map keeps one entry per category, so a second option for a category
                // would silently discard the first.
                if (taken.TryGetValue(category, out first))
                    throw new VerbError("options set category '" + category + "' twice ('"
                        + first + "' then '" + t.dataName + "'); one option per category");
                taken[category] = t.dataName;
                chosen.Add(t);
            }
            for (int i = 0; i < chosen.Count; i++)
                menu.UpdateStartOptions(chosen[i].newCampaignOptionCategory, chosen[i]);
        }

        // A blank entry is a caller bug: dropping it would launch a default-option
        // campaign under a reply that says the option was honored.
        static List<string> OptionNames(JObject args)
        {
            var names = new List<string>();
            JToken t = args != null ? args["options"] : null;
            if (t == null || t.Type == JTokenType.Null) return names;
            var array = t as JArray;
            if (array == null)
                throw new VerbError("arg 'options' must be a list of start option "
                    + "dataNames");
            for (int i = 0; i < array.Count; i++) names.Add(OptionName(array[i], i));
            return names;
        }

        static string OptionName(JToken t, int index)
        {
            string name = t != null && t.Type != JTokenType.Null ? t.ToString().Trim() : null;
            if (string.IsNullOrEmpty(name))
                throw new VerbError("arg 'options' entry " + index + " is empty");
            return name;
        }

        // A start option is a meta template the picker offers outside the scenario
        // dropdown. Scenario is refused here: it resets the faction list, so the verb
        // settles it first from its own arg.
        static TIMetaTemplate OptionTemplate(string wanted)
        {
            var names = new List<string>();
            TIMetaTemplate found = null;
            foreach (TIMetaTemplate t in TemplateManager.IterateByClass<TIMetaTemplate>(true))
            {
                if (t == null) continue;
                if (Same(t.dataName, wanted)) { found = t; break; }
                if (SelectableOption(t)) names.Add(t.dataName);
            }
            if (found == null)
                throw new VerbError("no start option named '" + wanted + "' ("
                    + string.Join(", ", names.ToArray()) + ")");
            if (!found.isNewCampaignOption)
                throw new VerbError("meta template '" + found.dataName
                    + "' is not a new-campaign option");
            if (string.IsNullOrEmpty(found.newCampaignOptionCategory))
                throw new VerbError("new-campaign option '" + found.dataName
                    + "' has no option category");
            if (IsScenarioCategory(found.newCampaignOptionCategory))
                throw new VerbError("'" + found.dataName
                    + "' is a scenario; pass it as arg 'scenario'");
            return found;
        }

        static bool SelectableOption(TIMetaTemplate t)
        {
            return t.isNewCampaignOption
                && !string.IsNullOrEmpty(t.newCampaignOptionCategory)
                && !IsScenarioCategory(t.newCampaignOptionCategory);
        }

        static bool IsScenarioCategory(string category)
        {
            return string.Equals(category, "Scenario", StringComparison.Ordinal);
        }

        // The category->dataName map the launch reads back to load each option's
        // templates, so a caller can assert what the campaign was created with.
        static JToken StartOptionsReport(StartMenuController menu)
        {
            if (startOptionsField == null) return JValue.CreateNull();
            var options = Safe<Dictionary<string, string>>(
                delegate { return startOptionsField.GetValue(menu) as Dictionary<string, string>; },
                null);
            if (options == null) return JValue.CreateNull();
            var o = new JObject();
            foreach (KeyValuePair<string, string> pair in options)
            {
                if (pair.Key == null) continue;
                o[pair.Key] = pair.Value;
            }
            return o;
        }

        // Accepts the campaign's own 1..4 numbering or the start screen's label.
        static int DifficultyIndex(JObject args)
        {
            JToken t = args["difficulty"];
            if (t == null || t.Type == JTokenType.Null) return DefaultDifficulty;
            string s = t.ToString();
            int level;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
            {
                if (level < 1 || level > DifficultyNames.Length)
                    throw new VerbError("arg 'difficulty' must be 1.." + DifficultyNames.Length);
                return level - 1;
            }
            for (int i = 0; i < DifficultyNames.Length; i++)
            {
                if (Same(DifficultyNames[i], s)) return i;
            }
            throw new VerbError("unknown difficulty '" + s + "' ("
                + string.Join(", ", DifficultyNames) + ")");
        }

        // The dropdown index is the index into the allowed-faction list, and the menu's own
        // handler is what copies it into the scenario's active player faction.
        static TIFactionTemplate SelectFaction(StartMenuController menu, string wanted)
        {
            List<TIFactionTemplate> allowed = AllowedFactions(menu);
            int index = menu.newCampaignChooseFactionDropdown.value;
            if (!string.IsNullOrEmpty(wanted))
            {
                index = -1;
                var names = new List<string>();
                for (int i = 0; i < allowed.Count; i++)
                {
                    TIFactionTemplate f = allowed[i];
                    if (f == null) continue;
                    if (Same(f.dataName, wanted) || Same(FactionName(f), wanted)) { index = i; break; }
                    names.Add(f.dataName);
                }
                if (index < 0)
                    throw new VerbError("no faction named '" + wanted + "' ("
                        + string.Join(", ", names.ToArray()) + ")");
                menu.newCampaignChooseFactionDropdown.value = index;
                menu.OnFactionOptionSelected();
            }
            if (index < 0 || index >= allowed.Count) throw new VerbError("no faction selected");
            return allowed[index];
        }

        // Compared by dataName: a rebuilt allowed-faction list holds the same templates,
        // and what matters is which faction the dropdown index now names.
        static void RequireFactionHolds(StartMenuController menu, TIFactionTemplate chosen)
        {
            List<TIFactionTemplate> allowed = AllowedFactions(menu);
            int index = menu.newCampaignChooseFactionDropdown.value;
            TIFactionTemplate live = index >= 0 && index < allowed.Count ? allowed[index] : null;
            string now = live != null ? live.dataName : null;
            string want = chosen != null ? chosen.dataName : null;
            if (string.Equals(now, want, StringComparison.Ordinal)) return;
            throw new VerbError("the faction dropdown no longer selects '"
                + (want != null ? want : "none") + "' but '" + (now != null ? now : "none")
                + "'; a start screen control was set out of order");
        }

        static List<TIFactionTemplate> AllowedFactions(StartMenuController menu)
        {
            if (allowedFactionsField == null)
                throw new VerbError("no currentAllowedFactions field on start menu");
            var list = allowedFactionsField.GetValue(menu) as List<TIFactionTemplate>;
            if (list == null || list.Count == 0) throw new VerbError("start menu has no faction options");
            return list;
        }

        static string FactionName(TIFactionTemplate f)
        {
            return Safe<string>(delegate { return f.capitalizedFactionNameCurrent; }, null);
        }

        static JToken FactionReport(TIFactionTemplate f)
        {
            if (f == null) return JValue.CreateNull();
            var o = new JObject();
            o["dataName"] = f.dataName;
            o["name"] = FactionName(f);
            return o;
        }

        static TIPromptQueueState PromptQueue()
        {
            TIPromptQueueState queue = null;
            try { queue = GameStateManager.PromptQueue(); }
            catch (Exception) { }
            if (queue == null) throw new VerbError("no prompt queue");
            return queue;
        }

        static JToken PromptsList(JObject args)
        {
            TIPromptQueueState queue = PromptQueue();
            var o = new JObject();
            TIFactionState player = ActivePlayer();
            o["activePlayer"] = player != null ? Describe(player) : null;
            try { o["blocked"] = queue.anyActivePlayerBlocking; }
            catch (Exception) { o["blocked"] = null; }

            List<Prompt> faction = FactionPrompts(queue);
            List<Prompt> nation = NationPrompts(queue);
            // Read once for the whole listing rather than once per prompt, and
            // only when the queue holds the one prompt kind the flag means
            // anything for: it is a scene search, and prompts.list is on paths
            // that call it often. This is the read path, so the flag the dismiss
            // pass reports on a prompt it left standing has to be readable here
            // too -- a client waiting for a narrative box otherwise has to
            // attempt a dismissal to learn whether anything can answer it yet.
            bool boxAnswerable = (AnyNarrative(faction) || AnyNarrative(nation))
                && NarrativeBoxAnswerable(Find<NotificationScreenController>());
            var a = new JArray();
            Append(a, faction, "faction", boxAnswerable);
            Append(a, nation, "nation", boxAnswerable);
            o["prompts"] = a;
            return o;
        }

        static bool AnyNarrative(List<Prompt> prompts)
        {
            for (int i = 0; prompts != null && i < prompts.Count; i++)
                if (Same(prompts[i].name, NarrativePrompt)) return true;
            return false;
        }

        static void Append(JArray a, List<Prompt> prompts, string scope,
                           bool boxAnswerable)
        {
            for (int i = 0; i < prompts.Count; i++)
                a.Add(DescribePrompt(prompts[i], scope, boxAnswerable));
        }

        static JToken DescribePrompt(Prompt prompt, string scope, bool boxAnswerable)
        {
            var o = new JObject();
            o["name"] = prompt.name;
            o["scope"] = scope;
            // Only for the one prompt kind it means anything for. The narrative
            // prompt is answered by its alert box and by nothing else, so "is
            // anything on screen able to answer this yet" is a real question for
            // it and a category error for a tech or trajectory prompt. Same
            // reading and same sense as the dismiss pass's key: true means
            // nothing can answer it on this pass.
            if (Same(prompt.name, NarrativePrompt))
                o["waitingForBox"] = !boxAnswerable;
            try { o["value"] = prompt.value; } catch (Exception) { o["value"] = null; }
            o["handled"] = Handled(prompt.name);
            o["actingState"] = State(prompt.actingState);
            o["promptingState"] = State(prompt.promptingGameState);
            o["relatedState"] = State(prompt.relatedGameState);
            return o;
        }

        static JToken State(TIGameState state)
        {
            try { return state != null ? Describe(state) : JValue.CreateNull(); }
            catch (Exception) { return JValue.CreateNull(); }
        }

        // The prompt whose answer is a story decision rather than a housekeeping one,
        // named because two places have to agree on it: the answers table below and the
        // `narrative` switch that turns it off.
        const string NarrativePrompt = "PromptAddressNarrativeEvent";

        // The neutral answers by prompt name, each the player action the game's own AI
        // path runs for an AI faction. A prompt type outside this table has no neutral
        // answer.
        //
        // The third argument is the entry this dismissal is being reported under, so an
        // answer that made a real choice can say which one it made. Only the narrative
        // answer uses it: the rest pick the one thing there is to pick.
        static readonly Dictionary<string, Func<Prompt, TIFactionState, JObject, string>> answers =
            BuildAnswers();

        static Dictionary<string, Func<Prompt, TIFactionState, JObject, string>> BuildAnswers()
        {
            var t = new Dictionary<string, Func<Prompt, TIFactionState, JObject, string>>(StringComparer.Ordinal);
            t["PromptSelectTech"] = AnswerTech;
            t["PromptSelectProject"] = AnswerProject;
            t["PromptSelectCouncilorMissions"] = AnswerMissions;
            t["PromptDropOrgs"] = AnswerOrgSale;
            t["PromptSelectTrajectory"] = AnswerTrajectory;
            // The stance the precombat screen is waiting for. Defend, through the
            // screen's own submit button, and only while a live precombat controller
            // is up and the autoresolve machine is idle -- otherwise the prompt is
            // reported as skipped with whichever of those two was missing.
            // PromptBeginCombat is deliberately absent: every button that clears it
            // commits an outcome, and one of them is the accept combat.autoresolve
            // owns. See AnswerCombatStance.
            t[StancePrompt] = AnswerCombatStance;
            t[NarrativePrompt] = AnswerNarrativeEvent;
            return t;
        }

        static bool Handled(string name)
        {
            return name != null && answers.ContainsKey(name);
        }

        // The refusal every verb that presses a human-UI button returns once the
        // active-player seat has been moved. One builder, because prompts.dismiss
        // and alert.choose press the same class of button on the same screens for
        // the same reason, and two copies of this would drift.
        //
        // `presses` names what this verb does with those handlers, `left` says
        // what the refusal leaves behind, and `reading` names the read path that
        // stays open. Everything else is common: the shape a client matches on
        // (`ok`, `reason`, `activePlayer`, `seatedFaction`, `text`) and the
        // account of why the press is refused.
        static JObject SeatMovedRefusal(string presses, string left, string reading)
        {
            var refused = new JObject();
            refused["ok"] = false;
            refused["reason"] = "activePlayerMoved";
            refused["activePlayer"] = Safe<JToken>(delegate
            {
                TIFactionState f = ActivePlayer();
                return f != null ? Describe(f) : JValue.CreateNull();
            }, JValue.CreateNull());
            // The faction to put back, named rather than left to the caller
            // to remember across however many calls have run since.
            refused["seatedFaction"] = Safe<JToken>(delegate
            {
                TIFactionState f = SeatedFaction;
                return f != null ? Describe(f) : JValue.CreateNull();
            }, JValue.CreateNull());
            refused["text"] = "the active-player seat has been moved off the "
                + "faction this campaign came up with, which only console "
                + "setfaction does, and " + presses + " through the "
                + "human UI's handlers: those read the seated faction's "
                + "councilors and crash on a faction the human is not "
                + "playing. A mission confirmation taken this way killed the "
                + "game in MapController.Fly. Nothing was " + left + ". Put the "
                + "original faction back in the seat -- console setfaction "
                + "<faction> -- and call again. Reading is unaffected: "
                + reading;
            return refused;
        }

        static JToken PromptsDismiss(JObject args)
        {
            TIPromptQueueState queue = PromptQueue();
            string filter = Str(args, "type");
            bool all = Flag(args, "all");

            // Nothing in this verb runs once the active-player seat has been
            // moved off the faction this campaign came up with. Console
            // `setfaction` is the only thing that moves it.
            //
            // Every answer here is a HUMAN player action taken through the human
            // UI's handlers, and those handlers assume the councilors, habs and
            // map visualizers of the faction the human is playing. Observed: with
            // the active player moved to another faction, this pass confirmed
            // that faction's mission assignments, the AI's own mission planner
            // ran the resulting actions through the map controller, and the game
            // died in an unhandled NullReferenceException in MapController.Fly by
            // way of OnMissionAssigned -- a visualizer only the human faction's
            // councilors carry. The screens pass is refused with it: it presses
            // the same class of button on the same screens.
            //
            // The seat identity is what is tested, and not the seated player's
            // isAI flag: the engine rewrites isAI for every player from the seat
            // it has just assigned, so the faction handed the seat always reads
            // isAI false however it got there. See ActivePlayerSeatMoved.
            //
            // Refused rather than degraded to a partial pass. The half of the
            // verb that is safe -- dropping prompts -- is not a recovery from
            // this state, and running it would report success on a call whose
            // point was to answer something.
            if (ActivePlayerSeatMoved())
                return SeatMovedRefusal("this verb answers prompts",
                    "answered, dropped or closed",
                    "prompts.list still reports the queue");

            // Defaults to answering, because the point of an unattended run is that it
            // stays unattended and a narrative event is the one prompt that used to
            // stop it dead. `narrative: false` presses no option button and leaves an
            // answerable event standing, for a caller that wants to take the decision
            // itself through alert.choose. Neither setting force-drops one: see the
            // `all` pass below for why there is no forfeit. The one exception is not an
            // answer at all and runs under both settings -- the prompt loop drops the
            // narrative prompt nothing can ever answer, see NarrativeDeadEnd.
            bool answerNarrative = Bool(args, "narrative", true);
            // Who is expected to press the button when this pass does not.
            // `narrative` stays the switch the DLL acts on; the mode is a label
            // that travels into the reply, because "left standing" means two
            // different things -- a caller that stops and answers it (llm) and a
            // person at the screen (false) -- and a digest that cannot tell them
            // apart cannot say whether a run was waiting for itself or for someone.
            // Reported as given: a client naming a mode this build has not heard of
            // is not an error, since the behaviour is carried by `narrative`.
            string narrativeMode = NarrativeMode(args, answerNarrative);

            var dismissed = new JArray();
            var skipped = new JArray();
            // The prompt behind each skipped entry, in step with `skipped`. The
            // force-drop pass annotates the entry belonging to ONE prompt, and a
            // name is not an identity: two nations can hold the same prompt name at
            // once, and flagging both because one of them would not drop reports a
            // failure against a prompt that was never touched. Prompt is a sealed
            // struct implementing IEquatable<Prompt> over all five fields, which is
            // the same equality the engine's own RemovePrompt matches on.
            var skippedPrompts = new List<Prompt>();

            // A screen carries no prompt type, so a filtered call leaves the UI alone and
            // touches only the prompts it names.
            //
            // This runs BEFORE the prompt loop and it is where a narrative event is
            // normally answered, not in the loop below. The alert box is the engine's
            // own path: NotificationScreenController.OnOptionButtonPressed builds the
            // SelectNarrativeEventOption itself, and its tail clears
            // receivingInputForNarrativeHotkeys, drops the resource listener, calls
            // CleanUp and resumes the clock (IL_011d-IL_0147). Answering through the
            // prompt queue alone would resolve the event and leave that box standing
            // with the clock still held, so the screens pass presses the button and the
            // prompt loop is the fallback for a narrative prompt with no box up.
            var screens = new JArray();
            var narrativeNotes = new JArray();
            if (string.IsNullOrEmpty(filter))
                DismissScreens(screens, answerNarrative, narrativeMode,
                               dismissed, narrativeNotes);
            // Whether anything on screen could answer a narrative event right now,
            // read once for the loop below and only under the switch that can leave
            // one standing: a scene search per prompt would cost one for every prompt
            // in the queue. Taken AFTER the screens pass, because that pass is what
            // presses a box this one is about to describe.
            bool boxAnswerable = !answerNarrative
                && NarrativeBoxAnswerable(Find<NotificationScreenController>());
            List<Prompt> prompts = ActivePlayerPrompts(queue);
            for (int i = 0; i < prompts.Count; i++)
            {
                Prompt prompt = prompts[i];
                if (!string.IsNullOrEmpty(filter) && !Same(prompt.name, filter)) continue;
                var entry = new JObject();
                entry["name"] = prompt.name;
                // The dead-end test comes first, and the skip is what it gates. A
                // narrative prompt nothing can ever answer is dropped whichever way
                // `narrative` is set, because dropping one applies no option and so is
                // not answering a story event. Order matters: narrative=false is the
                // mode a caller is in while driving alert.choose by hand, and a press
                // on an event whose target is gone is what creates the dead end, so a
                // skip ahead of the test would make the drop unreachable in the one
                // mode that produces the state.
                if (!answerNarrative && Same(prompt.name, NarrativePrompt)
                    && NarrativeDeadEnd(prompt) == null)
                {
                    // Deliberate, and marked as such. A client that waits on a narrative
                    // prompt has to tell this apart from the queue-drain wait below, and
                    // telling them apart by reading the reason text is how a wording
                    // change becomes a silent behavior change.
                    //
                    // The flag means the same thing in both branches: nothing on screen
                    // can answer this prompt yet. It used to be a hard false here,
                    // stamped from the switch without looking at the box at all, so a
                    // box still queued behind the notifications in front of it was
                    // reported as a decision standing ready to be taken -- and a client
                    // waiting for the box gave up on it two polls in.
                    entry["waitingForBox"] = !boxAnswerable;
                    entry["mode"] = narrativeMode;
                    entry["reason"] = "narrative=false, so this was left standing for a "
                        + "deliberate answer through alert.choose ("
                        + NarrativeModeNote(narrativeMode)
                        + "); it still blocks the clock" + (boxAnswerable
                            ? ", and its alert box is up with a live option button"
                            : ", and its alert box has not come up yet, so nothing "
                              + "can answer it on this pass");
                    AddSkipped(skipped, skippedPrompts, prompt, entry);
                    continue;
                }
                string via = null;
                string reason = null;
                try { via = Answer(prompt, entry); }
                catch (VerbError e) { reason = e.Message; }
                catch (Exception e) { reason = Note(e); }
                if (via != null)
                {
                    entry["via"] = via;
                    dismissed.Add(entry);
                }
                else
                {
                    if (reason == null && Same(prompt.name, NarrativePrompt))
                    {
                        // The queue is draining, not a decision waiting on the caller.
                        // The flag is what a client should read; the sentence is for a
                        // person.
                        entry["waitingForBox"] = true;
                        reason = "its alert box is the only path that may answer it, and "
                            + "that box has not come up yet; the screens pass takes it "
                            + "as soon as it does";
                    }
                    if (reason == null) reason = "no neutral answer";
                    entry["reason"] = reason;
                    AddSkipped(skipped, skippedPrompts, prompt, entry);
                }
            }

            // The escape hatch: drop what is left so the clock runs. The decision behind the
            // prompt goes unmade, which costs the player a research slot or a mission phase
            // and leaves the save intact.
            var forced = new JArray();
            if (all)
            {
                List<Prompt> left = ActivePlayerPrompts(queue);
                for (int i = 0; i < left.Count; i++)
                {
                    Prompt prompt = left[i];
                    if (!string.IsNullOrEmpty(filter) && !Same(prompt.name, filter)) continue;
                    // Never force-dropped, whichever way `narrative` is set. A narrative
                    // prompt is half of a pair: TIGlobalValuesState.TriggerNarrativeEvent
                    // queues an alert on the NOTIFICATION queue at IL_0233 and a prompt
                    // on this one at IL_0258. AlertNarrativeEvent is called once, at
                    // trigger time, and nothing on the prompt side reaches back into the
                    // notification queue. So RemovePrompt takes this half and leaves the
                    // box standing with its option buttons live: it arrives on schedule,
                    // and pressing an option applies the choice with its costs and
                    // grants exactly as if the prompt were still there. Dropping is
                    // therefore not a forfeit here: the decision is still made, by the
                    // box, and this pass has only hidden the fact that it is pending.
                    // The box is left to take it.
                    //
                    // The prompt pass above still drops the one narrative prompt no box
                    // can answer, on the engine's own condition. That is not this rule's
                    // business: the drop there is a dead end being cleared, not a
                    // pending decision being forfeited.
                    //
                    // The prompt pass above already entered it in `skipped` with the
                    // reason it is still standing, so this annotates that entry rather
                    // than adding a second one for the same prompt.
                    if (Same(prompt.name, NarrativePrompt))
                    {
                        NotForced(skipped, skippedPrompts, prompt);
                        continue;
                    }
                    string threw = null;
                    try { queue.RemovePrompt(prompt); }
                    catch (Exception e) { threw = Note(e); }
                    // Measured against the two lists the clock reads, not taken from
                    // RemovePrompt's return value. That value is the MASTER list's
                    // Remove result; the mirror the clock counts is touched on two
                    // conditions only, and a nation handed to a different, non-null
                    // executive faction meets neither, so the call returns true with the
                    // entry still blocking. Trusting it reports the prompt forced while
                    // `remaining` still counts it, so the loop stops, the caller drops
                    // again, that reports success again, and the run spins there.
                    if (!StillBlocking(queue, prompt))
                    {
                        var o = new JObject();
                        o["name"] = prompt.name;
                        forced.Add(o);
                        continue;
                    }
                    NotDropped(skipped, skippedPrompts, prompt, threw);
                }
            }

            var result = new JObject();
            result["screens"] = screens;
            // Echoed so a caller can see which mode this pass actually ran under,
            // rather than the one it believes it sent: an argument the DLL does not
            // read is indistinguishable from one it does, and this is the key that
            // tells an old DLL from a new one.
            result["narrativeMode"] = narrativeMode;
            result["narrativeNotes"] = narrativeNotes;
            result["dismissed"] = dismissed;
            result["skipped"] = skipped;
            result["forced"] = forced;
            result["remaining"] = ActivePlayerPrompts(queue).Count;
            try { result["blocked"] = queue.anyActivePlayerBlocking; }
            catch (Exception) { result["blocked"] = null; }
            return result;
        }

        // The caller's stated mode, or the one implied by `narrative` for a client
        // that does not send it. The two are kept consistent in one place because
        // three sites read the mode and none of them may disagree with the switch.
        static string NarrativeMode(JObject args, bool answerNarrative)
        {
            string mode = Str(args, "narrativeMode");
            if (!string.IsNullOrEmpty(mode)) return mode;
            return answerNarrative ? "ai" : "false";
        }

        // What "left standing" means under this mode, in the reply a person reads.
        static string NarrativeModeNote(string mode)
        {
            if (Same(mode, "llm"))
                return "narrativeMode llm: the caller stops on it and answers it "
                    + "through alert.choose";
            if (Same(mode, "false"))
                return "narrativeMode false: attended, a person answers it";
            return "narrativeMode " + (mode != null ? mode : "?");
        }

        // One skipped entry, with the prompt it belongs to recorded beside it so the
        // force-drop pass can annotate that entry and no other.
        static void AddSkipped(JArray skipped, List<Prompt> skippedPrompts,
                               Prompt prompt, JObject entry)
        {
            skipped.Add(entry);
            skippedPrompts.Add(prompt);
        }

        // Marks the skipped entry `all` declined to force-drop. Every prompt the drop
        // pass sees has already been through the prompt pass, so the entry exists; the
        // guard is for a build where that stops being true.
        static void NotForced(JArray skipped, List<Prompt> skippedPrompts, Prompt prompt)
        {
            Annotate(skipped, skippedPrompts, prompt, null,
                "still queued and not force-dropped");
        }

        // A force-drop that was attempted and did not take. Reported as still blocking
        // rather than as forced, because that is what the clock sees.
        static void NotDropped(JArray skipped, List<Prompt> skippedPrompts,
                               Prompt prompt, string threw)
        {
            string why = "RemovePrompt was called and the prompt is still in the "
                + "active player's blocking lists"
                + (threw != null ? " (it threw: " + threw + ")" : "")
                + "; a nation prompt whose nation changed hands is removed from the "
                + "master list and left in the mirror the clock counts";
            Annotate(skipped, skippedPrompts, prompt, "stillBlocking", why);
        }

        // Annotates the ONE entry this prompt produced. Located by prompt identity
        // rather than by name: Prompt is a struct with value equality over all five
        // fields, and two prompts sharing a name are two prompts. Matching on the name
        // flagged every entry carrying it, so one nation's undroppable prompt reported
        // a failure against another nation's prompt that dropped cleanly.
        static void Annotate(JArray skipped, List<Prompt> skippedPrompts,
                             Prompt prompt, string flag, string why)
        {
            int at = skippedPrompts.IndexOf(prompt);
            JObject entry = at >= 0 && at < skipped.Count
                ? skipped[at] as JObject : null;
            if (entry != null)
            {
                entry["forceDropped"] = false;
                if (flag != null)
                {
                    entry[flag] = true;
                    entry["forceDropReason"] = why;
                }
                return;
            }
            var added = new JObject();
            added["name"] = prompt.name;
            added["forceDropped"] = false;
            if (flag != null) added[flag] = true;
            added["reason"] = why;
            AddSkipped(skipped, skippedPrompts, prompt, added);
        }

        static List<Prompt> ActivePlayerPrompts(TIPromptQueueState queue)
        {
            List<Prompt> all = FactionPrompts(queue);
            all.AddRange(NationPrompts(queue));
            return all;
        }

        // Copies: answering a prompt removes it from the live list mid-walk.
        static List<Prompt> FactionPrompts(TIPromptQueueState queue)
        {
            try
            {
                List<Prompt> list = queue.activePlayerFactionPromptList;
                return list != null ? new List<Prompt>(list) : new List<Prompt>();
            }
            catch (Exception) { return new List<Prompt>(); }
        }

        static List<Prompt> NationPrompts(TIPromptQueueState queue)
        {
            try
            {
                List<Prompt> list = queue.activePlayerNationPromptList;
                return list != null ? new List<Prompt>(list) : new List<Prompt>();
            }
            catch (Exception) { return new List<Prompt>(); }
        }

        // Returns null for a prompt with no answer in the table.
        static string Answer(Prompt prompt, JObject entry)
        {
            Func<Prompt, TIFactionState, JObject, string> answer;
            if (prompt.name == null || !answers.TryGetValue(prompt.name, out answer)) return null;
            // The narrative answer is the one entry that takes no faction: the only
            // thing this pass does to a narrative prompt is drop a dead end, which
            // reads nothing off a faction. So the lookup is skipped for it rather
            // than run for a value nothing uses, and the drop cannot be lost to a
            // PromptFaction throw. What it would cost is the whole point of the drop:
            // a dead end nothing removes holds the clock for the rest of the
            // campaign.
            TIFactionState faction = Same(prompt.name, NarrativePrompt)
                ? null : PromptFaction(prompt);
            return answer(prompt, faction, entry);
        }

        static string AnswerTech(Prompt prompt, TIFactionState faction, JObject entry)
        {
            TITechTemplate tech = new PavonisInteractive.TerraInvicta.Tasks.StratTechSelector()
                .SelectTech(faction);
            if (tech == null) throw new VerbError("no tech to select");
            Runner(faction).StartAction(
                new PavonisInteractive.TerraInvicta.Actions.SelectTechAction(faction, prompt.value, tech));
            return "SelectTechAction " + tech.dataName;
        }

        static string AnswerProject(Prompt prompt, TIFactionState faction, JObject entry)
        {
            TIProjectTemplate project = new PavonisInteractive.TerraInvicta.Tasks.StratProjectSelector()
                .SelectProject(faction, prompt.value);
            if (project == null) throw new VerbError("no project to select");
            Runner(faction).StartAction(
                new PavonisInteractive.TerraInvicta.Actions.SelectProjectForDevelopmentAction(
                    faction, prompt.value, project));
            return "SelectProjectForDevelopmentAction " + project.dataName;
        }

        static string AnswerMissions(Prompt prompt, TIFactionState faction, JObject entry)
        {
            CouncilorMissionCanvasController missions =
                Require<CouncilorMissionCanvasController>("councilor mission controller");
            if (!Clickable(missions.confirmAssignmentsButton))
                throw new VerbError("mission assignments are not confirmable yet");
            missions.PlayerConfirmsMissionAssignments();
            return "PlayerConfirmsMissionAssignments";
        }

        static string AnswerOrgSale(Prompt prompt, TIFactionState faction, JObject entry)
        {
            TIOrgState org = SellableOrg(faction);
            if (org == null) throw new VerbError("faction has no sellable unassigned org");
            Runner(faction).StartAction(
                new PavonisInteractive.TerraInvicta.Actions.SellOrgAction(org, faction, null));
            return "SellOrgAction " + StateName(org);
        }

        // PromptSelectTrajectory marks an open thrust-profile tool, and nothing else.
        // OperationCanvasController.OpenThrustProfileTool queues
        // (activePlayer, null, null, "PromptSelectTrajectory", 0) and its private
        // CloseThrustProfileTool removes that same tuple. It is a faction prompt, so it
        // reaches the active player's list unconditionally, freezes the clock and blocks
        // saving; it is in the engine's AI prompt handler nowhere (the handled name is
        // PromptChangeTrajectory, a different prompt), and an unhandled faction prompt is
        // ignored in silence rather than dropped.
        //
        // The screens pass presses changeTrajectoryCancelButton when it is up, which is
        // the change-trajectory flow's own route to CleanupTrajectoryChange. The same
        // tool also opens during ordinary operation targeting, where that button is not
        // up, and this is the second path for that: CloseActionPanel is public and
        // reaches ShutdownTargetSelection, CloseThrustProfileTool and the engine's own
        // removal. The direct drop last is for a panel that is already down with the
        // prompt still standing, where no engine path is left to run.
        //
        // No decision is forfeited by any of these. The prompt records that a tool is
        // open; the order it was being used to plan is untouched, and re-opening the
        // tool queues the prompt again.
        static string AnswerTrajectory(Prompt prompt, TIFactionState faction, JObject entry)
        {
            TIPromptQueueState queue = PromptQueue();
            OperationCanvasController operations = Safe<OperationCanvasController>(
                delegate { return OperationCanvasController.Singleton; }, null);

            if (operations != null && Clickable(operations.changeTrajectoryCancelButton))
            {
                try { operations.OnCancelTrajectoryChange(); }
                catch (Exception e) { entry["cancelThrew"] = Note(e); }
                if (!StillBlocking(queue, prompt)) return "OnCancelTrajectoryChange";
            }
            if (operations != null)
            {
                try { operations.CloseActionPanel(true); }
                catch (Exception e) { entry["closeThrew"] = Note(e); }
                if (!StillBlocking(queue, prompt)) return "CloseActionPanel";
            }

            // Nothing on screen answers it. The prompt struct is removed by value, which
            // is an exact match on all five fields, and the result is measured against
            // the lists the clock reads rather than read off RemovePrompt.
            try { queue.RemovePrompt(prompt); }
            catch (Exception e)
            {
                throw new VerbError("no operation canvas path cleared "
                    + "PromptSelectTrajectory and RemovePrompt threw: " + Note(e));
            }
            if (StillBlocking(queue, prompt))
                throw new VerbError("PromptSelectTrajectory is still in the active "
                    + "player's prompt lists after the cancel button, CloseActionPanel "
                    + "and a direct removal; it holds the clock and blocks saving");
            entry["dropped"] = true;
            return "dropped: the thrust profile tool's prompt with no tool on screen to "
                + "close, so no engine path was left to remove it; no order was changed";
        }

        // A narrative event, answered the way the engine answers one for an AI faction.
        // TIPromptQueueState.HandleRespondToNarrativeEvent is that path and is private,
        // so its three steps are repeated here out of public pieces, in its order and
        // with its arguments: the dispatcher hands it the prompt's own
        // promptingGameState as the event target and its relatedGameState as the
        // secondary, then it reads the pending event, asks its narrativeResponseStrategy
        // for an option and runs SelectNarrativeEventOption.
        //
        // The strategy is the rule, not "first option". StratNarrativeResponseSelector
        // is the class the prompt queue's own constructor installs, and it weighs each
        // VALID option by its baseAIPreference and the faction's AI values before
        // picking; it falls back to option 0 by itself when no option carries a weight.
        // A single-option acknowledgement therefore comes back as 0 either way. The
        // pick is recorded on the dismissal entry, index and button text both, because
        // an unattended run that answers story events silently is worse than one that
        // stops: the record is the point.
        // THE PROMPT PASS NEVER ANSWERS A NARRATIVE EVENT. Exactly one call site in this
        // mod may, and it is the alert box in Alerts above.
        //
        // A narrative event is two independent queue entries, not one:
        // TIGlobalValuesState.TriggerNarrativeEvent calls
        // TINotificationQueueState.AlertNarrativeEvent at IL_0233 and
        // TIPromptQueueState.AddPrompt at IL_0258, then appends to
        // pendingNarrativeEvents. Answering through the prompt queue runs
        // SelectNarrativeEventOption, whose Execute reaches
        // ExecuteNarrativeEventOption and RemovePromptStatic (IL_00c8, IL_00d6) and
        // touches the NOTIFICATION queue nowhere. So the queued alert still arrives a
        // poll or two later with its option buttons live, and pressing one applies the
        // player's choice A SECOND TIME -- the effects, the costs, the org and project
        // grants, all of it.
        //
        // It also corrupts the bookkeeping on the way through: the box resolves its
        // prompt with FindPromptForNarrativeEvent (IL_006b), which now finds nothing,
        // so ExecuteNarrativeEventOption's closing ClearNarrativeEvent(prompt) at
        // IL_040a matches no entry, RemoveAll returns 0 instead of 1 and the engine
        // logs "Error in clearing narrative events" over a pendingNarrativeEvents entry
        // that outlives the event.
        //
        // The box is therefore the only path that resolves the prompt while the pending
        // entry still matches it, and the only one that runs the notification-side
        // teardown.
        //
        // `faction` is unused and Answer passes null for it: the drop below is the only
        // thing this method does and it needs no faction, which is what lets a
        // nation-scoped narrative prompt through a pass whose faction lookup would
        // throw on one.
        static string AnswerNarrativeEvent(Prompt prompt, TIFactionState faction,
                                           JObject entry)
        {
            PendingNarrativeEvent pending =
                TIGlobalValuesState.GetCurrentNarrativeEvent(prompt);
            string pendingName = Safe<string>(delegate { return pending.dataName; }, null);

            // The one thing this pass may still do, and it applies no option: a prompt
            // nothing can ever answer, by the box least of all, would block the clock
            // for the rest of the campaign.
            string deadEnd = NarrativeDeadEnd(prompt);
            if (deadEnd != null)
            {
                entry["event"] = pendingName != null
                    ? (JToken)new JValue(pendingName) : JValue.CreateNull();
                entry["option"] = JValue.CreateNull();
                entry["deadEnd"] = deadEnd;
                TIPromptQueueState queue = PromptQueue();
                queue.RemovePrompt(prompt);
                TIGlobalValuesState.ClearNarrativeEvent(prompt);
                // Measured, not taken from RemovePrompt's return value, and measured
                // against the two lists the clock itself reads. Reporting the intent
                // instead would count a drop that silently failed as a story event
                // disposed of, once per poll for the rest of the run.
                bool gone = !StillBlocking(queue, prompt);
                entry["dropped"] = gone;
                if (!gone)
                    throw new VerbError("dead end (" + deadEnd + "), so nothing could "
                        + "ever answer it, and it is still in the active player's "
                        + "prompt lists after the removal; the pending event was "
                        + "cleared and the prompt still blocks the clock");
                return "dropped, dead end (" + deadEnd + "): its alert box would apply "
                    + "no option and would not come back, so nothing could ever answer "
                    + "it; no option was applied here either";
            }

            // Answerable, and not by this pass. Reported as skipped so the caller sees
            // it standing rather than silently waiting.
            return null;
        }

        // Why nothing can ever answer this narrative prompt, or null when something
        // still can. Both halves are the engine's own test.
        //
        // TIPromptQueueState.HandleRespondToNarrativeEvent drops the prompt and clears
        // the pending event when `narrativeEvent == null || !TIGameState.Valid(
        // eventTarget)` (IL_001b-IL_0024), rather than selecting an option. Valid(x) is
        // x != null && x.exists, and get_deleted is !get_exists(), so the two forms are
        // the same test and this file writes it the second way throughout.
        //
        // The target half is the one the alert box turns on.
        // NotificationScreenController.OnOptionButtonPressed tests
        // currentNarrativeEvent.selectedTarget for null at IL_0082 and for deleted at
        // IL_0094, and both branch to IL_00e1, which logs "Target for NarrativeEvent
        // <name> deleted or didn't exist" and skips the StartAction at IL_00da -- the
        // only construction of SelectNarrativeEventOption on that path, and therefore
        // the only thing that ever removes the prompt. The teardown from IL_0100 runs
        // on both branches, so the press consumes the box, the box does not come back,
        // and the prompt holds the clock with nothing left to clear it.
        //
        // prompt.promptingGameState is that same object. The dispatcher hands it to
        // HandleRespondToNarrativeEvent as eventTarget, and
        // TIGlobalValuesState.FindPromptForNarrativeEvent builds the stored Prompt with
        // the box's selectedTarget in the promptingGameState slot (IL_0015-IL_0029).
        // PendingNarrativeEvent carries no target field of its own: it holds prompt,
        // dataName, allTargetsandSeconds and the narrativeEvent property.
        static string NarrativeDeadEnd(Prompt prompt)
        {
            PendingNarrativeEvent pending = Safe<PendingNarrativeEvent>(
                delegate { return TIGlobalValuesState.GetCurrentNarrativeEvent(prompt); },
                default(PendingNarrativeEvent));
            TINarrativeEventTemplate template = Safe<TINarrativeEventTemplate>(
                delegate { return pending.narrativeEvent; }, null);
            if (template == null)
                return "no narrative event template behind the prompt";

            TIGameState target = Safe<TIGameState>(
                delegate { return prompt.promptingGameState; }, null);
            if (target == null)
                return "the narrative event's target is null";
            if (Safe<bool>(delegate { return target.deleted; }, false))
                return "the narrative event's target " + StateName(target)
                    + " has been deleted";
            return null;
        }

        // Whether this prompt is still in one of the two lists that hold the clock.
        // TIPromptQueueState.get_anyActivePlayerBlocking counts
        // activePlayerNationPromptList and activePlayerFactionPromptList and nothing
        // else (IL_0001-IL_001a), so those lists are the state worth measuring.
        //
        // RemovePrompt's return value does not measure them. It is set from the master
        // factionList/nationList Remove (IL_001c, IL_0081) and the mirror list's own
        // Remove result is discarded with a pop (IL_0044, IL_00ca). The mirror is
        // touched on two conditions only: the prompt's nation still has
        // executiveFaction == activePlayer (IL_0088-IL_009c), or it has no executive
        // faction at all and the nation is gone (IL_009e-IL_00bc). The entry entered
        // that mirror under whatever executive faction held the nation when AddPrompt
        // ran, and neither condition fires on a handover to a different, non-null
        // faction. Such a handover makes RemovePrompt return true with the prompt still
        // blocking, which is the costly direction: the caller is told the queue is clear
        // and the clock stays frozen.
        static bool StillBlocking(TIPromptQueueState queue, Prompt prompt)
        {
            return Safe<bool>(
                delegate { return ActivePlayerPrompts(queue).Contains(prompt); }, false);
        }

        // The pick and the rule that made it, shared by both paths that answer a
        // narrative event so the alert box and the prompt queue cannot drift apart.
        class NarrativeChoice
        {
            public int option;
            public int options;     // what the popup actually offers
            public string pickedBy;
        }

        // How many options the event really has. NOT eventOptions.Count:
        // StratNarrativeResponseSelector loops `i < eventTemplate.numOptions`
        // (IL_0126-IL_012E) and the popup builds its buttons from the same field, while
        // 179 of the 260 shipped events carry MORE entries in eventOptions than
        // numOptions -- event_Hurricane is 2 against 4. Counting the list would report a
        // number the player never sees and would let the fallback pick an index the game
        // never offers. Clamped to the list because the engine indexes it with this.
        static int NarrativeOptionCount(TINarrativeEventTemplate template)
        {
            return Safe<int>(delegate
            {
                List<NarrativeEventOption> list = template.eventOptions;
                int declared = template.numOptions;
                int available = list != null ? list.Count : 0;
                if (declared < 0) declared = 0;
                return declared < available ? declared : available;
            }, 0);
        }

        static NarrativeChoice ChooseNarrativeOption(TINarrativeEventTemplate template,
            TIFactionState faction, TIGameState target, TIGameState secondary)
        {
            int options = NarrativeOptionCount(template);
            if (options <= 0) return null;

            var choice = new NarrativeChoice();
            choice.options = options;
            choice.pickedBy = "StratNarrativeResponseSelector";
            choice.option = Safe<int>(delegate
            {
                return new PavonisInteractive.TerraInvicta.Tasks
                    .StratNarrativeResponseSelector()
                    .SelectOption(faction, target, secondary, template);
            }, -1);
            if (choice.option >= 0 && choice.option < options) return choice;

            // The strategy threw or answered outside the offered range. Alerts falls
            // back to the first clickable button, whose interactability the engine
            // derives from the same ValidOption test the strategy filters on, index 0
            // excepted: the popup always offers it.
            return null;
        }

        // The event's target map, off the controller's own record. optionButtonDetail
        // takes it as a fifth argument and NarrativeEventStringReplacement reads it to
        // fill the target names into the text; passing an empty one costs a detail
        // string with the placeholders unresolved rather than a throw, which is why the
        // read is guarded rather than fatal.
        static Dictionary<TIGameState, TIGameState> AllTargets(
            CurrentNarrativeEventData current)
        {
            Dictionary<TIGameState, TIGameState> map =
                Safe<Dictionary<TIGameState, TIGameState>>(
                    delegate { return current.allTargetsandSeconds; }, null);
            return map != null ? map : new Dictionary<TIGameState, TIGameState>();
        }

        static void ReportNarrativeChoice(JObject entry,
            TINarrativeEventTemplate template, NarrativeChoice choice,
            TIFactionState faction, TIGameState target, TIGameState secondary,
            Dictionary<TIGameState, TIGameState> allTargets)
        {
            // Every read here is guarded. This runs before the button press and its
            // result is the only record of what the press answered, so a throw in it
            // must not be what decides whether the event is reported.
            entry["event"] = Safe<JToken>(
                delegate { return (JToken)new JValue(template.dataName); },
                JValue.CreateNull());
            entry["option"] = choice.option;
            entry["options"] = choice.options > 0
                ? (JToken)new JValue(choice.options)
                : (JToken)new JValue(NarrativeOptionCount(template));
            entry["pickedBy"] = choice.pickedBy;
            entry["optionText"] = Safe<JToken>(delegate
            {
                string text = template.optionButtonText(faction, target, secondary,
                    choice.option);
                return text != null ? (JToken)new JValue(text) : JValue.CreateNull();
            }, JValue.CreateNull());
            // What the button says it will do, under the popup's own rules. The text
            // is the option's outcomes, its costs and its conditions, and it is the
            // only record of what an unattended press applied: optionText alone names
            // the button and says nothing about the effect.
            //
            // Player-visible, gate and all. OptionDetail stops at
            // "hideOptionInfoFromFaction" when the option carries a
            // TIFactionCondition_eIdeology the acting faction fails, and a digest of
            // what the game showed has to show that too. alert.choose detail=true is
            // the reading that crosses the gate.
            entry["optionDetail"] = Safe<JToken>(delegate
            {
                string text = template.optionButtonDetail(faction, target, secondary,
                    choice.option, allTargets);
                return text != null ? (JToken)new JValue(text) : JValue.CreateNull();
            }, JValue.CreateNull());
            entry["target"] = Safe<JToken>(
                delegate { return State(target); }, JValue.CreateNull());
        }

        static TIFactionState PromptFaction(Prompt prompt)
        {
            TIFactionState faction = null;
            try
            {
                TIGameState acting = prompt.actingState;
                if (acting != null) faction = acting.ref_faction;
            }
            catch (Exception) { }
            if (faction == null) throw new VerbError("prompt has no faction");
            return faction;
        }

        static PavonisInteractive.TerraInvicta.Entities.Player Runner(TIFactionState faction)
        {
            PavonisInteractive.TerraInvicta.Entities.Player player = null;
            try { player = faction.playerControl; }
            catch (Exception) { }
            if (player == null) throw new VerbError("faction has no player control");
            return player;
        }

        // Only a marketable org can be sold, which is the same filter the vanilla Autopilot
        // applies before it drops one.
        static TIOrgState SellableOrg(TIFactionState faction)
        {
            List<TIOrgState> orgs = null;
            try { orgs = faction.unassignedOrgs; }
            catch (Exception) { }
            if (orgs == null) return null;
            for (int i = 0; i < orgs.Count; i++)
            {
                TIOrgState org = orgs[i];
                if (org == null) continue;
                try
                {
                    TIOrgTemplate template = org.template;
                    if (template != null && template.allowedOnMarket) return org;
                }
                catch (Exception) { }
            }
            return null;
        }

        // A notification that carries a choice blocks until an option is taken, and which
        // option matters to a test: prompts.dismiss always takes the first live one. With
        // no arguments the verb reports the open alert's text and its live options; with
        // an option index it presses that button. detail=true adds what each option
        // would do, read off the event template rather than the popup, and lists the
        // options the popup withheld along with the ones it drew.
        static JToken AlertChoose(JObject args)
        {
            NotificationScreenController notices =
                Require<NotificationScreenController>("notification controller");

            bool open = false;
            try { open = notices.singleAlertBox != null && notices.singleAlertBox.activeInHierarchy; }
            catch (Exception) { }
            var o = new JObject();
            o["open"] = open;
            if (!open) return o;

            var texts = new JArray();
            try
            {
                var labels = notices.singleAlertBox.GetComponentsInChildren<TMPro.TMP_Text>(false);
                for (int i = 0; i < labels.Length && texts.Count < 8; i++)
                {
                    string s = labels[i] != null ? labels[i].text : null;
                    if (!string.IsNullOrEmpty(s)) texts.Add(new JValue(s));
                }
            }
            catch (Exception) { }
            o["text"] = texts;

            UnityEngine.UI.Button[] options = null;
            try { options = notices.optionButtons; }
            catch (Exception) { }

            // Whether the box on screen is a narrative event box at all, which decides
            // whether the controller's record describes it. PushNextAlert branches on
            // the queue item's narrativeEventAlert (IL_0446) and the two sides part ways
            // over this panel: the narrative side turns it on (IL_04a7) one instruction
            // after writing currentNarrativeEvent (IL_04a2), and both non-narrative sides
            // turn it off (IL_0585, IL_0666). That stfld is the record's only writer and
            // nothing ever clears it, so on a plain notification box the record still
            // names the last story event answered. Reading the panel is what keeps that
            // stale name out of this reply.
            //
            // Three answers rather than two, because the panel object can be null and
            // that is not the same as a panel that is off. A build that renames or
            // drops the field, or a controller read before its references are wired,
            // leaves a null behind while a real story event is on screen, and
            // reporting that as a notification box is a claim the reading cannot
            // support. So a null is unknown, and the press below refuses on it the
            // way it refuses on a panel that is off: a box that cannot be shown to
            // belong to the event a press would answer is not one to press.
            UnityEngine.GameObject panel = Safe<UnityEngine.GameObject>(
                delegate { return notices.narrativeEventButtonsPanel; }, null);
            bool panelKnown = panel != null;
            bool narrativeBox = panelKnown
                && Safe<bool>(delegate { return panel.activeInHierarchy; }, false);
            o["narrativeBox"] = panelKnown
                ? (JToken)new JValue(narrativeBox) : JValue.CreateNull();
            if (!panelKnown)
                o["narrativeBoxNote"] = "the controller's option-button panel could not "
                    + "be read, so whether this box is a narrative event box is unknown; "
                    + "no event is reported for it and a press is refused";

            // The controller's record of the event this box is showing, read once and
            // used twice: by detail below, and by the press further down, which has to
            // read it BEFORE pressing because the press is what destroys it.
            CurrentNarrativeEventData current = new CurrentNarrativeEventData();
            bool haveEvent = narrativeBox && CurrentNarrativeEvent(notices, out current);
            // The event behind this box, named off the controller's own record and
            // read here, before anything in this verb presses anything, because the
            // press is what destroys the record.
            //
            // This is the ONE write of `event`. It used to be written twice: detail
            // set it from the template's dataName and the press branch overwrote it
            // with the record's name for the same event a few lines later. The
            // record's name is the authoritative one, because it survives a template
            // this build cannot resolve -- which is exactly the case detail has to
            // report on -- and the key now means the same thing on every reply: the
            // narrative event this box is showing, null when there is none. A plain
            // notification box has none, whatever the controller's record still holds.
            string eventName = haveEvent
                ? Safe<string>(delegate { return current.eventTemplateName; }, null)
                : null;
            o["event"] = eventName != null
                ? (JToken)new JValue(eventName) : JValue.CreateNull();
            bool detail = Flag(args, "detail");
            TINarrativeEventTemplate template = haveEvent
                ? Safe<TINarrativeEventTemplate>(
                    delegate { return current.eventTemplate; }, null)
                : null;
            TIFactionState acting = Safe<TIFactionState>(
                delegate { return notices.activePlayer; }, null);

            // Two walks, and which one runs is what `detail` selects.
            //
            // Without detail: the live buttons, which is what a caller pressing one
            // needs and what every client reading this reply has always got. An
            // option with no live button cannot be pressed, and listing it here
            // would make an unanswerable box read as an answerable one.
            //
            // With detail: every option the EVENT declares, up to numOptions,
            // paired with its button by index -- which is the engine's own pairing,
            // since FillOutOptionButtons walks `i < numOptions` and writes option i
            // into button i. An option whose actingFactionCondition fails leaves
            // its button active and non-interactable, so the button walk drops it
            // and `infoHiddenFromFaction`, the one reading detail exists to
            // produce, could never be true. `hidden` says the popup is not drawing
            // that option, and pressing it is still refused below.
            var opts = new JArray();
            int declared = detail && template != null
                ? NarrativeOptionCount(template) : 0;
            int buttons = options != null ? options.Length : 0;
            int walk = declared > buttons ? declared : buttons;
            for (int i = 0; i < walk; i++)
            {
                UnityEngine.UI.Button button = i < buttons ? options[i] : null;
                bool clickable = Clickable(button);
                if (!clickable && i >= declared) continue;
                var row = new JObject();
                row["option"] = i;
                // Reads a hidden button's text too, so the label is present for
                // every row whose button object exists and null only past the
                // end of the array. See OptionLabel.
                row["label"] = OptionLabel(button);
                if (detail && template != null)
                {
                    // The engine's own signal for "is the popup drawing this
                    // option": FillOutOptionButtons calls SetActive(i <
                    // numOptions) on each button and nothing else touches it.
                    //
                    // Interactability is a different question and gets its own
                    // key. Every drawn button is set non-interactable at fill
                    // time and the valid ones are turned back on by
                    // EnableNarrativeButtonWithDelay, which waits
                    // notificationReceiveInputDelay seconds -- half a second on
                    // stock config. Deriving `hidden` from interactability, as
                    // this once did, reported every option of a freshly opened
                    // box as hidden for that whole window.
                    row["hidden"] = !ButtonOffered(button);
                    row["interactable"] = ButtonInteractable(button);
                    row["detail"] = NarrativeOptionDetail(template, i, acting,
                        current.selectedTarget, current.secondaryTarget,
                        AllTargets(current));
                }
                opts.Add(row);
            }
            o["options"] = opts;
            // Whether a press would actually land. A live option button is not
            // enough: the controller arms narrative input from a delayed coroutine,
            // and a press ahead of that is discarded without a trace -- which is why
            // the press below refuses rather than makes one. Reported so a caller
            // reading the box can wait a poll instead of pressing into the window and
            // getting the refusal.
            o["pressLands"] = NarrativePressLands(notices);
            if (detail && template == null)
            {
                if (!panelKnown)
                    o["detailNote"] = "no option detail: this build's notification "
                        + "controller would not say whether the box is a narrative "
                        + "event box (see narrativeBoxNote)";
                else if (!narrativeBox)
                    o["detailNote"] = "no narrative event behind this box, so there is "
                        + "nothing to detail: its buttons are a notification's, not an "
                        + "event's";
                else if (haveEvent)
                    o["detailNote"] = "the narrative event on this box does not resolve "
                        + "to a template in this build, so no option detail is available";
                else
                    o["detailNote"] = "the controller's narrative event record could not "
                        + "be read on this build, so no option detail is available";
            }

            int pick = OptionalInt(args, "option");
            if (pick >= 0)
            {
                // The press and nothing else. OnOptionButtonPressed below is a
                // human-UI handler on the same screens prompts.dismiss refuses to
                // touch once the seat has been moved, and answering a narrative
                // event is the exact action that took the game down there: the
                // option is applied for the faction the engine thinks is playing,
                // and its follow-on runs through visualizers only the seated
                // faction's councilors carry.
                //
                // Ahead of the clickability and input-window checks, because a
                // moved seat is a refusal of the whole press whatever those two
                // would have said, and the caller has to put the seat back before
                // either answer is worth anything. The read above already ran and
                // stands; a call with no `option` never reaches this and reads the
                // box, `detail` included, with the seat wherever it is.
                if (ActivePlayerSeatMoved())
                    return SeatMovedRefusal("this verb presses the option button",
                        "pressed and the event stands unanswered",
                        "alert.choose without `option` still reads the box, "
                        + "`detail` included");
                // A press is only ever an answer to a narrative event. Every path
                // through OnOptionButtonPressed past its guard reads
                // currentNarrativeEvent and runs a SelectNarrativeEventOption with
                // the index pressed, and nothing ever clears that record -- so a
                // press on a box that is not a narrative box answers the last story
                // event the campaign saw, which is not on screen and not what the
                // caller is looking at. This used to press and then report
                // `answered: false`, which described the box rather than what the
                // press did.
                //
                // The unknown case is refused for the same reason and not a weaker
                // one: a panel that cannot be read is a box that cannot be shown to
                // belong to the event the press would answer.
                if (!narrativeBox)
                    throw new VerbError(panelKnown
                        ? "this box is a notification rather than a narrative event "
                            + "(its option-button panel is off), and a press on it "
                            + "would answer the last narrative event the controller "
                            + "recorded, which is not this box. Nothing was pressed; "
                            + "prompts.dismiss closes a notification through its own "
                            + "okay button"
                        : "the controller's option-button panel could not be read, so "
                            + "this box cannot be shown to be the narrative event a "
                            + "press would answer. Nothing was pressed");
                if (options == null || pick >= options.Length || !Clickable(options[pick]))
                    throw new VerbError("option " + pick + " is not clickable");
                if (!NarrativePressLands(notices))
                    throw new VerbError("the option buttons are live but the controller "
                        + "is not taking narrative input yet, so this press would be "
                        + "discarded without a trace; call again in a moment");

                // The record was read above, before anything in this verb pressed
                // anything, because the press is what destroys it.
                // OnOptionButtonPressed applies an option only when
                // currentNarrativeEvent.selectedTarget is non-null and not deleted
                // (IL_0082, IL_0094); otherwise it logs the target away and skips the
                // StartAction at IL_00da while running the teardown from IL_0100 all
                // the same. So the box goes either way and the caller cannot tell from
                // the reply which of the two happened.
                //
                // Reported rather than refused. Refusing would leave the box standing
                // with no verb that can clear it, and the press is what tears it down;
                // the prompt it leaves behind is cleared by prompts.dismiss, whose
                // prompt pass drops a narrative prompt on this same condition.
                TIGameState target = haveEvent ? current.selectedTarget : null;
                bool targetLive = haveEvent && Safe<bool>(
                    delegate { return target != null && !target.deleted; }, false);

                notices.OnOptionButtonPressed(pick);
                o["pressed"] = pick;
                if (!haveEvent)
                {
                    // No readable event record on this build, so whether the press
                    // answered anything is unknown and must not be claimed either way.
                    o["answered"] = JValue.CreateNull();
                    o["note"] = "the controller's narrative event record could not be "
                        + "read, so what this press answered is unknown";
                }
                else if (!targetLive)
                {
                    o["answered"] = false;
                    o["note"] = "the narrative event "
                        + (eventName != null ? eventName : "behind this box")
                        + " was NOT answered: its selectedTarget is "
                        + (target == null ? "null" : "deleted")
                        + ", so the engine logged the target away and applied no "
                        + "option. The box is gone and its prompt is still queued; "
                        + "prompts (or advance) drops it on the same condition";
                }
                else
                {
                    // `event` was written above, off the record read before the
                    // press. Nothing here writes it again.
                    o["answered"] = true;
                }
            }
            return o;
        }

        // One option's outcome, read off NarrativeEventOption rather than out of the
        // popup text, and reported only under detail=true.
        //
        // Two reasons for the struct over the string. The text stops dead at
        // "hideOptionInfoFromFaction" when the option carries a
        // TIFactionCondition_eIdeology the acting faction fails (OptionDetail
        // IL_0050-IL_00ae), so what a test would read is exactly what the game
        // withheld; and what it does print is prose, which nothing can assert against.
        // Crossing that gate is the point of the flag, and `infoHiddenFromFaction`
        // says when it was crossed.
        //
        // Every read is guarded. This is a reporting path over template data a mod may
        // have written, and a null list or a missing template name must not take the
        // whole reply down.
        static JToken NarrativeOptionDetail(TINarrativeEventTemplate template, int idx,
            TIFactionState faction, TIGameState target, TIGameState secondary,
            Dictionary<TIGameState, TIGameState> allTargets)
        {
            List<NarrativeEventOption> list = Safe<List<NarrativeEventOption>>(
                delegate { return template.eventOptions; }, null);
            // Past the offered count is a button the popup does not build, and
            // optionButtonDetail indexes the list with no bounds test of its own.
            if (list == null || idx < 0 || idx >= list.Count
                || idx >= NarrativeOptionCount(template))
                return JValue.CreateNull();
            NarrativeEventOption option = list[idx];

            var o = new JObject();
            Put(o, "text", delegate
            {
                string text = template.optionButtonDetail(faction, target, secondary,
                    idx, allTargets);
                return text != null ? (JToken)new JValue(text) : JValue.CreateNull();
            });
            // The engine's own reading, index 0 included. FillOutOptionButtons
            // branches on the loop index before it tests anything: for option 0
            // it pushes a hardcoded true in place of the ValidOption call, so the
            // first option is offered however its condition reads. Calling
            // ValidOption for it anyway would mark a live button invalid, which
            // is a reading of the event and not of the popup.
            o["valid"] = idx == 0 || Safe<bool>(delegate
                { return option.ValidOption(faction, target, secondary); }, false);
            Put(o, "baseAIPreference", delegate { return Num(option.baseAIPreference); });
            // The gate itself, by the type name rather than a typeref: naming
            // TIFactionCondition_eIdeology here would bind this file to a class that
            // exists for one test, and the name is what the engine branches on.
            o["infoHiddenFromFaction"] = Safe<bool>(delegate
            {
                TICondition condition = option.actingFactionCondition;
                return condition != null
                    && condition.GetType().Name == "TIFactionCondition_eIdeology"
                    && !condition.PassesCondition(faction);
            }, false);
            Put(o, "factionCondition",
                delegate { return ConditionText(option.actingFactionCondition); });
            Put(o, "targetCondition",
                delegate { return ConditionText(option.targetCondition); });

            var outcomes = new JArray();
            List<NarrativeEventOutcome> all = Safe<List<NarrativeEventOutcome>>(
                delegate { return option.outcomes; }, null);
            for (int i = 0; all != null && i < all.Count; i++)
                outcomes.Add(NarrativeOutcomeToken(option, all[i], i, faction, target,
                                                   secondary));
            o["outcomes"] = outcomes;
            return o;
        }

        // One outcome of one option. `chance` is the engine's own weighting over the
        // option's whole outcome list, which is why the index into that list is passed
        // rather than the outcome alone: outcomeChance takes the index.
        static JToken NarrativeOutcomeToken(NarrativeEventOption option,
            NarrativeEventOutcome outcome, int idx, TIFactionState faction,
            TIGameState target, TIGameState secondary)
        {
            var o = new JObject();
            Put(o, "chance", delegate
                { return Num(option.outcomeChance(idx, faction, target, secondary)); });
            Put(o, "weight", delegate { return Num(outcome.weight); });
            o["aiFavored"] = Safe<bool>(delegate { return outcome.AIFavored; }, false);
            o["effects"] = ToArray(Safe<List<string>>(
                delegate { return outcome.effectTemplateNames; }, null));
            o["delayedEffects"] = ToArray(Safe<List<string>>(
                delegate { return outcome.delayedEffectTemplateNames; }, null));
            o["addsEvents"] = ToArray(Safe<List<string>>(
                delegate { return outcome.addNarrativeEvents; }, null));
            o["removesEvents"] = ToArray(Safe<List<string>>(
                delegate { return outcome.removeNarrativeEvents; }, null));
            Put(o, "projectGranted", delegate
            {
                string name = outcome.projectGrantedTemplateName;
                return string.IsNullOrEmpty(name)
                    ? JValue.CreateNull() : (JToken)new JValue(name);
            });
            Put(o, "orgGranted", delegate
            {
                string name = outcome.orgGrantedTemplateName;
                return string.IsNullOrEmpty(name)
                    ? JValue.CreateNull() : (JToken)new JValue(name);
            });
            // The cost the outcome would charge against this event's own target, which
            // is what the popup prices: GetCosts reads the target for its multipliers.
            // Guarded like everything else here, so an event whose target the multiplier
            // path will not take reports a null cost rather than losing the reply.
            Put(o, "costs", delegate { return CostToken(outcome.GetCosts(target)); });
            return o;
        }

        static JToken ConditionText(TICondition condition)
        {
            if (condition == null) return JValue.CreateNull();
            return Safe<JToken>(delegate
            {
                System.Text.StringBuilder text = condition.GetDescription();
                return text != null
                    ? (JToken)new JValue(text.ToString()) : JValue.CreateNull();
            }, JValue.CreateNull());
        }

        // The label of an alert-box option button, a hidden one included.
        // The search includes inactive children, so a button the engine has
        // SetActive(false) still reports its label -- and a row for an option
        // the popup is not drawing is exactly what detail=true exists to
        // report. The label is null only past the end of the array.
        static JToken OptionLabel(UnityEngine.UI.Button button)
        {
            if (button == null) return JValue.CreateNull();
            string text = Safe<string>(delegate
            {
                var label = button.GetComponentInChildren<TMPro.TMP_Text>(true);
                return label != null ? label.text : null;
            }, null);
            return text != null ? (JToken)new JValue(text) : JValue.CreateNull();
        }

        // Whether the popup is drawing this option at all. FillOutOptionButtons
        // walks all four buttons and calls SetActive(i < numOptions) on each, so
        // the active flag is the engine's own record of the offered count and
        // says nothing about whether the button may be pressed yet.
        static bool ButtonOffered(UnityEngine.UI.Button button)
        {
            if (button == null) return false;
            return Safe<bool>(
                delegate { return button.gameObject.activeInHierarchy; }, false);
        }

        // The button's own interactable flag, null when there is no button.
        // Separate from `hidden` because the two move independently: a drawn
        // button is non-interactable for notificationReceiveInputDelay seconds
        // after the box opens, and a drawn button whose option fails its
        // condition stays non-interactable for good.
        static JToken ButtonInteractable(UnityEngine.UI.Button button)
        {
            if (button == null) return JValue.CreateNull();
            return Safe<JToken>(
                delegate { return (JToken)new JValue(button.interactable); },
                JValue.CreateNull());
        }

        // The modal screens the vanilla Autopilot clears on its way through a frame, each
        // taking the same decline-or-close branch it takes. The precombat screen is left
        // out: combat.autoresolve owns it and drives it a step per frame.
        static void DismissScreens(JArray log, bool answerNarrative, string narrativeMode,
                                   JArray dismissed, JArray notes)
        {
            NotificationScreenController notices = Find<NotificationScreenController>();
            if (notices != null)
            {
                Alerts(notices, log, answerNarrative, narrativeMode, dismissed, notes);
                Screen(log, "policy.cancel", notices.masterPolicyPanelObject, delegate
                {
                    notices.PolicySelected(new CancelOption());
                    notices.OnConfirmPolicy();
                });
                Screen(log, "response.decline", notices.responsePanelObject, notices.OnResponseDecline);
                Screen(log, "warCall.decline", notices.callAllyResponseObject, notices.DeclineWarButton);
                Screen(log, "diplomacy.continue", Owner(notices.factionDiplomacyGreetingContinueButton),
                    notices.OnDiplomacyGreetingContinueButton);
                Screen(log, "diplomacy.close", Owner(notices.diplomacyController), notices.DiplomacyClose);
                Screen(log, "armies.goHome", notices.removeArmiesPromptObject,
                    notices.removeArmies_GoHomePressed);
            }

            ResearchScreenController research = Find<ResearchScreenController>();
            if (research != null)
                Screen(log, "research.close",
                    delegate { return research.Canvas != null && research.Canvas.enabled; },
                    delegate { research.CloseInfoScreen(false); });

            OperationCanvasController operations =
                Safe<OperationCanvasController>(delegate { return OperationCanvasController.Singleton; }, null);
            if (operations != null)
                Screen(log, "trajectory.cancel",
                    delegate { return Clickable(operations.changeTrajectoryCancelButton); },
                    operations.OnCancelTrajectoryChange);
        }

        // One dismissable screen: when ready reports it up, press is run and the label
        // logged. A throw in either leaves the rest of the pass untouched.
        static void Screen(JArray log, string label, Func<bool> ready, Action press)
        {
            try
            {
                if (!ready()) return;
                press();
                log.Add(new JValue(label));
            }
            catch (Exception) { }
        }

        static void Screen(JArray log, string label, UnityEngine.GameObject panel, Action press)
        {
            Screen(log, label,
                delegate { return panel != null && panel.activeInHierarchy; }, press);
        }

        static UnityEngine.GameObject Owner(UnityEngine.Component component)
        {
            return Safe<UnityEngine.GameObject>(
                delegate { return component != null ? component.gameObject : null; }, null);
        }

        // The alert box restacks: each press can reveal the next queued notification, so the
        // press order the Autopilot uses is repeated until nothing is left to click.
        static void Alerts(NotificationScreenController notices, JArray log,
                           bool answerNarrative, string narrativeMode,
                           JArray dismissed, JArray notes)
        {
            try
            {
                if (notices.singleAlertBox == null || !notices.singleAlertBox.activeInHierarchy) return;
                int clicks = 0;
                while (clicks < MaxAlertClicks)
                {
                    if (Clickable(notices.okayButton)) notices.OkayButtonPressed();
                    else if (Clickable(notices.closeButton)) notices.CloseButtonPressed();
                    else if (Clickable(notices.gotoButton)) notices.GotoButtonPressed();
                    else break;
                    clicks++;
                }
                if (clicks > 0) log.Add(new JValue("alert.close x" + clicks));

                // An option button on this controller means one thing. Every path
                // through OnOptionButtonPressed past its guard reads
                // currentNarrativeEvent and runs a SelectNarrativeEventOption with the
                // index pressed, so "take the first live option" was never a neutral
                // click: it answered the player's story event with option 0 and said so
                // only as "alert.option 0" in the screens log.
                UnityEngine.UI.Button[] options = notices.optionButtons;
                if (options == null) return;
                int firstLive = -1;
                for (int i = 0; i < options.Length; i++)
                {
                    if (!Clickable(options[i])) continue;
                    firstLive = i;
                    break;
                }
                if (firstLive < 0) return;
                // A live button is not yet a press that lands: see NarrativePressLands.
                // Returning leaves the prompt in `skipped` with waitingForBox true, and
                // the next poll takes it once the coroutine has armed the controller.
                //
                // Noted on the way out, because that skipped entry says the box has not
                // come up yet and here the box is standing open. One poll of this is
                // the coroutine's delay and is expected; a run that holds for its whole
                // patience and reports a box that never came up is this line's case,
                // and without it the two are indistinguishable from the digest.
                if (!NarrativePressLands(notices))
                {
                    NarrativeNote(notes, "alert.narrative box is open with live option "
                        + "buttons but the controller is not taking narrative input "
                        + "yet, so no press would land; left for the next pass");
                    return;
                }

                CurrentNarrativeEventData current;
                bool haveEvent = CurrentNarrativeEvent(notices, out current);
                TINarrativeEventTemplate template = haveEvent
                    ? Safe<TINarrativeEventTemplate>(
                        delegate { return current.eventTemplate; }, null)
                    : null;

                // An event with one option is not a decision. Whoever the mode leaves
                // the choice to has exactly one thing to press, so every mode presses
                // it here -- narrative=false included -- rather than blocking the clock
                // on a box with nothing to weigh. The record is the same as any other
                // answer, plus `single`, so a caller driving the box by hand can see
                // which events were taken out of its hands and what they applied.
                //
                // The count is NarrativeOptionCount, the engine's own numOptions
                // clamped to the list, not the number of live buttons: the popup builds
                // its buttons from that field, and 179 of the 260 shipped events carry
                // more entries in eventOptions than they offer.
                bool single = template != null && NarrativeOptionCount(template) == 1;

                // The switch turns on the live option button, not on the event record
                // behind it. A press IS the answer whether or not this pass can read
                // what it is answering, so gating the refusal on a readable template
                // would let narrative=false press option 0 on exactly the build where
                // the field moved -- the case the caller most needs the switch for. A
                // template that will not read is therefore never single either.
                if (!answerNarrative && !single)
                {
                    NarrativeNote(notes, "alert.narrative left for alert.choose ("
                        + (template != null
                            ? Safe<string>(delegate { return template.dataName; }, "?")
                            : (haveEvent ? "no event behind the box"
                                         : "narrative event data unreadable")) + ", "
                        + NarrativeModeNote(narrativeMode) + ")");
                    return;
                }

                if (template == null)
                {
                    // No event behind the box, or this build moved the field the engine
                    // reads. Falls back to what this pass has always done rather than
                    // stalling, and says which of the two it was.
                    notices.OnOptionButtonPressed(firstLive);
                    NarrativeNote(notes, "alert.option " + firstLive + " pressed with no readable "
                        + "narrative event (" + (haveEvent ? "no event behind the box"
                            : "narrative event data unreadable") + "), so what it "
                        + "answered is unknown");
                    return;
                }

                TIFactionState faction = Safe<TIFactionState>(
                    delegate { return notices.activePlayer; }, null);
                TIGameState target = current.selectedTarget;
                TIGameState secondary = current.secondaryTarget;
                Dictionary<TIGameState, TIGameState> allTargets = AllTargets(current);
                NarrativeChoice choice = ChooseNarrativeOption(
                    template, faction, target, secondary);
                // The engine's guard ignores a press on a button that is not live, so a
                // pick the popup does not offer would silently leave the box up.
                int pick = choice != null && choice.option >= 0
                    && choice.option < options.Length && Clickable(options[choice.option])
                    ? choice.option : firstLive;
                if (choice == null || pick != choice.option)
                {
                    if (choice == null) choice = new NarrativeChoice();
                    choice.option = pick;
                    choice.pickedBy = "first live option button";
                }

                // The engine applies nothing when the event's own target is gone.
                // OnOptionButtonPressed tests currentNarrativeEvent.selectedTarget for
                // null at IL_0082 and for deleted at IL_0094, and both send it to
                // IL_00e1, which logs "Target for NarrativeEvent <name> deleted or
                // didn't exist before narrative event was resolved" and skips the
                // StartAction at IL_00da that would build the SelectNarrativeEventOption
                // -- while the teardown from IL_0100 runs either way. So the press still
                // consumes the box and the prompt is left standing, and reporting that
                // as an answered story event is the one thing this pass must not do.
                bool targetLive = Safe<bool>(
                    delegate { return target != null && !target.deleted; }, false);
                if (!targetLive)
                {
                    notices.OnOptionButtonPressed(pick);
                    NarrativeNote(notes, "alert.narrative "
                        + Safe<string>(delegate { return template.dataName; }, "?")
                        + " was NOT answered: its selectedTarget is "
                        + (target == null ? "null" : "deleted")
                        + ", so the engine logs the target away and applies no option. "
                        + "The box was torn down by the press; the prompt behind it is "
                        + "a dead end and the prompt pass of this same call drops it");
                    return;
                }

                // Built AND handed over BEFORE the press, both of them, because the
                // catch below swallows everything and the press is not atomic.
                // OnOptionButtonPressed applies the option at IL_00da and then runs a
                // long tail -- tooltip teardown, the hotkey flag, listener removal,
                // CleanUp, gameTime.Play -- through IL_0147, and a throw anywhere in
                // that tail leaves the choice applied with its costs and grants while
                // this method unwinds. Adding afterwards loses the record of an answer
                // the save already carries.
                //
                // Adding first has its own error: a throw before IL_00da reports an
                // answer that was never applied. That is the better error to hold.
                // A lost record is undetectable -- the digest says the event never
                // fired and nothing contradicts it. An over-report contradicts itself
                // immediately: the prompt is still queued and the box is still up, so
                // the next poll answers the event for real and reports it again, and
                // the caller sees a duplicate rather than a hole. The file's own rule
                // is that an unattended run answering story events silently is the
                // thing to prevent.
                //
                // The engine's other way of doing nothing is not a throw: the guard at
                // IL_0000-IL_0037 returns having applied nothing. All four of its tests
                // are checked above, by Clickable on the pick and by NarrativePressLands
                // on the rest, so that path cannot reach here.
                //
                // Not into `log`. That array is the screens pass's closed-notice list
                // and the server counts it as screensClosed; an answered story event is
                // neither a closed screen nor an anomaly, and it already travels whole
                // in `dismissed`.
                if (dismissed != null)
                {
                    var entry = new JObject();
                    entry["name"] = NarrativePrompt;
                    entry["via"] = "NotificationScreenController.OnOptionButtonPressed "
                        + Safe<string>(delegate { return template.dataName; }, "?")
                        + " option " + pick;
                    // Present on every answered event, true only for the one-option
                    // case, so a caller reading a digest under narrative=false can
                    // tell the events this pass took from the ones it left.
                    entry["single"] = single;
                    entry["mode"] = narrativeMode;
                    ReportNarrativeChoice(entry, template, choice,
                        faction, target, secondary, allTargets);
                    dismissed.Add(entry);
                }

                notices.OnOptionButtonPressed(pick);
            }
            catch (Exception) { }
        }

        // The screens pass's exceptional lines about a narrative event, kept apart from
        // the closed-notice log so a caller does not have to recognize them by their
        // wording. An ordinary answer is not one of these: it rides in `dismissed`.
        static void NarrativeNote(JArray notes, string text)
        {
            if (notes != null) notes.Add(new JValue(text));
        }

        // The controller's own record of the event the box is showing. The field is
        // private and PushNextAlert is its only writer (IL_04a2), which is also what
        // activates the option buttons, so reading it is how this pass learns that a
        // press would answer a story event rather than close a notice.
        static readonly FieldInfo currentNarrativeEventField = FindNarrativeEventField();

        static FieldInfo FindNarrativeEventField()
        {
            try
            {
                return typeof(NotificationScreenController).GetField(
                    "currentNarrativeEvent",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception) { return null; }
        }

        // The two halves of OnOptionButtonPressed's entry guard that a clickable button
        // does not cover. The guard is four tests: TIInputManager
        // .receivingInputForNarrativeHotkeys at IL_0000, the button's own activeSelf at
        // IL_0014 and interactable at IL_0023, and singleAlertBoxBody.activeSelf at
        // IL_0030. Any of them false returns at IL_0037 having done nothing at all.
        //
        // Clickable covers the middle two. The first one has a window of its own: the
        // buttons go live with the box, while the flag is set by the
        // EnableNarrativeButtonHotkeysWithDelay coroutine after a WaitForSeconds. A
        // press inside that window is discarded silently, and a caller that reads the
        // button state alone would record an answer the engine never applied and press
        // the same event again on the next pass.
        static bool NarrativePressLands(NotificationScreenController notices)
        {
            return Safe<bool>(delegate
            {
                return TIInputManager.receivingInputForNarrativeHotkeys
                    && notices.singleAlertBoxBody != null
                    && notices.singleAlertBoxBody.activeSelf;
            }, false);
        }

        // Whether the alert box that alone may answer a narrative prompt is up AND
        // would take a press right now: the box active, a live option button on it,
        // and the controller past its input-arming delay. The same three readings
        // alert.choose reports as `open`, `options` and `pressLands`, in one answer,
        // because a pass that leaves a narrative prompt standing has to say whether
        // anything on screen can answer it or the caller is waiting for a box.
        static bool NarrativeBoxAnswerable(NotificationScreenController notices)
        {
            return Safe<bool>(delegate
            {
                if (notices == null) return false;
                if (notices.singleAlertBox == null
                    || !notices.singleAlertBox.activeInHierarchy) return false;
                UnityEngine.UI.Button[] options = notices.optionButtons;
                if (options == null) return false;
                for (int i = 0; i < options.Length; i++)
                {
                    if (Clickable(options[i]))
                        return NarrativePressLands(notices);
                }
                return false;
            }, false);
        }

        static bool CurrentNarrativeEvent(NotificationScreenController notices,
                                          out CurrentNarrativeEventData current)
        {
            current = new CurrentNarrativeEventData();
            if (currentNarrativeEventField == null) return false;
            try
            {
                object boxed = currentNarrativeEventField.GetValue(notices);
                if (!(boxed is CurrentNarrativeEventData)) return false;
                current = (CurrentNarrativeEventData)boxed;
                return true;
            }
            catch (Exception) { return false; }
        }

        // The same vocabulary the server's own flag reader accepts, because `raw` reaches
        // these verbs without passing through it: a client that writes "1" or "yes" to a
        // switch defaulting to true would otherwise turn it off, which is the one
        // direction a switch must never fail in.
        static bool Flag(JObject args, string key)
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return false;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float)
                return Safe<bool>(delegate { return (double)t != 0.0; }, false);
            string s = t.ToString().Trim();
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "1", StringComparison.Ordinal)
                || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
