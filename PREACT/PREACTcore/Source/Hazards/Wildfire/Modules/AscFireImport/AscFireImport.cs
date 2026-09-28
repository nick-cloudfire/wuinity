//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.IO;
using System.Collections.Generic;
using PREACT.Input;
using PREACT.Math;

namespace PREACT.Wildfire
{
    public struct FireRasterData
    {
        public float TimeOfAArrival; //seconds
        public float RateOfSpread; //m/min
        public float FirelineIntensity; //kW/m
        public float SpreadDirection; // Degrees, 0 in North, then clockwise
        public bool isActive;
    }

    /// <summary>
    /// Thus far only tested using only Flammap version of Farsite.
    /// </summary>
    public class AscFireImport : WildfireModule
    {
        private float _startTime;
        private float _maxTimeOfArrival = float.MinValue;
        private int ncols, nrows, _activeCells;
        //The cell width and height. A GeoTIFF's pixels need not be square (the Mati DEM's are 27.592 x 27.616 m);
        //taking the height to be the width misplaced the northern edge by the difference times the row count.
        private double _xllcorner, _yllcorner, _cellsize, _cellsizeY, _NODATA_VALUE;
        private FireRasterData[,] _data;
        private Vector2d _landscapeSize;
        private bool _ignited = false;

        //TODO: clean this up, this is duplicate data but is needed for shaders, come up with some way of better data storage
        float[] _firelineIntensityData;

        List<Vector2int> _newlyIgnitedCells;
        float[] _sootInjection;

        /// <summary>The fire rasters this module reads. The scenario's own <c>[AscImport]</c> for an imported fire;
        /// a runtime copy for an ELMFIRE fire, so a run never writes its derived paths into the scenario (C4).</summary>
        private readonly AscImportInput _fire;

        public AscFireImport(Simulation simulation) : this(simulation, null, null)
        {
        }

        /// <param name="fire">The rasters to read; null reads the scenario's <c>[AscImport]</c> section.</param>
        /// <param name="weather">
        /// The weather the fire was computed against, when the caller knows it (the ELMFIRE module does). Null
        /// builds it from <c>[AscImport] MidflameWindSpeedFile</c> when that is set.
        /// </param>
        public AscFireImport(Simulation simulation, AscImportInput fire, FireWeatherRasters weather) : base(simulation)
        {
            _fire = fire ?? _simulation.Input.WildfireModule.AscImportInput;

            _startTime = (float)_simulation.Time.GetSimulationTime(_fire.StartDateTime);
            //Resolved the way the scenario check resolves them (forward or back slashes, a file since moved within
            //the scenario's folders, an absolute path as it is), so a scenario that passed the check reads the
            //same files here.
            string TOAFile = PREACTInput.ResolvePath(_simulation.Input.RootFolder, _fire.TimeOfArrivalFile);
            string ROSFile = PREACTInput.ResolvePath(_simulation.Input.RootFolder, _fire.RateOfSpreadFile);
            //Left null when no raster is named, rather than combined unconditionally. Path.Combine with an
            //empty second argument returns the folder, so an absent fireline intensity produced the scenario
            //directory as a path - which ReadOutput then found non-empty, tried to read as a raster, and
            //warned about on every single run. The raster is genuinely optional, and this is what says so.
            string FIFile = NullIfNotNamed(_fire.FirelineIntensityFile);
            string SDFile = PREACTInput.ResolvePath(_simulation.Input.RootFolder, _fire.SpreadDirectionFile);
            ReadOutput(TOAFile, ROSFile, FIFile, SDFile);

            Vector2d ascUTM = new Vector2d(_xllcorner, _yllcorner);
            _originOffset = ascUTM - _simulation.Input.Simulation.Data.UTMOrigin;

            _landscapeSize = new Vector2d(ncols * _cellsize, nrows * _cellsizeY);

            _firelineIntensityData = new float[ncols * nrows];
            _newlyIgnitedCells = new List<Vector2int>();
            _sootInjection = new float[ncols * nrows];

            FireWeather = weather;
            if (FireWeather == null && !string.IsNullOrWhiteSpace(_fire.MidflameWindSpeedFile))
            {
                //Not authoritative: an imported fire's midflame wind is the fire's own, but the direction, the
                //band interval and the WUI area stay the scenario's [kPERIL] business.
                FireWeather = new FireWeatherRasters
                {
                    MidflameWindSpeedFile = NullIfNotNamed(_fire.MidflameWindSpeedFile),
                    Origin = "[AscImport] " + nameof(AscImportInput.MidflameWindSpeedFile),
                };
            }

            Engine.Message(_simulation, Engine.LogType.Log, "Wildfire ASCII data offset by (x/y) meters: " + _originOffset.x + ", " + _originOffset.y);
        }

        /// <summary>
        /// An optional raster's full path, or null when the scenario does not name one.
        /// </summary>
        /// <remarks>
        /// <c>Path.Combine(folder, "")</c> returns <c>folder</c>, so combining an unset path yields something
        /// that looks like a perfectly good path and is a directory. Every downstream emptiness check then
        /// passes and the directory is opened as a raster.
        /// </remarks>
        private string NullIfNotNamed(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return null;
            }

            return PREACTInput.ResolvePath(_simulation.Input.RootFolder, relativePath);
        }

        bool _first = true;

        /// <summary>
        /// When the burned-cell sweep last ran. The sweep is a minute's worth of arrival times at a time,
        /// which is fine for the display and for soot, and far cheaper than every step.
        /// </summary>
        /// <remarks>
        /// Elapsed time rather than the <c>(int)currentTime % 60 == 0</c> this used to test. That only fires
        /// when the simulation clock lands exactly on a multiple of 60, so it depends on the timestep
        /// dividing 60: at <c>DeltaTime = 7</c> the clock goes 56, 63 and never hits it, and the fire is
        /// never drawn and no soot is injected for the whole run. It is not merely rarer - it never happens
        /// after t = 0.
        /// </remarks>
        private double _lastSweepTime = double.NegativeInfinity;
        private const double SweepIntervalSeconds = 60.0;

        public override void Step(double currentTime, double deltaTime)
        {
            bool updateVisuals = currentTime - _lastSweepTime >= SweepIntervalSeconds;
            if (updateVisuals)
            {
                _lastSweepTime = currentTime;
            }

            if (_first && currentTime >= _startTime)
            {
                _first = false;
                Engine.Message(_simulation, Engine.LogType.Log, "AscFire started.");
            }

            if (updateVisuals)
            {
                int index = 0;
                for (int y = 0; y < nrows; y++)
                {
                    for (int x = 0; x < ncols; x++)
                    {
                        if (!_data[x, y].isActive && _simulation.Time.SimulationTime > _data[x, y].TimeOfAArrival + _startTime)
                        {
                            _ignited = true;
                            _data[x, y].isActive = true;
                            _newlyIgnitedCells.Add(new Vector2int(x, y));
                            _firelineIntensityData[index] = _data[x, y].FirelineIntensity;
                            _sootInjection[index] = 1f;
                            ++_activeCells;
                        }
                        ++index;
                    }
                }
            }            
        }

        public override List<Vector2int> GetIgnitedFireCells()
        {
            return _newlyIgnitedCells;
        }

        public override void ConsumeIgnitedFireCells()
        {
            _newlyIgnitedCells.Clear();
        }

        /// <summary>The arrival-time raster's own lower-left corner, which is the fire grid's.</summary>
        public override Vector2d GetGridOriginUtm()
        {
            return new Vector2d(_xllcorner, _yllcorner);
        }

        public override void GetOffsetAndSize(out Vector2d offset, out Vector2d size)
        {
            offset = _originOffset;
            size = new Vector2d(_cellsize * ncols, _cellsizeY * nrows);
        }

        /// <summary>
        /// Done once the clock is past the last arrival - on the simulation's clock, so the fire's own start
        /// offset is added, as everywhere else here. Without it a fire that starts late was "done" that much early.
        /// </summary>
        public override bool IsSimulationDone()
        {
            return _simulation.Time.SimulationTime > _maxTimeOfArrival + _startTime;
        }

        float[,] maxROS;
        /// <summary>
        /// Rate of spread (m/min) as <c>[x, y]</c> with a <b>lower-left origin</b> — y = 0 is the southern
        /// row, the same convention as everything else in this module and in <see cref="Utility.AscRaster"/>.
        /// </summary>
        /// <remarks>
        /// This used to flip the y axis, and documented itself as doing so, while both of its callers treated
        /// the result as lower-left — as did every raster they combine it with. The consequences were not
        /// cosmetic:
        ///
        /// <see cref="Simulation.HazardManager"/> builds the distance-to-fire field from this array and then
        /// reads it back at indices from <see cref="SimulationPosToCellIndex"/>, which is lower-left, so every
        /// evacuee's distance to the fire was sampled from the mirrored row — people fleeing a fire to the
        /// south were told about a fire to the north. k-PERIL received this flipped while its WUI mask, wind,
        /// elevation, slope and aspect all arrived lower-left, so the trigger boundary was computed with the
        /// spread field upside down relative to the terrain driving it.
        ///
        /// A vertical mirror is invisible on a domain whose fire happens to be roughly symmetric, which is
        /// why this survived: nothing errors, and the numbers all stay in range.
        /// </remarks>
        public override float[,] GetMaxROS()
        {
            if(maxROS == null)
            {
                maxROS = new float[ncols, nrows];
                for(int y = 0; y < nrows; ++y)
                {
                    for (int x = 0; x < ncols; ++x)
                    {
                        maxROS[x, y] = _data[x, y].RateOfSpread;
                    }
                }
            }

            return maxROS;
        }

        float[,] maxROSAzimuth;
        /// <summary>
        /// Spread direction (degrees clockwise from north) in the same lower-left <c>[x, y]</c> layout as
        /// <see cref="GetMaxROS"/>, and flipped for the same reason it no longer is.
        /// </summary>
        /// <remarks>
        /// Note that only the array layout was ever wrong here, not the bearings themselves: mirroring the
        /// rows moves a direction to the wrong cell but does not negate it. So the fix is the layout alone —
        /// there is no compensating change owed to the angles.
        /// </remarks>
        public override float[,] GetMaxROSAzimuth()
        {
            if (maxROSAzimuth == null)
            {
                maxROSAzimuth = new float[ncols, nrows];
                for (int y = 0; y < nrows; ++y)
                {
                    for (int x = 0; x < ncols; ++x)
                    {
                        maxROSAzimuth[x, y] = _data[x, y].SpreadDirection;
                    }
                }
            }

            return maxROSAzimuth;
        }

        public override int GetCellCountX()
        {
            return ncols;
        }

        public override int GetCellCountY()
        {
            return nrows;
        }

        public override float GetCellSizeX()
        {
            return (float)_cellsize;
        }

        public override float GetCellSizeY()
        {
            return (float)_cellsizeY;
        }

        /// <summary>
        /// Imports as files from external wildfire simulation.
        /// </summary>
        /// <param name="TOAFile">Time of arrival, minutes.</param>
        /// <param name="ROSFile">Rate of spread, m/min.</param>
        /// <param name="FIFile">Fireline intensity, kW/m.</param>
        /// <param name="SDFile">Spread direction, degrees from North = 0 and then clockwise.</param>
        private void ReadOutput(string TOAFile, string ROSFile, string FIFile, string SDFile)
        {
            //Read via the format-agnostic raster reader, so inputs may be .asc or GeoTIFF (.tif).
            //All arrays come back as [ncols, nrows] with a lower-left origin.
            float[,] toa = Utility.AscRaster.Read(TOAFile, out Utility.AscRaster.Header header, out bool toaOk);
            if (!toaOk)
            {
                Engine.Message(null, Engine.LogType.SimulationError, "Time of arrival raster could not be read: " + TOAFile);
                return;
            }
            float[,] ros = Utility.AscRaster.Read(ROSFile, out Utility.AscRaster.Header rosHeader, out bool rosOk);
            if (!rosOk)
            {
                Engine.Message(null, Engine.LogType.SimulationError, "Rate of spread raster could not be read: " + ROSFile);
                return;
            }
            float[,] sd = Utility.AscRaster.Read(SDFile, out Utility.AscRaster.Header sdHeader, out bool sdOk);
            if (!sdOk)
            {
                Engine.Message(null, Engine.LogType.SimulationError, "Spread direction raster could not be read: " + SDFile);
                return;
            }
            //Fireline intensity is optional.
            float[,] fi = null;
            bool fiOk = false;
            if (!string.IsNullOrEmpty(FIFile))
            {
                fi = Utility.AscRaster.Read(FIFile, out Utility.AscRaster.Header fiHeader, out fiOk);
                if (!fiOk)
                {
                    Engine.Message(null, Engine.LogType.Warning, "Fireline intensity raster could not be read, using 0: " + FIFile);
                }
                else if (fiHeader.Ncols != header.Ncols || fiHeader.Nrows != header.Nrows)
                {
                    Engine.Message(null, Engine.LogType.Warning,
                        $"Fireline intensity raster is {fiHeader.Ncols}x{fiHeader.Nrows}, not the {header.Ncols}x{header.Nrows} "
                        + "of the arrival times; using 0: " + FIFile);
                    fiOk = false;
                }
            }

            //The four describe one fire cell by cell. Read at the arrival raster's indices, a smaller one threw an
            //IndexOutOfRangeException and a larger one was silently read at the wrong cells.
            foreach ((string what, string file, Utility.AscRaster.Header other) in new[]
                     {
                         ("Rate of spread", ROSFile, rosHeader),
                         ("Spread direction", SDFile, sdHeader),
                     })
            {
                if (other.Ncols != header.Ncols || other.Nrows != header.Nrows)
                {
                    Engine.Message(null, Engine.LogType.SimulationError,
                        $"{what} raster is {other.Ncols}x{other.Nrows}, not the {header.Ncols}x{header.Nrows} of the "
                        + $"arrival times {TOAFile}; the fire rasters have to come from one run: {file}");
                    return;
                }
            }

            ncols = header.Ncols;
            nrows = header.Nrows;
            _xllcorner = header.XllCorner;
            _yllcorner = header.YllCorner;
            _cellsize = header.CellSize;
            _cellsizeY = header.CellSizeY > 0.0 ? header.CellSizeY : header.CellSize;
            _NODATA_VALUE = header.NoDataValue;

            _data = new FireRasterData[ncols, nrows];

            //Everything downstream works in seconds, and so does the default. Which unit the raster is in
            //cannot be read off the file, so the scenario states it: seconds for ELMFIRE, minutes for the
            //.asc products this module was originally written for. It was hardcoded to minutes, so an
            //ELMFIRE fire arrived 60 times too late and barely moved over an evacuation - with nothing
            //anywhere saying so, which is why the unit is logged below whichever way it goes.
            AscImportInput.TimeUnits units = _fire.TimeOfArrivalUnits;
            float toSeconds = units == AscImportInput.TimeUnits.Seconds ? 1f : 60f;
            Engine.Message(_simulation, Engine.LogType.Log,
                $"Fire arrival times read as {units.ToString().ToLowerInvariant()}.");

            for (int y = 0; y < nrows; y++)
            {
                for (int x = 0; x < ncols; x++)
                {
                    //Unburned is stored as float.MaxValue. The raster says so with its nodata value, a negative
                    //number (ELMFIRE's -9999) or NaN; a positive nodata such as 3.4e38 used to be taken as a real
                    //arrival, became the last one, and the fire never finished.
                    float raw = toa[x, y];
                    float TOAValue = IsNoArrival(raw, header.NoDataValue) ? float.MaxValue : raw * toSeconds;
                    if (TOAValue != float.MaxValue && TOAValue > _maxTimeOfArrival)
                    {
                        _maxTimeOfArrival = TOAValue;
                    }

                    _data[x, y].TimeOfAArrival = TOAValue;
                    _data[x, y].RateOfSpread = ros[x, y];
                    _data[x, y].FirelineIntensity = fiOk ? fi[x, y] : 0f;
                    _data[x, y].SpreadDirection = sd[x, y];
                    _data[x, y].isActive = false;
                }
            }
        }

        private static bool IsNoArrival(float raw, double noData)
        {
            if (float.IsNaN(raw) || float.IsInfinity(raw) || raw < 0f) return true;
            //A nodata of 0 is not honoured: 0 is also the ignition cell's arrival time.
            if (noData != 0.0 && !double.IsNaN(noData) && System.Math.Abs(raw - noData) <= System.Math.Abs(noData) * 1e-6) return true;
            return raw >= 1e30f;
        }

        /// <summary>
        /// Whether the fire reached this cell at all.
        ///
        /// Read off the arrival time rather than the cell's current state: an unburned cell is stored as
        /// float.MaxValue by the reader above, which is exactly "the fire never got here" - within however
        /// long the fire that produced these rasters was run for.
        /// </summary>
        public override bool CellHasBurned(int x, int y)
        {
            if (!IsInside(x, y))
            {
                return false;
            }

            return _data[x, y].TimeOfAArrival != float.MaxValue;
        }

        /// <summary>
        /// Arrival time in seconds from the start of the simulation, including the fire's own offset.
        /// </summary>
        /// <remarks>
        /// <c>_startTime</c> is added because the stored value is measured from the fire's start, which the
        /// scenario may place after the simulation's - the same sum <see cref="Step"/> compares the clock
        /// against. A caller mapping this onto weather bands has to be on the simulation's clock, since that
        /// is what the bands are indexed from.
        /// </remarks>
        public override float GetTimeOfArrival(int x, int y)
        {
            if (!IsInside(x, y) || _data[x, y].TimeOfAArrival == float.MaxValue)
            {
                return float.MaxValue;
            }

            return _data[x, y].TimeOfAArrival + _startTime;
        }

        /// <summary>
        /// Whether the fire has reached the cell containing a simulation position. Outside the raster counts
        /// as not reached.
        /// </summary>
        /// <remarks>
        /// The position is mapped through <see cref="SimulationPosToCellIndex"/> rather than converted here.
        /// This had its own copy of the conversion which <b>added</b> <c>_originOffset</c> where the shared one
        /// subtracts it, and since that offset is the raster's lower-left corner measured from the simulation
        /// origin, subtracting is what turns a simulation position into a raster index. The sign error
        /// displaced every lookup by twice the offset — on the Mati case a couple of kilometres, comfortably
        /// enough to read a different part of the fire.
        ///
        /// What that reached was <see cref="Simulation.EvacuationManager"/> deciding whether a fire has
        /// blocked an evacuation destination, so destinations were being closed, or left open, on the fire
        /// state somewhere else entirely. One conversion now serves both callers so they cannot drift again.
        /// </remarks>
        public override FireCellState GetFireCellState(Vector2d simulationPos)
        {
            Vector2int cell = SimulationPosToCellIndex(simulationPos, out bool inside);

            //On the simulation's clock, like Step and GetTimeOfArrival: the stored arrival is from the fire's start.
            if (!inside || _data[cell.x, cell.y].TimeOfAArrival == float.MaxValue
                || _simulation.Time.SimulationTime < _data[cell.x, cell.y].TimeOfAArrival + _startTime)
            {
                return FireCellState.Dead;
            }

            return FireCellState.Ignited;
        }

        private bool IsInside(int x, int y)
        {
            bool result = true;
            if(x < 0 || x >= ncols || y < 0 || y >= nrows)
            {
                result = false;
            }

            return result;
        }

        public override double GetInternalDeltaTime()
        {
            return _simulation.Input.Simulation.DeltaTime;
        }

        public override float[] GetFireLineIntensityData()
        {
            return _firelineIntensityData;
        }

        /// <summary>
        /// Null: an imported fire carries no fuel model.
        /// </summary>
        /// <remarks>
        /// This module reads four rasters - arrival time, spread rate, spread direction and fireline
        /// intensity - and a fuel model is not among them; the fuel was an input to whatever computed the
        /// fire, not an output of it. An ELMFIRE case does have one on disk (<c>inputs/fbfm40.tif</c>), so
        /// this could be filled in, but nothing reads it today.
        ///
        /// Null rather than the <c>NotImplementedException</c> this used to throw. The renderer calls this
        /// every frame from <c>UpdateFireRenderer</c>, so the throw was not one error but one per frame for
        /// the rest of the session, and it fired from the render loop rather than from the click that
        /// caused it. The renderer already skips a null.
        /// </remarks>
        public override float[] GetSootProduction()
        {
            return _sootInjection;
        }

        public override int GetActiveCellCount()
        {
            return _activeCells;
        }

        public override void Stop()
        {
            //throw new System.NotImplementedException();
        }

        public override Vector2int SimulationPosToCellIndex(Vector2d simulationPos, out bool inside)
        {
            Vector2d LocalPos = simulationPos;
            LocalPos -= _originOffset;
            //Floor, not a cast: a cast truncates towards zero, so up to a cell west or south of the grid mapped to
            //column or row 0 and counted as inside.
            int xIndex = (int)System.Math.Floor(ncols * LocalPos.x / _landscapeSize.x);
            int yIndex = (int)System.Math.Floor(nrows * LocalPos.y / _landscapeSize.y);
            inside = IsInside(xIndex, yIndex);

            return new Vector2int(xIndex, yIndex);
        }

        public override bool Ignited()
        {
            return _ignited;
        }
    }
}

