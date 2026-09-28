using System;
using System.Collections.Generic;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// The file names an ELMFIRE case is made of, in one place.
    /// </summary>
    /// <remarks>
    /// These used to be spelled out wherever they were needed - the five weather stems in four places, the fuel
    /// stems in three, the output stems in the runner and again in the campaign driver - and the copies drifted:
    /// ignition sampling read <c>fbfm40.tif</c> while the namelist ran <c>fbfm40_roads101</c>. Stems are
    /// ELMFIRE's own convention (a name without directory or extension, resolved under a directory key), so they
    /// are constants; which fuel stem a case actually uses is read from its namelist by <see cref="FuelStem"/>.
    /// </remarks>
    public static class ElmfireStems
    {
        // ---- terrain and constants every case carries
        public const string Dem = "dem";
        public const string Slope = "slp";
        public const string Aspect = "asp";
        public const string Adj = "adj";
        public const string Phi = "phi";

        // ---- weather: one series split across five files, which must agree on band count
        public const string WindSpeed = "ws";
        public const string WindDirection = "wd";
        public const string M1 = "m1";
        public const string M10 = "m10";
        public const string M100 = "m100";

        public static readonly string[] Weather = { WindSpeed, WindDirection, M1, M10, M100 };

        // ---- fuel, in the order a case carrying both is resolved: the finer standard first
        public static readonly string[] Fuel = { "fbfm40", "fbfm13" };

        // ---- masks
        public const string IgnitionMask = "ignition_mask";

        /// <summary>The painted WUI area on the case grid, written by the case builder for k-PERIL.</summary>
        public const string WuiArea = "wui_area";

        // ---- tables read from MISCELLANEOUS_INPUTS_DIRECTORY
        public const string FuelModelTable = "fuel_models.csv";
        public const string BuildingFuelModelTable = "building_fuel_models.csv";

        // ---- outputs, named <stem>_<7-digit case>_<7-digit time in seconds>.tif
        public const string TimeOfArrival = "time_of_arrival";

        /// <summary>Velocity of spread, m/min with SPREAD_RATE_IN_M.</summary>
        public const string SpreadRate = "vs";

        public const string SpreadDirection = "spread_dir";

        /// <summary>Fireline intensity, kW/m.</summary>
        public const string FirelineIntensity = "flin";

        /// <summary>
        /// Midflame wind speed in <b>ft/min</b> for every cell the fire reached, nodata elsewhere - written on the
        /// final dump only, by <c>DUMP_MIDFLAME_WINDSPEED</c> (ELMFIRE-WUINITY a7fb9d6 and later).
        /// </summary>
        public const string MidflameWindSpeed = "mfws";

        /// <summary>ft/min per mi/h. k-PERIL's length-to-breadth correlation takes midflame wind in mi/h.</summary>
        public const double FeetPerMinutePerMph = 88.0;

        /// <summary>A stem's GeoTIFF inside <paramref name="directory"/>.</summary>
        public static string Tif(string directory, string stem) => Path.Combine(directory, stem + ".tif");

        /// <summary>
        /// The fuel model stem a namelist runs: its <c>FBFM_FILENAME</c> when that raster exists, otherwise the first
        /// of <see cref="Fuel"/> the inputs folder holds, or null.
        /// </summary>
        /// <remarks>
        /// The namelist first, because that is the raster ELMFIRE will burn - a case whose namelist names
        /// <c>fbfm40_roads101</c> has to have its ignitions sampled and its mask restricted against that raster,
        /// not against the plain <c>fbfm40.tif</c> sitting beside it.
        /// </remarks>
        public static string FuelStem(string[] namelistLines, string inputsDirectory)
        {
            string named = namelistLines == null
                ? null
                : ElmfireNamelist.GetKeyInGroup(namelistLines, ElmfireNamelistKeys.InputsGroup, "FBFM_FILENAME");

            if (!string.IsNullOrWhiteSpace(named)
                && (inputsDirectory == null || File.Exists(Tif(inputsDirectory, named))))
            {
                return named;
            }

            if (inputsDirectory == null) return null;

            foreach (string stem in Fuel)
            {
                if (File.Exists(Tif(inputsDirectory, stem))) return stem;
            }

            return null;
        }

        /// <summary>
        /// Whether a fuel code can carry fire. The 91-99 block is non-burnable in both Anderson FBFM13 and Scott
        /// &amp; Burgan FBFM40 (urban, snow, agriculture, water, barren), as is 0 and anything negative - which
        /// covers NoData, the case that matters most since a clipped domain is padded with it. Anderson's 14 is
        /// its unburnable class and is excluded by default.
        /// </summary>
        public static bool IsBurnable(float code, ICollection<int> extraNonBurnable = null)
        {
            if (float.IsNaN(code) || code <= 0f) return false;
            int c = (int)System.Math.Round(code);
            if (c >= 91 && c <= 99) return false;
            return extraNonBurnable == null ? c != 14 : !extraNonBurnable.Contains(c);
        }

        /// <summary>
        /// Where a namelist's <c>*_DIRECTORY</c> key points, resolved against the directory ELMFIRE runs in.
        /// Null when the key is unset or ELMFIRE's own <c>'null'</c>.
        /// </summary>
        public static string ResolveDirectory(string[] namelistLines, string group, string key, string runDirectory)
        {
            string value = ElmfireNamelist.GetKeyInGroup(namelistLines, group, key);
            if (string.IsNullOrWhiteSpace(value) || value.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string path = Path.IsPathRooted(value) ? value : Path.Combine(runDirectory, value);
            try
            {
                return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// A path as a namelist should hold it for a run in <paramref name="runDirectory"/>: relative when it can
        /// be, with forward slashes, and never with a trailing separator (ELMFIRE appends its own).
        /// </summary>
        /// <remarks>
        /// Relative because ELMFIRE builds its <c>gdal_translate</c> command lines by concatenation without quoting
        /// the data paths (elmfire_io.f90), so an absolute path through <c>C:\Users\First Last\...</c> or
        /// <c>OneDrive - Org</c> splits into extra arguments and every realization fails with "Too many command
        /// options". Relative paths drop everything above the common ancestor, which is where those spaces live.
        /// Forward slashes work for gfortran and Intel Fortran on Windows alike.
        /// </remarks>
        public static string ForNamelist(string runDirectory, string path)
        {
            string full = Path.GetFullPath(path);
            string relative = GetRelativePath(Path.GetFullPath(runDirectory), full);
            relative = relative.Replace('\\', '/').TrimEnd('/');
            if (relative.Length == 0) return ".";
            if (!Path.IsPathRooted(relative) && !relative.StartsWith(".", StringComparison.Ordinal))
            {
                relative = "./" + relative;
            }
            return relative;
        }

        /// <summary>
        /// <c>Path.GetRelativePath</c>, which netstandard2.1 has; wrapped so a path on another drive (Windows)
        /// comes back absolute rather than throwing.
        /// </summary>
        private static string GetRelativePath(string from, string to)
        {
            try
            {
                return Path.GetRelativePath(from, to);
            }
            catch
            {
                return to;
            }
        }
    }
}
