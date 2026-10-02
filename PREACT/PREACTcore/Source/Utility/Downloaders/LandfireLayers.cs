using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using OSGeo.GDAL;

namespace PREACT.Tools
{
    /// <summary>
    /// Reads an LFPS result - one GeoTIFF with a band per requested layer - and splits it into the single-band rasters an
    /// ELMFIRE case takes as source layers: the fuel model, CC, CH, CBH and CBD.
    /// </summary>
    /// <remarks>
    /// Bands are found by their descriptions, which LFPS writes into the GeoTIFF itself (<c>LF2024_FBFM40_CONUS</c>,
    /// <c>LF2024_CH_CONUS</c>, ...), and by request order only when a band has none. Their units are read from the
    /// <c>.aux.xml</c> beside it (<c>&lt;UnitOfMeasure&gt;Meters * 10&lt;/UnitOfMeasure&gt;</c>), which is what decides
    /// ELMFIRE's scaling flags: LANDFIRE stores canopy height and base height in m x 10, bulk density in kg/m3 x 100 and
    /// cover in percent, and ELMFIRE's <c>CH_TIMES_10</c>, <c>CBH_TIMES_10</c>, <c>CBD_TIMES_100</c> and
    /// <c>CC_IN_PERCENT</c> say exactly that.
    ///
    /// The fuel model is written as Int16. ELMFIRE reads it into an INTEGER*2 array and converts nothing: LFPS's bands
    /// are Int32, and an Int32 fuel raster leaves that array unset. Canopy is written as Float32 holding LANDFIRE's
    /// scaled values unchanged, so the case build's bilinear warp onto the case grid does not round them.
    /// </remarks>
    public static class LandfireLayers
    {
        public sealed class Band
        {
            /// <summary>1-based.</summary>
            public int Index;
            public string Description = string.Empty;

            /// <summary>FBFM40, FBFM13, CC, CH, CBH, CBD, ELEV, SLPD, ASP, FCCS, ... (upper case); empty when unknown.</summary>
            public string Product = string.Empty;

            /// <summary>The release year (2024 for <c>LF2024_CC_CONUS</c>); 0 when the description does not say.</summary>
            public int Year;

            /// <summary>The release as LFPS names it (<c>LF2024</c>), or empty.</summary>
            public string Release => Year > 0 ? "LF" + Year.ToString(CultureInfo.InvariantCulture) : string.Empty;

            public string Region = string.Empty;

            /// <summary>The aux.xml's UnitOfMeasure, or GDAL's unit type; empty when neither says.</summary>
            public string Unit = string.Empty;
        }

        /// <summary>
        /// Splits a description into product, year and region. Handles the current names (<c>LF2024_FBFM40_CONUS</c>,
        /// <c>LF2020_SlpD_CONUS</c>, <c>LF2023_CC</c>) and the older LFPS ones (<c>US_230FBFM40</c>, <c>US_220CBH</c>), whose
        /// three-digit LANDFIRE version maps to a year: 200 = 2016 remap, 220 = 2020, 230 = 2022, 240 = 2023, 250 = 2024.
        /// </summary>
        public static Band ParseDescription(string description)
        {
            var band = new Band { Description = description ?? string.Empty };
            string d = band.Description.Trim().ToUpperInvariant();
            if (d.Length == 0) return band;

            string[] parts = d.Split('_');
            if (parts.Length >= 2 && parts[0].StartsWith("LF") && int.TryParse(parts[0].Substring(2), NumberStyles.None,
                    CultureInfo.InvariantCulture, out int year))
            {
                band.Year = year;
                band.Product = parts[1];
                if (parts.Length >= 3) band.Region = parts[2];
                return band;
            }

            //US_230FBFM40, AK_220CC, HI_200CBH
            if (parts.Length >= 2 && parts[1].Length > 3 && char.IsDigit(parts[1][0]) && char.IsDigit(parts[1][1]) && char.IsDigit(parts[1][2]))
            {
                band.Region = parts[0] == "US" ? "CONUS" : parts[0];
                band.Product = parts[1].Substring(3);
                switch (parts[1].Substring(0, 3))
                {
                    case "200": band.Year = 2016; break;
                    case "220": band.Year = 2020; break;
                    case "230": band.Year = 2022; break;
                    case "240": band.Year = 2023; break;
                    case "250": band.Year = 2024; break;
                }
                return band;
            }

            band.Product = d;
            return band;
        }

        /// <summary>Every band of the raster, with what its description and the aux.xml say about it.</summary>
        public static List<Band> ReadBands(string rasterPath)
        {
            var bands = new List<Band>();
            Dictionary<int, string> units = ReadAuxUnits(rasterPath + ".aux.xml");

            Gdal.AllRegister();
            using (Dataset ds = Gdal.Open(rasterPath, Access.GA_ReadOnly))
            {
                if (ds == null) throw new Exception("GDAL could not open " + rasterPath + ".");
                for (int b = 1; b <= ds.RasterCount; ++b)
                {
                    using (OSGeo.GDAL.Band gdalBand = ds.GetRasterBand(b))
                    {
                        Band band = ParseDescription(gdalBand.GetDescription());
                        band.Index = b;
                        if (units.TryGetValue(b, out string unit)) band.Unit = unit;
                        else band.Unit = gdalBand.GetUnitType() ?? string.Empty;
                        bands.Add(band);
                    }
                }
            }
            return bands;
        }

        /// <summary>
        /// The band units an LFPS aux.xml states: <c>&lt;PAMRasterBand band="6"&gt;&lt;UnitOfMeasure&gt;Meters * 10</c>.
        /// GDAL does not read that element (its own is UnitType), so it is read here. Empty when there is no file.
        /// </summary>
        internal static Dictionary<int, string> ReadAuxUnits(string auxPath)
        {
            var units = new Dictionary<int, string>();
            if (!File.Exists(auxPath)) return units;

            try
            {
                var doc = new XmlDocument();
                doc.Load(auxPath);
                XmlNodeList bands = doc.SelectNodes("/PAMDataset/PAMRasterBand");
                if (bands == null) return units;
                foreach (XmlNode node in bands)
                {
                    string index = node.Attributes?["band"]?.Value;
                    if (!int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out int b)) continue;
                    string unit = node.SelectSingleNode("UnitOfMeasure")?.InnerText ?? node.SelectSingleNode("UnitType")?.InnerText;
                    if (!string.IsNullOrWhiteSpace(unit)) units[b] = unit.Trim();
                }
            }
            catch (XmlException)
            {
            }
            return units;
        }

        /// <summary>
        /// The multiplier a LANDFIRE unit carries: 10 for "Meters * 10", 100 for "Kilograms per cubic meter * 100", 1 for
        /// "Meters"; 0 when the unit is empty.
        /// </summary>
        public static int UnitMultiplier(string unit)
        {
            if (string.IsNullOrWhiteSpace(unit)) return 0;
            string u = unit.Replace(" ", string.Empty).ToLowerInvariant();
            foreach (string sep in new[] { "*", "x" })
            {
                int at = u.LastIndexOf(sep, StringComparison.Ordinal);
                if (at >= 0 && at < u.Length - 1
                    && int.TryParse(u.Substring(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int m) && m > 0)
                {
                    return m;
                }
            }
            return 1;
        }

        /// <summary>The namelist flags the canopy's units call for, and what decided each.</summary>
        public sealed class Scaling
        {
            public bool CcInPercent = true;
            public bool ChTimes10 = true;
            public bool CbhTimes10 = true;
            public bool CbdTimes100 = true;
            public readonly List<string> Notes = new List<string>();
        }

        /// <summary>What a split produced.</summary>
        public sealed class Split
        {
            /// <summary>ELMFIRE stem (fbfm40 or fbfm13, cc, ch, cbh, cbd) to the raster written for it.</summary>
            public readonly Dictionary<string, string> Layers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>ELMFIRE stem to the band it came from.</summary>
            public readonly Dictionary<string, Band> Sources = new Dictionary<string, Band>(StringComparer.OrdinalIgnoreCase);

            public string FuelStem;
            public string Release = string.Empty;
            public Scaling Scaling = new Scaling();
            public readonly List<string> Warnings = new List<string>();
        }

        private static readonly (string Stem, string Product, int RequestOrder)[] Wanted =
        {
            ("cc", "CC", 5), ("ch", "CH", 6), ("cbh", "CBH", 7), ("cbd", "CBD", 8),
        };

        /// <summary>
        /// Writes the fuel model and the four canopy layers of <paramref name="rasterPath"/> as
        /// <c>&lt;prefix&gt;_&lt;release&gt;_&lt;stem&gt;.tif</c> in <paramref name="outputFolder"/>, and works out the scaling
        /// flags from the bands' units. Throws when there is no fuel model band.
        /// </summary>
        public static Split SplitLayers(string rasterPath, string outputFolder, string prefix, bool anderson13, Action<string> log)
        {
            log = log ?? (m => { });
            List<Band> bands = ReadBands(rasterPath);
            var split = new Split { FuelStem = anderson13 ? "fbfm13" : "fbfm40" };

            string fuelProduct = anderson13 ? "FBFM13" : "FBFM40";
            Band fuel = Find(bands, fuelProduct, 4, split, log);
            if (fuel == null)
            {
                Band other = bands.Find(b => b.Product.StartsWith("FBFM", StringComparison.Ordinal));
                throw new Exception(other != null
                    ? $"The LANDFIRE download holds {other.Description}, not the {fuelProduct} the scenario's FuelModelStandard asks for."
                    : $"The LANDFIRE download ({bands.Count} band(s)) holds no fuel model band.");
            }

            split.Release = fuel.Release;
            string tag = string.IsNullOrEmpty(split.Release) ? "lf" : split.Release;
            Directory.CreateDirectory(outputFolder);

            Gdal.AllRegister();
            using (Dataset source = Gdal.Open(rasterPath, Access.GA_ReadOnly))
            {
                if (source == null) throw new Exception("GDAL could not open " + rasterPath + ".");

                Write(source, fuel, split.FuelStem, "Int16", outputFolder, prefix, tag, split, log);
                foreach ((string stem, string product, int order) in Wanted)
                {
                    Band band = Find(bands, product, order, split, log);
                    if (band == null)
                    {
                        split.Warnings.Add($"no {product} band in the download; {stem} is left unset, so it is zero in the case");
                        continue;
                    }
                    if (band.Year > 0 && fuel.Year > 0 && band.Year != fuel.Year)
                    {
                        split.Warnings.Add($"{product} is {band.Release} and the fuel model {fuel.Release}");
                    }
                    Write(source, band, stem, "Float32", outputFolder, prefix, tag, split, log);
                }
            }

            split.Scaling = DecideScaling(split, rasterPath);
            foreach (string note in split.Scaling.Notes) log("  " + note);
            foreach (string warning in split.Warnings) log("  WARNING " + warning + ".");
            return split;
        }

        private static Band Find(List<Band> bands, string product, int requestOrder, Split split, Action<string> log)
        {
            Band byName = bands.Find(b => b.Product == product);
            if (byName != null) return byName;

            //Unlabelled bands only: a labelled band of another product is never taken for this one by its position.
            if (requestOrder <= bands.Count && string.IsNullOrEmpty(bands[requestOrder - 1].Product))
            {
                split.Warnings.Add($"band {requestOrder} has no description; taken as {product} by request order");
                return bands[requestOrder - 1];
            }
            return null;
        }

        private static void Write(Dataset source, Band band, string stem, string type, string folder, string prefix, string tag,
            Split split, Action<string> log)
        {
            string path = Path.Combine(folder, $"{prefix}_{tag}_{stem}.tif");
            string[] args =
            {
                "-b", band.Index.ToString(CultureInfo.InvariantCulture), "-ot", type, "-of", "GTiff",
                "-a_nodata", "-9999", "-co", "COMPRESS=DEFLATE",
                "-mo", "LANDFIRE_LAYER=" + (band.Description.Length > 0 ? band.Description : stem),
                "-mo", "LANDFIRE_UNIT=" + band.Unit,
            };
            using (var options = new GDALTranslateOptions(args))
            using (Dataset written = Gdal.wrapper_GDALTranslate(path, source, options, null, null))
            {
                if (written == null)
                {
                    throw new Exception($"Could not write band {band.Index} ({band.Description}) to {path}: {Gdal.GetLastErrorMsg()}");
                }
                using (OSGeo.GDAL.Band b = written.GetRasterBand(1))
                {
                    b.SetDescription(band.Description);
                    if (band.Unit.Length > 0) b.SetUnitType(band.Unit);
                }
                written.FlushCache();
            }

            split.Layers[stem] = path;
            split.Sources[stem] = band;
            log($"  band {band.Index} ({(band.Description.Length > 0 ? band.Description : "no description")}"
                + (band.Unit.Length > 0 ? ", " + band.Unit : string.Empty) + $") -> {Path.GetFileName(path)} ({type})");
        }

        /// <summary>
        /// The flags from the units: a "* 10" on CH and CBH, a "* 100" on CBD, "Percent" on CC. A unit that is missing
        /// keeps LANDFIRE's convention, and says so; one that states real units turns the flag off. The values are then
        /// checked against what a forest can be, so a unit and a flag that disagree with the data are reported.
        /// </summary>
        private static Scaling DecideScaling(Split split, string rasterPath)
        {
            var s = new Scaling();
            if (split.Sources.TryGetValue("cc", out Band cc))
            {
                string u = cc.Unit.ToLowerInvariant();
                if (u.Length == 0) s.Notes.Add("CC states no unit; LANDFIRE's percent assumed, CC_IN_PERCENT on.");
                else if (u.Contains("percent") || u.Contains("%")) s.Notes.Add($"CC is in {cc.Unit}: CC_IN_PERCENT on.");
                else
                {
                    s.CcInPercent = false;
                    s.Notes.Add($"CC is in {cc.Unit}, not percent: CC_IN_PERCENT off.");
                }
            }

            s.ChTimes10 = Flag(split, "ch", 10, s.Notes, "CH_TIMES_10");
            s.CbhTimes10 = Flag(split, "cbh", 10, s.Notes, "CBH_TIMES_10");
            s.CbdTimes100 = Flag(split, "cbd", 100, s.Notes, "CBD_TIMES_100");

            //The data, read with the flags as decided: tens of metres of canopy, a few tenths of a kg/m3.
            Plausible(split, "ch", s.ChTimes10 ? 0.1 : 1.0, 120.0, "m", "CH_TIMES_10");
            Plausible(split, "cbh", s.CbhTimes10 ? 0.1 : 1.0, 60.0, "m", "CBH_TIMES_10");
            Plausible(split, "cbd", s.CbdTimes100 ? 0.01 : 1.0, 2.0, "kg/m3", "CBD_TIMES_100");
            Plausible(split, "cc", 1.0, 100.0, "%", "CC_IN_PERCENT");
            return s;
        }

        private static bool Flag(Split split, string stem, int expected, List<string> notes, string key)
        {
            if (!split.Sources.TryGetValue(stem, out Band band)) return true;
            int multiplier = UnitMultiplier(band.Unit);
            if (multiplier == 0)
            {
                notes.Add($"{stem.ToUpperInvariant()} states no unit; LANDFIRE's x{expected} assumed, {key} on.");
                return true;
            }
            if (multiplier == expected)
            {
                notes.Add($"{stem.ToUpperInvariant()} is in {band.Unit}: {key} on.");
                return true;
            }
            if (multiplier == 1)
            {
                notes.Add($"{stem.ToUpperInvariant()} is in {band.Unit} (real units): {key} off.");
                return false;
            }
            split.Warnings.Add($"{stem.ToUpperInvariant()} is in {band.Unit}, which no ELMFIRE flag describes; {key} left on");
            return true;
        }

        private static void Plausible(Split split, string stem, double scale, double max, string unit, string key)
        {
            if (!split.Layers.TryGetValue(stem, out string path)) return;
            try
            {
                using (Dataset ds = Gdal.Open(path, Access.GA_ReadOnly))
                using (OSGeo.GDAL.Band band = ds.GetRasterBand(1))
                {
                    double[] minMax = new double[2];
                    band.ComputeRasterMinMax(minMax, 0);
                    double top = minMax[1] * scale;
                    if (top > max)
                    {
                        split.Warnings.Add($"{stem.ToUpperInvariant()} reaches {top:0.##} {unit} as {key} reads it - more than a "
                            + "canopy can be; check the band's units");
                    }
                }
            }
            catch (Exception)
            {
                //A raster with no valid cells has no min/max; nothing to judge.
            }
        }
    }
}
