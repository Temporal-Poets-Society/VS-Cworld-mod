using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Vintagestory.ServerMods;

namespace CylinderWorld
{
    /// <summary>
    /// Renders the whole world as a PNG without generating any chunks: land/sea from a private copy of vanilla's ocean
    /// map generator (same seed and settings, plus our seam ocean), coloured by our temperature curve, with the walls,
    /// latitude lines, equator, jump lines, spawn and story locations drawn on top. Terrain height, lakes and rivers are
    /// not included (they come later in worldgen), so coastlines are approximate. Earth worlds show Earth's own sea, land
    /// and ice instead (one sample of Earth's terrain model per pixel).
    /// </summary>
    public static class WorldPreview
    {
        public class Result
        {
            public string Path;
            public int Width, Height, BlocksPerPixel;
            public Dictionary<string, double> LandShareByZone = new();
        }

        /// <param name="ocean">A private generator instance (not vanilla's: worldgen threads use that one).</param>
        /// <param name="oceanPixelBlocks">Blocks per ocean-map pixel.</param>
        public static Result Render(SeamParams p, MapLayerBase ocean, int oceanPixelBlocks, IList<(int x, int z, bool spawn)> markers, string path, int maxWidth = 2048)
        {
            // Render the ring only (jump line to jump line): anything beyond is never visited
            int x0 = p.JumpInset, ringWidth = (int)p.Circumference, L = p.WorldLength;
            int k = Math.Max(1, (int)Math.Ceiling(ringWidth / (double)(maxWidth * oceanPixelBlocks)));
            int step = k * oceanPixelBlocks;
            int w = ringWidth / step, h = L / step;
            int oceanRowStart = x0 / oceanPixelBlocks, oceanRowWidth = ringWidth / oceanPixelBlocks + 1;

            var rgb = new byte[w * h * 3];
            // one date for the whole background render
            var season = p.CurrentSeaIceDate;
            var zoneLand = new Dictionary<string, (long land, long total)>();
            // Earth worlds draw Earth's own land, sea and ice, one sample per pixel; vanilla's ocean map, our ocean force and
            // our ice cover aren't used there
            bool earth = p.EarthPolarIce && p.EarthPolar != null;

            for (int r = 0; r < h; r++)
            {
                double z = r * step + step / 2.0;
                int[] row = earth ? null : ocean.GenLayer(oceanRowStart, r * step / oceanPixelBlocks, oceanRowWidth, 1);
                float temp = p.TemperatureAt(z);
                string zone = ZoneName(temp);

                for (int c = 0; c < w; c++)
                {
                    double x = x0 + c * step + step / 2.0;
                    double dz = p.DistToZEdge(z);
                    bool sea, wall, packIce;
                    if (earth)
                    {
                        var sample = p.EarthPolar.Sample(x, z);
                        // the sea only (Water 1): lakes and rivers aren't drawn, as in the other worlds
                        sea = sample.Ocean;
                        bool frozen = p.SeasonalSeaIce
                            ? SeaIcePolicy.At(p, x, z, sample, season.Year, season.FixedSeason) != SeaIceKind.Open
                            : p.HasArcticIce(x, z, sample);
                        wall = dz < p.WallFaceMax || sample.Glacier || frozen;
                        packIce = false;
                    }
                    else
                    {
                        int o = Math.Max(row[Math.Min(row.Length - 1, (int)(x / oceanPixelBlocks) - oceanRowStart)], p.OceanForce(x, z));
                        sea = o >= 128;
                        wall = p.IsIceCovered(x, z);   // wall + glacier / ice shelf
                        packIce = p.HasWalls && !wall && dz < p.PolarZoneDepth;
                    }

                    var (cr, cg, cb) = wall ? ((byte)246, (byte)249, (byte)252)
                        : sea && packIce ? ((byte)205, (byte)220, (byte)235)
                        : sea ? SeaColor(temp) : LandColor(temp);
                    Set(rgb, w, c, r, cr, cg, cb);

                    if (!wall && !p.InSeamBand(x))
                    {
                        zoneLand.TryGetValue(zone, out var t);
                        zoneLand[zone] = (t.land + (sea ? 0 : 1), t.total + 1);
                    }
                }
            }

            DrawOverlays(p, rgb, w, h, step, x0, markers);
            WritePng(path, rgb, w, h);

            var res = new Result { Path = path, Width = w, Height = h, BlocksPerPixel = step };
            foreach (var (zone, t) in zoneLand) res.LandShareByZone[zone] = t.total > 0 ? (double)t.land / t.total : 0;
            return res;
        }

        public static string ZoneName(float t) => t < -10 ? "arctic" : t < 5 ? "cold" : t < 20 ? "temperate" : "tropical";

        /// <summary>Deep blue, fading to pale icy blue between -2 and -15°C.</summary>
        static (byte, byte, byte) SeaColor(float t)
        {
            float f = Math.Clamp((-2 - t) / 13f, 0, 1);
            return ((byte)(38 + (150 - 38) * f), (byte)(84 + (175 - 84) * f), (byte)(150 + (205 - 150) * f));
        }

        /// <summary>Land tinted by temperature: ice → tundra → boreal → temperate green → warm olive → hot sand.</summary>
        static readonly (float t, int r, int g, int b)[] stops =
        {
            (-20, 236, 238, 240), (-10, 190, 194, 196), (0, 104, 132, 96), (10, 86, 150, 66), (20, 150, 168, 70), (28, 206, 176, 102)
        };

        static (byte, byte, byte) LandColor(float t)
        {
            if (t <= stops[0].t) return ((byte)stops[0].r, (byte)stops[0].g, (byte)stops[0].b);
            for (int i = 1; i < stops.Length; i++)
            {
                if (t <= stops[i].t)
                {
                    float f = (t - stops[i - 1].t) / (stops[i].t - stops[i - 1].t);
                    return ((byte)(stops[i - 1].r + (stops[i].r - stops[i - 1].r) * f),
                            (byte)(stops[i - 1].g + (stops[i].g - stops[i - 1].g) * f),
                            (byte)(stops[i - 1].b + (stops[i].b - stops[i - 1].b) * f));
                }
            }
            var last = stops[^1];
            return ((byte)last.r, (byte)last.g, (byte)last.b);
        }

        static void DrawOverlays(SeamParams p, byte[] rgb, int w, int h, int step, int x0, IList<(int x, int z, bool spawn)> markers)
        {
            // Latitude lines every 15°, equator in red
            if (p.IsPlanetLayout && p.PoleDistance > 0)
            {
                for (int deg = -75; deg <= 75; deg += 15)
                {
                    int r = (int)((p.EquatorZ - deg / 90.0 * p.PoleDistance) / step);
                    if (r < 0 || r >= h) continue;
                    for (int c = 0; c < w; c++)
                    {
                        if (deg == 0) Set(rgb, w, c, r, 210, 50, 50);
                        else if (c % 4 < 2) Blend(rgb, w, c, r, 255, 255, 255, 0.55f);
                    }
                }
            }

            // The image spans the ring from jump line to jump line: mark its left/right edges as the seam
            for (int r = 0; r < h; r++)
            {
                if (r % 6 >= 4) continue;
                Set(rgb, w, 0, r, 10, 30, 110);
                Set(rgb, w, w - 1, r, 10, 30, 110);
            }

            // Map center cross
            int mc = (p.WorldWidth / 2 - x0) / step, mr = h / 2;
            for (int d = -4; d <= 4; d++) { Set(rgb, w, mc + d, mr, 0, 0, 0); Set(rgb, w, mc, mr + d, 0, 0, 0); }

            // Story locations (purple), spawn (red, white ring) on top. Markers off the ring (beyond a jump line) are left out.
            int ring = (int)p.Circumference;
            foreach (var m in markers)
            {
                if (!m.spawn && m.x >= x0 && m.x < x0 + ring) Disk(rgb, w, h, (m.x - x0) / step, m.z / step, 3, 140, 60, 200, 255, 255, 255);
            }
            foreach (var m in markers)
            {
                if (m.spawn && m.x >= x0 && m.x < x0 + ring) Disk(rgb, w, h, (m.x - x0) / step, m.z / step, 5, 225, 40, 40, 255, 255, 255);
            }
        }

        static void Disk(byte[] rgb, int w, int h, int cx, int cz, int radius, byte r, byte g, byte b, byte or, byte og, byte ob)
        {
            for (int dz = -radius - 1; dz <= radius + 1; dz++)
            {
                for (int dx = -radius - 1; dx <= radius + 1; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= w || z >= h) continue;
                    double d = Math.Sqrt(dx * dx + dz * dz);
                    if (d <= radius) Set(rgb, w, x, z, r, g, b);
                    else if (d <= radius + 1.2) Set(rgb, w, x, z, or, og, ob);
                }
            }
        }

        static void Set(byte[] rgb, int w, int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= w || (y * w + x) * 3 + 2 >= rgb.Length) return;
            int i = (y * w + x) * 3;
            rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
        }

        static void Blend(byte[] rgb, int w, int x, int y, byte r, byte g, byte b, float a)
        {
            int i = (y * w + x) * 3;
            rgb[i] = (byte)(rgb[i] * (1 - a) + r * a);
            rgb[i + 1] = (byte)(rgb[i + 1] * (1 - a) + g * a);
            rgb[i + 2] = (byte)(rgb[i + 2] * (1 - a) + b * a);
        }

        // ---------------------------------------------------------------- minimal PNG writer (8-bit RGB)

        public static void WritePng(string path, byte[] rgb, int w, int h)
        {
            using var fs = File.Create(path);
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

            var ihdr = new byte[13];
            WriteBE(ihdr, 0, w); WriteBE(ihdr, 4, h);
            ihdr[8] = 8; ihdr[9] = 2;  // bit depth 8, colour type RGB
            WriteChunk(fs, "IHDR", ihdr);

            using (var raw = new MemoryStream())
            {
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
                {
                    for (int y = 0; y < h; y++)
                    {
                        z.WriteByte(0);  // filter: none
                        z.Write(rgb, y * w * 3, w * 3);
                    }
                }
                WriteChunk(fs, "IDAT", raw.ToArray());
            }
            WriteChunk(fs, "IEND", Array.Empty<byte>());
        }

        static void WriteChunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, data.Length);
            s.Write(len);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            s.Write(typeBytes);
            s.Write(data);
            uint crc = Crc32(typeBytes, 0xFFFFFFFF);
            crc = Crc32(data, crc) ^ 0xFFFFFFFF;
            var c = new byte[4]; WriteBE(c, 0, (int)crc);
            s.Write(c);
        }

        static void WriteBE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        static uint[] crcTable;
        static uint Crc32(byte[] data, uint crc)
        {
            if (crcTable == null)
            {
                var t = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int i = 0; i < 8; i++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    t[n] = c;
                }
                crcTable = t;
            }
            foreach (byte b in data) crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
