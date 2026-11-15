using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using CylinderWorld;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

// Offline: real world attributes and the SeamParams packet; no game process or data directory.
// CW-D48 (the per-world seam mist switch, from the archived world-options line) and CW-D50 (polarEquatorDistance).
class Program
{
    static int checks;
    static int Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (string dir in new[] { args[0], Path.Combine(args[0], "Lib"), Path.Combine(args[0], "Mods"), args[1] })
            {
                string file = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(file);
            }
            return null;
        };
        try { Run(args[2]); Console.WriteLine($"PASS {checks} world-option checks"); return 0; }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        checks++;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run(string root)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var apply = typeof(CylinderWorldCore).GetMethod("ApplyMistSettings", flags);
        foreach (bool fallback in new[] { false, true })
        foreach (bool? choice in new bool?[] { null, false, true })
        foreach (bool asString in new[] { false, true })
        {
            var config = new CylinderWorldConfig { SeamMist = fallback, MistDensity = 0.025f, MistMin = 0.3f,
                MistRange = 321, JumpFogMin = 0.8f, JumpFogSeconds = 2.5f };
            var world = new TreeAttribute();
            if (choice.HasValue)
            {
                if (asString) world.SetString("cylinderSeamMist", choice.Value ? "true" : "false");
                else world.SetBool("cylinderSeamMist", choice.Value);
            }
            var p = new SeamParams { WorldWidth = 256000, JumpInset = 25600, Version = 10 };
            apply.Invoke(null, new object[] { p, config, world });
            bool enabled = choice ?? fallback;
            Check(p.MistDensity == (enabled ? config.MistDensity : 0) && p.MistRange == (enabled ? config.MistRange : 0)
                && p.JumpFogMin == (enabled ? config.JumpFogMin : 0), "world choice overrides either fallback; a missing key keeps the config's");
            Check(p.MistMin == config.MistMin && p.JumpFogSeconds == config.JumpFogSeconds, "other mist tuning kept");
            Check(config.SeamMist == fallback && world.HasAttribute("cylinderSeamMist") == choice.HasValue, "resolution never writes the config or a world default");
            var client = SerializerUtil.Deserialize<SeamParams>(SerializerUtil.Serialize(p));
            Check(client.MistDensity == p.MistDensity && client.MistRange == p.MistRange && client.JumpFogMin == p.JumpFogMin
                && client.MistMin == p.MistMin && client.JumpFogSeconds == p.JumpFogSeconds, "the SeamParams packet carries the effective settings to clients");
            Check(client.WorldWidth == 256000 && client.JumpInset == 25600 && client.Version == 10, "geometry and layout unchanged");
            world.SetBool("cylinderSeamMist", !enabled);
            apply.Invoke(null, new object[] { p, config, world });
            Check(p.MistRange == (!enabled ? config.MistRange : 0), "a changed option applies at the next start in both directions");
        }
        var clamped = new SeamParams();
        apply.Invoke(null, new object[] { clamped, new CylinderWorldConfig { JumpFogMin = 2, JumpFogSeconds = 0 }, new TreeAttribute() });
        Check(clamped.JumpFogMin == 1 && clamped.JumpFogSeconds == 0.1f, "fog clamps kept");
        var junk = new TreeAttribute(); junk.SetString("cylinderSeamMist", "maybe");
        var pj = new SeamParams();
        apply.Invoke(null, new object[] { pj, new CylinderWorldConfig { SeamMist = true }, junk });
        Check(pj.MistRange > 0, "an unreadable world value keeps the config's");

        using var declaration = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "worldconfig.json")));
        var json = declaration.RootElement;
        Check(json.GetProperty("playStyles").GetArrayLength() == 0, "non-null empty playStyles (the world list needs it)");
        var attrs = json.GetProperty("worldConfigAttributes");
        Check(attrs.GetArrayLength() == 1, "only the seam mist option is declared");
        var attr = attrs[0];
        Check(attr.GetProperty("code").GetString() == "cylinderSeamMist" && attr.GetProperty("category").GetString() == "earth"
            && attr.GetProperty("dataType").GetString() == "bool" && attr.GetProperty("default").GetString() == "false"
            && !attr.GetProperty("onlyDuringWorldCreate").GetBoolean(), "unticked by default (D40), changeable later");
        // (1.1.1) off the world-creation screen: vanilla still writes the default (false) into every new world
        // (WorldConfig.allDefaultValues) and /worldconfig still sets it, so only the screen changes
        Check(attr.TryGetProperty("onCustomizeScreen", out var shown) && shown.ValueKind == JsonValueKind.False,
            "not on the world-creation screen (onCustomizeScreen false); set with /worldconfig");
        using var lang = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "assets", "game", "lang", "worldconfig-en.json")));
        foreach (string key in new[] { "worldconfig-category-earth", "worldattribute-cylinderSeamMist", "worldattribute-cylinderSeamMist-desc",
            "worldconfig-cylinderSeamMist-true", "worldconfig-cylinderSeamMist-false" })
            Check(!string.IsNullOrWhiteSpace(lang.RootElement.GetProperty(key).GetString()), "English label: " + key);
        string csproj = File.ReadAllText(Path.Combine(root, "CylinderWorld.csproj"));
        Check(csproj.Contains("worldconfig.json") && !csproj.Contains("OldModZips"), "the zip carries worldconfig.json; the build deletes no other release zip (CW-0007)");

        // CW-0011 (with CW-D47): FlattenSeabed and ExtendTemperatureRange are saved with a new record; an older record
        // without the marker keeps reading the config at every start; the packet carries them to clients
        var freeze = typeof(CylinderWorldCore).GetMethod("FreezeWorldSettings", flags);
        var start = typeof(CylinderWorldCore).GetMethod("ApplyStartSettings", flags);
        foreach (bool made in new[] { false, true })
        {
            var fresh = new SeamParams { Version = 10, WorldWidth = 256000 };
            freeze.Invoke(null, new object[] { fresh, new CylinderWorldConfig { FlattenSeabed = made, ExtendTemperatureRange = made } });
            var saved = SerializerUtil.Deserialize<SeamParams>(SerializerUtil.Serialize(fresh));
            Check(saved.FlattenSeabedFrozen && saved.FlattenSeabed == made && saved.ExtendTemperatureFrozen && saved.ExtendTemperature == made,
                "a new record keeps the config's FlattenSeabed and ExtendTemperatureRange, marked frozen");
            start.Invoke(null, new object[] { saved, new CylinderWorldConfig { FlattenSeabed = !made, ExtendTemperatureRange = !made } });
            Check(saved.FlattenSeabed == made && saved.ExtendTemperature == made, "a frozen world ignores a changed config");
            var old = SerializerUtil.Deserialize<SeamParams>(SerializerUtil.Serialize(new SeamParams { Version = 10, WorldWidth = 256000 }));
            Check(!old.FlattenSeabedFrozen && !old.ExtendTemperatureFrozen, "a record saved before 1.1 has no marker");
            start.Invoke(null, new object[] { old, new CylinderWorldConfig { FlattenSeabed = made, ExtendTemperatureRange = made, WallBuildMargin = -5 } });
            Check(old.FlattenSeabed == made && old.ExtendTemperature == made && old.WallBuildMargin == 0, "an older record reads the config at every start");
            var client = SerializerUtil.Deserialize<SeamParams>(SerializerUtil.Serialize(saved));
            Check(client.FlattenSeabed == made, "the packet carries the world's FlattenSeabed");
        }
        // A world made without the flatten has no identical part to pair
        var twin = typeof(SeamBlocks).GetMethod("Twin", flags);
        var band = new SeamParams { Version = 10, WorldWidth = 256000, WorldLength = 102400, JumpInset = 25600, SeamHalfWidth = 26272, CoastFalloff = 1024 };
        bool Paired(SeamParams sp) => (bool)twin.Invoke(null, new object[] { sp, new Vintagestory.API.MathTools.BlockPos(25602, 120, 51211), null });
        band.FlattenSeabed = true;
        bool withFlatten = Paired(band);
        band.FlattenSeabed = false;
        Check(withFlatten && !Paired(band), "mirroring pairs the band's identical part only where the band was flattened");

        // CW-D50: polarEquatorDistance is the planet's pole distance in new worlds with this mod's latitude
        var ped = typeof(CylinderWorldCore).GetMethod("ApplyPolarEquatorDistance", flags);
        string Ped(SeamParams sp, string before, out string line)
        {
            var wc = new TreeAttribute();
            if (before != null) wc.SetString("polarEquatorDistance", before);
            line = (string)ped.Invoke(null, new object[] { wc, sp });
            return wc.HasAttribute("polarEquatorDistance") ? wc.GetAsString("polarEquatorDistance") : null;
        }
        var planet = new SeamParams { Version = 10, PolarClimate = true, WorldLength = 1024000, WallDepth = 48 };
        Check(Ped(planet, "50000", out string l1) == "511952" && l1 != null && l1.Contains("was 50000"), "a 1,024,000-long planet: 512,000 less the 48-block wall, over the tab's 50000");
        var earth100 = new SeamParams { Version = 10, PolarClimate = true, WorldLength = 200192, WallDepth = 48 };
        Check(Ped(earth100, "100000", out _) == "100048", "an Earth 1:100 world: the value Earth 0072 writes too");
        var noWalls = new SeamParams { Version = 10, PolarClimate = true, WorldLength = 8192 };
        Check(Ped(noWalls, null, out string l2) == "4096" && l2.Contains("not set"), "without walls: half the length");
        Check(Ped(new SeamParams { Version = 10, PolarClimate = false, WorldLength = 8192 }, "50000", out string l3) == "50000" && l3 == null,
            "no polar climate: vanilla's latitude stays, so its value stays");
        Check(Ped(new SeamParams { Version = 2, PolarClimate = true, WorldLength = 8192 }, "50000", out string l4) == "50000" && l4 == null,
            "an old (pre-planet) layout: not written");
        Check((string)ped.Invoke(null, new object[] { null, planet }) == null && (string)ped.Invoke(null, new object[] { new TreeAttribute(), null }) == null,
            "no world config or no params: nothing");
    }
}
