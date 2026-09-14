using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // ui.screenshot, ui.tooltip, ui.describe.
    //
    // Three ways to read the screen without the screen. The capture runs inside the
    // process, so it sees the game's own back buffer rather than whatever the
    // desktop has on top of the window; the tooltip verb calls the same static
    // builders the hover text is made of, so the string is the player's, not a
    // reconstruction of it.
    public static partial class Verbs
    {
        #region ui.screenshot

        // Under persistentDataPath, which is the one directory the game process
        // and the host agree on: the server maps it as the folder Player.log
        // sits in.
        const string ShotFolder = "mcp-screenshots";

        // Captures kept on disk. The server deletes each file after reading it,
        // so this only bounds what a raw caller leaves behind.
        const int MaxShotsKept = 20;

        // Unity accepts 1-4, but 4 is 16x the pixels: the PNG runs to tens of
        // megabytes, takes long enough that a client's wait budget has to grow
        // with it, and is far past what fits in a tool response as an image. Two
        // is the useful end of the range (small UI text becomes legible), so the
        // verb stops there and the wait stays predictable.
        const int MaxSupersize = 2;

        static string lastShotName;
        static string lastShotPath;
        static string lastShotRelative;
        static int lastShotSupersize;
        static int lastShotFrame = -1;
        static int shotCounter;

        static JToken UiScreenshot(JObject args)
        {
            if (Flag(args, "status")) return ShotStatus(false);

            // Read through the token rather than OptionalInt's -1-means-absent:
            // a supersize is a small positive number, so a negative one is a
            // mistake that must not quietly become the default.
            int supersize = 1;
            JToken sizeArg = args != null ? args["supersize"] : null;
            if (sizeArg != null && sizeArg.Type != JTokenType.Null)
            {
                supersize = Int(args, "supersize");
                if (supersize < 1 || supersize > MaxSupersize)
                    throw new VerbError("arg 'supersize' must be 1.." + MaxSupersize);
            }

            string dir = ShotDir();
            string name = ShotName(args);
            string path = Path.Combine(dir, name);

            try { Directory.CreateDirectory(dir); }
            catch (Exception e)
            {
                throw new VerbError("could not create " + dir + ": " + Note(e));
            }
            // A stale file at this path would make `exists` mean "some capture
            // once landed here" instead of "this one did", and the file is the
            // only completion signal there is.
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e)
            {
                throw new VerbError("a file is already at " + path
                    + " and could not be removed: " + Note(e));
            }
            Prune(dir);

            // Unity finishes the capture at the end of the current frame and
            // writes the PNG from native code, so nothing here can wait for it:
            // the verb arms the capture and the file's appearance is the
            // completion signal. CaptureScreenshot(path) is CaptureScreenshot with
            // superSize 1, so the two calls differ only in resolution.
            if (supersize > 1)
                UnityEngine.ScreenCapture.CaptureScreenshot(path, supersize);
            else
                UnityEngine.ScreenCapture.CaptureScreenshot(path);

            lastShotName = name;
            lastShotPath = path;
            lastShotRelative = ShotFolder + "/" + name;
            lastShotSupersize = supersize;
            lastShotFrame = Safe<int>(
                delegate { return UnityEngine.Time.frameCount; }, -1);
            return ShotStatus(true);
        }

        static JObject ShotStatus(bool armedNow)
        {
            var o = new JObject();
            o["armed"] = lastShotPath != null;
            o["armedNow"] = armedNow;
            if (lastShotPath == null)
            {
                o["name"] = JValue.CreateNull();
                o["path"] = JValue.CreateNull();
                o["relativePath"] = JValue.CreateNull();
                o["dir"] = PersistentRoot();
                o["folder"] = ShotDir();
                o["exists"] = false;
                o["bytes"] = -1;
                o["frames"] = -1;
                o["supersize"] = 0;
                return o;
            }
            o["name"] = lastShotName;
            // The path as the process sees it (a Windows path under Proton), for
            // a human. `relativePath` is relative to `dir`, which is
            // persistentDataPath: a client outside the process joins the two,
            // using its own view of that directory (the one Player.log sits in).
            o["path"] = lastShotPath;
            o["relativePath"] = lastShotRelative;
            o["dir"] = PersistentRoot();
            o["folder"] = ShotDir();
            o["supersize"] = lastShotSupersize;
            long bytes = Safe<long>(delegate
            {
                var info = new FileInfo(lastShotPath);
                return info.Exists ? info.Length : -1L;
            }, -1L);
            // No "pending" flag: a file that exists may still be growing, and any
            // single read of its size -- zero or not -- can land mid-write, so a
            // flag derived from one read would report done before it is. `bytes`
            // is the raw signal and the caller decides: the capture is finished
            // when two reads a moment apart return the same positive size.
            o["exists"] = bytes >= 0;
            o["bytes"] = bytes;
            o["frames"] = lastShotFrame < 0 ? -1
                : Safe<int>(delegate
                    { return UnityEngine.Time.frameCount - lastShotFrame; }, -1);
            return o;
        }

        // persistentDataPath itself, which is what relativePath is relative to.
        // A client outside the game process cannot see the process's own path
        // (it is a Windows path inside the Wine prefix on Linux), but it can
        // find this directory: it is the one Player.log sits in.
        static string PersistentRoot()
        {
            string root = Safe<string>(
                delegate { return UnityEngine.Application.persistentDataPath; }, null);
            if (string.IsNullOrEmpty(root))
                throw new VerbError("no persistentDataPath");
            return root;
        }

        static string ShotDir()
        {
            return Path.Combine(PersistentRoot(), ShotFolder);
        }

        // A plain file name in the shot folder, PNG whatever was asked for:
        // ScreenCapture writes PNG regardless of the extension, and a .jpg that
        // holds PNG bytes is a trap for whatever reads it.
        static string ShotName(JObject args)
        {
            string name = Str(args, "name");
            if (string.IsNullOrEmpty(name))
            {
                shotCounter++;
                return string.Format(CultureInfo.InvariantCulture,
                    "mcp-{0:yyyyMMdd-HHmmss}-{1:D3}.png", DateTime.Now, shotCounter);
            }
            name = name.Trim();
            if (name.Length == 0) throw new VerbError("arg 'name' is empty");
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0
                || name.IndexOf(':') >= 0 || name.IndexOf("..") >= 0)
                throw new VerbError("arg 'name' must be a plain file name");
            int dot = name.IndexOf('.');
            string stem = dot >= 0 ? name.Substring(0, dot) : name;
            for (int i = 0; i < ReservedNames.Length; i++)
            {
                if (string.Equals(stem, ReservedNames[i], StringComparison.OrdinalIgnoreCase))
                    throw new VerbError("arg 'name' is a reserved device name");
            }
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                name = stem + ".png";
            return name;
        }

        // Best effort, and silent: a capture that lands is worth more than a
        // tidy folder, so nothing here may fail the verb.
        static void Prune(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "*.png");
                if (files.Length <= MaxShotsKept) return;
                Array.Sort(files, delegate(string a, string b)
                {
                    return File.GetLastWriteTimeUtc(a).CompareTo(
                        File.GetLastWriteTimeUtc(b));
                });
                for (int i = 0; i < files.Length - MaxShotsKept; i++)
                {
                    try { File.Delete(files[i]); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
        }

        #endregion

        #region ui.options

        // ui.options: the options screen's own state, and the engine's own toggle.
        //
        // Nothing else reaches that screen headlessly. The escape key only ever
        // CLOSES it -- GeneralControlsController.CheckKeys tests
        // canvasManager.OptionsScreen.Visible() before it calls MainMenu()
        // (IL_05e1-IL_0604), so a keypress on a screen that is down does nothing --
        // and no console command opens it. That left one question unanswerable:
        // game.main_menu calls Hide() on this controller and sets its Behaviour
        // enabled false when its canvas was up, so "did that leave the options
        // screen usable" had no verb behind it.
        //
        // `status` is the answer and presses nothing. The engine rebuilds this
        // controller for each campaign -- CanvasManager's Initialize instantiates
        // the prefab, calls ICanvas.Initialize on it, and
        // OptionsScreenController.Initialize sets Behaviour.enabled true at its
        // IL_0006-IL_0008 -- so a controller that comes back from a campaign cycle
        // disabled, or that is no longer the instance CanvasManager holds, is a
        // defect, and `usable` is the field that says so.
        //
        // `open` / `close` / `toggle` call GeneralControlsController.MainMenu(),
        // which is exactly what the escape key and the menu button call. It is a
        // toggle, so `open` presses only while the screen is down and `close` only
        // while it is up; both report `pressed` either way.
        //
        // THE DRIVE MOVES THE CLOCK, and that is the engine's behaviour rather
        // than this verb's. MainMenu()'s show arm calls OptionsScreen.Show(),
        // whose IL_0006-IL_0020 banks a running clock and pauses it, then hides
        // the codex, sets TIInputManager.acceptingInput false and calls
        // CoroutineDummy.PauseAll(); the hide arm calls Hide(), which plays the
        // clock again if it banked the pause (IL_0011-IL_0026), sets
        // acceptingInput true and calls UnpauseAll(). `paused` is reported on both
        // sides of the press so a caller can put the clock back where it was.
        static readonly string[] OptionsActions =
            new string[] { "status", "open", "close", "toggle" };

        // CanvasControllerBase.canvasManager is protected, so the instance the
        // toggle actually reads is only reachable by reflection. MainMenu()
        // dereferences it and its OptionsScreen with no null test at IL_0001-IL_000b,
        // which is why the drive checks both before it presses anything.
        static readonly System.Reflection.PropertyInfo canvasManagerProperty =
            Safe<System.Reflection.PropertyInfo>(delegate
            {
                return typeof(CanvasControllerBase).GetProperty("canvasManager",
                    System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance);
            }, null);

        static JToken UiOptions(JObject args)
        {
            string action = Str(args, "action");
            if (string.IsNullOrEmpty(action)) action = "status";
            action = action.ToLowerInvariant();
            if (Array.IndexOf(OptionsActions, action) < 0)
                throw new VerbError("action must be one of "
                    + string.Join(", ", OptionsActions));

            OptionsScreenController options = Find<OptionsScreenController>();
            var o = new JObject();
            o["action"] = action;
            o["found"] = options != null;
            o["pressed"] = false;
            o["previous"] = JValue.CreateNull();
            if (action == "status")
            {
                o["options"] = OptionsReport(options);
                return o;
            }

            if (options == null)
                throw new VerbError("no OptionsScreenController in the scene, so "
                    + "there is no options screen to drive; nothing was pressed. "
                    + "The controller is instantiated per campaign by CanvasManager, "
                    + "so this is the ordinary answer at the main menu");

            GeneralControlsController controls = Find<GeneralControlsController>();
            if (controls == null)
                throw new VerbError("no GeneralControlsController, which owns the "
                    + "toggle (its MainMenu() is what the escape key and the menu "
                    + "button call); nothing was pressed");

            string blocker = ToggleBlocker(controls);
            if (blocker != null)
                throw new VerbError(blocker + "; MainMenu() dereferences it with no "
                    + "null test, so nothing was pressed");

            // Read through the same call MainMenu() branches on. A canvas that
            // cannot be read is a canvas MainMenu() would have thrown on.
            JToken visibleToken = Safe<JToken>(
                delegate { return (JToken)options.Visible(); }, JValue.CreateNull());
            if (visibleToken.Type == JTokenType.Null)
                throw new VerbError("the options screen's Visible() could not be "
                    + "read, which means its Canvas is missing; MainMenu() calls the "
                    + "same method first and would have thrown. Nothing was pressed");
            bool visible = (bool)visibleToken;

            o["previous"] = OptionsReport(options);
            bool press = action == "toggle"
                || (action == "open" && !visible)
                || (action == "close" && visible);
            if (press)
            {
                try { controls.MainMenu(); }
                catch (Exception e)
                {
                    throw new VerbError("GeneralControlsController.MainMenu() threw "
                        + "part way through: " + Note(e) + ". The screen, the clock "
                        + "and TIInputManager.acceptingInput may each be half moved; "
                        + "read ui.options action=status to see where they landed");
                }
                o["pressed"] = true;
            }
            else
            {
                o["note"] = "the options screen is already "
                    + (visible ? "up" : "down")
                    + " and MainMenu() is a toggle, so pressing it would have "
                    + (visible ? "closed" : "opened") + " it. Nothing was pressed";
            }
            o["options"] = OptionsReport(options);
            return o;
        }

        // Null when MainMenu() may be called, otherwise the reason it may not.
        static string ToggleBlocker(GeneralControlsController controls)
        {
            if (canvasManagerProperty == null)
                return "CanvasControllerBase has no canvasManager property, so the "
                    + "one MainMenu() reads cannot be checked";
            object manager = Safe<object>(
                delegate { return canvasManagerProperty.GetValue(controls, null); },
                null);
            if (manager == null)
                return "the GeneralControlsController has no canvasManager";
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager typed =
                manager as PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager;
            if (typed == null)
                return "the GeneralControlsController's canvasManager is a "
                    + manager.GetType().Name + ", not a CanvasManager";
            if (Safe<bool>(delegate { return typed.OptionsScreen == null; }, true))
                return "the CanvasManager the toggle reads holds no OptionsScreen";
            return null;
        }

        // Everything about the screen that a gate can assert, read with nothing
        // opened. Null for a controller that is not there at all, which is what
        // the main menu answers.
        static JToken OptionsReport(OptionsScreenController options)
        {
            if (options == null) return JValue.CreateNull();
            var o = new JObject();
            // Survives a campaign cycle only if the controller does, so a caller
            // comparing two calls across game.main_menu can tell a rebuild from a
            // survivor without guessing from the other fields.
            o["instanceId"] = Safe<JToken>(
                delegate { return (JToken)options.GetInstanceID(); }, JValue.CreateNull());
            o["componentEnabled"] = Safe<JToken>(
                delegate { return (JToken)options.enabled; }, JValue.CreateNull());
            o["gameObjectActive"] = Safe<JToken>(
                delegate { return (JToken)options.gameObject.activeInHierarchy; },
                JValue.CreateNull());
            UnityEngine.Canvas canvas = Safe<UnityEngine.Canvas>(
                delegate { return options.Canvas; }, null);
            o["canvas"] = canvas != null;
            // CanvasControllerBase.Visible() is exactly Canvas.enabled and reads
            // the canvas with no null test, so it goes through the guard.
            o["visible"] = Safe<JToken>(
                delegate { return (JToken)options.Visible(); }, JValue.CreateNull());
            // The instance CanvasManager holds is the one every engine path uses.
            // A live controller that is not it is unreachable from the game's own
            // input, however healthy it looks.
            OptionsScreenController registered = RegisteredOptionsScreen();
            o["registered"] = registered != null && ReferenceEquals(registered, options);
            o["acceptingInput"] = Safe<JToken>(
                delegate { return (JToken)TIInputManager.acceptingInput; },
                JValue.CreateNull());
            o["savingBlocked"] = Safe<JToken>(
                delegate { return (JToken)SaveMenuController.SavingIsBlocked(); },
                JValue.CreateNull());
            o["paused"] = Safe<JToken>(delegate
            {
                PavonisInteractive.TerraInvicta.Systems.GameTime.GameTimeManager clock =
                    PavonisInteractive.TerraInvicta.Systems.GameTime.GameTimeManager.Singleton;
                return clock != null ? (JToken)clock.Paused : JValue.CreateNull();
            }, JValue.CreateNull());

            var buttons = new JObject();
            Button(buttons, "exitToMainMenu",
                Safe<UnityEngine.UI.Button>(
                    delegate { return options.exitToMainMenuButton; }, null));
            Button(buttons, "loadGame",
                Safe<UnityEngine.UI.Button>(
                    delegate { return options.loadGameButton; }, null));
            Button(buttons, "saveGame",
                Safe<UnityEngine.UI.Button>(
                    delegate { return options.saveGameButton; }, null));
            Button(buttons, "mainLoadGame",
                Safe<UnityEngine.UI.Button>(
                    delegate { return options.mainLoadGameButton; }, null));
            Button(buttons, "mainSaveGame",
                Safe<UnityEngine.UI.Button>(
                    delegate { return options.mainSaveGameButton; }, null));
            o["buttons"] = buttons;

            // The one-field verdict, and deliberately not a function of `visible`:
            // a closed options screen is the normal state of a healthy campaign.
            // These four are exactly what a teardown can leave wrong -- the
            // controller exists, it is the one CanvasManager hands the engine, its
            // Behaviour is enabled, and it still has a Canvas to show.
            o["usable"] = (bool)o["registered"]
                && o["componentEnabled"].Type == JTokenType.Boolean
                && (bool)o["componentEnabled"]
                && canvas != null;
            return o;
        }

        static void Button(JObject o, string key, UnityEngine.UI.Button button)
        {
            if (button == null) { o[key] = JValue.CreateNull(); return; }
            var b = new JObject();
            b["active"] = Safe<JToken>(
                delegate { return (JToken)button.gameObject.activeInHierarchy; },
                JValue.CreateNull());
            b["interactable"] = Safe<JToken>(
                delegate { return (JToken)button.interactable; }, JValue.CreateNull());
            // The same test every other verb here presses a button behind.
            b["clickable"] = Clickable(button);
            o[key] = b;
        }

        static OptionsScreenController RegisteredOptionsScreen()
        {
            return Safe<OptionsScreenController>(delegate
            {
                PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack =
                    GameControl.canvasStack;
                return stack != null
                    ? stack.OptionsScreen as OptionsScreenController : null;
            }, null);
        }

        #endregion

        #region ui.tooltip

        // NationInfoController's tooltip builders are public statics taking one
        // nation and returning the built string, with no controller instance and
        // no active-player read in any of them, so they answer with the screen
        // never opened. Bound at compile time rather than by reflection: a game
        // update that renames one breaks the build instead of the verb.
        //
        // BuildRegionDataTooltip is deliberately absent -- it takes a region, a
        // faction and a viewing nation, which is a different verb's shape.
        static readonly Dictionary<string, Func<TINationState, string>> tooltips
            = BuildTooltipTable();

        static Dictionary<string, Func<TINationState, string>> BuildTooltipTable()
        {
            var t = new Dictionary<string, Func<TINationState, string>>(
                StringComparer.Ordinal);
            t["investment"] = delegate(TINationState n)
                { return NationInfoController.BuildInvestmentTooltip(n); };
            t["publicopinion"] = delegate(TINationState n)
                { return NationInfoController.BuildPublicOpinionTooltip(n); };
            t["specialrelationship"] = delegate(TINationState n)
                { return NationInfoController.BuildSpecialRelationshipTooltip(n); };
            t["nukes"] = delegate(TINationState n)
                { return NationInfoController.BuildNukesTooltip(n); };
            t["armies"] = delegate(TINationState n)
                { return NationInfoController.BuildnumArmiesTooltip(n); };
            t["stofighters"] = delegate(TINationState n)
                { return NationInfoController.BuildSTOFightersTooltip(n); };
            t["education"] = delegate(TINationState n)
                { return NationInfoController.BuildEducationTooltip(n); };
            t["cohesion"] = delegate(TINationState n)
                { return NationInfoController.BuildCohesionTooltip(n); };
            t["inequality"] = delegate(TINationState n)
                { return NationInfoController.BuildInequalityTooltip(n); };
            t["unrest"] = delegate(TINationState n)
                { return NationInfoController.BuildUnrestTooltip(n); };
            t["democracy"] = delegate(TINationState n)
                { return NationInfoController.BuildDemocracyTooltip(n); };
            t["population"] = delegate(TINationState n)
                { return NationInfoController.BuildPopulationTooltip(n); };
            t["miltech"] = delegate(TINationState n)
                { return NationInfoController.BuildMiltechTooltip(n); };
            t["percapitagdp"] = delegate(TINationState n)
                { return NationInfoController.BuildPerCapitaGDPTooltip(n); };
            t["gdp"] = delegate(TINationState n)
                { return NationInfoController.BuildGDPTooltip(n); };
            t["sustainability"] = delegate(TINationState n)
                { return NationInfoController.BuildSustainabilityTooltip(n); };
            t["spacefunding"] = delegate(TINationState n)
                { return NationInfoController.BuildSpaceFundingTooltip(n); };
            t["research"] = delegate(TINationState n)
                { return NationInfoController.BuildResearchTooltip(n); };
            t["boost"] = delegate(TINationState n)
                { return NationInfoController.BuildBoostTooltip(n); };
            t["missioncontrol"] = delegate(TINationState n)
                { return NationInfoController.BuildMissionControlTooltip(n); };
            t["naval"] = delegate(TINationState n)
                { return NationInfoController.BuildNavalTooltip(n); };
            // Its second argument defaults to true, which is the panel's own
            // call and the string a player reads.
            t["policies"] = delegate(TINationState n)
                { return NationInfoController.BuildPoliciesTooltip(n); };
            return t;
        }

        // Rich text the builders emit for the UI: TMP colour and sprite tags.
        static readonly Regex tagPattern = new Regex("<[^<>]*>", RegexOptions.None);

        static JToken UiTooltip(JObject args)
        {
            TINationState nation = ArgNation(args, "nation");
            string kind = Str(args, "kind");
            if (string.IsNullOrEmpty(kind))
                throw new VerbError("missing arg 'kind'; " + TooltipKinds());
            string key = TooltipKey(kind);
            Func<TINationState, string> build;
            if (!tooltips.TryGetValue(key, out build))
                throw new VerbError("unknown tooltip kind '" + kind + "'; "
                    + TooltipKinds());

            // The one builder with a gate of its own: it enumerates the nation's
            // set-policy options, which reach the executive control point through
            // a bare indexer and throw for a nation that holds none.
            if (string.Equals(key, "policies", StringComparison.Ordinal))
                RequireExecutiveControlPoint(nation);

            string text;
            try { text = build(nation); }
            catch (Exception e)
            {
                throw new VerbError("the " + key + " tooltip builder threw for "
                    + StateName(nation) + " (" + (int)nation.ID + "): " + Note(e));
            }
            if (text == null) text = "";

            var o = new JObject();
            o["nation"] = Describe(nation);
            o["kind"] = key;
            o["text"] = text;
            // The same string with the TMP markup removed, for an assertion that
            // should not care whether a number came out coloured.
            o["plain"] = Safe<JToken>(
                delegate { return (JToken)tagPattern.Replace(text, ""); },
                new JValue(text));
            o["length"] = text.Length;
            return o;
        }

        // Case, spaces, dashes, dots and underscores all fall away, so
        // "publicOpinion", "public_opinion" and "Public Opinion" are one kind.
        static string TooltipKey(string kind)
        {
            var sb = new System.Text.StringBuilder(kind.Length);
            for (int i = 0; i < kind.Length; i++)
            {
                char c = kind[i];
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        static string TooltipKinds()
        {
            var names = new List<string>(tooltips.Keys);
            names.Sort(StringComparer.Ordinal);
            return "kind is one of: " + string.Join(", ", names.ToArray());
        }

        #endregion

        #region ui.describe

        // The long-form panel text a player reads on a module or a project,
        // built by the engine and returned with no screen opened. Bound at
        // compile time the way the tooltip table is: a game update that renames
        // one of these breaks the mod build instead of the verb.
        //
        // The subjects are deliberately unlike each other, because the engine's
        // are: benefitsAndCostsDescription hangs off the module TEMPLATE and is
        // what the build menu and the tech screen show, while FullSummary is a
        // static over an installed module STATE and is what a hab's own panel
        // shows. One takes a data name and the other a state id. That is the
        // trap this verb has, so both refusals name it.
        class Described
        {
            public string builder;      // engine method, quoted in a refusal
            public string subjectName;  // what it ran on, quoted in a refusal
            public JObject subject;
            public Func<string> build;
        }

        class DescribeKind
        {
            public string kind;         // canonical, dotted
            public Func<JObject, Described> resolve;
        }

        // One list rather than a dictionary plus a name table: three entries
        // scan for free, and a second static field in a partial class would
        // depend on an initializer order the language does not fix.
        static readonly List<DescribeKind> describeKinds = BuildDescribeTable();

        static List<DescribeKind> BuildDescribeTable()
        {
            var t = new List<DescribeKind>();
            AddDescribeKind(t, "module.benefits", delegate(JObject args)
            {
                TIHabModuleTemplate module = DescribeModuleTemplate(args);
                // Both optional and both meaningful when absent: a null hab is
                // the "no hab yet" call the tech and build screens make, and
                // the builder tests for it and substitutes reference bodies --
                // Luna for the mass and prospective-power lines, Earth for the
                // solar-output one. A null faction drops the faction-specific
                // lines rather than failing.
                TIFactionState faction = OptionalState<TIFactionState>(args, "faction");
                TIHabState hab = OptionalState<TIHabState>(args, "hab");
                bool prospective = Flag(args, "prospective");
                RequireMiningInputs("module.benefits", module, faction, hab, null);
                var d = new Described();
                d.builder = "TIHabModuleTemplate.benefitsAndCostsDescription";
                d.subjectName = module.dataName;
                d.subject = new JObject();
                d.subject["module"] = module.dataName;
                d.subject["faction"] = faction != null
                    ? Describe(faction) : JValue.CreateNull();
                d.subject["hab"] = hab != null ? Describe(hab) : JValue.CreateNull();
                d.subject["prospective"] = prospective;
                d.build = delegate
                {
                    return module.benefitsAndCostsDescription(faction, hab, prospective);
                };
                return d;
            });
            AddDescribeKind(t, "module.summary", delegate(JObject args)
            {
                TIHabModuleState module = DescribeModuleState(args);
                bool extended = Flag(args, "extended");
                // FullSummary is not a separate builder tree. At IL_012f-IL_0142 it
                // reads the module's own moduleTemplate, GetFaction() and hab and calls
                // benefitsAndCostsDescription with all three, so both mining
                // NullReferenceExceptions the kind above refuses by argument are
                // reachable here through the module state instead. spawn.module bypasses
                // ValidModuleForSlot, so a mining module on an orbital station -- a hab
                // with no hab site -- is a pairing this harness can produce even though
                // the game cannot, and describing it walks MonthlyResourceIncome into
                // GetMiningIncome_Day's unguarded ref_habSite dereference at its
                // IL_001d. Same refusal, named for this kind's own inputs.
                RequireMiningInputs("module.summary",
                    Safe<TIHabModuleTemplate>(
                        delegate { return module.moduleTemplate; }, null),
                    Safe<TIFactionState>(delegate { return module.GetFaction(); }, null),
                    Safe<TIHabState>(delegate { return module.hab; }, null),
                    module);
                var d = new Described();
                d.builder = "TIHabModuleState.FullSummary";
                d.subjectName = "module " + (int)module.ID;
                d.subject = new JObject();
                d.subject["module"] = DescribeModule(module);
                d.subject["extended"] = extended;
                d.build = delegate
                {
                    return TIHabModuleState.FullSummary(module, extended);
                };
                return d;
            });
            AddDescribeKind(t, "project.unlocks", delegate(JObject args)
            {
                TIProjectTemplate project = DescribeProjectTemplate(args);
                bool header = Flag(args, "header");
                bool truncate = Flag(args, "truncate");
                var d = new Described();
                d.builder = "TIProjectTemplate.AllUnlocksDetails";
                d.subjectName = project.dataName;
                d.subject = new JObject();
                d.subject["project"] = project.dataName;
                d.subject["header"] = header;
                d.subject["truncate"] = truncate;
                d.build = delegate
                {
                    return project.AllUnlocksDetails(header, truncate);
                };
                return d;
            });
            return t;
        }

        static void AddDescribeKind(List<DescribeKind> t, string kind,
            Func<JObject, Described> resolve)
        {
            var entry = new DescribeKind();
            entry.kind = kind;
            entry.resolve = resolve;
            t.Add(entry);
        }

        static JToken UiDescribe(JObject args)
        {
            string kind = Str(args, "kind");
            if (string.IsNullOrEmpty(kind))
                throw new VerbError("missing arg 'kind'; " + DescribeKinds());
            // The same case- and punctuation-insensitive match ui.tooltip uses,
            // so "module.benefits", "moduleBenefits" and "Module Benefits" are
            // one kind.
            string key = TooltipKey(kind);
            DescribeKind entry = null;
            for (int i = 0; i < describeKinds.Count; i++)
            {
                if (string.Equals(TooltipKey(describeKinds[i].kind), key,
                        StringComparison.Ordinal))
                {
                    entry = describeKinds[i];
                    break;
                }
            }
            if (entry == null)
                throw new VerbError("unknown describe kind '" + kind + "'; "
                    + DescribeKinds());

            // Resolution first and separately: a bad argument is a refusal
            // naming the argument, never a builder throwing halfway through.
            Described described = entry.resolve(args);
            string text;
            try { text = described.build(); }
            catch (Exception e)
            {
                throw new VerbError(described.builder + " threw for "
                    + described.subjectName + ": " + Note(e));
            }
            if (text == null) text = "";

            var o = new JObject();
            o["kind"] = entry.kind;
            o["builder"] = described.builder;
            o["subject"] = described.subject;
            // Untrimmed, both of them. These builders end in newlines the panel
            // relies on, and a character-exact assertion is the point of the
            // verb.
            o["text"] = text;
            o["plain"] = Safe<JToken>(
                delegate { return (JToken)tagPattern.Replace(text, ""); },
                new JValue(text));
            o["length"] = text.Length;
            return o;
        }

        static string DescribeKinds()
        {
            var names = new List<string>();
            for (int i = 0; i < describeKinds.Count; i++)
                names.Add(describeKinds[i].kind);
            return "kind is one of: " + string.Join(", ", names.ToArray());
        }

        // Two unguarded dereferences inside the builder, both gated on the template's
        // `mine` flag: Mass_tons reads GetMineSizeModifier off the faction (IL_0076),
        // and GetMiningIncome_Day reads the hab site (IL_001d). Refused rather than
        // substituted: either number invented here would be a mass, a cost and an
        // income no player ever sees. Takes the kind and the module state because the
        // two kinds get their faction and hab from different places.
        static void RequireMiningInputs(string kind, TIHabModuleTemplate module,
            TIFactionState faction, TIHabState hab, TIHabModuleState state)
        {
            if (module == null) return;
            if (!Safe<bool>(delegate { return module.mine; }, false)) return;

            if (faction == null)
                throw new VerbError("kind '" + kind + "' cannot describe the mining "
                    + "module '" + module.dataName + "' with no faction: it is a "
                    + "mining module (mine: true), and the builder's Mass_tons call "
                    + "reads GetMineSizeModifier off the faction with no null check, so "
                    + "a null faction throws inside the engine whatever the hab is. "
                    + (state != null
                        ? "FullSummary reads the faction off the module itself, so "
                          + "module " + (int)state.ID + " belongs to a hab with no "
                          + "owning faction; describe a module on an owned hab"
                        : "Pass arg 'faction' as a faction state id. 'hab' is not the "
                          + "trigger and a null one stays fine for this module"));

            // A null hab is the tech-screen call and is safe: the income guard
            // returns 0 before anything reads a site.
            if (hab == null) return;
            TIHabSiteState site = Safe<TIHabSiteState>(
                delegate { return hab.ref_habSite; }, null);
            if (site != null) return;
            throw new VerbError("kind '" + kind + "' cannot describe the "
                + "mining module '" + module.dataName + "' at "
                + StateName(hab) + " (" + (int)hab.ID + "), a "
                + Safe<string>(delegate { return hab.habType.ToString(); }, "hab")
                + " with no hab site: the builder reads daily production off "
                + "the site with no null check, so it throws inside the engine. "
                + "A mining module is habType Base and belongs on a hab that "
                + "sits on a site; "
                + (state != null
                    ? "spawn.module installs one without ValidModuleForSlot, which is "
                      + "the only way module " + (int)state.ID + " got here. Describe a "
                      + "module on a base, or use kind 'module.benefits' with no 'hab' "
                      + "for the tech-screen form of the text"
                    : "pass such a hab, or omit 'hab' for the "
                      + "tech-screen form of the text"));
        }

        static TIHabModuleTemplate DescribeModuleTemplate(JObject args)
        {
            string name = Str(args, "module");
            if (string.IsNullOrEmpty(name))
                throw new VerbError("kind 'module.benefits' needs arg 'module' as "
                    + "a TIHabModuleTemplate dataName (not a module state id -- "
                    + "that is kind 'module.summary')");
            TIHabModuleTemplate template =
                TemplateManager.Find<TIHabModuleTemplate>(name.Trim(), false);
            if (template == null)
                throw new VerbError("no TIHabModuleTemplate named '" + name
                    + "'; query.template type=TIHabModuleTemplate lists them");
            return template;
        }

        // The mirror image of the one above, and the reason this verb's refusals
        // spell the asymmetry out: `module` here is an installed module's state
        // id. The discovery path is kill.module's, through the same listing,
        // because a module state id is not addressable any other way.
        static TIHabModuleState DescribeModuleState(JObject args)
        {
            JToken moduleArg = args != null ? args["module"] : null;
            int habId = OptionalInt(args, "hab");
            if (moduleArg == null || moduleArg.Type == JTokenType.Null)
            {
                if (habId >= 0)
                    throw new VerbError("kind 'module.summary' takes arg 'module' "
                        + "as a module state id, not a template dataName (that is "
                        + "kind 'module.benefits'); "
                        + ModuleListFor(habId));
                throw new VerbError("kind 'module.summary' needs arg 'module' as a "
                    + "TIHabModuleState id, not a template dataName (that is kind "
                    + "'module.benefits'); pass 'hab' alone to list a hab's "
                    + "modules and their ids");
            }
            // A data name is the mistake this catches, and it is caught here so the
            // answer names the other kind rather than leaving Int() to report a bad
            // integer. Every non-integer token is caught, a numeric string
            // included: Int() refuses one on the next line, so accepting it here
            // only moved the refusal.
            if (moduleArg.Type != JTokenType.Integer)
                throw new VerbError("kind 'module.summary' takes arg 'module' as a "
                    + "TIHabModuleState id and got '" + moduleArg.ToString()
                    + "'; a template dataName is kind 'module.benefits'"
                    + (habId >= 0 ? ". " + ModuleListFor(habId)
                        : ". Pass 'hab' alone to list a hab's modules and their ids"));

            TIHabModuleState module = Arg<TIHabModuleState>(args, "module");
            if (habId >= 0)
            {
                TIHabState hab = Safe<TIHabState>(delegate { return module.hab; }, null);
                if (hab == null || habId != (int)hab.ID)
                    throw new VerbError("module " + (int)module.ID + " belongs to "
                        + (hab != null ? "hab " + (int)hab.ID + " (" + StateName(hab)
                            + ")" : "no hab") + ", not " + habId);
            }
            return module;
        }

        static TIProjectTemplate DescribeProjectTemplate(JObject args)
        {
            string name = Str(args, "project");
            if (string.IsNullOrEmpty(name))
                throw new VerbError("kind 'project.unlocks' needs arg 'project' as "
                    + "a TIProjectTemplate dataName");
            TIProjectTemplate template =
                TemplateManager.Find<TIProjectTemplate>(name.Trim(), false);
            if (template == null)
                throw new VerbError("no TIProjectTemplate named '" + name
                    + "'; query.template type=TIProjectTemplate lists them");
            return template;
        }

        // An absent optional state, distinguished from a present wrong one: null
        // is a meaningful argument to both of the builders that take one, so an
        // id that resolves to the wrong class must not quietly become it.
        static T OptionalState<T>(JObject args, string key) where T : TIGameState
        {
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return null;
            return Arg<T>(args, key);
        }

        #endregion

        #region ui.view

        // ui.view: which of the game's four top-level views is up, and the two a
        // caller may switch between.
        //
        // This exists so a screen-gated surface can be photographed without
        // clicks. ui.screenshot captures whatever is on screen, and most of the
        // game's UI is behind a view or an info screen that only a mouse reaches;
        // for QA the UI is view-only, so anything not reachable by a verb is not
        // testable at all.
        //
        // ViewControl.currentView is a public field and GotoView(ViewType) is the
        // only public mutator. The two arms this verb allows are the same two the
        // vanilla buttons run: GeneralControlsController.SolarSystem() and
        // .PoliticalMap() each call canvasManager.CloseActiveInfoScreen() and then
        // GotoView, in that order, so the info screen goes down before the view
        // moves. (SolarSystem() also zooms the camera afterwards; that is a camera
        // preference, not part of the view change, and is left alone.)
        //
        // The other two arms are refused rather than driven:
        //
        //  - MainMenu tears the campaign down -- ClearGameData then an async load
        //    of StartScreenScene -- which game.main_menu already owns, with the
        //    cinematic handling and the wait for the unload that a bare GotoView
        //    has none of.
        //  - SpaceCombat calls SpaceCombatManager.Initialize() and raises
        //    CombatStarts. Starting a fight from a view verb would put the harness
        //    in a state combat.status did not arm and combat.autoresolve does not
        //    know about.
        //
        // SolarSystem is refused as well when the active scene is not
        // SolarSystemScene, because that arm falls through to
        // SceneManager.LoadScene("SolarSystemScene"), which does not complete
        // inside this frame: the reply would describe a view that had not arrived.
        static JToken UiView(JObject args)
        {
            ViewControl viewMgr = ViewManager();
            var o = new JObject();
            o["scene"] = ActiveSceneName();
            o["currentView"] = ViewName(viewMgr);
            o["settable"] = new JArray(new JValue("SolarSystem"),
                                       new JValue("PoliticalMap"));
            o["changed"] = false;

            string wanted = Str(args, "view");
            if (string.IsNullOrEmpty(wanted)) return o;

            ViewType target = ParseView(wanted);
            o["requested"] = target.ToString();

            // Leaving the combat view is how a tactical fight is abandoned: the
            // SolarSystem arm disables SpaceCombatManager and clears
            // isSpaceCombatEnabled, with the combat state still standing and no
            // verb aware it happened.
            ViewType now = CurrentView(viewMgr);
            if (now == ViewType.SpaceCombat)
                throw new VerbError("the SpaceCombat view is up, and switching "
                    + "away from it disables SpaceCombatManager with the fight "
                    + "still standing; the combat verbs own that transition "
                    + "(combat.status, combat.autoresolve, combat.precombat). "
                    + "Nothing was changed");

            if (target == ViewType.SolarSystem)
            {
                string scene = ActiveSceneName();
                if (!string.Equals(scene, SolarSystemScene, StringComparison.Ordinal))
                    throw new VerbError("the active scene is '" + scene + "', not "
                        + SolarSystemScene + ", so GotoView(SolarSystem) would load "
                        + "that scene instead of switching, and the load does not "
                        + "finish inside this call. Nothing was changed");
            }

            // The vanilla ordering, and it matters: an info screen left up covers
            // the view that was just switched to, so a screenshot after this call
            // would photograph the screen rather than the map.
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack =
                Safe<PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager>(
                    delegate { return GameControl.canvasStack; }, null);
            o["infoScreenClosed"] = false;
            if (stack != null && ActiveInfoScreenName(stack) != null)
            {
                try { stack.CloseActiveInfoScreen(); }
                catch (Exception e)
                {
                    throw new VerbError("CloseActiveInfoScreen() threw: " + Note(e)
                        + ". The view was NOT changed");
                }
                o["infoScreenClosed"] = ActiveInfoScreenName(stack) == null;
            }

            try { viewMgr.GotoView(target); }
            catch (Exception e)
            {
                o["currentView"] = ViewName(viewMgr);
                throw new VerbError("GotoView(" + target + ") threw part way "
                    + "through: " + Note(e) + ". The view may be half moved; read "
                    + "ui.view with no arguments to see where it landed");
            }

            // GotoView's last two instructions write currentView from its
            // argument whatever the arm did, so this is read back rather than
            // assumed only for the case where the field says one thing and the
            // arm bailed early.
            o["currentView"] = ViewName(viewMgr);
            o["changed"] = CurrentView(viewMgr) == target;
            o["scene"] = ActiveSceneName();
            return o;
        }

        const string SolarSystemScene = "SolarSystemScene";

        static ViewControl ViewManager()
        {
            ViewControl viewMgr = Safe<ViewControl>(delegate
            {
                GameControl control = GameControl.control;
                return control != null ? control.viewMgr : null;
            }, null);
            if (viewMgr == null)
                throw new VerbError("GameControl has no viewMgr, which is the only "
                    + "thing that owns the view; nothing was read or changed");
            return viewMgr;
        }

        static ViewType CurrentView(ViewControl viewMgr)
        {
            return Safe<ViewType>(delegate { return viewMgr.currentView; },
                                  ViewType.None);
        }

        static JToken ViewName(ViewControl viewMgr)
        {
            return new JValue(CurrentView(viewMgr).ToString());
        }

        static string ActiveSceneName()
        {
            return Safe<string>(delegate
            {
                return UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene().name;
            }, null);
        }

        static ViewType ParseView(string wanted)
        {
            string s = wanted.Trim();
            if (string.Equals(s, "SolarSystem", StringComparison.OrdinalIgnoreCase))
                return ViewType.SolarSystem;
            if (string.Equals(s, "PoliticalMap", StringComparison.OrdinalIgnoreCase))
                return ViewType.PoliticalMap;
            if (string.Equals(s, "MainMenu", StringComparison.OrdinalIgnoreCase))
                throw new VerbError("view 'MainMenu' unloads the campaign; "
                    + "game.main_menu owns that path and handles the cinematics "
                    + "and the wait for the unload. Nothing was changed");
            if (string.Equals(s, "SpaceCombat", StringComparison.OrdinalIgnoreCase))
                throw new VerbError("view 'SpaceCombat' initialises a combat "
                    + "(SpaceCombatManager.Initialize and a CombatStarts event); "
                    + "the combat verbs own that. Nothing was changed");
            throw new VerbError("unknown view '" + wanted
                + "'; this verb sets SolarSystem or PoliticalMap");
        }

        #endregion

        #region ui.screen

        // ui.screen: the info screens -- habitats, fleets, research, nations and
        // the rest -- plus the space object detail panel and both rename panels.
        //
        // Same reason as ui.view: for a driving agent the UI is view-only, so a
        // panel no verb opens cannot be photographed and cannot be tested. The
        // rename panels are the case that forced it: a mod that adds a button to
        // either of them has no way to show it, because nothing headless could put
        // either on screen. The rename itself is not the point: ChangeHabBio is
        // already reachable through action.invoke.
        //
        // Screens have no enum. CanvasManager keeps them in a
        // Dictionary<Type, IInfoScreen> keyed by the controller's concrete runtime
        // type (its Initialize adds every ICanvas that is also an IInfoScreen under
        // canvas.GetType()), and ShowInfoScreen<T>() indexes that dictionary with
        // get_Item, which throws KeyNotFoundException for a type that is not in it.
        // So `show` resolves against the dictionary's own keys and refuses with the
        // list rather than throwing.
        //
        // The field is private initonly, so it is read by reflection, the same way
        // ui.options reads CanvasControllerBase.canvasManager.
        static readonly System.Reflection.FieldInfo infoScreensField =
            Safe<System.Reflection.FieldInfo>(delegate
            {
                return typeof(PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager)
                    .GetField("infoScreens",
                        System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance);
            }, null);

        // SpaceObjectDetailController.selectedSpaceObject is private, and it is
        // what OnClickHabRename reads to decide whether to open the rename panel
        // at all. A rename press with no way to check it first would silently do
        // nothing on another faction's hab.
        static readonly System.Reflection.FieldInfo selectedSpaceObjectField =
            Safe<System.Reflection.FieldInfo>(delegate
            {
                return typeof(SpaceObjectDetailController)
                    .GetField("selectedSpaceObject",
                        System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Instance);
            }, null);

        // Short names for the screens a test actually asks for. The full type name
        // is always accepted too, and `list` prints the real keys, so this table
        // going stale after a game update costs nothing.
        const string HabitatsScreen = "HabitatsScreenController";

        static readonly Dictionary<string, string> ScreenAliases = BuildScreenAliases();

        static Dictionary<string, string> BuildScreenAliases()
        {
            var t = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            t["habitats"] = HabitatsScreen;
            t["habs"] = HabitatsScreen;
            t["fleets"] = "FleetsScreenController";
            t["research"] = "ResearchScreenController";
            t["nations"] = "NationsScreenController";
            t["intel"] = "IntelScreenController";
            t["objectives"] = "ObjectivesScreenController";
            t["council"] = "CouncilGridController";
            t["councilors"] = "CouncilGridController";
            return t;
        }

        static JToken UiScreen(JObject args)
        {
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack =
                Safe<PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager>(
                    delegate { return GameControl.canvasStack; }, null);
            if (stack == null)
                throw new VerbError("GameControl has no canvasStack, so there are "
                    + "no screens to drive; nothing was changed");

            bool hide = Flag(args, "hide");
            bool rename = Flag(args, "rename");
            bool manage = Flag(args, "manage");
            string show = Str(args, "show");
            int habId = OptionalInt(args, "hab");
            int detailId = OptionalInt(args, "detail");
            // 'manage' is in this list so a lone manage=true reaches the refusal
            // below rather than reading as a bare list.
            bool acting = hide || rename || manage
                || show != null || habId >= 0 || detailId >= 0;

            var o = new JObject();
            o["activeInfoScreen"] = NameOrNull(ActiveInfoScreenName(stack));
            // Listing is the default and is also available alongside an action,
            // so one call can show a screen and say what else it could have shown.
            if (!acting || Flag(args, "list")) o["screens"] = ScreenNames(stack);
            if (!acting)
            {
                o["action"] = "list";
                if (infoScreensField == null) o["note"] = NoInfoScreensFieldNote;
                return o;
            }

            // Every combination below is one argument silently winning over
            // another, which would report an action the caller did not ask for.
            if (hide && (rename || show != null || habId >= 0 || detailId >= 0))
                throw new VerbError("'hide' closes the active info screen and "
                    + "cannot be combined with an argument that opens one; pass "
                    + "'hide' on its own. Nothing was changed");
            if (manage && habId < 0)
                throw new VerbError("'manage' is the second argument of "
                    + "HabDetailRequested and only means anything alongside "
                    + "'hab'; without one there is no event to put it on and it "
                    + "would be dropped in silence. Pass hab=<id> with it, or "
                    + "leave it out. Nothing was changed");
            if (habId >= 0 && detailId >= 0)
                throw new VerbError("'hab' and 'detail' open two different panels "
                    + "(the habitats screen and the space object detail panel); "
                    + "pass one. Nothing was changed");
            if (detailId >= 0 && show != null)
                throw new VerbError("'detail' opens the space object detail panel, "
                    + "which is not an info screen, so it cannot be combined with "
                    + "show='" + show + "'. Nothing was changed");
            if (habId >= 0 && rename)
                throw new VerbError("'hab' and 'rename' cannot be done in one "
                    + "call. HabitatsScreenController listens for HabDetailRequested "
                    + "as a QUEUEABLE listener, so EventManager defers it to a later "
                    + "frame, and when it does run it ends in SelectHabFromMenu, "
                    + "whose last statement is RevertRename() -- it would close a "
                    + "rename panel opened now. Call ui.screen hab=" + habId
                    + " first, then ui.screen rename=true. Nothing was changed");

            if (hide)
            {
                o["action"] = "hide";
                string before = ActiveInfoScreenName(stack);
                try { stack.CloseActiveInfoScreen(); }
                catch (Exception e)
                {
                    throw new VerbError("CloseActiveInfoScreen() threw: " + Note(e));
                }
                o["closed"] = NameOrNull(before);
                o["activeInfoScreen"] = NameOrNull(ActiveInfoScreenName(stack));
                return o;
            }

            if (detailId >= 0) return ScreenDetail(args, stack, rename, o);
            if (habId >= 0) return ScreenHab(args, stack, manage, show, o);
            if (show != null) return ScreenShow(stack, show, rename, o);
            return ScreenRename(stack, o);
        }

        // show=<type name or alias>: the engine's own ShowInfoScreen<T>, reached
        // by MakeGenericMethod because the type is only known at run time.
        static JToken ScreenShow(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack,
            string show, bool rename, JObject o)
        {
            o["action"] = "show";
            Type type = ResolveScreen(stack, show);
            o["screen"] = type.Name;
            // The rename button belongs to the habitats screen; no other info
            // screen has one, so asking for it on another is a mistake worth
            // refusing before anything is shown.
            if (rename && !string.Equals(type.Name, HabitatsScreen,
                                         StringComparison.Ordinal))
                throw new VerbError("'rename' presses the habitats screen's rename "
                    + "button, and show='" + show + "' resolves to " + type.Name
                    + ", which has none. Nothing was changed");
            // The remaining rename refusals run ahead of the screen change as
            // well, so every refusal on this path leaves the screen as it found
            // it, which is what "nothing was changed" claims. Reading the selected
            // hab this early reads the same hab the press would have got:
            // ShowInfoScreen<T> returns at once when T is already the active info
            // screen (IL_0000-IL_001d), and when it is not, Show() runs
            // SetEmptyHabView (Show IL_014d), which clears habToDisplay to the
            // null this refuses on either way.
            HabitatsScreenController habs = null;
            TIHabState selected = null;
            if (rename)
            {
                habs = HabitatsController(stack);
                selected = Safe<TIHabState>(
                    delegate { return habs.habToDisplay; }, null);
                RefuseUnrenameableHabOnHabitats(selected);
            }
            ShowScreen(stack, type);
            o["activeInfoScreen"] = NameOrNull(ActiveInfoScreenName(stack));
            o["shown"] = string.Equals(ActiveInfoScreenName(stack), type.Name,
                                       StringComparison.Ordinal);
            if (rename) PressRenameOnHabitats(habs, selected, o);
            return o;
        }

        // hab=<id>: the habitats screen with one hab selected, through the engine's
        // own event. Its listener is queueable, so the screen comes up on a later
        // frame and the reply says so rather than claiming a screen that is not
        // there yet.
        static JToken ScreenHab(JObject args,
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack,
            bool manage, string show, JObject o)
        {
            o["action"] = "hab";
            TIHabState hab = Arg<TIHabState>(args, "hab");
            if (show != null)
            {
                Type asked = ResolveScreen(stack, show);
                if (!string.Equals(asked.Name, HabitatsScreen,
                                   StringComparison.Ordinal))
                    throw new VerbError("'hab' selects a hab on the habitats "
                        + "screen, so show='" + show + "' (" + asked.Name
                        + ") cannot be combined with it. Nothing was changed");
            }
            o["hab"] = Describe(hab);
            o["manage"] = manage;

            PavonisInteractive.TerraInvicta.EventManager events =
                Safe<PavonisInteractive.TerraInvicta.EventManager>(
                    delegate { return GameControl.eventManager; }, null);
            if (events == null)
                throw new VerbError("GameControl has no eventManager, which is what "
                    + "carries HabDetailRequested; nothing was changed");
            try { events.TriggerEvent(new HabDetailRequested(hab, manage)); }
            catch (Exception e)
            {
                throw new VerbError("TriggerEvent(HabDetailRequested) threw: "
                    + Note(e));
            }

            o["activeInfoScreen"] = NameOrNull(ActiveInfoScreenName(stack));
            o["deferred"] = true;
            o["note"] = "the event was raised. HabitatsScreenController registers "
                + "OnHabDetailRequested as a queueable listener, so EventManager "
                + "runs it from its own Update on a later frame rather than inside "
                + "this call: activeInfoScreen above is the screen as it stands NOW. "
                + "Call ui.screen again (no arguments) to read it back, then "
                + "ui.screen rename=true for the rename panel";
            return o;
        }

        // detail=<id>: the space object detail panel. A direct method call, so
        // unlike the habitats event the panel itself has landed by the time the
        // verb returns, and rename can be pressed in the same call.
        //
        // What has NOT landed is the info screen going away. ViewSpaceObject's
        // hab arm ends in LaunchDetailHab, which calls
        // CanvasManager.SetActiveInfoPanel (IL_0045) when the hab panel was not
        // already enabled; that raises InfoPanelOpened, and CanvasManager
        // registers its own OnInfoPanelOpened with queueable: true, so
        // EventManager drains it from its own Update on a later frame. The
        // handler is what calls CloseInfoScreen on the active info screen. So a
        // screen that was up is still up when this returns and goes down a frame
        // later, and the reply says so instead of leaving the caller to guess
        // from an activeInfoScreen that is about to be wrong.
        static JToken ScreenDetail(JObject args,
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack,
            bool rename, JObject o)
        {
            o["action"] = "detail";
            TISpaceObjectState target = Arg<TISpaceObjectState>(args, "detail");
            SpaceObjectDetailController detail = DetailController(stack);
            o["target"] = Describe(target);

            if (rename) RefuseUnrenameableHabOnDetail(HabOf(target), detail);

            InfoPanel panelBefore = ActiveInfoPanel(stack);

            // updatePrevious false: true pushes the object onto the panel's own
            // back stack, which is a player's navigation history and not something
            // a test fixture should be writing into.
            try { detail.ViewSpaceObject(target, false); }
            catch (Exception e)
            {
                throw new VerbError("ViewSpaceObject threw: " + Note(e)
                    + ". The detail panel may be half built; read ui.screenshot");
            }
            detailOpenedFrame = FrameCount();
            o["detailPanelVisible"] = Safe<JToken>(
                delegate { return (JToken)detail.Visible(); }, JValue.CreateNull());
            o["selected"] = NameOrNull(SelectedDetailName(detail));

            // Read back rather than reuse the value taken at the top of the verb:
            // the panel change is what queues the close, so both readings belong
            // to the same call.
            string screenAfter = ActiveInfoScreenName(stack);
            o["activeInfoScreen"] = NameOrNull(screenAfter);
            // SetActiveInfoPanel raises InfoPanelOpened only when the panel value
            // changed (IL_0015) and the new value is non-zero (IL_0067); a change
            // to zero raises InfoWindowEntirelyClosed instead, which closes
            // nothing. Both conditions, so the reply never claims a queued close
            // the engine did not queue.
            InfoPanel panelAfter = ActiveInfoPanel(stack);
            bool panelOpened = panelAfter != panelBefore
                && panelAfter != default(InfoPanel);
            o["infoPanel"] = panelAfter.ToString();
            o["infoPanelChanged"] = panelAfter != panelBefore;
            o["infoScreenCloseQueued"] = panelOpened && screenAfter != null;
            if (panelOpened && screenAfter != null)
                o["note"] = "the detail panel is up, and " + screenAfter + " is "
                    + "still the active info screen ONLY until the next frame: "
                    + "SetActiveInfoPanel raised InfoPanelOpened, whose "
                    + "CanvasManager listener is queueable, and that listener "
                    + "closes the active info screen. Read ui.screen again to see "
                    + "it gone; a screenshot taken now would still show it";
            if (rename) RenameOnDetail(detail, o);
            return o;
        }

        // The frame ViewSpaceObject last ran on. ScreenRename reads it because a
        // batch can put detail= and rename=true in the same frame, where the
        // habitats screen the queued close has not reached yet would otherwise
        // win the rename.
        static int detailOpenedFrame = -1;

        static int FrameCount()
        {
            return Safe<int>(delegate { return UnityEngine.Time.frameCount; }, -1);
        }

        static InfoPanel ActiveInfoPanel(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack)
        {
            return Safe<InfoPanel>(delegate { return stack.GetActiveInfoPanel(); },
                                   default(InfoPanel));
        }

        // rename=true on its own: press whichever rename button belongs to the
        // panel that is up. Each engine handler returns in silence when its own
        // ownership test fails, so that is checked here and refused by name.
        //
        // The habitats screen normally wins because it is the active info screen
        // and the detail panel is not one. It must not win in the frame a detail
        // panel was opened: that close is queued (see ScreenDetail), so a batch
        // holding detail= and rename=true in one frame would still read the old
        // screen as active and press the button of a screen on its way out.
        static JToken ScreenRename(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack,
            JObject o)
        {
            o["action"] = "rename";
            string active = ActiveInfoScreenName(stack);
            SpaceObjectDetailController detail = Safe<SpaceObjectDetailController>(
                delegate { return DetailController(stack); }, null);
            bool detailVisible = detail != null
                && Safe<bool>(delegate { return detail.Visible(); }, false);
            bool detailJustOpened = detailVisible && detailOpenedFrame >= 0
                && detailOpenedFrame == FrameCount();
            o["detailOpenedThisFrame"] = detailJustOpened;
            if (!detailJustOpened
                && string.Equals(active, HabitatsScreen, StringComparison.Ordinal))
            {
                RenameOnHabitats(stack, o);
                return o;
            }
            if (detailVisible)
            {
                RenameOnDetail(detail, o);
                return o;
            }
            throw new VerbError("neither rename panel has an owner on screen: the "
                + "habitats screen is not the active info screen (it is "
                + NameOrText(active) + ") and the space object detail panel is not "
                + "visible. Open one first with ui.screen hab=<id> or ui.screen "
                + "detail=<id>. Nothing was pressed");
        }

        static void RenameOnHabitats(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack,
            JObject o)
        {
            HabitatsScreenController habs = HabitatsController(stack);
            TIHabState shown = Safe<TIHabState>(
                delegate { return habs.habToDisplay; }, null);
            RefuseUnrenameableHabOnHabitats(shown);
            PressRenameOnHabitats(habs, shown, o);
        }

        // The press alone, over a controller and a hab already read and already
        // through RefuseUnrenameableHabOnHabitats. Separate so the show path can
        // run both refusals before it changes what is on screen.
        static void PressRenameOnHabitats(HabitatsScreenController habs,
            TIHabState shown, JObject o)
        {
            o["renamePanel"] = "HabitatsScreenController.renameMyHabPanel";
            try { habs.OnClickRename(); }
            catch (Exception e)
            {
                throw new VerbError("OnClickRename() threw: " + Note(e));
            }
            o["renameHab"] = Describe(shown);
            o["renamePressed"] = true;
            o["renamePanelActive"] = PanelActive(Safe<UnityEngine.GameObject>(
                delegate { return habs.renameMyHabPanel; }, null));
        }

        // The instance CanvasManager registered, not whatever a scene search finds:
        // the registry entry is the one every engine path drives, and a search can
        // return an unregistered copy sitting on a disabled canvas.
        static HabitatsScreenController HabitatsController(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack)
        {
            HabitatsScreenController habs = Safe<HabitatsScreenController>(delegate
            {
                if (infoScreensField == null) return null;
                var map = infoScreensField.GetValue(stack)
                    as System.Collections.IDictionary;
                return map != null
                    ? map[typeof(HabitatsScreenController)] as HabitatsScreenController
                    : null;
            }, null);
            if (habs == null)
                throw new VerbError(infoScreensField == null
                    ? "the registered screens could not be read at all: "
                        + NoInfoScreensFieldNote + ". Nothing was pressed"
                    : "CanvasManager has no HabitatsScreenController "
                        + "registered, so there is no rename button to press; "
                        + "ui.screen with no arguments names the screens this "
                        + "campaign registered. Nothing was pressed");
            return habs;
        }

        static void RenameOnDetail(SpaceObjectDetailController detail, JObject o)
        {
            o["renamePanel"] = "SpaceObjectDetailController.renameHabPanel";
            TIHabState shown = SelectedDetailHab(detail);
            RefuseUnrenameableHabOnDetail(shown, detail);
            try { detail.OnClickHabRename(); }
            catch (Exception e)
            {
                throw new VerbError("OnClickHabRename() threw: " + Note(e));
            }
            o["renameHab"] = Describe(shown);
            o["renamePressed"] = true;
            o["renamePanelActive"] = PanelActive(Safe<UnityEngine.GameObject>(
                delegate { return detail.renameHabPanel; }, null));
        }

        // Each handler opens with a hab test and an ownership test and returns
        // without a sound when either fails, so a press made anyway would report a
        // panel that never came up and no reason for it.
        //
        // The two do NOT read the same owner. HabitatsScreenController.OnClickRename
        // compares habToDisplay's faction against GameControl.control.activePlayer
        // (IL_0019-IL_0023). SpaceObjectDetailController.OnClickHabRename compares
        // the selected hab's faction against the controller's own
        // CanvasControllerBase.activePlayer (IL_002b), an auto-property whose only
        // writer is CanvasControllerBase.SetActivePlayer. A console setfaction moves
        // GameControl's and leaves the controller's behind until something calls
        // SetActivePlayer, so the two diverge and a single check would refuse a
        // press the engine would take, or take one the engine would refuse.
        static void RefuseUnrenameableHabOnHabitats(TIHabState hab)
        {
            TIFactionState player = Safe<TIFactionState>(delegate
            {
                GameControl control = GameControl.control;
                return control != null ? control.activePlayer : null;
            }, null);
            RefuseUnrenameableHab(hab, "habitats", player,
                "the active player GameControl holds");
        }

        static void RefuseUnrenameableHabOnDetail(TIHabState hab,
            SpaceObjectDetailController detail)
        {
            TIFactionState player = Safe<TIFactionState>(delegate
            {
                return detail != null ? detail.activePlayer : null;
            }, null);
            RefuseUnrenameableHab(hab, "detail", player,
                "the active player the detail controller itself holds (its own "
                + "cached CanvasControllerBase.activePlayer, which is what "
                + "OnClickHabRename reads and which a console setfaction leaves "
                + "stale)");
        }

        static void RefuseUnrenameableHab(TIHabState hab, string where,
            TIFactionState player, string playerSource)
        {
            if (hab == null)
                throw new VerbError("the " + where + " panel has no hab selected, "
                    + "and the rename handler returns without opening anything in "
                    + "that case. Nothing was pressed");
            TIFactionState owner = Safe<TIFactionState>(
                delegate { return hab.faction; }, null);
            if (player == null)
                throw new VerbError("there is no active player to compare the hab's "
                    + "owner against: the " + where + " handler reads "
                    + playerSource + ", and it is null. Nothing was pressed");
            if (!ReferenceEquals(owner, player))
                throw new VerbError("hab " + (int)hab.ID + " (" + StateName(hab)
                    + ") belongs to " + NameOrText(StateName(owner))
                    + ", not " + NameOrText(StateName(player)) + ", which is "
                    + playerSource + "; the engine's " + where + " rename handler "
                    + "returns without opening the panel for another faction's "
                    + "hab. Nothing was pressed");
        }

        static void ShowScreen(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack,
            Type type)
        {
            System.Reflection.MethodInfo generic = Safe<System.Reflection.MethodInfo>(
                delegate
                {
                    return typeof(PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager)
                        .GetMethod("ShowInfoScreen",
                            System.Reflection.BindingFlags.Public
                            | System.Reflection.BindingFlags.Instance);
                }, null);
            if (generic == null || !generic.IsGenericMethodDefinition)
                throw new VerbError("CanvasManager has no generic ShowInfoScreen<T> "
                    + "method on this build, so no screen can be shown");
            try { generic.MakeGenericMethod(type).Invoke(stack, null); }
            catch (System.Reflection.TargetInvocationException e)
            {
                throw new VerbError("ShowInfoScreen<" + type.Name + "> threw: "
                    + Note(e.InnerException != null ? e.InnerException : e));
            }
            catch (Exception e)
            {
                throw new VerbError("ShowInfoScreen<" + type.Name + "> could not be "
                    + "called: " + Note(e));
            }
        }

        // The type has to satisfy both of ShowInfoScreen's requirements before it
        // is handed to MakeGenericMethod: the IInfoScreen constraint, which
        // MakeGenericMethod itself throws on, and membership of the dictionary,
        // which get_Item throws on inside the call.
        static Type ResolveScreen(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack,
            string wanted)
        {
            string name = wanted != null ? wanted.Trim() : null;
            if (string.IsNullOrEmpty(name))
                throw new VerbError("arg 'show' is empty; ui.screen list=true names "
                    + "the screens this campaign registered");
            string alias;
            if (ScreenAliases.TryGetValue(name, out alias)) name = alias;

            List<Type> keys = ScreenTypes(stack);
            for (int i = 0; i < keys.Count; i++)
            {
                if (!string.Equals(keys[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!typeof(PavonisInteractive.TerraInvicta.Systems.UI.IInfoScreen)
                        .IsAssignableFrom(keys[i]))
                    throw new VerbError(keys[i].Name + " is registered but does not "
                        + "implement IInfoScreen, which ShowInfoScreen<T> requires; "
                        + "nothing was shown");
                return keys[i];
            }
            throw new VerbError("no info screen named '" + wanted + "'. This "
                + "campaign registered: " + ScreenList(keys)
                + ". Short names also accepted: " + AliasList());
        }

        static List<Type> ScreenTypes(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack)
        {
            var found = new List<Type>();
            if (infoScreensField == null) return found;
            var map = Safe<System.Collections.IDictionary>(
                delegate { return infoScreensField.GetValue(stack)
                    as System.Collections.IDictionary; }, null);
            if (map == null) return found;
            try
            {
                foreach (object key in map.Keys)
                {
                    var type = key as Type;
                    if (type != null) found.Add(type);
                }
            }
            catch (Exception) { }
            found.Sort(delegate(Type a, Type b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });
            return found;
        }

        static JArray ScreenNames(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack)
        {
            List<Type> keys = ScreenTypes(stack);
            var a = new JArray();
            for (int i = 0; i < keys.Count; i++) a.Add(new JValue(keys[i].Name));
            return a;
        }

        // An empty list has two causes and they need different answers. No
        // campaign is the caller's problem; a missing field is the mod's, and
        // blaming the campaign for it sends the reader looking in the wrong place
        // after a game update renames CanvasManager.infoScreens.
        const string NoInfoScreensFieldNote =
            "CanvasManager has no private 'infoScreens' field on this build, so "
            + "the field this verb reads by reflection is gone -- a game update "
            + "that renamed or replaced it is the likely cause, and this says "
            + "nothing about whether a campaign is loaded. list, show= and "
            + "rename= on the habitats screen all read that dictionary and stay "
            + "unavailable until the mod is updated; hide=, detail= and rename= "
            + "on the detail panel do not touch it and still work";

        static string ScreenList(List<Type> keys)
        {
            if (keys.Count == 0)
                return infoScreensField == null
                    ? "none, and none could be read: " + NoInfoScreensFieldNote
                    : "none (the campaign UI registers them at load, so this reads "
                        + "empty outside a loaded campaign)";
            var parts = new string[keys.Count];
            for (int i = 0; i < keys.Count; i++) parts[i] = keys[i].Name;
            return string.Join(", ", parts);
        }

        static string AliasList()
        {
            var parts = new List<string>();
            foreach (KeyValuePair<string, string> pair in ScreenAliases)
                parts.Add(pair.Key);
            parts.Sort(StringComparer.Ordinal);
            return string.Join(", ", parts.ToArray());
        }

        static string ActiveInfoScreenName(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack)
        {
            return Safe<string>(delegate
            {
                PavonisInteractive.TerraInvicta.Systems.UI.IInfoScreen active =
                    stack.ActiveInfoScreen;
                return active != null ? active.GetType().Name : null;
            }, null);
        }

        static SpaceObjectDetailController DetailController(
            PavonisInteractive.TerraInvicta.Systems.UI.CanvasManager stack)
        {
            SpaceObjectDetailController detail =
                Safe<SpaceObjectDetailController>(delegate
                {
                    return stack.SpaceObjectDetail as SpaceObjectDetailController;
                }, null);
            if (detail == null)
                throw new VerbError("CanvasManager holds no SpaceObjectDetail "
                    + "controller, so there is no detail panel to drive");
            return detail;
        }

        static TIHabState SelectedDetailHab(SpaceObjectDetailController detail)
        {
            if (selectedSpaceObjectField == null) return null;
            TIGameState selected = Safe<TIGameState>(
                delegate { return selectedSpaceObjectField.GetValue(detail)
                    as TIGameState; }, null);
            return HabOf(selected);
        }

        static string SelectedDetailName(SpaceObjectDetailController detail)
        {
            if (selectedSpaceObjectField == null) return null;
            return Safe<string>(delegate
            {
                var selected = selectedSpaceObjectField.GetValue(detail) as TIGameState;
                return selected != null ? StateName(selected) : null;
            }, null);
        }

        // The engine's own reduction: ViewSpaceObject dispatches a hab-typed object
        // to LaunchDetailHab(state.ref_hab), and OnClickHabRename reads the same
        // property, so a state that is a hab answers itself and anything else null.
        static TIHabState HabOf(TIGameState state)
        {
            if (state == null) return null;
            return Safe<TIHabState>(delegate { return state.ref_hab; }, null);
        }

        static JToken PanelActive(UnityEngine.GameObject panel)
        {
            if (panel == null) return JValue.CreateNull();
            return Safe<JToken>(
                delegate { return (JToken)panel.activeInHierarchy; },
                JValue.CreateNull());
        }

        static JToken NameOrNull(string name)
        {
            return name != null ? (JToken)new JValue(name) : JValue.CreateNull();
        }

        static string NameOrText(string name)
        {
            return name != null ? name : "none";
        }

        #endregion
    }
}
