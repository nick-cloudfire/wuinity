using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PREACT.Input;
using PREACT.Math;

namespace PREACT.Tools
{
    /// <summary>
    /// LANDFIRE fuel and canopy for a scenario, start to finish: which release, the area the case grid needs, the LFPS
    /// job, the split into source layers, and the scenario keys that name them. One function for the GUI's fuels step
    /// and the command line, so the two cannot download different things for the same <c>.wui</c>.
    /// </summary>
    /// <remarks>
    /// The area asked for is the case's padded domain (the evacuation domain plus <c>[ELMFIRE] PaddingMetres</c>), grown
    /// so the UTM grid cut from it is covered to its corners. It used to be the unpadded domain, which left the fire's
    /// whole 2 km margin without fuel.
    /// </remarks>
    public static class LandfireFuels
    {
        /// <summary>Where the layers go, relative to the scenario.</summary>
        public const string Folder = "downloads/landfire";

        /// <summary>The case stems a LANDFIRE download replaces.</summary>
        public static readonly string[] Stems = { "fbfm40", "fbfm13", "cc", "ch", "cbh", "cbd" };

        public sealed class Options
        {
            public string Root;
            public string Name;
            public Vector2d LowerLeft;
            public Vector2d DomainSize;
            public int ScenarioYear;
            public double PaddingMetres = 2000.0;

            /// <summary>The case folder, absolute; its superseded fuel and canopy layers are removed after a download.</summary>
            public string CaseDirectory;

            /// <summary><c>[ELMFIRE] LandfireVersion</c>: closest, or a release.</summary>
            public string Version = LandfireVersions.Closest;
            public bool Anderson13;

            /// <summary>For this run only; otherwise <see cref="LandfireContact.Resolve"/> finds it.</summary>
            public string Email;

            /// <summary>Whether a template namelist decides the scaling flags rather than [ElmfireNamelist].</summary>
            public string NamelistTemplate;

            public string ServiceUrl = LandfireLandscapeDownloader.DefaultServiceUrl;
            public TimeSpan PollInterval = TimeSpan.FromSeconds(20);
            public TimeSpan MaxWait = TimeSpan.FromMinutes(30);
            public TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);
            public Action<string> Log;
            public CancellationToken Cancellation;

            /// <summary>The scenario's own settings; call on the thread that owns the scenario.</summary>
            public static Options FromScenario(PREACTInput input)
            {
                ElmfireInput e = input.WildfireModule.ElmfireInput;
                return new Options
                {
                    Root = input.RootFolder,
                    Name = input.Simulation.Name,
                    LowerLeft = input.Simulation.LowerLeftLatLon,
                    DomainSize = input.Simulation.DomainSize,
                    ScenarioYear = input.Simulation.StartDateTime.Year,
                    PaddingMetres = e.PaddingMetres,
                    CaseDirectory = PREACT.Utility.ElmfireCoupling.CaseDirectoryPath(input.RootFolder, e),
                    Version = e.LandfireVersion,
                    Anderson13 = e.FuelModelStandard == ElmfireInput.FuelModelStandards.FBFM13,
                    NamelistTemplate = e.NamelistTemplate,
                };
            }
        }

        public sealed class Result
        {
            /// <summary>The release the layers are from (<c>LF2024</c>).</summary>
            public string Release;

            /// <summary>ELMFIRE stem to the layer's path relative to the scenario, forward slashes.</summary>
            public readonly Dictionary<string, string> Layers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string FuelStem;
            public LandfireLayers.Scaling Scaling;

            /// <summary>The record of what was asked for and what came back, relative to the scenario.</summary>
            public string ProvenanceFile;

            /// <summary>Case layers removed because these replace them.</summary>
            public readonly List<string> RemovedCaseLayers = new List<string>();

            public readonly List<string> Warnings = new List<string>();
        }

        /// <summary>
        /// The area to ask LANDFIRE for: the case's padded domain, grown to cover the UTM grid cut from it plus a few
        /// cells. Also the zone that grid is in, which is the zone the layers are delivered in.
        /// </summary>
        public static (Vector2d LowerLeft, Vector2d UpperRight, int UtmEpsg) RequestArea(Vector2d lowerLeft, Vector2d domainSize,
            double paddingMetres)
        {
            var o = new PREACT.Utility.ElmfireCaseBuilder.Options
            {
                LowerLeftLatLon = lowerLeft,
                DomainSizeMetres = domainSize,
                PaddingMetres = paddingMetres,
            };
            (Vector2d southWest, Vector2d northEast) = PREACT.Utility.ElmfireCaseBuilder.PaddedBounds(o);

            //The zone the case builder cuts its grid in: the padded box's centre.
            int epsg = PREACT.Utility.UtmUtility.GetUtmEpsgCode(0.5 * (southWest.x + northEast.x), 0.5 * (southWest.y + northEast.y));

            //LANDFIRE's 30 m cells are about 0.0003 degrees: five of them for LFPS's own snapping and the warp's kernel.
            (Vector2d ll, Vector2d ur) = DownloadArea.CoverUtmGrid(southWest, northEast, epsg, 0.0015);
            return (ll, ur, epsg);
        }

        /// <summary>Downloads and splits. Throws, saying why, when there is no usable fuel model at the end of it.</summary>
        public static async Task<Result> DownloadAsync(Options o)
        {
            Action<string> log = o.Log ?? (m => Engine.Message(null, Engine.LogType.Log, m));
            if (o.DomainSize.x <= 0.0 || o.DomainSize.y <= 0.0)
            {
                throw new ArgumentException("The scenario has no area of interest.");
            }

            (Vector2d ll, Vector2d ur, int epsg) = RequestArea(o.LowerLeft, o.DomainSize, o.PaddingMetres);
            LandfireVersions.Region region = LandfireVersions.RegionOf(ll, ur);
            if (region == LandfireVersions.Region.None)
            {
                throw new Exception("LANDFIRE covers the United States only, and this domain is outside it. Name a fuel "
                    + "model raster of your own, and the FIRE-RES canopy folder for Europe, instead.");
            }

            LandfireVersions.Release release = LandfireVersions.Resolve(o.Version, o.ScenarioYear, region, out string why);
            log("LANDFIRE release: " + why);

            string email = LandfireContact.Resolve(o.Email, out string emailSource);
            if (email == null)
            {
                throw new Exception(LandfireContact.MissingMessage);
            }
            log("Contact e-mail for LFPS from " + emailSource + ".");

            string folder = Path.Combine(o.Root, Folder.Replace('/', Path.DirectorySeparatorChar));
            var request = new LandfireLandscapeDownloader.Request
            {
                LowerLeft = ll,
                UpperRight = ur,
                LayerList = LandfireVersions.LayerList(release, o.Anderson13),
                OutputEpsg = epsg,
                Email = email,
                DownloadFolder = folder,
                ServiceUrl = o.ServiceUrl,
                PollInterval = o.PollInterval,
                MaxWait = o.MaxWait,
                FirstBackoff = o.FirstBackoff,
                Log = log,
                Cancellation = o.Cancellation,
            };
            log($"The area asked for is the case's domain padded by {o.PaddingMetres:F0} m, and a margin so its UTM grid "
                + "is covered to the corners.");
            LandfireLandscapeDownloader.Result downloaded = await LandfireLandscapeDownloader.DownloadAsync(request).ConfigureAwait(false);

            log("Splitting " + Path.GetFileName(downloaded.RasterPath) + " into the layers ELMFIRE takes...");
            LandfireLayers.Split split = LandfireLayers.SplitLayers(downloaded.RasterPath, folder, o.Name, o.Anderson13, log);
            if (!string.IsNullOrEmpty(split.Release) && split.Release != release.Name)
            {
                split.Warnings.Add($"{release.Name} was asked for and the fuel band is {split.Release}");
            }

            var result = new Result
            {
                Release = string.IsNullOrEmpty(split.Release) ? release.Name : split.Release,
                FuelStem = split.FuelStem,
                Scaling = split.Scaling,
            };
            foreach (KeyValuePair<string, string> layer in split.Layers)
            {
                result.Layers[layer.Key] = Relative(o.Root, layer.Value);
            }
            result.Warnings.AddRange(split.Warnings);

            if (!string.IsNullOrWhiteSpace(o.NamelistTemplate))
            {
                result.Warnings.Add($"the scenario runs the namelist template {o.NamelistTemplate} as it is, so its own "
                    + "CC_IN_PERCENT, CH_TIMES_10, CBH_TIMES_10 and CBD_TIMES_100 decide; for LANDFIRE's canopy they must be "
                    + $"{Bool(split.Scaling.CcInPercent)}, {Bool(split.Scaling.ChTimes10)}, {Bool(split.Scaling.CbhTimes10)} "
                    + $"and {Bool(split.Scaling.CbdTimes100)}");
            }

            result.ProvenanceFile = Relative(o.Root, WriteProvenance(folder, o, release, why, request, downloaded, split));

            //The case keeps a layer it already has, so without this the next build would go on using the old fuel and
            //canopy whatever these are - a release switched for a historic fire would change nothing.
            RemoveSupersededCaseLayers(o.CaseDirectory, result, log);

            foreach (string warning in result.Warnings) log("WARNING " + warning + ".");
            return result;
        }

        /// <summary>
        /// Names the layers as the scenario's ELMFIRE source layers and sets the canopy scaling flags their units call for.
        /// On the thread that owns the scenario.
        /// </summary>
        public static void Apply(Result result, ElmfireInput e)
        {
            e.FuelModelFile = result.Layers[result.FuelStem];
            e.FuelModelStandard = result.FuelStem == "fbfm13" ? ElmfireInput.FuelModelStandards.FBFM13 : ElmfireInput.FuelModelStandards.FBFM40;
            if (result.Layers.TryGetValue("cc", out string cc)) e.CanopyCoverFile = cc;
            if (result.Layers.TryGetValue("ch", out string ch)) e.CanopyHeightFile = ch;
            if (result.Layers.TryGetValue("cbh", out string cbh)) e.CanopyBaseHeightFile = cbh;
            if (result.Layers.TryGetValue("cbd", out string cbd)) e.CanopyBulkDensityFile = cbd;

            e.Namelist.CC_IN_PERCENT = result.Scaling.CcInPercent;
            e.Namelist.CH_TIMES_10 = result.Scaling.ChTimes10;
            e.Namelist.CBH_TIMES_10 = result.Scaling.CbhTimes10;
            e.Namelist.CBD_TIMES_100 = result.Scaling.CbdTimes100;
        }

        private static void RemoveSupersededCaseLayers(string caseDirectory, Result result, Action<string> log)
        {
            if (string.IsNullOrEmpty(caseDirectory)) return;
            string inputs = Path.Combine(caseDirectory, "inputs");
            if (!Directory.Exists(inputs)) return;

            foreach (string stem in Stems)
            {
                string tif = Path.Combine(inputs, stem + ".tif");
                if (!File.Exists(tif)) continue;
                try
                {
                    File.Delete(tif);
                    //What ELMFIRE and GDAL leave beside a raster; a stale .bsq would be read under USE_EXISTING_BSQS.
                    foreach (string extension in new[] { ".tif.aux.xml", ".bsq", ".hdr", ".xml" })
                    {
                        string companion = Path.Combine(inputs, stem + extension);
                        if (File.Exists(companion)) File.Delete(companion);
                    }
                    result.RemovedCaseLayers.Add(stem);
                }
                catch (Exception e)
                {
                    result.Warnings.Add($"could not remove the case's old {stem}.tif ({e.Message}), so the next build keeps it; "
                        + "rebuild the fire case from scratch to use the new layer");
                }
            }

            if (result.RemovedCaseLayers.Count > 0)
            {
                log($"The fire case's {string.Join(", ", result.RemovedCaseLayers)} came from the previous fuel source and were "
                    + "removed, so the next build of the fire case warps these instead; its terrain and weather are kept.");
            }
        }

        private static string WriteProvenance(string folder, Options o, LandfireVersions.Release release, string why,
            LandfireLandscapeDownloader.Request request, LandfireLandscapeDownloader.Result downloaded, LandfireLayers.Split split)
        {
            string path = Path.Combine(folder, $"{o.Name}_{(string.IsNullOrEmpty(split.Release) ? release.Name : split.Release)}_landfire.txt");
            var lines = new List<string>
            {
                "# Where this scenario's LANDFIRE fuel and canopy came from, written by the download.",
                "Release=" + (string.IsNullOrEmpty(split.Release) ? release.Name : split.Release),
                "Requested=" + LandfireVersions.Normalise(o.Version) + $" (scenario year {o.ScenarioYear})",
                "Why=" + why,
                "Downloaded=" + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                "LayerList=" + request.LayerList,
                FormattableString.Invariant($"AreaOfInterest={request.LowerLeft.y:R} {request.LowerLeft.x:R} {request.UpperRight.y:R} {request.UpperRight.x:R}"),
                "OutputProjection=EPSG:" + request.OutputEpsg.ToString(CultureInfo.InvariantCulture),
                "JobId=" + downloaded.JobId,
                "Zip=" + Path.GetFileName(downloaded.ZipPath),
                "Raster=" + Relative(o.Root, downloaded.RasterPath),
                "CC_IN_PERCENT=" + Bool(split.Scaling.CcInPercent),
                "CH_TIMES_10=" + Bool(split.Scaling.ChTimes10),
                "CBH_TIMES_10=" + Bool(split.Scaling.CbhTimes10),
                "CBD_TIMES_100=" + Bool(split.Scaling.CbdTimes100),
                "",
                "# stem = layer file : LFPS band (unit)",
            };
            foreach (KeyValuePair<string, string> layer in split.Layers)
            {
                LandfireLayers.Band band = split.Sources[layer.Key];
                lines.Add($"{layer.Key}={Relative(o.Root, layer.Value)} : band {band.Index} {band.Description} ({band.Unit})");
            }
            File.WriteAllLines(path, lines);
            return path;
        }

        private static string Bool(bool b) => b ? ".TRUE." : ".FALSE.";

        private static string Relative(string root, string path)
        {
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
