using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;
using PavonisInteractive.TerraInvicta.Actions;
using PavonisInteractive.TerraInvicta.Entities;

namespace TerraInvictaMCP
{
    // action.list, action.invoke.
    //
    // Reflective coverage of the engine's player-action catalog: every concrete class
    // in PavonisInteractive.TerraInvicta.Actions, enumerated at runtime so a game patch
    // that adds one needs no change here.
    //
    // The namespace holds TWO independent hierarchies, both extending System.Object:
    // PlayerAction (95 concrete classes) and SimulationAction (4). GameControl
    // .StartSimulationAction takes only a SimulationAction, so it cannot carry the other
    // 95; those go through Entities.Player.StartAction. Both submission methods are one
    // instruction -- a bare virtual Execute() call -- so an action runs inside this
    // frame and the caller's next verb sees its result.
    //
    // This is a fixture, in the sense the spawn verbs are: it constructs the engine's
    // own action object and fires it, with none of the UI's cost, availability or
    // turn-order checks in front. It proves a mechanism fires; it never shows that
    // content is reachable through play.
    public static partial class Verbs
    {
        // The one namespace this verb will construct out of. Membership is checked
        // twice: once when the catalog is built, once at the point of construction.
        const string ActionsNamespace = "PavonisInteractive.TerraInvicta.Actions";

        // Bound at 5x the 99-class catalog: the cap exists so the response has a
        // bound at all, not because the engine is near it.
        const int MaxActionsListed = 500;

        // Enum members named in one parameter's value list.
        const int MaxEnumValues = 128;

        // Full parameter detail for the whole catalog is roughly 90KB, which is a
        // discovery call nobody can afford to make casually. A narrowed result carries
        // it; a wide one carries constructor signatures, which is enough to pick the
        // class to narrow to. `detail` overrides the choice either way.
        const int MaxDetailedActions = 12;

        // Constructor candidates quoted in a refusal.
        const int MaxCtorsListed = 8;

        // Effects are never claimed. A generic invoker has no idea what success looks
        // like for 99 different actions, so it reports what it submitted and stops.
        const string InvokeNote = "submitted, not verified: this verb reports the call it "
            + "made, never its outcome. Read the world back with query.state / query.* to "
            + "see what the action did.";

        const string DryRunNote = "dry run: class, constructor and every argument were "
            + "resolved; nothing was constructed and nothing was submitted.";

        #region catalog

        class ActionParam
        {
            public ParameterInfo info;
            public Type type;
            public string kind;         // state | template | enum | primitive | unsupported
            public bool coercible;
        }

        class ActionCtor
        {
            public ConstructorInfo info;
            public List<ActionParam> parameters = new List<ActionParam>();
            public int required;
            public string signature;
            // true, "nullOnly", or false. See InvokableToken.
            public bool anyValue;       // every required parameter is coercible
            public bool reachable;      // every required parameter takes a value at all
        }

        class ActionClass
        {
            public Type type;
            public string name;
            public string fullName;
            public string hierarchy;    // PlayerAction | SimulationAction
            public List<ActionCtor> ctors = new List<ActionCtor>();
        }

        static List<ActionClass> actionCatalog;

        // Interfaces some TIGameState subclass implements. A parameter declared as one
        // of these (CombatTargetableState) is reachable from a state id.
        static Dictionary<Type, bool> stateInterfaces;

        // Interfaces some TIDataTemplate subclass implements. IOperation is the one that
        // matters: its only implementor is TIOperationTemplate, a template, so an
        // operation parameter resolves from a data name like any other template.
        // IOrbitalTransfer has no implementor of either kind and stays unsupported.
        static Dictionary<Type, bool> templateInterfaces;

        // One pass over the game assembly, cached: the loaded assembly cannot change
        // inside a process, and the pass costs a full GetTypes().
        static List<ActionClass> Catalog()
        {
            if (actionCatalog != null) return actionCatalog;

            Type[] types;
            try { types = typeof(PlayerAction).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }

            // Interfaces first, whole assembly: a parameter's kind depends on whether
            // some game state or template implements its interface, so both tables have
            // to be complete before the first parameter is classified. Concrete types
            // only, on both sides: an abstract implementor is never the thing a state id
            // or a data name resolves to.
            var states = new Dictionary<Type, bool>();
            var templates = new Dictionary<Type, bool>();
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] == null) continue;
                CollectInterfaces(states, templates, types[i]);
            }
            stateInterfaces = states;
            templateInterfaces = templates;

            var catalog = new List<ActionClass>();
            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];
                if (t == null || !IsActionClass(t)) continue;
                catalog.Add(DescribeAction(t));
            }
            catalog.Sort(delegate(ActionClass a, ActionClass b)
            {
                return string.CompareOrdinal(a.name, b.name);
            });
            actionCatalog = catalog;
            return actionCatalog;
        }

        static void CollectInterfaces(Dictionary<Type, bool> states,
            Dictionary<Type, bool> templates, Type t)
        {
            if (t.IsInterface || t.IsAbstract) return;
            bool isState = typeof(TIGameState).IsAssignableFrom(t);
            bool isTemplate = typeof(TIDataTemplate).IsAssignableFrom(t);
            if (!isState && !isTemplate) return;
            Type[] implemented;
            try { implemented = t.GetInterfaces(); }
            catch (Exception) { return; }
            Dictionary<Type, bool> found = isState ? states : templates;
            for (int i = 0; i < implemented.Length; i++) found[implemented[i]] = true;
        }

        // Concrete, instantiable, in the namespace, under one of the two bases. The
        // bases themselves are excluded: SimulationAction is abstract, and PlayerAction
        // is concrete but its Execute is an empty body, so it is a base and not an
        // action. Nested types are excluded, which is what keeps the compiler-generated
        // '<>c' and '<>c__DisplayClass' lambda holders out.
        static bool IsActionClass(Type t)
        {
            if (!t.IsClass || t.IsAbstract || t.IsNested) return false;
            if (t.IsGenericType || t.IsGenericTypeDefinition) return false;
            if (!string.Equals(t.Namespace, ActionsNamespace, StringComparison.Ordinal))
                return false;
            if (t.Name.IndexOf('<') >= 0) return false;
            if (t == typeof(PlayerAction) || t == typeof(SimulationAction)) return false;
            return typeof(PlayerAction).IsAssignableFrom(t)
                || typeof(SimulationAction).IsAssignableFrom(t);
        }

        static ActionClass DescribeAction(Type t)
        {
            var entry = new ActionClass();
            entry.type = t;
            entry.name = t.Name;
            entry.fullName = t.FullName;
            entry.hierarchy = typeof(SimulationAction).IsAssignableFrom(t)
                ? "SimulationAction" : "PlayerAction";

            ConstructorInfo[] ctors;
            try { ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance); }
            catch (Exception) { ctors = new ConstructorInfo[0]; }
            for (int i = 0; i < ctors.Length; i++) entry.ctors.Add(DescribeCtor(t, ctors[i]));
            return entry;
        }

        static ActionCtor DescribeCtor(Type owner, ConstructorInfo ctor)
        {
            var described = new ActionCtor();
            described.info = ctor;
            ParameterInfo[] ps;
            try { ps = ctor.GetParameters(); }
            catch (Exception) { ps = new ParameterInfo[0]; }

            var labels = new List<string>();
            described.anyValue = true;
            described.reachable = true;
            for (int i = 0; i < ps.Length; i++)
            {
                var p = new ActionParam();
                p.info = ps[i];
                p.type = ps[i].ParameterType;
                p.kind = KindOf(p.type);
                p.coercible = p.kind != "unsupported";
                described.parameters.Add(p);
                if (!ps[i].IsOptional) described.required++;
                if (!ps[i].IsOptional && !p.coercible)
                {
                    // A required parameter this verb cannot coerce takes only null when
                    // it is a reference type, and nothing at all when it is a struct.
                    // Neither is "invokable" without qualification: null flowing into a
                    // constructor that dereferences it is a NullReferenceException, and
                    // null flowing past one that does not is a call the engine never
                    // makes (BuildHabModuleAction with cost: null is the example).
                    described.anyValue = false;
                    if (p.type.IsValueType) described.reachable = false;
                }
                labels.Add((ps[i].IsOptional ? "[opt] " : "") + TypeLabel(p.type)
                    + " " + ps[i].Name);
            }
            described.signature = owner.Name + "(" + string.Join(", ", labels.ToArray()) + ")";
            return described;
        }

        // Order matters. TIPolicyOption is a TIDataTemplate, so the template test has to
        // come after the game-state test and before anything structural.
        static string KindOf(Type t)
        {
            if (typeof(TIGameState).IsAssignableFrom(t)) return "state";
            if (t.IsInterface && stateInterfaces != null && stateInterfaces.ContainsKey(t))
                return "state";
            if (typeof(TIDataTemplate).IsAssignableFrom(t)) return "template";
            if (t.IsInterface && templateInterfaces != null && templateInterfaces.ContainsKey(t))
                return "template";
            if (t.IsEnum) return "enum";
            if (t == typeof(string) || t == typeof(bool) || t == typeof(char)) return "primitive";
            switch (Type.GetTypeCode(t))
            {
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
                    return "primitive";
            }
            return "unsupported";
        }

        // Source-shaped, because the caller reads it to decide what to pass: generic
        // arguments expanded, a nested type qualified by its declaring type (the
        // AbortReason case).
        static string TypeLabel(Type t)
        {
            if (t == null) return "?";
            if (t.IsArray) return TypeLabel(t.GetElementType()) + "[]";
            string name = t.Name;
            if (t.IsGenericType)
            {
                int tick = name.IndexOf('`');
                if (tick > 0) name = name.Substring(0, tick);
                Type[] a = t.GetGenericArguments();
                var parts = new string[a.Length];
                for (int i = 0; i < a.Length; i++) parts[i] = TypeLabel(a[i]);
                name = name + "<" + string.Join(", ", parts) + ">";
            }
            if (t.IsNested && t.DeclaringType != null)
                return TypeLabel(t.DeclaringType) + "." + name;
            return name;
        }

        #endregion

        #region action.list

        static JToken ActionList(JObject args)
        {
            string filter = Str(args, "filter");
            if (filter != null) filter = filter.Trim();
            List<ActionClass> all = Catalog();

            var matched = new List<ActionClass>();
            for (int i = 0; i < all.Count; i++)
            {
                if (!string.IsNullOrEmpty(filter)
                    && all[i].name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                matched.Add(all[i]);
            }

            JToken ask = args != null ? args["detail"] : null;
            bool detail = ask != null && ask.Type != JTokenType.Null
                ? Flag(args, "detail")
                : matched.Count <= MaxDetailedActions;

            var a = new JArray();
            for (int i = 0; i < matched.Count && a.Count < MaxActionsListed; i++)
                a.Add(ActionRow(matched[i], detail));

            var o = new JObject();
            o["filter"] = filter != null ? (JToken)new JValue(filter) : JValue.CreateNull();
            o["namespace"] = ActionsNamespace;
            o["detail"] = detail;
            // The size is the point of the hint in both directions: a caller that
            // omitted detail has to know what it is missing, and a caller that forced it
            // over the threshold has to know what it just spent.
            o["hint"] = Hint(detail, matched.Count);
            AddPage(o, a, matched.Count, MaxActionsListed);
            o["actions"] = a;
            return o;
        }

        // Measured on the 1.0.51 catalog: 99 classes collapse to about 34KB, and the
        // same 99 with full parameter detail to about 97KB, which is roughly 27k tokens
        // -- over any reasonable budget for a discovery call.
        const int BytesPerDetailedAction = 980;

        static JToken Hint(bool detail, int count)
        {
            if (!detail)
                return new JValue("parameter detail omitted for " + count + " classes ("
                    + "roughly " + Kilobytes(count) + "KB if asked for); narrow with "
                    + "'filter', or pass detail=true to take it anyway");
            if (count <= MaxDetailedActions) return JValue.CreateNull();
            return new JValue("full parameter detail for " + count + " classes, roughly "
                + Kilobytes(count) + "KB; 'filter' narrows it");
        }

        static int Kilobytes(int count)
        {
            int bytes = count * BytesPerDetailedAction;
            return bytes / 1024;
        }

        static JObject ActionRow(ActionClass entry, bool detail)
        {
            var o = new JObject();
            o["name"] = entry.name;
            o["fullName"] = entry.fullName;
            o["hierarchy"] = entry.hierarchy;
            o["submitVia"] = entry.hierarchy == "SimulationAction"
                ? "GameControl.StartSimulationAction" : "Player.StartAction";
            var ctors = new JArray();
            for (int i = 0; i < entry.ctors.Count; i++)
                ctors.Add(CtorRow(entry.ctors[i], detail));
            o["constructors"] = ctors;
            return o;
        }

        // The signature carries every parameter's name and type either way, so the
        // summary form still says what a call would need; what it drops is the kind,
        // coercibility and enum members.
        static JObject CtorRow(ActionCtor ctor, bool detail)
        {
            var o = new JObject();
            o["signature"] = ctor.signature;
            o["required"] = ctor.required;
            o["total"] = ctor.parameters.Count;
            o["invokable"] = InvokableToken(ctor);
            if (!detail) return o;
            var ps = new JArray();
            for (int i = 0; i < ctor.parameters.Count; i++) ps.Add(ParamRow(ctor.parameters[i]));
            o["parameters"] = ps;
            return o;
        }

        // Three values, not two. `true` means every required parameter takes a real
        // value. `"nullOnly"` means the constructor can be called but at least one
        // required parameter accepts nothing except null, so the call is a null flowing
        // into engine code that never receives one from the game -- read the parameter
        // list before firing it. `false` means a required parameter takes no value at
        // all, so no call exists.
        static JToken InvokableToken(ActionCtor ctor)
        {
            if (!ctor.reachable) return new JValue(false);
            if (!ctor.anyValue) return new JValue("nullOnly");
            return new JValue(true);
        }

        static JObject ParamRow(ActionParam p)
        {
            var o = new JObject();
            o["name"] = p.info.Name;
            o["type"] = TypeLabel(p.type);
            o["typeFull"] = p.type.FullName;
            o["kind"] = p.kind;
            o["coercible"] = p.coercible;
            // An uncoercible reference parameter is still reachable: explicit null is
            // accepted for every reference type, which is what makes a constructor with
            // an optional Trajectory or callback usable.
            o["acceptsNull"] = !p.type.IsValueType;
            o["accepts"] = Accepts(p);
            o["optional"] = p.info.IsOptional;
            if (p.info.IsOptional) o["default"] = DefaultToken(p);
            if (p.kind == "enum") o["values"] = EnumValues(p.type, o);
            return o;
        }

        static string Accepts(ActionParam p)
        {
            switch (p.kind)
            {
                case "state":
                    return "integer game-state id";
                case "template":
                    return "template dataName (string)";
                case "enum":
                    return "member name (case-insensitive) or integer";
                case "primitive":
                    return "JSON " + TypeLabel(p.type);
            }
            return p.type.IsValueType
                ? "nothing this verb can build"
                : "null only (this verb cannot build " + TypeLabel(p.type) + ")";
        }

        static JToken EnumValues(Type t, JObject row)
        {
            string[] names;
            try { names = Enum.GetNames(t); }
            catch (Exception) { return JValue.CreateNull(); }
            var a = new JArray();
            for (int i = 0; i < names.Length && a.Count < MaxEnumValues; i++)
                a.Add(new JValue(names[i]));
            if (names.Length > a.Count) row["valuesTruncated"] = true;
            return a;
        }

        static JToken DefaultToken(ActionParam p)
        {
            object value = RawDefault(p.info);
            if (value == null) return JValue.CreateNull();
            if (p.type.IsEnum) return new JValue(value.ToString());
            return Value(value);
        }

        #endregion

        #region action.invoke

        static JToken ActionInvoke(JObject args)
        {
            string wanted = Str(args, "class");
            if (string.IsNullOrEmpty(wanted)) throw new VerbError("missing arg 'class'");
            bool dryRun = Bool(args, "dryRun", false);
            ActionClass entry = ResolveActionClass(wanted.Trim());

            JToken supplied = args != null ? args["args"] : null;
            if (supplied != null && supplied.Type == JTokenType.Null) supplied = null;
            if (supplied != null && supplied.Type != JTokenType.Object
                && supplied.Type != JTokenType.Array)
                throw new VerbError("arg 'args' must be an object keyed by parameter name "
                    + "or an array of positional values");

            ActionCtor chosen;
            object[] values;
            JArray report;
            SelectCtor(entry, supplied, out chosen, out values, out report);

            var o = new JObject();
            o["class"] = entry.name;
            o["fullName"] = entry.fullName;
            o["hierarchy"] = entry.hierarchy;
            o["constructor"] = chosen.signature;
            o["arguments"] = report;
            o["dryRun"] = dryRun;

            if (dryRun)
            {
                o["submitted"] = false;
                o["submittedVia"] = JValue.CreateNull();
                o["note"] = DryRunNote;
                return o;
            }

            // The catalog is built from the namespace and base-class filters, so nothing
            // else can reach this point. Re-asserted anyway: a generic invoker that ever
            // constructs an arbitrary engine type is the one failure this verb must not
            // have, and a catalog bug would be the only way in.
            if (entry.type == null || !IsActionClass(entry.type))
                throw new VerbError("refusing to construct "
                    + (entry.type != null ? entry.type.FullName : "null")
                    + ": action.invoke builds only concrete " + ActionsNamespace
                    + " classes deriving from PlayerAction or SimulationAction");

            object action;
            try { action = chosen.info.Invoke(values); }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                throw new VerbError("constructing " + entry.name + " threw " + Note(inner));
            }
            catch (Exception e)
            {
                throw new VerbError("constructing " + entry.name + " threw " + Note(e));
            }

            o["submittedVia"] = Submit(entry, action, values);
            o["submitted"] = true;
            o["note"] = InvokeNote;
            return o;
        }

        // Both submission methods are a single virtual Execute() call, so the action runs
        // inside this frame. The Player instance is a dispatcher and nothing more --
        // StartAction ignores `this` -- but it is resolved the way the engine's own call
        // sites resolve it (288 of 326 of them read <state>.ref_faction.playerControl),
        // so the reported runner is the one vanilla would have used.
        static JToken Submit(ActionClass entry, object action, object[] values)
        {
            var simulation = action as SimulationAction;
            if (simulation != null)
            {
                Run(entry, delegate { GameControl.StartSimulationAction(simulation); });
                return new JValue("GameControl.StartSimulationAction");
            }

            var player = (PlayerAction)action;
            TIFactionState faction = RunnerFaction(values);
            Player runner = faction != null
                ? Safe<Player>(delegate { return faction.playerControl; }, null) : null;
            if (runner != null)
            {
                Run(entry, delegate { runner.StartAction(player); });
                return new JValue("Player.StartAction (runner: " + StateName(faction) + ")");
            }
            // Player.StartAction is `action.Execute()` and nothing else, so this is the
            // same call with the dispatcher missing. Named in the response rather than
            // hidden, because the divergence is only guaranteed harmless today.
            Run(entry, delegate { player.Execute(); });
            return new JValue("PlayerAction.Execute (no Player runner"
                + (faction != null ? " for " + StateName(faction) : "") + ")");
        }

        static void Run(ActionClass entry, Action submit)
        {
            try { submit(); }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                throw new VerbError(entry.name + ".Execute threw " + Note(inner));
            }
            catch (Exception e)
            {
                throw new VerbError(entry.name + ".Execute threw " + Note(e));
            }
        }

        // The acting faction as the action's own arguments name it: a faction argument
        // outright, else the faction of the first game state passed. The active player
        // is the fallback for an action that names neither.
        static TIFactionState RunnerFaction(object[] values)
        {
            for (int i = 0; values != null && i < values.Length; i++)
            {
                var faction = values[i] as TIFactionState;
                if (faction != null) return faction;
            }
            for (int i = 0; values != null && i < values.Length; i++)
            {
                var state = values[i] as TIGameState;
                if (state == null) continue;
                TIFactionState owner = FactionOf(state);
                if (owner != null) return owner;
            }
            return ActivePlayer();
        }

        #endregion

        #region class resolution

        // A name with a dot is a full name and must match one exactly; a bare name is
        // matched against short names. Both are case-insensitive; neither reaches
        // outside the catalog, which is the whole namespace guarantee.
        static ActionClass ResolveActionClass(string wanted)
        {
            List<ActionClass> all = Catalog();
            bool qualified = wanted.IndexOf('.') >= 0;
            var hits = new List<ActionClass>();
            for (int i = 0; i < all.Count; i++)
            {
                string name = qualified ? all[i].fullName : all[i].name;
                if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                    hits.Add(all[i]);
            }
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1)
            {
                var listed = new List<string>();
                for (int i = 0; i < hits.Count && i < MaxCtorsListed; i++)
                    listed.Add(hits[i].fullName);
                throw new VerbError("'" + wanted + "' matches " + hits.Count
                    + " action classes: " + string.Join(", ", listed.ToArray())
                    + "; pass the full name");
            }
            var names = new List<string>();
            for (int i = 0; i < all.Count; i++) names.Add(all[i].name);
            throw new VerbError("no action class named '" + wanted + "'"
                + Near(names, wanted) + "; action.list enumerates them (only "
                + ActionsNamespace + " classes exist here -- a policy option is not an "
                + "action and no option name resolves, though ConfirmPolicyAction takes "
                + "one as a parameter and will enact it with no gates at all; "
                + "faction.diplomacy is the gated path to the five faction-level ones)");
        }

        #endregion

        #region constructor selection

        // Every constructor is tried in full, coercions included, and exactly one has to
        // survive. A single survivor is the answer, none is a refusal carrying each
        // constructor's own reason, and more than one is a refusal rather than a guess.
        static void SelectCtor(ActionClass entry, JToken supplied, out ActionCtor chosen,
            out object[] values, out JArray report)
        {
            if (entry.ctors.Count == 0)
                throw new VerbError(entry.name + " has no public constructor, so this "
                    + "verb cannot build it");

            var matched = new List<ActionCtor>();
            var matchedValues = new List<object[]>();
            var matchedReports = new List<JArray>();
            var reasons = new List<string>();

            for (int i = 0; i < entry.ctors.Count; i++)
            {
                ActionCtor ctor = entry.ctors[i];
                object[] built;
                JArray built_report;
                try
                {
                    Bind(ctor, supplied, out built, out built_report);
                }
                catch (VerbError e)
                {
                    reasons.Add(ctor.signature + " -- " + e.Message);
                    continue;
                }
                matched.Add(ctor);
                matchedValues.Add(built);
                matchedReports.Add(built_report);
            }

            if (matched.Count == 1)
            {
                chosen = matched[0];
                values = matchedValues[0];
                report = matchedReports[0];
                return;
            }
            if (matched.Count > 1)
            {
                var listed = new List<string>();
                for (int i = 0; i < matched.Count && i < MaxCtorsListed; i++)
                    listed.Add(matched[i].signature);
                throw new VerbError("these arguments fit " + matched.Count
                    + " constructors of " + entry.name + ": "
                    + string.Join(" | ", listed.ToArray())
                    + "; name every argument, or pass a value for a parameter that "
                    + "tells them apart");
            }
            if (reasons.Count == 1) throw new VerbError(reasons[0]);
            var quoted = new List<string>();
            for (int i = 0; i < reasons.Count && i < MaxCtorsListed; i++)
                quoted.Add(reasons[i]);
            throw new VerbError("no constructor of " + entry.name + " fits: "
                + string.Join(" | ", quoted.ToArray()));
        }

        // Named or positional. Named is the form that survives an overload set and a
        // future parameter insertion, so it is the one the docs lead with.
        static void Bind(ActionCtor ctor, JToken supplied, out object[] values,
            out JArray report)
        {
            var byIndex = new JToken[ctor.parameters.Count];
            var wasSupplied = new bool[ctor.parameters.Count];

            var asArray = supplied as JArray;
            var asObject = supplied as JObject;
            if (asArray != null)
            {
                if (asArray.Count > ctor.parameters.Count)
                    throw new VerbError("takes at most " + ctor.parameters.Count
                        + " arguments, " + asArray.Count + " given");
                for (int i = 0; i < asArray.Count; i++)
                {
                    byIndex[i] = asArray[i];
                    wasSupplied[i] = true;
                }
            }
            else if (asObject != null)
            {
                foreach (KeyValuePair<string, JToken> kv in asObject)
                {
                    int index = IndexOfParam(ctor, kv.Key);
                    if (index < 0)
                        throw new VerbError("no parameter named '" + kv.Key + "'");
                    // Parameter names match case-insensitively, so two keys differing
                    // only by case both land here. Silently keeping the last one is how
                    // a caller fires a different call than the one it wrote.
                    if (wasSupplied[index])
                        throw new VerbError("parameter '" + ctor.parameters[index].info.Name
                            + "' is given more than once ('" + kv.Key + "' among them); "
                            + "argument names match case-insensitively");
                    byIndex[index] = kv.Value;
                    wasSupplied[index] = true;
                }
            }

            values = new object[ctor.parameters.Count];
            report = new JArray();
            for (int i = 0; i < ctor.parameters.Count; i++)
            {
                ActionParam p = ctor.parameters[i];
                JObject row;
                if (!wasSupplied[i])
                {
                    if (!p.info.IsOptional)
                        throw new VerbError("missing argument '" + p.info.Name + "' ("
                            + TypeLabel(p.type) + ", " + Accepts(p) + ")");
                    values[i] = RawDefault(p.info);
                    row = new JObject();
                    row["name"] = p.info.Name;
                    row["type"] = TypeLabel(p.type);
                    row["kind"] = p.kind;
                    row["source"] = "default";
                    row["received"] = JValue.CreateNull();
                    row["resolved"] = Resolved(values[i]);
                }
                else
                {
                    values[i] = Coerce(p, byIndex[i], out row);
                }
                report.Add(row);
            }
        }

        static int IndexOfParam(ActionCtor ctor, string name)
        {
            for (int i = 0; i < ctor.parameters.Count; i++)
            {
                if (string.Equals(ctor.parameters[i].info.Name, name,
                        StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        static object RawDefault(ParameterInfo p)
        {
            object d = null;
            try { d = p.DefaultValue; }
            catch (Exception) { d = null; }
            if (d is DBNull) d = null;
            if (d == null && p.ParameterType.IsValueType)
                return Activator.CreateInstance(p.ParameterType);
            return d;
        }

        #endregion

        #region coercion

        static object Coerce(ActionParam p, JToken raw, out JObject row)
        {
            row = new JObject();
            row["name"] = p.info.Name;
            row["type"] = TypeLabel(p.type);
            row["kind"] = p.kind;
            row["source"] = "argument";
            row["received"] = raw != null ? raw.DeepClone() : JValue.CreateNull();

            object value;
            if (raw == null || raw.Type == JTokenType.Null)
            {
                // Explicit null. Legal for any reference type and for no value type,
                // which is what makes an optional Trajectory or callback reachable
                // without making an int nullable.
                if (p.type.IsValueType)
                    throw new VerbError("argument '" + p.info.Name + "' is "
                        + TypeLabel(p.type) + " and cannot be null");
                value = null;
            }
            else
            {
                switch (p.kind)
                {
                    case "state": value = CoerceState(p, raw); break;
                    case "template": value = CoerceTemplate(p, raw); break;
                    case "enum": value = CoerceEnum(p, raw); break;
                    case "primitive": value = CoercePrimitive(p, raw); break;
                    default: throw new VerbError("argument '" + p.info.Name + "' is "
                        + TypeLabel(p.type) + ", which this verb cannot build"
                        + (p.type.IsValueType
                            ? "; no value is accepted for it"
                            : "; pass null, or use a contracted verb for this action"));
                }
            }
            row["resolved"] = Resolved(value);
            return value;
        }

        static object CoerceState(ActionParam p, JToken raw)
        {
            if (raw.Type != JTokenType.Integer)
                throw new VerbError("argument '" + p.info.Name + "' expects an integer "
                    + "game-state id for " + TypeLabel(p.type) + "; received "
                    + Received(raw));
            int id;
            try { id = (int)raw; }
            catch (Exception)
            {
                throw new VerbError("argument '" + p.info.Name
                    + "' is not a state id: " + Received(raw));
            }
            TIGameState state = Safe<TIGameState>(delegate
            {
                return GameStateManager.FindGameState<TIGameState>(new GameStateID(id), true);
            }, null);
            if (state == null)
                throw new VerbError("argument '" + p.info.Name + "': no game state with "
                    + "id " + id);
            if (!p.type.IsInstanceOfType(state))
                throw new VerbError("argument '" + p.info.Name + "': state " + id + " is a "
                    + state.GetType().Name + " (" + StateName(state) + "), not a "
                    + TypeLabel(p.type));
            return state;
        }

        // TemplateManager.Find<T>(name, allowChild: true) does an exact-type lookup and
        // then falls back to any subtype registered under that data name, which is what
        // a parameter declared as a base template class (TIShipPartTemplate) needs.
        static object CoerceTemplate(ActionParam p, JToken raw)
        {
            if (raw.Type != JTokenType.String)
                throw new VerbError("argument '" + p.info.Name + "' expects a "
                    + TypeLabel(p.type) + " dataName (string); received " + Received(raw));
            string name = ((string)raw).Trim();
            if (name.Length == 0)
                throw new VerbError("argument '" + p.info.Name + "' is an empty dataName");
            // Find<T> is constrained to TIDataTemplate, so an interface parameter
            // (IOperation) cannot bind it. Those go through the base class and are
            // checked against the declared interface afterwards; a class parameter binds
            // its own type and gets the exact-then-subtype lookup.
            bool viaInterface = p.type.IsInterface;
            Type bound = viaInterface ? typeof(TIDataTemplate) : p.type;
            MethodInfo find;
            try { find = templateFind.MakeGenericMethod(bound); }
            catch (Exception e)
            {
                throw new VerbError("argument '" + p.info.Name + "': TemplateManager.Find "
                    + "cannot be bound to " + TypeLabel(bound) + " (" + Note(e) + ")");
            }
            object found;
            try { found = find.Invoke(null, new object[] { name }); }
            catch (TargetInvocationException e)
            {
                Exception inner = e.InnerException != null ? e.InnerException : e;
                throw new VerbError("argument '" + p.info.Name
                    + "': TemplateManager.Find threw " + Note(inner));
            }
            if (found == null)
                throw new VerbError("argument '" + p.info.Name + "': no " + TypeLabel(p.type)
                    + " named '" + name + "'; query.template type=" + p.type.Name
                    + " lists the data names");
            if (viaInterface && !p.type.IsInstanceOfType(found))
                throw new VerbError("argument '" + p.info.Name + "': '" + name + "' is a "
                    + found.GetType().Name + ", which does not implement "
                    + TypeLabel(p.type));
            return found;
        }

        static object CoerceEnum(ActionParam p, JToken raw)
        {
            if (raw.Type == JTokenType.Integer)
            {
                object boxed;
                try { boxed = Enum.ToObject(p.type, (long)raw); }
                catch (Exception)
                {
                    throw new VerbError("argument '" + p.info.Name + "': " + Received(raw)
                        + " is out of range for " + TypeLabel(p.type));
                }
                // An undefined integer is refused unless the type is [Flags], where a
                // combination of members is the point. None of the 13 enum parameters in
                // the catalog is [Flags], and an undefined value is not inert: an
                // undefined PriorityType handed to SetPriorityAction lands as a key in a
                // control point's serialized priority dictionary through a bare
                // set_Item, and stays in every save written afterwards.
                if (!IsFlags(p.type) && !Enum.IsDefined(p.type, boxed))
                    throw new VerbError("argument '" + p.info.Name + "': " + Received(raw)
                        + " is not a defined " + TypeLabel(p.type)
                        + " member; members: " + EnumNameList(p.type));
                return boxed;
            }
            if (raw.Type != JTokenType.String)
                throw new VerbError("argument '" + p.info.Name + "' expects a "
                    + TypeLabel(p.type) + " member name or an integer; received "
                    + Received(raw));
            string name = ((string)raw).Trim();
            object parsed;
            try { parsed = Enum.Parse(p.type, name, true); }
            catch (Exception)
            {
                throw new VerbError("argument '" + p.info.Name + "': " + TypeLabel(p.type)
                    + " has no member '" + name + "'; members: " + EnumNameList(p.type));
            }
            // Enum.Parse accepts a numeric string too, so the defined-value gate has to
            // cover this path as well.
            if (!IsFlags(p.type) && !Enum.IsDefined(p.type, parsed))
                throw new VerbError("argument '" + p.info.Name + "': '" + name + "' is not "
                    + "a defined " + TypeLabel(p.type) + " member; members: "
                    + EnumNameList(p.type));
            return parsed;
        }

        static bool IsFlags(Type t)
        {
            return Safe<bool>(delegate
            {
                return t.IsDefined(typeof(FlagsAttribute), false);
            }, false);
        }

        static string EnumNameList(Type t)
        {
            string[] names;
            try { names = Enum.GetNames(t); }
            catch (Exception) { return "<unreadable>"; }
            var listed = new List<string>();
            for (int i = 0; i < names.Length && i < MaxEnumValues; i++) listed.Add(names[i]);
            return string.Join(", ", listed.ToArray())
                + (names.Length > listed.Count ? ", ..." : "");
        }

        static object CoercePrimitive(ActionParam p, JToken raw)
        {
            if (p.type == typeof(string))
            {
                if (raw.Type != JTokenType.String)
                    throw new VerbError("argument '" + p.info.Name
                        + "' expects a string; received " + Received(raw));
                return (string)raw;
            }
            if (p.type == typeof(bool))
            {
                if (raw.Type != JTokenType.Boolean)
                    throw new VerbError("argument '" + p.info.Name
                        + "' expects true or false; received " + Received(raw));
                return (bool)raw;
            }
            if (p.type == typeof(char))
            {
                string s = raw.Type == JTokenType.String ? (string)raw : null;
                if (s == null || s.Length != 1)
                    throw new VerbError("argument '" + p.info.Name
                        + "' expects a one-character string; received " + Received(raw));
                return s[0];
            }
            if (raw.Type != JTokenType.Integer && raw.Type != JTokenType.Float)
                throw new VerbError("argument '" + p.info.Name + "' expects a number ("
                    + TypeLabel(p.type) + "); received " + Received(raw));
            // A fraction handed to an integer parameter would be rounded silently, and a
            // rounded slot index or army count is a wrong call that looks like a right one.
            if (raw.Type == JTokenType.Float && !Fractional(p.type))
                throw new VerbError("argument '" + p.info.Name + "' is " + TypeLabel(p.type)
                    + " and takes a whole number; received " + Received(raw));
            // JSON.NET reads the non-standard NaN, Infinity and -Infinity literals as
            // floats, and a big enough exponent becomes an infinity on its own. Every
            // range test after this point is a comparison, and a comparison against NaN
            // is false in both directions, so a nonfinite value reaches the game as it
            // is and sits in campaign state that nothing later can undo.
            double number;
            // The cast is inside a try of its own because a number too large for a
            // double at all -- JSON.NET keeps one as a BigInteger -- failed inside
            // the conversion below before and has to answer the same way now.
            try { number = (double)raw; }
            catch (Exception) { throw OutOfRange(p, raw); }
            if (double.IsNaN(number) || double.IsInfinity(number))
                throw new VerbError("argument '" + p.info.Name + "' must be a finite "
                    + "number; received " + Received(raw));
            object coerced;
            try
            {
                coerced = Convert.ChangeType(number, p.type, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                throw OutOfRange(p, raw);
            }
            // Convert.ToSingle answers Infinity for a double past float's range instead
            // of throwing, so the overflow the catch above is written for arrives as a
            // value rather than as an exception.
            if (coerced is float && float.IsInfinity((float)coerced)) throw OutOfRange(p, raw);
            return coerced;
        }

        static VerbError OutOfRange(ActionParam p, JToken raw)
        {
            return new VerbError("argument '" + p.info.Name + "': " + Received(raw)
                + " is out of range for " + TypeLabel(p.type));
        }

        static bool Fractional(Type t)
        {
            switch (Type.GetTypeCode(t))
            {
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return true;
            }
            return false;
        }

        // What the caller actually sent, quoted back so a type error names both sides.
        static string Received(JToken raw)
        {
            if (raw == null) return "nothing";
            string text = raw.ToString(Newtonsoft.Json.Formatting.None);
            if (text.Length > 80) text = text.Substring(0, 80) + "...";
            return text + " (" + raw.Type.ToString().ToLowerInvariant() + ")";
        }

        // The argument as the verb understood it: a state collapses to id/type/name, a
        // template to its data name, an enum to its member name.
        //
        // A state also carries `archived` and `exists`, because ArchiveState only sets a
        // flag -- it never removes the state from GameStateManager.gamestates -- so a
        // destroyed hab or a dead councilor still resolves by id and looks exactly like a
        // live one here. Disclosed rather than refused: acting on an archived state is a
        // legitimate fixture, and acting on one by accident is not, so the caller is told
        // which it has.
        static JToken Resolved(object value)
        {
            if (value == null) return JValue.CreateNull();
            var state = value as TIGameState;
            if (state != null)
            {
                var described = Describe(state) as JObject;
                if (described != null)
                {
                    Put(described, "archived", delegate { return (JToken)state.archived; });
                    Put(described, "exists", delegate { return (JToken)state.exists; });
                }
                return described;
            }
            var template = value as TIDataTemplate;
            if (template != null)
            {
                return Safe<JToken>(delegate { return new JValue(template.dataName); },
                    new JValue("<error: dataName>"));
            }
            return Value(value);
        }

        // Find<T> is constrained to TIDataTemplate, so it cannot be called with a type
        // known only at runtime. This wrapper is what gets bound to the parameter's type
        // instead, and the call inside it is direct: a rename in the engine breaks the
        // build rather than the verb.
        static object FindTemplate<T>(string name) where T : TIDataTemplate
        {
            return TemplateManager.Find<T>(name, true);
        }

        static readonly MethodInfo templateFind = typeof(Verbs).GetMethod(nameof(FindTemplate),
            BindingFlags.NonPublic | BindingFlags.Static);

        #endregion
    }
}
