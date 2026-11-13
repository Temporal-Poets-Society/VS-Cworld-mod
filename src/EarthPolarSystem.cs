using System;
using System.Collections.Concurrent;
using System.Linq;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace CylinderWorld
{
    /// <summary>Bind after Earth (0.05), before world generation. The default layout never uses this adapter.</summary>
    public class EarthPolarSystem : ModSystem
    {
        ICoreServerAPI sapi;
        CylinderWorldCore core;
        Harmony harmony;
        static EarthPolarSystem active;
        public override double ExecuteOrder() => 0.09;
        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            core = api.ModLoader.GetModSystem<CylinderWorldCore>();
            api.Event.ServerRunPhase(EnumServerRunPhase.WorldReady, Bind);
            api.Event.OnTrySpawnEntity += AllowEntity;
            api.Event.OnTrySpawnGroupNearOffthread += AllowGroup;
        }

        void Bind()
        {
            var p = core.Params;
            if (p == null || !p.EarthPolarIce) return;
            // Earth stopped the server in its own WorldReady step (its data or terrain couldn't be had) and has said why.
            // Nothing is saved before the world runs, so the next start decides again; nothing to decide or save now.
            if (sapi.Server.IsShuttingDown) return;
            object model = EarthModel(out bool earthClaims, out string unreadable);
            if (model == null)
            {
                // Never silently replace a saved Earth coastline with the default plateau if the dependency disappears.
                if (!core.IsNewWorld) throw new InvalidOperationException(unreadable != null
                    ? "Cylinder World: this world uses Earth's polar layout, but the installed Earth can't be read (" + unreadable + "). Install an Earth that matches this Cylinder World to load it."
                    : "Cylinder World: this world uses Earth's polar layout and requires an active Earth world. Restore Earth to load it.");
                // A new world that Earth generates, or may: Earth has already dropped its seam gutter for our layout
                // (EarthPolarIce was true when it read it), so our plateaus would be laid over Earth's terrain for good.
                if (earthClaims || unreadable != null) throw new InvalidOperationException(unreadable != null
                    ? "Cylinder World: Earth is installed, but this Cylinder World can't read it (" + unreadable + "), so it can't tell whether Earth generates this new world. Nothing has been generated; install an Earth that matches this Cylinder World and start again."
                    : "Cylinder World: Earth generates this new world, but its terrain model isn't there for Cylinder World's polar layout. Nothing has been generated; install an Earth that matches this Cylinder World and start again.");
                // A new world that Earth doesn't generate (not installed, disabled, another play style, not 'standard').
                // Nothing has generated yet, so it can still take the default layout, which ComputeParams also set up.
                p.EarthPolarIce = false;
                p.SeasonalSeaIce = false;
                core.KeepSpawnOffPlateaus();
                core.SaveSeam();
                if (core.PolarIceChoice == PolarIce.Earth)
                    Mod.Logger.Warning("Cylinder World: PolarIceMode is 'earth', but Earth is not active in this new world. Using the polar plateaus.");
                else Mod.Logger.Notification("Cylinder World: not an Earth world, using the polar plateaus.");
                return;
            }
            try { p.EarthPolar = new EarthPolarModel(model); }
            // the adapter's own refusals are complete messages (version mismatch or a model in the wrong state)
            catch (InvalidOperationException) { throw; }
            catch (Exception e)
            {
                // A changed member of Earth's model would otherwise end the start with a bare reflection error
                throw new InvalidOperationException("Cylinder World: the installed Earth's terrain model doesn't match Cylinder World's Earth adapter (" +
                    e.Message + "). Install an Earth and a Cylinder World that belong together.", e);
            }
            active = this;
            harmony = new Harmony("cylinderworld.earthpolar");
            // A game update that renames or changes either method only loses that guard, not the world
            hotSprings = new GameFeature("the guard that keeps hot springs out of the lifeless kilometre at the Walls (they may appear there)", harmony, Mod.Logger);
            hotSprings.Patch(hotSprings.Method(typeof(GenHotSprings), "genHotspring", new[] { typeof(int), typeof(int), typeof(Vec3i), typeof(HotSpringGenData) }),
                prefix: new HarmonyMethod(typeof(EarthPolarSystem), nameof(HotSpringGuard)));
            hotSprings.Apply();
            traders = new GameFeature("the Earth polar guard for trader camps (they may appear near the poles)", harmony, Mod.Logger);
            traders.Field(typeof(WorldGenStructure), "schematicDatas", typeof(BlockSchematicStructure[][]));
            traders.Patch(traders.Method(typeof(WorldGenStructure), "TryGenerate", new[] { typeof(IBlockAccessor), typeof(IWorldAccessor), typeof(BlockPos),
                    typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(string) }),
                prefix: new HarmonyMethod(typeof(EarthPolarSystem), nameof(TraderGuard)));
            traders.Apply();
            if (core.IsNewWorld)
            {
                if (core.Config.WallTopY <= 0)
                {
                    int highest = p.SeaLevel;
                    for (double x = p.JumpInset; x < p.WorldWidth - p.JumpInset; x += 32)
                    {
                        highest = Math.Max(highest, p.EarthPolar.Sample(x, p.WallFaceMax + 1).Top);
                        highest = Math.Max(highest, p.EarthPolar.Sample(x, p.WorldLength - p.WallFaceMax - 1).Top);
                    }
                    p.WallTopY = Math.Min(sapi.WorldManager.MapSizeY - 6, Math.Max(p.WallTopY, highest + Math.Max(8, core.Config.WallAbovePlateau)));
                }
                // CW-D26: the band keeps Earth's real seabed where Earth provides one that repeats around the ring
                p.EarthSeabed = p.Version >= 10 && p.EarthPolar.RealGutterSeabed;
                // CW-D31: and its land where the seam crosses land
                p.EarthSeamLand = p.EarthSeabed && p.EarthPolar.GutterLand;
                // and, from 1.1, that land's lakes and rivers keep their water (SeamWorldGen reads it), when Earth says
                // where their surface is
                p.EarthSeamWater = p.EarthSeamLand && p.EarthPolar.HasWaterTop;
                // Earth's climate is the real one, and Earth extends it below vanilla's -20 °C with its own setting, frozen
                // per world; Cylinder World's extension would put its own latitude curve on top (CW-D47)
                p.ExtendTemperature = false;
                core.SaveSeam();
                // the story chain was fitted against the plateaus' ice sheet, which Earth's layout doesn't have
                core.RefitStory();
            }
            else if (p.EarthSeabed && !p.EarthPolar.RealGutterSeabed)
                Mod.Logger.Warning("Cylinder World: this world's seam band follows Earth's real seabed, but the installed Earth doesn't repeat it across the seam. New band chunks may not match their neighbours or the other side of the jump.");
            else if (p.EarthSeamLand && !p.EarthPolar.GutterLand)
                Mod.Logger.Warning("Cylinder World: this world's seam band keeps Earth's land across the seam, but the installed Earth doesn't carry it across. New band chunks may not match their neighbours.");
            Mod.Logger.Notification("Cylinder World: Earth polar mode active: Antarctic terrain preserved, low Arctic sea ice, no natural creature spawns within {0} blocks of either wall, no polar bears or arctic foxes in Antarctica. Wall top y={1}. Seam band seabed: {2}.",
                EarthPolarModel.LifelessBorder, p.WallTopY, p.EarthSeamLand ? "Earth's real seabed and land" : p.EarthSeabed ? "Earth's real seabed" : "Cylinder World's");
        }

        /// <summary>
        /// Earth's terrain model for this world, read from Earth's public EarthMapSystem (Earth is no build dependency):
        /// null when Earth isn't installed, doesn't generate this world, or can't be read. <paramref name="claims"/>: Earth
        /// generates this world (its Params.Enabled). <paramref name="unreadable"/>: Earth is installed but a member this
        /// needs is missing (a mismatched Earth version), so whether it generates the world can't be told.
        /// </summary>
        object EarthModel(out bool claims, out string unreadable)
        {
            claims = false;
            unreadable = null;
            var earth = sapi.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "EarthMap.EarthMapSystem");
            if (earth == null)
            {
                // Earth enabled by its modid (it was earthmap before 2.0) without that system: renamed in a newer Earth, or
                // it failed to start. It may still have read our layout and left its seam gutter out, so it is never
                // taken for "not installed"
                string modid = new[] { "earth", "earthmap" }.FirstOrDefault(sapi.ModLoader.IsModEnabled);
                if (modid != null) unreadable = "the enabled mod '" + modid + "' runs no EarthMap.EarthMapSystem: renamed, or it failed to start";
                return null;
            }
            var feature = new GameFeature("Earth's polar layout", null, Mod.Logger, "the installed Earth");
            var modelProperty = feature.Property(earth.GetType(), "Model");
            var paramsProperty = feature.Property(earth.GetType(), "Params");
            object earthParams = paramsProperty?.GetValue(earth);
            // set in Earth's own WorldReady step, which runs before this one; null if Earth didn't get that far
            var enabled = earthParams == null ? null : feature.Field(earthParams.GetType(), "Enabled", typeof(bool));
            if (!feature.Apply())
            {
                unreadable = "missing " + string.Join(", ", new[] { modelProperty == null ? "EarthMapSystem.Model" : null,
                    paramsProperty == null ? "EarthMapSystem.Params" : null, earthParams != null && enabled == null ? "EarthParams.Enabled" : null }
                    .Where(m => m != null));
                return null;
            }
            claims = enabled != null && (bool)enabled.GetValue(earthParams);
            return claims ? modelProperty.GetValue(earth) : null;
        }

        bool AllowEntity(IBlockAccessor accessor, ref EntityProperties properties, Vec3d pos, long herdId) => AllowSpawn(properties, pos.Z);
        bool AllowGroup(IBlockAccessor accessor, EntityProperties properties, BlockPos pos) => AllowSpawn(properties, pos.Z);
        bool AllowSpawn(EntityProperties type, double z)
        {
            var p = core.Params;
            return p?.EarthPolar == null || !ForbidsSpawn(p, type, z);
        }
        bool AllowFeature(double z)
        {
            var p = core.Params;
            return p?.EarthPolar == null || !p.EarthPolar.ForbidFeatures(p, z);
        }
        /// <summary>Hot springs: kept out of the lifeless kilometre at either Wall only. Elsewhere, Antarctica included, Earth
        /// marks vanilla's hot springs only at real geothermal sites (Deception Island, Mount Erebus), so where they go is
        /// Earth's to decide (CW-D46).</summary>
        bool AllowHotSpring(double z)
        {
            var p = core.Params;
            return p?.EarthPolar == null || !p.EarthPolar.ForbidNaturalSpawn(p, z);
        }

        /// <summary>The spawn veto of both spawn events: everything in the lifeless border, Arctic predators in Antarctica.</summary>
        public static bool ForbidsSpawn(SeamParams p, EntityProperties type, double z) =>
            p.EarthPolar.ForbidNaturalSpawn(p, z) || p.EarthPolar.ForbidPolarPredators(p, z) && IsVanillaPolarPredator(type);

        static readonly ConcurrentDictionary<string, bool> polarPredators = new();

        /// <summary>A vanilla creature whose spawn climate tops out at or below freezing and that eats meat: the
        /// same cold-specialist rule as the floes (polar bears -9°C, arctic foxes -10°C). Mod species are left to
        /// Earth's regional fauna, which places them by real geography.</summary>
        public static bool IsVanillaPolarPredator(EntityProperties type)
        {
            if (type?.Code == null || type.Code.Domain != "game") return false;
            return polarPredators.GetOrAdd(type.Code.ToString(), _ =>
            {
                var sc = type.Server?.SpawnConditions;
                bool cold = sc?.Climate?.MaxTemp <= 0 || sc?.Runtime?.MaxTemp <= 0 || sc?.Worldgen?.MaxTemp <= 0;
                return cold && BlockGlacierIce.EatsMeat(type);
            });
        }

        /// <summary>The two worldgen guards, each a game feature of its own: static, as their patch bodies are.</summary>
        static GameFeature hotSprings, traders;

        // Stop the whole feature before any blocks, schematic entities or land claims are placed.
        // Sampling the extent also prevents a feature just outside the border spilling into it.
        static bool HotSpringGuard(int baseZ, Vec3i centerPos, HotSpringGenData gendata)
        {
            var feature = hotSprings;
            if (active == null || feature?.On != true) return true;
            try
            {
                int radius = (int)Math.Ceiling(2 * gendata.horRadius) + 1;
                return active.AllowHotSpring(baseZ + centerPos.Z - radius)
                    && active.AllowHotSpring(baseZ + centerPos.Z + radius);
            }
            catch (Exception e)
            {
                feature.Fail(e);
                return true;
            }
        }

        static bool TraderGuard(WorldGenStructure __instance, BlockPos startPos,
            BlockSchematicStructure[][] ___schematicDatas, ref bool __result)
        {
            var feature = traders;
            if (active == null || feature?.On != true) return true;
            try
            {
                if (!IsTrader(__instance.Group, __instance.Code)) return true;
                int radius = 0;
                if (___schematicDatas != null)
                    foreach (var rotations in ___schematicDatas)
                        if (rotations != null)
                            foreach (var schematic in rotations)
                                if (schematic != null) radius = Math.Max(radius, Math.Max(schematic.SizeX, schematic.SizeZ));
                if (active.AllowFeature(startPos.Z - radius) && active.AllowFeature(startPos.Z + radius)) return true;
                __result = false;
                return false;
            }
            catch (Exception e)
            {
                feature.Fail(e);
                return true;
            }
        }

        public static bool IsTrader(string group, string code) =>
            string.Equals(group, "trader", StringComparison.OrdinalIgnoreCase)
            || code?.StartsWith("trader-", StringComparison.OrdinalIgnoreCase) == true;

        public override void Dispose()
        {
            harmony?.UnpatchAll(harmony.Id);
            if (active == this)
            {
                active = null;
                hotSprings = traders = null;
            }
            // Keyed by entity code: another world (singleplayer) can have other mods or patched spawn conditions
            polarPredators.Clear();
            if (sapi != null)
            {
                sapi.Event.OnTrySpawnEntity -= AllowEntity;
                sapi.Event.OnTrySpawnGroupNearOffthread -= AllowGroup;
            }
            base.Dispose();
        }
    }
}
