using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

// Offline (CW-0012): the members Earth, World Map and Sea read from Cylinder World by reflection, each looked up the way
// that mod looks it up, so a rename, a changed type or a new overload fails here with the consumer's name instead of in
// their worlds. The list, with where each consumer binds it: docs/REFLECTION-SURFACE.md. Keep the two in step.
class Program
{
    static int checks, failures;
    const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;

    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (string dir in new[] { args[0], Path.Combine(args[0], "Lib"), Path.Combine(args[0], "Mods"), Path.GetDirectoryName(args[1]) })
            {
                string file = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
            }
            return null;
        };
        try { Run(AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]))); }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        Console.WriteLine(failures == 0 ? $"PASS {checks} reflection contract checks" : $"FAIL {failures} of {checks} reflection contract checks");
        return failures == 0 ? 0 : 1;
    }

    static void Check(bool ok, string consumer, string what)
    {
        checks++;
        if (ok) return;
        failures++;
        Console.Error.WriteLine($"FAIL {consumer} binds {what}");
    }

    /// <summary>Earth's CylinderCompat.Get/GetBool and World Map's WorldSource.Member: a public instance field, else property.</summary>
    static Type MemberType(Type t, string name, BindingFlags flags)
    {
        var f = t.GetField(name, flags);
        if (f != null) return f.FieldType;
        return t.GetProperty(name, flags)?.PropertyType;
    }

    static bool Numeric(Type t) => t != null && (t == typeof(int) || t == typeof(long) || t == typeof(float) || t == typeof(double) || t == typeof(short) || t == typeof(byte));

    /// <summary>World Map's Bind: a bare GetMethod(name) (one public method by that name, else it throws), then
    /// CreateDelegate to an exact signature.</summary>
    static MethodInfo Bare(Type t, string name, string consumer)
    {
        try
        {
            var m = t.GetMethod(name);
            Check(m != null, consumer, $"{t.Name}.{name} (by name alone)");
            return m;
        }
        catch (AmbiguousMatchException)
        {
            Check(false, consumer, $"{t.Name}.{name} by name alone, but it now has overloads (GetMethod throws AmbiguousMatchException)");
            return null;
        }
    }

    static void Signature(MethodInfo m, string consumer, Type ret, params Type[] parameters)
    {
        if (m == null) return;
        var ps = m.GetParameters().Select(x => x.ParameterType).ToArray();
        Check(!m.IsStatic && m.ReturnType == ret && ps.SequenceEqual(parameters), consumer,
            $"{m.DeclaringType.Name}.{m.Name} as {ret.Name}({string.Join(", ", parameters.Select(x => x.Name))}), found {m.ReturnType.Name}({string.Join(", ", ps.Select(x => x.Name))})");
    }

    static void Constant(Type t, string name, Type type, string consumer)
    {
        var f = t.GetField(name);
        Check(f != null && f.IsLiteral && f.FieldType == type, consumer, $"{t.Name}.{name} as a {type.Name} constant (GetRawConstantValue)");
    }

    static void Run(Assembly cw)
    {
        const string Earth = "Earth", Wmap = "World Map", Sea = "Sea";
        var core = cw.GetType("CylinderWorld.CylinderWorldCore");
        Check(core != null, "Earth, World Map and Sea", "the type CylinderWorld.CylinderWorldCore (found among the mod systems by full name)");
        if (core == null) return;

        // The core: Params and Config (Earth and Sea: GetProperty, public instance; World Map: field or property)
        var paramsProp = core.GetProperty("Params", PublicInstance);
        Check(paramsProp?.GetMethod?.IsPublic == true, "Earth, World Map and Sea", "CylinderWorldCore.Params, a public instance property");
        var configProp = core.GetProperty("Config", PublicInstance);
        Check(configProp?.GetMethod?.IsPublic == true, "Earth and World Map", "CylinderWorldCore.Config, a public instance property");
        var spawn = core.GetMethod("SetSpawn", PublicInstance, null, new[] { typeof(int), typeof(int) }, null);
        Check(spawn?.ReturnType == typeof(bool), Earth, "CylinderWorldCore.SetSpawn(int, int), returning bool");
        var key = core.GetField("SaveKey", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Check(key?.IsLiteral == true && (string)key.GetRawConstantValue() == "cylinderworld-seam", Earth,
            "the save key \"cylinderworld-seam\" (EarthMapSystem.CylinderRecordKey: its new-world test reads the record)");

        // The config switches Earth's preflight reads (field or property, true when set), and World Map's FlattenSeabed
        var config = configProp?.PropertyType;
        if (config != null)
        {
            foreach (string name in new[] { "Enabled", "PolarWalls", "PolarClimate" })
                Check(MemberType(config, name, PublicInstance) == typeof(bool), Earth, $"Config.{name} (bool)");
            Check(MemberType(config, "FlattenSeabed", PublicInstance) == typeof(bool), Wmap, "Config.FlattenSeabed (bool; worlds saved before 1.1 have no FlattenSeabedFrozen)");
        }

        var p = paramsProp?.PropertyType;
        if (p == null) return;
        // Numbers: Earth's Get (Convert.ToDouble), World Map's Num/Number (Convert.ToDouble/ToInt32)
        foreach (string name in new[] { "JumpInset", "Circumference", "EquatorZ", "PoleDistance", "SeamHalfWidth", "SeabedY", "SpawnX", "SpawnZ", "WallDepth", "Version", "SeaLevel" })
            Check(Numeric(MemberType(p, name, PublicInstance)), Earth, $"SeamParams.{name} (a number)");
        foreach (string name in new[] { "JumpInset", "Circumference", "EquatorZ", "PoleDistance", "Version", "SeabedY", "WallDepth", "WallTopY", "WallFaceMax", "PolarZoneDepth", "SeamWorkReach" })
            Check(Numeric(MemberType(p, name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)), Wmap, $"SeamParams.{name} (a number)");
        foreach (string name in new[] { "PolarClimate", "EarthPolarIce" })
            Check(MemberType(p, name, PublicInstance) == typeof(bool), Earth, $"SeamParams.{name} (bool)");
        foreach (string name in new[] { "IsPlanetLayout", "HasWalls", "EarthPolarIce", "FlattenSeabed" })
            Check(MemberType(p, name, PublicInstance) == typeof(bool), Wmap, $"SeamParams.{name} (bool)");
        // World Map 0.5.3 (WorldSource.Create): Member(params, "FlattenSeabedFrozen") is true, then SeamParams.FlattenSeabed;
        // else Config.FlattenSeabed. Renamed or no longer a bool, it reads as unset and every 1.1 world follows the config
        // silently (CW-D52)
        Check(MemberType(p, "FlattenSeabedFrozen", PublicInstance) == typeof(bool), Wmap,
            "SeamParams.FlattenSeabedFrozen (bool; set, World Map takes SeamParams.FlattenSeabed instead of the config's)");
        // Earth's TrySet: a writable public instance field
        var extend = p.GetField("ExtendTemperature", PublicInstance);
        Check(extend != null && extend.FieldType == typeof(bool) && !extend.IsInitOnly, Earth, "SeamParams.ExtendTemperature, a writable bool field (it switches it off)");
        // Sea: Circumference by GetProperty, JumpInset by GetField
        Check(p.GetProperty("Circumference") != null, Sea, "SeamParams.Circumference, a property");
        Check(p.GetField("JumpInset") != null, Sea, "SeamParams.JumpInset, a field");
        // World Map: SeamWorkReach must be a property (it tests GetProperty before binding SeamGround)
        Check(p.GetProperty("SeamWorkReach") != null, Wmap, "SeamParams.SeamWorkReach, a property");
        Constant(p, "IceCover", typeof(int), Wmap);
        Constant(p, "CurtainHalfWidth", typeof(int), Wmap);

        // World Map's delegates, CreateDelegate'd to these signatures
        foreach (var (name, ret, ps) in new[] {
            ("InSeamBand", typeof(bool), new[] { typeof(double) }),
            ("SeamBlendWeight", typeof(double), new[] { typeof(double) }),
            ("SeamGround", typeof(int), new[] { typeof(double), typeof(double), typeof(int) }),
            ("SeamFloorY", typeof(int), new[] { typeof(double), typeof(double) }),
            ("DistToEdge", typeof(double), new[] { typeof(double) }),
            ("DistToJumpLine", typeof(double), new[] { typeof(double) }),
            ("DistToZEdge", typeof(double), new[] { typeof(double) }),
            ("PolarOwnsSeabed", typeof(bool), new[] { typeof(double), typeof(double) }),
            ("PlaneCap", typeof(int), new[] { typeof(double), typeof(double) }),
            ("IsWallBlock", typeof(bool), new[] { typeof(double), typeof(int), typeof(double) }),
            ("OceanForce", typeof(int), new[] { typeof(double), typeof(double) }),
            ("EarthSnowEighths", typeof(int), new[] { typeof(double), typeof(double) }),
            ("GetPolarColumn", null, new[] { typeof(double), typeof(double) }) })
            Bound(p, name, ret, ps);
        var polar = Typed(p, "GetPolarColumn", typeof(double), typeof(double));
        if (polar != null)
        {
            // Expression.Field on the result, into World Map's PolarColumn(int, double, int, int, bool)
            foreach (var (name, type) in new[] { ("Floor", typeof(int)), ("ShelfBlend", typeof(double)), ("IceTop", typeof(int)), ("IceBottom", typeof(int)), ("Floe", typeof(bool)) })
                Check(polar.ReturnType.GetField(name, PublicInstance)?.FieldType == type, Wmap, $"PolarColumn.{name}, a {type.Name} field");
        }

        // The Earth polar model behind SeamParams.EarthPolar (World Map, Earth worlds)
        var earth = MemberType(p, "EarthPolar", PublicInstance);
        Check(earth != null, Wmap, "SeamParams.EarthPolar");
        if (earth != null)
        {
            Bound(earth, "Latitude", typeof(double), typeof(double));
            Constant(earth, "LifelessBorder", typeof(int), Wmap);
            Bound(earth, "Sample", null, typeof(double), typeof(double));
            var sample = Typed(earth, "Sample", typeof(double), typeof(double));
            if (sample != null)
            {
                var column = sample.ReturnType;
                Check(column.Name == "Column" && column.DeclaringType == earth, Wmap, "EarthPolarModel.Sample returning EarthPolarModel.Column");
                Bound(p, "HasArcticIce", typeof(bool), typeof(double), typeof(double), column);
                Bound(p, "EarthIceRise", typeof(int), typeof(double), typeof(double), column);
            }
        }

        // World Map's chart cache key: the ProtoMember fields, less runtime settings (named Mist*, JumpFog*, WallBuildMargin)
        var proto = p.GetFields(PublicInstance).Where(f => f.GetCustomAttributes(false).Any(a => a.GetType().Name == "ProtoMemberAttribute")).Select(f => f.Name).ToArray();
        Check(proto.Contains("FlattenSeabed") && proto.Contains("FlattenSeabedFrozen") && proto.Contains("Version") && proto.Contains("NoiseSeed") && proto.Contains("WallBuildMargin"), Wmap,
            "the saved layout's ProtoMember fields (its chart cache key)");
        // CW-0020: the runtime settings carry [RuntimeSetting], which a cache key can read instead of a name prefix
        var runtime = p.GetFields(PublicInstance).Where(f => f.GetCustomAttributes(false).Any(a => a.GetType().Name == "RuntimeSettingAttribute"))
            .Select(f => f.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check(runtime.SequenceEqual(new[] { "JumpFogMin", "JumpFogSeconds", "MistDensity", "MistMin", "MistRange", "WallBuildMargin" }), Wmap,
            "[RuntimeSetting] on exactly the mist, jump fog and wall build margin fields (found " + string.Join(", ", runtime) + ")");
    }

    /// <summary>World Map's lookup from 0.5.3 (Wmap c865281: GetMethod(name, Public | Instance, null, types, null)), with the return
    /// type it binds (null: any); and the published 0.5.0 to 0.5.3-rc.3's by name alone, which a public overload breaks.</summary>
    static void Bound(Type t, string name, Type ret, params Type[] ps)
    {
        var m = Typed(t, name, ps);
        string sig = $"{t.Name}.{name}({string.Join(", ", ps.Select(x => x.Name))})";
        Check(m != null && !m.IsStatic && (ret == null || m.ReturnType == ret), "World Map", sig + (ret == null ? "" : " returning " + ret.Name));
        Signature(Bare(t, name, "World Map 0.5.0 to 0.5.3-rc.3 (published)"), "World Map 0.5.0 to 0.5.3-rc.3 (published)", m?.ReturnType ?? ret ?? typeof(void), ps);
    }

    static MethodInfo Typed(Type t, string name, params Type[] ps) => t.GetMethod(name, PublicInstance, null, ps, null);
}
