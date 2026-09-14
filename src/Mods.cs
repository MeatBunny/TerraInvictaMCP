using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.Modding;
using UnityModManagerNet;

namespace TerraInvictaMCP
{
    // mods.list, query.template, query.enums, harmony.patches.
    public static partial class Verbs
    {
        // A mod file that restates a whole vanilla array carries hundreds of names; the
        // count is always reported, the names themselves only up to here.
        const int MaxModDataNames = 100;

        // Template classes run to ~2000 entries, so a name listing pages.
        const int MaxTemplateNames = 500;
        const int MaxTemplateNameLimit = 5000;
        const int MaxTemplateErrors = 20;

        // One serialized template rides in one response line. Well under the 1 MB the
        // reader accepts, so a client that echoes a response back stays inside the cap.
        const int MaxTemplateChars = 512 * 1024;

        const int MaxHarmonyMethods = 200;
        const int MaxHarmonyMethodLimit = 2000;

        static JToken ModsList(JObject args)
        {
            var o = new JObject();
            // JSON and localization mods are ignored outright when this is off, so it is
            // the first thing to check when a template mod appears to have done nothing.
            Put(o, "useMods", delegate { return (JToken)TIPlayerProfileManager.useMods; });
            o["dll"] = DllMods();
            o["loaders"] = Loaders();
            o["templates"] = TemplateMods();
            return o;
        }

        // Every mod loader that could be feeding code into this process. UMM is the one
        // the bridge itself runs under; MonoMod and MelonLoader are detected by their
        // marker assemblies in the AppDomain plus their telltale files under the game
        // directory, because a loader that failed to bootstrap leaves only the files.
        static JToken Loaders()
        {
            var o = new JObject();

            var umm = new JObject();
            bool ummUp = Safe<bool>(delegate { return UnityModManager.modEntries != null; }, false);
            umm["present"] = ummUp;
            Put(umm, "version", delegate
            {
                return (JToken)typeof(UnityModManager).Assembly.GetName().Version.ToString();
            });
            o["UMM"] = umm;

            string root = Safe<string>(delegate
            {
                return Path.GetDirectoryName(UnityEngine.Application.dataPath);
            }, null);
            string managed = Safe<string>(delegate
            {
                return Path.Combine(UnityEngine.Application.dataPath, "Managed");
            }, null);

            var melonMarkers = new JArray();
            string melonVersion = AssemblyMarker("MelonLoader", melonMarkers);
            if (root != null)
            {
                FileMarker(melonMarkers, Path.Combine(root, "MelonLoader"), true);
                FileMarker(melonMarkers, Path.Combine(root, "version.dll"), false);
                FileMarker(melonMarkers, Path.Combine(root, "dobby.dll"), false);
                FileMarker(melonMarkers, Path.Combine(root, "doorstop_config.ini"), false);
            }
            var melon = new JObject();
            melon["present"] = melonVersion != null;
            melon["version"] = melonVersion;
            melon["markers"] = melonMarkers;
            o["MelonLoader"] = melon;

            var monoMarkers = new JArray();
            string monoVersion = AssemblyMarker("MonoMod", monoMarkers);
            if (managed != null)
            {
                GlobMarker(monoMarkers, managed, "MonoMod*.dll");
                GlobMarker(monoMarkers, managed, "*.mm.dll");
            }
            var monoMod = new JObject();
            monoMod["present"] = monoVersion != null || monoMarkers.Count > 0;
            monoMod["version"] = monoVersion;
            monoMod["markers"] = monoMarkers;
            o["MonoMod"] = monoMod;

            return o;
        }

        // First loaded assembly whose simple name is or starts with the prefix; every
        // hit is logged as a marker, the first one's version is returned.
        static string AssemblyMarker(string prefix, JArray markers)
        {
            string version = null;
            try
            {
                Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < loaded.Length; i++)
                {
                    string name = null;
                    try { name = loaded[i].GetName().Name; }
                    catch (Exception) { }
                    if (name == null) continue;
                    if (!name.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                        && !name.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
                        continue;
                    markers.Add(new JValue("assembly:" + name));
                    if (version == null)
                    {
                        try { version = loaded[i].GetName().Version.ToString(); }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return version;
        }

        static void FileMarker(JArray markers, string path, bool directory)
        {
            try
            {
                bool found = directory ? Directory.Exists(path) : File.Exists(path);
                if (found) markers.Add(new JValue("file:" + Path.GetFileName(path)));
            }
            catch (Exception) { }
        }

        static void GlobMarker(JArray markers, string folder, string pattern)
        {
            try
            {
                string[] hits = Directory.GetFiles(folder, pattern);
                Array.Sort(hits, StringComparer.Ordinal);
                for (int i = 0; i < hits.Length && markers.Count < 20; i++)
                    markers.Add(new JValue("file:" + Path.GetFileName(hits[i])));
            }
            catch (Exception) { }
        }

        static JToken DllMods()
        {
            var a = new JArray();
            List<UnityModManager.ModEntry> entries = null;
            try { entries = UnityModManager.modEntries; }
            catch (Exception) { }
            if (entries == null) return a;
            for (int i = 0; i < entries.Count; i++)
            {
                UnityModManager.ModEntry e = entries[i];
                if (e == null) continue;
                var o = new JObject();
                UnityModManager.ModInfo info =
                    Safe<UnityModManager.ModInfo>(delegate { return e.Info; }, null);
                o["id"] = info != null ? info.Id : null;
                o["displayName"] = info != null ? info.DisplayName : null;
                o["assembly"] = info != null ? info.AssemblyName : null;
                Put(o, "version", delegate { return (JToken)(e.Version != null ? e.Version.ToString() : null); });
                Put(o, "path", delegate { return (JToken)e.Path; });
                Put(o, "enabled", delegate { return (JToken)e.Enabled; });
                Put(o, "active", delegate { return (JToken)e.Active; });
                Put(o, "loaded", delegate { return (JToken)e.Loaded; });
                Put(o, "started", delegate { return (JToken)e.Started; });
                Put(o, "errorOnLoading", delegate { return (JToken)e.ErrorOnLoading; });
                Put(o, "hasAssembly", delegate { return (JToken)e.HasAssembly; });
                o["loader"] = "UMM";
                o["errors"] = UmmErrors(info != null ? info.Id : null);
                a.Add(o);
            }
            return a;
        }

        // UMM keeps its log in a static history list its UI tab reads; a mod's ModLogger
        // prefixes every line with "[<Id>]" and errors additionally with "[Error]", so
        // the per-mod error lines can be filtered back out. Reached by reflection: the
        // field is internal and its absence in another UMM build just means no errors.
        static readonly FieldInfo ummHistoryField = UmmHistoryField();

        static FieldInfo UmmHistoryField()
        {
            try
            {
                Type logger = typeof(UnityModManager).GetNestedType("Logger",
                    BindingFlags.Public | BindingFlags.NonPublic);
                if (logger == null) return null;
                return logger.GetField("history",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception) { return null; }
        }

        const int MaxUmmErrors = 10;

        static JArray UmmErrors(string id)
        {
            var a = new JArray();
            if (ummHistoryField == null || string.IsNullOrEmpty(id)) return a;
            IEnumerable lines = Safe<IEnumerable>(
                delegate { return ummHistoryField.GetValue(null) as IEnumerable; }, null);
            if (lines == null) return a;
            string tag = "[" + id + "]";
            try
            {
                foreach (object entry in lines)
                {
                    if (a.Count >= MaxUmmErrors) break;
                    string line = entry as string;
                    if (line == null) continue;
                    if (line.IndexOf("[Error]", StringComparison.Ordinal) < 0) continue;
                    if (line.IndexOf(tag, StringComparison.Ordinal) < 0) continue;
                    a.Add(new JValue(line));
                }
            }
            catch (Exception) { }
            return a;
        }

        static JToken TemplateMods()
        {
            var o = new JObject();
            // The game's own record, filled once at boot by GetEnabledModFiles. Calling
            // that method again would clear and rebuild these lists, so they are only read.
            o["enabled"] = ToArray(ModNames(true));
            o["disabled"] = ToArray(ModNames(false));
            o["files"] = JsonModFiles();

            string mods = ModsFolder();
            var folders = new JObject();
            folders["enabled"] = Folders(Path.Combine(mods, "Enabled"));
            folders["disabled"] = Folders(Path.Combine(mods, "Disabled"));
            o["folders"] = folders;
            return o;
        }

        static List<string> ModNames(bool enabled)
        {
            return Safe<List<string>>(
                delegate { return enabled ? ModManager.ModNames : ModManager.DisabledModNames; }, null);
        }

        static JToken JsonModFiles()
        {
            var a = new JArray();
            List<JsonMod> mods = null;
            try { mods = ModTemplateManager.jsonMods; }
            catch (Exception) { }
            if (mods == null) return a;
            for (int i = 0; i < mods.Count; i++)
            {
                JsonMod m = mods[i];
                if (m == null) continue;
                var o = new JObject();
                string path = Safe<string>(delegate { return m.ModFilePath; }, null);
                o["mod"] = ParentName(path);
                Put(o, "file", delegate { return (JToken)m.ModFileName; });
                o["path"] = path;
                Put(o, "target", delegate { return (JToken)m.TargetFilePath; });
                Put(o, "loadOrder", delegate { return (JToken)m.LoadOrder; });
                // False means the file names no vanilla template, which is the shape that
                // crashes the game at template load.
                Put(o, "matchedVanilla", delegate { return (JToken)m.foundVanillaMatch; });
                AddDataNames(o, m);
                a.Add(o);
            }
            return a;
        }

        static void AddDataNames(JObject o, JsonMod m)
        {
            List<string> names = null;
            try
            {
                HashSet<string> set = m.GetDataNames();
                if (set != null)
                {
                    names = new List<string>(set);
                    names.Sort(StringComparer.Ordinal);
                }
            }
            catch (Exception) { }
            if (names == null)
            {
                o["entries"] = null;
                o["dataNames"] = null;
                return;
            }
            o["entries"] = names.Count;
            o["dataNames"] = ToArray(names, MaxModDataNames);
            if (names.Count > MaxModDataNames) o["dataNamesTruncated"] = true;
        }

        // The game's loader addresses "Mods/Enabled" relative to the working directory;
        // deriving the same folder from dataPath survives a working directory that is not
        // the install root.
        static string ModsFolder()
        {
            try
            {
                string data = UnityEngine.Application.dataPath;
                if (!string.IsNullOrEmpty(data))
                {
                    string root = Path.GetDirectoryName(data);
                    if (!string.IsNullOrEmpty(root)) return Path.Combine(root, "Mods");
                }
            }
            catch (Exception) { }
            return "Mods";
        }

        static JArray Folders(string parent)
        {
            var a = new JArray();
            try
            {
                if (!Directory.Exists(parent)) return a;
                string[] dirs = Directory.GetDirectories(parent);
                Array.Sort(dirs, StringComparer.Ordinal);
                for (int i = 0; i < dirs.Length; i++) a.Add(new JValue(Path.GetFileName(dirs[i])));
            }
            catch (Exception) { }
            return a;
        }

        // A mod file path names the mod through its containing folder.
        static string ParentName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                string dir = Path.GetDirectoryName(path);
                return string.IsNullOrEmpty(dir) ? null : Path.GetFileName(dir);
            }
            catch (Exception) { return null; }
        }

        static JToken QueryTemplate(JObject args)
        {
            string wanted = Str(args, "type");
            if (string.IsNullOrEmpty(wanted)) throw new VerbError("missing arg 'type'");
            wanted = wanted.Trim();
            Type type = TemplateType(wanted);
            if (type == null) throw new VerbError("no template class named '" + wanted + "'");

            if (!TemplatesLoaded()) throw new VerbError("templates are not loaded yet");
            TIDataTemplate[] all = AllTemplates(type);

            string dataName = Str(args, "dataName");
            if (!string.IsNullOrEmpty(dataName)) return OneTemplate(type, all, dataName.Trim());

            string contains = Str(args, "contains");
            int offset = OptionalInt(args, "offset");
            if (offset < 0) offset = 0;

            // Bulk mode: named members projected across every matching entry in one call.
            JToken fieldsToken = args != null ? args["fields"] : null;
            if (fieldsToken != null && fieldsToken.Type != JTokenType.Null)
            {
                List<string> fields = StringList(fieldsToken, MaxFieldNames, "fields");
                if (fields.Count > 0)
                    return TemplateFields(type, all, contains, fields, args, offset);
            }

            int limit = Limit(args, MaxTemplateNames, MaxTemplateNameLimit);

            var matched = new List<string>();
            for (int i = 0; i < all.Length; i++)
            {
                string name = TemplateName(all[i]);
                if (name == null) continue;
                if (!string.IsNullOrEmpty(contains)
                    && name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matched.Add(name);
            }
            matched.Sort(StringComparer.Ordinal);

            var a = new JArray();
            for (int i = offset; i < matched.Count && a.Count < limit; i++)
                a.Add(new JValue(matched[i]));
            var o = new JObject();
            o["type"] = type.Name;
            // count stays the class total; matched is the filtered list the page walks.
            o["count"] = all.Length;
            o["matched"] = matched.Count;
            o["offset"] = offset;
            o["returned"] = a.Count;
            o["truncated"] = offset + a.Count < matched.Count;
            o["dataNames"] = a;
            return o;
        }

        // Bulk mode never projects more than this many entries or members per call; the
        // per-member budget in MemberToken bounds each cell on top.
        const int MaxFieldEntries = 200;
        const int MaxFieldEntriesLimit = 2000;
        const int MaxFieldNames = 32;

        static JToken TemplateFields(Type type, TIDataTemplate[] all, string contains,
            List<string> fields, JObject args, int offset)
        {
            int limit = Limit(args, MaxFieldEntries, MaxFieldEntriesLimit);

            // Sorted by data name so offset paging is stable across calls.
            var matched = new List<TIDataTemplate>();
            for (int i = 0; i < all.Length; i++)
            {
                string name = TemplateName(all[i]);
                if (name == null) continue;
                if (!string.IsNullOrEmpty(contains)
                    && name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matched.Add(all[i]);
            }
            matched.Sort(delegate(TIDataTemplate x, TIDataTemplate y)
            {
                return string.CompareOrdinal(TemplateName(x), TemplateName(y));
            });

            var errors = new List<string>();
            var missing = new Dictionary<string, bool>(StringComparer.Ordinal);
            var entries = new JArray();
            JsonSerializer serializer = templateSerializer;
            for (int i = offset; i < matched.Count && entries.Count < limit; i++)
            {
                TIDataTemplate entry = matched[i];
                string entryName = TemplateName(entry);
                var row = new JObject();
                row["dataName"] = entryName;
                Dictionary<string, MemberInfo> map = MemberMap(entry.GetType());
                for (int f = 0; f < fields.Count; f++)
                {
                    string field = fields[f];
                    MemberInfo member;
                    if (!map.TryGetValue(field, out member))
                    {
                        row[field] = JValue.CreateNull();
                        if (!missing.ContainsKey(field))
                        {
                            missing[field] = true;
                            if (errors.Count < MaxTemplateErrors)
                                errors.Add(field + ": no such member on " + entry.GetType().Name);
                        }
                        continue;
                    }
                    // Every read goes through the guarded path: a throwing getter or a
                    // graph past the serialization budget nulls the cell, never the call.
                    FieldInfo fieldInfo = member as FieldInfo;
                    object value;
                    try
                    {
                        value = fieldInfo != null ? fieldInfo.GetValue(entry)
                            : ((PropertyInfo)member).GetValue(entry, null);
                    }
                    catch (Exception e)
                    {
                        Record(errors, entryName + "." + field, e);
                        row[field] = JValue.CreateNull();
                        continue;
                    }
                    row[field] = MemberToken(value, serializer, errors, entryName + "." + field);
                }
                entries.Add(row);
            }

            // Serialized once for the size check and reused verbatim in the response.
            string text = entries.ToString(Formatting.None);
            if (text.Length > MaxTemplateChars)
                throw new VerbError("projection over the " + MaxTemplateChars
                    + " character response budget; lower 'limit' or drop fields");

            var o = new JObject();
            o["type"] = type.Name;
            var names = new JArray();
            for (int i = 0; i < fields.Count; i++) names.Add(new JValue(fields[i]));
            o["fields"] = names;
            o["count"] = matched.Count;
            o["offset"] = offset;
            o["returned"] = entries.Count;
            o["truncated"] = offset + entries.Count < matched.Count;
            o["entries"] = new JRaw(text);
            o["errors"] = ToArray(errors);
            return o;
        }

        // Member lookup per runtime type, cached: bulk calls touch the same class for
        // hundreds of entries. Names are case-insensitive; the JSON field names in the
        // template files match the member names exactly anyway.
        static readonly Dictionary<Type, Dictionary<string, MemberInfo>> memberMaps =
            new Dictionary<Type, Dictionary<string, MemberInfo>>();

        static Dictionary<string, MemberInfo> MemberMap(Type type)
        {
            Dictionary<string, MemberInfo> map;
            if (memberMaps.TryGetValue(type, out map)) return map;
            map = new Dictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase);
            List<MemberInfo> members = ReadableMembers(type);
            for (int i = 0; i < members.Count; i++)
            {
                MemberInfo m = members[i];
                if (Ignored(m)) continue;
                PropertyInfo p = m as PropertyInfo;
                if (p != null && p.GetGetMethod() == null) continue;
                if (!map.ContainsKey(m.Name)) map[m.Name] = m;
            }
            memberMaps[type] = map;
            return map;
        }

        // Rows keyed for a stable ordinal order, then the first page of them.
        static JArray SortedPage(List<string> keys, List<JObject> rows, int limit)
        {
            string[] keyArray = keys.ToArray();
            JObject[] rowArray = rows.ToArray();
            Array.Sort(keyArray, rowArray, StringComparer.Ordinal);
            var a = new JArray();
            int n = rowArray.Length < limit ? rowArray.Length : limit;
            for (int i = 0; i < n; i++) a.Add(rowArray[i]);
            return a;
        }

        // The paging triple every list verb reports: how many candidates exist, how
        // many rode along, and whether the limit cut the list.
        static void AddPage(JObject o, JArray items, int total, int limit)
        {
            o["count"] = total;
            o["returned"] = items.Count;
            o["truncated"] = total > limit;
        }

        static JToken OneTemplate(Type type, TIDataTemplate[] all, string dataName)
        {
            TIDataTemplate found = null;
            for (int i = 0; i < all.Length; i++)
            {
                if (Same(TemplateName(all[i]), dataName)) { found = all[i]; break; }
            }
            if (found == null)
                throw new VerbError("no " + type.Name + " named '" + dataName + "'");

            var errors = new List<string>();
            JToken body = SerializeTemplate(found, errors);
            var o = new JObject();
            o["type"] = found.GetType().Name;
            o["dataName"] = TemplateName(found);
            o["template"] = body;
            o["errors"] = ToArray(errors);
            return o;
        }

        static bool TemplatesLoaded()
        {
            return Safe<bool>(
                delegate { return TemplateManager.self != null && TemplateManager.self.Initialized; },
                false);
        }

        static string TemplateName(TIDataTemplate template)
        {
            if (template == null) return null;
            return Safe<string>(delegate { return template.dataName; }, null);
        }

        // Resolved type names are cached, misses included: the fallback scan walks every
        // type in every loaded assembly and must not run twice for the same bad name.
        static readonly Dictionary<string, Type> templateTypes =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        static Type TemplateType(string wanted)
        {
            Type cached;
            if (templateTypes.TryGetValue(wanted, out cached)) return cached;
            Type found = FindTemplateType(wanted);
            templateTypes[wanted] = found;
            return found;
        }

        static Type FindTemplateType(string wanted)
        {
            // Template classes sit in the global namespace, so the bare name is tried
            // first; the TI prefix and the Template suffix are both optional in the arg.
            string[] variants = new string[] {
                wanted, "TI" + wanted, wanted + "Template", "TI" + wanted + "Template"
            };
            Assembly game = typeof(TIDataTemplate).Assembly;
            for (int i = 0; i < variants.Length; i++)
            {
                Type t = NamedType(game, variants[i]);
                if (t != null) return t;
            }
            return ScanForTemplateType(variants);
        }

        static Type NamedType(Assembly assembly, string name)
        {
            Type t = LoadType(assembly, name);
            if (t == null) t = LoadType(assembly, "PavonisInteractive.TerraInvicta." + name);
            return IsTemplateType(t) ? t : null;
        }

        static Type LoadType(Assembly assembly, string name)
        {
            return Safe<Type>(delegate { return assembly.GetType(name, false, true); }, null);
        }

        // A template class added by a DLL mod lives in that mod's assembly, so a miss in
        // Assembly-CSharp falls back to a by-name sweep of everything loaded.
        static Type ScanForTemplateType(string[] variants)
        {
            return SweepTypes(delegate(Type t)
            {
                if (!IsTemplateType(t)) return false;
                for (int k = 0; k < variants.Length; k++)
                {
                    if (string.Equals(t.Name, variants[k], StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            });
        }

        // Abstract bases are kept: they hold no entries of their own but answer for their
        // subclasses. An open generic cannot be bound and is not a template class.
        static bool IsTemplateType(Type t)
        {
            return t != null && !t.ContainsGenericParameters
                && typeof(TIDataTemplate).IsAssignableFrom(t);
        }

        static readonly MethodInfo getAllTemplates = GetAllTemplatesMethod();

        static MethodInfo GetAllTemplatesMethod()
        {
            try
            {
                return typeof(TemplateManager).GetMethod("GetAllTemplates",
                    BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception) { return null; }
        }

        // GetAllTemplates<T> keys off the exact class, which is the 1:1 match for one
        // template JSON file. A class whose entries all belong to subclasses answers
        // nothing that way, so an empty exact answer retries with subclasses included.
        static TIDataTemplate[] AllTemplates(Type type)
        {
            if (getAllTemplates == null) throw new VerbError("no GetAllTemplates on TemplateManager");
            MethodInfo bound;
            try { bound = getAllTemplates.MakeGenericMethod(type); }
            catch (Exception) { throw new VerbError("'" + type.Name + "' is not a template class"); }
            TIDataTemplate[] exact = CallGetAllTemplates(bound, false);
            return exact.Length > 0 ? exact : CallGetAllTemplates(bound, true);
        }

        static TIDataTemplate[] CallGetAllTemplates(MethodInfo bound, bool allowChild)
        {
            object result;
            try { result = bound.Invoke(null, new object[] { allowChild }); }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                throw new VerbError("template lookup failed: " + inner.GetType().Name);
            }
            catch (Exception e)
            {
                throw new VerbError("template lookup failed: " + e.GetType().Name);
            }
            // Array covariance: a TITechTemplate[] is a TIDataTemplate[].
            var array = result as TIDataTemplate[];
            return array != null ? array : new TIDataTemplate[0];
        }

        // Newtonsoft over a live template. Any template, game state, Unity asset or
        // System.Type reached anywhere in the member graph collapses to a short form by
        // RUNTIME type: converters registered on the serializer see every value, so a ref
        // hiding behind an object member, an interface, or a nested collection collapses
        // the same as a plainly declared one. Collapsing by declared member type left
        // those paths open, and one nation entry expanded the nation-region-nation graph
        // until the main thread never came back. The root's members are walked by hand
        // because the root is itself a template and a serializer-level converter would
        // collapse it to its data name. Loops are dropped, and a member whose getter
        // throws is recorded rather than failing the whole response.
        static JToken SerializeTemplate(TIDataTemplate template, List<string> errors)
        {
            JsonSerializer serializer = templateSerializer;
            EventHandler<Newtonsoft.Json.Serialization.ErrorEventArgs> onError =
                delegate(object sender, Newtonsoft.Json.Serialization.ErrorEventArgs ev)
            {
                if (errors.Count < MaxTemplateErrors)
                {
                    string path = ev.ErrorContext.Path;
                    errors.Add((string.IsNullOrEmpty(path) ? "<root>" : path)
                        + ": " + ev.ErrorContext.Error.GetType().Name);
                }
                ev.ErrorContext.Handled = true;
            };

            var body = new JObject();
            // The serializer is shared; only the per-call error sink rides along, and
            // it is detached before the next call can attach its own.
            serializer.Error += onError;
            try
            {
                Type type = template.GetType();
                List<MemberInfo> list = ReadableMembers(type);
                for (int i = 0; i < list.Count; i++)
                {
                    MemberInfo member = list[i];
                    if (Ignored(member)) continue;
                    FieldInfo field = member as FieldInfo;
                    object value;
                    try
                    {
                        value = field != null ? field.GetValue(template)
                            : ((PropertyInfo)member).GetValue(template, null);
                    }
                    catch (Exception e) { Record(errors, member.Name, e); continue; }
                    body[member.Name] = MemberToken(value, serializer, errors, member.Name);
                }
            }
            finally { serializer.Error -= onError; }

            // Serialized once: the size check and the response reuse the same string,
            // written through the envelope verbatim.
            string text = body.ToString(Formatting.None);
            if (text.Length > MaxTemplateChars)
                throw new VerbError("template '" + TemplateName(template)
                    + "' is larger than the " + MaxTemplateChars + " character response budget");
            return new JRaw(text);
        }

        static bool Ignored(MemberInfo member)
        {
            return Safe<bool>(
                delegate { return member.IsDefined(typeof(JsonIgnoreAttribute), true); }, false);
        }

        // One member gets a bounded serialization: a hard cap on nesting depth and on
        // container count. The nation templates proved that a graph can be expensive
        // without ever cycling (fresh objects defeat the reference-loop check), so the
        // only reliable guard is a budget that fails the member, not the response.
        const int MaxMemberDepth = 12;
        const int MaxMemberNodes = 20000;

        static JToken MemberToken(object value, JsonSerializer serializer,
            List<string> errors, string name)
        {
            if (value == null) return JValue.CreateNull();
            var writer = new BudgetTokenWriter();
            try
            {
                serializer.Serialize(writer, value);
                return writer.Token != null ? writer.Token : JValue.CreateNull();
            }
            catch (BudgetError)
            {
                if (errors.Count < MaxTemplateErrors)
                    errors.Add(name + ": over serialization budget, omitted");
                return JValue.CreateNull();
            }
            catch (Exception e)
            {
                Record(errors, name, e);
                return JValue.CreateNull();
            }
        }

        class BudgetError : Exception { }

        class BudgetTokenWriter : JTokenWriter
        {
            int depth;
            int nodes;

            void Enter()
            {
                if (++depth > MaxMemberDepth || ++nodes > MaxMemberNodes)
                    throw new BudgetError();
            }

            public override void WriteStartObject() { Enter(); base.WriteStartObject(); }
            public override void WriteStartArray() { Enter(); base.WriteStartArray(); }
            public override void WriteEndObject() { depth--; base.WriteEndObject(); }
            public override void WriteEndArray() { depth--; base.WriteEndArray(); }
        }

        static void Record(List<string> errors, string name, Exception e)
        {
            Exception inner = e is TargetInvocationException && e.InnerException != null
                ? e.InnerException : e;
            if (errors.Count < MaxTemplateErrors)
                errors.Add(name + ": " + inner.GetType().Name);
        }

        static readonly JsonSerializer templateSerializer = BuildTemplateSerializer();

        static JsonSerializer BuildTemplateSerializer()
        {
            var serializer = new JsonSerializer();
            serializer.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            // Templates are full of enums, and a bare ordinal says nothing to a client
            // reading the response against the vanilla JSON.
            serializer.Converters.Add(new Newtonsoft.Json.Converters.StringEnumConverter());
            serializer.Converters.Add(new TemplateRefConverter());
            serializer.Converters.Add(new GameStateRefConverter());
            serializer.Converters.Add(new UnityRefConverter());
            // Order matters: System.Type is a MemberInfo, and the first matching
            // converter wins.
            serializer.Converters.Add(new TypeRefConverter());
            serializer.Converters.Add(new MemberInfoRefConverter());
            serializer.Converters.Add(new DelegateRefConverter());
            serializer.Converters.Add(new ColorConverter());
            return serializer;
        }

        abstract class WriteOnlyConverter : JsonConverter
        {
            public override bool CanRead { get { return false; } }

            public override object ReadJson(JsonReader reader, Type objectType,
                object existingValue, JsonSerializer serializer)
            {
                throw new NotSupportedException();
            }
        }

        class TemplateRefConverter : WriteOnlyConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return typeof(TIDataTemplate).IsAssignableFrom(objectType);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                writer.WriteValue(TemplateName(value as TIDataTemplate));
            }
        }

        class GameStateRefConverter : WriteOnlyConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return typeof(TIGameState).IsAssignableFrom(objectType);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var state = value as TIGameState;
                if (state == null) { writer.WriteNull(); return; }
                Describe(state).WriteTo(writer);
            }
        }

        class UnityRefConverter : WriteOnlyConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return typeof(UnityEngine.Object).IsAssignableFrom(objectType);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var asset = value as UnityEngine.Object;
                // A destroyed asset compares equal to null through Unity's operator and
                // throws on name, so both go through the same guard.
                string name = null;
                try { if (asset != null) name = asset.name; }
                catch (Exception) { }
                writer.WriteValue(name);
            }
        }

        class TypeRefConverter : WriteOnlyConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return typeof(Type).IsAssignableFrom(objectType);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var t = value as Type;
                writer.WriteValue(t != null ? t.FullName : null);
            }
        }

        // Reflection objects and delegates surface through policy and callback members.
        // Expanded, a single MethodInfo drags in modules and assemblies; collapsed, it
        // is a name.
        class MemberInfoRefConverter : WriteOnlyConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return typeof(MemberInfo).IsAssignableFrom(objectType);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var m = value as MemberInfo;
                writer.WriteValue(m != null ? m.Name : null);
            }
        }

        // Color.linear and Color.gamma each return a new Color, so the default
        // serialization is an endless fresh-instance tree that no loop check catches.
        // This was the member that hung the nation templates.
        class ColorConverter : WriteOnlyConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(UnityEngine.Color)
                    || objectType == typeof(UnityEngine.Color32);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                UnityEngine.Color c;
                if (value is UnityEngine.Color32) c = (UnityEngine.Color32)value;
                else if (value is UnityEngine.Color) c = (UnityEngine.Color)value;
                else { writer.WriteNull(); return; }
                var o = new JObject();
                o["r"] = c.r;
                o["g"] = c.g;
                o["b"] = c.b;
                o["a"] = c.a;
                o.WriteTo(writer);
            }
        }

        class DelegateRefConverter : WriteOnlyConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return typeof(Delegate).IsAssignableFrom(objectType);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var d = value as Delegate;
                string name = null;
                try { if (d != null && d.Method != null) name = d.Method.Name; }
                catch (Exception) { }
                writer.WriteValue(name);
            }
        }

        // Harmony is linked (Main.cs constructs one), so it is called directly.
        static JToken HarmonyPatches(JObject args)
        {
            var o = new JObject();
            o["harmony"] = typeof(Harmony).Assembly.GetName().Version.ToString();

            string owner = Str(args, "owner");
            int limit = Limit(args, MaxHarmonyMethods, MaxHarmonyMethodLimit);

            var keys = new List<string>();
            var rows = new List<JObject>();
            foreach (MethodBase method in Harmony.GetAllPatchedMethods())
            {
                if (method == null) continue;
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null) continue;

                var kinds = new JArray[] {
                    Owners(info.Prefixes), Owners(info.Postfixes),
                    Owners(info.Transpilers), Owners(info.Finalizers)
                };
                bool wanted = string.IsNullOrEmpty(owner);
                for (int i = 0; i < kinds.Length && !wanted; i++)
                    wanted = HasOwner(kinds[i], owner);
                if (!wanted) continue;

                string declaring = null;
                try
                {
                    if (method.DeclaringType != null) declaring = method.DeclaringType.FullName;
                }
                catch (Exception) { }

                var row = new JObject();
                row["type"] = declaring;
                row["method"] = method.Name;
                row["prefixes"] = kinds[0];
                row["postfixes"] = kinds[1];
                row["transpilers"] = kinds[2];
                row["finalizers"] = kinds[3];
                keys.Add(declaring + "." + method.Name);
                rows.Add(row);
            }

            JArray a = SortedPage(keys, rows, limit);
            AddPage(o, a, rows.Count, limit);
            o["methods"] = a;
            return o;
        }

        static JArray Owners(IEnumerable<Patch> patches)
        {
            var a = new JArray();
            if (patches == null) return a;
            foreach (Patch patch in patches)
            {
                if (patch != null) a.Add(new JValue(patch.owner));
            }
            return a;
        }

        // Substring match: harmony ids are dotted and a client rarely has the whole one.
        static bool HasOwner(JArray owners, string wanted)
        {
            for (int i = 0; i < owners.Count; i++)
            {
                string id = owners[i].Type == JTokenType.String ? (string)owners[i] : null;
                if (id != null && id.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        // The whole enum universe of the game assembly, plus the enum-typed fields of
        // every template class. A client validating mod template data needs both: the
        // legal member names and ordinals of an enum, and which enum a given field on a
        // given template class actually holds.
        //
        // Cached as the serialized response text, one string per scope: the loaded
        // assembly cannot change inside a process, and the build costs a full GetTypes()
        // plus a field walk over every template class.
        static string enumsAllJson;
        static string enumsTemplatesJson;

        static JToken QueryEnums(JObject args)
        {
            string scope = Str(args, "scope");
            scope = string.IsNullOrEmpty(scope) ? "all" : scope.Trim().ToLowerInvariant();
            bool templatesOnly;
            if (scope == "all") templatesOnly = false;
            else if (scope == "templates") templatesOnly = true;
            else throw new VerbError("arg 'scope' must be 'all' or 'templates'");

            string cached = templatesOnly ? enumsTemplatesJson : enumsAllJson;
            if (cached != null) return new JRaw(cached);

            string text = BuildEnumTable(scope, templatesOnly);
            if (templatesOnly) enumsTemplatesJson = text;
            else enumsAllJson = text;
            return new JRaw(text);
        }

        static string BuildEnumTable(string scope, bool templatesOnly)
        {
            var errors = new List<string>();
            Assembly game = typeof(TIGameState).Assembly;

            // Another DLL mod in the process can leave a type unresolvable, which makes
            // an unguarded GetTypes() throw for the whole assembly. The partial list the
            // exception carries is the answer; its null entries are the broken types.
            Type[] types;
            try { types = game.GetTypes(); }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
                // The client cannot tell a pruned table from a complete one, so say
                // how much of the assembly went missing.
                int lost = 0;
                if (e.Types != null)
                {
                    for (int i = 0; i < e.Types.Length; i++) if (e.Types[i] == null) lost++;
                }
                errors.Add("Assembly.GetTypes: " + lost
                    + " type(s) did not load; enums and template fields they declare "
                    + "are missing from this table");
            }
            catch (Exception e)
            {
                throw new VerbError("assembly enumeration failed: " + e.GetType().Name);
            }
            if (types == null) types = new Type[0];

            // Keyed by simple type name throughout: that is what a template JSON value
            // and a field's declared type are both read as.
            var enums = new Dictionary<string, Dictionary<string, JToken>>(StringComparer.Ordinal);
            var sources = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var classes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t == null) continue;
                if (t.IsEnum) { CollectEnum(t, enums, sources, errors); continue; }
                if (IsTemplateType(t)) CollectEnumFields(t, classes, errors);
            }

            // The escape hatch: only the enums some template field can actually hold.
            if (templatesOnly)
            {
                var used = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, Dictionary<string, string>> c in classes)
                {
                    foreach (KeyValuePair<string, string> f in c.Value) used[f.Value] = true;
                }
                var all = new List<string>(enums.Keys);
                for (int i = 0; i < all.Count; i++)
                {
                    if (used.ContainsKey(all[i])) continue;
                    enums.Remove(all[i]);
                    sources.Remove(all[i]);
                }
            }

            var enumNames = new List<string>(enums.Keys);
            enumNames.Sort(StringComparer.Ordinal);
            var enumsJson = new JObject();
            var collisions = new JObject();
            int memberCount = 0;
            for (int i = 0; i < enumNames.Count; i++)
            {
                string name = enumNames[i];
                Dictionary<string, JToken> members = enums[name];
                var memberNames = new List<string>(members.Keys);
                memberNames.Sort(StringComparer.Ordinal);
                var o = new JObject();
                for (int j = 0; j < memberNames.Count; j++) o[memberNames[j]] = members[memberNames[j]];
                enumsJson[name] = o;
                memberCount += memberNames.Count;

                List<string> from;
                if (!sources.TryGetValue(name, out from) || from.Count < 2) continue;
                from.Sort(StringComparer.Ordinal);
                collisions[name] = ToArray(from);
            }

            var classNames = new List<string>(classes.Keys);
            classNames.Sort(StringComparer.Ordinal);
            var classesJson = new JObject();
            int fieldCount = 0;
            for (int i = 0; i < classNames.Count; i++)
            {
                Dictionary<string, string> map = classes[classNames[i]];
                var fieldNames = new List<string>(map.Keys);
                fieldNames.Sort(StringComparer.Ordinal);
                var o = new JObject();
                for (int j = 0; j < fieldNames.Count; j++) o[fieldNames[j]] = map[fieldNames[j]];
                classesJson[classNames[i]] = o;
                fieldCount += fieldNames.Count;
            }

            var response = new JObject();
            response["gameVersion"] = Safe<string>(
                delegate { return UnityEngine.Application.version; }, null);
            response["assembly"] = Safe<string>(delegate { return game.GetName().Name; }, null);
            response["scope"] = scope;
            response["enums"] = enumsJson;
            response["collisions"] = collisions;
            response["classes"] = classesJson;
            response["enumCount"] = enumsJson.Count;
            response["memberCount"] = memberCount;
            response["classCount"] = classesJson.Count;
            response["fieldCount"] = fieldCount;
            response["errors"] = ToArray(errors);

            // Serialized once: the size check and the cached response are the same text.
            string text = response.ToString(Formatting.None);
            if (text.Length > MaxTemplateChars)
                throw new VerbError("enum table is larger than the " + MaxTemplateChars
                    + " character response budget; call with scope 'templates'");
            return text;
        }

        // Members through the literal fields rather than Enum.GetNames/GetValues: the
        // raw constant is the underlying primitive with no boxed round trip, and it is
        // the same thing the .field literal lines of a disassembly carry.
        static void CollectEnum(Type t, Dictionary<string, Dictionary<string, JToken>> enums,
            Dictionary<string, List<string>> sources, List<string> errors)
        {
            string simple = t.Name;
            FieldInfo[] fields;
            try { fields = t.GetFields(BindingFlags.Public | BindingFlags.Static); }
            catch (Exception e) { Record(errors, EnumTypeLabel(t), e); return; }
            if (fields == null) return;

            // Two enums with the same simple name in different namespaces MERGE their
            // members rather than one overwriting the other, and a member name the two
            // declare with DIFFERENT ordinals keeps both as an array. A union only ever
            // accepts more values, so it can never manufacture a false finding
            // downstream; an overwrite could, by calling the other enum's legal ordinal
            // a mismatch. The merge is reported in 'collisions' so it is visible.
            Dictionary<string, JToken> members;
            if (!enums.TryGetValue(simple, out members))
            {
                members = new Dictionary<string, JToken>(StringComparer.Ordinal);
                enums[simple] = members;
            }
            List<string> from;
            if (!sources.TryGetValue(simple, out from))
            {
                from = new List<string>();
                sources[simple] = from;
            }
            string full = EnumTypeLabel(t);
            if (!from.Contains(full)) from.Add(full);

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                if (f == null || !f.IsLiteral) continue;
                // The instance field holding the value is not a member.
                if (string.Equals(f.Name, "value__", StringComparison.Ordinal)) continue;
                JValue ordinal = Ordinal(t, f, errors);
                if (ordinal == null) continue;
                JToken existing;
                if (!members.TryGetValue(f.Name, out existing))
                {
                    members[f.Name] = ordinal;
                    continue;
                }
                JArray both = existing as JArray;
                if (both == null)
                {
                    if (JToken.DeepEquals(existing, ordinal)) continue;
                    both = new JArray();
                    both.Add(existing);
                    members[f.Name] = both;
                }
                else if (ArrayHas(both, ordinal)) continue;
                both.Add(ordinal);
            }
        }

        static bool ArrayHas(JArray a, JToken value)
        {
            for (int i = 0; i < a.Count; i++) if (JToken.DeepEquals(a[i], value)) return true;
            return false;
        }

        static JValue Ordinal(Type t, FieldInfo f, List<string> errors)
        {
            object raw;
            try { raw = f.GetRawConstantValue(); }
            catch (Exception e) { Record(errors, EnumTypeLabel(t) + "." + f.Name, e); return null; }
            if (raw == null) return null;
            try { return new JValue(Convert.ToInt64(raw)); }
            catch (OverflowException)
            {
                // A ulong-backed enum with the top bit set does not fit a long.
                try { return new JValue(Convert.ToUInt64(raw)); }
                catch (Exception e) { Record(errors, EnumTypeLabel(t) + "." + f.Name, e); return null; }
            }
            catch (Exception e) { Record(errors, EnumTypeLabel(t) + "." + f.Name, e); return null; }
        }

        // Nested types are written with the disassembly's separator, so a collision
        // entry reads the same as the IL it was once parsed from.
        static string EnumTypeLabel(Type t)
        {
            string name = Safe<string>(delegate { return t.FullName; }, null);
            if (string.IsNullOrEmpty(name)) name = Safe<string>(delegate { return t.Name; }, "?");
            return name.Replace('+', '/');
        }

        // The enum a field can hold, unwrapping the containers a template field uses.
        static Type EnumOf(Type ft)
        {
            if (ft == null) return null;
            if (ft.IsEnum) return ft;
            if (ft.IsArray) return EnumOf(ft.GetElementType());
            if (ft.IsGenericType)
            {
                Type def = ft.GetGenericTypeDefinition();
                if (def == typeof(List<>) || def == typeof(Nullable<>) || def == typeof(HashSet<>))
                    return EnumOf(ft.GetGenericArguments()[0]);
            }
            return null;
        }

        // Flattened per class, so a client does one lookup instead of walking a base
        // chain. Fields only: the template JSON is deserialized into fields, and a
        // property was never a key in it. Reflection inherits public instance fields but
        // not the protected and internal ones, so the base chain is walked by hand;
        // first declaration wins, which is a derived class shadowing its base.
        static void CollectEnumFields(Type type,
            Dictionary<string, Dictionary<string, string>> classes, List<string> errors)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var seen = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo[] fields;
                try { fields = t.GetFields(flags); }
                catch (Exception e) { Record(errors, EnumTypeLabel(t), e); continue; }
                if (fields == null) continue;
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo f = fields[i];
                    if (f == null || f.IsStatic || f.IsLiteral || f.IsPrivate) continue;
                    // A compiler-generated backing field carries angle brackets in its
                    // name and is never a JSON key.
                    if (f.Name.IndexOf('<') >= 0) continue;
                    if (seen.ContainsKey(f.Name)) continue;
                    seen[f.Name] = true;
                    Type held = Safe<Type>(delegate { return EnumOf(f.FieldType); }, null);
                    if (held != null) map[f.Name] = held.Name;
                }
            }
            if (map.Count > 0) classes[type.Name] = map;
        }
    }
}
