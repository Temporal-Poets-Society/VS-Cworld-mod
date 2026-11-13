using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace CylinderWorld
{
    /// <summary>The /cyl command: seam info and test helpers. Reads the world's geometry from the core; changes nothing in it.</summary>
    internal sealed class CylinderCommands
    {
        readonly CylinderWorldCore core;
        readonly ICoreServerAPI sapi;
        readonly ILogger log;

        public CylinderCommands(CylinderWorldCore core, ICoreServerAPI sapi, ILogger log)
        {
            this.core = core;
            this.sapi = sapi;
            this.log = log;
        }

        public void Register()
        {
            var api = sapi;
            var parsers = api.ChatCommands.Parsers;
            // The root's own handler (info) needs a player; a precondition on the root would also block /cyl map from the
            // server console, since the game checks it before choosing the subcommand
            api.ChatCommands.Create("cyl")
                .WithDescription("Cylinder World: seam info and test helpers")
                .RequiresPrivilege(Privilege.chat)
                .HandleWith(args => args.Caller.Player == null ? TextCommandResult.Error("Caller must be player") : CmdInfo(args))
                .BeginSubCommand("info")
                    .WithDescription("Show seam geometry and where you are on the ring")
                    .RequiresPlayer()
                    .HandleWith(CmdInfo)
                .EndSubCommand()
                .BeginSubCommand("goseam")
                    .WithDescription("Teleport onto the sea just before the east jump line (default 80 blocks before it)")
                    .RequiresPrivilege(Privilege.tp)
                    .RequiresPlayer()
                    .WithArgs(parsers.OptionalInt("distance", 80))
                    .HandleWith(CmdGoSeam)
                .EndSubCommand()
                .BeginSubCommand("gowall")
                    .WithDescription("Teleport on top of the north (or south) ice wall, at your current x")
                    .RequiresPrivilege(Privilege.tp)
                    .RequiresPlayer()
                    .WithArgs(parsers.OptionalWordRange("side", "north", "south"))
                    .HandleWith(CmdGoWall)
                .EndSubCommand()
                .BeginSubCommand("golat")
                    .WithDescription("Teleport to a latitude at your current x (45 = 45°N, -30 = 30°S, 0 = equator), high up: fly down in creative")
                    .RequiresPrivilege(Privilege.tp)
                    .RequiresPlayer()
                    .WithArgs(parsers.Double("degrees"))
                    .HandleWith(CmdGoLat)
                .EndSubCommand()
                .BeginSubCommand("map")
                    .WithDescription("Render the whole world (land/sea, temperature, walls, seam, spawn, story locations) to a PNG without generating chunks")
                    .RequiresPrivilege(Privilege.tp)
                    .HandleWith(CmdMap)
                .EndSubCommand();
        }

        TextCommandResult CmdGoLat(TextCommandCallingArgs args)
        {
            var p = core.Params;
            if (p == null || !p.IsPlanetLayout || !p.HasWalls) return TextCommandResult.Error("Needs the planet layout (a world created with Cylinder World 0.3+ and walls on).");

            double deg = Math.Clamp((double)args[0], -90, 90);
            double z = Math.Clamp(p.EquatorZ - deg / 90.0 * p.PoleDistance, p.WallZoneDepth + 2, p.WorldLength - p.WallZoneDepth - 2);
            double y = p.WallTopY + 2;
            var ent = args.Caller.Entity;
            ent.TeleportToDouble(ent.Pos.X, y, z);
            return TextCommandResult.Success($"Teleported to {Math.Abs(deg):0.#}°{(deg >= 0 ? "N" : "S")} (z={z:0}), base temperature {p.TemperatureAt(z):0.#}°C. You're high up at y={y:0}: fly down in creative mode.");
        }

        TextCommandResult CmdMap(TextCommandCallingArgs args)
        {
            var p = core.Params;
            if (p == null) return TextCommandResult.Error("Cylinder World is not active in this world.");

            var genMaps = sapi.ModLoader.GetModSystem<GenMaps>();
            if (genMaps?.requireLandAt == null || genMaps.requireLandAt.Count == 0 || genMaps.noiseSizeOcean <= 0)
            {
                return TextCommandResult.Error("Vanilla's map generators aren't set up (only standard worlds are supported).");
            }

            // A private generator with vanilla's seed and settings: vanilla's own instance is in use by worldgen threads.
            var save = sapi.WorldManager.SaveGame;
            var wc = save.WorldConfiguration;
            float landcover = wc.GetString("landcover", "1").ToFloat(1f);
            float oceanscale = wc.GetString("oceanscale", "1").ToFloat(1f);
            bool spawnOffset = GameVersion.IsLowerVersionThan(save.CreatedGameVersion, "1.20.0-pre.14");
            var ocean = GenMaps.GetOceanMapGen(sapi.WorldManager.Seed + 1873, landcover, TerraGenConfig.oceanMapScale, oceanscale, new List<XZ>(genMaps.requireLandAt), spawnOffset);
            int oceanPixelBlocks = sapi.WorldManager.RegionSize / genMaps.noiseSizeOcean;

            var markers = new List<(int x, int z, bool spawn)>();
            var story = sapi.ModLoader.GetModSystem<GenStoryStructures>();
            if (story?.Structures != null)
            {
                foreach (var (_, loc) in story.Structures) markers.Add((loc.CenterPos.X, loc.CenterPos.Z, false));
            }
            markers.Add(p.SpawnZ > 0 ? (p.SpawnX, p.SpawnZ, true) : (p.WorldWidth / 2, p.WorldLength / 2, true));

            string path = System.IO.Path.Combine(sapi.GetOrCreateDataPath("CylinderWorld"), $"worldmap-{save.Seed}.png");
            var caller = args.Caller.Player as IServerPlayer;
            // Two renders at once would write the same file
            if (Interlocked.Exchange(ref mapBusy, 1) == 1) return TextCommandResult.Error("A world map is already being rendered; wait for it to finish.");
            var earthHeights = p.EarthPolar != null ? EarthBuiltInHeights() : null;

            // Its own thread: Earth's switch below is per thread, and a pool thread may next run other work
            new Thread(() =>
            {
                string msg;
                try
                {
                    // Earth worlds: Earth's built-in heights, as Earth's own whole-world sweeps use. Reading GLO-90 here would
                    // decode the tiles of every 1° square on the globe through the cache and lock that worldgen threads use.
                    earthHeights?.SetValue(null, true);
                    var res = WorldPreview.Render(p, ocean, oceanPixelBlocks, markers, path);
                    var shares = new[] { "arctic", "cold", "temperate", "tropical" }
                        .Where(res.LandShareByZone.ContainsKey)
                        .Select(zone => $"{zone} {res.LandShareByZone[zone] * 100:0}%");
                    msg = $"World map written to {res.Path} ({res.Width}×{res.Height} px, {res.BlocksPerPixel} blocks per pixel). Land share by zone: {string.Join(", ", shares)}. " +
                          "Red dot = spawn, purple = story locations, red line = equator, dotted lines = every 15° latitude, dashed blue = jump lines.";
                }
                catch (Exception e)
                {
                    msg = "World map failed: " + e.Message;
                    log.Error(e);
                }
                finally { Volatile.Write(ref mapBusy, 0); }
                log.Notification(msg);
                sapi.Event.EnqueueMainThreadTask(() => caller?.SendMessage(GlobalConstants.GeneralChatGroup, msg, EnumChatType.CommandSuccess), "cylinderworld-map");
            }) { IsBackground = true, Name = "Cylinder World map", Priority = ThreadPriority.BelowNormal }.Start();

            return TextCommandResult.Success("Rendering the world map in the background, this takes a few seconds...");
        }

        int mapBusy;
        GameFeature earthHeightsFeature;
        PropertyInfo builtInOnly;

        /// <summary>Earth's public switch for a thread that sweeps the whole world for a picture
        /// (EarthMap.Core.FineElevation.BuiltInOnly: that thread takes Earth's built-in heights and never reads GLO-90).
        /// Null when this Earth has no such switch (one log line); the map then renders as before, only slower.</summary>
        PropertyInfo EarthBuiltInHeights()
        {
            if (earthHeightsFeature != null) return builtInOnly;
            var feature = earthHeightsFeature = new GameFeature("/cyl map's quick Earth heights", null, log, "the installed Earth");
            var prop = feature.Property(feature.Type("EarthMap.Core.FineElevation"), "BuiltInOnly");
            feature.Require(prop == null || prop.PropertyType == typeof(bool) && prop.GetSetMethod()?.IsStatic == true,
                "a static settable bool FineElevation.BuiltInOnly");
            if (feature.Apply()) builtInOnly = prop;
            return builtInOnly;
        }

        TextCommandResult CmdGoWall(TextCommandCallingArgs args)
        {
            var p = core.Params;
            if (p == null || !p.HasWalls) return TextCommandResult.Error("This world has no ice walls.");

            bool south = (args[0] as string) == "south";
            var ent = args.Caller.Entity;
            double z = south ? p.WorldLength - p.WallDepth / 2.0 : p.WallDepth / 2.0;
            ent.TeleportToDouble(ent.Pos.X, p.WallTopY + 1.5, z);
            return TextCommandResult.Success($"Teleported onto the {(south ? "south" : "north")} wall at y={p.WallTopY + 1}. The face is {p.WallDepth / 2} blocks {(south ? "north" : "south")} of you.");
        }

        TextCommandResult CmdInfo(TextCommandCallingArgs args)
        {
            var p = core.Params;
            if (p == null) return TextCommandResult.Error("Cylinder World is not active in this world (see server log).");

            var pos = args.Caller.Entity.Pos;
            string msg = string.Format(
                "World width {0}, circumference {1:0}. Jump lines at absolute x={2} and x={3}. Seam ocean: {4} blocks in from each edge.\n" +
                "You: absolute x={5:0.0}, longitude {6:0.00}°, {7:0} blocks from the nearest jump line{8}.",
                p.WorldWidth, p.Circumference, p.JumpInset, p.WorldWidth - p.JumpInset, p.SeamHalfWidth - p.JumpInset,
                pos.X, p.Longitude(pos.X), p.DistToJumpLine(pos.X), p.InSeamBand(pos.X) ? " (inside the seam band)" : "");

            if (p.HasWalls)
            {
                msg += string.Format("\nIce walls: z below {0} and above {1}, top y={2}. You are {3:0} blocks from the nearest wall face (absolute z={4:0}).",
                    p.WallDepth, p.WorldLength - p.WallDepth, p.WallTopY, p.DistToZEdge(pos.Z) - p.WallDepth, pos.Z);
            }
            if (p.PolarClimate && p.IsPlanetLayout)
            {
                double lat = p.SignedLatitude(pos.Z) * 90;
                msg += string.Format("\nLatitude {0:0.0}°{1}, {2:0} blocks from the equator (z={3:0}). Base temperature here {4:0.#}°C ({5:0.#}°C at the equator, {6:0.#}°C at the walls).",
                    Math.Abs(lat), lat >= 0 ? "N" : "S", Math.Abs(pos.Z - p.EquatorZ), p.EquatorZ,
                    p.TemperatureAt(pos.Z), p.EquatorTemperature, p.PoleTemperature);
            }
            else if (p.PolarClimate)
            {
                msg += string.Format("\nPolar climate: {0:0.#}°C at the walls to {1:0.#}°C at the center. Base temperature here: {2:0.#}°C.",
                    p.PoleTemperature, p.EquatorTemperature, p.TemperatureAt(pos.Z));
            }
            if (p.SpawnZ > 0)
            {
                msg += string.Format("\nSpawn was moved to x={0} z={1}; the story locations moved with it.", p.SpawnX, p.SpawnZ);
            }
            var now = sapi.World.BlockAccessor.GetClimateAt(args.Caller.Entity.Pos.AsBlockPos, EnumGetClimateMode.NowValues);
            if (now != null)
            {
                msg += string.Format("\nTemperature here now {0:0.#}°C (yearly average {1:0.#}°C).", now.Temperature, now.WorldGenTemperature);
            }
            if (p.Version >= 8 && p.HasWalls)
            {
                double harsh = p.SheetHarshness(pos.X, pos.Z);
                float exposure = sapi.ModLoader.GetModSystem<PolarExposureSystem>()?.GetExposure(args.Caller.Player?.PlayerUID ?? "") ?? 0;
                if (p.EarthPolarIce)
                {
                    msg += "\nEarth polar mode: Antarctica follows Earth terrain; the Arctic has low sea ice.";
                    if (p.SeasonalSeaIce && p.EarthPolar != null)
                    {
                        var c = p.EarthPolar.Sample(pos.X, pos.Z);
                        var season = p.CurrentSeaIceDate;
                        if (SeaIcePolicy.InPolarOcean(c))
                            msg += string.Format("\nSea ice target: {0}; ocean-season temperature {1:0.0}°C (cycle month {2:0.0}{3}).",
                                p.SeaIceAt(pos.X, pos.Z, c), SeaIcePolicy.OceanTemperature(c, season.Year, season.FixedSeason),
                                season.Year * 12 + 1, season.FixedSeason ? ", fixed season" : "");
                    }
                    if (p.EarthPolar?.ForbidNaturalSpawn(p, pos.Z) == true) msg += " No natural creature spawning here.";
                    else if (p.EarthPolar?.ForbidPolarPredators(p, pos.Z) == true) msg += " No polar bears or arctic foxes spawn here.";
                }
                msg += harsh > 0
                    ? string.Format("\nOn the ice sheet (harshness {0:0}%). Cold exposure {1:0}%.", harsh * 100, exposure * 100)
                    : p.EarthPolarIce ? "" : string.Format("\nIce sheet edge here is ~{0:0} blocks from the wall face.", p.SheetEdgeDistance(pos.X));
            }
            return TextCommandResult.Success(msg);
        }

        TextCommandResult CmdGoSeam(TextCommandCallingArgs args)
        {
            var p = core.Params;
            if (p == null) return TextCommandResult.Error("Cylinder World is not active in this world.");

            // In the band's identical part, before the line
            int dist = Math.Clamp((int)args[0], 1, Math.Max(1, (int)p.IdenticalReach));
            var ent = args.Caller.Entity;
            double x = p.WorldWidth - p.JumpInset - dist + 0.5, z = ent.Pos.Z;
            // On top of whatever is there (sea, sea ice, the ice sheet near the walls, Earth's land in the band), read once its
            // chunk column is loaded
            sapi.WorldManager.LoadChunkColumnPriority((int)x / GlobalConstants.ChunkSize, (int)z / GlobalConstants.ChunkSize, new ChunkLoadOptions
            {
                OnLoaded = () =>
                {
                    int top = sapi.World.BlockAccessor.GetRainMapHeightAt((int)x, (int)z);
                    ent.TeleportToDouble(x, Math.Max(top + 1, sapi.World.SeaLevel) + 0.5, z);
                }
            });
            return TextCommandResult.Success($"Teleporting to x={x:0}, {dist} blocks west of the east jump line. Head east (+X) to cross.");
        }
    }
}
