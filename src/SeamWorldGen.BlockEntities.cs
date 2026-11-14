using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using Vintagestory.ServerMods;

namespace CylinderWorld
{
    // SeamWorldGen, part: Block entities whose block a rewrite replaced. (CW-0019: moved unchanged from SeamWorldGen.cs.)
    public partial class SeamWorldGen
    {
        /// <summary>
        /// Remove block entities whose block was replaced: the block now at their position has no entity class (rock,
        /// gravel, water, air, ice). Surviving structure blocks, such as a chest the seam blend left in place, keep
        /// their contents. Checking the result rather than predicting which cells were rewritten keeps this correct
        /// for every rewrite rule, including ones added later.
        /// </summary>
        void RemoveOrphanedBlockEntities(IServerChunk[] chunks)
        {
            const int cs = GlobalConstants.ChunkSize;
            var blocks = sapi.World.Blocks;
            foreach (var chunk in chunks)
            {
                var bes = chunk.BlockEntities;
                if (bes == null || bes.Count == 0) continue;
                var positions = BlockEntityPositions(chunk, bes);
                if (positions == null) continue;
                foreach (var pos in positions)
                {
                    if (pos == null) continue;
                    int id = SolidId(chunk.Data, CellIndex(pos.Y, pos.Z % cs, pos.X % cs));
                    var block = id > 0 && id < blocks.Count ? blocks[id] : null;
                    if (!string.IsNullOrEmpty(block?.EntityClass)) continue;
                    // Vanilla's own path when worldgen replaces a block entity's block (BlockAccessorWorldGen.SetSolidBlock):
                    // the chunk's removal, under its lock, and the map chunk's saved list of new block entities, or a
                    // block entity placed there later would get OnBlockPlaced again on its next load
                    chunk.RemoveBlockEntity(pos);
                    ForgetNewBlockEntity(chunk.MapChunk, pos);
                }
            }
        }

        /// <summary>
        /// The positions of a chunk's block entities. A neighbour's structure on another worldgen thread can add one to this
        /// chunk meanwhile (BlockAccessorWorldGen writes to any chunk still generating), so they are copied under the
        /// chunk's own lock, which WorldChunk.AddBlockEntity and RemoveBlockEntity take: the writer waits for the copy.
        /// Without the lock (a game update renamed it) the copy is made once without it; if it met a write, null: this pass
        /// leaves the chunk's block entities as they are, and a later pass of the column, if any, looks again.
        /// </summary>
        BlockPos[] BlockEntityPositions(IServerChunk chunk, Dictionary<BlockPos, BlockEntity> bes)
        {
            if (ChunkLock(chunk) is object gate) lock (gate) return Copy(bes);
            try { return Copy(bes); }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IndexOutOfRangeException) { return null; }

            static BlockPos[] Copy(Dictionary<BlockPos, BlockEntity> bes)
            {
                var keys = new BlockPos[bes.Count];
                bes.Keys.CopyTo(keys, 0);
                return keys;
            }
        }

        /// <summary>ServerMapChunk.NewBlockEntities: public in VintagestoryLib but not in the API, so it is resolved through
        /// GameFeature; a game update without it only loses this part of the cleanup.</summary>
        GameFeature newBlockEntities;
        FieldInfo newBlockEntitiesField;
        /// <summary>WorldChunk.packUnpackLock: internal in VintagestoryLib, and the API has no locked way to list a chunk's
        /// block entities, so it is resolved through GameFeature; without it the copy is unlocked (BlockEntityPositions).</summary>
        GameFeature chunkLock;
        FieldInfo chunkLockField;

        void ResolveBlockEntityCleanup()
        {
            newBlockEntities = new GameFeature("removal of replaced worldgen block entities from the map chunk's list", null, Mod.Logger);
            newBlockEntitiesField = newBlockEntities.Field(newBlockEntities.Type("Vintagestory.Server.ServerMapChunk"), "NewBlockEntities", typeof(HashSet<BlockPos>));
            newBlockEntities.Apply();
            chunkLock = new GameFeature("the locked copy of a worldgen chunk's block entities (a copy that meets a neighbour's write skips that chunk's cleanup in that pass)", null, Mod.Logger);
            chunkLockField = chunkLock.Field(chunkLock.Type("Vintagestory.Common.WorldChunk"), "packUnpackLock", typeof(object));
            chunkLock.Apply();
        }

        /// <summary>The chunk's own lock object, null when the feature is off or the chunk isn't the game's WorldChunk.</summary>
        object ChunkLock(IServerChunk chunk)
        {
            var field = chunkLockField;
            if (field == null || chunkLock?.On != true || !field.DeclaringType.IsInstanceOfType(chunk)) return null;
            try { return field.GetValue(chunk); }
            catch (Exception e) { chunkLock.Fail(e); return null; }
        }

        void ForgetNewBlockEntity(IMapChunk mapChunk, BlockPos pos)
        {
            var field = newBlockEntitiesField;
            if (field == null || newBlockEntities?.On != true || !field.DeclaringType.IsInstanceOfType(mapChunk)) return;
            try { (field.GetValue(mapChunk) as HashSet<BlockPos>)?.Remove(pos); }
            catch (Exception e) { newBlockEntities.Fail(e); }
        }
    }
}
