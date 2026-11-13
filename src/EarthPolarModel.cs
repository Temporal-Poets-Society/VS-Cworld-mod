using System;
using System.Linq.Expressions;

namespace CylinderWorld
{
    /// <summary>
    /// Optional adapter to Earth's actual terrain model, including its Pacific shear and inserted seam sea.
    /// Reflection is used once to compile a delegate; no Earth DLL dependency, data copy or per-block reflection.
    /// The supplied model and Earth data are read-only and safe to sample from worldgen threads.
    /// </summary>
    public sealed class EarthPolarModel
    {
        public const int LifelessBorder = 1000;
        /// <summary>|latitude| from which the sampler reads Earth's climate; elsewhere the column's temperatures are 0 (so
        /// anything that reads them, like the seam band's land surface, must use this same threshold).</summary>
        public const double ClimateSampleLatitude = 55;
        /// <summary>Arctic sea ice and pressure ridges start here (°N).</summary>
        public const double ArcticIceLatitude = 66;
        /// <summary>Antarctica: kept land in the seam band, no Arctic predators, hot springs or traders south of this (°).</summary>
        public const double AntarcticLatitude = -60;
        /// <summary>Seasonal sea ice reaches this far north in the south (°).</summary>
        public const double SeasonalSouthLatitude = -55;
        readonly Func<double, double, Column> sample;
        readonly double equatorZ, blocksPerDegreeZ;
        /// <summary>Earth fills its seam gutter with the real seabed, the same one ring apart (Earth's TerrainModel.GutterSeabed
        /// ≥ 1; missing in older Earth Map versions), or has no gutter at all (RealSeam).</summary>
        public readonly bool RealGutterSeabed;
        /// <summary>Earth carries the seam meridian's land across its gutter as well (TerrainModel.GutterSeabed ≥ 2, CW-D31),
        /// or has no gutter at all (RealSeam).</summary>
        public readonly bool GutterLand;
        /// <summary>
        /// Earth has no gutter: its real terrain, lakes and rivers included, runs across the jump line (Earth's
        /// TerrainModel.RealSeam, decision 0030; GutterSeabed 3). Read from Earth's own member rather than from a
        /// GutterSeabed threshold; false for Earth versions without it.
        /// </summary>
        public readonly bool RealSeam;
        /// <summary>
        /// Earth's column has WaterTop (Earth 1.0 and 2.0 do). Bound as optional, as only the seam band's lakes and rivers
        /// read it, but a world whose saved layout keeps them (SeamParams.EarthSeamWater) can't generate without it: its new
        /// band chunks would get dry beds beside the water of earlier chunks and of the other lap. So a new world takes
        /// EarthSeamWater only with it, and an existing one is refused with <see cref="WaterTopMismatch"/>.
        /// </summary>
        public readonly bool HasWaterTop;
        /// <summary>The binder's refusal for a missing or changed WaterTop, in a world with SeamParams.EarthSeamWater (null
        /// when <see cref="HasWaterTop"/>).</summary>
        public readonly string WaterTopMismatch;

        public struct Column
        {
            public int Top, IceBottom;
            /// <summary>Top water block of Earth's sea, lake or river (-1 for none, also from an Earth without it).</summary>
            public int WaterTop;
            /// <summary>0 none, 1 sea, 2 lake or river (fresh), 3 salt lake.</summary>
            public byte Water;
            public double Latitude, MeanTemperature, WarmestMonth, ColdestMonth;
            public bool Ocean => Water == 1;
            public bool Glacier => IceBottom <= Top;
        }

        /// <summary>
        /// Binds Earth's terrain model. Every member is resolved up front, with its type; if Earth changed any of them this
        /// refuses with one message naming Earth's version and everything missing, instead of a NullReference or cast error
        /// from the first one. The world cannot fall back: its saved layout is Earth's (CW-D11).
        /// </summary>
        public EarthPolarModel(object model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var type = model.GetType();
            var bind = new EarthBinder(type);
            var geometryField = bind.Field(type, "Geometry");
            var dataField = bind.Field(type, "Data");
            object geometry = geometryField?.GetValue(model);
            object data = dataField?.GetValue(model);
            var geometryType = geometry?.GetType() ?? geometryField?.FieldType;
            var dataType = data?.GetType() ?? dataField?.FieldType;
            var equatorField = bind.Field(geometryType, "EquatorZ", typeof(double));
            var perDegreeField = bind.Field(geometryType, "BlocksPerDegreeZ", typeof(double));
            var hasTerrain = bind.Property(dataType, "HasTerrain", typeof(bool));
            var getColumn = bind.Method(type, "GetColumn");
            var climateMethod = bind.Method(dataType, "SampleClimate");
            var columnType = getColumn?.ReturnType;
            var climateType = climateMethod?.ReturnType;
            var top = bind.Field(columnType, "Top", typeof(int));
            var iceBottom = bind.Field(columnType, "IceBottom", typeof(int));
            var water = bind.Field(columnType, "Water", typeof(byte));
            var lat = bind.Field(columnType, "Lat", typeof(double));
            var lon = bind.Field(columnType, "Lon", typeof(double));
            var mean = bind.Field(climateType, "TMean", typeof(double));
            var cold = bind.Field(climateType, "TCold", typeof(double));
            var warm = bind.Field(climateType, "TWarm", typeof(double));
            bind.ThrowIfMissing();
            // Optional (Earth 1.0 and 2.0 have it): only the band's lakes and rivers read it, in EarthSeamWater worlds
            var optional = new EarthBinder(type);
            var waterTopField = optional.Field(columnType, "WaterTop", typeof(int));
            HasWaterTop = waterTopField != null;
            WaterTopMismatch = optional.Mismatch(" for a world whose seam band keeps Earth's lakes and rivers");

            // From here on not version mismatches but Earth's state (its model isn't built or loaded): each message says which
            if (geometry == null) throw new InvalidOperationException("Earth's terrain model has no world geometry yet (" + type.Name
                + ".Geometry is null), so this world's Earth polar layout can't be generated.");
            equatorZ = (double)equatorField.GetValue(geometry);
            blocksPerDegreeZ = (double)perDegreeField.GetValue(geometry);
            if (!double.IsFinite(blocksPerDegreeZ) || blocksPerDegreeZ <= 0)
                throw new InvalidOperationException("Earth's world geometry is invalid (" + geometryType.Name + ".BlocksPerDegreeZ is " + blocksPerDegreeZ + ").");
            // Optional: older Earth versions have no gutter seabed, and only newer ones say when there is no gutter
            int gutter = type.GetField("GutterSeabed")?.GetValue(model) is int gutterSeabed ? gutterSeabed : 0;
            RealSeam = type.GetProperty("RealSeam")?.GetValue(model) is bool realSeam && realSeam;
            RealGutterSeabed = gutter >= 1 || RealSeam;
            GutterLand = gutter >= 2 || RealSeam;
            if (data == null || !(bool)hasTerrain.GetValue(data))
                throw new InvalidOperationException("Earth's server terrain data isn't loaded (" + (data == null ? type.Name + ".Data is null" : dataType.Name + ".HasTerrain is false")
                    + "), so this world's Earth polar layout can't be generated.");

            var x = Expression.Parameter(typeof(double), "x");
            var z = Expression.Parameter(typeof(double), "z");
            var col = Expression.Variable(columnType, "column");
            var climate = Expression.Variable(climateType, "climate");
            MemberAssignment Bind(string target, Expression value) => Expression.Bind(typeof(Column).GetField(target), value);
            sample = Expression.Lambda<Func<double, double, Column>>(
                Expression.Block(new[] { col, climate },
                    Expression.Assign(col, Expression.Call(Expression.Constant(model), getColumn, x, z)),
                    Expression.Assign(climate, Expression.Condition(
                        Expression.GreaterThanOrEqual(Expression.Call(typeof(Math).GetMethod(nameof(Math.Abs), new[] { typeof(double) }), Expression.Field(col, lat)), Expression.Constant(ClimateSampleLatitude)),
                        Expression.Call(Expression.Constant(data), climateMethod, Expression.Field(col, lat), Expression.Field(col, lon)),
                        Expression.Default(climateType))),
                    Expression.MemberInit(Expression.New(typeof(Column)),
                        Bind(nameof(Column.Top), Expression.Field(col, top)),
                        Bind(nameof(Column.IceBottom), Expression.Field(col, iceBottom)),
                        Bind(nameof(Column.WaterTop), waterTopField != null ? Expression.Field(col, waterTopField) : Expression.Constant(-1)),
                        Bind(nameof(Column.Water), Expression.Field(col, water)),
                        Bind(nameof(Column.Latitude), Expression.Field(col, lat)),
                        Bind(nameof(Column.MeanTemperature), Expression.Field(climate, mean)),
                        Bind(nameof(Column.ColdestMonth), Expression.Field(climate, cold)),
                        Bind(nameof(Column.WarmestMonth), Expression.Field(climate, warm)))), x, z).Compile();
        }

        /// <summary>
        /// Resolves Earth's members (public instance members, with their types) and collects whatever is missing. A member
        /// of a type that is itself missing (the fields of GetColumn's result when GetColumn is gone) isn't listed again:
        /// its owner already is. The message is complete for players and admins, so it is shown as it is.
        /// </summary>
        sealed class EarthBinder
        {
            readonly Type earth;
            readonly System.Collections.Generic.List<string> missing = new();

            public EarthBinder(Type earth) => this.earth = earth;

            public System.Reflection.FieldInfo Field(Type type, string name, Type fieldType = null)
            {
                if (type == null) return null;
                var f = type.GetField(name);
                if (f == null || fieldType != null && f.FieldType != fieldType)
                {
                    missing.Add(type.Name + "." + name + Wanted(fieldType?.Name, f?.FieldType.Name));
                    return null;
                }
                return f;
            }

            public System.Reflection.PropertyInfo Property(Type type, string name, Type propertyType)
            {
                if (type == null) return null;
                var p = type.GetProperty(name);
                if (p == null || p.PropertyType != propertyType || p.GetMethod == null)
                {
                    missing.Add(type.Name + "." + name + Wanted(propertyType.Name, p?.PropertyType.Name));
                    return null;
                }
                return p;
            }

            /// <summary>A method taking (double, double) and returning a struct (a column or climate sample).</summary>
            public System.Reflection.MethodInfo Method(Type type, string name)
            {
                if (type == null) return null;
                var m = type.GetMethod(name, new[] { typeof(double), typeof(double) });
                if (m == null || !m.ReturnType.IsValueType)
                {
                    missing.Add(type.Name + "." + name + "(double, double)" + (m == null ? "" : " (returning a struct, now " + m.ReturnType.Name + ")"));
                    return null;
                }
                return m;
            }

            /// <summary>" (Int32)", or " (Int32, now Byte)" for a member that is there with another type.</summary>
            static string Wanted(string type, string now) => type == null ? "" : " (" + type + (now == null || now == type ? "" : ", now " + now) + ")";

            /// <summary>The refusal naming Earth's version and everything missing (null when nothing is).</summary>
            public string Mismatch(string forWhat = "")
            {
                if (missing.Count == 0) return null;
                var assembly = earth.Assembly;
                string version = (Attribute.GetCustomAttribute(assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute))
                    as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion ?? assembly.GetName().Version?.ToString();
                return "Earth " + version + " is not compatible with this Cylinder World's Earth adapter" + forWhat + ": missing or changed "
                    + string.Join("; ", missing) + ". Install the Earth version this Cylinder World release names, or a Cylinder World update for this Earth.";
            }

            public void ThrowIfMissing()
            {
                if (Mismatch() is string message) throw new InvalidOperationException(message);
            }
        }

        public Column Sample(double x, double z) => sample(x, z);
        public double Latitude(double z) => (equatorZ - z) / blocksPerDegreeZ;

        /// <summary>Fixed gameplay approximation, not an observed sea-ice dataset or seasonal simulation.
        /// Cold Arctic ocean has continuous low ice, with an irregular floe margin toward warmer Atlantic water.</summary>
        public static double ArcticCoverage(Column c) => !c.Ocean ? 0 :
            Smooth((c.Latitude - ArcticIceLatitude) / 4) * Smooth((-8 - c.MeanTemperature) / 6) * Smooth((3 - c.WarmestMonth) / 1.5);

        static double Smooth(double value) => SeamParams.SmoothStep(Math.Clamp(value, 0, 1));

        /// <summary>All natural creature spawning is forbidden in the last kilometre at the poles. This also covers
        /// exposed rock, snow, water and caves where a custom glacier surface alone cannot help.</summary>
        public bool ForbidNaturalSpawn(SeamParams p, double z) =>
            p.EarthPolarIce && p.HasWalls && p.DistToWallFace(z) <= LifelessBorder;

        /// <summary>South of 60°S vanilla's Arctic predators (polar bears, arctic foxes) are vetoed; see
        /// EarthPolarSystem.IsVanillaPolarPredator. Other creatures keep their own rules, so Earth Map's regional
        /// fauna still decides where mod species such as penguins live.</summary>
        public bool ForbidPolarPredators(SeamParams p, double z) =>
            p.EarthPolarIce && p.HasWalls && Latitude(z) <= AntarcticLatitude;

        /// <summary>Hot springs and trader camps are blocked in Antarctica and in the last kilometre at either pole.</summary>
        public bool ForbidFeatures(SeamParams p, double z) =>
            ForbidPolarPredators(p, z) || ForbidNaturalSpawn(p, z);
    }
}
