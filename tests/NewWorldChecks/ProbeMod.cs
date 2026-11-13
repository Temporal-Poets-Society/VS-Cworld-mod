using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using CylinderWorld;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

// Test-only mod, compiled and installed solely in a temporary server data directory by run.ps1. Reads Cylinder World by
// reflection where a member is newer than 1.0.4, so it also runs against the baseline build.
//
// Two worlds. The first ("stop", "retry", "reload"): a vanilla first start that stops at WorldReady. The second plays
// Earth's part with its "still creating" save key ("keystop", "keyretry", "stuck", "cleared", "badconfig"): the key is set
// at StartPre of the new save and the first start stops; the retry is a new world, takes a spawn by SetSpawn at WorldReady
// (as Earth does) and runs without clearing the key (Earth removed); the next start has the key and our record (stuck),
// and clears it as Earth would; then one start with it cleared, and one with a config file that fails to load.
public class NewWorldProbe : ModSystem
{
    const string EarthCreatingKey = "earth-creating";
    ICoreServerAPI api;
    string pass;
    bool finished;
    byte[] keyAtStart;
    bool? setSpawnAtWorldReady;
    int askedSpawnZ;
    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    // Earth's WorldReady step runs here: after Cylinder World's InitSeam (0.01), before its Earth binding (0.09)
    public override double ExecuteOrder() => 0.05;

    static bool KeyWorld(string pass) => pass is "keystop" or "keyretry" or "stuck" or "cleared" or "badconfig";
    static string KeyText(byte[] key) => key == null ? "absent" : "[" + string.Join(",", key) + "]";

    // Earth's bootstrap marks a new Earth world here, before the game writes the new save to disk (EarthVerticalBootstrap)
    public override void StartPre(ICoreAPI coreApi)
    {
        if (coreApi is not ICoreServerAPI sapi) return;
        pass = File.ReadAllText(Path.Combine(GamePaths.ModConfig, "newworld-pass.txt")).Trim();
        var save = sapi.WorldManager.SaveGame;
        keyAtStart = save.GetData(EarthCreatingKey);
        Mod.Logger.Notification("NEWWORLD {0}: earth-creating {1} at StartPre, IsNew={2}", pass, KeyText(keyAtStart), save.IsNew);
        if (pass == "keystop" && save.IsNew) save.StoreData(EarthCreatingKey, new byte[] { 1 });
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        if (pass is "stop" or "keystop")
        {
            api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, () =>
            {
                // What Earth does when a new world's data can't be had (EarthMapSystem.LoadData): stop and throw
                Mod.Logger.Notification("NEWWORLD stopping the first start at WorldReady");
                api.Server.ShutDown();
                throw new Exception("NEWWORLD test stop at WorldReady");
            });
            return;
        }
        if (KeyWorld(pass)) api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, AskSpawn);
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ => Check(), 500));
        api.Event.RegisterCallback(_ => End("FAIL timeout"), 180000);
    }

    /// <summary>As Earth's WorldReady step does for a world it has just created: hands Cylinder World a spawn. This one is
    /// nearer the wall than the plateaus allow but not than Earth's layout does, so it is taken while that layout is still
    /// asked for, and the binding's fallback to the plateaus (no Earth here) moves it out to their limit.</summary>
    void AskSpawn()
    {
        var core = api.ModLoader.GetModSystem<CylinderWorldCore>();
        var p = core.Params;
        if (p == null) return;
        askedSpawnZ = p.WallFaceMax + EarthPolarModel.LifelessBorder + 64;
        setSpawnAtWorldReady = core.SetSpawn(p.WorldWidth / 2, askedSpawnZ);
        Mod.Logger.Notification("NEWWORLD {0}: SetSpawn(z={1}) at WorldReady: {2}", pass, askedSpawnZ, setSpawnAtWorldReady);
    }

    string failed;

    void Check()
    {
        // the map check runs whatever this finds, so a baseline build shows both results
        try { CheckWorld(); }
        catch (Exception e)
        {
            failed = e.Message;
            Mod.Logger.Notification("NEWWORLD FAIL {0}: {1}", pass, e);
        }
        if (KeyWorld(pass)) { End(failed == null ? "PASS all" : "FAIL " + failed); return; }
        try { CheckMap(); }
        catch (Exception e) { End("FAIL " + e); }
    }

    void CheckWorld()
    {
        var core = api.ModLoader.GetModSystem<CylinderWorldCore>();
        var save = api.WorldManager.SaveGame;
        if (pass == "badconfig")
        {
            // The file says "Enabled": false next to a value of the wrong type: Cylinder World stays off
            if (core.Params != null || core.Config?.Enabled != false) throw new Exception("Cylinder World is on although its unreadable config says \"Enabled\": false");
            if (File.ReadAllText(Path.Combine(GamePaths.ModConfig, "newworld-keyspawn.txt")) != save.DefaultSpawn?.x + " " + save.DefaultSpawn?.z)
                throw new Exception("the default spawn changed while Cylinder World was off");
            Mod.Logger.Notification("NEWWORLD PASS {0}", pass);
            return;
        }
        var p = core.Params ?? throw new Exception("Cylinder World is not active");
        object isNew = typeof(CylinderWorldCore).GetProperty("IsNewWorld", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(core);
        Mod.Logger.Notification("NEWWORLD {0}: spawn x={1} z={2}, default spawn {3},{4}, IsNew={5}, IsNewWorld={6}, Earth polar={7}, story scaling {8}",
            pass, p.SpawnX, p.SpawnZ, save.DefaultSpawn?.x, save.DefaultSpawn?.z, save.IsNew, isNew ?? "n/a", p.EarthPolarIce,
            api.World.Config.GetDecimal("storyStructuresDistScaling", 1));
        if (p.EarthPolarIce) throw new Exception("Earth's polar layout kept in a world without Earth");
        // Past world generation's start the spawn can't be handed over any more
        if (core.SetSpawn(p.SpawnX, p.SpawnZ)) throw new Exception("SetSpawn accepted a call after world generation started");
        if (save.DefaultSpawn == null || save.DefaultSpawn.x != p.SpawnX || save.DefaultSpawn.z != p.SpawnZ) throw new Exception("the default spawn is not Cylinder World's");
        if (KeyWorld(pass)) CheckKeyWorld(p, save, isNew);
        else
        {
            string spawnFile = Path.Combine(GamePaths.ModConfig, "newworld-spawn.txt");
            if (pass == "retry")
            {
                // The first start stopped before anything was generated: this start must set the world up as new
                if (p.SpawnZ <= 0) throw new Exception("spawn was not moved: the world was taken for an existing one");
                if (isNew is false) throw new Exception("IsNewWorld false on the retry");
                File.WriteAllText(spawnFile, p.SpawnX + " " + p.SpawnZ);
            }
            else
            {
                if (File.ReadAllText(spawnFile) != p.SpawnX + " " + p.SpawnZ) throw new Exception("the spawn changed on reload");
                if (isNew is true) throw new Exception("IsNewWorld true on a reload");
            }
        }
        Mod.Logger.Notification("NEWWORLD PASS {0}", pass);
    }

    void CheckKeyWorld(SeamParams p, ISaveGame save, object isNew)
    {
        string spawnFile = Path.Combine(GamePaths.ModConfig, "newworld-keyspawn.txt");
        double plateauLimit = p.WallZoneDepth + SeamParams.PolarMargin;
        switch (pass)
        {
            case "keyretry":
                if (KeyText(keyAtStart) != "[1]") throw new Exception("the first start's earth-creating key was not on disk: " + KeyText(keyAtStart));
                if (isNew is not true) throw new Exception("IsNewWorld false while Earth's key says the world is still being created");
                if (askedSpawnZ >= plateauLimit) throw new Exception("the test spawn z=" + askedSpawnZ + " is not inside the plateaus' limit " + plateauLimit);
                if (setSpawnAtWorldReady != true) throw new Exception("SetSpawn at WorldReady was refused in a world being created");
                if (p.DistToZEdge(p.SpawnZ) < plateauLimit || p.SpawnZ > plateauLimit + 1)
                    throw new Exception("the generator's spawn z=" + askedSpawnZ + " was not moved to the plateaus' limit " + plateauLimit + " (spawn z=" + p.SpawnZ + ")");
                File.WriteAllText(spawnFile, p.SpawnX + " " + p.SpawnZ);
                // the key stays set: as if Earth were removed after its first start failed and the world played without it
                break;
            case "stuck":
                if (KeyText(keyAtStart) != "[1]") throw new Exception("the key did not stay on disk: " + KeyText(keyAtStart));
                if (isNew is not false) throw new Exception("a played world with Earth's key still set was taken for new");
                if (setSpawnAtWorldReady != false) throw new Exception("SetSpawn at WorldReady was accepted in a played world");
                if (File.ReadAllText(spawnFile) != p.SpawnX + " " + p.SpawnZ) throw new Exception("the spawn changed in a played world with the key set");
                // what Earth stores once its params are: an empty key, saved with the world at shutdown
                save.StoreData(EarthCreatingKey, Array.Empty<byte>());
                break;
            case "cleared":
                if (keyAtStart is { Length: > 0 }) throw new Exception("the cleared key came back as " + KeyText(keyAtStart));
                if (isNew is not false) throw new Exception("IsNewWorld true with the key cleared");
                if (setSpawnAtWorldReady != false) throw new Exception("SetSpawn at WorldReady was accepted with the key cleared");
                if (File.ReadAllText(spawnFile) != p.SpawnX + " " + p.SpawnZ) throw new Exception("the spawn changed with the key cleared");
                break;
        }
    }

    /// <summary>/cyl map from the server console: it renders once at a time and writes the PNG.</summary>
    void CheckMap()
    {
        string png = Path.Combine(api.GetOrCreateDataPath("CylinderWorld"), "worldmap-" + api.WorldManager.SaveGame.Seed + ".png");
        if (File.Exists(png)) File.Delete(png);
        var console = new TextCommandCallingArgs { Caller = new Caller { Type = EnumCallerType.Console, CallerPrivileges = new[] { "*" }, CallerRole = "admin", FromChatGroupId = GlobalConstants.ConsoleGroup } };
        api.ChatCommands.ExecuteUnparsed("/cyl map", console, r => Mod.Logger.Notification("NEWWORLD /cyl map: {0} {1}", r.Status, r.StatusMessage));
        var second = new TextCommandCallingArgs { Caller = console.Caller };
        EnumCommandStatus? again = null;
        api.ChatCommands.ExecuteUnparsed("/cyl map", second, r =>
        {
            again = r.Status;
            Mod.Logger.Notification("NEWWORLD /cyl map again at once: {0} {1}", r.Status, r.StatusMessage);
        });
        // one render at a time: the second, while the first still renders, is refused
        if (again != EnumCommandStatus.Error) failed ??= "the second /cyl map at once was not refused (" + (again?.ToString() ?? "no result") + ")";
        long deadline = api.World.ElapsedMilliseconds + 120000;
        void Poll(float _)
        {
            if (finished) return;
            bool done = false;
            try { done = File.Exists(png) && new FileInfo(png).Length > 0 && File.ReadAllBytes(png).Length > 0 && !IsLocked(png); } catch (IOException) { }
            if (done)
            {
                System.Threading.Thread.Sleep(200);
                File.Copy(png, Path.ChangeExtension(png, pass + ".png"), true);
                Mod.Logger.Notification("NEWWORLD map {0} sha256 {1}", pass, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(png))));
                End(failed == null ? "PASS all" : "FAIL " + failed);
                return;
            }
            if (api.World.ElapsedMilliseconds > deadline) { End("FAIL /cyl map wrote no picture"); return; }
            api.Event.RegisterCallback(Poll, 500);
        }
        api.Event.RegisterCallback(Poll, 500);
    }

    static bool IsLocked(string path)
    {
        try { using var f = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    void End(string result)
    {
        if (finished) return;
        finished = true;
        Mod.Logger.Notification("NEWWORLD " + result);
        api.Server.ShutDown();
    }
}
