using PREACT.Math;
using System.Collections.Generic;
using PREACT.Input;
using PREACT.Pedestrian;
using PREACT.Wildfire;
using PREACT.Traffic;
using System.Diagnostics;
using System.IO;

namespace PREACT.Evacuation
{
    public class EvacuationManager
    {
        Simulation _simulation;
        private TrafficModule _trafficModule;
        private PedestrianModule _pedestrianModule;
        private TriggerBufferModule _triggerBufferModule;


        private Stopwatch _pathfindingStopwatch = new Stopwatch();
        private Stopwatch _roadClosureStopwatch = new Stopwatch();


        PREACTInput _input;
        EvacuationGroup _defaultEvacuationGroup;        
        Dictionary<string, EvacuationDestination> _evacuationDestinationsDict;
        List<EvacuationDestination> _evacuationDestinations;
        List<EvacuationDestination> _availableEvacuationDestinations;
        EvacuationGroup[] _evacuationGroups;
        DemographicsInput _defaultDemographics;

        public PedestrianModule PedestrianModule { get => _pedestrianModule; }
        public TrafficModule TrafficModule { get => _trafficModule; }
        public TriggerBufferModule TriggerBufferModule { get => _triggerBufferModule; }

        //Data, move?
        public List<EvacuationDestination> Destinations { get => _evacuationDestinations; }
        public DemographicsInput DefaultDemographics { get => _defaultDemographics; }

        public EvacuationManager(Simulation simulation)
        {
            _simulation = simulation;
            _input = _simulation.Input;
            _evacuationDestinationsDict = EvacuationDestination.CreateEvacacuationDestinationsFromInput(_simulation, _input.Evacuation.EvacuationDestinationInputs);
            SetDefaulDemographics(_input.Population.Demographics);
            _evacuationGroups = EvacuationGroup.CreateGroupsFromInput(_input.Evacuation.EvacuationGroupInputs, _evacuationDestinationsDict, _input.Evacuation.ResponseCurves, _input.Population.Demographics, _simulation);
            SetDefaulEvacuationtGroup(); //just sets default group fallback
            BuildEvacuationDestinationList(); //duplicate of destination but in an array, needed for random pull of destination
            BuildAvailableEvacuationDestinations();
        }

        public void PostStep()
        {
            //check for distance to wildfire front, affect evacuees
            //(_pedestrianModule is null when no pedestrian module is enabled)
            if(_simulation.Hazards.Wildfire != null && _pedestrianModule != null)
            {
                _pedestrianModule.ReactToWildfire(_simulation.Time.SimulationTime);
            }

            //handle all damage/impact on road network
            AffectRoadNetwork();

            //check if any goal has been blocked by fire, this is done after everything has progressed the current time step
            UpdateDestinationsWildfireStatus();

            //inject vehicles from all sources
            HandleNewVehicles();
        }

        private void AffectRoadNetwork()
        {
            //handle any fire effects on road network
            if (_simulation.Hazards.Wildfire != null)
            {
                if (_trafficModule != null)
                {
                    _roadClosureStopwatch.Start();
                    _trafficModule.HandleIgnitedFireCells(_simulation.Hazards.Wildfire.GetIgnitedFireCells());
                    _roadClosureStopwatch.Stop();
                }
                _simulation.Hazards.Wildfire.ConsumeIgnitedFireCells();
            }
        }

        private void HandleNewVehicles()
        {
            //handle/inject cars that arrived this timestep
            if (_trafficModule != null)
            {
                _pathfindingStopwatch.Start();
                _trafficModule.HandleNewCars();
                _pathfindingStopwatch.Stop();
            }
        }


        public List<SimulationModule> CreateModules(WeatherManager weather, TimeManager time, out bool success)
        {
            List<SimulationModule> createdModules = new List<SimulationModule>();

            CreatePedestrianModule(_simulation, _input, weather, time, out success);
            if(success && _pedestrianModule != null)
            {
                createdModules.Add(_pedestrianModule);
            }
            else
            {
                return createdModules;
            }

            CreateTrafficModule(_simulation, _input, weather, time, out success);
            if (success && _trafficModule != null)
            {
                createdModules.Add(_trafficModule);
            }
            else
            {
                return createdModules;
            }

            return createdModules;
        }

        private void CreatePedestrianModule(Simulation simulation, PREACTInput input, WeatherManager weather, TimeManager time, out bool success)
        {
            success = false;

            if (_input.PedestrianModule.Enabled)
            {
                if (_input.PedestrianModule.Module == PedestrianModuleInput.PedestrianModules.MacroHouseholdSim)
                {
                    _pedestrianModule = new MacroHouseholdSim(simulation);
                    Engine.Message(simulation, Engine.LogType.Log, "Pedestrian module MacroHouseholdSim initiated.");
                }
            }
            else
            {
                success = true;
                Engine.Message(simulation, Engine.LogType.Log, "No pedestrian module was enabled.");
            }

            if(_pedestrianModule != null)
            {
                success = true;
            }
        }

        private void CreateTrafficModule(Simulation simulation, PREACTInput input, WeatherManager weather, TimeManager time, out bool success)
        {
            success = false;

            if (_input.TrafficModule.Enabled)
            {
                if (_input.TrafficModule.Module == TrafficModuleInput.TrafficModules.SUMO)
                {
                    _trafficModule = new SUMOModule(simulation, out success);
                    if (success)
                    {
                        Engine.Message(simulation, Engine.LogType.Log, "Traffic module SUMO initiated.");
                    }
                    else
                    {
                        _trafficModule = null;
                    }
                }
                else
                {
                    Engine.Message(simulation, Engine.LogType.SimulationError, "No valid traffic module was specified.");
                }
            }
            else
            {
                success = true;
                Engine.Message(simulation, Engine.LogType.Log, "No traffic module was enabled.");
            }

            if (_trafficModule != null)
            {
                success = true;
            }
        }

        /// <summary>
        /// WRSET = the last-arrival (100%) evacuation time for this run, in minutes:
        /// the moment the final vehicle reaches safety. Fed to k-PERIL as the RSET.
        /// </summary>
        private float CalculateWRSETMinutes(Simulation simulation)
        {
            System.Collections.Generic.List<double> arrivals = simulation.Output.GetTrafficArrivalData();
            double lastArrivalSeconds = 0.0;
            if (arrivals != null)
            {
                for (int i = 0; i < arrivals.Count; i++)
                {
                    if (arrivals[i] > lastArrivalSeconds)
                    {
                        lastArrivalSeconds = arrivals[i];
                    }
                }
            }
            return (float)(lastArrivalSeconds / 60.0);
        }

        /// <summary>
        /// Load a WUI-area mask (.asc or .tif, 1 = protected cell) into a bool[] flattened
        /// as index = x + y*xCount, aligned with the fire ROS grid. Returns null if no file
        /// was given, it could not be read, or its dimensions do not match the ROS grid.
        /// </summary>
        private bool[] LoadWuiAreaMask(string wuiAreaFile, string rootFolder, int xCount, int yCount)
        {
            if (string.IsNullOrEmpty(wuiAreaFile))
            {
                return null;
            }

            string path = System.IO.Path.Combine(rootFolder, wuiAreaFile);
            float[,] mask = Utility.AscRaster.Read(path, out Utility.AscRaster.Header header, out bool ok);
            if (!ok || mask == null)
            {
                return null;
            }

            if (header.Ncols != xCount || header.Nrows != yCount)
            {
                Engine.Message(null, Engine.LogType.Warning, $"WUI mask dimensions ({header.Ncols}x{header.Nrows}) do not match the fire grid ({xCount}x{yCount}); ignoring the mask.");
                return null;
            }

            //Size alone is not registration. Two rasters of the same shape on different ground line up cell for
            //cell and describe different places, and the boundary would then protect somewhere the community is
            //not - with nothing about the result looking wrong. Checked to a tenth of a cell, the same tolerance
            //the case validator uses, since a warped raster's corner can differ in the last bits.
            Math.Vector2d fireOrigin = _simulation.Hazards.Wildfire.GetGridOriginUtm();
            if (fireOrigin.x != 0.0 || fireOrigin.y != 0.0)
            {
                double tolerance = 0.1 * header.CellSize;
                double dx = header.XllCorner - fireOrigin.x;
                double dy = header.YllCorner - fireOrigin.y;

                if (System.Math.Abs(dx) > tolerance || System.Math.Abs(dy) > tolerance)
                {
                    Engine.Message(null, Engine.LogType.Warning,
                        $"The WUI mask starts at ({header.XllCorner:F1}, {header.YllCorner:F1}) but the fire grid "
                        + $"starts at ({fireOrigin.x:F1}, {fireOrigin.y:F1}) - offset by ({dx:F1}, {dy:F1}) m, about "
                        + $"({dx / header.CellSize:F1}, {dy / header.CellSize:F1}) cells. It describes different "
                        + "ground; ignoring it.");
                    return null;
                }
            }

            //Any positive value marks a WUI cell. This accepts both a crisp 1/0 mask and a
            //fractional raster such as ELMFIRE's bldg_footprint_frac (0 = no buildings).
            bool[] wuiArea = new bool[xCount * yCount];
            for (int y = 0; y < yCount; y++)
            {
                for (int x = 0; x < xCount; x++)
                {
                    float v = mask[x, y];
                    wuiArea[x + y * xCount] = v > 0f && v != (float)header.NoDataValue;
                }
            }
            return wuiArea;
        }

        /// <summary>
        /// Reads a wind raster onto the fire grid, returning null if it is missing, unreadable or
        /// the wrong size. AscRaster handles both .asc and .tif and returns [x, y] with a lower-left
        /// origin, which is the same convention GetMaxROS() uses - k-PERIL takes totalX/totalY from
        /// the ROS raster and throws on a size mismatch, so a transposed grid would be caught, but
        /// only on a non-square domain. Checking here means a square domain cannot slip through
        /// silently transposed.
        /// </summary>
        /// <summary>
        /// One wind value per cell, taken from the band covering the hour the fire reached that cell.
        /// </summary>
        /// <remarks>
        /// k-PERIL's solver has no time axis: the wind enters it once, as the length-to-breadth ratio of the
        /// spread ellipse at each cell. A multi-hour fire therefore has to be collapsed to a single field,
        /// and this picks the hour that is actually true of each cell - the one the front passed through it
        /// in. It used to take one band for the whole domain, so an eight-hour burn was evaluated entirely
        /// on its first hour however the wind turned; a fire that swung 90 degrees mid-run had its later
        /// half analysed against wind it never saw.
        ///
        /// Cells the fire never reached take the last band. They lie ahead of the front, so the latest wind
        /// is the closest thing to relevant, and they need some value because k-PERIL evaluates the whole
        /// grid rather than the burned footprint.
        /// </remarks>
        private float[,] LoadWindRaster(string windFile, string rootFolder, int xCount, int yCount, string what,
            bool isDirection, WildfireModule wildfire, double secondsPerBand)
        {
            if (string.IsNullOrEmpty(windFile))
            {
                Engine.Message(null, Engine.LogType.InputError, what + " was not specified; k-PERIL needs a wind field.");
                return null;
            }

            string path = System.IO.Path.Combine(rootFolder, windFile);
            float[,] raster = Utility.AscRaster.Read(path, 1, out Utility.AscRaster.Header header, out bool ok,
                out int bandCount);
            if (!ok || raster == null)
            {
                Engine.Message(null, Engine.LogType.InputError, what + " could not be read: " + path);
                return null;
            }

            if (header.Ncols != xCount || header.Nrows != yCount)
            {
                Engine.Message(null, Engine.LogType.InputError, $"{what} dimensions ({header.Ncols}x{header.Nrows}) do not match the fire grid ({xCount}x{yCount}).");
                return null;
            }

            if (bandCount > 1)
            {
                raster = ComposeWindAtArrivalTime(raster, path, xCount, yCount, bandCount, what, wildfire,
                    secondsPerBand);
                if (raster == null)
                {
                    return null;
                }
            }

            float min = float.MaxValue;
            float max = float.MinValue;
            for (int x = 0; x < xCount; ++x)
            {
                for (int y = 0; y < yCount; ++y)
                {
                    float v = raster[x, y];
                    if (v <= -9000f || float.IsNaN(v))
                    {
                        continue;
                    }
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }

            if (min > max)
            {
                Engine.Message(null, Engine.LogType.InputError, what + " contains no valid data: " + path);
                return null;
            }

            //An all-zero or otherwise flat wind field is almost always the wrong file rather than a
            //real calm. It is worth saying so, because it fails silently: zero wind gives a
            //length-to-breadth ratio of 1, so the spread template turns into a circle and the
            //trigger boundary comes out isotropic instead of wind-driven.
            if (min == max)
            {
                Engine.Message(null, Engine.LogType.Warning, $"{what} is constant at {min}; the spread ellipse will be circular. Check that the weather pipeline actually produced this raster.");
            }

            //Directions outside [0, 360] mean the field was resampled in angle space, which
            //interpolates across the 0/360 wrap: averaging 350 and 10 yields 180, the opposite
            //direction. Overshoot past the ends is the visible symptom of it. Wind rasters have to
            //be warped as u/v components, which is what WindNinjaRunner does.
            if (isDirection && (min < -0.001f || max > 360.001f))
            {
                Engine.Message(null, Engine.LogType.Warning, $"{what} spans {min} to {max} degrees, outside 0-360. It was most likely resampled as angles rather than as u/v components, so directions near the 0/360 wrap are wrong.");
            }

            return raster;
        }

        /// <summary>
        /// Replaces <paramref name="firstBand"/> cell by cell with the band covering that cell's arrival time.
        /// </summary>
        /// <remarks>
        /// Read one band at a time rather than all of them at once: a band of the Mati grid is 1.2 MB and
        /// there can be days of them in an ensemble case, and only the cells belonging to a band are needed
        /// while it is in hand.
        /// </remarks>
        private float[,] ComposeWindAtArrivalTime(float[,] firstBand, string path, int xCount, int yCount,
            int bandCount, string what, WildfireModule wildfire, double secondsPerBand)
        {
            if (wildfire == null || secondsPerBand <= 0.0)
            {
                //Nothing to map arrival times onto bands with. The first band is what the old behaviour used,
                //so this degrades to that rather than failing - but it is worth saying, because the boundary
                //is then computed against one hour of an N-hour fire.
                Engine.Message(null, Engine.LogType.Warning,
                    $"{what} holds {bandCount} bands but the arrival times or the band interval are unknown, so "
                    + "band 1 is used for the whole domain.");
                return firstBand;
            }

            //Which band each cell wants, and how many cells want each - the counts are only for the summary,
            //but a distribution is the one thing that shows at a glance whether this did anything.
            var wanted = new int[xCount, yCount];
            var perBand = new int[bandCount + 1];
            int unburned = 0;

            for (int x = 0; x < xCount; ++x)
            {
                for (int y = 0; y < yCount; ++y)
                {
                    float arrival = wildfire.GetTimeOfArrival(x, y);
                    int band;

                    if (arrival == float.MaxValue)
                    {
                        band = bandCount;
                        ++unburned;
                    }
                    else
                    {
                        //Bands are 1-based and cover [ (b-1)*dt, b*dt ). Clamped at both ends: an arrival
                        //before the first band belongs to it, and one past the last - which a fire outliving
                        //its weather produces - belongs to the last.
                        band = (int)System.Math.Floor(arrival / secondsPerBand) + 1;
                        if (band < 1) band = 1;
                        if (band > bandCount) band = bandCount;
                    }

                    wanted[x, y] = band;
                    ++perBand[band];
                }
            }

            var composed = new float[xCount, yCount];

            for (int band = 1; band <= bandCount; ++band)
            {
                if (perBand[band] == 0)
                {
                    continue;
                }

                float[,] source = firstBand;
                if (band != 1)
                {
                    source = Utility.AscRaster.Read(path, band, out Utility.AscRaster.Header _, out bool bandOk,
                        out int _);
                    if (source == null || !bandOk)
                    {
                        Engine.Message(null, Engine.LogType.InputError,
                            $"{what} band {band} could not be read: {path}");
                        return null;
                    }
                }

                for (int x = 0; x < xCount; ++x)
                {
                    for (int y = 0; y < yCount; ++y)
                    {
                        if (wanted[x, y] == band)
                        {
                            composed[x, y] = source[x, y];
                        }
                    }
                }
            }

            var used = new System.Collections.Generic.List<string>();
            for (int band = 1; band <= bandCount; ++band)
            {
                if (perBand[band] > 0) used.Add($"{band}:{perBand[band]}");
            }

            Engine.Message(null, Engine.LogType.Log,
                $"{what}: sampled per cell at the fire's arrival time across {bandCount} band(s) of "
                + $"{secondsPerBand:F0} s. Cells per band - {string.Join(", ", used)}"
                + (unburned > 0 ? $"; {unburned} cell(s) the fire never reached took the last band." : "."));

            return composed;
        }

        /// <summary>
        /// Seconds covered by one band of the weather rasters.
        /// </summary>
        /// <remarks>
        /// The ELMFIRE settings state it, and for an ELMFIRE fire that is authoritative - it is the same
        /// value written into the namelist the fire was computed with. For a fire imported from elsewhere
        /// there is nothing in the scenario that says, and hourly is the convention of every product this
        /// reads, so an hour is assumed and said.
        /// </remarks>
        private double ResolveSecondsPerWeatherBand()
        {
            if (_input.WildfireModule.Module == Input.WildfireModuleInput.WildfireModules.ELMFIRE
                && _input.WildfireModule.ElmfireInput?.Namelist != null
                && _input.WildfireModule.ElmfireInput.Namelist.DT_METEOROLOGY > 0.0)
            {
                return _input.WildfireModule.ElmfireInput.Namelist.DT_METEOROLOGY;
            }

            return 3600.0;
        }

        /// <summary>One WUI area to protect, and a label used to name its output.</summary>
        private struct WuiAreaRun
        {
            public bool[] WuiArea;
            public string Label;
        }

        /// <summary>
        /// Works out which areas k-PERIL should protect, one entry per boundary to compute.
        /// </summary>
        /// <summary>
        /// The painted WUI area, but only if it was painted on the grid k-PERIL is about to compute on.
        ///
        /// Checked because the two can legitimately differ: the masks are painted on whatever grid the
        /// scenario had at the time, and changing the landscape - swapping a 27.6 m DEM for a 30 m one, say
        /// - changes the fire grid under them. Indexed as x + y*xCount against the wrong width, a mask
        /// silently marks a sheared, offset region as the community to protect, and the trigger boundary
        /// that comes out looks perfectly plausible. Refused with a reason instead.
        /// </summary>
        private bool[] PaintedWuiArea(Simulation simulation, int xCount, int yCount)
        {
            bool[] painted = _input.WildfireModule.Data.WuiArea;
            if (painted == null)
            {
                return null;
            }

            if (painted.Length == xCount * yCount)
            {
                return painted;
            }

            Math.Vector2int cells = _input.WildfireModule.Data.PaintedCellCount;
            Engine.Message(simulation, Engine.LogType.SimulationError,
                $"The painted WUI area covers {cells.x} x {cells.y} cells but the fire grid is {xCount} x {yCount}. "
                + "It was painted against a different landscape, so it cannot be used here - repaint it, or set "
                + "[kPERIL] WuiAreaFile to a mask on this grid.");
            return null;
        }

        /// <summary>
        /// Whether the fire reached any cell of a WUI area within the simulated period, and how many.
        ///
        /// Any cell, not all of them: a fire that reaches the edge of a community is a fire that community
        /// has to leave ahead of, and the trigger boundary is what says when. Counted rather than just
        /// tested so the log can say how much of the area burned, which is the difference between a fire that
        /// brushed one corner and one that went through it.
        /// </summary>
        private bool FireReachedArea(Simulation simulation, bool[] wuiArea, int xCount, int yCount, out int burnedCells)
        {
            burnedCells = 0;

            if (wuiArea == null || simulation.Hazards.Wildfire == null)
            {
                return false;
            }

            for (int y = 0; y < yCount; ++y)
            {
                for (int x = 0; x < xCount; ++x)
                {
                    int index = x + y * xCount;
                    if (index < wuiArea.Length && wuiArea[index] && simulation.Hazards.Wildfire.CellHasBurned(x, y))
                    {
                        ++burnedCells;
                    }
                }
            }

            return burnedCells > 0;
        }

        private List<WuiAreaRun> BuildWuiAreaRuns(Simulation simulation, int xCount, int yCount)
        {
            var runs = new List<WuiAreaRun>();
            kPERILInput peril = _input.TriggerBufferModule.kPERILInput;

            if (peril.WuiAreaSource == kPERILInput.WuiAreaSources.Raster)
            {
                //As before: an explicit mask when given, otherwise whatever the wildfire data carried.
                bool[] wuiArea = LoadWuiAreaMask(peril.WuiAreaFile, _input.RootFolder, xCount, yCount)
                                 ?? PaintedWuiArea(simulation, xCount, yCount);
                if (wuiArea != null)
                {
                    runs.Add(new WuiAreaRun { WuiArea = wuiArea, Label = "wui" });
                }
                return runs;
            }

            if (_evacuationGroups == null || _evacuationGroups.Length == 0)
            {
                Engine.Message(simulation, Engine.LogType.SimulationError, "WuiAreaSource is set to evacuation groups, but the scenario has none.");
                return runs;
            }

            bool separate = peril.WuiAreaSource == kPERILInput.WuiAreaSources.EvacuationGroupsSeparate;
            bool[] combined = separate ? null : new bool[xCount * yCount];
            int combinedCells = 0;

            for (int i = 0; i < _evacuationGroups.Length; ++i)
            {
                bool[] mask = RasterizeEvacuationGroup(simulation, _evacuationGroups[i], xCount, yCount, out int cellCount);

                if (cellCount == 0)
                {
                    //Silence here would produce an empty boundary with no indication why.
                    Engine.Message(simulation, Engine.LogType.Warning, $"Evacuation group {_evacuationGroups[i].Name} covers no cell of the fire grid; it contributes no WUI area.");
                    continue;
                }

                if (separate)
                {
                    runs.Add(new WuiAreaRun { WuiArea = mask, Label = _evacuationGroups[i].Name });
                }
                else
                {
                    for (int c = 0; c < combined.Length; ++c)
                    {
                        if (mask[c] && !combined[c])
                        {
                            combined[c] = true;
                            ++combinedCells;
                        }
                    }
                }
            }

            if (!separate && combinedCells > 0)
            {
                Engine.Message(simulation, Engine.LogType.Log, $"WUI area combined from {_evacuationGroups.Length} evacuation groups: {combinedCells} cells.");
                runs.Add(new WuiAreaRun { WuiArea = combined, Label = "groups" });
            }

            return runs;
        }

        /// <summary>
        /// Marks every fire-grid cell whose centre falls inside the group's polygon.
        ///
        /// Cell centres are built in simulation coordinates, which is what the group's polygon is
        /// already stored in, and the fire grid's own offset is applied so the two line up even when
        /// the fire domain does not start at the simulation origin. The resulting index order,
        /// x + y*xCount with y running north, is the order k-PERIL and the WUI mask reader use.
        /// </summary>
        private bool[] RasterizeEvacuationGroup(Simulation simulation, EvacuationGroup group, int xCount, int yCount, out int cellCount)
        {
            bool[] mask = new bool[xCount * yCount];
            cellCount = 0;

            simulation.Hazards.Wildfire.GetOffsetAndSize(out Vector2d offset, out Vector2d size);
            double cellSizeX = size.x / xCount;
            double cellSizeY = size.y / yCount;

            for (int y = 0; y < yCount; ++y)
            {
                double posY = offset.y + (y + 0.5) * cellSizeY;
                for (int x = 0; x < xCount; ++x)
                {
                    double posX = offset.x + (x + 0.5) * cellSizeX;
                    if (group.SimulationPositionBelongsToGroup(new Vector2d(posX, posY)))
                    {
                        mask[x + y * xCount] = true;
                        ++cellCount;
                    }
                }
            }

            return mask;
        }

        /// <summary>
        /// Samples the landscape's elevation, slope and aspect onto the fire grid, for k-PERIL.
        ///
        /// k-PERIL adds 0.06 times the slope to the wind, as a vector, before working out how elongated
        /// spread is - so on a flat grid that term contributes nothing and a trigger boundary comes out
        /// the same on a hillside as on level ground. Nothing was passed before, so that is what happened:
        /// the raster path defaulted its optional topography arguments to null and k-PERIL stood in a grid
        /// of zeros. On Mati's terrain, which rises 770 m out of the sea, the difference is not small.
        ///
        /// The two grids are not assumed to be the same. Each fire cell's centre is taken in simulation
        /// coordinates and looked up in the landscape by its own origin and cell size, so a landscape of a
        /// different resolution or extent still lands in the right place. Cells outside the landscape keep
        /// nodata rather than the nearest edge value, so a landscape that does not cover the fire grid is
        /// visible as a gap instead of a smear.
        /// </summary>
        private bool TrySampleTopographyOntoFireGrid(Simulation simulation, int xCount, int yCount,
            out float[,] elevation, out float[,] slope, out float[,] aspect)
        {
            simulation.Hazards.Wildfire.GetOffsetAndSize(out Vector2d fireOffset, out Vector2d fireSize);
            return TrySampleTopographyOntoGrid(_input.WildfireModule.Data.LandscapeData,
                fireOffset, fireSize, xCount, yCount, out elevation, out slope, out aspect);
        }

        /// <summary>
        /// The geometry of the above, taking the target grid as numbers rather than reading it off a
        /// running simulation - so it can be checked against real data without one.
        /// </summary>
        public static bool TrySampleTopographyOntoGrid(Wildfire.LandscapeData landscape,
            Vector2d fireOffset, Vector2d fireSize, int xCount, int yCount,
            out float[,] elevation, out float[,] slope, out float[,] aspect)
        {
            elevation = null;
            slope = null;
            aspect = null;

            if (landscape == null)
            {
                return false;
            }

            double fireCellX = fireSize.x / xCount;
            double fireCellY = fireSize.y / yCount;

            Vector2d landscapeOffset = landscape.OriginOffset;
            double landscapeCellX = landscape.RasterCellResolutionX;
            double landscapeCellY = landscape.RasterCellResolutionY;
            int landscapeCountX = landscape.GetCellCountX();
            int landscapeCountY = landscape.GetCellCountY();

            if (landscapeCellX <= 0.0 || landscapeCellY <= 0.0)
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "The landscape has no cell size, so no topography can be sampled for k-PERIL.");
                return false;
            }

            elevation = new float[xCount, yCount];
            slope = new float[xCount, yCount];
            aspect = new float[xCount, yCount];

            int covered = 0;
            for (int y = 0; y < yCount; ++y)
            {
                double posY = fireOffset.y + (y + 0.5) * fireCellY;
                int landscapeY = (int)System.Math.Floor((posY - landscapeOffset.y) / landscapeCellY);

                for (int x = 0; x < xCount; ++x)
                {
                    double posX = fireOffset.x + (x + 0.5) * fireCellX;
                    int landscapeX = (int)System.Math.Floor((posX - landscapeOffset.x) / landscapeCellX);

                    if (landscapeX < 0 || landscapeX >= landscapeCountX || landscapeY < 0 || landscapeY >= landscapeCountY)
                    {
                        //Flat, not nodata. k-PERIL multiplies the slope by 0.06 and vector-adds it to the
                        //wind with no check for a nodata value, so a -9999 here becomes a 600 mi/h wind in
                        //that cell rather than a gap - and Mati's grid has 8,619 cells of sea in it. Zero
                        //contributes nothing, which is the honest answer for ground that is not described.
                        elevation[x, y] = 0f;
                        slope[x, y] = 0f;
                        aspect[x, y] = 0f;
                        continue;
                    }

                    //GetCellData indexes the landscape directly, with y running north, which is the same
                    //direction the fire grid and every raster here run.
                    Wildfire.LandscapeCellData cell = landscape.GetCellData(landscapeX, landscapeY);

                    //Same reasoning as above for cells the landscape covers but has no height for.
                    bool haveCell = cell.elevation > -9999 && cell.slope > -9999;
                    elevation[x, y] = haveCell ? cell.elevation : 0f;
                    slope[x, y] = haveCell ? cell.slope : 0f;
                    //A flat cell has no aspect; the landscape marks that -1 and k-PERIL's own convention
                    //is 0. Translated rather than passed through, so the two agree - it makes no difference
                    //to the result, since the slope it multiplies is zero either way, but a value k-PERIL
                    //does not use the same way is not worth handing it.
                    aspect[x, y] = haveCell && cell.aspect >= 0 ? cell.aspect : 0f;

                    if (haveCell)
                    {
                        ++covered;
                    }
                }
            }

            if (covered == 0)
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "The landscape does not overlap the fire grid at all, so k-PERIL will run on flat ground.");
                elevation = null;
                slope = null;
                aspect = null;
                return false;
            }

            double coveredFraction = (double)covered / (xCount * yCount);
            if (coveredFraction < 0.999)
            {
                //Not necessarily a problem: Mati's DEM has no height over the sea, which is a tenth of the
                //grid and is genuinely flat. Reported so that a landscape that really is too small to cover
                //the domain is distinguishable from one that simply has water in it.
                Engine.Message(null, Engine.LogType.Log,
                    $"The landscape gives a height for {coveredFraction * 100.0:F1}% of the fire grid; the rest is "
                    + "treated as flat.");
            }

            Engine.Message(null, Engine.LogType.Log,
                $"k-PERIL topography sampled from the landscape onto the fire grid ({coveredFraction * 100.0:F1}% covered).");
            return true;
        }

        public void CreateAndRunTriggerBufferModule(Simulation simulation, PREACTInput input, WeatherManager weather, TimeManager time)
        {
            if (_input.TriggerBufferModule.Enabled)
            {
                if (_input.TriggerBufferModule.Module == TriggerBufferModuleInput.TriggerBufferModules.kPERIL)
                {
                    //Everything below is measured on the fire's grid, starting with the cell counts a few
                    //lines down. Without this the run reached those and threw a NullReferenceException,
                    //which says nothing about a module not being enabled.
                    if (simulation.Hazards.Wildfire == null)
                    {
                        Engine.Message(simulation, Engine.LogType.Warning,
                            "Can't compute a trigger boundary without a wildfire module: it is back-propagated "
                            + "from the fire's arrival times, on the fire's own grid. Enable the wildfire module, "
                            + "or turn the trigger boundary off.");
                        return;
                    }
                    else
                    {
                        //WRSET = the required safe egress time this run produced: the last-arrival
                        //(100%) evacuation time, in minutes. This is what k-PERIL back-propagates.
                        float wrsetMinutes = CalculateWRSETMinutes(simulation);
                        Engine.Message(simulation, Engine.LogType.Log, "WRSET (last-arrival evacuation time) = " + wrsetMinutes + " minutes.");

                        //A boundary computed from a zero egress time is the WUI area and nothing more: with no
                        //time to travel, no cell outside it can reach the community in time. It is not a
                        //result, and it is dangerous to publish as one - in a campaign it aggregates in
                        //alongside real boundaries and pulls the probability field *inward*, making the trigger
                        //look tighter than the evidence supports.
                        //
                        //Refused rather than warned about, because nothing downstream can tell the difference.
                        if (wrsetMinutes <= 0f)
                        {
                            Engine.Message(simulation, Engine.LogType.SimulationError,
                                "No evacuation arrivals were recorded, so the required egress time is zero and "
                                + "the trigger boundary would collapse onto the WUI area. Not computing one. "
                                + "Check that the traffic module ran and that the population reaches a "
                                + "destination - a run that evacuates nobody cannot say when to leave.");
                            return;
                        }

                        int xCount = simulation.Hazards.Wildfire.GetCellCountX();
                        int yCount = simulation.Hazards.Wildfire.GetCellCountY();

                        //k-PERIL takes a wind field, not a representative number. Both rasters are
                        //required: without them there is nothing to derive the spread ellipse's
                        //elongation from, and silently standing in a constant would discard the
                        //terrain-driven variation the WindNinja step exists to produce.
                        //Sampled per cell at the hour the fire reached it, so a multi-hour burn is not
                        //evaluated against a single hour's wind. Needs the fire module for its arrival times.
                        double secondsPerBand = ResolveSecondsPerWeatherBand();
                        float[,] windSpeedMph = LoadWindRaster(_input.TriggerBufferModule.kPERILInput.WindSpeedFile,
                            _input.RootFolder, xCount, yCount, nameof(kPERILInput.WindSpeedFile), false,
                            simulation.Hazards.Wildfire, secondsPerBand);
                        float[,] windDirectionDegrees = LoadWindRaster(_input.TriggerBufferModule.kPERILInput.WindDirectionFile,
                            _input.RootFolder, xCount, yCount, nameof(kPERILInput.WindDirectionFile), true,
                            simulation.Hazards.Wildfire, secondsPerBand);

                        if (windSpeedMph == null || windDirectionDegrees == null)
                        {
                            Engine.Message(simulation, Engine.LogType.SimulationError, "Can't run kPERIL without wind speed and direction rasters matching the fire grid.");
                            return;
                        }

                        //One run per WUI area. A raster or combined groups give a single area and so
                        //a single boundary; per-group gives one each, which is the point of the
                        //option - groups evacuating on different orders have different egress times
                        //and therefore different triggers, and unioning them would hide that.
                        List<WuiAreaRun> runs = BuildWuiAreaRuns(simulation, xCount, yCount);
                        if (runs.Count == 0)
                        {
                            Engine.Message(simulation, Engine.LogType.SimulationError, "Can't run kPERIL without a WUI area to protect.");
                            return;
                        }

                        //Sampled once for every run: the terrain does not change between WUI areas.
                        //Absence is not fatal - a boundary on flat ground is still a boundary - but it is
                        //said out loud, because the difference on real terrain is not small and a silent
                        //flat grid is indistinguishable from genuinely level ground.
                        if (!TrySampleTopographyOntoFireGrid(simulation, xCount, yCount,
                            out float[,] elevation, out float[,] slope, out float[,] aspect))
                        {
                            Engine.Message(simulation, Engine.LogType.Warning,
                                "k-PERIL has no terrain to work with, so it will treat the ground as flat and the "
                                + "slope will not affect the boundary. Add an ElevationFile to the Landscape section; "
                                + "slope and aspect are computed from it.");
                        }

                        for (int i = 0; i < runs.Count; ++i)
                        {
                            //A trigger boundary around an area this fire never reached is not a result. The
                            //boundary answers "when must this area leave, given the fire coming at it" - and
                            //with no fire arriving there is nothing to be given. Producing one anyway is
                            //worse than producing none: it looks like every other boundary, so a case whose
                            //fire went the other way, or whose ELMFIRE run simply stopped too early, is
                            //indistinguishable from one that was genuinely threatened.
                            if (!FireReachedArea(simulation, runs[i].WuiArea, xCount, yCount, out int burnedCells))
                            {
                                Engine.Message(simulation, Engine.LogType.Warning,
                                    $"The fire never reached {runs[i].Label}, so no trigger boundary was computed for "
                                    + "it. Either it is not threatened in this scenario, or the fire was not run for "
                                    + "long enough to get there - a probabilistic campaign runs ELMFIRE for days for "
                                    + "exactly this reason. Raise [ELMFIRE] SimulationTstopHours to give the fire "
                                    + "time to arrive.");
                                continue;
                            }

                            //Topography goes in with the rate of spread. k-PERIL vector-adds 0.06 of
                            //the slope to the wind before deriving how elongated spread is, so
                            //leaving these out - which is what happened, the optional arguments
                            //defaulting to null and k-PERIL standing in zeros - meant every boundary
                            //was computed as if the ground were level.
                            _triggerBufferModule = new kPERIL(wrsetMinutes, runs[i].WuiArea, windSpeedMph, windDirectionDegrees,
                                simulation.Hazards.Wildfire.GetMaxROS(), simulation.Hazards.Wildfire.GetMaxROSAzimuth(),
                                simulation.Hazards.Wildfire.GetCellSizeX(), elevation, slope, aspect);

                            Engine.Message(simulation, Engine.LogType.Log,
                                $"k-PERIL run {i + 1} of {runs.Count}: {runs[i].Label}, which the fire reached in "
                                + $"{burnedCells} cell(s).");

                            _triggerBufferModule.Run();

                            //Per-group outputs are named after the group, so several boundaries from
                            //one simulation do not overwrite each other.
                            string outputName = _input.TriggerBufferModule.kPERILInput.OutputName;
                            if (runs.Count > 1)
                            {
                                outputName = Path.GetFileNameWithoutExtension(outputName) + "_" + runs[i].Label + Path.GetExtension(outputName);
                            }
                            string outputFilePath = Path.Combine(simulation.Engine.OutputFolder, simulation.SimulationIndex + "_" + outputName);
                            kPERIL.SaveToFile(_triggerBufferModule.TriggerBufferOutput,
                                simulation.Hazards.Wildfire.GetCellSizeX(), outputFilePath,
                                simulation.Hazards.Wildfire.GetGridOriginUtm());

                            //Registered here, once per run, rather than once after the loop - which
                            //would have recorded only the last group's boundary.
                            simulation.Output.AddTriggerBufferOutput(_triggerBufferModule.TriggerBufferOutput, simulation.SimulationIndex);
                        }
                    }
                }
            }
            else
            {
                Engine.Message(simulation, Engine.LogType.Log, "No trigger buffer module was enabled.");
            }
        }

        public void InsertNewCar(Vector2d startLatLon, EvacuationDestination evacuationGoal, uint numberOfPeopleInCar)
        {
            if (_trafficModule != null)
            {
                _trafficModule.InsertNewCar(startLatLon, evacuationGoal, numberOfPeopleInCar);
            }
        }

        int _runtimeDestinationCount = 0;
        public EvacuationDestination AddRuntimeDestination(Vector2d latLon)
        {
            EvacuationDestination eD = new EvacuationDestination(_simulation, latLon, "RuntimeDestination" + _runtimeDestinationCount);
            _evacuationDestinations.Add(eD);
            ++_runtimeDestinationCount;
            _simulation.Engine.UpdateEvacuationDestinations(_simulation, _evacuationDestinations);

            return eD;
        }

        public uint GetTotalEvacuated()
        {
            uint result = 0;
            foreach (EvacuationDestination eD in _evacuationDestinations)
            {
                result += eD.CurrentPeople;
            }

            return result;
        }

        public void UpdateDestinationsWildfireStatus()
        {
            if (!_input.WildfireModule.Enabled)
            {
                return;
            }

            foreach (EvacuationDestination eD in _evacuationDestinations)
            {
                if (!eD.Blocked)
                {
                    FireCellState cellState = _simulation.Hazards.Wildfire.GetFireCellState(eD.SimulationPos);
                    if (cellState == FireCellState.Ignited)
                    {
                        Engine.Message(_simulation, Engine.LogType.Log, " Destination blocked by fire: " + eD.Name);
                        BlockDestination(eD);
                    }
                }
            }
        }

        public void TryBlockDestination(string destinationName)
        {
            EvacuationDestination eD;
            if(_evacuationDestinationsDict.TryGetValue(destinationName, out eD))
            {
                BlockDestination(eD);
                Engine.Message(_simulation, Engine.LogType.Event, "Goal blocked by user specified event: " + eD.Name);
            }
            else
            {
                Engine.Message(_simulation, Engine.LogType.Event, $"Could not block the destination {destinationName} as it does not exist.");
            }
        }

        public void BlockDestination(EvacuationDestination blockedEvacDest)
        {
            blockedEvacDest.BlockDestination();
            UpdateEvacuationDestinations(blockedEvacDest);
        }

        private void UpdateEvacuationDestinations(EvacuationDestination evacDestThatTriggeredUpdate)
        {
            //check that we have at least one goal left
            bool allBlocked = true;
            _availableEvacuationDestinations.Clear();
            foreach (EvacuationDestination eD in _evacuationDestinations)
            {
                if (!eD.Blocked)
                {
                    _availableEvacuationDestinations.Add(eD);
                    allBlocked = false;
                }
            }
            if (allBlocked)
            {
                _simulation.Stop("All destinations are unavailable, stopping simulation.", false);
                return;
            }

            //TODO: delay this as nobody can actually know that the destination is closed without arriving there, we need a signaling system and compliance system
            /*if (_trafficModule != null)
            {
                _trafficModule.UpdateDestinations();
            }*/

            if (evacDestThatTriggeredUpdate.GoalType == DestinationTypes.Shelter)
            {

            }
            else if (evacDestThatTriggeredUpdate.GoalType == DestinationTypes.Exit)
            {

            }       
        }

        private void BuildEvacuationDestinationList()
        {
            _evacuationDestinations = new List<EvacuationDestination>(_evacuationDestinationsDict.Count);
            foreach (EvacuationDestination eD in _evacuationDestinationsDict.Values)
            {
                _evacuationDestinations.Add(eD);
            }
        }

        private void BuildAvailableEvacuationDestinations()
        {
            _availableEvacuationDestinations = new List<EvacuationDestination>(_evacuationDestinations.Count);
            foreach(EvacuationDestination eD in _evacuationDestinations)
            {
                if(!eD.Blocked)
                {
                    _availableEvacuationDestinations.Add(eD);
                }
            }
        }

        private EvacuationDestination GetRandomEvacuationDestination()
        {
            int randomChoice = Random.Range(0, _evacuationDestinations.Count);
            return _evacuationDestinations[randomChoice];
        }

        private EvacuationDestination GetRandomAvailableEvacuationDestination()
        {
            int randomChoice = Random.Range(0, _availableEvacuationDestinations.Count);
            return _availableEvacuationDestinations[randomChoice];
        }

        private EvacuationDestination GetClosestEuclideanDestination(Vector2d currentLatLon, List<EvacuationDestination> destinationsToConsider)
        {
            double closestDistance = double.MaxValue;
            Vector2d simPos = _input.Simulation.Data.GetSimulationPosition(currentLatLon);
            EvacuationDestination pickedDestination = null;

            foreach (EvacuationDestination eD in destinationsToConsider)
            {
                Vector2d destPos = _input.Simulation.Data.GetSimulationPosition(eD.LatLon);
                double distance = Vector2d.SqrMagnitude(destPos - simPos);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    pickedDestination = eD;
                }
            }

            return pickedDestination;
        }

        private EvacuationDestination GetClosestEuclideanAvailableDestination(Vector2d currentLatLon)
        {
            EvacuationDestination pickedDestination = GetClosestEuclideanDestination(currentLatLon, _availableEvacuationDestinations);
            return pickedDestination;
        }

        private EvacuationDestination GetClosestEuclideanDestination(Vector2d currentLatLon)
        {
            EvacuationDestination pickedDestination = GetClosestEuclideanDestination(currentLatLon, _evacuationDestinations);
            return pickedDestination;
        }


        private void SetDefaulEvacuationtGroup()
        {
            for(int i = 0; i < _evacuationGroups.Length; ++i)
            {
                if (_evacuationGroups[i].Default)
                {
                    _defaultEvacuationGroup = _evacuationGroups[i];
                    break;
                }
            }
        }

        private void SetDefaulDemographics(Dictionary<string, DemographicsInput> demographics)
        {
            foreach(DemographicsInput d in demographics.Values)
            {
                if(d.Default)
                {
                    _defaultDemographics = d;
                    break;
                }
            }
        }

        public EvacuationDestination GetEvacuationDestination(Vector2d latLon, EvacuationGroup evacuationGroup)
        {
            EvacuationDestination goal = null;

            if (evacuationGroup.DestinationChoice == DestinationChoices.EvacGroupCDF)
            {
                goal = evacuationGroup.GetWeightedRandomDestination();
            }
            else if (evacuationGroup.DestinationChoice == DestinationChoices.EvacGroupClosestEuclidean)
            {
                goal = evacuationGroup.GetClosestEuclideanDestination(latLon, _simulation);
            }
            else if (evacuationGroup.DestinationChoice == DestinationChoices.Random)
            {
                goal = GetRandomEvacuationDestination();

            }
            else //default to closest
            {
                goal = GetClosestEuclideanDestination(latLon);
            }

            if (goal == null)
            {
                Engine.Message(_simulation, Engine.LogType.SimulationError, "Issue with assigning evacuation destination, traffic simulation will not run.");
            }

            return goal;
        }

        public EvacuationGroup GetEvacuationGroup(Vector2d latLon, out bool insideGroup)
        {
            EvacuationGroup pickedGroup = _defaultEvacuationGroup;
            insideGroup = false;

            for (int i = 0; i < _evacuationGroups.Length; ++i)
            {
                if (_evacuationGroups[i].LatLonBelongsToGroup(latLon, _simulation))
                {
                    pickedGroup = _evacuationGroups[i];
                    insideGroup = true;
                    break;
                }
            }

            return pickedGroup;
        }

        public EvacuationDestination GetBestAvailableDestination(EvacuationGroup evacuationGroup, Vector2d currentLatLon)
        {
            EvacuationDestination result = null;

            //TODO: actual priority pick based on random weight or proximity?
            for (int i = 0; i < evacuationGroup.Destinations.Count; ++i)
            {
                if (!evacuationGroup.Destinations[i].Blocked)
                {
                    result = evacuationGroup.Destinations[i];
                    break;
                }
            }     

            //all group choices are blocked, pick something else
            if(result == null)
            {
                result = GetClosestEuclideanAvailableDestination(currentLatLon);
            }

            return result;
        }

        public EvacuationDestination GetBestAvailableDestination(Vector2d currentSimulationPos)
        {
            Vector2d currentLatLon = _simulation.Spatial.GetWGS84FromSimulationPosition(currentSimulationPos);
            EvacuationDestination result = GetClosestEuclideanAvailableDestination(currentLatLon);
            return result;
        }

        public void PostRun(Stopwatch simStopwatch)
        {
            Engine.Message(_simulation, Engine.LogType.Log, "Total time spent on road closures [s]:" + _roadClosureStopwatch.ElapsedMilliseconds * 0.001 + string.Format(" [{0}%]", (int)(100.0 * _roadClosureStopwatch.ElapsedMilliseconds / simStopwatch.ElapsedMilliseconds)));
            Engine.Message(_simulation, Engine.LogType.Log, "Total time spent on initial traffic route pathfinding [s]:" + _pathfindingStopwatch.ElapsedMilliseconds * 0.001 + string.Format(" [{0}%]", (int)(100.0 * _pathfindingStopwatch.ElapsedMilliseconds / simStopwatch.ElapsedMilliseconds)));
        }
    }
}
