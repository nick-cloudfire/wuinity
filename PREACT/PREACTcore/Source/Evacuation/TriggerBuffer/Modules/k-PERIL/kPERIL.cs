//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using PREACT.Wildfire.Behave;
using PREACT.Wildfire;
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
    public class kPERIL : TriggerBufferModule
    {
        private const BehaveUnits.MoistureUnits.MoistureUnitsEnum moistureUnits = BehaveUnits.MoistureUnits.MoistureUnitsEnum.Percent;
        private const WindHeightInputMode windHeightInputMode = WindHeightInputMode.DirectMidflame;
        private const BehaveUnits.SlopeUnits.SlopeUnitsEnum slopeUnits = BehaveUnits.SlopeUnits.SlopeUnitsEnum.Degrees;
        private const BehaveUnits.CoverUnits.CoverUnitsEnum coverUnits = BehaveUnits.CoverUnits.CoverUnitsEnum.Fraction;
        private const BehaveUnits.LengthUnits.LengthUnitsEnum lengthUnits = BehaveUnits.LengthUnits.LengthUnitsEnum.Meters;
        private const BehaveUnits.SpeedUnits.SpeedUnitsEnum windSpeedUnits = BehaveUnits.SpeedUnits.SpeedUnitsEnum.MetersPerSecond;
        private const WindAndSpreadOrientationMode windAndSpreadOrientationMode = WindAndSpreadOrientationMode.RelativeToNorth;

        private int _xDim, _yDim;
        private bool _calculateROS;
        private LandscapeData? _lcpData;
        private float _RSET; //minutes
        private bool[] _wuiArea;
        private float _midflameWindspeed;
        private float _windDirection;
        private float _cellSize;

        private float[,]? _maxROS;
        private float[,]? _rosAzimuth;
        private float[,]? _elevation;
        private float[,]? _slope;
        private float[,]? _aspect;
        private InitialFuelMoistureLibrary? _fuelMoisture;
        private FuelModelInput? _fuelModel;

        /// <summary>
        /// Use a rate-of-spread field (and azimuth) computed elsewhere (e.g. imported from
        /// ELMFIRE via AscImport). Slope/aspect are used directly when supplied (ELMFIRE
        /// provides slp/asp rasters); otherwise they are derived from elevation, or assumed
        /// flat if neither is available.
        /// </summary>
        public kPERIL(float rsetMinutes, bool[] wuiArea, float midflameWindspeed, float windDirection, float[,] maxROS, float[,] rosAzimuth, float cellSize, float[,]? elevation = null, float[,]? slope = null, float[,]? aspect = null)
        {
            _calculateROS = false;
            _xDim = maxROS.GetLength(0);
            _yDim = maxROS.GetLength(1);

            _RSET = rsetMinutes;
            _wuiArea = wuiArea;
            _midflameWindspeed = midflameWindspeed;
            _windDirection = windDirection;
            _cellSize = cellSize;

            _maxROS = maxROS;
            _rosAzimuth = rosAzimuth;
            _elevation = elevation;
            _slope = slope;
            _aspect = aspect;
        }

        /// <summary>Compute the rate-of-spread field internally from the landscape using Behave.</summary>
        public kPERIL(LandscapeData lcpData, float rsetMinutes, bool[] wuiArea, float midflameWindspeed, float windDirection, InitialFuelMoistureLibrary fuelMoisture, FuelModelInput fuelModel)
        {
            _calculateROS = true;
            _xDim = lcpData.GetCellCountX();
            _yDim = lcpData.GetCellCountY();

            _lcpData = lcpData;
            _RSET = rsetMinutes;
            _wuiArea = wuiArea;
            _midflameWindspeed = midflameWindspeed;
            _windDirection = windDirection;
            _cellSize = (float)lcpData.RasterCellResolutionX;

            _fuelMoisture = fuelMoisture;
            _fuelModel = fuelModel;
        }

        /// <summary>
        /// Runs k-PERIL. Should be called after a completed simulation, once the RSET is known.
        /// </summary>
        public override void Run()
        {
            Engine.Message(null, Engine.LogType.Log, "Starting calculation of trigger buffer using k-PERIL. RSET = " + _RSET + " minutes.");

            PerilCore peril = new PerilCore();

            float[,] maxROS;
            float[,] rosAzimuth;
            float[,] slope = null;
            float[,] aspect = null;

            if (_calculateROS)
            {
                Engine.Message(null, Engine.LogType.Log, "k-PERIL is calculating ROS using Behave.");
                bool[,] wuiArea2D = GetWUIArea2D(_wuiArea, _xDim, _yDim);
                CalculateAllRateOfSpreadsAndDirections(_lcpData, out maxROS, out rosAzimuth, out slope, out aspect, _midflameWindspeed, _windDirection, _fuelMoisture, wuiArea2D, _fuelModel);
            }
            else
            {
                Engine.Message(null, Engine.LogType.Log, "k-PERIL is using the provided ROS (and azimuth) field.");
                maxROS = _maxROS;
                rosAzimuth = _rosAzimuth;
                slope = _slope;
                aspect = _aspect;
            }

            //ROS must be imported first so perilData knows the raster dimensions.
            peril.perilData.importFireRastersByVariable(maxROS, rosAzimuth);
            peril.perilData.cellSize = _cellSize;
            peril.perilData.noDataValue = -9999f;

            //topography: use slope/aspect from the landscape when available, otherwise
            //derive them from a (flat) elevation grid.
            if (slope != null && aspect != null)
            {
                peril.perilData.importTopographyRastersByFileName(new float[_xDim, _yDim], slope, aspect);
            }
            else
            {
                peril.perilData.importTopographyRastersByVariable(_elevation ?? new float[_xDim, _yDim]);
            }

            //wind must come after topography (it combines with slope). k-PERIL takes a single
            //representative mid-flame wind as it does not model changing weather.
            peril.perilData.rasteriseWindFromScalars(_midflameWindspeed, _windDirection);

            peril.perilData.importWuiRastersByVariable(BuildWuiRaster(_wuiArea, _xDim, _yDim));

            var brokenDownROS = peril.breakdownRateOfSpread();
            var travelTime = peril.getTravelTimeFromBrokenDownRos(brokenDownROS);
            _triggerBufferOutput = peril.getTriggerBoundary(travelTime, _RSET);

            Engine.Message(null, Engine.LogType.Log, "k-PERIL trigger boundary calculated.");
        }

        public static void SaveToFile(float[,] data, float cellsize, string outputFilePath)
        {
            try
            {
                using (StreamWriter outputWriter = new StreamWriter(outputFilePath))
                {
                    int xDim = data.GetLength(0);
                    int yDim = data.GetLength(1);

                    outputWriter.WriteLine("ncols " + xDim);
                    outputWriter.WriteLine("nrows " + yDim);
                    outputWriter.WriteLine("xllcorner " + 0);
                    outputWriter.WriteLine("yllcorner " + 0);
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

        private static void CalculateAllRateOfSpreadsAndDirections(LandscapeData lcpData, out float[,] rateOfSpreads, out float[,] spreadDirections, out float[,] slope, out float[,] aspect, float midFlameWindspeed, float windDirection, InitialFuelMoistureLibrary initialFuelMoistureLibrary, bool[,]? wuiArea = null, FuelModelInput? fuelModelInputs = null)
        {
            int xDim = lcpData.GetCellCountX();
            int yDim = lcpData.GetCellCountY();
            rateOfSpreads = new float[xDim, yDim];
            spreadDirections = new float[xDim, yDim];
            slope = new float[xDim, yDim];
            aspect = new float[xDim, yDim];

            FuelModelSet fuelModelSet = new FuelModelSet();
            if (fuelModelInputs != null)
            {
                for (int i = 0; i < fuelModelInputs.Fuels.Count; i++)
                {
                    fuelModelSet.setFuelModelRecord(fuelModelInputs.Fuels[i]);
                }
            }
            Surface surfaceFire = new Surface(fuelModelSet);

            for (int y = 0; y < yDim; ++y)
            {
                for (int x = 0; x < xDim; ++x)
                {
                    LandscapeCellData cellData = lcpData.GetCellData(x, y);
                    slope[x, y] = (float)cellData.slope;
                    aspect[x, y] = (float)cellData.aspect;

                    //if WUI area specified we skip ROS calc here (interior is protected)
                    if (wuiArea != null && wuiArea[x, y])
                    {
                        continue;
                    }

                    //k-PERIL crashes if edges have non-zero data
                    if (x < 1 || x > xDim - 2 || y < 1 || y > yDim - 2)
                    {
                        continue;
                    }

                    InitialFuelMoisture moisture = initialFuelMoistureLibrary.GetInitialFuelMoisture(cellData.fuel_model);
                    double crownRatio = 1.5; //TODO: how to get this data? LCP does not seem to carry it

                    surfaceFire.updateSurfaceInputs(cellData.fuel_model, moisture.OneHour, moisture.TenHour, moisture.HundredHour, moisture.LiveHerbaceous, moisture.LiveWoody, moistureUnits,
                        midFlameWindspeed, windSpeedUnits, windHeightInputMode, windDirection, windAndSpreadOrientationMode, cellData.slope, slopeUnits, cellData.aspect, cellData.canopy_cover, coverUnits, cellData.crown_canopy_height, lengthUnits, crownRatio);

                    surfaceFire.doSurfaceRunInDirectionOfMaxSpread();
                    rateOfSpreads[x, y] = (float)surfaceFire.getSpreadRate(BehaveUnits.SpeedUnits.SpeedUnitsEnum.MetersPerMinute);
                    spreadDirections[x, y] = (float)surfaceFire.getDirectionOfMaxSpread();
                }
            }
        }

        private static bool[,] GetWUIArea2D(bool[] wuiArea, int xDim, int yDim)
        {
            bool[,] result = new bool[xDim, yDim];
            for (int i = 0; i < wuiArea.Length; i++)
            {
                result[i % xDim, i / xDim] = wuiArea[i];
            }
            return result;
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
