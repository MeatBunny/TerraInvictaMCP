using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.Modding;
using PavonisInteractive.TerraInvicta.Systems.Bootstrap;

namespace TerraInvictaMCP
{
    // query.localize, query.scenarios, query.scenario, query.templateDupes,
    // assets.bundles, assets.resolve, query.designs, query.councilors, query.nations.
    public static partial class Verbs
    {
        const int MaxLocalizeKeys = 200;
        const int MaxDupeRows = 200;
        const int MaxDupeRowLimit = 2000;
        const int MaxBundleAssets = 500;
        const int MaxBundleAssetLimit = 5000;
        const int MaxListRows = 200;
        const int MaxListRowLimit = 2000;

        #region query.localize

        // The engine's own loaded key tables, never disk: mod and scenario localization
        // only exists merged in here. Bound in GlobalInstaller at boot, so this answers
        // with no campaign loaded.
        static LocalizationManager LocManager()
        {
            LocalizationManager loc = null;
            try
            {
                var container = GlobalInstaller.container;
                if (container != null) loc = container.Resolve<LocalizationManager>();
            }
            catch (Exception) { }
            if (loc == null) throw new VerbError("no localization manager");
            return loc;
        }

        static readonly FieldInfo languagesField = LanguagesField();

        static FieldInfo LanguagesField()
        {
            try
            {
                return typeof(LocalizationManager).GetField("languages",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception) { return null; }
        }

        static IDictionary<string, IDictionary<string, string>> LanguageTables(LocalizationManager loc)
        {
            if (languagesField == null) throw new VerbError("no languages field on localization manager");
            var languages = languagesField.GetValue(loc)
                as IDictionary<string, IDictionary<string, string>>;
            if (languages == null) throw new VerbError("localization tables are not loaded yet");
            return languages;
        }

        // The loaded campaign's scenario metatemplate, read from GameControl's private
        // field rather than the public getter: the getter falls back to a scene walk for
        // the start menu's current selection, and this must report only what a campaign
        // actually loaded.
        static readonly FieldInfo scenarioMetaField = ScenarioMetaField();

        static FieldInfo ScenarioMetaField()
        {
            try
            {
                return typeof(GameControl).GetField("_scenarioMetaTemplate",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception) { return null; }
        }

        static TIMetaTemplate LoadedScenarioMeta()
        {
            try
            {
                GameControl control = GameControl.control;
                if (control == null || scenarioMetaField == null) return null;
                return scenarioMetaField.GetValue(control) as TIMetaTemplate;
            }
            catch (Exception) { return null; }
        }

        static string ScenarioPostfix()
        {
            TIMetaTemplate meta = LoadedScenarioMeta();
            if (meta == null) return null;
            string postfix = Safe<string>(delegate { return meta.scenarioLocalizationPostfix; }, null);
            return string.IsNullOrEmpty(postfix) ? null : postfix;
        }

        static JToken QueryLocalize(JObject args)
        {
            LocalizationManager loc = LocManager();
            IDictionary<string, IDictionary<string, string>> languages = LanguageTables(loc);

            string language = Str(args, "language");
            if (string.IsNullOrEmpty(language))
                language = Safe<string>(delegate { return loc.currentLanguageKey; }, null);
            if (string.IsNullOrEmpty(language))
                throw new VerbError("no language set; pass 'language'");
            IDictionary<string, string> table;
            if (!languages.TryGetValue(language, out table) || table == null)
                throw new VerbError("unknown language '" + language + "' ("
                    + LanguageNames(languages) + ")");
            IDictionary<string, string> english;
            languages.TryGetValue("en", out english);

            var results = new JArray();
            JToken keysToken = args["keys"];
            if (keysToken != null && keysToken.Type != JTokenType.Null)
            {
                List<string> keys = StringList(keysToken, MaxLocalizeKeys, "keys");
                if (keys.Count == 0) throw new VerbError("arg 'keys' is empty");
                for (int i = 0; i < keys.Count; i++)
                    results.Add(ResolveKey(keys[i], null, table, english));
            }
            else
            {
                string typeName = Str(args, "type");
                string dataName = Str(args, "dataName");
                if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(dataName))
                    throw new VerbError("pass 'keys' or 'type' + 'dataName'");
                TIDataTemplate found = NamedTemplate(typeName.Trim(), dataName.Trim());
                string locName = Safe<string>(delegate { return found.localizationName; },
                    dataName.Trim());
                // The same postfix fallback Loc.T_Scenario runs: <key><postfix> first,
                // then the plain key. No postfix outside a campaign.
                string postfix = ScenarioPostfix();
                string[] locFields = new string[] { "displayName", "summary", "description" };
                for (int i = 0; i < locFields.Length; i++)
                {
                    string baseKey = found.GetType().Name + "." + locFields[i] + "." + locName;
                    JObject row = ResolveKey(baseKey, postfix, table, english);
                    row["field"] = locFields[i];
                    results.Add(row);
                }
            }

            var o = new JObject();
            o["language"] = language;
            o["count"] = results.Count;
            o["returned"] = results.Count;
            o["truncated"] = false;
            o["results"] = results;
            return o;
        }

        // Candidates most-specific first: postfixed key in the requested language, then
        // its English fallback, then the plain key in both. fellBack marks any answer
        // that did not come from the first candidate, which is both fallbacks the engine
        // itself takes (scenario postfix to plain, non-core language to English).
        static JObject ResolveKey(string baseKey, string postfix,
            IDictionary<string, string> table, IDictionary<string, string> english)
        {
            var o = new JObject();
            o["key"] = baseKey;
            string text;
            bool first = true;
            string[] candidates = postfix != null
                ? new string[] { baseKey + postfix, baseKey }
                : new string[] { baseKey };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (table.TryGetValue(candidates[i], out text))
                {
                    o["text"] = text;
                    o["found"] = true;
                    o["fellBack"] = !first;
                    return o;
                }
                first = false;
                if (english != null && !ReferenceEquals(english, table)
                    && english.TryGetValue(candidates[i], out text))
                {
                    o["text"] = text;
                    o["found"] = true;
                    o["fellBack"] = true;
                    return o;
                }
            }
            o["text"] = null;
            o["found"] = false;
            o["fellBack"] = false;
            return o;
        }

        static string LanguageNames(IDictionary<string, IDictionary<string, string>> languages)
        {
            var names = new List<string>(languages.Keys);
            names.Sort(StringComparer.Ordinal);
            return string.Join(", ", names.ToArray());
        }

        static TIDataTemplate NamedTemplate(string typeName, string dataName)
        {
            Type type = TemplateType(typeName);
            if (type == null) throw new VerbError("no template class named '" + typeName + "'");
            if (!TemplatesLoaded()) throw new VerbError("templates are not loaded yet");
            TIDataTemplate[] all = AllTemplates(type);
            for (int i = 0; i < all.Length; i++)
            {
                if (Same(TemplateName(all[i]), dataName)) return all[i];
            }
            throw new VerbError("no " + type.Name + " named '" + dataName + "'");
        }

        #endregion

        #region query.scenarios / query.scenario

        // The two scenarios the picker hides behind the DLC ownership check; requiredDLC
        // itself is metadata the picker never reads.
        static readonly string[] DlcGatedScenarios =
            new string[] { "BrokenEarthScenario", "2003Scenario" };

        // The picker's own enumeration, replicated from template data so it answers at
        // the main menu and in-game alike: isNewCampaignOption metas grouped by
        // newCampaignOptionCategory, categories ordered by optionPriority, options by
        // listPriority; priority 999 is editor-only and hidden, and the two DLC
        // scenarios need the DLC validated.
        static JToken QueryScenarios(JObject args)
        {
            if (!TemplatesLoaded()) throw new VerbError("templates are not loaded yet");
            bool dlc = Safe<bool>(delegate { return GameControl.DLCValidated; }, false);

            var metas = new List<TIMetaTemplate>();
            foreach (TIMetaTemplate t in TemplateManager.IterateByClass<TIMetaTemplate>(true))
            {
                if (t == null) continue;
                if (!Safe<bool>(delegate { return t.isNewCampaignOption; }, false)) continue;
                metas.Add(t);
            }

            var categoryNames = new List<string>();
            var categoryPriority = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < metas.Count; i++)
            {
                string category = metas[i].newCampaignOptionCategory;
                if (category == null) category = "";
                if (categoryPriority.ContainsKey(category)) continue;
                categoryPriority[category] = metas[i].optionPriority;
                categoryNames.Add(category);
            }
            categoryNames.Sort(delegate(string x, string y)
            {
                int byPriority = categoryPriority[x].CompareTo(categoryPriority[y]);
                return byPriority != 0 ? byPriority : string.CompareOrdinal(x, y);
            });

            var categories = new JArray();
            for (int c = 0; c < categoryNames.Count; c++)
            {
                string category = categoryNames[c];
                if (categoryPriority[category] == 999) continue;

                var options = new List<TIMetaTemplate>();
                for (int i = 0; i < metas.Count; i++)
                {
                    TIMetaTemplate m = metas[i];
                    string cat = m.newCampaignOptionCategory;
                    if (!string.Equals(cat != null ? cat : "", category, StringComparison.Ordinal))
                        continue;
                    if (m.listPriority == 999) continue;
                    if (!dlc && IsDlcGated(m.dataName)) continue;
                    options.Add(m);
                }
                options.Sort(delegate(TIMetaTemplate x, TIMetaTemplate y)
                {
                    int byPriority = x.listPriority.CompareTo(y.listPriority);
                    return byPriority != 0 ? byPriority
                        : string.CompareOrdinal(x.dataName, y.dataName);
                });

                var rows = new JArray();
                for (int i = 0; i < options.Count; i++)
                {
                    TIMetaTemplate m = options[i];
                    var row = new JObject();
                    row["dataName"] = m.dataName;
                    string display = ScenarioName(m);
                    row["displayName"] = display != null ? display : m.dataName;
                    row["priority"] = m.listPriority;
                    row["requiredDLC"] = Strings(m.requiredDLC);
                    Put(row, "tutorialAllowed", delegate { return (JToken)m.tutorialAllowed; });
                    rows.Add(row);
                }

                var entry = new JObject();
                entry["name"] = category;
                entry["priority"] = categoryPriority[category];
                entry["options"] = rows;
                categories.Add(entry);
            }

            var o = new JObject();
            o["dlcValidated"] = dlc;
            o["categories"] = categories;
            return o;
        }

        static bool IsDlcGated(string dataName)
        {
            for (int i = 0; i < DlcGatedScenarios.Length; i++)
            {
                if (string.Equals(DlcGatedScenarios[i], dataName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        static JToken QueryScenario(JObject args)
        {
            TITimeState time = Safe<TITimeState>(delegate { return GameStateManager.Time(); }, null);
            if (time == null) throw new VerbError("no campaign time state");
            string name = Safe<string>(delegate { return time.scenarioMetaTemplateName; }, null);
            TIMetaTemplate meta = Safe<TIMetaTemplate>(
                delegate { return time.scenarioMetaTemplate; }, null);

            var o = new JObject();
            o["dataName"] = meta != null ? TemplateName(meta) : name;
            Put(o, "master", delegate { return (JToken)time.masterMetaTemplateName; });
            if (meta != null)
            {
                var capturedMeta = meta;
                Put(o, "scenarioTags", delegate { return Strings(capturedMeta.scenarioTags); });
                Put(o, "localizationPostfix",
                    delegate { return (JToken)capturedMeta.scenarioLocalizationPostfix; });
                Put(o, "requiredDLC", delegate { return Strings(capturedMeta.requiredDLC); });
                Put(o, "templateNames", delegate { return Strings(capturedMeta.templateNames); });
                Put(o, "tutorialAllowed", delegate { return (JToken)capturedMeta.tutorialAllowed; });
            }
            else
            {
                o["scenarioTags"] = null;
                o["localizationPostfix"] = null;
                o["requiredDLC"] = null;
                o["templateNames"] = null;
                o["tutorialAllowed"] = null;
                o["note"] = "scenario metatemplate not found in the live tables";
            }

            TIStartTimeTemplate start = Safe<TIStartTimeTemplate>(
                delegate { return time.template; }, null);
            if (start != null)
            {
                var startTime = new JObject();
                startTime["dataName"] = TemplateName(start);
                Put(startTime, "year", delegate { return (JToken)start.year; });
                o["startTime"] = startTime;
            }
            else o["startTime"] = null;

            // Both campaign entry paths write the scenario into GameControl and run
            // ResolveScenarioTemplates on it in the same breath, so a matching entry
            // there is the record that resolution ran for this scenario.
            TIMetaTemplate loaded = LoadedScenarioMeta();
            o["resolved"] = loaded != null
                && (ReferenceEquals(loaded, meta) || Same(TemplateName(loaded), name));
            return o;
        }

        static JToken Strings(IEnumerable<string> values)
        {
            if (values == null) return JValue.CreateNull();
            var a = new JArray();
            foreach (string value in values) a.Add(new JValue(value));
            return a;
        }

        #endregion

        #region query.templateDupes

        static readonly FieldInfo templatesByTypeField = TemplateManagerField("templatesByType");
        static readonly FieldInfo duplicatesByTypeField =
            TemplateManagerField("duplicateTemplatesByType");

        static FieldInfo TemplateManagerField(string name)
        {
            try
            {
                return typeof(TemplateManager).GetField(name,
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch (Exception) { return null; }
        }

        // The engine keeps later registrations with differing scenarioTags in a private
        // duplicates table and resolves them at campaign start; this is the only view of
        // which candidate is live and which were parked or dropped. The engine records
        // no per-entry provenance, so each candidate's source is null and the row
        // carries the mod files that write the data name instead.
        static JToken QueryTemplateDupes(JObject args)
        {
            if (!TemplatesLoaded()) throw new VerbError("templates are not loaded yet");
            TemplateManager manager = TemplateManager.self;
            if (manager == null || duplicatesByTypeField == null)
                throw new VerbError("no duplicate tables");
            var duplicates = duplicatesByTypeField.GetValue(manager)
                as IDictionary<Type, Dictionary<string, List<TIDataTemplate>>>;
            if (duplicates == null) throw new VerbError("no duplicate tables");
            var liveTables = templatesByTypeField != null
                ? templatesByTypeField.GetValue(manager)
                    as IDictionary<Type, Dictionary<string, TIDataTemplate>>
                : null;

            Type typeFilter = null;
            string wanted = Str(args, "type");
            if (!string.IsNullOrEmpty(wanted))
            {
                typeFilter = TemplateType(wanted.Trim());
                if (typeFilter == null)
                    throw new VerbError("no template class named '" + wanted + "'");
            }
            string dataNameFilter = Str(args, "dataName");
            int limit = Limit(args, MaxDupeRows, MaxDupeRowLimit);

            var keys = new List<string>();
            var rows = new List<JObject>();
            foreach (KeyValuePair<Type, Dictionary<string, List<TIDataTemplate>>> byType in duplicates)
            {
                if (typeFilter != null && byType.Key != typeFilter) continue;
                if (byType.Value == null) continue;
                Dictionary<string, TIDataTemplate> liveOfType = null;
                if (liveTables != null) liveTables.TryGetValue(byType.Key, out liveOfType);
                foreach (KeyValuePair<string, List<TIDataTemplate>> byName in byType.Value)
                {
                    if (byName.Value == null || byName.Value.Count == 0) continue;
                    if (!string.IsNullOrEmpty(dataNameFilter)
                        && !Same(byName.Key, dataNameFilter)) continue;

                    var candidates = new JArray();
                    TIDataTemplate live = null;
                    if (liveOfType != null) liveOfType.TryGetValue(byName.Key, out live);
                    if (live != null) candidates.Add(Candidate(live, true));
                    for (int i = 0; i < byName.Value.Count; i++)
                    {
                        TIDataTemplate parked = byName.Value[i];
                        if (parked == null || ReferenceEquals(parked, live)) continue;
                        candidates.Add(Candidate(parked, false));
                    }

                    var row = new JObject();
                    row["type"] = byType.Key.Name;
                    row["dataName"] = byName.Key;
                    row["candidates"] = candidates;
                    row["modFiles"] = ModsTouching(byType.Key.Name, byName.Key);
                    keys.Add(byType.Key.Name + "." + byName.Key);
                    rows.Add(row);
                }
            }

            JArray a = SortedPage(keys, rows, limit);
            var o = new JObject();
            AddPage(o, a, rows.Count, limit);
            o["duplicates"] = a;
            return o;
        }

        static JObject Candidate(TIDataTemplate template, bool live)
        {
            var o = new JObject();
            Put(o, "scenarioTags", delegate { return Strings(template.scenarioTags); });
            o["source"] = null;
            o["live"] = live;
            return o;
        }

        // The mod files the game's own loader records as writing this data name into
        // this template file: file-level attribution, the closest the engine keeps to a
        // per-candidate source.
        static JArray ModsTouching(string typeName, string dataName)
        {
            var a = new JArray();
            List<JsonMod> mods = Safe<List<JsonMod>>(
                delegate { return ModTemplateManager.jsonMods; }, null);
            if (mods == null) return a;
            string file = typeName + ".json";
            for (int i = 0; i < mods.Count && a.Count < 20; i++)
            {
                JsonMod m = mods[i];
                if (m == null) continue;
                string target = Safe<string>(delegate { return m.TargetFilePath; }, null);
                if (target == null) continue;
                string baseName = null;
                try { baseName = Path.GetFileName(target); }
                catch (Exception) { }
                if (!string.Equals(baseName, file, StringComparison.OrdinalIgnoreCase)) continue;
                HashSet<string> names = Safe<HashSet<string>>(
                    delegate { return m.GetDataNames(); }, null);
                if (names == null || !names.Contains(dataName)) continue;
                string mod = ParentName(Safe<string>(delegate { return m.ModFilePath; }, null));
                a.Add(new JValue(mod != null ? mod : baseName));
            }
            return a;
        }

        #endregion

        #region assets.bundles / assets.resolve

        static readonly FieldInfo loadedBundlesField = LoadedBundlesField();

        static FieldInfo LoadedBundlesField()
        {
            try
            {
                return typeof(AssetBundles.AssetBundleManager).GetField("loadedBundles",
                    BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch (Exception) { return null; }
        }

        // The registry AssetBundleManager.Initialize fills at boot from the base
        // manifest, the DLC, and every mod's declared bundles.
        static Dictionary<string, UnityEngine.AssetBundle> LoadedBundles()
        {
            if (loadedBundlesField == null) throw new VerbError("no bundle registry");
            var bundles = Safe<Dictionary<string, UnityEngine.AssetBundle>>(delegate
            {
                return loadedBundlesField.GetValue(null)
                    as Dictionary<string, UnityEngine.AssetBundle>;
            }, null);
            if (bundles == null) throw new VerbError("asset bundles are not initialized yet");
            return bundles;
        }

        // Bundle file paths every mod declared, keyed by the lowercased file name the
        // registry uses as the bundle name.
        static Dictionary<string, string> ModBundlePaths()
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> declared = Safe<List<string>>(
                delegate { return ModManager.ModAssetBundles; }, null);
            if (declared == null) return paths;
            for (int i = 0; i < declared.Count; i++)
            {
                string path = declared[i];
                if (string.IsNullOrEmpty(path)) continue;
                string name = null;
                try { name = Path.GetFileName(path).ToLowerInvariant(); }
                catch (Exception) { }
                if (!string.IsNullOrEmpty(name) && !paths.ContainsKey(name)) paths[name] = path;
            }
            return paths;
        }

        static JToken AssetsBundles(JObject args)
        {
            Dictionary<string, UnityEngine.AssetBundle> bundles = LoadedBundles();
            string wanted = Str(args, "bundle");
            if (!string.IsNullOrEmpty(wanted)) return OneBundle(bundles, wanted.Trim(), args);

            Dictionary<string, string> modPaths = ModBundlePaths();
            var names = new List<string>(bundles.Keys);
            names.Sort(StringComparer.Ordinal);
            var a = new JArray();
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                UnityEngine.AssetBundle bundle = bundles[name];
                var row = new JObject();
                row["name"] = name;
                row["source"] = modPaths.ContainsKey(name) ? "mod" : "game";
                bool alive = false;
                try { alive = bundle != null; }
                catch (Exception) { }
                row["loaded"] = alive;
                if (alive)
                {
                    var captured = bundle;
                    Put(row, "assetCount",
                        delegate { return (JToken)captured.GetAllAssetNames().Length; });
                }
                else row["assetCount"] = null;
                a.Add(row);
            }
            // A declared mod bundle the registry never picked up is the typo'd-path
            // shape this verb exists to catch.
            foreach (KeyValuePair<string, string> declared in modPaths)
            {
                if (bundles.ContainsKey(declared.Key)) continue;
                var row = new JObject();
                row["name"] = declared.Key;
                row["source"] = "mod";
                row["loaded"] = false;
                row["assetCount"] = null;
                row["path"] = declared.Value;
                a.Add(row);
            }

            var o = new JObject();
            o["count"] = a.Count;
            o["returned"] = a.Count;
            o["truncated"] = false;
            o["bundles"] = a;
            return o;
        }

        static JToken OneBundle(Dictionary<string, UnityEngine.AssetBundle> bundles,
            string wanted, JObject args)
        {
            UnityEngine.AssetBundle bundle = FindBundle(bundles, wanted);
            int limit = Limit(args, MaxBundleAssets, MaxBundleAssetLimit);

            string[] assets;
            try { assets = bundle.GetAllAssetNames(); }
            catch (Exception e)
            {
                throw new VerbError("bundle '" + wanted + "' failed to list assets: "
                    + e.GetType().Name);
            }
            Array.Sort(assets, StringComparer.Ordinal);
            var a = new JArray();
            for (int i = 0; i < assets.Length && a.Count < limit; i++)
                a.Add(new JValue(assets[i]));
            var o = new JObject();
            o["name"] = wanted.ToLowerInvariant();
            AddPage(o, a, assets.Length, limit);
            o["assets"] = a;
            return o;
        }

        static UnityEngine.AssetBundle FindBundle(
            Dictionary<string, UnityEngine.AssetBundle> bundles, string wanted)
        {
            UnityEngine.AssetBundle bundle;
            if (!bundles.TryGetValue(wanted.ToLowerInvariant(), out bundle))
            {
                var names = new List<string>(bundles.Keys);
                names.Sort(StringComparer.Ordinal);
                if (names.Count > 40) names.RemoveRange(40, names.Count - 40);
                throw new VerbError("no bundle named '" + wanted + "' ("
                    + string.Join(", ", names.ToArray()) + ")");
            }
            bool alive = false;
            try { alive = bundle != null; }
            catch (Exception) { }
            if (!alive) throw new VerbError("bundle '" + wanted + "' is unloaded");
            return bundle;
        }

        static JToken AssetsResolve(JObject args)
        {
            string path = Str(args, "path");
            if (string.IsNullOrEmpty(path)) throw new VerbError("missing arg 'path'");
            int slash = path.IndexOf('/');
            if (slash <= 0 || slash == path.Length - 1)
                throw new VerbError("arg 'path' must be \"bundle/asset\"");
            string bundleName = path.Substring(0, slash).Trim();
            string assetName = path.Substring(slash + 1).Trim();

            Dictionary<string, UnityEngine.AssetBundle> bundles = LoadedBundles();
            UnityEngine.AssetBundle bundle = FindBundle(bundles, bundleName);

            UnityEngine.Object asset = Safe<UnityEngine.Object>(
                delegate { return bundle.LoadAsset<UnityEngine.Object>(assetName); }, null);
            var o = new JObject();
            o["path"] = bundleName.ToLowerInvariant() + "/" + assetName;
            bool alive = false;
            try { alive = asset != null; }
            catch (Exception) { }
            o["found"] = alive;
            if (!alive)
            {
                o["type"] = null;
                o["info"] = null;
                o["hint"] = AssetHints(bundle, assetName);
                return o;
            }
            o["type"] = asset.GetType().Name;
            o["info"] = AssetInfo(asset);
            return o;
        }

        // Asset names in a bundle are lowercased full paths ("assets/.../foo.png"), so a
        // miss on a bare name usually has a near match worth reporting.
        static JArray AssetHints(UnityEngine.AssetBundle bundle, string assetName)
        {
            var a = new JArray();
            try
            {
                string leaf = Path.GetFileNameWithoutExtension(assetName).ToLowerInvariant();
                if (leaf.Length == 0) return a;
                string[] assets = bundle.GetAllAssetNames();
                for (int i = 0; i < assets.Length && a.Count < 8; i++)
                {
                    if (assets[i].IndexOf(leaf, StringComparison.Ordinal) >= 0)
                        a.Add(new JValue(assets[i]));
                }
            }
            catch (Exception) { }
            return a;
        }

        static JToken AssetInfo(UnityEngine.Object asset)
        {
            var o = new JObject();
            Put(o, "name", delegate { return (JToken)asset.name; });

            var texture2D = asset as UnityEngine.Texture2D;
            if (texture2D != null)
            {
                Put(o, "width", delegate { return (JToken)texture2D.width; });
                Put(o, "height", delegate { return (JToken)texture2D.height; });
                Put(o, "format", delegate { return (JToken)texture2D.format.ToString(); });
                return o;
            }
            var texture = asset as UnityEngine.Texture;
            if (texture != null)
            {
                Put(o, "width", delegate { return (JToken)texture.width; });
                Put(o, "height", delegate { return (JToken)texture.height; });
                return o;
            }
            var sprite = asset as UnityEngine.Sprite;
            if (sprite != null)
            {
                Put(o, "width", delegate { return Num(sprite.rect.width); });
                Put(o, "height", delegate { return Num(sprite.rect.height); });
                Put(o, "texture", delegate
                {
                    return (JToken)(sprite.texture != null ? sprite.texture.name : null);
                });
                return o;
            }
            var clip = asset as UnityEngine.AudioClip;
            if (clip != null)
            {
                Put(o, "length", delegate { return Num(clip.length); });
                Put(o, "frequency", delegate { return (JToken)clip.frequency; });
                Put(o, "channels", delegate { return (JToken)clip.channels; });
                return o;
            }
            var mesh = asset as UnityEngine.Mesh;
            if (mesh != null)
            {
                Put(o, "vertexCount", delegate { return (JToken)mesh.vertexCount; });
                Put(o, "subMeshCount", delegate { return (JToken)mesh.subMeshCount; });
                return o;
            }
            var gameObject = asset as UnityEngine.GameObject;
            if (gameObject != null)
            {
                Put(o, "children", delegate { return (JToken)gameObject.transform.childCount; });
                Put(o, "components", delegate
                {
                    return (JToken)gameObject.GetComponents<UnityEngine.Component>().Length;
                });
                return o;
            }
            var material = asset as UnityEngine.Material;
            if (material != null)
            {
                Put(o, "shader", delegate
                {
                    return (JToken)(material.shader != null ? material.shader.name : null);
                });
                return o;
            }
            return o;
        }

        #endregion

        #region query.designs / query.councilors / query.nations

        static JToken QueryDesigns(JObject args)
        {
            TIFactionState faction = Arg<TIFactionState>(args, "faction");
            int limit = Limit(args, MaxListRows, MaxListRowLimit);
            List<TISpaceShipTemplate> designs = Safe<List<TISpaceShipTemplate>>(
                delegate { return faction.shipDesigns; }, null);

            var a = new JArray();
            int total = 0;
            if (designs != null)
            {
                for (int i = 0; i < designs.Count; i++)
                {
                    TISpaceShipTemplate d = designs[i];
                    if (d == null) continue;
                    total++;
                    if (a.Count >= limit) continue;
                    var row = new JObject();
                    row["dataName"] = d.dataName;
                    row["displayName"] = DesignName(d);
                    Put(row, "hull", delegate { return (JToken)d.hullName; });
                    Put(row, "drive", delegate { return (JToken)d.driveName; });
                    row["weapons"] = Weapons(d);
                    row["weaponNames"] = WeaponNames(d);
                    Put(row, "refitIteration", delegate { return (JToken)d.refitIteration; });
                    a.Add(row);
                }
            }

            var o = new JObject();
            o["faction"] = Describe(faction);
            AddPage(o, a, total, limit);
            o["designs"] = a;
            return o;
        }

        static JArray WeaponNames(TISpaceShipTemplate d)
        {
            var a = new JArray();
            AppendModuleNames(a, Safe<List<ModuleDataTemplateEntry>>(
                delegate { return d.noseWeaponTemplateEntries; }, null));
            AppendModuleNames(a, Safe<List<ModuleDataTemplateEntry>>(
                delegate { return d.hullWeaponTemplateEntries; }, null));
            return a;
        }

        static void AppendModuleNames(JArray a, List<ModuleDataTemplateEntry> entries)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                string name = entries[i].moduleName;
                if (!string.IsNullOrEmpty(name)) a.Add(new JValue(name));
            }
        }

        static JToken QueryCouncilors(JObject args)
        {
            int factionFilter = OptionalInt(args, "faction");
            int limit = Limit(args, MaxListRows, MaxListRowLimit);
            var a = new JArray();
            int total = 0;
            foreach (TICouncilorState councilor in GameStateManager.IterateByClass<TICouncilorState>(false))
            {
                if (councilor == null) continue;
                TIFactionState owner = FactionOf(councilor);
                if (factionFilter >= 0 && (owner == null || (int)owner.ID != factionFilter)) continue;
                total++;
                if (a.Count >= limit) continue;
                var o = new JObject();
                o["id"] = (int)councilor.ID;
                o["name"] = StateName(councilor);
                o["faction"] = owner != null ? Describe(owner) : null;
                Put(o, "type", delegate { return (JToken)councilor.typeTemplateName; });
                Put(o, "status", delegate { return (JToken)councilor.status.ToString(); });
                JToken location = null;
                try
                {
                    TIGameState at = councilor.location;
                    if (at != null) location = Describe(at);
                }
                catch (Exception) { }
                o["location"] = location;
                Put(o, "missionNames",
                    delegate { return Strings(councilor.learnedMissionsTemplateNames); });
                a.Add(o);
            }
            var result = new JObject();
            AddPage(result, a, total, limit);
            result["councilors"] = a;
            return result;
        }

        static JToken QueryNations(JObject args)
        {
            string contains = Str(args, "contains");
            int limit = Limit(args, MaxListRows, MaxListRowLimit);
            var keys = new List<string>();
            var rows = new List<JObject>();
            foreach (TINationState nation in GameStateManager.IterateByClass<TINationState>(false))
            {
                if (nation == null) continue;
                string name = StateName(nation);
                if (!string.IsNullOrEmpty(contains))
                {
                    if (name == null
                        || name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }
                var o = new JObject();
                o["id"] = (int)nation.ID;
                o["name"] = name;
                Put(o, "regionCount", delegate
                {
                    List<TIRegionState> regions = nation.regions;
                    return (JToken)(regions != null ? regions.Count : 0);
                });
                o["controlPoints"] = ControlPoints(nation);
                TIFactionState executive = Safe<TIFactionState>(delegate
                {
                    TIControlPoint point = nation.executiveControlPoint;
                    return point != null ? point.faction : null;
                }, null);
                o["executive"] = executive != null ? Describe(executive) : null;
                keys.Add((name != null ? name : "") + "#" + (int)nation.ID);
                rows.Add(o);
            }

            JArray a = SortedPage(keys, rows, limit);
            var result = new JObject();
            AddPage(result, a, rows.Count, limit);
            result["nations"] = a;
            return result;
        }

        static JArray ControlPoints(TINationState nation)
        {
            var a = new JArray();
            List<TIControlPoint> points = Safe<List<TIControlPoint>>(
                delegate { return nation.controlPoints; }, null);
            if (points == null) return a;
            for (int i = 0; i < points.Count; i++)
            {
                TIControlPoint point = points[i];
                if (point == null) continue;
                var o = new JObject();
                TIFactionState owner = Safe<TIFactionState>(
                    delegate { return point.faction; }, null);
                o["faction"] = owner != null ? Describe(owner) : null;
                Put(o, "type", delegate { return (JToken)point.controlPointType.ToString(); });
                Put(o, "executive", delegate { return (JToken)point.executive; });
                Put(o, "defended", delegate { return (JToken)point.defended; });
                a.Add(o);
            }
            return a;
        }

        #endregion
    }
}
