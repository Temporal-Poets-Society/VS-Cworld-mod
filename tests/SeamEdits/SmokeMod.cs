using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CylinderWorld;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Common;
using Vintagestory.GameContent;
using Vintagestory.Server;

// A disposable server mod. The same source runs against an older build and the fix; no graphical client.
public class SeamEditSmoke : ModSystem
{
    ICoreServerAPI api;
    SeamParams p;
    BlockPos a, b;
    bool reload, done;
    int failures, checks;
    Harmony tests;
    /// <summary>SeamBlocks' journal (canonical position -> cell).</summary>
    System.Collections.IDictionary journal;
    static readonly List<(int client, int id, int x, int y, int z)> packets = new();
    const int ClientA = 100001, ClientB = 100002;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
    public override double ExecuteOrder() => 1;
    public override void Start(ICoreAPI api) => api.RegisterBlockEntityClass(ThrowingEntity.ClassName, typeof(ThrowingEntity));
    public override void StartServerSide(ICoreServerAPI api)
    {
        this.api = api;
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ => Start(), 1000));
        api.Event.RegisterCallback(_ => End("timeout"), 180000);
    }
    void Check(bool ok, string name)
    {
        checks++;
        if (!ok) failures++;
        Mod.Logger.Notification("SEAMEDIT {0} {1}", ok ? "PASS" : "FAIL", name);
    }
    void Start()
    {
        try
        {
            p = api.ModLoader.GetModSystem<CylinderWorldCore>().Params;
            journal = (System.Collections.IDictionary)AccessTools.Field(typeof(SeamBlocks), "cells").GetValue(api.ModLoader.GetModSystem<SeamBlocks>());
            reload = api.WorldManager.SaveGame.GetData("seamedit-test-phase") != null;
            a = new BlockPos(p.JumpInset + 2, api.World.SeaLevel + 12, p.WorldLength / 2 + 11);
            b = a.Copy(); b.X += (int)p.Circumference;
            Load(a, () =>
            {
                if (!reload)
                {
                    Check(api.World.BlockAccessor.GetChunkAtBlockPos(b) == null, "test starts with counterpart unloaded");
                    api.World.BlockAccessor.SetBlock(Block("planks-oak-ud").Id, a);
                }
                Load(b, Analyze);
            });
        }
        catch (Exception e) { End(e.ToString()); }
    }
    void Load(BlockPos pos, Action next) => api.WorldManager.LoadChunkColumnPriority(pos.X / 32, pos.Z / 32,
        new ChunkLoadOptions { KeepLoaded = true, OnLoaded = () => api.Event.EnqueueMainThreadTask(() =>
        {
            try { next(); } catch (Exception e) { End(e.ToString()); }
        }, "seam-edit-smoke") });
    Block Block(string path) => api.World.GetBlock(new AssetLocation("game", path)) ?? throw new Exception("missing block " + path);
    Item Flint => api.World.GetItem(new AssetLocation("game", "flint")) ?? throw new Exception("missing item flint");
    Block At(BlockPos pos, int layer = BlockLayersAccess.Solid) => api.World.BlockAccessor.GetBlock(pos, layer);
    IInventory Inventory(BlockPos pos) => ((IBlockEntityContainer)api.World.BlockAccessor.GetBlockEntity(pos)).Inventory;
    bool MirroringOn()
    {
        var feature = AccessTools.Field(typeof(SeamBlocks), "feature").GetValue(null);
        return feature != null && (bool)AccessTools.Property(feature.GetType(), "On").GetValue(feature);
    }
    void Equal(string name, BlockPos pos = null)
    {
        pos ??= a;
        var twin = pos.Copy(); twin.X += pos.X < p.WorldWidth / 2 ? (int)p.Circumference : -(int)p.Circumference;
        Check(At(pos).Id == At(twin).Id && At(pos, BlockLayersAccess.Fluid).Id == At(twin, BlockLayersAccess.Fluid).Id, name);
    }
    void Analyze()
    {
        var ba = api.World.BlockAccessor;
        if (reload)
        {
            Equal("solid/fluid edits survive shutdown and reload");
            Check(!journal.Contains(a.AddCopy(0, 0, 7)) && !journal.Contains(a.AddCopy(0, 0, 8)), "journal cells holding only snow cover (older builds' snow: a layer, tall grass under snow) are dropped at load");
            Check(At(a).Id == Block("planks-birch-ud").Id, "latest opposite-side edit persisted (actual=" + At(a).Code + ")");
            var chest = a.AddCopy(0, 1, 0); var chestTwin = b.AddCopy(0, 1, 0);
            Equal("container block persists", chest);
            Check(Inventory(chest)[0].StackSize == 7 && Inventory(chestTwin)[0].StackSize == 7, "one inventory state persists on both sides");
            Check(Inventory(chest)[1].Empty && Inventory(chestTwin)[1].Empty, "taken stack stays removed after reload");
            Check(Inventory(chest)[2].StackSize == 3 && Inventory(chestTwin)[2].StackSize == 3, "slot transfer without MarkDirty persists on both sides");
            // Remove through the replica, then make sure the authority also loses its BE and contents.
            int DroppedPlanks() => api.World.LoadedEntities.Values.OfType<EntityItem>()
                .Where(e => e.Itemstack?.Collectible.Code.Path == "planks-oak-ud").Sum(e => e.Itemstack.StackSize);
            int beforeDrops = DroppedPlanks();
            ba.BreakBlock(chestTwin, null, 1);
            Check(DroppedPlanks() - beforeDrops == 7, "container inventory drops once when replica is broken");
            Check(ba.GetBlockEntity(chest) == null && ba.GetBlockEntity(chestTwin) == null, "breaking replica removes both block entities");
            Equal("breaking replica removes both blocks", chest);
            var farSource = a.AddCopy(0, 0, 640); var farTarget = b.AddCopy(0, 0, 640);
            Check(ba.GetChunkAtBlockPos(farSource) == null, "journal replay test keeps saved source unloaded");
            Load(farTarget, () =>
            {
                Check(At(farTarget).Id == Block("planks-oak-ud").Id, "unloaded-counterpart journal survives shutdown before target generation");
                CheckHopperReplay(() => CheckShutdownDrain(() => End(null)));
            });
            return;
        }
        Equal("change reaches a counterpart generated later");
        // Only the band's identical part is paired: the ring-side ramp beyond its wavy edge lies opposite the far edge's sea
        var twinOf = AccessTools.Method(typeof(SeamBlocks), "Twin");
        bool Paired(int x) => (bool)twinOf.Invoke(null, new object[] { p, new BlockPos(x, a.Y, a.Z), null });
        int ramp = (int)Math.Ceiling(p.SeamEdge(p.JumpInset, a.Z + 0.5) - 0.5);
        Check(Paired(a.X) && (ramp >= 2 * p.JumpInset || !Paired(ramp) && Paired(ramp - 1)),
            $"twins only within the identical part (edge at x={ramp})");
        ba.SetBlock(Block("planks-birch-ud").Id, b);
        Equal("placement from the opposite copy");
        ba.SetBlock(0, a);
        Equal("breaking on the canonical side");
        ba.SetBlock(Block("saltwater-still-7").Id, b, BlockLayersAccess.Fluid);
        Equal("fluid change from replica");
        ba.SetBlock(0, a, BlockLayersAccess.Fluid);
        Equal("fluid removal");
        ba.SetBlock(Block("planks-oak-ud").Id, a);
        ba.ExchangeBlock(Block("planks-birch-ud").Id, b);
        Equal("ExchangeBlock preserves mirrored state");
        // A raw edit followed by the game's required modification notification (WorldEdit/bulk style).
        var chunk = ba.GetChunkAtBlockPos(a); chunk.Unpack();
        int i = ((a.Y & 31) * 32 + (a.Z & 31)) * 32 + (a.X & 31);
        chunk.Data[i] = Block("planks-oak-ud").Id;
        ba.MarkBlockModified(a);
        Equal("bulk/direct change with MarkBlockModified");
        chunk.Data[i] = Block("planks-birch-ud").Id;
        var map = (ServerWorldMap)AccessTools.Field(typeof(BlockAccessorBase), "worldmap").GetValue(ba);
        map.SendBlockUpdateBulkMinimal(new Dictionary<BlockPos, BlockUpdate> { [a.Copy()] = default });
        Equal("minimal bulk change");
        ba.ExchangeBlock(Block("planks-birch-ud").Id, a);
        ba.SetDecor(Block("planks-oak-ud"), a, BlockFacing.NORTH);
        Check(ba.GetSubDecors(b)?.Values.Any(x => x.Id == Block("planks-oak-ud").Id) == true, "decoration state reaches counterpart");
        ba.SetDecor(api.World.Blocks[0], b, BlockFacing.NORTH);
        Check(ba.GetSubDecors(a)?.Count is null or 0, "decoration removal reaches authority");

        var chestPos = a.AddCopy(0, 1, 0); var chestTwinPos = b.AddCopy(0, 1, 0);
        var chestBlock = api.World.Blocks.First(x => x?.Code?.Path == "chest-east");
        ba.SetBlock(chestBlock.Id, chestPos);
        Check(ba.GetBlockEntity(chestTwinPos) != null, "container block entity created on replica");
        if (ba.GetBlockEntity(chestTwinPos) != null)
        {
            var stack = new ItemStack(Block("planks-oak-ud"), 11);
            Inventory(chestPos)[0].Itemstack = stack;
            Inventory(chestPos)[0].MarkDirty();
            ba.GetBlockEntity(chestPos).MarkDirty();
            Check(Inventory(chestTwinPos)[0].StackSize == 11, "container inventory reaches replica");
            Inventory(chestTwinPos)[0].TakeOut(4);
            Inventory(chestTwinPos)[0].MarkDirty();
            ba.GetBlockEntity(chestTwinPos).MarkDirty();
            Check(Inventory(chestPos)[0].StackSize == 7, "inventory taken through replica updates authority");
            Inventory(chestPos)[1].Itemstack = new ItemStack(Block("planks-birch-ud"), 3);
            ba.GetBlockEntity(chestPos).MarkDirty();
            Inventory(chestTwinPos)[1].TakeOutWhole();
            ba.GetBlockEntity(chestTwinPos).MarkDirty();
            Check(Inventory(chestPos)[1].Empty, "taken whole stack is removed from both inventories");
            // A GUI transfer reports only the slot change: vanilla's InWorldContainer marks the chunk, not the block entity
            new DummySlot(new ItemStack(Block("planks-birch-ud"), 5)).TryPutInto(api.World, Inventory(chestPos)[2], 5);
            Check(Inventory(chestTwinPos)[2].StackSize == 5, "slot transfer without MarkDirty reaches replica");
            Inventory(chestTwinPos)[2].TryPutInto(api.World, new DummySlot(), 2);
            Check(Inventory(chestPos)[2].StackSize == 3, "slot transfer without MarkDirty through replica updates authority");
        }
        CheckSuppress();
        CheckSnowScope();
        CheckSnowLanding();
        JournalOldSnow();
        CheckUnregistered();
        CheckPackets();
        api.WorldManager.SaveGame.StoreData("seamedit-test-phase", new byte[] { 1 });
        var silent = SilentSlotChange(chestPos, chestTwinPos);
        CheckTickAuthority(chestPos, chestTwinPos, silent);
    }

    /// <summary>A write inside SeamBlocks.Suppress() (the live sea-ice update) is not copied, and drops the older journal
    /// cell for its position, which a replay after a restart would otherwise put back (snow over open water).</summary>
    void CheckSuppress()
    {
        var ba = api.World.BlockAccessor;
        var pos = a.AddCopy(0, 0, 2); var twin = b.AddCopy(0, 0, 2);
        int birch = Block("planks-birch-ud").Id;
        ba.SetBlock(birch, pos);
        bool journaled = journal.Contains(pos) && At(twin).Id == birch;
        var quiet = (IDisposable)AccessTools.Method(typeof(SeamBlocks), "Suppress").Invoke(null, null);
        try { ba.SetBlock(0, pos); }
        finally { quiet.Dispose(); }
        Check(journaled && At(pos).Id == 0 && At(twin).Id == birch && !journal.Contains(pos),
            "a write inside Suppress is not copied and drops the older journal cell");
        ba.SetBlock(0, twin);
        Equal("an ordinary edit after it reaches both copies again", pos);
    }

    /// <summary>CW-0010 (CW-D51): vanilla's snow accumulation commit is patched, and a write inside it is left to each copy
    /// (not copied, not journaled) while an older journal cell for its position (a player's block) stays.</summary>
    void CheckSnowScope()
    {
        var target = AccessTools.Method(typeof(WeatherSimulationSnowAccum), "OnServerTick100ms", new[] { typeof(float) });
        var info = target == null ? null : Harmony.GetPatchInfo(target);
        Check(info != null && info.Prefixes.Any(x => x.owner == "cylinderworld.seamblocks.snow") && info.Finalizers.Any(x => x.owner == "cylinderworld.seamblocks.snow"),
            "vanilla's snow accumulation commit is patched");
        var ba = api.World.BlockAccessor;
        var pos = a.AddCopy(0, 0, 5); var twin = b.AddCopy(0, 0, 5);
        int birch = Block("planks-birch-ud").Id, snow = Block("snowlayer-1").Id;
        ba.SetBlock(birch, pos);
        bool journaled = journal.Contains(pos) && At(twin).Id == birch;
        var blocks = api.ModLoader.GetModSystem<SeamBlocks>();
        var snowing = AccessTools.Field(typeof(SeamBlocks), "snowing");
        snowing.SetValue(blocks, 1);
        try { ba.SetBlock(snow, pos); ba.SetBlock(snow, pos.UpCopy()); }
        finally { snowing.SetValue(blocks, 0); }
        Check(journaled && At(pos).Id == snow && At(twin).Id == birch && At(twin.UpCopy()).Id == 0 && journal.Contains(pos) && !journal.Contains(pos.UpCopy()),
            "a snow accumulation write is left to each copy, and the older journal cell stays");
        ba.SetBlock(0, pos.UpCopy()); ba.SetBlock(0, twin.UpCopy());
        ba.SetBlock(0, pos);
        Equal("an ordinary edit after it reaches both copies again", pos);
    }

    /// <summary>CW-D51 (re-review): with snow left to each copy the destination can lie a layer or two deeper in snow; the
    /// landing check lets an entity onto it (snowlayer-2 here, snowlayer-5 there) and still refuses a full snow block.</summary>
    void CheckSnowLanding()
    {
        var ba = api.World.BlockAccessor;
        var pos = a.AddCopy(0, 0, 6); var twin = b.AddCopy(0, 0, 6);
        var blocks = api.ModLoader.GetModSystem<SeamBlocks>();
        var snowing = AccessTools.Field(typeof(SeamBlocks), "snowing");
        void Snow(BlockPos at, string code)
        {
            snowing.SetValue(blocks, 1);
            try { ba.SetBlock(code == null ? 0 : Block(code).Id, at); }
            finally { snowing.SetValue(blocks, 0); }
        }
        ba.SetBlock(Block("planks-oak-ud").Id, pos);
        Snow(pos.UpCopy(), "snowlayer-2");
        Snow(twin.UpCopy(), "snowlayer-5");
        var wrap = api.ModLoader.GetModSystem<SeamWrapSystem>();
        var passable = AccessTools.Method(typeof(SeamWrapSystem), "IsPassable");
        var probe = api.World.ClassRegistry.CreateEntity(api.World.GetEntityType(new AssetLocation("game:boat-raft-aged")));
        // Standing on the source's snowlayer-2, landing at the same height on the other side
        bool Passable() => (bool)passable.Invoke(wrap, new object[] { twin.X + 0.5, pos.Y + 1.125, twin.Z + 0.5, probe });
        bool onDeeperSnow = At(pos).Id == At(twin).Id && At(twin.UpCopy()).Code.Path == "snowlayer-5" && Passable();
        Snow(twin.UpCopy(), "snowblock");
        bool onSnowBlock = Passable();
        Check(onDeeperSnow && !onSnowBlock, $"the landing check lets an entity onto a deeper snow layer, not into a snow block (layer {onDeeperSnow}, block {onSnowBlock})");
        Snow(pos.UpCopy(), null); Snow(twin.UpCopy(), null);
        ba.SetBlock(0, pos);
        Equal("the snow landing test leaves both copies cleared", pos);
    }

    /// <summary>CW-D51 (re-review): a journal cell holding only a snow layer, as 1.0.4 to 1.1.0-rc.4 journaled vanilla's
    /// snow, is dropped at the next load (the reload checks it).</summary>
    void JournalOldSnow()
    {
        var blocks = api.ModLoader.GetModSystem<SeamBlocks>();
        foreach (var (dz, code) in new[] { (7, "game:snowlayer-3"), (8, "game:tallgrass-medium-snow2") })
        {
            var cell = a.AddCopy(0, 0, dz);
            journal[cell] = new SeamBlocks.Cell { X = cell.X, Y = cell.Y, Z = cell.Z, Solid = code, Fluid = "game:air" };
            AccessTools.Method(typeof(SeamBlocks), "Index").Invoke(blocks, new object[] { cell });
        }
    }

    /// <summary>A block entity whose class has no registered name can't be made on the other side: each copy keeps its
    /// own, and the older journal cell for the position goes (replayed, it put another block under that entity).</summary>
    void CheckUnregistered()
    {
        var ba = api.World.BlockAccessor;
        var pos = a.AddCopy(0, 0, 3);
        ba.SetBlock(Block("planks-birch-ud").Id, pos);
        bool journaled = journal.Contains(pos);
        var stray = new StrayEntity { Pos = pos.Copy(), Block = At(pos) };
        stray.Initialize(api);
        ba.SpawnBlockEntity(stray);
        Check(journaled && MirroringOn() && !journal.Contains(pos), "an unregistered block entity is not copied and drops the older journal cell");
        ba.RemoveBlockEntity(pos);
        ba.SetBlock(0, pos);
    }

    /// <summary>An inventory that changes a slot after base.DidModifyItemSlot returns, with no notification (the firepit
    /// drops and clears its cooking slots when the pot is taken out). Returns the check to make after the next tick.</summary>
    Action SilentSlotChange(BlockPos pos, BlockPos twin)
    {
        var ba = api.World.BlockAccessor;
        if (ba.GetBlockEntity(pos) == null || ba.GetBlockEntity(twin) == null) return () => Check(false, "chest on both copies for the re-capture test");
        var inv = Inventory(pos); var other = Inventory(twin);
        inv[3].Itemstack = new ItemStack(Block("planks-birch-ud"), 4);
        inv[4].Itemstack = new ItemStack(Block("planks-birch-ud"), 6);
        ba.GetBlockEntity(pos).MarkDirty();
        bool copied = other[3].StackSize == 4 && other[4].StackSize == 6;
        inv[3].TakeOutWhole();      // reported: captured at once, slot 4 still full
        inv[4].Itemstack = null;    // then changed silently, as an override does after calling base
        bool before = other[3].Empty && other[4].StackSize == 6;
        return () => Check(copied && before && other[4].Empty,
            $"a slot changed silently right after a reported change reaches the other copy at the next tick (in step before it: {before})");
    }

    void CheckTickAuthority(BlockPos pos, BlockPos twin, Action afterTick)
    {
        var ba = api.World.BlockAccessor;
        var first = ba.GetBlockEntity(pos); var second = ba.GetBlockEntity(twin);
        if (first == null || second == null) { Check(false, "both block entities present for tick test"); End(null); return; }
        int canonical = 0, replica = 0;
        long one = first.RegisterGameTickListener(_ => canonical++, 50);
        long two = second.RegisterGameTickListener(_ => replica++, 50);
        api.Event.RegisterCallback(_ =>
        {
            first.UnregisterGameTickListener(one); second.UnregisterGameTickListener(two);
            Check(canonical > 0 && replica == 0, $"one loaded block entity simulates (authority={canonical}, replica={replica})");
            Check(At(a).Id == Block("planks-birch-ud").Id, "last update still live at save (actual=" + At(a).Code + ")");
            afterTick();
            var farSource = a.AddCopy(0, 0, 640); var farTarget = b.AddCopy(0, 0, 640);
            Load(farSource, () =>
            {
                Check(ba.GetChunkAtBlockPos(farTarget) == null, "another target remains unloaded through shutdown");
                ba.SetBlock(Block("planks-oak-ud").Id, farSource);
                // Two world saves: a cell both copies hold leaves the journal at the second (the reload checks then read
                // it from the saved chunks); one whose other copy was never loaded stays.
                var blocks = api.ModLoader.GetModSystem<SeamBlocks>();
                var save = AccessTools.Method(typeof(SeamBlocks), "Save");
                bool heldBefore = journal.Contains(a);
                save.Invoke(blocks, null); save.Invoke(blocks, null);
                Check(heldBefore && !journal.Contains(a) && journal.Contains(farSource), $"journal drops cells both copies hold, keeps pending ones ({journal.Count} left)");
                // A journal cell naming a block this game doesn't have (a removed mod) is skipped at load, not a crash
                var bad = a.AddCopy(0, 0, 1280);
                journal[bad] = new SeamBlocks.Cell { X = bad.X, Y = bad.Y, Z = bad.Z, Solid = "nosuchmod:nosuchblock", Fluid = "game:air" };
                AccessTools.Method(typeof(SeamBlocks), "Index").Invoke(blocks, new object[] { bad });
                Load(bad, () =>
                {
                    // Nothing written there: the open air above the band's sea stays
                    Check(MirroringOn() && journal.Contains(bad) && At(bad).Id == 0, "an unknown journaled block is skipped and kept, mirroring stays on");
                    CheckEntityWrap(() => CheckOffThreadUnload(() => SetUpHopper(() =>
                    {
                        // On a build without the per-cell guard it switches mirroring off
                        CheckCaptureFailure();
                        // Last: it shuts the server down in the same tick
                        QueueAtShutdown();
                    })));
                });
            });
        }, 500);
    }

    /// <summary>A change reported off the main thread (as vanilla's chunk thread reports scheduled block updates) is
    /// captured before its column unloads, even when that happens before the next tick.</summary>
    void CheckOffThreadUnload(Action next)
    {
        var src = a.AddCopy(0, 0, 3200); var dst = b.AddCopy(0, 0, 3200);
        Load(src, () => Load(dst, () =>
        {
            var ba = api.World.BlockAccessor;
            int oak = Block("planks-oak-ud").Id;
            var chunk = ba.GetChunkAtBlockPos(src); chunk.Unpack();
            chunk.Data[((src.Y & 31) * 32 + (src.Z & 31)) * 32 + (src.X & 31)] = oak;
            Task.Run(() => ba.MarkBlockModified(src)).Wait();
            bool queued = At(dst).Id != oak;
            api.WorldManager.UnloadChunkColumn(src.X / 32, src.Z / 32);
            Check(queued && ba.GetChunkAtBlockPos(src) == null && At(dst).Id == oak,
                $"an off-thread change is captured before its column unloads (queued until then: {queued})");
            next();
        }));
    }

    /// <summary>First half of the shutdown drain test (CW-0009): a change reported off the main thread, both copies loaded,
    /// and the server stopped in the same tick, before SeamBlocks' tick could capture it. The 1.22 server disposes the mods
    /// before its shutdown save, so the drain has to happen in Dispose.</summary>
    void QueueAtShutdown()
    {
        var src = a.AddCopy(0, 0, 3840); var dst = b.AddCopy(0, 0, 3840);
        Load(src, () => Load(dst, () =>
        {
            var ba = api.World.BlockAccessor;
            int oak = Block("planks-oak-ud").Id;
            var chunk = ba.GetChunkAtBlockPos(src); chunk.Unpack();
            chunk.Data[((src.Y & 31) * 32 + (src.Z & 31)) * 32 + (src.X & 31)] = oak;
            chunk.MarkModified();
            Task.Run(() => ba.MarkBlockModified(src)).Wait();
            Check(At(src).Id == oak && At(dst).Id != oak, "an off-thread change waits for the next tick (the server stops in this one)");
            End(null, stopNow: true);
        }));
    }

    /// <summary>Second half: after the restart both copies hold the change queued at the shutdown.</summary>
    void CheckShutdownDrain(Action next)
    {
        var src = a.AddCopy(0, 0, 3840); var dst = b.AddCopy(0, 0, 3840);
        Load(src, () => Load(dst, () =>
        {
            int oak = Block("planks-oak-ud").Id;
            Check(At(src).Id == oak && At(dst).Id == oak,
                $"a change queued off the main thread in the shutdown tick reaches the other copy (source {At(src).Code}, other copy {At(dst).Code})");
            next();
        }));
    }

    /// <summary>First half of the hopper test: a hopper holding items in the identical part, on both copies, saved at
    /// shutdown (the reload breaks it at one copy while the other isn't loaded).</summary>
    void SetUpHopper(Action next)
    {
        var k = a.AddCopy(0, 0, 2560); var t = b.AddCopy(0, 0, 2560);
        Load(k, () => Load(t, () =>
        {
            var ba = api.World.BlockAccessor;
            // Something solid below it, or the hopper spits its items out into the air
            ba.SetBlock(Block("planks-oak-ud").Id, k.DownCopy());
            ba.SetBlock(Block("hopper").Id, k);
            if (ba.GetBlockEntity(k) is IBlockEntityContainer)
            {
                Inventory(k)[0].Itemstack = new ItemStack(Flint, 5);
                Inventory(k)[0].MarkDirty();
            }
            Check(ba.GetBlockEntity(t) is IBlockEntityContainer c && c.Inventory[0].StackSize == 5 && At(t.DownCopy()).Id == Block("planks-oak-ud").Id,
                "a hopper with items reaches the other copy");
            next();
        }));
    }

    /// <summary>Second half (after the restart, where every journal cell is pending on both copies): broken at one copy
    /// while the other isn't loaded, the hopper drops its items there; replaying the removal when the other copy loads
    /// must not drop them again (removing an item-flow block entity drops its contents).</summary>
    void CheckHopperReplay(Action next)
    {
        var k = a.AddCopy(0, 0, 2560); var t = b.AddCopy(0, 0, 2560);
        int Flints() => api.World.LoadedEntities.Values.OfType<EntityItem>().Where(e => e.Itemstack?.Collectible.Code.Path == "flint").Sum(e => e.Itemstack.StackSize);
        Check(api.World.BlockAccessor.GetChunkAtBlockPos(t) == null, "the hopper's other copy starts unloaded");
        Load(k, () =>
        {
            var ba = api.World.BlockAccessor;
            bool had = ba.GetBlockEntity(k) is IBlockEntityContainer c && c.Inventory[0].StackSize == 5;
            int before = Flints();
            ba.BreakBlock(k, null, 1);
            int dropped = Flints() - before;
            Load(t, () =>
            {
                int again = Flints() - before - dropped;
                Check(had && dropped == 5 && again == 0 && ba.GetBlockEntity(t) == null && At(t).Id == 0,
                    $"a hopper broken at one copy drops its items once, not again when the other copy loads (dropped {dropped}, again {again})");
                next();
            });
        });
    }

    /// <summary>A block entity whose state can't be read (a mod's, half initialised) costs its own cell, logged once,
    /// not mirroring as a whole.</summary>
    void CheckCaptureFailure()
    {
        var ba = api.World.BlockAccessor;
        var pos = a.AddCopy(0, 0, 4);
        ba.SetBlock(Block("planks-birch-ud").Id, pos);
        bool journaled = journal.Contains(pos);
        ThrowingEntity.Boom = true;
        try { ba.SpawnBlockEntity(ThrowingEntity.ClassName, pos); }
        finally { ThrowingEntity.Boom = false; }
        Check(journaled && MirroringOn() && !journal.Contains(pos), "a block entity that can't be read costs its own cell, mirroring stays on");
        ba.RemoveBlockEntity(pos);
        ba.SetBlock(0, pos);
    }

    /// <summary>An empty raft past the west jump line (no players aboard) is moved one circumference east by the entity
    /// tick once its destination has loaded, with its block-centred position kept.</summary>
    void CheckEntityWrap(Action next)
    {
        var start = new BlockPos(p.JumpInset - 4, api.World.SeaLevel, a.Z + 1920);
        Load(start, () =>
        {
            var raft = api.World.ClassRegistry.CreateEntity(api.World.GetEntityType(new AssetLocation("game:boat-raft-aged")));
            raft.Pos.SetPos(start.X + 0.5, start.Y, start.Z + 0.5);
            api.World.SpawnEntity(raft);
            long deadline = api.World.ElapsedMilliseconds + 15000;
            void Poll(float _)
            {
                double moved = raft.Pos.X - (start.X + 0.5);
                if (Math.Abs(moved - p.Circumference) < 4 || api.World.ElapsedMilliseconds > deadline)
                {
                    Check(Math.Abs(moved - p.Circumference) < 4 && Math.Abs(raft.Pos.Z - (start.Z + 0.5)) < 4,
                        $"an empty raft past the line wraps by one circumference (moved {moved:0.#} of {p.Circumference})");
                    next();
                    return;
                }
                api.Event.RegisterCallback(Poll, 250);
            }
            Poll(0);
        });
    }

    void CheckPackets()
    {
        var server = (ServerMain)api.World;
        tests = new Harmony("seam-edit-smoke-network");
        tests.Patch(AccessTools.Method(typeof(ServerMain), "SendPacket", new[] { typeof(IServerPlayer), typeof(Packet_Server) }),
            prefix: new HarmonyMethod(typeof(SeamEditSmoke), nameof(Packet)));
        var clients = new List<ConnectedClient>();
        foreach (int id in new[] { ClientA, ClientB })
        {
            var player = (ServerPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ServerPlayer));
            var client = (ConnectedClient)RuntimeHelpers.GetUninitializedObject(typeof(ConnectedClient));
            client.Id = id; client.Player = player; client.ChunkSent = new();
            var data = (ServerWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ServerWorldPlayerData));
            AccessTools.Field(typeof(ServerWorldPlayerData), "currentClientId").SetValue(data, id);
            AccessTools.Field(typeof(ServerPlayer), "worlddata").SetValue(player, data);
            AccessTools.Field(typeof(ServerPlayer), "client").SetValue(player, client);
            client.State = EnumClientState.Playing;
            foreach (var pos in new[] { a, b }) client.ChunkSent.Add(api.WorldManager.ChunkIndex3D(pos.X / 32, pos.Y / 32, pos.Z / 32));
            server.Clients[id] = client; clients.Add(client);
        }
        try
        {
            packets.Clear();
            api.World.BlockAccessor.ExchangeBlock(Block("planks-oak-ud").Id, a);
            foreach (int id in new[] { ClientA, ClientB })
                Check(packets.Any(x => x.client == id && x.x == a.X) && packets.Any(x => x.client == id && x.x == b.X),
                    "native updates reach both kept copies for client " + id);
            api.World.BlockAccessor.ExchangeBlock(Block("planks-birch-ud").Id, b);
            // A slot change alone sends the block entity, not two block packets for the other copy
            var chest = a.AddCopy(0, 1, 0); var chestTwin = b.AddCopy(0, 1, 0);
            if (api.World.BlockAccessor.GetBlockEntity(chest) is IBlockEntityContainer && api.World.BlockAccessor.GetBlockEntity(chestTwin) is IBlockEntityContainer)
            {
                packets.Clear();
                Inventory(chest)[5].Itemstack = new ItemStack(Block("planks-birch-ud"), 2);
                Inventory(chest)[5].MarkDirty();
                Check(Inventory(chestTwin)[5].StackSize == 2 && !packets.Any(x => x.y == chest.Y && x.z == chest.Z),
                    $"a container slot change reaches the other copy without block packets ({packets.Count} sent)");
                Inventory(chest)[5].Itemstack = null;
                Inventory(chest)[5].MarkDirty();
            }
        }
        finally
        {
            foreach (var client in clients) server.Clients.TryRemove(client.Id, out _);
            tests.UnpatchAll(tests.Id);
        }
    }
    static bool Packet(IServerPlayer player, Packet_Server packet)
    {
        if (player.ClientId != ClientA && player.ClientId != ClientB) return true;
        if (packet.SetBlock != null) packets.Add((player.ClientId, packet.Id, packet.SetBlock.X, packet.SetBlock.Y, packet.SetBlock.Z));
        if (packet.ExchangeBlock != null) packets.Add((player.ClientId, packet.Id, packet.ExchangeBlock.X, packet.ExchangeBlock.Y, packet.ExchangeBlock.Z));
        return false;
    }
    /// <param name="stopNow">Stop the server in this tick (from the main thread ShutDown stops it at once), else 250 ms later.</param>
    void End(string error, bool stopNow = false)
    {
        if (done) return; done = true;
        if (error != null) { failures++; Mod.Logger.Error("SEAMEDIT FAIL " + error); }
        Mod.Logger.Notification("SEAMEDIT RESULT {0} checks={1} failures={2} reload={3}", failures == 0 ? "PASS" : "FAIL", checks, failures, reload);
        if (stopNow) api.Server.ShutDown();
        else api.Event.RegisterCallback(_ => api.Server.ShutDown(), 250);
    }
}

/// <summary>A registered block entity whose state can't be read while Boom is set (only for one check: the game's own
/// packet and chunk saving read it later).</summary>
public class ThrowingEntity : BlockEntity
{
    public const string ClassName = "SeamEditSmokeThrowing";
    public static bool Boom;
    public override void ToTreeAttributes(ITreeAttribute tree)
    {
        if (Boom) throw new InvalidOperationException("seam edit smoke: this block entity can't be read");
        base.ToTreeAttributes(tree);
    }
}

/// <summary>A block entity type that is never registered.</summary>
public class StrayEntity : BlockEntity { }



