using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PREACT.Utility;

namespace PREACT.Visualization.MapLayers
{
    /// <summary>How a layer's values become colours on the map, and how one value is read out.</summary>
    public enum LayerStyle
    {
        /// <summary>Scott and Burgan 40 codes, LANDFIRE's colours.</summary>
        FuelModel40,
        /// <summary>Anderson 13 codes, LANDFIRE's colours.</summary>
        FuelModel13,
        /// <summary>A quantity: a sequential ramp from the smallest value to the largest.</summary>
        Sequential,
        /// <summary>A direction in degrees (aspect, wind direction): a ramp that ends where it starts.</summary>
        Cyclic,
        /// <summary>In or out: any positive value is in.</summary>
        Mask,
        /// <summary>Codes with no standard table (building fuel models, pyromes): a colour per code.</summary>
        Classes,
    }

    /// <summary>What a layer is about, in the order the list shows them.</summary>
    public enum LayerCategory { Fuel, Canopy, Terrain, Weather, Masks, Buildings, Other }

    /// <summary>One raster that can go on the map as an input layer.</summary>
    public sealed class InputLayer
    {
        public string Path;
        public string Stem;

        /// <summary>What it is: "Canopy height", "Fuel model (FBFM40)".</summary>
        public string Label;

        /// <summary>"Fire case inputs" or "LANDFIRE sources (LF2024)".</summary>
        public string Group;

        public LayerCategory Category;
        public LayerStyle Style;

        /// <summary>The unit a value is shown in after <see cref="Scale"/>: "m", "%", "mi/h", "°"; empty for none.</summary>
        public string Unit = string.Empty;

        /// <summary>
        /// What a stored value is multiplied by to be in <see cref="Unit"/>: 0.1 for a canopy height stored in m x 10
        /// (ELMFIRE's CH_TIMES_10), 100 for a cover stored as a fraction (CC_IN_PERCENT off).
        /// </summary>
        public double Scale = 1.0;

        /// <summary>A few words on the scaling or the height of the value: "stored x 10 (CH_TIMES_10)", "at 20 ft".</summary>
        public string Note = string.Empty;

        /// <summary>A weather raster: one band per DT_METEOROLOGY step.</summary>
        public bool Weather;

        /// <summary>Bands in the file; 0 until read (<see cref="InputLayerCatalog.ReadBandCounts"/>).</summary>
        public int Bands;

        /// <summary>The namelist key that names it, when one does.</summary>
        public string Key = string.Empty;

        /// <summary>For a wind direction: degrees the wind blows from.</summary>
        public bool IsWindDirection;

        public string FileName => System.IO.Path.GetFileName(Path ?? string.Empty);
    }

    /// <summary>
    /// The case's namelist flags that say what the stored numbers mean, with ELMFIRE's own defaults for any it leaves
    /// out (elmfire_vars.f90: the four canopy flags on, WS_IN_KPH and WS_AT_10M off, moisture in percent).
    /// </summary>
    public sealed class LayerUnits
    {
        public bool CcInPercent = true;
        public bool ChTimes10 = true;
        public bool CbhTimes10 = true;
        public bool CbdTimes100 = true;
        public bool WsInKph;
        public bool WsAt10m;
        public bool DeadMcInPercent = true;
        public bool LiveMcInPercent = true;

        /// <summary>Seconds per weather band.</summary>
        public double DtMeteorology = 3600.0;

        public static LayerUnits FromNamelist(string[] lines)
        {
            var u = new LayerUnits();
            if (lines == null) return u;
            u.CcInPercent = Flag(lines, "CC_IN_PERCENT", u.CcInPercent);
            u.ChTimes10 = Flag(lines, "CH_TIMES_10", u.ChTimes10);
            u.CbhTimes10 = Flag(lines, "CBH_TIMES_10", u.CbhTimes10);
            u.CbdTimes100 = Flag(lines, "CBD_TIMES_100", u.CbdTimes100);
            u.WsInKph = Flag(lines, "WS_IN_KPH", u.WsInKph);
            u.WsAt10m = Flag(lines, "WS_AT_10M", u.WsAt10m);
            u.DeadMcInPercent = Flag(lines, "DEAD_MC_IN_PERCENT", u.DeadMcInPercent);
            u.LiveMcInPercent = Flag(lines, "LIVE_MC_IN_PERCENT", u.LiveMcInPercent);
            string dt = ElmfireNamelist.GetKeyInGroup(lines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.DtMeteorology);
            if (!string.IsNullOrWhiteSpace(dt) && double.TryParse(dt.Trim().Replace('d', 'e').Replace('D', 'E'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double seconds) && seconds > 0.0)
            {
                u.DtMeteorology = seconds;
            }
            return u;
        }

        /// <summary>
        /// The flags a LANDFIRE download recorded in its provenance file (<c>&lt;Name&gt;_&lt;release&gt;_landfire.txt</c>,
        /// "CH_TIMES_10=true"); LANDFIRE's own convention (the ELMFIRE defaults) for any it does not state.
        /// </summary>
        public static LayerUnits FromLandfireProvenance(string path)
        {
            var u = new LayerUnits();
            if (path == null || !File.Exists(path)) return u;
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0 || raw.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                    string key = raw.Substring(0, eq).Trim().ToUpperInvariant();
                    string value = raw.Substring(eq + 1).Trim();
                    bool on = value.Equals("true", StringComparison.OrdinalIgnoreCase) || ElmfireNamelist.IsTrue(value);
                    switch (key)
                    {
                        case "CC_IN_PERCENT": u.CcInPercent = on; break;
                        case "CH_TIMES_10": u.ChTimes10 = on; break;
                        case "CBH_TIMES_10": u.CbhTimes10 = on; break;
                        case "CBD_TIMES_100": u.CbdTimes100 = on; break;
                    }
                }
            }
            catch (IOException)
            {
                //Unreadable is the same as absent: LANDFIRE's convention.
            }
            return u;
        }

        private static bool Flag(string[] lines, string key, bool fallback)
        {
            string v = ElmfireNamelist.GetKeyInGroup(lines, ElmfireNamelistKeys.InputsGroup, key);
            return string.IsNullOrWhiteSpace(v) ? fallback : ElmfireNamelist.IsTrue(v);
        }
    }

    /// <summary>
    /// Which input rasters a fire case has, and what each one is: the rasters its namelist names, the standard stems in
    /// its inputs folder that the namelist does not name (a case not built yet, the plain fbfm40 beside a roads-burnt
    /// one, wui_area), and the LANDFIRE source layers a download left under downloads/landfire.
    /// </summary>
    /// <remarks>
    /// Only <see cref="File.Exists"/> and directory listings: nothing is opened, so a listing is cheap enough to redo
    /// whenever the case may have changed (still not every frame - the GUI lists on a worker when asked).
    /// </remarks>
    public static class InputLayerCatalog
    {
        public const string CaseGroup = "Fire case inputs";

        /// <summary>The stems a case's inputs folder is looked through for, whatever its namelist says.</summary>
        public static readonly string[] StandardStems =
        {
            "fbfm40", "fbfm13", "cc", "ch", "cbh", "cbd", "dem", "slp", "asp",
            "ws", "wd", "m1", "m10", "m100", ElmfireStems.IgnitionMask, ElmfireStems.WuiArea,
            "bldg_area_avg", "baa", "bldg_separation_distance", "ssd", "bldg_nonburnable_frac", "nbf_h",
            "bldg_footprint_frac", "ff_h", "bldg_fuel_model", "bfm_h",
        };

        //A stem the namelist does not name, mapped to the key it would be named by.
        private static readonly Dictionary<string, string> _stemKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "fbfm40", "FBFM_FILENAME" }, { "fbfm13", "FBFM_FILENAME" }, { "cc", "CC_FILENAME" }, { "ch", "CH_FILENAME" },
            { "cbh", "CBH_FILENAME" }, { "cbd", "CBD_FILENAME" }, { "dem", "DEM_FILENAME" }, { "slp", "SLP_FILENAME" },
            { "asp", "ASP_FILENAME" }, { "adj", "ADJ_FILENAME" }, { "phi", "PHI_FILENAME" },
            { "ws", "WS_FILENAME" }, { "wd", "WD_FILENAME" }, { "m1", "M1_FILENAME" }, { "m10", "M10_FILENAME" },
            { "m100", "M100_FILENAME" }, { ElmfireStems.IgnitionMask, "IGNITION_MASK_FILENAME" },
            { "bldg_area_avg", "BLDG_AREA_FILENAME" }, { "baa", "BLDG_AREA_FILENAME" },
            { "bldg_separation_distance", "BLDG_SEPARATION_DIST_FILENAME" }, { "ssd", "BLDG_SEPARATION_DIST_FILENAME" },
            { "bldg_nonburnable_frac", "BLDG_NONBURNABLE_FRAC_FILENAME" }, { "nbf_h", "BLDG_NONBURNABLE_FRAC_FILENAME" },
            { "bldg_footprint_frac", "BLDG_FOOTPRINT_FRAC_FILENAME" }, { "ff_h", "BLDG_FOOTPRINT_FRAC_FILENAME" },
            { "bldg_fuel_model", "BLDG_FUEL_MODEL_FILENAME" }, { "bfm_h", "BLDG_FUEL_MODEL_FILENAME" },
        };

        /// <summary>
        /// Every input layer that exists. <paramref name="namelistLines"/> may be null (no namelist yet), and so may
        /// <paramref name="landfireFolder"/>. Ordered by category, the case's own before the LANDFIRE sources.
        /// </summary>
        public static List<InputLayer> List(string caseDirectory, string[] namelistLines, string landfireFolder)
        {
            var layers = new List<InputLayer>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            LayerUnits units = LayerUnits.FromNamelist(namelistLines);

            if (!string.IsNullOrEmpty(caseDirectory))
            {
                if (namelistLines != null)
                {
                    foreach (ElmfireStems.NamelistRaster r in ElmfireStems.ReferencedRasters(namelistLines, caseDirectory))
                    {
                        if (!File.Exists(r.Path) || !seen.Add(Full(r.Path))) continue;
                        layers.Add(Describe(r.Key, r.Stem, r.Path, units, CaseGroup));
                    }
                }

                string inputs = Path.Combine(caseDirectory, "inputs");
                if (Directory.Exists(inputs))
                {
                    foreach (string stem in StandardStems)
                    {
                        string path = ElmfireStems.Tif(inputs, stem);
                        if (!File.Exists(path) || !seen.Add(Full(path))) continue;
                        _stemKeys.TryGetValue(stem, out string key);
                        layers.Add(Describe(key ?? string.Empty, stem, path, units, CaseGroup));
                    }
                }
            }

            if (!string.IsNullOrEmpty(landfireFolder) && Directory.Exists(landfireFolder))
            {
                foreach (string path in Directory.GetFiles(landfireFolder, "*.tif"))
                {
                    if (!TryParseSourceName(Path.GetFileName(path), out string prefix, out string release, out string stem)) continue;
                    if (!seen.Add(Full(path))) continue;
                    LayerUnits sourceUnits = LayerUnits.FromLandfireProvenance(
                        Path.Combine(landfireFolder, prefix + "_" + release + "_landfire.txt"));
                    _stemKeys.TryGetValue(stem, out string key);
                    layers.Add(Describe(key, stem, path, sourceUnits, "LANDFIRE sources (" + release + ")"));
                }
            }

            //Stable: within a category the namelist's order (the raster ELMFIRE runs first), then the sources.
            var order = new Dictionary<InputLayer, int>();
            for (int i = 0; i < layers.Count; ++i) order[layers[i]] = i;
            layers.Sort((a, b) =>
            {
                int byGroup = (a.Group == CaseGroup ? 0 : 1).CompareTo(b.Group == CaseGroup ? 0 : 1);
                if (byGroup != 0) return byGroup;
                byGroup = string.CompareOrdinal(a.Group, b.Group); //one release's sources together
                if (byGroup != 0) return byGroup;
                int byCategory = a.Category.CompareTo(b.Category);
                if (byCategory != 0) return byCategory;
                //A download folder lists alphabetically (cbd before cc); the sources take the case's order, cc ch cbh cbd.
                if (a.Group != CaseGroup)
                {
                    int byStem = Array.IndexOf(StandardStems, a.Stem).CompareTo(Array.IndexOf(StandardStems, b.Stem));
                    if (byStem != 0) return byStem;
                }
                return order[a].CompareTo(order[b]);
            });
            return layers;
        }

        /// <summary>
        /// A LANDFIRE source layer's file name, <c>&lt;Name&gt;_&lt;release&gt;_&lt;stem&gt;.tif</c> as the download writes it
        /// (<c>Auburn2_LF2024_fbfm40.tif</c>); false for anything else in the folder (the LFPS GeoTIFF itself, a .txt).
        /// </summary>
        public static bool TryParseSourceName(string fileName, out string prefix, out string release, out string stem)
        {
            prefix = release = stem = null;
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) return false;
            string name = fileName.Substring(0, fileName.Length - 4);
            int last = name.LastIndexOf('_');
            if (last <= 0) return false;
            string s = name.Substring(last + 1);
            if (Array.IndexOf(PREACT.Tools.LandfireFuels.Stems, s.ToLowerInvariant()) < 0) return false;
            int previous = name.LastIndexOf('_', last - 1);
            if (previous <= 0) return false;
            string r = name.Substring(previous + 1, last - previous - 1);
            if (r.Length < 3 || !r.StartsWith("LF", StringComparison.OrdinalIgnoreCase)) return false;
            prefix = name.Substring(0, previous);
            release = r;
            stem = s.ToLowerInvariant();
            return true;
        }

        /// <summary>Fills in <see cref="InputLayer.Bands"/> (opens each file's header only). For a worker.</summary>
        public static void ReadBandCounts(IEnumerable<InputLayer> layers)
        {
            foreach (InputLayer layer in layers)
            {
                layer.Bands = System.Math.Max(1, AscRaster.GetBandCount(layer.Path));
            }
        }

        /// <summary>What the raster a key names is, and how its numbers read, under <paramref name="u"/>.</summary>
        public static InputLayer Describe(string key, string stem, string path, LayerUnits u, string group)
        {
            var l = new InputLayer { Key = key ?? string.Empty, Stem = stem, Path = path, Group = group, Style = LayerStyle.Sequential };
            string k = (key ?? string.Empty).ToUpperInvariant();
            string s = (stem ?? string.Empty).ToLowerInvariant();

            if (k.Length == 0 && s == ElmfireStems.WuiArea)
            {
                Set(l, "WUI area", LayerCategory.Masks, LayerStyle.Mask);
                return l;
            }

            switch (k)
            {
                case "FBFM_FILENAME":
                    bool anderson = s.Contains("13");
                    Set(l, anderson ? "Fuel model (FBFM13)" : "Fuel model (FBFM40)", LayerCategory.Fuel,
                        anderson ? LayerStyle.FuelModel13 : LayerStyle.FuelModel40);
                    break;
                case "CC_FILENAME":
                    Set(l, "Canopy cover", LayerCategory.Canopy, LayerStyle.Sequential, "%", u.CcInPercent ? 1.0 : 100.0,
                        u.CcInPercent ? "stored in percent (CC_IN_PERCENT)" : "stored as a fraction (CC_IN_PERCENT off)");
                    break;
                case "CH_FILENAME":
                    Set(l, "Canopy height", LayerCategory.Canopy, LayerStyle.Sequential, "m", u.ChTimes10 ? 0.1 : 1.0,
                        u.ChTimes10 ? "stored in m x 10 (CH_TIMES_10)" : "stored in m");
                    break;
                case "CBH_FILENAME":
                    Set(l, "Canopy base height", LayerCategory.Canopy, LayerStyle.Sequential, "m", u.CbhTimes10 ? 0.1 : 1.0,
                        u.CbhTimes10 ? "stored in m x 10 (CBH_TIMES_10)" : "stored in m");
                    break;
                case "CBD_FILENAME":
                    Set(l, "Canopy bulk density", LayerCategory.Canopy, LayerStyle.Sequential, "kg/m³", u.CbdTimes100 ? 0.01 : 1.0,
                        u.CbdTimes100 ? "stored in kg/m³ x 100 (CBD_TIMES_100)" : "stored in kg/m³");
                    break;
                case "DEM_FILENAME":
                    Set(l, "Elevation", LayerCategory.Terrain, LayerStyle.Sequential, "m");
                    break;
                case "SLP_FILENAME":
                    Set(l, "Slope", LayerCategory.Terrain, LayerStyle.Sequential, "°");
                    break;
                case "ASP_FILENAME":
                    Set(l, "Aspect", LayerCategory.Terrain, LayerStyle.Cyclic, "°", 1.0, "the direction the slope faces");
                    break;
                case "ADJ_FILENAME":
                    Set(l, "Spread rate adjustment", LayerCategory.Terrain, LayerStyle.Sequential);
                    break;
                case "PHI_FILENAME":
                    Set(l, "Initial level set (phi)", LayerCategory.Other, LayerStyle.Sequential);
                    break;
                case "WS_FILENAME":
                    Set(l, "Wind speed", LayerCategory.Weather, LayerStyle.Sequential, u.WsInKph ? "km/h" : "mi/h", 1.0,
                        u.WsAt10m ? "at 10 m (WS_AT_10M)" : "at 20 ft");
                    l.Weather = true;
                    break;
                case "WD_FILENAME":
                    Set(l, "Wind direction", LayerCategory.Weather, LayerStyle.Cyclic, "°", 1.0, "the direction the wind blows from");
                    l.Weather = true;
                    l.IsWindDirection = true;
                    break;
                case "M1_FILENAME":
                case "M10_FILENAME":
                case "M100_FILENAME":
                    string hours = k.Substring(1, k.IndexOf('_') - 1);
                    Set(l, hours + "-h dead fuel moisture", LayerCategory.Weather, LayerStyle.Sequential, "%",
                        u.DeadMcInPercent ? 1.0 : 100.0, u.DeadMcInPercent ? string.Empty : "stored as a fraction (DEAD_MC_IN_PERCENT off)");
                    l.Weather = true;
                    break;
                case "MLH_FILENAME":
                case "MLW_FILENAME":
                case "FMC_FILENAME":
                    Set(l, k == "MLH_FILENAME" ? "Live herbaceous moisture" : k == "MLW_FILENAME" ? "Live woody moisture" : "Foliar moisture",
                        LayerCategory.Weather, LayerStyle.Sequential, "%", u.LiveMcInPercent || k == "FMC_FILENAME" ? 1.0 : 100.0);
                    l.Weather = true;
                    break;
                case "ERC_FILENAME":
                    Set(l, "Energy release component", LayerCategory.Weather, LayerStyle.Sequential);
                    l.Weather = true;
                    break;
                case "IGNITION_MASK_FILENAME":
                    Set(l, "Ignition mask", LayerCategory.Masks, LayerStyle.Mask);
                    break;
                case "BARRIER_FILENAME":
                    Set(l, "Barriers", LayerCategory.Masks, LayerStyle.Mask);
                    break;
                case "ALREADY_BURNED_FILENAME":
                    Set(l, "Already burned", LayerCategory.Masks, LayerStyle.Mask);
                    break;
                case "PYROMES_FILENAME":
                    Set(l, "Pyromes", LayerCategory.Other, LayerStyle.Classes);
                    break;
                case "BLDG_AREA_FILENAME":
                    Set(l, "Building area", LayerCategory.Buildings, LayerStyle.Sequential, "m²");
                    break;
                case "BLDG_SEPARATION_DIST_FILENAME":
                    Set(l, "Building separation distance", LayerCategory.Buildings, LayerStyle.Sequential, "m");
                    break;
                case "BLDG_NONBURNABLE_FRAC_FILENAME":
                    Set(l, "Building non-burnable fraction", LayerCategory.Buildings, LayerStyle.Sequential);
                    break;
                case "BLDG_FOOTPRINT_FRAC_FILENAME":
                    Set(l, "Building footprint fraction", LayerCategory.Buildings, LayerStyle.Sequential);
                    break;
                case "BLDG_FUEL_MODEL_FILENAME":
                    Set(l, "Building fuel model", LayerCategory.Buildings, LayerStyle.Classes);
                    break;
                default:
                    //A namelist key this list does not know (land value, population density, SDI): its own name.
                    string name = k.EndsWith("_FILENAME", StringComparison.Ordinal) ? k.Substring(0, k.Length - 9) : (stem ?? "raster");
                    Set(l, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.Replace('_', ' ').ToLowerInvariant()),
                        LayerCategory.Other, LayerStyle.Sequential);
                    l.Weather = k == "ERC_FILENAME";
                    break;
            }
            return l;
        }

        private static void Set(InputLayer l, string label, LayerCategory category, LayerStyle style, string unit = "",
            double scale = 1.0, string note = "")
        {
            l.Label = label;
            l.Category = category;
            l.Style = style;
            l.Unit = unit;
            l.Scale = scale;
            l.Note = note;
        }

        private static string Full(string path)
        {
            try { return Path.GetFullPath(path); } catch { return path; }
        }
    }
}
