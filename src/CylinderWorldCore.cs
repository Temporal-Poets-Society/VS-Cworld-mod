using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace CylinderWorld
{
    /// <summary>
    /// Owns config + seam geometry, persists the geometry in the savegame, syncs it to clients, and takes a world
    /// generator's spawn (SetSpawn). The /cyl command is in CylinderCommands.
    /// </summary>
    public class CylinderWorldCore : ModSystem
    {
        public const string ChannelName = "cylinderworld";
        const string SaveKey = "cylinderworld-seam";
        const string ConfigFile = "cylinderworld.json";

        /// <summary>Null until the world is loaded (server) or the params packet arrived (client), or if the mod is disabled.</summary>
        public SeamParams Params { get; private set; }
        public CylinderWorldConfig Config { get; private set; }
        /// <summary>Each side's own geometry, for Harmony patches on game code (in singleplayer one process runs both sides,
        /// so every patch reads the copy of the side it runs on). The client's copy has no Earth adapter or sea-ice date.</summary>
        internal static volatile SeamParams ServerShared, ClientShared;
        internal static SeamParams For(EnumAppSide side) => side == EnumAppSide.Server ? ServerShared : ClientShared;
        EnumAppSide side;

        /// <summary>Persist a new world's params again after the Earth adapter has chosen the automatic wall height,
        /// or has fallen back to the default layout because Earth Map isn't active.</summary>
        internal void SaveSeam() => sapi.WorldManager.SaveGame.StoreData(SaveKey, SerializerUtil.Serialize(Params));

        /// <summary>
        /// For a world generator that chooses its own spawn (Earth): makes (x, z) this world's spawn, so the story chain
        /// moves there instead of to the starting-climate latitude, and fits the story distance to it. New worlds only, from
        /// WorldReady until world generation starts (the story chain moves then). x is taken to the same place on the ring.
        /// Returns false when it is too late, Cylinder World isn't active, or z is in the polar zone (see SpawnPolarLimit).
        /// </summary>
        public bool SetSpawn(int x, int z)
        {
            var p = Params;
            var save = sapi?.WorldManager.SaveGame;
            if (p == null || save == null || !IsNewWorld || worldgenStarted || sapi.Server.CurrentRunPhase > EnumServerRunPhase.WorldReady) return false;
            if (z <= 0 || z >= p.WorldLength || p.HasWalls && p.DistToZEdge(z) < SpawnPolarLimit(p)) return false;
            // past a jump line, or anywhere: its place on the ring (x and x ± circumference are the same place)
            long ring = (long)p.Circumference;
            x = (int)(p.JumpInset + ((x - (long)p.JumpInset) % ring + ring) % ring);
            p.SpawnX = x;
            p.SpawnZ = z;
            save.DefaultSpawn = new PlayerSpawnPos(x, null, z);
            spawnFromGenerator = true;
            // the first fit was for the old spawn
            RefitStory();
            SaveSeam();
            Mod.Logger.Notification("Cylinder World: spawn set by the world generator to x={0} z={1} (latitude {2:0.#}°); the story locations follow it",
                x, z, p.SignedLatitude(z) * 90);
            return true;
        }

        /// <summary>SetSpawn took a world generator's spawn in this start.</summary>
        bool spawnFromGenerator;

        /// <summary>
        /// For the Earth binding when a new world falls back to our plateaus (Earth doesn't generate it): SetSpawn took a
        /// generator's spawn with Earth's polar limit while Earth's layout was still asked for. A spawn nearer the wall than
        /// the plateaus allow would lie under their ice sheet, so it moves to that limit (same x) and the story chain is
        /// fitted to it again. Before anything is generated; the caller saves the params.
        /// </summary>
        internal void KeepSpawnOffPlateaus()
        {
            var p = Params;
            if (!spawnFromGenerator || p == null || !p.HasWalls || p.EarthPolarIce) return;
            double limit = SpawnPolarLimit(p);
            if (p.DistToZEdge(p.SpawnZ) >= limit) return;
            // a world too short for the limit on both sides: as far from both walls as it gets
            int z = 2 * limit > p.WorldLength ? p.WorldLength / 2
                : p.SpawnZ < p.WorldLength / 2.0 ? (int)Math.Ceiling(limit) : (int)Math.Floor(p.WorldLength - limit);
            Mod.Logger.Warning("Cylinder World: the world generator's spawn z={0} is within {1:0} blocks of a wall, under the polar plateaus this world gets instead of Earth's layout; spawn moved to z={2}.",
                p.SpawnZ, limit, z);
            p.SpawnZ = z;
            sapi.WorldManager.SaveGame.DefaultSpawn = new PlayerSpawnPos(p.SpawnX, null, z);
            RefitStory();
        }

        /// <summary>The world's story distance scaling before <see cref="FitStoryDistance"/> first changed it (new worlds).</summary>
        double? storyScaleBeforeFit;

        /// <summary>World generation has started: our first InitWorldGenerator handler of a 'standard' world ran, and SeamWorldGen's,
        /// which moves the story chain to spawn, comes after it. The run phase is still WorldReady then.</summary>
        bool worldgenStarted;

        /// <summary>
        /// How close to either Z edge a world generator's spawn may be. Earth's polar layout has no ice sheet: there it is the
        /// wall and Earth's lifeless kilometre (Earth allows spawns up to 85°). With our plateaus it is the wall zone (wall
        /// and ice sheet) and PolarMargin, where our own spawn keeps clear of too. Decided before the Earth binding, by the
        /// layout the world asks for; a world that then falls back to the plateaus checks again (KeepSpawnOffPlateaus).
        /// </summary>
        static double SpawnPolarLimit(SeamParams p) =>
            p.EarthPolarIce ? p.WallFaceMax + EarthPolarModel.LifelessBorder : p.WallZoneDepth + SeamParams.PolarMargin;

        /// <summary>
        /// This world is still being created (set at WorldReady; InitSeam, SetSpawn and the Earth binding decide by it): a
        /// new save, or one whose first start stopped before the world ran, as Earth's does when it can't get its data (it
        /// stops the server in its WorldReady step). The game writes a new save to disk when it creates it, with what mods
        /// stored before that (Earth's "still creating" key, from StartPre), but saves nothing more until the world runs (it
        /// subscribes its save to OnGameWorldBeingSaved only at RunGame): on the next start SaveGame.IsNew is false and
        /// nothing stored from WorldReady on is there, our params included. So a world without our record is new while
        /// Earth's key says Earth is still creating it, or while nothing was generated; a world with our record has run.
        /// Public for a world generator that runs after our WorldReady step (Earth) and needs the same decision.
        /// </summary>
        public bool IsNewWorld { get; private set; }

        /// <summary>Earth 2.0's save key, non-empty while Earth is still creating the world (Earth's EarthMapSystem.IsNewWorld).</summary>
        const string EarthCreatingKey = "earth-creating";

        /// <summary>No chunk was ever saved where world generation starts: vanilla generates the spawn area around the map
        /// centre before anything else (ServerSystemSupplyChunks.InitWorldgenAndSpawnChunks), and players appear at the
        /// default spawn when one is set. A world with neither has generated nothing.</summary>
        bool NothingGenerated(ISaveGame save)
        {
            var wm = sapi.WorldManager;
            int size = GlobalConstants.ChunkSize;
            if (wm.BlockingTestMapChunkExists(wm.MapSizeX / size / 2, wm.MapSizeZ / size / 2)) return false;
            var spawn = save.DefaultSpawn;
            return spawn == null || !wm.BlockingTestMapChunkExists(spawn.x / size, spawn.z / size);
        }

        ICoreServerAPI sapi;
        IServerNetworkChannel serverChannel;

        public override double ExecuteOrder() => 0.01;

        public override void Start(ICoreAPI api)
        {
            api.RegisterBlockClass("CylinderWorldWallIce", typeof(BlockWallIce));
            api.RegisterBlockClass("CylinderWorldGlacierIce", typeof(BlockGlacierIce));
            api.RegisterBlockClass("CylinderWorldSeaIce", typeof(BlockSeaIce));
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            side = EnumAppSide.Server;
            Config = LoadConfig(api);

            serverChannel = api.Network.RegisterChannel(ChannelName).RegisterMessageType<SeamParams>();
            // Not SaveGameLoaded: at that point MapSizeX still reports the default 1,024,000; the real
            // world size is applied just before WorldReady ("Server map set"). Worldgen starts after WorldReady.
            api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, InitSeam);
            // Registered before SeamWorldGen's (higher ExecuteOrder), so it runs before the story chain moves
            api.Event.InitWorldGenerator(() => worldgenStarted = true, "standard");
            api.Event.PlayerJoin += plr => { if (Params != null) serverChannel.SendPacket(Params, plr); };

            new CylinderCommands(this, api, Mod.Logger).Register();
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            side = EnumAppSide.Client;
            api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<SeamParams>()
                .SetMessageHandler<SeamParams>(p =>
                {
                    Params = ClientShared = p;
                    ApplyClientLatitude(api);
                });

            // Vanilla GenMaps assigns its own latitude function in LevelFinalize; take over one tick after that.
            api.Event.LevelFinalize += () => api.Event.RegisterCallback(_ => ApplyClientLatitude(api), 0);
        }

        public override void Dispose()
        {
            // The next world (singleplayer) may not use the mod; each side's patches must see no seam then. Only this
            // side's copy is cleared: in singleplayer the other side may still be running.
            if (side == EnumAppSide.Server) ServerShared = null;
            else ClientShared = null;
            base.Dispose();
        }

        void ApplyClientLatitude(ICoreClientAPI api)
        {
            var p = Params;
            if (p != null && p.PolarClimate && api.World?.Calendar != null) api.World.Calendar.OnGetLatitude = p.Latitude;
        }

        const int MaxPreSend = 16;

        /// <summary>The config's PolarIceMode, read once at start; a value that is none of the three counts as "default".</summary>
        internal PolarIce PolarIceChoice { get; private set; }

        CylinderWorldConfig LoadConfig(ICoreServerAPI api)
        {
            CylinderWorldConfig cfg = null;
            bool read = true;
            try { cfg = api.LoadModConfig<CylinderWorldConfig>(ConfigFile); }
            catch (Exception e)
            {
                // A value of the wrong type ("WallDepth": "48m") fails the whole file. Writing it back now would replace
                // every setting in it with the defaults, so it is left as it is. Enabled is still taken from it where it
                // can be read, so a server that switched Cylinder World off doesn't get it in an existing world.
                read = false;
                cfg = new CylinderWorldConfig();
                bool? enabled = EnabledInFile(api);
                if (enabled == false)
                {
                    cfg.Enabled = false;
                    Mod.Logger.Error("{0} could not be read ({1}). Cylinder World stays off, as its \"Enabled\": false says; the file is left as it is: fix it and restart.",
                        ConfigFile, e.Message);
                }
                else Mod.Logger.Error(enabled == true
                    ? "{0} could not be read ({1}). This session uses the default for every other setting (a world created now keeps them); the file is left as it is: fix it and restart."
                    : "{0} could not be read ({1}), not even its \"Enabled\". This session runs Cylinder World with the default for every setting (a world created now keeps them, and an existing world without Cylinder World gets it added); the file is left as it is: fix it and restart.",
                    ConfigFile, e.Message);
            }
            cfg ??= new CylinderWorldConfig();
            if (read) api.StoreModConfig(cfg, ConfigFile);  // writes defaults / new fields back out
            // Pre-sent area: each extra chunk of radius adds a ring of columns sent to the client before every jump
            if (cfg.PreSendDiscRadius > MaxPreSend || cfg.PreSendAhead > MaxPreSend)
            {
                Mod.Logger.Warning("{0}: PreSendDiscRadius and PreSendAhead are limited to {1} chunks.", ConfigFile, MaxPreSend);
                cfg.PreSendDiscRadius = Math.Min(cfg.PreSendDiscRadius, MaxPreSend);
                cfg.PreSendAhead = Math.Min(cfg.PreSendAhead, MaxPreSend);
            }
            var mode = CylinderWorldConfig.ParsePolarIce(cfg.PolarIceMode);
            if (mode == null)
                Mod.Logger.Warning("{0}: PolarIceMode '{1}' is not 'default', 'earth' or 'plateaus'; new worlds use 'default' (Earth's polar layout in Earth worlds, the plateaus elsewhere).",
                    ConfigFile, cfg.PolarIceMode);
            PolarIceChoice = mode ?? PolarIce.Automatic;
            return cfg;
        }

        /// <summary>The file's Enabled alone, read as the full load would (name in any case, true when the key is absent);
        /// null when the file isn't JSON or Enabled isn't a true/false value.</summary>
        static bool? EnabledInFile(ICoreServerAPI api)
        {
            try
            {
                var enabled = api.LoadModConfig(ConfigFile)?[nameof(CylinderWorldConfig.Enabled)];
                return enabled?.Exists != true ? true : enabled.Token.ToObject<bool>();
            }
            catch (Exception) { return null; }
        }

        void InitSeam()
        {
            if (!Config.Enabled) return;

            var save = sapi.WorldManager.SaveGame;
            int worldWidth = sapi.WorldManager.MapSizeX;

            byte[] data = save.GetData(SaveKey);
            SeamParams p = data != null ? SerializerUtil.Deserialize<SeamParams>(data) : null;
            // Earth 2.0 keeps its own marker (save key "earth-creating", stored at StartPre, so it reaches disk with the new
            // save, and cleared once its params are stored): while it says Earth is still creating this world, so does
            // Cylinder World, so the two never disagree about the seam band, the walls and the spawn, even if the stopped
            // first start left chunks on disk. Only without a record of ours: that is saved once the world runs, so a key
            // Earth never cleared (Earth removed after its first start failed, the world played without it) doesn't make
            // a played world new again.
            bool earthCreating = save.GetData(EarthCreatingKey) is { Length: > 0 };
            IsNewWorld = save.IsNew || data == null && (earthCreating || NothingGenerated(save));
            if (!save.IsNew && IsNewWorld)
                Mod.Logger.Notification(earthCreating
                    ? "Cylinder World: this world's first start stopped before the world ran (Earth is still creating it); it is set up as a new world now."
                    : "Cylinder World: this world's first start stopped before anything was generated; it is set up as a new world now.");
            else if (earthCreating && data != null)
                Mod.Logger.Notification("Cylinder World: Earth's mark that it is still creating this world is set, but the world has run with Cylinder World; it stays an existing world.");
            bool fresh = false;

            // A layout this build doesn't know: its chunks would be generated with this build's v10 branches, next to ones
            // the newer build made. The world is refused before anything is generated or saved.
            if (p != null && p.Version > SeamParams.CurrentVersion) throw new InvalidOperationException(string.Format(
                "Cylinder World: this world was made by a newer Cylinder World (layout v{0}; this one knows up to v{1}). It isn't loaded, so its edges and poles aren't generated wrongly: install that newer Cylinder World again.",
                p.Version, SeamParams.CurrentVersion));

            // Stored params that don't fit mean the mod was disabled in this world so far; safe to recompute.
            if (p == null || p.WorldWidth != worldWidth || !Fits(p))
            {
                p = ComputeParams(worldWidth, IsNewWorld);
                fresh = true;
            }
            else if (p.Version < SeamParams.CurrentVersion)
            {
                // Changing the layout now would cut through terrain that already exists.
                Mod.Logger.Notification("This world was created with an older Cylinder World version (layout v{0}) and keeps that layout. Create a new world for the current one.", p.Version);
            }

            if (!Fits(p))
            {
                Mod.Logger.Error("World width {0} is too small for a seam (jump inset {1}, band half-width {2}, falloff {3}). Cylinder World disabled. Use a wider world (51k+ recommended).",
                    worldWidth, p.JumpInset, p.SeamHalfWidth, p.CoastFalloff);
                return;
            }
            if (fresh)
            {
                // Also when an earlier record didn't fit (the mod was off here) and a changed config makes one fit now
                if (!IsNewWorld)
                    Mod.Logger.Warning("Cylinder World was added to an existing world. Chunks that were already generated near the world edges will not have the seam ocean or ice walls, and spawn stays where it is.");
                // The temperature extension also changes world generation's climate readings, so the world keeps the
                // value it was made with (CW-D47); worlds saved before this keep reading the config below
                p.ExtendTemperature = Config.ExtendTemperatureRange;
                p.ExtendTemperatureFrozen = true;
                // Stored only once it fits: a world where the mod is off keeps no record of it
                save.StoreData(SaveKey, SerializerUtil.Serialize(p));
                // CW-D50: vanilla's world generators start after WorldReady, so they read the world's own distance
                if (IsNewWorld && ApplyPolarEquatorDistance(save.WorldConfiguration, p) is string polar) Mod.Logger.Notification(polar);
            }

            ApplyMistSettings(p, Config, save.WorldConfiguration);
            p.WallBuildMargin = Math.Max(0, Config.WallBuildMargin);
            if (!p.ExtendTemperatureFrozen) p.ExtendTemperature = Config.ExtendTemperatureRange;
            p.SeasonalSeaIce = p.EarthPolarIce && Config.SeasonalSeaIce;
            Params = ServerShared = p;
            // Seasons and the sun path follow the planet's latitude, as on the client (ApplyClientLatitude). In 'standard'
            // worlds the worldgen sets it again after vanilla's (InstallLatitude); in 'superflat' worlds vanilla's map
            // generator sets its own at InitWorldGen and keeps it on the server (CW-P06).
            if (p.PolarClimate && sapi.World.Calendar != null) sapi.World.Calendar.OnGetLatitude = p.Latitude;

            string worldType = save.WorldType;
            if (worldType != "standard")
            {
                // The worldgen handlers are registered for "standard" only; the wrap and its protection still run
                Mod.Logger.Warning("World type '{0}': Cylinder World generates its seam ocean, walls and climate only in 'standard' worlds. The east-west wrap is still active, so the jump may be visible.", worldType);
            }

            // New world: this runs before any worldgen, so spawn and story placement can still be changed. A new world has no
            // record of ours (IsNewWorld needs none), so its params were computed just now; every new world gets the story
            // fit, which SetSpawn and the Earth binding redo from the scaling it started with.
            if (IsNewWorld)
            {
                if (p.SpawnZ > 0)
                {
                    // y = null: the engine picks the terrain surface there, same as for its own map-center spawn
                    save.DefaultSpawn = new PlayerSpawnPos(p.SpawnX, null, p.SpawnZ);
                }
                if (Config.FitStoryToWorld && p.HasWalls)
                {
                    storyScaleBeforeFit = sapi.World.Config.GetDecimal("storyStructuresDistScaling", 1);
                    FitStoryDistance(p);
                }
            }

            Mod.Logger.Notification("Cylinder World: W={0}, circumference={1}, jump lines at x={2} and x={3}, seam band half-width={4}, seabed y={5}",
                p.WorldWidth, p.Circumference, p.JumpInset, p.WorldWidth - p.JumpInset, p.SeamHalfWidth, p.SeabedY);
            if (p.HasWalls)
            {
                Mod.Logger.Notification("Cylinder World: ice walls {0} blocks thick at z<{0} and z>{1}, top at y={2}",
                    p.WallDepth, p.WorldLength - p.WallDepth, p.WallTopY);
            }
            if (p.PolarClimate)
            {
                Mod.Logger.Notification("Cylinder World: {0} climate, {1:0.#}°C at the walls to {2:0.#}°C at the {3}",
                    p.IsPlanetLayout ? "planet" : "polar", p.PoleTemperature, p.EquatorTemperature, p.IsPlanetLayout ? "equator (map center)" : "map center");
            }
            if (p.SpawnZ > 0)
            {
                Mod.Logger.Notification("Cylinder World: spawn moved to x={0} z={1} (latitude {2:0.#}°, base {3:0.#}°C), {4:0} blocks from the equator",
                    p.SpawnX, p.SpawnZ, p.SignedLatitude(p.SpawnZ) * 90, p.TemperatureAt(p.SpawnZ), Math.Abs(p.SpawnZ - p.EquatorZ));
            }
        }

        /// <summary>
        /// Vanilla places the story chain around spawn with no world-size check (up to ~12k blocks N/S and ~19k E/W
        /// at 100%). Lower the world's "story structures distance scaling" so the whole chain stays clear of the walls
        /// and out of the seam ocean. Runs at WorldReady, before vanilla's story worldgen reads the setting.
        /// </summary>
        void FitStoryDistance(SeamParams p)
        {
            var (reachX, reachZ) = StoryReach();
            if (reachX <= 0 && reachZ <= 0) return;

            double spawnZ = p.SpawnZ > 0 ? p.SpawnZ : p.EquatorZ;
            double spawnX = p.SpawnX > 0 ? p.SpawnX : p.WorldWidth / 2.0;
            double zRoom = Math.Min(spawnZ, p.WorldLength - spawnZ) - (StoryPolarLimit(p) + SeamParams.PolarMargin);
            double xRoom = Math.Min(spawnX, p.WorldWidth - spawnX) - (p.OceanForceReach + 256);

            var worldConfig = sapi.World.Config;
            double current = worldConfig.GetDecimal("storyStructuresDistScaling", 1);
            double scale = current;
            if (reachZ > 0) scale = Math.Min(scale, zRoom / reachZ);
            if (reachX > 0) scale = Math.Min(scale, xRoom / reachX);
            scale = Math.Floor(Math.Max(0.05, scale) * 100) / 100;

            if (scale < current)
            {
                worldConfig.SetString("storyStructuresDistScaling", scale.ToString(CultureInfo.InvariantCulture));
                Mod.Logger.Notification("Cylinder World: at 100% the story reaches {0:0} blocks E/W and {1:0} N/S of spawn; lowered story distance scaling from {2} to {3} so it fits",
                    reachX, reachZ, current, scale);
            }
            else
            {
                Mod.Logger.Notification("Cylinder World: story chain fits at the current distance scaling ({0})", current);
            }
        }

        /// <summary>
        /// How far from either Z edge the story chain must stay clear, before PolarMargin: the wall zone (wall and ice sheet)
        /// with our plateaus; the wall and Earth's lifeless kilometre once Earth's polar layout is bound, which has no ice
        /// sheet. Until the binding (InitSeam, Earth's SetSpawn call) the plateaus' limit holds, so a world that falls back
        /// to them keeps exactly that fit.
        /// </summary>
        static double StoryPolarLimit(SeamParams p) => p.EarthPolar != null ? p.WallFaceMax + EarthPolarModel.LifelessBorder : p.WallZoneDepth;

        /// <summary>Fits the story chain again from the scaling the world started with (new worlds, before world generation):
        /// after a world generator set the spawn, and once Earth's polar layout is bound.</summary>
        internal void RefitStory()
        {
            var p = Params;
            if (p == null || storyScaleBeforeFit == null) return;
            sapi.World.Config.SetString("storyStructuresDistScaling", storyScaleBeforeFit.Value.ToString(CultureInfo.InvariantCulture));
            FitStoryDistance(p);
        }

        /// <summary>Worst-case distance of any story location from spawn at 100% scaling, following the dependency chain.</summary>
        (double x, double z) StoryReach()
        {
            var nodes = new Dictionary<string, JObject>();
            try
            {
                foreach (var asset in sapi.Assets.GetMany("worldgen/storystructures.json"))
                {
                    if (asset.ToObject<JObject>()?.GetValue("structures", StringComparison.OrdinalIgnoreCase) is not JArray arr) continue;
                    foreach (var s in arr.OfType<JObject>())
                    {
                        string code = (string)s.GetValue("code", StringComparison.OrdinalIgnoreCase);
                        if (code != null) nodes[code] = s;
                    }
                }
            }
            catch (Exception e)
            {
                Mod.Logger.Warning("Could not read the story structure config, story distance not adjusted: {0}", e.Message);
                return (0, 0);
            }

            double Num(JObject o, string key) => o.GetValue(key, StringComparison.OrdinalIgnoreCase)?.Value<double?>() ?? 0;
            var memo = new Dictionary<string, (double x, double z)>();

            (double x, double z) Reach(string code, int depth)
            {
                if (code == null || code == "spawn" || depth > 20 || !nodes.TryGetValue(code, out var n)) return (0, 0);
                if (memo.TryGetValue(code, out var r)) return r;
                var dep = Reach((string)n.GetValue("dependsOnStructure", StringComparison.OrdinalIgnoreCase), depth + 1);
                r = (dep.x + Math.Max(Math.Abs(Num(n, "minSpawnDistX")), Math.Abs(Num(n, "maxSpawnDistX"))),
                     dep.z + Math.Max(Math.Abs(Num(n, "minSpawnDistZ")), Math.Abs(Num(n, "maxSpawnDistZ"))));
                return memo[code] = r;
            }

            double maxX = 0, maxZ = 0;
            foreach (var (code, n) in nodes)
            {
                var r = Reach(code, 0);
                double radius = Math.Max(Num(n, "landformRadius"), Num(n, "generationRadius"));
                maxX = Math.Max(maxX, r.x + radius);
                maxZ = Math.Max(maxZ, r.z + radius);
            }
            return (maxX, maxZ);
        }

        /// <summary>The world option that switches the seam mist per world (CW-D48, suite D28/D40): on the Earth tab,
        /// unticked by default, changeable later (applies at the next start).</summary>
        public const string SeamMistKey = "cylinderSeamMist";

        /// <summary>
        /// The mist settings sent to clients in SeamParams, resolved at every start (CW-D48): the world's cylinderSeamMist
        /// when it has one (bool or "true"/"false"), else the config's SeamMist. Off clears the mist and the jump fog
        /// pulse, and SeamPreload then pre-sends the destination to the whole view radius. Nothing is written to the world
        /// or the config.
        /// </summary>
        internal static void ApplyMistSettings(SeamParams p, CylinderWorldConfig config, Vintagestory.API.Datastructures.ITreeAttribute worldConfig)
        {
            bool seamMist = config.SeamMist;
            if (worldConfig != null && worldConfig.HasAttribute(SeamMistKey))
            {
                var attr = worldConfig[SeamMistKey];
                if (attr is Vintagestory.API.Datastructures.BoolAttribute b) seamMist = b.value;
                else if (bool.TryParse(attr?.GetValue()?.ToString()?.Trim(), out bool parsed)) seamMist = parsed;
            }
            p.MistDensity = config.MistDensity;
            p.MistMin = config.MistMin;
            p.MistRange = config.MistRange;
            p.JumpFogMin = Math.Clamp(config.JumpFogMin, 0, 1);
            p.JumpFogSeconds = Math.Max(0.1f, config.JumpFogSeconds);
            if (!seamMist) { p.MistDensity = 0; p.MistRange = 0; p.JumpFogMin = 0; }
        }

        /// <summary>Vanilla's world setting for the distance from the equator to a pole in blocks.</summary>
        public const string PolarEquatorDistanceKey = "polarEquatorDistance";

        /// <summary>
        /// CW-D50 (CW-0008): a new world whose latitude is this planet's (polar climate, planet layout) gets that latitude's
        /// equator-to-pole distance, PoleDistance, as polarEquatorDistance, so mods that take the scale from it (Rough Seas'
        /// metres per block, Astra Terra's longitude fallback) agree with the calendar's latitude. Earth writes the same
        /// value for its worlds (Earth decision 0072). Returns a line for the log, or null when nothing is written.
        /// </summary>
        internal static string ApplyPolarEquatorDistance(Vintagestory.API.Datastructures.ITreeAttribute worldConfig, SeamParams p)
        {
            if (worldConfig == null || p == null || !p.PolarClimate || !p.IsPlanetLayout || !(p.PoleDistance >= 1)) return null;
            string was = worldConfig.GetAsString(PolarEquatorDistanceKey, "")?.Trim() ?? "";
            string now = ((int)Math.Round(p.PoleDistance)).ToString(CultureInfo.InvariantCulture);
            worldConfig.SetString(PolarEquatorDistanceKey, now);
            return string.Format("Cylinder World: polarEquatorDistance {0} blocks, the equator to a pole on this planet{1}.",
                now, was == now ? "" : " (was " + (was.Length > 0 ? was : "not set") + ")");
        }

        SeamParams ComputeParams(int worldWidth, bool newWorld)
        {
            int viewRadius = Math.Max(sapi.Server.Config.MaxChunkRadius, 12) * GlobalConstants.ChunkSize;
            // Auto inset is rounded up to a multiple of 256, so that (for widths that are a multiple of 512) the
            // circumference is a whole number of 512-block map regions. Mirrored seam regions then line up exactly,
            // which is what matching the weather across the seam will need.
            int minInset = (viewRadius + 32 + 255) / 256 * 256;
            int inset = Config.JumpInset > 0 ? Config.JumpInset : minInset;

            // Earth proportions: ring = 2 × world length, if the map is wide enough (kept a multiple of 256 as above)
            if (Config.JumpInset <= 0 && Config.EarthProportions && Config.PolarWalls)
            {
                long ring = 2L * sapi.WorldManager.MapSizeZ;
                int earthInset = (int)((worldWidth - ring) / 2) / 256 * 256;
                if (earthInset >= minInset)
                {
                    inset = earthInset;
                    Mod.Logger.Notification("Cylinder World: Earth proportions, ring of {0} blocks = 2 × world length. The outer {1} blocks at each side are hidden seam ocean.",
                        worldWidth - 2 * inset, inset);
                }
            }
            // The band's identical part ends on its wavy edge, as close as H - SeamBlend - SeamWave to the world edge (v10):
            // it must still pass the jump line by 32 blocks, or the columns just before the line are ramped from natural
            // ground and differ one ring apart
            const int identicalMargin = SeamParams.SeamBlend + SeamParams.SeamWave + 32;
            // Auto band: the view radius past the jump line, a 64-block margin, the wavy edge's reach (v10), then the ramp
            int half = Math.Max(Config.SeamHalfWidth > 0 ? Config.SeamHalfWidth : inset + viewRadius + 64 + SeamParams.SeamWave + SeamParams.SeamBlend, inset + identicalMargin);
            int falloff = Math.Max(0, Config.CoastFalloff);

            // Small worlds: each seam side may take at most a quarter of the width. Shrink the coast falloff
            // first (min 128); if the band itself still doesn't fit, shrink it too. A band narrower than the
            // view radius means the jump can become visible, and only the mist hides it then.
            int maxReach = worldWidth / 4;
            if (half + falloff > maxReach)
            {
                falloff = Math.Max(Math.Min(falloff, 128), maxReach - half);
                if (half + falloff > maxReach)
                {
                    half = maxReach - falloff;
                    // the jump line moves in so the identical part still passes it, where the band leaves room for that
                    int keep = half - identicalMargin;
                    inset = Math.Min(inset, keep >= 32 ? Math.Min(half / 2, keep) : half / 2);
                    Mod.Logger.Warning("World width {0} is narrower than the view distance needs; seam band shrunk to {1} (jump inset {2}). The jump may be visible, only the mist hides it.",
                        worldWidth, half, inset);
                }
                else
                {
                    Mod.Logger.Notification("World width {0} is small; coast falloff reduced from {1} to {2} so the seam fits.",
                        worldWidth, Config.CoastFalloff, falloff);
                }
            }

            var p = new SeamParams
            {
                Version = SeamParams.CurrentVersion,
                WorldWidth = worldWidth,
                JumpInset = inset,
                SeamHalfWidth = half,
                CoastFalloff = falloff,
                SeabedY = Math.Max(8, sapi.World.SeaLevel - Math.Max(3, Config.SeabedDepth)),
                WorldLength = sapi.WorldManager.MapSizeZ,
                PolarClimate = Config.PolarClimate,
                PoleTemperature = Config.PoleTemperature,
                EquatorTemperature = Config.EquatorTemperature ?? 30f,
                ClimateCurveExponent = Math.Clamp(Config.ClimateCurveExponent, 0.3f, 4f),
                NoiseSeed = sapi.WorldManager.Seed,
                SeaLevel = sapi.World.SeaLevel,
                // Earth's layout is tried in every new world; EarthPolarSystem falls back to the plateaus at WorldReady
                // when Earth isn't generating it, before anything is generated
                EarthPolarIce = newWorld && Config.PolarWalls && PolarIceChoice != PolarIce.Plateaus,
            };

            if (Config.PolarWalls)
            {
                int mapSizeY = sapi.WorldManager.MapSizeY;
                p.WallDepth = Math.Max(8, Config.WallDepth);
                // Kept ≥ 0 where the walls don't fit at all (they are disabled below then), or the clamps would throw
                double poleDistance = Math.Max(0, p.WorldLength / 2.0 - p.WallDepth);

                // Antarctic ice sheet (layout v8): from IceSheetLatitude to the wall, plateau PlateauHeight above sea level
                p.ApronDepth = (int)Math.Clamp(poleDistance * (90 - Config.IceSheetLatitude) / 90.0, 0, poleDistance / 3);
                p.ApronHeight = Math.Clamp(Config.PlateauHeight, 0, mapSizeY - 20 - p.SeaLevel);
                p.WallTopY = Config.WallTopY > 0 ? Config.WallTopY : p.SeaLevel + p.ApronHeight + Math.Max(8, Config.WallAbovePlateau);
                p.WallTopY = Math.Clamp(p.WallTopY, p.SeaLevel + p.ApronHeight + 8, mapSizeY - 6);
                p.PackIceDepth = (int)Math.Clamp(Config.PackIceDepth, 0, poleDistance / 20);
                p.ShelfExtra = (int)Math.Clamp(Config.ShelfExtra, 0, poleDistance / 16);
                p.BarrenDepth = (int)Math.Max(0, poleDistance * (Config.IceSheetLatitude - Config.TundraLatitude) / 90.0);
                // Whatever the plateau's altitude doesn't already provide to reach the wall's yearly average
                p.SheetExtraCold = Config.WallMeanTemperature - (p.PoleTemperature - p.ApronHeight / 6.375f);

                if (p.WallZoneDepth > p.WorldLength / 8)
                {
                    Mod.Logger.Warning("World length {0} is too small for {1}-block ice walls; walls disabled.", p.WorldLength, p.WallDepth);
                    p.WallDepth = 0;
                }
            }

            // Spawn goes to the latitude where the curve gives the world's starting-climate temperature.
            if (newWorld && Config.MoveSpawn && p.PolarClimate && p.HasWalls)
            {
                double absLat = p.AbsLatitudeForTemperature(StartingClimateTemperature());
                double maxLat = Math.Max(0, (p.PoleDistance - SeamParams.PolarMargin) / p.PoleDistance);
                absLat = Math.Min(absLat, maxLat);
                int sign = string.Equals(Config.SpawnHemisphere, "south", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
                p.SpawnX = worldWidth / 2;
                p.SpawnZ = (int)Math.Round(p.EquatorZ - sign * absLat * p.PoleDistance);
            }

            return p;
        }

        /// <summary>Midpoint of vanilla's spawn temperature range for the world's "starting climate" setting.</summary>
        float StartingClimateTemperature()
        {
            return sapi.WorldManager.SaveGame.WorldConfiguration.GetString("startingClimate") switch
            {
                "hot" => 30f,
                "warm" => 21f,
                "cool" => -2f,
                "icy" => -12.5f,
                _ => 10f,   // temperate
            };
        }

        static bool Fits(SeamParams p) =>
            p.JumpInset >= 32 && p.SeamHalfWidth >= p.JumpInset + 32 && 2 * (p.SeamHalfWidth + p.CoastFalloff) <= p.WorldWidth / 2;
    }
}
