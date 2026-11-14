namespace CylinderWorld
{
    /// <summary>A chunk column's x and z packed into one long, the way SeamPreload and SeaIceSystem key their columns
    /// (CW-0021: one packing instead of two).</summary>
    internal static class ColumnKey
    {
        internal static long Of(int cx, int cz) => ((long)cx << 32) | (uint)cz;
        internal static int X(long key) => (int)(key >> 32);
        internal static int Z(long key) => (int)(uint)key;
    }
}
