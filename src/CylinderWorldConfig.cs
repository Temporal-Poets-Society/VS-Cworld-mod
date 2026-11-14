namespace CylinderWorld
{
    /// <summary>
    /// Server config, stored as ModConfig/cylinderworld.json. The values that shape terrain (the geometry: JumpInset,
    /// SeamHalfWidth, CoastFalloff, SeabedDepth, the walls and the climate; and FlattenSeabed and ExtendTemperatureRange
    /// since 1.1) are frozen into the savegame the first time a world loads with the mod, so editing them later only
    /// affects new worlds. Worlds saved before 1.1 keep reading FlattenSeabed and ExtendTemperatureRange from here.
    /// </summary>
    public class CylinderWorldConfig
    {
        public bool Enabled = true;

        /// <summary>Blocks from world edge to jump line. 0 = auto (server view radius + 32, or see EarthProportions).</summary>
        public int JumpInset = 0;
        /// <summary>
        /// With ice walls and a map at least twice as wide as long (e.g. 256k × 102.4k), move the jump lines inward so the
        /// ring is exactly 2 × the world length, like Earth (pole to pole = half the circumference). The strips beyond the
        /// jump lines are seam ocean that is never visited, so they're never generated.
        /// </summary>
        public bool EarthProportions = true;
        /// <summary>Half-width of the ocean band at each edge, including its 160-block blend. 0 = auto (JumpInset + view radius + 64,
        /// then the wavy edge's 64 and the 160-block blend).</summary>
        public int SeamHalfWidth = 0;
        public int CoastFalloff = 1024;
        public int SeabedDepth = 40;
        /// <summary>Build the band's identical seabed and the jump curtain. New worlds only, saved with the world: off, both
        /// sides of the jump keep their natural terrain and block edits there are not mirrored.</summary>
        public bool FlattenSeabed = true;

        /// <summary>No placing/breaking blocks inside the band (creative mode exempt).</summary>
        public bool ProtectSeamBand = true;
        /// <summary>Also wrap drifting boats, items, animals etc.</summary>
        public bool WrapOtherEntities = true;

        /// <summary>When a player gets this close to a jump line, start loading the destination.</summary>
        public int PrepareDistance = 96;
        /// <summary>Faster travellers start earlier: this many seconds before they reach the line at their current speed
        /// (at most 512 blocks out).</summary>
        public float PrepareSeconds = 12f;
        /// <summary>Also push those destination chunks (and their map regions first) to the client early, so the other
        /// side is already there at the jump.</summary>
        public bool PreSendChunks = true;
        /// <summary>Chunk radius loaded and sent around the point where the player will come out.</summary>
        public int PreSendDiscRadius = 6;
        /// <summary>Send at least the player's whole view radius around that point instead (as far as the seam mist lets
        /// anything be seen), so nothing arrives after the jump.</summary>
        public bool PreSendFullView = true;
        /// <summary>The other side is kept on the client while the player is near the line (both sides, so crossing back
        /// and forth reloads nothing). Leaving the zone, it stays this long, then is unloaded gradually over
        /// UnloadOldSideSeconds instead of all at once (which stalls the client for a frame). 0 = vanilla, all at once.</summary>
        public float UnloadOldSideDelay = 1.5f;
        public float UnloadOldSideSeconds = 6f;
        /// <summary>Chunks further ahead along the player's heading, in a strip 7 chunks wide.</summary>
        public int PreSendAhead = 8;
        /// <summary>
        /// Seconds after a player's jump before they can jump again (0 = off). A jump makes the client drop its whole view
        /// and load the other side; this lets it finish before the next one. A player who turns straight back waits in the
        /// open sea just past the line (identical on both sides) until then, at most 32 blocks past it.
        /// </summary>
        public float JumpCooldown = 3f;

        /// <summary>Keep the weather identical in the regions on both sides of the seam (rings that are a whole number of regions).</summary>
        public bool SyncWeatherAcrossSeam = true;

        /// <summary>The seam mist and the jump fog pulse in worlds without the world option cylinderSeamMist (made before it,
        /// or on a server without the world creation screen); false = no mist and no pulse, and the destination is pre-sent
        /// to the whole view radius. A world's own option wins (CW-D48).</summary>
        public bool SeamMist = true;
        public float MistDensity = 0.012f;
        public float MistMin = 0.2f;
        public int MistRange = 200;
        /// <summary>Right after a jump the mist briefly thickens to this fog minimum (0 = off) and eases back over
        /// JumpFogSeconds, hiding whatever is still arriving.</summary>
        public float JumpFogMin = 0.6f;
        public float JumpFogSeconds = 1.5f;

        /// <summary>Unbreakable ice cliffs at the north and south (Z) world edges.</summary>
        public bool PolarWalls = true;
        /// <summary>Wall thickness from the world edge to its face.</summary>
        public int WallDepth = 48;
        /// <summary>Y of the wall top. 0 = auto (ice plateau + WallAbovePlateau, capped just below the build limit).</summary>
        public int WallTopY = 0;
        /// <summary>How far the Wall rises above the ice plateau in front of it.</summary>
        public int WallAbovePlateau = 48;
        /// <summary>No building this many blocks in front of the wall (so nobody pillars over it). Creative exempt.</summary>
        public int WallBuildMargin = 32;
        /// <summary>Survival players who get on top of or behind the wall are put back in front of it.</summary>
        public bool WallGuard = true;

        /// <summary>The Antarctic-style ice sheet covers everything from this latitude to the wall (land and sea alike).
        /// 85° keeps it clear of all land on an Earth map (the northernmost land is ~83.6°N).</summary>
        public float IceSheetLatitude = 85f;
        /// <summary>New worlds only, saved with the world. "default" is automatic: an Earth world gets Earth's polar layout
        /// (Earth's Antarctica, low Arctic sea ice, Earth's land across the seam), any other world our two ice plateaus.
        /// "earth" is the same, but warns when the new world isn't an Earth world. "plateaus" always uses our plateaus.</summary>
        public string PolarIceMode = "default";
        /// <summary>Seasonal polar ocean ice in Earth-mode worlds; the Wall approach remains permanent. Read at every start.
        /// Switched off in a world that had it, the ice stays where the last season left it (nothing thaws it) and new
        /// chunks get solid floes.</summary>
        public bool SeasonalSeaIce = true;
        /// <summary>Height of the ice plateau above sea level near the wall (it drops steeply only near its edge).</summary>
        public int PlateauHeight = 70;
        /// <summary>Trees fade out from this latitude to the ice sheet (the tundra belt, where polar bears live).</summary>
        public float TundraLatitude = 76f;
        /// <summary>Sea ice floes on open water beyond the ice front.</summary>
        public int PackIceDepth = 256;
        /// <summary>The continental shelf (seabed rising toward the wall) continues this far beyond the ice front.</summary>
        public int ShelfExtra = 320;
        // (BarrenDepth, the v4-v7 forest fade, was removed in 1.1: new worlds are v10 and older worlds keep their saved
        // value, so nothing read it. An old file that still has the key loads as before; the key is dropped on rewrite.)

        /// <summary>Replace vanilla temperature with an Earth-like curve: PoleTemperature at the walls, EquatorTemperature at the map center.</summary>
        public bool PolarClimate = true;
        public float PoleTemperature = -30f;
        /// <summary>°C at the equator (map center). null = 30.</summary>
        public float? EquatorTemperature = 30f;
        /// <summary>Temperature ∝ cos(latitude)^this. 1.25 matches Earth's yearly averages; higher = colder mid-latitudes.</summary>
        public float ClimateCurveExponent = 1.25f;

        /// <summary>Move spawn (and the story locations) to the latitude matching the world's starting climate. New worlds only.</summary>
        public bool MoveSpawn = true;
        /// <summary>"north" or "south".</summary>
        public string SpawnHemisphere = "north";
        /// <summary>Lower the world's story distance scaling if the story chain wouldn't fit between spawn and the walls.</summary>
        public bool FitStoryToWorld = true;

        /// <summary>Let live temperatures go below vanilla's -20°C map floor where the climate curve and altitude say so.</summary>
        public bool ExtendTemperatureRange = true;
        /// <summary>Yearly average on the ice plateau at the wall. With vanilla's seasons: about -8°C in summer, -72°C in winter.</summary>
        public float WallMeanTemperature = -40f;

        /// <summary>Winter rule on the ice sheet: below ColdStart (live °C) a cold exposure meter fills, fastest at ColdFull and
        /// colder, and faster toward the wall. At the wall in deep winter it's full after FreezeMinutes (real minutes), then frost
        /// damage kills. A nearby fire only slows it (FireFactor). Summer on the ice stays plain vanilla.</summary>
        public bool IceSheetWinterRule = true;
        public float ColdStart = -40f;
        public float ColdFull = -65f;
        public float FreezeMinutes = 4f;
        public float FireFactor = 0.5f;
        /// <summary>HP per second once the meter is full.</summary>
        public float FrostDamagePerSecond = 0.5f;

        /// <summary><see cref="PolarIceMode"/> as one of its three values (case doesn't matter); null for anything else.</summary>
        internal static PolarIce? ParsePolarIce(string mode) =>
            string.Equals(mode, "default", System.StringComparison.OrdinalIgnoreCase) ? PolarIce.Automatic
            : string.Equals(mode, "earth", System.StringComparison.OrdinalIgnoreCase) ? PolarIce.Earth
            : string.Equals(mode, "plateaus", System.StringComparison.OrdinalIgnoreCase) ? PolarIce.Plateaus
            : null;
    }

    /// <summary>The polar layout a new world asks for (CW-D37): "default" = Earth's in an Earth world, the plateaus elsewhere;
    /// "earth" = the same, with a warning when the world isn't an Earth world; "plateaus" = always our plateaus.</summary>
    internal enum PolarIce { Automatic, Earth, Plateaus }
}
