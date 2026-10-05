using System;
using System.Collections.Generic;

namespace PREACT.Visualization.MapLayers
{
    /// <summary>
    /// The two surface fuel model systems ELMFIRE runs - Scott and Burgan's 40 (FBFM40) and Anderson's 13 (FBFM13) - with
    /// each code's short name and LANDFIRE's standard map colour.
    /// </summary>
    /// <remarks>
    /// <para>Colours: LANDFIRE's own symbology, read from the legend swatches of LANDFIRE's image services on
    /// lfps.usgs.gov (<c>Landfire_LF2024/LF2024_FBFM40_CONUS/ImageServer/legend</c>, <c>LF2024_FBFM13_CONUS</c>, and
    /// <c>Landfire_LF2022/LF2022_FBFM13_CONUS</c> for FBFM1, which the LF2024 CONUS legend leaves out), decoded pixel by
    /// pixel, 2026-10-05. The FBFM40 ones agree with the RED/GREEN/BLUE columns of the LANDFIRE-derived attribute table
    /// Montana DNRC serves for its 30 m FBFM40 (gis.dnrc.mt.gov, MWRA/fm40_30m) wherever both have the code.</para>
    /// <para>One exception: GR9 (109) is in no LANDFIRE legend (CONUS maps none), so its colour is not LANDFIRE's - it is
    /// chosen to continue the GR family's darkening olive past GR8 (139, 134, 78).</para>
    /// <para>Names: Scott, J.H. and Burgan, R.E. 2005, Standard Fire Behavior Fuel Models (RMRS-GTR-153), and Anderson,
    /// H.E. 1982, Aids to Determining Fuel Models (INT-GTR-122), shortened. Non-burnable codes 91-99 are LANDFIRE's, shared
    /// by both systems (in FBFM13 rasters too), each in the system's own colour.</para>
    /// </remarks>
    public static class FuelModelPalette
    {
        public enum FuelSystem { Fbfm40, Fbfm13 }

        public struct Entry
        {
            public int Code;

            /// <summary>The model's label: GR2, TL8, NB9 for FBFM40; 1..13 or Urban, Water for FBFM13.</summary>
            public string Label;

            /// <summary>A few words: "Low load, dry climate grass".</summary>
            public string Name;

            public byte R, G, B;

            /// <summary>Whether the colour is LANDFIRE's own (only GR9's is not).</summary>
            public bool Official;

            public bool Burnable => Code < 91 || Code > 99;

            internal Entry(int code, string label, string name, byte r, byte g, byte b, bool official = true)
            {
                Code = code; Label = label; Name = name; R = r; G = g; B = b; Official = official;
            }
        }

        public static readonly Entry[] Fbfm40 =
        {
            new Entry(91, "NB1", "Urban / developed", 104, 104, 104),
            new Entry(92, "NB2", "Snow / ice", 225, 225, 225),
            new Entry(93, "NB3", "Agricultural", 255, 237, 237),
            new Entry(98, "NB8", "Open water", 0, 14, 214),
            new Entry(99, "NB9", "Bare ground", 77, 110, 112),

            new Entry(101, "GR1", "Short, sparse dry climate grass", 255, 235, 190),
            new Entry(102, "GR2", "Low load, dry climate grass", 255, 211, 115),
            new Entry(103, "GR3", "Low load, very coarse, humid climate grass", 255, 236, 139),
            new Entry(104, "GR4", "Moderate load, dry climate grass", 255, 255, 115),
            new Entry(105, "GR5", "Low load, humid climate grass", 245, 222, 41),
            new Entry(106, "GR6", "Moderate load, humid climate grass", 230, 230, 64),
            new Entry(107, "GR7", "High load, dry climate grass", 205, 198, 115),
            new Entry(108, "GR8", "High load, very coarse, humid climate grass", 139, 134, 78),
            new Entry(109, "GR9", "Very high load, humid climate grass", 105, 100, 48, false),

            new Entry(121, "GS1", "Low load, dry climate grass-shrub", 255, 170, 0),
            new Entry(122, "GS2", "Moderate load, dry climate grass-shrub", 255, 167, 127),
            new Entry(123, "GS3", "Moderate load, humid climate grass-shrub", 255, 99, 0),
            new Entry(124, "GS4", "High load, humid climate grass-shrub", 205, 102, 0),

            new Entry(141, "SH1", "Low load, dry climate shrub", 215, 194, 158),
            new Entry(142, "SH2", "Moderate load, dry climate shrub", 215, 176, 158),
            new Entry(143, "SH3", "Moderate load, humid climate shrub", 205, 137, 102),
            new Entry(144, "SH4", "Low load, humid climate timber-shrub", 137, 90, 68),
            new Entry(145, "SH5", "High load, dry climate shrub", 205, 170, 102),
            new Entry(146, "SH6", "Low load, humid climate shrub", 237, 112, 68),
            new Entry(147, "SH7", "Very high load, dry climate shrub", 205, 125, 57),
            new Entry(148, "SH8", "High load, humid climate shrub", 168, 56, 0),
            new Entry(149, "SH9", "Very high load, humid climate shrub", 115, 26, 0),

            new Entry(161, "TU1", "Low load, dry climate timber-grass-shrub", 233, 255, 190),
            new Entry(162, "TU2", "Moderate load, humid climate timber-shrub", 170, 255, 0),
            new Entry(163, "TU3", "Moderate load, humid climate timber-grass-shrub", 180, 215, 158),
            new Entry(164, "TU4", "Dwarf conifer with understory", 112, 168, 0),
            new Entry(165, "TU5", "Very high load, dry climate timber-shrub", 38, 115, 0),

            new Entry(181, "TL1", "Low load compact conifer litter", 190, 255, 232),
            new Entry(182, "TL2", "Low load broadleaf litter", 0, 255, 197),
            new Entry(183, "TL3", "Moderate load conifer litter", 190, 210, 255),
            new Entry(184, "TL4", "Small downed logs", 123, 104, 238),
            new Entry(185, "TL5", "High load conifer litter", 190, 232, 255),
            new Entry(186, "TL6", "Moderate load broadleaf litter", 0, 197, 255),
            new Entry(187, "TL7", "Large downed logs", 0, 132, 168),
            new Entry(188, "TL8", "Long-needle litter", 0, 92, 230),
            new Entry(189, "TL9", "Very high load broadleaf litter", 77, 110, 145),

            new Entry(201, "SB1", "Low load activity fuel", 232, 190, 255),
            new Entry(202, "SB2", "Moderate load activity fuel or low load blowdown", 197, 0, 255),
            new Entry(203, "SB3", "High load activity fuel or moderate load blowdown", 255, 190, 232),
            new Entry(204, "SB4", "High load blowdown", 255, 127, 127),
        };

        public static readonly Entry[] Fbfm13 =
        {
            new Entry(1, "1", "Short grass (1 ft)", 255, 255, 190),
            new Entry(2, "2", "Timber (grass and understory)", 255, 255, 0),
            new Entry(3, "3", "Tall grass (2.5 ft)", 230, 197, 11),
            new Entry(4, "4", "Chaparral (6 ft)", 255, 211, 127),
            new Entry(5, "5", "Brush (2 ft)", 255, 170, 102),
            new Entry(6, "6", "Dormant brush, hardwood slash", 205, 170, 102),
            new Entry(7, "7", "Southern rough", 137, 112, 68),
            new Entry(8, "8", "Closed timber litter", 211, 255, 190),
            new Entry(9, "9", "Hardwood litter", 112, 168, 0),
            new Entry(10, "10", "Timber (litter and understory)", 38, 115, 0),
            new Entry(11, "11", "Light logging slash", 232, 190, 255),
            new Entry(12, "12", "Medium logging slash", 122, 142, 245),
            new Entry(13, "13", "Heavy logging slash", 197, 0, 255),

            new Entry(91, "Urban", "Urban / developed", 132, 0, 138),
            new Entry(92, "Snow/Ice", "Snow / ice", 159, 161, 240),
            new Entry(93, "Agriculture", "Agricultural", 233, 115, 255),
            new Entry(98, "Water", "Open water", 0, 0, 255),
            new Entry(99, "Barren", "Bare ground", 191, 191, 191),
        };

        private static readonly Dictionary<int, Entry> _by40 = Index(Fbfm40);
        private static readonly Dictionary<int, Entry> _by13 = Index(Fbfm13);

        private static Dictionary<int, Entry> Index(Entry[] entries)
        {
            var d = new Dictionary<int, Entry>();
            foreach (Entry e in entries) d[e.Code] = e;
            return d;
        }

        public static Entry[] Table(FuelSystem system) => system == FuelSystem.Fbfm13 ? Fbfm13 : Fbfm40;

        /// <summary>The entry for a code, or false for a code the system does not define.</summary>
        public static bool TryGet(FuelSystem system, int code, out Entry entry)
        {
            return (system == FuelSystem.Fbfm13 ? _by13 : _by40).TryGetValue(code, out entry);
        }

        /// <summary>
        /// The colour a code is drawn in. A code the system does not define (a custom fuel table's, a road code burnt in)
        /// is drawn in a neutral grey, so it shows without passing for a standard model.
        /// </summary>
        public static void Color(FuelSystem system, int code, out byte r, out byte g, out byte b)
        {
            if (TryGet(system, code, out Entry e))
            {
                r = e.R; g = e.G; b = e.B;
            }
            else
            {
                r = 150; g = 150; b = 160;
            }
        }

        /// <summary>"102 GR2 - Low load, dry climate grass"; "7 - Southern rough"; "250 - not a standard FBFM40 code".</summary>
        public static string Describe(FuelSystem system, int code)
        {
            string number = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!TryGet(system, code, out Entry e))
            {
                return number + " - not a standard " + (system == FuelSystem.Fbfm13 ? "FBFM13" : "FBFM40") + " code";
            }
            return string.Equals(e.Label, number, StringComparison.Ordinal)
                ? number + " - " + e.Name
                : number + " " + e.Label + " - " + e.Name;
        }
    }
}
