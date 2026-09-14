using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using PavonisInteractive.TerraInvicta;

namespace TerraInvictaMCP
{
    // design.create, design.delete, design.auto.
    //
    // A ship design is a TISpaceShipTemplate the game builds at run time and
    // registers with TemplateManager. Nothing else in the harness could make one:
    // the ship designer is a screen with a hull picker, a drive picker and a slot
    // grid, all mouse-driven, and for a driving agent the UI is view-only. Without
    // these verbs the only designs a test could use were the ones the campaign
    // shipped with, which is why "does this mod's laser fit on a vanilla hull"
    // had no headless answer.
    //
    // spawn.fleet is the other half. A design proves the parts go together and are
    // buildable; spawn.fleet takes a saved design and proves it flies.
    public static partial class Verbs
    {
        // Bounds the entry lists before any of them is resolved. The biggest
        // vanilla hull carries far fewer slots than this; the cap only keeps an
        // absurd request from turning into an absurd number of template lookups
        // on the main thread with the game frozen.
        const int MaxDesignEntries = 64;

        #region design.create

        // The field order below is the AI builder's own, read off
        // TIFactionState.<DesignShip>b__46: ctor(dataName), factionName, hullName,
        // InitAtRunTime(true), role, drive, power plant, radiator, then per facing
        // the armor materialName followed by TrySetArmor, then the weapon entry
        // lists, then the module entry list, then propellantTanks.
        //
        // The order is load-bearing in three places.
        //   - InitAtRunTime replaces moduleTemplateEntries, both weapon lists, the
        //     fire-mode list and all three ArmorFacingTemplate structs with fresh
        //     empties (IL_000a-IL_0069), so anything written before it is lost.
        //   - hullName is set BEFORE InitAtRunTime because TrySetArmor clamps
        //     against GetMaxAllowedArmorBySlot, which dereferences get_hullTemplate
        //     with callvirt (IL_0018) and would throw on a design with no hull.
        //   - materialName is written before TrySetArmor for the same facing,
        //     because the clamp reads the facing's own material template to work
        //     out how many points fit.
        //
        // Where the builder calls a helper that PICKS a part -- GetBestPowerPlant,
        // GetBestRadiator, SetShipDesignNoseWeapons, SetShipDesignHullWeapons,
        // GetBestUtilityModules, GetIdealPropellentTankCount -- this verb sets the
        // field from the caller's argument instead. A verb that quietly chose a
        // different drive than the one asked for would report a design nobody
        // requested. The builder itself takes the same path whenever the caller
        // supplied a list: its nose and hull weapon arms assign the given entries
        // directly at IL_0240 and IL_0286 and only call the helper when the list is
        // null.
        //
        // SetBaseCruiseDeltaV_kps is not called. The builder needs it because
        // GetIdealPropellentTankCount hands back the delta-v it solved for; here
        // the tank count is the caller's, and CacheTemplateValues(false) calls
        // baseCruiseDeltaV_mps(forceUpdate: true), which recomputes the field from
        // the finished design (IL_0010-IL_002f of baseCruiseDeltaV_kps).
        static JToken DesignCreate(JObject args)
        {
            TIFactionState faction = Arg<TIFactionState>(args, "faction");
            string name = RequiredText(args, "name");
            string hull = RequiredText(args, "hull");
            string drive = RequiredText(args, "drive");
            string powerPlant = RequiredText(args, "power_plant");
            string radiator = RequiredText(args, "radiator");
            int tanks = Int(args, "tanks");
            ShipRole role = ParseEnum<ShipRole>(RequiredText(args, "role"), "role");
            bool save = Bool(args, "save", true);
            bool legal = Bool(args, "legal", true);

            JObject armor = args != null ? args["armor"] as JObject : null;
            if (armor == null)
                throw new VerbError("arg 'armor' is required: an object with "
                    + "'nose', 'lateral' and 'tail', each {material, value}. Every "
                    + "facing needs a material template because ValidTemplate tests "
                    + "all three; nothing was created");
            ArmorArg nose = ParseArmor(armor, "nose");
            ArmorArg lateral = ParseArmor(armor, "lateral");
            ArmorArg tail = ParseArmor(armor, "tail");

            var modules = ParseEntries(args, "modules");
            var noseWeapons = ParseEntries(args, "nose_weapons");
            var hullWeapons = ParseEntries(args, "hull_weapons");
            var fireModes = ParseFireModes(args, "fire_modes");

            // Only when the design is going to be registered. The list is about
            // names in use, and nothing that is not saved takes a name: under
            // save=false the verb builds the design, runs the checks and throws it
            // away, which is how a caller asks whether a loadout validates. That
            // question has no answer at all if the name it was asked under is the
            // name of a design or a ship already in play.
            if (save) RefuseTakenClassName(name);

            // Refused before anything is built rather than after: the hull is what
            // TrySetArmor dereferences, and the message is more use than an engine
            // NullReferenceException with the same cause.
            TIShipHullTemplate hullTemplate =
                TemplateManager.Find<TIShipHullTemplate>(hull, false);
            if (hullTemplate == null)
                throw new VerbError("no TIShipHullTemplate named '" + hull
                    + "'; the design's armor clamp reads the hull and would throw. "
                    + "Nothing was created");

            // Needs the hull, so it runs here rather than beside the entry
            // parsing it belongs to.
            RefuseSlotCollisions(hullTemplate, modules, noseWeapons, hullWeapons);

            string dataName = Safe<string>(delegate
            {
                return TemplateManager.GenerateDataName(
                    faction.templateName + "ShipTemplate");
            }, null);
            if (string.IsNullOrEmpty(dataName))
                throw new VerbError("TemplateManager.GenerateDataName returned "
                    + "nothing, so the design would have no data name; nothing was "
                    + "created");

            TISpaceShipTemplate design;
            try { design = Build(faction, dataName, hull, drive, powerPlant,
                                 radiator, tanks, role, nose, lateral, tail,
                                 modules, noseWeapons, hullWeapons, fireModes); }
            catch (Exception e)
            {
                throw new VerbError("building the design threw: " + Note(e)
                    + ". Nothing was registered: the template is a loose object "
                    + "until faction.SaveShipDesign adds it");
            }

            var o = new JObject();
            o["faction"] = Describe(faction);
            o["dataName"] = dataName;
            o["name"] = name;
            o["role"] = role.ToString();
            o["hull"] = hull;
            o["drive"] = drive;
            o["powerPlant"] = powerPlant;
            o["radiator"] = radiator;
            o["tanks"] = tanks;
            o["armor"] = ArmorReport(design);
            o["moduleEntries"] = modules.Count;
            o["noseWeaponEntries"] = noseWeapons.Count;
            o["hullWeaponEntries"] = hullWeapons.Count;
            o["fireModeEntries"] = fireModes.Count;

            // get_utilityModules walks moduleTemplateEntries and quietly drops an
            // entry whose name is empty, whose name is the literal "Empty", whose
            // name resolves to no TIShipModuleTemplate, or whose slot the hull does
            // not accept for that part (IL_003c, IL_0051, IL_0066, IL_0075). Every
            // one of those is a design that reads as saved and flies without the
            // module, so the same four tests run here and the answer is reported.
            //
            // get_noseWeapons and get_hullWeapons apply the same four tests in the
            // same order and differ only in resolving the name through
            // TIShipWeaponTemplate (IL_003c, IL_0051, IL_0069, IL_0072 of each).
            // get_ValidTemplate never reads either weapon list, so before this a
            // laser dropped into a utility slot came back valid and saved as a
            // warship with no weapon.
            JArray droppedModules = Dropped(design, modules,
                                            "get_utilityModules", false);
            JArray droppedNose = Dropped(design, noseWeapons,
                                         "get_noseWeapons", true);
            JArray droppedHull = Dropped(design, hullWeapons,
                                         "get_hullWeapons", true);
            o["droppedModules"] = droppedModules;
            o["droppedNoseWeapons"] = droppedNose;
            o["droppedHullWeapons"] = droppedHull;

            // The ten predicates of get_ValidTemplate, run one at a time in its own
            // order (IL_0000 through IL_0050). The engine's version is a bare bool
            // and names nothing, which is what made a failed design impossible to
            // diagnose from outside.
            JArray checks = ValidityChecks(design, role);
            string checkFailure = FirstFailure(checks);
            o["checks"] = checks;

            // A dropped entry outranks a failed predicate as the reason, because it
            // is usually the cause of one: a dropped module is exactly what makes
            // AllowedRole false, and reporting "allowedRole" would send the caller
            // at the role rather than at the slot. `valid` is the ten predicates AND
            // an intact parts list, so a design the engine would gut is refused
            // rather than saved as something the caller did not ask for.
            string dropFailure = DropFailure(droppedModules, droppedNose,
                                             droppedHull);
            string reason = dropFailure != null ? dropFailure : checkFailure;
            o["valid"] = reason == null;
            o["reason"] = reason != null ? (JToken)new JValue(reason)
                                         : JValue.CreateNull();
            // The read-back, so a divergence between the ten predicates here and
            // the engine's own is visible rather than assumed. Compared against the
            // predicates alone. The engine's bool does not read either weapon
            // list, so a dropped weapon makes the two answers differ for a reason
            // this verb already reports, and comparing against `reason` would turn
            // that into a false drift warning on every such call.
            bool engineValid = Safe<bool>(delegate { return design.ValidTemplate; },
                                          false);
            o["engineValid"] = engineValid;
            if (engineValid != (checkFailure == null))
                o["warning"] = "the ten checks above and the engine's own "
                    + "get_ValidTemplate disagree (" + (checkFailure == null)
                    + " vs " + engineValid + "); the engine's answer is the one "
                    + "that counts and the checks in this reply need updating";

            // Validity has no faction gate at all: it never looks at who is
            // designing. Play legality is per part, TIShipPartTemplate.FactionCanBuild,
            // which is true when the part needs no project or the faction has that
            // project completed (IL_0008-IL_0021).
            //
            // An alien faction takes a different route through the same method.
            // IL_0000-IL_0006 jumps a true IsAlienFaction PAST the no-project
            // shortcut and straight into completedProjects.Contains(requiredProject),
            // so a part needing no project is tested as Contains(null) and comes
            // back false. Nothing is wrong with the design; the engine simply does
            // not describe alien parts in terms of human research, so under `legal`
            // an alien faction lists nearly everything as illegal. The note below
            // says so rather than leaving the caller to read a wall of refusals.
            JArray illegal = legal ? IllegalParts(faction, design, modules,
                                                  noseWeapons, hullWeapons)
                                   : new JArray();
            o["legalChecked"] = legal;
            o["illegalParts"] = illegal;
            if (illegal.Count > 0
                && Safe<bool>(delegate { return faction.IsAlienFaction; }, false))
                o["note"] = "the designing faction is the alien faction, and "
                    + "FactionCanBuild jumps an alien faction past its own "
                    + "no-project shortcut into completedProjects.Contains, which "
                    + "is false for every part that needs no project. So this list "
                    + "is what the engine says and not a research gap: pass "
                    + "legal=false to check and save an alien design";

            o["saved"] = false;
            if (!save)
            {
                o["savedReason"] = "save=false, so the design was built and checked "
                    + "and nothing was registered";
                return o;
            }
            if (reason != null)
            {
                o["savedReason"] = "the design is not valid (" + reason + "), so it "
                    + "was not registered; an invalid design in TemplateManager "
                    + "would be picked up by every later lookup"
                    + (dropFailure != null
                        ? ". A dropped entry is not a validity failure the engine "
                          + "would report: the engine drops it in silence and saves "
                          + "the rest, which is a design the caller did not ask for"
                        : "");
                return o;
            }
            if (illegal.Count > 0)
            {
                o["savedReason"] = "legal=true and " + illegal.Count + " part(s) "
                    + "the faction cannot build; nothing was registered. Pass "
                    + "legal=false to save it anyway, which is a fixture: the "
                    + "design becomes buildable without the research behind it";
                return o;
            }

            // The pair SaveShipDesignAction.Execute runs, in its order
            // (IL_0013, IL_001e). SaveShipDesign is what calls TemplateManager.Add,
            // so nothing above this line is registered anywhere.
            try
            {
                design.SetDisplayName(name);
                // InitAtRunTime(skipNaming: true) leaves hasDisplayName false, and
                // SetClassDisplayName would then overwrite the name just set. The
                // designer's own path ends with the flag true because it inits with
                // skipNaming false; this is that same end state.
                design.hasDisplayName = true;
                design.CacheTemplateValues(false);
                faction.SaveShipDesign(design);
            }
            catch (Exception e)
            {
                throw new VerbError("saving the design threw: " + Note(e)
                    + ". SaveShipDesign is the only call in that block that "
                    + "registers anything and it is the last statement, so a "
                    + "throw from SetDisplayName or CacheTemplateValues leaves "
                    + "nothing registered; query.designs faction="
                    + (int)faction.ID + " says whether it landed");
            }

            o["saved"] = Registered(faction, design);
            o["displayName"] = Safe<string>(
                delegate { return design.displayName; }, null);
            if (!(bool)o["saved"])
                o["warning"] = "SaveShipDesign returned without throwing but the "
                    + "design is not in the faction's list; nothing else in the "
                    + "engine removes one at that point";
            return o;
        }

        struct ArmorArg
        {
            public string Material;
            public int Value;
        }

        static ArmorArg ParseArmor(JObject armor, string facing)
        {
            JObject o = armor[facing] as JObject;
            if (o == null)
                throw new VerbError("arg 'armor." + facing + "' is required and "
                    + "must be an object {material, value}; ValidTemplate tests all "
                    + "three facings and a missing one fails it. Nothing was created");
            var a = new ArmorArg();
            a.Material = RequiredText(o, "material");
            a.Value = Int(o, "value");
            return a;
        }

        static List<ModuleDataTemplateEntry> ParseEntries(JObject args, string key)
        {
            var list = new List<ModuleDataTemplateEntry>();
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return list;
            JArray a = t as JArray;
            if (a == null)
                throw new VerbError("arg '" + key + "' must be an array of "
                    + "{slot, name}; nothing was created");
            if (a.Count > MaxDesignEntries)
                throw new VerbError("arg '" + key + "' holds " + a.Count
                    + " entries, over the cap of " + MaxDesignEntries
                    + "; nothing was created");
            for (int i = 0; i < a.Count; i++)
            {
                JObject e = a[i] as JObject;
                // A bare string would need this verb to invent a slot number, and
                // a slot chosen here is a slot the caller did not ask for.
                if (e == null)
                    throw new VerbError("arg '" + key + "'[" + i + "] must be an "
                        + "object {slot, name}: 'slot' is the index into the hull's "
                        + "shipModuleSlots list and there is no sane default for "
                        + "it. Nothing was created");
                var entry = new ModuleDataTemplateEntry();
                entry.moduleName = RequiredText(e, "name");
                entry.slot = Int(e, "slot");
                // ValidAssignedSlotForLocation tests only the upper bound
                // (slot >= shipModuleSlots.Count, IL_0000-IL_0011), so a negative
                // slot passes it and then throws in shipModuleSlots[slot] two
                // instructions later. Every path that reads the finished design
                // walks the same indexer, CacheTemplateValues included, so a
                // negative accepted here is an engine exception rather than an
                // answer about the design.
                if (entry.slot < 0)
                    throw new VerbError("arg '" + key + "'[" + i + "] has slot "
                        + entry.slot + "; a slot is an index into the hull's "
                        + "shipModuleSlots list and cannot be negative. Nothing "
                        + "was created");
                list.Add(entry);
            }
            return list;
        }

        static List<FireModeDataTemplateEntry> ParseFireModes(JObject args, string key)
        {
            var list = new List<FireModeDataTemplateEntry>();
            JToken t = args != null ? args[key] : null;
            if (t == null || t.Type == JTokenType.Null) return list;
            JArray a = t as JArray;
            if (a == null)
                throw new VerbError("arg '" + key + "' must be an array of "
                    + "{slot, mode}; nothing was created");
            if (a.Count > MaxDesignEntries)
                throw new VerbError("arg '" + key + "' holds " + a.Count
                    + " entries, over the cap of " + MaxDesignEntries
                    + "; nothing was created");
            for (int i = 0; i < a.Count; i++)
            {
                JObject e = a[i] as JObject;
                if (e == null)
                    throw new VerbError("arg '" + key + "'[" + i + "] must be an "
                        + "object {slot, mode}. Nothing was created");
                var entry = new FireModeDataTemplateEntry();
                entry.slot = Int(e, "slot");
                entry.fireMode = ParseEnum<FireMode>(RequiredText(e, "mode"), "fire mode");
                list.Add(entry);
            }
            return list;
        }

        static TISpaceShipTemplate Build(TIFactionState faction, string dataName,
            string hull, string drive, string powerPlant, string radiator,
            int tanks, ShipRole role, ArmorArg nose, ArmorArg lateral,
            ArmorArg tail, List<ModuleDataTemplateEntry> modules,
            List<ModuleDataTemplateEntry> noseWeapons,
            List<ModuleDataTemplateEntry> hullWeapons,
            List<FireModeDataTemplateEntry> fireModes)
        {
            var design = new TISpaceShipTemplate(dataName);
            design.factionName = faction.templateName;
            design.hullName = hull;
            // skipNaming true: the caller's display name is set at save time, and
            // SetClassDisplayName would generate one from a half-built design.
            design.InitAtRunTime(true);
            design.role = role;
            design.SetDriveTemplate(drive);
            design.SetPowerPlantTemplate(powerPlant);
            design.SetRadiatorTemplate(radiator);

            design.noseArmor.materialName = nose.Material;
            design.TrySetArmor(ShipModuleSlotType.NoseArmor, nose.Value);
            design.lateralArmor.materialName = lateral.Material;
            design.TrySetArmor(ShipModuleSlotType.LateralArmor, lateral.Value);
            design.tailArmor.materialName = tail.Material;
            design.TrySetArmor(ShipModuleSlotType.TailArmor, tail.Value);

            design.noseWeaponTemplateEntries = noseWeapons;
            design.hullWeaponTemplateEntries = hullWeapons;
            design.moduleTemplateEntries = modules;
            if (fireModes.Count > 0) design.fireModeTemplateEntries = fireModes;
            design.propellantTanks = tanks;
            return design;
        }

        static JToken ArmorReport(TISpaceShipTemplate design)
        {
            var o = new JObject();
            o["nose"] = Facing(design.noseArmor);
            o["lateral"] = Facing(design.lateralArmor);
            o["tail"] = Facing(design.tailArmor);
            return o;
        }

        // The value read back, not the value asked for: TrySetArmor clamps to
        // [0, GetMaxAllowedArmorBySlot] before it writes (IL_0000-IL_0011), so a
        // request over what the hull holds lands lower and the caller has to be
        // able to see that it did.
        static JToken Facing(ArmorFacingTemplate facing)
        {
            var o = new JObject();
            o["material"] = facing.materialName;
            o["value"] = facing.armorValue;
            o["materialFound"] = Safe<bool>(
                delegate { return facing.materialTemplate != null; }, false);
            return o;
        }

        static JArray ValidityChecks(TISpaceShipTemplate design, ShipRole role)
        {
            var a = new JArray();
            Check(a, "hull", Safe<bool>(
                delegate { return design.hullTemplate != null; }, false),
                "hullName resolves to a TIShipHullTemplate");
            Check(a, "drive", Safe<bool>(
                delegate { return design.driveTemplate != null; }, false),
                "driveName resolves to a TIDriveTemplate");
            Check(a, "radiator", Safe<bool>(
                delegate { return design.radiatorTemplate != null; }, false),
                "radiatorName resolves to a TIRadiatorTemplate");
            Check(a, "powerPlant", Safe<bool>(
                delegate { return design.powerPlantTemplate != null; }, false),
                "powerPlantName resolves to a TIPowerPlantTemplate");
            Check(a, "propellantTanks", design.propellantTanks > 0,
                "propellantTanks is greater than 0");
            Check(a, "noseArmor", Safe<bool>(
                delegate { return design.noseArmorTemplate != null; }, false),
                "noseArmor.materialName resolves to a TIShipArmorTemplate");
            Check(a, "lateralArmor", Safe<bool>(
                delegate { return design.lateralArmorTemplate != null; }, false),
                "lateralArmor.materialName resolves to a TIShipArmorTemplate");
            Check(a, "tailArmor", Safe<bool>(
                delegate { return design.tailArmorTemplate != null; }, false),
                "tailArmor.materialName resolves to a TIShipArmorTemplate");
            Check(a, "role", role != ShipRole.NoRole,
                "role is not NoRole");
            // Last, and only meaningful once the module list is in place.
            // AllowedRole switches on the role ordinal and covers 0 through 6,
            // NoRole through EarthSurveillance inclusive; ordinal 0 returns a
            // constant false (IL_00fd) and the other six ask whether the design
            // carries the module that role requires. Every ordinal past 6, which
            // is every combat and transport role, falls to the default and
            // answers true (IL_00ff).
            Check(a, "allowedRole", Safe<bool>(
                delegate { return design.AllowedRole(role); }, false),
                "AllowedRole(" + role + "): the modules the role requires are on "
                + "the design");
            return a;
        }

        static void Check(JArray a, string name, bool ok, string what)
        {
            var o = new JObject();
            o["check"] = name;
            o["ok"] = ok;
            o["test"] = what;
            a.Add(o);
        }

        static string FirstFailure(JArray checks)
        {
            for (int i = 0; i < checks.Count; i++)
            {
                JObject o = checks[i] as JObject;
                if (o == null) continue;
                if (!(bool)o["ok"]) return (string)o["check"];
            }
            return null;
        }

        static JArray Dropped(TISpaceShipTemplate design,
            List<ModuleDataTemplateEntry> entries, string getter, bool weapon)
        {
            var a = new JArray();
            for (int i = 0; i < entries.Count; i++)
            {
                ModuleDataTemplateEntry e = entries[i];
                string why = DropReason(design, e, getter, weapon);
                if (why == null) continue;
                var o = new JObject();
                o["index"] = i;
                o["name"] = e.moduleName;
                o["slot"] = e.slot;
                o["reason"] = why;
                a.Add(o);
            }
            return a;
        }

        // The first dropped entry across the three lists, named the way `reason`
        // names a failed predicate. Null when every entry survived.
        static string DropFailure(JArray modules, JArray nose, JArray hull)
        {
            var lists = new JArray[] { modules, nose, hull };
            var where = new string[] { "module", "noseWeapon", "hullWeapon" };
            for (int i = 0; i < lists.Length; i++)
            {
                if (lists[i] == null || lists[i].Count == 0) continue;
                JObject o = lists[i][0] as JObject;
                if (o == null) return where[i] + " dropped";
                return "dropped " + where[i] + "[" + o["index"] + "] '"
                    + o["name"] + "'";
            }
            return null;
        }

        // The four tests the engine's own getter applies, in its order. Null
        // rather than a reason means the entry survives them. get_utilityModules
        // resolves the name through TIShipModuleTemplate and the two weapon
        // getters through TIShipWeaponTemplate; that lookup is the only
        // difference between them, so `weapon` picks it and nothing else.
        static string DropReason(TISpaceShipTemplate design,
            ModuleDataTemplateEntry entry, string getter, bool weapon)
        {
            string kind = weapon ? "TIShipWeaponTemplate" : "TIShipModuleTemplate";
            if (string.IsNullOrEmpty(entry.moduleName))
                return "the part name is empty, and " + getter + " skips an "
                    + "empty name without a word";
            if (string.Equals(entry.moduleName, "Empty", StringComparison.Ordinal))
                return "\"Empty\" is the engine's own marker for an unused slot, "
                    + "so the entry is skipped by name";
            TIShipPartTemplate part = ResolvePart(entry.moduleName, weapon);
            if (part == null)
                return "no " + kind + " named '" + entry.moduleName + "', which is "
                    + "the lookup " + getter + " makes";
            bool fits = Safe<bool>(delegate
            {
                return design.ValidAssignedSlotForLocation(part, entry.slot);
            }, false);
            if (!fits)
                return "slot " + entry.slot + " does not take '" + entry.moduleName
                    + "': ValidAssignedSlotForLocation refuses it, either because "
                    + "the slot index is past the hull's shipModuleSlots list, or "
                    + "because the slot's moduleSlotType is not in the part's "
                    + "allowedSlots, or because the part is a weapon whose mount "
                    + "the hull does not carry";
            return null;
        }

        static JArray IllegalParts(TIFactionState faction,
            TISpaceShipTemplate design, List<ModuleDataTemplateEntry> modules,
            List<ModuleDataTemplateEntry> noseWeapons,
            List<ModuleDataTemplateEntry> hullWeapons)
        {
            var a = new JArray();
            AddIfIllegal(a, faction, "hull", Safe<TIShipPartTemplate>(
                delegate { return design.hullTemplate; }, null));
            AddIfIllegal(a, faction, "drive", Safe<TIShipPartTemplate>(
                delegate { return design.driveTemplate; }, null));
            AddIfIllegal(a, faction, "powerPlant", Safe<TIShipPartTemplate>(
                delegate { return design.powerPlantTemplate; }, null));
            AddIfIllegal(a, faction, "radiator", Safe<TIShipPartTemplate>(
                delegate { return design.radiatorTemplate; }, null));
            AddIfIllegal(a, faction, "noseArmor", Safe<TIShipPartTemplate>(
                delegate { return design.noseArmorTemplate; }, null));
            AddIfIllegal(a, faction, "lateralArmor", Safe<TIShipPartTemplate>(
                delegate { return design.lateralArmorTemplate; }, null));
            AddIfIllegal(a, faction, "tailArmor", Safe<TIShipPartTemplate>(
                delegate { return design.tailArmorTemplate; }, null));
            AddEntriesIfIllegal(a, faction, "module", modules, false);
            AddEntriesIfIllegal(a, faction, "noseWeapon", noseWeapons, true);
            AddEntriesIfIllegal(a, faction, "hullWeapon", hullWeapons, true);
            return a;
        }

        static void AddEntriesIfIllegal(JArray a, TIFactionState faction,
            string where, List<ModuleDataTemplateEntry> entries, bool weapon)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                string name = entries[i].moduleName;
                if (string.IsNullOrEmpty(name)) continue;
                if (string.Equals(name, "Empty", StringComparison.Ordinal)) continue;
                TIShipPartTemplate part = ResolvePart(name, weapon);
                // A name that resolves to nothing is a validity or drop problem,
                // and both are already reported; calling it illegal too would name
                // one fault twice.
                if (part == null) continue;
                AddIfIllegal(a, faction, where + "[" + i + "]", part);
            }
        }

        static void AddIfIllegal(JArray a, TIFactionState faction, string where,
            TIShipPartTemplate part)
        {
            if (part == null) return;
            if (Safe<bool>(delegate { return part.FactionCanBuild(faction); }, true))
                return;
            var o = new JObject();
            o["where"] = where;
            o["part"] = Safe<string>(delegate { return part.dataName; }, null);
            o["requiredProject"] = Safe<JToken>(delegate
            {
                TIProjectTemplate project = part.requiredProject;
                return project != null ? (JToken)new JValue(project.dataName)
                                       : JValue.CreateNull();
            }, JValue.CreateNull());
            a.Add(o);
        }

        static bool Registered(TIFactionState faction, TISpaceShipTemplate design)
        {
            return Safe<bool>(delegate
            {
                List<TISpaceShipTemplate> designs = faction.shipDesigns;
                return designs != null && designs.Contains(design);
            }, false);
        }

        // The lookup each of the engine's three part getters makes: a module
        // entry through TIShipModuleTemplate, a weapon entry through
        // TIShipWeaponTemplate. That choice is the only difference between
        // them, so `weapon` picks it and nothing else.
        static TIShipPartTemplate ResolvePart(string name, bool weapon)
        {
            if (weapon)
                return Safe<TIShipWeaponTemplate>(delegate
                {
                    return TemplateManager.Find<TIShipWeaponTemplate>(name, true);
                }, null);
            return Safe<TIShipModuleTemplate>(delegate
            {
                return TemplateManager.Find<TIShipModuleTemplate>(name, true);
            }, null);
        }

        // The gate the ship designer's own class-name field runs. It trims the
        // text, then asks TISpaceShipTemplate.illegalShipClassNames whether it
        // holds it (OnDesignerClassNameChanged IL_000b-IL_001c). That is
        // List<string>.Contains, so the comparison is ordinal and
        // case-sensitive. The list is stored nowhere: its getter rebuilds it on
        // every access from the display name of every ship template
        // TemplateManager holds (IL_0006-IL_002d) followed by the display name
        // of every ship state that is not archived (IL_003b-IL_006d). So a
        // fixture ship named Testbed blocks a design named Testbed even though
        // no design carries the name, and a design that saved under a taken
        // name would be one the designer screen refuses to let a player make.
        //
        // `name` arrives trimmed from RequiredText, which is the same text the
        // name field passes.
        static void RefuseTakenClassName(string name)
        {
            List<string> taken = Safe<List<string>>(delegate
            {
                return TISpaceShipTemplate.illegalShipClassNames;
            }, null);
            if (taken == null) return;
            if (!Safe<bool>(delegate { return taken.Contains(name); }, false))
                return;

            string holder = DesignWithClassName(name);
            if (holder != null)
                throw new VerbError("class name '" + name + "' is already the "
                    + "display name of design '" + holder + "', which is what "
                    + "the engine's illegalShipClassNames refuses. Pick another "
                    + "name, or free this one with design.delete. Nothing was "
                    + "created");
            throw new VerbError("class name '" + name + "' is in the engine's "
                + "illegalShipClassNames and no design holds it, so a ship in "
                + "play carries this class name: the list takes the display "
                + "name of every ship state that is not archived as well as "
                + "every design's. Pick another name, or take that ship out of "
                + "the campaign. Nothing was created");
        }

        // Which design holds the name, for the refusal above. Walked over
        // TemplateManager rather than one faction's designs, the way the list
        // itself is built: illegalShipClassNames is global, so the design that
        // blocks a name is often another faction's.
        static string DesignWithClassName(string name)
        {
            return Safe<string>(delegate
            {
                foreach (TISpaceShipTemplate d in
                         TemplateManager.IterateByClass<TISpaceShipTemplate>(true))
                {
                    if (d == null) continue;
                    string display = Safe<string>(
                        delegate { return d.displayName; }, null);
                    if (string.Equals(display, name, StringComparison.Ordinal))
                        return d.dataName;
                }
                return null;
            }, null);
        }

        // Every entry on a design indexes one list, the hull's shipModuleSlots,
        // whichever of the three arguments it arrived in: get_utilityModules,
        // get_noseWeapons and get_hullWeapons all hand the entry's slot to
        // ValidAssignedSlotForLocation, which indexes shipModuleSlots with it
        // (IL_0016-IL_0032). Nothing in the engine notices two entries sitting
        // on the same slot. The design saves and reads back valid, and which of
        // the two parts the finished ship carries depends on the order its part
        // lists happen to be read in, so the caller gets a ship that is not the
        // one asked for.
        static void RefuseSlotCollisions(TIShipHullTemplate hull,
            List<ModuleDataTemplateEntry> modules,
            List<ModuleDataTemplateEntry> noseWeapons,
            List<ModuleDataTemplateEntry> hullWeapons)
        {
            var taken = new Dictionary<int, string>();
            ClaimSlots(taken, hull, modules, "modules", false);
            ClaimSlots(taken, hull, noseWeapons, "nose_weapons", true);
            ClaimSlots(taken, hull, hullWeapons, "hull_weapons", true);
        }

        static void ClaimSlots(Dictionary<int, string> taken,
            TIShipHullTemplate hull, List<ModuleDataTemplateEntry> entries,
            string key, bool weapon)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                ModuleDataTemplateEntry e = entries[i];
                // An empty name, the literal "Empty" and a name that resolves
                // to no template are the three the engine's getters skip
                // without a word, so they hold no slot. Dropped reports all
                // three, and claiming a slot for one of them would refuse a
                // design over an entry that was never going to be on it.
                if (string.IsNullOrEmpty(e.moduleName)) continue;
                if (string.Equals(e.moduleName, "Empty", StringComparison.Ordinal))
                    continue;
                TIShipPartTemplate part = ResolvePart(e.moduleName, weapon);
                if (part == null) continue;

                List<int> slots = OccupiedSlots(hull, part, e.slot);
                string who = key + "[" + i + "] '" + e.moduleName + "' on slot "
                    + e.slot + Spread(slots, MountName(part));
                for (int s = 0; s < slots.Count; s++)
                {
                    string other;
                    if (taken.TryGetValue(slots[s], out other))
                        throw new VerbError("slot " + slots[s] + " is claimed "
                            + "twice: by " + other + ", and by " + who + ". All "
                            + "three entry lists index the same hull "
                            + "shipModuleSlots list, and the engine saves a "
                            + "design with two parts on one slot without a "
                            + "word, so which one the finished ship carries "
                            + "depends on the order its part lists are read in. "
                            + "Nothing was created");
                    taken[slots[s]] = who;
                }
            }
        }

        // Which slots the part actually holds. ValidAssignedSlotForLocation
        // reads the part's ref_weapon and switches on its mount
        // (IL_003c-IL_0053). A part that is not a weapon, and a weapon on a
        // single-slot mount, takes the slot named and no more. The four hull
        // mounts TwoHullHoriz through FourHull and the four nose mounts
        // TwoNoseHoriz through FourNose go down the other arm (IL_0098), which
        // walks hullTemplate.ValidBigWeaponSlotSets(mount) and accepts the
        // entry only where the entry's slot is the slotIndex of the set's FIRST
        // element (IL_00b9-IL_00cc). A set is keyed on its first slot, and the
        // weapon on it occupies every slot in the set.
        //
        // A big weapon whose slot keys no set is left holding its own slot
        // here. The engine drops such an entry and Dropped reports that;
        // expanding it to a set it does not key would name the wrong fault.
        static List<int> OccupiedSlots(TIShipHullTemplate hull,
            TIShipPartTemplate part, int slot)
        {
            var slots = new List<int>();
            slots.Add(slot);
            try
            {
                TIShipWeaponTemplate weapon = part.ref_weapon;
                if (weapon == null) return slots;
                if (!BigMount(weapon.mount)) return slots;
                foreach (var set in hull.ValidBigWeaponSlotSets(weapon.mount))
                {
                    if (set == null || set.Count == 0) continue;
                    if (hull.slotIndex(set[0]) != slot) continue;
                    for (int i = 1; i < set.Count; i++)
                    {
                        int other = hull.slotIndex(set[i]);
                        if (other >= 0 && !slots.Contains(other)) slots.Add(other);
                    }
                    break;
                }
            }
            // A read off the hull or the part that throws leaves the entry
            // holding its own slot. No VerbError is raised inside this block,
            // so a refusal above is never swallowed here.
            catch (Exception) { }
            return slots;
        }

        // The two ranges ValidBigWeaponSlotSets answers for: mounts
        // TwoHullHoriz through FourHull over the hull hard points, TwoNoseHoriz
        // through FourNose over the nose hard points. Every other mount value
        // falls past both tests to the empty list (IL_0006-IL_0013).
        static bool BigMount(Mount mount)
        {
            return (mount >= Mount.TwoHullHoriz && mount <= Mount.FourHull)
                || (mount >= Mount.TwoNoseHoriz && mount <= Mount.FourNose);
        }

        static string MountName(TIShipPartTemplate part)
        {
            return Safe<string>(delegate
            {
                TIShipWeaponTemplate weapon = part.ref_weapon;
                return weapon != null ? weapon.mount.ToString() : null;
            }, null);
        }

        // Said only when the entry holds more than the slot it named, which is
        // the big-weapon case: the refusal has to explain where the other slots
        // came from.
        static string Spread(List<int> slots, string mount)
        {
            if (slots.Count < 2) return "";
            var text = new List<string>();
            for (int i = 0; i < slots.Count; i++)
                text.Add(slots[i].ToString(CultureInfo.InvariantCulture));
            return " (a " + (mount != null ? mount : "multi-slot")
                + " mount, which holds slots "
                + string.Join(", ", text.ToArray()) + ")";
        }

        #endregion

        #region design.delete

        // Ship ids named in a refusal before the list is cut.
        const int MaxShipsListed = 10;

        // Deleting is gated by the engine, not by this verb.
        // TIFactionState.DeleteShipDesign opens with get_CanDeleteDesign and
        // returns in silence when it is false (IL_0000-IL_0006). So the gate is
        // read first and reported, and the faction's list is read back afterwards
        // rather than the delete being assumed.
        //
        // The gate has three false arms and only the third is about a queue, so a
        // refusal that blamed one would send most callers looking in the wrong
        // place. In the engine's order:
        //   - IL_0000-IL_0034: the designing faction's AISavingTarget is active
        //     and its desiredPurchase is this design. The AI is saving up to buy
        //     it, which no test does on purpose but a long unattended run reaches.
        //   - IL_0035-IL_004f: any TISpaceShipState that is not deleted carries
        //     this design's dataName as its templateName. That is a LIVE SHIP of
        //     the class, not a queued build, and it is the arm a test hits,
        //     because spawn.fleet builds ships of a design in one call.
        //   - IL_0050-IL_0114: a shipyard queue on any faction holds the design as
        //     a build or as the original of a refit.
        // The first two are read here so the reply names the arm that fired; the
        // third is what remains when neither of them did.
        static JToken DesignDelete(JObject args)
        {
            TIFactionState faction = Arg<TIFactionState>(args, "faction");
            string wanted = Str(args, "design");
            if (string.IsNullOrEmpty(wanted))
                throw new VerbError("arg 'design' is required: the design's data "
                    + "name or its display name. Nothing was deleted");
            // The spawn.fleet resolver, so one spelling works for both verbs.
            TISpaceShipTemplate design = Design(faction, wanted);

            var o = new JObject();
            o["faction"] = Describe(faction);
            o["design"] = DescribeDesign(design);
            bool can = Safe<bool>(delegate { return design.CanDeleteDesign; }, false);
            o["canDelete"] = can;
            if (!can)
            {
                o["deleted"] = false;
                o["reason"] = "CanDeleteDesign is false, so DeleteShipDesign would "
                    + "return without doing anything and nothing was attempted. "
                    + DeleteBlocker(design);
                return o;
            }

            try { faction.DeleteShipDesign(design); }
            catch (Exception e)
            {
                throw new VerbError("DeleteShipDesign threw: " + Note(e)
                    + ". The design may be half removed; query.designs faction="
                    + (int)faction.ID + " says what is left");
            }

            bool still = Registered(faction, design);
            o["deleted"] = !still;
            o["templateRegistered"] = Safe<bool>(delegate
            {
                return TemplateManager.Find<TISpaceShipTemplate>(
                    design.dataName, false) != null;
            }, false);
            if (still)
                o["warning"] = "CanDeleteDesign was true and DeleteShipDesign "
                    + "returned without throwing, but the design is still in the "
                    + "faction's list";
            return o;
        }

        // Which of the gate's three arms refused, checked in the gate's own
        // order. The first two are cheap to reproduce; the third is the shipyard
        // walk, and reaching the same answer would mean re-walking every faction's
        // queues, so it is reported as what remains rather than re-tested.
        static string DeleteBlocker(TISpaceShipTemplate design)
        {
            string saving = Safe<string>(delegate
            {
                TIFactionState designer = design.designingFaction;
                if (designer == null) return null;
                AISavingData target = designer.AISavingTarget;
                if (target == null || !target.active) return null;
                if (!ReferenceEquals(target.desiredPurchase, design)) return null;
                return "The designing faction's AI is saving up to buy this design "
                    + "(AISavingTarget.active with desiredPurchase set to it). "
                    + "Nothing in the harness clears a saving target; give the AI "
                    + "the resources with grant, or delete a different design";
            }, null);
            if (saving != null) return saving;

            string ships = Safe<string>(delegate { return LiveShipsOfClass(design); },
                                        null);
            if (ships != null) return ships;

            return "Neither of the two arms this verb can read fired, which leaves "
                + "the third: a shipyard queue on some faction holds the design as "
                + "a ship under construction or as the original of a refit. Cancel "
                + "the build, or delete a design nothing is building";
        }

        // The engine's second arm, read the way its closure reads it: a
        // TISpaceShipState that is not deleted and whose templateName equals the
        // design's dataName. Null when no such ship exists.
        static string LiveShipsOfClass(TISpaceShipTemplate design)
        {
            string dataName = design.dataName;
            var ids = new List<string>();
            int total = 0;
            foreach (TISpaceShipState ship in
                     GameStateManager.IterateByClass<TISpaceShipState>(false))
            {
                if (ship == null) continue;
                if (Safe<bool>(delegate { return ship.deleted; }, false)) continue;
                if (!string.Equals(ship.templateName, dataName,
                                   StringComparison.Ordinal)) continue;
                total++;
                if (ids.Count < MaxShipsListed)
                    ids.Add(((int)ship.ID).ToString(CultureInfo.InvariantCulture));
            }
            if (total == 0) return null;
            return "There " + (total == 1 ? "is 1 live ship" : "are " + total
                    + " live ships") + " of this class in the campaign (id"
                + (total == 1 ? " " : "s ") + string.Join(", ", ids.ToArray())
                + (total > ids.Count ? ", ..." : "") + "), and the engine refuses "
                + "to delete a design something is still flying. spawn.fleet builds "
                + "ships of a design, so a fixture run reaches this arm first: take "
                + "those ships out of the campaign first, then delete";
        }

        #endregion

        #region design.auto

        // The engine's own designer, the one behind the ship designer's AUTODESIGN
        // button. FleetsScreenController.OnAutodesignSelected calls DesignShip on
        // the UI thread and reads its outcome on the next instruction (IL_00a9,
        // IL_00ae), so this runs in one frame the same way that button does. The
        // search is bounded by the faction's own allowed parts rather than by any
        // budget here, and there is no partial result to return: DesignShip either
        // hands back a design or an outcome saying why it could not.
        //
        // The design it returns is NOT registered. Registration is the caller's
        // step, which is the same CacheTemplateValues/SaveShipDesign pair
        // design.create runs.
        static JToken DesignAuto(JObject args)
        {
            TIFactionState faction = Arg<TIFactionState>(args, "faction");
            ShipRole role = ParseEnum<ShipRole>(RequiredText(args, "role"), "role");
            float range = Float(args, "range_au", 1f);
            bool exotics = Bool(args, "exotics", false);
            bool antimatter = Bool(args, "antimatter", false);
            bool save = Bool(args, "save", true);

            TISpaceShipTemplate design = null;
            TIFactionState.ShipDesignerOutcome outcome;
            try
            {
                // playerAutodesign true: the value the AUTODESIGN button passes.
                // The closure reads it in exactly two places, the calls to
                // SetShipDesignNoseWeapons (IL_021a) and SetShipDesignHullWeapons
                // (IL_0260), so it steers how those two pick weapons and nothing
                // else in the search.
                outcome = faction.DesignShip(true, role, out design, range,
                                             exotics, antimatter);
            }
            catch (Exception e)
            {
                throw new VerbError("DesignShip threw: " + Note(e)
                    + ". Nothing was registered: DesignShip does not save");
            }

            var o = new JObject();
            o["faction"] = Describe(faction);
            o["role"] = role.ToString();
            o["rangeAU"] = range;
            o["exotics"] = exotics;
            o["antimatter"] = antimatter;
            o["outcome"] = outcome.ToString();
            o["success"] = outcome == TIFactionState.ShipDesignerOutcome.Success;
            o["saved"] = false;
            if (design == null)
            {
                o["design"] = JValue.CreateNull();
                o["reason"] = "DesignShip returned " + outcome + " and no design";
                return o;
            }
            o["design"] = DescribeDesign(design);
            o["valid"] = Safe<bool>(delegate { return design.ValidTemplate; }, false);

            if (!save)
            {
                o["savedReason"] = "save=false, so the design was built and "
                    + "discarded; DesignShip registers nothing on its own";
                return o;
            }
            if (outcome != TIFactionState.ShipDesignerOutcome.Success)
            {
                o["savedReason"] = "the outcome was " + outcome
                    + ", so the design was not registered";
                return o;
            }

            try
            {
                design.CacheTemplateValues(false);
                faction.SaveShipDesign(design);
            }
            catch (Exception e)
            {
                throw new VerbError("saving the design threw: " + Note(e)
                    + ". SaveShipDesign is the statement after CacheTemplateValues "
                    + "and is the only one that registers anything, so a throw from "
                    + "the cache leaves nothing registered; query.designs faction="
                    + (int)faction.ID + " says whether it landed");
            }
            o["saved"] = Registered(faction, design);
            o["dataName"] = Safe<string>(delegate { return design.dataName; }, null);
            return o;
        }

        #endregion

        #region shared

        // By name, case-insensitively. Enum.Parse also takes any number, defined
        // or not, so IsDefined gates it the way Actions.cs does.
        static T ParseEnum<T>(string wanted, string label) where T : struct
        {
            try
            {
                object parsed = Enum.Parse(typeof(T), wanted.Trim(), true);
                if (Enum.IsDefined(typeof(T), parsed)) return (T)parsed;
            }
            catch (Exception) { }
            throw new VerbError("unknown " + label + " '" + wanted + "'; "
                + typeof(T).Name + " is "
                + string.Join(", ", Enum.GetNames(typeof(T)))
                + ". Nothing was created");
        }

        static string RequiredText(JObject args, string key)
        {
            string s = Str(args, key);
            if (string.IsNullOrEmpty(s))
                throw new VerbError("arg '" + key + "' is required; nothing was "
                    + "created");
            return s.Trim();
        }

        #endregion
    }
}
