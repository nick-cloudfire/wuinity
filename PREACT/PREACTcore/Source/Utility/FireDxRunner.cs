//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using OSGeo.GDAL;
using PREACT.Input;

namespace PREACT.Utility
{
    /// <summary>
    /// Runs FireDX (Maria Theodori's building data engine, a Python package) on a scenario's FBFM40 fuel raster, and
    /// names what it makes as the scenario's ELMFIRE building layers and fuel model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FireDX takes the LANDFIRE FBFM40 raster, gets building footprints (Microsoft's global ML footprints and
    /// OpenStreetMap, or a file), joins attributes to them and writes, on the FBFM40 grid: the minimum building separation
    /// (<c>ssd_min.tif</c>), the mean building plan dimension (<c>baa_m.tif</c>, the square root of the mean footprint
    /// area), the building fuel model (<c>bfm.tif</c>), the footprint and non-burnable fractions (<c>ff.tif</c>,
    /// <c>nbf.tif</c>) and <c>fbfm40b.tif</c>: the fuel with LANDFIRE's urban 91 split into buildings (91) and the rest
    /// (256: pavement and roads, which ELMFIRE does not burn). Those are the five rasters ELMFIRE's building spread model
    /// reads and the fuel it is meant to be run on.
    /// </para>
    /// <para>
    /// ELMFIRE's <c>BLDG_AREA</c> is the "average building plan dimension, m" (<c>elmfire_spread_rate.f90</c>, the Hamada
    /// model's <c>A_0</c>, default constant 20.0), a length - so <c>baa_m.tif</c>, not <c>baa_m2.tif</c>.
    /// </para>
    /// <para>
    /// FireDX is proprietary and is not part of PREACT: it is the git submodule <c>WUInity/Assets/ThirdParty/firedx</c> (a
    /// private repository), or a folder the user names, and is run from there with <c>PYTHONPATH</c> - no install into the
    /// environment needed. PREACT writes its own small driver (<c>preact_firedx.py</c>, an embedded resource) beside the
    /// output; see that file for what it adds: dependency and network checks before any work, and the "basic" attribute
    /// path for anywhere FireDX's own US/California attribute join has no data.
    /// </para>
    /// </remarks>
    public static class FireDxRunner
    {
        /// <summary>Where the output goes, relative to the scenario.</summary>
        public const string Folder = "downloads/firedx";

        public const string LogFileName = "firedx.log";
        public const string ProvenanceFileName = "firedx_sources.txt";
        public const string InputFileName = "fbfm40_input.tif";
        public const string ScriptFileName = "preact_firedx.py";
        public const string BuildingTableFileName = "building_fuel_models.csv";

        /// <summary>The submodule FireDX is checked out in, relative to the repository.</summary>
        public const string SubmodulePath = "WUInity/Assets/ThirdParty/firedx";

        /// <summary>The conda environment FireDX's Makefile and environment.yml create.</summary>
        public const string CondaEnvironment = "firedx";

        private const string Tag = "PREACT-FIREDX-";

        /// <summary>Which attribute path FireDX takes; see <c>preact_firedx.py</c>.</summary>
        public enum AttributePath
        {
            /// <summary>FireDX's own (California) when the area's centre is in California, otherwise basic.</summary>
            Auto,

            /// <summary>FireDX's own attribute join: USACE NSI structures and CAL FIRE hazard zones; California only, online.</summary>
            California,

            /// <summary>Footprints, FireDX's metrics and fuel models, residential where nothing says otherwise; anywhere.</summary>
            Basic,
        }

        /// <summary>One raster FireDX writes: its file, the [ELMFIRE] key it becomes, and the case stem that key fills.</summary>
        public sealed class Layer
        {
            public readonly string File;
            public readonly string Key;
            public readonly string Stem;

            public Layer(string file, string key, string stem)
            {
                File = file;
                Key = key;
                Stem = stem;
            }
        }

        /// <summary>The rasters a run must produce, in the order they are named.</summary>
        public static readonly Layer[] Layers =
        {
            new Layer("fbfm40b.tif", nameof(ElmfireInput.FuelModelFile), "fbfm40"),
            new Layer("baa_m.tif", nameof(ElmfireInput.BuildingAreaFile), "bldg_area_avg"),
            new Layer("ssd_min.tif", nameof(ElmfireInput.BuildingSeparationFile), "bldg_separation_distance"),
            new Layer("nbf.tif", nameof(ElmfireInput.BuildingNonBurnableFractionFile), "bldg_nonburnable_frac"),
            new Layer("ff.tif", nameof(ElmfireInput.BuildingFootprintFractionFile), "bldg_footprint_frac"),
            new Layer("bfm.tif", nameof(ElmfireInput.BuildingFuelModelFile), "bldg_fuel_model"),
        };

        public sealed class Options
        {
            /// <summary>The Python to run; null finds one (<see cref="FindPython"/>).</summary>
            public string Python;

            /// <summary>The folder holding the <c>firedx</c> package; null finds one (<see cref="FindPackage"/>).</summary>
            public string Package;

            /// <summary>The scenario folder, which the paths given to the scenario are relative to.</summary>
            public string Root;

            /// <summary>The FBFM40 raster to run on, absolute.</summary>
            public string Fbfm40Path;

            /// <summary>Where the output goes, absolute; <see cref="Folder"/> under <see cref="Root"/> when null.</summary>
            public string OutputDirectory;

            /// <summary>Buildings built after this year are left out (a hindcast); 0 keeps every building.</summary>
            public int FireYear;

            /// <summary>An existing footprints file (GeoJSON, shapefile, GeoPackage, ...), or null to download them.</summary>
            public string FootprintsPath;

            public AttributePath Attributes = AttributePath.Auto;

            /// <summary>The case folder, absolute; its superseded fuel and building layers are moved aside after a run.</summary>
            public string CaseDirectory;

            public Action<string> Log;
            public CancellationToken Cancellation;
        }

        public sealed class Result
        {
            public bool Ok;

            /// <summary>What went wrong, said for a user, or the summary of what was made.</summary>
            public string Message;

            public int ExitCode = -1;
            public bool Stopped;

            /// <summary>Modules the Python environment lacks.</summary>
            public readonly List<string> MissingModules = new List<string>();

            /// <summary>Hosts that could not be reached, with what each is for.</summary>
            public readonly List<string> Unreachable = new List<string>();

            /// <summary>The attribute path FireDX took (california or basic).</summary>
            public string AttributesUsed;

            /// <summary>[ELMFIRE] key to the layer's path relative to the scenario, forward slashes.</summary>
            public readonly Dictionary<string, string> Layers = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>FireDX's building fuel model table, absolute.</summary>
            public string BuildingTable;

            public string LogFile;
            public string ProvenanceFile;

            /// <summary>Cells of fbfm40b.tif that are buildings (91), pavement and roads (256), and LANDFIRE's 91 before.</summary>
            public long BuildingCells, RoadCells, UrbanCellsBefore;

            /// <summary>Case layers moved aside because these replace them.</summary>
            public readonly List<string> RemovedCaseLayers = new List<string>();
        }

        // ------------------------------------------------------------------ finding Python and FireDX

        private static bool OnWindows => System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows);

        /// <summary>
        /// The Python to run FireDX with: the user's setting (Help &gt; External tools and keys), else the Python of a conda
        /// environment named <c>firedx</c> under the usual conda installs. Null when there is neither: a system Python is
        /// not guessed at, since one without FireDX's geospatial stack fails only after a while, and confusingly.
        /// </summary>
        public static string FindPython(out ToolPaths.Source source)
        {
            string setting = ToolPaths.UserSetting(ToolPaths.Tool.FireDxPython);
            if (setting != null)
            {
                source = ToolPaths.Source.UserSetting;
                return setting;
            }

            foreach (string candidate in CondaPythonCandidates())
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        source = ToolPaths.Source.Automatic;
                        return Path.GetFullPath(candidate);
                    }
                }
                catch
                {
                    //an unusable candidate is simply not it
                }
            }

            source = ToolPaths.Source.None;
            return null;
        }

        /// <summary>Where the automatic search looks, for a message.</summary>
        public const string PythonSearchDescription =
            "a conda environment named firedx: the active one (CONDA_PREFIX), then envs/firedx under CONDA_EXE's install, "
            + "miniconda3, anaconda3, miniforge3, mambaforge, micromamba and ~/.conda";

        /// <summary>The paths <see cref="FindPython"/> tries after the setting, in order. Only file-exists checks: cheap.</summary>
        public static IEnumerable<string> CondaPythonCandidates()
        {
            string exe = OnWindows ? "python.exe" : Path.Combine("bin", "python");
            var roots = new List<string>();

            string prefix = Environment.GetEnvironmentVariable("CONDA_PREFIX");
            if (!string.IsNullOrWhiteSpace(prefix)
                && string.Equals(Path.GetFileName(prefix.TrimEnd('/', '\\')), CondaEnvironment, StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(prefix, exe);
            }

            string condaExe = Environment.GetEnvironmentVariable("CONDA_EXE");
            if (!string.IsNullOrWhiteSpace(condaExe))
            {
                //<root>/bin/conda or <root>\Scripts\conda.exe (or condabin\conda.bat)
                string root = Path.GetDirectoryName(Path.GetDirectoryName(condaExe));
                if (!string.IsNullOrEmpty(root)) roots.Add(root);
            }
            foreach (string variable in new[] { "MAMBA_ROOT_PREFIX", "CONDA_ROOT" })
            {
                string root = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
            }

            string home = Environment.GetEnvironmentVariable(OnWindows ? "USERPROFILE" : "HOME");
            var installs = new[] { "miniconda3", "anaconda3", "miniforge3", "mambaforge", "micromamba", "Miniconda3", "Anaconda3", ".conda" };
            if (!string.IsNullOrEmpty(home))
            {
                foreach (string name in installs) roots.Add(Path.Combine(home, name));
            }
            if (OnWindows)
            {
                foreach (string variable in new[] { "LOCALAPPDATA", "ProgramData" })
                {
                    string folder = Environment.GetEnvironmentVariable(variable);
                    if (string.IsNullOrEmpty(folder)) continue;
                    foreach (string name in installs) roots.Add(Path.Combine(folder, name));
                }
            }
            else
            {
                roots.Add("/opt/conda");
                roots.Add("/opt/miniconda3");
                roots.Add("/opt/miniforge3");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots)
            {
                if (!seen.Add(root)) continue;
                yield return Path.Combine(root, "envs", CondaEnvironment, exe);
            }
        }

        /// <summary>
        /// The folder holding the <c>firedx</c> package: the user's setting, else the submodule
        /// <see cref="SubmodulePath"/> found upwards from the running program and the working directory. Null when neither.
        /// </summary>
        public static string FindPackage(out ToolPaths.Source source)
        {
            string setting = ToolPaths.UserSetting(ToolPaths.Tool.FireDx);
            if (setting != null)
            {
                source = ToolPaths.Source.UserSetting;
                return setting;
            }

            string[] relatives = { SubmodulePath, "Assets/ThirdParty/firedx", "ThirdParty/firedx" };
            foreach (string start in new[] { AssemblyDirectory(), SafeCurrentDirectory() })
            {
                string dir = start;
                for (int up = 0; up < 8 && !string.IsNullOrEmpty(dir); ++up, dir = Path.GetDirectoryName(dir))
                {
                    foreach (string relative in relatives)
                    {
                        string candidate = Path.Combine(dir, Path.Combine(relative.Split('/')));
                        if (File.Exists(Path.Combine(candidate, "firedx", "generate.py")))
                        {
                            source = ToolPaths.Source.Automatic;
                            return Path.GetFullPath(candidate);
                        }
                    }
                }
            }

            source = ToolPaths.Source.None;
            return null;
        }

        /// <summary>Why FireDX cannot be run on this machine, or null when it can be: no Python, or no FireDX source.</summary>
        public static string WhyNotAvailable()
        {
            if (FindPython(out _) == null)
            {
                return "No Python for FireDX: create FireDX's conda environment (conda env create -f environment.yml in the FireDX "
                       + "folder, which makes one named firedx) or " + ToolPaths.WhereToSet(ToolPaths.Tool.FireDxPython) + ".";
            }
            if (FindPackage(out _) == null)
            {
                return "No FireDX source: check out the submodule (git submodule update --init " + SubmodulePath + "; it is a "
                       + "private repository, so its author has to give you access) or " + ToolPaths.WhereToSet(ToolPaths.Tool.FireDx) + ".";
            }
            return null;
        }

        private static string AssemblyDirectory()
        {
            try
            {
                return Path.GetDirectoryName(typeof(FireDxRunner).Assembly.Location);
            }
            catch
            {
                return null;
            }
        }

        private static string SafeCurrentDirectory()
        {
            try
            {
                return Directory.GetCurrentDirectory();
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ which fuel FireDX runs on

        /// <summary>
        /// The FBFM40 raster to run FireDX on, absolute, or null and why not.
        /// </summary>
        /// <remarks>
        /// The scenario's own fuel source, <c>[ELMFIRE] FuelModelFile</c> - the LANDFIRE layer on LANDFIRE's grid - rather
        /// than the case's resampled <c>inputs/fbfm40.tif</c>: the source exists before any case does, it is what the
        /// building layers have to be warped from alongside it (the next build carries all six onto the case grid the
        /// same way), and a case grid changes whenever the domain, the padding or the cell size does. The case's own fuel
        /// is the fallback for a scenario that names none (a case brought in ready-made). FireDX's output, or a raster the
        /// roads were burned into, leads back to what it was made from, so running FireDX again does not run it on its own
        /// result (whose 256 cells it would not recognise as urban).
        /// </remarks>
        public static string ResolveFuelSource(string root, ElmfireInput e, string caseDirectory, out string note, out string problem)
        {
            note = null;
            problem = null;
            if (e == null)
            {
                problem = "This scenario has no ELMFIRE settings.";
                return null;
            }
            if (e.FuelModelStandard == ElmfireInput.FuelModelStandards.FBFM13)
            {
                problem = "FireDX splits FBFM40's urban fuel model 91 into buildings and roads; this scenario's fuel is Anderson 13 "
                          + "([ELMFIRE] FuelModelStandard = FBFM13), which has no such code.";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(e.FuelModelFile))
            {
                string path = PREACTInput.ResolvePath(root, e.FuelModelFile);
                if (!File.Exists(path))
                {
                    problem = "[ELMFIRE] FuelModelFile names " + e.FuelModelFile + ", which is not there.";
                    return null;
                }

                for (int depth = 0; depth < 4; ++depth)
                {
                    string recorded = RecordedSource(path);
                    if (recorded != null)
                    {
                        string earlier = PREACTInput.ResolvePath(root, recorded);
                        if (!File.Exists(earlier))
                        {
                            problem = $"{e.FuelModelFile} is FireDX's output from {recorded}, which is not there any more; name the "
                                      + "LANDFIRE fuel again ([ELMFIRE] FuelModelFile) or get LANDFIRE's fuels again.";
                            return null;
                        }
                        note = $"{e.FuelModelFile} is FireDX's own output, so FireDX runs on what it was made from, {recorded}.";
                        path = earlier;
                        continue;
                    }

                    string burnedFrom = RoadFuelRasterizer.BurnedFrom(path);
                    if (burnedFrom != null)
                    {
                        string earlier = PREACTInput.ResolvePath(root, burnedFrom);
                        if (File.Exists(earlier))
                        {
                            note = $"{Path.GetFileName(path)} has the roads burned in, so FireDX runs on the fuel it was burned from, {burnedFrom}.";
                            path = earlier;
                            continue;
                        }
                    }
                    break;
                }
                return Path.GetFullPath(path);
            }

            if (!string.IsNullOrEmpty(caseDirectory))
            {
                string caseFuel = ElmfireStems.Tif(Path.Combine(caseDirectory, "inputs"), "fbfm40");
                if (File.Exists(caseFuel))
                {
                    string burnedFrom = RoadFuelRasterizer.BurnedFrom(caseFuel);
                    string earlier = burnedFrom == null ? null : PREACTInput.ResolvePath(root, burnedFrom);
                    if (earlier != null && File.Exists(earlier))
                    {
                        note = $"The scenario names no fuel layer; the case's fbfm40.tif has the roads burned in, so FireDX runs on {burnedFrom}.";
                        return Path.GetFullPath(earlier);
                    }
                    note = "The scenario names no fuel layer ([ELMFIRE] FuelModelFile), so FireDX runs on the case's own fbfm40.tif, "
                           + "on the case's grid.";
                    return Path.GetFullPath(caseFuel);
                }
            }

            problem = "There is no FBFM40 fuel to run FireDX on: get LANDFIRE's fuels first (Fuels, canopy and buildings > Get them), "
                      + "or name a fuel raster as [ELMFIRE] FuelModelFile.";
            return null;
        }

        /// <summary>The fuel a FireDX output folder records it was made from (relative to the scenario), or null.</summary>
        private static string RecordedSource(string fuelPath)
        {
            try
            {
                if (!string.Equals(Path.GetFileName(fuelPath), Layers[0].File, StringComparison.OrdinalIgnoreCase)) return null;
                string provenance = Path.Combine(Path.GetDirectoryName(fuelPath), ProvenanceFileName);
                if (!File.Exists(provenance)) return null;
                foreach (string line in File.ReadAllLines(provenance))
                {
                    if (line.StartsWith("Fbfm40Source=", StringComparison.Ordinal))
                    {
                        string value = line.Substring("Fbfm40Source=".Length).Trim();
                        return value.Length > 0 ? value : null;
                    }
                }
            }
            catch
            {
                //no record
            }
            return null;
        }

        // ------------------------------------------------------------------ the command

        /// <summary>The driver's arguments after the script, in order.</summary>
        internal static List<string> Arguments(Options o, string input, string outputDirectory)
        {
            var args = new List<string>
            {
                "--fbfm40", input,
                "--output-dir", outputDirectory,
                "--attributes", o.Attributes.ToString().ToLowerInvariant(),
            };
            if (o.FireYear > 0)
            {
                args.Add("--fire-year");
                args.Add(o.FireYear.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrWhiteSpace(o.FootprintsPath))
            {
                args.Add("--footprints");
                args.Add(o.FootprintsPath);
            }
            return args;
        }

        /// <summary>
        /// The conda environment a Python belongs to: the folder above <c>bin</c> or <c>Scripts</c>, else its own folder
        /// (a Windows environment keeps python.exe at its root).
        /// </summary>
        internal static string EnvironmentRoot(string python)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(python));
            string name = Path.GetFileName(folder);
            if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Scripts", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(folder);
            }
            return folder;
        }

        /// <summary>
        /// The environment the driver runs with: FireDX's source and the version shim first on <c>PYTHONPATH</c>, the
        /// environment's own library folders first on <c>PATH</c> (what <c>conda activate</c> does, which a Python started
        /// directly does not get), and PROJ and GDAL pointed at the environment's own data - never at the ones this process
        /// set up for its own GDAL (QGIS's, SUMO's), whose proj.db does not match the environment's PROJ.
        /// </summary>
        internal static Dictionary<string, string> ChildEnvironment(string python, string package, string shim,
            IDictionary<string, string> current)
        {
            var env = new Dictionary<string, string>(StringComparer.Ordinal);
            string separator = Path.PathSeparator.ToString();
            string Get(string name) => current != null && current.TryGetValue(name, out string v) ? v : null;
            bool Exists(string folder)
            {
                try { return Directory.Exists(folder); } catch { return false; }
            }

            string existing = Get("PYTHONPATH");
            env["PYTHONPATH"] = package + separator + shim + (string.IsNullOrEmpty(existing) ? string.Empty : separator + existing);
            env["PYTHONUNBUFFERED"] = "1";
            env["PYTHONIOENCODING"] = "utf-8";
            //tqdm's bars, one line each instead of carriage returns.
            env["TQDM_MININTERVAL"] = "5";

            string root = EnvironmentRoot(python);
            var folders = new List<string>();
            if (OnWindows)
            {
                folders.Add(root);
                folders.Add(Path.Combine(root, "Library", "mingw-w64", "bin"));
                folders.Add(Path.Combine(root, "Library", "usr", "bin"));
                folders.Add(Path.Combine(root, "Library", "bin"));
                folders.Add(Path.Combine(root, "Scripts"));
            }
            folders.Add(Path.Combine(root, "bin"));
            string path = string.Join(separator, folders.Where(Exists));
            string currentPath = current?.FirstOrDefault(kv => string.Equals(kv.Key, "PATH", StringComparison.OrdinalIgnoreCase)).Value;
            env["PATH"] = path + (string.IsNullOrEmpty(currentPath) ? string.Empty : (path.Length > 0 ? separator : string.Empty) + currentPath);

            string proj = new[] { Path.Combine(root, "Library", "share", "proj"), Path.Combine(root, "share", "proj") }.FirstOrDefault(Exists);
            string gdal = new[] { Path.Combine(root, "Library", "share", "gdal"), Path.Combine(root, "share", "gdal") }.FirstOrDefault(Exists);
            //null removes the variable: the environment's libraries then find their own data.
            env["PROJ_DATA"] = proj;
            env["PROJ_LIB"] = proj;
            env["GDAL_DATA"] = gdal;
            env["GDAL_DRIVER_PATH"] = null;
            return env;
        }

        // ------------------------------------------------------------------ running

        /// <summary>
        /// Runs FireDX on <see cref="Options.Fbfm40Path"/>. Blocks; call it on a worker. Never throws for a FireDX failure:
        /// <see cref="Result.Ok"/> and <see cref="Result.Message"/> say what happened, and <c>firedx.log</c> holds all of it.
        /// </summary>
        public static Result Run(Options o)
        {
            var result = new Result();
            Action<string> log = o.Log ?? (m => Engine.Message(null, Engine.LogType.Log, m));

            string python = string.IsNullOrWhiteSpace(o.Python) ? FindPython(out _) : o.Python;
            if (string.IsNullOrEmpty(python) || !File.Exists(python))
            {
                result.Message = string.IsNullOrWhiteSpace(o.Python)
                    ? "No Python for FireDX was found (looked for " + PythonSearchDescription + "). Create FireDX's conda environment "
                      + "(conda env create -f environment.yml in the FireDX folder) or " + ToolPaths.WhereToSet(ToolPaths.Tool.FireDxPython) + "."
                    : "The Python named for FireDX, " + o.Python + ", is not there.";
                return result;
            }

            string package = string.IsNullOrWhiteSpace(o.Package) ? FindPackage(out _) : o.Package;
            if (string.IsNullOrEmpty(package) || !File.Exists(Path.Combine(package, "firedx", "generate.py")))
            {
                result.Message = (string.IsNullOrWhiteSpace(o.Package) ? "No FireDX source was found" : o.Package + " holds no firedx/generate.py")
                                 + ". Check out the submodule (git submodule update --init " + SubmodulePath + "; a private repository, "
                                 + "so its author has to give you access) or " + ToolPaths.WhereToSet(ToolPaths.Tool.FireDx) + ".";
                return result;
            }

            if (string.IsNullOrEmpty(o.Fbfm40Path) || !File.Exists(o.Fbfm40Path))
            {
                result.Message = "No FBFM40 raster to run FireDX on" + (string.IsNullOrEmpty(o.Fbfm40Path) ? "." : ": " + o.Fbfm40Path + " is not there.");
                return result;
            }
            if (!string.IsNullOrWhiteSpace(o.FootprintsPath) && !File.Exists(o.FootprintsPath))
            {
                result.Message = "The footprints file " + o.FootprintsPath + " is not there.";
                return result;
            }

            string output = !string.IsNullOrEmpty(o.OutputDirectory)
                ? o.OutputDirectory
                : Path.Combine(o.Root ?? ".", Folder.Replace('/', Path.DirectorySeparatorChar));
            output = Path.GetFullPath(output);
            Directory.CreateDirectory(output);

            //Made in a folder of its own and moved into place only when complete, so a run that fails leaves the layers the
            //scenario names from an earlier run as they were.
            string work = Path.Combine(output, "run");
            if (Directory.Exists(work)) Directory.Delete(work, true);
            Directory.CreateDirectory(work);

            result.LogFile = Path.Combine(output, LogFileName);
            using (var file = new StreamWriter(result.LogFile, false, new UTF8Encoding(false)) { AutoFlush = true })
            {
                void Note(string line)
                {
                    lock (file) file.WriteLine(line);
                }

                Note("# FireDX run by PREACT, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                Note("# Python:  " + python);
                Note("# FireDX:  " + package);
                Note("# FBFM40:  " + o.Fbfm40Path);

                string input = Path.Combine(work, InputFileName);
                string inputProblem = PrepareInput(o.Fbfm40Path, input, out long urban);
                if (inputProblem != null)
                {
                    result.Message = inputProblem;
                    Note("# " + inputProblem);
                    return result;
                }
                result.UrbanCellsBefore = urban;
                if (urban == 0)
                {
                    log("FireDX: the fuel has no urban cells (FBFM40 91); FireDX will find no buildings on it to place.");
                }

                string script = Path.Combine(output, ScriptFileName);
                WriteScript(script);
                string shim = WriteVersionShim(output);

                List<string> arguments = Arguments(o, input, work);
                Note("# Command: " + python + " -u " + script + " " + string.Join(" ", arguments.Select(QuoteForLog)));
                log("FireDX: " + python + " (attributes: " + o.Attributes.ToString().ToLowerInvariant()
                    + (o.FireYear > 0 ? ", fire year " + o.FireYear.ToString(CultureInfo.InvariantCulture) : string.Empty)
                    + (string.IsNullOrWhiteSpace(o.FootprintsPath) ? ", footprints downloaded" : ", footprints from " + o.FootprintsPath) + ")");

                var psi = new ProcessStartInfo
                {
                    FileName = python,
                    WorkingDirectory = work,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(script);
                foreach (string a in arguments) psi.ArgumentList.Add(a);

                var current = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (System.Collections.DictionaryEntry kv in System.Environment.GetEnvironmentVariables())
                {
                    current[(string)kv.Key] = (string)kv.Value;
                }
                foreach (KeyValuePair<string, string> kv in ChildEnvironment(python, package, shim, current))
                {
                    //The variable as this process spells it (Windows has "Path"), so it is replaced rather than doubled.
                    string key = psi.Environment.Keys.FirstOrDefault(k => string.Equals(k, kv.Key,
                        OnWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) ?? kv.Key;
                    if (kv.Value == null) psi.Environment.Remove(key);
                    else psi.Environment[key] = kv.Value;
                }

                var tail = new Queue<string>();
                string error = null;
                string done = null;
                string table = null;

                void Line(string raw)
                {
                    if (raw == null) return;
                    //tqdm redraws a bar with carriage returns: only its last state is worth a line.
                    int cr = raw.LastIndexOf('\r');
                    string line = (cr >= 0 ? raw.Substring(cr + 1) : raw).TrimEnd();
                    if (line.Length == 0) return;
                    Note(line);
                    lock (tail)
                    {
                        tail.Enqueue(line);
                        while (tail.Count > 8) tail.Dequeue();
                    }

                    if (line.StartsWith(Tag, StringComparison.Ordinal))
                    {
                        int colon = line.IndexOf(':');
                        string key = colon > 0 ? line.Substring(Tag.Length, colon - Tag.Length) : line.Substring(Tag.Length);
                        string value = colon > 0 ? line.Substring(colon + 1).Trim() : string.Empty;
                        lock (result)
                        {
                            switch (key)
                            {
                                case "MISSING-MODULES": result.MissingModules.AddRange(value.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries)); break;
                                case "OFFLINE": result.Unreachable.Add(value); break;
                                case "ATTRIBUTES": result.AttributesUsed = value; break;
                                case "ERROR": case "NOT-CALIFORNIA": case "NO-BUILDINGS": case "NO-PACKAGE": error = value; break;
                                case "TABLE": table = value; break;
                                case "DONE": done = value; break;
                            }
                        }
                        log("FireDX: " + line.Substring(Tag.Length));
                    }
                    else
                    {
                        log("  " + line);
                    }
                }

                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (_, e) => Line(e.Data);
                    p.ErrorDataReceived += (_, e) => Line(e.Data);
                    try
                    {
                        p.Start();
                    }
                    catch (Exception e)
                    {
                        result.Message = "Could not start " + python + ": " + e.Message;
                        Note("# " + result.Message);
                        return result;
                    }

                    ElmfireProcesses.Register(p);
                    using (o.Cancellation.Register(() =>
                           {
                               result.Stopped = true;
                               ElmfireProcesses.KillTree(p);
                           }))
                    {
                        try
                        {
                            p.BeginOutputReadLine();
                            p.BeginErrorReadLine();
                            p.WaitForExit();
                        }
                        finally
                        {
                            ElmfireProcesses.Unregister(p);
                        }
                    }
                    result.ExitCode = p.ExitCode;
                }

                Note("# Exit code " + result.ExitCode.ToString(CultureInfo.InvariantCulture));
                if (result.Stopped || o.Cancellation.IsCancellationRequested)
                {
                    result.Stopped = true;
                    result.Message = "FireDX was stopped; the scenario's layers were not changed.";
                    return result;
                }

                if (result.ExitCode != 0 || done == null)
                {
                    string lastLines;
                    lock (tail) lastLines = string.Join(" | ", tail);
                    result.Message = Explain(result, error, lastLines, o) + " The whole log is in " + result.LogFile + ".";
                    return result;
                }

                var missing = Layers.Where(l => !File.Exists(Path.Combine(work, l.File))).Select(l => l.File).ToList();
                if (missing.Count > 0)
                {
                    result.Message = "FireDX finished without writing " + string.Join(", ", missing) + ". The whole log is in " + result.LogFile + ".";
                    return result;
                }

                //Into place: everything the run made, over an earlier run's files.
                foreach (string made in Directory.GetFiles(work))
                {
                    string target = Path.Combine(output, Path.GetFileName(made));
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(made, target);
                }
                try { Directory.Delete(work, true); } catch { }

                foreach (Layer layer in Layers)
                {
                    result.Layers[layer.Key] = Relative(o.Root, Path.Combine(output, layer.File));
                }
                string tableInPlace = Path.Combine(output, BuildingTableFileName);
                result.BuildingTable = File.Exists(tableInPlace) ? tableInPlace : null;
                if (table == null) result.BuildingTable = null;

                CountCells(Path.Combine(output, Layers[0].File), out result.BuildingCells, out result.RoadCells);
                result.ProvenanceFile = WriteProvenance(output, o, python, package, result);
                result.RemovedCaseLayers.AddRange(RemoveSupersededCaseLayers(o.CaseDirectory, result.BuildingTable, log));

                result.Ok = true;
                result.Message = $"FireDX ({result.AttributesUsed ?? "?"} attributes): of the fuel's {result.UrbanCellsBefore} urban cells (91), "
                                 + $"{result.BuildingCells} hold buildings and stay 91, and {result.RoadCells} are pavement and roads, now 256 "
                                 + "(non-burnable to ELMFIRE).";
                Note("# " + result.Message);
                return result;
            }
        }

        /// <summary>What a failed run means, for a user.</summary>
        private static string Explain(Result r, string error, string lastLines, Options o)
        {
            switch (r.ExitCode)
            {
                case 3:
                    return "FireDX's Python environment lacks " + string.Join(", ", r.MissingModules) + ". Create FireDX's own environment "
                           + "(conda env create -f environment.yml in the FireDX folder) and name its python under Help > External tools "
                           + "and keys, or install them into this one (conda install -c conda-forge " + string.Join(" ", r.MissingModules) + ").";
                case 4:
                {
                    bool footprints = string.IsNullOrWhiteSpace(o.FootprintsPath);
                    return "FireDX needs the network here and could not reach " + string.Join("; ", r.Unreachable) + ". "
                           + (footprints ? "Building footprints are downloaded (Microsoft's global footprints and OpenStreetMap) unless a "
                                           + "footprints file is given. " : string.Empty)
                           + (r.AttributesUsed == "california"
                               ? "FireDX's own attribute join asks the USACE National Structure Inventory and CAL FIRE's hazard zones; "
                                 + "the basic attribute path needs neither. "
                               : string.Empty)
                           + "Check the connection (a proxy or firewall)"
                           + (footprints ? ", or give a footprints file" : string.Empty)
                           + (r.AttributesUsed == "california" ? (footprints ? " and choose" : ", or choose") + " the basic attributes." : ".");
                }
                case 5:
                    return "FireDX could not be imported from " + (o.Package ?? "its folder") + ": " + (error ?? lastLines);
                case 6:
                    return "FireDX's own attribute join only has data in California (the USACE National Structure Inventory covers the "
                           + "USA, CAL FIRE's fire hazard severity zones California): " + (error ?? string.Empty) + ". Use the basic "
                           + "attribute path, which takes every building as residential and works anywhere.";
                case 7:
                    return "FireDX found no buildings: " + (error ?? "none in the area") + ".";
                case 2:
                    return "FireDX refused its input: " + (error ?? lastLines) + ".";
                default:
                    return "FireDX failed (exit code " + r.ExitCode.ToString(CultureInfo.InvariantCulture) + "): " + (error ?? lastLines) + ".";
            }
        }

        /// <summary>
        /// Copies the fuel as FireDX can take it: one Int16 band, nodata -9999, untiled. FireDX writes its own rasters that
        /// way and refuses to continue when the fuel's raster profile differs from them in anything but compression, type and
        /// strip height ("Raster profiles do not match!"), which a tiled LANDFIRE extract or an Int32 band would. Also
        /// counts the urban cells. Null when done, otherwise why not.
        /// </summary>
        internal static string PrepareInput(string source, string destination, out long urban)
        {
            urban = 0;
            try
            {
                Gdal.AllRegister();
                using (Dataset ds = Gdal.Open(source, Access.GA_ReadOnly))
                {
                    if (ds == null) return "Could not open " + source + ": " + Gdal.GetLastErrorMsg();
                    using (var srs = new OSGeo.OSR.SpatialReference(ds.GetProjection()))
                    {
                        if (string.IsNullOrWhiteSpace(ds.GetProjection()) || srs.IsProjected() != 1)
                        {
                            return source + " is not in a projected coordinate system; FireDX measures building areas and separations "
                                   + "in the fuel's own units, which have to be metres.";
                        }
                    }

                    string[] args = { "-of", "GTiff", "-b", "1", "-ot", "Int16", "-a_nodata", "-9999", "-co", "TILED=NO" };
                    using (var options = new GDALTranslateOptions(args))
                    using (Dataset written = Gdal.wrapper_GDALTranslate(destination, ds, options, null, null))
                    {
                        if (written == null) return "Could not copy " + source + " for FireDX: " + Gdal.GetLastErrorMsg();
                        written.FlushCache();
                    }
                }

                float[,] fuel = AscRaster.ReadGeoTiff(destination, out AscRaster.Header _, out bool ok);
                if (!ok || fuel == null) return "Could not read back " + destination;
                foreach (float v in fuel)
                {
                    if (v == 91f) ++urban;
                }
                return null;
            }
            catch (Exception e)
            {
                return "Could not prepare " + source + " for FireDX: " + e.Message;
            }
        }

        /// <summary>Cells of a fuel raster that are 91 (buildings, after FireDX) and 256 (pavement and roads).</summary>
        internal static void CountCells(string fuelPath, out long buildings, out long roads)
        {
            buildings = 0;
            roads = 0;
            float[,] fuel = AscRaster.ReadGeoTiff(fuelPath, out AscRaster.Header _, out bool ok);
            if (!ok || fuel == null) return;
            foreach (float v in fuel)
            {
                if (v == 91f) ++buildings;
                else if (v == 256f) ++roads;
            }
        }

        /// <summary>Writes the driver, PREACT's own <c>FireDxBootstrap.py</c> (an embedded resource).</summary>
        internal static void WriteScript(string path)
        {
            File.WriteAllText(path, Script(), new UTF8Encoding(false));
        }

        /// <summary>The driver's text.</summary>
        internal static string Script()
        {
            using (Stream stream = typeof(FireDxRunner).Assembly.GetManifestResourceStream("PREACT.Utility.FireDxBootstrap.py"))
            {
                if (stream == null) throw new InvalidOperationException("PREACTcore was built without its FireDX driver (FireDxBootstrap.py).");
                using (var reader = new StreamReader(stream, Encoding.UTF8)) return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// FireDX's <c>__init__</c> asks the installed package's metadata for its version, which a package run from its
        /// source folder does not have ("No package metadata was found for firedx"). A one-file dist-info beside the output,
        /// on PYTHONPATH after the source, answers it - so the submodule runs as it is, without a pip install. Returns the
        /// folder that goes on PYTHONPATH.
        /// </summary>
        internal static string WriteVersionShim(string output)
        {
            string shim = Path.Combine(output, ".pyshim");
            string folder = Path.Combine(shim, "firedx-0.0.0+preact.dist-info");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "METADATA"), "Metadata-Version: 2.1\nName: firedx\nVersion: 0.0.0+preact\n", new UTF8Encoding(false));
            return shim;
        }

        private static string WriteProvenance(string output, Options o, string python, string package, Result r)
        {
            string path = Path.Combine(output, ProvenanceFileName);
            var lines = new List<string>
            {
                "# What FireDX made these rasters from, written by PREACT's FireDX step.",
                "Fbfm40Source=" + Relative(o.Root, o.Fbfm40Path),
                "Attributes=" + (r.AttributesUsed ?? o.Attributes.ToString().ToLowerInvariant()),
                "FireYear=" + (o.FireYear > 0 ? o.FireYear.ToString(CultureInfo.InvariantCulture) : "none"),
                "Footprints=" + (string.IsNullOrWhiteSpace(o.FootprintsPath) ? "downloaded (Microsoft global ML footprints, OpenStreetMap)" : o.FootprintsPath),
                "Python=" + python,
                "FireDX=" + package,
                "Made=" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                "UrbanCellsBefore=" + r.UrbanCellsBefore.ToString(CultureInfo.InvariantCulture),
                "BuildingCells=" + r.BuildingCells.ToString(CultureInfo.InvariantCulture),
                "RoadCells=" + r.RoadCells.ToString(CultureInfo.InvariantCulture),
                "BuildingAreaUnit=m (baa_m.tif: the square root of the mean footprint area, ELMFIRE's building plan dimension)",
            };
            File.WriteAllLines(path, lines);
            return path;
        }

        // ------------------------------------------------------------------ into the scenario and the case

        /// <summary>
        /// Names the run's rasters as the scenario's ELMFIRE source layers - the fuel and the five building layers - and
        /// switches the building spread model on, which ELMFIRE then gets when the case has all five. On the thread that owns
        /// the scenario.
        /// </summary>
        public static void Apply(Result r, ElmfireInput e)
        {
            if (r == null || !r.Ok) throw new InvalidOperationException("Only a FireDX run that succeeded can be applied.");
            e.FuelModelFile = r.Layers[nameof(ElmfireInput.FuelModelFile)];
            e.FuelModelStandard = ElmfireInput.FuelModelStandards.FBFM40;
            e.BuildingAreaFile = r.Layers[nameof(ElmfireInput.BuildingAreaFile)];
            e.BuildingSeparationFile = r.Layers[nameof(ElmfireInput.BuildingSeparationFile)];
            e.BuildingNonBurnableFractionFile = r.Layers[nameof(ElmfireInput.BuildingNonBurnableFractionFile)];
            e.BuildingFootprintFractionFile = r.Layers[nameof(ElmfireInput.BuildingFootprintFractionFile)];
            e.BuildingFuelModelFile = r.Layers[nameof(ElmfireInput.BuildingFuelModelFile)];
            e.Namelist.USE_BLDG_SPREAD_MODEL = true;
        }

        /// <summary>
        /// Moves the case's fuel and building layers out of the way of FireDX's - into
        /// <c>inputs/_replaced/&lt;stem&gt;.before-firedx.tif</c> - so the next build warps the new ones instead of keeping the
        /// old, and puts FireDX's building fuel model table into the case's inputs (its codes are FireDX's, not those of
        /// ELMFIRE's default table), keeping a table that was there beside the moved layers. Returns the stems moved.
        /// </summary>
        public static List<string> RemoveSupersededCaseLayers(string caseDirectory, string buildingTable, Action<string> log)
        {
            var moved = new List<string>();
            if (string.IsNullOrEmpty(caseDirectory)) return moved;
            string inputs = Path.Combine(caseDirectory, "inputs");
            if (!Directory.Exists(inputs)) return moved;

            foreach (Layer layer in Layers)
            {
                string tif = ElmfireStems.Tif(inputs, layer.Stem);
                if (!File.Exists(tif)) continue;
                try
                {
                    string aside = ElmfireCaseBuilder.ReplacedPath(inputs, layer.Stem, "before-firedx");
                    Directory.CreateDirectory(Path.GetDirectoryName(aside));
                    File.Move(tif, aside);
                    if (File.Exists(tif + ".aux.xml"))
                    {
                        try { File.Move(tif + ".aux.xml", aside + ".aux.xml"); } catch { }
                    }
                    foreach (string extension in new[] { ".bsq", ".hdr", ".xml" })
                    {
                        string companion = Path.Combine(inputs, layer.Stem + extension);
                        try { if (File.Exists(companion)) File.Delete(companion); } catch { }
                    }
                    moved.Add(layer.Stem);
                }
                catch (Exception e)
                {
                    log?.Invoke($"WARNING could not move the case's {layer.Stem}.tif aside ({e.Message}), so the next build keeps it; "
                                + "rebuild the fire case from scratch to use FireDX's.");
                }
            }

            if (!string.IsNullOrEmpty(buildingTable) && File.Exists(buildingTable))
            {
                string table = Path.Combine(inputs, ElmfireStems.BuildingFuelModelTable);
                try
                {
                    if (File.Exists(table) && !SameContents(table, buildingTable))
                    {
                        string aside = Path.Combine(inputs, ElmfireCaseBuilder.ReplacedFolder,
                            Path.GetFileNameWithoutExtension(table) + ".before-firedx" + Path.GetExtension(table));
                        Directory.CreateDirectory(Path.GetDirectoryName(aside));
                        if (File.Exists(aside)) File.Delete(aside);
                        File.Move(table, aside);
                    }
                    File.Copy(buildingTable, table, true);
                }
                catch (Exception e)
                {
                    log?.Invoke($"WARNING could not put FireDX's {ElmfireStems.BuildingFuelModelTable} into the case ({e.Message}).");
                }
            }

            if (moved.Count > 0)
            {
                log?.Invoke($"The fire case's {string.Join(", ", moved)} were moved to inputs/{ElmfireCaseBuilder.ReplacedFolder}, so the next "
                            + "build of the fire case warps FireDX's layers instead; its terrain and weather are kept. Save the scenario to "
                            + "keep it naming them.");
            }
            return moved;
        }

        private static bool SameContents(string a, string b)
        {
            try
            {
                return File.ReadAllText(a).Replace("\r\n", "\n") == File.ReadAllText(b).Replace("\r\n", "\n");
            }
            catch
            {
                return false;
            }
        }

        private static string QuoteForLog(string s)
        {
            return s.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + s.Replace("\"", "\\\"") + "\"" : s;
        }

        private static string Relative(string root, string path)
        {
            if (string.IsNullOrEmpty(root)) return path.Replace('\\', '/');
            try
            {
                string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
                return relative.Replace('\\', '/');
            }
            catch (Exception)
            {
                return path.Replace('\\', '/');
            }
        }
    }
}
