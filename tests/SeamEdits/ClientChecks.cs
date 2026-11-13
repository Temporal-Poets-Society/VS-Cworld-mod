using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.CompilerServices;
using CylinderWorld;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server.Systems;

// Native packet handlers and camera/physics buffers, with API interfaces faked. No ClientMain, window, GL or server.
public class SurfaceProxy : DispatchProxy
{
    public System.Func<MethodInfo, object[], object> Call;
    protected override object Invoke(MethodInfo method, object[] args) => Call(method, args);
    public static T Make<T>(System.Func<MethodInfo, object[], object> call) where T : class
    { T value = Create<T, SurfaceProxy>(); ((SurfaceProxy)(object)value).Call = call; return value; }
    public static object Default(MethodInfo method) => method.ReturnType == typeof(void) ? null
        : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
}
public class TestCarrier : Entity { }
public static class ClientChecks
{
    static EntityPlayer player;
    static Entity carrier;
    static ICoreClientAPI api;
    static IClientWorldAccessor world;
    static Vintagestory.Client.NoObf.ClientWorldPlayerData playerData;
    static readonly Dictionary<long, Entity> entities = new();
    static SeamParams p;
    static Type jump;
    static DefaultShaderUniforms uniforms;
    static int failures, checks;
    public static int Main(string[] args)
    {
        string game = Path.GetFullPath(args[0]), mod = Path.GetFullPath(args[1]);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (string dir in new[] { mod, game, Path.Combine(game, "Lib"), Path.Combine(game, "Mods") })
            { string file = Path.Combine(dir, name.Name + ".dll"); if (File.Exists(file)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(file); }
            return null;
        };
        try { Run(); } catch (Exception e) { Console.WriteLine(e); return 2; }
        Console.WriteLine($"CLIENTCHECK RESULT checks={checks} failures={failures}");
        return failures == 0 ? 0 : 1;
    }
    static void Check(bool ok, string name)
    { checks++; if (!ok) failures++; Console.WriteLine($"CLIENTCHECK {(ok ? "PASS" : "FAIL")} {name}"); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Run()
    {
        p = new SeamParams { WorldWidth = 256000, WorldLength = 102400, JumpInset = 25600, SeamHalfWidth = 26272 };
        // The client patches read ClientShared; Shared only while the build still has it
        AccessTools.Field(typeof(CylinderWorldCore), "Shared")?.SetValue(null, p);
        AccessTools.Field(typeof(CylinderWorldCore), "ClientShared").SetValue(null, p);
        var block = SurfaceProxy.Make<IBlockAccessor>((m, a) => SurfaceProxy.Default(m));
        var render = SurfaceProxy.Make<IRenderAPI>((m, a) => m.Name == "get_ShaderUniforms" ? uniforms : SurfaceProxy.Default(m));
        var modLoader = SurfaceProxy.Make<IModLoader>((m, a) => SurfaceProxy.Default(m));
        var localPlayer = (Vintagestory.Client.NoObf.ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.ClientPlayer));
        playerData = (Vintagestory.Client.NoObf.ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.Client.NoObf.ClientWorldPlayerData));
        AccessTools.Field(localPlayer.GetType(), "worlddata").SetValue(localPlayer, playerData);
        world = SurfaceProxy.Make<IClientWorldAccessor>((m, a) => m.Name switch
        {
            "get_Player" => localPlayer, "get_Side" => EnumAppSide.Client, "get_BlockAccessor" => block,
            "GetEntityById" => entities.GetValueOrDefault((long)a[0]), "get_LoadedEntities" => entities, _ => SurfaceProxy.Default(m)
        });
        api = SurfaceProxy.Make<ICoreClientAPI>((m, a) => m.Name switch
        {
            "get_World" => world, "get_Side" => EnumAppSide.Client, "get_ElapsedMilliseconds" => 1000L,
            "get_Render" => render, "get_ModLoader" => modLoader, _ => SurfaceProxy.Default(m)
        });
        var logger = SurfaceProxy.Make<ILogger>((m, a) => { if (m.Name == "Warning") Console.WriteLine("PATCH WARNING " + string.Join(" ", a)); return SurfaceProxy.Default(m); });
        uniforms = new DefaultShaderUniforms { playerReferencePos = new Vec3d(), playerReferencePosForFoam = new Vec3d() };
        jump = typeof(CylinderWorldCore).Assembly.GetType("CylinderWorld.SeamSmoothJump");
        AccessTools.Field(typeof(SeamJumpClient), "Capi").SetValue(null, api);
        var harmony = new Harmony("seam-offline-client-checks");
        harmony.Patch(AccessTools.Method(typeof(EntityPlayer), "UpdatePartitioning"), prefix: new HarmonyMethod(typeof(ClientChecks), nameof(NoPartition)));
        AccessTools.Method(jump, "Patch").Invoke(null, new object[] { harmony, logger });
        Check(AccessTools.Property(jump, "TeleportsOn").GetValue(null) is true, "every member the teleport patches use is there; they are on");
        Check(Laps(true, logger), "every member the lap feature uses (other creatures on the lap nearest the player) is there; it is on");
        Check(Harmony.GetPatchInfo(AccessTools.Method(typeof(EntityPlayer), "OnReceivedServerPacket"))?.Prefixes.Any(x => x.owner == harmony.Id) == true,
            "teleport patch runs before EntityPlayer moves the mount");
        RingHashes(harmony);
        TrackingTranspiler();

        // A server that doesn't announce its wraps (and one that announces this one): jumps by distance, smoothly
        foreach (bool announced in new[] { false, true })
        foreach (double direction in new[] { 1.0, -1.0 })
        foreach (bool mounted in new[] { false, true })
        {
            foreach (bool positionFirst in new[] { false, true })
            {
                Setup(mounted, direction);
                ClearNotices();
                if (announced) { Notice(0, 0); Notice(carrier.EntityId, (int)direction); }
                double before = carrier.Pos.X, c = p.Circumference * direction;
                var target = new Vec3d(before + c + 0.75 * direction, 119, 102); // deliberately older than the local view
                if (positionFirst) { Position(11, target); Teleport(target); }
                else { Teleport(target); Position(11, target); }
                string label = $"announced={announced} direction={direction} mounted={mounted} position-first={positionFirst}";
                Check(carrier.Pos.X == before + c && carrier.Pos.Y == 120.125 && carrier.Pos.Z == 100.75, "current view preserved, " + label);
                Check(carrier.Pos.Motion.X == -0.31 * direction && carrier.Pos.Motion.Y == 0.02 && carrier.Pos.Yaw == 1.25f, "motion/angles preserved, " + label);
                Check(player.Pos.X == (mounted ? before + 0.35 : before) + c, "rider offset preserved, " + label);
                if (mounted && !announced && !positionFirst)
                {
                    // A fellow passenger's teleport packet (the server sends one per player aboard) must not move the boat
                    var other = Remote(3, true);
                    other.OnReceivedServerPacket(1, SerializerUtil.Serialize(target));
                    Check(carrier.Pos.X == before + c && Math.Abs(other.Pos.X - target.X) < 1e-9, "a fellow passenger's teleport leaves the boat where the client has it, " + label);
                }
                carrier.Pos.X -= 0.03125 * direction; player.Pos.X -= mounted ? 0.03125 * direction : 0;
                double moved = carrier.Pos.X;
                Position(12, target); // late companion packet, same exact server teleport target
                Check(carrier.Pos.X == moved, "companion teleport cannot pull back a later frame, " + label);
                var networkType = typeof(Packet_EntityPosition).Assembly.GetType("Vintagestory.Client.NoObf.SystemNetworkProcess");
                var physics = player.GetBehavior<EntityBehaviorControlledPhysics>();
                var prev = (Vec3d)AccessTools.Field(typeof(EntityBehaviorControlledPhysics), "prevPos").GetValue(physics);
                var cameraType = networkType.Assembly.GetType("Vintagestory.Client.NoObf.PlayerCamera");
                object camera = RuntimeHelpers.GetUninitializedObject(cameraType);
                AccessTools.Field(cameraType, "prevPos").SetValue(camera, new Vec3d());
                AccessTools.Method(cameraType, "OnPlayerPhysicsTick").Invoke(camera, new object[] { 0.008f, prev });
                var cameraPrev = (Vec3d)AccessTools.Field(cameraType, "prevPos").GetValue(camera);
                Check(Math.Abs(cameraPrev.X - player.Pos.X) < 1, "no-step physics frame keeps camera and water references on new lap, " + label);
                double stable = carrier.Pos.X;
                target.X -= 2 * c;
                Position(5, target); // obsolete tick must do nothing, even if its coordinate looks like another jump
                Check(carrier.Pos.X == stable, "out-of-order position packet cannot create a jump, " + label);
                Position(13, target, false, 0);
                Check(carrier.Pos.X == stable, "late pre-jump correction is discarded by position version, " + label);
                target.X = stable + 0.5;
                Position(14, target, false, 1);
                Check(carrier.Pos.X == target.X, "current-version ordinary corrections still apply, " + label);
            }
        }

        // A server that announces its wraps, and a teleport of about one ring it didn't announce (/cyl goseam, /tp,
        // another mod): it lands exactly where the server says, also when its position packet came first
        foreach (double direction in new[] { 1.0, -1.0 })
        foreach (bool mounted in new[] { false, true })
        foreach (bool positionFirst in new[] { false, true })
        {
            Setup(mounted, direction);
            ClearNotices();
            Notice(0, 0);
            var target = new Vec3d(carrier.Pos.X + (p.Circumference - 80) * direction, 119, 102);
            if (positionFirst) { Position(11, target); Teleport(target); }
            else Teleport(target);
            Check(carrier.Pos.X == target.X && carrier.Pos.Y == target.Y && carrier.Pos.Z == target.Z,
                $"an unannounced teleport of about one ring goes where the server says, direction={direction} mounted={mounted} position-first={positionFirst}");
        }

        // Another player's teleport one ring away from where this player is: onto the nearest lap
        foreach (double direction in new[] { 1.0, -1.0 })
        {
            Setup(false, direction);
            ClearNotices();
            var other = Remote(3, false);
            double x = player.Pos.X + 5 * direction;
            other.OnReceivedServerPacket(1, SerializerUtil.Serialize(new Vec3d(x + p.Circumference * direction, 119, 102)));
            Check(Math.Abs(other.Pos.X - x) < 1e-9 && other.Pos.Y == 119 && other.Pos.Z == 102,
                $"another player's teleport lands on the lap nearest the player, direction={direction}");
        }
        ClearNotices();
        FrameJumps(logger);
        ClearNotices();
        LapBoundary(logger);
        ClearNotices();
        object transitions = NewFeature("weather transitions", harmony, logger);
        AccessTools.Method(typeof(SeamJumpClient), "BindTransitions").Invoke(null, new[] { transitions });
        bool kept = AccessTools.Method(FeatureType, "Apply").Invoke(transitions, null) is true;
        Check(kept && Harmony.GetPatchInfo(AccessTools.Method(typeof(WeatherSystemClient), "ProcessWeatherUpdate"))?.Postfixes.Any(x => x.owner == harmony.Id) == true,
            "keeping weather transitions near the seam: every member it uses is there, the update handler is patched");
        AccessTools.Method(FeatureType, "Unpatch").Invoke(transitions, null);
        ClassicClouds(harmony, logger);
        MapRace(harmony, logger);
        harmony.UnpatchAll(harmony.Id);
    }

    static Type FeatureType => typeof(CylinderWorldCore).Assembly.GetType("CylinderWorld.GameFeature");
    static object NewFeature(string name, Harmony harmony, ILogger logger)
    {
        // (name, harmony, log, optional parameters with their defaults): reflection doesn't fill optional ones itself
        var ctor = FeatureType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length >= 3 && c.GetParameters().Skip(3).All(p => p.IsOptional));
        var args = new object[] { name, harmony, logger }.Concat(ctor.GetParameters().Skip(3).Select(p => p.DefaultValue)).ToArray();
        return ctor.Invoke(args);
    }

    /// <summary>SeamEntities' lap feature (other creatures and players on the lap nearest the player): bound and applied,
    /// or off as on a game without the interpolation members it needs. Whether it is on.</summary>
    static bool Laps(bool on, ILogger logger)
    {
        object f = NewFeature("other creatures on the nearest lap", null, logger);
        if (on)
        {
            AccessTools.Method(typeof(SeamEntities), "BindInterpolation").Invoke(null, new[] { f });
            AccessTools.Method(FeatureType, "Apply").Invoke(f, null);
        }
        AccessTools.Field(typeof(SeamEntities), "laps").SetValue(null, f);
        return AccessTools.Property(FeatureType, "On").GetValue(f) is true;
    }

    /// <summary>
    /// SeamJumpClient's frame (R2C1): a jump the teleport patches report is handled once, and a teleport of about one ring
    /// they leave to vanilla (one the server didn't announce: /cyl goseam after crossing, Earth's teleport across the Bering
    /// Strait) is still handled as a jump: the other creatures move onto the player's new lap, the water reference points
    /// move with it and the clouds are told (JumpCount/JumpTotal).
    /// </summary>
    static void FrameJumps(ILogger logger)
    {
        var core = new CylinderWorldCore();
        AccessTools.Property(typeof(CylinderWorldCore), "Params").SetValue(core, p);
        var client = new SeamJumpClient();
        AccessTools.Field(typeof(SeamJumpClient), "capi").SetValue(client, api);
        AccessTools.Field(typeof(SeamJumpClient), "core").SetValue(client, core);
        // The camera, the chunk view and the weather copy need a running game: off here, as on a game without their members
        foreach (var name in new[] { "camera", "chunkView", "weatherCopy" }) AccessTools.Field(typeof(SeamJumpClient), name).SetValue(client, NewFeature(name, null, logger));
        var trace = Activator.CreateInstance(typeof(CylinderWorldCore).Assembly.GetType("CylinderWorld.SeamJumpTrace"), api, logger, new Func<int>(() => -1));
        AccessTools.Field(typeof(SeamJumpClient), "trace").SetValue(client, trace);
        int Count() => (int)AccessTools.Field(typeof(SeamJumpClient), "JumpCount").GetValue(null);
        double Total() => (double)AccessTools.Field(typeof(SeamJumpClient), "JumpTotal").GetValue(null);
        void Frame() => client.OnRenderFrame(0.016f, EnumRenderStage.Before);
        double c = p.Circumference;

        Setup(false, 1);
        Notice(0, 0);   // a server that announces its wraps
        var creature = new TestCarrier { EntityId = 7, Api = api, World = world };
        Props(creature);
        double from = player.Pos.X;
        creature.Pos.SetPos(from + 10, 120, 100);
        entities[creature.EntityId] = creature;
        uniforms.playerReferencePos.Set(from, 0, 0);
        Frame();
        int count = Count();
        double total = Total();

        // Not announced: the teleport patches leave it to vanilla, which puts the player where the server says
        var target = new Vec3d(from + c - 90, 119, 102);
        Teleport(target);
        Frame();
        Check(player.Pos.X == target.X && Count() == count + 1 && Total() == total + c,
            "an unannounced teleport of about one ring is still handled as a seam jump (the clouds are told)");
        Check(creature.Pos.X == from + 10 + c && uniforms.playerReferencePos.X == from + c,
            "after an unannounced teleport of about one ring the other creatures are on the player's new lap, the water reference with them");
        Frame();
        Check(Count() == count + 1 && Total() == total + c, "the unannounced jump is handled once");

        // Announced: made and reported by the teleport patches; the move also looks like one ring between frames
        Notice(player.EntityId, -1);
        double before = player.Pos.X;
        Teleport(new Vec3d(before - c + 0.5, 119, 102));
        Frame();
        Frame();
        Check(player.Pos.X == before - c && Count() == count + 2 && Total() == total,
            "a reported seam jump is handled once, by the move the teleport patches made");
        Check(creature.Pos.X == from + 10 && uniforms.playerReferencePos.X == from,
            "after a reported seam jump the other creatures are on the player's new lap, the water reference with them");
        entities.Remove(creature.EntityId);
    }

    /// <summary>
    /// The lap feature is the one switch for other creatures and players: on, a creature's spawn, position packet and
    /// another player's teleport one ring away land on the lap nearest the player; off (a game without the interpolation
    /// members), all of them land where the server says, so their positions and their interpolation history stay on one
    /// lap. The player's own smooth jump works either way.
    /// </summary>
    static void LapBoundary(ILogger logger)
    {
        double c = p.Circumference;
        var arrived = AccessTools.Method(typeof(SeamEntities), "OnEntityArrived");
        var prefix = AccessTools.Method(jump, "PositionPacket");
        foreach (bool on in new[] { true, false })
        {
            Check(Laps(on, logger) == on, $"lap feature {(on ? "on" : "off")}");
            Setup(false, 1);
            ClearNotices();
            string label = on ? "onto the lap nearest the player" : "where the server says (lap feature off)";
            double x = player.Pos.X + 20;

            var spawned = new TestCarrier { EntityId = 8, Api = api, World = world };
            Props(spawned);
            spawned.Pos.SetPos(x + c, 120, 100);
            entities[spawned.EntityId] = spawned;
            arrived.Invoke(new SeamEntities(), new object[] { spawned });
            Check(spawned.Pos.X == (on ? x : x + c), "a creature spawning one ring away: " + label);

            var creature = new TestCarrier { EntityId = 9, Api = api, World = world };
            Props(creature);
            creature.Pos.SetPos(x, 120, 100);
            entities[creature.EntityId] = creature;
            var packet = new Packet_EntityPosition { EntityId = 9, Tick = 5, PositionVersion = 1, X = CollectibleNet.SerializeDoublePrecise(x + c),
                Y = CollectibleNet.SerializeDoublePrecise(120), Z = CollectibleNet.SerializeDoublePrecise(100) };
            object[] args = prefix.GetParameters().Select(a => a.Name switch { "packet" => (object)packet, "___bulkPositions" => false, _ => null }).ToArray();
            bool run = prefix.Invoke(null, args) is not false;
            Check(run && CollectibleNet.DeserializeDoublePrecise(packet.X) == (on ? x : x + c), "a creature's position packet one ring away: " + label);

            var other = Remote(3, false);
            other.OnReceivedServerPacket(1, SerializerUtil.Serialize(new Vec3d(x + c, 119, 102)));
            Check(other.Pos.X == (on ? x : x + c), "another player's teleport one ring away: " + label);

            // The server doesn't announce its wraps here: a jump by distance
            double before = player.Pos.X;
            Teleport(new Vec3d(before + c + 0.75, 119, 102));
            Check(player.Pos.X == before + c, $"the player's own seam jump keeps their place with the lap feature {(on ? "on" : "off")}");
        }
        Laps(true, logger);
        entities.Remove(8);
        entities.Remove(9);
    }

    /// <summary>The client map race fix locks the map chunk writers; map regions are a ConcurrentDictionary in 1.22.</summary>
    static void MapRace(Harmony harmony, ILogger logger)
    {
        var featureType = FeatureType;
        object feature = NewFeature("map race", harmony, logger);
        AccessTools.Field(typeof(ClientMapRaceFix), "race").SetValue(null, feature);
        AccessTools.Method(typeof(ClientMapRaceFix), "Bind").Invoke(null, new[] { feature, logger });
        bool applied = AccessTools.Method(featureType, "Apply").Invoke(feature, null) is true;
        var lib = typeof(Packet_EntityPosition).Assembly;
        bool Locked(string type, string method) => Harmony.GetPatchInfo(AccessTools.Method(lib.GetType(type), method))?.Prefixes.Any(x => x.owner == harmony.Id) == true;
        Check(applied && Locked("Vintagestory.Client.NoObf.SystemNetworkProcess", "ProcessInBackground") && Locked("Vintagestory.Client.NoObf.SystemUnloadChunks", "HandleChunkUnload")
            && !Locked("Vintagestory.Client.NoObf.ClientWorldMap", "UnloadMapRegion") && AccessTools.Field(typeof(ClientMapRaceFix), "lockRegions").GetValue(null) is false,
            "client map race fix: map chunk writers locked, map regions (a ConcurrentDictionary in 1.22) not");
        AccessTools.Method(featureType, "Unpatch").Invoke(feature, null);
        AccessTools.Field(typeof(ClientMapRaceFix), "race").SetValue(null, null);
        // A game whose client map has no MapRegions field: its own check says so (one line), regions stay unlocked; the
        // map chunk fix doesn't depend on it
        object inspect = NewFeature("map regions", null, logger);
        bool regionsLocked = AccessTools.Method(typeof(ClientMapRaceFix), "RegionsNeedLock").Invoke(null, new[] { inspect, typeof(TestCarrier) }) is true;
        Check(!regionsLocked && AccessTools.Property(featureType, "On").GetValue(inspect) is false,
            "client map race fix: a client map without MapRegions leaves the regions unlocked, in a check of its own");
    }

    /// <summary>The classic clouds' undulation noise reads (centre tile − TileOffsetX): the same before and after a jump
    /// while they draw, after CloudTick committed a new state; TileOffsetX itself (part of the thickness key) as before.</summary>
    static void ClassicClouds(Harmony harmony, ILogger logger)
    {
        Setup(false, -1);
        var clouds = typeof(CylinderWorldCore).Assembly.GetType("CylinderWorld.SeamClouds");
        AccessTools.Method(clouds, "Patch").Invoke(null, new object[] { harmony, logger });
        var t = typeof(CloudRenderer);
        object r = RuntimeHelpers.GetUninitializedObject(t);
        AccessTools.Property(t, "CloudTileSize").SetValue(r, 50);
        var states = new[] { "committedState", "mainThreadState", "offThreadState" }.Select(n => AccessTools.Field(t, n)).ToArray();
        foreach (var f in states)
        {
            var s = new CloudTilesState { WindTileOffsetX = 77 };
            s.CenterTilePos.Set((int)player.Pos.X / 50, 0, 900);
            f.SetValue(r, s);
        }
        AccessTools.Field(t, "windOffsetX").SetValue(r, 12.5);
        AccessTools.Field(t, "newStateRready").SetValue(r, true);
        CloudTilesState State(int i) => (CloudTilesState)states[i].GetValue(r);
        int undulation = State(0).CenterTilePos.X - State(0).TileOffsetX, thickness = State(0).CenterTilePos.X - State(0).WindTileOffsetX + State(0).TileOffsetX;
        var before = AccessTools.Method(clouds, "BeforeRender");
        var afterTick = AccessTools.Method(clouds, "AfterCloudTick");
        var after = AccessTools.Method(clouds, "AfterRender");
        void Frame(bool commit)
        {
            object[] args = { r, null };
            bool run = before.Invoke(null, args) is not false;
            if (run && commit) State(0).Set(State(2)); // CloudTick committing the worker's state
            if (run) afterTick.Invoke(null, new object[] { r });
            Check(!run || State(0).CenterTilePos.X - State(0).TileOffsetX == undulation, $"classic clouds: undulation key kept while drawing, commit={commit}");
            after.Invoke(null, new object[] { r, null, args[1] });
            Check(State(0).TileOffsetX == 0 && State(2).TileOffsetX == 0, $"classic clouds: TileOffsetX itself unchanged after drawing, commit={commit}");
        }
        Frame(false); // the renderer's first frame: nothing to do yet
        // The teleport patch moved the player by -C and SeamJumpClient counted the jump
        player.Pos.X -= p.Circumference;
        AccessTools.Field(typeof(SeamJumpClient), "JumpCount").SetValue(null, (int)AccessTools.Field(typeof(SeamJumpClient), "JumpCount").GetValue(null) + 1);
        AccessTools.Field(typeof(SeamJumpClient), "JumpTotal").SetValue(null, (double)AccessTools.Field(typeof(SeamJumpClient), "JumpTotal").GetValue(null) - p.Circumference);
        Frame(false);
        Check(Math.Abs(State(0).CenterTilePos.X - (int)player.Pos.X / 50) <= 1 && State(0).CenterTilePos.X - State(0).WindTileOffsetX + State(0).TileOffsetX == thickness,
            "classic clouds: rebased onto the new lap, thickness key kept");
        Frame(true);
    }

    /// <summary>Cube block variants (BleedingTextureHelper), plant offsets and decals hash x around the ring.</summary>
    static void RingHashes(Harmony harmony)
    {
        var lib = typeof(Packet_EntityPosition).Assembly;
        bool Transpiled(MethodBase m) => m != null && Harmony.GetPatchInfo(m)?.Transpilers.Any(x => x.owner == harmony.Id) == true;
        var face = AccessTools.Method(lib.GetType("Vintagestory.Client.NoObf.BleedingTextureHelper"), "GetFaceTextureSubId");
        Check(Transpiled(face), "cube texture variants (BleedingTextureHelper.GetFaceTextureSubId) are ring-hashed");
        var calls = PatchProcessor.GetCurrentInstructions(face).Select(i => i.operand).OfType<MethodInfo>().ToList();
        Check(calls.Contains(AccessTools.Method(jump, "MurmurHash3")) && calls.Contains(AccessTools.Method(jump, "TiledTexturesSelector"))
            && !calls.Any(m => m.DeclaringType == typeof(GameMath) && m.Name == "MurmurHash3"),
            "the cube texture helper calls the ring hash and the ring tile selector, no absolute hash");
        Check(AccessTools.GetDeclaredMethods(lib.GetType("Vintagestory.Client.NoObf.ChunkTesselator")).Where(m => m.Name == "TesselateBlock").Any(Transpiled), "plant offsets (ChunkTesselator.TesselateBlock) are ring-hashed");
        Check(AccessTools.GetDeclaredMethods(lib.GetType("Vintagestory.Client.NoObf.SystemRenderDecals")).Any(Transpiled), "block decals are ring-hashed like the meshes they cover");

        int c = (int)p.Circumference, x0 = (int)p.JumpInset + 1234;
        object Ring(string name, params object[] a) => AccessTools.Method(jump, name).Invoke(null, a);
        Check((int)Ring("MurmurHash3", x0, 5, 7) == GameMath.MurmurHash3(x0, 5, 7) && (int)Ring("MurmurHash3Mod", x0, 5, 7, 4) == GameMath.MurmurHash3Mod(x0, 5, 7, 4)
            && (int)Ring("OaatHash", x0, 5, 7) == GameMath.oaatHash(x0, 5, 7) && (uint)Ring("OaatHashU", x0, 5, 7) == GameMath.oaatHashU(x0, 5, 7),
            "inside the ring the hashes are vanilla's");
        bool same = true;
        foreach (int x in new[] { x0, (int)p.JumpInset, (int)p.JumpInset + c - 1 })
        {
            foreach (int lap in new[] { -c, c })
            {
                same &= (int)Ring("MurmurHash3", x + lap, 5, 7) == (int)Ring("MurmurHash3", x, 5, 7);
                same &= (int)Ring("MurmurHash3Mod", x + lap, 5, 7, 4) == (int)Ring("MurmurHash3Mod", x, 5, 7, 4);
                same &= (int)Ring("OaatHash", x + lap, 5, 7) == (int)Ring("OaatHash", x, 5, 7);
                same &= (uint)Ring("OaatHashU", x + lap, 5, 7) == (uint)Ring("OaatHashU", x, 5, 7);
            }
        }
        Check(same, "a block one ring over hashes like its twin");
        var tiles = Enumerable.Range(0, 9).Select(_ => new BakedCompositeTexture { BakedName = new AssetLocation("game:block/tile"), TilesWidth = 3 }).ToArray();
        int Tile(int x, int side) => (int)Ring("TiledTexturesSelector", tiles, side, x, 3, 4);
        bool tilesSame = true, vanillaDiffers = false;
        for (int side = 0; side < 6; side++)
        {
            tilesSame &= Tile(x0, side) == BakedCompositeTexture.GetTiledTexturesSelector(tiles, side, x0, 3, 4) && Tile(x0 + c, side) == Tile(x0, side) && Tile(x0 - c, side) == Tile(x0, side);
            vanillaDiffers |= BakedCompositeTexture.GetTiledTexturesSelector(tiles, side, x0 + c, 3, 4) != BakedCompositeTexture.GetTiledTexturesSelector(tiles, side, x0, 3, 4);
        }
        Check(tilesSame && vanillaDiffers, "texture tiles: vanilla's inside the ring, the same one ring over (where vanilla's differ)");
    }

    /// <summary>The server's entity tracking (VintagestoryLib loads here too): the ring distance goes after exactly the
    /// one x difference, x being the local stored from the entity's Pos.X.</summary>
    static void TrackingTranspiler()
    {
        var method = AccessTools.Method(typeof(Packet_EntityPosition).Assembly.GetType("Vintagestory.Server.PhysicsManager"), "UpdateTrackedEntityState");
        var original = PatchProcessor.GetOriginalInstructions(method);
        var transpiler = AccessTools.Method(typeof(SeamEntities), "RingDistance");
        var result = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original.Select(i => i.Clone()).ToList() })).ToList();
        var ringDx = AccessTools.Method(typeof(SeamEntities), "RingDx", new[] { typeof(double) });
        int at = result.FindIndex(i => i.Calls(ringDx));
        var getX = AccessTools.PropertyGetter(typeof(EntityPos), "X");
        int xLocal = original.FindIndex(i => i.Calls(getX));
        Check(result.Count(i => i.Calls(ringDx)) == 1 && at > 4 && result[at - 1].opcode == System.Reflection.Emit.OpCodes.Sub
            && result[at - 2].opcode == System.Reflection.Emit.OpCodes.Ldelem_R8 && xLocal >= 0 && original[xLocal + 1].IsStloc()
            && result[at - 5].IsLdloc() && result[at - 5].LocalIndex() == original[xLocal + 1].LocalIndex(),
            "entity tracking: the ring distance wraps x - (a player's x), x from the entity's Pos.X, once");
        // The y difference has the same shape around an index expression; doubled x differences are refused
        var doubled = original.Select(i => i.Clone()).ToList();
        int sub = result.FindIndex(i => i.Calls(ringDx)) - 1;
        doubled.InsertRange(sub + 1, original.Skip(sub - 4).Take(5).Select(i => i.Clone()).Append(new CodeInstruction(System.Reflection.Emit.OpCodes.Pop)));
        var refused = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { doubled })).ToList();
        Check(!refused.Any(i => i.Calls(ringDx)), "entity tracking: two candidate x differences patch nothing");
    }

    static void Position(int tick, Vec3d target, bool teleport = true, int version = 1)
    {
        var packet = new Packet_EntityPosition { EntityId = carrier.EntityId, Tick = tick, Teleport = teleport, PositionVersion = version,
            X = CollectibleNet.SerializeDoublePrecise(target.X), Y = CollectibleNet.SerializeDoublePrecise(target.Y), Z = CollectibleNet.SerializeDoublePrecise(target.Z),
            MotionX = 0, MotionY = 0, MotionZ = 0 };
        var prefix = AccessTools.Method(jump, "PositionPacket");
        // Arguments by name, as Harmony passes them: the packet, the network system's bulk flag, the state for the postfix
        object[] args = prefix.GetParameters().Select(a => a.Name switch { "packet" => (object)packet, "___bulkPositions" => false, _ => null }).ToArray();
        bool run = prefix.Invoke(null, args) is not false;
        object state = args[Array.FindIndex(prefix.GetParameters(), a => a.Name == "__state")];
        if (run && packet.Tick > carrier.Attributes.GetInt("tick"))
        {
            carrier.Attributes.SetInt("tick", packet.Tick);
            carrier.Pos.SetFromPacket(packet, carrier, false);
            carrier.OnReceivedServerPos(packet.Teleport);
        }
        AccessTools.Method(jump, "AfterPositionPacket")?.Invoke(null, new[] { packet, state });
    }
    static void Teleport(Vec3d target) => player.OnReceivedServerPacket(1, SerializerUtil.Serialize(target));
    static void Notice(long entityId, int laps) => AccessTools.Method(typeof(SeamEntities), "Notice").Invoke(null, new object[] { entityId, laps });
    static void ClearNotices() => AccessTools.Method(typeof(SeamEntities), "ClearNotices").Invoke(null, null);
    static bool NoPartition() => false;
    static void Props(Entity e)
    {
        var props = new EntityProperties { Client = new EntityClientProperties(Array.Empty<JsonObject>(), new()) };
        AccessTools.Property(typeof(Entity), "Properties").SetValue(e, props);
    }
    /// <summary>Another player's entity on this client (optionally aboard the local carrier).</summary>
    static EntityPlayer Remote(long id, bool aboard)
    {
        var other = new EntityPlayer { EntityId = id, Api = api, World = world };
        Props(other);
        other.Pos.SetPos(carrier.Pos.X, 120.125, 100.75);
        entities[id] = other;
        if (aboard)
        {
            var seat = SurfaceProxy.Make<IMountableSeat>((m, a) => m.Name == "get_Entity" ? carrier : SurfaceProxy.Default(m));
            AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(other, seat);
        }
        return other;
    }
    static void Setup(bool mounted, double direction)
    {
        entities.Clear();
        player = new EntityPlayer { EntityId = 1, Api = api, World = world };
        playerData.EntityPlayer = player;
        carrier = mounted ? new TestCarrier { EntityId = 2, Api = api, World = world } : player;
        foreach (var e in new[] { player, carrier }.Distinct())
        {
            Props(e);
            var props = e.Properties;
            e.Pos.SetPos(direction > 0 ? p.JumpInset - 0.125 : p.WorldWidth - p.JumpInset + 0.125, 120.125, 100.75);
            e.Pos.Motion.Set(-0.31 * direction, 0.02, 0.1); e.Pos.Yaw = 1.25f;
            e.PreviousServerPos.SetFrom(e.Pos);
            e.Attributes.SetInt("tick", 10);
            var physics = new EntityBehaviorPlayerPhysics(e);
            props.Client.Behaviors.Add(physics);
            ((Vec3d)AccessTools.Field(typeof(EntityBehaviorControlledPhysics), "prevPos").GetValue(physics)).Set(e.Pos.X + 0.125, e.Pos.Y, e.Pos.Z);
            entities[e.EntityId] = e;
        }
        if (mounted)
        {
            var seat = SurfaceProxy.Make<IMountableSeat>((m, a) => m.Name == "get_Entity" ? carrier : SurfaceProxy.Default(m));
            AccessTools.Property(typeof(EntityAgent), "MountedOn").SetValue(player, seat);
            player.Pos.X += 0.35;
        }
    }
}
