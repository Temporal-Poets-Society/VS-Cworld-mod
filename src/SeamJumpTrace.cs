using System;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace CylinderWorld
{
    /// <summary>
    /// Measures each seam jump on the client and writes one line to the client log, to find what still shows: how long
    /// after crossing the line the server's teleport arrived, whether the mist pulse ran, and for TraceSeconds after the
    /// jump the worst frames and, per second, the chunks received, unloaded and waiting to be meshed.
    /// </summary>
    class SeamJumpTrace
    {
        const int TraceSeconds = 5;

        readonly ICoreClientAPI capi;
        readonly ILogger logger;
        /// <summary>Chunks on the client (SeamJumpClient's typed access; -1 when that is off).</summary>
        readonly System.Func<int> chunks;
        readonly FieldInfo received, unloaded, awaitingMesh;

        long crossedMs = long.MinValue;
        bool active;
        long startMs, worstAt;
        double latency, overshoot, jumpFrameMs, worstMs;
        int missing;
        int frames, over50, over100, lastReceived, lastUnloaded, chunks0, second;
        readonly int[] receivedPerSecond = new int[TraceSeconds], unloadedPerSecond = new int[TraceSeconds], meshAt = new int[TraceSeconds + 1];

        public SeamJumpTrace(ICoreClientAPI capi, ILogger logger, System.Func<int> chunks)
        {
            this.capi = capi;
            this.logger = logger;
            this.chunks = chunks;
            // Through the gateway like every other reflected game member (CW-D39, CW-0015). A game version without them
            // switches only these statistics off (one line); the trace then reads -1 for all three.
            var f = new GameFeature("the seam jump trace's chunk statistics", null, logger);
            var stats = f.Type("Vintagestory.Client.RuntimeStats");
            received = stats == null ? null : f.Field(stats, "chunksReceived", typeof(int));
            unloaded = stats == null ? null : f.Field(stats, "chunksUnloaded", typeof(int));
            awaitingMesh = stats == null ? null : f.Field(stats, "chunksAwaitingTesselation", typeof(int));
            if (!f.Apply()) received = unloaded = awaitingMesh = null;
        }

        /// <summary>A diagnostic: a statistic this game version doesn't have reads -1.</summary>
        static int Stat(FieldInfo f) => f?.GetValue(null) is int v ? v : -1;

        int Chunks()
        {
            try { return chunks(); }
            catch (Exception) { return -1; }
        }

        /// <summary>Every frame, before the jump check: notes when the player crossed the line, and traces after a jump.</summary>
        public void Frame(SeamParams p, double x, float dt)
        {
            long now = capi.ElapsedMilliseconds;
            if (!p.PastJumpLine(x)) crossedMs = long.MinValue;
            else if (crossedMs == long.MinValue) crossedMs = now;

            if (!active) return;
            double ms = dt * 1000;
            frames++;
            if (ms > 50) over50++;
            if (ms > 100) over100++;
            if (ms > worstMs) { worstMs = ms; worstAt = now - startMs; }
            while (second < TraceSeconds && now - startMs >= (second + 1) * 1000L)
            {
                // RuntimeStats counts are totals since the world was joined (the client resets them then)
                int r = Stat(received), u = Stat(unloaded);
                receivedPerSecond[second] = r - lastReceived;
                unloadedPerSecond[second] = u - lastUnloaded;
                lastReceived = r;
                lastUnloaded = u;
                meshAt[++second] = Stat(awaitingMesh);
            }
            if (second >= TraceSeconds) Finish();
        }

        /// <summary>The frame the teleport arrived in.</summary>
        /// <param name="missingColumns">Arrival columns the client didn't have (the mist pulse ran if any).</param>
        public void Jump(SeamParams p, double fromX, int missingColumns, float dt)
        {
            long now = capi.ElapsedMilliseconds;
            if (active) Finish();
            latency = crossedMs == long.MinValue ? -1 : now - crossedMs;
            overshoot = p.PastJumpLine(fromX) ? p.DistToJumpLine(fromX) : 0;
            crossedMs = long.MinValue;
            missing = missingColumns;
            jumpFrameMs = dt * 1000;
            active = true;
            startMs = now;
            worstMs = 0;
            worstAt = 0;
            frames = over50 = over100 = second = 0;
            lastReceived = Stat(received);
            lastUnloaded = Stat(unloaded);
            chunks0 = Chunks();
            Array.Clear(receivedPerSecond);
            Array.Clear(unloadedPerSecond);
            meshAt[0] = Stat(awaitingMesh);
        }

        void Finish()
        {
            active = false;
            logger.Notification("Seam jump trace: teleport {0} ms after crossing the line ({1:0.0} blocks past), arrival columns missing {2} (mist pulse if any); " +
                "jump frame {3:0} ms, then worst {4:0} ms at +{5} ms, {6} of {7} frames over 50 ms ({8} over 100 ms); " +
                "per second: chunks received {9}, unloaded {10}, waiting to be meshed {11}; client chunks {12} -> {13}",
                latency, overshoot, missing, jumpFrameMs, worstMs, worstAt, over50, frames, over100,
                string.Join("/", receivedPerSecond), string.Join("/", unloadedPerSecond), string.Join("/", meshAt),
                chunks0, Chunks());
        }
    }
}
