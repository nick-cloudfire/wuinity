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
        /// <remarks>
        /// Every raster is handed to kPERILcore in kPERILcore's own layout and the boundary is converted back, at
        /// this one place (<see cref="ToCoreLayout"/>, <see cref="FromCoreLayout"/>). The engine keeps rasters as
        /// <c>[x = column (east), y = row from the bottom (north)]</c>; kPERILcore keeps them as
        /// <c>[row from the top, column]</c> - its own GeoTIFF/ASCII reader fills them that way - and its geometry
        /// is written for that: direction 0 is the neighbour one row up (north), and the rate towards direction
        /// <c>d</c> is the spread ellipse evaluated at compass bearing <c>45 d</c> against the compass spread
        /// direction. The engine's arrays used to go in as they were, so direction 0 was the neighbour one column
        /// to the west and every rate was applied 90 degrees counter-clockwise of where it belonged: a fire
        /// spreading east produced a boundary reaching south of the WUI area instead of west (upwind) of it, on
        /// every single run and campaign.
        /// </remarks>
        public override void Run()
        {
            Engine.Message(null, Engine.LogType.Log, "Starting calculation of trigger buffer using k-PERIL. RSET = "
                + _RSET.ToString(System.Globalization.CultureInfo.InvariantCulture) + " minutes.");

            PerilCore peril = new PerilCore();

            //ROS must be imported first so perilData knows the raster dimensions.
            peril.perilData.importFireRastersByVariable(ToCoreLayout(_maxROS), ToCoreLayout(_rosAzimuth));
            peril.perilData.cellSize = _cellSize;
            peril.perilData.noDataValue = -9999f;

            //topography: use slope/aspect from the landscape when available, otherwise
            //derive them from a (flat) elevation grid.
            if (_slope != null && _aspect != null)
            {
                //The real elevation when there is one, rather than a grid of zeros. k-PERIL works from the
                //slope and aspect given here, so the elevation is not what drives the result, but handing
                //it a flat surface alongside a slope field is a contradiction worth not writing down.
                peril.perilData.importTopographyRastersByFileName(ToCoreLayout(_elevation ?? new float[_xDim, _yDim]),
                    ToCoreLayout(_slope), ToCoreLayout(_aspect));
                Engine.Message(null, Engine.LogType.Log, "k-PERIL is using the supplied slope and aspect.");
            }
            else if (_elevation != null)
            {
                //k-PERIL derives the slope and aspect itself from the elevation.
                peril.perilData.importTopographyRastersByVariable(ToCoreLayout(_elevation));
                Engine.Message(null, Engine.LogType.Log, "k-PERIL is deriving slope and aspect from the supplied elevation.");
            }
            else
            {
                //Flat ground. Said plainly, because the slope term then contributes nothing to the
                //effective wind and the boundary comes out the same as it would on a plain.
                peril.perilData.importTopographyRastersByVariable(new float[_yDim, _xDim]);
                Engine.Message(null, Engine.LogType.Warning,
                    "k-PERIL has no topography, so the boundary is computed as if the ground were flat.");
            }

            //Wind must come after topography, because k-PERIL combines the two into an effective
            //wind before using it. The field goes in as a raster: k-PERIL does not model weather
            //changing over TIME, but it handles wind varying over SPACE perfectly well, and the
            //weather pipeline runs WindNinja precisely to resolve that variation over terrain.
            //Speeds are in mi/h, the unit Anderson's length-to-breadth correlation expects.
            peril.perilData.importWeatherRastersByVariable(ToCoreLayout(_windSpeedMph), ToCoreLayout(_windDirectionDegrees));

            peril.perilData.importWuiRastersByVariable(ToCoreLayout(BuildWuiRaster(_wuiArea, _xDim, _yDim)));

            var brokenDownROS = peril.breakdownRateOfSpread();
            var travelTime = peril.getTravelTimeFromBrokenDownRos(brokenDownROS);
            _triggerBufferOutput = FromCoreLayout(peril.getTriggerBoundary(travelTime, _RSET));

            Engine.Message(null, Engine.LogType.Log, "k-PERIL trigger boundary calculated.");
        }

        /// <summary>
        /// An engine raster (<c>[x east, y north from the bottom]</c>) in kPERILcore's layout
        /// (<c>[row from the top, column]</c>): <c>core[r, c] = engine[c, rows - 1 - r]</c>.
        /// </summary>
        public static float[,] ToCoreLayout(float[,] engine)
        {
            int columns = engine.GetLength(0);
            int rows = engine.GetLength(1);
            var core = new float[rows, columns];
            for (int r = 0; r < rows; ++r)
            {
                int y = rows - 1 - r;
                for (int c = 0; c < columns; ++c)
                {
                    core[r, c] = engine[c, y];
                }
            }
            return core;
        }

        /// <summary>The inverse of <see cref="ToCoreLayout"/>: <c>engine[c, rows - 1 - r] = core[r, c]</c>.</summary>
        public static float[,] FromCoreLayout(float[,] core)
        {
            int rows = core.GetLength(0);
            int columns = core.GetLength(1);
            var engine = new float[columns, rows];
            for (int r = 0; r < rows; ++r)
            {
                int y = rows - 1 - r;
                for (int c = 0; c < columns; ++c)
                {
                    engine[c, y] = core[r, c];
                }
            }
            return engine;
        }

        /// <summary>
        /// Writes a trigger boundary as an ESRI ASCII grid, georeferenced on the fire grid it was computed on: north
        /// row first, <c>xllcorner</c>/<c>yllcorner</c> the grid's south-west corner, and a <c>.prj</c> beside it when
        /// the grid's CRS is known (<paramref name="epsgCode"/> &gt; 0), so GDAL and QGIS place it without help.
        /// </summary>
        /// <remarks>
        /// The corner used to be hardcoded to (0, 0). The raster was therefore correct cell for cell and
        /// positioned nowhere: it could not be overlaid on the domain, on the fire it came from, or on the
        /// ensemble statistics a campaign writes beside it — those carry the real corner, so the two output
        /// families of the same tool disagreed by the full easting of the domain. Every boundary the platform
        /// has ever written has this problem, so an old one has to be repositioned by hand.
        ///
        /// Numbers are written with the invariant culture: the cell size went through the thread's, so a
        /// 27.6 m grid written on a Greek or German machine read "cellsize 27,6", which no reader accepts.
        /// </remarks>
        public static void SaveToFile(float[,] data, float cellsize, string outputFilePath, Vector2d originUtm = default,
            int epsgCode = 0)
        {
            System.Globalization.CultureInfo invariant = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                using (StreamWriter outputWriter = new StreamWriter(outputFilePath))
                {
                    int xDim = data.GetLength(0);
                    int yDim = data.GetLength(1);

                    outputWriter.WriteLine("ncols " + xDim.ToString(invariant));
                    outputWriter.WriteLine("nrows " + yDim.ToString(invariant));
                    outputWriter.WriteLine("xllcorner " + originUtm.x.ToString("R", invariant));
                    outputWriter.WriteLine("yllcorner " + originUtm.y.ToString("R", invariant));
                    outputWriter.WriteLine("cellsize " + cellsize.ToString("R", invariant));
                    outputWriter.WriteLine("NODATA_value -9999");

                    var line = new System.Text.StringBuilder(xDim * 2);
                    for (int y = 0; y < yDim; ++y)
                    {
                        line.Clear();
                        for (int x = 0; x < xDim; ++x)
                        {
                            //flip y to follow the .asc convention (north row first, lower-left origin)
                            if (x > 0) line.Append(' ');
                            line.Append(data[x, yDim - 1 - y].ToString(invariant));
                        }
                        outputWriter.WriteLine(line.ToString());
                    }
                }

                if (epsgCode > 0)
                {
                    Utility.AscRaster.WriteCompanionPrj(outputFilePath, epsgCode);
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
