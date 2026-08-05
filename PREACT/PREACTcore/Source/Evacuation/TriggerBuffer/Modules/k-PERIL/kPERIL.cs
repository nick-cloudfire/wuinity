//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using PREACT.Math;
using System.IO;
// The vendored k-PERIL engine (PREACT/kPERILcore). Aliased because its class name
// (kPERIL.kPERIL) collides with this wrapper class (PREACT.kPERIL).
using PerilCore = global::kPERIL.kPERIL;

namespace PREACT
{
    /// <summary>
    /// WUInity wrapper around the vendored k-PERIL trigger-boundary engine.
    /// Pipeline: import ROS + direction, wind, topography and the WUI area, then
    /// breakdownRateOfSpread -> getTravelTime -> getTriggerBoundary(RSET).
    /// RSET (WRSET) is the evacuation time in MINUTES.
    /// </summary>
    /// <remarks>
    /// The rate of spread always comes from the fire module. There used to be a second path that derived
    /// it here from the landscape with BEHAVE; BEHAVE has been removed, and with the fire coming from
    /// ELMFIRE the derived field was the wrong answer anyway - it would have been computed from a
    /// different fuel model table, a single wind field and no crown fire, and then used to back-propagate
    /// through a fire that spread by other rules.
    /// </remarks>
    public class kPERIL : TriggerBufferModule
    {
        private int _xDim, _yDim;
        private float _RSET; //minutes
        private bool[] _wuiArea;
        private float[,] _windSpeedMph;
        private float[,] _windDirectionDegrees;
        private float _cellSize;

        private float[,] _maxROS;
        private float[,] _rosAzimuth;
        private float[,]? _elevation;
        private float[,]? _slope;
        private float[,]? _aspect;

        /// <summary>
        /// Takes the rate-of-spread field (and azimuth) the fire module produced - for ELMFIRE, its
        /// <c>vs</c> and <c>spread_dir</c> rasters. Slope/aspect are used directly when supplied (an
        /// ELMFIRE case has slp/asp); otherwise they are derived from elevation, or assumed flat if
        /// neither is available.
        /// </summary>
        public kPERIL(float rsetMinutes, bool[] wuiArea, float[,] windSpeedMph, float[,] windDirectionDegrees, float[,] maxROS, float[,] rosAzimuth, float cellSize, float[,]? elevation = null, float[,]? slope = null, float[,]? aspect = null)
        {
            _xDim = maxROS.GetLength(0);
            _yDim = maxROS.GetLength(1);

            _RSET = rsetMinutes;
            _wuiArea = wuiArea;
            _windSpeedMph = windSpeedMph;
            _windDirectionDegrees = windDirectionDegrees;
            _cellSize = cellSize;

            _maxROS = maxROS;
            _rosAzimuth = rosAzimuth;
            _elevation = elevation;
            _slope = slope;
            _aspect = aspect;
        }

        /// <summary>
        /// Runs k-PERIL. Should be called after a completed simulation, once the RSET is known.
        /// </summary>
        public override void Run()
        {
            Engine.Message(null, Engine.LogType.Log, "Starting calculation of trigger buffer using k-PERIL. RSET = " + _RSET + " minutes.");

            PerilCore peril = new PerilCore();

            float[,] maxROS = _maxROS;
            float[,] rosAzimuth = _rosAzimuth;
            float[,] slope = _slope;
            float[,] aspect = _aspect;

            //ROS must be imported first so perilData knows the raster dimensions.
            peril.perilData.importFireRastersByVariable(maxROS, rosAzimuth);
            peril.perilData.cellSize = _cellSize;
            peril.perilData.noDataValue = -9999f;

            //topography: use slope/aspect from the landscape when available, otherwise
            //derive them from a (flat) elevation grid.
            if (slope != null && aspect != null)
            {
                //The real elevation when there is one, rather than a grid of zeros. k-PERIL works from the
                //slope and aspect given here, so the elevation is not what drives the result, but handing
                //it a flat surface alongside a slope field is a contradiction worth not writing down.
                peril.perilData.importTopographyRastersByFileName(_elevation ?? new float[_xDim, _yDim], slope, aspect);
                Engine.Message(null, Engine.LogType.Log, "k-PERIL is using the supplied slope and aspect.");
            }
            else if (_elevation != null)
            {
                //k-PERIL derives the slope and aspect itself from the elevation.
                peril.perilData.importTopographyRastersByVariable(_elevation);
                Engine.Message(null, Engine.LogType.Log, "k-PERIL is deriving slope and aspect from the supplied elevation.");
            }
            else
            {
                //Flat ground. Said plainly, because the slope term then contributes nothing to the
                //effective wind and the boundary comes out the same as it would on a plain.
                peril.perilData.importTopographyRastersByVariable(new float[_xDim, _yDim]);
                Engine.Message(null, Engine.LogType.Warning,
                    "k-PERIL has no topography, so the boundary is computed as if the ground were flat.");
            }

            //Wind must come after topography, because k-PERIL combines the two into an effective
            //wind before using it. The field goes in as a raster: k-PERIL does not model weather
            //changing over TIME, but it handles wind varying over SPACE perfectly well, and the
            //weather pipeline runs WindNinja precisely to resolve that variation over terrain.
            //Speeds are in mi/h, the unit Anderson's length-to-breadth correlation expects.
            peril.perilData.importWeatherRastersByVariable(_windSpeedMph, _windDirectionDegrees);

            peril.perilData.importWuiRastersByVariable(BuildWuiRaster(_wuiArea, _xDim, _yDim));

            var brokenDownROS = peril.breakdownRateOfSpread();
            var travelTime = peril.getTravelTimeFromBrokenDownRos(brokenDownROS);
            _triggerBufferOutput = peril.getTriggerBoundary(travelTime, _RSET);

            Engine.Message(null, Engine.LogType.Log, "k-PERIL trigger boundary calculated.");
        }

        /// <summary>
        /// Writes a trigger boundary as an ESRI ASCII grid, georeferenced on the fire grid it was computed on.
        /// </summary>
        /// <remarks>
        /// The corner used to be hardcoded to (0, 0). The raster was therefore correct cell for cell and
        /// positioned nowhere: it could not be overlaid on the domain, on the fire it came from, or on the
        /// ensemble statistics a campaign writes beside it — those carry the real corner, so the two output
        /// families of the same tool disagreed by the full easting of the domain. Every boundary the platform
        /// has ever written has this problem, so an old one has to be repositioned by hand.
        ///
        /// Still no CRS: an <c>.asc</c> cannot carry one, and nothing here writes the <c>.prj</c> that would.
        /// The corner at least puts it in the right place once the zone is known.
        /// </remarks>
        public static void SaveToFile(float[,] data, float cellsize, string outputFilePath, Vector2d originUtm = default)
        {
            try
            {
                using (StreamWriter outputWriter = new StreamWriter(outputFilePath))
                {
                    int xDim = data.GetLength(0);
                    int yDim = data.GetLength(1);

                    outputWriter.WriteLine("ncols " + xDim);
                    outputWriter.WriteLine("nrows " + yDim);
                    outputWriter.WriteLine("xllcorner " + originUtm.x.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    outputWriter.WriteLine("yllcorner " + originUtm.y.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    outputWriter.WriteLine("cellsize " + cellsize);
                    outputWriter.WriteLine("NODATA_value " + -9999);

                    for (int y = 0; y < yDim; ++y)
                    {
                        string line = "";
                        for (int x = 0; x < xDim; ++x)
                        {
                            //flip y to follow the .asc convention (lower-left origin)
                            line += data[x, yDim - 1 - y];
                            if (x < xDim - 1)
                            {
                                line += " ";
                            }
                        }
                        outputWriter.WriteLine(line);
                    }
                }
            }
            catch (System.Exception e)
            {
                Engine.Message(null, Engine.LogType.Warning, e.Message);
            }
        }

        private static float[,] BuildWuiRaster(bool[] wuiArea, int xDim, int yDim)
        {
            float[,] result = new float[xDim, yDim];
            for (int i = 0; i < wuiArea.Length; i++)
            {
                if (wuiArea[i])
                {
                    result[i % xDim, i / xDim] = 1f;
                }
            }
            return result;
        }
    }
}
